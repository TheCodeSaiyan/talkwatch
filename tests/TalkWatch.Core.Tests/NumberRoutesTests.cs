using TalkWatch.Core.Calls;
using TalkWatch.Core.Talk;
using TalkWatch.Replay;

namespace TalkWatch.Core.Tests;

/// <summary>What a number covers as an access boundary: itself, the switchboards answering it, the ring groups it rings.</summary>
public class NumberRoutesTests
{
    private static readonly NumberNormaliser Uk = new("GB");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task On_the_captured_console_each_number_covers_its_switchboard_and_the_ring_group_its_menus_reach()
    {
        var talk = new TalkClient(new FixtureConsole(FixtureConsole.DefaultDirectory).CreateClient());
        var directory = await talk.GetDirectoryAsync(Uk, Ct);
        directory.Switchboard = await talk.GetSwitchboardAsync(Ct);

        var routes = directory.Routes(Uk);

        Assert.Equal(new HashSet<LineRef> { new(LineKind.Did, "+441144960042"), new(LineKind.Attendant, "45") }, routes["+441144960042"].ToHashSet());
        Assert.Equal(new HashSet<LineRef> { new(LineKind.Did, "+441174960404"), new(LineKind.Attendant, "15"), new(LineKind.RingGroup, "1") }, routes["+441174960404"].ToHashSet());
    }

    [Fact]
    public void A_number_covers_the_group_it_rings_and_where_that_group_sends_unanswered_calls_but_never_people()
    {
        var directory = new LineDirectory(
            [new TalkUser { Uuid = "u1", FullName = "Ann", Did = "+441144960009" }],
            [
                new TalkGroup { Id = "1", Name = "Sales", MemberList = ["u1"], DidList = ["01144960001"], TransferToGroupId = "2" },
                new TalkGroup { Id = "2", Name = "Overflow", MemberList = ["u1"] },
                new TalkGroup { Id = "3", Name = "Support", MemberList = ["u1"], DidList = ["01144960002"] },
            ],
            [new TalkNumber { Did = "+441144960001" }, new TalkNumber { Did = "+441144960002" }], Uk);

        var routes = directory.Routes(Uk);

        Assert.Equal(new HashSet<LineRef> { new(LineKind.Did, "+441144960001"), new(LineKind.RingGroup, "1"), new(LineKind.RingGroup, "2") }, routes["+441144960001"].ToHashSet());
        // Ann answers both numbers' groups: following her would carry Support's calls into Sales.
        Assert.DoesNotContain(routes["+441144960001"], l => l.Kind == LineKind.User);
        Assert.DoesNotContain(new LineRef(LineKind.RingGroup, "3"), routes["+441144960001"]);
    }
}
