using Microsoft.EntityFrameworkCore;
using TalkWatch.Core.Calls;
using TalkWatch.Core.Talk;
using TalkWatch.Replay;

namespace TalkWatch.Data.Tests;

/// <summary>
/// The grants, tested where they are enforced: in the query layer, against real PostgreSQL. Each test compares what a
/// scoped context returns with the answer worked out independently by an unscoped (system) context.
/// </summary>
public sealed class AccessScopeTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string Did = "+441144960042";
    private const string Attendant = "45";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record World(string Connection, Guid SiteId, Guid OtherSiteId, Guid Alice, Guid Bob, int CallCount);

    /// <summary>Two sites with the fixture history in each, and two people in the first site with no grants yet.</summary>
    private async Task<World> WorldAsync()
    {
        var connection = await postgres.NewDatabaseAsync();
        var siteId = Guid.NewGuid();
        var otherSiteId = Guid.NewGuid();
        var console = new FixtureConsole(FixtureConsole.DefaultDirectory);

        foreach (var id in new[] { siteId, otherSiteId })
        {
            await using var db = PostgresFixture.Context(connection, AccessScope.System(id));
            db.Sites.Add(new Site { Id = id, Name = "Site", DefaultRegion = "GB", CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(Ct);
            var talk = new TalkClient(console.CreateClient());
            await talk.SignInAsync(FixtureConsole.Username, FixtureConsole.Password, Ct);
            await new CallLogIngestor(talk, db, id, new NumberNormaliser("GB"), TimeProvider.System).IngestAsync(Ct);
        }

        await using (var db = PostgresFixture.Context(connection, AccessScope.System(siteId)))
        {
            foreach (var name in new[] { "alice", "bob" })
            {
                db.Users.Add(new AppUser { Id = Guid.NewGuid(), SiteId = siteId, UserName = name, NormalizedUserName = name.ToUpperInvariant() });
            }

            await db.SaveChangesAsync(Ct);
        }

        await using var read = PostgresFixture.Context(connection, AccessScope.System(siteId));
        var alice = await read.Users.Where(u => u.UserName == "alice").Select(u => u.Id).SingleAsync(Ct);
        var bob = await read.Users.Where(u => u.UserName == "bob").Select(u => u.Id).SingleAsync(Ct);
        return new World(connection, siteId, otherSiteId, alice, bob, console.CallCount);
    }

    private static async Task GrantAsync(World world, Guid user, LineKind kind, string key)
    {
        await using var db = PostgresFixture.Context(world.Connection, AccessScope.System(world.SiteId));
        db.Grants.Add(new Grant { Id = Guid.NewGuid(), SiteId = world.SiteId, UserId = user, Kind = kind, Key = key, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(Ct);
    }

    /// <summary>The calls that touched any of these lines, worked out without any scope.</summary>
    private static async Task<HashSet<Guid>> CallsTouchingAsync(World world, params (LineKind Kind, string Key)[] lines)
    {
        await using var db = PostgresFixture.Context(world.Connection, AccessScope.System(world.SiteId));
        var all = await db.CallLines.Select(l => new { l.CallId, l.Kind, l.Key }).ToListAsync(Ct);
        return [.. all.Where(l => lines.Contains((l.Kind, l.Key))).Select(l => l.CallId)];
    }

    private static TalkWatchDbContext As(World world, Guid user, bool admin = false) =>
        PostgresFixture.Context(world.Connection, AccessScope.ForUser(world.SiteId, user, admin));

    [Fact]
    public async Task A_context_with_no_scope_sees_nothing_at_all()
    {
        var world = await WorldAsync();
        await using var db = PostgresFixture.Context(world.Connection, AccessScope.Nobody);

        Assert.Equal(0, await db.Calls.CountAsync(Ct));
        Assert.Equal(0, await db.CallEvents.CountAsync(Ct));
        Assert.Equal(0, await db.CallLines.CountAsync(Ct));
        Assert.Equal(0, await db.RawPayloads.CountAsync(Ct));
        Assert.Equal(0, await db.Grants.CountAsync(Ct));
    }

    [Fact]
    public async Task An_admin_sees_every_call_in_their_site_and_none_in_another()
    {
        var world = await WorldAsync();
        await using var db = As(world, world.Alice, admin: true);

        Assert.Equal(world.CallCount, await db.Calls.CountAsync(Ct));
        Assert.True(await db.Calls.AllAsync(c => c.SiteId == world.SiteId, Ct));
        Assert.True(await db.RawPayloads.AnyAsync(Ct));
    }

    [Fact]
    public async Task Someone_with_no_grants_sees_no_calls()
    {
        var world = await WorldAsync();
        await using var db = As(world, world.Alice);

        Assert.Equal(0, await db.Calls.CountAsync(Ct));
        Assert.Equal(0, await db.CallEvents.CountAsync(Ct));
    }

    [Fact]
    public async Task A_grant_on_a_number_shows_exactly_the_calls_that_came_through_it()
    {
        var world = await WorldAsync();
        await GrantAsync(world, world.Alice, LineKind.Did, Did);
        var expected = await CallsTouchingAsync(world, (LineKind.Did, Did));
        await using var db = As(world, world.Alice);

        var seen = await db.Calls.Select(c => c.Id).ToListAsync(Ct);

        Assert.NotEmpty(expected);
        Assert.True(expected.Count < world.CallCount, "the test needs calls the grant does not cover");
        Assert.Equal(expected, seen.ToHashSet());
    }

    [Fact]
    public async Task Events_and_lines_follow_the_calls_they_belong_to()
    {
        var world = await WorldAsync();
        await GrantAsync(world, world.Alice, LineKind.Attendant, Attendant);
        var expected = await CallsTouchingAsync(world, (LineKind.Attendant, Attendant));
        await using var db = As(world, world.Alice);

        var eventCalls = await db.CallEvents.Select(e => e.CallId).Distinct().ToListAsync(Ct);
        var lines = await db.CallLines.ToListAsync(Ct);

        Assert.Subset(expected, eventCalls.ToHashSet());
        Assert.NotEmpty(eventCalls);
        Assert.All(lines, l => Assert.Equal((LineKind.Attendant, Attendant), (l.Kind, l.Key)));
    }

    [Fact]
    public async Task Two_grants_show_the_calls_of_either()
    {
        var world = await WorldAsync();
        await GrantAsync(world, world.Alice, LineKind.Did, Did);
        await GrantAsync(world, world.Alice, LineKind.Attendant, Attendant);
        var expected = await CallsTouchingAsync(world, (LineKind.Did, Did), (LineKind.Attendant, Attendant));
        await using var db = As(world, world.Alice);

        Assert.Equal(expected, (await db.Calls.Select(c => c.Id).ToListAsync(Ct)).ToHashSet());
    }

    [Fact]
    public async Task One_persons_grants_give_another_nothing()
    {
        var world = await WorldAsync();
        await GrantAsync(world, world.Alice, LineKind.Did, Did);
        await using var db = As(world, world.Bob);

        Assert.Equal(0, await db.Calls.CountAsync(Ct));
        Assert.Equal(0, await db.Grants.CountAsync(Ct));
        Assert.Equal(0, await db.RawPayloads.CountAsync(Ct));
    }

    [Fact]
    public async Task Loading_a_call_with_its_lines_shows_only_the_granted_lines()
    {
        var world = await WorldAsync();
        await GrantAsync(world, world.Alice, LineKind.Did, Did);
        await using var db = As(world, world.Alice);

        var calls = await db.Calls.Include(c => c.Lines).ToListAsync(Ct);

        Assert.All(calls, c => Assert.Equal([(LineKind.Did, Did)], c.Lines.Select(l => (l.Kind, l.Key))));
    }
}
