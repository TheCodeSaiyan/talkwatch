using TalkWatch.Core.Audio;
using TalkWatch.Core.Talk;
using TalkWatch.Replay;

namespace TalkWatch.Core.Tests;

public class SwitchboardTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// MPEG-1 layer III frames at 128 kbit/s and 44.1 kHz: 1152 samples each, 417 bytes, or 418 with padding. An ID3v2
    /// tag in front and an ID3v1 tag behind, as encoders write them.
    /// </summary>
    private static byte[] Frames(int count)
    {
        var bytes = new List<byte>();
        bytes.AddRange([(byte)'I', (byte)'D', (byte)'3', 3, 0, 0, 0, 0, 0, 20]);
        bytes.AddRange(new byte[20]);
        for (var i = 0; i < count; i++)
        {
            var padded = i % 3 == 0;
            var frame = new byte[padded ? 418 : 417];
            (frame[0], frame[1], frame[2], frame[3]) = (0xFF, 0xFB, (byte)(0x90 | (padded ? 0x02 : 0)), 0x64);
            bytes.AddRange(frame);
        }

        bytes.AddRange([(byte)'T', (byte)'A', (byte)'G', .. new byte[125]]);
        return [.. bytes];
    }

    [Theory]
    [InlineData(1)]
    [InlineData(383)] // Ten seconds, to the frame.
    public void An_mp3_lasts_as_long_as_its_frames(int frames) =>
        Assert.Equal(frames * 1152 / 44100.0, Mp3.Duration(Frames(frames))!.Value.TotalSeconds, precision: 6);

    [Fact]
    public void Something_that_is_not_mp3_has_no_length() => Assert.Null(Mp3.Duration("not audio at all"u8));

    [Fact]
    public async Task The_switchboard_tree_reads_as_switchboards_menus_and_where_they_lead_with_greetings_that_can_be_timed()
    {
        var console = new FixtureConsole(FixtureConsole.DefaultDirectory);
        var talk = new TalkClient(console.CreateClient());
        await talk.SignInAsync(FixtureConsole.Username, FixtureConsole.Password, Ct);

        var nodes = await talk.GetSwitchboardAsync(Ct);

        var roots = nodes.Where(n => n.Type == SwitchboardNode.Root).ToList();
        Assert.Equal([15, 45], roots.Select(r => r.InternalId!.Value).Order());
        Assert.Equal(["+441144960042"], roots.Single(r => r.InternalId == 45).Numbers);
        // The menu on switchboard 15: keys 1, 2 and 3, and a menu of its own under 1.
        Assert.Equal([1, 2, 3], nodes.Where(n => n.ParentId == "swb_15" && n.Type == SwitchboardNode.Menu).Select(n => n.Key!.Value).Order());
        Assert.Equal([1, 2], nodes.Where(n => n.ParentId == "swb_16").Select(n => n.Key!.Value).Order());

        var greeting = await talk.GetSwitchboardAudioAsync(roots.Single(r => r.InternalId == 15).GreetingFileName!, Ct);
        Assert.InRange(Mp3.Duration(greeting)!.Value.TotalSeconds, 1, 120);
        Assert.Null(await talk.GetSwitchboardAudioAsync("not-there.mp3", Ct));
    }

    [Fact]
    public void A_journey_is_the_switchboard_reached_and_the_options_chosen_in_order()
    {
        var journey = MenuJourney.Of(
        [
            ("call_started", """{"to": "+441174960404", "to_smart_attendant_id": 15}"""),
            ("keypress", """{"key": "1"}"""),
            ("entered_sa_menu", """{"sa_id": 15, "sa_item_id": 16, "sa_item_key": 1, "sa_item_type": "ivr", "sa_item_title": "Sales"}"""),
            ("entered_sa_menu", """{"sa_id": 16, "sa_item_id": 42, "sa_item_key": 2, "sa_item_type": "ivr", "sa_item_title": "New orders"}"""),
            ("call_hangup", """{"hangup_cause": "normal_end"}"""),
        ]);

        Assert.Equal(15, journey.SwitchboardId);
        Assert.Equal([new MenuChoice(15, 16, 1, "Sales"), new MenuChoice(16, 42, 2, "New orders")], journey.Choices);
        // A call straight to a number: no switchboard, no choices.
        var direct = MenuJourney.Of([("call_started", """{"to": "+441174960404"}"""), ("call_hangup", null)]);
        Assert.Null(direct.SwitchboardId);
        Assert.Empty(direct.Choices);
    }
}
