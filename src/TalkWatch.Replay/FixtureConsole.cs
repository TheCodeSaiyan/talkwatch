using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Web;
using TalkWatch.Core.Talk;

namespace TalkWatch.Replay;

/// <summary>
/// A fake UniFi console that serves the committed fixtures, for tests and later the demo mode. The call log is
/// rebuilt from every distinct call in the fixtures and served newest first, one page at a time, as Talk does.
/// </summary>
public sealed class FixtureConsole : HttpMessageHandler
{
    public const string Username = "talkwatch";
    public const string Password = "fixture-password";

    // Replaced whole, never changed in place, once the console is answering: a demo adds calls while polls read them.
    private List<JsonElement> _calls;
    private readonly Lock _adding = new();

    private readonly string _directory;

    public FixtureConsole(string fixtureDirectory)
    {
        _directory = fixtureDirectory;
        _calls = LoadCalls(fixtureDirectory);

        // Recordings the capture fetched, by call uuid. Their audio is the synthetic silence the fixtures hold.
        using var index = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(fixtureDirectory, "index.json")));
        foreach (var entry in index.RootElement.EnumerateArray())
        {
            var path = entry.GetProperty("path").GetString()!;
            if (path.StartsWith(RecordingPath, StringComparison.Ordinal) && entry.GetProperty("file").GetString() is { } file)
            {
                _recordings[path[RecordingPath.Length..]] = Path.Combine(fixtureDirectory, file);
            }
            else if (entry.GetProperty("kind").GetString() == "http" && entry.GetProperty("method").GetString() == "GET"
                && entry.GetProperty("file").GetString() is { } json && json.EndsWith(".json", StringComparison.Ordinal))
            {
                // Any other captured GET, first capture wins: /info, /users, /ucore/system_info and the rest.
                _captured.TryAdd(path.Split('?')[0], Path.Combine(fixtureDirectory, json));
            }
            else if (entry.GetProperty("kind").GetString() == "http" && entry.GetProperty("method").GetString() == "GET"
                && entry.GetProperty("file").GetString() is { } mp3 && mp3.EndsWith(".mp3", StringComparison.Ordinal))
            {
                // Other captured audio, such as switchboard greetings: synthetic silence of the real length.
                _capturedAudio.TryAdd(path.Split('?')[0], Path.Combine(fixtureDirectory, mp3));
            }
        }

        if (Directory.Exists(fixtureDirectory + "-voicemail"))
        {
            LoadVoicemail(fixtureDirectory + "-voicemail");
        }
    }

    /// <summary>
    /// The voicemail capture, kept beside the main one: its details GETs are served like any captured GET, and each
    /// message's audio by the file path the Talk app POSTed for it, which is the request just before the response.
    /// </summary>
    private void LoadVoicemail(string directory)
    {
        using var index = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "index.json")));
        string? requestedPath = null;
        foreach (var entry in index.RootElement.EnumerateArray())
        {
            var path = entry.GetProperty("path").GetString()!;
            var file = Path.Combine(directory, entry.GetProperty("file").GetString()!);
            switch (entry.GetProperty("kind").GetString(), entry.GetProperty("method").GetString())
            {
                case ("http", "GET") when file.EndsWith(".json", StringComparison.Ordinal):
                    _captured.TryAdd(path, file);
                    break;
                case ("req", "POST") when path == VoicemailAudioPath:
                    using (var body = JsonDocument.Parse(File.ReadAllBytes(file)))
                    {
                        requestedPath = body.RootElement.GetProperty("path").GetString();
                    }

                    break;
                case ("http", "POST") when path == VoicemailAudioPath && requestedPath is not null:
                    _voicemail[requestedPath] = file;
                    requestedPath = null;
                    break;
            }
        }
    }

    /// <summary>
    /// Moves every call, and each of its events, by the same amount so that the newest happened at
    /// <paramref name="newestAt"/>: a demo then has calls from today rather than from when the capture was taken.
    /// </summary>
    public void ShiftTimes(DateTimeOffset newestAt)
    {
        if (_calls.Count == 0)
        {
            return;
        }

        var by = newestAt - _calls.Max(c => c.GetProperty("time").GetDateTimeOffset());
        for (var i = 0; i < _calls.Count; i++)
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(_calls[i].GetRawText())!;
            node["time"] = Shifted(node["time"]);
            if (node["call_events"] is System.Text.Json.Nodes.JsonArray events)
            {
                foreach (var e in events.OfType<System.Text.Json.Nodes.JsonObject>())
                {
                    e["time"] = Shifted(e["time"]);
                }
            }

            using var document = JsonDocument.Parse(node.ToJsonString());
            _calls[i] = document.RootElement.Clone();
        }

        System.Text.Json.Nodes.JsonNode? Shifted(System.Text.Json.Nodes.JsonNode? time) =>
            time?.GetValue<string>() is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
                ? (at + by).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)
                : time;
    }

    private static List<JsonElement> LoadCalls(string fixtureDirectory)
    {
        var byUuid = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(fixtureDirectory, "*-http-proxy-talk-api-call-log.json"))
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(file));
            foreach (var record in document.RootElement.GetProperty("records").EnumerateArray())
            {
                byUuid.TryAdd(record.GetProperty("uuid").GetString()!, record.Clone());
            }
        }

        return [.. byUuid.Values.OrderByDescending(r => r.GetProperty("time").GetDateTimeOffset())];
    }

    /// <summary>
    /// The captured calls again, as they were, with the newest at <paramref name="newestAt"/>: every call added since is
    /// gone. A demo starts over this way.
    /// </summary>
    public void StartOver(DateTimeOffset newestAt)
    {
        lock (_adding)
        {
            _calls = LoadCalls(_directory);
            ShiftTimes(newestAt);
        }
    }

    /// <summary>Adds a call record, as if it had just happened: say, one with events the capture has no call for.</summary>
    public void AddCall(string recordJson)
    {
        using var document = JsonDocument.Parse(recordJson);
        lock (_adding)
        {
            List<JsonElement> next = [.. _calls, document.RootElement.Clone()];
            next.Sort((a, b) => b.GetProperty("time").GetDateTimeOffset().CompareTo(a.GetProperty("time").GetDateTimeOffset()));
            _calls = next;
        }
    }

    /// <summary>
    /// Adds a call record, or replaces the one with the same uuid: a call that is still going changes as it rings, is
    /// answered and ends, and the console answers with its latest state each time it is asked.
    /// </summary>
    public void UpsertCall(string recordJson)
    {
        using var document = JsonDocument.Parse(recordJson);
        var record = document.RootElement.Clone();
        var uuid = record.GetProperty("uuid").GetString();
        lock (_adding)
        {
            List<JsonElement> next = [.. _calls.Where(c => c.GetProperty("uuid").GetString() != uuid), record];
            next.Sort((a, b) => b.GetProperty("time").GetDateTimeOffset().CompareTo(a.GetProperty("time").GetDateTimeOffset()));
            _calls = next;
        }
    }

    private const string VoicemailAudioPath = "/proxy/talk/api/voicemail/recording";
    private readonly Dictionary<string, string> _voicemail = new(StringComparer.Ordinal);

    /// <summary>Console file paths the fake console holds a voicemail message for.</summary>
    public IReadOnlyCollection<string> VoicemailPaths => _voicemail.Keys;

    private const string RecordingPath = "/proxy/talk/api/call_log/recording/";
    private readonly Dictionary<string, string> _captured = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _capturedAudio = new(StringComparer.Ordinal);

    /// <summary>Replaces what a path answers with, to stand in for a console that has changed: say, a Talk update.</summary>
    public Dictionary<string, string> Overrides { get; } = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _recordings = new(StringComparer.Ordinal);

    /// <summary>Call uuids the fake console holds a recording for.</summary>
    public IReadOnlyCollection<string> RecordingUuids => _recordings.Keys;

    /// <summary>Answers recording downloads with this status instead, to stand in for a console error.</summary>
    public HttpStatusCode? RecordingStatus { get; set; }

    public int CallCount => _calls.Count;

    /// <summary>Paths requested, in order, for asserting how a client pages.</summary>
    public List<string> Requests { get; } = [];

    /// <summary>Makes the next call-log response a shape TalkWatch does not recognise, as a firmware update might.</summary>
    public bool Drifted { get; set; }

    /// <summary>Hides the newest <c>n</c> calls, to simulate calls arriving between polls.</summary>
    public int HideNewest { get; set; }

    /// <summary>Sends the newest <c>n</c> calls with no direction or status, as the real console did on its first run.</summary>
    public int BlankDirectionNewest { get; set; }

    /// <summary>
    /// Serves these calls (by uuid) with another status. The capture has no inbound call that was missed, so tests
    /// that need one make it from a real call.
    /// </summary>
    public Dictionary<string, string> Statuses { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Behaves like a console that tracks sessions: data requests need a signed-in session, and <see cref="ExpireSession"/>
    /// ends it, so the next data request gets 401 until the client signs in again.
    /// </summary>
    public bool RequireSession { get; set; }

    public void ExpireSession() => _signedIn = false;

    /// <summary>Refuses sign-in with 429 and this Retry-After, as UniFi OS does when sign-ins come too fast.</summary>
    public TimeSpan? RateLimitSignIn { get; set; }

    /// <summary>Throws from the next call-log request, standing in for any failure TalkWatch does not expect.</summary>
    public bool ThrowOnCallLog { get; set; }

    public int SignIns => Requests.Count(r => r == "/api/auth/login");

    private bool _signedIn;

    public static string DefaultDirectory
    {
        get
        {
            var directory = AppContext.BaseDirectory;
            while (directory is not null && !File.Exists(Path.Combine(directory, "TalkWatch.slnx")))
            {
                directory = Path.GetDirectoryName(directory);
            }

            return Path.Combine(directory ?? throw new InvalidOperationException("Repository root not found."), "tests", "fixtures", "talk-5.3.2");
        }
    }

    public HttpClient CreateClient() => new(this) { BaseAddress = new Uri("https://console.test") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        Requests.Add(uri.PathAndQuery);

        if (uri.AbsolutePath == "/api/auth/login")
        {
            if (RateLimitSignIn is { } wait)
            {
                var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(wait);
                return limited;
            }

            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var login = JsonDocument.Parse(body);
            var ok = login.RootElement.GetProperty("username").GetString() == Username
                && login.RootElement.GetProperty("password").GetString() == Password;
            _signedIn = ok;
            var response = new HttpResponseMessage(ok ? HttpStatusCode.OK : HttpStatusCode.Forbidden);
            response.Headers.Add("X-Csrf-Token", "fixture-csrf");
            return response;
        }

        if (uri.AbsolutePath == "/proxy/talk/api/call_log")
        {
            if (RequireSession && !_signedIn)
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }

            if (ThrowOnCallLog)
            {
                throw new InvalidOperationException("Simulated failure nobody planned for.");
            }

            if (Drifted)
            {
                return Json("""{"records":[{"uuid":42,"time":"not a time"}],"total_count":"many"}""");
            }

            var query = HttpUtility.ParseQueryString(uri.Query);
            var page = int.Parse(query["page"] ?? "0", CultureInfo.InvariantCulture);
            var size = int.Parse(query["items_per_page"] ?? "25", CultureInfo.InvariantCulture);
            var visible = _calls.Skip(HideNewest).ToList();
            var records = visible.Skip(page * size).Take(size).Select((r, i) => page * size + i < BlankDirectionNewest ? Blanked(r) : Restated(r));
            return Json($$"""{"records":[{{string.Join(',', records)}}],"total_count":{{visible.Count}}}""");
        }

        if (uri.AbsolutePath.StartsWith(RecordingPath, StringComparison.Ordinal))
        {
            if (RequireSession && !_signedIn)
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }

            if (RecordingStatus is { } status)
            {
                return new HttpResponseMessage(status);
            }

            return _recordings.TryGetValue(uri.AbsolutePath[RecordingPath.Length..], out var file)
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(File.ReadAllBytes(file)) { Headers = { ContentType = new("audio/mpeg") } },
                }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        if (uri.AbsolutePath == VoicemailAudioPath && request.Method == HttpMethod.Post)
        {
            if (RequireSession && !_signedIn)
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }

            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            return _voicemail.TryGetValue(body.RootElement.GetProperty("path").GetString() ?? "", out var audio)
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(File.ReadAllBytes(audio)) { Headers = { ContentType = new("audio/mpeg") } },
                }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        if (Overrides.TryGetValue(uri.AbsolutePath, out var replaced))
        {
            return Json(replaced);
        }

        if (_capturedAudio.TryGetValue(uri.AbsolutePath, out var audioFile))
        {
            if (RequireSession && !_signedIn)
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(File.ReadAllBytes(audioFile)) { Headers = { ContentType = new("audio/mpeg") } },
            };
        }

        if (_captured.TryGetValue(uri.AbsolutePath, out var capturedFile))
        {
            if (RequireSession && !_signedIn)
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }

            return Json(File.ReadAllText(capturedFile));
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static string Blanked(JsonElement record)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(record.GetRawText())!;
        node["direction"] = null;
        node["status"] = null;
        return node.ToJsonString();
    }

    private string Restated(JsonElement record)
    {
        if (!Statuses.TryGetValue(record.GetProperty("uuid").GetString()!, out var status))
        {
            return record.GetRawText();
        }

        var node = System.Text.Json.Nodes.JsonNode.Parse(record.GetRawText())!;
        node["status"] = status;
        return node.ToJsonString();
    }

    /// <summary>
    /// Rewrites every JSON answer before it is sent: a demo gives the capture's pseudonyms readable names this way, with
    /// the fixtures themselves left as they are.
    /// </summary>
    public Func<string, string>? Rewrite { get; set; }

    private HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(Rewrite is null ? json : Rewrite(json), Encoding.UTF8, "application/json") };

    /// <summary>Every call in the fixtures, deserialised as TalkWatch reads them.</summary>
    public IReadOnlyList<CallLogRecord> Calls() =>
        [.. _calls.Select(c => c.Deserialize<CallLogRecord>(TalkJson.Options)!)];
}
