using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace TalkWatch.FixtureGuard;

public sealed record Finding(string Path, int Line, string Value, string Reason)
{
    public override string ToString() => Line > 0 ? $"{Path}:{Line}: {Reason}: {Value}" : $"{Path}: {Reason}";
}

/// <summary>
/// Refuses anything under tests/fixtures that could be real personal data: a phone number outside the
/// fictional ranges, audio not produced by the synthesiser, or a binary file it cannot read.
/// </summary>
public static partial class Guard
{
    public const string FixturesDirectory = "tests/fixtures/";
    public const string AllowListPath = FixturesDirectory + ".guard-allow";
    public const string AudioManifestPath = FixturesDirectory + "synthetic-audio.sha256";

    private static readonly HashSet<string> AudioExtensions =
        new([".wav", ".mp3", ".ogg", ".opus", ".m4a", ".flac", ".aac", ".gsm"], StringComparer.OrdinalIgnoreCase);

    // Dates, times, UUIDs and IPv4 addresses are blanked before scanning, because their digit runs look like numbers.
    [GeneratedRegex(@"\d{4}-\d{2}-\d{2}(?:[T ]\d{2}:\d{2}(?::\d{2}(?:\.\d+)?)?)?")]
    private static partial Regex IsoDateTime();

    [GeneratedRegex(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex Uuid();

    [GeneratedRegex(@"(?<![\d.])(?:\d{1,3}\.){3}\d{1,3}(?![\d.])")]
    private static partial Regex IPv4();

    /// <summary>A property whose name says it holds a time, such as received_at, call_time or created_date.</summary>
    [GeneratedRegex(@"(^|_)(at|time|timestamp|date|ts)$|(Time|Date|At|Ts)$", RegexOptions.None)]
    public static partial Regex TimeField();

    // "received_at": "1759147200" — the name of the property the value belongs to, when the value opens a JSON string.
    [GeneratedRegex(@"""([A-Za-z0-9_]+)""\s*:\s*""$")]
    private static partial Regex StringValueOf();

    // An optional +, then 9 to 15 digits with up to two spaces, dashes, dots or brackets between each.
    // Short runs are left alone: extensions and ring-group numbers are internal, not personal.
    // Only a neighbouring digit ends a match, not a letter: a number glued to text, such as the %2B-encoded
    // plus in a query string, must still be found. A long digit run inside an identifier is the price.
    [GeneratedRegex(@"(?<![\d+])\+?\(?\d(?:[ \-.()]{0,2}\d){8,14}(?!\d)")]
    private static partial Regex PhoneLike();

    /// <summary>Scans the given repository-relative paths under <paramref name="root"/>. Paths outside tests/fixtures are ignored.</summary>
    public static IReadOnlyList<Finding> Scan(string root, IEnumerable<string> relativePaths)
    {
        var allowed = ReadAllowList(root);
        var manifest = ReadAudioManifest(root);
        var findings = new List<Finding>();

        foreach (var path in relativePaths.Select(p => p.Replace('\\', '/')).Distinct())
        {
            if (!path.StartsWith(FixturesDirectory, StringComparison.Ordinal) || path == AllowListPath || path == AudioManifestPath)
            {
                continue;
            }

            var fullPath = Path.Combine(root, path);
            if (!File.Exists(fullPath))
            {
                continue;
            }

            if (AudioExtensions.Contains(Path.GetExtension(path)))
            {
                var hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(fullPath)));
                if (!manifest.TryGetValue(path, out var expected) || expected != hash)
                {
                    findings.Add(new Finding(path, 0, hash, $"audio not listed with this hash in {AudioManifestPath}; only synthetic audio may be committed"));
                }

                continue;
            }

            if (IsBinary(fullPath))
            {
                findings.Add(new Finding(path, 0, "", "binary file; fixtures must be text or listed synthetic audio"));
                continue;
            }

            findings.AddRange(ScanText(path, File.ReadAllLines(fullPath), allowed));
        }

        return findings;
    }

    /// <summary>Every phone-like value in <paramref name="lines"/> that is neither reserved nor on the allow list.</summary>
    public static IEnumerable<Finding> ScanText(string path, IEnumerable<string> lines, IReadOnlySet<string> allowed)
    {
        var lineNumber = 0;
        foreach (var line in lines)
        {
            lineNumber++;
            foreach (var (index, value) in FindPhoneLike(line))
            {
                if (!ReservedNumbers.IsReserved(value) && !allowed.Contains(value) && !IsJsonTimestamp(line, index, value)
                    && !IsNamedStringTimestamp(line, index, value))
                {
                    yield return new Finding(path, lineNumber, value, "number outside the fictional ranges");
                }
            }
        }
    }

    /// <summary>
    /// True for a bare run of digits that reads as an epoch time between 2000 and 2100, in seconds, milliseconds or
    /// microseconds. APIs send times this way, and 10 and 13 digits are also phone-number lengths.
    /// </summary>
    public static bool IsTimestampLike(string value)
    {
        if (value.Length is not (10 or 13 or 16) || value[0] == '0' || !value.All(char.IsAsciiDigit) || !long.TryParse(value, out var number))
        {
            return false;
        }

        var seconds = value.Length switch { 10 => number, 13 => number / 1_000, _ => number / 1_000_000 };
        return seconds is >= 946_684_800 and < 4_102_444_800;
    }

    // A timestamp is only let through where it is a JSON number, not a string: a real national number written
    // without its leading 0 could fall in the same range, and those arrive as strings.
    // Judged by the whole JSON number the match sits in, not only the digits the pattern caught: a long decimal
    // such as 15151660586.53 is more than 15 digits, so the pattern catches just the part before the point.
    private static bool IsJsonTimestamp(string line, int index, string value)
    {
        var start = index;
        while (start > 0 && (char.IsAsciiDigit(line[start - 1]) || line[start - 1] is '.' or '-'))
        {
            start--;
        }

        var end = index + value.Length;
        while (end < line.Length && (char.IsAsciiDigit(line[end]) || line[end] is '.' or 'e' or 'E' or '+' or '-'))
        {
            end++;
        }

        var token = line[start..end];
        var before = line[..start].TrimEnd();
        var after = line[end..].TrimStart();
        var isJsonNumber = before.Length > 0 && before[^1] is ':' or '[' or ','
            && (after.Length == 0 || after[0] is ',' or '}' or ']');

        // A decimal or exponent JSON number, such as a rate or a time with fractional seconds, is never a phone number.
        return isJsonNumber && (IsTimestampLike(token) || IsNonInteger(token));
    }

    // Some payloads send times as strings ("received_at": "1759147200"). Those pass only when the whole string is the
    // time and the property is named as one, because an unnamed string of digits could be a number without its 0.
    private static bool IsNamedStringTimestamp(string line, int index, string value)
    {
        var end = index + value.Length;
        return IsTimestampLike(value) && end < line.Length && line[end] == '"'
            && StringValueOf().Match(line[..index]) is { Success: true } property && TimeField().IsMatch(property.Groups[1].Value);
    }

    public static bool IsNonInteger(string token) =>
        token.IndexOfAny(['.', 'e', 'E']) > 0
        && double.TryParse(token, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _);

    /// <summary>
    /// Every phone-like value in <paramref name="text"/> with its position, by the same rule the guard enforces.
    /// The pseudonymiser uses this too, so anything the guard would refuse is something it replaces.
    /// </summary>
    public static IEnumerable<(int Index, string Value)> FindPhoneLike(string text)
    {
        var blanked = IPv4().Replace(Uuid().Replace(IsoDateTime().Replace(text, Blank), Blank), Blank);
        foreach (Match match in PhoneLike().Matches(blanked))
        {
            var trimmedStart = match.Value.Length - match.Value.TrimStart().Length;
            yield return (match.Index + trimmedStart, match.Value.Trim());
        }
    }

    private static string Blank(Match match) => new(' ', match.Length);

    private static bool IsBinary(string fullPath)
    {
        using var stream = File.OpenRead(fullPath);
        var buffer = new byte[8192];
        var read = stream.Read(buffer, 0, buffer.Length);
        return Array.IndexOf(buffer, (byte)0, 0, read) >= 0;
    }

    private static HashSet<string> ReadAllowList(string root)
    {
        var path = Path.Combine(root, AllowListPath);
        return File.Exists(path)
            ? File.ReadAllLines(path).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToHashSet(StringComparer.Ordinal)
            : [];
    }

    // Lines in sha256sum format: "<hash>  <repository-relative path>".
    private static Dictionary<string, string> ReadAudioManifest(string root)
    {
        var path = Path.Combine(root, AudioManifestPath);
        if (!File.Exists(path))
        {
            return [];
        }

        return File.ReadAllLines(path)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l => l.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[1].TrimStart('*').Replace('\\', '/'), parts => parts[0].ToLowerInvariant(), StringComparer.Ordinal);
    }
}
