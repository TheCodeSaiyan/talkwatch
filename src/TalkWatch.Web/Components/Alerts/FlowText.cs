using TalkWatch.Core.Alerts;
using TalkWatch.Core.Calls;

namespace TalkWatch.Web.Components.Alerts;

/// <summary>Names for what a flow refers to by id, as the person reading it knows them.</summary>
/// <param name="ReachablePeople">People with at least one channel turned on: a flow can reach them.</param>
public sealed record FlowNames(
    IReadOnlyDictionary<Guid, string> People,
    IReadOnlyDictionary<Guid, string> Channels,
    IReadOnlyDictionary<LineRef, string> Lines,
    IReadOnlySet<Guid> ReachablePeople)
{
    public static readonly FlowNames None = new(new Dictionary<Guid, string>(), new Dictionary<Guid, string>(), new Dictionary<LineRef, string>(), new HashSet<Guid>());

    /// <summary>Ring groups whoever-is-free can name, by Talk's id: every group for an admin, none for anyone else.</summary>
    public IReadOnlyDictionary<string, string> Groups { get; init; } = new Dictionary<string, string>();

    /// <summary>Switchboard menu options a flow can test for, by Talk's item id: "1 Sales".</summary>
    public IReadOnlyDictionary<int, string> MenuOptions { get; init; } = new Dictionary<int, string>();

    /// <summary>Groups with at least one member linked to a person in TalkWatch who has a channel turned on.</summary>
    public IReadOnlySet<string> ReachableGroups { get; init; } = new HashSet<string>();

    /// <summary>Talk's contacts a flow can name, by uuid, with what Talk holds for them: every one for an admin, none otherwise.</summary>
    public IReadOnlyDictionary<string, ContactName> Contacts { get; init; } = new Dictionary<string, ContactName>();

    /// <summary>
    /// Whether the people behind outside contacts can be named: for an admin, once Talk's contacts can be read. Their
    /// alerts reach people about calls they may not see, so no one else names them.
    /// </summary>
    public bool OutsideOffered { get; init; }

    /// <summary>Whether anyone linked to an outside contact has a channel other than email turned on, so such a step reaches someone.</summary>
    public bool OutsideReachable { get; init; }
}

/// <summary>A contact as a flow names them: their name, their email if Talk holds one, and their first number.</summary>
public sealed record ContactName(string Name, string? Email, string? Number);

/// <summary>How a flow reads, in words: shared by the alerts page's chains and the flow editor.</summary>
public static class FlowText
{
    public static readonly DayOfWeek[] WeekFromMonday =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday];

    public static string EventName(AlertEventType type) => type switch
    {
        AlertEventType.MissedCall => "Missed call",
        AlertEventType.Voicemail => "New voicemail",
        AlertEventType.VoicemailTranscribed => "Voicemail transcribed",
        AlertEventType.InboundCall => "Any incoming call",
        AlertEventType.HandsetOffline => "Handset offline",
        AlertEventType.HungUpAtSwitchboard => "Hung up at the switchboard",
        AlertEventType.NegativeCall => "Call rated negative",
        AlertEventType.PoorQualityCall => "Poor call quality",
        AlertEventType.HandsetUnregistered => "Handset can't take calls",
        AlertEventType.HandsetUpdateAvailable => "Handset update available",
        AlertEventType.AccountProblem => "Talk account problem",
        AlertEventType.SettingTurnedOff => "Recording or transcription switched off",
        _ => "TalkWatch stopped copying calls",
    };

    public static string Window(int mask, TimeOnly start, TimeOnly end, bool outside)
    {
        var days = TimeWindow.FromMask(mask);
        var listed = string.Join(" ", WeekFromMonday.Where(days.Contains).Select(d => d.ToString()[..3]));
        return $"{(outside ? "outside " : "")}{listed} {start:HH\\:mm}–{end:HH\\:mm}";
    }

    public static string Condition(FlowCondition condition, FlowNames names) => condition switch
    {
        LineCondition l when IsRang(l) => $"it rang {string.Join(" or ", l.Lines.Select(line => Line(line, names)))}",
        LineCondition l => $"on {string.Join(" or ", l.Lines.Select(line => Line(line, names)))}",
        RepeatCallerCondition r => $"the caller has rung {r.Calls} times in {Minutes(r.Minutes)}",
        CallerMissedCondition c => $"the caller has gone unanswered {c.Calls} times in {Minutes(c.Minutes)}",
        NumberMissedCondition n => $"the number has gone unanswered {n.Calls} times in {Minutes(n.Minutes)}",
        WaitingCallersCondition w => $"{w.Callers} or more callers on the number are waiting to be called back",
        AnswerRateCondition a => $"the number's answer rate over {Minutes(a.Hours * 60)} is below {a.Percent}%",
        RangLongerCondition r => $"it rang for more than {r.Seconds} seconds",
        KnownCallerCondition { Known: true } => "the caller is someone known",
        KnownCallerCondition => "the caller is not someone known",
        ForwardedOutsideCondition { Forwarded: true } => "it was put through to an outside contact",
        ForwardedOutsideCondition => "it was not put through to an outside contact",
        AbroadCondition { Abroad: true } => "the caller rang from abroad",
        AbroadCondition => "the caller rang from this country",
        QualityCondition q => $"call quality was below {q.Below} of 100",
        TranscriptWordsCondition w => $"the voicemail says {string.Join(" or ", w.Words.Select(x => $"\u201c{x}\u201d"))}",
        MenuOptionCondition m => $"the caller chose {string.Join(" or ", m.Items.Select(i => names.MenuOptions.GetValueOrDefault(i, "a removed option")))}",
        TimeCondition t => Window(t.Days, t.Start, t.End, t.Outside),
        CallerFlowCondition { Mode: CallerMode.Only } c => $"from {string.Join(" or ", c.Entries)}",
        CallerFlowCondition c => $"not from {string.Join(" or ", c.Entries)}",
        _ => "",
    };

    /// <summary>The icon a condition is drawn with, in the diagram and in the editor alike.</summary>
    public static string Icon(FlowCondition condition) => condition switch
    {
        TimeCondition => "clock",
        RepeatCallerCondition or CallerMissedCondition or WaitingCallersCondition => "callbacks",
        NumberMissedCondition or AnswerRateCondition => "chart",
        RangLongerCondition => "clock",
        KnownCallerCondition => "user",
        ForwardedOutsideCondition => "phone",
        AbroadCondition => "did",
        QualityCondition => "warning",
        MenuOptionCondition => "attendant",
        TranscriptWordsCondition => "transcript",
        LineCondition l when IsRang(l) => "group",
        LineCondition => "did",
        _ => "user",
    };

    /// <summary>The kinds of line a call rings: a person, a ring group, a queue.</summary>
    public static bool Rings(LineKind kind) => kind is LineKind.User or LineKind.RingGroup or LineKind.Queue;

    /// <summary>A line condition only about who a call rang, so it reads as "it rang Sales" and edits as "If it rang".</summary>
    public static bool IsRang(LineCondition condition) => condition.Lines.Count > 0 && condition.Lines.All(l => Rings(l.Kind));

    public static string Line(LineRef line, FlowNames names) => names.Lines.GetValueOrDefault(line, line.Key);

    public static string Recipient(FlowRecipient recipient, FlowNames names) => recipient switch
    {
        { Person: { } person } => names.People.GetValueOrDefault(person, "someone removed"),
        { Channel: { } channel } => names.Channels.GetValueOrDefault(channel, "a removed channel"),
        { Rang: true } => "whoever it rang",
        { Outside: OutsideScope.ThisCall } => "whoever it was put through to outside",
        { Outside: OutsideScope.AnyLinked } => "everyone behind an outside contact",
        { FreeIn: { } group } => $"whoever is free in {names.Groups.GetValueOrDefault(group) ?? names.Lines.GetValueOrDefault(new LineRef(LineKind.RingGroup, group), "a removed group")}",
        { Contact: { } contact } => names.Contacts.GetValueOrDefault(contact) is { } known ? $"{known.Name} (contact)" : "a contact no longer in Talk",
        _ => "",
    };

    /// <summary>
    /// A person the flow names who has no channel turned on, or a group nobody in TalkWatch is linked to, so the step
    /// would reach nobody.
    /// </summary>
    public static bool Unreachable(FlowRecipient recipient, FlowNames names) => recipient switch
    {
        { Person: { } person } => !names.ReachablePeople.Contains(person),
        { FreeIn: { } group } => !names.ReachableGroups.Contains(group),
        { Contact: { } contact } => names.Contacts.GetValueOrDefault(contact)?.Email is null,
        { Outside: not null } => !names.OutsideReachable,
        _ => false,
    };

    /// <summary>What follows an unreachable recipient's name, saying why it would reach nobody.</summary>
    public static string UnreachableNote(FlowRecipient recipient, FlowNames names) =>
        !Unreachable(recipient, names) ? "" : recipient.Contact is not null ? " (no email)" : recipient.FreeIn is null && recipient.Outside is null ? " (no channel)" : " (nobody linked)";

    /// <summary>The hover text for an unreachable recipient.</summary>
    public static string? UnreachableWhy(FlowRecipient recipient, FlowNames names) =>
        !Unreachable(recipient, names) ? null : recipient.Contact is not null
            ? "Talk holds no email for this contact, and texting a number is not set up yet"
            : recipient.Outside is not null
            ? "Nobody linked to an outside contact has a channel other than email turned on. Link people to contacts under Configure, Outside contacts."
            : recipient.FreeIn is null
            ? "Has no channel turned on, so hears nothing"
            : "Nobody in this group is linked to a person in TalkWatch with a channel turned on. Link them on their page under People.";

    /// <summary>What a notify step sends with the alert, in words: "the summary and the voicemail".</summary>
    public static string Includes(NotifyIncludes include)
    {
        var parts = new List<string>();
        if (include.HasFlag(NotifyIncludes.Summary)) parts.Add("the summary");
        if (include.HasFlag(NotifyIncludes.Transcript)) parts.Add("what was said");
        if (include.HasFlag(NotifyIncludes.Voicemail)) parts.Add("the voicemail");
        return parts.Count <= 1 ? string.Concat(parts) : string.Join(", ", parts[..^1]) + " and " + parts[^1];
    }

    public static string Minutes(int minutes) => minutes switch
    {
        1 => "1 minute",
        < 60 => $"{minutes} minutes",
        _ when minutes % 60 == 0 => minutes == 60 ? "1 hour" : $"{minutes / 60} hours",
        _ => $"{minutes / 60} h {minutes % 60} min",
    };
}
