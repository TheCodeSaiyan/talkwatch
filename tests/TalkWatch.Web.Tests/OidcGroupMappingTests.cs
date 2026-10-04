using System.Net;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TalkWatch.Core.Calls;
using TalkWatch.Data;
using TalkWatch.Replay;
using TalkWatch.Web.Services;

namespace TalkWatch.Web.Tests;

public sealed class OidcGroupMappingTests(TalkWatchApp talkwatch) : IClassFixture<TalkWatchApp>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly LineRef Sales = new(LineKind.Did, "+441144960042");
    private static readonly LineRef Support = new(LineKind.Did, "+441174960404");

    private static readonly Dictionary<string, string> Settings = new()
    {
        ["Oidc:Authority"] = FakeOidcProvider.Authority,
        ["Oidc:ClientId"] = FakeOidcProvider.ClientId,
        ["Oidc:ClientSecret"] = FakeOidcProvider.ClientSecret,
        ["Oidc:AdminGroups"] = "THS Admins",
    };

    private WebApplicationFactory<Program> Create(FakeOidcProvider provider) =>
        talkwatch.Create(new FixtureConsole(FixtureConsole.DefaultDirectory), settings: Settings,
            services: s => s.Configure<OpenIdConnectOptions>(OidcOptions.Scheme, o => o.BackchannelHttpHandler = provider));

    private static async Task<string> SignInAsync(WebApplicationFactory<Program> app, FakeOidcProvider provider, string subject, string username, params string[] groups)
    {
        using var browser = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true, BaseAddress = new Uri("https://localhost") });
        var challenge = await browser.GetAsync(new Uri("/account/signin/oidc?returnUrl=%2Fcalls", UriKind.Relative), Ct);
        var callback = await browser.GetAsync(provider.Authorise(challenge.Headers.Location!, subject, username, groups), Ct);
        var done = await browser.GetAsync(callback.Headers.Location!, Ct);
        return done.Headers.Location!.OriginalString;
    }

    private static async Task<T> AsSystemAsync<T>(WebApplicationFactory<Program> app, Func<TalkWatchDbContext, UserManager<AppUser>, Task<T>> work)
    {
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<AccessScopeHolder>().UseSystemScope();
        return await work(scope.ServiceProvider.GetRequiredService<TalkWatchDbContext>(), scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>());
    }

    private static Task<int> MapAsync(WebApplicationFactory<Program> app, string group, string? role, int order, params (LineRef Line, bool Recordings)[] lines) =>
        AsSystemAsync(app, async (db, _) =>
        {
            var site = await db.Sites.Select(s => s.Id).SingleAsync(Ct);
            var mapping = new GroupMapping { Id = Guid.NewGuid(), SiteId = site, Group = group, Role = role, Order = order };
            mapping.Lines.AddRange(lines.Select(l => new GroupMappingLine { Id = Guid.NewGuid(), Kind = l.Line.Kind, Key = l.Line.Key, AllowRecordings = l.Recordings }));
            db.GroupMappings.Add(mapping);
            await db.SaveChangesAsync(Ct);
            return 0;
        });

    private static Task<(string[] Roles, List<Grant> Grants)> AccessOfAsync(WebApplicationFactory<Program> app, string username) =>
        AsSystemAsync(app, async (db, users) =>
        {
            var user = (await users.FindByNameAsync(username))!;
            return ((await users.GetRolesAsync(user)).ToArray(), await db.Grants.Where(g => g.UserId == user.Id).OrderBy(g => g.Key).ThenBy(g => g.ByGroups).ToListAsync(Ct));
        });

    [Fact]
    public async Task A_mapped_group_gives_its_role_and_lines_and_leaving_it_takes_them_away_but_not_lines_given_by_hand()
    {
        using var provider = new FakeOidcProvider();
        await using var app = Create(provider);
        await MapAsync(app, "sales", Roles.Manager, 10, (Sales, true));
        await MapAsync(app, "support", null, 20, (Support, false));

        Assert.Equal("/calls", await SignInAsync(app, provider, "s-1", "robin", "sales"));
        var (roles, grants) = await AccessOfAsync(app, "robin");
        Assert.Equal([Roles.Manager], roles);
        var sales = Assert.Single(grants);
        Assert.Equal((Sales.Key, true, true, "sales"), (sales.Key, sales.ByGroups, sales.AllowRecordings, sales.Groups));

        // An admin also gives Robin the support line by hand; then Robin moves from sales to support.
        await AsSystemAsync(app, async (db, users) =>
        {
            var robin = (await users.FindByNameAsync("robin"))!;
            db.Grants.Add(new Grant { Id = Guid.NewGuid(), SiteId = robin.SiteId, UserId = robin.Id, Kind = Support.Kind, Key = Support.Key, AllowVoicemail = true, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(Ct);
            return 0;
        });
        Assert.Equal("/calls", await SignInAsync(app, provider, "s-1", "robin", "support"));

        (roles, grants) = await AccessOfAsync(app, "robin");
        // Support names no role, so Robin keeps Manager; sales' line went with sales; the hand-given line stays beside support's.
        Assert.Equal([Roles.Manager], roles);
        Assert.Equal([(Support.Key, false), (Support.Key, true)], grants.Select(g => (g.Key, g.ByGroups)));
        Assert.True(grants.Single(g => !g.ByGroups).AllowVoicemail);

        // Leaving every mapped group: turned away, though the account and its hand-given line remain.
        Assert.Equal("/signin?oidc=not-allowed", await SignInAsync(app, provider, "s-1", "robin", "elsewhere"));
    }

    [Fact]
    public async Task The_first_mapping_in_order_decides_the_role_lines_add_up_and_the_admin_group_always_wins()
    {
        using var provider = new FakeOidcProvider();
        await using var app = Create(provider);
        await MapAsync(app, "leads", Roles.Manager, 5, (Sales, false));
        await MapAsync(app, "everyone", Roles.Viewer, 50, (Sales, true), (Support, false));

        await SignInAsync(app, provider, "s-2", "kim", "everyone", "leads");
        var (roles, grants) = await AccessOfAsync(app, "kim");
        Assert.Equal([Roles.Manager], roles);
        // The same line from two groups is one grant, with the ticks of both.
        Assert.Equal([(Sales.Key, true, "leads,everyone"), (Support.Key, false, "everyone")], grants.Select(g => (g.Key, g.AllowRecordings, g.Groups)));

        await SignInAsync(app, provider, "s-2", "kim", "everyone", "leads", "THS Admins");
        Assert.Equal([Roles.Admin], (await AccessOfAsync(app, "kim")).Roles);

        // Out of the admin group again: the mappings decide, not "Viewer".
        await SignInAsync(app, provider, "s-2", "kim", "everyone", "leads");
        Assert.Equal([Roles.Manager], (await AccessOfAsync(app, "kim")).Roles);
    }

    [Fact]
    public async Task Group_grants_cannot_be_changed_by_hand_and_the_mappings_page_manages_mappings()
    {
        using var provider = new FakeOidcProvider();
        await using var app = Create(provider);
        await MapAsync(app, "sales", null, 10, (Sales, false));
        await SignInAsync(app, provider, "s-3", "lee", "sales");
        var (_, grants) = await AccessOfAsync(app, "lee");
        var lee = grants.Single().UserId;
        using var admin = TalkWatchApp.Browser(app);
        await TalkWatchApp.SignInAsync(admin, TalkWatchApp.AdminUsername, TalkWatchApp.AdminPassword);

        var person = await admin.GetStringAsync(new Uri($"/admin/users/{lee}", UriKind.Relative), Ct);
        var removed = await admin.PostAsync(new Uri($"/admin/users/{lee}/grants/{grants.Single().Id}/delete", UriKind.Relative),
            new FormUrlEncodedContent([new("__RequestVerificationToken", Token(person))]), Ct);
        var page = await admin.GetStringAsync(new Uri("/admin/groups", UriKind.Relative), Ct);
        var mapped = await admin.PostAsync(new Uri("/admin/groups", UriKind.Relative),
            new FormUrlEncodedContent([new("Group", "night"), new("Role", Roles.Viewer), new("Order", "30"), new("__RequestVerificationToken", Token(page))]), Ct);

        Assert.Contains("From group sales", person, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, removed.StatusCode);
        Assert.Single((await AccessOfAsync(app, "lee")).Grants);
        Assert.Contains("data-group=\"sales\"", page, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Redirect, mapped.StatusCode);
        Assert.Equal(Roles.Viewer, await AsSystemAsync(app, (db, _) => db.GroupMappings.Where(m => m.Group == "night").Select(m => m.Role).SingleAsync(Ct)));
    }

    private static string Token(string html) =>
        WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Match(html, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""").Groups[1].Value);
}
