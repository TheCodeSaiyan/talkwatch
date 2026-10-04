using Microsoft.EntityFrameworkCore;
using TalkWatch.Core.Calls;
using TalkWatch.Core.Talk;
using TalkWatch.Replay;

namespace TalkWatch.Data.Tests;

public sealed class CallBackTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Call(string uuid, string at, string direction, string from, string to, params string[] events) =>
        $$"""
        {"uuid": "{{uuid}}", "time": "{{at}}", "direction": "{{direction}}", "status": "accepted", "duration": 30,
         "from": "{{from}}", "to": "{{to}}",
         "call_events": [{{string.Join(",", events.Select(e => $$"""{"time": "{{at}}", "event": "{{e}}"}"""))}}]}
        """;

    private static readonly string[] RangOut = ["call_started", "seq_call_trying_endpoints", "call_hangup"];
    private static readonly string[] Answered = ["call_started", "seq_call_trying_endpoints", "call_accepted", "call_hangup"];
    private static readonly string[] Voicemail = ["call_started", "call_sent_to_voicemail", "vm_msg_recorded", "call_hangup"];
    private static readonly string[] Outbound = ["call_started", "call_hangup"];

    [Fact]
    public async Task A_missed_caller_is_returned_by_a_later_call_back_or_a_later_answered_call_and_by_nothing_else()
    {
        var connection = await postgres.NewDatabaseAsync();
        var siteId = Guid.NewGuid();
        await using var db = PostgresFixture.Context(connection, AccessScope.System(siteId));
        db.Sites.Add(new Site { Id = siteId, Name = "Test site", DefaultRegion = "GB", CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(Ct);

        var console = new FixtureConsole(FixtureConsole.DefaultDirectory);
        // Called back half an hour later, in national format, then missed again after that.
        console.AddCall(Call("a-missed", "2026-09-30T10:00:00Z", "in", "+447700900111", "+441144960042", RangOut));
        console.AddCall(Call("a-call-back", "2026-09-30T10:30:00Z", "out", "0002", "07700 900111", Outbound));
        console.AddCall(Call("a-missed-again", "2026-09-30T10:40:00Z", "in", "+447700900111", "+441144960042", RangOut));
        // Rang again an hour later and got through.
        console.AddCall(Call("b-missed", "2026-09-30T10:00:00Z", "in", "+447700900222", "+441144960042", RangOut));
        console.AddCall(Call("b-got-through", "2026-09-30T11:00:00Z", "in", "+447700900222", "+441144960042", Answered));
        // Rung before they called: that returns nothing.
        console.AddCall(Call("c-rung-first", "2026-09-30T09:00:00Z", "out", "0002", "+447700900333", Outbound));
        console.AddCall(Call("c-missed", "2026-09-30T10:00:00Z", "in", "+447700900333", "+441144960042", RangOut));
        // A voicemail nobody has returned.
        console.AddCall(Call("d-voicemail", "2026-09-30T10:00:00Z", "in", "+447700900444", "+441144960042", Voicemail));

        var talk = new TalkClient(console.CreateClient());
        await talk.SignInAsync(FixtureConsole.Username, FixtureConsole.Password, Ct);
        await new CallLogIngestor(talk, db, siteId, new NumberNormaliser("GB"), TimeProvider.System) { PageSize = 200 }.IngestAsync(Ct);

        db.ChangeTracker.Clear();
        var calls = await db.Calls.AsNoTracking().Where(c => c.TalkUuid.Length < 20).ToDictionaryAsync(c => c.TalkUuid, Ct);
        Assert.Equal((CallBackHow.CalledBack, calls["a-call-back"].Time), (calls["a-missed"].ReturnedHow!.Value, calls["a-missed"].ReturnedAt!.Value));
        Assert.Null(calls["a-missed-again"].ReturnedAt);
        Assert.Equal((CallBackHow.GotThrough, calls["b-got-through"].Time), (calls["b-missed"].ReturnedHow!.Value, calls["b-missed"].ReturnedAt!.Value));
        Assert.Null(calls["c-missed"].ReturnedAt);
        Assert.Null(calls["d-voicemail"].ReturnedAt);
        Assert.Equal(["a-missed-again", "c-missed", "d-voicemail"],
            await db.Calls.Returnable().Where(c => c.ReturnedAt == null && c.TalkUuid.Length < 20).OrderBy(c => c.TalkUuid).Select(c => c.TalkUuid).ToListAsync(Ct));
    }
}
