using TalkWatch.Core.Calls;
using TalkWatch.Core.Talk;
using TalkWatch.Replay;

namespace TalkWatch.Core.Tests;

public class DirectoryTests
{
    private static readonly NumberNormaliser Uk = new("GB");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static CallLogRecord Call(string direction, string to) => new()
    {
        Uuid = Guid.NewGuid().ToString(), Time = DateTimeOffset.UtcNow, Direction = direction, Status = "accepted", From = "0002", To = to,
    };

    private static LineDirectory WithGroup(IReadOnlyList<string>? dids = null, IReadOnlyList<string>? exts = null) =>
        new([], [new TalkGroup { Id = "7", Name = "Sales", MemberList = ["u1", "u2"], DidList = dids, ExtList = exts }], [], Uk);

    [Fact]
    public async Task The_captured_console_names_its_users_groups_numbers_and_attendants()
    {
        var talk = new TalkClient(new FixtureConsole(FixtureConsole.DefaultDirectory).CreateClient());

        var directory = await talk.GetDirectoryAsync(Uk, Ct);
        var lines = directory.Lines(Uk).ToList();

        Assert.NotEmpty(directory.Users);
        Assert.Single(directory.Groups);
        Assert.Contains(lines, l => l.Kind == LineKind.RingGroup && l.Key == directory.Groups[0].Id);
        Assert.Contains(lines, l => l.Kind == LineKind.Did && l.Key == "+441144960042");
        Assert.Contains(lines, l => l.Kind == LineKind.Attendant && l.Key == "45");
        Assert.Equal(directory.Users.Count(u => !u.HideFromUserList), lines.Count(l => l.Kind == LineKind.User));
    }

    [Fact]
    public void A_call_dialled_to_a_groups_extension_went_through_the_group() =>
        Assert.Contains(new LineRef(LineKind.RingGroup, "7"), CallRouting.TouchedLines(Call("internal", "0004"), Uk, WithGroup(exts: ["0004"])));

    [Fact]
    public void A_call_in_on_a_did_routed_to_a_group_went_through_the_group()
    {
        var lines = CallRouting.TouchedLines(Call("in", "+441144960042"), Uk, WithGroup(dids: ["01144960042"]));

        Assert.Contains(new LineRef(LineKind.RingGroup, "7"), lines);
        Assert.Contains(new LineRef(LineKind.Did, "+441144960042"), lines);
    }

    [Fact]
    public void Calls_that_match_no_group_configuration_get_no_group_line()
    {
        // Ringing a group's members is not, by itself, evidence the call went through the group: not guessed.
        var lines = CallRouting.TouchedLines(Call("in", "+441144960099"), Uk, WithGroup(dids: ["01144960042"], exts: ["0004"]));

        Assert.DoesNotContain(lines, l => l.Kind == LineKind.RingGroup);
    }

    [Fact]
    public void An_outbound_call_to_a_number_that_is_also_a_group_did_is_not_a_group_call() =>
        Assert.DoesNotContain(CallRouting.TouchedLines(Call("out", "+441144960042"), Uk, WithGroup(dids: ["01144960042"])), l => l.Kind == LineKind.RingGroup);
}
