using System.Globalization;
using Microsoft.Extensions.Logging;
using NetworkMonitor.Core.Models;
using NetworkMonitor.Core.Storage;

namespace NetworkMonitor.Core.Sync;

public sealed class RollupSyncer(IRollupSink sink, IRollupStore store, TimeProvider timeProvider, ILogger logger)
{
    private bool _schemaEnsured;
    private bool _announced;

    public IRollupSink Sink => sink;

    public async Task<int> SyncOnceAsync(CancellationToken cancellationToken)
    {
        if (!sink.IsEnabled)
        {
            return 0;
        }

        if (!_schemaEnsured)
        {
            if (!await sink.EnsureSchemaAsync(cancellationToken))
            {
                return 0;
            }

            _schemaEnsured = true;
        }

        var options = sink.Options;
        var bucketMs = options.BucketMinutes * 60_000L;
        var nowMs = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var openBucketStart = nowMs - (nowMs % bucketMs);
        var batchSize = options.BatchSize;
        var maxRows = Math.Max(batchSize, options.MaxRowsPerSync);
        var checkpoint = await ReadCheckpointAsync(cancellationToken);
        var total = 0;

        while (total < maxRows && checkpoint < openBucketStart && !cancellationToken.IsCancellationRequested)
        {
            IReadOnlyList<CheckRollup> rows;
            try
            {
                rows = await store.GetRollupsAsync(checkpoint, openBucketStart, options.BucketMinutes, batchSize + 1, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "{Sink} could not read local rollups", sink.Name);
                break;
            }

            if (rows.Count == 0)
            {
                await WriteCheckpointAsync(openBucketStart, cancellationToken);
                break;
            }

            var truncated = rows.Count > batchSize;
            var (batch, next) = CompleteBuckets(truncated ? [.. rows.Take(batchSize)] : rows, truncated, bucketMs);

            if (!await sink.PushAsync(batch, timeProvider.GetUtcNow().ToUnixTimeMilliseconds(), cancellationToken))
            {
                break;
            }

            checkpoint = next;
            await WriteCheckpointAsync(checkpoint, cancellationToken);
            total += batch.Count;

            if (!truncated)
            {
                break;
            }
        }

        if (total > 0)
        {
            logger.Log(
                _announced ? LogLevel.Debug : LogLevel.Information,
                "{Sink} pushed {Count} rollup row(s) to {Destination}",
                sink.Name,
                total,
                sink.Destination);
            _announced = true;
        }

        return total;
    }

    internal static (IReadOnlyList<CheckRollup> Batch, long NextCheckpoint) CompleteBuckets(
        IReadOnlyList<CheckRollup> page,
        bool truncated,
        long bucketMs)
    {
        var lastBucket = page[^1].BucketStartMs;
        if (!truncated)
        {
            return (page, lastBucket + bucketMs);
        }

        CheckRollup[] complete = [.. page.Where(r => r.BucketStartMs < lastBucket)];
        return complete.Length == 0
            ? (page, lastBucket + bucketMs)
            : (complete, complete[^1].BucketStartMs + bucketMs);
    }

    private async Task<long> ReadCheckpointAsync(CancellationToken cancellationToken)
    {
        try
        {
            var raw = await store.GetSyncStateAsync(sink.CheckpointKey, cancellationToken);
            return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "{Sink} could not read its checkpoint; starting from 0", sink.Name);
            return 0;
        }
    }

    private async Task WriteCheckpointAsync(long value, CancellationToken cancellationToken)
    {
        try
        {
            await store.SetSyncStateAsync(sink.CheckpointKey, value.ToString(CultureInfo.InvariantCulture), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "{Sink} could not persist its checkpoint", sink.Name);
        }
    }
}
