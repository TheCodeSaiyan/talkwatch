using Microsoft.EntityFrameworkCore;
using TalkWatch.Core.Talk;

namespace TalkWatch.Data.Tests;

/// <summary>A switchboard node's setup kept over time: a snapshot when it changes, kept as long as a kept call could have met it.</summary>
public sealed class SwitchboardSnapshotTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Now = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static SwitchboardNode Node(string json) =>
        Switchboard.Parse("{\"entities\": {\"nodes\": {\"byId\": {\"swb_1\": " + json + "}}}}").Single();

    [Fact]
    public void A_node_is_worth_a_snapshot_only_when_its_setup_changes()
    {
        var row = new SwitchboardNodeRow { NodeId = "swb_1" };
        var before = Node("""{"id": "swb_1", "type": "time", "ext": "0005", "disabled": false, "missing_audio": true, "is_default_time_node": true, "path": ["container"]}""");

        Assert.True(row.TakeSetup(before));
        Assert.Equal((false, "0005", true, true), (row.Disabled, row.Ext, row.MissingAudio, row.IsDefaultTime));
        Assert.Equal("0005", row.Setup.Ext);
        Assert.False(row.TakeSetup(before));

        Assert.True(row.TakeSetup(Node("""{"id": "swb_1", "type": "time", "ext": "0005", "disabled": true, "path": ["container"]}""")));
        // What this Talk no longer says is no longer known, rather than kept from before; a time node not marked default is not.
        Assert.Equal((true, (bool?)null, (bool?)false), (row.Disabled, row.MissingAudio, row.IsDefaultTime));
    }

    [Fact]
    public async Task A_sweep_keeps_every_setup_a_kept_call_could_have_met()
    {
        var connection = await postgres.NewDatabaseAsync();
        var siteId = Guid.NewGuid();
        await using var db = PostgresFixture.Context(connection, AccessScope.System(siteId));
        db.Sites.Add(new Site { Id = siteId, Name = "Test site", DefaultRegion = "GB", CreatedAt = Now });
        db.RetentionSettings.Add(new RetentionSettings { SiteId = siteId, Preset = RetentionPreset.Custom, CallDays = 200, UpdatedAt = Now });
        SwitchboardNodeSnapshot At(string node, int daysAgo) =>
            new() { SiteId = siteId, NodeId = node, CapturedAt = Now.AddDays(-daysAgo), JsonHash = $"{node}-{daysAgo}", Json = "{}" };
        db.SwitchboardNodeSnapshots.AddRange(At("swb_1", 400), At("swb_1", 300), At("swb_1", 100), At("swb_2", 400));
        await db.SaveChangesAsync(Ct);

        await new RetentionSweeper(db, new AudioStore(Directory.CreateTempSubdirectory("talkwatch-snapshots-").FullName), new FixedClock(Now)).RunAsync(Ct);

        // Calls are kept from 200 days back. swb_1's setup of 400 days back was replaced at 300, before any kept call;
        // the one from 300 was still in force at 200, so calls since met it. swb_2 never changed.
        Assert.Equal(
            ["swb_1-100", "swb_1-300", "swb_2-400"],
            (await db.SwitchboardNodeSnapshots.Select(s => s.JsonHash).ToListAsync(Ct)).Order());
    }
}
