using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TalkWatch.Data;
using TalkWatch.Replay;
using TalkWatch.Web.Services;

namespace TalkWatch.Web.Tests;

/// <summary>What the first run against a real console taught: the poller must survive anything, and go easy on sign-in.</summary>
public sealed class PollerTests(TalkWatchApp talkwatch) : IClassFixture<TalkWatchApp>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static CallLogPoller Poller(WebApplicationFactory<Program> app) => app.Services.GetRequiredService<CallLogPoller>();

    private static IngestionStatus Status(WebApplicationFactory<Program> app) => app.Services.GetRequiredService<IngestionStatus>();

    private static async Task<List<CallRow>> CallsAsync(WebApplicationFactory<Program> app)
    {
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<AccessScopeHolder>().UseSystemScope();
        return await scope.ServiceProvider.GetRequiredService<TalkWatchDbContext>().Calls.ToListAsync(Ct);
    }

    [Fact]
    public async Task Calls_with_no_direction_or_status_are_stored_as_unknown()
    {
        var console = new FixtureConsole(FixtureConsole.DefaultDirectory) { BlankDirectionNewest = 3 };
        await using var app = talkwatch.Create(console);

        await Poller(app).RunOnceAsync(Ct);

        var calls = await CallsAsync(app);
        Assert.Null(Status(app).LastError);
        Assert.Equal(console.CallCount, calls.Count);
        Assert.Equal(3, calls.Count(c => c.Direction == "unknown" && c.Status == "unknown"));
    }

    [Fact]
    public async Task A_failure_nobody_planned_for_is_recorded_not_thrown()
    {
        var console = new FixtureConsole(FixtureConsole.DefaultDirectory) { ThrowOnCallLog = true };
        await using var app = talkwatch.Create(console);

        await Poller(app).RunOnceAsync(Ct);

        Assert.Contains("Simulated failure", Status(app).LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Polls_reuse_one_console_session()
    {
        var console = new FixtureConsole(FixtureConsole.DefaultDirectory) { RequireSession = true };
        await using var app = talkwatch.Create(console);

        await Poller(app).RunOnceAsync(Ct);
        await Poller(app).RunOnceAsync(Ct);
        await Poller(app).RunOnceAsync(Ct);

        Assert.Equal(1, console.SignIns);
        Assert.Null(Status(app).LastError);
    }

    [Fact]
    public async Task An_expired_session_is_renewed_and_the_poll_still_succeeds()
    {
        var console = new FixtureConsole(FixtureConsole.DefaultDirectory) { RequireSession = true };
        await using var app = talkwatch.Create(console);
        await Poller(app).RunOnceAsync(Ct);

        console.ExpireSession();
        await Poller(app).RunOnceAsync(Ct);

        Assert.Equal(2, console.SignIns);
        Assert.Null(Status(app).LastError);
    }

    [Fact]
    public async Task A_refused_sign_in_is_not_retried_every_poll()
    {
        // Wrong credentials, retried each minute, are how the console account gets locked.
        var console = new FixtureConsole(FixtureConsole.DefaultDirectory);
        await using var app = talkwatch.Create(console, talkPassword: "not the password");

        await Poller(app).RunOnceAsync(Ct);
        await Poller(app).RunOnceAsync(Ct);

        Assert.Equal(1, console.SignIns);
        Assert.True(Status(app).BackOffUntil > DateTimeOffset.UtcNow.AddMinutes(14));
    }

    [Fact]
    public async Task A_rate_limited_sign_in_waits_before_trying_again()
    {
        var console = new FixtureConsole(FixtureConsole.DefaultDirectory) { RateLimitSignIn = TimeSpan.FromMinutes(10) };
        await using var app = talkwatch.Create(console);

        await Poller(app).RunOnceAsync(Ct);
        await Poller(app).RunOnceAsync(Ct);

        Assert.Equal(1, console.SignIns);
        Assert.Contains("429", Status(app).LastError, StringComparison.Ordinal);
        Assert.True(Status(app).BackOffUntil > DateTimeOffset.UtcNow.AddMinutes(9));
    }
}
