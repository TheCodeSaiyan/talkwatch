using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TalkWatch.Core.Calls;
using TalkWatch.Core.Talk;
using TalkWatch.Data;

namespace TalkWatch.Web.Services;

/// <summary>What the poller last did, for the status line on every page.</summary>
public sealed class IngestionStatus
{
    public DateTimeOffset? LastSuccess { get; set; }
    public DateTimeOffset? LastAttempt { get; set; }
    public string? LastError { get; set; }

    /// <summary>Talk answered in a shape TalkWatch does not recognise. Nothing is written until a fixed release ships.</summary>
    public bool Drifted { get; set; }

    public int CallsAddedLastRun { get; set; }

    /// <summary>The console asked TalkWatch to slow down (HTTP 429); no request is sent before this time.</summary>
    public DateTimeOffset? BackOffUntil { get; set; }

    /// <summary>Raised after calls are stored or changed, by the poll or the live feed, so live views read them again.</summary>
    public event Action? CallsStored;

    public void Stored() => CallsStored?.Invoke();
}

/// <summary>Copies new calls from the console on a schedule.</summary>
public sealed partial class CallLogPoller(
    IServiceScopeFactory scopes, TalkSession session, ConsoleVersionMonitor versions, LineDirectorySync directory, TranscriptSync transcripts, ConsoleHealthMonitor health, AlertService alerts, IOptions<TalkOptions> options, IngestionStatus status,
    AudioStore audio, TimeProvider clock, ILogger<CallLogPoller> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.Value.ConsoleUrl is null)
        {
            LogDisabled(logger);
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(10, options.Value.PollSeconds)), clock);
        do
        {
            await RunOnceAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// One poll. Nothing escapes: an exception out of a background service stops the whole app, which is how the first
    /// real run turned one bad row into a restart loop. Failures are recorded and the next tick tries again.
    /// </summary>
    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        using var activity = Telemetry.Source.StartActivity("poll");
        try
        {
            // One console session, shared with the live listener (TalkSession). Signing in on every poll is what got
            // the first real run rate-limited.
            if (session.BackingOff)
            {
                return;
            }

            status.LastAttempt = clock.GetUtcNow();
            using var scope = scopes.CreateScope();
            scope.ServiceProvider.GetRequiredService<AccessScopeHolder>().UseSystemScope();
            var db = scope.ServiceProvider.GetRequiredService<TalkWatchDbContext>();
            var site = scope.ServiceProvider.GetRequiredService<CurrentSite>();

            if (!await session.EnsureSignedInAsync(cancellationToken))
            {
                return;
            }

            // What the console runs, at least hourly: a newer or pre-release build is where endpoints change first.
            await versions.CheckIfDueAsync(cancellationToken);
            await directory.RefreshIfDueAsync(cancellationToken);
            await health.CheckIfDueAsync(cancellationToken);

            var retention = await db.RetentionSettings.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
            var result = await new CallLogIngestor(session.Client, db, site.Id, new NumberNormaliser(site.Region), clock, directory.Current, alerts.RaiseForCallsAsync)
            {
                KeepSince = retention.KeepCallsSince(clock.GetUtcNow()),
            }.IngestAsync(cancellationToken);
            await new CallLogIngestor(null, db, site.Id, new NumberNormaliser(site.Region), clock).SettleAsync(cancellationToken);

            status.LastSuccess = clock.GetUtcNow();
            status.LastError = null;
            status.Drifted = false;
            status.BackOffUntil = null;
            status.CallsAddedLastRun = result.CallsAdded;
            if (result.CallsAdded + result.CallsUpdated > 0)
            {
                status.Stored();
            }

            LogIngested(logger, result.CallsAdded, result.CallsUpdated, result.PagesRead);
            Telemetry.Polls.Add(1, new KeyValuePair<string, object?>("result", "ok"));
            Telemetry.CallsStored.Add(result.CallsAdded, new("source", "poll"), new("change", "added"));
            Telemetry.CallsStored.Add(result.CallsUpdated, new("source", "poll"), new("change", "updated"));

            // Recordings and voicemail after calls, over the same session, a few of each per poll.
            var copier = new AudioCopier(session.Client, db, site.Id, audio, clock);
            foreach (var (kind, copy) in new[] { ("Recordings", await copier.CopyRecordingsAsync(cancellationToken)), ("Voicemail", await copier.CopyVoicemailAsync(cancellationToken)) })
            {
                if (copy.Copied + copy.Unavailable + copy.Failed > 0)
                {
                    LogCopied(logger, kind, copy.Copied, copy.Unavailable, copy.Failed);
                }

                Telemetry.AudioCopies.Add(copy.Copied, new("kind", kind), new("result", "copied"));
                Telemetry.AudioCopies.Add(copy.Unavailable, new("kind", kind), new("result", "unavailable"));
                Telemetry.AudioCopies.Add(copy.Failed, new("kind", kind), new("result", "failed"));
            }

            // Transcripts after the calls they belong to; the live feed brings each as Talk finishes it.
            await transcripts.SyncIfDueAsync(cancellationToken);
        }
        catch (TalkRateLimitedException e)
        {
            Telemetry.Polls.Add(1, new KeyValuePair<string, object?>("result", "rate_limited"));
            session.BackOff(e.RetryAfter ?? TalkSession.RateLimitBackOff, e);
        }
        catch (TalkSchemaException e)
        {
            Telemetry.Polls.Add(1, new KeyValuePair<string, object?>("result", "drift"));
            activity?.SetStatus(ActivityStatusCode.Error, "drift");
            if (!status.Drifted)
            {
                await alerts.RaiseDriftAsync(e.Message, cancellationToken);
            }

            status.Drifted = true;
            status.LastError = e.Message;
            LogDrift(logger, e);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // Deliberately catches everything: see the summary.
        catch (Exception e)
#pragma warning restore CA1031
        {
            Telemetry.Polls.Add(1, new KeyValuePair<string, object?>("result", "failed"));
            activity?.SetStatus(ActivityStatusCode.Error, e.GetType().Name);
            status.LastError = e.Message;
            LogFailed(logger, e);
        }
        finally
        {
            _gate.Release();
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Polling is off: Talk__ConsoleUrl is not set.")]
    private static partial void LogDisabled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Call log: {Added} new, {Updated} updated, {Pages} page(s) read.")]
    private static partial void LogIngested(ILogger logger, int added, int updated, int pages);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Talk's call log has changed shape. Nothing is written until TalkWatch is updated.")]
    private static partial void LogDrift(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Kind}: {Copied} copied, {Unavailable} no longer on the console, {Failed} failed and will be retried.")]
    private static partial void LogCopied(ILogger logger, string kind, int copied, int unavailable, int failed);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Polling the console failed; the next poll will try again.")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
