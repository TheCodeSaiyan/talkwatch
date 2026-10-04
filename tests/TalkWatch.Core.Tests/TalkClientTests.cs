using TalkWatch.Core.Talk;
using TalkWatch.Replay;

namespace TalkWatch.Core.Tests;

public class TalkClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Pages_through_the_call_log_newest_first()
    {
        var console = new FixtureConsole(FixtureConsole.DefaultDirectory);
        var client = new TalkClient(console.CreateClient());
        await client.SignInAsync(FixtureConsole.Username, FixtureConsole.Password, Ct);

        var first = await client.GetCallLogPageAsync(0, 10, Ct);
        var second = await client.GetCallLogPageAsync(1, 10, Ct);

        Assert.Equal(console.CallCount, first.TotalCount);
        Assert.Equal(10, first.Records.Count);
        Assert.True(first.Records[^1].Time >= second.Records[0].Time);
        Assert.Empty(first.Records.Select(r => r.Uuid).Intersect(second.Records.Select(r => r.Uuid)));
    }

    [Fact]
    public async Task A_refused_sign_in_is_reported_without_the_password()
    {
        var client = new TalkClient(new FixtureConsole(FixtureConsole.DefaultDirectory).CreateClient());

        var error = await Assert.ThrowsAsync<TalkApiException>(() => client.SignInAsync(FixtureConsole.Username, "wrong", Ct));

        Assert.Contains("403", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("wrong", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_shape_talkwatch_does_not_recognise_is_reported_as_drift()
    {
        var console = new FixtureConsole(FixtureConsole.DefaultDirectory) { Drifted = true };
        var client = new TalkClient(console.CreateClient());

        await Assert.ThrowsAsync<TalkSchemaException>(() => client.GetCallLogPageAsync(0, 25, Ct));
    }
}
