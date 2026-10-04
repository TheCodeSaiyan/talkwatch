using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TalkWatch.Core.Calls;
using TalkWatch.Core.Talk;
using TalkWatch.Data;
using TalkWatch.Replay;
using TalkWatch.Web.Services;

namespace TalkWatch.Web.Tests;

public sealed class LineDirectoryTests(TalkWatchApp talkwatch) : IClassFixture<TalkWatchApp>
{
    private const string GroupList = "/proxy/talk/api/group_list";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task PollAsync(WebApplicationFactory<Program> app) =>
        await app.Services.GetRequiredService<CallLogPoller>().RunOnceAsync(Ct);

    private static async Task<List<LineRecord>> LinesAsync(WebApplicationFactory<Program> app, Func<IServiceProvider, AccessScope>? scope = null)
    {
        using var services = app.Services.CreateScope();
        var holder = services.ServiceProvider.GetRequiredService<AccessScopeHolder>();
        if (scope is null)
        {
            holder.UseSystemScope();
        }

        var db = scope is null
            ? services.ServiceProvider.GetRequiredService<TalkWatchDbContext>()
            : new TalkWatchDbContext(
                new DbContextOptionsBuilder<TalkWatchDbContext>().UseNpgsql(services.ServiceProvider.GetRequiredService<TalkWatchDbContext>().Database.GetConnectionString()).Options,
                new FixedAccessScope(scope(services.ServiceProvider)));
        return await db.Lines.ToListAsync(Ct);
    }

    [Fact]
    public async Task The_first_poll_names_every_user_group_number_and_attendant()
    {
        await using var app = talkwatch.Create(new FixtureConsole(FixtureConsole.DefaultDirectory));

        await PollAsync(app);
        var lines = await LinesAsync(app);
        var directory = app.Services.GetRequiredService<LineDirectorySync>().Current;

        Assert.Equal(directory.Users.Count(u => !u.HideFromUserList), lines.Count(l => l.Kind == LineKind.User));
        Assert.Single(lines, l => l.Kind == LineKind.RingGroup);
        Assert.Contains(lines, l => l.Kind == LineKind.Did && l.Key == "+441144960042");
        Assert.Contains(lines, l => l.Kind == LineKind.Attendant && l.Key == "45");
        Assert.All(lines, l => Assert.True(l.Present));
    }

    [Fact]
    public async Task A_viewer_sees_only_the_names_of_lines_they_are_granted()
    {
        await using var app = talkwatch.Create(new FixtureConsole(FixtureConsole.DefaultDirectory));
        await PollAsync(app);
        Guid viewerId;
        Guid siteId;
        using (var scope = app.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<AccessScopeHolder>().UseSystemScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var db = scope.ServiceProvider.GetRequiredService<TalkWatchDbContext>();
            siteId = scope.ServiceProvider.GetRequiredService<CurrentSite>().Id;
            var viewer = new AppUser { Id = Guid.NewGuid(), UserName = "namer", SiteId = siteId };
            Assert.True((await users.CreateAsync(viewer, "a long enough password")).Succeeded);
            viewerId = viewer.Id;
            db.Grants.Add(new Grant { Id = Guid.NewGuid(), SiteId = siteId, UserId = viewerId, Kind = LineKind.Attendant, Key = "45", CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(Ct);
        }

        var seen = await LinesAsync(app, _ => AccessScope.ForUser(siteId, viewerId, isAdmin: false));

        var line = Assert.Single(seen);
        Assert.Equal((LineKind.Attendant, "45"), (line.Kind, line.Key));
    }

    [Fact]
    public async Task A_group_that_disappears_is_kept_and_marked_absent()
    {
        var console = new FixtureConsole(FixtureConsole.DefaultDirectory);
        await using var app = talkwatch.Create(console);
        await PollAsync(app);

        console.Overrides[GroupList] = "[]";
        await app.Services.GetRequiredService<LineDirectorySync>().RefreshAsync(Ct);

        var group = Assert.Single(await LinesAsync(app), l => l.Kind == LineKind.RingGroup);
        Assert.False(group.Present);
    }

    [Fact]
    public async Task A_live_call_on_a_did_routed_to_a_group_gets_the_groups_line()
    {
        var console = new FixtureConsole(FixtureConsole.DefaultDirectory);
        console.Overrides[GroupList] = """[{"id":7,"uuid":"g-7","name":"Sales","group_type":"ring_group","member_list":[],"ext_list":["0099"],"did_list":["+441144960777"]}]""";
        await using var app = talkwatch.Create(console);
        await PollAsync(app);

        var call = LiveMessage.Parse("""
            {"event":"CALL_LOG_UPDATED","data":{"records":[{"uuid":"group-call-1","time":"2026-10-01T09:00:00Z","direction":"in","status":"accepted",
             "from":"+447700900111","to":"+441144960777","duration":12,"call_events":[]}]}}
            """)!;
        await app.Services.GetRequiredService<LiveListener>().HandleAsync(call, Ct);

        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<AccessScopeHolder>().UseSystemScope();
        var stored = await scope.ServiceProvider.GetRequiredService<TalkWatchDbContext>().Calls.Include(c => c.Lines).SingleAsync(c => c.TalkUuid == "group-call-1", Ct);
        Assert.Contains(stored.Lines, l => l.Kind == LineKind.RingGroup && l.Key == "7");
    }
}
