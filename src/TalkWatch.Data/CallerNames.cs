using Microsoft.EntityFrameworkCore;

namespace TalkWatch.Data;

/// <summary>A name for a number, from Talk's contacts: who a caller is when the call itself does not say.</summary>
public sealed class CallerName
{
    public Guid SiteId { get; set; }

    /// <summary>The number in E.164.</summary>
    public required string Number { get; set; }

    /// <summary>The contact's name and the number's label, such as "Alex Smith (mobile)".</summary>
    public required string Name { get; set; }
}

public static class CallerNames
{
    /// <summary>Names for these numbers, where the contacts have one.</summary>
    public static async Task<IReadOnlyDictionary<string, string>> ForAsync(TalkWatchDbContext db, IEnumerable<string?> numbers, CancellationToken cancellationToken = default)
    {
        var wanted = numbers.OfType<string>().Distinct().ToList();
        return wanted.Count == 0
            ? new Dictionary<string, string>()
            : await db.CallerNames.AsNoTracking().Where(n => wanted.Contains(n.Number)).ToDictionaryAsync(n => n.Number, n => n.Name, cancellationToken);
    }

    /// <summary>
    /// How a caller reads: the name Talk sent with the call, else the contacts' name for the number, with the number as
    /// it came in.
    /// </summary>
    public static string Who(string? callerName, string? raw, string? e164, IReadOnlyDictionary<string, string> names) =>
        callerName is { Length: > 0 } name ? $"{name} ({raw})"
        : e164 is not null && names.TryGetValue(e164, out var contact) ? $"{contact} ({raw})"
        : raw ?? "an unknown number";

    /// <summary>Replaces the stored names with the contacts' as they are now.</summary>
    public static async Task ReplaceAsync(TalkWatchDbContext db, Guid siteId, IReadOnlyDictionary<string, string> names, CancellationToken cancellationToken)
    {
        var existing = await db.CallerNames.ToDictionaryAsync(n => n.Number, cancellationToken);
        foreach (var (number, name) in names)
        {
            if (existing.Remove(number, out var row))
            {
                row.Name = name;
            }
            else
            {
                db.CallerNames.Add(new CallerName { SiteId = siteId, Number = number, Name = name });
            }
        }

        db.CallerNames.RemoveRange(existing.Values);
        await db.SaveChangesAsync(cancellationToken);
    }
}
