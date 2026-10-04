using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TalkWatch.Data;
using TalkWatch.Replay;
using TalkWatch.Web.Services;

namespace TalkWatch.Web.Tests;

/// <summary>A change to someone's role, or to a role's permissions, holds from their next page, and does not sign them out.</summary>
public sealed partial class LiveRoleTests(TalkWatchApp talkwatch) : IClassFixture<TalkWatchApp>
{
    private const string Password = "a long enough password";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<HttpResponseMessage> PostAsync(HttpClient browser, string page, string action, params (string Name, string Value)[] fields)
    {
        var html = await browser.GetStringAsync(new Uri(page, UriKind.Relative), Ct);
        using var content = new FormUrlEncodedContent(fields.Select(f => new KeyValuePair<string, string>(f.Name, f.Value))
            .Append(new("__RequestVerificationToken", WebUtility.HtmlDecode(Token().Match(html).Groups[1].Value))));
        return await browser.PostAsync(new Uri(action, UriKind.Relative), content, Ct);
    }

    [Fact]
    public async Task A_new_role_and_new_permissions_hold_from_the_next_page_without_signing_anyone_out()
    {
        await using var app = talkwatch.Create(new FixtureConsole(FixtureConsole.DefaultDirectory));
        using var admin = TalkWatchApp.Browser(app);
        await TalkWatchApp.SignInAsync(admin, TalkWatchApp.AdminUsername, TalkWatchApp.AdminPassword);
        var created = await PostAsync(admin, "/admin/users", "/admin/users", ("Username", "riley"), ("Role", Roles.Viewer), ("Password", Password));
        var riley = created.Headers.Location!.OriginalString.Split('/').Last().Split('?')[0];
        using var browser = TalkWatchApp.Browser(app);
        await TalkWatchApp.SignInAsync(browser, "riley", Password);
        Assert.NotEqual(HttpStatusCode.OK, (await browser.GetAsync(new Uri("/reports/new", UriKind.Relative), Ct)).StatusCode);

        // The Viewer role gains Set up reports: Riley can open the report editor on the very next request.
        using (var scope = app.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<AccessScopeHolder>().UseSystemScope();
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
            var viewer = (await roles.FindByNameAsync(Roles.Viewer))!;
            await RolePermissions.ChangeAsync(roles, viewer, await RolePermissions.GetAsync(roles, viewer) | Permission.ManageReports);
        }

        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync(new Uri("/reports/new", UriKind.Relative), Ct)).StatusCode);

        // Made an Admin by hand: the people pages open at once, and they are still signed in.
        await PostAsync(admin, $"/admin/users/{riley}", $"/admin/users/{riley}/role", ("Role", Roles.Admin));

        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync(new Uri("/admin/users", UriKind.Relative), Ct)).StatusCode);
        Assert.Contains("<title>Calls", await browser.GetStringAsync(new Uri("/calls", UriKind.Relative), Ct), StringComparison.Ordinal);
    }

    [GeneratedRegex(@"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""")]
    private static partial Regex Token();
}
