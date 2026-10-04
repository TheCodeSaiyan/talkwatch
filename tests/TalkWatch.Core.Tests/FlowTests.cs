using TalkWatch.Core.Alerts;
using TalkWatch.Core.Calls;

namespace TalkWatch.Core.Tests;

public class FlowTests
{
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
    private static readonly LineRef Sales = new(LineKind.Did, "+441144960042");
    private static readonly LineRef Support = new(LineKind.Did, "+441144960043");
    private static readonly Guid SalesTeam = Guid.NewGuid(), OnCall = Guid.NewGuid(), Nigel = Guid.NewGuid(), Manager = Guid.NewGuid();

    private static readonly TimeCondition OfficeHours = new(TimeWindow.ToMask(TimeWindow.Weekdays), new TimeOnly(9, 0), new TimeOnly(17, 30), Outside: false);

    // The flow the design was agreed on: in hours to the sales team, then Nigel, then the office manager; out of hours to
    // whoever is on call, then Nigel at high priority.
    private static readonly FlowDefinition MissedOnSales = new()
    {
        Trigger = AlertEventType.MissedCall,
        Conditions = [new LineCondition([Sales]), new CallerFlowCondition(CallerMode.Except, [CallerCondition.Withheld])],
        Steps =
        [
            new BranchStep(OfficeHours,
                Then:
                [
                    new NotifyStep([FlowRecipient.ToChannel(SalesTeam)]),
                    new WaitStep(10),
                    new NotifyStep([FlowRecipient.ToPerson(Nigel)]),
                    new WaitStep(30),
                    new NotifyStep([FlowRecipient.ToPerson(Manager)]),
                ],
                Otherwise:
                [
                    new NotifyStep([FlowRecipient.ToChannel(OnCall)]),
                    new WaitStep(15),
                    new NotifyStep([FlowRecipient.ToPerson(Nigel)], Urgent: true),
                ]),
        ],
    };

    private static FlowFacts Missed(string at, LineRef line, string? caller = "+447700900123") =>
        new(AlertEventType.MissedCall, DateTimeOffset.Parse(at, System.Globalization.CultureInfo.InvariantCulture), new HashSet<LineRef> { line }, IsCall: true, caller, London);

    [Fact]
    public void In_office_hours_the_flow_runs_the_then_ladder()
    {
        // A Tuesday at 11:00 London time.
        var plan = Flows.Plan(MissedOnSales, Missed("2026-09-29T10:00:00Z", Sales));

        Assert.Equal([0, 10, 30], plan.Select(s => s.WaitMinutes));
        Assert.Equal([SalesTeam], plan[0].Notify.SelectMany(n => n.To).Select(r => r.Channel));
        Assert.Equal([Nigel], plan[1].Notify.SelectMany(n => n.To).Select(r => r.Person));
        Assert.Equal([Manager], plan[2].Notify.SelectMany(n => n.To).Select(r => r.Person));
    }

    [Fact]
    public void Out_of_hours_the_flow_runs_the_otherwise_ladder()
    {
        // A Saturday.
        var plan = Flows.Plan(MissedOnSales, Missed("2026-10-03T10:00:00Z", Sales));

        Assert.Equal([0, 15], plan.Select(s => s.WaitMinutes));
        Assert.Equal([OnCall], plan[0].Notify.SelectMany(n => n.To).Select(r => r.Channel));
        Assert.True(plan[1].Notify.Single().Urgent);
    }

    [Theory]
    [InlineData("+441144960042", "+447700900123", true)]
    [InlineData("+441144960043", "+447700900123", false)] // Another line.
    [InlineData("+441144960042", null, false)] // Withheld.
    public void Conditions_all_have_to_hold(string line, string? caller, bool applies) =>
        Assert.Equal(applies, Flows.Applies(MissedOnSales, Missed("2026-09-29T10:00:00Z", new LineRef(LineKind.Did, line), caller)));

    // Calls from the caller of a 10:00 missed call, this one included, as TalkWatch has them stored.
    private static FlowFacts MissedFrom(string? caller, params string[] earlierCallTimes) =>
        Missed("2026-09-29T10:00:00Z", Sales, caller) with
        {
            CallerCalls = [.. earlierCallTimes.Append("2026-09-29T10:00:00Z").Select(t => DateTimeOffset.Parse(t, System.Globalization.CultureInfo.InvariantCulture))],
        };

    [Theory]
    [InlineData(new[] { "2026-09-29T09:45:00Z", "2026-09-29T09:55:00Z" }, true)] // Three calls in twenty minutes.
    [InlineData(new[] { "2026-09-29T09:55:00Z" }, false)] // Only two.
    [InlineData(new[] { "2026-09-29T09:10:00Z", "2026-09-29T09:55:00Z" }, false)] // Three, but one was 50 minutes ago.
    public void A_repeat_caller_has_rung_that_many_times_within_the_window_counting_this_call(string[] earlier, bool matches) =>
        Assert.Equal(matches, new RepeatCallerCondition(Calls: 3, Minutes: 30).Matches(MissedFrom("+447700900123", earlier)));

    [Fact]
    public void A_withheld_caller_is_never_a_repeat_caller() =>
        Assert.False(new RepeatCallerCondition(Calls: 2, Minutes: 30).Matches(MissedFrom(null, "2026-09-29T09:55:00Z")));

    [Fact]
    public void A_repeat_caller_branch_sends_a_second_attempt_somewhere_else()
    {
        var flow = new FlowDefinition
        {
            Trigger = AlertEventType.MissedCall,
            Steps = [new BranchStep(new RepeatCallerCondition(2, 60), Then: [new NotifyStep([FlowRecipient.ToPerson(Manager)], Urgent: true)], Otherwise: [new NotifyStep([FlowRecipient.ToChannel(SalesTeam)])])],
        };

        Assert.Equal([Manager], Flows.Plan(flow, MissedFrom("+447700900123", "2026-09-29T09:30:00Z")).Single().Notify.SelectMany(n => n.To).Select(r => r.Person));
        Assert.Equal([SalesTeam], Flows.Plan(flow, MissedFrom("+447700900123")).Single().Notify.SelectMany(n => n.To).Select(r => r.Channel));
    }

    [Theory]
    [InlineData(1, 30, "at least 2")]
    [InlineData(21, 30, "at most 20")]
    [InlineData(3, 0, "between 1 and 1440 minutes")]
    public void A_repeat_caller_condition_needs_a_sensible_count_and_window(int calls, int minutes, string problem)
    {
        var flow = MissedOnSales with { Conditions = [new RepeatCallerCondition(calls, minutes)] };

        Assert.Contains(problem, Flows.Problem(flow), StringComparison.Ordinal);
    }

    [Fact]
    public void A_repeat_caller_condition_is_for_call_alerts() =>
        Assert.Contains("call alerts", Flows.Problem(MissedOnSales with { Trigger = AlertEventType.HandsetOffline, Conditions = [new RepeatCallerCondition(2, 30)] }), StringComparison.Ordinal);

    [Fact]
    public void Whoever_is_free_in_a_ring_group_is_a_recipient_that_survives_being_stored()
    {
        var flow = MissedOnSales with { Steps = [new NotifyStep([FlowRecipient.ToFreeIn("12")])] };

        var read = Flows.Read(Flows.Write(flow));

        Assert.Null(Flows.Problem(flow));
        Assert.Equal("12", Assert.IsType<NotifyStep>(Assert.Single(read!.Steps)).To.Single().FreeIn);
    }

    [Fact]
    public void A_recipient_names_one_person_channel_or_group_not_two() =>
        Assert.Contains("a person, a channel or a ring group", Flows.Problem(MissedOnSales with { Steps = [new NotifyStep([new FlowRecipient(Person: Nigel, FreeIn: "12")])] }), StringComparison.Ordinal);

    // A 10:00 missed call with the facts the newer conditions are judged on.
    private static FlowFacts MissedWith(double? rang = null, bool known = false, bool? abroad = null, int? quality = null, params int[] chose) =>
        Missed("2026-09-29T10:00:00Z", Sales) with { RingSeconds = rang, CallerKnown = known, FromAbroad = abroad, Quality = quality, MenuChoices = chose.ToHashSet() };

    [Theory]
    [InlineData(45.0, true)]
    [InlineData(30.0, false)] // Exactly the threshold is not longer than it.
    [InlineData(null, false)] // Not known how long it rang.
    public void Rang_longer_than_holds_only_past_the_threshold(double? rang, bool matches) =>
        Assert.Equal(matches, new RangLongerCondition(30).Matches(MissedWith(rang: rang)));

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, false, true)]
    public void A_known_caller_condition_picks_contacts_or_strangers(bool wantKnown, bool known, bool matches) =>
        Assert.Equal(matches, new KnownCallerCondition(wantKnown).Matches(MissedWith(known: known)));

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(true, null, false)] // Withheld: where they rang from is not known.
    [InlineData(false, null, false)]
    public void An_abroad_condition_needs_to_know_where_the_caller_rang_from(bool wantAbroad, bool? abroad, bool matches) =>
        Assert.Equal(matches, new AbroadCondition(wantAbroad).Matches(MissedWith(abroad: abroad)));

    [Theory]
    [InlineData(55, true)]
    [InlineData(70, false)]
    [InlineData(null, false)] // Talk did not score it.
    public void A_quality_condition_holds_below_the_score(int? quality, bool matches) =>
        Assert.Equal(matches, new QualityCondition(70).Matches(MissedWith(quality: quality)));

    [Fact]
    public void A_menu_option_condition_holds_when_the_caller_chose_any_of_them()
    {
        var condition = new MenuOptionCondition([16, 42]);

        Assert.True(condition.Matches(MissedWith(chose: [3, 42])));
        Assert.False(condition.Matches(MissedWith(chose: [3])));
        Assert.False(condition.Matches(MissedWith()));
    }

    [Fact]
    public void The_newer_conditions_and_whoever_it_rang_survive_being_stored()
    {
        var flow = MissedOnSales with
        {
            Conditions = [new RangLongerCondition(20), new KnownCallerCondition(false), new AbroadCondition(true), new QualityCondition(60), new MenuOptionCondition([16])],
            Steps = [new NotifyStep([FlowRecipient.ToRang()])],
        };

        var read = Flows.Read(Flows.Write(flow))!;

        Assert.Null(Flows.Problem(flow));
        Assert.Equal(flow.Conditions, read.Conditions, (a, b) => a.GetType() == b.GetType() && Flows.Write(MissedOnSales with { Conditions = [a] }) == Flows.Write(MissedOnSales with { Conditions = [b] }));
        Assert.True(Assert.IsType<NotifyStep>(Assert.Single(read.Steps)).To.Single().Rang);
    }

    [Theory]
    [InlineData("rang", "between 1 and 3600 seconds")]
    [InlineData("quality", "between 1 and 100")]
    [InlineData("menu", "Choose at least one menu option")]
    [InlineData("handset", "for call alerts")]
    public void The_newer_conditions_say_what_is_wrong(string which, string problem)
    {
        var flow = which switch
        {
            "rang" => MissedOnSales with { Conditions = [new RangLongerCondition(0)] },
            "quality" => MissedOnSales with { Conditions = [new QualityCondition(101)] },
            "menu" => MissedOnSales with { Conditions = [new MenuOptionCondition([])] },
            _ => MissedOnSales with { Trigger = AlertEventType.HandsetOffline, Conditions = [new KnownCallerCondition(true)] },
        };

        Assert.Contains(problem, Flows.Problem(flow), StringComparison.Ordinal);
    }

    [Fact]
    public void Assigning_the_call_back_happens_in_the_stage_it_is_in()
    {
        var flow = MissedOnSales with
        {
            Conditions = [],
            Steps = [new NotifyStep([FlowRecipient.ToChannel(SalesTeam)]), new WaitStep(15), new AssignStep(Nigel), new NotifyStep([FlowRecipient.ToPerson(Nigel)])],
        };

        var plan = Flows.Plan(flow, Missed("2026-09-29T10:00:00Z", Sales));

        Assert.Equal([0, 15], plan.Select(s => s.WaitMinutes));
        Assert.Empty(plan[0].Assign);
        Assert.Equal([Nigel], plan[1].Assign);
    }

    [Fact]
    public void A_flow_may_only_assign_the_call_back_and_tell_nobody()
    {
        var flow = MissedOnSales with { Conditions = [], Steps = [new AssignStep(Nigel)] };

        Assert.Null(Flows.Problem(flow));
        Assert.Equal([Nigel], Assert.Single(Flows.Plan(flow, Missed("2026-09-29T10:00:00Z", Sales))).Assign);
        Assert.IsType<AssignStep>(Assert.Single(Flows.Read(Flows.Write(flow))!.Steps));
    }

    [Fact]
    public void Assigning_the_call_back_is_for_call_alerts() =>
        Assert.Contains("for call alerts", Flows.Problem(MissedOnSales with { Trigger = AlertEventType.HandsetOffline, Conditions = [], Steps = [new AssignStep(Nigel)] }), StringComparison.Ordinal);

    [Fact]
    public void A_bundle_step_gathers_the_stages_after_it()
    {
        var flow = MissedOnSales with
        {
            Conditions = [],
            Steps = [new NotifyStep([FlowRecipient.ToChannel(OnCall)]), new BundleStep(15), new NotifyStep([FlowRecipient.ToChannel(SalesTeam)]), new WaitStep(30), new NotifyStep([FlowRecipient.ToPerson(Nigel)])],
        };

        var plan = Flows.Plan(flow, Missed("2026-09-29T10:00:00Z", Sales));

        // On call hears at once; the bundle starts a stage of its own, so the sales team and Nigel's later stage are
        // each gathered for 15 minutes.
        Assert.Equal([0, 0, 30], plan.Select(s => s.WaitMinutes));
        Assert.Equal([0, 15, 15], plan.Select(s => s.BundleMinutes));
        Assert.Equal([OnCall], plan[0].Notify.SelectMany(n => n.To).Select(r => r.Channel));
        Assert.Equal([SalesTeam], plan[1].Notify.SelectMany(n => n.To).Select(r => r.Channel));
    }

    [Fact]
    public void A_bundle_step_applies_from_the_next_stage_and_to_every_stage_after()
    {
        var flow = MissedOnSales with
        {
            Conditions = [],
            Steps = [new BundleStep(15), new NotifyStep([FlowRecipient.ToChannel(SalesTeam)]), new WaitStep(30), new NotifyStep([FlowRecipient.ToPerson(Nigel)])],
        };

        var plan = Flows.Plan(flow, Missed("2026-09-29T10:00:00Z", Sales));

        Assert.Equal([15, 15], plan.Select(s => s.BundleMinutes));
        Assert.IsType<BundleStep>(Flows.Read(Flows.Write(flow))!.Steps[0]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(241)]
    public void A_bundle_lasts_between_a_minute_and_four_hours(int minutes) =>
        Assert.Contains("between 1 and 240 minutes", Flows.Problem(MissedOnSales with { Conditions = [], Steps = [new BundleStep(minutes), new NotifyStep([FlowRecipient.ToChannel(SalesTeam)])] }), StringComparison.Ordinal);

    private static FlowFacts Transcribed(string? text) =>
        new FlowFacts(AlertEventType.VoicemailTranscribed, DateTimeOffset.Parse("2026-09-29T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture), new HashSet<LineRef> { Sales }, IsCall: true, "+447700900123", London)
        with { TranscriptText = text };

    [Theory]
    [InlineData("Hi, it's Sam. The boiler has burst, it's urgent, please ring back.", true)]
    [InlineData("hi it's sam, URGENT please", true)]              // Whatever the case.
    [InlineData("This isn't an emergency, just urgently curious.", false)] // Whole words only: "urgently" is not "urgent".
    [InlineData("Just ringing to say thanks.", false)]
    [InlineData(null, false)]                                       // No transcript, nothing said.
    public void A_words_condition_holds_when_the_voicemail_says_any_of_them(string? text, bool matches) =>
        Assert.Equal(matches, new TranscriptWordsCondition(["urgent", "burst pipe"]).Matches(Transcribed(text)));

    [Fact]
    public void A_words_condition_matches_a_phrase_as_a_whole() =>
        Assert.True(new TranscriptWordsCondition(["burst pipe"]).Matches(Transcribed("There's a burst  pipe in the kitchen")));

    [Fact]
    public void Voicemail_transcribed_is_a_call_alert_and_a_words_condition_is_only_for_it()
    {
        Assert.True(Flows.IsCallAlert(AlertEventType.VoicemailTranscribed));
        var words = new TranscriptWordsCondition(["urgent"]);

        Assert.Null(Flows.Problem(MissedOnSales with { Trigger = AlertEventType.VoicemailTranscribed, Conditions = [words] }));
        Assert.Contains("Voicemail transcribed", Flows.Problem(MissedOnSales with { Conditions = [words] }), StringComparison.Ordinal);
        Assert.Contains("at least one word", Flows.Problem(MissedOnSales with { Trigger = AlertEventType.VoicemailTranscribed, Conditions = [new TranscriptWordsCondition([])] }), StringComparison.Ordinal);
        Assert.IsType<TranscriptWordsCondition>(Flows.Read(Flows.Write(MissedOnSales with { Trigger = AlertEventType.VoicemailTranscribed, Conditions = [words] }))!.Conditions.Single());
    }

    [Fact]
    public void Another_trigger_does_not_apply() =>
        Assert.False(Flows.Applies(MissedOnSales, Missed("2026-09-29T10:00:00Z", Sales) with { Type = AlertEventType.Voicemail }));

    [Fact]
    public void Waits_in_a_row_add_up_a_leading_wait_delays_the_first_stage_and_a_trailing_wait_is_dropped()
    {
        var flow = new FlowDefinition
        {
            Trigger = AlertEventType.Voicemail,
            Steps =
            [
                new WaitStep(5),
                new NotifyStep([FlowRecipient.ToChannel(SalesTeam)]),
                new NotifyStep([FlowRecipient.ToPerson(Nigel)]),
                new WaitStep(10),
                new WaitStep(20),
                new NotifyStep([FlowRecipient.ToChannel(OnCall)]),
                new WaitStep(60),
            ],
        };

        var plan = Flows.Plan(flow, Missed("2026-09-29T10:00:00Z", Sales) with { Type = AlertEventType.Voicemail });

        Assert.Equal([(5, 2), (30, 1)], plan.Select(s => (s.WaitMinutes, s.Notify.Count)));
    }

    [Fact]
    public void A_flow_survives_being_stored_and_read_back()
    {
        var read = Flows.Read(Flows.Write(MissedOnSales))!;

        Assert.Equal(Flows.Write(MissedOnSales), Flows.Write(read));
        Assert.Equal(
            Flows.Plan(MissedOnSales, Missed("2026-10-03T10:00:00Z", Sales)).Select(s => s.WaitMinutes),
            Flows.Plan(read, Missed("2026-10-03T10:00:00Z", Sales)).Select(s => s.WaitMinutes));
    }

    [Fact]
    public void The_stored_form_is_plain_json_that_names_each_step()
    {
        var json = Flows.Write(new FlowDefinition
        {
            Trigger = AlertEventType.MissedCall,
            Conditions = [new TimeCondition(62, new TimeOnly(9, 0), new TimeOnly(17, 30), Outside: true)],
            Steps = [new NotifyStep([FlowRecipient.ToPerson(Nigel)], Urgent: true)],
        });

        Assert.Equal(
            $$"""{"trigger":"MissedCall","conditions":[{"kind":"time","days":62,"start":"09:00:00","end":"17:30:00","outside":true}],"steps":[{"kind":"notify","to":[{"person":"{{Nigel}}"}],"urgent":true}]}""",
            json);
    }

    [Theory]
    [InlineData("""{"trigger":"MissedCall","steps":[{"kind":"teleport"}]}""")]
    [InlineData("""{"trigger":"MissedCall","steps":[{"minutes":5}]}""")]
    [InlineData("not json")]
    [InlineData("")]
    public void What_is_not_a_flow_reads_as_nothing(string json) => Assert.Null(Flows.Read(json));

    public static TheoryData<FlowDefinition, string> Broken => new()
    {
        { new FlowDefinition { Trigger = AlertEventType.MissedCall, Steps = [new WaitStep(5)] }, "Add a step that notifies someone." },
        { new FlowDefinition { Trigger = AlertEventType.MissedCall, Steps = [new NotifyStep([])] }, "Each notify step needs at least one person or channel." },
        { new FlowDefinition { Trigger = AlertEventType.MissedCall, Steps = [new NotifyStep([new FlowRecipient()])] }, "Each recipient is a person, a channel or a ring group." },
        { new FlowDefinition { Trigger = AlertEventType.MissedCall, Steps = [new NotifyStep([new FlowRecipient(Nigel, SalesTeam)])] }, "Each recipient is a person, a channel or a ring group." },
        { new FlowDefinition { Trigger = AlertEventType.MissedCall, Steps = [new NotifyStep([FlowRecipient.ToPerson(Nigel)]), new WaitStep(0), new NotifyStep([FlowRecipient.ToPerson(Nigel)])] }, "A wait is between 1 and 1440 minutes." },
        { new FlowDefinition { Trigger = AlertEventType.MissedCall, Conditions = [new LineCondition([])], Steps = [new NotifyStep([FlowRecipient.ToPerson(Nigel)])] }, "Choose at least one line." },
        { new FlowDefinition { Trigger = AlertEventType.Drift, Conditions = [new LineCondition([Sales])], Steps = [new NotifyStep([FlowRecipient.ToPerson(Nigel)])] }, "An alert about the whole site is on no line." },
        { new FlowDefinition { Trigger = AlertEventType.MissedCall, Conditions = [OfficeHours with { Days = 0 }], Steps = [new NotifyStep([FlowRecipient.ToPerson(Nigel)])] }, "Choose at least one day." },
        { new FlowDefinition { Trigger = AlertEventType.MissedCall, Conditions = [OfficeHours with { End = new TimeOnly(9, 0) }], Steps = [new NotifyStep([FlowRecipient.ToPerson(Nigel)])] }, "A time window needs a start and an end, and they cannot be the same." },
        { new FlowDefinition { Trigger = AlertEventType.HandsetOffline, Steps = [new BranchStep(new CallerFlowCondition(CallerMode.Only, ["+447700900123"]), [new NotifyStep([FlowRecipient.ToPerson(Nigel)])], [])] }, "A caller condition is for call alerts: a handset or TalkWatch itself has no caller." },
        { new FlowDefinition { Trigger = AlertEventType.MissedCall, Conditions = [new CallerFlowCondition(CallerMode.Only, [])], Steps = [new NotifyStep([FlowRecipient.ToPerson(Nigel)])] }, "List the callers the condition is about." },
        { new FlowDefinition { Trigger = AlertEventType.MissedCall, Steps = [new BranchStep(OfficeHours, [new BranchStep(OfficeHours, [new BranchStep(OfficeHours, [new NotifyStep([FlowRecipient.ToPerson(Nigel)])], [])], [])], [])] }, "Branches go at most 2 deep." },
    };

    [Theory]
    [MemberData(nameof(Broken))]
    public void A_flow_that_cannot_work_says_why(FlowDefinition flow, string problem) => Assert.Equal(problem, Flows.Problem(flow));

    [Fact]
    public void The_agreed_example_is_a_working_flow() => Assert.Null(Flows.Problem(MissedOnSales));

    // Partway through a flow, what a voicemail says can send it one way or the other, as the flow's own condition can.
    [Fact]
    public void A_branch_on_what_a_voicemail_says_takes_the_way_its_words_point()
    {
        var flow = new FlowDefinition
        {
            Trigger = AlertEventType.VoicemailTranscribed,
            Steps = [new BranchStep(new TranscriptWordsCondition(["urgent", "leak"]),
                Then: [new NotifyStep([FlowRecipient.ToPerson(Manager)], Urgent: true)],
                Otherwise: [new NotifyStep([FlowRecipient.ToChannel(SalesTeam)])])],
        };
        FlowFacts Said(string words) => Missed("2026-09-29T10:00:00Z", Sales) with { Type = AlertEventType.VoicemailTranscribed, TranscriptText = words };

        Assert.Null(Flows.Problem(flow));
        Assert.Equal([Manager], Flows.Plan(flow, Said("Hi, there's a LEAK in the kitchen, please call")).Single().Notify.SelectMany(n => n.To).Select(r => r.Person));
        Assert.Equal([SalesTeam], Flows.Plan(flow, Said("Just ringing to say thanks")).Single().Notify.SelectMany(n => n.To).Select(r => r.Channel));
        // On any other trigger there is no transcript to read: refused, as the flow's own condition is.
        Assert.NotNull(Flows.Problem(flow with { Trigger = AlertEventType.MissedCall }));
    }

    // The figures conditions count: a caller not getting through, a number going unanswered, callers left waiting, a
    // number's answer rate. Each judged on the calls before the alert, this one included.
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-03T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    private static FlowFacts Figures(IReadOnlyList<PastCall>? caller = null, IReadOnlyList<PastCall>? number = null, int? waiting = null) =>
        new(AlertEventType.MissedCall, Now, new HashSet<LineRef> { Sales }, IsCall: true, "+447700900123", London)
        { CallerHistory = caller ?? [], NumberCalls = number, WaitingCallers = waiting };

    private static PastCall Ago(double hours, CallOutcome outcome) => new(Now - TimeSpan.FromHours(hours), outcome);

    [Fact]
    public void A_caller_who_keeps_going_unanswered_over_days_is_counted_and_answered_calls_are_not()
    {
        var history = new[] { Ago(0, CallOutcome.Missed), Ago(30, CallOutcome.Voicemail), Ago(50, CallOutcome.Answered), Ago(70, CallOutcome.Missed), Ago(150, CallOutcome.Missed) };

        Assert.True(new CallerMissedCondition(3, 3 * 24 * 60).Matches(Figures(caller: history)));   // now, 30 h and 70 h; not the answered one
        Assert.False(new CallerMissedCondition(4, 3 * 24 * 60).Matches(Figures(caller: history)));  // the fourth was 150 h ago, outside 3 days
        Assert.True(new CallerMissedCondition(4, 7 * 24 * 60).Matches(Figures(caller: history)));
        Assert.False(new CallerMissedCondition(1, 60).Matches(Figures(caller: history) with { CallerE164 = null })); // withheld
    }

    [Fact]
    public void A_number_going_unanswered_callers_waiting_and_its_answer_rate_are_judged_on_that_numbers_calls()
    {
        var day = new[] { Ago(0, CallOutcome.Missed), Ago(0.2, CallOutcome.Missed), Ago(0.4, CallOutcome.Answered), Ago(0.5, CallOutcome.Voicemail), Ago(5, CallOutcome.Answered), Ago(6, CallOutcome.HungUpAtSwitchboard) };

        Assert.True(new NumberMissedCondition(3, 60).Matches(Figures(number: day)));
        Assert.False(new NumberMissedCondition(4, 60).Matches(Figures(number: day)));
        Assert.False(new NumberMissedCondition(1, 60).Matches(Figures(number: null))); // no number, no figures

        Assert.True(new WaitingCallersCondition(5).Matches(Figures(waiting: 6)));
        Assert.False(new WaitingCallersCondition(5).Matches(Figures(waiting: 4)));

        // Over 1 hour: 1 answered of 4 counted = 25%. Over 8 hours: 2 of 5 = 40%; the hang-up in the menu is not counted.
        Assert.True(new AnswerRateCondition(30, 1).Matches(Figures(number: day)));
        Assert.False(new AnswerRateCondition(25, 1).Matches(Figures(number: day)));
        Assert.True(new AnswerRateCondition(50, 8).Matches(Figures(number: day)));
        // Too few calls to judge a rate by.
        Assert.False(new AnswerRateCondition(90, 1).Matches(Figures(number: [Ago(0, CallOutcome.Missed), Ago(0.1, CallOutcome.Missed)])));
    }

    [Fact]
    public void The_figures_conditions_are_saved_and_read_back_and_refused_out_of_range()
    {
        var flow = new FlowDefinition
        {
            Trigger = AlertEventType.MissedCall,
            Conditions = [new CallerMissedCondition(3, 1440), new NumberMissedCondition(5, 30), new WaitingCallersCondition(10), new AnswerRateCondition(70, 2)],
            Steps = [new NotifyStep([FlowRecipient.ToPerson(Manager)])],
        };

        Assert.Null(Flows.Problem(flow));
        Assert.Equal(flow.Conditions, Flows.Read(Flows.Write(flow))!.Conditions);
        Assert.NotNull(Flows.Problem(flow with { Conditions = [new CallerMissedCondition(3, 8 * 24 * 60)] }));
        Assert.NotNull(Flows.Problem(flow with { Conditions = [new AnswerRateCondition(0, 2)] }));
        Assert.NotNull(Flows.Problem(flow with { Trigger = AlertEventType.Drift, Conditions = [new WaitingCallersCondition(1)] }));
    }
}
