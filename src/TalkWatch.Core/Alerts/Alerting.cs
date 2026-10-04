using TalkWatch.Core.Calls;

namespace TalkWatch.Core.Alerts;

/// <summary>What can raise an alert.</summary>
public enum AlertEventType
{
    /// <summary>
    /// An inbound call that rang a phone or reached voicemail, and ended with nobody answering and no message left.
    /// </summary>
    MissedCall,

    /// <summary>An inbound call that left a voicemail message.</summary>
    Voicemail,

    /// <summary>Any inbound call, answered or not: with a time window this is the after-hours alert.</summary>
    InboundCall,

    /// <summary>A handset went from online to offline.</summary>
    HandsetOffline,

    /// <summary>Talk's data changed shape and TalkWatch stopped copying calls.</summary>
    Drift,

    /// <summary>
    /// An inbound call that ended at the switchboard before ringing anyone: a caller hanging up during the greeting or
    /// the menu, or a robocall. Kept apart from missed calls because most of them are not.
    /// </summary>
    HungUpAtSwitchboard,

    /// <summary>
    /// A call Talk's transcription rated negative. Raised when the transcript arrives, which is after the call; the
    /// alert says which call, never what was said.
    /// </summary>
    NegativeCall,

    /// <summary>A call Talk scored low for quality (under 70 of 100): usually the network, a busy link or a failing handset.</summary>
    PoorQualityCall,

    /// <summary>
    /// A handset that was registered for calls no longer is: still on the network, so not offline, but it cannot make
    /// or take calls.
    /// </summary>
    HandsetUnregistered,

    /// <summary>A handset has a firmware update waiting.</summary>
    HandsetUpdateAvailable,

    /// <summary>
    /// Something wrong with the Talk account itself: not active, calling suspended, a payment that failed, blocked,
    /// unauthorised use, or emergency mode. About the whole site, so for admins.
    /// </summary>
    AccountProblem,

    /// <summary>Call recording or AI transcription was switched off in Talk. About the whole site, so for admins.</summary>
    SettingTurnedOff,

    /// <summary>
    /// A voicemail's transcript has arrived, a little after the voicemail itself. What a flow can test the words of; the
    /// alert says which call, never what was said.
    /// </summary>
    VoicemailTranscribed,
}

public static class CallAlerts
{
    /// <summary>
    /// The alerts a call raises: every inbound call is an incoming call, and one that has ended raises its outcome as
    /// well (<see cref="CallOutcomes.Of"/>). Blocked calls are spam Talk already stopped, and outbound and internal
    /// calls are not alerted on; they raise nothing.
    /// </summary>
    public static IReadOnlyList<AlertEventType> For(string? direction, string? status, IReadOnlyCollection<string> events) => For(CallOutcomes.Of(direction, status, events));

    /// <summary>
    /// The alerts a call raises, from its stored outcome when there is one: a call an outside answering line's voicemail
    /// took is stored as such once its transcript has been read, which its events alone never show.
    /// </summary>
    public static IReadOnlyList<AlertEventType> For(string? direction, string? status, IReadOnlyCollection<string> events, CallOutcome stored) =>
        stored is CallOutcome.OutsideVoicemail or CallOutcome.OutsideMissed ? For(stored) : For(direction, status, events);

    private static IReadOnlyList<AlertEventType> For(CallOutcome outcome) =>
        outcome switch
        {
            CallOutcome.Blocked or CallOutcome.Outbound or CallOutcome.Other => [],
            CallOutcome.Voicemail => [AlertEventType.InboundCall, AlertEventType.Voicemail],
            CallOutcome.Missed => [AlertEventType.InboundCall, AlertEventType.MissedCall],
            CallOutcome.OutsideVoicemail => [AlertEventType.InboundCall, AlertEventType.Voicemail],
            CallOutcome.OutsideMissed => [AlertEventType.InboundCall, AlertEventType.MissedCall],
            CallOutcome.HungUpAtSwitchboard => [AlertEventType.InboundCall, AlertEventType.HungUpAtSwitchboard],
            _ => [AlertEventType.InboundCall],
        };
}

/// <summary>
/// A weekly window in the site's time zone, such as Monday to Friday 09:00 to 17:30. A window whose end is before its
/// start runs overnight (22:00 to 06:00). A rule fires inside the window, or, with <see cref="Outside"/>, outside it:
/// that is how an after-hours rule is written.
/// </summary>
public sealed record TimeWindow(IReadOnlySet<DayOfWeek> Days, TimeOnly Start, TimeOnly End, bool Outside)
{
    public static readonly IReadOnlySet<DayOfWeek> Weekdays =
        new HashSet<DayOfWeek> { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday };

    public bool Matches(DateTimeOffset at, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(at, zone);
        var time = TimeOnly.FromDateTime(local.DateTime);
        var inside = Start <= End
            ? Days.Contains(local.DayOfWeek) && time >= Start && time < End
            // Overnight: the evening part belongs to the listed day, the early-morning part to the day before.
            : (Days.Contains(local.DayOfWeek) && time >= Start) || (Days.Contains(local.AddDays(-1).DayOfWeek) && time < End);
        return inside != Outside;
    }

    /// <summary>Days as a bit mask (Sunday = bit 0), for storage.</summary>
    public static int ToMask(IEnumerable<DayOfWeek> days) => days.Aggregate(0, (mask, day) => mask | (1 << (int)day));

    public static HashSet<DayOfWeek> FromMask(int mask) => [.. Enum.GetValues<DayOfWeek>().Where(d => (mask & (1 << (int)d)) != 0)];
}
