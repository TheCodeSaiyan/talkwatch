namespace TalkWatch.Data;

/// <summary>The parts a report can hold. Any mix.</summary>
[Flags]
public enum ReportSections
{
    None = 0,

    /// <summary>The dashboard's figures for the period: outcomes, answer rate and speed, missed by hour, quality, call-backs.</summary>
    Figures = 1 << 0,

    /// <summary>Missed callers nobody has got back to yet, as the call-back list has them when the report runs.</summary>
    CallBacks = 1 << 1,

    /// <summary>Each line's calls in the period: answered, missed, voicemail, hung up.</summary>
    Lines = 1 << 2,

    /// <summary>Each switchboard's greeting hang-ups and what its menu options led to.</summary>
    Switchboard = 1 << 3,

    /// <summary>Every call in the period, attached as CSV.</summary>
    CallList = 1 << 4,

    /// <summary>Each person's calls: answered, average talk, how fast they picked up, calls out, voicemail for them.</summary>
    People = 1 << 5,

    /// <summary>Every missed call and voicemail in the period, and whether the caller has been got back to.</summary>
    MissedList = 1 << 6,

    /// <summary>Calls and missed calls by day of the week and hour.</summary>
    BusiestTimes = 1 << 7,

    /// <summary>The top callers, repeat callers, and new callers against returning ones.</summary>
    Callers = 1 << 8,

    /// <summary>How Talk's transcription rated the calls, positive to negative, and the negative ones.</summary>
    Sentiment = 1 << 9,

    /// <summary>How quickly missed callers were got back to, and how.</summary>
    CallBackTimes = 1 << 10,

    /// <summary>Calls an outside answering line's voicemail took: messages left and hang-ups, per contact.</summary>
    OutsideVoicemail = 1 << 11,

    /// <summary>The alerts raised, how many were acknowledged, how fast, and by whom.</summary>
    Alerts = 1 << 12,
}

public enum ReportSchedule
{
    /// <summary>Every day at <see cref="Report.At"/>, covering the day before.</summary>
    Daily,

    /// <summary>Every week on <see cref="Report.Weekday"/> at <see cref="Report.At"/>, covering the seven days before.</summary>
    Weekly,

    /// <summary>On the 1st of every month at <see cref="Report.At"/>, covering the month before.</summary>
    Monthly,

    /// <summary>Whenever <see cref="Report.Cron"/> says, covering <see cref="Report.Period"/>.</summary>
    Cron,
}

/// <summary>What a cron report covers, ending at the start of the day it runs.</summary>
public enum ReportPeriod
{
    Day,
    Week,
    Month,

    /// <summary>The <see cref="Report.PeriodDays"/> days before.</summary>
    Days,
}

/// <summary>
/// A report: which sections, how often, and for whom. Each recipient gets a copy built with their own access, so a
/// Viewer's report covers only their lines; every copy is kept, to open on the Reports page.
/// </summary>
public sealed class Report
{
    public Guid Id { get; set; }
    public Guid SiteId { get; set; }
    public required string Name { get; set; }
    public ReportSections Sections { get; set; }

    public ReportSchedule Schedule { get; set; }

    /// <summary>The time of day it runs, in the site's zone, for the daily, weekly and monthly schedules.</summary>
    public TimeOnly At { get; set; } = new(7, 0);

    /// <summary>The day a weekly report runs.</summary>
    public DayOfWeek Weekday { get; set; } = DayOfWeek.Monday;

    /// <summary>A five-field cron expression in the site's zone, for <see cref="ReportSchedule.Cron"/>.</summary>
    public string? Cron { get; set; }

    public ReportPeriod Period { get; set; }
    public int? PeriodDays { get; set; }

    public bool Enabled { get; set; } = true;
    public DateTimeOffset? NextRunAt { get; set; }
    public DateTimeOffset? LastRunAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public List<ReportRecipient> Recipients { get; set; } = [];

    /// <summary>
    /// The numbers the report covers, each copy showing only their calls (on the DID, and through what it routes to);
    /// none means every number. Never more than a copy's reader may see either way.
    /// </summary>
    public List<ReportNumber> Numbers { get; set; } = [];

    /// <summary>Each headline figure shown against the period before, with the change.</summary>
    public bool ComparePrevious { get; set; }

    /// <summary>
    /// Count only calls on these days (<see cref="Core.Alerts.TimeWindow.ToMask"/>) between <see cref="HoursFrom"/> and
    /// <see cref="HoursTo"/>; null for every hour.
    /// </summary>
    public int? HoursDays { get; set; }

    public TimeOnly? HoursFrom { get; set; }

    public TimeOnly? HoursTo { get; set; }

    /// <summary>The headline figures split by number as well as in total.</summary>
    public bool SplitByNumber { get; set; }

    /// <summary>The hours it counts, or null for every hour.</summary>
    public Core.Alerts.TimeWindow? Hours =>
        HoursDays is { } days && HoursFrom is { } from && HoursTo is { } to ? new Core.Alerts.TimeWindow(Core.Alerts.TimeWindow.FromMask(days), from, to, Outside: false) : null;
}

/// <summary>A number a report covers, as E.164.</summary>
public sealed class ReportNumber
{
    public Guid ReportId { get; set; }
    public required string Did { get; set; }
}

/// <summary>Who a report goes to: a person, or an email channel. One of the two.</summary>
public sealed class ReportRecipient
{
    public Guid Id { get; set; }
    public Guid ReportId { get; set; }
    public Guid? UserId { get; set; }
    public Guid? ChannelId { get; set; }
}

/// <summary>
/// One copy of a report, as it ran for one audience: built with that person's access (none for a copy with the whole
/// site's), kept as the page it was, with the call list when the report includes one.
/// </summary>
public sealed class ReportRun
{
    public Guid Id { get; set; }
    public Guid SiteId { get; set; }
    public Guid ReportId { get; set; }

    /// <summary>Whose access the copy was built with; null for the whole site's.</summary>
    public Guid? AudienceUserId { get; set; }

    public required string ReportName { get; set; }
    public DateTimeOffset From { get; set; }
    public DateTimeOffset To { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>The report as HTML, rendered when it ran.</summary>
    public required string Html { get; set; }

    /// <summary>Every call in the period as CSV, when the report includes the call list.</summary>
    public string? Csv { get; set; }
}

/// <summary>
/// One copy of a report emailed to one address: a person's own, or an email channel's. Retried with back-off until
/// sent or given up, like an alert.
/// </summary>
public sealed class ReportDelivery
{
    public Guid Id { get; set; }
    public Guid SiteId { get; set; }
    public Guid RunId { get; set; }

    /// <summary>Where it goes; empty for a person with no email address, which is recorded and never sent.</summary>
    public required string Address { get; set; }

    /// <summary>Who or what it is for, as the report's page names it: a username or a channel's name.</summary>
    public required string Recipient { get; set; }

    /// <summary>
    /// The person it is for, when it is for a person rather than a channel: each try goes to their email as it is then,
    /// so an address corrected after the report ran is the one used.
    /// </summary>
    public Guid? UserId { get; set; }

    public DeliveryState State { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public string? LastError { get; set; }
}
