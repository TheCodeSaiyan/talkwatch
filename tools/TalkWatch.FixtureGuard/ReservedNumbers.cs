namespace TalkWatch.FixtureGuard;

/// <summary>
/// Decides whether a phone-like string falls in a range reserved for fiction, so it cannot belong to anyone.
/// </summary>
public static class ReservedNumbers
{
    // Ofcom's numbers for TV and radio drama, as listed at
    // https://www.ofcom.org.uk/phones-and-broadband/phone-numbers/numbers-for-drama (last updated 11 May 2023).
    // Each entry is the first seven digits of a ten-digit national significant number (the number
    // without its leading 0), covering a block of 1,000.
    private static readonly string[] UkBlocks =
    [
        "1134960", "1144960", "1154960", "1164960", "1174960", "1184960",
        "1214960", "1314960", "1414960", "1514960", "1614960", "1914980",
        "2079460", "2896496", "2920180",
        "1632960", "7700900", "8081570", "9098790", "3069990",
    ];

    /// <summary>True when every digit in <paramref name="candidate"/> is accounted for by a reserved range.</summary>
    public static bool IsReserved(string candidate)
    {
        var international = candidate.TrimStart().StartsWith('+');
        var digits = new string(candidate.Where(char.IsAsciiDigit).ToArray());

        if (digits.StartsWith("00", StringComparison.Ordinal))
        {
            international = true;
            digits = digits[2..];
        }

        if (international || digits.Length > 11)
        {
            return (digits.StartsWith("44", StringComparison.Ordinal) && IsReservedUk(digits[2..]))
                || (digits.StartsWith('1') && IsReservedNorthAmerican(digits[1..]));
        }

        if (digits.StartsWith('0'))
        {
            return IsReservedUk(digits[1..]);
        }

        // A bare number with no prefix is only accepted as North American, with or without its country code.
        return IsReservedNorthAmerican(digits.Length == 11 && digits.StartsWith('1') ? digits[1..] : digits);
    }

    private static bool IsReservedUk(string nationalNumber) =>
        nationalNumber.Length == 10 && UkBlocks.Any(block => nationalNumber.StartsWith(block, StringComparison.Ordinal));

    // NANP reserves 555-0100 to 555-0199 in every area code for fiction.
    private static bool IsReservedNorthAmerican(string nationalNumber) =>
        nationalNumber.Length == 10 && nationalNumber[3..8] == "55501";
}
