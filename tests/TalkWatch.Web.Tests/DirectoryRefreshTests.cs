using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TalkWatch.Data;
using TalkWatch.Replay;
using TalkWatch.Web.Services;

namespace TalkWatch.Web.Tests;

/// <summary>
/// The directory is read by the poller, the live feed and the demo, which can all start a read at once: on a fresh
/// database each saw no lines and inserted them all, and every read but one failed on a duplicate key.
/// </summary>
public sealed class DirectoryRefreshTests(TalkWatchApp talkwatch) : IClassFixture<TalkWatchApp>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Captured : ILoggerProvider
    {
        public ConcurrentQueue<(string Category, LogLevel Level, string Message)> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, Entries);

        public void Dispose()
        {
        }

        private sealed class Logger(string category, ConcurrentQueue<(string, LogLevel, string)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(logLevel))
                {
                    entries.Enqueue((category, logLevel, formatter(state, exception) + " " + exception?.GetType().Name));
                }
            }
        }
    }

    [Fact]
    public async Task Reads_started_together_on_a_fresh_database_all_succeed()
    {
        var logs = new Captured();
        await using var app = talkwatch.Create(new FixtureConsole(FixtureConsole.DefaultDirectory), services: s => s.AddSingleton<ILoggerProvider>(logs));
        var sync = app.Services.GetRequiredService<LineDirectorySync>();

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => sync.RefreshAsync(Ct), Ct)));

        Assert.DoesNotContain(logs.Entries, e => e.Category == typeof(LineDirectorySync).FullName);
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<AccessScopeHolder>().UseSystemScope();
        var db = scope.ServiceProvider.GetRequiredService<TalkWatchDbContext>();
        Assert.NotEmpty(await db.Lines.ToListAsync(Ct));
        Assert.NotEmpty(await db.SwitchboardNodes.ToListAsync(Ct));
    }
}
