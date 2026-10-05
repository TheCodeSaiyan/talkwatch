using TalkWatch.Core.Calls;
using TalkWatch.Data;

namespace TalkWatch.Web.Components.Calls;

/// <summary>A call on a live board: its row, its chronicle, its state now and when that state began.</summary>
public sealed record LiveCall(CallRow Call, Chronicle Chronicle, string State, bool Live, DateTimeOffset Since, string Who, string Number);

/// <summary>What Now and Operator share: which calls are current, and what state each is in, from its own events.</summary>
public static class LiveCalls
{
    /// <summary>
    /// Calls in progress, for at most a few hours (a call whose hang-up never arrived is settled by the poller a day
    /// later), and calls that ended in the last few minutes, which stay a moment before leaving for the call log. A call
    /// being made, or answered and still going, is in progress until its hang-up arrives; its stored outcome stays
    /// Outbound or Answered, so analytics count it as they always have.
    /// </summary>
    public static IQueryable<CallRow> Current(this IQueryable<CallRow> calls, DateTimeOffset now)
    {
        var lately = now - TimeSpan.FromMinutes(3);
        var window = now - TimeSpan.FromHours(2);
        var oldest = now - TimeSpan.FromHours(4);
        return calls.Where(c => (c.Outcome == CallOutcome.InProgress && c.Time >= oldest)
            || ((c.Outcome == CallOutcome.Outbound || c.Outcome == CallOutcome.Answered) && c.Time >= oldest && !c.Events.Any(e => e.Event == "call_hangup"))
            || (c.Outcome != CallOutcome.InProgress && c.UpdatedAt >= lately && c.Time >= window));
    }

    /// <summary>The call's state from the last step Talk reported, and when that state began.</summary>
    public static LiveCall Of(CallRow c, Chronicle ch, ChronicleNames names)
    {
        // A call still ringing says so in its outcome. One that has been answered, or is being made, is stored as Answered or
        // Outbound from then on, so it is live until its chronicle has an end.
        var live = c.Outcome == CallOutcome.InProgress
            || (c.Outcome is CallOutcome.Outbound or CallOutcome.Answered && !ch.Steps.Any(s => s.Kind == StepKind.Ended));
        var last = ch.Steps.LastOrDefault(s => s.Kind is StepKind.Menu or StepKind.Ringing or StepKind.Skipped or StepKind.Answered or StepKind.Voicemail);
        var state = !live ? "ended" : last?.Kind switch
        {
            StepKind.Answered => "connected",
            StepKind.Ringing or StepKind.Skipped => "ringing",
            StepKind.Menu => "menu",
            _ when c.Outcome == CallOutcome.Outbound => "calling",
            StepKind.Voicemail => "voicemail",
            _ => "routing",
        };
        var since = state == "connected" ? ch.Steps.Last(s => s.Kind == StepKind.Answered).Time : c.Time;
        var who = c.Direction == "out" ? names.Number(c.ToE164 ?? "") ?? c.ToRaw ?? "Someone" : c.CallerName ?? names.Number(c.FromE164 ?? "") ?? c.FromRaw ?? "Withheld number";
        var number = c.Direction == "out" ? c.ToRaw : c.FromRaw;
        var line = names.Number(c.ToE164 ?? "") ?? c.ToRaw ?? "";
        return new LiveCall(c, ch, state, live, since, who, number == who ? (c.Direction == "in" ? "to " + line : "") : number ?? "");
    }

    /// <summary>
    /// How long a call that has ended stays on the Now board, resolved and dimmed, before it rides its stream into Recent
    /// activity. Operator keeps ended calls longer; this is the board's own.
    /// </summary>
    public static readonly TimeSpan Linger = TimeSpan.FromSeconds(6);

    /// <summary>The Data Current stream a finished call leaves Live calls by: answered, voicemail or missed.</summary>
    public static string Stream(CallOutcome outcome) => outcome switch
    {
        CallOutcome.Answered or CallOutcome.Outbound => "answered",
        _ when outcome.IsVoicemail() => "voicemail",
        _ => "missed",
    };

    public static string Words(string state) => state switch
    {
        "menu" => "In the menu",
        "ringing" => "Ringing",
        "connected" => "Connected",
        "voicemail" => "Leaving voicemail",
        "calling" => "Calling",
        _ => "Just came in",
    };

    /// <summary>The design system's status mark key (<c>.st[data-s]</c>) for a live state.</summary>
    public static string Mark(string state) => state switch
    {
        "menu" => "routing",
        "voicemail" => "parked",
        "calling" => "ringing",
        _ => state,
    };
}
