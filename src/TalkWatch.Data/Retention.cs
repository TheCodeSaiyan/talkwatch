using Microsoft.EntityFrameworkCore;

namespace TalkWatch.Data;

public enum RetentionPreset
{
    /// <summary>Nothing is removed. The default, so installing TalkWatch never deletes anything by surprise.</summary>
    KeepEverything,

    /// <summary>UK GDPR sets no fixed period, only 'no longer than necessary': these are starting points to adjust.</summary>
    UkGdpr,

    /// <summary>As UK GDPR: the same principle, the same starting points.</summary>
    EuGdpr,

    /// <summary>FCA COBS 11.8: recordings kept at least 5 years, and up to 7 where the FCA asks.</summary>
    Fca,

    /// <summary>Nothing is removed, whatever else is set, until the hold is lifted.</summary>
    LegalHold,

    Custom,
}

/// <summary>How long a site keeps its calls and audio, as set on the retention page.</summary>
public sealed class RetentionSettings
{
    public Guid SiteId { get; set; }
    public RetentionPreset Preset { get; set; }

    /// <summary>Calls, their events, lines, stored responses, alerts and report copies are removed after this many days; null keeps them.</summary>
    public int? CallDays { get; set; }

    /// <summary>Recordings and voicemail are removed after this many days; null keeps them while their call is kept.</summary>
    public int? AudioDays { get; set; }

    /// <summary>Nothing younger than this many days is removed: the periods above cannot be set below it.</summary>
    public int? MinimumDays { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? LastSweepAt { get; set; }
    public int LastSweepCalls { get; set; }
    public int LastSweepFiles { get; set; }

    public bool Held => Preset == RetentionPreset.LegalHold;
}

public sealed record SweepResult(int Calls, int Files);

public static class Retention
{
    private const int Year = 365;

    /// <summary>What a preset sets: call days, audio days and minimum days.</summary>
    public static (int? CallDays, int? AudioDays, int? MinimumDays) Of(RetentionPreset preset) => preset switch
    {
        RetentionPreset.UkGdpr or RetentionPreset.EuGdpr => (2 * Year, Year, null),
        RetentionPreset.Fca => ((7 * Year) + 2, (7 * Year) + 2, (5 * Year) + 1),
        _ => (null, null, null),
    };

    /// <summary>Why a set of periods cannot be used, or null when it can.</summary>
    public static string? Problem(int? callDays, int? audioDays, int? minimumDays)
    {
        if (callDays is < 1 || audioDays is < 1 || minimumDays is < 0)
        {
            return "Periods are whole days, one or more.";
        }

        if (minimumDays is { } minimum && (callDays < minimum || audioDays < minimum))
        {
            return $"Nothing younger than the minimum of {minimum} days can be removed, so no period can be shorter than it.";
        }

        return audioDays > callDays
            ? "Audio goes with its call, so it cannot be kept longer than the call."
            : null;
    }

    /// <summary>
    /// The oldest call a site still keeps, or null when it keeps them all. The poller and the live feed store nothing
    /// older, so the console cannot feed back what a sweep removed.
    /// </summary>
    public static DateTimeOffset? KeepCallsSince(this RetentionSettings? settings, DateTimeOffset now) =>
        settings is { Held: false, CallDays: { } days } ? now.AddDays(-Math.Max(days, settings.MinimumDays ?? 0)) : null;

    public static DateTimeOffset? KeepAudioSince(this RetentionSettings? settings, DateTimeOffset now) =>
        settings is { Held: false, AudioDays: { } days } ? now.AddDays(-Math.Max(days, settings.MinimumDays ?? 0)) : null;
}

/// <summary>
/// Removes what the retention settings say has been kept long enough: audio first, then whole calls with their events,
/// lines, audio, the console responses that held them, and the alerts raised about them. The audit log is kept.
/// </summary>
public sealed class RetentionSweeper(TalkWatchDbContext db, AudioStore store, TimeProvider clock)
{
    public const int Batch = 500;

    public async Task<SweepResult> RunAsync(CancellationToken cancellationToken)
    {
        var settings = await db.RetentionSettings.SingleOrDefaultAsync(cancellationToken);
        var now = clock.GetUtcNow();
        var files = 0;
        var calls = 0;

        // Audio past its own period, and audio of calls about to go: the file first, so none outlives its row.
        var audioBefore = settings.KeepAudioSince(now);
        var callsBefore = settings.KeepCallsSince(now);
        var before = new[] { audioBefore, callsBefore }.Where(d => d is not null).Max();
        if (before is { } cutoff)
        {
            while (true)
            {
                var due = await db.AudioFiles
                    .Where(a => a.State == AudioState.Copied && db.Calls.Any(c => c.Id == a.CallId && c.Time < cutoff))
                    .Take(Batch)
                    .ToListAsync(cancellationToken);
                if (due.Count == 0)
                {
                    break;
                }

                foreach (var audio in due)
                {
                    if (audio.RelativePath is { } path)
                    {
                        store.Delete(path);
                    }

                    audio.State = AudioState.Expired;
                    audio.RelativePath = null;
                    audio.LastError = "Removed under the retention policy.";
                    files++;
                }

                await db.SaveChangesAsync(cancellationToken);
            }
        }

        // Transcripts are what was said, like the audio, so they keep to the audio's period.
        if (before is { } transcriptCutoff)
        {
            await db.CallTranscripts.Where(t => db.Calls.Any(c => c.Id == t.CallId && c.Time < transcriptCutoff)).ExecuteDeleteAsync(cancellationToken);
        }

        if (callsBefore is { } callCutoff)
        {
            // Events, lines and audio rows go with their call.
            calls = await db.Calls.Where(c => c.Time < callCutoff).ExecuteDeleteAsync(cancellationToken);
            await db.RawPayloads.Where(r => r.FetchedAt < callCutoff).ExecuteDeleteAsync(cancellationToken);
            await db.AlertEvents.Where(e => e.At < callCutoff).ExecuteDeleteAsync(cancellationToken);

            // A report copy holds callers' numbers, and every call with the call list, so it goes once the last call it
            // covers has; its deliveries go with it. The report itself is a setting and stays.
            await db.ReportRuns.Where(r => r.To < callCutoff).ExecuteDeleteAsync(cancellationToken);
        }

        if (settings is not null)
        {
            settings.LastSweepAt = now;
            settings.LastSweepCalls = calls;
            settings.LastSweepFiles = files;
            await db.SaveChangesAsync(cancellationToken);
        }

        return new SweepResult(calls, files);
    }
}
