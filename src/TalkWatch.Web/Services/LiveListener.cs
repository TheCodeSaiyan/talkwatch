using Microsoft.EntityFrameworkCore;
using System.Net.WebSockets;
using TalkWatch.Core.Alerts;
using TalkWatch.Core.Calls;
using TalkWatch.Core.Talk;
using TalkWatch.Data;

namespace TalkWatch.Web.Services;

/// <summary>
/// Keeps Talk's live WebSocket open: calls are stored as they happen, and presence and handset state feed
/// <see cref="LiveStatus"/>. Polling carries on as the backstop for anything missed while disconnected.
/// </summary>
public sealed partial class LiveListener(
    TalkSession session, ConsoleConnection connection, LiveStatus live, ConsoleVersionMonitor versions, LineDirectorySync directory, TranscriptSync transcripts, AlertService alerts, IServiceScopeFactory scopes,
    Microsoft.Extensions.Options.IOptions<DemoOptions> demo, TimeProvider clock, IngestionStatus ingestion, ILogger<LiveListener> logger) : BackgroundService
{
    private static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LongestRetry = TimeSpan.FromMinutes(5);

    private readonly Lock _lock = new();
    private CancellationTokenSource? _connection;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // A replayed console in demo mode has no live feed to listen to.
        if (demo.Value.Enabled)
        {
            return;
        }

        connection.Changed += Reconnect;
        try
        {
            await ListenAsync(stoppingToken);
        }
        finally
        {
            connection.Changed -= Reconnect;
        }
    }

    private async Task ListenAsync(CancellationToken stoppingToken)
    {
        var retry = FirstRetry;
        while (!stoppingToken.IsCancellationRequested)
        {
            // One connection's life: ended early when the Console page changes the console or the way to it.
            using var current = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            lock (_lock)
            {
                _connection = current;
            }

            try
            {
                // The console can be set on the Console page while TalkWatch runs, so an unset one waits rather than stops.
                if (await session.ConsoleUrlAsync(current.Token) is { } console && await session.EnsureSignedInAsync(current.Token))
                {
                    // A reconnect often follows a console update or reboot, so the versions are read afresh each time.
                    await versions.CheckAsync(current.Token);
                    await directory.RefreshAsync(current.Token);
                    live.SetDirectory(directory.Current);
                    using var invoker = session.CreateInvoker();
                    live.SetConnected(true);
                    LogConnected(logger);
                    await foreach (var message in TalkLive.ReadAsync(console, invoker, current.Token))
                    {
                        await HandleAsync(message, current.Token);
                        retry = FirstRetry;
                    }

                    live.SetConnected(false, "The console closed the live connection.");
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException) when (current.IsCancellationRequested)
            {
                live.SetConnected(false, "The console's settings changed; reconnecting.");
                retry = FirstRetry;
                continue;
            }
#pragma warning disable CA1031 // The listener must outlive any failure; see the class summary.
            catch (Exception e)
#pragma warning restore CA1031
            {
                // A refused upgrade usually means the console ended the session: sign in again next time.
                if (e is WebSocketException && (e.Message.Contains("401", StringComparison.Ordinal) || e.Message.Contains("403", StringComparison.Ordinal)))
                {
                    session.Client.SessionEnded();
                }

                live.SetConnected(false, e.Message);
                LogDisconnected(logger, retry.TotalSeconds, e);
            }

            // New settings end the wait too, so a console just set on the Console page is listened to at once.
            await Task.Delay(retry, clock, current.Token).ContinueWith(_ => { }, TaskScheduler.Default);
            retry = TimeSpan.FromTicks(Math.Min(retry.Ticks * 2, LongestRetry.Ticks));
        }
    }

    private void Reconnect()
    {
        lock (_lock)
        {
            try
            {
                _connection?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Between connections: the next one reads the new settings anyway.
            }
        }
    }

    /// <summary>Handles one live message. A message that fails is logged and skipped; it never ends the connection.</summary>
    public async Task HandleAsync(LiveMessage message, CancellationToken cancellationToken)
    {
        try
        {
            // Handsets that were online and now are not: compare before and after this message. Any status other
            // than online counts, since only "online" has been seen from a console so far.
            var before = message.Event == LiveMessage.DevicesUpdated
                ? live.Snapshot().Devices.ToDictionary(d => d.Mac, StringComparer.OrdinalIgnoreCase)
                : null;
            live.Apply(message, clock.GetUtcNow());
            if (before is not null)
            {
                foreach (var device in live.Snapshot().Devices)
                {
                    if (!before.TryGetValue(device.Mac, out var was))
                    {
                        continue;
                    }

                    if (device.Status is { } now && now != "online" && was.Status == "online")
                    {
                        await alerts.RaiseHandsetOfflineAsync(device, cancellationToken);
                    }
                    else if (device.Registered == false && was.Registered == true)
                    {
                        // Online but no longer registered for calls: a different fault from being offline.
                        await alerts.RaiseHandsetAsync(device, AlertEventType.HandsetUnregistered, "unregistered", "Handset can't take calls", "lost its registration and can't make or take calls", cancellationToken);
                    }

                    if (device.UpdateAvailable == true && was.UpdateAvailable != true)
                    {
                        await alerts.RaiseHandsetAsync(device, AlertEventType.HandsetUpdateAvailable, "update", "Handset update available", "has a firmware update waiting", cancellationToken);
                    }
                }
            }

            if (message.Transcript() is { } transcript)
            {
                await transcripts.HandleAsync(transcript, cancellationToken);
            }

            if (message.Event == LiveMessage.CallLogUpdated && message.CallRecords() is { Count: > 0 } records)
            {
                using var scope = scopes.CreateScope();
                scope.ServiceProvider.GetRequiredService<AccessScopeHolder>().UseSystemScope();
                var db = scope.ServiceProvider.GetRequiredService<TalkWatchDbContext>();
                var site = scope.ServiceProvider.GetRequiredService<CurrentSite>();
                var retention = await db.RetentionSettings.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
                var (added, updated) = await new CallLogIngestor(null, db, site.Id, new NumberNormaliser(site.Region), clock, directory.Current, alerts.RaiseForCallsAsync)
                    {
                        KeepSince = retention.KeepCallsSince(clock.GetUtcNow()),
                    }
                    .UpsertAsync(records, "live", message.Data.GetRawText(), cancellationToken);
                LogLiveCalls(logger, added, updated);
                if (added + updated > 0)
                {
                    // A call starting, ringing, being answered or ending: the Now board reads it the moment it is stored.
                    ingestion.Stored();
                }

                Telemetry.CallsStored.Add(added, new("source", "live"), new("change", "added"));
                Telemetry.CallsStored.Add(updated, new("source", "live"), new("change", "updated"));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031
        catch (Exception e)
#pragma warning restore CA1031
        {
            LogMessageFailed(logger, message.Event, e);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Live connection to the console open.")]
    private static partial void LogConnected(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Live connection lost; reconnecting in {Seconds:0} s.")]
    private static partial void LogDisconnected(ILogger logger, double seconds, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Live: {Added} new call(s), {Updated} updated.")]
    private static partial void LogLiveCalls(ILogger logger, int added, int updated);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A live {Event} message could not be handled; skipped.")]
    private static partial void LogMessageFailed(ILogger logger, string @event, Exception exception);
}
