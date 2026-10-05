using TalkWatch.Core.Calls;
using TalkWatch.Core.Talk;
using TalkWatch.Data;
using TalkWatch.Web.Components.Calls;

namespace TalkWatch.Web.Tests;

/// <summary>A switchboard as Operator draws it: its stages, today's traffic, and where each caller in it is now.</summary>
public sealed class SwitchboardFlowTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UtcNow.AddMinutes(-1);
    private static readonly ChronicleNames Names = new(_ => null, _ => null, _ => null, _ => null);

    // The shape of the console it was tried on: a switchboard on one number, a menu option that opens a second menu,
    // and options that put calls through to ring groups.
    private static readonly SwitchboardNodeRow[] Tree =
    [
        new() { NodeId = "swb_15", InternalId = 15, Type = "root", Title = "Main switchboard", Numbers = "+441174960404" },
        new() { NodeId = "ivr_16", InternalId = 16, Type = "ivr", Key = 1, Title = "Sales and billing", ParentId = "swb_15" },
        new() { NodeId = "ivr_17", InternalId = 17, Type = "ivr", Key = 1, Title = "Sales", ParentId = "ivr_16" },
        new() { NodeId = "grp_40", Type = "group", Title = "Sales", ParentId = "ivr_17" },
        new() { NodeId = "ivr_18", InternalId = 18, Type = "ivr", Key = 2, Title = "Support", ParentId = "swb_15" },
        new() { NodeId = "grp_39", Type = "group", Title = "Support", ParentId = "ivr_18" },
        new() { NodeId = "swb_20", InternalId = 20, Type = "root", Title = "Out of hours", Numbers = "+441174960999" },
    ];

    private static string Started() => """{"to":"+441174960404","from":"+447700900774","to_smart_attendant_id":15}""";
    private static string Chose(int item, int key, string title) => $$"""{"sa_id":15,"sa_item_id":{{item}},"sa_item_key":{{key}},"sa_item_type":"ivr","sa_item_title":"{{title}}"}""";

    private static (LiveCall, MenuJourney) Caller(string uuid, params (string Event, string? Data)[] events)
    {
        var row = new CallRow { Id = Guid.NewGuid(), TalkUuid = uuid, Direction = "in", Status = "ringing", Time = T0, Outcome = CallOutcome.InProgress, FromRaw = "+447700900774" };
        var chronicle = Chronicle.Build(events.Select((e, i) => new ChronicleEvent(T0.AddSeconds(i * 5), e.Event, e.Data)), "in", Names);
        return (LiveCalls.Of(row, chronicle, Names), MenuJourney.Of(events));
    }

    private static IReadOnlyList<SwitchboardFlow> Build(string? chosen = null, (LiveCall, MenuJourney)[]? live = null, FlowHistory[]? today = null) =>
        SwitchboardFlows.Build(Tree, today ?? [], live ?? [], chosen, n => n);

    [Fact]
    public void The_switchboard_is_drawn_as_stages_one_column_further_in_at_each_step()
    {
        var flow = Build()[0];

        Assert.Equal("Main switchboard", flow.Title);
        Assert.Equal(
            [("in", 0, "Calling in"), ("n:swb_15", 1, "Main switchboard"), ("n:ivr_16", 2, "1 · Sales and billing"), ("n:ivr_17", 3, "1 · Sales"), ("n:grp_40", 4, "Sales"),
             ("n:ivr_18", 2, "2 · Support"), ("n:grp_39", 3, "Support"), ("hungup", 2, "Hung up in the menu")],
            flow.Stages.Select(s => (s.Id, s.Column, s.Label)));
        Assert.Equal("menu", flow.Stages.Single(s => s.Id == "n:ivr_16").Kind);
        Assert.Equal("n:ivr_16", flow.Stages.Single(s => s.Id == "n:ivr_17").From);
    }

    // As on the console it was built against: each option splits by opening hours before its ring group, and the ring
    // group has no title of its own. The split passes the option's calls on; Talk logs no step for it.
    [Fact]
    public void An_opening_hours_split_passes_its_options_calls_on_to_where_they_end_up()
    {
        SwitchboardNodeRow[] tree =
        [
            new() { NodeId = "swb_15", InternalId = 15, Type = "root", Title = "Main switchboard", Numbers = "+441174960404" },
            new() { NodeId = "swb_18", InternalId = 18, Type = "ivr", Key = 2, Title = "Support", ParentId = "swb_15" },
            new() { NodeId = "swb_41", InternalId = 41, Type = "time", Title = "Open", ParentId = "swb_18" },
            new() { NodeId = "grp_41_1", Type = "group", ParentId = "swb_41" },
        ];
        var flow = SwitchboardFlows.Build(tree, [new(new MenuJourney(15, [new MenuChoice(15, 18, 2, "Support")]), CallOutcome.Answered)], [], null, n => n)[0];

        Assert.Equal([("n:swb_41", "hours", 1), ("n:grp_41_1", "destination", 1)],
            flow.Stages.Where(s => s.Id is "n:swb_41" or "n:grp_41_1").Select(s => (s.Id, s.Kind, s.Today)));
        Assert.Equal("Ring group", flow.Stages.Single(s => s.Id == "n:grp_41_1").Label);
    }

    [Fact]
    public void With_a_number_chosen_only_the_switchboards_it_reaches_are_drawn()
    {
        Assert.Equal(["Main switchboard", "Out of hours"], Build().Select(f => f.Title));
        Assert.Equal(["Main switchboard"], Build("+441174960404").Select(f => f.Title));
        Assert.Empty(Build("+441174960111"));
    }

    [Fact]
    public void Each_caller_is_at_the_stage_their_last_step_put_them()
    {
        var flow = Build(live:
        [
            Caller("listening", ("call_started", Started())),
            Caller("chose", ("call_started", Started()), ("keypress", """{"key":"2"}"""), ("entered_sa_menu", Chose(18, 2, "Support"))),
            Caller("ringing", ("call_started", Started()), ("entered_sa_menu", Chose(18, 2, "Support")), ("seq_call_trying_endpoints", """{"contact_uuids":[]}""")),
            Caller("nested", ("call_started", Started()), ("entered_sa_menu", Chose(16, 1, "Sales and billing"))),
        ])[0];

        Assert.Equal("n:swb_15", flow.Callers.Single(c => c.Key == "listening").StageId);
        Assert.Equal(("n:ivr_18", "routing", "Caller pressed 2 for Support"), flow.Callers.Single(c => c.Key == "chose") is var c ? (c.StageId, c.Mark, c.Step) : default);
        Assert.Equal(("n:grp_39", "ringing"), flow.Callers.Single(c => c.Key == "ringing") is var r ? (r.StageId, r.Mark) : default);
        Assert.Equal("n:ivr_16", flow.Callers.Single(c => c.Key == "nested").StageId);
        Assert.Equal(3, flow.InTheMenu);
    }

    [Fact]
    public void A_caller_in_another_switchboard_is_not_drawn_in_this_one()
    {
        var elsewhere = Caller("elsewhere", ("call_started", """{"to":"+441174960999","to_smart_attendant_id":20}"""));

        Assert.Empty(Build("+441174960404", live: [elsewhere])[0].Callers);
    }

    [Fact]
    public void Todays_calls_set_each_stages_traffic_and_the_hang_ups()
    {
        MenuJourney Journey(params MenuChoice[] choices) => new(15, choices);
        var flow = Build(today:
        [
            new(Journey(new MenuChoice(15, 18, 2, "Support")), CallOutcome.Answered),
            new(Journey(new MenuChoice(15, 18, 2, "Support")), CallOutcome.Missed),
            new(Journey(new MenuChoice(15, 16, 1, "Sales and billing"), new MenuChoice(16, 17, 1, "Sales")), CallOutcome.Answered),
            new(Journey(), CallOutcome.HungUpAtSwitchboard),
            new(new MenuJourney(20, []), CallOutcome.Answered),
        ])[0];

        int Today(string id) => flow.Stages.Single(s => s.Id == id).Today;
        Assert.Equal((4, 4, 2, 1, 1, 1), (Today("in"), Today("n:swb_15"), Today("n:ivr_18"), Today("n:ivr_16"), Today("n:ivr_17"), Today("hungup")));
        Assert.Equal(2, Today("n:grp_39"));
    }
}
