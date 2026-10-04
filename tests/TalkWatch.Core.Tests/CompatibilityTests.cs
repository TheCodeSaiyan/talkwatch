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

        Assert.Equal(ConsoleVersions.Tested, versions);
        Assert.True(ConsoleVersions.IsPreRelease(versions.TalkChannel));
    }

    [Theory]
    [InlineData("5.1.33", "5.3.2", VersionFit.Tested)]
    [InlineData("5.1.33", "5.3.3", VersionFit.NewerUntested)]
    [InlineData("5.2.0", "5.3.2", VersionFit.NewerUntested)]
    [InlineData("5.1.30", "5.3.2", VersionFit.OlderUntested)]
    [InlineData("5.1.33", "5.4.0-beta.1", VersionFit.NewerUntested)]
    [InlineData(null, "5.3.2", VersionFit.Unknown)]
    [InlineData("5.1.33", "not a version", VersionFit.Unknown)]
    public void Versions_are_placed_against_the_tested_baseline(string? os, string? talk, VersionFit expected) =>
        Assert.Equal(expected, Compatibility.Assess(new ConsoleVersions(os, "release", talk, "release"), ConsoleVersions.Tested));

    [Theory]
    [InlineData("release", false)]
    [InlineData("stable", false)]
    [InlineData("release-candidate", true)]
    [InlineData("early-access", true)]
    [InlineData(null, false)]
    public void Pre_release_channels_are_recognised(string? channel, bool preRelease) =>
        Assert.Equal(preRelease, ConsoleVersions.IsPreRelease(channel));
}
