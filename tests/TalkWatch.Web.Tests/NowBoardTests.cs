using System.Globalization;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using TalkWatch.Replay;
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
