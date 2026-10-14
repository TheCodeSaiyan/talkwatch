using System.Globalization;
using TalkWatch.Core.Talk;
using TalkWatch.Replay;

namespace TalkWatch.Web.Services;

/// <summary>A scenario the demo's bar can play: what it is called, the group it is under, and what it shows.</summary>
public sealed record DemoScenario(string Id, string Category, string Label, string Hint);

/// <summary>
/// Scenarios a demo visitor can set going from the bar at the foot of every page: a call answered, missed or left as
/// voicemail; a caller going through the main switchboard's menu; the calls that raise alerts; someone going busy. Each
/// plays out in real time on the replayed console, a step every few seconds, and is read in at once after each step,
/// so Now and Operator show it as it happens. Only in demo mode, where nothing here can reach a real console.
/// </summary>
public sealed partial class DemoScenarios(
    FixtureConsole console, CallLogPoller poller, LiveStatus live, LineDirectorySync directory, LiveListener listener, TimeProvider clock,
    ILogger<DemoScenarios> logger)
{
    public static readonly IReadOnlyList<DemoScenario> All =
    [
        new("answered", "Calls", "Answered", "Rings the support team, someone picks up, they talk, it ends."),
        new("missed", "Calls", "Missed", "Rings the support team until the caller gives up."),
        new("voicemail", "Calls", "Voicemail", "Nobody answers, so the caller leaves a message."),
        new("outside", "Calls", "Outside phone answers", "Put through to an outside phone, which picks up."),
        new("parked", "More calls", "Parked and picked up", "Answered, parked for a moment, then picked up again."),
        new("call-back", "More calls", "Missed, then called back", "A missed call, then someone rings the caller back, which clears it from the call-back list."),
        new("poor-line", "More calls", "Poor line", "An answered call Talk scores 48 of 100 for quality, which the example flows alert on."),
        new("rush", "More calls", "A rush of calls", "Six callers in about a minute: some answered, some missed, one to voicemail, one through the menu."),
        new("menu", "Switchboard", "Presses 1, then 2", "Into Sales and billing, then Billing, then rings the team."),
        new("wrong-key", "Switchboard", "Presses a wrong key", "Presses 7, which isn't an option, then 2 for Technical support."),
        new("menu-hangup", "Switchboard", "Hangs up in the menu", "Presses 1, listens, and hangs up before choosing again."),
        new("after-hours", "Switchboard", "Out of hours", "The main switchboard's opening hours close for a minute, so calls take its out-of-hours branch. Operator's badges say which branch is open."),
        new("no-audio", "Switchboard", "A prompt loses its audio", "Option 2's prompt has no audio for a minute. Its node on Operator says so, and its panel says why."),
        new("broken-draft", "Switchboard", "A draft option", "A switched-off option 4 with nothing under it appears for a minute: Off to operators, and a draft's problem in the admin panel."),
        new("missed-alert", "Alerts", "Missed call alert", "A missed call, which the example flows alert on."),
        new("hangup-alert", "Alerts", "Hang-up alert", "Hangs up during the greeting, which the example flows alert on."),
        new("repeat-caller", "Alerts", "Caller tries again", "The same caller is missed twice in a row, which the example flows treat as urgent."),
        new("dnd", "People", "Do not disturb", "Someone in the team goes do-not-disturb for a minute."),
        new("busy", "People", "On a call", "Someone in the team is on a call for a minute."),
        new("handset-offline", "Phones", "Handset goes offline", "A desk phone drops off the network for a minute, which raises an alert."),
        new("cloud-down", "Phones", "Talk loses its cloud", "Talk can't reach its cloud services for a minute; the strip under the rail says so on the next page you open."),
        new("degraded", "Phones", "Talk under strain", "Talk reports it is running degraded for a minute; the strip under the rail says so on the next page you open."),
    ];

    /// <summary>How many scenarios can play at once: the demo is shared, and a crowd of calls says nothing more.</summary>
    public const int MaxPlaying = 4;

    private int _playing;

    /// <summary>Scenarios playing now.</summary>
    public int Playing => _playing;

    /// <summary>Raised when a scenario starts or finishes, so the bar can say how many are playing.</summary>
    public event Action? Changed;

    // The main switchboard, on its own number, and the options a caller takes through it, as the capture has them.
    private const string MainNumber = "+441174960404", DemoLine = "+441144960042";
    private const int MainSwitchboard = 15, FrontDesk = 45;
    private static readonly string[] Callers =
        ["+447700900232", "+447700900880", "+447700900417", "+447700900111", "+447700900345", "+447700900678", "+447700900519", "+447700900790"];
    private int _caller;

    /// <summary>Starts a scenario, done when it has played out; null when it is unknown or as many as allowed are playing.</summary>
    public Task? Start(string id)
    {
        if (All.All(s => s.Id != id))
        {
            return null;
        }

        if (Interlocked.Increment(ref _playing) > MaxPlaying)
        {
            Interlocked.Decrement(ref _playing);
            return null;
        }

        Changed?.Invoke();
        return Task.Run(async () =>
        {
            try
            {
                await PlayAsync(id, CancellationToken.None);
            }
#pragma warning disable CA1031 // A scenario that fails is logged; the demo goes on.
            catch (Exception e)
#pragma warning restore CA1031
            {
                LogFailed(logger, id, e);
            }
            finally
            {
                Interlocked.Decrement(ref _playing);
                Changed?.Invoke();
            }
        });
    }

    private Task PlayAsync(string id, CancellationToken ct) => id switch
    {
        "answered" => RingAsync(Answer.Member, ct),
        "missed" or "missed-alert" => RingAsync(Answer.Nobody, ct),
        "parked" => ParkedAsync(ct),
        "call-back" => CallBackAsync(ct),
        "poor-line" => RingAsync(Answer.Member, ct, quality: 48),
        "rush" => RushAsync(ct),
        "repeat-caller" => RepeatCallerAsync(ct),
        "handset-offline" => HandsetOfflineAsync(ct),
        "cloud-down" => TalkStateAsync(cloud: true, ct),
        "degraded" => TalkStateAsync(cloud: false, ct),
        "voicemail" => RingAsync(Answer.Voicemail, ct),
        "outside" => RingAsync(Answer.Outside, ct),
        "menu" => MenuAsync([(16, 1, "Title 11"), (42, 2, "Title 15")], wrongKey: false, hangUp: false, ct),
        "wrong-key" => MenuAsync([(18, 2, "Title 16")], wrongKey: true, hangUp: false, ct),
        "menu-hangup" => MenuAsync([(16, 1, "Title 11")], wrongKey: false, hangUp: true, ct),
        "hangup-alert" => GreetingHangUpAsync(ct),
        "after-hours" or "no-audio" or "broken-draft" => SetupAsync(id, ct),
        "dnd" => PresenceAsync(dnd: true, ct),
        "busy" => PresenceAsync(dnd: false, ct),
        _ => Task.CompletedTask,
    };

    private enum Answer { Member, Nobody, Voicemail, Outside }

    // A call to the demo's line that rings the support team, and how it ends.
    private async Task RingAsync(Answer answer, CancellationToken ct, string? from = null, int? quality = null)
    {
        var call = NewCall(DemoLine, from);
        var group = Group();
        if (group is not null)
        {
            call.Fields["to_group_id"] = Quote(group.Id);
        }

        call.Event("call_started");
        call.Event("seq_call_trying_endpoints", answer == Answer.Outside ? $$"""{"contact_uuids":["{{DemoActivity.ContactWithEmail}}"]}""" : "{}");
        await PushAsync(call, ct);

        switch (answer)
        {
            case Answer.Member or Answer.Outside:
                await WaitAsync(7, ct);
                call.Status = "accepted";
                if (answer == Answer.Outside)
                {
                    call.Event("call_accepted", $$"""{"accepted_by_contact_uuid":"{{DemoActivity.ContactWithEmail}}"}""");
                }
                else
                {
                    call.Event("call_accepted");
                    if (Member() is { } member)
                    {
                        call.Fields["answered_by_user_uuid"] = Quote(member);
                    }
                }

                await PushAsync(call, ct);
                await WaitAsync(20, ct);
                if (quality is { } score)
                {
                    call.Fields["quality_score"] = score.ToString(CultureInfo.InvariantCulture);
                }

                break;

            case Answer.Nobody:
                await WaitAsync(18, ct);
                call.Status = "cancelled";
                break;

            case Answer.Voicemail:
                await WaitAsync(10, ct);
                call.Status = "accepted";
                var to = $$"""{"recipient_user_uuids":["{{Member()}}"]}""";
                call.Event("call_sent_to_voicemail", to);
                await PushAsync(call, ct);
                await WaitAsync(12, ct);
                call.Event("vm_msg_recorded", to);
                break;
        }

        await EndAsync(call, ct);
    }

    // A call to the main switchboard: each choice a few seconds apart, then put through to the support team, or not.
    private async Task MenuAsync((int Item, int Key, string Title)[] choices, bool wrongKey, bool hangUp, CancellationToken ct)
    {
        var call = NewCall(MainNumber);
        call.Fields["to_smart_attendant_id"] = MainSwitchboard.ToString(CultureInfo.InvariantCulture);
        call.Event("call_started", $$"""{"to_smart_attendant_id":{{MainSwitchboard}}}""");
        await PushAsync(call, ct);

        if (wrongKey)
        {
            await WaitAsync(6, ct);
            call.Event("keypress", """{"key":"7"}""");
            await PushAsync(call, ct);
        }

        foreach (var (item, key, title) in choices)
        {
            await WaitAsync(6, ct);
            call.Event("keypress", $$"""{"key":"{{key}}"}""");
            call.Event("entered_sa_menu", $$"""{"sa_id":{{MainSwitchboard}},"sa_item_id":{{item}},"sa_item_key":{{key}},"sa_item_type":"ivr","sa_item_title":"{{title}}"}""");
            await PushAsync(call, ct);
        }

        if (hangUp)
        {
            await WaitAsync(8, ct);
            await EndAsync(call, ct);
            return;
        }

        await WaitAsync(5, ct);
        if (Group() is { } group)
        {
            call.Fields["to_group_id"] = Quote(group.Id);
        }

        call.Event("seq_call_trying_endpoints");
        await PushAsync(call, ct);
        await WaitAsync(8, ct);
        call.Status = "accepted";
        call.Event("call_accepted");
        if (Member() is { } member)
        {
            call.Fields["answered_by_user_uuid"] = Quote(member);
        }

        await PushAsync(call, ct);
        await WaitAsync(15, ct);
        await EndAsync(call, ct);
    }

    // A call to the demo's line, whose switchboard greets it, and the caller hangs up before it has finished.
    private async Task GreetingHangUpAsync(CancellationToken ct)
    {
        var call = NewCall(DemoLine);
        call.Fields["to_smart_attendant_id"] = FrontDesk.ToString(CultureInfo.InvariantCulture);
        call.Event("call_started", $$"""{"to_smart_attendant_id":{{FrontDesk}}}""");
        await PushAsync(call, ct);
        await WaitAsync(6, ct);
        await EndAsync(call, ct);
    }

    // Answered, then parked and picked up again, as Talk 5.4.5 logs it: who parked it and which lot, then who picked it up.
    private async Task ParkedAsync(CancellationToken ct)
    {
        var call = NewCall(DemoLine);
        var member = Member();
        if (Group() is { } group)
        {
            call.Fields["to_group_id"] = Quote(group.Id);
        }

        call.Event("call_started");
        call.Event("seq_call_trying_endpoints");
        await PushAsync(call, ct);
        await WaitAsync(6, ct);
        call.Status = "accepted";
        call.Event("call_accepted");
        if (member is not null)
        {
            call.Fields["answered_by_user_uuid"] = Quote(member);
        }

        await PushAsync(call, ct);
        await WaitAsync(10, ct);
        call.Event("call_parked", $$"""{"parking_lot_uuid":"{{ParkingLot}}","parked_by_user_uuid":"{{member}}"}""");
        await PushAsync(call, ct);
        await WaitAsync(20, ct);
        call.Event("call_unparked", $$"""{"unparked_by_user_uuid":"{{member}}"}""");
        await PushAsync(call, ct);
        await WaitAsync(15, ct);
        await EndAsync(call, ct);
    }

    // A missed call, then someone in the team rings the caller back: the outbound call to their number returns it.
    private async Task CallBackAsync(CancellationToken ct)
    {
        var caller = NextCaller();
        await RingAsync(Answer.Nobody, ct, from: caller);
        await WaitAsync(10, ct);

        var member = Member();
        var ext = live.Snapshot().Users.FirstOrDefault(u => u.Uuid == member)?.Ext ?? "0002";
        var call = new ScenarioCall(ext, caller, clock.GetUtcNow(), clock) { Direction = "out" };
        if (member is not null)
        {
            call.Fields["from_id"] = Quote(member);
        }

        call.Event("call_started", member is null ? "{}" : $$"""{"from_user_uuid":"{{member}}"}""");
        await PushAsync(call, ct);
        await WaitAsync(6, ct);
        call.Status = "accepted";
        call.Event("call_accepted");
        await PushAsync(call, ct);
        await WaitAsync(15, ct);
        await EndAsync(call, ct);
    }

    // The same caller missed twice, the second time a few seconds after giving up the first.
    private async Task RepeatCallerAsync(CancellationToken ct)
    {
        var caller = NextCaller();
        await RingAsync(Answer.Nobody, ct, from: caller);
        await WaitAsync(8, ct);
        await RingAsync(Answer.Nobody, ct, from: caller);
    }

    // Six callers inside about a minute, overlapping: what a busy moment looks like on Now. One scenario for the limit.
    private Task RushAsync(CancellationToken ct)
    {
        (int After, Func<Task> Play)[] callers =
        [
            (0, () => RingAsync(Answer.Member, ct)),
            (5, () => RingAsync(Answer.Nobody, ct)),
            (9, () => MenuAsync([(16, 1, "Title 11"), (42, 2, "Title 15")], wrongKey: false, hangUp: false, ct)),
            (16, () => RingAsync(Answer.Member, ct)),
            (24, () => RingAsync(Answer.Voicemail, ct)),
            (31, () => RingAsync(Answer.Nobody, ct)),
        ];
        return Task.WhenAll(callers.Select(async c =>
        {
            await WaitAsync(c.After, ct);
            await c.Play();
        }));
    }

    // A desk phone drops off the network for a minute and comes back. Through the live listener, as Talk's own message
    // would arrive, so the Handset offline alert is raised as it would be.
    private async Task HandsetOfflineAsync(CancellationToken ct)
    {
        if (live.Snapshot().Devices.FirstOrDefault(d => d.Status == "online") is not { } device)
        {
            return;
        }

        await DeviceAsync(device, "offline", ct);
        await WaitAsync(60, ct);
        await DeviceAsync(device, "online", ct);
    }

    private async Task DeviceAsync(LiveDevice device, string status, CancellationToken ct)
    {
        var data = System.Text.Json.JsonSerializer.Serialize(
            new[] { new { device.Mac, device.Model, DisplayName = device.Name, UserId = device.UserUuid, device.Ext, Status = status, SipReg = device.Registered } },
            TalkJson.Options);
        if (LiveMessage.Parse($$"""{"event":"{{LiveMessage.DevicesUpdated}}","data":{{data}}}""") is { } message)
        {
            await listener.HandleAsync(message, ct);
        }
    }

    // Talk loses its cloud services, or runs degraded, for a minute: /api/info says so, as Talk 5.4.5 reports it, and a
    // poll reads it at once. Both can be playing together, so the response is built from what is playing now.
    private async Task TalkStateAsync(bool cloud, CancellationToken ct)
    {
        if (cloud)
        {
            Interlocked.Increment(ref _cloudDown);
        }
        else
        {
            Interlocked.Increment(ref _degraded);
        }

        try
        {
            await SetInfoAsync(ct);
            await WaitAsync(60, ct);
        }
        finally
        {
            if (cloud)
            {
                Interlocked.Decrement(ref _cloudDown);
            }
            else
            {
                Interlocked.Decrement(ref _degraded);
            }

            await SetInfoAsync(ct);
        }
    }

    private async Task SetInfoAsync(CancellationToken ct)
    {
        if (_capturedInfo is null)
        {
            // Read before any override: the capture's own response, which each override adds to.
            using var client = console.CreateClient();
            _capturedInfo = await client.GetStringAsync(new Uri(InfoPath, UriKind.Relative), ct);
        }

        lock (_info)
        {
            if (_cloudDown == 0 && _degraded == 0)
            {
                console.Overrides.Remove(InfoPath);
            }
            else
            {
                var now = clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
                var info = System.Text.Json.Nodes.JsonNode.Parse(_capturedInfo)!.AsObject();
                info["cloud_connectivity"] = System.Text.Json.Nodes.JsonNode.Parse(_cloudDown > 0
                    ? $$"""{"degraded":true,"reason":"no_response","since":"{{now}}"}"""
                    : """{"degraded":false,"reason":null,"since":null}""");
                info["degradation"] = System.Text.Json.Nodes.JsonNode.Parse(_degraded > 0
                    ? $$"""{"degraded":true,"degradation_level":2,"load_level":null,"pressures":[],"targeted_for_suspension":false,"suspended_at":null,"since":"{{now}}"}"""
                    : """{"degraded":false,"degradation_level":0,"load_level":null,"pressures":[],"targeted_for_suspension":false,"suspended_at":null,"since":null}""");
                console.Overrides[InfoPath] = info.ToJsonString();
            }
        }

        await poller.RunOnceAsync(ct);
    }

    // The switchboard's setup changes for a minute: the response is built from the changes playing now, over the
    // capture's own, and read in at once, so Operator shows it on its next read.
    private async Task SetupAsync(string change, CancellationToken ct)
    {
        lock (_setup)
        {
            _setupPlaying.Add(change);
        }

        try
        {
            await SetSwitchboardAsync(ct);
            await WaitAsync(60, ct);
        }
        finally
        {
            lock (_setup)
            {
                _setupPlaying.Remove(change);
            }

            await SetSwitchboardAsync(ct);
        }
    }

    private async Task SetSwitchboardAsync(CancellationToken ct)
    {
        if (_capturedSwitchboard is null)
        {
            // Read before any override: the capture's own tree, which each change is made to.
            using var client = console.CreateClient();
            _capturedSwitchboard = await client.GetStringAsync(new Uri(SwitchboardPath, UriKind.Relative), ct);
        }

        lock (_setup)
        {
            if (_setupPlaying.Count == 0)
            {
                console.Overrides.Remove(SwitchboardPath);
            }
            else
            {
                var tree = System.Text.Json.Nodes.JsonNode.Parse(_capturedSwitchboard)!;
                var byId = tree["entities"]!["nodes"]!["byId"]!.AsObject();
                var nodes = byId.Select(p => p.Value).OfType<System.Text.Json.Nodes.JsonObject>().ToList();
                if (_setupPlaying.Contains("after-hours"))
                {
                    foreach (var hours in nodes.Where(n => (string?)n["type"] == "time" && n["is_default_time_node"]?.GetValue<bool>() != true))
                    {
                        hours["day_ranges"] = new System.Text.Json.Nodes.JsonObject();
                    }
                }

                if (_setupPlaying.Contains("no-audio") && byId["swb_18"] is System.Text.Json.Nodes.JsonObject option)
                {
                    option["missing_audio"] = true;
                }

                if (_setupPlaying.Contains("broken-draft") && byId[$"swb_{MainSwitchboard}"] is System.Text.Json.Nodes.JsonObject main)
                {
                    byId["swb_90"] = System.Text.Json.Nodes.JsonNode.Parse($$"""
                        {"id":"swb_90","internal_id":90,"key":4,"type":"ivr","title":"Holiday line","disabled":true,"greeting":"We're closed for the holiday.",
                         "greeting_type":"generated","generating_audio":false,"childNodeIds":[],"path":["container","swb_{{MainSwitchboard}}"]}
                        """);
                    main["childNodeIds"]?.AsArray().Add("swb_90");
                }

                console.Overrides[SwitchboardPath] = tree.ToJsonString();
            }
        }

        await directory.RefreshAsync(ct);
    }

    private const string SwitchboardPath = "/proxy/talk/api/switchboard";
    private readonly Lock _setup = new();
    private readonly HashSet<string> _setupPlaying = [];
    private string? _capturedSwitchboard;

    private const string InfoPath = "/proxy/talk/api/info", ParkingLot = "c4d04912-1f2a-4712-bf86-1571832bc550";
    private readonly Lock _info = new();
    private string? _capturedInfo;
    private int _cloudDown, _degraded;

    // Someone in the team goes do-not-disturb, or onto a call, for a minute, then back.
    private async Task PresenceAsync(bool dnd, CancellationToken ct)
    {
        var person = Member() ?? (live.Snapshot().Users is [var first, ..] ? first.Uuid : null);
        if (person is null)
        {
            return;
        }

        Presence(person, dnd ? """{"status":"dnd"}""" : """{"has_active_calls":true}""");
        await WaitAsync(60, ct);
        Presence(person, dnd ? """{"status":"available"}""" : """{"has_active_calls":false}""");
    }

    private void Presence(string user, string item)
    {
        if (LiveMessage.Parse($$$"""{"event":"{{{LiveMessage.UserStoreUpdated}}}","data":{"user_uuid":"{{{user}}}","item":{{{item}}}}}""") is { } message)
        {
            live.Apply(message, clock.GetUtcNow());
        }
    }

    private ScenarioCall NewCall(string to, string? from = null) => new(from ?? NextCaller(), to, clock.GetUtcNow(), clock);

    private string NextCaller() => Callers[Interlocked.Increment(ref _caller) % Callers.Length];

    private async Task EndAsync(ScenarioCall call, CancellationToken ct)
    {
        call.Event("call_hangup");
        call.Duration = (int)(clock.GetUtcNow() - call.Started).TotalSeconds;
        if (call.Status == "ringing")
        {
            call.Status = "accepted";
        }

        await PushAsync(call, ct);
    }

    // The call as the console now has it, read in straight away rather than at the next poll.
    private async Task PushAsync(ScenarioCall call, CancellationToken ct)
    {
        console.UpsertCall(call.Json());
        await poller.RunOnceAsync(ct);
    }

    private Task WaitAsync(int seconds, CancellationToken ct) => Task.Delay(TimeSpan.FromSeconds(seconds), clock, ct);

    private TalkGroup? Group() => directory.Current.Groups.FirstOrDefault(g => g.MemberList is { Count: > 0 });

    private string? Member() => Group()?.MemberList?[0];


    private static string Quote(string value) => "\"" + value + "\"";

    private sealed class ScenarioCall(string from, string to, DateTimeOffset started, TimeProvider clock)
    {
        private readonly string _uuid = Guid.NewGuid().ToString();
        private readonly List<string> _events = [];

        public DateTimeOffset Started => started;

        public string Status { get; set; } = "ringing";

        public int Duration { get; set; }

        public string Direction { get; init; } = "in";

        /// <summary>Further fields of the record, as raw JSON values.</summary>
        public Dictionary<string, string> Fields { get; } = new(StringComparer.Ordinal);

        public void Event(string name, string data = "{}") =>
            _events.Add($$"""{"time":"{{At(clock.GetUtcNow())}}","event":"{{name}}","event_data":{{data}},"event_uuid":"{{Guid.NewGuid()}}"}""");

        public string Json() =>
            $$"""{"uuid":"{{_uuid}}","time":"{{At(started)}}","direction":"{{Direction}}","status":"{{Status}}","duration":{{Duration}},"from":"{{from}}","to":"{{to}}","country":"GB"{{string.Concat(Fields.Select(f => $",\"{f.Key}\":{f.Value}"))}},"call_events":[{{string.Join(",", _events)}}]}""";

        private static string At(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Demo: the scenario {Id} failed.")]
    private static partial void LogFailed(ILogger logger, string id, Exception exception);
}
