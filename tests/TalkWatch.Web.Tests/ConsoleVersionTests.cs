using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TalkWatch.Core.Talk;
using TalkWatch.Data;
using TalkWatch.Replay;
using TalkWatch.Web.Services;

namespace TalkWatch.Web.Tests;

public sealed class ConsoleVersionTests(TalkWatchApp talkwatch) : IClassFixture<TalkWatchApp>
{
    private const string InfoPath = "/proxy/talk/api/info";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task PollAsync(WebApplicationFactory<Program> app) =>
        await app.Services.GetRequiredService<CallLogPoller>().RunOnceAsync(Ct);

    private static PlatformStatus Platform(WebApplicationFactory<Program> app) => app.Services.GetRequiredService<PlatformStatus>();

    private static async Task<int> HistoryAsync(WebApplicationFactory<Program> app)
    {
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<AccessScopeHolder>().UseSystemScope();
        return await scope.ServiceProvider.GetRequiredService<TalkWatchDbContext>().ConsoleVersions.CountAsync(Ct);
    }

    private static async Task<string> HeaderAsync(WebApplicationFactory<Program> app)
    {
        using var browser = TalkWatchApp.Browser(app);
        await TalkWatchApp.SignInAsync(browser, TalkWatchApp.AdminUsername, TalkWatchApp.AdminPassword);
        return await browser.GetStringAsync(new Uri("/calls", UriKind.Relative), Ct);
    }

    [Fact]
    public async Task The_captured_console_is_the_tested_versions_on_a_pre_release_channel_and_the_header_says_so()
    {
        await using var app = talkwatch.Create(new FixtureConsole(FixtureConsole.DefaultDirectory));

        await PollAsync(app);
        var page = await HeaderAsync(app);

        Assert.Equal(ConsoleVersions.Tested, Platform(app).Versions);
        Assert.Equal(VersionFit.Tested, Platform(app).Fit);
        Assert.True(Platform(app).PreRelease);
        Assert.Equal(1, await HistoryAsync(app));
        Assert.Contains("pre-release channel", page, StringComparison.Ordinal);
        Assert.DoesNotContain("Not yet tested", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_talk_update_is_recorded_and_flagged_as_untested()
    {
        var console = new FixtureConsole(FixtureConsole.DefaultDirectory);
        await using var app = talkwatch.Create(console);
        await PollAsync(app);

        console.Overrides[InfoPath] = """{"version":"5.4.0","update_channel":"release"}""";
        await app.Services.GetRequiredService<ConsoleVersionMonitor>().CheckAsync(Ct);
        var page = await HeaderAsync(app);

        Assert.Equal("5.4.0", Platform(app).Versions!.Talk);
        Assert.Equal(VersionFit.NewerUntested, Platform(app).Fit);
        Assert.Equal("5.3.2", Platform(app).Previous!.Talk);
        Assert.Equal(2, await HistoryAsync(app));
        Assert.Contains("Not yet tested", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Calls_are_still_copied_when_the_version_endpoints_break()
    {
        var console = new FixtureConsole(FixtureConsole.DefaultDirectory);
        console.Overrides[InfoPath] = """{"version":{"unexpected":true}}""";
        await using var app = talkwatch.Create(console);

        await PollAsync(app);

        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<AccessScopeHolder>().UseSystemScope();
        Assert.Equal(console.CallCount, await scope.ServiceProvider.GetRequiredService<TalkWatchDbContext>().Calls.CountAsync(Ct));
        Assert.Null(Platform(app).Versions);
        Assert.Null(app.Services.GetRequiredService<IngestionStatus>().LastError);
    }

    [Fact]
    public async Task Drift_after_an_update_names_the_update()
    {
        var console = new FixtureConsole(FixtureConsole.DefaultDirectory);
        await using var app = talkwatch.Create(console);
        await PollAsync(app);

        console.Overrides[InfoPath] = """{"version":"5.4.0","update_channel":"release-candidate"}""";
        await app.Services.GetRequiredService<ConsoleVersionMonitor>().CheckAsync(Ct);
        console.Drifted = true;
        await PollAsync(app);
        var page = await HeaderAsync(app);

        Assert.Contains("changed shape", page, StringComparison.Ordinal);
        Assert.Contains("5.3.2 → 5.4.0", page, StringComparison.Ordinal);
    }
}
