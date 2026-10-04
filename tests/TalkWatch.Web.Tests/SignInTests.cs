using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TalkWatch.Data;
using TalkWatch.Replay;
using TalkWatch.Web.Services;

namespace TalkWatch.Web.Tests;

public sealed partial class SignInTests(TalkWatchApp talkwatch) : IClassFixture<TalkWatchApp>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [GeneratedRegex(@"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""")]
    private static partial Regex Token();

    [GeneratedRegex(@"data-authenticator-key>([^<]+)<")]
    private static partial Regex AuthenticatorKey();

    [GeneratedRegex(@"<li><code>([^<]+)</code></li>")]
    private static partial Regex RecoveryCode();

    private static async Task<HttpResponseMessage> PostAsync(HttpClient browser, string page, string action, params (string Name, string Value)[] fields)
    {
        var html = await browser.GetStringAsync(new Uri(page, UriKind.Relative), Ct);
        var form = fields.Select(f => new KeyValuePair<string, string>(f.Name, f.Value))
            .Append(new("__RequestVerificationToken", WebUtility.HtmlDecode(Token().Match(html).Groups[1].Value)));
        using var content = new FormUrlEncodedContent(form);
        return await browser.PostAsync(new Uri(action, UriKind.Relative), content);
    }

    /// <summary>The six-digit code an authenticator app shows now for a base32 key (RFC 6238, as Identity checks it).</summary>
    private static string Totp(string base32)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bits = string.Concat(base32.ToUpperInvariant().Where(char.IsLetterOrDigit).Select(c => Convert.ToString(alphabet.IndexOf(c), 2).PadLeft(5, '0')));
        var key = Enumerable.Range(0, bits.Length / 8).Select(i => Convert.ToByte(bits.Substring(i * 8, 8), 2)).ToArray();
        var counter = BitConverter.GetBytes(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(counter);
        }

#pragma warning disable CA5350 // RFC 6238's default, which authenticator apps use and Identity checks against.
        var hash = HMACSHA1.HashData(key, counter);
#pragma warning restore CA5350
        var offset = hash[^1] & 0x0F;
        var value = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (value % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Turns two-factor sign-in on through the account page, as a person would. Returns the key and recovery codes.</summary>
    private static async Task<(string Key, List<string> RecoveryCodes)> TurnOnAsync(HttpClient browser)
    {
        var page = await browser.GetStringAsync(new Uri("/account", UriKind.Relative), Ct);
        var key = WebUtility.HtmlDecode(AuthenticatorKey().Match(page).Groups[1].Value).Replace(" ", "", StringComparison.Ordinal);
        var response = await PostAsync(browser, "/account", "/account", ("_handler", "turn-on"), ("Form.Code", Totp(key)));
        var done = await response.Content.ReadAsStringAsync(Ct);
        Assert.Contains("data-two-factor=\"on\"", done, StringComparison.Ordinal);
        return (key, [.. RecoveryCode().Matches(done).Select(m => m.Groups[1].Value)]);
    }

    private static async Task<HttpResponseMessage> PasswordAsync(HttpClient browser) =>
        await TalkWatchApp.SignInAsync(browser, TalkWatchApp.AdminUsername, TalkWatchApp.AdminPassword);

    private static Task<HttpResponseMessage> CodeAsync(HttpClient browser, string code, bool recovery = false) =>
        PostAsync(browser, "/signin/code", "/account/signin/code", [("Code", code), .. recovery ? new[] { ("Recovery", "true") } : []]);

    [Fact]
    public async Task With_two_factor_on_the_password_alone_is_not_enough_and_the_code_finishes_signing_in()
    {
        await using var app = talkwatch.Create(new FixtureConsole(FixtureConsole.DefaultDirectory));
        using var first = TalkWatchApp.Browser(app);
        await PasswordAsync(first);
        var (key, codes) = await TurnOnAsync(first);

        using var browser = TalkWatchApp.Browser(app);
        var password = await PasswordAsync(browser);
        var beforeCode = await browser.GetAsync(new Uri("/calls", UriKind.Relative), Ct);
        var wrong = await CodeAsync(browser, "000000");
        var right = await CodeAsync(browser, Totp(key));
        var after = await browser.GetAsync(new Uri("/calls", UriKind.Relative), Ct);

        Assert.Equal(10, codes.Count);
        Assert.StartsWith("/signin/code", password.Headers.Location!.OriginalString, StringComparison.Ordinal);
        Assert.NotEqual(HttpStatusCode.OK, beforeCode.StatusCode);
        Assert.StartsWith("/signin/code?failed", wrong.Headers.Location!.OriginalString, StringComparison.Ordinal);
        Assert.Equal("/live", right.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        Assert.Equal(1, await AsSystemAsync(app, db => db.AuditEvents.CountAsync(e => e.Action == "2fa.enable", Ct)));
    }

    [Fact]
    public async Task A_recovery_code_signs_in_once()
    {
        await using var app = talkwatch.Create(new FixtureConsole(FixtureConsole.DefaultDirectory));
        using var first = TalkWatchApp.Browser(app);
        await PasswordAsync(first);
        var (_, codes) = await TurnOnAsync(first);

        using var once = TalkWatchApp.Browser(app);
        await PasswordAsync(once);
        var used = await CodeAsync(once, codes[0], recovery: true);
        using var twice = TalkWatchApp.Browser(app);
        await PasswordAsync(twice);
        var reused = await CodeAsync(twice, codes[0], recovery: true);

        Assert.Equal("/live", used.Headers.Location!.OriginalString);
        Assert.StartsWith("/signin/code?failed", reused.Headers.Location!.OriginalString, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_admin_can_turn_off_two_factor_for_someone_who_lost_their_phone()
    {
        await using var app = talkwatch.Create(new FixtureConsole(FixtureConsole.DefaultDirectory));
        Guid id;
        using (var scope = app.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<AccessScopeHolder>().UseSystemScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var viewer = new AppUser { Id = Guid.NewGuid(), UserName = "viewer", SiteId = scope.ServiceProvider.GetRequiredService<CurrentSite>().Id };
            Assert.True((await users.CreateAsync(viewer, "a long enough password")).Succeeded);
            await users.AddToRoleAsync(viewer, Roles.Viewer);
            await users.ResetAuthenticatorKeyAsync(viewer);
            await users.SetTwoFactorEnabledAsync(viewer, true);
            id = viewer.Id;
        }

        using var admin = TalkWatchApp.Browser(app);
        await PasswordAsync(admin);
        await PostAsync(admin, $"/admin/users/{id}", $"/admin/users/{id}/two-factor/reset");
        using var viewerBrowser = TalkWatchApp.Browser(app);
        var signIn = await TalkWatchApp.SignInAsync(viewerBrowser, "viewer", "a long enough password");

        Assert.Equal("/live", signIn.Headers.Location!.OriginalString);
        Assert.Equal(1, await AsSystemAsync(app, db => db.AuditEvents.CountAsync(e => e.Action == "2fa.reset", Ct)));
    }

    [Fact]
    public async Task Every_page_carries_the_security_headers_and_the_import_map_its_nonce()
    {
        await using var app = talkwatch.Create(new FixtureConsole(FixtureConsole.DefaultDirectory));
        using var browser = TalkWatchApp.Browser(app);

        var response = await browser.GetAsync(new Uri("/signin", UriKind.Relative), Ct);
        var page = await response.Content.ReadAsStringAsync(Ct);

        var policy = string.Join(' ', response.Headers.GetValues("Content-Security-Policy"));
        var nonce = Regex.Match(policy, "'nonce-([^']+)'").Groups[1].Value;
        Assert.NotEmpty(nonce);
        Assert.Contains($"<script type=\"importmap\" nonce=\"{nonce}\"", page, StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'none'", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-inline' 'nonce", policy, StringComparison.Ordinal);
        // The player plays a recording it fetched from here as a blob: allowed for media, and for nothing else.
        Assert.Contains("media-src 'self' blob:;", policy, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(policy, "blob:"));
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());

        var another = await browser.GetAsync(new Uri("/signin", UriKind.Relative), Ct);
        Assert.DoesNotContain(nonce, string.Join(' ', another.Headers.GetValues("Content-Security-Policy")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Too_many_sign_in_attempts_from_one_place_are_turned_away()
    {
        await using var app = talkwatch.Create(new FixtureConsole(FixtureConsole.DefaultDirectory));
        using var browser = TalkWatchApp.Browser(app);

        var answers = new List<string>();
        for (var i = 0; i < 11; i++)
        {
            answers.Add((await TalkWatchApp.SignInAsync(browser, "nobody", "not a real password")).Headers.Location!.OriginalString);
        }

        Assert.All(answers.Take(10), a => Assert.StartsWith("/signin?failed", a, StringComparison.Ordinal));
        Assert.Equal("/signin?limited=1", answers[10]);
    }

    private static async Task<T> AsSystemAsync<T>(WebApplicationFactory<Program> app, Func<TalkWatchDbContext, Task<T>> work)
    {
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<AccessScopeHolder>().UseSystemScope();
        return await work(scope.ServiceProvider.GetRequiredService<TalkWatchDbContext>());
    }
}
