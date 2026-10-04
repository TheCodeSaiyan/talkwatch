using TalkWatch.Data;

namespace TalkWatch.Web.Services;

/// <summary>Sweeps under the retention policy every six hours, starting a minute after start-up, and on request.</summary>
public sealed partial class RetentionService(IServiceScopeFactory scopes, AudioStore audio, TimeProvider clock, ILogger<RetentionService> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    private readonly SemaphoreSlim _gate = new(1, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(1), clock, stoppingToken);
        using var timer = new PeriodicTimer(Interval, clock);
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
#pragma warning disable CA1031 // A sweep that fails is tried again at the next interval; the app carries on.
            catch (Exception e)
#pragma warning restore CA1031
            {
                LogFailed(logger, e);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task<SweepResult> RunOnceAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var scope = scopes.CreateScope();
            scope.ServiceProvider.GetRequiredService<AccessScopeHolder>().UseSystemScope();
            var result = await new RetentionSweeper(scope.ServiceProvider.GetRequiredService<TalkWatchDbContext>(), audio, clock).RunAsync(cancellationToken);
            if (result.Calls + result.Files > 0)
            {
                LogSwept(logger, result.Calls, result.Files);
                Telemetry.RetentionRemoved.Add(result.Calls, new KeyValuePair<string, object?>("what", "calls"));
                Telemetry.RetentionRemoved.Add(result.Files, new KeyValuePair<string, object?>("what", "audio_files"));
            }

            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public override void Dispose()
    {
        _gate.Dispose();
        base.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Retention: removed {Calls} calls and {Files} audio files.")]
    private static partial void LogSwept(ILogger logger, int calls, int files);

    [LoggerMessage(Level = LogLevel.Error, Message = "The retention sweep failed; it runs again at the next interval.")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
