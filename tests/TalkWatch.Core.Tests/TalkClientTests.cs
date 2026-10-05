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

    // Captured while a switchboard call was still going: the menu had played and the caller had chosen an option, and
    // nobody had answered yet. Talk announces each such step as CALL_EVENTS_UPDATED, naming only the call.
    [Fact]
    public async Task A_call_still_going_has_its_events_so_far_read_by_its_id()
    {
        var client = new TalkClient(new FixtureConsole(FixtureConsole.DefaultDirectory).CreateClient());
        await client.SignInAsync(FixtureConsole.Username, FixtureConsole.Password, Ct);

        var events = await client.GetCallEventsAsync("ce09f9d8-011c-4e5d-97ce-c47f01ea8e7c", Ct);

        Assert.Equal("call_started", events[0].Event);
        Assert.Contains(events, e => e.Event == "entered_sa_menu" && e.Text("sa_item_key") == "2");
        Assert.All(events, e => Assert.NotNull(e.EventUuid));
    }

    [Fact]
    public void Talks_notice_that_a_calls_events_changed_names_the_call()
    {
        var notice = LiveMessage.Parse("""{"event":"CALL_EVENTS_UPDATED","data":{"call_id":"d2bc39ae-0000-4000-8000-000000000001","updated_at":"2026-10-05T19:40:07Z"}}""")!;

        Assert.Equal("d2bc39ae-0000-4000-8000-000000000001", notice.CallId());
        Assert.Null(LiveMessage.Parse("""{"event":"DEVICES_UPDATED","data":[]}""")!.CallId());
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
