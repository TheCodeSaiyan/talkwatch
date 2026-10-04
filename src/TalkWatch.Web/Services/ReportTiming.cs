using Cronos;
using TalkWatch.Data;

namespace TalkWatch.Web.Services;

/// <summary>
/// When a report runs and what it covers, in the site's time zone. The daily, weekly and monthly schedules are cron
/// expressions underneath, so all four follow the clocks changing the same way.
/// </summary>
public static class ReportTiming
{
    /// <summary>The longest period a report can cover: the dashboard's.</summary>
    public const int LongestPeriodDays = CallStatistics.LongestPeriodDays;

    public static string CronOf(Report report) => report.Schedule switch
    {
        ReportSchedule.Daily => $"{report.At.Minute} {report.At.Hour} * * *",
        ReportSchedule.Weekly => $"{report.At.Minute} {report.At.Hour} * * {(int)report.Weekday}",
        ReportSchedule.Monthly => $"{report.At.Minute} {report.At.Hour} 1 * *",
        _ => report.Cron ?? "",
    };

    /// <summary>What is wrong with a cron expression, in words, or null when it reads.</summary>
    public static string? CronProblem(string? cron)
    {
        if (string.IsNullOrWhiteSpace(cron))
        {
            return "Give a cron expression: minute, hour, day of the month, month and day of the week, such as 0 8 * * 1-5.";
        }

        try
        {
            CronExpression.Parse(cron.Trim());
            return null;
        }
        catch (CronFormatException e)
        {
            return $"That cron expression does not read: {e.Message}";
        }
    }

    /// <summary>
    /// The next time the report runs after <paramref name="after"/>, or null when its schedule never comes round. In
    /// UTC: Npgsql stores only that, and Cronos answers in the zone's offset.
    /// </summary>
    public static DateTimeOffset? Next(Report report, DateTimeOffset after, TimeZoneInfo zone) =>
        CronExpression.Parse(CronOf(report)).GetNextOccurrence(after, zone)?.ToUniversalTime();

    /// <summary>
    /// What a report run at <paramref name="runAt"/> covers: whole days in the site's zone, ending at the start of the
    /// day it runs. A month is the calendar month before.
    /// </summary>
    public static (DateTimeOffset From, DateTimeOffset To) PeriodOf(Report report, DateTimeOffset runAt, TimeZoneInfo zone)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(runAt, zone).DateTime);
        var period = report.Schedule switch
        {
            ReportSchedule.Daily => ReportPeriod.Day,
            ReportSchedule.Weekly => ReportPeriod.Week,
            ReportSchedule.Monthly => ReportPeriod.Month,
            _ => report.Period,
        };
        var from = period switch
        {
            ReportPeriod.Week => today.AddDays(-7),
            ReportPeriod.Month => new DateOnly(today.Year, today.Month, 1).AddMonths(-1),
            ReportPeriod.Days => today.AddDays(-Math.Clamp(report.PeriodDays ?? 1, 1, LongestPeriodDays)),
            _ => today.AddDays(-1),
        };
        var to = period == ReportPeriod.Month ? new DateOnly(today.Year, today.Month, 1) : today;
        return (Midnight(from, zone), Midnight(to, zone));
    }

    /// <summary>The start of a day in the site's zone, as an instant.</summary>
    public static DateTimeOffset Midnight(DateOnly day, TimeZoneInfo zone) =>
        new(TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified), zone), TimeSpan.Zero);

    /// <summary>The schedule in words, for the reports page.</summary>
    public static string Describe(Report report) => report.Schedule switch
    {
        ReportSchedule.Daily => $"Daily at {report.At:HH\\:mm}, covering the day before",
        ReportSchedule.Weekly => $"{report.Weekday}s at {report.At:HH\\:mm}, covering the 7 days before",
        ReportSchedule.Monthly => $"On the 1st at {report.At:HH\\:mm}, covering the month before",
        _ => $"Cron {report.Cron}, covering {report.Period switch
        {
            ReportPeriod.Week => "the 7 days before",
            ReportPeriod.Month => "the month before",
            ReportPeriod.Days => $"the {report.PeriodDays} days before",
            _ => "the day before",
        }}",
    };
}
