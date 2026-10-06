using TalkWatch.Replay;

namespace TalkWatch.Web.Tests;

/// <summary>
/// Behind a proxy that ends TLS, TalkWatch may see plain http even though people reach it over https. Its public address
/// says which: over https, the sign-in cookie never goes over http, and browsers are told to stay on https.
/// </summary>
public sealed class SecureCookieTests(TalkWatchApp talkwatch) : IClassFixture<TalkWatchApp>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Reached_over_https_the_sign_in_cookie_is_secure_and_browsers_are_told_to_keep_to_https()
    {
        await using var app = talkwatch.Create(new FixtureConsole(FixtureConsole.DefaultDirectory),
            settings: new Dictionary<string, string> { ["Site:PublicUrl"] = "https://talkwatch.example" });
        using var browser = TalkWatchApp.Browser(app);

        var page = await browser.GetAsync(new Uri("/signin", UriKind.Relative), Ct);
        var signedIn = await TalkWatchApp.SignInAsync(browser, TalkWatchApp.AdminUsername, TalkWatchApp.AdminPassword);

        var session = signedIn.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("talkwatch=", StringComparison.Ordinal));
        Assert.Contains("secure", session, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("max-age=31536000", page.Headers.GetValues("Strict-Transport-Security").Single());
    }

    // A first run over http on the LAN still signs in: a secure cookie set over http would never come back.
    [Fact]
    public async Task Without_an_https_public_address_the_cookie_follows_the_request()
    {
        await using var app = talkwatch.Create(new FixtureConsole(FixtureConsole.DefaultDirectory));
        using var browser = TalkWatchApp.Browser(app);

        var signedIn = await TalkWatchApp.SignInAsync(browser, TalkWatchApp.AdminUsername, TalkWatchApp.AdminPassword);

        Assert.DoesNotContain("secure", signedIn.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("talkwatch=", StringComparison.Ordinal)), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(System.Net.HttpStatusCode.OK, (await browser.GetAsync(new Uri("/calls", UriKind.Relative), Ct)).StatusCode);
    }
}
