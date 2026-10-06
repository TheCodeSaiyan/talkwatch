using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TalkWatch.Core.Calls;
using TalkWatch.Data;
using TalkWatch.Replay;
using TalkWatch.Web.Services;

namespace TalkWatch.Web.Tests;

public sealed partial class DashboardTests(TalkWatchApp talkwatch) : IClassFixture<TalkWatchApp>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    // The fixtures' last 92 days, as whole days in London.
    private const string Period = "from=2026-06-29&to=2026-09-28";
    private static readonly DateTimeOffset Start = new(2026, 6, 29, 0, 0, 0, TimeSpan.FromHours(1));
    private static readonly DateTimeOffset End = new(2026, 9, 29, 0, 0, 0, TimeSpan.FromHours(1));

    [GeneratedRegex("""data-figure="([a-z-]+)"><strong>([^<]*)</strong>""")]
    private static partial Regex Figure();

    private static Dictionary<string, string> Figures(string html) =>
        Figure().Matches(html).ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);

    private static async Task<WebApplicationFactory<Program>> ImportedAsync(TalkWatchApp talkwatch)
    {
        var app = talkwatch.Create(new FixtureConsole(FixtureConsole.DefaultDirectory));
        await app.Services.GetRequiredService<CallLogPoller>().RunOnceAsync(Ct);
        return app;
    }

    private static async Task<T> AsSystemAsync<T>(WebApplicationFactory<Program> app, Func<TalkWatchDbContext, Task<T>> work)
    {
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<AccessScopeHolder>().UseSystemScope();
        return await work(scope.ServiceProvider.GetRequiredService<TalkWatchDbContext>());
    }

    [Fact]
    public async Task Missed_callers_who_give_up_before_the_usual_answer_are_told_as_sooner_never_as_negative_seconds()
    {
        await using var app = await ImportedAsync(talkwatch);
        using var admin = TalkWatchApp.Browser(app);
        await TalkWatchApp.SignInAsync(admin, TalkWatchApp.AdminUsername, TalkWatchApp.AdminPassword);

        var page = WebUtility.HtmlDecode(await admin.GetStringAsync(new Uri($"/dashboard?{Period}", UriKind.Relative), Ct));
        var expected = await AsSystemAsync(app, db => CallStatistics.ComputeAsync(db, Start, End, London, Ct));

        Assert.DoesNotMatch(@"only -\d", page);
        if (expected.Speed is { MedianRing: { } ring, MedianGiveUp: { } giveUp } && giveUp <= ring)
        {
            Assert.Contains("gave up sooner than calls are usually answered", page, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task An_admin_sees_the_figures_for_every_line()
    {
        await using var app = await ImportedAsync(talkwatch);
        using var admin = TalkWatchApp.Browser(app);
        await TalkWatchApp.SignInAsync(admin, TalkWatchApp.AdminUsername, TalkWatchApp.AdminPassword);

        var page = await admin.GetStringAsync(new Uri($"/dashboard?{Period}", UriKind.Relative), Ct);

        var expected = await AsSystemAsync(app, db => CallStatistics.ComputeAsync(db, Start, End, London, Ct));
        var figures = Figures(page);
        Assert.Equal(expected.Inbound.ToString(System.Globalization.CultureInfo.InvariantCulture), figures["inbound"]);
        Assert.Equal(expected.Count(CallOutcome.Answered).ToString(System.Globalization.CultureInfo.InvariantCulture), figures["answered"]);
        Assert.Equal(expected.Count(CallOutcome.Missed).ToString(System.Globalization.CultureInfo.InvariantCulture), figures["missed"]);
        Assert.True(expected.Inbound > 0);
        Assert.Equal(expected.ByLine.Count, Regex.Count(page, "data-line=\""));
        Assert.Equal(24, Regex.Count(page, "data-hour=\""));

        // Answer speed, quality and the weekday-by-hour grid, as the statistics have them.
        Assert.Equal($"{Math.Round(expected.Speed.MedianRing!.Value.TotalSeconds):0} s", figures["median-ring"]);
        Assert.Equal($"{expected.Quality.Poor} of {expected.Quality.Scored}", figures["quality-poor"]);
        Assert.Equal(7 * 24, Regex.Count(page, "data-cell=\""));
        Assert.Equal(expected.MissedByWeekdayAndHour.Sum(row => row.Sum(c => c.Missed)), Regex.Matches(page, "data-missed=\"(\\d+)\"").Sum(m => int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)));
        Assert.Equal(expected.Quality.Worst.Count, Regex.Count(page, "data-poor-call=\""));
    }

    // Each line says what kind it is in words, as the rest of TalkWatch does, never by the name it has in the code.
    [Fact]
    public async Task Each_line_says_what_kind_it_is_in_words()
    {
        await using var app = await ImportedAsync(talkwatch);
        using var admin = TalkWatchApp.Browser(app);
        await TalkWatchApp.SignInAsync(admin, TalkWatchApp.AdminUsername, TalkWatchApp.AdminPassword);

        var page = await admin.GetStringAsync(new Uri($"/dashboard?{Period}", UriKind.Relative), Ct);

        var kinds = LineKindCell().Matches(page).Select(m => (Kind: m.Groups[1].Value, Said: m.Groups[2].Value)).ToList();
        Assert.Equal(Regex.Count(page, "data-line=\""), kinds.Count);
        Assert.Contains(kinds, k => k.Kind == "Did");
        Assert.Contains(kinds, k => k.Kind == "Attendant");
        var words = new Dictionary<string, string>
        {
            ["Did"] = "Number", ["User"] = "Person", ["RingGroup"] = "Ring group", ["Attendant"] = "Switchboard", ["Queue"] = "Queue", ["Contact"] = "Outside number",
        };
        Assert.All(kinds, k => Assert.Equal(words[k.Kind], k.Said));
    }

    [GeneratedRegex("""data-line="([A-Za-z]+):[^"]*">\s*<td class="first">.*?</td>\s*<td class="muted">([^<]*)</td>""", RegexOptions.Singleline)]
    private static partial Regex LineKindCell();

    [Fact]
    public async Task A_viewer_sees_figures_only_for_their_line()
    {
        await using var app = await ImportedAsync(talkwatch);
        var did = await AsSystemAsync(app, db => db.CallLines.Where(l => l.Kind == LineKind.Did)
            .GroupBy(l => l.Key).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstAsync(Ct));
        using (var scope = app.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<AccessScopeHolder>().UseSystemScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var db = scope.ServiceProvider.GetRequiredService<TalkWatchDbContext>();
            var site = scope.ServiceProvider.GetRequiredService<CurrentSite>();
            var viewer = new AppUser { Id = Guid.NewGuid(), UserName = "viewer", SiteId = site.Id };
            Assert.True((await users.CreateAsync(viewer, "a long enough password")).Succeeded);
            await users.AddToRoleAsync(viewer, Roles.Viewer);
            db.Grants.Add(new Grant { Id = Guid.NewGuid(), SiteId = site.Id, UserId = viewer.Id, Kind = LineKind.Did, Key = did, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(Ct);
        }

        using var browser = TalkWatchApp.Browser(app);
        await TalkWatchApp.SignInAsync(browser, "viewer", "a long enough password");
        var page = await browser.GetStringAsync(new Uri($"/dashboard?{Period}", UriKind.Relative), Ct);

        var everything = await AsSystemAsync(app, db => CallStatistics.ComputeAsync(db, Start, End, London, Ct));
        var onTheirLine = everything.ByLine.Single(l => l.Kind == LineKind.Did && l.Key == did);
        Assert.Equal(onTheirLine.Inbound.ToString(System.Globalization.CultureInfo.InvariantCulture), Figures(page)["inbound"]);
        Assert.Equal([$"Did:{did}"], Regex.Matches(page, "data-line=\"([^\"]+)\"").Select(m => WebUtility.HtmlDecode(m.Groups[1].Value)));
    }

    [Theory]
    [InlineData("/dashboard", HttpStatusCode.OK, null)]
    [InlineData("/dashboard?days=30", HttpStatusCode.OK, null)]
    [InlineData("/dashboard?from=2026-09-28&to=2026-09-01", HttpStatusCode.OK, "Give the period")]
    [InlineData("/dashboard?from=2026-01-01&to=2026-09-01", HttpStatusCode.OK, "at most 92 days")]
    public async Task The_period_can_be_chosen_and_a_bad_one_is_explained(string path, HttpStatusCode status, string? message)
    {
        await using var app = await ImportedAsync(talkwatch);
        using var admin = TalkWatchApp.Browser(app);
        await TalkWatchApp.SignInAsync(admin, TalkWatchApp.AdminUsername, TalkWatchApp.AdminPassword);

        var response = await admin.GetAsync(new Uri(path, UriKind.Relative), Ct);
        var page = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(status, response.StatusCode);
        if (message is null)
        {
            Assert.Contains("data-figure=\"inbound\"", page, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains(message, page, StringComparison.Ordinal);
        }
    }
}
