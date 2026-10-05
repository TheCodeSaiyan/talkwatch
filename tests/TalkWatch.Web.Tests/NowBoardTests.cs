using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TalkWatch.Data;
using TalkWatch.Replay;
using TalkWatch.Web.Components.Calls;
using TalkWatch.Web.Services;

namespace TalkWatch.Web.Tests;

/// <summary>The Now board: a call Talk reports as still ringing is on it, saying so, with a duration that keeps time.</summary>
public sealed class NowBoardTests(TalkWatchApp talkwatch) : IClassFixture<TalkWatchApp>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Ringing(string uuid, DateTimeOffset at)
    {
        var started = at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        var ringing = at.AddSeconds(8).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        return $$"""
            {"time":"{{started}}","from":"+447700900318","to":"+441144960042","answered_by":null,"status":"ringing","duration":null,
             "direction":"in","uuid":"{{uuid}}","country":"GB","recording":false,"vm_data":{},"to_smart_attendant_id":45,
             "call_events":[
               {"time":"{{started}}","event":"call_started","event_data":{"to":"+441144960042","from":"+447700900318","to_smart_attendant_id":45},"event_uuid":"{{Guid.NewGuid()}}"},
               {"time":"{{ringing}}","event":"seq_call_trying_endpoints","event_data":{"contact_uuids":["ac7a4901-2722-4e41-9f20-87f95df72cb2"]},"event_uuid":"{{Guid.NewGuid()}}"}]}
            """;
    }

    // Someone in the office dialling out, not hung up yet: Talk has the start and nothing more.
    private static string Dialling(string uuid, DateTimeOffset at, bool answered)
    {
        var started = at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        var accepted = answered ? $$$""",{"time":"{{{at.AddSeconds(6).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)}}}","event":"call_accepted","event_uuid":"{{{Guid.NewGuid()}}}"}""" : "";
        return $$$"""
            {"time":"{{{started}}}","from":"0002","to":"+447700900555","status":"ringing","duration":null,"direction":"out","uuid":"{{{uuid}}}","country":"GB",
             "call_events":[{"time":"{{{started}}}","event":"call_started","event_uuid":"{{{Guid.NewGuid()}}}"}{{{accepted}}}]}
            """;
    }

    [Theory]
    [InlineData(false, "calling", "Calling")]
    [InlineData(true, "connected", "Connected")]
    public async Task A_call_being_made_is_on_the_board_until_it_hangs_up(bool answered, string state, string words)
    {
        var console = new FixtureConsole(FixtureConsole.DefaultDirectory);
        var uuid = Guid.NewGuid().ToString();
        console.AddCall(Dialling(uuid, DateTimeOffset.UtcNow.AddSeconds(-20), answered));
        await using var app = talkwatch.Create(console);
        await app.Services.GetRequiredService<CallLogPoller>().RunOnceAsync(Ct);
        using var browser = TalkWatchApp.Browser(app);
        await TalkWatchApp.SignInAsync(browser, TalkWatchApp.AdminUsername, TalkWatchApp.AdminPassword);

        var page = WebUtility.HtmlDecode(await browser.GetStringAsync(new Uri("/live", UriKind.Relative), Ct));

        var at = page.IndexOf($"data-live-call=\"{uuid}\"", StringComparison.Ordinal);
        Assert.True(at > 0, "the call being made is not on the board");
        var start = page.LastIndexOf("<div class=\"lc", at, StringComparison.Ordinal);
        var next = page.IndexOf("data-live-call=", at + 20, StringComparison.Ordinal);
        var row = page[start..(next > 0 ? next : Math.Min(page.Length, at + 4000))];
        Assert.DoesNotContain(" ended", row[..row.IndexOf('>', StringComparison.Ordinal)], StringComparison.Ordinal);
        Assert.Contains($"data-s=\"{state}\"", row, StringComparison.Ordinal);
        Assert.Contains($">{words}<", row.Replace("<i></i>", "", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_call_still_ringing_is_on_the_board_as_ringing_with_a_running_duration()
    {
        var console = new FixtureConsole(FixtureConsole.DefaultDirectory);
        var uuid = Guid.NewGuid().ToString();
        console.AddCall(Ringing(uuid, DateTimeOffset.UtcNow.AddSeconds(-20)));
        await using var app = talkwatch.Create(console);
        await app.Services.GetRequiredService<CallLogPoller>().RunOnceAsync(Ct);
        using var browser = TalkWatchApp.Browser(app);
        await TalkWatchApp.SignInAsync(browser, TalkWatchApp.AdminUsername, TalkWatchApp.AdminPassword);

        var page = WebUtility.HtmlDecode(await browser.GetStringAsync(new Uri("/live", UriKind.Relative), Ct));

        // The row's own markup: from its opening tag to the next call row, or the end of the list.
        var at = page.IndexOf($"data-live-call=\"{uuid}\"", StringComparison.Ordinal);
        Assert.True(at > 0, "the ringing call is not on the board");
        var start = page.LastIndexOf("<div class=\"lc", at, StringComparison.Ordinal);
        var next = page.IndexOf("data-live-call=", at + 20, StringComparison.Ordinal);
        var row = page[start..(next > 0 ? next : Math.Min(page.Length, at + 4000))];

        Assert.Contains("data-s=\"ringing\"", row, StringComparison.Ordinal);
        Assert.Contains(">Ringing<", row.Replace("<i></i>", "", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("data-since=", row, StringComparison.Ordinal);
    }

    [Fact]
    public async Task On_the_operator_screen_a_ringing_call_is_under_needs_someone_not_in_progress()
    {
        var console = new FixtureConsole(FixtureConsole.DefaultDirectory);
        var uuid = Guid.NewGuid().ToString();
        console.AddCall(Ringing(uuid, DateTimeOffset.UtcNow.AddSeconds(-20)));
        await using var app = talkwatch.Create(console);
        await app.Services.GetRequiredService<CallLogPoller>().RunOnceAsync(Ct);
        using var browser = TalkWatchApp.Browser(app);
        await TalkWatchApp.SignInAsync(browser, TalkWatchApp.AdminUsername, TalkWatchApp.AdminPassword);

        var page = await browser.GetStringAsync(new Uri("/operator", UriKind.Relative), Ct);

        var waiting = page[page.IndexOf("data-op-waiting", StringComparison.Ordinal)..page.IndexOf("data-op-talking", StringComparison.Ordinal)];
        Assert.Contains($"data-live-call=\"{uuid}\"", waiting, StringComparison.Ordinal);
        Assert.Equal(1, page.Split($"data-live-call=\"{uuid}\"").Length - 1);
    }

    // Rang the group and nobody answered.
    private static string Missed(string uuid, DateTimeOffset at)
    {
        var time = at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        return $$"""
            {"uuid":"{{uuid}}","time":"{{time}}","direction":"in","status":"accepted","duration":30,"from":"+447700900318","to":"+441144960042",
             "call_events":[
               {"time":"{{time}}","event":"call_started"},
               {"time":"{{time}}","event":"seq_call_trying_endpoints"},
               {"time":"{{time}}","event":"call_hangup"}]}
            """;
    }

    // One node of the flow board: from its section to the next, or the end of the board.
    private static string Node(string page, string node)
    {
        var start = page.IndexOf($"class=\"plane node {node}\"", StringComparison.Ordinal);
        Assert.True(start > 0, $"the board has no {node}");
        var end = page.IndexOf("<section", start + 10, StringComparison.Ordinal);
        return page[start..(end > 0 ? end : page.Length)];
    }

    [Fact]
    public async Task A_call_that_has_just_ended_stays_on_the_board_resolved_then_is_in_recent_activity_and_the_call_log()
    {
        var console = new FixtureConsole(FixtureConsole.DefaultDirectory);
        var uuid = Guid.NewGuid().ToString();
        console.AddCall(Missed(uuid, DateTimeOffset.UtcNow.AddSeconds(-40)));
        await using var app = talkwatch.Create(console);
        await app.Services.GetRequiredService<CallLogPoller>().RunOnceAsync(Ct);
        using var browser = TalkWatchApp.Browser(app);
        await TalkWatchApp.SignInAsync(browser, TalkWatchApp.AdminUsername, TalkWatchApp.AdminPassword);

        // Just ended: still on Live calls, resolved, saying which stream it leaves by and carrying its mark for the rail.
        var page = WebUtility.HtmlDecode(await browser.GetStringAsync(new Uri("/live", UriKind.Relative), Ct));
        var live = Node(page, "node-live");
        var at = live.IndexOf($"data-live-call=\"{uuid}\"", StringComparison.Ordinal);
        Assert.True(at > 0, "the call that has just ended is not on the board");
        var row = live[live.LastIndexOf("<div class=\"lc", at, StringComparison.Ordinal)..live.IndexOf('>', at)];
        Assert.Contains(" ended", row, StringComparison.Ordinal);
        Assert.Contains("data-stream=\"missed\"", row, StringComparison.Ordinal);
        Assert.Contains("data-signal=\"missed\"", row, StringComparison.Ordinal);
        Assert.DoesNotContain(uuid, Node(page, "node-recent"), StringComparison.Ordinal);
        Assert.DoesNotContain(uuid, Node(page, "node-log"), StringComparison.Ordinal);

        // Its moment on the board is up: it has ridden into Recent activity, and on into the call log.
        using (var scope = app.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<AccessScopeHolder>().UseSystemScope();
            Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<TalkWatchDbContext>().Calls.Where(c => c.TalkUuid == uuid)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.UpdatedAt, DateTimeOffset.UtcNow - LiveCalls.Linger - TimeSpan.FromSeconds(1)), Ct));
        }

        page = WebUtility.HtmlDecode(await browser.GetStringAsync(new Uri("/live", UriKind.Relative), Ct));
        Assert.DoesNotContain(uuid, Node(page, "node-live"), StringComparison.Ordinal);
        Assert.Contains($"data-key=\"{uuid}\" data-stream=\"missed\"", Node(page, "node-recent"), StringComparison.Ordinal);
        Assert.Contains($"data-key=\"{uuid}\"", Node(page, "node-log"), StringComparison.Ordinal);
    }

    // Put through to the outside phone, which picked up; Talk scored the audio poorly.
    private static string AnsweredPoorly(string uuid, DateTimeOffset at)
    {
        var time = at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        return $$$"""
            {"uuid":"{{{uuid}}}","time":"{{{time}}}","direction":"in","status":"accepted","duration":90,"from":"+447700900318","to":"+441144960042","quality_score":42,
             "call_events":[
               {"time":"{{{time}}}","event":"call_started"},
               {"time":"{{{time}}}","event":"seq_call_trying_endpoints","event_data":{"contact_uuids":["ac7a4901-2722-4e41-9f20-87f95df72cb2"]}},
               {"time":"{{{time}}}","event":"call_accepted","event_data":{"accepted_by_contact_uuid":"ac7a4901-2722-4e41-9f20-87f95df72cb2","accepted_by":"+447700900210"}},
               {"time":"{{{time}}}","event":"call_hangup"}]}
            """;
    }

    [Fact]
    public async Task The_call_log_says_who_answered_the_calls_quality_its_rating_and_the_alerts_it_raised()
    {
        var console = new FixtureConsole(FixtureConsole.DefaultDirectory);
        var uuid = Guid.NewGuid().ToString();
        console.AddCall(AnsweredPoorly(uuid, DateTimeOffset.UtcNow.AddMinutes(-10)));
        await using var app = talkwatch.Create(console);
        await app.Services.GetRequiredService<CallLogPoller>().RunOnceAsync(Ct);
        int raised;
        using (var scope = app.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<AccessScopeHolder>().UseSystemScope();
            var db = scope.ServiceProvider.GetRequiredService<TalkWatchDbContext>();
            var call = await db.Calls.SingleAsync(c => c.TalkUuid == uuid, Ct);
            call.UpdatedAt = DateTimeOffset.UtcNow - LiveCalls.Linger - TimeSpan.FromSeconds(1);
            db.CallTranscripts.Add(new CallTranscript { SiteId = call.SiteId, CallId = call.Id, TalkId = "t-" + uuid, SentimentClass = "negative", Lines = "[]", Text = "" });
            await db.SaveChangesAsync(Ct);
            // A poor call raises alerts of its own as it is read in.
            raised = await db.AlertEvents.CountAsync(e => e.CallId == call.Id, Ct);
        }

        using var browser = TalkWatchApp.Browser(app);
        await TalkWatchApp.SignInAsync(browser, TalkWatchApp.AdminUsername, TalkWatchApp.AdminPassword);
        var log = Node(WebUtility.HtmlDecode(await browser.GetStringAsync(new Uri("/live", UriKind.Relative), Ct)), "node-log");

        var at = log.IndexOf($"data-key=\"{uuid}\"", StringComparison.Ordinal);
        Assert.True(at > 0, "the call is not in the call log");
        var row = log[at..log.IndexOf("</a>", at, StringComparison.Ordinal)];
        Assert.Matches("<span class=\"by ext\">[^<]+</span>", row);
        Assert.Contains("data-poor=\"true\"", row, StringComparison.Ordinal);
        Assert.Contains(">42</span>", row, StringComparison.Ordinal);
        Assert.Contains("data-r=\"negative\"", row, StringComparison.Ordinal);
        Assert.True(raised > 0, "the poor call raised no alert");
        Assert.Contains($"title=\"{raised} alert{(raised == 1 ? "" : "s")} raised\"", row, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_board_joins_its_nodes_at_ports_and_its_streams_are_as_loud_as_the_last_five_minutes()
    {
        var console = new FixtureConsole(FixtureConsole.DefaultDirectory);
        console.AddCall(Missed(Guid.NewGuid().ToString(), DateTimeOffset.UtcNow.AddSeconds(-40)));
        await using var app = talkwatch.Create(console);
        await app.Services.GetRequiredService<CallLogPoller>().RunOnceAsync(Ct);
        using var browser = TalkWatchApp.Browser(app);
        await TalkWatchApp.SignInAsync(browser, TalkWatchApp.AdminUsername, TalkWatchApp.AdminPassword);

        var raw = await browser.GetStringAsync(new Uri("/live", UriKind.Relative), Ct);
        var page = WebUtility.HtmlDecode(raw);

        foreach (var port in new[] { "inlet", "answered", "voicemail", "missed", "in", "archive", "log" })
        {
            Assert.Contains($"data-port=\"{port}\"", page, StringComparison.Ordinal);
        }

        // One call came in and was missed in the last five minutes: a fifth of a call a minute on the inlet and the missed
        // stream, and nothing answered. The captured calls were all stored just now too, but ended days ago.
        var rates = JsonDocument.Parse(WebUtility.HtmlDecode(Regex.Match(raw, "data-rates=\"([^\"]*)\"").Groups[1].Value)).RootElement;
        Assert.Equal(0.2, rates.GetProperty("inlet").GetDouble());
        Assert.Equal(0.2, rates.GetProperty("missed").GetDouble());
        Assert.Equal(0, rates.GetProperty("answered").GetDouble());

        // The rail's count rolls from the figure it carries, which stays in the page for screen readers.
        Assert.Matches("class=\"num roll\" data-roll=\"missed\"><span class=\"rv\">1</span>", page);
    }

    // Choosing a call opens it in the Context Inspector, its name growing from the row; the link still opens its page.
    [Fact]
    public async Task Calls_on_now_and_in_the_call_log_open_in_the_inspector_with_a_name_to_grow_from()
    {
        var console = new FixtureConsole(FixtureConsole.DefaultDirectory);
        var uuid = Guid.NewGuid().ToString();
        var ended = Guid.NewGuid().ToString();
        console.AddCall(Ringing(uuid, DateTimeOffset.UtcNow.AddSeconds(-20)));
        console.AddCall(Missed(ended, DateTimeOffset.UtcNow.AddSeconds(-40)));
        await using var app = talkwatch.Create(console);
        await app.Services.GetRequiredService<CallLogPoller>().RunOnceAsync(Ct);
        using (var scope = app.Services.CreateScope())
        {
            // Past its moment on the board, so it is in Recent activity.
            scope.ServiceProvider.GetRequiredService<AccessScopeHolder>().UseSystemScope();
            await scope.ServiceProvider.GetRequiredService<TalkWatchDbContext>().Calls.Where(c => c.TalkUuid == ended)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.UpdatedAt, DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1)), Ct);
        }

        using var browser = TalkWatchApp.Browser(app);
        await TalkWatchApp.SignInAsync(browser, TalkWatchApp.AdminUsername, TalkWatchApp.AdminPassword);

        var now = await browser.GetStringAsync(new Uri("/live", UriKind.Relative), Ct);
        var log = await browser.GetStringAsync(new Uri("/calls", UriKind.Relative), Ct);

        Assert.Matches($"<a class=\"who\" href=\"/calls/{uuid}\" data-inspect=\"{uuid}\"[^>]*><b data-grow>", now);
        Assert.Matches("<a class=\"ev\" href=\"/calls/([^\"]+)\" data-inspect=\"\\1\"[\\s\\S]*?<span data-grow>", now);
        Assert.Matches("<a class=\"lg\" role=\"listitem\" href=\"/calls/([^\"]+)\" data-inspect=\"\\1\"[\\s\\S]*?<b data-grow>", now);
        Assert.Matches("href=\"/calls/([^\"]+)\" class=\"row\" data-inspect=\"\\1\"[\\s\\S]*?<b data-grow>", log);
        // The workspace has room beside it for the inspector, in every page's layout.
        Assert.Matches("<div class=\"shell-work\"[^>]*>\\s*<main", log);
    }

    // The rail's line is drawn by script on a canvas whose drawing size the script sets. Changing page merges the new
    // page into this one, which would strip that size and leave the line stretched out of sight; permanent, it is left be.
    [Fact]
    public async Task The_rail_line_is_kept_as_it_is_when_the_page_changes()
    {
        await using var app = talkwatch.Create(new FixtureConsole(FixtureConsole.DefaultDirectory));
        using var browser = TalkWatchApp.Browser(app);
        await TalkWatchApp.SignInAsync(browser, TalkWatchApp.AdminUsername, TalkWatchApp.AdminPassword);

        var page = await browser.GetStringAsync(new Uri("/calls", UriKind.Relative), Ct);

        // Its box is permanent, not the canvas itself: a permanent element's own attributes are still merged.
        Assert.Matches("<div id=\"rail-current\"[^>]*data-permanent[^>]*>\\s*<canvas[^>]*data-rail-current", page);
    }
}
