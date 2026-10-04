namespace TalkWatch.Core.Calls;

/// <summary>What happened to a call. Stored with each call, so analytics can count by it and alerts agree with them.</summary>
public enum CallOutcome
{
    /// <summary>Neither direction nor status says enough: a call from a source that sent no events, and no 'accepted'.</summary>
    Unknown,

    /// <summary>An inbound call that has not ended yet.</summary>
    InProgress,

    /// <summary>An inbound call a person answered.</summary>
    Answered,

    /// <summary>An inbound call that rang a phone or reached voicemail, and ended unanswered with no message left.</summary>
    Missed,

    /// <summary>An inbound call that left a voicemail message.</summary>
    Voicemail,

    /// <summary>An inbound call that ended at the switchboard before ringing anyone.</summary>
    HungUpAtSwitchboard,

    /// <summary>An inbound call Talk blocked as spam.</summary>
    Blocked,

    Outbound,

    /// <summary>Any other direction: internal calls, and calls whose direction the console did not send.</summary>
    Other,

    /// <summary>
    /// A call forwarded to an outside answering line where the provider's voicemail took a message: Talk logged it
    /// answered, its transcript says otherwise (<see cref="OutsideVoicemail"/>). Counts as voicemail.
    /// </summary>
    OutsideVoicemail,

    /// <summary>
    /// A call forwarded to an outside answering line where the caller hung up at the provider's greeting. Counts as
    /// missed.
    /// </summary>
    OutsideMissed,
}

public static class CallOutcomes
{
    // Every rule that asks whether a call was missed, left a message or went unanswered asks these, never naming outcomes
    // itself, so an outcome added later counts everywhere at once. The arrays are for database queries; the methods for
    // calls in hand.

    /// <summary>The outcomes that count as a missed call: an inbound call nobody took, with no message left.</summary>
    public static readonly CallOutcome[] MissedKinds = [CallOutcome.Missed, CallOutcome.OutsideMissed];

    /// <summary>The outcomes that count as voicemail: an inbound call where the caller left a message.</summary>
    public static readonly CallOutcome[] VoicemailKinds = [CallOutcome.Voicemail, CallOutcome.OutsideVoicemail];

    /// <summary>Missed or voicemail: an inbound call nobody spoke to, which wants ringing back.</summary>
    public static readonly CallOutcome[] UnansweredKinds = [.. MissedKinds, .. VoicemailKinds];

    public static bool IsMissed(this CallOutcome outcome) => Array.IndexOf(MissedKinds, outcome) >= 0;

    public static bool IsVoicemail(this CallOutcome outcome) => Array.IndexOf(VoicemailKinds, outcome) >= 0;

    public static bool IsUnanswered(this CallOutcome outcome) => Array.IndexOf(UnansweredKinds, outcome) >= 0;

    /// <summary>
    /// The outcome with what an outside answering line's transcript showed laid over it: an answered call the provider's
    /// voicemail took becomes <see cref="CallOutcome.OutsideVoicemail"/> or <see cref="CallOutcome.OutsideMissed"/>.
    /// Anything else is left as Talk's events have it, so a re-read of the call keeps the finding and never loses it.
    /// </summary>
    public static CallOutcome With(this CallOutcome outcome, OutsideFinding? finding) => (outcome, finding) switch
    {
        (CallOutcome.Answered or CallOutcome.OutsideVoicemail or CallOutcome.OutsideMissed, OutsideFinding.MessageLeft) => CallOutcome.OutsideVoicemail,
        (CallOutcome.Answered or CallOutcome.OutsideVoicemail or CallOutcome.OutsideMissed, OutsideFinding.HungUpAtGreeting) => CallOutcome.OutsideMissed,
        (CallOutcome.OutsideVoicemail or CallOutcome.OutsideMissed, _) => CallOutcome.Answered,
        _ => outcome,
    };

    private static readonly HashSet<string> MissedStatuses = new(["cancelled", "refused", "timed_out"], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// What a call amounts to, from its direction, status and the names of its call events.
    /// </summary>
    /// <remarks>
    /// Talk marks nearly every inbound call 'accepted', whether a person answered, it went to voicemail or the caller
    /// hung up at the switchboard; what happened is in the events. call_accepted is a person answering,
    /// seq_call_trying_endpoints is phones ringing, call_sent_to_voicemail and vm_msg_recorded are voicemail, and how a
    /// call ended is only known at call_hangup. A call with no events at all came from a source that sends none, so its
    /// status is all there is. The migration that added outcomes repeats this rule in SQL, and a test holds the two
    /// together.
    /// </remarks>
    /// <summary>
    /// How long after it started a call is taken to have ended, whether or not its hang-up was recorded. A real
    /// console has calls whose record never got a call_hangup; without this they would be in progress for ever.
    /// </summary>
    public static readonly TimeSpan LongOver = TimeSpan.FromDays(1);

    /// <summary>
    /// How long a call may go on ringing or sitting in a menu with nothing new before it is taken as over. Talk reports
    /// every step of a call that is still being routed, and an answered one is stored as answered at once, so only a call
    /// whose hang-up Talk never recorded goes this long: it would otherwise show as live for hours.
    /// </summary>
    public static readonly TimeSpan RingingOver = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Whether a call that has not ended is over all the same: started a day ago (<see cref="LongOver"/>), or with nothing
    /// new since its last event for <see cref="RingingOver"/>. Only decides a call still ringing or in a menu.
    /// </summary>
    public static bool Over(DateTimeOffset started, DateTimeOffset? lastEvent, DateTimeOffset now) =>
        now - started > LongOver || now - (lastEvent ?? started) > RingingOver;

    /// <param name="longOver">The call started more than <see cref="LongOver"/> ago, so it has ended.</param>
    public static CallOutcome Of(string? direction, string? status, IReadOnlyCollection<string> events, bool longOver = false)
    {
        if (string.Equals(direction, "out", StringComparison.OrdinalIgnoreCase))
        {
            return CallOutcome.Outbound;
        }

        if (!string.Equals(direction, "in", StringComparison.OrdinalIgnoreCase))
        {
            return CallOutcome.Other;
        }

        if (string.Equals(status, "blocked", StringComparison.OrdinalIgnoreCase))
        {
            return CallOutcome.Blocked;
        }

        if (events.Contains("vm_msg_recorded"))
        {
            return CallOutcome.Voicemail;
        }

        if (status is not null && MissedStatuses.Contains(status))
        {
            return CallOutcome.Missed;
        }

        if (events.Contains("call_accepted"))
        {
            return CallOutcome.Answered;
        }

        if (events.Count == 0)
        {
            return string.Equals(status, "accepted", StringComparison.OrdinalIgnoreCase) ? CallOutcome.Answered : CallOutcome.Unknown;
        }

        if (!events.Contains("call_hangup") && !longOver)
        {
            return CallOutcome.InProgress;
        }

        return events.Contains("seq_call_trying_endpoints") || events.Contains("call_sent_to_voicemail")
            ? CallOutcome.Missed
            : CallOutcome.HungUpAtSwitchboard;
    }
}
