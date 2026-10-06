using System.Net;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TalkWatch.Data;
using TalkWatch.Replay;
using TalkWatch.Web.Services;

namespace TalkWatch.Web.Tests;

public sealed class OidcTests(TalkWatchApp talkwatch) : IClassFixture<TalkWatchApp>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Dictionary<string, string> Settings = new()
    {
        ["Oidc:Authority"] = FakeOidcProvider.Authority,
        ["Oidc:ClientId"] = FakeOidcProvider.ClientId,
        ["Oidc:ClientSecret"] = FakeOidcProvider.ClientSecret,
        ["Oidc:AdminGroups"] = "THS Admins",
        ["Oidc:ViewerGroups"] = "talkwatch-users",
    };

    private WebApplicationFactory<Program> Create(FakeOidcProvider provider) =>
        talkwatch.Create(new FixtureConsole(FixtureConsole.DefaultDirectory), settings: Settings,
            services: s => s.Configure<OpenIdConnectOptions>(OidcOptions.Scheme, o => o.BackchannelHttpHandler = provider));

    // HTTPS, as behind a real proxy: the handler's correlation cookies are Secure and would not come back over http.
    private static HttpClient Browser(WebApplicationFactory<Program> app) =>
        app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true, BaseAddress = new Uri("https://localhost") });

    /// <summary>The whole round trip: the button, the provider's page, and the way back. Returns where the app ends up.</summary>
    private static async Task<string> SignInAsync(HttpClient browser, FakeOidcProvider provider, string subject, string username, params string[] groups)
    {
        var challenge = await browser.GetAsync(new Uri("/account/signin/oidc?returnUrl=%2Fdashboard", UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.Redirect, challenge.StatusCode);
        Assert.StartsWith($"{FakeOidcProvider.Authority}/authorize", challenge.Headers.Location!.ToString(), StringComparison.Ordinal);

        var callback = await browser.GetAsync(provider.Authorise(challenge.Headers.Location!, subject, username, groups), Ct);
        var done = await browser.GetAsync(callback.Headers.Location!, Ct);
        return done.Headers.Location!.OriginalString;
    }

    private static async Task<T> AsSystemAsync<T>(WebApplicationFactory<Program> app, Func<UserManager<AppUser>, Task<T>> work)
    {
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<AccessScopeHolder>().UseSystemScope();
        return await work(scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>());
    }

    [Fact]
    public async Task Someone_in_the_viewer_group_gets_a_viewer_account_on_first_sign_in()
    {
        using var provider = new FakeOidcProvider();
        await using var app = Create(provider);
        using var browser = Browser(app);

        var landed = await SignInAsync(browser, provider, "subject-1", "sam", "talkwatch-users");
        var calls = await browser.GetAsync(new Uri("/calls", UriKind.Relative), Ct);

        Assert.Equal("/dashboard", landed);
        Assert.Equal(HttpStatusCode.OK, calls.StatusCode);
        var roles = await AsSystemAsync(app, async users => await users.GetRolesAsync((await users.FindByNameAsync("sam"))!));
        Assert.Equal([Roles.Viewer], roles);
    }

    [Fact]
    public async Task Admin_follows_the_admin_group_at_each_sign_in()
    {
        using var provider = new FakeOidcProvider();
        await using var app = Create(provider);

        using (var first = Browser(app))
        {
            await SignInAsync(first, provider, "subject-2", "alex", "THS Admins");
        }

        var whileInGroup = await AsSystemAsync(app, async users => await users.IsInRoleAsync((await users.FindByNameAsync("alex"))!, Roles.Admin));

        using (var second = Browser(app))
        {
            await SignInAsync(second, provider, "subject-2", "alex", "talkwatch-users");
        }

        var afterLeaving = await AsSystemAsync(app, async users => await users.GetRolesAsync((await users.FindByNameAsync("alex"))!));
        Assert.True(whileInGroup);
        Assert.Equal([Roles.Viewer], afterLeaving);
    }

    [Fact]
    public async Task Someone_in_neither_group_is_turned_away_and_no_account_is_made()
    {
        using var provider = new FakeOidcProvider();
        await using var app = Create(provider);
        using var browser = Browser(app);

        var landed = await SignInAsync(browser, provider, "subject-3", "stranger", "some-other-group");
        var calls = await browser.GetAsync(new Uri("/calls", UriKind.Relative), Ct);

        Assert.Equal("/signin?oidc=not-allowed", landed);
        Assert.NotEqual(HttpStatusCode.OK, calls.StatusCode);
        Assert.Null(await AsSystemAsync(app, users => users.FindByNameAsync("stranger")));
    }

    // Some providers let people choose their username: one who calls themselves "admin" there is not the admin here.
    [Fact]
    public async Task An_account_of_the_same_name_with_a_password_is_not_taken_over()
    {
        using var provider = new FakeOidcProvider();
        await using var app = Create(provider);
        using var browser = Browser(app);

        var landed = await SignInAsync(browser, provider, "subject-4", TalkWatchApp.AdminUsername, "talkwatch-users");
        var calls = await browser.GetAsync(new Uri("/calls", UriKind.Relative), Ct);

        Assert.Equal("/signin?oidc=link-first", landed);
        Assert.NotEqual(HttpStatusCode.OK, calls.StatusCode);
        Assert.Null(await AsSystemAsync(app, users => users.FindByLoginAsync(OidcOptions.Scheme, "subject-4")));
        Assert.True(await AsSystemAsync(app, async users => await users.IsInRoleAsync((await users.FindByNameAsync(TalkWatchApp.AdminUsername))!, Roles.Admin)));
    }

    [Fact]
    public async Task Someone_signed_in_with_their_password_links_the_provider_from_their_account_and_keeps_it()
    {
        using var provider = new FakeOidcProvider();
        await using var app = Create(provider);
        var existing = await AsSystemAsync(app, async users => (await users.FindByNameAsync(TalkWatchApp.AdminUsername))!.Id);
        using var browser = Browser(app);
        await TalkWatchApp.SignInAsync(browser, TalkWatchApp.AdminUsername, TalkWatchApp.AdminPassword);

        // Started by a form post with the page's antiforgery token, then the provider's round trip.
        var account = await browser.GetStringAsync(new Uri("/account", UriKind.Relative), Ct);
        var token = System.Text.RegularExpressions.Regex.Match(account, @"action=""/account/link/oidc""[^>]*><input[^>]*name=""__RequestVerificationToken""[^>]*value=""([^""]+)""").Groups[1].Value;
        using var form = new FormUrlEncodedContent([new("__RequestVerificationToken", WebUtility.HtmlDecode(token))]);
        var challenge = await browser.PostAsync(new Uri("/account/link/oidc", UriKind.Relative), form, Ct);
        var callback = await browser.GetAsync(provider.Authorise(challenge.Headers.Location!, "subject-5", "someone-else-there", "THS Admins"), Ct);
        var done = await browser.GetAsync(callback.Headers.Location!, Ct);

        Assert.Equal("/account?oidc=linked", done.Headers.Location!.OriginalString);
        Assert.Equal(existing, await AsSystemAsync(app, async users => (await users.FindByLoginAsync(OidcOptions.Scheme, "subject-5"))?.Id));

        // From then on the provider signs them in to that account, whatever it calls them.
        using var later = Browser(app);
        Assert.Equal("/dashboard", await SignInAsync(later, provider, "subject-5", "someone-else-there", "THS Admins"));
    }

    [Fact]
    public async Task Linking_without_the_pages_antiforgery_token_is_refused()
    {
        using var provider = new FakeOidcProvider();
        await using var app = Create(provider);
        using var browser = Browser(app);
        await TalkWatchApp.SignInAsync(browser, TalkWatchApp.AdminUsername, TalkWatchApp.AdminPassword);

        using var form = new FormUrlEncodedContent([]);
        var forged = await browser.PostAsync(new Uri("/account/link/oidc", UriKind.Relative), form, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
    }

    [Fact]
    public async Task The_button_is_a_full_page_load_not_an_enhanced_navigation()
    {
        // Enhanced navigation follows a link with fetch(), which can't follow the redirect to the provider's site.
        using var provider = new FakeOidcProvider();
        await using var app = Create(provider);
        using var browser = Browser(app);

        var page = await browser.GetStringAsync(new Uri("/signin", UriKind.Relative), Ct);

        Assert.Matches("<a [^>]*href=\"/account/signin/oidc[^\"]*\"[^>]*data-enhance-nav=\"false\"", page);
    }

    [Fact]
    public async Task Without_settings_there_is_no_button_and_no_route()
    {
        await using var app = talkwatch.Create(new FixtureConsole(FixtureConsole.DefaultDirectory));
        using var browser = TalkWatchApp.Browser(app);

        var page = await browser.GetStringAsync(new Uri("/signin", UriKind.Relative), Ct);
        var start = await browser.GetAsync(new Uri("/account/signin/oidc", UriKind.Relative), Ct);

        Assert.DoesNotContain("/account/signin/oidc", page, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, start.StatusCode);
    }

    // What the provider says of someone is kept on their account: their email, followed when it changes there, and the
    // Talk user with the same email, linked when nobody has linked them yet.
    [Fact]
    public async Task Signing_in_keeps_the_email_from_the_provider_and_links_the_talk_user_with_it()
    {
        using var provider = new FakeOidcProvider();
        var console = new FixtureConsole(FixtureConsole.DefaultDirectory);
        console.Overrides["/proxy/talk/api/users"] = """
            [{"unique_id": "talk-sam", "id": 41, "full_name": "Sam Rivers", "ext": "0041", "email": "Sam.Rivers@example.test", "hide_from_user_list": false}]
            """;
        await using var app = talkwatch.Create(console, settings: Settings,
            services: s => s.Configure<OpenIdConnectOptions>(OidcOptions.Scheme, o => o.BackchannelHttpHandler = provider));
        await app.Services.GetRequiredService<LineDirectorySync>().RefreshAsync(Ct);

        provider.Email = "sam.rivers@example.test";
        using (var browser = Browser(app))
        {
            await SignInAsync(browser, provider, "subject-email", "sam", "talkwatch-users");
        }

        var sam = await AsSystemAsync(app, users => users.FindByNameAsync("sam"));
        Assert.Equal(("sam.rivers@example.test", "talk-sam"), (sam!.Email, sam.TalkUserUuid));

        provider.Email = "sam@example.test";
        using (var again = Browser(app))
        {
            await SignInAsync(again, provider, "subject-email", "sam", "talkwatch-users");
        }

        Assert.Equal("sam@example.test", (await AsSystemAsync(app, users => users.FindByNameAsync("sam")))!.Email);
    }

    // Some providers let people set their own email, and say so: one the provider hasn't checked could be a colleague's,
    // and would link the person to that colleague's Talk user, and the calls that ring it.
    [Fact]
    public async Task An_email_the_provider_has_not_checked_is_kept_but_links_no_talk_user()
    {
        using var provider = new FakeOidcProvider();
        var console = new FixtureConsole(FixtureConsole.DefaultDirectory);
        console.Overrides["/proxy/talk/api/users"] = """
            [{"unique_id": "talk-sam", "id": 41, "full_name": "Sam Rivers", "ext": "0041", "email": "sam.rivers@example.test", "hide_from_user_list": false}]
            """;
        await using var app = talkwatch.Create(console, settings: Settings,
            services: s => s.Configure<OpenIdConnectOptions>(OidcOptions.Scheme, o => o.BackchannelHttpHandler = provider));
        await app.Services.GetRequiredService<LineDirectorySync>().RefreshAsync(Ct);

        (provider.Email, provider.EmailVerified) = ("sam.rivers@example.test", false);
        using var browser = Browser(app);
        await SignInAsync(browser, provider, "subject-unverified", "mallory", "talkwatch-users");

        var mallory = await AsSystemAsync(app, users => users.FindByNameAsync("mallory"));
        Assert.Equal(("sam.rivers@example.test", false, (string?)null), (mallory!.Email, mallory.EmailConfirmed, mallory.TalkUserUuid));
    }
}
