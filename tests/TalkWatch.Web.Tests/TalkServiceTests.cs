using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using TalkWatch.Core.Talk;
using TalkWatch.Replay;
using TalkWatch.Web.Services;

namespace TalkWatch.Web.Tests;

/// <summary>
/// Talk 5.4.5 reports, in /api/info, whether it can reach its cloud services and whether it is running degraded, and
/// pushes degradation changes on the live feed. The strip under the rail says so on every page.
/// </summary>
public sealed class TalkServiceTests(TalkWatchApp talkwatch) : IClassFixture<TalkWatchApp>
{
    private const string InfoPath = "/proxy/talk/api/info";
    private const string Healthy = """
        {"version":"5.4.5","update_channel":"release-candidate",
         "cloud_connectivity":{"degraded":false,"reason":null,"since":null},
         "degradation":{"degraded":false,"degradation_level":0,"load_level":null,"pressures":[],"targeted_for_suspension":false,"suspended_at":null,"since":null}}
        """;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task PollAsync(WebApplicationFactory<Program> app) =>
        await app.Services.GetRequiredService<CallLogPoller>().RunOnceAsync(Ct);

    private static async Task<string> HeaderAsync(WebApplicationFactory<Program> app)
    {
        using var browser = TalkWatchApp.Browser(app);
        await TalkWatchApp.SignInAsync(browser, TalkWatchApp.AdminUsername, TalkWatchApp.AdminPassword);
        return await browser.GetStringAsync(new Uri("/calls", UriKind.Relative), Ct);
    }

    [Fact]
    public async Task Talk_losing_its_cloud_services_shows_on_every_page_until_it_is_back()
    {
        var console = new FixtureConsole(FixtureConsole.DefaultDirectory);
        await using var app = talkwatch.Create(console);

        console.Overrides[InfoPath] = Healthy.Replace(
            "\"cloud_connectivity\":{\"degraded\":false,\"reason\":null", "\"cloud_connectivity\":{\"degraded\":true,\"reason\":\"no_route\"", StringComparison.Ordinal);
        await PollAsync(app);
        var down = await HeaderAsync(app);

        console.Overrides[InfoPath] = Healthy;
        await PollAsync(app);
        var back = await HeaderAsync(app);

        Assert.Contains("Talk can't reach its cloud services", down, StringComparison.Ordinal);
        Assert.Contains("no route", down, StringComparison.Ordinal);
        Assert.DoesNotContain("cloud services", back, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Talk_running_degraded_shows_from_the_poll_and_from_the_live_feed()
    {
        var console = new FixtureConsole(FixtureConsole.DefaultDirectory);
        await using var app = talkwatch.Create(console);
        console.Overrides[InfoPath] = Healthy;
        await PollAsync(app);

        await app.Services.GetRequiredService<LiveListener>().HandleAsync(LiveMessage.Parse(
            """{"event":"DEGRADATION_UPDATED","data":{"degraded":true,"degradation_level":2,"load_level":null,"pressures":[],"targeted_for_suspension":false,"suspended_at":null,"since":null}}""")!, Ct);
        var degraded = await HeaderAsync(app);

        await app.Services.GetRequiredService<LiveListener>().HandleAsync(LiveMessage.Parse(
            """{"event":"DEGRADATION_UPDATED","data":{"degraded":false,"degradation_level":0,"pressures":[]}}""")!, Ct);
        var recovered = await HeaderAsync(app);

        Assert.Contains("Talk reports it is running degraded", degraded, StringComparison.Ordinal);
        Assert.DoesNotContain("running degraded", recovered, StringComparison.Ordinal);
    }

    // Talk 5.3.2 sends neither: nothing is shown, and nothing fails.
    [Fact]
    public async Task A_console_that_does_not_report_its_cloud_or_load_shows_nothing()
    {
        await using var app = talkwatch.Create(new FixtureConsole(FixtureConsole.DefaultDirectory));

        await PollAsync(app);
        var page = await HeaderAsync(app);

        Assert.DoesNotContain("cloud services", page, StringComparison.Ordinal);
        Assert.DoesNotContain("running degraded", page, StringComparison.Ordinal);
    }
}
