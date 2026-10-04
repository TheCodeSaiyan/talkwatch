using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using TalkWatch.FixtureGuard;

namespace TalkWatch.Capture;

/// <summary>
/// Replaces the personal parts of captured payloads with consistent fictional ones: phone numbers, names,
/// e-mail addresses, free text, hardware addresses and public IP addresses. Everything else, including the
/// shape of every payload, is left exactly as captured, because the shape is what the fixtures are for.
/// </summary>
/// <remarks>
/// Names and free text are found by property name, which is a heuristic. <see cref="FieldNames"/> lists every
/// property name seen, so a person can check nothing personal slipped through under a name the rules missed.
/// </remarks>
public sealed partial class Pseudonymiser(byte[] key)
{
    private static readonly string[] FirstNames =
        ["Alex", "Sam", "Jordan", "Robin", "Charlie", "Jamie", "Morgan", "Casey", "Riley", "Avery", "Quinn", "Rowan",
         "Frankie", "Hayden", "Sky", "Reese", "Emerson", "Dakota", "Ellis", "Harper", "Kit", "Lee", "Marley", "Nico"];
    private static readonly string[] LastNames =
        ["Archer", "Baker", "Carter", "Dawson", "Ellison", "Fletcher", "Gray", "Hughes", "Irving", "Jennings", "Keane",
         "Lowe", "Mercer", "Norris", "Osborne", "Price", "Quigley", "Rhodes", "Sutton", "Turner", "Vaughan", "Walsh"];
    private const string Filler =
        "Lorem ipsum dolor sit amet, consectetur adipiscing elit, sed do eiusmod tempor incididunt ut labore et dolore magna aliqua. ";

    // Relaxed escaping keeps '+' and non-ASCII names readable, as the console writes them; fixtures are never served to a browser.
    private static readonly JsonWriterOptions WriterOptions = new() { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly NumberMapper _numbers = new(key);
    private readonly Dictionary<string, string> _names = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _emails = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _macs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _ips = new(StringComparer.Ordinal);

    public SortedSet<string> FieldNames { get; } = new(StringComparer.Ordinal);

    public int NumbersReplaced => _numbers.Count;

    [GeneratedRegex(@"name|callerid|cnam|alias|contact|owner|user|speaker|organi[sz]ation|company|business|brand", RegexOptions.IgnoreCase)]
    private static partial Regex NameField();

    // Transcripts and SMS keep their words under generic names such as text, content or body, so those count as free text too.
    [GeneratedRegex(@"transcri|note|comment|message|description|memo|reason|text|content|body|subject|summary|snippet|preview|utterance|sentence|word|sms|keyword", RegexOptions.IgnoreCase)]
    private static partial Regex TextField();

    // Where Talk keeps transcript words, their search vectors and IVR greetings. Whole names only: 'line' as a
    // substring would also catch online or timeline.
    [GeneratedRegex(@"^(line|lines|document|vector|sum_vector|greeting|greetings|greeting_text)$", RegexOptions.IgnoreCase)]
    private static partial Regex ProseField();

    // Property names that contain a trigger word but hold something that is never personal.
    [GeneratedRegex(@"^(hostname|filename|file_name|typename|type_name|username_attribute|content_?type|content_?length|text_?direction)$|file_?name$", RegexOptions.IgnoreCase)]
    private static partial Regex NotPersonal();

    // Menu, group and attendant titles are chosen by the business, and often name it.
    [GeneratedRegex(@"title", RegexOptions.IgnoreCase)]
    private static partial Regex TitleField();

    private readonly Dictionary<string, string> _titles = new(StringComparer.Ordinal);

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}")]
    private static partial Regex Email();

    [GeneratedRegex(@"\b(?:[0-9A-Fa-f]{2}[:\-]){5}[0-9A-Fa-f]{2}\b")]
    private static partial Regex Mac();

    [GeneratedRegex(@"\b(?:\d{1,3}\.){3}\d{1,3}\b")]
    private static partial Regex IPv4();

    [GeneratedRegex(@"(?<![0-9A-Fa-f:])(?:[0-9A-Fa-f]{0,4}:){2,7}[0-9A-Fa-f]{0,4}(?![0-9A-Fa-f:])")]
    private static partial Regex IPv6();

    // Values that grant access. Only strings are replaced, so a flag such as sharedTokens: true is left alone.
    [GeneratedRegex(@"(^|_)(token|secret|password|passwd|auth_key|api_key|hashed_key|private_key|webhook)$|rtsp|slack_url|teams_url", RegexOptions.IgnoreCase)]
    private static partial Regex SecretField();

    [GeneratedRegex(@"^(street|street_?secondary|street2|address_?line_?\d?|city|town|county|postal_?code|postcode|zip|zip_?code)$", RegexOptions.IgnoreCase)]
    private static partial Regex AddressField();

    [GeneratedRegex(@"^(lat|lng|long|latitude|longitude)$|_(latitude|longitude)$", RegexOptions.IgnoreCase)]
    private static partial Regex GeoField();

    [GeneratedRegex(@"birth|^dob$", RegexOptions.IgnoreCase)]
    private static partial Regex BirthField();

    [GeneratedRegex(@"serial", RegexOptions.IgnoreCase)]
    private static partial Regex SerialField();

    [GeneratedRegex(@"avatar|picture|photo|^(url|urls|local_url)$", RegexOptions.IgnoreCase)]
    private static partial Regex LinkField();

    [GeneratedRegex(@"(^|_)sid$", RegexOptions.IgnoreCase)]
    private static partial Regex SidField();

    private readonly Dictionary<string, string> _secrets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _links = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _ipv6 = new(StringComparer.OrdinalIgnoreCase);

    public string Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            Walk(writer, document.RootElement, null);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>Plain text or CSV. In CSV, columns whose header looks like a name or free text are replaced whole.</summary>
    public string Text(string text, bool isCsv)
    {
        if (!isCsv)
        {
            // Plain text has no field names to go by, so any line of three or more words is treated as prose.
            return string.Join('\n', text.Split('\n').Select(line =>
                line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length >= 3 ? FillerOfLength(line.TrimEnd('\r').Length) : Scrub(line)));
        }

        var lines = text.Split('\n');
        var header = Csv.Split(lines[0].TrimEnd('\r'));
        foreach (var column in header)
        {
            FieldNames.Add(column);
        }

        var output = new StringBuilder();
        output.Append(Scrub(lines[0].TrimEnd('\r'))).Append('\n');
        foreach (var line in lines.Skip(1))
        {
            if (line.Length == 0)
            {
                continue;
            }

            var cells = Csv.Split(line.TrimEnd('\r'));
            for (var i = 0; i < cells.Count; i++)
            {
                cells[i] = i < header.Count ? Value(header[i], cells[i]) : Scrub(cells[i]);
            }

            output.Append(Csv.Join(cells)).Append('\n');
        }

        return output.ToString();
    }

    /// <summary>A URL path and query, with any number or e-mail address in it replaced.</summary>
    public string Path(string pathAndQuery) => Scrub(pathAndQuery);

    // Streams from reader to writer rather than through a node tree, so repeated keys and key order are copied
    // exactly as the console sent them. A node tree refuses a repeated key.
    private void Walk(Utf8JsonWriter writer, JsonElement element, string? property)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                // A postal address object: every text field in it is part of where someone is, whatever it is called
                // (region and state mean something else elsewhere, so they are only replaced here).
                var isAddress = element.EnumerateObject().Any(c => c.Name is "street" or "postal_code" or "postalCode");
                writer.WriteStartObject();
                foreach (var child in element.EnumerateObject())
                {
                    FieldNames.Add(child.Name);
                    writer.WritePropertyName(Scrub(child.Name));
                    if (isAddress && child.Value.ValueKind == JsonValueKind.String
                        && child.Name is not ("isoCountry" or "iso_country" or "address_type") && !SidField().IsMatch(child.Name))
                    {
                        writer.WriteStringValue(AddressValue(child.Name, child.Value.GetString()!));
                        continue;
                    }

                    Walk(writer, child.Value, child.Name);
                }

                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    Walk(writer, item, property);
                }

                writer.WriteEndArray();
                break;

            case JsonValueKind.String:
                writer.WriteStringValue(Value(property, element.GetString()!));
                break;

            case JsonValueKind.Number:
                writer.WriteRawValue(property is not null && GeoField().IsMatch(property) ? "0.0" : Number(element.GetRawText()));
                break;

            default:
                element.WriteTo(writer);
                break;
        }
    }

    private string Number(string raw)
    {
        if (Guard.IsTimestampLike(raw) || raw.IndexOfAny(['.', 'e', 'E']) >= 0)
        {
            return raw;
        }

        var mapped = Scrub(raw);
        if (mapped == raw)
        {
            return raw;
        }

        // A JSON number cannot start with 0, so a national-style replacement is written with its country code.
        var digits = new string(mapped.Where(char.IsAsciiDigit).ToArray());
        return digits.StartsWith('0') ? "44" + digits[1..] : digits;
    }

    private string Value(string? property, string text)
    {
        if (text.Length == 0 || property is null || NotPersonal().IsMatch(property))
        {
            return Scrub(text);
        }

        // A time sent as a string, in a field named as one: kept, by the same rule the guard applies.
        if (Guard.TimeField().IsMatch(property) && Guard.IsTimestampLike(text))
        {
            return text;
        }

        if (TitleField().IsMatch(property))
        {
            return Consistent(_titles, text, n => $"Title {n}");
        }

        if (SecretField().IsMatch(property))
        {
            return Consistent(_secrets, text, n => $"redacted-{n}");
        }

        if (SidField().IsMatch(property))
        {
            return KeyedLike(text, "sid");
        }

        if (AddressField().IsMatch(property))
        {
            return AddressValue(property, text);
        }

        if (BirthField().IsMatch(property))
        {
            return "2000-01-01";
        }

        if (property.Equals("last4", StringComparison.OrdinalIgnoreCase))
        {
            return "0000";
        }

        if (SerialField().IsMatch(property))
        {
            return KeyedLike(text, "serial");
        }

        if (LinkField().IsMatch(property) && text.Contains('/', StringComparison.Ordinal))
        {
            return Consistent(_links, text, n => $"https://example.invalid/redacted/{n}");
        }

        // A field that holds nothing but a phone number keeps a number, so its format and joins survive.
        var numbers = Guard.FindPhoneLike(text).Take(2).ToList();
        if (numbers.Count == 1 && numbers[0].Value.Length == text.Trim().Length)
        {
            return Scrub(text);
        }

        if (TextField().IsMatch(property) || ProseField().IsMatch(property))
        {
            return FillerOfLength(text.Length);
        }

        if (NameField().IsMatch(property) && !LooksLikeIdentifier(text))
        {
            return Name(text);
        }

        // The catch-all: three or more words that no rule above claimed are prose, and prose is where people's words
        // and names hide under field names nobody predicted. Replacing a harmless sentence costs a little fidelity;
        // missing a transcript line would publish it.
        if (text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length >= 3 && !text.Contains("://", StringComparison.Ordinal))
        {
            return FillerOfLength(text.Length);
        }

        return Scrub(text);
    }

    // Numbers, e-mail addresses, MAC addresses and public IPs, wherever they appear in a string.
    private string Scrub(string text)
    {
        // A replacement can differ in length from what it replaced, and a neighbouring run of digits can then join
        // it into a new, real-looking number. So replace until nothing but fictional numbers is left.
        for (var pass = 0; ; pass++)
        {
            var found = Guard.FindPhoneLike(text).Where(f => !ReservedNumbers.IsReserved(f.Value)).ToList();
            if (found.Count == 0)
            {
                break;
            }

            if (pass == 5)
            {
                throw new InvalidOperationException("Numbers in a value could not be made fictional after five passes; nothing was written.");
            }

            var builder = new StringBuilder(text);
            foreach (var (index, value) in found.OrderByDescending(f => f.Index))
            {
                builder.Remove(index, value.Length).Insert(index, _numbers.Map(value));
            }

            text = builder.ToString();
        }

        text = Email().Replace(text, m => Consistent(_emails, m.Value, n => $"person{n}@example.invalid"));
        text = Mac().Replace(text, m => Consistent(_macs, m.Value, _ => FakeMac(m.Value)));
        text = IPv6().Replace(text, m => IsPublicIPv6(m.Value) ? Consistent(_ipv6, m.Value, n => $"2001:db8::{n:x}") : m.Value);
        return IPv4().Replace(text, m => IsPublic(m.Value) ? Consistent(_ips, m.Value, n => $"203.0.113.{n % 254 + 1}") : m.Value);
    }

    private string Name(string real) =>
        Consistent(_names, real, n =>
        {
            var hash = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes("name:" + real));
            var first = FirstNames[hash[0] % FirstNames.Length];
            var last = LastNames[hash[1] % LastNames.Length];
            return real.Contains(' ', StringComparison.Ordinal) ? $"{first} {last} {n}" : $"{first}{n}";
        });

    private string FakeMac(string real)
    {
        var hash = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes("mac:" + real.ToUpperInvariant()));
        var separator = real[2];
        // 02 marks a locally administered address, which no manufacturer assigns.
        return string.Join(separator, new[] { (byte)0x02 }.Concat(hash.Take(5)).Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
    }

    private static string Consistent(Dictionary<string, string> map, string real, Func<int, string> make)
    {
        if (!map.TryGetValue(real, out var fake))
        {
            fake = make(map.Count + 1);
            map[real] = fake;
        }

        return fake;
    }

    // Ids, UUIDs and numeric codes can sit in fields called "user" or "owner"; replacing them with names would break joins.
    private static bool LooksLikeIdentifier(string text) =>
        text.All(c => char.IsAsciiHexDigit(c) || c is '-' or '_') || Guid.TryParse(text, out _);

    // Global unicast (2000::/3) outside the documentation range. Times such as 12:34:56 and MAC addresses do not
    // parse as IPv6, and link-local or unique-local addresses stay, like private IPv4.
    private static bool IsPublicIPv6(string candidate) =>
        IPAddress.TryParse(candidate, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
        && (ip.GetAddressBytes()[0] & 0xE0) == 0x20
        && !(ip.GetAddressBytes() is [0x20, 0x01, 0x0d, 0xb8, ..]);

    private static string AddressValue(string field, string real) => field.ToUpperInvariant() switch
    {
        _ when real.Length == 0 => real,
        var f when f.Contains("STREET", StringComparison.Ordinal) || f.Contains("LINE", StringComparison.Ordinal) => "1 Example Street",
        "CITY" or "TOWN" => "Exampleton",
        "REGION" or "COUNTY" or "STATE" => "Exampleshire",
        var f when f.Contains("POSTAL", StringComparison.Ordinal) || f.Contains("POSTCODE", StringComparison.Ordinal) || f.Contains("ZIP", StringComparison.Ordinal) => "ZZ99 9ZZ",
        _ => FillerOfLength(real.Length),
    };

    // An identifier replaced by a keyed one of the same length, keeping a two-letter prefix such as a Twilio SID's,
    // so it still joins across files but cannot be traced back.
    private string KeyedLike(string real, string purpose)
    {
        var hash = Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes($"{purpose}:{real}")));
        var keep = real.Length > 2 && char.IsAsciiLetter(real[0]) && char.IsAsciiLetter(real[1]) ? 2 : 0;
        var body = string.Concat(Enumerable.Repeat(hash, (real.Length / hash.Length) + 1))[..(real.Length - keep)].ToCharArray();

        // Hex can run to nine digits in a row, which reads as a phone number. The eighth digit of any run becomes a
        // letter, so no run gets that long and the result still looks like hex.
        for (int i = 0, run = 0; i < body.Length; i++)
        {
            run = char.IsAsciiDigit(body[i]) ? run + 1 : 0;
            if (run == 8)
            {
                body[i] = (char)('A' + (body[i] - '0') % 6);
                run = 0;
            }
        }

        var fake = new string(body);
        return real[..keep] + (real.Skip(keep).Any(char.IsAsciiLetterLower) ? fake.ToLowerInvariant() : fake);
    }

    private static bool IsPublic(string candidate)
    {
        if (!IPAddress.TryParse(candidate, out var ip) || candidate.Split('.').Any(p => int.Parse(p, CultureInfo.InvariantCulture) > 255))
        {
            return false;
        }

        var b = ip.GetAddressBytes();
        return !(b[0] == 10 || b[0] == 127 || b[0] == 0 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168)
            || (b[0] == 169 && b[1] == 254) || (b[0] == 100 && b[1] is >= 64 and <= 127) || b[0] >= 224
            || (b[0] == 192 && b[1] == 0 && b[2] == 2) || (b[0] == 198 && b[1] == 51 && b[2] == 100) || (b[0] == 203 && b[1] == 0 && b[2] == 113));
    }

    /// <summary>
    /// True for a string of three or more words that is not one of the pseudonymiser's own replacements. In output,
    /// that is prose a rule missed; the fixtures step refuses to keep it.
    /// </summary>
    public static bool IsUnreplacedProse(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length >= 3
            && !trimmed.Contains("://", StringComparison.Ordinal)
            && trimmed != FillerOfLength(trimmed.Length)
            && text != FillerOfLength(text.Length)
            && trimmed != "1 Example Street"
            && !FakeFullName().IsMatch(trimmed);
    }

    // What Name() produces for a real name with a space in it: "First Last N".
    [GeneratedRegex(@"^[A-Z][a-z]+ [A-Z][a-z]+ \d+$")]
    private static partial Regex FakeFullName();

    private static string FillerOfLength(int length)
    {
        var builder = new StringBuilder(length);
        while (builder.Length < length)
        {
            builder.Append(Filler);
        }

        return builder.ToString(0, length);
    }
}
