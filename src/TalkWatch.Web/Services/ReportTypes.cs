using TalkWatch.Data;

namespace TalkWatch.Web.Services;

/// <summary>
/// Ready-made reports to start a new one from: its parts, schedule and options filled in, everything still to change
/// before it is saved. Who it goes to and the numbers it covers are always chosen.
/// </summary>
public static class ReportTypes
{
    public sealed record ReportType(string Key, string Title, string What, Func<Report> Start);

    public static readonly IReadOnlyList<ReportType> All =
    [
        new("daily-missed", "Daily missed calls", "Every morning: yesterday's missed calls and voicemail, who is still to call back, and how quickly people were called back.",
            () => New("Daily missed calls", ReportSections.MissedList | ReportSections.CallBacks | ReportSections.CallBackTimes, ReportSchedule.Daily, ReportPeriod.Day)),
        new("weekly-summary", "Weekly summary", "Monday morning, against the week before: the figures, each line, who answered, the busiest times and the callers.",
            () => New("Weekly summary", ReportSections.Figures | ReportSections.Lines | ReportSections.People | ReportSections.BusiestTimes | ReportSections.Callers
                | ReportSections.CallBacks, ReportSchedule.Weekly, ReportPeriod.Week, compare: true)),
        new("monthly-trends", "Monthly trends", "On the 1st, against the month before and by number: the figures, busiest times, callers, how calls went, switchboards, outside voicemail and alerts.",
            () => New("Monthly trends", ReportSections.Figures | ReportSections.BusiestTimes | ReportSections.Callers | ReportSections.Sentiment | ReportSections.Switchboard
                | ReportSections.OutsideVoicemail | ReportSections.Alerts, ReportSchedule.Monthly, ReportPeriod.Month, compare: true, split: true)),
        new("callback-accountability", "Call-back accountability", "Monday morning, against the week before: how quickly missed callers were called back, every missed call, and who is still waiting.",
            () => New("Call-back accountability", ReportSections.CallBackTimes | ReportSections.MissedList | ReportSections.CallBacks | ReportSections.People,
                ReportSchedule.Weekly, ReportPeriod.Week, compare: true)),
    ];

    /// <summary>A new, unsaved report from the type with this key; null for none.</summary>
    public static Report? Start(string? key) => All.FirstOrDefault(t => t.Key == key)?.Start();

    private static Report New(string name, ReportSections sections, ReportSchedule schedule, ReportPeriod period, bool compare = false, bool split = false) => new()
    {
        Name = name, Sections = sections, Schedule = schedule, Period = period, At = new TimeOnly(8, 0), Weekday = DayOfWeek.Monday, Enabled = true,
        ComparePrevious = compare, SplitByNumber = split,
    };
}
