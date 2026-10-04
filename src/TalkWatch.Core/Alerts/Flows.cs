using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;
using TalkWatch.Core.Calls;

namespace TalkWatch.Core.Alerts;

/// <summary>
/// What happens when something is alerted on: a trigger, conditions that must all hold, then steps. Steps notify people
/// and channels, wait for someone to acknowledge, or branch on a condition. A flow reads top to bottom, like the chain
/// the alerts page draws.
/// </summary>
public sealed record FlowDefinition
{
    public AlertEventType Trigger { get; init; }

    public IReadOnlyList<FlowCondition> Conditions { get; init; } = [];

    public IReadOnlyList<FlowStep> Steps { get; init; } = [];
}

/// <summary>The facts about one alert that conditions are judged on.</summary>
/// <param name="CallerE164">The caller of a call alert, null when their number did not read as one.</param>
/// <param name="IsCall">Whether the alert is about a call: a caller condition holds for anything else.</param>
/// <param name="CallerCalls">When the caller rang in the day before the alert, this call included; empty when withheld.</param>
public sealed record FlowFacts(AlertEventType Type, DateTimeOffset At, IReadOnlySet<LineRef> Lines, bool IsCall, string? CallerE164, TimeZoneInfo Zone)
{
    public IReadOnlyList<DateTimeOffset> CallerCalls { get; init; } = [];

    /// <summary>How long the call rang before it was answered, went to voicemail or the caller hung up; null when not known.</summary>
    public double? RingSeconds { get; init; }

    /// <summary>Whether the caller is someone known: a name of their own on the call, or a contact with their number.</summary>
    public bool CallerKnown { get; init; }

    /// <summary>Whether the caller's number is from another country than the site's; null when there is no number to tell by.</summary>
    public bool? FromAbroad { get; init; }

    /// <summary>Talk's quality score for the call, out of 100; null when it was not scored.</summary>
    public int? Quality { get; init; }

    /// <summary>A voicemail's transcript, for a words condition; null for every other alert.</summary>
    public string? TranscriptText { get; init; }

    /// <summary>The switchboard menu options the caller chose, by Talk's item id.</summary>
    public IReadOnlySet<int> MenuChoices { get; init; } = new HashSet<int>();

    /// <summary>The caller's calls in the week before the alert, this call included; empty when withheld.</summary>
    public IReadOnlyList<PastCall> CallerHistory { get; init; } = [];

    /// <summary>
    /// Calls in on the number this call came in on, in the day before the alert, this call included; null when the call
    /// came in on no number (an internal call, say), so no number's figures apply.
    /// </summary>
    public IReadOnlyList<PastCall>? NumberCalls { get; init; }

    /// <summary>Missed callers on that number still to be called back, as the Call-backs list counts them; null with no number.</summary>
    public int? WaitingCallers { get; init; }

    /// <summary>Whether Talk put the call through to one of its outside contacts: a mobile in a ring group, a switchboard option, an overflow.</summary>
    public bool ForwardedOutside { get; init; }
}

/// <summary>A call before an alert, for the conditions that count: when, and how it ended.</summary>
public readonly record struct PastCall(DateTimeOffset At, CallOutcome Outcome)
{
    /// <summary>Nobody answered: missed, or sent to voicemail.</summary>
    public bool Unanswered => Outcome.IsUnanswered();

    /// <summary>A call that counts towards an answer rate: answered, missed or sent to voicemail.</summary>
    public bool Counts => Outcome == CallOutcome.Answered || Outcome.IsUnanswered();
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(LineCondition), "line")]
[JsonDerivedType(typeof(TimeCondition), "time")]
[JsonDerivedType(typeof(CallerFlowCondition), "caller")]
[JsonDerivedType(typeof(RepeatCallerCondition), "repeat")]
[JsonDerivedType(typeof(RangLongerCondition), "rang-longer")]
[JsonDerivedType(typeof(KnownCallerCondition), "known")]
[JsonDerivedType(typeof(AbroadCondition), "abroad")]
[JsonDerivedType(typeof(QualityCondition), "quality")]
[JsonDerivedType(typeof(MenuOptionCondition), "menu")]
[JsonDerivedType(typeof(TranscriptWordsCondition), "words")]
[JsonDerivedType(typeof(CallerMissedCondition), "caller-missed")]
[JsonDerivedType(typeof(NumberMissedCondition), "number-missed")]
[JsonDerivedType(typeof(WaitingCallersCondition), "waiting")]
[JsonDerivedType(typeof(AnswerRateCondition), "answer-rate")]
[JsonDerivedType(typeof(ForwardedOutsideCondition), "forwarded")]
public abstract record FlowCondition
{
    public abstract bool Matches(FlowFacts facts);
}

/// <summary>The alert touches any of these lines.</summary>
public sealed record LineCondition(IReadOnlyList<LineRef> Lines) : FlowCondition
{
    public override bool Matches(FlowFacts facts) => Lines.Any(facts.Lines.Contains);
}

/// <summary>The alert happened inside a weekly window, or outside it (<see cref="TimeWindow"/>).</summary>
/// <param name="Days">Days as a bit mask, Sunday bit 0, as <see cref="TimeWindow.ToMask"/>.</param>
public sealed record TimeCondition(int Days, TimeOnly Start, TimeOnly End, bool Outside) : FlowCondition
{
    [JsonIgnore]
    public TimeWindow Window => new(TimeWindow.FromMask(Days), Start, End, Outside);

    public override bool Matches(FlowFacts facts) => Window.Matches(facts.At, facts.Zone);
}

/// <summary>
/// The caller has rung at least <paramref name="Calls"/> times in the last <paramref name="Minutes"/>, this call
/// included: someone trying again and again. A withheld caller cannot be counted, so is never one.
/// </summary>
public sealed record RepeatCallerCondition(int Calls, int Minutes) : FlowCondition
{
    public const int MaxCalls = 20;

    public override bool Matches(FlowFacts facts) =>
        facts.IsCall && facts.CallerE164 is not null
        && facts.CallerCalls.Count(t => t <= facts.At && t > facts.At - TimeSpan.FromMinutes(Minutes)) >= Calls;
}

/// <summary>
/// The caller has gone unanswered (missed, or sent to voicemail) at least <paramref name="Calls"/> times in the last
/// <paramref name="Minutes"/>, up to a week, this call included: someone not getting through.
/// </summary>
public sealed record CallerMissedCondition(int Calls, int Minutes) : FlowCondition
{
    public const int MaxCalls = 50;
    public const int MaxMinutes = 7 * 24 * 60;

    public override bool Matches(FlowFacts facts) =>
        facts.IsCall && facts.CallerE164 is not null
        && facts.CallerHistory.Count(c => c.Unanswered && c.At <= facts.At && c.At > facts.At - TimeSpan.FromMinutes(Minutes)) >= Calls;
}

/// <summary>
/// At least <paramref name="Calls"/> calls in on the same number went unanswered in the last <paramref name="Minutes"/>,
/// up to a day, this call included: the phones going unanswered.
/// </summary>
public sealed record NumberMissedCondition(int Calls, int Minutes) : FlowCondition
{
    public const int MaxCalls = 200;
    public const int MaxMinutes = 24 * 60;

    public override bool Matches(FlowFacts facts) =>
        facts.NumberCalls is { } calls && calls.Count(c => c.Unanswered && c.At <= facts.At && c.At > facts.At - TimeSpan.FromMinutes(Minutes)) >= Calls;
}

/// <summary>At least <paramref name="Callers"/> missed callers on the same number are still to be called back.</summary>
public sealed record WaitingCallersCondition(int Callers) : FlowCondition
{
    public const int MaxCallers = 500;

    public override bool Matches(FlowFacts facts) => facts.WaitingCallers >= Callers;
}

/// <summary>
/// The same number's answer rate over the last <paramref name="Hours"/>, up to a day, is below <paramref name="Percent"/>:
/// answered out of answered, missed and sent to voicemail. Only once there are <see cref="MinimumCalls"/> such calls,
/// so one missed call is not a rate of nothing.
/// </summary>
public sealed record AnswerRateCondition(int Percent, int Hours) : FlowCondition
{
    public const int MinimumCalls = 3;
    public const int MaxHours = 24;

    public override bool Matches(FlowFacts facts)
    {
        if (facts.NumberCalls is not { } calls)
        {
            return false;
        }

        var counted = calls.Where(c => c.Counts && c.At <= facts.At && c.At > facts.At - TimeSpan.FromHours(Hours)).ToList();
        return counted.Count >= MinimumCalls && counted.Count(c => c.Outcome == CallOutcome.Answered) * 100 < Percent * counted.Count;
    }
}

/// <summary>The call rang for longer than this before it was answered, went to voicemail or the caller hung up.</summary>
public sealed record RangLongerCondition(int Seconds) : FlowCondition
{
    public const int MaxSeconds = 3600;

    public override bool Matches(FlowFacts facts) => facts.IsCall && facts.RingSeconds > Seconds;
}

/// <summary>The caller is, or is not, someone known: a name of their own on the call or a contact with their number.</summary>
public sealed record KnownCallerCondition(bool Known) : FlowCondition
{
    public override bool Matches(FlowFacts facts) => facts.IsCall && facts.CallerKnown == Known;
}

/// <summary>The caller rang from another country, or from the site's own. Withheld callers are neither.</summary>
public sealed record AbroadCondition(bool Abroad) : FlowCondition
{
    public override bool Matches(FlowFacts facts) => facts.IsCall && facts.FromAbroad == Abroad;
}

/// <summary>
/// Talk put the call through, or did not, to one of its outside contacts. On an incoming call flow, as one of the flow's
/// own conditions, the flow waits for the call to be put through rather than starting when it comes in
/// (<see cref="Flows.WaitsForForward"/>), so it can alert while the outside phone is still ringing.
/// </summary>
public sealed record ForwardedOutsideCondition(bool Forwarded) : FlowCondition
{
    public override bool Matches(FlowFacts facts) => facts.IsCall && facts.ForwardedOutside == Forwarded;
}

/// <summary>Talk scored the call's quality below this, out of 100. A call it did not score is not below anything.</summary>
public sealed record QualityCondition(int Below) : FlowCondition
{
    public override bool Matches(FlowFacts facts) => facts.Quality < Below;
}

/// <summary>At a switchboard, the caller chose any of these menu options (Talk's item ids).</summary>
public sealed record MenuOptionCondition(IReadOnlyList<int> Items) : FlowCondition
{
    public override bool Matches(FlowFacts facts) => Items.Any(facts.MenuChoices.Contains);
}

/// <summary>
/// The voicemail's transcript says any of these words or phrases: whole words, whatever the case, with any run of
/// spaces between the words of a phrase. Only for the Voicemail transcribed trigger.
/// </summary>
public sealed record TranscriptWordsCondition(IReadOnlyList<string> Words) : FlowCondition
{
    public const int MaxWords = 20;
    public const int MaxLength = 50;

    public override bool Matches(FlowFacts facts)
    {
        if (string.IsNullOrWhiteSpace(facts.TranscriptText))
        {
            return false;
        }

        var text = Spaces().Replace(facts.TranscriptText, " ");
        return Words.Select(w => Spaces().Replace(w.Trim(), " ")).Where(w => w.Length > 0)
            .Any(w => Regex.IsMatch(text, $@"\b{Regex.Escape(w)}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)));
    }

    private static Regex Spaces() => SpacesRegex;

    private static readonly Regex SpacesRegex = new(@"\s+", RegexOptions.Compiled, TimeSpan.FromSeconds(1));
}

/// <summary>The call is from, or not from, these callers (<see cref="CallerCondition"/>).</summary>
public sealed record CallerFlowCondition(CallerMode Mode, IReadOnlyList<string> Entries) : FlowCondition
{
    public override bool Matches(FlowFacts facts) => !facts.IsCall || new CallerCondition(Mode, Entries).Matches(facts.CallerE164);
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(NotifyStep), "notify")]
[JsonDerivedType(typeof(WaitStep), "wait")]
[JsonDerivedType(typeof(BranchStep), "branch")]
[JsonDerivedType(typeof(AssignStep), "assign")]
[JsonDerivedType(typeof(BundleStep), "bundle")]
public abstract record FlowStep;

/// <summary>Sends the alert to these people and channels. Urgent sends it at high priority where the channel has one.</summary>
public sealed record NotifyStep(
    IReadOnlyList<FlowRecipient> To, bool Urgent = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] NotifyIncludes Include = NotifyIncludes.None) : FlowStep;

/// <summary>
/// What a notify step sends with the alert, about the call: Talk's summary of it, what was said, and the voicemail. Each
/// reaches a channel only when its owner may read or hear it themselves, and only once there is one.
/// </summary>
[Flags]
public enum NotifyIncludes
{
    None = 0,
    Summary = 1,
    Transcript = 2,
    Voicemail = 4,
}

/// <summary>Waits this long for someone to acknowledge the alert; if nobody does, the flow carries on.</summary>
public sealed record WaitStep(int Minutes) : FlowStep;

/// <summary>Carries on with <see cref="Then"/> when the condition holds, otherwise with <see cref="Otherwise"/>.</summary>
public sealed record BranchStep(FlowCondition If, IReadOnlyList<FlowStep> Then, IReadOnlyList<FlowStep> Otherwise) : FlowStep;

/// <summary>Puts the call on this person's call-backs: they are the one to ring the caller back. Only for call alerts.</summary>
public sealed record AssignStep(Guid Person) : FlowStep;

/// <summary>
/// From here on, what the flow sends to a channel is gathered for this long and sent as one message, instead of one
/// message per alert. It starts a stage of its own, so what comes before it is sent as it was.
/// </summary>
public sealed record BundleStep(int Minutes) : FlowStep
{
    public const int MaxMinutes = 240;
}

/// <summary>
/// Which of the people linked to Talk's outside contacts an <see cref="FlowRecipient.Outside"/> recipient reaches: those
/// behind a contact this call was put through to, or everyone linked to any contact.
/// </summary>
public enum OutsideScope
{
    ThisCall,
    AnyLinked,
}

/// <summary>
/// Who a notify step reaches: a person, on their own channels and only about what they may see; a channel;
/// whoever in a ring group is free when the step runs (<see cref="FreeIn"/>, Talk's group id); whoever the call rang;
/// one of Talk's contacts; or the people linked to Talk's outside contacts. Exactly one of them.
/// </summary>
public sealed record FlowRecipient(Guid? Person = null, Guid? Channel = null, string? FreeIn = null, bool? Rang = null, string? Contact = null, OutsideScope? Outside = null)
{
    /// <summary>
    /// The people in TalkWatch linked to Talk's outside contacts, on their own channels other than email, at once: those
    /// behind a contact the call was put through to, or everyone linked to one. Someone who may not see the call's line
    /// still hears who is calling, and nothing more. Only for call alerts.
    /// </summary>
    public static FlowRecipient ToOutside(OutsideScope scope) => new(Outside: scope);

    /// <summary>
    /// One of Talk's contacts, by uuid, at what Talk holds for them: their email for now. Kept in step with Talk's
    /// directory, so a changed address is used from the next alert.
    /// </summary>
    public static FlowRecipient ToContact(string uuid) => new(Contact: uuid);

    /// <summary>
    /// The people the call rang, as people in TalkWatch linked to their Talk user: the person whose line it was, or
    /// everyone in the group it rang. Only for call alerts.
    /// </summary>
    public static FlowRecipient ToRang() => new(Rang: true);

    public static FlowRecipient ToPerson(Guid id) => new(Person: id);

    public static FlowRecipient ToChannel(Guid id) => new(Channel: id);

    /// <summary>
    /// The members of a ring group who are free, as people in TalkWatch linked to their Talk user; when nobody is free,
    /// every linked member, so the step still reaches someone.
    /// </summary>
    public static FlowRecipient ToFreeIn(string group) => new(FreeIn: group);

    /// <summary>Whether it names exactly one person, channel or group.</summary>
    [JsonIgnore]
    public bool IsOne => (Person is null ? 0 : 1) + (Channel is null ? 0 : 1) + (string.IsNullOrWhiteSpace(FreeIn) ? 0 : 1) + (Rang == true ? 1 : 0)
        + (string.IsNullOrWhiteSpace(Contact) ? 0 : 1) + (Outside is null ? 0 : 1) == 1;
}

/// <summary>
/// One stage of a flow as it runs for one alert: wait this long without an acknowledgement, then notify, and assign the
/// call-back to whoever the stage names.
/// </summary>
public sealed record FlowStage(int WaitMinutes, IReadOnlyList<NotifyStep> Notify)
{
    public IReadOnlyList<Guid> Assign { get; init; } = [];

    /// <summary>How long what this stage sends to a channel is gathered before it goes as one message; 0 sends each at once.</summary>
    public int BundleMinutes { get; init; }
}

public static class Flows
{
    public const int MaxWaitMinutes = 24 * 60;
    public const int MaxSteps = 50;
    public const int MaxDepth = 3;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        // PostgreSQL's jsonb keeps keys in its own order, so "kind" is not always first when a flow is read back.
        AllowOutOfOrderMetadataProperties = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Write(FlowDefinition flow) => JsonSerializer.Serialize(flow, Json);

    /// <summary>Reads a stored or submitted flow; null when it is not one.</summary>
    public static FlowDefinition? Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<FlowDefinition>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            // An unknown or missing "kind".
            return null;
        }
    }

    public static string WritePlan(IReadOnlyList<FlowStage> plan) => JsonSerializer.Serialize(plan, Json);

    public static IReadOnlyList<FlowStage> ReadPlan(string json) => JsonSerializer.Deserialize<List<FlowStage>>(json, Json) ?? [];

    /// <summary>Whether an alert of this type is about a call, and so has a caller.</summary>
    public static bool IsCallAlert(AlertEventType type) =>
        type is AlertEventType.InboundCall or AlertEventType.MissedCall or AlertEventType.Voicemail or AlertEventType.HungUpAtSwitchboard or AlertEventType.NegativeCall
            or AlertEventType.PoorQualityCall or AlertEventType.VoicemailTranscribed;

    /// <summary>Whether an alert of this type is about the whole site rather than any line: for admins' flows only, with no line conditions.</summary>
    public static bool IsSiteAlert(AlertEventType type) =>
        type is AlertEventType.Drift or AlertEventType.AccountProblem or AlertEventType.SettingTurnedOff;

    /// <summary>
    /// Whether an incoming call flow waits for the call to be put through to an outside contact: one of its own conditions
    /// says it must have been. Such a flow runs on the alert raised when Talk puts the call through, not on the one
    /// raised when the call comes in, so it neither runs twice nor misses a call put through after it started ringing.
    /// </summary>
    public static bool WaitsForForward(FlowDefinition flow) =>
        flow.Trigger == AlertEventType.InboundCall && flow.Conditions.Any(c => c is ForwardedOutsideCondition { Forwarded: true });

    /// <summary>Whether the flow is for this alert: its trigger and every condition.</summary>
    public static bool Applies(FlowDefinition flow, FlowFacts facts) =>
        flow.Trigger == facts.Type && flow.Conditions.All(c => c.Matches(facts));

    /// <summary>
    /// The stages the flow runs for this alert. Branches depend only on the alert, so the whole path is known when it
    /// is raised. Notifies with no wait between them are one stage; waits in a row add up; a wait with nothing after it
    /// waits for nothing, and is dropped.
    /// </summary>
    public static IReadOnlyList<FlowStage> Plan(FlowDefinition flow, FlowFacts facts)
    {
        var stages = new List<FlowStage>();
        var wait = 0;
        var notify = new List<NotifyStep>();
        var assign = new List<Guid>();
        var bundle = 0;
        bool Acts() => notify.Count > 0 || assign.Count > 0;
        FlowStage Stage() => new(wait, [.. notify]) { Assign = [.. assign], BundleMinutes = bundle };

        void Walk(IReadOnlyList<FlowStep> steps)
        {
            foreach (var step in steps)
            {
                switch (step)
                {
                    case NotifyStep n:
                        notify.Add(n);
                        break;
                    case AssignStep a:
                        assign.Add(a.Person);
                        break;
                    case BundleStep b:
                        if (Acts())
                        {
                            stages.Add(Stage());
                            (wait, notify, assign) = (0, [], []);
                        }

                        bundle = b.Minutes;
                        break;
                    case WaitStep w when !Acts():
                        wait += w.Minutes;
                        break;
                    case WaitStep w:
                        stages.Add(Stage());
                        (wait, notify, assign) = (w.Minutes, [], []);
                        break;
                    case BranchStep b:
                        Walk(b.If.Matches(facts) ? b.Then : b.Otherwise);
                        break;
                }
            }
        }

        Walk(flow.Steps);
        if (Acts())
        {
            stages.Add(Stage());
        }

        return stages;
    }

    /// <summary>What is wrong with a flow, in words for the person building it, or null when nothing is.</summary>
    public static string? Problem(FlowDefinition flow)
    {
        if (!Enum.IsDefined(flow.Trigger))
        {
            return "Choose what the flow starts on.";
        }

        foreach (var condition in flow.Conditions)
        {
            if (ConditionProblem(condition, flow.Trigger) is { } problem)
            {
                return problem;
            }
        }

        if (Count(flow.Steps) > MaxSteps)
        {
            return $"A flow has at most {MaxSteps} steps.";
        }

        if (!AllNotify(flow.Steps).Any() && !AllAssign(flow.Steps).Any())
        {
            return "Add a step that notifies someone.";
        }

        if (!IsCallAlert(flow.Trigger) && AllNotify(flow.Steps).Any(n => n.Include != NotifyIncludes.None))
        {
            return "A summary, what was said and the voicemail come with alerts about a call.";
        }

        return StepsProblem(flow.Steps, flow.Trigger, depth: 1);
    }

    /// <summary>Every notify step, down every branch.</summary>
    public static IEnumerable<NotifyStep> AllNotify(IReadOnlyList<FlowStep> steps) =>
        steps.SelectMany(s => s switch
        {
            NotifyStep n => [n],
            BranchStep b => AllNotify(b.Then).Concat(AllNotify(b.Otherwise)),
            _ => Enumerable.Empty<NotifyStep>(),
        });

    /// <summary>Every assign step, down every branch.</summary>
    public static IEnumerable<AssignStep> AllAssign(IReadOnlyList<FlowStep> steps) =>
        steps.SelectMany(s => s switch
        {
            AssignStep a => [a],
            BranchStep b => AllAssign(b.Then).Concat(AllAssign(b.Otherwise)),
            _ => Enumerable.Empty<AssignStep>(),
        });

    /// <summary>Every condition the flow tests, in its conditions and its branches.</summary>
    public static IEnumerable<FlowCondition> AllConditions(FlowDefinition flow) => flow.Conditions.Concat(BranchConditions(flow.Steps));

    private static IEnumerable<FlowCondition> BranchConditions(IReadOnlyList<FlowStep> steps) =>
        steps.OfType<BranchStep>().SelectMany(b => new[] { b.If }.Concat(BranchConditions(b.Then)).Concat(BranchConditions(b.Otherwise)));

    private static int Count(IReadOnlyList<FlowStep> steps) =>
        steps.Sum(s => s is BranchStep b ? 1 + Count(b.Then) + Count(b.Otherwise) : 1);

    private static string? StepsProblem(IReadOnlyList<FlowStep> steps, AlertEventType trigger, int depth)
    {
        for (var i = 0; i < steps.Count; i++)
        {
            switch (steps[i])
            {
                case NotifyStep n when n.To.Count == 0:
                    return "Each notify step needs at least one person or channel.";
                case NotifyStep n when n.To.Any(r => !r.IsOne):
                    return "Each recipient is a person, a channel or a ring group.";
                case BundleStep b when b.Minutes is < 1 or > BundleStep.MaxMinutes:
                    return $"A bundle gathers for between 1 and {BundleStep.MaxMinutes} minutes.";
                case AssignStep when !IsCallAlert(trigger):
                    return "Assigning the call-back is for call alerts: a handset or TalkWatch itself has no caller to ring back.";
                case AssignStep a when a.Person == Guid.Empty:
                    return "Choose who the call-back goes to.";
                case WaitStep w when w.Minutes is < 1 or > MaxWaitMinutes:
                    return $"A wait is between 1 and {MaxWaitMinutes} minutes.";
                case BranchStep when depth >= MaxDepth:
                    return $"Branches go at most {MaxDepth - 1} deep.";
                case BranchStep b:
                    if (ConditionProblem(b.If, trigger) is { } condition)
                    {
                        return condition;
                    }

                    if ((StepsProblem(b.Then, trigger, depth + 1) ?? StepsProblem(b.Otherwise, trigger, depth + 1)) is { } inner)
                    {
                        return inner;
                    }

                    break;
                case null:
                    return "A step is missing.";
            }
        }

        return null;
    }

    private static string? ConditionProblem(FlowCondition? condition, AlertEventType trigger) => condition switch
    {
        null => "A condition is missing.",
        LineCondition { Lines.Count: 0 } => "Choose at least one line.",
        LineCondition when IsSiteAlert(trigger) => "An alert about the whole site is on no line.",
        TimeCondition t when t.Days is < 1 or > 127 => "Choose at least one day.",
        TimeCondition t when t.Start == t.End => "A time window needs a start and an end, and they cannot be the same.",
        CallerFlowCondition when !IsCallAlert(trigger) => "A caller condition is for call alerts: a handset or TalkWatch itself has no caller.",
        CallerFlowCondition c when !Enum.IsDefined(c.Mode) || c.Mode == CallerMode.Any => "Choose only or every caller except.",
        CallerFlowCondition { Entries.Count: 0 } => "List the callers the condition is about.",
        RangLongerCondition or KnownCallerCondition or AbroadCondition or QualityCondition or MenuOptionCondition or ForwardedOutsideCondition when !IsCallAlert(trigger) =>
            "That condition is for call alerts: a handset or TalkWatch itself has no call.",
        RangLongerCondition r when r.Seconds is < 1 or > RangLongerCondition.MaxSeconds => $"How long it rang is between 1 and {RangLongerCondition.MaxSeconds} seconds.",
        QualityCondition q when q.Below is < 1 or > 100 => "A quality score is between 1 and 100.",
        MenuOptionCondition { Items.Count: 0 } => "Choose at least one menu option.",
        TranscriptWordsCondition when trigger != AlertEventType.VoicemailTranscribed => "What a voicemail says can be tested only when the flow starts on Voicemail transcribed.",
        TranscriptWordsCondition w when w.Words.All(string.IsNullOrWhiteSpace) => "List at least one word or phrase.",
        TranscriptWordsCondition w when w.Words.Count > TranscriptWordsCondition.MaxWords => $"List at most {TranscriptWordsCondition.MaxWords} words or phrases.",
        TranscriptWordsCondition w when w.Words.Any(x => x.Length > TranscriptWordsCondition.MaxLength) => $"A word or phrase is at most {TranscriptWordsCondition.MaxLength} characters.",
        RepeatCallerCondition when !IsCallAlert(trigger) => "A repeat caller condition is for call alerts: a handset or TalkWatch itself has no caller.",
        RepeatCallerCondition r when r.Calls < 2 => "A repeat caller has rung at least 2 times.",
        RepeatCallerCondition r when r.Calls > RepeatCallerCondition.MaxCalls => $"Count at most {RepeatCallerCondition.MaxCalls} calls.",
        RepeatCallerCondition r when r.Minutes is < 1 or > MaxWaitMinutes => $"The window is between 1 and {MaxWaitMinutes} minutes.",
        CallerMissedCondition or NumberMissedCondition or WaitingCallersCondition or AnswerRateCondition when !IsCallAlert(trigger) =>
            "That condition is about calls: a handset or TalkWatch itself has none.",
        CallerMissedCondition c when c.Calls is < 1 or > CallerMissedCondition.MaxCalls => $"Count between 1 and {CallerMissedCondition.MaxCalls} calls.",
        CallerMissedCondition c when c.Minutes is < 1 or > CallerMissedCondition.MaxMinutes => "Look back between 1 hour and 7 days.",
        NumberMissedCondition n when n.Calls is < 1 or > NumberMissedCondition.MaxCalls => $"Count between 1 and {NumberMissedCondition.MaxCalls} calls.",
        NumberMissedCondition n when n.Minutes is < 1 or > NumberMissedCondition.MaxMinutes => $"Look back between 1 and {NumberMissedCondition.MaxMinutes} minutes.",
        WaitingCallersCondition w when w.Callers is < 1 or > WaitingCallersCondition.MaxCallers => $"Count between 1 and {WaitingCallersCondition.MaxCallers} callers.",
        AnswerRateCondition a when a.Percent is < 1 or > 100 => "An answer rate is between 1 and 100 percent.",
        AnswerRateCondition a when a.Hours is < 1 or > AnswerRateCondition.MaxHours => $"Look back between 1 and {AnswerRateCondition.MaxHours} hours.",
        _ => null,
    };
}
