using TalkWatch.Core.Talk;
using TalkWatch.Replay;

namespace TalkWatch.Core.Tests;

public class CompatibilityTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_captured_console_reports_the_tested_versions_and_its_channels()
    {
        var talk = new TalkClient(new FixtureConsole(FixtureConsole.DefaultDirectory).CreateClient());

        var versions = await talk.GetVersionsAsync(Ct);

        Assert.Equal(ConsoleVersions.TestedFrom, versions);
        Assert.True(ConsoleVersions.IsPreRelease(versions.TalkChannel));
    }

    // The newest capture: Talk 5.4.5 reports whether it can reach its cloud and whether it is degraded; both healthy then.
    [Fact]
    public async Task Talk_5_4_5_reports_its_cloud_and_load()
    {
        var talk = new TalkClient(new FixtureConsole(Path.Combine(Path.GetDirectoryName(FixtureConsole.DefaultDirectory)!, "talk-5.4.5")).CreateClient());

        var info = await talk.GetInfoAsync(Ct);

        Assert.Equal("5.4.5", info.Version);
        Assert.Equal((false, false), (info.CloudConnectivity?.Degraded, info.Degradation?.Degraded));
        Assert.Equal("0", TalkValue.Words(info.Degradation?.DegradationLevel));
        Assert.Null(TalkValue.Time(info.CloudConnectivity?.Since));
    }

    [Theory]
    [InlineData("5.1.33", "5.3.2", VersionFit.Tested)]
    [InlineData("5.1.33", "5.4.0", VersionFit.Tested)]
    [InlineData("5.1.33", "5.4.5", VersionFit.Tested)]
    [InlineData("5.1.33", "5.4.6", VersionFit.NewerUntested)]
    [InlineData("5.1.33", "5.3.1", VersionFit.OlderUntested)]
    [InlineData("5.2.0", "5.3.2", VersionFit.NewerUntested)]
    [InlineData("5.1.30", "5.3.2", VersionFit.OlderUntested)]
    [InlineData("5.1.33", "5.5.0-beta.1", VersionFit.NewerUntested)]
    [InlineData(null, "5.3.2", VersionFit.Unknown)]
    [InlineData("5.1.33", "not a version", VersionFit.Unknown)]
    public void Versions_are_placed_against_the_tested_baseline(string? os, string? talk, VersionFit expected) =>
        Assert.Equal(expected, Compatibility.Assess(new ConsoleVersions(os, "release", talk, "release"), ConsoleVersions.TestedFrom, ConsoleVersions.TestedTo));

    [Theory]
    [InlineData("release", false)]
    [InlineData("stable", false)]
    [InlineData("release-candidate", true)]
    [InlineData("early-access", true)]
    [InlineData(null, false)]
    public void Pre_release_channels_are_recognised(string? channel, bool preRelease) =>
        Assert.Equal(preRelease, ConsoleVersions.IsPreRelease(channel));
}
