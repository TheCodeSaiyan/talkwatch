using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using TalkWatch.Replay;
using TalkWatch.Web.Services;

namespace TalkWatch.Web.Tests;

/// <summary>Operator draws each switchboard as the flow its callers move through, with each caller where they are now.</summary>
public sealed partial class OperatorSwitchboardTests(TalkWatchApp talkwatch) : IClassFixture<TalkWatchApp>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // A caller still in switchboard 15 of the replayed console, who has pressed 2 and is listening to what that option plays.
    private static string InTheMenu(string uuid, DateTimeOffset at)
    {
        string T(int s) => at.AddSeconds(s).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        return $$"""
            {"time":"{{T(0)}}","from":"+447700900318","to":"+441174960404","status":"ringing","duration":null,"direction":"in","uuid":"{{uuid}}","country":"GB",
             "to_smart_attendant_id":15,"call_events":[
               {"time":"{{T(0)}}","event":"call_started","event_data":{"to":"+441174960404","from":"+447700900318","to_smart_attendant_id":15},"event_uuid":"{{Guid.NewGuid()}}"},
               {"time":"{{T(9)}}","event":"keypress","event_data":{"key":"2"},"event_uuid":"{{Guid.NewGuid()}}"},
               {"time":"{{T(9)}}","event":"entered_sa_menu","event_data":{"sa_id":15,"sa_item_id":18,"sa_item_key":2,"sa_item_type":"ivr","sa_item_title":"Title 16"},"event_uuid":"{{Guid.NewGuid()}}"}]}
            """;
    }

    // A stage's list of callers: from its opening tag to its closing </div> (the rows inside are links and spans).
    [GeneratedRegex("""data-stage-list="([^"]+)"[^>]*>(.*?)</div>""", RegexOptions.Singleline)]
    private static partial Regex StageList();

    private async Task<(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> App, HttpClient Browser, string Uuid)> StartAsync()
    {
        var console = new FixtureConsole(FixtureConsole.DefaultDirectory);
        var uuid = Guid.NewGuid().ToString();
        console.AddCall(InTheMenu(uuid, DateTimeOffset.UtcNow.AddSeconds(-20)));
        var app = talkwatch.Create(console);
        await app.Services.GetRequiredService<LineDirectorySync>().RefreshAsync(Ct);
        await app.Services.GetRequiredService<CallLogPoller>().RunOnceAsync(Ct);
        var browser = TalkWatchApp.Browser(app);
        await TalkWatchApp.SignInAsync(browser, TalkWatchApp.AdminUsername, TalkWatchApp.AdminPassword);
        return (app, browser, uuid);
    }

    [Fact]
    public async Task A_caller_in_the_menu_is_drawn_at_the_option_they_chose()
    {
        var (app, browser, uuid) = await StartAsync();
        await using var _ = app;
        using var __ = browser;

        var page = WebUtility.HtmlDecode(await browser.GetStringAsync(new Uri("/operator", UriKind.Relative), Ct));

        Assert.Contains("data-switchboard=\"15\"", page, StringComparison.Ordinal);
        Assert.Contains("2 · Title 16", page, StringComparison.Ordinal);
        var at = StageList().Matches(page).Single(m => m.Groups[2].Value.Contains($"data-key=\"{uuid}\"", StringComparison.Ordinal));
        Assert.Equal("n:swb_18", at.Groups[1].Value);
        Assert.Contains("Caller pressed 2 for Title 16", at.Groups[2].Value, StringComparison.Ordinal);
        Assert.Contains("data-since=", at.Groups[2].Value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_another_number_chosen_its_switchboard_is_not_drawn()
    {
        var (app, browser, _) = await StartAsync();
        await using var __ = app;
        using var ___ = browser;
        var html = await browser.GetStringAsync(new Uri("/operator", UriKind.Relative), Ct);
        var token = WebUtility.HtmlDecode(Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
        using var form = new FormUrlEncodedContent([new("Did", "+441144960042"), new("ReturnUrl", "/operator"), new("__RequestVerificationToken", token)]);
        await browser.PostAsync(new Uri("/account/number", UriKind.Relative), form, Ct);

        var page = await browser.GetStringAsync(new Uri("/operator", UriKind.Relative), Ct);

        Assert.DoesNotContain("data-switchboard=\"15\"", page, StringComparison.Ordinal);
        Assert.Contains("data-switchboard=\"45\"", page, StringComparison.Ordinal);
    }
}
