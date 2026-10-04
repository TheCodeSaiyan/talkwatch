using TalkWatch.Core.Calls;

namespace TalkWatch.Core.Alerts;

public enum CallerMode
{
    /// <summary>Any caller.</summary>
    Any,

    /// <summary>Only callers on the list.</summary>
    Only,

    /// <summary>Every caller except those on the list.</summary>
    Except,
}

/// <summary>
/// Which callers a rule is about, by number. Entries are numbers in E.164, prefixes such as +44800* (numbers that
/// start with it), or 'withheld': a caller whose number does not read as a phone number, which covers however the
/// console marks a withheld one.
/// </summary>
public sealed record CallerCondition(CallerMode Mode, IReadOnlyList<string> Entries)
{
    public const string Withheld = "withheld";

    public static readonly CallerCondition Anyone = new(CallerMode.Any, []);

    /// <summary>Whether a call from <paramref name="callerE164"/> (null when it did not read as a number) passes.</summary>
    public bool Matches(string? callerE164)
    {
        if (Mode == CallerMode.Any)
        {
            return true;
        }

        var listed = Entries.Any(entry => entry == Withheld
            ? callerE164 is null
            : callerE164 is not null && (entry.EndsWith('*')
                ? callerE164.StartsWith(entry[..^1], StringComparison.Ordinal)
                : callerE164 == entry));
        return Mode == CallerMode.Only ? listed : !listed;
    }

    /// <summary>
    /// Reads what a person typed: entries separated by commas or new lines. Numbers are normalised to E.164 in the
    /// site's region; a prefix must already be in E.164, since a partial number has no region-free reading. Returns the
    /// entries, or the first entry that could not be read.
    /// </summary>
    public static (IReadOnlyList<string>? Entries, string? Problem) Parse(string? text, NumberNormaliser numbers)
    {
        var entries = new List<string>();
        foreach (var raw in (text ?? "").Split([',', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.Equals(raw, Withheld, StringComparison.OrdinalIgnoreCase))
            {
                entries.Add(Withheld);
            }
            else if (raw.EndsWith('*'))
            {
                var prefix = raw[..^1].Replace(" ", "", StringComparison.Ordinal);
                if (prefix.Length < 2 || prefix[0] != '+' || !prefix[1..].All(char.IsAsciiDigit))
                {
                    return (null, raw);
                }

                entries.Add(prefix + "*");
            }
            else if (numbers.ToE164(raw) is { } e164)
            {
                entries.Add(e164);
            }
            else
            {
                return (null, raw);
            }
        }

        return (entries.Distinct(StringComparer.Ordinal).ToList(), null);
    }
}
