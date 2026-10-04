using Microsoft.EntityFrameworkCore;
using TalkWatch.Core.Talk;

namespace TalkWatch.Data;

public sealed record CopyResult(int Copied, int Unavailable, int Failed);

/// <summary>
/// Copies call recordings and voicemail from the console into TalkWatch's own storage, a few per run so a first run
/// over a long history does not hammer the console. Idempotent: audio copied, or known to be gone, is not fetched again.
/// </summary>
public sealed class AudioCopier(TalkClient talk, TalkWatchDbContext db, Guid siteId, AudioStore store, TimeProvider clock)
{
    public const int MaxAttempts = 5;

    public int MaxPerRun { get; init; } = 20;

    public Task<CopyResult> CopyRecordingsAsync(CancellationToken cancellationToken) =>
        CopyAsync(AudioKind.Recording, db.Calls.Where(c => c.SiteId == siteId && c.HasRecording),
            talk.GetRecordingAsync, "The console no longer has this recording.", cancellationToken);

    /// <summary>
    /// Voicemail messages: every call whose events say a message was recorded. Talk keeps a message by file path, so
    /// its details are read first and the audio fetched by that path.
    /// </summary>
    public Task<CopyResult> CopyVoicemailAsync(CancellationToken cancellationToken) =>
        CopyAsync(AudioKind.Voicemail, db.Calls.Where(c => c.SiteId == siteId && c.Events.Any(e => e.Event == "vm_msg_recorded")),
            FetchVoicemailAsync, "The console no longer has this voicemail.", cancellationToken);

    private async Task<HttpResponseMessage?> FetchVoicemailAsync(string callUuid, CancellationToken cancellationToken) =>
        await talk.GetVoicemailAsync(callUuid, cancellationToken) is { FilePath: { Length: > 0 } path }
            ? await talk.GetVoicemailAudioAsync(path, cancellationToken)
            : null;

    private async Task<CopyResult> CopyAsync(
        AudioKind kind, IQueryable<CallRow> calls, Func<string, CancellationToken, Task<HttpResponseMessage?>> fetch, string gone,
        CancellationToken cancellationToken)
    {
        var due = await calls
            .Where(c => !db.AudioFiles.Any(a => a.CallId == c.Id && a.Kind == kind
                && (a.State != AudioState.Failed || a.Attempts >= MaxAttempts)))
            .OrderByDescending(c => c.Time)
            .Take(MaxPerRun)
            .Select(c => new { c.Id, c.TalkUuid, c.Time })
            .ToListAsync(cancellationToken);

        int copied = 0, unavailable = 0, failed = 0;
        foreach (var call in due)
        {
            var row = await db.AudioFiles.SingleOrDefaultAsync(a => a.CallId == call.Id && a.Kind == kind, cancellationToken);
            if (row is null)
            {
                row = new AudioFile { Id = Guid.NewGuid(), SiteId = siteId, CallId = call.Id, Kind = kind };
                db.AudioFiles.Add(row);
            }

            row.Attempts++;
            row.LastAttemptAt = clock.GetUtcNow();
            try
            {
                using var response = await fetch(call.TalkUuid, cancellationToken);
                if (response is null)
                {
                    row.State = AudioState.Unavailable;
                    row.LastError = gone;
                    unavailable++;
                }
                else
                {
                    await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
                    var type = response.Content.Headers.ContentType?.MediaType ?? "audio/mpeg";
                    var (path, hash, size) = await store.SaveAsync(body, kind, call.TalkUuid, call.Time, type == "audio/mpeg" ? ".mp3" : ".bin", cancellationToken);
                    row.State = AudioState.Copied;
                    row.RelativePath = path;
                    row.Sha256 = hash;
                    row.SizeBytes = size;
                    row.ContentType = type;
                    row.CopiedAt = clock.GetUtcNow();
                    row.LastError = null;
                    copied++;
                }
            }
            catch (Exception e) when (e is TalkApiException or HttpRequestException or IOException && e is not TalkRateLimitedException)
            {
                // One file failing does not stop the rest; it is tried again on later runs.
                row.State = AudioState.Failed;
                row.LastError = e.Message;
                failed++;
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        return new CopyResult(copied, unavailable, failed);
    }
}
