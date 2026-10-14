using TalkWatch.Core.Talk;
using TalkWatch.Data;
using TalkWatch.Web.Components.Calls;

namespace TalkWatch.Web.Tests;

/// <summary>A switchboard node as Operator shows it: one badge on the flow, and a panel whose detail depends on who is looking.</summary>
public class NodeViewTests
{
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    // Wednesday 14 October 2026, 10:42 in London.
    private static readonly DateTimeOffset Wednesday = new(2026, 10, 14, 10, 42, 0, TimeSpan.FromHours(1));

    private static SwitchboardNodeRow Row(string json)
    {
        var id = System.Text.Json.JsonDocument.Parse(json).RootElement.GetProperty("id").GetString()!;
        return new SwitchboardNodeRow { NodeId = id, Json = json };
    }

    // A switchboard: key 1 split by weekday hours into a ring group or voicemail; key 2 with no audio; key 3 switched
    // off with nothing under it; allow-listed dialling by extension.
    private static readonly SwitchboardNodeRow[] Tree =
    [
        Row("""{"id": "swb_1", "type": "root", "internal_id": 1, "title": "Main line", "ext": "0005", "numbers": ["+441174960404"], "path": ["container"], "greeting": "Thanks for calling.", "greeting_type": "generated", "greeting_file_name": "a.mp3", "voice_type": "voice2", "voice_language": "en-GB", "sa_menu_noaction": "drop_call", "hold_music": {"type": "custom", "title": "Office loop"}, "ext_dialing": {"enabled": true, "target_scope": "allow_list", "target_user_group_ids": [1, "u-sam"]}}"""),
        Row("""{"id": "swb_2", "type": "ivr", "internal_id": 2, "key": 1, "title": "Sales", "path": ["container", "swb_1"], "greeting_file_name": "b.mp3"}"""),
        Row("""{"id": "swb_3", "type": "time", "title": "Weekdays", "day_ranges": {"1": [{"from": 540, "to": 1020}], "2": [{"from": 540, "to": 1020}], "3": [{"from": 540, "to": 1020}], "4": [{"from": 540, "to": 1020}], "5": [{"from": 540, "to": 1020}]}, "path": ["container", "swb_1", "swb_2"]}"""),
        Row("""{"id": "grp_3_1", "type": "group", "group_id": 1, "group_name": "Sales team", "group_vm_receiver_uuids": ["u-sam"], "path": ["container", "swb_1", "swb_2", "swb_3"]}"""),
        Row("""{"id": "swb_4", "type": "time", "title": "Out of hours", "is_default_time_node": true, "path": ["container", "swb_1", "swb_2"]}"""),
        Row("""{"id": "grp_4_1", "type": "group", "group_id": 1, "group_name": "Sales team", "call_action": "voicemail", "path": ["container", "swb_1", "swb_2", "swb_4"]}"""),
        Row("""{"id": "swb_5", "type": "ivr", "internal_id": 5, "key": 2, "title": "Accounts", "missing_audio": true, "path": ["container", "swb_1"]}"""),
        Row("""{"id": "grp_5_2", "type": "group", "group_id": 2, "group_name": "Accounts team", "path": ["container", "swb_1", "swb_5"]}"""),
        Row("""{"id": "swb_6", "type": "ivr", "internal_id": 6, "key": 3, "title": "Christmas closure", "disabled": true, "path": ["container", "swb_1"]}"""),
    ];

    private static string? Name(string id) => id switch { "1" => "Sales team", "u-sam" => "Sam Kit", _ => null };

    [Fact]
    public void Each_node_carries_the_most_pressing_badge()
    {
        var badges = NodeViews.Badges(NodeViews.Nodes(Tree), Wednesday, London);

        Assert.Equal(NodeBadge.OpenNow, badges["swb_3"]);
        Assert.Equal(NodeBadge.NotNow, badges["swb_4"]);
        Assert.Equal(NodeBadge.MissingAudio, badges["swb_5"]);
        // Switched off says so, rather than that it goes nowhere: operators see a clean 'Off'.
        Assert.Equal(NodeBadge.Off, badges["swb_6"]);
        Assert.Equal(NodeBadge.None, badges["swb_2"]);

        var evening = NodeViews.Badges(NodeViews.Nodes(Tree), Wednesday.AddHours(8), London);
        Assert.Equal((NodeBadge.NotNow, NodeBadge.OpenNow), (evening["swb_3"], evening["swb_4"]));
    }

    [Fact]
    public void An_operator_gets_the_summary_and_never_the_setup()
    {
        var panel = NodeViews.Panel("swb_1", NodeViews.Nodes(Tree), Wednesday, London, admin: false, Name, [])!;

        Assert.Equal(("Main line", "0005", "Drops the call"), (panel.Title, panel.Ext, panel.NoKeyPress));
        Assert.True(panel.CanPlay);
        Assert.Equal("Thanks for calling.", panel.Greeting!.Text);
        Assert.Equal([(1, "Sales", NodeBadge.None), (2, "Accounts", NodeBadge.MissingAudio), (3, "Christmas closure", NodeBadge.Off)],
            panel.Routes.Select(r => (r.Key!.Value, r.Label, r.Badge)));
        Assert.Equal("Europe/London", panel.Zone);
        Assert.Null(panel.Setup);
        // The switched-off option's problem is not the operator's: it raises nothing in their panel.
        Assert.Empty(panel.Flags);
    }

    [Fact]
    public void An_admin_also_gets_the_setup_and_what_a_switched_off_node_would_raise()
    {
        var nodes = NodeViews.Nodes(Tree);
        var root = NodeViews.Panel("swb_1", nodes, Wednesday, London, admin: true, Name, [])!;

        Assert.Equal("On, limited to an allow-list", root.Setup!.DialByExt);
        Assert.Equal(["Sales team", "Sam Kit"], root.Setup.AllowList);
        Assert.Equal("Custom · Office loop", root.Setup.HoldMusic);

        var off = NodeViews.Panel("swb_6", nodes, Wednesday, London, admin: true, Name, [])!;
        Assert.Equal([HealthIssue.EmptyTarget], off.Setup!.DraftFlags.Select(f => f.Issue));
        Assert.Empty(off.Flags);
    }

    [Fact]
    public void A_node_split_by_hours_says_which_branch_is_taking_calls_and_until_when()
    {
        var sales = NodeViews.Panel("swb_2", NodeViews.Nodes(Tree), Wednesday, London, admin: false, Name, [])!;

        Assert.Equal("until 17:00", sales.Until);
        Assert.Equal([("Weekdays", "Rings Sales team", NodeBadge.OpenNow), ("Out of hours", "Voicemail at Sales team", NodeBadge.NotNow)],
            sales.Routes.Select(r => (r.Label, r.Detail, r.Badge)));
        Assert.Equal([new MinuteRange(540, 1020)], sales.Week!.Days[DayOfWeek.Wednesday]);
        Assert.Equal("Main line · menu option, key 1", sales.Context);
    }

    [Fact]
    public void Changes_say_what_changed_newest_first_and_ignore_a_redrawn_waveform()
    {
        SwitchboardNodeSnapshot At(int day, string json) => new() { NodeId = "swb_1", CapturedAt = Wednesday.AddDays(day), JsonHash = day.ToString(System.Globalization.CultureInfo.InvariantCulture), Json = json };
        var changes = NodeViews.Changes(
        [
            At(-10, """{"title": "Main", "greeting": "Hello", "greeting_waveform_data": [1]}"""),
            At(-5, """{"title": "Main", "greeting": "Hello", "greeting_waveform_data": [2]}"""),
            At(-2, """{"title": "Main line", "greeting": "Thanks", "disabled": true, "greeting_waveform_data": [2]}"""),
        ]);

        Assert.Equal(["Renamed, Greeting changed, Switched off", "First seen"], changes.Select(c => c.Words));
    }
}
