using TalkWatch.Web.Components.Calls;

namespace TalkWatch.Web.Tests;

/// <summary>A list on the Now board holds still while someone points at it: rows never shift under a click.</summary>
public sealed class HeldListTests
{
    private sealed record Row(string Key, string State);

    [Fact]
    public void A_held_list_keeps_its_rows_and_order_and_counts_what_is_waiting()
    {
        List<Row> shown = [new("a", "ringing"), new("b", "connected")];
        List<Row> truth = [new("c", "ringing"), new("b", "connected"), new("a", "connected")];

        var (kept, waiting) = HeldList.Hold(shown, truth, r => r.Key);

        Assert.Equal(["a", "b"], kept.Select(r => r.Key));
        Assert.Equal(1, waiting); // c, arriving
    }

    [Fact]
    public void Rows_already_shown_still_change_in_place_while_the_list_is_held()
    {
        List<Row> shown = [new("a", "ringing")];
        List<Row> truth = [new("a", "connected")];

        var (kept, waiting) = HeldList.Hold(shown, truth, r => r.Key);

        Assert.Equal("connected", Assert.Single(kept).State);
        Assert.Equal(0, waiting);
    }

    [Fact]
    public void A_row_that_has_gone_waits_in_the_held_list_as_it_last_was()
    {
        List<Row> shown = [new("a", "ended"), new("b", "connected")];
        List<Row> truth = [new("b", "connected")];

        var (kept, waiting) = HeldList.Hold(shown, truth, r => r.Key);

        Assert.Equal([new Row("a", "ended"), new Row("b", "connected")], kept);
        Assert.Equal(1, waiting); // a, leaving
    }
}
