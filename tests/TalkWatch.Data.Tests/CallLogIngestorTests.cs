using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using TalkWatch.Core.Calls;
using TalkWatch.Core.Talk;
using TalkWatch.Replay;

namespace TalkWatch.Data.Tests;

/// <summary>One PostgreSQL container for the class; each test gets its own database in it.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    // In local CI the tests run in a container on the same Docker bridge as the database, and connect to it there
    // directly (TALKWATCH_TEST_DATABASES=container-address): its published port is reachable from inside a container only
    // by way of Docker Desktop's forwarding through Windows, where now and then a connection hung for a minute.
    private string Server() => Environment.GetEnvironmentVariable("TALKWATCH_TEST_DATABASES") == "container-address"
        ? new Npgsql.NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Host = _container.IpAddress, Port = PostgreSqlBuilder.PostgreSqlPort }.ConnectionString
        : _container.GetConnectionString();

    /// <summary>Runs a command inside the PostgreSQL container, such as pg_dump; fails the test if it fails.</summary>
    public async Task ExecAsync(params string[] command)
    {
        var result = await _container.ExecAsync(command);
        Assert.True(result.ExitCode == 0, $"{string.Join(' ', command)}: {result.Stderr}");
    }

    /// <summary>A fresh database with no schema yet; returns its connection string.</summary>
    public string EmptyDatabase() =>
        new Npgsql.NpgsqlConnectionStringBuilder(Server()) { Database = "t" + Guid.NewGuid().ToString("N"), Timeout = 60 }.ConnectionString;

    /// <summary>A fresh, migrated database; returns its connection string.</summary>
    public async Task<string> NewDatabaseAsync()
    {
        var name = "t" + Guid.NewGuid().ToString("N");
        var connection = new Npgsql.NpgsqlConnectionStringBuilder(Server()) { Database = name, Timeout = 60 }.ConnectionString;

        // Migrations, not EnsureCreated: this is also the test that the migrations build the schema the model expects.
        await using var db = Context(connection, AccessScope.Nobody);
        await db.Database.MigrateAsync();
        return connection;
    }

    public static TalkWatchDbContext Context(string connection, AccessScope scope) =>
        new(new DbContextOptionsBuilder<TalkWatchDbContext>().UseNpgsql(connection).Options, new FixedAccessScope(scope));
}

public sealed class CallLogIngestorTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(TalkWatchDbContext Db, Guid SiteId)> WithSiteAsync(string connection)
    {
        var siteId = Guid.NewGuid();
        var db = PostgresFixture.Context(connection, AccessScope.System(siteId));
        db.Sites.Add(new Site { Id = siteId, Name = "Test site", DefaultRegion = "GB", CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(Ct);
        return (db, siteId);
    }

    private static async Task<CallLogIngestor> IngestorAsync(FixtureConsole console, TalkWatchDbContext db, Guid siteId, int pageSize = 10)
    {
        var talk = new TalkClient(console.CreateClient());
        await talk.SignInAsync(FixtureConsole.Username, FixtureConsole.Password, Ct);
        return new CallLogIngestor(talk, db, siteId, new NumberNormaliser("GB"), TimeProvider.System) { PageSize = pageSize };
    }

    [Fact]
    public async Task The_first_run_copies_every_call_with_its_events_and_lines()
    {
        var (db, siteId) = await WithSiteAsync(await postgres.NewDatabaseAsync());
        var console = new FixtureConsole(FixtureConsole.DefaultDirectory);

        var result = await (await IngestorAsync(console, db, siteId)).IngestAsync(Ct);

        Assert.Equal(console.CallCount, result.CallsAdded);
        Assert.Equal(console.CallCount, await db.Calls.CountAsync(Ct));
        Assert.False(await db.Calls.AnyAsync(c => !c.Lines.Any(), Ct));
        Assert.True(await db.CallEvents.AnyAsync(Ct));
        Assert.True(await db.RawPayloads.AnyAsync(r => r.Endpoint == "call_log", Ct));

        var call = await db.Calls.Include(c => c.Lines).SingleAsync(c => c.TalkUuid == "c8d59f53-0744-4ec3-aa39-4dba46b51135", Ct);
        Assert.Equal("+441144960042", call.ToE164);
        Assert.Contains(call.Lines, l => l is { Kind: LineKind.Did, Key: "+441144960042" });
        Assert.Contains(call.Lines, l => l is { Kind: LineKind.Attendant, Key: "45" });
    }

    [Fact]
    public async Task A_call_first_seen_while_ringing_takes_its_later_events_when_it_ends()
    {
        // As on a real console: the live feed stores the call as it starts ringing, and a later poll (another
        // context, as in the app) brings the finished record with more events. The new events must be inserted.
        var connection = await postgres.NewDatabaseAsync();
        var (db, siteId) = await WithSiteAsync(connection);
        var finished = new FixtureConsole(FixtureConsole.DefaultDirectory).Calls().First(c => c.CallEvents.Count >= 4);
        var ringing = finished with { Status = "ringing", Duration = 0, CallEvents = [.. finished.CallEvents.Take(2)] };
        await new CallLogIngestor(null, db, siteId, new NumberNormaliser("GB"), TimeProvider.System).UpsertAsync([ringing], "live", "{}", Ct);

        await using var later = PostgresFixture.Context(connection, AccessScope.System(siteId));
        var (added, updated) = await new CallLogIngestor(null, later, siteId, new NumberNormaliser("GB"), TimeProvider.System)
            .UpsertAsync([finished], "call_log", "{}", Ct);

        await using var check = PostgresFixture.Context(connection, AccessScope.System(siteId));
        var call = await check.Calls.Include(c => c.Events).SingleAsync(c => c.TalkUuid == finished.Uuid, Ct);
        Assert.Equal((0, 1), (added, updated));
        Assert.Equal(finished.Status, call.Status);
        Assert.Equal(finished.CallEvents.Select(e => e.Event), call.Events.OrderBy(e => e.Sequence).Select(e => e.Event));
    }

    [Fact]
    public async Task Calls_already_stored_gain_their_voicemail_recipients_line_when_migrated()
    {
        var connection = postgres.EmptyDatabase();
        await using (var before = PostgresFixture.Context(connection, AccessScope.Nobody))
        {
            await before.Database.MigrateAsync("20260930155131_AlertSettings", Ct);
        }

        // Written in SQL for the schema as it was then: the model today has columns that schema does not.
        var (db, siteId) = await WithSiteAsync(connection);
        var callId = Guid.NewGuid();
        var recipients = """{"recipient_user_uuids": ["abe3a229-7539-48dd-a8e3-e23fd4d9f72f"]}""";
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO calls ("Id", "SiteId", "TalkUuid", "Time", "Direction", "Status", "DurationSeconds", "HasRecording", "IngestedAt", "UpdatedAt")
            VALUES ({callId}, {siteId}, 'vm-call', now(), 'in', 'accepted', 60, false, now(), now());
            INSERT INTO call_lines ("CallId", "Kind", "Key", "SiteId") VALUES ({callId}, 'Attendant', '15', {siteId});
            INSERT INTO call_events ("Id", "SiteId", "CallId", "Sequence", "Time", "Event", "DataJson")
            VALUES ({Guid.NewGuid()}, {siteId}, {callId}, 0, now(), 'vm_msg_recorded', {recipients}::jsonb);
            """, Ct);

        await db.Database.MigrateAsync(Ct);

        var lines = await db.CallLines.Where(l => l.CallId == callId).Select(l => new LineRef(l.Kind, l.Key)).ToListAsync(Ct);
        Assert.Equivalent(new[] { new LineRef(LineKind.Attendant, "15"), new LineRef(LineKind.User, "abe3a229-7539-48dd-a8e3-e23fd4d9f72f") }, lines);
    }

    // A few minutes ago, whenever the test runs: a call still ringing has to be recent, or the rule settles it as long
    // over (CallOutcomes.LongOver) while the backfill, which never had that rule, leaves it in progress.
    private static readonly string Recently = DateTimeOffset.UtcNow.AddMinutes(-5).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);

    private static string Added(string uuid, string direction, string status, params string[] events) =>
        $$"""
        {"uuid": "{{uuid}}", "time": "{{Recently}}", "direction": "{{direction}}", "status": "{{status}}", "duration": 30,
         "from": "+447700900123", "to": "+441144960042",
         "call_events": [{{string.Join(",", events.Select(e => $$"""{"time": "{{Recently}}", "event": "{{e}}"}"""))}}]}
        """;

    [Fact]
    public async Task The_outcome_backfill_in_sql_agrees_with_the_rule_for_every_call()
    {
        var connection = await postgres.NewDatabaseAsync();
        var (db, siteId) = await WithSiteAsync(connection);
        var console = new FixtureConsole(FixtureConsole.DefaultDirectory);
        // What the fixtures' call log does not have: a voicemail, a call still ringing, and a call with no events.
        console.AddCall(Added("vm", "in", "accepted", "call_started", "call_sent_to_voicemail", "vm_msg_recorded", "call_hangup"));
        console.AddCall(Added("ringing", "in", "ringing", "call_started", "seq_call_trying_endpoints"));
        console.AddCall(Added("no-events-answered", "in", "accepted"));
        console.AddCall(Added("no-events-other", "in", "normal_end"));
        await (await IngestorAsync(console, db, siteId, pageSize: 100)).IngestAsync(Ct);
        var fromCode = await db.Calls.ToDictionaryAsync(c => c.TalkUuid, c => c.Outcome, Ct);

        await db.Database.ExecuteSqlRawAsync("""UPDATE calls SET "Outcome" = 'Unknown'""", Ct);
        await db.Database.ExecuteSqlRawAsync(Migrations.CallOutcome.Backfill, Ct);
        db.ChangeTracker.Clear();
        var fromSql = await db.Calls.ToDictionaryAsync(c => c.TalkUuid, c => c.Outcome, Ct);

        Assert.Equal(fromCode, fromSql);
        Assert.Equal(CallOutcome.Voicemail, fromCode["vm"]);
        Assert.Equal(CallOutcome.InProgress, fromCode["ringing"]);
        Assert.Equal((CallOutcome.Answered, CallOutcome.Unknown), (fromCode["no-events-answered"], fromCode["no-events-other"]));
        foreach (var outcome in new[] { CallOutcome.Answered, CallOutcome.Missed, CallOutcome.HungUpAtSwitchboard, CallOutcome.Blocked, CallOutcome.Outbound })
        {
            Assert.Contains(outcome, fromCode.Values);
        }
    }

    [Fact]
    public async Task Calls_whose_hang_up_never_came_are_settled_after_a_day()
    {
        var (db, siteId) = await WithSiteAsync(await postgres.NewDatabaseAsync());
        var ingestor = new CallLogIngestor(null, db, siteId, new NumberNormaliser("GB"), TimeProvider.System);
        var now = DateTimeOffset.UtcNow;
        CallLogRecord Record(string uuid, DateTimeOffset time, params string[] events) =>
            System.Text.Json.JsonSerializer.Deserialize<CallLogRecord>(Added(uuid, "in", "accepted", events), TalkJson.Options)! with { Time = time };

        // Stored while still in progress: a recent one and two old ones, as a real console left them.
        var ringingNow = Record("now", now.AddMinutes(-5), "call_started", "seq_call_trying_endpoints");
        var oldVoicemail = Record("old-vm", now.AddDays(-2), "call_started", "seq_call_trying_endpoints", "call_sent_to_voicemail");
        var oldGreeting = Record("old-greeting", now.AddDays(-3), "call_started");
        await ingestor.UpsertAsync([ringingNow], "live", "{}", Ct);
        await db.Calls.Where(c => c.TalkUuid == "now").ExecuteUpdateAsync(u => u.SetProperty(c => c.Outcome, CallOutcome.InProgress), Ct);
        await ingestor.UpsertAsync([oldVoicemail, oldGreeting], "live", "{}", Ct);
        await db.Calls.ExecuteUpdateAsync(u => u.SetProperty(c => c.Outcome, CallOutcome.InProgress), Ct);
        db.ChangeTracker.Clear(); // as the poller's fresh context each run: nothing held from before the update

        var settled = await ingestor.SettleAsync(Ct);
        db.ChangeTracker.Clear();

        var outcomes = await db.Calls.ToDictionaryAsync(c => c.TalkUuid, c => c.Outcome, Ct);
        Assert.Equal(2, settled);
        Assert.Equal(CallOutcome.InProgress, outcomes["now"]);
        Assert.Equal(CallOutcome.Missed, outcomes["old-vm"]);
        Assert.Equal(CallOutcome.HungUpAtSwitchboard, outcomes["old-greeting"]);
    }

    // Talk can leave a call with only its start, never recording the hang-up (seen when TalkWatch restarted during the
    // call): still "ringing" a quarter of an hour on, it is over, and a re-read from Talk keeps it so rather than flipping
    // it back to live each minute.
    [Fact]
    public async Task A_call_ringing_with_nothing_new_for_a_quarter_of_an_hour_is_over_and_stays_over()
    {
        var (db, siteId) = await WithSiteAsync(await postgres.NewDatabaseAsync());
        var ingestor = new CallLogIngestor(null, db, siteId, new NumberNormaliser("GB"), TimeProvider.System);
        var now = DateTimeOffset.UtcNow;
        CallLogRecord Record(string uuid, DateTimeOffset at, params string[] events)
        {
            var record = System.Text.Json.JsonSerializer.Deserialize<CallLogRecord>(Added(uuid, "in", "accepted", events), TalkJson.Options)!;
            return record with { Time = at, CallEvents = [.. record.CallEvents.Select(e => e with { Time = at })] };
        }

        var stuck = Record("stuck", now.AddMinutes(-20), "call_started");
        var ringing = Record("ringing", now.AddMinutes(-20), "call_started", "seq_call_trying_endpoints");
        var fresh = Record("fresh", now.AddMinutes(-5), "call_started");
        await ingestor.UpsertAsync([stuck, ringing, fresh], "call_log", "{}", Ct);
        db.ChangeTracker.Clear();
        var first = await db.Calls.ToDictionaryAsync(c => c.TalkUuid, c => (c.Outcome, c.UpdatedAt), Ct);

        // Talk sends them again, unchanged, on the next poll.
        await ingestor.UpsertAsync([stuck, ringing, fresh], "call_log", "{}", Ct);
        db.ChangeTracker.Clear();
        var again = await db.Calls.ToDictionaryAsync(c => c.TalkUuid, c => (c.Outcome, c.UpdatedAt), Ct);

        Assert.Equal(CallOutcome.HungUpAtSwitchboard, first["stuck"].Outcome);
        Assert.Equal(CallOutcome.Missed, first["ringing"].Outcome);
        Assert.Equal(CallOutcome.InProgress, first["fresh"].Outcome);
        Assert.Equal(first, again);
    }

    [Fact]
    public async Task A_call_stored_ringing_and_left_a_quarter_of_an_hour_is_settled()
    {
        var (db, siteId) = await WithSiteAsync(await postgres.NewDatabaseAsync());
        var ingestor = new CallLogIngestor(null, db, siteId, new NumberNormaliser("GB"), TimeProvider.System);
        var now = DateTimeOffset.UtcNow;
        CallLogRecord Record(string uuid, DateTimeOffset at, params string[] events)
        {
            var record = System.Text.Json.JsonSerializer.Deserialize<CallLogRecord>(Added(uuid, "in", "accepted", events), TalkJson.Options)!;
            return record with { Time = at, CallEvents = [.. record.CallEvents.Select(e => e with { Time = at })] };
        }

        // Stored while live, then nothing more from Talk: one left twenty minutes, one five.
        await ingestor.UpsertAsync([Record("left", now.AddMinutes(-20), "call_started"), Record("just", now.AddMinutes(-5), "call_started")], "live", "{}", Ct);
        await db.Calls.ExecuteUpdateAsync(u => u.SetProperty(c => c.Outcome, CallOutcome.InProgress), Ct);
        db.ChangeTracker.Clear();

        var settled = await ingestor.SettleAsync(Ct);
        db.ChangeTracker.Clear();

        var outcomes = await db.Calls.ToDictionaryAsync(c => c.TalkUuid, c => c.Outcome, Ct);
        Assert.Equal(1, settled);
        Assert.Equal((CallOutcome.HungUpAtSwitchboard, CallOutcome.InProgress), (outcomes["left"], outcomes["just"]));
    }

    [Fact]
    public async Task Every_row_carries_the_site()
    {
        var (db, siteId) = await WithSiteAsync(await postgres.NewDatabaseAsync());
        await (await IngestorAsync(new FixtureConsole(FixtureConsole.DefaultDirectory), db, siteId)).IngestAsync(Ct);

        Assert.True(await db.Calls.AllAsync(c => c.SiteId == siteId, Ct));
        Assert.True(await db.CallEvents.AllAsync(e => e.SiteId == siteId, Ct));
        Assert.True(await db.CallLines.AllAsync(l => l.SiteId == siteId, Ct));
        Assert.True(await db.RawPayloads.AllAsync(r => r.SiteId == siteId, Ct));
    }

    [Fact]
    public async Task A_routine_poll_reads_one_page_and_adds_nothing()
    {
        var (db, siteId) = await WithSiteAsync(await postgres.NewDatabaseAsync());
        var console = new FixtureConsole(FixtureConsole.DefaultDirectory);
        await (await IngestorAsync(console, db, siteId)).IngestAsync(Ct);
        console.Requests.Clear();

        var again = await (await IngestorAsync(console, db, siteId)).IngestAsync(Ct);

        Assert.Equal((1, 0, 0), (again.PagesRead, again.CallsAdded, again.CallsUpdated));
        Assert.Equal(console.CallCount, await db.Calls.CountAsync(Ct));
    }

    [Fact]
    public async Task Calls_that_arrive_between_polls_are_picked_up()
    {
        var (db, siteId) = await WithSiteAsync(await postgres.NewDatabaseAsync());
        var console = new FixtureConsole(FixtureConsole.DefaultDirectory) { HideNewest = 5 };
        await (await IngestorAsync(console, db, siteId)).IngestAsync(Ct);

        console.HideNewest = 0;
        var next = await (await IngestorAsync(console, db, siteId)).IngestAsync(Ct);

        Assert.Equal(5, next.CallsAdded);
        Assert.Equal(console.CallCount, await db.Calls.CountAsync(Ct));
    }

    [Fact]
    public async Task A_page_in_an_unrecognised_shape_writes_nothing()
    {
        var (db, siteId) = await WithSiteAsync(await postgres.NewDatabaseAsync());
        var console = new FixtureConsole(FixtureConsole.DefaultDirectory) { Drifted = true };

        await Assert.ThrowsAsync<TalkSchemaException>(async () => await (await IngestorAsync(console, db, siteId)).IngestAsync(Ct));

        Assert.Equal(0, await db.Calls.CountAsync(Ct));
        Assert.Equal(0, await db.RawPayloads.CountAsync(Ct));
    }
}
