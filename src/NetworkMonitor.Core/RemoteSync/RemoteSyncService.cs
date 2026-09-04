using System.Globalization;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetworkMonitor.Core.Models;
using NetworkMonitor.Core.Storage;

namespace NetworkMonitor.Core.RemoteSync;

/// <summary>
/// Background service that replicates local check history to an optional remote
/// libSQL / Turso-compatible database as compact per-target, per-bucket rollups.
/// </summary>
/// <remarks>
/// Design guarantees (all enforced here):
/// <list type="bullet">
///   <item>Not configured (no URL/token) or malformed URL: the loop logs one
///         debug line and exits. It is a pure no-op, never an error.</item>
///   <item>Network or remote down: the attempt is skipped and retried on the
///         next interval. Nothing is thrown.</item>
///   <item>The remote schema is created once per process run (not on every
///         batch), then only rollup rows are shipped.</item>
///   <item>A local checkpoint (the next un-synced bucket start) is only advanced
///         after the remote confirms success, so nothing is lost or double-sent
///         across restarts. Upserts make re-sends idempotent regardless.</item>
///   <item>No failure in this service can interrupt network monitoring - it runs
///         independently and swallows all non-cancellation exceptions.</item>
/// </list>
///
/// WHY ROLLUPS: at dozens of targets on a few-second cadence, raw per-cycle rows
/// are hundreds of thousands per day - millions of remote row-writes per month,
/// which permanently outruns any reasonable sync budget. A rollup collapses a
/// whole bucket of cycles for one target into one row, so the remote receives at
/// most (number of targets) rows per bucket per machine. Only fully-elapsed
/// buckets are shipped; the current (open) bucket is held back until it closes.
///
/// Rows are tagged with <see cref="Environment.MachineName"/> so several machines
/// can safely share one remote database.
/// </remarks>
public sealed class RemoteSyncService : BackgroundService
{
    private const string CheckpointKey = "remote_rollup_next_bucket_ms";

    // 15 columns per rollup row; 50 rows keeps us to ~750 bound parameters,
    // comfortably under the conservative 999-parameter limit older SQLite
    // builds enforce.
    private const int RollupsPerInsertStatement = 50;

    private readonly IRemoteDatabaseClient _client;
    private readonly IStorageService _storage;
    private readonly RemoteSyncOptions _options;
    private readonly ILogger<RemoteSyncService> _logger;
    private readonly string _machine;
    private readonly string _table;
    private readonly int _bucketMinutes;
    private readonly long _bucketMs;

    private bool _schemaEnsured;
    private bool _firstSyncLogged;

    public RemoteSyncService(
        IRemoteDatabaseClient client,
        IStorageService storage,
        IOptions<RemoteSyncOptions> options,
        ILogger<RemoteSyncService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _client = client;
        _storage = storage;
        _options = options.Value;
        _logger = logger;
        _machine = Environment.MachineName;
        _table = SanitizeTableName(_options.TableName);
        _bucketMinutes = Math.Max(1, _options.BucketMinutes);
        _bucketMs = _bucketMinutes * 60_000L;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.IsConfigured || !_client.IsConfigured)
        {
            // Feature is opt-in. Say so once at debug level and stop cleanly.
            _logger.LogDebug("Remote sync is not configured; the feature is disabled.");
            return;
        }

        _logger.LogInformation(
            "Remote sync enabled (every {Minutes} min, {Bucket}-min rollups) to remote table '{Table}'.",
            Math.Max(5, _options.SyncIntervalMinutes),
            _bucketMinutes,
            _table);

        try
        {
            await Task.Delay(
                TimeSpan.FromSeconds(Math.Max(0, _options.InitialDelaySeconds)),
                stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var interval = TimeSpan.FromMinutes(Math.Max(5, _options.SyncIntervalMinutes));
        using var timer = new PeriodicTimer(interval);

        // Run once shortly after startup (so machines that reboot regularly still
        // sync), then once per interval thereafter.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SyncOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Never let a sync failure crash the host or affect monitoring.
                _logger.LogDebug(ex, "Remote sync attempt failed; will retry next interval.");
            }

            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken))
                {
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Performs a single sync pass: computes rollups for fully-elapsed buckets
    /// that have not been replicated yet and pushes them to the remote database,
    /// advancing the checkpoint only on confirmed success. Public to allow direct
    /// testing.
    /// </summary>
    /// <returns>The number of rollup rows successfully pushed in this pass.</returns>
    public async Task<int> SyncOnceAsync(CancellationToken cancellationToken)
    {
        if (!_options.IsConfigured || !_client.IsConfigured)
        {
            return 0;
        }

        // Ensure the remote schema exists exactly once per process run, before
        // shipping any data. If this fails, retry it on the next interval.
        if (!_schemaEnsured)
        {
            if (!await EnsureRemoteSchemaAsync(cancellationToken))
            {
                return 0;
            }

            _schemaEnsured = true;
        }

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var currentBucketStart = nowMs - (nowMs % _bucketMs);

        var checkpoint = await ReadCheckpointAsync(cancellationToken);
        var batchSize = Math.Clamp(_options.BatchSize, 1, 5000);
        var maxRows = Math.Max(batchSize, _options.MaxRowsPerSync);
        var totalSynced = 0;

        while (totalSynced < maxRows && !cancellationToken.IsCancellationRequested)
        {
            var from = checkpoint;
            var to = currentBucketStart;

            if (from >= to)
            {
                break; // No fully-elapsed, un-synced buckets remain.
            }

            IReadOnlyList<CheckRollup> rows;
            try
            {
                // Fetch one more than the batch so we can detect truncation.
                rows = await _storage.GetRollupsAsync(from, to, _bucketMinutes, batchSize + 1, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Remote sync could not read local rollups.");
                return totalSynced;
            }

            if (rows.Count == 0)
            {
                // Nothing in this range: skip past the empty history so we don't
                // rescan it next time. Cycles are monotonic in time, so no older
                // data will arrive for [from, to) later.
                checkpoint = to;
                await WriteCheckpointAsync(checkpoint, cancellationToken);
                break;
            }

            var truncated = rows.Count > batchSize;
            var page = truncated ? rows.Take(batchSize).ToList() : rows;

            List<CheckRollup> toPush;
            long newCheckpoint;

            if (truncated)
            {
                // The last bucket in a truncated page may be incomplete. Push only
                // whole buckets: everything strictly before the last bucket start.
                var lastBucket = page[^1].BucketStartMs;
                var completeThrough = page
                    .Where(r => r.BucketStartMs < lastBucket)
                    .Select(r => r.BucketStartMs)
                    .DefaultIfEmpty(long.MinValue)
                    .Max();

                if (completeThrough == long.MinValue)
                {
                    // A single bucket alone exceeded the batch (never happens while
                    // targets-per-bucket < batch size). Push it whole to progress.
                    toPush = page.ToList();
                    newCheckpoint = lastBucket + _bucketMs;
                }
                else
                {
                    toPush = page.Where(r => r.BucketStartMs <= completeThrough).ToList();
                    newCheckpoint = completeThrough + _bucketMs;
                }
            }
            else
            {
                toPush = page.ToList();
                newCheckpoint = page[^1].BucketStartMs + _bucketMs;
            }

            var statements = BuildInsertStatements(toPush);
            var ok = await _client.ExecutePipelineAsync(statements, cancellationToken);
            if (!ok)
            {
                // Leave the checkpoint untouched so the same buckets retry next time.
                return totalSynced;
            }

            checkpoint = newCheckpoint;
            await WriteCheckpointAsync(checkpoint, cancellationToken);
            totalSynced += toPush.Count;

            if (!truncated)
            {
                break; // Backlog drained.
            }
        }

        if (totalSynced > 0)
        {
            if (!_firstSyncLogged)
            {
                _logger.LogInformation(
                    "Remote sync active: pushed {Count} rollup row(s) to the remote database.",
                    totalSynced);
                _firstSyncLogged = true;
            }
            else
            {
                _logger.LogDebug("Remote sync pushed {Count} rollup row(s).", totalSynced);
            }
        }

        return totalSynced;
    }

    private async Task<bool> EnsureRemoteSchemaAsync(CancellationToken cancellationToken)
    {
        var noArgs = Array.Empty<object?>();
        var statements = new List<HranaStatement>
        {
            new(
                $"CREATE TABLE IF NOT EXISTS {_table} (" +
                "machine TEXT NOT NULL, " +
                "bucket_start INTEGER NOT NULL, " +
                "target_name TEXT NOT NULL, " +
                "target_address TEXT NOT NULL, " +
                "target_category TEXT NOT NULL, " +
                "samples INTEGER NOT NULL, " +
                "ok INTEGER NOT NULL, " +
                "avg_rtt_ms REAL, " +
                "min_rtt_ms INTEGER, " +
                "max_rtt_ms INTEGER, " +
                "avg_jitter_ms REAL, " +
                "avg_dns_ms REAL, " +
                "avg_loss_pct REAL NOT NULL, " +
                "bucket_minutes INTEGER NOT NULL, " +
                "synced_at INTEGER NOT NULL, " +
                "PRIMARY KEY (machine, target_address, bucket_start))",
                noArgs),
            new(
                $"CREATE INDEX IF NOT EXISTS idx_{_table}_bucket ON {_table}(bucket_start)",
                noArgs),
        };

        try
        {
            return await _client.ExecutePipelineAsync(statements, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Remote sync could not ensure the remote schema.");
            return false;
        }
    }

    private async Task<long> ReadCheckpointAsync(CancellationToken cancellationToken)
    {
        try
        {
            var raw = await _storage.GetSyncStateAsync(CheckpointKey, cancellationToken);
            if (!string.IsNullOrWhiteSpace(raw) &&
                long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read remote sync checkpoint; starting from 0.");
        }

        return 0;
    }

    private async Task WriteCheckpointAsync(long value, CancellationToken cancellationToken)
    {
        try
        {
            await _storage.SetSyncStateAsync(
                CheckpointKey,
                value.ToString(CultureInfo.InvariantCulture),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not persist remote sync checkpoint.");
        }
    }

    private List<HranaStatement> BuildInsertStatements(IReadOnlyList<CheckRollup> rows)
    {
        var statements = new List<HranaStatement>();
        var syncedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        for (var offset = 0; offset < rows.Count; offset += RollupsPerInsertStatement)
        {
            var count = Math.Min(RollupsPerInsertStatement, rows.Count - offset);
            var valuesSql = new StringBuilder();
            var args = new List<object?>(count * 15);

            for (var i = 0; i < count; i++)
            {
                if (i > 0)
                {
                    valuesSql.Append(", ");
                }

                valuesSql.Append("(?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)");

                var row = rows[offset + i];
                args.Add(_machine);
                args.Add(row.BucketStartMs);
                args.Add(row.TargetName);
                args.Add(row.TargetAddress);
                args.Add(row.TargetCategory);
                args.Add((long)row.Samples);
                args.Add((long)row.Ok);
                args.Add(row.AvgRttMs);
                args.Add(row.MinRttMs);
                args.Add(row.MaxRttMs);
                args.Add(row.AvgJitterMs);
                args.Add(row.AvgDnsMs);
                args.Add(row.AvgLossPct);
                args.Add((long)row.BucketMinutes);
                args.Add(syncedAt);
            }

            var sql =
                $"INSERT OR REPLACE INTO {_table} " +
                "(machine, bucket_start, target_name, target_address, target_category, " +
                "samples, ok, avg_rtt_ms, min_rtt_ms, max_rtt_ms, avg_jitter_ms, avg_dns_ms, " +
                "avg_loss_pct, bucket_minutes, synced_at) " +
                $"VALUES {valuesSql}";

            statements.Add(new HranaStatement(sql, args));
        }

        return statements;
    }

    /// <summary>
    /// Reduces a configured table name to a safe SQL identifier. Falls back to
    /// <c>check_rollups</c> when the input is empty or would start with a digit.
    /// </summary>
    private static string SanitizeTableName(string? name)
    {
        const string fallback = "check_rollups";

        if (string.IsNullOrWhiteSpace(name))
        {
            return fallback;
        }

        var builder = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            if (char.IsLetterOrDigit(ch) || ch == '_')
            {
                builder.Append(ch);
            }
        }

        var cleaned = builder.ToString();
        if (cleaned.Length == 0 || char.IsDigit(cleaned[0]))
        {
            return fallback;
        }

        return cleaned;
    }
}
