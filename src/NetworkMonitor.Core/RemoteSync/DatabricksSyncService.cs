using System.Globalization;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetworkMonitor.Core.Models;
using NetworkMonitor.Core.Storage;

namespace NetworkMonitor.Core.RemoteSync;

/// <summary>
/// Background service that replicates local check history to a Databricks SQL
/// warehouse as compact per-target, per-bucket rollups, using the Databricks SQL
/// Statement Execution REST API.
/// </summary>
/// <remarks>
/// This is the Databricks counterpart of <see cref="RemoteSyncService"/> and is
/// intentionally structured the same way, but it is completely independent: it
/// has its own options, its own client, and - crucially - its own sync
/// checkpoint (<see cref="CheckpointKey"/>), so the two sinks advance separately
/// and one can be enabled without the other. The same design guarantees apply:
/// <list type="bullet">
///   <item>Not configured or malformed config: the loop logs one debug line and
///         exits. Pure no-op, never an error.</item>
///   <item>Network or warehouse unavailable: the attempt is skipped and retried
///         next interval. Nothing is thrown.</item>
///   <item>The remote schema is created once per process run, then only rollup
///         rows are shipped.</item>
///   <item>The checkpoint advances only after the warehouse confirms success, so
///         nothing is lost or double-counted across restarts. Rows are upserted
///         with a MERGE keyed on (machine, target_address, bucket_start), so
///         re-sends are idempotent regardless.</item>
///   <item>No failure here can interrupt network monitoring - it runs
///         independently and swallows all non-cancellation exceptions.</item>
/// </list>
/// Rows are tagged with <see cref="Environment.MachineName"/> so several machines
/// can safely share one warehouse table.
/// </remarks>
public sealed class DatabricksSyncService : BackgroundService
{
    // Distinct from the Turso checkpoint so the two sinks are independent.
    private const string CheckpointKey = "databricks_rollup_next_bucket_ms";

    // 15 columns per rollup row. 30 rows per MERGE keeps parameter counts small
    // (450 markers) - well within Databricks limits and easy on statement size.
    private const int MergeRowsPerStatement = 30;

    private static readonly string[] ColumnNames =
    {
        "machine", "bucket_start", "target_name", "target_address", "target_category",
        "samples", "ok", "avg_rtt_ms", "min_rtt_ms", "max_rtt_ms", "avg_jitter_ms",
        "avg_dns_ms", "avg_loss_pct", "bucket_minutes", "synced_at",
    };

    private readonly IDatabricksClient _client;
    private readonly IStorageService _storage;
    private readonly DatabricksSyncOptions _options;
    private readonly ILogger<DatabricksSyncService> _logger;
    private readonly string _machine;
    private readonly string _qualifiedTable;
    private readonly int _bucketMinutes;
    private readonly long _bucketMs;

    private bool _schemaEnsured;
    private bool _firstSyncLogged;

    public DatabricksSyncService(
        IDatabricksClient client,
        IStorageService storage,
        IOptions<DatabricksSyncOptions> options,
        ILogger<DatabricksSyncService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _client = client;
        _storage = storage;
        _options = options.Value;
        _logger = logger;
        _machine = Environment.MachineName;
        _bucketMinutes = Math.Max(1, _options.BucketMinutes);
        _bucketMs = _bucketMinutes * 60_000L;

        var catalog = DatabricksSqlClient.SanitizeIdentifier(_options.Catalog, "workspace");
        var schema = DatabricksSqlClient.SanitizeIdentifier(_options.Schema, "default");
        var table = DatabricksSqlClient.SanitizeIdentifier(_options.TableName, "network_monitor_rollups");
        _qualifiedTable = string.Concat(catalog, ".", schema, ".", table);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.IsConfigured || !_client.IsConfigured)
        {
            _logger.LogDebug("Databricks sync is not configured; the feature is disabled.");
            return;
        }

        _logger.LogInformation(
            "Databricks sync enabled (every {Minutes} min, {Bucket}-min rollups) to table '{Table}'.",
            Math.Max(5, _options.SyncIntervalMinutes),
            _bucketMinutes,
            _qualifiedTable);

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
                _logger.LogDebug(ex, "Databricks sync attempt failed; will retry next interval.");
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
    /// that have not been replicated yet and pushes them to Databricks, advancing
    /// the checkpoint only on confirmed success. Public to allow direct testing.
    /// </summary>
    /// <returns>The number of rollup rows successfully pushed in this pass.</returns>
    public async Task<int> SyncOnceAsync(CancellationToken cancellationToken)
    {
        if (!_options.IsConfigured || !_client.IsConfigured)
        {
            return 0;
        }

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
                rows = await _storage.GetRollupsAsync(from, to, _bucketMinutes, batchSize + 1, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Databricks sync could not read local rollups.");
                return totalSynced;
            }

            if (rows.Count == 0)
            {
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
                var lastBucket = page[^1].BucketStartMs;
                var completeThrough = page
                    .Where(r => r.BucketStartMs < lastBucket)
                    .Select(r => r.BucketStartMs)
                    .DefaultIfEmpty(long.MinValue)
                    .Max();

                if (completeThrough == long.MinValue)
                {
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

            var statements = BuildMergeStatements(toPush);
            var ok = await _client.ExecuteAsync(statements, cancellationToken);
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
                    "Databricks sync active: pushed {Count} rollup row(s) to the warehouse.",
                    totalSynced);
                _firstSyncLogged = true;
            }
            else
            {
                _logger.LogDebug("Databricks sync pushed {Count} rollup row(s).", totalSynced);
            }
        }

        return totalSynced;
    }

    private async Task<bool> EnsureRemoteSchemaAsync(CancellationToken cancellationToken)
    {
        var ddl =
            $"CREATE TABLE IF NOT EXISTS {_qualifiedTable} (" +
            "machine STRING NOT NULL, " +
            "bucket_start BIGINT NOT NULL, " +
            "target_name STRING NOT NULL, " +
            "target_address STRING NOT NULL, " +
            "target_category STRING NOT NULL, " +
            "samples BIGINT NOT NULL, " +
            "ok BIGINT NOT NULL, " +
            "avg_rtt_ms DOUBLE, " +
            "min_rtt_ms BIGINT, " +
            "max_rtt_ms BIGINT, " +
            "avg_jitter_ms DOUBLE, " +
            "avg_dns_ms DOUBLE, " +
            "avg_loss_pct DOUBLE NOT NULL, " +
            "bucket_minutes BIGINT NOT NULL, " +
            "synced_at BIGINT NOT NULL" +
            ") USING DELTA";

        var statements = new List<DatabricksStatement>
        {
            new(ddl, Array.Empty<DatabricksParameter>()),
        };

        try
        {
            return await _client.ExecuteAsync(statements, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Databricks sync could not ensure the warehouse schema.");
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
            _logger.LogDebug(ex, "Could not read Databricks sync checkpoint; starting from 0.");
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
            _logger.LogDebug(ex, "Could not persist Databricks sync checkpoint.");
        }
    }

    /// <summary>
    /// Builds one or more idempotent MERGE statements from the rollup rows. Each
    /// statement carries at most <see cref="MergeRowsPerStatement"/> rows as a
    /// parameterized VALUES source and upserts on
    /// (machine, target_address, bucket_start).
    /// </summary>
    private List<DatabricksStatement> BuildMergeStatements(List<CheckRollup> rows)
    {
        var statements = new List<DatabricksStatement>();
        var syncedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var columnList = string.Join(", ", ColumnNames);

        for (var offset = 0; offset < rows.Count; offset += MergeRowsPerStatement)
        {
            var count = Math.Min(MergeRowsPerStatement, rows.Count - offset);
            var valuesSql = new StringBuilder();
            var parameters = new List<DatabricksParameter>(count * ColumnNames.Length);
            var marker = 0;

            for (var i = 0; i < count; i++)
            {
                if (i > 0)
                {
                    valuesSql.Append(", ");
                }

                var row = rows[offset + i];

                // One (type, value) pair per column, in ColumnNames order.
                var cells = new (string Type, string? Value)[]
                {
                    ("STRING", _machine),
                    ("BIGINT", Int(row.BucketStartMs)),
                    ("STRING", row.TargetName),
                    ("STRING", row.TargetAddress),
                    ("STRING", row.TargetCategory),
                    ("BIGINT", Int(row.Samples)),
                    ("BIGINT", Int(row.Ok)),
                    ("DOUBLE", Dbl(row.AvgRttMs)),
                    ("BIGINT", Int(row.MinRttMs)),
                    ("BIGINT", Int(row.MaxRttMs)),
                    ("DOUBLE", Dbl(row.AvgJitterMs)),
                    ("DOUBLE", Dbl(row.AvgDnsMs)),
                    ("DOUBLE", Dbl(row.AvgLossPct)),
                    ("BIGINT", Int(row.BucketMinutes)),
                    ("BIGINT", Int(syncedAt)),
                };

                valuesSql.Append('(');
                for (var c = 0; c < cells.Length; c++)
                {
                    if (c > 0)
                    {
                        valuesSql.Append(", ");
                    }

                    var name = string.Concat("p", marker.ToString(CultureInfo.InvariantCulture));
                    valuesSql.Append(':').Append(name);
                    parameters.Add(new DatabricksParameter(name, cells[c].Value, cells[c].Type));
                    marker++;
                }

                valuesSql.Append(')');
            }

            var sql =
                $"MERGE INTO {_qualifiedTable} AS t " +
                $"USING (SELECT * FROM (VALUES {valuesSql}) AS raw({columnList})) AS s " +
                "ON t.machine = s.machine AND t.target_address = s.target_address " +
                "AND t.bucket_start = s.bucket_start " +
                "WHEN MATCHED THEN UPDATE SET * " +
                "WHEN NOT MATCHED THEN INSERT *";

            statements.Add(new DatabricksStatement(sql, parameters));
        }

        return statements;
    }

    private static string Int(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string? Int(long? value) =>
        value is null ? null : value.Value.ToString(CultureInfo.InvariantCulture);

    private static string Dbl(double value) => value.ToString(CultureInfo.InvariantCulture);

    private static string? Dbl(double? value) =>
        value is null ? null : value.Value.ToString(CultureInfo.InvariantCulture);
}
