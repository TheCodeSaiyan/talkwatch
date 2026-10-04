using Microsoft.EntityFrameworkCore;
using TalkWatch.Core.Calls;
using TalkWatch.Data;

namespace TalkWatch.Web.Components.Calls;

/// <summary>
/// Builds the chronicle for a page of calls: their routing events in one query, and the names those events refer to by
/// id in another. Everything is read through the viewer's access scope, as the calls themselves were.
/// </summary>
public static class ChronicleLoader
{
    public static async Task<IReadOnlyDictionary<Guid, Chronicle>> ForAsync(TalkWatchDbContext db, IReadOnlyCollection<CallRow> calls, ChronicleNames? names = null, CancellationToken cancellationToken = default)
    {
        if (calls.Count == 0)
        {
            return new Dictionary<Guid, Chronicle>();
        }

        var ids = calls.Select(c => c.Id).ToList();
        var events = await db.CallEvents.AsNoTracking().Where(e => ids.Contains(e.CallId))
            .OrderBy(e => e.CallId).ThenBy(e => e.Sequence)
            .Select(e => new { e.CallId, e.Time, e.Event, e.DataJson })
            .ToListAsync(cancellationToken);

        names ??= await NamesAsync(db, calls.SelectMany(c => new[] { c.FromE164, c.ToE164, c.AnsweredByE164 }), cancellationToken);
        var byCall = events.GroupBy(e => e.CallId).ToDictionary(g => g.Key, g => g.Select(e => new ChronicleEvent(e.Time, e.Event, e.DataJson)).ToList());
        return calls.ToDictionary(c => c.Id, c => Chronicle.Build(byCall.GetValueOrDefault(c.Id) ?? [], c.Direction, names));
    }

    /// <summary>
    /// Names from the directory TalkWatch keeps (attendants, people, contacts, the account's own numbers) and from Talk's
    /// contacts for outside numbers. A name that is not known stays unknown; the chronicle then says "a contact".
    /// </summary>
    public static async Task<ChronicleNames> NamesAsync(TalkWatchDbContext db, IEnumerable<string?> numbers, CancellationToken cancellationToken = default)
    {
        var lines = await db.Lines.AsNoTracking()
            .Where(l => l.Kind == LineKind.Attendant || l.Kind == LineKind.User || l.Kind == LineKind.Contact || l.Kind == LineKind.Did)
            .Select(l => new { l.Kind, l.Key, l.Name })
            .ToListAsync(cancellationToken);
        Dictionary<string, string> Of(LineKind kind) => lines.Where(l => l.Kind == kind).GroupBy(l => l.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Name, StringComparer.Ordinal);
        var attendants = Of(LineKind.Attendant);
        var users = Of(LineKind.User);
        var contacts = Of(LineKind.Contact);
        var dids = Of(LineKind.Did);
        var callers = await CallerNames.ForAsync(db, numbers, cancellationToken);

        return new ChronicleNames(
            Attendant: id => attendants.GetValueOrDefault(id),
            Contact: key => contacts.GetValueOrDefault(key),
            User: uuid => users.GetValueOrDefault(uuid),
            Number: n => dids.GetValueOrDefault(n) ?? callers.GetValueOrDefault(n));
    }
}
