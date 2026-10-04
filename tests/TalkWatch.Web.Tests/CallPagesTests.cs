using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TalkWatch.Core.Calls;
using TalkWatch.Data;
using TalkWatch.Replay;
using TalkWatch.Web.Services;

namespace TalkWatch.Web.Tests;

/// <summary>The call log's filters and the Call Chronicle, read the way a person would see them.</summary>
public sealed partial class CallPagesTests(TalkWatchApp talkwatch) : IClassFixture<TalkWatchApp>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(WebApplicationFactory<Program> App, HttpClient Browser)> SignedInAsync(TalkWatchApp talkwatch)
    {
        var app = talkwatch.Create(new FixtureConsole(FixtureConsole.DefaultDirectory));
        await app.Services.GetRequiredService<CallLogPoller>().RunOnceAsync(Ct);
        var browser = TalkWatchApp.Browser(app);
        await TalkWatchApp.SignInAsync(browser, TalkWatchApp.AdminUsername, TalkWatchApp.AdminPassword);
        return (app, browser);
    }

    private static async Task<List<CallRow>> CallsAsync(WebApplicationFactory<Program> app)
    {
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<AccessScopeHolder>().UseSystemScope();
        return await scope.ServiceProvider.GetRequiredService<TalkWatchDbContext>().Calls.AsNoTracking().Include(c => c.Events).ToListAsync(Ct);
    }

    [Fact]
    public async Task Filtering_by_result_shows_only_those_calls_and_says_so_with_a_chip_that_takes_it_off()
    {
        var (app, browser) = await SignedInAsync(talkwatch);
        await using var _ = app;
        using var __ = browser;
        var answered = (await CallsAsync(app)).Count(c => c.Outcome == CallOutcome.Answered);

        var page = await browser.GetStringAsync(new Uri("/calls?outcome=answered", UriKind.Relative), Ct);

        var outcomes = Outcome().Matches(page).Select(m => m.Groups[1].Value).ToList();
        Assert.NotEmpty(outcomes);
        Assert.All(outcomes, o => Assert.Equal(nameof(CallOutcome.Answered), o));
        Assert.Contains($"of {answered}", page, StringComparison.Ordinal);
        Assert.Contains("data-chip=\"outcome\"", page, StringComparison.Ordinal);
        Assert.Contains("href=\"/calls\" aria-label=\"Remove the result filter\"", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Filters_that_match_nothing_say_which_filters_and_offer_to_clear_each()
    {
        var (app, browser) = await SignedInAsync(talkwatch);
        await using var _ = app;
        using var __ = browser;

        // Missed is an inbound outcome, so no outbound call can be missed.
        var page = WebUtility.HtmlDecode(await browser.GetStringAsync(new Uri("/calls?outcome=missed&dir=out", UriKind.Relative), Ct));

        Assert.Contains("No missed outbound calls matched these filters.", page, StringComparison.Ordinal);
        Assert.Contains("Clear result", page, StringComparison.Ordinal);
        Assert.Contains("Clear direction", page, StringComparison.Ordinal);
        Assert.Equal(0, TalkWatchApp.CallRows(page));
    }

    [Fact]
    public async Task A_call_is_told_as_what_happened_from_Talks_routing_events()
    {
        var (app, browser) = await SignedInAsync(talkwatch);
        await using var _ = app;
        using var __ = browser;
        // A call that rang long enough for the gap to be shown (gaps under 3 seconds are not drawn), chosen the same way
        // every run: one in ten captured calls was answered within 3 seconds.
        static TimeSpan Rang(CallRow c) => c.Events.First(e => e.Event == "call_accepted").Time - c.Events.First(e => e.Event == "seq_call_trying_endpoints").Time;
        var call = (await CallsAsync(app))
            .Where(c => c.Outcome == CallOutcome.Answered && c.Events.Any(e => e.Event == "seq_call_trying_endpoints") && c.Events.Any(e => e.Event == "call_accepted"))
            .Where(c => Rang(c) >= TimeSpan.FromSeconds(5))
            .OrderBy(c => c.TalkUuid, StringComparer.Ordinal).First();

        var page = WebUtility.HtmlDecode(await browser.GetStringAsync(new Uri($"/calls/{call.TalkUuid}", UriKind.Relative), Ct));

        Assert.Contains("data-chronicle", page, StringComparison.Ordinal);
        Assert.Contains("data-band", page, StringComparison.Ordinal);
        Assert.Matches(@"answered after \d+s", page);
        Assert.Contains("ringing</span>", page, StringComparison.Ordinal);
        Assert.Contains($"data-step-kind=\"{nameof(StepKind.Ended)}\"", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_route_of_a_call_rung_to_one_person_who_answered_names_them_once()
    {
        var (app, browser) = await SignedInAsync(talkwatch);
        await using var _ = app;
        using var __ = browser;
        var call = (await CallsAsync(app)).First(c => c.Outcome == CallOutcome.Answered && c.Events.Count(e => e.Event == "seq_call_trying_endpoints") == 1);

        var page = await browser.GetStringAsync(new Uri($"/calls/{call.TalkUuid}", UriKind.Relative), Ct);
        var route = Route().Match(page).Groups[1].Value;

        Assert.Single(Regex.Matches(route, "data-k=\"user\""));
        Assert.DoesNotContain("data-k=\"group\"", route, StringComparison.Ordinal);
    }

    [GeneratedRegex("data-outcome=\"([A-Za-z]+)\"")]
    private static partial Regex Outcome();

    [GeneratedRegex("data-route>(.*?)</div>", RegexOptions.Singleline)]
    private static partial Regex Route();
}
