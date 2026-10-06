using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using TalkWatch.Replay;
using TalkWatch.Web.Services;

namespace TalkWatch.Web.Tests;

/// <summary>
/// The database going away for a minute, as it did when the Docker host's DNS stopped answering for the database's
/// name: the background services log the failed round and try again, rather than one of them throwing, which stops the
/// whole app and leaves it to Docker to start it again.
/// </summary>
public sealed class DatabaseOutageTests(TalkWatchApp talkwatch) : IClassFixture<TalkWatchApp>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_app_keeps_running_while_its_database_is_unreachable_and_carries_on_after()
    {
        await using var app = talkwatch.Create(new FixtureConsole(FixtureConsole.DefaultDirectory));
        var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
        var dispatcher = app.Services.GetRequiredService<AlertDispatcher>();
        var database = new NpgsqlConnectionStringBuilder(app.Services.GetRequiredService<IConfiguration>().GetConnectionString("TalkWatch"));
        var server = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Database = "postgres", Pooling = false }.ConnectionString;

        // No new connections, and the open ones ended: every query the app makes now fails.
        await RunAsync(server, $"ALTER DATABASE \"{database.Database}\" ALLOW_CONNECTIONS false");
        await RunAsync(server, $"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{database.Database}'");
        NpgsqlConnection.ClearAllPools();
        // The dispatcher's round comes every five seconds: two of them, failing.
        await Task.Delay(TimeSpan.FromSeconds(12), Ct);

        Assert.False(lifetime.ApplicationStopping.IsCancellationRequested, "The app stopped while its database was unreachable.");
        Assert.False(dispatcher.ExecuteTask?.IsCompleted ?? true, "The alert dispatcher's loop ended.");

        await RunAsync(server, $"ALTER DATABASE \"{database.Database}\" ALLOW_CONNECTIONS true");
        Assert.Equal(0, await dispatcher.RunOnceAsync(Ct));
        Assert.False(lifetime.ApplicationStopping.IsCancellationRequested);
    }

    private static async Task RunAsync(string connection, string sql)
    {
        await using var c = new NpgsqlConnection(connection);
        await c.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, c);
        await command.ExecuteNonQueryAsync(Ct);
    }
}
