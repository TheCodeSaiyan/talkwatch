using TalkWatch.Core.Calls;

namespace TalkWatch.Web.Components.Calls;

/// <summary>How a call reads at a glance: its outcome in words and the design system's mark for it.</summary>
public static class CallMarks
{
    /// <summary>The outcome as people say it.</summary>
    public static string Words(CallOutcome outcome) => outcome switch
    {
        CallOutcome.Answered => "Answered",
        CallOutcome.Missed => "Missed",
        CallOutcome.Voicemail => "Voicemail",
        CallOutcome.OutsideVoicemail => "Voicemail, outside",
        CallOutcome.OutsideMissed => "Missed, outside",
        CallOutcome.HungUpAtSwitchboard => "Hung up in the menu",
        CallOutcome.Blocked => "Blocked",
        CallOutcome.InProgress => "In progress",
        CallOutcome.Outbound => "Outbound",
        _ => "Not known",
    };

    /// <summary>The design system's outcome key (<c>.oc[data-o]</c>): shape and colour together.</summary>
    public static string Key(CallOutcome outcome) => outcome switch
    {
        CallOutcome.Answered => "answered",
        CallOutcome.Missed => "missed",
        CallOutcome.Voicemail => "voicemail",
        CallOutcome.OutsideVoicemail => "voicemail",
        CallOutcome.OutsideMissed => "missed",
        CallOutcome.HungUpAtSwitchboard => "abandoned",
        CallOutcome.Blocked => "failed",
        CallOutcome.InProgress => "transferred",
        CallOutcome.Outbound => "outbound",
        _ => "internal",
    };

    public static string Icon(CallOutcome outcome) => outcome switch
    {
        CallOutcome.Answered => "check",
        CallOutcome.Missed => "missed",
        CallOutcome.Voicemail => "vm",
        CallOutcome.OutsideVoicemail => "vm",
        CallOutcome.OutsideMissed => "missed",
        CallOutcome.HungUpAtSwitchboard => "hangup",
        CallOutcome.Blocked => "warning",
        CallOutcome.InProgress => "phone",
        CallOutcome.Outbound => "out",
        _ => "info",
    };

    public static string Direction(string direction) => direction switch
    {
        "in" => "Inbound",
        "out" => "Outbound",
        _ => "Internal",
    };

    /// <summary>The direction glyph's key (<c>.dir[data-d]</c>).</summary>
    public static string DirectionKey(string direction) => direction switch
    {
        "in" => "in",
        "out" => "out",
        _ => "int",
    };

    public static string BandClass(SegmentKind kind) => kind switch
    {
        SegmentKind.Menu => "b-route",
        SegmentKind.Ringing => "b-ring",
        SegmentKind.Talking => "b-talk",
        SegmentKind.Voicemail => "b-vm",
        _ => "b-fail",
    };

    public static string BandWord(SegmentKind kind) => kind switch
    {
        SegmentKind.Menu => "menu",
        SegmentKind.Ringing => "ringing",
        SegmentKind.Talking => "talking",
        SegmentKind.Voicemail => "voicemail",
        _ => "unanswered",
    };

    /// <summary>The chronicle's marker key (<c>.chron [data-k]</c>) for a step.</summary>
    public static string StepKey(StepKind kind) => kind switch
    {
        StepKind.Menu => "route",
        StepKind.Ringing or StepKind.Started => "ring",
        StepKind.Skipped => "route",
        StepKind.Answered => "answer",
        StepKind.Voicemail => "vm",
        StepKind.Ended => "end",
        _ => "route",
    };

    /// <summary>m:ss, or h:mm:ss past an hour; the mono figure under every duration.</summary>
    public static string Duration(TimeSpan t) => t.ToString(t.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss", System.Globalization.CultureInfo.InvariantCulture);
}
