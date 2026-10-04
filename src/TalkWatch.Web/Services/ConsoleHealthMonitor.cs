using Microsoft.EntityFrameworkCore;
using TalkWatch.Core.Alerts;
using TalkWatch.Core.Talk;
using TalkWatch.Data;

namespace TalkWatch.Web.Services;

/// <summary>
/// Watches the Talk account and the settings TalkWatch depends on, hourly, and alerts when they go wrong: a problem
/// with the account as it appears (one alert each, not one an hour while it lasts), and recording or transcription
/// as it is switched off. A setting off when first seen raises nothing, since it may be off on purpose. What was last
/// seen is kept in the database, so a restart neither misses a change nor raises one twice.
/// </summary>
public sealed partial class ConsoleHealthMonitor(
    TalkSession session, IServiceScopeFactory scopes, AlertService alerts, TimeProvider clock, ILogger<ConsoleHealthMonitor> logger)
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    private const string AccountProblems = "account.problems";

    private static readonly (string Fact, string Name, Func<TalkSettings, bool?> Read)[] Watched =
    [
        ("setting.recording", "Call recording", s => s.CallLogRecordingEnabled),
        ("setting.transcription", "AI call transcription", s => s.AiCallTranscriptionsEnabled),
    ];

    private DateTimeOffset? _checkedAt;

    public Task CheckIfDueAsync(CancellationToken cancellationToken) =>
        _checkedAt is { } last && clock.GetUtcNow() - last < Interval ? Task.CompletedTask : CheckAsync(cancellationToken);

    public async Task CheckAsync(CancellationToken cancellationToken)
    {
        try
        {
            var account = await session.Client.GetAccountAsync(cancellationToken);
            var settings = await session.Client.GetSettingsAsync(cancellationToken);
            var now = clock.GetUtcNow();

            using var scope = scopes.CreateScope();
            scope.ServiceProvider.GetRequiredService<AccessScopeHolder>().UseSystemScope();
            var db = scope.ServiceProvider.GetRequiredService<TalkWatchDbContext>();
            var site = scope.ServiceProvider.GetRequiredService<CurrentSite>();
            var facts = await db.ConsoleFacts.ToDictionaryAsync(f => f.Name, cancellationToken);

            var problems = account.Problems();
            var before = facts.TryGetValue(AccountProblems, out var seen) ? seen.Value.Split('\n', StringSplitOptions.RemoveEmptyEntries) : [];
            foreach (var problem in problems.Except(before))
            {
                await alerts.RaiseSiteAsync(AlertEventType.AccountProblem, $"account:{Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(problem)))[..12]}:{now.UtcTicks}",
                    "Talk account problem", problem, cancellationToken);
            }

            Set(db, facts, site.Id, AccountProblems, string.Join('\n', problems), now);

            foreach (var (fact, name, read) in Watched)
            {
                if (read(settings) is not { } on)
                {
                    continue;
                }

                if (!on && facts.TryGetValue(fact, out var was) && was.Value == "on")
                {
                    await alerts.RaiseSiteAsync(AlertEventType.SettingTurnedOff, $"{fact}:off:{now.UtcTicks}",
                        $"{name} switched off", $"{name} has been switched off in Talk, so TalkWatch has none to copy until it is switched back on.", cancellationToken);
                }

                Set(db, facts, site.Id, fact, on ? "on" : "off", now);
            }

            await db.SaveChangesAsync(cancellationToken);
            _checkedAt = now;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // A health check that fails must not stop calls being copied.
        catch (Exception e)
#pragma warning restore CA1031
        {
            LogFailed(logger, e);
        }
    }

    private static void Set(TalkWatchDbContext db, Dictionary<string, ConsoleFact> facts, Guid site, string name, string value, DateTimeOffset now)
    {
        if (facts.TryGetValue(name, out var fact))
        {
            if (fact.Value != value)
            {
                (fact.Value, fact.UpdatedAt) = (value, now);
            }
        }
        else
        {
            fact = new ConsoleFact { SiteId = site, Name = name, Value = value, UpdatedAt = now };
            facts[name] = fact;
            db.ConsoleFacts.Add(fact);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not check the Talk account and settings; trying again later.")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
