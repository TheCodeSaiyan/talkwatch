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
    FixtureConsole console, CallLogPoller poller, LiveStatus live, LineDirectorySync directory, TimeProvider clock, ILogger<DemoScenarios> logger)
{
    public static readonly IReadOnlyList<DemoScenario> All =
    [
        new("answered", "Calls", "Answered", "Rings the support team, someone picks up, they talk, it ends."),
        new("missed", "Calls", "Missed", "Rings the support team until the caller gives up."),
        new("voicemail", "Calls", "Voicemail", "Nobody answers, so the caller leaves a message."),
        new("outside", "Calls", "Outside phone answers", "Put through to an outside phone, which picks up."),
        new("menu", "Switchboard", "Presses 1, then 2", "Into Sales and billing, then Billing, then rings the team."),
        new("wrong-key", "Switchboard", "Presses a wrong key", "Presses 7, which isn't an option, then 2 for Technical support."),
        new("menu-hangup", "Switchboard", "Hangs up in the menu", "Presses 1, listens, and hangs up before choosing again."),
        new("missed-alert", "Alerts", "Missed call alert", "A missed call, which the example flows alert on."),
        new("hangup-alert", "Alerts", "Hang-up alert", "Hangs up during the greeting, which the example flows alert on."),
        new("dnd", "People", "Do not disturb", "Someone in the team goes do-not-disturb for a minute."),
        new("busy", "People", "On a call", "Someone in the team is on a call for a minute."),
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
    private static readonly string[] Callers = ["+447700900232", "+447700900880", "+447700900417", "+447700900111"];
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
        "voicemail" => RingAsync(Answer.Voicemail, ct),
        "outside" => RingAsync(Answer.Outside, ct),
        "menu" => MenuAsync([(16, 1, "Title 11"), (42, 2, "Title 15")], wrongKey: false, hangUp: false, ct),
        "wrong-key" => MenuAsync([(18, 2, "Title 16")], wrongKey: true, hangUp: false, ct),
        "menu-hangup" => MenuAsync([(16, 1, "Title 11")], wrongKey: false, hangUp: true, ct),
        "hangup-alert" => GreetingHangUpAsync(ct),
        "dnd" => PresenceAsync(dnd: true, ct),
        "busy" => PresenceAsync(dnd: false, ct),
        _ => Task.CompletedTask,
    };

    private enum Answer { Member, Nobody, Voicemail, Outside }

    // A call to the demo's line that rings the support team, and how it ends.
    private async Task RingAsync(Answer answer, CancellationToken ct)
    {
        var call = NewCall(DemoLine);
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

    private ScenarioCall NewCall(string to)
    {
        var from = Callers[Interlocked.Increment(ref _caller) % Callers.Length];
        return new ScenarioCall(from, to, clock.GetUtcNow(), clock);
    }

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

        /// <summary>Further fields of the record, as raw JSON values.</summary>
        public Dictionary<string, string> Fields { get; } = new(StringComparer.Ordinal);

        public void Event(string name, string data = "{}") =>
            _events.Add($$"""{"time":"{{At(clock.GetUtcNow())}}","event":"{{name}}","event_data":{{data}},"event_uuid":"{{Guid.NewGuid()}}"}""");

        public string Json() =>
            $$"""{"uuid":"{{_uuid}}","time":"{{At(started)}}","direction":"in","status":"{{Status}}","duration":{{Duration}},"from":"{{from}}","to":"{{to}}","country":"GB"{{string.Concat(Fields.Select(f => $",\"{f.Key}\":{f.Value}"))}},"call_events":[{{string.Join(",", _events)}}]}""";

        private static string At(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Demo: the scenario {Id} failed.")]
    private static partial void LogFailed(ILogger logger, string id, Exception exception);
}
