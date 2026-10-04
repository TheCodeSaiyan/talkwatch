using PhoneNumbers;

namespace TalkWatch.Core.Calls;

/// <summary>
/// Normalises phone numbers to E.164 for matching, using the site's own region for numbers written nationally.
/// The raw form is always kept beside it: short codes, internal extensions and withheld numbers do not normalise.
/// </summary>
public sealed class NumberNormaliser(string defaultRegion)
{
    private static readonly PhoneNumberUtil Util = PhoneNumberUtil.GetInstance();

    public string DefaultRegion { get; } = defaultRegion;

    /// <summary>
    /// A number in E.164 as people write it, grouped in the international form (+44 7564 761865); null for none, and
    /// the number as it came when it cannot be parsed.
    /// </summary>
    public static string? Display(string? e164)
    {
        if (string.IsNullOrWhiteSpace(e164))
        {
            return null;
        }

        try
        {
            return Util.Format(Util.Parse(e164, null), PhoneNumberFormat.INTERNATIONAL);
        }
        catch (NumberParseException)
        {
            return e164;
        }
    }

    /// <summary>
    /// The region (ISO 3166 code, such as "GB") a number in E.164 belongs to, or null when it cannot be told. A dialling
    /// code several countries share (+1) gives the number's own country when it can be told, else the code's main one.
    /// </summary>
    public static string? RegionOf(string? e164)
    {
        if (string.IsNullOrWhiteSpace(e164))
        {
            return null;
        }

        try
        {
            // A number in a range not in service (such as the ones set aside for drama) has no region of its own; the
            // country its dialling code belongs to is still where it is from.
            var number = Util.Parse(e164, null);
            return Util.GetRegionCodeForNumber(number) is { Length: 2 } region and not "ZZ" ? region
                : Util.GetRegionCodeForCountryCode(number.CountryCode) is { Length: 2 } main and not "ZZ" ? main : null;
        }
        catch (NumberParseException)
        {
            return null;
        }
    }

    /// <summary>The number in E.164, or null when it is not a possible phone number in any region.</summary>
    public string? ToE164(string? raw)
    {
        // Extensions and short codes (1001, 999) parse as 'possible' numbers but are not external numbers.
        if (string.IsNullOrWhiteSpace(raw) || raw.Count(char.IsAsciiDigit) < 7)
        {
            return null;
        }

        try
        {
            var number = Util.Parse(raw, DefaultRegion);
            return Util.IsPossibleNumber(number) ? Util.Format(number, PhoneNumberFormat.E164) : null;
        }
        catch (NumberParseException)
        {
            return null;
        }
    }
}
