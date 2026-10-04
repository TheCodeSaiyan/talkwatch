using Microsoft.EntityFrameworkCore;
using TalkWatch.Core.Calls;
using TalkWatch.Data;

namespace TalkWatch.Web.Services;

/// <summary>What the command palette searches: calls by number or caller, and lines by name or number.</summary>
public static class SearchEndpoints
{
    public sealed record CallHit(string Uuid, string Who, string? Number, string Outcome, DateTimeOffset Time, string Direction);

    public sealed record LineHit(string Name, string Kind, string Key, string? Number, string? Ext);

    public sealed record SearchResult(IReadOnlyList<CallHit> Calls, IReadOnlyList<LineHit> Lines);

    private const int Most = 8;

    public static void MapSearch(this IEndpointRouteBuilder app)
    {
        // Everything goes through the signed-in person's access scope, as the pages do: a call their grants do not
        // cover is not found, so the palette cannot reveal it exists. A number is matched on its digits, so "0752",
        // "07700 900752" and "+44 7700 900752" all find the same caller.
        app.MapGet("/search", async (string? q, TalkWatchDbContext db, HttpContext http, CancellationToken cancellationToken) =>
        {
            http.Response.Headers.CacheControl = "private, no-store";
            var text = (q ?? "").Trim();
            if (text.Length < 2)
            {
                return Results.Json(new SearchResult([], []));
            }

            var digits = new string(text.Where(char.IsAsciiDigit).ToArray());
            var calls = db.Calls.AsNoTracking();
            List<CallRow> found;
            if (digits.Length >= 3)
            {
                // Numbers are stored in E.164, so a national "07700…" is compared without its leading zero.
                var tail = digits.TrimStart('0');
                found = await calls.Where(c => (c.FromE164 != null && c.FromE164.Contains(tail)) || (c.ToE164 != null && c.ToE164.Contains(tail)))
                    .OrderByDescending(c => c.Time).Take(Most).ToListAsync(cancellationToken);
            }
            else
            {
                var pattern = "%" + Escape(text) + "%";
                found = await calls.Where(c => c.CallerName != null && EF.Functions.ILike(c.CallerName, pattern, @"\"))
                    .OrderByDescending(c => c.Time).Take(Most).ToListAsync(cancellationToken);
            }

            var names = await CallerNames.ForAsync(db, found.Select(c => c.Direction == "out" ? c.ToE164 : c.FromE164), cancellationToken);
            var hits = found.Select(c => new CallHit(
                c.TalkUuid,
                c.Direction == "out" ? CallerNames.Who(null, c.ToRaw, c.ToE164, names) : CallerNames.Who(c.CallerName, c.FromRaw, c.FromE164, names),
                c.Direction == "out" ? c.ToRaw : c.FromRaw,
                c.Outcome.ToString(),
                c.Time,
                c.Direction)).ToList();

            // The directory is kept per site, not per person, so it is narrowed here: someone who cannot see every call
            // finds only the lines they are granted, and the palette never names a line they could not open.
            var linePattern = "%" + Escape(text) + "%";
            var directory = db.Lines.AsNoTracking();
            if (!http.User.Can(Permission.AllCalls))
            {
                directory = directory.Where(l => db.Grants.Any(g => g.Kind == l.Kind && g.Key == l.Key));
            }

            var lines = await directory
                .Where(l => l.Present && (l.Kind == LineKind.Did || l.Kind == LineKind.User || l.Kind == LineKind.RingGroup || l.Kind == LineKind.Attendant)
                    && (EF.Functions.ILike(l.Name, linePattern, @"\") || (digits.Length >= 3 && l.Key.Contains(digits.TrimStart('0'))) || (l.Ext != null && l.Ext == text)))
                .OrderBy(l => l.Name).Take(Most)
                .Select(l => new LineHit(l.Name, l.Kind.ToString(), l.Key, l.Kind == LineKind.Did ? l.Key : null, l.Ext))
                .ToListAsync(cancellationToken);

            return Results.Json(new SearchResult(hits, lines));
        }).RequireAuthorization();
    }

    // Typed text matched literally: % and _ in it are not wildcards.
    private static string Escape(string text) =>
        text.Replace(@"\", @"\\", StringComparison.Ordinal).Replace("%", @"\%", StringComparison.Ordinal).Replace("_", @"\_", StringComparison.Ordinal);
}
