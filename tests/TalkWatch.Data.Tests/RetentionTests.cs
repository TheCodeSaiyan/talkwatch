using Microsoft.EntityFrameworkCore;
using TalkWatch.Core.Calls;
using TalkWatch.Core.Talk;
using TalkWatch.Replay;

namespace TalkWatch.Data.Tests;

public sealed class RetentionTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // After every fixture call (the newest is 2026-09-28), so each period reaches back into them.
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed record Imported(TalkWatchDbContext Db, Guid SiteId, FixtureConsole Console, TalkClient Talk, AudioStore Store, IReadOnlyList<CallLogRecord> Calls);

    /// <summary>Every fixture call stored, and every recording the fixtures hold copied to disk.</summary>
    private async Task<Imported> ImportedAsync()
    {
        var connection = await postgres.NewDatabaseAsync();
        var siteId = Guid.NewGuid();
        var db = PostgresFixture.Context(connection, AccessScope.System(siteId));
        db.Sites.Add(new Site { Id = siteId, Name = "Test site", DefaultRegion = "GB", CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(Ct);

        var console = new FixtureConsole(FixtureConsole.DefaultDirectory);
        var talk = new TalkClient(console.CreateClient());
        await talk.SignInAsync(FixtureConsole.Username, FixtureConsole.Password, Ct);
        await new CallLogIngestor(talk, db, siteId, new NumberNormaliser("GB"), TimeProvider.System) { PageSize = 100 }.IngestAsync(Ct);
        var store = new AudioStore(Directory.CreateTempSubdirectory("talkwatch-retention-").FullName);
        await new AudioCopier(talk, db, siteId, store, TimeProvider.System) { MaxPerRun = 1000 }.CopyRecordingsAsync(Ct);
        return new Imported(db, siteId, console, talk, store, console.Calls());
    }

    private static async Task SetAsync(Imported i, RetentionPreset preset, int? callDays = null, int? audioDays = null, int? minimumDays = null)
    {
        i.Db.RetentionSettings.Add(new RetentionSettings
        {
            SiteId = i.SiteId, Preset = preset, CallDays = callDays, AudioDays = audioDays, MinimumDays = minimumDays, UpdatedAt = Now,
        });
        await i.Db.SaveChangesAsync(Ct);
    }

    private static Task<SweepResult> SweepAsync(Imported i) => new RetentionSweeper(i.Db, i.Store, new FixedClock(Now)).RunAsync(Ct);

    [Fact]
    public async Task A_sweep_removes_exactly_the_calls_and_audio_the_periods_say()
    {
        var i = await ImportedAsync();
        await SetAsync(i, RetentionPreset.Custom, callDays: 200, audioDays: 10);
        var callsBefore = Now.AddDays(-200);
        var audioBefore = Now.AddDays(-10);
        var files = await i.Db.AudioFiles.Where(a => a.State == AudioState.Copied)
            .Join(i.Db.Calls, a => a.CallId, c => c.Id, (a, c) => new { a.Id, a.RelativePath, c.Time, c.TalkUuid }).ToListAsync(Ct);
        Assert.Contains(files, f => f.Time < audioBefore && f.Time >= callsBefore);
        Assert.Contains(files, f => f.Time >= audioBefore);

        var result = await SweepAsync(i);
        i.Db.ChangeTracker.Clear();

        // Worked out from the fixture records, not from the database the sweep changed.
        var removed = i.Calls.Count(c => c.Time < callsBefore);
        Assert.True(removed > 0, "The period should reach back into the fixture calls.");
        Assert.Equal(removed, result.Calls);
        Assert.Equal(i.Calls.Count - removed, await i.Db.Calls.CountAsync(Ct));
        Assert.False(await i.Db.Calls.AnyAsync(c => c.Time < callsBefore, Ct));
        Assert.False(await i.Db.CallEvents.AnyAsync(e => !i.Db.Calls.Any(c => c.Id == e.CallId), Ct));

        Assert.Equal(files.Count(f => f.Time < audioBefore), result.Files);
        foreach (var file in files)
        {
            var onDisk = File.Exists(Path.Combine(i.Store.Root, file.RelativePath!));
            Assert.Equal(file.Time >= audioBefore, onDisk);
        }

        var kept = await i.Db.AudioFiles.Where(a => a.State == AudioState.Expired).Join(i.Db.Calls, a => a.CallId, c => c.Id, (a, c) => c.Time).ToListAsync(Ct);
        Assert.All(kept, time => Assert.InRange(time, callsBefore, audioBefore));
        Assert.Equal(result.Calls, (await i.Db.RetentionSettings.SingleAsync(Ct)).LastSweepCalls);
    }

    [Fact]
    public async Task Transcripts_are_removed_with_the_audio_not_the_calls()
    {
        var i = await ImportedAsync();
        var (transcripts, _) = await i.Talk.GetTranscriptsAsync(1, 50, Ct);
        await TranscriptStore.UpsertAsync(i.Db, i.SiteId, transcripts, Now, Ct);
        var times = await i.Db.CallTranscripts.Join(i.Db.Calls, t => t.CallId, c => c.Id, (t, c) => c.Time).OrderBy(t => t).ToListAsync(Ct);
        // An audio period that falls among the transcribed calls; calls kept far longer.
        var audioBefore = times[times.Count / 2];
        await SetAsync(i, RetentionPreset.Custom, callDays: 3650, audioDays: (int)Math.Ceiling((Now - audioBefore).TotalDays));
        var cutoff = Now.AddDays(-(int)Math.Ceiling((Now - audioBefore).TotalDays));

        await SweepAsync(i);
        i.Db.ChangeTracker.Clear();

        var left = await i.Db.CallTranscripts.Join(i.Db.Calls, t => t.CallId, c => c.Id, (t, c) => c.Time).ToListAsync(Ct);
        Assert.Equal(times.Count(t => t >= cutoff), left.Count);
        Assert.True(left.Count < times.Count, "The period should reach back into the transcribed calls.");
        Assert.Equal(i.Calls.Count, await i.Db.Calls.CountAsync(Ct));
    }

    [Fact]
    public async Task The_console_does_not_feed_back_what_a_sweep_removed()
    {
        var i = await ImportedAsync();
        await SetAsync(i, RetentionPreset.Custom, callDays: 200);
        await SweepAsync(i);
        var remaining = await i.Db.Calls.CountAsync(Ct);
        var settings = await i.Db.RetentionSettings.SingleAsync(Ct);

        // A first run over the whole history again, as after a restore: the old calls are on the console still.
        await new CallLogIngestor(i.Talk, i.Db, i.SiteId, new NumberNormaliser("GB"), TimeProvider.System)
        {
            PageSize = 10, KeepSince = settings.KeepCallsSince(Now),
        }.IngestAsync(Ct);

        Assert.Equal(remaining, await i.Db.Calls.CountAsync(Ct));
    }

    [Theory]
    [InlineData(RetentionPreset.Fca)]          // at least 5 years: every fixture call is younger
    [InlineData(RetentionPreset.LegalHold)]
    [InlineData(RetentionPreset.KeepEverything)]
    public async Task Nothing_is_removed_under_a_minimum_a_hold_or_no_policy(RetentionPreset preset)
    {
        var i = await ImportedAsync();
        var (calls, audio, minimum) = Retention.Of(preset);
        await SetAsync(i, preset, calls, audio, minimum);
        var before = await i.Db.Calls.CountAsync(Ct);

        var result = await SweepAsync(i);

        Assert.Equal(new SweepResult(0, 0), result);
        Assert.Equal(before, await i.Db.Calls.CountAsync(Ct));
    }

    [Fact]
    public async Task A_hold_keeps_everything_even_with_periods_set()
    {
        var i = await ImportedAsync();
        await SetAsync(i, RetentionPreset.LegalHold, callDays: 1, audioDays: 1);

        Assert.Equal(new SweepResult(0, 0), await SweepAsync(i));
    }

    [Fact]
    public async Task A_report_copy_goes_once_every_call_it_covers_has_gone()
    {
        var i = await ImportedAsync();
        var report = new Report { Id = Guid.NewGuid(), SiteId = i.SiteId, Name = "Weekly", CreatedAt = Now };
        i.Db.Reports.Add(report);
        ReportRun Copy(DateTimeOffset to) => new()
        {
            Id = Guid.NewGuid(), SiteId = i.SiteId, ReportId = report.Id, ReportName = report.Name,
            From = to.AddDays(-7), To = to, CreatedAt = to, Html = "<p>+441144960042</p>", Csv = "from\n+441144960042",
        };
        var gone = Copy(Now.AddDays(-201));
        var straddling = Copy(Now.AddDays(-197));
        var recent = Copy(Now.AddDays(-1));
        i.Db.ReportRuns.AddRange(gone, straddling, recent);
        await i.Db.SaveChangesAsync(Ct);
        await SetAsync(i, RetentionPreset.Custom, callDays: 200);

        await SweepAsync(i);

        // A copy holds callers' numbers and, with the call list, every call: it can't outlive the calls themselves.
        var kept = await i.Db.ReportRuns.AsNoTracking().Select(r => r.Id).ToListAsync(Ct);
        Assert.Equal(new[] { straddling.Id, recent.Id }.Order(), kept.Order());
        Assert.True(await i.Db.Reports.AnyAsync(r => r.Id == report.Id, Ct), "The report itself is a setting, not a record of calls, and stays.");
    }

    [Theory]
    [InlineData(30, 30, 60)]    // shorter than the minimum
    [InlineData(100, 200, null)] // audio outliving its call
    [InlineData(0, null, null)]
    public void Periods_that_cannot_be_kept_are_refused(int? callDays, int? audioDays, int? minimumDays) =>
        Assert.NotNull(Retention.Problem(callDays, audioDays, minimumDays));

    [Fact]
    public void The_fca_preset_keeps_at_least_five_years_and_at_most_seven()
    {
        var (calls, audio, minimum) = Retention.Of(RetentionPreset.Fca);

        Assert.True(minimum >= 5 * 365);
        Assert.True(calls <= (7 * 365) + 2 && audio == calls);
        Assert.Null(Retention.Problem(calls, audio, minimum));
    }
}
