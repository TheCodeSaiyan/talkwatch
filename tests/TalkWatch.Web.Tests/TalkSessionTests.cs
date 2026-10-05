using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TalkWatch.Replay;
using TalkWatch.Web.Services;

namespace TalkWatch.Web.Tests;

/// <summary>The one console session: however many parts of TalkWatch ask for it at once, they share one client.</summary>
public sealed class TalkSessionTests(TalkWatchApp talkwatch) : IClassFixture<TalkWatchApp>
{
    // The poller and the live listener start together. Two clients sharing one cookie jar but each with its own idea of
    // being signed in meant the second signed in over the first's session, the console refused it, and the session was
    // stuck refusing every fifteen minutes: no live feed, no polling.
    [Fact]
    public async Task Everything_asking_for_the_client_at_once_gets_the_same_one()
    {
        await using var app = talkwatch.Create(new FixtureConsole(FixtureConsole.DefaultDirectory));
        var services = app.Services;
        for (var round = 0; round < 300; round++)
        {
            var session = new TalkSession(services.GetRequiredService<IHttpClientFactory>(), services.GetRequiredService<IHttpMessageHandlerFactory>(),
                services.GetRequiredService<ConsoleConnection>(), new IngestionStatus(), TimeProvider.System, NullLogger<TalkSession>.Instance);
            using var start = new Barrier(8);
            var clients = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => { start.SignalAndWait(TestContext.Current.CancellationToken); return session.Client; })));
            Assert.Single(clients.Distinct());
            session.Dispose();
        }
    }
}
