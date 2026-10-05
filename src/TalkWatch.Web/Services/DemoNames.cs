using System.Text.RegularExpressions;

namespace TalkWatch.Web.Services;

/// <summary>
/// Readable names for the demo, in place of the capture's pseudonyms ("Title 11", "Morgan9 Nico10", "Jordan Baker 27"),
/// applied to everything the replayed console answers. The fixtures keep their pseudonyms, so tests are unchanged.
/// </summary>
public static partial class DemoNames
{
    // What each of the capture's titles is on the console it came from: two switchboards, one with a menu that opens a
    // second menu, and each option split by opening hours before its ring group.
    private static readonly Dictionary<string, string> Titles = new(StringComparer.Ordinal)
    {
        ["Title 9"] = "Welcome greeting",
        ["Title 10"] = "Main switchboard",
        ["Title 11"] = "Sales and billing",
        ["Title 12"] = "Sales",
        ["Title 13"] = "Opening hours",
        ["Title 14"] = "Out of hours",
        ["Title 15"] = "Billing",
        ["Title 16"] = "Technical support",
        ["Title 17"] = "Anything else",
        ["Title 18"] = "Front desk",
    };

    // The ring group every option puts calls through to.
    private static readonly Dictionary<string, string> Names = new(StringComparer.Ordinal)
    {
        ["Jordan Baker 27"] = "Support team",
    };

    /// <summary>The console's answer with readable names: the titles and group above, then any name with a number on the end without it.</summary>
    public static string Apply(string json)
    {
        json = Title().Replace(json, m => Titles.GetValueOrDefault(m.Value, m.Value));
        foreach (var (from, to) in Names)
        {
            json = json.Replace(from, to, StringComparison.Ordinal);
        }

        json = FullName().Replace(json, "$1");

        // A one-word pseudonym only where it is a name: the capture made some ids and settings into pseudonyms too, and
        // two ids that differ only by their number must stay apart.
        return NameField().Replace(json, m => m.Groups[1].Value + Numbered().Replace(m.Groups[2].Value, "$1") + "\"");
    }

    [GeneratedRegex(@"(""(?:first_name|last_name|name|display_name|full_name|group_name|dialed_name|speaker|source_name|alias)""\s*:\s*"")([^""]*)""")]
    private static partial Regex NameField();

    [GeneratedRegex(@"\bTitle \d+\b")]
    private static partial Regex Title();

    // "Jordan Hughes 29"
    [GeneratedRegex(@"\b([A-Z][a-z]+ [A-Z][a-z]+) \d+\b")]
    private static partial Regex FullName();

    // "Morgan9", "Nico10"
    [GeneratedRegex(@"\b([A-Z][a-z]+)\d+\b")]
    private static partial Regex Numbered();
}
