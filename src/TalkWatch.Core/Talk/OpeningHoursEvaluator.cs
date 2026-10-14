namespace TalkWatch.Core.Talk;

public enum OpenState
{
    /// <summary>The hours cannot be settled: holidays are on but TalkWatch cannot read their dates. Callers fall back to not knowing the branch.</summary>
    Unknown,
    Open,
    Closed,
}

/// <summary>
/// Whether a time node covers a moment, and so which branch of a split by opening hours a call took. The hours are wall
/// clock times in one zone, the zone reports use, so a range keeps its local times across a clock change.
/// </summary>
public static class OpeningHoursEvaluator
{
    private const int Day = 24 * 60;

    /// <summary>Whether a non-default time node covers <paramref name="instant"/>. A default node covers whatever its siblings do not, so it is settled by <see cref="Branch"/>.</summary>
    public static OpenState Evaluate(OpeningHours hours, DateTimeOffset instant, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(instant, zone);
        if (hours.HolidaysEnabled)
        {
            if (hours.Holidays is null)
            {
                return OpenState.Unknown;
            }

            if (hours.Holidays.Contains(DateOnly.FromDateTime(local.DateTime)))
            {
                return OpenState.Closed;
            }
        }

        var minute = (int)local.TimeOfDay.TotalMinutes;
        var today = Ranges(hours, local.DayOfWeek);
        var yesterday = Ranges(hours, (DayOfWeek)(((int)local.DayOfWeek + 6) % 7));
        var open = today.Any(r => minute >= r.From && minute < End(r))
            // A range from yesterday that runs past midnight.
            || yesterday.Any(r => End(r) > Day && minute < End(r) - Day);
        return open ? OpenState.Open : OpenState.Closed;
    }

    /// <summary>
    /// Of the time nodes under one parent, the one a call at <paramref name="instant"/> went to: the first whose hours
    /// cover it, otherwise the default. Null when that cannot be settled, or when nothing covers it and there is no default.
    /// </summary>
    public static SwitchboardNode? Branch(IEnumerable<SwitchboardNode> timeNodes, DateTimeOffset instant, TimeZoneInfo zone)
    {
        SwitchboardNode? fallback = null;
        foreach (var node in timeNodes)
        {
            if (node.Setup.OpeningHours is not { } hours)
            {
                continue;
            }

            if (hours.IsDefault)
            {
                fallback ??= node;
                continue;
            }

            switch (Evaluate(hours, instant, zone))
            {
                case OpenState.Open:
                    return node;
                case OpenState.Unknown:
                    return null;
            }
        }

        return fallback;
    }

    private static IReadOnlyList<MinuteRange> Ranges(OpeningHours hours, DayOfWeek day) => hours.Days.TryGetValue(day, out var ranges) ? ranges : [];

    // Talk may write a range past midnight either as 22:00 to 02:00 or as 22:00 to 26:00; both end at 26:00 here.
    private static int End(MinuteRange range) => range.To <= range.From ? range.To + Day : range.To;
}
