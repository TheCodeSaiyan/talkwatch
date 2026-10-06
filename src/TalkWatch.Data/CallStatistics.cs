using Microsoft.EntityFrameworkCore;
using TalkWatch.Core.Calls;

namespace TalkWatch.Data;

/// <summary>One line's inbound calls over a period, by outcome.</summary>
public sealed record LineStatistics(LineKind Kind, string Key, string Name, int Inbound, int Answered, int Missed, int Voicemail, int HungUpAtSwitchboard);

/// <summary>
/// Missed and voicemail calls in a period that could be returned (the caller's number came through): how many have been,
/// how many are still open, and the median time to ring someone back. Marked done counts as returned but not in the
/// time, which would measure the marking.
/// </summary>
public sealed record CallBackStatistics(int Returnable, int Returned, int Open, TimeSpan? MedianCallBack)
{
    public static readonly CallBackStatistics None = new(0, 0, 0, null);
}

/// <summary>
/// How quickly calls were answered: from a call starting to someone picking up, for answered calls; and how long
/// callers who were missed waited before they gave up. Nulls when there were none to time.
/// </summary>
public sealed record AnswerSpeed(TimeSpan? MedianRing, TimeSpan? SlowestTenthRing, TimeSpan? MedianGiveUp)
{
    public static readonly AnswerSpeed None = new(null, null, null);
}

/// <summary>A call Talk scored low for quality.</summary>
public sealed record PoorCall(string TalkUuid, DateTimeOffset Time, int Score);

/// <summary>Talk's quality scores (0 to 100) for the period's calls that have one, with the worst calls.</summary>
public sealed record QualityStatistics(int Scored, int Poor, int? Median, IReadOnlyList<PoorCall> Worst)
{
    /// <summary>Below this a call counts as poor.</summary>
    public const int PoorBelow = 70;

    public static readonly QualityStatistics None = new(0, 0, null, []);
}

/// <summary>Inbound calls on one day, in the site's time zone.</summary>
public sealed record DayStatistics(DateOnly Day, int Inbound, int Answered, int Missed);

/// <summary>
/// Figures for the dashboard over a period. Counted through the caller's access scope, so a person sees figures for
/// the calls and lines they are granted and no others.
/// </summary>
/// <remarks>
/// 'Inbound' leaves out calls Talk blocked. Answer rate is answered calls over the callers who tried to reach someone:
/// answered, missed and voicemail; a caller who hung up at the switchboard never did. Hours and days are in the
/// site's time zone, so they follow the clocks.
/// </remarks>
public sealed record CallStatistics(
    DateTimeOffset From,
    DateTimeOffset To,
    IReadOnlyDictionary<CallOutcome, int> ByOutcome,
    TimeSpan AverageAnswered,
    IReadOnlyList<int> InboundByHour,
    IReadOnlyList<DayStatistics> ByDay,
    IReadOnlyList<LineStatistics> ByLine,
    CallBackStatistics CallBacks,
    AnswerSpeed Speed,
    QualityStatistics Quality,
    IReadOnlyList<IReadOnlyList<(int Inbound, int Missed)>> MissedByWeekdayAndHour)
{
    /// <summary>Monday first, as the grid is drawn.</summary>
    public static readonly DayOfWeek[] Week =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday];

    public const int LongestPeriodDays = 92;

    private static readonly CallOutcome[] InboundOutcomes =
        [CallOutcome.Answered, .. CallOutcomes.UnansweredKinds, CallOutcome.HungUpAtSwitchboard, CallOutcome.InProgress, CallOutcome.Unknown];

    public int Count(CallOutcome outcome) => ByOutcome.GetValueOrDefault(outcome);

    /// <summary>The calls that count as missed (<see cref="CallOutcomes.MissedKinds"/>).</summary>
    public int CountMissed => CallOutcomes.MissedKinds.Sum(Count);

    /// <summary>The calls that count as voicemail (<see cref="CallOutcomes.VoicemailKinds"/>).</summary>
    public int CountVoicemail => CallOutcomes.VoicemailKinds.Sum(Count);

    public int Inbound => InboundOutcomes.Sum(Count);

    /// <summary>Answered over answered, missed and voicemail; null when there were none of those.</summary>
    public double? AnswerRate =>
        Count(CallOutcome.Answered) + CountMissed + CountVoicemail is > 0 and var tried
            ? (double)Count(CallOutcome.Answered) / tried
            : null;

    /// <summary>The figures for calls from <paramref name="start"/> up to, not including, <paramref name="end"/>.</summary>
    public static async Task<CallStatistics> ComputeAsync(
        TalkWatchDbContext db, DateTimeOffset start, DateTimeOffset end, TimeZoneInfo zone, CancellationToken cancellationToken)
    {
        if (end <= start || end - start > TimeSpan.FromDays(LongestPeriodDays))
        {
            throw new ArgumentException($"A period runs forwards and is at most {LongestPeriodDays} days.", nameof(end));
        }

        // Npgsql writes timestamptz parameters only in UTC, and a period usually starts at a local midnight.
        (start, end) = (start.ToUniversalTime(), end.ToUniversalTime());

        // A period's calls are few enough to count here, which keeps hours and days in the site's zone across a change
        // of the clocks; the per-line counts are grouped by the database.
        var calls = await db.Calls.AsNoTracking()
            .Where(c => c.Time >= start && c.Time < end)
            .Select(c => new { c.Id, c.TalkUuid, c.Time, c.Outcome, c.DurationSeconds, c.QualityScore })
            .ToListAsync(cancellationToken);

        var byOutcome = calls.GroupBy(c => c.Outcome).ToDictionary(g => g.Key, g => g.Count());
        var answered = calls.Where(c => c.Outcome == CallOutcome.Answered).ToList();
        var average = answered.Count > 0 ? TimeSpan.FromSeconds(Math.Round(answered.Average(c => c.DurationSeconds))) : TimeSpan.Zero;

        var inbound = calls.Where(c => InboundOutcomes.Contains(c.Outcome)).Select(c => (Local: TimeZoneInfo.ConvertTime(c.Time, zone), c.Outcome)).ToList();
        var byHour = new int[24];
        foreach (var call in inbound)
        {
            byHour[call.Local.Hour]++;
        }

        var byDay = inbound
            .GroupBy(c => DateOnly.FromDateTime(c.Local.DateTime))
            .OrderBy(g => g.Key)
            .Select(g => new DayStatistics(g.Key, g.Count(), g.Count(c => c.Outcome == CallOutcome.Answered), g.Count(c => c.Outcome.IsMissed())))
            .ToList();

        var lines = await db.Lines.AsNoTracking().Select(l => new { l.Kind, l.Key, l.Name, l.SameAs }).ToListAsync(cancellationToken);
        var names = lines.ToDictionary(l => (l.Kind, l.Key), l => l.Name);
        // A line that is another name for one of the same kind (a contact Talk names by uuid and by id) counts as that one.
        var sameAs = lines.Where(l => l.SameAs is not null).ToDictionary(l => (l.Kind, l.Key), l => l.SameAs!);
        var perLine = await (
                from l in db.CallLines
                join c in db.Calls on l.CallId equals c.Id
                where c.Time >= start && c.Time < end && InboundOutcomes.Contains(c.Outcome)
                select new { l.Kind, l.Key, l.CallId, c.Outcome })
            .ToListAsync(cancellationToken);
        var byLine = perLine
            .Select(r => (r.Kind, Key: sameAs.GetValueOrDefault((r.Kind, r.Key), r.Key), r.CallId, r.Outcome))
            .Distinct()
            .GroupBy(r => (r.Kind, r.Key))
            .Select(g => new LineStatistics(
                g.Key.Kind, g.Key.Key, names.GetValueOrDefault(g.Key, g.Key.Key),
                g.Count(),
                g.Count(r => r.Outcome == CallOutcome.Answered),
                g.Count(r => r.Outcome.IsMissed()),
                g.Count(r => r.Outcome.IsVoicemail()),
                g.Count(r => r.Outcome == CallOutcome.HungUpAtSwitchboard)))
            .OrderByDescending(l => l.Inbound)
            .ThenBy(l => l.Name, StringComparer.Ordinal)
            .ToList();

        var returnable = await db.Calls.AsNoTracking().Returnable()
            .Where(c => c.Time >= start && c.Time < end)
            .Select(c => new { c.Time, c.ReturnedAt, c.ReturnedHow })
            .ToListAsync(cancellationToken);
        var waits = returnable.Where(c => c.ReturnedAt is not null && c.ReturnedHow != CallBackHow.MarkedDone)
            .Select(c => c.ReturnedAt!.Value - c.Time).Order().ToList();
        var callBacks = new CallBackStatistics(
            returnable.Count,
            returnable.Count(c => c.ReturnedAt is not null),
            returnable.Count(c => c.ReturnedAt is null),
            waits.Count == 0 ? null : waits.Count % 2 == 1 ? waits[waits.Count / 2] : (waits[(waits.Count / 2) - 1] + waits[waits.Count / 2]) / 2);

        // Missed here means missed or sent to voicemail: either way nobody picked up.
        var grid = Week.Select(_ => Enumerable.Range(0, 24).Select(_ => (Inbound: 0, Missed: 0)).ToArray()).ToArray();
        foreach (var call in inbound)
        {
            var cell = grid[Array.IndexOf(Week, call.Local.DayOfWeek)];
            var missed = call.Outcome.IsUnanswered();
            cell[call.Local.Hour] = (cell[call.Local.Hour].Inbound + 1, cell[call.Local.Hour].Missed + (missed ? 1 : 0));
        }

        // Ring and give-up times come from the events: started to accepted, or started to hung up.
        var timed = calls.Where(c => c.Outcome == CallOutcome.Answered || c.Outcome.IsUnanswered()).Select(c => c.Id).ToList();
        var marks = await db.CallEvents.AsNoTracking()
            .Where(e => timed.Contains(e.CallId) && (e.Event == "call_started" || e.Event == "call_accepted" || e.Event == "call_hangup"))
            .Select(e => new { e.CallId, e.Event, e.Time })
            .ToListAsync(cancellationToken);
        var byCall = marks.GroupBy(m => m.CallId).ToDictionary(g => g.Key, g => g.ToList());
        TimeSpan? Between(Guid call, string to) =>
            byCall.TryGetValue(call, out var m) && m.FirstOrDefault(e => e.Event == "call_started") is { } s && m.FirstOrDefault(e => e.Event == to) is { } t && t.Time >= s.Time
                ? t.Time - s.Time : null;
        var ring = calls.Where(c => c.Outcome == CallOutcome.Answered).Select(c => Between(c.Id, "call_accepted")).OfType<TimeSpan>().Order().ToList();
        var gaveUp = calls.Where(c => c.Outcome.IsUnanswered()).Select(c => Between(c.Id, "call_hangup")).OfType<TimeSpan>().Order().ToList();
        var speed = new AnswerSpeed(Median(ring), ring.Count == 0 ? null : ring[(int)Math.Ceiling(ring.Count * 0.9) - 1], Median(gaveUp));

        var scored = calls.Where(c => c.QualityScore is not null).ToList();
        var scores = scored.Select(c => c.QualityScore!.Value).Order().ToList();
        var quality = new QualityStatistics(
            scored.Count,
            scored.Count(c => c.QualityScore < QualityStatistics.PoorBelow),
            scores.Count == 0 ? null : scores[scores.Count / 2],
            [.. scored.Where(c => c.QualityScore < QualityStatistics.PoorBelow).OrderBy(c => c.QualityScore).ThenByDescending(c => c.Time).Take(5)
                .Select(c => new PoorCall(c.TalkUuid, c.Time, c.QualityScore!.Value))]);

        return new CallStatistics(start, end, byOutcome, average, byHour, byDay, byLine, callBacks, speed, quality,
            [.. grid.Select(row => (IReadOnlyList<(int, int)>)row)]);
    }

    private static TimeSpan? Median(List<TimeSpan> sorted) =>
        sorted.Count == 0 ? null : sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[(sorted.Count / 2) - 1] + sorted[sorted.Count / 2]) / 2;
}
