using Microsoft.EntityFrameworkCore;
using TalkWatch.Core.Talk;
using TalkWatch.Data;

namespace TalkWatch.Web.Services;

/// <summary>What the console is running now, and how that compares with what TalkWatch was tested against.</summary>
public sealed class PlatformStatus
{
    public ConsoleVersions? Versions { get; set; }
    public VersionFit Fit { get; set; } = VersionFit.Unknown;
    public DateTimeOffset? CheckedAt { get; set; }

    /// <summary>The versions before the last change, and when the change was seen: for explaining drift.</summary>
    public ConsoleVersions? Previous { get; set; }
    public DateTimeOffset? ChangedAt { get; set; }

    public bool PreRelease => ConsoleVersions.IsPreRelease(Versions?.TalkChannel) || ConsoleVersions.IsPreRelease(Versions?.UnifiOsChannel);
}

/// <summary>
/// Reads the console's UniFi OS and Talk versions and release channels, records every change, and keeps
/// <see cref="PlatformStatus"/> current. Per the version policy: newer or untested versions are warned about, not
/// refused; only an actual change of shape (drift) stops writes.
/// </summary>
public sealed partial class ConsoleVersionMonitor(
    TalkSession session, PlatformStatus platform, IServiceScopeFactory scopes, TimeProvider clock, ILogger<ConsoleVersionMonitor> logger)
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    /// <summary>Checks if the last check is older than <see cref="Interval"/>.</summary>
    public Task CheckIfDueAsync(CancellationToken cancellationToken) =>
        platform.CheckedAt is { } last && clock.GetUtcNow() - last < Interval ? Task.CompletedTask : CheckAsync(cancellationToken);

    /// <summary>
    /// Reads the versions now. Failures are logged and swallowed: these endpoints can move in an update too, and not
    /// knowing the version must never stop calls being copied.
    /// </summary>
    public async Task CheckAsync(CancellationToken cancellationToken)
    {
        try
        {
            var now = clock.GetUtcNow();
            var versions = await session.Client.GetVersionsAsync(cancellationToken);
            platform.CheckedAt = now;
            platform.Fit = Compatibility.Assess(versions, ConsoleVersions.Tested);

            using var scope = scopes.CreateScope();
            scope.ServiceProvider.GetRequiredService<AccessScopeHolder>().UseSystemScope();
            var db = scope.ServiceProvider.GetRequiredService<TalkWatchDbContext>();
            var site = scope.ServiceProvider.GetRequiredService<CurrentSite>();

            var last = await db.ConsoleVersions.OrderByDescending(v => v.SeenAt).FirstOrDefaultAsync(cancellationToken);
            var lastVersions = last is null ? null : new ConsoleVersions(last.UnifiOs, last.UnifiOsChannel, last.Talk, last.TalkChannel);
            if (lastVersions != versions)
            {
                db.ConsoleVersions.Add(new ConsoleVersionRecord
                {
                    Id = Guid.NewGuid(), SiteId = site.Id, SeenAt = now,
                    UnifiOs = versions.UnifiOs, UnifiOsChannel = versions.UnifiOsChannel, Talk = versions.Talk, TalkChannel = versions.TalkChannel,
                });
                await db.SaveChangesAsync(cancellationToken);

                if (lastVersions is not null)
                {
                    platform.Previous = lastVersions;
                    platform.ChangedAt = now;
                    LogChanged(logger, lastVersions.UnifiOs, lastVersions.Talk, versions.UnifiOs, versions.Talk, versions.UnifiOsChannel, versions.TalkChannel);
                }
            }
            else if (last is not null && platform.ChangedAt is null)
            {
                platform.ChangedAt = last.SeenAt;
            }

            platform.Versions = versions;
            if (platform.Fit is not VersionFit.Tested)
            {
                LogUntested(logger, versions.UnifiOs, versions.Talk, ConsoleVersions.Tested.UnifiOs, ConsoleVersions.Tested.Talk);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // Not knowing the version must never stop the poll or the live feed.
        catch (Exception e)
#pragma warning restore CA1031
        {
            LogFailed(logger, e);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The console was updated: UniFi OS {OldOs} -> {NewOs}, Talk {OldTalk} -> {NewTalk} (channels: {OsChannel}, {TalkChannel}).")]
    private static partial void LogChanged(ILogger logger, string? oldOs, string? oldTalk, string? newOs, string? newTalk, string? osChannel, string? talkChannel);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The console runs UniFi OS {Os} and Talk {Talk}; TalkWatch was tested against {TestedOs} and {TestedTalk}.")]
    private static partial void LogUntested(ILogger logger, string? os, string? talk, string? testedOs, string? testedTalk);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read the console's versions; carrying on without them.")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
