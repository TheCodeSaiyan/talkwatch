using TalkWatch.Replay;

namespace TalkWatch.Web.Tests;

public class HealthTests(TalkWatchApp talkwatch) : IClassFixture<TalkWatchApp>
{
    [Fact]
    public async Task Health_endpoint_reports_healthy()
    {
        await using var app = talkwatch.Create(new FixtureConsole(FixtureConsole.DefaultDirectory));
        using var client = app.CreateClient();

        var response = await client.GetAsync(new Uri("/healthz", UriKind.Relative), TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }
}
