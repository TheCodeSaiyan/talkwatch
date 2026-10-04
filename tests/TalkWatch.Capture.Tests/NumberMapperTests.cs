using System.Globalization;
using TalkWatch.Capture;
using TalkWatch.FixtureGuard;

namespace TalkWatch.Capture.Tests;

public class NumberMapperTests
{
    private static readonly byte[] Key = [.. Enumerable.Range(1, 32).Select(i => (byte)i)];

    [Fact]
    public void Every_output_is_in_a_reserved_range_and_distinct_inputs_stay_distinct()
    {
        var mapper = new NumberMapper(Key);
        var random = new Random(42);
        var inputs = new HashSet<string>();
        while (inputs.Count < 3000)
        {
            var n = random.Next(0, 100_000_000).ToString("D8", CultureInfo.InvariantCulture);
            inputs.Add(random.Next(4) switch
            {
                0 => "+4420" + n,
                1 => "077" + n,
                2 => "+1212" + n[..7],
                _ => "+331" + n,
            });
        }

        var outputs = inputs.Select(mapper.Map).ToList();

        Assert.All(outputs, o => Assert.True(ReservedNumbers.IsReserved(o), o));
        Assert.Equal(inputs.Count, outputs.Distinct().Count());
    }

    [Fact]
    public void The_same_number_written_different_ways_maps_to_the_same_fictional_number()
    {
        var mapper = new NumberMapper(Key);

        var plus = mapper.Map("+44 20 7123 4567");
        var national = mapper.Map("020 7123 4567");
        var doubleZero = mapper.Map("00442071234567");

        Assert.Equal(plus[3..], national[1..]);
        Assert.Equal(plus[3..], doubleZero[4..]);
        Assert.Equal(plus, mapper.Map("+442071234567"));
    }

    [Theory]
    [InlineData("+44 7911 123456", "+447700900")]
    [InlineData("07911 123456", "07700900")]
    [InlineData("0800 123 4567", "08081570")]
    [InlineData("+1 415 867 5309", "+1")]
    [InlineData("4158675309", "")]
    public void The_style_and_kind_of_number_are_kept(string real, string expectedPrefix)
    {
        var fake = new NumberMapper(Key).Map(real);

        Assert.StartsWith(expectedPrefix, fake, StringComparison.Ordinal);
        Assert.True(ReservedNumbers.IsReserved(fake));
    }

    [Fact]
    public void A_different_key_gives_a_different_mapping()
    {
        byte[] otherKey = [.. Key.Reverse()];
        var numbers = Enumerable.Range(0, 20).Select(i => $"+44 20 7123 {i:D4}").ToList();

        Assert.NotEqual(numbers.Select(new NumberMapper(Key).Map), numbers.Select(new NumberMapper(otherKey).Map));
    }

    [Fact]
    public void A_number_that_is_already_fictional_is_left_alone() =>
        Assert.Equal("+44 20 7946 0123", new NumberMapper(Key).Map("+44 20 7946 0123"));
}
