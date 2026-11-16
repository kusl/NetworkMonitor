using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NetworkMonitor.Core.Storage;

namespace NetworkMonitor.Core.Sync;

public sealed class RollupSyncService(
    IEnumerable<IRollupSink> sinks,
    IRollupStore store,
    TimeProvider timeProvider,
    ILogger<RollupSyncService> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        List<Task> loops = [];
        foreach (var sink in sinks)
        {
            if (sink.IsEnabled)
            {
                loops.Add(RunAsync(new RollupSyncer(sink, store, timeProvider, logger), stoppingToken));
            }
            else
            {
                logger.LogDebug("{Sink} is not configured; disabled", sink.Name);
            }
        }

        return Task.WhenAll(loops);
    }

    private async Task RunAsync(RollupSyncer syncer, CancellationToken stoppingToken)
    {
        var sink = syncer.Sink;
        var options = sink.Options;
        logger.LogInformation(
            "{Sink} enabled (every {Minutes} min, {Bucket}-min rollups) to {Destination}",
            sink.Name,
            options.SyncIntervalMinutes,
            options.BucketMinutes,
            sink.Destination);

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(options.InitialDelaySeconds), timeProvider, stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(options.SyncIntervalMinutes), timeProvider);

            do
            {
                try
                {
                    await syncer.SyncOnceAsync(stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogDebug(ex, "{Sink} attempt failed; retrying next interval", sink.Name);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
