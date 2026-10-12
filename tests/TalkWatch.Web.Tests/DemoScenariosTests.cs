using TalkWatch.Core.Talk;
using TalkWatch.Core.Alerts;
using Microsoft.AspNetCore.Mvc.Testing;
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
        var scenarios = Scenarios(app, console, new NoWaiting());

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
        var scenarios = Scenarios(app, console, new NoWaiting());

        await scenarios.Start(scenario)!;

        Assert.Equal(outcome, Assert.Single(await CallsSinceAsync(app.Services, since)).Outcome);
    }

    [Fact]
    public void An_unknown_scenario_never_plays()
    {
        var console = new FixtureConsole(FixtureConsole.DefaultDirectory);
        var scenarios = new DemoScenarios(console, null!, new LiveStatus(), null!, null!, TimeProvider.System, NullLogger<DemoScenarios>.Instance);

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

    private static DemoScenarios Scenarios(WebApplicationFactory<Program> app, FixtureConsole console, TimeProvider clock) =>
        new(console, app.Services.GetRequiredService<CallLogPoller>(), app.Services.GetRequiredService<LiveStatus>(),
            app.Services.GetRequiredService<LineDirectorySync>(), app.Services.GetRequiredService<LiveListener>(), clock, NullLogger<DemoScenarios>.Instance);

    private static async Task<List<CallRow>> CallRowsSinceAsync(IServiceProvider services, DateTimeOffset since)
    {
        using var scope = services.CreateScope();
        scope.ServiceProvider.GetRequiredService<AccessScopeHolder>().UseSystemScope();
        return await scope.ServiceProvider.GetRequiredService<TalkWatchDbContext>().Calls.AsNoTracking().Where(c => c.Time >= since).OrderBy(c => c.Time).ToListAsync(Ct);
    }

    // Every wait of a minute or more never ends, and the rest end at once: a scenario that holds a state for a minute stays
    // in it, so the state can be looked at.
    private sealed class HeldAtTheMinute : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            System.CreateTimer(callback, state, dueTime >= TimeSpan.FromMinutes(1) ? Timeout.InfiniteTimeSpan : TimeSpan.Zero, period);
    }

    [Fact]
    public async Task A_parked_call_is_answered_parked_and_picked_up()
    {
        var console = new FixtureConsole(FixtureConsole.DefaultDirectory);
        await using var app = talkwatch.Create(console);
        await app.Services.GetRequiredService<CallLogPoller>().RunOnceAsync(Ct);
        var since = DateTimeOffset.UtcNow.AddSeconds(-1);

        await Scenarios(app, console, new NoWaiting()).Start("parked")!;

        var call = Assert.Single(await CallsSinceAsync(app.Services, since));
        Assert.Equal(CallOutcome.Answered, call.Outcome);
        Assert.Equal(["call_parked", "call_unparked"], call.Events.Where(e => e.Contains("park", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_missed_caller_rung_back_is_returned()
    {
        var console = new FixtureConsole(FixtureConsole.DefaultDirectory);
        await using var app = talkwatch.Create(console);
        await app.Services.GetRequiredService<CallLogPoller>().RunOnceAsync(Ct);
        var since = DateTimeOffset.UtcNow.AddSeconds(-1);

        await Scenarios(app, console, new NoWaiting()).Start("call-back")!;

        var calls = await CallRowsSinceAsync(app.Services, since);
        Assert.Equal(2, calls.Count);
        var missed = Assert.Single(calls, c => c.Direction == "in");
        var back = Assert.Single(calls, c => c.Direction == "out");
        Assert.Equal(CallOutcome.Missed, missed.Outcome);
        Assert.Equal(missed.FromE164, back.ToE164);
        Assert.NotNull(missed.ReturnedAt);
    }

    [Fact]
    public async Task A_caller_who_tries_again_is_missed_twice_from_one_number()
    {
        var console = new FixtureConsole(FixtureConsole.DefaultDirectory);
        await using var app = talkwatch.Create(console);
        await app.Services.GetRequiredService<CallLogPoller>().RunOnceAsync(Ct);
        var since = DateTimeOffset.UtcNow.AddSeconds(-1);

        await Scenarios(app, console, new NoWaiting()).Start("repeat-caller")!;

        var calls = await CallRowsSinceAsync(app.Services, since);
        Assert.Equal(2, calls.Count);
        Assert.All(calls, c => Assert.Equal(CallOutcome.Missed, c.Outcome));
        Assert.Single(calls.Select(c => c.FromE164).Distinct());
    }

    [Fact]
    public async Task A_poor_line_is_answered_and_scored_low()
    {
        var console = new FixtureConsole(FixtureConsole.DefaultDirectory);
        await using var app = talkwatch.Create(console);
        await app.Services.GetRequiredService<CallLogPoller>().RunOnceAsync(Ct);
        var since = DateTimeOffset.UtcNow.AddSeconds(-1);

        await Scenarios(app, console, new NoWaiting()).Start("poor-line")!;

        var call = Assert.Single(await CallRowsSinceAsync(app.Services, since));
        Assert.Equal((CallOutcome.Answered, 48), (call.Outcome, call.QualityScore));
    }

    // One scenario for the limit of four, however many callers it brings.
    [Fact]
    public async Task A_rush_brings_six_callers_ending_every_way_and_counts_as_one_scenario()
    {
        var console = new FixtureConsole(FixtureConsole.DefaultDirectory);
        await using var app = talkwatch.Create(console);
        await app.Services.GetRequiredService<CallLogPoller>().RunOnceAsync(Ct);
        var since = DateTimeOffset.UtcNow.AddSeconds(-1);
        var scenarios = Scenarios(app, console, new NoWaiting());

        var rush = scenarios.Start("rush")!;
        var playing = scenarios.Playing;
        await rush;

        var calls = await CallRowsSinceAsync(app.Services, since);
        Assert.Equal(1, playing);
        Assert.Equal(6, calls.Count);
        Assert.Equal(6, calls.Select(c => c.FromE164).Distinct().Count());
        Assert.Equal([CallOutcome.Answered, CallOutcome.Missed, CallOutcome.Voicemail], calls.Select(c => c.Outcome).Distinct().Order());
        Assert.Contains(calls, c => c.ToRaw == "+441174960404");
    }

    [Fact]
    public async Task A_handset_that_goes_offline_raises_its_alert_and_comes_back()
    {
        var console = new FixtureConsole(FixtureConsole.DefaultDirectory);
        await using var app = talkwatch.Create(console);
        var listener = app.Services.GetRequiredService<LiveListener>();
        await listener.HandleAsync(LiveMessage.Parse(
            """{"event":"DEVICES_UPDATED","data":[{"mac":"02aabbccddee","model":"UTP-G3","display_name":"Front desk","status":"online","sip_reg":true}]}""")!, Ct);

        await Scenarios(app, console, new NoWaiting()).Start("handset-offline")!;

        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<AccessScopeHolder>().UseSystemScope();
        var raised = await scope.ServiceProvider.GetRequiredService<TalkWatchDbContext>().AlertEvents.CountAsync(e => e.Type == AlertEventType.HandsetOffline, Ct);
        Assert.Equal(1, raised);
        Assert.Equal("online", app.Services.GetRequiredService<LiveStatus>().Snapshot().Devices.Single(d => d.Mac == "02aabbccddee").Status);
    }

    [Theory]
    [InlineData("cloud-down", true, false)]
    [InlineData("degraded", false, true)]
    public async Task Talk_losing_its_cloud_or_running_degraded_shows_while_it_plays_and_clears_after(string scenario, bool cloudDown, bool degraded)
    {
        var console = new FixtureConsole(FixtureConsole.DefaultDirectory);
        await using var app = talkwatch.Create(console);
        var platform = app.Services.GetRequiredService<PlatformStatus>();
        await app.Services.GetRequiredService<CallLogPoller>().RunOnceAsync(Ct);

        // Held in its minute, the state can be read; then played through, it clears.
        _ = Scenarios(app, console, new HeldAtTheMinute()).Start(scenario);
        for (var i = 0; i < 100 && !(platform.CloudDown || platform.Degraded); i++)
        {
            await Task.Delay(50, Ct);
        }

        var during = (platform.CloudDown, platform.Degraded);
        await Scenarios(app, console, new NoWaiting()).Start(scenario)!;

        Assert.Equal((cloudDown, degraded), during);
        Assert.Equal((false, false), (platform.CloudDown, platform.Degraded));
    }
}
