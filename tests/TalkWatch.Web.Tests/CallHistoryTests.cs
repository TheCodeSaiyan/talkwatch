using System.Net;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TalkWatch.Core.Calls;
using TalkWatch.Data;
using TalkWatch.Replay;
using TalkWatch.Web.Services;

namespace TalkWatch.Web.Tests;

public sealed class CallHistoryTests(TalkWatchApp talkwatch) : IClassFixture<TalkWatchApp>
{
    private const string Did = "+441144960042";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task PollAsync(WebApplicationFactory<Program> app) =>
        await app.Services.GetRequiredService<CallLogPoller>().RunOnceAsync(Ct);

    [Fact]
    public async Task Signed_out_the_call_history_sends_you_to_sign_in()
    {
        await using var app = talkwatch.Create(new FixtureConsole(FixtureConsole.DefaultDirectory));
        using var browser = TalkWatchApp.Browser(app);

        var response = await browser.GetAsync(new Uri("/calls", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/signin", response.Headers.Location!.PathAndQuery, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_bootstrap_admin_signs_in_and_sees_every_polled_call()
    {
        var console = new FixtureConsole(FixtureConsole.DefaultDirectory);
        await using var app = talkwatch.Create(console);
        await PollAsync(app);
        using var browser = TalkWatchApp.Browser(app);

        var signIn = await TalkWatchApp.SignInAsync(browser, TalkWatchApp.AdminUsername, TalkWatchApp.AdminPassword);
        var page = await browser.GetStringAsync(new Uri("/calls", UriKind.Relative), Ct);

        Assert.Equal("/live", signIn.Headers.Location!.OriginalString);
        Assert.Equal(Math.Min(50, console.CallCount), TalkWatchApp.CallRows(page));
        Assert.Contains($"of {console.CallCount}", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_keys_that_sign_cookies_are_kept_in_the_database()
    {
        // In the container they were lost on every restart, signing everyone out.
        await using var app = talkwatch.Create(new FixtureConsole(FixtureConsole.DefaultDirectory));
        using var browser = TalkWatchApp.Browser(app);
        await TalkWatchApp.SignInAsync(browser, TalkWatchApp.AdminUsername, TalkWatchApp.AdminPassword);

        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<AccessScopeHolder>().UseSystemScope();
        var keys = await scope.ServiceProvider.GetRequiredService<TalkWatchDbContext>().DataProtectionKeys.CountAsync(Ct);

        Assert.True(keys >= 1);
    }

    [Fact]
    public async Task A_wrong_password_does_not_sign_in()
    {
        await using var app = talkwatch.Create(new FixtureConsole(FixtureConsole.DefaultDirectory));
        using var browser = TalkWatchApp.Browser(app);

        var signIn = await TalkWatchApp.SignInAsync(browser, TalkWatchApp.AdminUsername, "not the password");
        var calls = await browser.GetAsync(new Uri("/calls", UriKind.Relative), Ct);

        // Follow the redirect: the page it lands on must say what happened. On the first real deploy it threw instead.
        var landing = await browser.GetAsync(signIn.Headers.Location!, Ct);

        Assert.StartsWith("/signin?failed=", signIn.Headers.Location!.OriginalString, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Redirect, calls.StatusCode);
        Assert.Equal(HttpStatusCode.OK, landing.StatusCode);
        Assert.Contains("Sign-in failed", await landing.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/signin?failed=1")]
    [InlineData("/signin?failed=nonsense")]
    [InlineData("/signin?failed=true&returnUrl=%2Fcalls")]
    public async Task The_sign_in_page_renders_whatever_the_query_says(string path)
    {
        await using var app = talkwatch.Create(new FixtureConsole(FixtureConsole.DefaultDirectory));
        using var browser = TalkWatchApp.Browser(app);

        var response = await browser.GetAsync(new Uri(path, UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_return_address_off_the_site_is_ignored()
    {
        await using var app = talkwatch.Create(new FixtureConsole(FixtureConsole.DefaultDirectory));
        using var browser = TalkWatchApp.Browser(app);

        var signIn = await TalkWatchApp.SignInAsync(browser, TalkWatchApp.AdminUsername, TalkWatchApp.AdminPassword, "//evil.example/steal");

        Assert.Equal("/live", signIn.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task A_viewer_sees_only_the_calls_through_their_granted_number()
    {
        await using var app = talkwatch.Create(new FixtureConsole(FixtureConsole.DefaultDirectory));
        await PollAsync(app);

        int expected;
        using (var scope = app.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<AccessScopeHolder>().UseSystemScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var db = scope.ServiceProvider.GetRequiredService<TalkWatchDbContext>();
            var site = scope.ServiceProvider.GetRequiredService<CurrentSite>();

            var viewer = new AppUser { Id = Guid.NewGuid(), UserName = "viewer", SiteId = site.Id };
            Assert.True((await users.CreateAsync(viewer, "a long enough password")).Succeeded);
            await users.AddToRoleAsync(viewer, Roles.Viewer);
            db.Grants.Add(new Grant { Id = Guid.NewGuid(), SiteId = site.Id, UserId = viewer.Id, Kind = LineKind.Did, Key = Did, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(Ct);

            expected = await db.CallLines.Where(l => l.Kind == LineKind.Did && l.Key == Did).Select(l => l.CallId).Distinct().CountAsync(Ct);
        }

        using var browser = TalkWatchApp.Browser(app);
        await TalkWatchApp.SignInAsync(browser, "viewer", "a long enough password");
        var page = await browser.GetStringAsync(new Uri("/calls", UriKind.Relative), Ct);

        Assert.InRange(expected, 1, 49);
        Assert.Equal(expected, TalkWatchApp.CallRows(page));
    }

    [Fact]
    public async Task Drift_is_recorded_and_shown_and_nothing_is_written()
    {
        var console = new FixtureConsole(FixtureConsole.DefaultDirectory) { Drifted = true };
        await using var app = talkwatch.Create(console);
        await PollAsync(app);
        using var browser = TalkWatchApp.Browser(app);
        await TalkWatchApp.SignInAsync(browser, TalkWatchApp.AdminUsername, TalkWatchApp.AdminPassword);

        var page = await browser.GetStringAsync(new Uri("/calls", UriKind.Relative), Ct);

        Assert.True(app.Services.GetRequiredService<IngestionStatus>().Drifted);
        Assert.Contains("changed shape", page, StringComparison.Ordinal);
        Assert.Equal(0, TalkWatchApp.CallRows(page));
    }
}
