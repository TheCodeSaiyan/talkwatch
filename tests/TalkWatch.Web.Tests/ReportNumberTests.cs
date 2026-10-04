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

/// <summary>
/// Reports set up by number: a report covers chosen numbers, each copy showing only their calls; and someone whose role on
/// a number lets them set up reports does so for that number only.
/// </summary>
public sealed partial class ReportNumberTests(TalkWatchApp talkwatch) : IClassFixture<TalkWatchApp>
{
    private const string Support = "+441174960404";
    private const string Main = "+441144960042";
    private const string Password = "a long enough password";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // A call in on the support number a day ago: in the capture's last months every call came in on the main line, so
    // these are what put both numbers in a report's period.
    private static string OnSupport(string uuid, int hoursAgo)
    {
        var at = DateTimeOffset.UtcNow.AddHours(-hoursAgo).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture);
        return $$$"""
            {"uuid": "{{{uuid}}}", "time": "{{{at}}}", "direction": "in", "status": "missed", "duration": 0, "from": "+447700900774", "to": "{{{Support}}}", "to_smart_attendant_id": "15",
             "call_events": [{"time": "{{{at}}}", "event": "call_started", "event_data": {"to_smart_attendant_id": 15}}, {"time": "{{{at}}}", "event": "call_hangup", "event_data": {"hangup_cause": "normal_end"}}]}
            """;
    }

    private static async Task<WebApplicationFactory<Program>> ReadyAsync(TalkWatchApp talkwatch)
    {
        var console = new FixtureConsole(FixtureConsole.DefaultDirectory);
        console.ShiftTimes(DateTimeOffset.UtcNow.AddDays(-2));
        console.AddCall(OnSupport(Guid.NewGuid().ToString(), 20));
        console.AddCall(OnSupport(Guid.NewGuid().ToString(), 26));
        var app = talkwatch.Create(console);
        await app.Services.GetRequiredService<LineDirectorySync>().RefreshAsync(Ct);
        await app.Services.GetRequiredService<CallLogPoller>().RunOnceAsync(Ct);
        return app;
    }

    private static async Task<T> DbAsync<T>(WebApplicationFactory<Program> app, Func<TalkWatchDbContext, IServiceProvider, Task<T>> work)
    {
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<AccessScopeHolder>().UseSystemScope();
        return await work(scope.ServiceProvider.GetRequiredService<TalkWatchDbContext>(), scope.ServiceProvider);
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient browser, string page, string action, params (string Name, string Value)[] fields)
    {
        var html = await browser.GetStringAsync(new Uri(page, UriKind.Relative), Ct);
        using var content = new FormUrlEncodedContent(fields.Select(f => new KeyValuePair<string, string>(f.Name, f.Value))
            .Append(new("__RequestVerificationToken", WebUtility.HtmlDecode(Token().Match(html).Groups[1].Value))));
        return await browser.PostAsync(new Uri(action, UriKind.Relative), content, Ct);
    }

    private static (string, string)[] Report(string name, Guid reader, params string[] numbers) =>
    [
        ("Name", name), ("Schedule", "Cron"), ("Cron", "0 7 * * *"), ("Period", "Days"), ("PeriodDays", "92"), ("Enabled", "true"),
        ("Sections", "Figures"), ("Sections", "CallList"), ("Users", reader.ToString()),
        .. numbers.Select(n => ("Numbers", n)),
    ];

    private static int Rows(string? csv) => csv!.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length - 1;

    [Fact]
    public async Task A_report_on_a_number_shows_in_every_copy_only_that_numbers_calls()
    {
        await using var app = await ReadyAsync(talkwatch);
        using var admin = TalkWatchApp.Browser(app);
        await TalkWatchApp.SignInAsync(admin, TalkWatchApp.AdminUsername, TalkWatchApp.AdminPassword);
        var adminId = await DbAsync(app, (db, _) => db.Users.Where(u => u.UserName == TalkWatchApp.AdminUsername).Select(u => u.Id).SingleAsync(Ct));

        await PostAsync(admin, "/reports/new", "/reports/save", Report("Support only", adminId, Support));
        var report = await DbAsync(app, (db, _) => db.Reports.Select(r => r.Id).SingleAsync(Ct));
        await PostAsync(admin, "/reports", $"/reports/{report}/run");

        var run = await DbAsync(app, (db, _) => db.ReportRuns.SingleAsync(Ct));
        // What the number covers, worked out from the call lines and the routing directly, within the copy's period.
        var covered = await DbAsync(app, async (db, _) =>
        {
            var routes = (await db.NumberRoutes.Where(r => r.Did == Support).ToListAsync(Ct)).Select(r => (r.Kind, r.Key)).ToHashSet();
            routes.Add((LineKind.Did, Support));
            var lines = await db.CallLines.Select(l => new { l.CallId, l.Kind, l.Key }).ToListAsync(Ct);
            var ids = lines.Where(l => routes.Contains((l.Kind, l.Key))).Select(l => l.CallId).ToHashSet();
            return await db.Calls.CountAsync(c => ids.Contains(c.Id) && c.Time >= run.From && c.Time < run.To, Ct);
        });
        var everything = await DbAsync(app, (db, _) => db.Calls.CountAsync(c => c.Time >= run.From && c.Time < run.To, Ct));

        Assert.InRange(covered, 1, everything - 1);
        Assert.Equal(covered, Rows(run.Csv));
        Assert.Contains("data-report-numbers", run.Html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Someone_who_sets_up_reports_on_a_number_does_so_for_that_number_only_and_sees_only_those()
    {
        await using var app = await ReadyAsync(talkwatch);
        using var admin = TalkWatchApp.Browser(app);
        await TalkWatchApp.SignInAsync(admin, TalkWatchApp.AdminUsername, TalkWatchApp.AdminPassword);
        var adminId = await DbAsync(app, (db, _) => db.Users.Where(u => u.UserName == TalkWatchApp.AdminUsername).Select(u => u.Id).SingleAsync(Ct));
        var lead = await DbAsync(app, async (db, sp) =>
        {
            var users = sp.GetRequiredService<UserManager<AppUser>>();
            var roles = sp.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
            var nothing = new IdentityRole<Guid>("Nothing site-wide");
            await roles.CreateAsync(nothing);
            await RolePermissions.ChangeAsync(roles, nothing, Permission.None);
            var reporter = new IdentityRole<Guid>("Number reporter");
            await roles.CreateAsync(reporter);
            await RolePermissions.ChangeAsync(roles, reporter, Permission.ManageReports);
            var person = new AppUser { Id = Guid.NewGuid(), UserName = "lead", SiteId = sp.GetRequiredService<CurrentSite>().Id };
            await users.CreateAsync(person, Password);
            await users.AddToRoleAsync(person, nothing.Name!);
            db.NumberRoles.Add(new NumberRole { Id = Guid.NewGuid(), SiteId = person.SiteId, UserId = person.Id, Did = Support, RoleId = reporter.Id, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(Ct);
            return person.Id;
        });
        await PostAsync(admin, "/reports/new", "/reports/save", Report("Everything", adminId));
        using var browser = TalkWatchApp.Browser(app);
        await TalkWatchApp.SignInAsync(browser, "lead", Password);

        var everyNumber = await PostAsync(browser, "/reports/new", "/reports/save", Report("All of it", lead));
        var notTheirs = await PostAsync(browser, "/reports/new", "/reports/save", Report("Main line", lead, Main));
        var theirs = await PostAsync(browser, "/reports/new", "/reports/save", Report("Support weekly", lead, Support));

        Assert.Contains("numbers%20you%20set%20up%20reports%20for", everyNumber.Headers.Location!.OriginalString, StringComparison.Ordinal);
        Assert.Contains("numbers%20you%20set%20up%20reports%20for", notTheirs.Headers.Location!.OriginalString, StringComparison.Ordinal);
        Assert.Contains("saved", theirs.Headers.Location!.OriginalString, StringComparison.Ordinal);
        var names = await DbAsync(app, (db, _) => db.Reports.Select(r => r.Name).ToListAsync(Ct));
        Assert.Equal(new HashSet<string> { "Everything", "Support weekly" }, names.ToHashSet());

        // Their list shows the report on their number, not the site-wide one, which they cannot open to edit either.
        var list = await browser.GetStringAsync(new Uri("/reports", UriKind.Relative), Ct);
        Assert.Contains("Support weekly", list, StringComparison.Ordinal);
        Assert.DoesNotContain("Everything", list, StringComparison.Ordinal);
        var everything = await DbAsync(app, (db, _) => db.Reports.Where(r => r.Name == "Everything").Select(r => r.Id).SingleAsync(Ct));
        var edit = await browser.GetStringAsync(new Uri($"/reports/{everything}/edit", UriKind.Relative), Ct);
        Assert.DoesNotContain("value=\"Everything\"", edit, StringComparison.Ordinal);
    }

    [GeneratedRegex(@"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""")]
    private static partial Regex Token();
}
