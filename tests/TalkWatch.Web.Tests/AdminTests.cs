using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TalkWatch.Core.Calls;
using TalkWatch.Data;
using TalkWatch.Replay;
using TalkWatch.Web.Services;

namespace TalkWatch.Web.Tests;

public sealed partial class AdminTests(TalkWatchApp talkwatch) : IClassFixture<TalkWatchApp>
{
    private const string Did = "+441144960042";
    private const string ViewerPassword = "a long enough password";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [GeneratedRegex(@"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""")]
    private static partial Regex Token();

    /// <summary>Posts a form the way a browser would: the antiforgery token comes from a page the person can see.</summary>
    private static async Task<HttpResponseMessage> PostAsync(HttpClient browser, string page, string action, params (string Name, string Value)[] fields)
    {
        var html = await browser.GetStringAsync(new Uri(page, UriKind.Relative), Ct);
        var form = fields.Select(f => new KeyValuePair<string, string>(f.Name, f.Value))
            .Append(new("__RequestVerificationToken", WebUtility.HtmlDecode(Token().Match(html).Groups[1].Value)));
        using var content = new FormUrlEncodedContent(form);
        return await browser.PostAsync(new Uri(action, UriKind.Relative), content);
    }

    private static async Task<T> AsSystemAsync<T>(WebApplicationFactory<Program> app, Func<TalkWatchDbContext, UserManager<AppUser>, Task<T>> work)
    {
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<AccessScopeHolder>().UseSystemScope();
        return await work(scope.ServiceProvider.GetRequiredService<TalkWatchDbContext>(), scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>());
    }

    private static async Task<(WebApplicationFactory<Program> App, HttpClient Admin)> AdminAsync(TalkWatchApp talkwatch)
    {
        var app = talkwatch.Create(new FixtureConsole(FixtureConsole.DefaultDirectory));
        await app.Services.GetRequiredService<CallLogPoller>().RunOnceAsync(Ct);
        var admin = TalkWatchApp.Browser(app);
        await TalkWatchApp.SignInAsync(admin, TalkWatchApp.AdminUsername, TalkWatchApp.AdminPassword);
        return (app, admin);
    }

    private static async Task<Guid> CreateViewerAsync(HttpClient admin, string username = "viewer")
    {
        var created = await PostAsync(admin, "/admin/users", "/admin/users", ("Username", username), ("Role", Roles.Viewer), ("Password", ViewerPassword));
        return Guid.Parse(created.Headers.Location!.OriginalString.Split('/').Last().Split('?')[0]);
    }

    [Fact]
    public async Task Someone_who_is_not_an_admin_cannot_reach_the_admin_pages_or_endpoints()
    {
        var (app, admin) = await AdminAsync(talkwatch);
        await using var _ = app;
        await CreateViewerAsync(admin);
        using var viewer = TalkWatchApp.Browser(app);
        await TalkWatchApp.SignInAsync(viewer, "viewer", ViewerPassword);

        var page = await viewer.GetAsync(new Uri("/admin/users", UriKind.Relative), Ct);
        var post = await PostAsync(viewer, "/calls", "/admin/users", ("Username", "sneaky"), ("Role", Roles.Admin), ("Password", ViewerPassword));

        Assert.NotEqual(HttpStatusCode.OK, page.StatusCode);
        Assert.DoesNotContain("/admin/users/", post.Headers.Location?.OriginalString ?? "", StringComparison.Ordinal);
        Assert.False(await AsSystemAsync(app, (_, users) => users.Users.AnyAsync(u => u.UserName == "sneaky", Ct)));
    }

    [Fact]
    public async Task An_admin_adds_a_viewer_grants_a_number_and_the_viewer_sees_exactly_its_calls()
    {
        var (app, admin) = await AdminAsync(talkwatch);
        await using var _ = app;

        var id = await CreateViewerAsync(admin);
        await PostAsync(admin, $"/admin/users/{id}", $"/admin/users/{id}/grants", ("Line", $"Did:{Did}"), ("AllowRecordings", "true"));

        using var viewer = TalkWatchApp.Browser(app);
        await TalkWatchApp.SignInAsync(viewer, "viewer", ViewerPassword);
        var calls = await viewer.GetStringAsync(new Uri("/calls", UriKind.Relative), Ct);
        var expected = await AsSystemAsync(app, (db, _) => db.CallLines.Where(l => l.Kind == LineKind.Did && l.Key == Did).Select(l => l.CallId).Distinct().CountAsync(Ct));
        var grant = await AsSystemAsync(app, (db, _) => db.Grants.SingleAsync(g => g.UserId == id, Ct));

        Assert.True(await AsSystemAsync(app, (_, users) => users.IsInRoleAsync(users.Users.Single(u => u.Id == id), Roles.Viewer)));
        Assert.True(grant.AllowRecordings);
        Assert.False(grant.AllowVoicemail);
        Assert.Equal(Math.Min(50, expected), TalkWatchApp.CallRows(calls));
        Assert.Equal(2, await AsSystemAsync(app, (db, _) => db.AuditEvents.CountAsync(e => e.Action == "user.create" || e.Action == "grant.add", Ct)));
    }

    [Fact]
    public async Task Changing_and_removing_a_grant_takes_effect()
    {
        var (app, admin) = await AdminAsync(talkwatch);
        await using var _ = app;
        var id = await CreateViewerAsync(admin);
        await PostAsync(admin, $"/admin/users/{id}", $"/admin/users/{id}/grants", ("Line", $"Did:{Did}"));
        var grantId = await AsSystemAsync(app, (db, _) => db.Grants.Where(g => g.UserId == id).Select(g => g.Id).SingleAsync(Ct));

        await PostAsync(admin, $"/admin/users/{id}", $"/admin/users/{id}/grants/{grantId}", ("AllowVoicemail", "true"));
        Assert.True(await AsSystemAsync(app, (db, _) => db.Grants.Where(g => g.Id == grantId).Select(g => g.AllowVoicemail).SingleAsync(Ct)));

        await PostAsync(admin, $"/admin/users/{id}", $"/admin/users/{id}/grants/{grantId}/delete");
        using var viewer = TalkWatchApp.Browser(app);
        await TalkWatchApp.SignInAsync(viewer, "viewer", ViewerPassword);

        Assert.Equal(0, TalkWatchApp.CallRows(await viewer.GetStringAsync(new Uri("/calls", UriKind.Relative), Ct)));
        Assert.Equal(1, await AsSystemAsync(app, (db, _) => db.AuditEvents.CountAsync(e => e.Action == "grant.remove", Ct)));
    }

    [Fact]
    public async Task The_last_admin_cannot_be_demoted_and_nobody_can_lock_themselves()
    {
        var (app, admin) = await AdminAsync(talkwatch);
        await using var _ = app;
        var self = await AsSystemAsync(app, (_, users) => users.Users.Where(u => u.UserName == TalkWatchApp.AdminUsername).Select(u => u.Id).SingleAsync(Ct));

        var demote = await PostAsync(admin, $"/admin/users/{self}", $"/admin/users/{self}/role", ("Role", Roles.Viewer));
        var locked = await PostAsync(admin, $"/admin/users/{self}", $"/admin/users/{self}/lock");

        Assert.Contains("msg=", demote.Headers.Location!.OriginalString, StringComparison.Ordinal);
        Assert.True(await AsSystemAsync(app, (_, users) => users.IsInRoleAsync(users.Users.Single(u => u.Id == self), Roles.Admin)));
        Assert.Contains("msg=", locked.Headers.Location!.OriginalString, StringComparison.Ordinal);
        Assert.Null(await AsSystemAsync(app, (_, users) => users.Users.Where(u => u.Id == self).Select(u => u.LockoutEnd).SingleAsync(Ct)));
    }

    [Fact]
    public async Task A_locked_account_cannot_sign_in_and_unlocking_or_a_new_password_restores_it()
    {
        var (app, admin) = await AdminAsync(talkwatch);
        await using var _ = app;
        var id = await CreateViewerAsync(admin);

        await PostAsync(admin, $"/admin/users/{id}", $"/admin/users/{id}/lock");
        using (var viewer = TalkWatchApp.Browser(app))
        {
            var refused = await TalkWatchApp.SignInAsync(viewer, "viewer", ViewerPassword);
            Assert.StartsWith("/signin?failed=", refused.Headers.Location!.OriginalString, StringComparison.Ordinal);
        }

        await PostAsync(admin, $"/admin/users/{id}", $"/admin/users/{id}/password", ("Password", "another long password"));
        using (var viewer = TalkWatchApp.Browser(app))
        {
            var allowed = await TalkWatchApp.SignInAsync(viewer, "viewer", "another long password");
            Assert.Equal("/live", allowed.Headers.Location!.OriginalString);
        }
    }

    [Fact]
    public async Task The_page_offers_lines_by_name_from_the_directory()
    {
        var (app, admin) = await AdminAsync(talkwatch);
        await using var _ = app;
        var id = await CreateViewerAsync(admin);

        var page = WebUtility.HtmlDecode(await admin.GetStringAsync(new Uri($"/admin/users/{id}", UriKind.Relative), Ct));
        var lines = await AsSystemAsync(app, (db, _) => db.Lines.ToListAsync(Ct));

        Assert.All(lines, l => Assert.Contains($"value=\"{l.Kind}:{l.Key}\"", page, StringComparison.Ordinal));
    }
}
