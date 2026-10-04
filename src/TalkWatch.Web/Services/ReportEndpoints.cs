using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TalkWatch.Data;

namespace TalkWatch.Web.Services;

/// <summary>Setting reports up, for those who manage them, and fetching a copy's call list, for whoever may read the copy.</summary>
public static class ReportEndpoints
{
    public sealed class ReportForm
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public List<ReportSections>? Sections { get; set; }
        public ReportSchedule Schedule { get; set; }
        public string? At { get; set; }
        public DayOfWeek Weekday { get; set; } = DayOfWeek.Monday;
        public string? Cron { get; set; }
        public ReportPeriod Period { get; set; }
        public string? PeriodDays { get; set; }
        public List<Guid>? Users { get; set; }
        public List<Guid>? Channels { get; set; }

        /// <summary>The numbers the report covers, as E.164; none for every number.</summary>
        public List<string>? Numbers { get; set; }
        public bool Enabled { get; set; }

        public bool ComparePrevious { get; set; }
        public bool SplitByNumber { get; set; }
        public bool HoursOnly { get; set; }
        public List<DayOfWeek>? HoursDays { get; set; }
        public string? HoursFrom { get; set; }
        public string? HoursTo { get; set; }
    }

    public static void MapReports(this IEndpointRouteBuilder app)
    {
        // A 404 stays a 404, rather than being re-run through the not-found page, whose antiforgery check turns it into a 400.
        // Site report managers, and anyone who may set up reports on a number: the query filters show them only their own.
        var manage = app.MapGroup("/reports").RequireAuthorization(Permissions.ReportsPolicy).WithMetadata(new SkipStatusCodePagesAttribute());

        manage.MapPost("/save", async ([FromForm] ReportForm form, TalkWatchDbContext db, CurrentSite site, ReportBuilder builder, Audit audit, TimeProvider clock, HttpContext http) =>
        {
            Guid? id = Guid.TryParse(form.Id, out var parsed) ? parsed : null;
            var back = id is { } existing ? $"/reports/{existing}/edit" : "/reports/new";
            if (string.IsNullOrWhiteSpace(form.Name) || form.Name.Trim().Length > 100)
            {
                return Back(back, "Give the report a name of up to 100 characters.");
            }

            var sections = (form.Sections ?? []).Aggregate(ReportSections.None, (all, s) => all | s);
            if (sections == ReportSections.None)
            {
                return Back(back, "Choose at least one thing for the report to hold.");
            }

            if (!Enum.IsDefined(form.Schedule) || !Enum.IsDefined(form.Period) || !Enum.IsDefined(form.Weekday))
            {
                return Back(back, "Choose how often it runs.");
            }

            var at = new TimeOnly(7, 0);
            if (form.Schedule != ReportSchedule.Cron && !TimeOnly.TryParse(form.At, System.Globalization.CultureInfo.InvariantCulture, out at))
            {
                return Back(back, "Give the time it runs.");
            }

            if (form.Schedule == ReportSchedule.Cron && ReportTiming.CronProblem(form.Cron) is { } cron)
            {
                return Back(back, cron);
            }

            int? days = null;
            if (form.Schedule == ReportSchedule.Cron && form.Period == ReportPeriod.Days)
            {
                if (!int.TryParse(form.PeriodDays, out var d) || d < 1 || d > ReportTiming.LongestPeriodDays)
                {
                    return Back(back, $"A report covers between 1 and {ReportTiming.LongestPeriodDays} days.");
                }

                days = d;
            }

            // Only some hours: the days, and a window that may run overnight (22:00 to 06:00).
            (int Days, TimeOnly From, TimeOnly To)? hours = null;
            if (form.HoursOnly)
            {
                var hourDays = (form.HoursDays ?? []).Where(Enum.IsDefined).Distinct().ToList();
                if (hourDays.Count == 0
                    || !TimeOnly.TryParse(form.HoursFrom, System.Globalization.CultureInfo.InvariantCulture, out var hoursFrom)
                    || !TimeOnly.TryParse(form.HoursTo, System.Globalization.CultureInfo.InvariantCulture, out var hoursTo)
                    || hoursFrom == hoursTo)
                {
                    return Back(back, "For only some hours, choose the days and a start and end time.");
                }

                hours = (Core.Alerts.TimeWindow.ToMask(hourDays), hoursFrom, hoursTo);
            }

            // Which numbers: any, or every one, for a site report manager; for anyone else, at least one, and only those they
            // may set up reports on.
            var numbers = (form.Numbers ?? []).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).Distinct(StringComparer.Ordinal).ToList();
            var siteWide = http.User.Can(Permission.ManageReports);
            if (await db.Lines.IgnoreQueryFilters().CountAsync(l => l.SiteId == site.Id && l.Kind == Core.Calls.LineKind.Did && numbers.Contains(l.Key)) != numbers.Count)
            {
                return Back(back, "Choose numbers from those listed.");
            }

            Guid? me = Guid.TryParse(http.User.FindFirstValue(System.Security.Claims.ClaimTypes.NameIdentifier), out var parsedMe) ? parsedMe : null;
            if (!siteWide)
            {
                var mine = me is { } self ? await NumberAccess.NumbersWith(db, self, Permission.ManageReports).ToListAsync() : [];
                if (numbers.Count == 0 || numbers.Any(n => !mine.Contains(n)))
                {
                    return Back(back, "Choose one or more of the numbers you set up reports for.");
                }
            }

            var users = (form.Users ?? []).Distinct().ToList();
            var channels = (form.Channels ?? []).Distinct().ToList();
            // A channel goes out to an address of its own: someone setting up reports on their numbers sends only to theirs.
            if (!siteWide && channels.Count > 0 && await db.AlertChannels.CountAsync(c => channels.Contains(c.Id) && c.OwnerUserId == me) != channels.Count)
            {
                return Back(back, "Choose your own email channels.");
            }
            if (await db.Users.CountAsync(u => users.Contains(u.Id) && u.SiteId == site.Id) != users.Count
                || await db.AlertChannels.CountAsync(c => channels.Contains(c.Id) && c.Kind == ChannelKind.Email) != channels.Count)
            {
                return Back(back, "Choose people and email channels from those listed.");
            }

            Report report;
            if (id is { } found)
            {
                if (await db.Reports.Include(r => r.Recipients).SingleOrDefaultAsync(r => r.Id == found) is not { } saved)
                {
                    return Results.NotFound();
                }

                report = saved;
                db.RemoveRange(report.Recipients);
                report.Recipients.Clear();
                db.RemoveRange(await db.ReportNumbers.Where(n => n.ReportId == report.Id).ToListAsync());
            }
            else
            {
                report = new Report { Id = Guid.NewGuid(), SiteId = site.Id, Name = "", CreatedAt = clock.GetUtcNow() };
                db.Reports.Add(report);
            }

            (report.Name, report.Sections, report.Schedule, report.At, report.Weekday) = (form.Name.Trim(), sections, form.Schedule, at, form.Weekday);
            (report.Cron, report.Period, report.PeriodDays, report.Enabled) = (form.Schedule == ReportSchedule.Cron ? form.Cron!.Trim() : null, form.Period, days, form.Enabled);
            (report.ComparePrevious, report.SplitByNumber) = (form.ComparePrevious, form.SplitByNumber);
            (report.HoursDays, report.HoursFrom, report.HoursTo) = hours is { } h ? (h.Days, h.From, h.To) : ((int?)null, (TimeOnly?)null, (TimeOnly?)null);
            // Added as new rows outright: attached through an existing report's list with their ids already set, they were
            // taken for rows already there, and the update that followed matched nothing.
            db.AddRange(users.Select(u => new ReportRecipient { Id = Guid.NewGuid(), ReportId = report.Id, UserId = u }));
            db.AddRange(channels.Select(c => new ReportRecipient { Id = Guid.NewGuid(), ReportId = report.Id, ChannelId = c }));
            db.ReportNumbers.AddRange(numbers.Select(n => new ReportNumber { ReportId = report.Id, Did = n }));
            report.NextRunAt = report.Enabled ? ReportTiming.Next(report, clock.GetUtcNow(), builder.Zone) : null;
            await db.SaveChangesAsync();
            await audit.WriteAsync(id is null ? "report.add" : "report.change", "report", report.Id, $"{report.Name}: {ReportTiming.Describe(report)}");
            return Back("/reports", $"{report.Name} saved.{(report.NextRunAt is { } next ? $" It next runs {TimeZoneInfo.ConvertTime(next, builder.Zone):ddd d MMM HH:mm}." : "")}");
        });

        manage.MapPost("/{id:guid}/run", async (Guid id, TalkWatchDbContext db, ReportBuilder builder, ReportMailer mailer, Audit audit, TimeProvider clock, CancellationToken cancellationToken) =>
        {
            if (!await db.Reports.AnyAsync(r => r.Id == id, cancellationToken))
            {
                return Results.NotFound();
            }

            var runs = await builder.RunAsync(id, clock.GetUtcNow(), cancellationToken);
            await mailer.SendDueAsync(cancellationToken);
            await audit.WriteAsync("report.run", "report", id, $"{runs.Count} copies");
            return Back("/reports", $"Report run: {runs.Count} {(runs.Count == 1 ? "copy" : "copies")}.");
        });

        manage.MapPost("/{id:guid}/delete", async (Guid id, TalkWatchDbContext db, Audit audit) =>
        {
            if (await db.Reports.SingleOrDefaultAsync(r => r.Id == id) is not { } report)
            {
                return Results.NotFound();
            }

            db.Reports.Remove(report);
            await db.SaveChangesAsync();
            await audit.WriteAsync("report.remove", "report", id, report.Name);
            return Back("/reports", $"{report.Name} removed, with its copies.");
        });

        // A copy's call list: its own filter lets in the person it was built for, or someone who manages reports.
        app.MapGet("/reports/runs/{id:guid}/calls.csv", async (Guid id, TalkWatchDbContext db, Audit audit, HttpContext http) =>
        {
            if (await db.ReportRuns.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id) is not { Csv: { } csv } run)
            {
                return Results.NotFound();
            }

            await audit.WriteAsync("report.calls", "report_run", id, run.ReportName);
            http.Response.Headers.CacheControl = "no-store";
            return Results.File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv)).ToArray(), "text/csv; charset=utf-8",
                $"talkwatch-report-{run.From:yyyy-MM-dd}-to-{run.To:yyyy-MM-dd}.csv");
        }).RequireAuthorization().WithMetadata(new SkipStatusCodePagesAttribute());
    }

    private static IResult Back(string path, string message) => Results.Redirect($"{path}?msg={Uri.EscapeDataString(message)}");
}
