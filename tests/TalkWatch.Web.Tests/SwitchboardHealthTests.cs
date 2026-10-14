using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TalkWatch.Core.Alerts;
using TalkWatch.Data;
using TalkWatch.Replay;
using TalkWatch.Web.Services;

namespace TalkWatch.Web.Tests;

/// <summary>Something going wrong with a switchboard's setup raises one alert as it appears, for admins' flows.</summary>
public sealed class SwitchboardHealthTests(TalkWatchApp talkwatch) : IClassFixture<TalkWatchApp>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Path = "/proxy/talk/api/switchboard";

    // The captured switchboard with option 2 (swb_18) changed as given.
    private static async Task SetOptionTwoAsync(FixtureConsole console, bool missingAudio, bool disabled)
    {
        console.Overrides.Remove(Path);
        using var client = console.CreateClient();
        var tree = JsonNode.Parse(await client.GetStringAsync(new Uri(Path, UriKind.Relative), Ct))!;
        var option = tree["entities"]!["nodes"]!["byId"]!["swb_18"]!;
        option["missing_audio"] = missingAudio;
        option["disabled"] = disabled;
        console.Overrides[Path] = tree.ToJsonString();
    }

    private static async Task<List<AlertEvent>> AlertsAsync(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> app)
    {
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<AccessScopeHolder>().UseSystemScope();
        return await scope.ServiceProvider.GetRequiredService<TalkWatchDbContext>().AlertEvents.AsNoTracking()
            .Where(e => e.Type == AlertEventType.SwitchboardProblem).ToListAsync(Ct);
    }

    [Fact]
    public async Task A_problem_raises_one_alert_as_it_appears_and_again_only_if_it_comes_back()
    {
        var console = new FixtureConsole(FixtureConsole.DefaultDirectory);
        await using var app = talkwatch.Create(console);
        var directory = app.Services.GetRequiredService<LineDirectorySync>();
        var monitor = app.Services.GetRequiredService<SwitchboardHealthMonitor>();
        async Task ReadAsync()
        {
            await directory.RefreshAsync(Ct);
            await monitor.CheckAsync(Ct);
        }

        await ReadAsync();
        Assert.Empty(await AlertsAsync(app));

        await SetOptionTwoAsync(console, missingAudio: true, disabled: false);
        await ReadAsync();
        await ReadAsync();
        var raised = Assert.Single(await AlertsAsync(app));
        Assert.Equal("Switchboard problem", raised.Title);
        Assert.Contains("Its greeting has no audio", raised.Message, StringComparison.Ordinal);
        Assert.Contains(" › 2 ", raised.Message, StringComparison.Ordinal);

        // Mended, then wrong again: a new alert.
        await SetOptionTwoAsync(console, missingAudio: false, disabled: false);
        await ReadAsync();
        await SetOptionTwoAsync(console, missingAudio: true, disabled: false);
        await ReadAsync();
        Assert.Equal(2, (await AlertsAsync(app)).Count);
    }

    [Fact]
    public async Task A_switched_off_option_raises_nothing()
    {
        var console = new FixtureConsole(FixtureConsole.DefaultDirectory);
        await SetOptionTwoAsync(console, missingAudio: true, disabled: true);
        await using var app = talkwatch.Create(console);

        await app.Services.GetRequiredService<LineDirectorySync>().RefreshAsync(Ct);
        await app.Services.GetRequiredService<SwitchboardHealthMonitor>().CheckAsync(Ct);

        Assert.Empty(await AlertsAsync(app));
    }
}
