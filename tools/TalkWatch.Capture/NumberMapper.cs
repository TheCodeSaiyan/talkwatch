using System.Security.Cryptography;
using System.Text;
using TalkWatch.FixtureGuard;

namespace TalkWatch.Capture;

/// <summary>
/// Maps real phone numbers to fictional ones, the same real number always to the same fictional one,
/// so calls, extensions and DIDs still join up across every file of a capture.
/// </summary>
/// <remarks>
/// The starting slot comes from a keyed HMAC, so the mapping cannot be reversed without the key. Collisions are
/// resolved by taking the next free slot, which keeps distinct numbers distinct; the result is stable for the
/// same key, the same captures and the same processing order.
/// </remarks>
public sealed class NumberMapper(byte[] key)
{
    // UK pools by the kind of number, so a mobile stays a mobile and a freephone stays a freephone.
    private static readonly string[] Geographic =
        ["2079460", "1134960", "1144960", "1154960", "1164960", "1174960", "1184960", "1214960",
         "1314960", "1414960", "1514960", "1614960", "1914980", "2896496", "2920180", "1632960"];
    private static readonly string[] Mobile = ["7700900"];
    private static readonly string[] Freephone = ["8081570"];
    private static readonly string[] Premium = ["9098790"];
    private static readonly string[] NonGeographic = ["3069990"];

    // North America: 555-0100 to 555-0199 in area codes 201 upwards.
    private const int NanpAreaCodes = 799;

    private readonly Dictionary<string, string> _canonicalToFake = [];
    private readonly Dictionary<string, HashSet<int>> _taken = [];

    public int Count => _canonicalToFake.Count;

    /// <summary>The fictional replacement for <paramref name="original"/>, written in the same style (+44, 0044, 0 or bare).</summary>
    public string Map(string original)
    {
        if (ReservedNumbers.IsReserved(original))
        {
            return original;
        }

        var (style, country, national) = Parse(original);
        var canonical = country + national;
        if (!_canonicalToFake.TryGetValue(canonical, out var fake))
        {
            fake = country == "1" ? AllocateNanp(canonical) : AllocateUk(canonical, national);
            _canonicalToFake[canonical] = fake;
        }

        var (fakeCountry, fakeNational) = fake.StartsWith('1') ? ("1", fake[1..]) : ("44", fake[2..]);
        var written = style switch
        {
            Style.Plus => "+" + fakeCountry + fakeNational,
            Style.DoubleZero => "00" + fakeCountry + fakeNational,
            Style.National => fakeCountry == "44" ? "0" + fakeNational : fakeNational,
            Style.BareWithCountry => fakeCountry + fakeNational,
            _ => fakeCountry == "1" ? fakeNational : "0" + fakeNational,
        };

        return ReservedNumbers.IsReserved(written)
            ? written
            : throw new InvalidOperationException("Internal error: a mapped number fell outside the reserved ranges.");
    }

    private enum Style { Plus, DoubleZero, National, BareWithCountry, Bare }

    private static (Style Style, string Country, string National) Parse(string original)
    {
        var digits = new string(original.Where(char.IsAsciiDigit).ToArray());
        if (original.TrimStart().StartsWith('+'))
        {
            return SplitInternational(Style.Plus, digits);
        }

        if (digits.StartsWith("00", StringComparison.Ordinal))
        {
            return SplitInternational(Style.DoubleZero, digits[2..]);
        }

        if (digits.StartsWith('0'))
        {
            return (Style.National, "44", digits[1..]);
        }

        if (digits.Length == 12 && digits.StartsWith("44", StringComparison.Ordinal))
        {
            return (Style.BareWithCountry, "44", digits[2..]);
        }

        if (digits.Length == 11 && digits.StartsWith('1'))
        {
            return (Style.BareWithCountry, "1", digits[1..]);
        }

        return digits.Length == 10 ? (Style.Bare, "1", digits) : (Style.Bare, "x", digits);
    }

    private static (Style, string, string) SplitInternational(Style style, string digits) =>
        digits.StartsWith("44", StringComparison.Ordinal) ? (style, "44", digits[2..])
        : digits.StartsWith('1') ? (style, "1", digits[1..])
        : (style, "x", digits); // Other countries have no reserved ranges, so they become UK fictional numbers.

    private string AllocateUk(string canonical, string national)
    {
        var pool = national.Length > 0 && canonical.StartsWith("44", StringComparison.Ordinal)
            ? national[0] switch
            {
                '7' => Mobile,
                '8' => Freephone,
                '9' => Premium,
                '3' => NonGeographic,
                _ => Geographic,
            }
            : Geographic;

        var slot = Allocate(string.Join(",", pool), pool.Length * 1000, canonical);
        return "44" + pool[slot / 1000] + (slot % 1000).ToString("D3", System.Globalization.CultureInfo.InvariantCulture);
    }

    private string AllocateNanp(string canonical)
    {
        var slot = Allocate("nanp", NanpAreaCodes * 100, canonical);
        var areaCode = 201 + (slot / 100);
        return FormattableString.Invariant($"1{areaCode}55501{slot % 100:D2}");
    }

    private int Allocate(string poolName, int size, string canonical)
    {
        var taken = _taken.TryGetValue(poolName, out var set) ? set : _taken[poolName] = [];
        if (taken.Count >= size)
        {
            throw new InvalidOperationException($"More distinct numbers than the fictional pool '{poolName}' can hold ({size}).");
        }

        var hash = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(canonical));
        var slot = (int)(BitConverter.ToUInt32(hash, 0) % (uint)size);
        while (!taken.Add(slot))
        {
            slot = (slot + 1) % size;
        }

        return slot;
    }
}
