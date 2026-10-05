namespace TalkWatch.Web.Components.Calls;

/// <summary>
/// A list on the Now board that holds still while someone is pointing at it, so rows never shift under a click. What is
/// shown keeps its rows and their order; each row still takes its latest version, so status and durations stay true.
/// Rows that would arrive or leave wait, and are counted, until the list is let go.
/// </summary>
public static class HeldList
{
    /// <summary>The rows to show while held, and how many changes are waiting: arrivals plus departures.</summary>
    public static (List<T> Shown, int Waiting) Hold<T>(IReadOnlyList<T> shown, IReadOnlyList<T> truth, Func<T, string> key)
    {
        var fresh = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var row in truth)
        {
            fresh.TryAdd(key(row), row);
        }

        // A row that has gone keeps its last version: a finished call waits in the list, resolved, until it is let go.
        var kept = shown.Select(row => fresh.TryGetValue(key(row), out var latest) ? latest : row).ToList();
        var here = kept.Select(key).ToHashSet(StringComparer.Ordinal);
        var waiting = fresh.Keys.Count(k => !here.Contains(k)) + here.Count(k => !fresh.ContainsKey(k));
        return (kept, waiting);
    }
}
