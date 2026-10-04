using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TalkWatch.Data;
using TalkWatch.Replay;
using TalkWatch.Web.Services;

namespace TalkWatch.Web.Tests;

public sealed class DemoTests(TalkWatchApp talkwatch) : IClassFixture<TalkWatchApp>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Demo_mode_replays_the_fixtures_as_if_their_calls_were_recent_and_says_so()
    {
        // No fake console injected: demo mode's own replay is what runs.
        await using var app = talkwatch.Create(new FixtureConsole(FixtureConsole.DefaultDirectory), fakeConsole: false,
            settings: new Dictionary<string, string> { ["Demo:Enabled"] = "true", ["Demo:Fixtures"] = FixtureConsole.DefaultDirectory });

        await app.Services.GetRequiredService<CallLogPoller>().RunOnceAsync(Ct);

        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<AccessScopeHolder>().UseSystemScope();
        var db = scope.ServiceProvider.GetRequiredService<TalkWatchDbContext>();
        var newest = await db.Calls.MaxAsync(c => c.Time, Ct);
        using var browser = TalkWatchApp.Browser(app);
        var page = await browser.GetStringAsync(new Uri("/signin", UriKind.Relative), Ct);

        Assert.Equal(new FixtureConsole(FixtureConsole.DefaultDirectory).CallCount, await db.Calls.CountAsync(Ct));
        Assert.InRange(DateTimeOffset.UtcNow - newest, TimeSpan.FromMinutes(55), TimeSpan.FromMinutes(65));
        Assert.True(await db.AudioFiles.AnyAsync(a => a.State == AudioState.Copied, Ct));
        Assert.Contains("data-demo", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task In_demo_mode_the_sign_in_page_offers_the_guest_and_signing_in_as_it_opens_on_Now()
    {
        await using var app = talkwatch.Create(new FixtureConsole(FixtureConsole.DefaultDirectory), fakeConsole: false,
            settings: new Dictionary<string, string> { ["Demo:Enabled"] = "true", ["Demo:Fixtures"] = FixtureConsole.DefaultDirectory, ["Demo:CallEveryMinutes"] = "0" });
        await app.Services.GetRequiredService<DemoActivity>().GuestAsync(TestContext.Current.CancellationToken);
        using var browser = TalkWatchApp.Browser(app);

        var page = await browser.GetStringAsync(new Uri("/signin", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Contains("data-demo-guest", page, StringComparison.Ordinal);
        Assert.Contains("Sign in as guest", page, StringComparison.Ordinal);

        var token = System.Text.RegularExpressions.Regex.Match(page, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        using var form = new FormUrlEncodedContent([new("ReturnUrl", ""), new("Username", "guest"), new("Password", new DemoOptions().GuestPassword), new("__RequestVerificationToken", System.Net.WebUtility.HtmlDecode(token))]);
        var signedIn = await browser.PostAsync(new Uri("/account/signin", UriKind.Relative), form, TestContext.Current.CancellationToken);
        Assert.True(signedIn.Headers.Location is not null, $"{(int)signedIn.StatusCode}: {(await signedIn.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))[..Math.Min(300, (int)(signedIn.Content.Headers.ContentLength ?? 300))]}");
        Assert.Equal("/live", signedIn.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Without_demo_mode_there_is_no_banner()
    {
        await using var app = talkwatch.Create(new FixtureConsole(FixtureConsole.DefaultDirectory));
        using var browser = TalkWatchApp.Browser(app);

        Assert.DoesNotContain("data-demo", await browser.GetStringAsync(new Uri("/signin", UriKind.Relative), Ct), StringComparison.Ordinal);
    }

    [Fact]
    public void Shifting_moves_every_call_and_event_by_the_same_amount()
    {
        var original = new FixtureConsole(FixtureConsole.DefaultDirectory).Calls();
        var shifted = new FixtureConsole(FixtureConsole.DefaultDirectory);
        var target = new DateTimeOffset(2030, 6, 1, 12, 0, 0, TimeSpan.Zero);

        shifted.ShiftTimes(target);

        var by = target - original.Max(c => c.Time);
        var after = shifted.Calls().ToDictionary(c => c.Uuid);
        Assert.Equal(target, after.Values.Max(c => c.Time));
        Assert.All(original, c =>
        {
            Assert.Equal(c.Time + by, after[c.Uuid].Time);
            Assert.Equal(c.CallEvents.Select(e => e.Time + by), after[c.Uuid].CallEvents.Select(e => e.Time));
        });
    }
}
