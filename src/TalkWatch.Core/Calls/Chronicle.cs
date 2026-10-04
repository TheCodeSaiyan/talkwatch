using System.Globalization;
using System.Text.Json;

namespace TalkWatch.Core.Calls;

/// <summary>One of Talk's routing events as stored: its time, its name and its event_data, raw.</summary>
public sealed record ChronicleEvent(DateTimeOffset Time, string Event, string? DataJson);

public enum StepKind
{
    Started,
    Menu,
    Ringing,
    Skipped,
    Answered,
    Voicemail,
    Ended,
    Other,
}

/// <summary>
/// A step in plain words ("Ringing Alex Morgan", "Alex Morgan answered after 7s"), with its Subject on its own: the line,
/// menu, people or answerer the step is about, so a route can be drawn without reading the sentence.
/// </summary>
public sealed record ChronicleStep(DateTimeOffset Time, StepKind Kind, string Text, string? Detail, string? Subject = null);

public enum SegmentKind
{
    Menu,
    Ringing,
    Talking,
    Voicemail,
    Unanswered,
}

/// <summary>A stretch of the call's life, for the proportional band at the top of a call.</summary>
public sealed record ChronicleSegment(SegmentKind Kind, TimeSpan Length);

/// <summary>Names for what an event refers to by id. Each returns null when it does not know.</summary>
public sealed record ChronicleNames(
    Func<string, string?> Attendant,
    Func<string, string?> Contact,
    Func<string, string?> User,
    Func<string, string?> Number)
{
    public static ChronicleNames None { get; } = new(_ => null, _ => null, _ => null, _ => null);
}

/// <summary>
/// A call told as what happened, in order, from Talk's own routing events: when it came in, how long it spent in the
/// menu, who it rang, who answered and after how long, and how it ended. Built only from what Talk reported, so an
/// event Talk did not send is never invented; an event TalkWatch does not know yet is still shown, by its name.
/// </summary>
public sealed record Chronicle(IReadOnlyList<ChronicleStep> Steps, IReadOnlyList<ChronicleSegment> Band, TimeSpan? Total, TimeSpan? TimeToAnswer)
{
    public static Chronicle Build(IEnumerable<ChronicleEvent> events, string direction, ChronicleNames names)
    {
        var ordered = events.OrderBy(e => e.Time).ToList();
        var steps = new List<ChronicleStep>();

        // A contact Talk rang and who then answered is named by the number it answered on, when the directory has no
        // name: so the ringing step and the answer agree, and the route names the person once.
        var answeredOn = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var e in ordered.Where(e => e.Event == "call_accepted"))
        {
            using var accepted = Parse(e.DataJson);
            if (Text(accepted?.RootElement, "accepted_by_contact_uuid") is { } uuid && Text(accepted?.RootElement, "accepted_by") is { } number)
            {
                answeredOn[uuid] = number;
            }
        }

        string? ContactName(string uuid) => names.Contact(uuid) ?? (answeredOn.TryGetValue(uuid, out var n) ? Who(n, names) : null);
        DateTimeOffset? start = null, menuAt = null, ringAt = null, answerAt = null, voicemailAt = null, endAt = null;

        foreach (var e in ordered)
        {
            using var data = Parse(e.DataJson);
            var d = data?.RootElement;
            switch (e.Event)
            {
                case "call_started":
                    start ??= e.Time;
                    var from = Who(Text(d, "from"), names);
                    var to = Who(Text(d, "to"), names);
                    if (direction == "out")
                    {
                        var caller = Text(d, "from_user_uuid") is { } uuid ? names.User(uuid) : null;
                        steps.Add(new(e.Time, StepKind.Started, $"{caller ?? from ?? "Someone"} dialled {to ?? "out"}", null, caller ?? from));
                    }
                    else
                    {
                        steps.Add(new(e.Time, StepKind.Started, to is null ? "Call came in" : $"Call came in on {to}", from is null ? null : $"From {from}", to));
                    }

                    if (Text(d, "to_smart_attendant_id") is { } attendant)
                    {
                        menuAt ??= e.Time;
                        var title = names.Attendant(attendant) ?? "The switchboard";
                        steps.Add(new(e.Time, StepKind.Menu, $"{title} played its menu", null, title));
                    }

                    break;

                case "entered_sa_menu":
                    menuAt ??= e.Time;
                    var menu = Text(d, "smart_attendant_id") ?? Text(d, "to_smart_attendant_id");
                    var menuTitle = (menu is null ? null : names.Attendant(menu)) ?? "The switchboard";
                    steps.Add(new(e.Time, StepKind.Menu, $"{menuTitle} played its menu", null, menuTitle));
                    break;

                case "seq_call_trying_endpoints":
                    ringAt ??= e.Time;
                    var rung = Endpoints(d, names, ContactName);
                    steps.Add(new(e.Time, StepKind.Ringing, rung.Count == 0 ? "Ringing" : $"Ringing {Join(rung)}", null, rung.Count == 0 ? null : Join(rung)));
                    break;

                case "skipped_endpoints":
                    var skipped = Endpoints(d, names, ContactName);
                    steps.Add(new(e.Time, StepKind.Skipped, skipped.Count == 0 ? "Skipped someone who could not take the call" : $"Skipped {Join(skipped)}", "Not available to take the call"));
                    break;

                case "call_accepted":
                    answerAt ??= e.Time;
                    var by = (Text(d, "accepted_by_contact_uuid") is { } c ? ContactName(c) : null) ?? Who(Text(d, "accepted_by"), names) ?? "Someone";
                    var rang = ringAt is { } r ? e.Time - r : (TimeSpan?)null;
                    steps.Add(new(e.Time, StepKind.Answered, rang is { } wait ? $"{by} answered after {Seconds(wait)}" : $"{by} answered", null, by));
                    break;

                case "call_sent_to_voicemail":
                    voicemailAt ??= e.Time;
                    steps.Add(new(e.Time, StepKind.Voicemail, "Sent to voicemail", null));
                    break;

                case "vm_msg_recorded":
                    voicemailAt ??= e.Time;
                    steps.Add(new(e.Time, StepKind.Voicemail, "Voicemail left", null));
                    break;

                case "vm_recording_canceled":
                    steps.Add(new(e.Time, StepKind.Voicemail, "Caller hung up before leaving a message", null));
                    break;

                case "call_hangup":
                    endAt ??= e.Time;
                    steps.Add(new(e.Time, StepKind.Ended, Ending(Text(d, "hangup_cause"), answerAt is not null), null));
                    break;

                default:
                    // Something Talk sent that TalkWatch has no words for yet: shown by its own name, not dropped.
                    steps.Add(new(e.Time, StepKind.Other, "Talk reported " + e.Event.Replace('_', ' '), null));
                    break;
            }
        }

        // The band: each stretch from one milestone to the next. A stretch with no end yet is left out.
        var band = new List<ChronicleSegment>();
        void Add(SegmentKind kind, DateTimeOffset? from, DateTimeOffset? to)
        {
            if (from is { } a && to is { } b && b > a)
            {
                band.Add(new(kind, b - a));
            }
        }

        var afterRing = answerAt ?? voicemailAt ?? endAt;
        if (menuAt is not null)
        {
            Add(SegmentKind.Menu, menuAt, ringAt ?? answerAt ?? voicemailAt ?? endAt);
        }

        Add(SegmentKind.Ringing, ringAt, afterRing);
        Add(SegmentKind.Talking, answerAt, endAt);
        Add(SegmentKind.Voicemail, voicemailAt, answerAt is null ? endAt : null);
        if (answerAt is null && voicemailAt is null && ringAt is null && menuAt is null)
        {
            Add(SegmentKind.Unanswered, start, endAt);
        }

        return new Chronicle(steps, band, start is { } s && endAt is { } end ? end - s : null, ringAt is { } ring && answerAt is { } ans ? ans - ring : null);
    }

    /// <summary>What someone was doing in the gap after a step: "in the menu", "ringing", "talking".</summary>
    public static string? Doing(StepKind kind) => kind switch
    {
        StepKind.Menu => "in the menu",
        StepKind.Ringing or StepKind.Skipped => "ringing",
        StepKind.Answered => "talking",
        StepKind.Voicemail => "leaving a message",
        _ => null,
    };

    public static string Seconds(TimeSpan t) => t.TotalSeconds < 60
        ? Math.Round(t.TotalSeconds).ToString(CultureInfo.InvariantCulture) + "s"
        : $"{(int)t.TotalMinutes}:{t.Seconds:00}";

    private static string Ending(string? cause, bool answered) => cause switch
    {
        "normal_end" or "normal_clearing" or null => answered ? "Call ended" : "Caller hung up",
        "originator_cancel" or "cancelled" or "canceled" => answered ? "Caller hung up" : "Caller hung up before anyone answered",
        "no_answer" => "No one answered",
        "user_busy" => "The line was busy",
        _ => "Call ended (" + cause.Replace('_', ' ') + ")",
    };

    private static List<string> Endpoints(JsonElement? d, ChronicleNames names, Func<string, string?> contactName)
    {
        var found = new List<string>();
        foreach (var uuid in List(d, "user_uuids"))
        {
            found.Add(names.User(uuid) ?? "a colleague");
        }

        foreach (var uuid in List(d, "contact_uuids"))
        {
            found.Add(contactName(uuid) ?? "a contact");
        }

        foreach (var did in List(d, "external_dids"))
        {
            found.Add(names.Number(did) ?? did);
        }

        // "a colleague and a colleague" reads worse than "2 colleagues"
        var result = new List<string>();
        foreach (var g in found.GroupBy(n => n))
        {
            if (g.Key.StartsWith("a ", StringComparison.Ordinal) && g.Count() > 1)
            {
                result.Add($"{g.Count()} {g.Key[2..]}s");
            }
            else
            {
                result.AddRange(g);
            }
        }

        return result;
    }

    private static string Join(List<string> names) => names.Count switch
    {
        1 => names[0],
        2 => names[0] + " and " + names[1],
        _ => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1],
    };

    private static string? Who(string? number, ChronicleNames names) =>
        string.IsNullOrWhiteSpace(number) ? null : names.Number(number) ?? number;

    private static JsonDocument? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement? d, string field) =>
        d is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(field, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number => v.GetRawText(),
                _ => null,
            }
            : null;

    private static List<string> List(JsonElement? d, string field) =>
        d is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
            : [];
}
