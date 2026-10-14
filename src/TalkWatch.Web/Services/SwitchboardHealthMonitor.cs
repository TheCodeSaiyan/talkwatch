using Microsoft.EntityFrameworkCore;
using TalkWatch.Core.Alerts;
using TalkWatch.Core.Talk;
using TalkWatch.Data;
using TalkWatch.Web.Components.Calls;

namespace TalkWatch.Web.Services;

/// <summary>
/// Alerts when something goes wrong with a switchboard's setup that callers meet: one alert for each problem as it
/// appears, not one each read while it lasts. Switched-off parts raise nothing, as they reach no caller. What was last
/// seen is kept in the database, so a restart neither misses a problem nor raises one twice. Run after each read of
/// the directory, which holds the switchboard as last read.
/// </summary>
public sealed partial class SwitchboardHealthMonitor(
    LineDirectorySync directory, IServiceScopeFactory scopes, AlertService alerts, TimeProvider clock, ILogger<SwitchboardHealthMonitor> logger)
{
    private const string Fact = "switchboard.problems";

    // The directory last checked: each read makes a new one, so the same one has nothing new to say.
    private LineDirectory? _checked;

    public async Task CheckAsync(CancellationToken cancellationToken)
    {
        var current = directory.Current;
        if (current.Switchboard is not { } nodes || ReferenceEquals(current, _checked))
        {
            return;
        }

        try
        {
            var byId = nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
            var problems = NodeHealth.Of(nodes).Where(f => !f.Disabled).ToDictionary(f => $"{f.NodeId}:{f.Issue}", StringComparer.Ordinal);
            var now = clock.GetUtcNow();

            using var scope = scopes.CreateScope();
            scope.ServiceProvider.GetRequiredService<AccessScopeHolder>().UseSystemScope();
            var db = scope.ServiceProvider.GetRequiredService<TalkWatchDbContext>();
            var site = scope.ServiceProvider.GetRequiredService<CurrentSite>();
            var seen = await db.ConsoleFacts.FirstOrDefaultAsync(f => f.Name == Fact, cancellationToken);
            var before = seen?.Value.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal) ?? [];

            foreach (var (key, flag) in problems.Where(p => !before.Contains(p.Key)))
            {
                var (title, detail) = NodeViews.Words(flag);
                await alerts.RaiseSiteAsync(AlertEventType.SwitchboardProblem, $"switchboard:{key}:{now.UtcTicks}",
                    "Switchboard problem", $"{Where(flag.NodeId, byId)}: {title}. {detail}", cancellationToken);
            }

            var value = string.Join('\n', problems.Keys.Order(StringComparer.Ordinal));
            if (seen is null)
            {
                db.ConsoleFacts.Add(new ConsoleFact { SiteId = site.Id, Name = Fact, Value = value, UpdatedAt = now });
            }
            else if (seen.Value != value)
            {
                (seen.Value, seen.UpdatedAt) = (value, now);
            }

            await db.SaveChangesAsync(cancellationToken);
            _checked = current;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // A failed check must not stop calls being copied.
        catch (Exception e)
#pragma warning restore CA1031
        {
            LogFailed(logger, e);
        }
    }

    // Where a node is, from its switchboard down: "Main switchboard › 2 Technical support".
    private static string Where(string nodeId, Dictionary<string, SwitchboardNode> byId)
    {
        var path = new List<string>();
        for (var at = byId.GetValueOrDefault(nodeId); at is not null; at = at.ParentId is { } p ? byId.GetValueOrDefault(p) : null)
        {
            path.Add(at.Type == SwitchboardNode.Menu && at.Key is { } key ? $"{key} {at.Title}".Trim() : at.Title ?? "Switchboard");
        }

        path.Reverse();
        return string.Join(" › ", path);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not check the switchboard's setup for problems; trying again after the next read.")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
