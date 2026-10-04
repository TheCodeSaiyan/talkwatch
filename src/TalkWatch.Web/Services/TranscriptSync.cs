using Microsoft.Extensions.Options;
using TalkWatch.Core.Talk;
using TalkWatch.Data;

namespace TalkWatch.Web.Services;

/// <summary>
/// Copies Talk's transcripts: one as soon as the live feed says Talk has finished it, and the transcript list at least
/// hourly, newest first, until a page brings nothing new (all of it, the first time). Off with Talk__CopyTranscripts.
/// A failure here is logged and never stops calls being copied.
/// </summary>
public sealed partial class TranscriptSync(
    TalkSession session, IServiceScopeFactory scopes, AlertService alerts, AnsweringLineCheck answeringLines, IOptions<TalkOptions> talk, TimeProvider clock,
    ILogger<TranscriptSync> logger)
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    public const int PageSize = 50;
    public const int MaxPages = 200;

    /// <summary>A call rated negative alerts only while it is this recent: a first copy of old transcripts alerts on nothing.</summary>
    public static readonly TimeSpan RecentEnoughToAlert = TimeSpan.FromHours(24);

    private DateTimeOffset? _syncedAt;

    public Task SyncIfDueAsync(CancellationToken cancellationToken) =>
        _syncedAt is { } last && clock.GetUtcNow() - last < Interval ? Task.CompletedTask : SyncAsync(cancellationToken);

    public async Task SyncAsync(CancellationToken cancellationToken)
    {
        if (!talk.Value.CopyTranscripts)
        {
            return;
        }

        await GuardAsync(async () =>
        {
            for (var page = 1; page <= MaxPages; page++)
            {
                var (transcripts, total) = await session.Client.GetTranscriptsAsync(page, PageSize, cancellationToken);
                var changed = await StoreAsync(transcripts, cancellationToken);
                if (changed == 0 || transcripts.Count < PageSize || page * PageSize >= total)
                {
                    break;
                }
            }

            _syncedAt = clock.GetUtcNow();
        }, cancellationToken);
    }

    /// <summary>A transcript the live feed brought.</summary>
    public async Task HandleAsync(TalkTranscript transcript, CancellationToken cancellationToken)
    {
        if (talk.Value.CopyTranscripts)
        {
            await GuardAsync(() => StoreAsync([transcript], cancellationToken), cancellationToken);
        }
    }

    /// <summary>Stores what is new or changed and alerts on recent calls rated negative. Returns how many changed.</summary>
    private async Task<int> StoreAsync(IReadOnlyList<TalkTranscript> transcripts, CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        scope.ServiceProvider.GetRequiredService<AccessScopeHolder>().UseSystemScope();
        var db = scope.ServiceProvider.GetRequiredService<TalkWatchDbContext>();
        var site = scope.ServiceProvider.GetRequiredService<CurrentSite>();
        var (added, changed) = await TranscriptStore.UpsertAsync(db, site.Id, transcripts, clock.GetUtcNow(), cancellationToken);
        foreach (var (callId, transcript) in added.Where(a => a.Transcript.SentimentClass == "negative"))
        {
            await alerts.RaiseNegativeCallAsync(callId, RecentEnoughToAlert, cancellationToken);
        }

        // A call an outside answering line took: did its voicemail take it? Before the alert below, so a message left
        // there raises Voicemail and then Voicemail transcribed in this same pass.
        foreach (var (callId, transcript) in added)
        {
            await answeringLines.CheckAsync(callId, TranscriptStore.LinesOf(transcript), RecentEnoughToAlert, cancellationToken);
        }

        // Every new transcript: the alert raises only for a voicemail, and once per call however often it comes.
        foreach (var (callId, transcript) in added)
        {
            await alerts.RaiseVoicemailTranscribedAsync(callId, transcript.Text, RecentEnoughToAlert, cancellationToken);
        }

        if (changed > 0)
        {
            LogCopied(logger, changed);
        }

        return changed;
    }

    private async Task GuardAsync(Func<Task> work, CancellationToken cancellationToken)
    {
        try
        {
            await work();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // Transcripts are extra: a failure must not stop the poll or the live feed.
        catch (Exception e)
#pragma warning restore CA1031
        {
            LogFailed(logger, e);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Copied {Count} transcripts, new or changed.")]
    private static partial void LogCopied(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not copy Talk's transcripts; trying again later.")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
