using Microsoft.EntityFrameworkCore;
using TalkWatch.Data;

namespace TalkWatch.Web.Services;

/// <summary>
/// Runs reports when they are due, checking every minute. A report that fails is logged and tried again at its next
/// time; one missed while TalkWatch was down runs once when it comes back, not once for every time it missed.
/// </summary>
public sealed partial class ReportScheduler(IServiceScopeFactory scopes, ReportBuilder builder, ReportMailer mailer, TimeProvider clock, ILogger<ReportScheduler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), clock);
        do
        {
            // A failure is logged and tried again next minute: a background service that throws stops the whole app,
            // and a report that could not be emailed once kept TalkWatch from starting at all.
            try
            {
                await RunDueAsync(stoppingToken);
                await mailer.SendDueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
#pragma warning disable CA1031
            catch (Exception e)
#pragma warning restore CA1031
            {
                LogRoundFailed(logger, e);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Running or emailing reports failed; trying again in a minute.")]
    private static partial void LogRoundFailed(ILogger logger, Exception exception);

    /// <summary>Runs every report that is due now. Returns how many ran.</summary>
    public async Task<int> RunDueAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        List<Report> due;
        using (var scope = scopes.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<AccessScopeHolder>().UseSystemScope();
            var db = scope.ServiceProvider.GetRequiredService<TalkWatchDbContext>();
            due = await db.Reports.Where(r => r.Enabled && r.NextRunAt != null && r.NextRunAt <= now).ToListAsync(cancellationToken);

            // The next time is set before running, so a report that fails is not retried every minute.
            foreach (var report in due)
            {
                report.NextRunAt = ReportTiming.Next(report, now, builder.Zone);
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        var ran = 0;
        foreach (var report in due)
        {
            try
            {
                await builder.RunAsync(report.Id, now, cancellationToken);
                ran++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
#pragma warning disable CA1031 // One report failing must not stop the others.
            catch (Exception e)
#pragma warning restore CA1031
            {
                LogFailed(logger, report.Name, e);
            }
        }

        return ran;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The report '{Report}' could not be run; it runs again at its next time.")]
    private static partial void LogFailed(ILogger logger, string report, Exception exception);
}
