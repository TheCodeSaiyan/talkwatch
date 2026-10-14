using TalkWatch.Core.Talk;
using TalkWatch.Replay;

namespace TalkWatch.Core.Tests;

/// <summary>A switchboard node's setup beyond its place in the tree: what it plays, when it is open, and whether it is healthy.</summary>
public class SwitchboardSetupTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    private static async Task<IReadOnlyList<SwitchboardNode>> Captured(string version)
    {
        var directory = Path.Combine(Path.GetDirectoryName(FixtureConsole.DefaultDirectory)!, "talk-" + version);
        var talk = new TalkClient(new FixtureConsole(directory).CreateClient());
        await talk.SignInAsync(FixtureConsole.Username, FixtureConsole.Password, Ct);
        return await talk.GetSwitchboardAsync(Ct);
    }

    /// <summary>A switchboard response holding these nodes, each a JSON object with its id.</summary>
    private static IReadOnlyList<SwitchboardNode> Tree(params string[] nodes)
    {
        var byId = string.Join(", ", nodes.Select(n => $"\"{System.Text.Json.JsonDocument.Parse(n).RootElement.GetProperty("id").GetString()}\": {n}"));
        return Switchboard.Parse("{\"entities\": {\"nodes\": {\"byId\": {" + byId + "}}}}");
    }

    private static OpeningHours Hours(string dayRanges, bool holidaysEnabled = false, string holidays = "[]") =>
        NodeSetup.Of($$"""{"type": "time", "day_ranges": {{dayRanges}}, "holidays_enabled": {{(holidaysEnabled ? "true" : "false")}}, "holidays": {{holidays}}}""").OpeningHours!;

    [Theory]
    [InlineData("5.3.2")]
    [InlineData("5.4.5")]
    public async Task Each_node_keeps_its_setup_on_both_talk_versions(string version)
    {
        var nodes = await Captured(version);
        var root = nodes.Single(n => n.Id == "swb_15").Setup;

        Assert.Equal("0005", root.Ext);
        Assert.False(root.Disabled);
        Assert.Equal(("generated", "voice2", "en-GB", false), (root.Greeting!.Type, root.Greeting.Voice, root.Greeting.Language, root.Greeting.Generating));
        Assert.StartsWith("Lorem ipsum", root.Greeting.Text);
        // Only 5.4.5 says whether the audio is missing; on 5.3.2 it is not known, rather than false.
        Assert.Equal(version == "5.4.5" ? false : null, root.Greeting.Missing);
        Assert.Equal("drop_call", root.NoKeyPress!.Action);
        Assert.Equal((true, "allow_list", true), (root.DialByExt!.Enabled, root.DialByExt.Scope, root.DialByExt.LimitedToAllowList));
        Assert.Equal(["1", "abe3a229-7539-48dd-a8e3-e23fd4d9f72f"], root.DialByExt.AllowList);

        // Opening hours: Monday to Friday, 09:00 to 17:00, and a default branch for the rest.
        var open = nodes.Single(n => n.Id == "swb_39").Setup.OpeningHours!;
        Assert.False(open.IsDefault);
        Assert.Equal([new MinuteRange(540, 1020)], open.Days[DayOfWeek.Monday]);
        Assert.Empty(open.Days[DayOfWeek.Sunday]);
        Assert.Equal([], open.Holidays);
        Assert.True(nodes.Single(n => n.Id == "swb_38").Setup.OpeningHours!.IsDefault);
        Assert.Equal("voicemail", nodes.Single(n => n.Id == "grp_38_1").Setup.CallAction);
        Assert.Equal([], nodes.Single(n => n.Id == "grp_39_1").Setup.Receivers);

        // A node that is not about time has no hours, and one that plays nothing has no greeting.
        Assert.Null(root.OpeningHours);
        Assert.Null(nodes.Single(n => n.Id == "grp_39_1").Setup.Greeting);
        Assert.Contains("greeting_waveform_data", nodes.Single(n => n.Id == "swb_15").Json);
    }

    [Fact]
    public void A_user_node_on_talk_5_4_5_names_its_user_by_ulp_id()
    {
        // A later capture than the one the fixture console serves, taken after a user was added to a menu.
        var nodes = Switchboard.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(FixtureConsole.DefaultDirectory)!, "talk-5.4.5", "0195-http-proxy-talk-api-switchboard.json")));

        Assert.Equal("abe3a229-7539-48dd-a8e3-e23fd4d9f72f", nodes.Single(n => n.Id == "usr_43_abe3a229-7539-48dd-a8e3-e23fd4d9f72f").UserUuid);
        Assert.All(nodes.Where(n => n.Type == "user"), n => Assert.NotNull(n.UserUuid));
    }

    [Theory]
    [InlineData("5.3.2")]
    [InlineData("5.4.5")]
    public async Task The_captured_switchboards_are_healthy(string version) =>
        Assert.Empty(NodeHealth.Of(await Captured(version)));

    [Theory]
    [InlineData("2026-10-12T09:00:00+01:00", OpenState.Open)] // Monday at nine, British Summer Time.
    [InlineData("2026-10-12T08:59:00+01:00", OpenState.Closed)]
    [InlineData("2026-10-12T17:00:00+01:00", OpenState.Closed)] // The range ends at 17:00, not after it.
    [InlineData("2026-10-17T12:00:00+01:00", OpenState.Closed)] // Saturday.
    [InlineData("2026-03-29T08:30:00Z", OpenState.Closed)] // Sunday, the day the clocks go forward.
    [InlineData("2026-03-30T08:30:00Z", OpenState.Open)] // Monday 09:30 in London, 08:30 by the clock in UTC.
    [InlineData("2026-11-02T08:30:00Z", OpenState.Closed)] // Monday 08:30 in London once the clocks are back.
    public void Hours_are_read_by_the_clock_in_the_zone(string at, OpenState expected)
    {
        var weekdays = Hours("""{"1": [{"from": 540, "to": 1020}], "2": [{"from": 540, "to": 1020}], "3": [{"from": 540, "to": 1020}], "4": [{"from": 540, "to": 1020}], "5": [{"from": 540, "to": 1020}], "6": [], "7": []}""");

        Assert.Equal(expected, OpeningHoursEvaluator.Evaluate(weekdays, DateTimeOffset.Parse(at, System.Globalization.CultureInfo.InvariantCulture), London));
    }

    [Theory]
    [InlineData("""{"from": 1320, "to": 120}""")] // 22:00 to 02:00.
    [InlineData("""{"from": 1320, "to": 1560}""")] // 22:00 to 26:00.
    public void A_range_past_midnight_runs_into_the_next_day(string range)
    {
        var friday = Hours($$"""{"5": [{{range}}]}""");

        Assert.Equal(OpenState.Open, OpeningHoursEvaluator.Evaluate(friday, new DateTimeOffset(2026, 10, 16, 23, 0, 0, TimeSpan.FromHours(1)), London));
        Assert.Equal(OpenState.Open, OpeningHoursEvaluator.Evaluate(friday, new DateTimeOffset(2026, 10, 17, 1, 59, 0, TimeSpan.FromHours(1)), London));
        Assert.Equal(OpenState.Closed, OpeningHoursEvaluator.Evaluate(friday, new DateTimeOffset(2026, 10, 17, 2, 0, 0, TimeSpan.FromHours(1)), London));
        Assert.Equal(OpenState.Closed, OpeningHoursEvaluator.Evaluate(friday, new DateTimeOffset(2026, 10, 16, 1, 0, 0, TimeSpan.FromHours(1)), London));
    }

    [Fact]
    public void A_range_keeps_its_clock_times_through_the_hour_repeated_when_the_clocks_go_back()
    {
        // Sunday 25 October 2026: 01:00 to 02:00 happens twice in London, once in summer time and once in winter time.
        var sunday = Hours("""{"7": [{"from": 60, "to": 120}]}""");

        Assert.Equal(OpenState.Open, OpeningHoursEvaluator.Evaluate(sunday, DateTimeOffset.Parse("2026-10-25T00:30:00Z", System.Globalization.CultureInfo.InvariantCulture), London));
        Assert.Equal(OpenState.Open, OpeningHoursEvaluator.Evaluate(sunday, DateTimeOffset.Parse("2026-10-25T01:30:00Z", System.Globalization.CultureInfo.InvariantCulture), London));
        Assert.Equal(OpenState.Closed, OpeningHoursEvaluator.Evaluate(sunday, DateTimeOffset.Parse("2026-10-25T02:30:00Z", System.Globalization.CultureInfo.InvariantCulture), London));
    }

    [Fact]
    public void A_holiday_closes_the_day_and_holidays_that_cannot_be_read_leave_it_unknown()
    {
        const string allWeek = """{"1": [{"from": 0, "to": 1440}], "2": [{"from": 0, "to": 1440}], "3": [{"from": 0, "to": 1440}], "4": [{"from": 0, "to": 1440}], "5": [{"from": 0, "to": 1440}], "6": [{"from": 0, "to": 1440}], "7": [{"from": 0, "to": 1440}]}""";
        var christmas = new DateTimeOffset(2026, 12, 25, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal(OpenState.Closed, OpeningHoursEvaluator.Evaluate(Hours(allWeek, true, """["2026-12-25"]"""), christmas, London));
        Assert.Equal(OpenState.Closed, OpeningHoursEvaluator.Evaluate(Hours(allWeek, true, """[{"date": "2026-12-25T00:00:00Z"}]"""), christmas, London));
        Assert.Equal(OpenState.Open, OpeningHoursEvaluator.Evaluate(Hours(allWeek, true, """["2026-12-26"]"""), christmas, London));
        // Holidays switched off count for nothing, whatever they say.
        Assert.Equal(OpenState.Open, OpeningHoursEvaluator.Evaluate(Hours(allWeek, false, """[{"something": 1}]"""), christmas, London));
        Assert.Equal(OpenState.Unknown, OpeningHoursEvaluator.Evaluate(Hours(allWeek, true, """[{"month": 12, "day": 25}]"""), christmas, London));
    }

    [Fact]
    public void The_branch_taken_is_the_first_open_one_else_the_default_and_none_when_it_cannot_be_settled()
    {
        var nodes = Tree(
            """{"id": "swb_1", "type": "root", "internal_id": 1, "path": ["container"]}""",
            """{"id": "swb_2", "type": "time", "is_default_time_node": true, "path": ["container", "swb_1"]}""",
            """{"id": "swb_3", "type": "time", "day_ranges": {"1": [{"from": 540, "to": 1020}]}, "path": ["container", "swb_1"]}""",
            """{"id": "swb_4", "type": "time", "day_ranges": {"1": [{"from": 540, "to": 1020}]}, "holidays_enabled": true, "holidays": [{"month": 12}], "path": ["container", "swb_1"]}""");
        var timed = nodes.Where(n => n.Type == "time").ToList();
        var mondayMorning = new DateTimeOffset(2026, 10, 12, 10, 0, 0, TimeSpan.FromHours(1));

        Assert.Equal("swb_3", OpeningHoursEvaluator.Branch(timed, mondayMorning, London)!.Id);
        Assert.Equal("swb_2", OpeningHoursEvaluator.Branch(timed.Where(n => n.Id != "swb_4"), mondayMorning.AddHours(10), London)!.Id);
        // Out of swb_3's hours, swb_4 might be open on a holiday TalkWatch cannot read, so the branch is not known.
        Assert.Null(OpeningHoursEvaluator.Branch(timed, mondayMorning.AddHours(10), London));
        Assert.Null(OpeningHoursEvaluator.Branch(timed.Where(n => n.Id == "swb_3"), mondayMorning.AddHours(10), London));
    }

    [Fact]
    public void Health_flags_come_from_the_setup_and_say_when_the_node_is_switched_off()
    {
        var nodes = Tree(
            """{"id": "swb_1", "type": "root", "internal_id": 1, "path": ["container"], "greeting_type": "generated", "missing_audio": true, "generating_audio": false, "ext_dialing": {"enabled": true, "target_scope": "allow_list", "target_user_group_ids": []}}""",
            """{"id": "swb_2", "type": "ivr", "internal_id": 2, "key": 1, "path": ["container", "swb_1"], "greeting_type": "generated", "generating_audio": true, "missing_audio": false}""",
            """{"id": "swb_3", "type": "ivr", "internal_id": 3, "key": 2, "path": ["container", "swb_1"], "noaction_transfer_to_node_id": "swb_99"}""",
            """{"id": "grp_3_1", "type": "group", "group_id": null, "path": ["container", "swb_1", "swb_3"]}""",
            """{"id": "swb_10", "type": "root", "internal_id": 10, "disabled": true, "path": ["container"]}""",
            """{"id": "swb_11", "type": "time", "day_ranges": {}, "path": ["container", "swb_10"]}""",
            """{"id": "usr_11_a", "type": "user", "ulp_id": "a", "path": ["container", "swb_10", "swb_11"]}""");

        var flags = NodeHealth.Of(nodes).Select(f => (f.NodeId, f.Issue, f.Disabled)).ToHashSet();

        Assert.Equal(
            [
                ("swb_1", HealthIssue.MissingAudio, false),
                ("swb_1", HealthIssue.EmptyAllowList, false),
                ("swb_2", HealthIssue.GeneratingAudio, false),
                ("swb_2", HealthIssue.EmptyTarget, false),
                ("swb_3", HealthIssue.NoKeyPressTargetMissing, false),
                ("grp_3_1", HealthIssue.EmptyTarget, false),
                // Switched off: a split by hours with nowhere to go out of hours, raised for admins as a draft's problem.
                ("swb_10", HealthIssue.ClosedBranchWithNoAction, true),
            ],
            flags);
    }
}
