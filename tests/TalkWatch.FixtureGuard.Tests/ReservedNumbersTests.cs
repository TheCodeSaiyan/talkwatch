using TalkWatch.FixtureGuard;

namespace TalkWatch.FixtureGuard.Tests;

public class ReservedNumbersTests
{
    [Theory]
    [InlineData("+44 20 7946 0123")]
    [InlineData("020 7946 0999")]
    [InlineData("01632 960000")]
    [InlineData("07700900123")]
    [InlineData("+447700900999")]
    [InlineData("0044 113 496 0500")]
    [InlineData("447700900001")]
    [InlineData("028 9649 6123")]
    [InlineData("0909 879 0000")]
    [InlineData("+1 (212) 555-0100")]
    [InlineData("12125550199")]
    [InlineData("2125550150")]
    public void Fictional_numbers_are_reserved(string number) => Assert.True(ReservedNumbers.IsReserved(number));

    [Theory]
    [InlineData("+44 20 7946 1123")]   // one block past London's reserved thousand
    [InlineData("07700 901000")]
    [InlineData("01632 961000")]
    [InlineData("028 9018 0123")]      // not an Ofcom range, whatever it looks like
    [InlineData("+44 7700 90012")]     // too short to be the reserved number it resembles
    [InlineData("+1 212 555 1234")]    // 555 outside 0100-0199 is real
    [InlineData("+1 212 555 0200")]
    [InlineData("+33 1 23 45 67 89")]  // no reserved ranges known for other countries
    [InlineData("1759147200")]         // an epoch timestamp is not accepted by default
    public void Other_numbers_are_not_reserved(string number) => Assert.False(ReservedNumbers.IsReserved(number));
}
