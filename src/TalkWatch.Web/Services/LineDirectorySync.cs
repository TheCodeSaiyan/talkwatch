using Microsoft.EntityFrameworkCore;
using TalkWatch.Core.Calls;
using TalkWatch.Core.Talk;
using TalkWatch.Data;

namespace TalkWatch.Web.Services;

/// <summary>
/// Keeps the console's users, ring groups, numbers and attendants: in memory, for recognising which calls went
/// through which ring group as they are stored, and in the lines table, for naming lines on screen.
/// </summary>
public sealed partial class LineDirectorySync(
    TalkSession session, IServiceScopeFactory scopes, CurrentSite site, TimeProvider clock, ILogger<LineDirectorySync> logger)
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    private DateTimeOffset? _refreshedAt;

    /// <summary>The directory as last read; empty until the first refresh.</summary>
    public LineDirectory Current { get; private set; } = LineDirectory.Empty;

    public Task RefreshIfDueAsync(CancellationToken cancellationToken) =>
        _refreshedAt is { } last && clock.GetUtcNow() - last < Interval ? Task.CompletedTask : RefreshAsync(cancellationToken);

    /// <summary>Reads the directory now. A failure keeps the last directory: calls are still stored, with fewer group lines.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            var numbers = new NumberNormaliser(site.Region);
            var directory = await session.Client.GetDirectoryAsync(numbers, cancellationToken);
            var now = clock.GetUtcNow();

            using var scope = scopes.CreateScope();
            scope.ServiceProvider.GetRequiredService<AccessScopeHolder>().UseSystemScope();
            var db = scope.ServiceProvider.GetRequiredService<TalkWatchDbContext>();
            var existing = await db.Lines.ToDictionaryAsync(l => (l.Kind, l.Key), cancellationToken);
            var seen = new HashSet<(LineKind, string)>();
            foreach (var (kind, key, name, ext) in directory.Lines(numbers))
            {
                if (!seen.Add((kind, key)))
                {
                    continue;
                }

                if (existing.TryGetValue((kind, key), out var line))
                {
                    if (line.Name != name || line.Ext != ext || !line.Present)
                    {
                        line.Name = name;
                        line.Ext = ext;
                        line.Present = true;
                        line.UpdatedAt = now;
                    }
                }
                else
                {
                    db.Lines.Add(new LineRecord { SiteId = site.Id, Kind = kind, Key = key, Name = name, Ext = ext, Present = true, UpdatedAt = now });
                }
            }

            // Gone from the console, but old calls still name them: kept, marked absent.
            foreach (var line in existing.Values.Where(l => l.Present && !seen.Contains((l.Kind, l.Key))))
            {
                line.Present = false;
                line.UpdatedAt = now;
            }

            await db.SaveChangesAsync(cancellationToken);
            await CallerNames.ReplaceAsync(db, site.Id, directory.CallerNames(numbers), cancellationToken);
            await DropGoneContactLinksAsync(db, directory, cancellationToken);
            Current = directory;
            _refreshedAt = now;
            // Kept with the directory too: it says which users and contacts a number reaches.
            directory.Switchboard = await RefreshSwitchboardAsync(db, now, cancellationToken);
            if (directory.Switchboard is not null)
            {
                await RefreshRoutesAsync(db, directory.Routes(numbers), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // A failed refresh must not stop calls being stored.
        catch (Exception e)
#pragma warning restore CA1031
        {
            LogFailed(logger, e);
        }
    }

    /// <summary>
    /// Links to outside contacts Talk no longer has: they would reach nobody, without anyone noticing. Only when Talk
    /// listed its contacts at all, since an account that may not read them lists none, and that is no reason to drop links.
    /// </summary>
    public static async Task<int> DropGoneContactLinksAsync(TalkWatchDbContext db, LineDirectory directory, CancellationToken cancellationToken)
    {
        if (directory.Contacts.Count == 0)
        {
            return 0;
        }

        var present = directory.Contacts.Select(c => c.Uuid).OfType<string>().ToList();
        return await db.ContactLinks.Where(l => !present.Contains(l.ContactUuid)).ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>
    /// The switchboard tree, for naming menu options, and each greeting's length, measured from its audio when the file
    /// changes. A failure here keeps the last tree and never the lines: those are already saved.
    /// </summary>
    private async Task<IReadOnlyList<SwitchboardNode>?> RefreshSwitchboardAsync(TalkWatchDbContext db, DateTimeOffset now, CancellationToken cancellationToken)
    {
        try
        {
            var nodes = await session.Client.GetSwitchboardAsync(cancellationToken);
            var existing = await db.SwitchboardNodes.ToDictionaryAsync(n => n.NodeId, cancellationToken);
            foreach (var node in nodes)
            {
                if (!existing.TryGetValue(node.Id, out var row))
                {
                    row = new SwitchboardNodeRow { SiteId = site.Id, NodeId = node.Id };
                    db.SwitchboardNodes.Add(row);
                }

                var greetingChanged = row.GreetingFile != node.GreetingFileName;
                (row.InternalId, row.Type, row.Title, row.Key, row.ParentId) = (node.InternalId, node.Type, node.Title, node.Key, node.ParentId);
                (row.Numbers, row.GreetingFile, row.Present, row.UpdatedAt) = (node.Numbers.Count == 0 ? null : string.Join(',', node.Numbers), node.GreetingFileName, true, now);
                if (node.Type is SwitchboardNode.Root or SwitchboardNode.Menu && node.GreetingFileName is { } file && (greetingChanged || row.GreetingSeconds is null))
                {
                    row.GreetingSeconds = await session.Client.GetSwitchboardAudioAsync(file, cancellationToken) is { } audio
                        ? Core.Audio.Mp3.Duration(audio)?.TotalSeconds
                        : null;
                }
            }

            var seen = nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var gone in existing.Values.Where(n => n.Present && !seen.Contains(n.NodeId)))
            {
                (gone.Present, gone.UpdatedAt) = (false, now);
            }

            await db.SaveChangesAsync(cancellationToken);
            return nodes;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // The switchboard only names things; it must not stop the directory.
        catch (Exception e)
#pragma warning restore CA1031
        {
            LogSwitchboardFailed(logger, e);
            return null;
        }
    }

    /// <summary>
    /// What each number routes calls through, which a role on the number covers. Only with the switchboard read: without
    /// it, routes through switchboards would go missing and roles would cover less than they should until the next read.
    /// </summary>
    private async Task RefreshRoutesAsync(TalkWatchDbContext db, IReadOnlyDictionary<string, IReadOnlySet<LineRef>> routes, CancellationToken cancellationToken)
    {
        var wanted = routes.SelectMany(r => r.Value.Select(l => (Did: r.Key, l.Kind, l.Key))).ToHashSet();
        var existing = await db.NumberRoutes.ToListAsync(cancellationToken);
        db.NumberRoutes.RemoveRange(existing.Where(r => !wanted.Contains((r.Did, r.Kind, r.Key))));
        var have = existing.Select(r => (r.Did, r.Kind, r.Key)).ToHashSet();
        db.NumberRoutes.AddRange(wanted.Where(w => !have.Contains(w)).Select(w => new NumberRoute { SiteId = site.Id, Did = w.Did, Kind = w.Kind, Key = w.Key }));
        await db.SaveChangesAsync(cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read the console's switchboard; keeping the last one.")]
    private static partial void LogSwitchboardFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read the console's users, groups and numbers; keeping the last directory.")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
