using System.Net;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TalkWatch.Core.Alerts;
using TalkWatch.Core.Calls;
using TalkWatch.Data;
using TalkWatch.Replay;
using TalkWatch.Web.Services;

namespace TalkWatch.Web.Tests;

/// <summary>A demo that looks alive: example flows for the first admin, and new calls that raise alerts.</summary>
public sealed class DemoActivityTests(TalkWatchApp talkwatch) : IClassFixture<TalkWatchApp>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_new_demo_gets_example_flows_and_its_calls_raise_every_kind_of_call_alert()
    {
        var console = new FixtureConsole(FixtureConsole.DefaultDirectory);
        await using var app = talkwatch.Create(console);
        var poller = app.Services.GetRequiredService<CallLogPoller>();
        await poller.RunOnceAsync(Ct);
        var demo = new DemoActivity(console, app.Services.GetRequiredService<IServiceScopeFactory>(), app.Services.GetRequiredService<LineDirectorySync>(),
            Options.Create(new DemoOptions { Enabled = true }), Options.Create(new BootstrapOptions { AdminUsername = TalkWatchApp.AdminUsername }),
            TimeProvider.System, NullLogger<DemoActivity>.Instance);

        await demo.SeedAsync(Ct);
        await demo.SeedAsync(Ct);

        using (var scope = app.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<AccessScopeHolder>().UseSystemScope();
            var db = scope.ServiceProvider.GetRequiredService<TalkWatchDbContext>();
            var admin = await db.Users.SingleAsync(u => u.UserName == TalkWatchApp.AdminUsername, Ct);
            Assert.Equal(DemoActivity.ExampleFlows, await db.AlertFlows.CountAsync(Ct)); // Once, however often it runs.
            Assert.True(await db.AlertChannels.AnyAsync(c => c.Kind == ChannelKind.Browser && c.OwnerUserId == admin.Id, Ct));
            Assert.NotNull(admin.TalkUserUuid);
            Assert.All(await db.AlertFlows.Select(f => f.Definition).ToListAsync(Ct), d => Assert.Null(Flows.Problem(Flows.Read(d)!)));
        }

        // One of each kind of call, each stamped now: they are recent enough to alert on.
        for (var i = 0; i < 6; i++)
        {
            demo.AddCall();
            await poller.RunOnceAsync(Ct);
        }

        using (var scope = app.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<AccessScopeHolder>().UseSystemScope();
            var db = scope.ServiceProvider.GetRequiredService<TalkWatchDbContext>();
            var types = await db.AlertEvents.Select(e => e.Type).Distinct().ToListAsync(Ct);
            Assert.Contains(AlertEventType.MissedCall, types);
            Assert.Contains(AlertEventType.Voicemail, types);
            Assert.Contains(AlertEventType.PoorQualityCall, types);
            Assert.Contains(AlertEventType.HungUpAtSwitchboard, types);
            var recent = DateTimeOffset.UtcNow.AddMinutes(-5);
            Assert.Contains(CallOutcome.Answered, await db.Calls.Where(c => c.Time > recent).Select(c => c.Outcome).ToListAsync(Ct));
            // The flows sent something to the admin's browser: the bell has something to say.
            Assert.True(await db.AlertDeliveries.AnyAsync(d => db.AlertChannels.Any(c => c.Id == d.ChannelId && c.Kind == ChannelKind.Browser), Ct));
        }
    }

    private static DemoActivity Demo(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> app, FixtureConsole console) =>
        new(console, app.Services.GetRequiredService<IServiceScopeFactory>(), app.Services.GetRequiredService<LineDirectorySync>(),
            Options.Create(new DemoOptions { Enabled = true }), Options.Create(new BootstrapOptions { AdminUsername = TalkWatchApp.AdminUsername }),
            TimeProvider.System, NullLogger<DemoActivity>.Instance);

    private static async Task<T> DbAsync<T>(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> app, Func<TalkWatchDbContext, UserManager<AppUser>, Task<T>> work)
    {
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<AccessScopeHolder>().UseSystemScope();
        return await work(scope.ServiceProvider.GetRequiredService<TalkWatchDbContext>(), scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>());
    }

    [Fact]
    public async Task The_guest_is_an_admin_and_keeps_its_published_password()
    {
        var console = new FixtureConsole(FixtureConsole.DefaultDirectory);
        await using var app = talkwatch.Create(console);
        var demo = Demo(app, console);

        await demo.GuestAsync(Ct);
        // Someone changes the guest's password; the next start puts the published one back.
        await DbAsync(app, async (_, users) => { var g = (await users.FindByNameAsync("guest"))!; await users.RemovePasswordAsync(g); return await users.AddPasswordAsync(g, "someone-else-s-password"); });
        await demo.GuestAsync(Ct);

        var (roles, lockout) = await DbAsync(app, async (_, users) => { var g = (await users.FindByNameAsync("guest"))!; return (await users.GetRolesAsync(g), g.LockoutEnabled); });
        Assert.Equal([DemoActivity.GuestRole], roles);
        Assert.False(lockout);
        using var browser = TalkWatchApp.Browser(app);
        await TalkWatchApp.SignInAsync(browser, "guest", new DemoOptions().GuestPassword);
        // Signed in: the call log opens, rather than sending them back to sign in.
        Assert.Contains("<title>Calls", await browser.GetStringAsync(new Uri("/calls", UriKind.Relative), Ct), StringComparison.Ordinal);
        // An admin, to try everything: the pages open, and what may not change is refused when it is changed (DemoGuardTests).
        var people = await browser.GetAsync(new Uri("/admin/users", UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.OK, people.StatusCode);
    }

    [Fact]
    public async Task Every_third_demo_call_is_put_through_to_the_outside_phone_its_people_carry_and_tells_them()
    {
        var console = new FixtureConsole(FixtureConsole.DefaultDirectory);
        await using var app = talkwatch.Create(console);
        var poller = app.Services.GetRequiredService<CallLogPoller>();
        await poller.RunOnceAsync(Ct);
        var demo = Demo(app, console);
        await demo.SeedAsync(Ct);

        var start = DateTimeOffset.UtcNow.AddSeconds(-5);
        for (var i = 0; i < 3; i++)
        {
            demo.StartCall(start);
        }

        await poller.RunOnceAsync(Ct);
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<AccessScopeHolder>().UseSystemScope();
        var db = scope.ServiceProvider.GetRequiredService<TalkWatchDbContext>();
        var admin = await db.Users.SingleAsync(u => u.UserName == TalkWatchApp.AdminUsername, Ct);
        Assert.True(await db.ContactLinks.AnyAsync(l => l.UserId == admin.Id, Ct));
        var forwarded = Assert.Single(await db.AlertEvents.Where(e => e.Key.EndsWith(":" + AlertService.ForwardedKey)).ToListAsync(Ct));
        Assert.True(await db.AlertDeliveries.AnyAsync(d => d.EventId == forwarded.Id && d.Outside
            && db.AlertChannels.Any(c => c.Id == d.ChannelId && c.OwnerUserId == admin.Id && c.Kind == ChannelKind.Browser), Ct));
    }

    [Fact]
    public async Task A_demo_call_in_progress_rings_then_connects_then_leaves_the_board()
    {
        var console = new FixtureConsole(FixtureConsole.DefaultDirectory);
        await using var app = talkwatch.Create(console);
        var poller = app.Services.GetRequiredService<CallLogPoller>();
        await poller.RunOnceAsync(Ct);
        var demo = Demo(app, console);
        using var browser = TalkWatchApp.Browser(app);
        await TalkWatchApp.SignInAsync(browser, TalkWatchApp.AdminUsername, TalkWatchApp.AdminPassword);
        async Task<string> Board() { await poller.RunOnceAsync(Ct); return await browser.GetStringAsync(new Uri("/live", UriKind.Relative), Ct); }

        var start = DateTimeOffset.UtcNow.AddSeconds(-5);
        demo.StartCall(start);
        Assert.Equal(1, demo.InProgress());
        var ringing = await Board();
        var at = ringing.IndexOf("data-live-call", StringComparison.Ordinal);
        Assert.True(ringing.Contains("<div class=\"lc\" data-s=\"ringing\"", StringComparison.Ordinal), at < 0 ? "no live call rows" : ringing[Math.Max(0, at - 120)..Math.Min(ringing.Length, at + 120)]);

        demo.Tick(start + DemoActivity.RingsFor + TimeSpan.FromSeconds(1));
        Assert.Contains("<div class=\"lc\" data-s=\"connected\"", await Board(), StringComparison.Ordinal);

        demo.Tick(start + DemoActivity.LastsFor + TimeSpan.FromSeconds(1));
        Assert.Equal(0, demo.InProgress());
        var after = await Board();
        Assert.DoesNotContain("<div class=\"lc\" data-s=\"connected\"", after, StringComparison.Ordinal);
        Assert.DoesNotContain("<div class=\"lc\" data-s=\"ringing\"", after, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Starting_over_clears_what_the_demo_gathered_and_sets_it_up_again_keeping_who_can_sign_in()
    {
        var console = new FixtureConsole(FixtureConsole.DefaultDirectory);
        await using var app = talkwatch.Create(console);
        var poller = app.Services.GetRequiredService<CallLogPoller>();
        await poller.RunOnceAsync(Ct);
        var captured = await DbAsync(app, (db, _) => db.Calls.CountAsync(Ct));
        var demo = Demo(app, console);
        await demo.GuestAsync(Ct);
        await demo.SeedAsync(Ct);
        for (var i = 0; i < 6; i++)
        {
            demo.AddCall();
            await poller.RunOnceAsync(Ct);
        }

        // A flow someone added in the demo, and the alerts the calls raised: all gone after.
        await DbAsync(app, async (db, _) => { db.AlertFlows.Add(new AlertFlow { Id = Guid.NewGuid(), SiteId = await db.Sites.Select(x => x.Id).SingleAsync(Ct), Name = "Mine", Trigger = AlertEventType.MissedCall, Definition = "{}" }); return await db.SaveChangesAsync(Ct); });
        Assert.True(await DbAsync(app, (db, _) => db.AlertEvents.AnyAsync(Ct)));

        await demo.ResetAsync(Ct);
        await poller.RunOnceAsync(Ct);

        var (flows, events, calls, people) = await DbAsync(app, async (db, users) => (
            await db.AlertFlows.Select(f => f.Name).ToListAsync(Ct), await db.AlertEvents.CountAsync(Ct), await db.Calls.CountAsync(Ct),
            await users.Users.Select(u => u.UserName).ToListAsync(Ct)));
        Assert.Equal(DemoActivity.ExampleFlows, flows.Count);
        Assert.DoesNotContain("Mine", flows);
        Assert.Equal(0, events);
        Assert.Equal(captured, calls); // The captured calls again, and none of the ones added since.
        Assert.Contains(TalkWatchApp.AdminUsername, people);
        Assert.Contains("guest", people);
        Assert.NotNull(demo.NextResetAt);
    }

    [Fact]
    public async Task A_demo_shows_number_roles_reports_and_the_newer_flow_steps()
    {
        var console = new FixtureConsole(FixtureConsole.DefaultDirectory);
        await using var app = talkwatch.Create(console);
        var poller = app.Services.GetRequiredService<CallLogPoller>();
        await poller.RunOnceAsync(Ct);
        var demo = Demo(app, console);
        await demo.GuestAsync(Ct);

        // A demo that already had its flows before reports and number roles existed gets those too, without a reset.
        await DbAsync(app, async (db, _) => { db.AlertFlows.Add(new AlertFlow { Id = Guid.NewGuid(), SiteId = await db.Sites.Select(x => x.Id).SingleAsync(Ct), Name = "Older", Trigger = AlertEventType.MissedCall, Definition = Flows.Write(new FlowDefinition { Trigger = AlertEventType.MissedCall, Steps = [new NotifyStep([FlowRecipient.ToRang()])] }) }); return await db.SaveChangesAsync(Ct); });
        await demo.SeedAsync(Ct);
        await demo.SeedAsync(Ct);

        var (held, reports, runs) = await DbAsync(app, async (db, _) => (
            await db.NumberRoles.ToListAsync(Ct),
            await db.Reports.Include(r => r.Numbers).Include(r => r.Recipients).ToListAsync(Ct),
            await db.ReportRuns.ToListAsync(Ct)));
        var roles = app.Services.CreateScope().ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        var heldRoles = new List<Permission>();
        foreach (var h in held)
        {
            heldRoles.Add(await RolePermissions.GetAsync(roles, (await roles.FindByIdAsync(h.RoleId.ToString()))!));
        }

        // Someone looks after the number and can give roles on it; someone else only sees it.
        Assert.True(held.Count >= 2);
        Assert.Contains(heldRoles, p => p.HasFlag(Permission.ManageNumberPeople));
        Assert.Contains(heldRoles, p => !p.HasFlag(Permission.ManageNumberPeople));
        // A site-wide report and one for a number, each already run once, so there is a copy to open.
        Assert.Equal(2, reports.Count);
        Assert.Contains(reports, r => r.Numbers.Count == 0);
        Assert.Contains(reports, r => r.Numbers.Count > 0 && r.Recipients.Count > 0);
        Assert.All(reports, r => Assert.Contains(runs, run => run.ReportId == r.Id));
        Assert.All(reports, r => Assert.NotNull(r.NextRunAt));

        // Flows are seeded only on a demo with none, so start over and look at the examples.
        await demo.ResetAsync(Ct);
        var flows = await DbAsync(app, async (db, _) => (await db.AlertFlows.Select(f => f.Definition).ToListAsync(Ct)).Select(d => Flows.Read(d)!).ToList());
        var conditions = flows.SelectMany(f => f.Conditions.Concat(Branches(f.Steps).Select(b => b.If))).ToList();
        var notifies = flows.SelectMany(f => Notifies(f.Steps)).ToList();
        Assert.Contains(conditions, c => c is CallerMissedCondition);
        Assert.Contains(conditions, c => c is AnswerRateCondition);
        Assert.Contains(conditions, c => c is WaitingCallersCondition);
        Assert.Contains(conditions, c => c is TranscriptWordsCondition);
        Assert.Contains(notifies, n => n.Include.HasFlag(NotifyIncludes.Voicemail));
        Assert.Contains(notifies, n => n.Include.HasFlag(NotifyIncludes.Summary));
        Assert.Contains(notifies, n => n.To.Any(r => r.Contact is not null));
        Assert.All(flows, f => Assert.Null(Flows.Problem(f)));
        // And the number roles and reports came back with them.
        Assert.Equal(2, await DbAsync(app, (db, _) => db.Reports.CountAsync(Ct)));
        Assert.True(await DbAsync(app, (db, _) => db.NumberRoles.AnyAsync(Ct)));
    }

    private static IEnumerable<BranchStep> Branches(IEnumerable<FlowStep> steps) =>
        steps.OfType<BranchStep>().SelectMany(b => Branches(b.Then).Concat(Branches(b.Otherwise)).Prepend(b));

    private static IEnumerable<NotifyStep> Notifies(IEnumerable<FlowStep> steps) =>
        steps.OfType<NotifyStep>().Concat(steps.OfType<BranchStep>().SelectMany(b => Notifies(b.Then).Concat(Notifies(b.Otherwise))));

    // The demo sets the guest up as it starts, and a test or a reset may at the same moment: two password resets on the
    // one account at once failed with a concurrency error, which stopped the whole app.
    [Fact]
    public async Task Setting_the_guest_up_from_several_places_at_once_is_safe()
    {
        var console = new FixtureConsole(FixtureConsole.DefaultDirectory);
        await using var app = talkwatch.Create(console);
        var demo = Demo(app, console);

        // From nothing, then again and again over an existing guest: each round several at once.
        for (var round = 0; round < 8; round++)
        {
            await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() => demo.GuestAsync(Ct), Ct)));
        }

        var roles = await DbAsync(app, async (_, users) => await users.GetRolesAsync((await users.FindByNameAsync("guest"))!));
        Assert.Equal([DemoActivity.GuestRole], roles);
    }
}
