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

public sealed partial class PermissionTests(TalkWatchApp talkwatch) : IClassFixture<TalkWatchApp>
{
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

    private static Task<Permission> PermissionsOfAsync(WebApplicationFactory<Program> app, string role) => AsSystemAsync(app, async s =>
    {
        var roles = s.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        return await RolePermissions.GetAsync(roles, (await roles.FindByNameAsync(role))!);
    });

    private static Task<Guid> RoleIdAsync(WebApplicationFactory<Program> app, string role) =>
        AsSystemAsync(app, async s => (await s.GetRequiredService<RoleManager<IdentityRole<Guid>>>().FindByNameAsync(role))!.Id);

    /// <summary>The app with the captured calls, a few recordings and the transcripts copied, and the admin signed in.</summary>
    private async Task<(WebApplicationFactory<Program> App, HttpClient Admin)> StartAsync()
    {
        var app = talkwatch.Create(new FixtureConsole(FixtureConsole.DefaultDirectory));
        await app.Services.GetRequiredService<CallLogPoller>().RunOnceAsync(Ct);
        var admin = TalkWatchApp.Browser(app);
        await TalkWatchApp.SignInAsync(admin, TalkWatchApp.AdminUsername, TalkWatchApp.AdminPassword);
        return (app, admin);
    }

    private static async Task<HttpClient> PersonAsync(WebApplicationFactory<Program> app, HttpClient admin, string username, string role)
    {
        await PostAsync(admin, "/admin/users", "/admin/users", ("Username", username), ("Role", role), ("Password", Password));
        var browser = TalkWatchApp.Browser(app);
        await TalkWatchApp.SignInAsync(browser, username, Password);
        return browser;
    }

    [Fact]
    public async Task The_built_in_roles_start_with_what_they_always_had()
    {
        var (app, admin) = await StartAsync();
        await using var _ = app;
        using var __ = admin;

        Assert.Equal(Permission.All, await PermissionsOfAsync(app, Roles.Admin));
        Assert.Equal(Permission.Export | Permission.ApiTokens | Permission.MarkCallBacks | Permission.OwnAlerts, await PermissionsOfAsync(app, Roles.Manager));
        Assert.Equal(Permission.Export | Permission.ApiTokens | Permission.MarkCallBacks, await PermissionsOfAsync(app, Roles.Viewer));
    }

    [Fact]
    public async Task A_supervisor_role_sees_every_call_and_hears_its_audio_but_reads_no_transcript_exports_nothing_and_manages_nobody()
    {
        var (app, admin) = await StartAsync();
        await using var _ = app;
        using var __ = admin;
        await PostAsync(admin, "/admin/roles", "/admin/roles", ("Name", "Supervisor"));
        var supervisorRole = await RoleIdAsync(app, "Supervisor");
        await PostAsync(admin, "/admin/roles", $"/admin/roles/{supervisorRole}/permissions",
            ("Permissions", nameof(Permission.AllCalls)), ("Permissions", nameof(Permission.AllAudio)));
        using var supervisor = await PersonAsync(app, admin, "supervisor", "Supervisor");
        await app.Services.GetRequiredService<TranscriptSync>().SyncAsync(Ct);
        var transcribed = await AsSystemAsync(app, s => s.GetRequiredService<TalkWatchDbContext>().CallTranscripts
            .Join(s.GetRequiredService<TalkWatchDbContext>().Calls, t => t.CallId, c => c.Id, (t, c) => c.TalkUuid).FirstAsync(Ct));

        var calls = await supervisor.GetStringAsync(new Uri("/calls", UriKind.Relative), Ct);
        var adminCalls = await admin.GetStringAsync(new Uri("/calls", UriKind.Relative), Ct);
        var callPage = await supervisor.GetStringAsync(new Uri($"/calls/{transcribed}", UriKind.Relative), Ct);
        var export = await supervisor.GetAsync(new Uri("/calls/export.csv?from=2025-01-01&to=2026-12-31", UriKind.Relative), Ct);
        var people = await supervisor.GetAsync(new Uri("/admin/users", UriKind.Relative), Ct);

        // Every call, with no line granted, and the same audio players the admin has; no transcript; no export or people.
        Assert.Contains("of " + Regex.Match(adminCalls, @"of (\d+)").Groups[1].Value, calls, StringComparison.Ordinal);
        Assert.Equal(Regex.Count(adminCalls, "data-audio=\""), Regex.Count(calls, "data-audio=\""));
        Assert.True(Regex.Count(calls, "data-audio=\"") > 0, "Some recordings should have been copied.");
        Assert.Contains("data-outcome", callPage, StringComparison.Ordinal);
        Assert.DoesNotContain("data-transcript", callPage, StringComparison.Ordinal);
        // Refused as any page is that someone's role does not allow: sent to the sign-in page, no file.
        Assert.Equal(HttpStatusCode.Redirect, export.StatusCode);
        Assert.StartsWith("/signin", export.Headers.Location!.AbsolutePath, StringComparison.Ordinal);
        Assert.NotEqual(HttpStatusCode.OK, people.StatusCode);
        Assert.DoesNotContain("href=\"/admin/users\"", calls, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Taking_a_permission_from_a_built_in_role_takes_it_from_its_members()
    {
        var (app, admin) = await StartAsync();
        await using var _ = app;
        using var __ = admin;
        using var viewer = await PersonAsync(app, admin, "viewer", Roles.Viewer);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync(new Uri("/calls/export.csv?from=2026-01-01&to=2026-01-02", UriKind.Relative), Ct)).StatusCode);

        // Viewers keep marking call-backs done, but may no longer export or make tokens.
        await PostAsync(admin, "/admin/roles", $"/admin/roles/{await RoleIdAsync(app, Roles.Viewer)}/permissions", ("Permissions", nameof(Permission.MarkCallBacks)));
        using var again = TalkWatchApp.Browser(app);
        await TalkWatchApp.SignInAsync(again, "viewer", Password);

        Assert.Equal(Permission.MarkCallBacks, await PermissionsOfAsync(app, Roles.Viewer));
        var refused = await again.GetAsync(new Uri("/calls/export.csv?from=2026-01-01&to=2026-01-02", UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.Redirect, refused.StatusCode);
        Assert.StartsWith("/signin", refused.Headers.Location!.AbsolutePath, StringComparison.Ordinal);
        Assert.Contains("data-no-tokens", await again.GetStringAsync(new Uri("/account", UriKind.Relative), Ct), StringComparison.Ordinal);
        Assert.Equal(1, await AsSystemAsync(app, s => s.GetRequiredService<TalkWatchDbContext>().AuditEvents.CountAsync(e => e.Action == "role.permissions", Ct)));
    }

    [Fact]
    public async Task Unticking_every_permission_leaves_a_role_with_none()
    {
        var (app, admin) = await StartAsync();
        await using var _ = app;
        using var __ = admin;

        // Nothing ticked posts no field at all.
        var saved = await PostAsync(admin, "/admin/roles", $"/admin/roles/{await RoleIdAsync(app, Roles.Viewer)}/permissions");

        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        Assert.Equal(Permission.None, await PermissionsOfAsync(app, Roles.Viewer));
    }

    [Fact]
    public async Task Admin_cannot_be_changed_and_a_role_someone_holds_cannot_be_removed()
    {
        var (app, admin) = await StartAsync();
        await using var _ = app;
        using var __ = admin;
        await PostAsync(admin, "/admin/roles", "/admin/roles", ("Name", "Night shift"));
        using var night = await PersonAsync(app, admin, "night", "Night shift");

        await PostAsync(admin, "/admin/roles", $"/admin/roles/{await RoleIdAsync(app, Roles.Admin)}/permissions", ("Permissions", nameof(Permission.Export)));
        var refused = await PostAsync(admin, "/admin/roles", $"/admin/roles/{await RoleIdAsync(app, "Night shift")}/delete");
        var builtIn = await PostAsync(admin, "/admin/roles", $"/admin/roles/{await RoleIdAsync(app, Roles.Viewer)}/delete");

        Assert.Equal(Permission.All, await PermissionsOfAsync(app, Roles.Admin));
        Assert.Contains("another%20role%20first", refused.Headers.Location!.OriginalString, StringComparison.Ordinal);
        Assert.Contains("built%20in", builtIn.Headers.Location!.OriginalString, StringComparison.Ordinal);
        Assert.Contains("data-role=\"Night shift\"", await admin.GetStringAsync(new Uri("/admin/roles", UriKind.Relative), Ct), StringComparison.Ordinal);
    }
}
