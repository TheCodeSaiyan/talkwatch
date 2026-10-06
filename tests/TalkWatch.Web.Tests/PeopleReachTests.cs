using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TalkWatch.Data;
using TalkWatch.Replay;
using TalkWatch.Web.Services;

namespace TalkWatch.Web.Tests;

/// <summary>
/// Someone who manages people without being an admin hands out no more than they hold: not the Admin role, not a
/// permission they lack, not a line they cannot see, and not the keys to an account that reaches further than theirs.
/// </summary>
public sealed partial class PeopleReachTests(TalkWatchApp talkwatch) : IClassFixture<TalkWatchApp>
{
    private const string Did = "+441144960042";
    private const string Password = "a long enough password";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [GeneratedRegex(@"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""")]
    private static partial Regex Token();

    private static async Task<HttpResponseMessage> PostAsync(HttpClient browser, string page, string action, params (string Name, string Value)[] fields)
    {
        var html = await browser.GetStringAsync(new Uri(page, UriKind.Relative), Ct);
        using var content = new FormUrlEncodedContent(fields.Select(f => new KeyValuePair<string, string>(f.Name, f.Value))
            .Append(new("__RequestVerificationToken", WebUtility.HtmlDecode(Token().Match(html).Groups[1].Value))));
        return await browser.PostAsync(new Uri(action, UriKind.Relative), content, Ct);
    }

    private static async Task<T> AsSystemAsync<T>(WebApplicationFactory<Program> app, Func<IServiceProvider, Task<T>> work)
    {
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<AccessScopeHolder>().UseSystemScope();
        return await work(scope.ServiceProvider);
    }

    private static Task<Guid> UserIdAsync(WebApplicationFactory<Program> app, string username) =>
        AsSystemAsync(app, async s => (await s.GetRequiredService<UserManager<AppUser>>().FindByNameAsync(username))!.Id);

    private static Task<Guid> RoleIdAsync(WebApplicationFactory<Program> app, string role) =>
        AsSystemAsync(app, async s => (await s.GetRequiredService<RoleManager<IdentityRole<Guid>>>().FindByNameAsync(role))!.Id);

    private static Task<IList<string>> RolesOfAsync(WebApplicationFactory<Program> app, string username) => AsSystemAsync(app, async s =>
    {
        var users = s.GetRequiredService<UserManager<AppUser>>();
        return await users.GetRolesAsync((await users.FindByNameAsync(username))!);
    });

    private static Task<Permission> PermissionsOfAsync(WebApplicationFactory<Program> app, string role) => AsSystemAsync(app, async s =>
    {
        var roles = s.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        return await RolePermissions.GetAsync(roles, (await roles.FindByNameAsync(role))!);
    });

    /// <summary>The app, its admin, and "desk": someone whose role lets them manage people and nothing else.</summary>
    private async Task<(WebApplicationFactory<Program> App, HttpClient Admin, HttpClient Desk)> StartAsync()
    {
        var app = talkwatch.Create(new FixtureConsole(FixtureConsole.DefaultDirectory));
        await app.Services.GetRequiredService<CallLogPoller>().RunOnceAsync(Ct);
        var admin = TalkWatchApp.Browser(app);
        await TalkWatchApp.SignInAsync(admin, TalkWatchApp.AdminUsername, TalkWatchApp.AdminPassword);
        await PostAsync(admin, "/admin/roles", "/admin/roles", ("Name", "People desk"));
        await PostAsync(admin, "/admin/roles", $"/admin/roles/{await RoleIdAsync(app, "People desk")}/permissions", ("Permissions", nameof(Permission.ManagePeople)));
        await PostAsync(admin, "/admin/users", "/admin/users", ("Username", "desk"), ("Role", "People desk"), ("Password", Password));
        var desk = TalkWatchApp.Browser(app);
        await TalkWatchApp.SignInAsync(desk, "desk", Password);
        return (app, admin, desk);
    }

    [Fact]
    public async Task They_cannot_make_anyone_an_admin()
    {
        var (app, admin, desk) = await StartAsync();
        await using var _ = app;
        using var __ = admin;
        using var ___ = desk;
        var me = await UserIdAsync(app, "desk");

        var self = await PostAsync(desk, $"/admin/users/{me}", $"/admin/users/{me}/role", ("Role", Roles.Admin));
        await PostAsync(desk, "/admin/users", "/admin/users", ("Username", "puppet"), ("Role", Roles.Admin), ("Password", Password));
        await PostAsync(desk, "/admin/groups", "/admin/groups", ("Group", "desk-group"), ("Role", Roles.Admin));

        Assert.Equal(["People desk"], await RolesOfAsync(app, "desk"));
        Assert.Contains("admin", Uri.UnescapeDataString(self.Headers.Location!.OriginalString), StringComparison.OrdinalIgnoreCase);
        Assert.False(await AsSystemAsync(app, s => s.GetRequiredService<UserManager<AppUser>>().Users.AnyAsync(u => u.UserName == "puppet", Ct)));
        Assert.False(await AsSystemAsync(app, s => s.GetRequiredService<TalkWatchDbContext>().GroupMappings.AnyAsync(m => m.Group == "desk-group", Ct)));
    }

    [Fact]
    public async Task They_cannot_give_a_role_a_permission_they_do_not_hold()
    {
        var (app, admin, desk) = await StartAsync();
        await using var _ = app;
        using var __ = admin;
        using var ___ = desk;

        await PostAsync(desk, "/admin/roles", $"/admin/roles/{await RoleIdAsync(app, "People desk")}/permissions",
            ("Permissions", nameof(Permission.ManagePeople)), ("Permissions", nameof(Permission.AllCalls)), ("Permissions", nameof(Permission.AllAudio)));
        await PostAsync(desk, "/admin/roles", $"/admin/roles/{await RoleIdAsync(app, Roles.Viewer)}/permissions", ("Permissions", nameof(Permission.ReadAudit)));

        Assert.Equal(Permission.ManagePeople, await PermissionsOfAsync(app, "People desk"));
        Assert.Equal(Permission.Export | Permission.ApiTokens | Permission.MarkCallBacks, await PermissionsOfAsync(app, Roles.Viewer));
    }

    [Fact]
    public async Task They_cannot_take_over_an_admin_or_anyone_who_reaches_further()
    {
        var (app, admin, desk) = await StartAsync();
        await using var _ = app;
        using var __ = admin;
        using var ___ = desk;
        var adminId = await UserIdAsync(app, TalkWatchApp.AdminUsername);
        await PostAsync(admin, "/admin/users", "/admin/users", ("Username", "manager"), ("Role", Roles.Manager), ("Password", Password));
        var managerId = await UserIdAsync(app, "manager");

        await PostAsync(desk, $"/admin/users/{adminId}", $"/admin/users/{adminId}/password", ("Password", "the desk's own choice"));
        await PostAsync(desk, $"/admin/users/{adminId}", $"/admin/users/{adminId}/lock");
        await PostAsync(desk, $"/admin/users/{managerId}", $"/admin/users/{managerId}/password", ("Password", "the desk's own choice"));

        using var tryAdmin = TalkWatchApp.Browser(app);
        using var tryManager = TalkWatchApp.Browser(app);
        Assert.DoesNotContain("/signin", (await TalkWatchApp.SignInAsync(tryAdmin, TalkWatchApp.AdminUsername, TalkWatchApp.AdminPassword)).Headers.Location!.OriginalString, StringComparison.Ordinal);
        Assert.DoesNotContain("/signin", (await TalkWatchApp.SignInAsync(tryManager, "manager", Password)).Headers.Location!.OriginalString, StringComparison.Ordinal);
    }

    [Fact]
    public async Task They_cannot_grant_a_line_they_cannot_see_themselves()
    {
        var (app, admin, desk) = await StartAsync();
        await using var _ = app;
        using var __ = admin;
        using var ___ = desk;
        var me = await UserIdAsync(app, "desk");

        await PostAsync(desk, $"/admin/users/{me}", $"/admin/users/{me}/grants", ("Line", $"Did:{Did}"), ("AllowRecordings", "true"));

        Assert.False(await AsSystemAsync(app, s => s.GetRequiredService<TalkWatchDbContext>().Grants.AnyAsync(g => g.UserId == me, Ct)));
    }

    [Fact]
    public async Task They_still_add_people_within_their_reach_and_set_their_passwords()
    {
        var (app, admin, desk) = await StartAsync();
        await using var _ = app;
        using var __ = admin;
        using var ___ = desk;
        // Viewers hold nothing the desk lacks once the desk's role also exports, makes tokens and marks call-backs.
        await PostAsync(admin, "/admin/roles", $"/admin/roles/{await RoleIdAsync(app, "People desk")}/permissions",
            ("Permissions", nameof(Permission.ManagePeople)), ("Permissions", nameof(Permission.Export)),
            ("Permissions", nameof(Permission.ApiTokens)), ("Permissions", nameof(Permission.MarkCallBacks)));

        await PostAsync(desk, "/admin/users", "/admin/users", ("Username", "newcomer"), ("Role", Roles.Viewer), ("Password", Password));
        var newcomer = await UserIdAsync(app, "newcomer");
        await PostAsync(desk, $"/admin/users/{newcomer}", $"/admin/users/{newcomer}/password", ("Password", "a different long password"));

        using var signIn = TalkWatchApp.Browser(app);
        Assert.Equal([Roles.Viewer], await RolesOfAsync(app, "newcomer"));
        Assert.DoesNotContain("/signin", (await TalkWatchApp.SignInAsync(signIn, "newcomer", "a different long password")).Headers.Location!.OriginalString, StringComparison.Ordinal);
    }
}
