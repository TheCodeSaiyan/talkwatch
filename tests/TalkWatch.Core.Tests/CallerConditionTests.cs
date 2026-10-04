using TalkWatch.Core.Alerts;
using TalkWatch.Core.Calls;

namespace TalkWatch.Core.Tests;

public class CallerConditionTests
{
    private static readonly NumberNormaliser Uk = new("GB");

    private static CallerCondition Condition(CallerMode mode, string text) => new(mode, CallerCondition.Parse(text, Uk).Entries!);

    [Theory]
    [InlineData("+447700900123", true)]
    [InlineData("+447700900999", false)]
    [InlineData(null, false)]
    public void Only_passes_listed_callers(string? caller, bool passes) =>
        Assert.Equal(passes, Condition(CallerMode.Only, "07700 900123").Matches(caller));

    [Theory]
    [InlineData("+448001234567", false)] // a freephone number, excluded by prefix
    [InlineData(null, false)]            // withheld, excluded
    [InlineData("+441144960042", true)]
    public void Except_passes_everyone_not_listed(string? caller, bool passes) =>
        Assert.Equal(passes, Condition(CallerMode.Except, "+44800*, withheld").Matches(caller));

    [Fact]
    public void Any_passes_everyone() =>
        Assert.True(CallerCondition.Anyone.Matches(null));

    [Fact]
    public void Numbers_are_normalised_and_repeats_dropped()
    {
        var (entries, problem) = CallerCondition.Parse("07700 900123\n+44 7700 900123, WITHHELD, +44 800*", Uk);

        Assert.Null(problem);
        Assert.Equal(["+447700900123", CallerCondition.Withheld, "+44800*"], entries);
    }

    [Theory]
    [InlineData("0800*")]     // a prefix must be in E.164: a partial national number has no certain reading
    [InlineData("+*")]
    [InlineData("not a number")]
    [InlineData("123")]
    public void What_cannot_be_read_is_named(string text) =>
        Assert.Equal(text, CallerCondition.Parse($"+447700900123, {text}", Uk).Problem);
}
