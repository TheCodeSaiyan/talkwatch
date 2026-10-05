using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TalkWatch.Core.Calls;
using TalkWatch.Core.Talk;
using TalkWatch.Data;
using TalkWatch.Replay;
using TalkWatch.Web.Services;

namespace TalkWatch.Web.Tests;

/// <summary>
/// A call's steps as they happen. Talk announces each step of a call still going (a key pressed, an option entered, a
/// ring group rung) as CALL_EVENTS_UPDATED, naming only the call, and TalkWatch reads the call's events there and then.
/// Seen on a real console: one notice when the call started, two for each key and the option it chose, one at the end.
/// </summary>
public sealed class LiveStepTests(TalkWatchApp talkwatch) : IClassFixture<TalkWatchApp>
{
    private const string Call = "5e1f0c11-0000-4000-8000-00000000c0de";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The console, with this call's events as they stand now served from the route request.</summary>
    private sealed class Console(FixtureConsole replay) : DelegatingHandler(replay)
    {
        public string Events { get; set; } = "[]";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            request.RequestUri!.AbsolutePath == "/proxy/talk/api/call_log/flow/" + Call
                ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Events, Encoding.UTF8, "application/json") })
                : base.SendAsync(request, cancellationToken);
    }

    private static string Started(DateTimeOffset at) =>
        $$"""{"time":"{{at:O}}","event":"call_started","event_data":{"to":"+441174960404","from":"+447700900774","to_smart_attendant_id":15},"event_uuid":"e1"}""";

    private static string Chose(DateTimeOffset at) =>
        $$"""{"time":"{{at:O}}","event":"keypress","event_data":{"key":"2"},"event_uuid":"e2"},{"time":"{{at:O}}","event":"entered_sa_menu","event_data":{"sa_id":15,"sa_item_id":18,"sa_item_key":2,"sa_item_type":"ivr","sa_item_title":"Support"},"event_uuid":"e3"}""";

    private static LiveMessage Record(DateTimeOffset at, string status, string events) => LiveMessage.Parse(
        $$$"""{"event":"CALL_LOG_UPDATED","data":{"records":[{"uuid":"{{{Call}}}","time":"{{{at:O}}}","direction":"in","status":"{{{status}}}","from":"+447700900774","to":"+441174960404","to_smart_attendant_id":15,"call_events":[{{{events}}}]}]}}""")!;

    private static LiveMessage Notice() => LiveMessage.Parse($$$"""{"event":"CALL_EVENTS_UPDATED","data":{"call_id":"{{{Call}}}","updated_at":"2026-10-05T19:40:07Z"}}""")!;

    private async Task<(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> App, Console Console)> StartAsync()
    {
        var console = new Console(new FixtureConsole(FixtureConsole.DefaultDirectory));
        var app = talkwatch.Create(new FixtureConsole(FixtureConsole.DefaultDirectory), services: s => s.AddHttpClient(TalkSession.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => console));
        await app.Services.GetRequiredService<TalkSession>().EnsureSignedInAsync(Ct);
        return (app, console);
    }

    private static async Task<List<string>> EventsAsync(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> app)
    {
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<AccessScopeHolder>().UseSystemScope();
        return await scope.ServiceProvider.GetRequiredService<TalkWatchDbContext>().Calls.Where(c => c.TalkUuid == Call)
            .SelectMany(c => c.Events).OrderBy(e => e.Sequence).Select(e => e.Event).ToListAsync(Ct);
    }

    [Fact]
    public async Task A_key_pressed_in_the_menu_is_stored_while_the_call_is_still_going()
    {
        var (app, console) = await StartAsync();
        await using var _ = app;
        var listener = app.Services.GetRequiredService<LiveListener>();
        var started = DateTimeOffset.UtcNow.AddSeconds(-20);
        await listener.HandleAsync(Record(started, "ringing", Started(started)), Ct);
        var stored = 0;
        app.Services.GetRequiredService<IngestionStatus>().CallsStored += () => stored++;

        console.Events = $"[{Started(started)},{Chose(started.AddSeconds(12))}]";
        await listener.HandleAsync(Notice(), Ct);

        Assert.Equal(["call_started", "keypress", "entered_sa_menu"], await EventsAsync(app));
        Assert.Equal(1, stored);
    }

    // The notice can arrive a moment before the call record itself; the record brings the call when it comes.
    [Fact]
    public async Task A_notice_for_a_call_not_stored_yet_is_left_for_its_record()
    {
        var (app, console) = await StartAsync();
        await using var _ = app;
        console.Events = $"[{Started(DateTimeOffset.UtcNow)}]";

        await app.Services.GetRequiredService<LiveListener>().HandleAsync(Notice(), Ct);

        Assert.Empty(await EventsAsync(app));
    }

    [Fact]
    public async Task A_call_that_has_ended_keeps_the_events_its_record_brought()
    {
        var (app, console) = await StartAsync();
        await using var _ = app;
        var listener = app.Services.GetRequiredService<LiveListener>();
        var started = DateTimeOffset.UtcNow.AddMinutes(-2);
        var hangup = $$"""{"time":"{{started.AddSeconds(30):O}}","event":"call_hangup","event_data":{"hangup_cause":"originator_cancel"},"event_uuid":"e9"}""";
        await listener.HandleAsync(Record(started, "missed", Started(started) + "," + hangup), Ct);

        console.Events = $"[{Started(started)}]";
        await listener.HandleAsync(Notice(), Ct);

        Assert.Equal(["call_started", "call_hangup"], await EventsAsync(app));
    }
}
