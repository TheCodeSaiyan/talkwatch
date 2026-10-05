using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TalkWatch.Core.Calls;
using TalkWatch.Data;
using TalkWatch.Replay;
using TalkWatch.Web.Services;

namespace TalkWatch.Web.Tests;

/// <summary>The demo's scenarios, played on the replayed console, and the readable names it gives the capture.</summary>
public sealed class DemoScenariosTests(TalkWatchApp talkwatch) : IClassFixture<TalkWatchApp>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // The real clock, but every wait over at once: a scenario that takes a minute plays in a moment.
    private sealed class NoWaiting : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            System.CreateTimer(callback, state, TimeSpan.Zero, period);
    }

    private static async Task<List<(CallOutcome Outcome, string To, List<string> Events)>> CallsSinceAsync(IServiceProvider services, DateTimeOffset since)
    {
        using var scope = services.CreateScope();
        scope.ServiceProvider.GetRequiredService<AccessScopeHolder>().UseSystemScope();
        var db = scope.ServiceProvider.GetRequiredService<TalkWatchDbContext>();
        var calls = await db.Calls.Where(c => c.Time >= since).Select(c => new { c.Id, c.Outcome, c.ToRaw }).ToListAsync(Ct);
        var events = await db.CallEvents.Where(e => calls.Select(c => c.Id).Contains(e.CallId)).OrderBy(e => e.Sequence).Select(e => new { e.CallId, e.Event }).ToListAsync(Ct);
        return [.. calls.Select(c => (c.Outcome, c.ToRaw ?? "", events.Where(e => e.CallId == c.Id).Select(e => e.Event).ToList()))];
    }

    [Theory]
    [InlineData("menu", CallOutcome.Answered, 2)]
    [InlineData("wrong-key", CallOutcome.Answered, 1)]
    [InlineData("menu-hangup", CallOutcome.HungUpAtSwitchboard, 1)]
    public async Task A_caller_goes_through_the_main_switchboard_a_step_at_a_time(string scenario, CallOutcome outcome, int choices)
    {
        var console = new FixtureConsole(FixtureConsole.DefaultDirectory);
        await using var app = talkwatch.Create(console);
        var poller = app.Services.GetRequiredService<CallLogPoller>();
        await poller.RunOnceAsync(Ct);
        var since = DateTimeOffset.UtcNow.AddSeconds(-1);
        var scenarios = new DemoScenarios(console, poller, app.Services.GetRequiredService<LiveStatus>(), app.Services.GetRequiredService<LineDirectorySync>(),
            new NoWaiting(), NullLogger<DemoScenarios>.Instance);

        await scenarios.Start(scenario)!;

        var call = Assert.Single(await CallsSinceAsync(app.Services, since));
        Assert.Equal(outcome, call.Outcome);
        Assert.Equal("+441174960404", call.To);
        Assert.Equal(choices, call.Events.Count(e => e == "entered_sa_menu"));
        Assert.Equal(0, scenarios.Playing);
    }

    [Theory]
    [InlineData("answered", CallOutcome.Answered)]
    [InlineData("missed", CallOutcome.Missed)]
    [InlineData("voicemail", CallOutcome.Voicemail)]
    [InlineData("outside", CallOutcome.Answered)]
    [InlineData("hangup-alert", CallOutcome.HungUpAtSwitchboard)]
    public async Task Each_call_ends_the_way_its_scenario_says(string scenario, CallOutcome outcome)
    {
        var console = new FixtureConsole(FixtureConsole.DefaultDirectory);
        await using var app = talkwatch.Create(console);
        var poller = app.Services.GetRequiredService<CallLogPoller>();
        await poller.RunOnceAsync(Ct);
        var since = DateTimeOffset.UtcNow.AddSeconds(-1);
        var scenarios = new DemoScenarios(console, poller, app.Services.GetRequiredService<LiveStatus>(), app.Services.GetRequiredService<LineDirectorySync>(),
            new NoWaiting(), NullLogger<DemoScenarios>.Instance);

        await scenarios.Start(scenario)!;

        Assert.Equal(outcome, Assert.Single(await CallsSinceAsync(app.Services, since)).Outcome);
    }

    [Fact]
    public void An_unknown_scenario_never_plays()
    {
        var console = new FixtureConsole(FixtureConsole.DefaultDirectory);
        var scenarios = new DemoScenarios(console, null!, new LiveStatus(), null!, TimeProvider.System, NullLogger<DemoScenarios>.Instance);

        Assert.Null(scenarios.Start("no-such-thing"));
        Assert.Equal(0, scenarios.Playing);
    }

    [Fact]
    public void The_capture_reads_with_readable_names_and_its_ids_stay_apart()
    {
        var served = DemoNames.Apply("""
            {"title":"Title 10","group_name":"Jordan Baker 27","first_name":"Morgan9","last_name":"Nico10","display_name":"Jordan Hughes 29",
             "sa_item_title":"Title 11","accepted_contact_ids":["Rowan30","Alex31"],"user_status":"Jamie8","text":"Charlie Vaughan 11 said hello"}
            """);

        Assert.Contains("\"title\":\"Main switchboard\"", served, StringComparison.Ordinal);
        Assert.Contains("\"group_name\":\"Support team\"", served, StringComparison.Ordinal);
        Assert.Contains("\"first_name\":\"Morgan\",\"last_name\":\"Nico\"", served, StringComparison.Ordinal);
        Assert.Contains("\"display_name\":\"Jordan Hughes\"", served, StringComparison.Ordinal);
        Assert.Contains("\"sa_item_title\":\"Sales and billing\"", served, StringComparison.Ordinal);
        Assert.Contains("[\"Rowan30\",\"Alex31\"]", served, StringComparison.Ordinal);
        Assert.Contains("\"user_status\":\"Jamie8\"", served, StringComparison.Ordinal);
        Assert.Contains("Charlie Vaughan said hello", served, StringComparison.Ordinal);
    }
}
