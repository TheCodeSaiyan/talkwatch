using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TalkWatch.Data;
using TalkWatch.Replay;
using TalkWatch.Web.Services;

namespace TalkWatch.Web.Tests;

public sealed partial class RetentionPageTests(TalkWatchApp talkwatch) : IClassFixture<TalkWatchApp>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [GeneratedRegex(@"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""")]
    private static partial Regex Token();

    private static async Task<HttpResponseMessage> PostAsync(HttpClient browser, string action, params (string Name, string Value)[] fields)
    {
        var html = await browser.GetStringAsync(new Uri("/admin/retention", UriKind.Relative), Ct);
        var form = fields.Select(f => new KeyValuePair<string, string>(f.Name, f.Value))
            .Append(new("__RequestVerificationToken", WebUtility.HtmlDecode(Token().Match(html).Groups[1].Value)));
        using var content = new FormUrlEncodedContent(form);
        return await browser.PostAsync(new Uri(action, UriKind.Relative), content);
    }

    private static async Task<T> AsSystemAsync<T>(WebApplicationFactory<Program> app, Func<TalkWatchDbContext, Task<T>> work)
    {
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<AccessScopeHolder>().UseSystemScope();
        return await work(scope.ServiceProvider.GetRequiredService<TalkWatchDbContext>());
    }

    private async Task<(WebApplicationFactory<Program> App, HttpClient Admin)> StartAsync()
    {
        var app = talkwatch.Create(new FixtureConsole(FixtureConsole.DefaultDirectory));
        await app.Services.GetRequiredService<CallLogPoller>().RunOnceAsync(Ct);
        var admin = TalkWatchApp.Browser(app);
        await TalkWatchApp.SignInAsync(admin, TalkWatchApp.AdminUsername, TalkWatchApp.AdminPassword);
        return (app, admin);
    }

    [Fact]
    public async Task By_default_everything_is_kept_and_the_page_says_so()
    {
        var (app, admin) = await StartAsync();
        await using var _ = app;
        using var __ = admin;

        var page = await admin.GetStringAsync(new Uri("/admin/retention", UriKind.Relative), Ct);
        var swept = await PostAsync(admin, "/admin/retention/sweep");

        Assert.Contains("data-policy=\"KeepEverything\"", page, StringComparison.Ordinal);
        Assert.Contains("Swept%3A%200%20calls", swept.Headers.Location!.OriginalString, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_preset_brings_its_own_periods_and_a_sweep_now_applies_it()
    {
        var (app, admin) = await StartAsync();
        await using var _ = app;
        using var __ = admin;

        await PostAsync(admin, "/admin/retention", ("Preset", "Fca"), ("CallDays", "1"));
        var settings = await AsSystemAsync(app, db => db.RetentionSettings.SingleAsync(Ct));
        await PostAsync(admin, "/admin/retention/sweep");

        Assert.Equal(Retention.Of(RetentionPreset.Fca), (settings.CallDays, settings.AudioDays, settings.MinimumDays));
        Assert.True(await AsSystemAsync(app, db => db.Calls.AnyAsync(Ct)));
        Assert.Equal(2, await AsSystemAsync(app, db => db.AuditEvents.CountAsync(e => e.Action.StartsWith("retention."), Ct)));
    }

    [Fact]
    public async Task A_custom_policy_shorter_than_its_minimum_is_refused()
    {
        var (app, admin) = await StartAsync();
        await using var _ = app;
        using var __ = admin;

        var refused = await PostAsync(admin, "/admin/retention", ("Preset", "Custom"), ("CallDays", "30"), ("MinimumDays", "60"));

        Assert.Contains("minimum", Uri.UnescapeDataString(refused.Headers.Location!.OriginalString), StringComparison.Ordinal);
        Assert.False(await AsSystemAsync(app, db => db.RetentionSettings.AnyAsync(Ct)));
    }

    [Fact]
    public async Task Check_audio_reads_every_copied_file_and_reports()
    {
        var (app, admin) = await StartAsync();
        await using var _ = app;
        using var __ = admin;
        var copied = await AsSystemAsync(app, db => db.AudioFiles.CountAsync(a => a.State == AudioState.Copied, Ct));

        var checkedAll = await PostAsync(admin, "/admin/retention/check-audio");

        Assert.True(copied > 0, "The first poll should have copied recordings.");
        Assert.Contains($"All {copied} copied files are present", Uri.UnescapeDataString(checkedAll.Headers.Location!.OriginalString), StringComparison.Ordinal);
        Assert.Equal(1, await AsSystemAsync(app, db => db.AuditEvents.CountAsync(e => e.Action == "audio.check", Ct)));
    }

    [Fact]
    public async Task Someone_who_is_not_an_admin_cannot_see_or_change_retention()
    {
        var (app, admin) = await StartAsync();
        await using var _ = app;
        using var __ = admin;
        using (var scope = app.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<AccessScopeHolder>().UseSystemScope();
            var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<AppUser>>();
            var viewer = new AppUser { Id = Guid.NewGuid(), UserName = "viewer", SiteId = scope.ServiceProvider.GetRequiredService<CurrentSite>().Id };
            Assert.True((await users.CreateAsync(viewer, "a long enough password")).Succeeded);
            await users.AddToRoleAsync(viewer, Roles.Viewer);
        }

        using var browser = TalkWatchApp.Browser(app);
        await TalkWatchApp.SignInAsync(browser, "viewer", "a long enough password");
        var page = await browser.GetAsync(new Uri("/admin/retention", UriKind.Relative), Ct);

        Assert.NotEqual(HttpStatusCode.OK, page.StatusCode);
    }
}
