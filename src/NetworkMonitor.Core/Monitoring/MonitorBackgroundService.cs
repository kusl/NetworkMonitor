using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetworkMonitor.Core.Configuration;
using NetworkMonitor.Core.Display;
using NetworkMonitor.Core.Models;
using NetworkMonitor.Core.Storage;

namespace NetworkMonitor.Core.Monitoring;

public sealed class MonitorBackgroundService(
    INetworkMonitor monitor,
    IStatusStore store,
    IStatusDisplay display,
    IOptions<MonitorOptions> options,
    TimeProvider timeProvider,
    ILogger<MonitorBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        var interval = TimeSpan.FromMilliseconds(settings.IntervalMs);
        logger.LogInformation(
            "Network Monitor starting. Interval: {IntervalMs}ms, Router: {Router}, Internet: {Internet}, CustomTargets: {CustomCount}",
            settings.IntervalMs,
            settings.RouterAddress,
            settings.InternetTarget,
            settings.CustomTargets.Count);

        using var timer = new PeriodicTimer(interval, timeProvider);
        var overrunWarned = false;

        try
        {
            do
            {
                var started = timeProvider.GetTimestamp();
                await RunCycleAsync(stoppingToken);
                var elapsed = timeProvider.GetElapsedTime(started);

                if (elapsed > interval && !overrunWarned)
                {
                    overrunWarned = true;
                    logger.LogWarning(
                        "A monitoring cycle took {Elapsed:F0}ms, exceeding the {Interval}ms interval. Consider raising IntervalMs, lowering PingsPerCycle, or trimming CustomTargets.",
                        elapsed.TotalMilliseconds,
                        settings.IntervalMs);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            display.Clear();
        }
    }

    internal async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        NetworkStatus status;
        try
        {
            status = await monitor.CheckAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during monitoring cycle");
            return;
        }

        await store.SaveAsync(status, cancellationToken);

        try
        {
            display.Update(status);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            logger.LogDebug(ex, "Status display failed");
        }
    }
}
