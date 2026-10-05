using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using TalkWatch.Core.Calls;

namespace TalkWatch.Core.Talk;

/// <summary>
/// Reads Talk through the console's local API, signed in as a console user (by username, not e-mail address).
/// </summary>
/// <remarks>
/// One sign-in serves many requests: UniFi OS rate-limits sign-in (HTTP 429), so a client is meant to be kept and
/// reused. When the console ends the session (401 or 403 on a data request), the client signs in again once and
/// retries. The session cookie lives in the HTTP handler, so the handler must outlive the client's use.
/// </remarks>
public sealed class TalkClient(HttpClient http)
{
    private static readonly string[] CsrfHeaders = ["X-Updated-Csrf-Token", "X-Csrf-Token"];
    private string? _csrfToken;
    private (string Username, string Password)? _credentials;

    public bool IsSignedIn { get; private set; }

    /// <summary>The console ended the session some other way (a refused WebSocket upgrade): sign in again next time.</summary>
    public void SessionEnded() => IsSignedIn = false;

    public async Task SignInAsync(string username, string password, CancellationToken cancellationToken)
    {
        _credentials = (username, password);
        IsSignedIn = false;
        using var response = await http.PostAsJsonAsync(new Uri("/api/auth/login", UriKind.Relative),
            new { username, password, rememberMe = false }, cancellationToken);
        ThrowIfRateLimited(response);
        if (!response.IsSuccessStatusCode)
        {
            throw new TalkApiException($"Sign-in was refused with HTTP {(int)response.StatusCode}.");
        }

        Remember(response);
        IsSignedIn = true;
    }

    /// <summary>One page of the call log, newest first. Pages count from 0.</summary>
    public async Task<CallLogPage> GetCallLogPageAsync(int page, int itemsPerPage, CancellationToken cancellationToken)
    {
        var path = string.Create(CultureInfo.InvariantCulture,
            $"/proxy/talk/api/call_log?page={page}&items_per_page={itemsPerPage}&sort_key=time&sort_order=desc");
        var (result, raw) = await GetAsync<CallLogPage>(path, cancellationToken);
        return (result ?? throw new TalkApiException("The call log came back empty.")) with { RawJson = raw };
    }

    /// <summary>
    /// The console's UniFi OS and Talk versions and the channels each follows. A pre-release channel gets endpoint
    /// changes before the general release does.
    /// </summary>
    public async Task<ConsoleVersions> GetVersionsAsync(CancellationToken cancellationToken)
    {
        var (talk, _) = await GetAsync<TalkInfo>("/proxy/talk/api/info", cancellationToken);
        var (system, _) = await GetAsync<ConsoleSystemInfo>("/proxy/talk/api/ucore/system_info", cancellationToken);
        return new ConsoleVersions(system?.Hardware?.FirmwareVersion, system?.ReleaseChannel, talk?.Version, talk?.UpdateChannel);
    }

    /// <summary>Users, ring groups and numbers: every line the console knows, with the configuration that routes calls.</summary>
    public async Task<LineDirectory> GetDirectoryAsync(NumberNormaliser numbers, CancellationToken cancellationToken)
    {
        var users = await GetUsersAsync(cancellationToken);
        var (groups, _) = await GetAsync<List<TalkGroup>>("/proxy/talk/api/group_list", cancellationToken);
        var (dids, _) = await GetAsync<List<TalkNumber>>("/proxy/talk/api/number/list", cancellationToken);
        return new LineDirectory(users, groups ?? [], dids ?? [], numbers) { Contacts = await GetContactsAsync(cancellationToken) };
    }

    /// <summary>
    /// Talk's contacts. An account that may not read them (Talk answers 403) gets none rather than no directory: they
    /// only put names to numbers.
    /// </summary>
    public async Task<IReadOnlyList<TalkContact>> GetContactsAsync(CancellationToken cancellationToken)
    {
        using var response = await GetResponseAsync("/proxy/talk/api/contacts", HttpCompletionOption.ResponseContentRead, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            return [];
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new TalkApiException($"GET /proxy/talk/api/contacts returned HTTP {(int)response.StatusCode}.");
        }

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<TalkContact>>(await response.Content.ReadAsStringAsync(cancellationToken), TalkJson.Options) ?? [];
        }
        catch (System.Text.Json.JsonException e)
        {
            throw new TalkSchemaException($"GET /proxy/talk/api/contacts returned a shape TalkWatch does not recognise: {e.Message}", e);
        }
    }

    /// <summary>A page of Talk's transcripts, newest call first. Pages count from 1, as the Talk app asks for them.</summary>
    public async Task<(IReadOnlyList<TalkTranscript> Transcripts, int Total)> GetTranscriptsAsync(int page, int size, CancellationToken cancellationToken)
    {
        var path = $"/proxy/talk/api/transcript?page={page}&size={size}&sortBy=call_time&sortDirection=DESC";
        using var response = await GetResponseAsync(path, HttpCompletionOption.ResponseContentRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new TalkApiException($"GET /proxy/talk/api/transcript returned HTTP {(int)response.StatusCode}.");
        }

        try
        {
            return TalkTranscript.ParsePage(await response.Content.ReadAsStringAsync(cancellationToken));
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new TalkSchemaException($"GET /proxy/talk/api/transcript returned a shape TalkWatch does not recognise: {e.Message}", e);
        }
    }

    /// <summary>The Talk account's standing: active, paid, unblocked.</summary>
    public async Task<TalkAccount> GetAccountAsync(CancellationToken cancellationToken) =>
        (await GetAsync<TalkAccount>("/proxy/talk/api/install", cancellationToken)).Value ?? new TalkAccount();

    /// <summary>Whether call recording and AI transcription are on.</summary>
    public async Task<TalkSettings> GetSettingsAsync(CancellationToken cancellationToken) =>
        (await GetAsync<TalkSettings>("/proxy/talk/api/setting/config", cancellationToken)).Value ?? new TalkSettings();

    /// <summary>The switchboard tree: every switchboard, its menu options and where they lead.</summary>
    public async Task<IReadOnlyList<SwitchboardNode>> GetSwitchboardAsync(CancellationToken cancellationToken)
    {
        const string path = "/proxy/talk/api/switchboard";
        using var response = await GetResponseAsync(path, HttpCompletionOption.ResponseContentRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new TalkApiException($"GET {path} returned HTTP {(int)response.StatusCode}.");
        }

        try
        {
            return Switchboard.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new TalkSchemaException($"GET {path} returned a shape TalkWatch does not recognise: {e.Message}", e);
        }
    }

    /// <summary>A switchboard greeting or prompt (MP3), or null when the console has no such file. Greetings are short, so it is read whole.</summary>
    public async Task<byte[]?> GetSwitchboardAudioAsync(string fileName, CancellationToken cancellationToken)
    {
        var path = "/proxy/talk/api/switchboard/audio/" + Uri.EscapeDataString(fileName);
        using var response = await GetResponseAsync(path, HttpCompletionOption.ResponseContentRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new TalkApiException($"GET /proxy/talk/api/switchboard/audio returned HTTP {(int)response.StatusCode}.");
        }

        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    /// <summary>The Talk user directory.</summary>
    public async Task<IReadOnlyList<TalkUser>> GetUsersAsync(CancellationToken cancellationToken)
    {
        var (users, _) = await GetAsync<List<TalkUser>>("/proxy/talk/api/users", cancellationToken);
        return users ?? [];
    }

    /// <summary>
    /// A call's recording as Talk serves it (MP3), or null when the console no longer has it. The caller owns the
    /// response and must dispose it; the body is streamed, not buffered, since recordings run to megabytes.
    /// </summary>
    public async Task<HttpResponseMessage?> GetRecordingAsync(string callUuid, CancellationToken cancellationToken)
    {
        var path = "/proxy/talk/api/call_log/recording/" + Uri.EscapeDataString(callUuid);
        var response = await GetResponseAsync(path, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            response.Dispose();
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            response.Dispose();
            throw new TalkApiException($"GET /proxy/talk/api/call_log/recording returned HTTP {status}.");
        }

        return response;
    }

    /// <summary>
    /// A call's routing events as they stand now, while it is still going: the call started, the menu played, a key
    /// pressed, an option entered, who rang. Talk announces each change as CALL_EVENTS_UPDATED with only the call's id;
    /// this is how the change itself is read. Empty when Talk has none for the call.
    /// </summary>
    public async Task<IReadOnlyList<CallEvent>> GetCallEventsAsync(string callUuid, CancellationToken cancellationToken)
    {
        var (events, _) = await GetAsync<List<CallEvent>>("/proxy/talk/api/call_log/flow/" + Uri.EscapeDataString(callUuid), cancellationToken);
        return events ?? [];
    }

    /// <summary>
    /// Where a call's voicemail message is kept on the console and how long it is, or null when the call has none. Read
    /// before <see cref="GetVoicemailAudioAsync"/>, which fetches the message by that path.
    /// </summary>
    public async Task<VoicemailInfo?> GetVoicemailAsync(string callUuid, CancellationToken cancellationToken)
    {
        var path = "/proxy/talk/api/voicemail/data/" + Uri.EscapeDataString(callUuid);
        using var response = await GetResponseAsync(path, HttpCompletionOption.ResponseContentRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new TalkApiException($"GET /proxy/talk/api/voicemail/data returned HTTP {(int)response.StatusCode}.");
        }

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<VoicemailInfo>(await response.Content.ReadAsStringAsync(cancellationToken), TalkJson.Options);
        }
        catch (System.Text.Json.JsonException e)
        {
            throw new TalkSchemaException($"GET /proxy/talk/api/voicemail/data returned a shape TalkWatch does not recognise: {e.Message}", e);
        }
    }

    /// <summary>
    /// A voicemail message as Talk serves it (MP3), by the file path <see cref="GetVoicemailAsync"/> gave, or null when
    /// the console no longer has it. A POST, as the Talk app does it; playing a message this way does not mark it read.
    /// The caller owns the response and must dispose it.
    /// </summary>
    public async Task<HttpResponseMessage?> GetVoicemailAudioAsync(string filePath, CancellationToken cancellationToken)
    {
        var body = System.Text.Json.JsonSerializer.Serialize(new { path = filePath });
        var response = await SendWithSessionAsync(
            () => new HttpRequestMessage(HttpMethod.Post, new Uri("/proxy/talk/api/voicemail/recording", UriKind.Relative))
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            },
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            response.Dispose();
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            response.Dispose();
            throw new TalkApiException($"POST /proxy/talk/api/voicemail/recording returned HTTP {status}.");
        }

        return response;
    }

    private async Task<(T? Value, string Raw)> GetAsync<T>(string path, CancellationToken cancellationToken)
    {
        using var response = await GetResponseAsync(path, HttpCompletionOption.ResponseContentRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new TalkApiException($"GET {path.Split('?')[0]} returned HTTP {(int)response.StatusCode}.");
        }

        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        try
        {
            return (System.Text.Json.JsonSerializer.Deserialize<T>(raw, TalkJson.Options), raw);
        }
        catch (System.Text.Json.JsonException e)
        {
            // The shape no longer matches what TalkWatch was built against: the drift case the design plans for.
            throw new TalkSchemaException($"GET {path.Split('?')[0]} returned a shape TalkWatch does not recognise: {e.Message}", e);
        }
    }

    private Task<HttpResponseMessage> GetResponseAsync(string path, HttpCompletionOption completion, CancellationToken cancellationToken) =>
        SendWithSessionAsync(() => new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative)), completion, cancellationToken);

    /// <summary>
    /// A request with the session handled: signs in again once if the console ended the session, and turns 429 into its
    /// exception. The request is made by <paramref name="create"/>, since a retried request needs a fresh one.
    /// </summary>
    private async Task<HttpResponseMessage> SendWithSessionAsync(Func<HttpRequestMessage> create, HttpCompletionOption completion, CancellationToken cancellationToken)
    {
        var response = await SendAsync(create, completion, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden && _credentials is { } credentials)
        {
            // The session ended on the console's side. Sign in again once; a second refusal is reported as it is.
            response.Dispose();
            await SignInAsync(credentials.Username, credentials.Password, cancellationToken);
            response = await SendAsync(create, completion, cancellationToken);
        }

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            using (response)
            {
                ThrowIfRateLimited(response);
            }
        }

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            IsSignedIn = false;
        }

        return response;
    }

    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> create, HttpCompletionOption completion, CancellationToken cancellationToken)
    {
        using var request = create();
        if (_csrfToken is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Csrf-Token", _csrfToken);
        }

        var response = await http.SendAsync(request, completion, cancellationToken);
        Remember(response);
        return response;
    }

    private static void ThrowIfRateLimited(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter is { Delta: { } delta } ? delta
                : response.Headers.RetryAfter is { Date: { } date } ? date - DateTimeOffset.UtcNow
                : (TimeSpan?)null;
            throw new TalkRateLimitedException(retryAfter);
        }
    }

    private void Remember(HttpResponseMessage response)
    {
        foreach (var name in CsrfHeaders)
        {
            if (response.Headers.TryGetValues(name, out var values) && values.FirstOrDefault() is { Length: > 0 } token)
            {
                _csrfToken = token;
                return;
            }
        }
    }
}

public class TalkApiException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Talk answered, but not in a shape TalkWatch recognises. Writes stop until a fixed release ships.</summary>
public sealed class TalkSchemaException(string message, Exception inner) : TalkApiException(message, inner);

/// <summary>The console refused with HTTP 429. <see cref="RetryAfter"/> is how long it asked for, when it said.</summary>
public sealed class TalkRateLimitedException(TimeSpan? retryAfter)
    : TalkApiException($"The console refused with HTTP 429 (too many requests){(retryAfter is { } wait ? $"; it asked for {wait.TotalSeconds:0} seconds" : "")}.")
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}
