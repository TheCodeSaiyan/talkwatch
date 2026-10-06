using Microsoft.EntityFrameworkCore;
using TalkWatch.Core.Calls;
using TalkWatch.Core.Talk;
using TalkWatch.Replay;

namespace TalkWatch.Data.Tests;

public sealed class CallStatisticsTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
    private static readonly NumberNormaliser Uk = new("GB");

    private async Task<(TalkWatchDbContext Db, IReadOnlyList<CallLogRecord> Calls)> ImportedAsync()
    {
        var connection = await postgres.NewDatabaseAsync();
        var siteId = Guid.NewGuid();
        var db = PostgresFixture.Context(connection, AccessScope.System(siteId));
        db.Sites.Add(new Site { Id = siteId, Name = "Test site", DefaultRegion = "GB", CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(Ct);

        var console = new FixtureConsole(FixtureConsole.DefaultDirectory);
        var talk = new TalkClient(console.CreateClient());
        await talk.SignInAsync(FixtureConsole.Username, FixtureConsole.Password, Ct);
        await new CallLogIngestor(talk, db, siteId, Uk, TimeProvider.System) { PageSize = 100 }.IngestAsync(Ct);
        return (db, console.Calls());
    }

    private static CallOutcome OutcomeOf(CallLogRecord c) => CallOutcomes.Of(c.Direction, c.Status, [.. c.CallEvents.Select(e => e.Event)]);

    private static bool IsInbound(CallOutcome o) =>
        o is CallOutcome.Answered or CallOutcome.Missed or CallOutcome.Voicemail or CallOutcome.HungUpAtSwitchboard or CallOutcome.InProgress or CallOutcome.Unknown;

    [Fact]
    public async Task Every_figure_matches_one_worked_out_by_hand_from_the_fixture_records()
    {
        var (db, records) = await ImportedAsync();
        var end = records.Max(c => c.Time).AddSeconds(1);
        var start = end.AddDays(-CallStatistics.LongestPeriodDays);
        var inPeriod = records.Where(c => c.Time >= start && c.Time < end).ToList();

        var stats = await CallStatistics.ComputeAsync(db, start, end, London, Ct);

        // The same figures, worked out from the records Talk sent rather than from the database.
        var outcomes = inPeriod.Select(c => (Call: c, Outcome: OutcomeOf(c))).ToList();
        var inbound = outcomes.Where(o => IsInbound(o.Outcome)).ToList();
        int Of(CallOutcome o) => outcomes.Count(x => x.Outcome == o);
        var answered = outcomes.Where(o => o.Outcome == CallOutcome.Answered).Select(o => o.Call.Duration ?? 0).ToList();
        var hours = new int[24];
        inbound.ForEach(o => hours[TimeZoneInfo.ConvertTime(o.Call.Time, London).Hour]++);

        // 41 inbound calls in the fixtures' last 92 days, answered and not; if that changes, the test says so.
        Assert.True(inbound.Count >= 30, $"Only {inbound.Count} inbound fixture calls in the period.");
        Assert.True(outcomes.Select(o => o.Outcome).Distinct().Count() >= 3, "The period should hold several outcomes.");
        Assert.Equal(inbound.Count, stats.Inbound);
        foreach (var outcome in Enum.GetValues<CallOutcome>())
        {
            Assert.Equal(Of(outcome), stats.Count(outcome));
        }

        Assert.Equal((double)Of(CallOutcome.Answered) / (Of(CallOutcome.Answered) + Of(CallOutcome.Missed) + Of(CallOutcome.Voicemail)), stats.AnswerRate);
        Assert.Equal(TimeSpan.FromSeconds(Math.Round(answered.Average())), stats.AverageAnswered);
        Assert.Equal(hours, stats.InboundByHour);
        Assert.Equal(
            inbound.GroupBy(o => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(o.Call.Time, London).DateTime)).OrderBy(g => g.Key)
                .Select(g => new DayStatistics(g.Key, g.Count(), g.Count(o => o.Outcome == CallOutcome.Answered), g.Count(o => o.Outcome == CallOutcome.Missed))),
            stats.ByDay);

        var expectedLines = inbound
            .SelectMany(o => CallRouting.TouchedLines(o.Call, Uk).Select(line => (Line: line, o.Outcome)))
            .GroupBy(x => x.Line)
            .ToDictionary(g => g.Key, g => (Inbound: g.Count(), Answered: g.Count(x => x.Outcome == CallOutcome.Answered), Missed: g.Count(x => x.Outcome == CallOutcome.Missed)));
        Assert.Equal(expectedLines.Count, stats.ByLine.Count);
        Assert.All(stats.ByLine, line =>
            Assert.Equal(expectedLines[new LineRef(line.Kind, line.Key)], (line.Inbound, line.Answered, line.Missed)));
    }

    [Fact]
    public async Task Answer_speed_quality_and_the_missed_grid_match_ones_worked_out_from_the_fixture_records()
    {
        var (db, records) = await ImportedAsync();
        var end = records.Max(c => c.Time).AddSeconds(1);
        var start = end.AddDays(-CallStatistics.LongestPeriodDays);
        var inPeriod = records.Where(c => c.Time >= start && c.Time < end).Select(c => (Call: c, Outcome: OutcomeOf(c))).ToList();

        var stats = await CallStatistics.ComputeAsync(db, start, end, London, Ct);

        TimeSpan? Between(CallLogRecord call, string to) =>
            call.CallEvents.FirstOrDefault(e => e.Event == "call_started") is { } s && call.CallEvents.FirstOrDefault(e => e.Event == to) is { } t && t.Time >= s.Time ? t.Time - s.Time : null;
        TimeSpan? Median(List<TimeSpan> l) => l.Count == 0 ? null : l.Count % 2 == 1 ? l[l.Count / 2] : (l[(l.Count / 2) - 1] + l[l.Count / 2]) / 2;
        var ring = inPeriod.Where(o => o.Outcome == CallOutcome.Answered).Select(o => Between(o.Call, "call_accepted")).OfType<TimeSpan>().Order().ToList();
        var gaveUp = inPeriod.Where(o => o.Outcome is CallOutcome.Missed or CallOutcome.Voicemail).Select(o => Between(o.Call, "call_hangup")).OfType<TimeSpan>().Order().ToList();
        Assert.True(ring.Count >= 5, $"Only {ring.Count} answered fixture calls could be timed.");
        Assert.Equal(Median(ring), stats.Speed.MedianRing);
        Assert.Equal(ring[(int)Math.Ceiling(ring.Count * 0.9) - 1], stats.Speed.SlowestTenthRing);
        Assert.Equal(Median(gaveUp), stats.Speed.MedianGiveUp);

        var scores = inPeriod.Where(o => o.Call.QualityScore is not null).Select(o => o.Call.QualityScore!.Value).Order().ToList();
        Assert.True(scores.Count > 0, "The fixtures should hold quality scores.");
        Assert.Equal((scores.Count, scores.Count(q => q < QualityStatistics.PoorBelow), scores[scores.Count / 2]), (stats.Quality.Scored, stats.Quality.Poor, stats.Quality.Median!.Value));
        Assert.Equal(inPeriod.Where(o => o.Call.QualityScore < QualityStatistics.PoorBelow).Select(o => o.Call.QualityScore!.Value).Order().Take(5), stats.Quality.Worst.Select(w => w.Score));

        var inbound = inPeriod.Where(o => IsInbound(o.Outcome)).ToList();
        for (var day = 0; day < 7; day++)
        {
            for (var hour = 0; hour < 24; hour++)
            {
                var cell = inbound.Where(o => TimeZoneInfo.ConvertTime(o.Call.Time, London) is var local && local.DayOfWeek == CallStatistics.Week[day] && local.Hour == hour).ToList();
                Assert.Equal((cell.Count, cell.Count(o => o.Outcome is CallOutcome.Missed or CallOutcome.Voicemail)), stats.MissedByWeekdayAndHour[day][hour]);
            }
        }

        Assert.Equal(inbound.Count, stats.MissedByWeekdayAndHour.Sum(row => row.Sum(c => c.Inbound)));
    }

    [Fact]
    public async Task A_person_sees_figures_only_for_the_lines_they_are_granted()
    {
        var (admin, records) = await ImportedAsync();
        var did = await admin.CallLines.Where(l => l.Kind == LineKind.Did).GroupBy(l => l.Key).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstAsync(Ct);
        var viewerId = Guid.NewGuid();
        var siteId = await admin.Sites.Select(s => s.Id).SingleAsync(Ct);
        admin.Users.Add(new AppUser { Id = viewerId, SiteId = siteId, UserName = "viewer", NormalizedUserName = "VIEWER" });
        admin.Grants.Add(new Grant { Id = Guid.NewGuid(), SiteId = siteId, UserId = viewerId, Kind = LineKind.Did, Key = did, CreatedAt = DateTimeOffset.UtcNow });
        await admin.SaveChangesAsync(Ct);
        await using var viewer = PostgresFixture.Context(admin.Database.GetConnectionString()!, AccessScope.ForUser(siteId, viewerId, isAdmin: false));
        var end = records.Max(c => c.Time).AddSeconds(1);
        var start = end.AddDays(-CallStatistics.LongestPeriodDays);

        var stats = await CallStatistics.ComputeAsync(viewer, start, end, London, Ct);

        var onThatNumber = records.Count(c => c.Time >= start && IsInbound(OutcomeOf(c)) && CallRouting.TouchedLines(c, Uk).Contains(new LineRef(LineKind.Did, did)));
        Assert.Equal(onThatNumber, stats.Inbound);
        Assert.Equal([new LineRef(LineKind.Did, did)], stats.ByLine.Select(l => new LineRef(l.Kind, l.Key)));
    }

    // Talk names one contact by its numeric id on some calls and by its uuid on others. Counted per line, that was two
    // rows of the same name, and a call carrying both names counted on each; counted as one contact, it's one row with
    // each call once.
    [Fact]
    public async Task A_contact_talk_names_two_ways_is_one_line_with_each_call_counted_once()
    {
        var (db, records) = await ImportedAsync();
        var siteId = await db.Sites.Select(s => s.Id).SingleAsync(Ct);
        var end = records.Max(c => c.Time).AddSeconds(1);
        var start = end.AddDays(-CallStatistics.LongestPeriodDays);
        var calls = await db.Calls.Where(c => c.Time >= start && c.Time < end && c.Direction == "in").OrderBy(c => c.Time).Take(2).ToListAsync(Ct);
        db.Lines.AddRange(
            new LineRecord { SiteId = siteId, Kind = LineKind.Contact, Key = "9001", Name = "Morgan Nico", Present = true, UpdatedAt = DateTimeOffset.UtcNow },
            new LineRecord { SiteId = siteId, Kind = LineKind.Contact, Key = "contact-uuid-9001", Name = "Morgan Nico", Present = true, UpdatedAt = DateTimeOffset.UtcNow, SameAs = "9001" });
        // The first call names the contact both ways, the second only by its uuid.
        db.CallLines.AddRange(
            new CallLine { SiteId = siteId, CallId = calls[0].Id, Kind = LineKind.Contact, Key = "9001" },
            new CallLine { SiteId = siteId, CallId = calls[0].Id, Kind = LineKind.Contact, Key = "contact-uuid-9001" },
            new CallLine { SiteId = siteId, CallId = calls[1].Id, Kind = LineKind.Contact, Key = "contact-uuid-9001" });
        await db.SaveChangesAsync(Ct);

        var stats = await CallStatistics.ComputeAsync(db, start, end, London, Ct);

        var contact = Assert.Single(stats.ByLine, l => l.Name == "Morgan Nico");
        Assert.Equal((LineKind.Contact, "9001", 2), (contact.Kind, contact.Key, contact.Inbound));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(CallStatistics.LongestPeriodDays + 1)]
    public async Task A_period_that_is_empty_backwards_or_too_long_is_refused(int days)
    {
        var (db, _) = await ImportedAsync();
        var start = DateTimeOffset.UtcNow;

        await Assert.ThrowsAsync<ArgumentException>(() => CallStatistics.ComputeAsync(db, start, start.AddDays(days), London, Ct));
    }
}
