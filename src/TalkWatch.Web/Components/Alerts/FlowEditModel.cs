using TalkWatch.Core.Alerts;
using TalkWatch.Core.Calls;

namespace TalkWatch.Web.Components.Alerts;

/// <summary>A condition as the editor holds it while someone changes it. <see cref="ToCondition"/> makes the stored one.</summary>
public sealed class EditCondition
{
    public const string LineKind = "line", TimeKind = "time", CallerKind = "caller", RangKind = "rang", RepeatKind = "repeat",
        RangLongerKind = "rang-longer", KnownKind = "known", AbroadKind = "abroad", QualityKind = "quality", MenuKind = "menu", WordsKind = "words",
        CallerMissedKind = "caller-missed", NumberMissedKind = "number-missed", WaitingKind = "waiting", AnswerRateKind = "answer-rate",
        ForwardedKind = "forwarded";

    /// <summary>The kinds only a call alert can have: a handset or TalkWatch itself has no caller and rang nobody.</summary>
    public static readonly string[] CallKinds = [CallerKind, RepeatKind, RangLongerKind, KnownKind, AbroadKind, QualityKind, MenuKind,
        CallerMissedKind, NumberMissedKind, WaitingKind, AnswerRateKind, ForwardedKind];

    /// <summary>As typed: words and phrases, separated by commas or lines.</summary>
    public string Words { get; set; } = "";

    public string Kind { get; set; } = TimeKind;

    public List<LineRef> Lines { get; set; } = [];

    public HashSet<DayOfWeek> Days { get; set; } = [.. TimeWindow.Weekdays];
    public TimeOnly Start { get; set; } = new(9, 0);
    public TimeOnly End { get; set; } = new(17, 30);
    public bool Outside { get; set; }

    public CallerMode Mode { get; set; } = CallerMode.Except;

    /// <summary>As typed: numbers in any format, +prefix* and 'withheld', separated by commas or lines. Saving reads them.</summary>
    public string Callers { get; set; } = CallerCondition.Withheld;

    public int RingSeconds { get; set; } = 30;
    public bool Known { get; set; }

    /// <summary>For a put-through-outside condition: whether the call was, or was not.</summary>
    public bool Forwarded { get; set; } = true;
    public bool Abroad { get; set; } = true;
    public int QualityBelow { get; set; } = 70;
    public List<int> MenuItems { get; set; } = [];

    public int RepeatCalls { get; set; } = 3;
    public int RepeatMinutes { get; set; } = 30;

    public int MissedCalls { get; set; } = 3;

    /// <summary>A caller-missed window in hours, as the editor asks for it; kept in minutes.</summary>
    public int MissedHours { get; set; } = 24;

    /// <summary>A number's window in minutes.</summary>
    public int MissedMinutes { get; set; } = 30;

    public int WaitingCallers { get; set; } = 5;
    public int RatePercent { get; set; } = 70;
    public int RateHours { get; set; } = 2;

    public static EditCondition New(string kind) => new() { Kind = kind };

    public FlowCondition ToCondition() => Kind switch
    {
        LineKind or RangKind => new LineCondition([.. Lines]),
        RepeatKind => new RepeatCallerCondition(RepeatCalls, RepeatMinutes),
        CallerMissedKind => new CallerMissedCondition(MissedCalls, MissedHours * 60),
        NumberMissedKind => new NumberMissedCondition(MissedCalls, MissedMinutes),
        WaitingKind => new WaitingCallersCondition(WaitingCallers),
        AnswerRateKind => new AnswerRateCondition(RatePercent, RateHours),
        RangLongerKind => new RangLongerCondition(RingSeconds),
        KnownKind => new KnownCallerCondition(Known),
        ForwardedKind => new ForwardedOutsideCondition(Forwarded),
        AbroadKind => new AbroadCondition(Abroad),
        QualityKind => new QualityCondition(QualityBelow),
        MenuKind => new MenuOptionCondition([.. MenuItems]),
        WordsKind => new TranscriptWordsCondition(Words.Split([',', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)),
        CallerKind => new CallerFlowCondition(Mode, Callers.Split([',', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)),
        _ => new TimeCondition(TimeWindow.ToMask(Days), Start, End, Outside),
    };

    public static EditCondition From(FlowCondition condition) => condition switch
    {
        LineCondition l => new() { Kind = FlowText.IsRang(l) ? RangKind : LineKind, Lines = [.. l.Lines] },
        RepeatCallerCondition r => new() { Kind = RepeatKind, RepeatCalls = r.Calls, RepeatMinutes = r.Minutes },
        CallerMissedCondition c => new() { Kind = CallerMissedKind, MissedCalls = c.Calls, MissedHours = Math.Max(1, c.Minutes / 60) },
        NumberMissedCondition n => new() { Kind = NumberMissedKind, MissedCalls = n.Calls, MissedMinutes = n.Minutes },
        WaitingCallersCondition w => new() { Kind = WaitingKind, WaitingCallers = w.Callers },
        AnswerRateCondition a => new() { Kind = AnswerRateKind, RatePercent = a.Percent, RateHours = a.Hours },
        RangLongerCondition r => new() { Kind = RangLongerKind, RingSeconds = r.Seconds },
        KnownCallerCondition k => new() { Kind = KnownKind, Known = k.Known },
        ForwardedOutsideCondition f => new() { Kind = ForwardedKind, Forwarded = f.Forwarded },
        AbroadCondition a => new() { Kind = AbroadKind, Abroad = a.Abroad },
        QualityCondition q => new() { Kind = QualityKind, QualityBelow = q.Below },
        MenuOptionCondition m => new() { Kind = MenuKind, MenuItems = [.. m.Items] },
        TranscriptWordsCondition w => new() { Kind = WordsKind, Words = string.Join(", ", w.Words) },
        CallerFlowCondition c => new() { Kind = CallerKind, Mode = c.Mode, Callers = string.Join(", ", c.Entries) },
        TimeCondition t => new() { Kind = TimeKind, Days = TimeWindow.FromMask(t.Days), Start = t.Start, End = t.End, Outside = t.Outside },
        _ => new(),
    };
}

/// <summary>A step as the editor holds it. A notify step's recipients are "person:{id}", "channel:{id}" or "free:{group}".</summary>
public sealed class EditStep
{
    public const string NotifyKind = "notify", WaitKind = "wait", BranchKind = "branch", AssignKind = "assign", BundleKind = "bundle";

    /// <summary>For an assign step: who gets the call-back.</summary>
    public Guid? Person { get; set; }

    public string Kind { get; set; } = NotifyKind;

    public List<string> To { get; set; } = [];
    public bool Urgent { get; set; }

    /// <summary>For a notify step: the summary, what was said and the voicemail, sent with the alert.</summary>
    public NotifyIncludes Include { get; set; }

    public int Minutes { get; set; } = 10;

    public EditCondition If { get; set; } = new();
    public List<EditStep> Then { get; set; } = [];
    public List<EditStep> Otherwise { get; set; } = [];

    public static EditStep New(string kind) => new() { Kind = kind };

    public FlowStep ToStep() => Kind switch
    {
        WaitKind => new WaitStep(Minutes),
        AssignKind => new AssignStep(Person ?? Guid.Empty),
        BundleKind => new BundleStep(Minutes),
        BranchKind => new BranchStep(If.ToCondition(), [.. Then.Select(s => s.ToStep())], [.. Otherwise.Select(s => s.ToStep())]),
        _ => new NotifyStep([.. To.Select(Recipient)], Urgent, Include),
    };

    public static EditStep From(FlowStep step) => step switch
    {
        WaitStep w => new() { Kind = WaitKind, Minutes = w.Minutes },
        AssignStep a => new() { Kind = AssignKind, Person = a.Person },
        BundleStep b => new() { Kind = BundleKind, Minutes = b.Minutes },
        BranchStep b => new() { Kind = BranchKind, If = EditCondition.From(b.If), Then = [.. b.Then.Select(From)], Otherwise = [.. b.Otherwise.Select(From)] },
        NotifyStep n => new() { Kind = NotifyKind, Urgent = n.Urgent, To = [.. n.To.Select(Key)], Include = n.Include },
        _ => new(),
    };

    public static string Key(FlowRecipient recipient) => recipient switch
    {
        { Person: { } person } => $"person:{person}",
        { FreeIn: { } group } => $"free:{group}",
        { Rang: true } => "rang:",
        { Outside: { } scope } => $"outside:{scope}",
        { Contact: { } contact } => $"contact:{contact}",
        _ => $"channel:{recipient.Channel}",
    };

    public static FlowRecipient Recipient(string key)
    {
        var at = key.IndexOf(':', StringComparison.Ordinal);
        var (kind, id) = (key[..at], key[(at + 1)..]);
        return kind switch
        {
            "person" => FlowRecipient.ToPerson(Guid.Parse(id)),
            "free" => FlowRecipient.ToFreeIn(id),
            "rang" => FlowRecipient.ToRang(),
            "outside" => FlowRecipient.ToOutside(Enum.Parse<OutsideScope>(id)),
            "contact" => FlowRecipient.ToContact(id),
            _ => FlowRecipient.ToChannel(Guid.Parse(id)),
        };
    }
}
