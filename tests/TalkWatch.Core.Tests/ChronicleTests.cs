using TalkWatch.Core.Calls;
using TalkWatch.Core.Talk;
using TalkWatch.Replay;

namespace TalkWatch.Core.Tests;

public class ChronicleTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 28, 17, 27, 1, TimeSpan.Zero);

    private static ChronicleEvent At(double seconds, string name, string? data = null) => new(T0.AddSeconds(seconds), name, data);

    private static readonly ChronicleNames Names = new(
        Attendant: id => id == "45" ? "Main Attendant" : null,
        Contact: uuid => uuid == "c-alex" ? "Alex Morgan" : null,
        User: uuid => uuid == "u-priya" ? "Priya Shah" : null,
        Number: n => n == "+441144960042" ? "Main Office" : null);

    [Fact]
    public void A_call_through_the_menu_reads_as_what_happened_with_its_gaps()
    {
        // The shape of a real captured call: 24s in the menu, 7s ringing, 44s talking.
        var c = Chronicle.Build(
        [
            At(0, "call_started", """{"to":"+441144960042","from":"+447700900083","to_smart_attendant_id":45}"""),
            At(24, "seq_call_trying_endpoints", """{"contact_uuids":["c-alex"]}"""),
            At(31, "call_accepted", """{"accepted_by":"+447700900210","accepted_by_contact_uuid":"c-alex"}"""),
            At(75, "call_hangup", """{"hangup_cause":"normal_end"}"""),
        ], "in", Names);

        Assert.Equal(
            ["Call came in on Main Office", "Main Attendant played its menu", "Ringing Alex Morgan", "Alex Morgan answered after 7s", "Call ended"],
            c.Steps.Select(s => s.Text));
        Assert.Equal("From +447700900083", c.Steps[0].Detail);
        Assert.Equal(["Main Office", "Main Attendant", "Alex Morgan", "Alex Morgan", null], c.Steps.Select(s => s.Subject));
        Assert.Equal([(SegmentKind.Menu, 24.0), (SegmentKind.Ringing, 7.0), (SegmentKind.Talking, 44.0)], c.Band.Select(b => (b.Kind, b.Length.TotalSeconds)));
        Assert.Equal(TimeSpan.FromSeconds(75), c.Total);
        Assert.Equal(TimeSpan.FromSeconds(7), c.TimeToAnswer);
    }

    [Fact]
    public void A_missed_call_says_nobody_answered_and_has_no_talking()
    {
        var c = Chronicle.Build(
        [
            At(0, "call_started", """{"to":"+441144960042","from":"+447700900083"}"""),
            At(1, "seq_call_trying_endpoints", """{"contact_uuids":["c-alex","c-unknown","c-other"]}"""),
            At(31, "call_hangup", """{"hangup_cause":"originator_cancel"}"""),
        ], "in", Names);

        Assert.Equal("Ringing Alex Morgan and 2 contacts", c.Steps[1].Text);
        Assert.Equal("Caller hung up before anyone answered", c.Steps[^1].Text);
        Assert.Equal([SegmentKind.Ringing], c.Band.Select(b => b.Kind));
        Assert.Null(c.TimeToAnswer);
    }

    [Fact]
    public void A_contact_the_directory_cannot_name_is_called_by_the_number_they_answered_on_in_both_steps()
    {
        var c = Chronicle.Build(
        [
            At(0, "call_started", """{"to":"+441144960042"}"""),
            At(2, "seq_call_trying_endpoints", """{"contact_uuids":["c-nobody-knows"]}"""),
            At(9, "call_accepted", """{"accepted_by":"+447700900210","accepted_by_contact_uuid":"c-nobody-knows"}"""),
        ], "in", Names);

        Assert.Equal("Ringing +447700900210", c.Steps[1].Text);
        Assert.Equal("+447700900210 answered after 7s", c.Steps[2].Text);
        Assert.Equal(c.Steps[1].Subject, c.Steps[2].Subject);
    }

    [Theory]
    [InlineData("cancelled")]
    [InlineData("originator_cancel")]
    public void A_caller_who_gives_up_reads_as_hanging_up_whatever_Talk_calls_it(string cause)
    {
        var c = Chronicle.Build([At(0, "call_started", "{}"), At(6, "call_hangup", $$"""{"hangup_cause":"{{cause}}"}""")], "in", Names);
        Assert.Equal("Caller hung up before anyone answered", c.Steps[^1].Text);
    }

    [Fact]
    public void Voicemail_is_its_own_stretch()
    {
        var c = Chronicle.Build(
        [
            At(0, "call_started", """{"to":"+441144960042"}"""),
            At(1, "seq_call_trying_endpoints", "{}"),
            At(21, "call_sent_to_voicemail", "{}"),
            At(44, "vm_msg_recorded", "{}"),
            At(45, "call_hangup", """{"hangup_cause":"normal_end"}"""),
        ], "in", Names);

        Assert.Equal([(SegmentKind.Ringing, 20.0), (SegmentKind.Voicemail, 24.0)], c.Band.Select(b => (b.Kind, b.Length.TotalSeconds)));
        Assert.Contains(c.Steps, s => s.Text == "Voicemail left");
        Assert.Equal("Caller hung up", c.Steps[^1].Text);
    }

    [Fact]
    public void An_outbound_call_names_who_dialled()
    {
        var c = Chronicle.Build([At(0, "call_started", """{"from_user_uuid":"u-priya","to":"+447700900318"}""")], "out", Names);
        Assert.Equal("Priya Shah dialled +447700900318", c.Steps[0].Text);
    }

    [Fact]
    public void An_event_TalkWatch_has_no_words_for_is_shown_not_dropped_and_bad_data_is_tolerated()
    {
        var c = Chronicle.Build([At(0, "call_parked_somewhere", null), At(1, "call_accepted", "{not json")], "in", Names);
        Assert.Equal(["Talk reported call parked somewhere", "Someone answered"], c.Steps.Select(s => s.Text));
    }

    [Fact]
    public void Every_captured_call_builds_without_falling_back_to_raw_event_names()
    {
        var calls = new FixtureConsole(FixtureConsole.DefaultDirectory).Calls();
        Assert.NotEmpty(calls);
        foreach (var call in calls)
        {
            var c = Chronicle.Build(call.CallEvents.Select(e => new ChronicleEvent(e.Time, e.Event, e.EventData?.GetRawText())), call.Direction ?? "in", ChronicleNames.None);
            Assert.DoesNotContain(c.Steps, s => s.Kind == StepKind.Other);
            Assert.All(c.Band, b => Assert.True(b.Length > TimeSpan.Zero));
        }
    }
}
