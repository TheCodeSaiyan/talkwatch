using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TalkWatch.Core.Calls;
using TalkWatch.Data;
using TalkWatch.Replay;
using TalkWatch.Web.Services;

namespace TalkWatch.Web.Tests;

public sealed partial class ReportTests(TalkWatchApp talkwatch) : IClassFixture<TalkWatchApp>
{
    private const string Password = "a long enough password";
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateTimeOffset At(string iso) => DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture);

    [Theory]
    // Daily at 07:00: next is today's if it has not passed, and it covers yesterday.
    [InlineData("Daily", "2026-09-30T05:00:00Z", "2026-09-30T06:00:00Z", "2026-09-28T23:00:00Z", "2026-09-29T23:00:00Z")]
    // Weekly on Monday at 07:00, asked on a Wednesday: the next Monday, covering the 7 days before it.
    [InlineData("Weekly", "2026-09-30T12:00:00Z", "2026-10-05T06:00:00Z", "2026-09-27T23:00:00Z", "2026-10-04T23:00:00Z")]
    // Monthly: the 1st, covering September; and across the clocks going back on 25 October, still at 07:00 local.
    [InlineData("Monthly", "2026-09-30T12:00:00Z", "2026-10-01T06:00:00Z", "2026-08-31T23:00:00Z", "2026-09-30T23:00:00Z")]
    [InlineData("Monthly", "2026-10-02T12:00:00Z", "2026-11-01T07:00:00Z", "2026-09-30T23:00:00Z", "2026-11-01T00:00:00Z")]
    public void Each_schedule_runs_at_its_time_and_covers_the_whole_days_before(string schedule, string after, string next, string from, string to)
    {
        var report = new Report { Name = "r", Schedule = Enum.Parse<ReportSchedule>(schedule), At = new TimeOnly(7, 0), Weekday = DayOfWeek.Monday };

        var runAt = ReportTiming.Next(report, At(after), London)!.Value;
        var (start, end) = ReportTiming.PeriodOf(report, runAt, London);

        Assert.Equal(At(next), runAt);
        Assert.Equal((At(from), At(to)), (start, end));
    }

    [Fact]
    public void A_cron_report_runs_when_its_expression_says_and_covers_the_period_chosen()
    {
        var report = new Report { Name = "r", Schedule = ReportSchedule.Cron, Cron = "30 8 * * 1-5", Period = ReportPeriod.Days, PeriodDays = 14 };

        // A Saturday: the next weekday at 08:30, Monday 5 October, covering the 14 days before.
        var runAt = ReportTiming.Next(report, At("2026-10-03T09:00:00Z"), London)!.Value;
        var (start, end) = ReportTiming.PeriodOf(report, runAt, London);

        Assert.Equal(At("2026-10-05T07:30:00Z"), runAt);
        Assert.Equal((At("2026-09-20T23:00:00Z"), At("2026-10-04T23:00:00Z")), (start, end));
        Assert.Null(ReportTiming.CronProblem("0 8 * * 1-5"));
        Assert.NotNull(ReportTiming.CronProblem("every morning"));
        Assert.NotNull(ReportTiming.CronProblem(""));
    }

    [GeneratedRegex(@"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""")]
    private static partial Regex Token();

    private static async Task<HttpResponseMessage> PostAsync(HttpClient browser, string page, string action, params (string Name, string Value)[] fields)
    {
        var html = await browser.GetStringAsync(new Uri(page, UriKind.Relative), Ct);
        using var content = new FormUrlEncodedContent(fields.Select(f => new KeyValuePair<string, string>(f.Name, f.Value))
            .Append(new("__RequestVerificationToken", WebUtility.HtmlDecode(Token().Match(html).Groups[1].Value))));
        return await browser.PostAsync(new Uri(action, UriKind.Relative), content, Ct);
    }

    private static async Task<T> DbAsync<T>(WebApplicationFactory<Program> app, Func<TalkWatchDbContext, Task<T>> work)
    {
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<AccessScopeHolder>().UseSystemScope();
        return await work(scope.ServiceProvider.GetRequiredService<TalkWatchDbContext>());
    }

    /// <summary>A report covering the last 92 days, so it reaches the captured calls, with every section.</summary>
    private static (string, string)[] EveryPart(params Guid[] users) =>
    [
        ("Name", "Quarter"), ("Schedule", "Cron"), ("Cron", "0 7 * * *"), ("Period", "Days"), ("PeriodDays", "92"), ("Enabled", "true"),
        ("Sections", "Figures"), ("Sections", "CallBacks"), ("Sections", "Lines"), ("Sections", "Switchboard"), ("Sections", "CallList"),
        .. users.Select(u => ("Users", u.ToString())),
    ];

    [Fact]
    public async Task Each_recipient_gets_a_copy_built_with_their_own_access_and_reads_only_their_own()
    {
        var console = new FixtureConsole(FixtureConsole.DefaultDirectory);
        // The captured calls into the last few days, so a 92-day report covers them whenever this runs.
        console.ShiftTimes(DateTimeOffset.UtcNow.AddDays(-2));
        await using var app = talkwatch.Create(console);
        await app.Services.GetRequiredService<CallLogPoller>().RunOnceAsync(Ct);
        using var admin = TalkWatchApp.Browser(app);
        await TalkWatchApp.SignInAsync(admin, TalkWatchApp.AdminUsername, TalkWatchApp.AdminPassword);
        // The number that carries the fewest calls, so the reader's copy is a part of the whole.
        var recent = DateTimeOffset.UtcNow.AddDays(-90);
        // Any kind of line: in the capture's last months every call came in on one number, so it is a person's line that
        // carries only some of them.
        var (kind, line) = await DbAsync(app, async db => (await db.CallLines.Where(l => db.Calls.Any(c => c.Id == l.CallId && c.Time >= recent))
            .GroupBy(l => new { l.Kind, l.Key }).OrderBy(g => g.Count()).Select(g => g.Key).FirstAsync(Ct)) is var k ? (k.Kind, k.Key) : default);
        await PostAsync(admin, "/admin/users", "/admin/users", ("Username", "reader"), ("Role", Roles.Viewer), ("Password", Password));
        var reader = await DbAsync(app, db => db.Users.Where(u => u.UserName == "reader").Select(u => u.Id).SingleAsync(Ct));
        await PostAsync(admin, $"/admin/users/{reader}", $"/admin/users/{reader}/grants", ("Line", $"{kind}:{line}"));
        var adminId = await DbAsync(app, db => db.Users.Where(u => u.UserName == TalkWatchApp.AdminUsername).Select(u => u.Id).SingleAsync(Ct));

        var saved = await PostAsync(admin, "/reports/new", "/reports/save", EveryPart(adminId, reader));
        var report = await DbAsync(app, db => db.Reports.Select(r => r.Id).SingleAsync(Ct));
        var ran = await PostAsync(admin, "/reports", $"/reports/{report}/run");

        Assert.Contains("saved", saved.Headers.Location!.OriginalString, StringComparison.Ordinal);
        Assert.Contains("2%20copies", ran.Headers.Location!.OriginalString, StringComparison.Ordinal);
        var runs = await DbAsync(app, db => db.ReportRuns.ToDictionaryAsync(r => r.AudienceUserId!.Value, Ct));
        int Rows(string? csv) => csv!.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length - 1;
        // Within the copy's own period: the captured calls go back further than 92 days.
        var (from, to) = (runs[adminId].From, runs[adminId].To);
        var adminCalls = await DbAsync(app, db => db.Calls.CountAsync(c => c.Time >= from && c.Time < to, Ct));
        var readerCalls = await DbAsync(app, db => db.Calls.CountAsync(c => c.Time >= from && c.Time < to && c.Lines.Any(l => l.Kind == kind && l.Key == line), Ct));
        Assert.Equal(adminCalls, Rows(runs[adminId].Csv));
        Assert.Equal(readerCalls, Rows(runs[reader].Csv));
        Assert.True(readerCalls > 0 && readerCalls < adminCalls, $"The reader's line should carry some of the calls, not none or all: {readerCalls} of {adminCalls}.");
        Assert.Contains("data-report-figures", runs[reader].Html, StringComparison.Ordinal);
        Assert.Contains("for reader", runs[reader].Html, StringComparison.Ordinal);

        // The reader sees their copy, and only theirs; the call list comes with it.
        using var browser = TalkWatchApp.Browser(app);
        await TalkWatchApp.SignInAsync(browser, "reader", Password);
        var list = await browser.GetStringAsync(new Uri("/reports", UriKind.Relative), Ct);
        var own = await browser.GetStringAsync(new Uri($"/reports/runs/{runs[reader].Id}", UriKind.Relative), Ct);
        var other = await browser.GetStringAsync(new Uri($"/reports/runs/{runs[adminId].Id}", UriKind.Relative), Ct);
        var ownCsv = await browser.GetAsync(new Uri($"/reports/runs/{runs[reader].Id}/calls.csv", UriKind.Relative), Ct);
        var otherCsv = await browser.GetAsync(new Uri($"/reports/runs/{runs[adminId].Id}/calls.csv", UriKind.Relative), Ct);
        var editor = await browser.GetAsync(new Uri("/reports/new", UriKind.Relative), Ct);

        Assert.Contains($"data-run=\"{runs[reader].Id}\"", list, StringComparison.Ordinal);
        Assert.DoesNotContain($"data-run=\"{runs[adminId].Id}\"", list, StringComparison.Ordinal);
        Assert.Contains("data-report-figures", own, StringComparison.Ordinal);
        Assert.Contains("No such report", other, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, ownCsv.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, otherCsv.StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, editor.StatusCode);
    }

    private static readonly string[] Weekdays = ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday"];
    private static readonly string[] FurtherParts = ["Figures", "People", "MissedList", "BusiestTimes", "Callers", "Sentiment", "CallBackTimes", "OutsideVoicemail", "Alerts"];
    private static readonly string[] PartMarks = ["data-report-people", "data-report-busiest", "data-report-callers", "data-report-sentiment", "data-report-callback-times", "data-report-alerts"];

    // Every further part a report can hold, worked out from the calls and shown; and through each copy's own access, so a
    // reader not allowed transcripts is shown no ratings of them.
    [Fact]
    public async Task Every_further_part_is_worked_out_and_shown_with_the_readers_own_access()
    {
        var console = new FixtureConsole(FixtureConsole.DefaultDirectory);
        console.ShiftTimes(DateTimeOffset.UtcNow.AddDays(-2));
        await using var app = talkwatch.Create(console);
        await app.Services.GetRequiredService<CallLogPoller>().RunOnceAsync(Ct);
        await app.Services.GetRequiredService<TranscriptSync>().SyncAsync(Ct);
        using var admin = TalkWatchApp.Browser(app);
        await TalkWatchApp.SignInAsync(admin, TalkWatchApp.AdminUsername, TalkWatchApp.AdminPassword);
        await PostAsync(admin, "/admin/users", "/admin/users", ("Username", "reader"), ("Role", Roles.Viewer), ("Password", Password));
        var reader = await DbAsync(app, db => db.Users.Where(u => u.UserName == "reader").Select(u => u.Id).SingleAsync(Ct));
        var line = await DbAsync(app, db => db.CallLines.GroupBy(l => new { l.Kind, l.Key }).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstAsync(Ct));
        await PostAsync(admin, $"/admin/users/{reader}", $"/admin/users/{reader}/grants", ("Line", $"{line.Kind}:{line.Key}"));
        var adminId = await DbAsync(app, db => db.Users.Where(u => u.UserName == TalkWatchApp.AdminUsername).Select(u => u.Id).SingleAsync(Ct));
        (string, string)[] parts =
        [
            ("Name", "Everything new"), ("Schedule", "Cron"), ("Cron", "0 7 * * *"), ("Period", "Days"), ("PeriodDays", "92"), ("Enabled", "true"),
            .. FurtherParts.Select(p => ("Sections", p)),
            ("Users", adminId.ToString()), ("Users", reader.ToString()),
        ];
        await PostAsync(admin, "/reports/new", "/reports/save", parts);
        var report = await DbAsync(app, db => db.Reports.Select(r => r.Id).SingleAsync(Ct));

        await PostAsync(admin, "/reports", $"/reports/{report}/run");

        var runs = await DbAsync(app, db => db.ReportRuns.ToDictionaryAsync(r => r.AudienceUserId!.Value, Ct));
        var html = WebUtility.HtmlDecode(runs[adminId].Html);
        foreach (var part in PartMarks)
        {
            Assert.Contains(part, html, StringComparison.Ordinal);
        }

        Assert.Contains("No calls went to an outside answering line's voicemail.", html, StringComparison.Ordinal);
        var (from, to) = (runs[adminId].From, runs[adminId].To);
        var (callers, missed) = await DbAsync(app, async db => (
            await db.Calls.Where(c => c.Direction == "in" && c.Time >= from && c.Time < to && c.FromE164 != null).Select(c => c.FromE164).Distinct().CountAsync(Ct),
            await db.Calls.CountAsync(c => c.Direction == "in" && c.Time >= from && c.Time < to && CallOutcomes.UnansweredKinds.Contains(c.Outcome), Ct)));
        Assert.Contains($"data-report-callers>{callers} caller", html, StringComparison.Ordinal);
        Assert.True(missed > 0, "The capture should have missed calls in the period.");
        var list = html[html.IndexOf("data-report-missed", StringComparison.Ordinal)..html.IndexOf("data-report-busiest", StringComparison.Ordinal)];
        Assert.Equal(Math.Min(missed, ReportParts.MissedShown), Regex.Count(list, "white-space: nowrap; color: #3b4650;"));

        // The reader may not read transcripts: no ratings of them in their copy.
        Assert.Contains("No calls Talk's transcription rated in the period.", WebUtility.HtmlDecode(runs[reader].Html), StringComparison.Ordinal);
    }

    // Compare with the period before, count only chosen hours, and split by number.
    [Fact]
    public async Task A_report_compares_counts_only_its_hours_and_splits_by_number()
    {
        var console = new FixtureConsole(FixtureConsole.DefaultDirectory);
        // Moved by whole weeks, so each call keeps the weekday and time of day it was made at. Moved by any other amount,
        // every call's time of day moves with it, and at some hours of the day all of them fell outside office hours.
        var newest = console.NewestCallTime!.Value;
        console.ShiftTimes(newest.AddDays(7 * Math.Floor((DateTimeOffset.UtcNow.AddDays(-2) - newest).TotalDays / 7)));
        await using var app = talkwatch.Create(console);
        await app.Services.GetRequiredService<CallLogPoller>().RunOnceAsync(Ct);
        using var admin = TalkWatchApp.Browser(app);
        await TalkWatchApp.SignInAsync(admin, TalkWatchApp.AdminUsername, TalkWatchApp.AdminPassword);
        (string, string)[] form =
        [
            ("Name", "Office hours"), ("Schedule", "Cron"), ("Cron", "0 7 * * *"), ("Period", "Days"), ("PeriodDays", "92"), ("Enabled", "true"),
            ("Sections", "Figures"), ("Sections", "CallList"), ("ComparePrevious", "true"), ("SplitByNumber", "true"),
            ("HoursOnly", "true"), .. Weekdays.Select(d => ("HoursDays", d)), ("HoursFrom", "09:00"), ("HoursTo", "17:30"),
        ];

        // Only some hours, but no days: refused.
        await PostAsync(admin, "/reports/new", "/reports/save", [.. form.Where(f => f.Item1 != "HoursDays")]);
        Assert.Equal(0, await DbAsync(app, db => db.Reports.CountAsync(Ct)));

        await PostAsync(admin, "/reports/new", "/reports/save", form);
        var report = await DbAsync(app, db => db.Reports.SingleAsync(Ct));
        Assert.Equal((true, true, new TimeOnly(9, 0), new TimeOnly(17, 30)), (report.ComparePrevious, report.SplitByNumber, report.HoursFrom, report.HoursTo));
        await PostAsync(admin, "/reports", $"/reports/{report.Id}/run");

        var run = await DbAsync(app, db => db.ReportRuns.SingleAsync(Ct));
        var html = WebUtility.HtmlDecode(run.Html);
        // The call list holds exactly the period's calls within the hours, worked out here on its own.
        var zone = app.Services.GetRequiredService<ReportBuilder>().Zone;
        var hours = report.Hours!;
        var inPeriod = await DbAsync(app, db => db.Calls.Where(c => c.Time >= run.From && c.Time < run.To).Select(c => c.Time).ToListAsync(Ct));
        var expected = inPeriod.Count(t => hours.Matches(t, zone));
        Assert.True(expected > 0 && expected < inPeriod.Count, $"The hours should keep some calls and leave some out: {expected} of {inPeriod.Count}.");
        Assert.Equal(expected, run.Csv!.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length - 1);
        Assert.Contains("Counting only calls", html, StringComparison.Ordinal);
        Assert.Contains("Mon Tue Wed Thu Fri 09:00–17:30", html, StringComparison.Ordinal);
        Assert.Contains("data-report-compared", html, StringComparison.Ordinal);
        Assert.Contains("data-report-change", html, StringComparison.Ordinal);
        Assert.Contains("data-report-by-number", html, StringComparison.Ordinal);
    }

    // A new report can start from a ready-made one: its name, parts, schedule and options filled in, still to change.
    // With nobody giving up in the period there is no wait to tell, so the report doesn't say they waited "–".
    [Fact]
    public async Task A_period_nobody_gave_up_in_says_nothing_of_how_long_they_waited()
    {
        await using var app = talkwatch.Create(new FixtureConsole(FixtureConsole.DefaultDirectory));
        using var admin = TalkWatchApp.Browser(app);
        await TalkWatchApp.SignInAsync(admin, TalkWatchApp.AdminUsername, TalkWatchApp.AdminPassword);
        await PostAsync(admin, "/reports/new", "/reports/save",
            ("Name", "Quiet"), ("Schedule", "Daily"), ("At", "07:00"), ("Enabled", "true"), ("Sections", "Figures"));
        var report = await DbAsync(app, db => db.Reports.SingleAsync(Ct));

        await PostAsync(admin, "/reports", $"/reports/{report.Id}/run");

        var html = WebUtility.HtmlDecode(await DbAsync(app, db => db.ReportRuns.Select(r => r.Html).SingleAsync(Ct)));
        Assert.Contains("data-report-figures", html, StringComparison.Ordinal);
        Assert.Contains("scored calls had poor quality", html, StringComparison.Ordinal);
        Assert.DoesNotContain("gave up had waited", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_new_report_starts_from_a_ready_made_one_filled_in()
    {
        await using var app = talkwatch.Create(new FixtureConsole(FixtureConsole.DefaultDirectory));
        using var admin = TalkWatchApp.Browser(app);
        await TalkWatchApp.SignInAsync(admin, TalkWatchApp.AdminUsername, TalkWatchApp.AdminPassword);

        var blank = await admin.GetStringAsync(new Uri("/reports/new", UriKind.Relative), Ct);
        var monthly = WebUtility.HtmlDecode(await admin.GetStringAsync(new Uri("/reports/new?type=monthly-trends", UriKind.Relative), Ct));

        Assert.All(ReportTypes.All, t => Assert.Contains($"data-report-type=\"{t.Key}\"", blank, StringComparison.Ordinal));
        Assert.Contains("value=\"Monthly trends\"", monthly, StringComparison.Ordinal);
        Assert.Matches("value=\"Monthly\" checked", monthly);
        Assert.Matches("name=\"Sections\" value=\"Sentiment\" checked", monthly);
        Assert.DoesNotMatch("name=\"Sections\" value=\"MissedList\" checked", monthly);
        Assert.Matches("name=\"ComparePrevious\" value=\"true\" checked", monthly);
        Assert.Matches("name=\"SplitByNumber\" value=\"true\" checked", monthly);
        // Every ready-made report holds something, and only parts there are.
        Assert.All(ReportTypes.All, t => Assert.NotEqual(ReportSections.None, t.Start().Sections));
    }

    // Saving a report that already exists, with its recipients and numbers, failed: the new recipients were taken for rows
    // already there and updated, which matched nothing.
    [Fact]
    public async Task An_existing_report_saves_again_with_its_recipients_and_numbers()
    {
        await using var app = talkwatch.Create(new FixtureConsole(FixtureConsole.DefaultDirectory));
        await app.Services.GetRequiredService<CallLogPoller>().RunOnceAsync(Ct);
        using var admin = TalkWatchApp.Browser(app);
        await TalkWatchApp.SignInAsync(admin, TalkWatchApp.AdminUsername, TalkWatchApp.AdminPassword);
        var adminId = await DbAsync(app, db => db.Users.Where(u => u.UserName == TalkWatchApp.AdminUsername).Select(u => u.Id).SingleAsync(Ct));
        var did = await DbAsync(app, db => db.Lines.Where(l => l.Kind == LineKind.Did).Select(l => l.Key).FirstAsync(Ct));
        (string, string)[] form =
        [
            ("Name", "Weekly"), ("Schedule", "Weekly"), ("Weekday", "Monday"), ("At", "08:00"), ("Period", "Week"), ("Enabled", "true"),
            ("Sections", "Figures"), ("Users", adminId.ToString()), ("Numbers", did),
        ];
        await PostAsync(admin, "/reports/new", "/reports/save", form);
        var id = await DbAsync(app, db => db.Reports.Select(r => r.Id).SingleAsync(Ct));

        var again = await PostAsync(admin, $"/reports/{id}/edit", "/reports/save", [("Id", id.ToString()), .. form.Select(f => f.Item1 == "Name" ? ("Name", "Weekly, renamed") : f)]);

        Assert.Equal(HttpStatusCode.Redirect, again.StatusCode);
        Assert.Contains("saved", again.Headers.Location!.OriginalString, StringComparison.Ordinal);
        var saved = await DbAsync(app, db => db.Reports.Include(r => r.Recipients).Include(r => r.Numbers).SingleAsync(Ct));
        Assert.Equal("Weekly, renamed", saved.Name);
        Assert.Equal([adminId], saved.Recipients.Select(r => r.UserId!.Value));
        Assert.Equal([did], saved.Numbers.Select(n => n.Did));
    }

    [Fact]
    public async Task A_report_due_runs_once_and_is_set_for_its_next_time()
    {
        await using var app = talkwatch.Create(new FixtureConsole(FixtureConsole.DefaultDirectory));
        using var admin = TalkWatchApp.Browser(app);
        await TalkWatchApp.SignInAsync(admin, TalkWatchApp.AdminUsername, TalkWatchApp.AdminPassword);
        await PostAsync(admin, "/reports/new", "/reports/save", ("Name", "Daily"), ("Schedule", "Daily"), ("At", "07:00"), ("Sections", "Figures"), ("Enabled", "true"));
        var id = await DbAsync(app, db => db.Reports.Select(r => r.Id).SingleAsync(Ct));
        await DbAsync(app, db => db.Reports.Where(r => r.Id == id).ExecuteUpdateAsync(u => u.SetProperty(r => r.NextRunAt, DateTimeOffset.UtcNow.AddMinutes(-1)), Ct));
        var scheduler = app.Services.GetRequiredService<ReportScheduler>();

        var first = await scheduler.RunDueAsync(Ct);
        var second = await scheduler.RunDueAsync(Ct);

        var report = await DbAsync(app, db => db.Reports.SingleAsync(Ct));
        Assert.Equal((1, 0), (first, second));
        Assert.True(report.NextRunAt > DateTimeOffset.UtcNow);
        Assert.Equal(TimeSpan.Zero, TimeZoneInfo.ConvertTime(report.NextRunAt!.Value, London).TimeOfDay - new TimeSpan(7, 0, 0));
        // No recipients: one copy, of the whole site, kept for those who manage reports.
        Assert.Null(await DbAsync(app, db => db.ReportRuns.Select(r => r.AudienceUserId).SingleAsync(Ct)));
    }

    [Theory]
    [InlineData("Name", "", "a name")]
    [InlineData("Cron", "every morning", "does not read")]
    [InlineData("PeriodDays", "400", "between 1 and 92")]
    public async Task A_report_that_cannot_work_is_refused(string field, string value, string problem)
    {
        await using var app = talkwatch.Create(new FixtureConsole(FixtureConsole.DefaultDirectory));
        using var admin = TalkWatchApp.Browser(app);
        await TalkWatchApp.SignInAsync(admin, TalkWatchApp.AdminUsername, TalkWatchApp.AdminPassword);
        var fields = EveryPart().Where(f => f.Item1 != field).Append((field, value)).ToArray();

        var refused = await PostAsync(admin, "/reports/new", "/reports/save", fields);

        Assert.Contains(Uri.EscapeDataString(problem), refused.Headers.Location!.OriginalString, StringComparison.Ordinal);
        Assert.Equal(0, await DbAsync(app, db => db.Reports.CountAsync(Ct)));
    }
}
