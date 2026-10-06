using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using TalkWatch.Data;

namespace TalkWatch.Web.Tests;

/// <summary>Setting reports up is not a way to see more calls than one's own role and grants show.</summary>
public sealed partial class ReportEmailTests
{
    /// <summary>Someone whose role sets reports up and does nothing else, signed in.</summary>
    private static async Task<HttpClient> ReportDeskAsync(WebApplicationFactory<Program> app, HttpClient admin)
    {
        await PostAsync(admin, "/admin/roles", "/admin/roles", ("Name", "Report desk"));
        var role = await DbAsync(app, async db => (await db.Roles.SingleAsync(r => r.Name == "Report desk", Ct)).Id);
        await PostAsync(admin, "/admin/roles", $"/admin/roles/{role}/permissions", ("Permissions", nameof(Permission.ManageReports)));
        await PostAsync(admin, "/admin/users", "/admin/users", ("Username", "desk"), ("Role", "Report desk"), ("Password", Password));
        var desk = TalkWatchApp.Browser(app);
        await TalkWatchApp.SignInAsync(desk, "desk", Password);
        return desk;
    }

    private static int CsvRows(string csv) => csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length - 1;

    [Fact]
    public async Task A_report_with_no_recipients_run_by_hand_shows_its_runner_only_the_calls_they_may_see()
    {
        var (app, admin, _, _, _) = await StartAsync(null);
        await using var _ = app;
        using var __ = admin;
        using var desk = await ReportDeskAsync(app, admin);

        await PostAsync(desk, "/reports/new", "/reports/save", Report());
        var report = await DbAsync(app, db => db.Reports.Select(r => r.Id).SingleAsync(Ct));
        await PostAsync(desk, "/reports", $"/reports/{report}/run");
        var run = await DbAsync(app, db => db.ReportRuns.SingleAsync(Ct));
        var csv = await desk.GetStringAsync(new Uri($"/reports/runs/{run.Id}/calls.csv", UriKind.Relative), Ct);

        // The desk holds no line and cannot see every call: its copy is its own, with none in it.
        Assert.Equal(await DbAsync(app, db => db.Users.Where(u => u.UserName == "desk").Select(u => (Guid?)u.Id).SingleAsync(Ct)), run.AudienceUserId);
        Assert.Equal(0, CsvRows(csv));
    }

    [Fact]
    public async Task Someone_who_sets_reports_up_reads_no_copy_built_for_someone_who_sees_more()
    {
        var (app, admin, _, _, _) = await StartAsync(null);
        await using var _ = app;
        using var __ = admin;
        using var desk = await ReportDeskAsync(app, admin);
        var adminId = await DbAsync(app, db => db.Users.Where(u => u.UserName == TalkWatchApp.AdminUsername).Select(u => u.Id).SingleAsync(Ct));

        await PostAsync(desk, "/reports/new", "/reports/save", Report(("Users", adminId.ToString())));
        var report = await DbAsync(app, db => db.Reports.Select(r => r.Id).SingleAsync(Ct));
        await PostAsync(desk, "/reports", $"/reports/{report}/run");
        var copy = await DbAsync(app, db => db.ReportRuns.SingleAsync(r => r.AudienceUserId == adminId, Ct));
        var read = await desk.GetAsync(new Uri($"/reports/runs/{copy.Id}/calls.csv", UriKind.Relative), Ct);

        Assert.True(CsvRows(copy.Csv!) > 0, "The admin's copy should list calls.");
        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
    }

    [Fact]
    public async Task Only_someone_who_sees_every_call_sends_a_report_to_a_channel_that_belongs_to_nobody()
    {
        var (app, admin, _, _, channel) = await StartAsync(null);
        await using var _ = app;
        using var __ = admin;
        using var desk = await ReportDeskAsync(app, admin);
        // Managing every alert too, so the site's channel is one it can see and choose.
        var role = await DbAsync(app, async db => (await db.Roles.SingleAsync(r => r.Name == "Report desk", Ct)).Id);
        await PostAsync(admin, "/admin/roles", $"/admin/roles/{role}/permissions",
            ("Permissions", nameof(Permission.ManageReports)), ("Permissions", nameof(Permission.ManageAlerts)));

        await PostAsync(desk, "/reports/new", "/reports/save", Report(("Channels", channel.ToString())));

        Assert.Equal(0, await DbAsync(app, db => db.Reports.CountAsync(Ct)));
    }
}
