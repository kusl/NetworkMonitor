using System.Globalization;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetworkMonitor.Core.Models;
using NetworkMonitor.Core.Storage;

namespace NetworkMonitor.Core.RemoteSync;

/// <summary>
/// Background service that replicates local ping history to an optional remote
/// libSQL / Turso-compatible database.
/// </summary>
/// <remarks>
/// Design guarantees (all enforced here):
/// <list type="bullet">
///   <item>Not configured (no URL/token) or malformed URL: the loop logs one
///         debug line and exits. It is a pure no-op, never an error.</item>
///   <item>Network or remote down: the attempt is skipped and retried on the
///         next interval. Nothing is thrown.</item>
///   <item>The remote is never assumed healthy: every batch (re)creates the
///         table and index with <c>IF NOT EXISTS</c>.</item>
///   <item>A local checkpoint row id is only advanced after the remote confirms
///         success, so nothing is lost or skipped across restarts.</item>
///   <item>No failure in this service can interrupt network monitoring - it runs
///         independently and swallows all non-cancellation exceptions.</item>
/// </list>
/// Rows are tagged with <see cref="Environment.MachineName"/> so several machines
/// can safely share one remote database.
/// </remarks>
public sealed class RemoteSyncService : BackgroundService
{
    private const string CheckpointKey = "remote_last_synced_id";

    // 11 columns per row; ~50 rows keeps us at ~550 bound parameters, well under
    // the conservative 999-parameter limit older SQLite builds enforce.
    private const int RowsPerInsertStatement = 50;

    private readonly IRemoteDatabaseClient _client;
    private readonly IStorageService _storage;
    private readonly RemoteSyncOptions _options;
    private readonly ILogger<RemoteSyncService> _logger;
    private readonly string _machine;
    private readonly string _table;

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
            "Remote sync enabled (every {Minutes} min) to remote table '{Table}'.",
            Math.Max(5, _options.SyncIntervalMinutes),
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

        // Run once shortly after startup (so machines that reboot daily still
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
    /// Performs a single sync pass: pages un-synced rows out of local storage and
    /// pushes them to the remote database, advancing the checkpoint only on
    /// confirmed success. Public to allow direct testing.
    /// </summary>
    /// <returns>The number of rows successfully pushed in this pass.</returns>
    public async Task<int> SyncOnceAsync(CancellationToken cancellationToken)
    {
        if (!_options.IsConfigured || !_client.IsConfigured)
        {
            return 0;
        }

        var checkpoint = await ReadCheckpointAsync(cancellationToken);
        var batchSize = Math.Clamp(_options.BatchSize, 1, 5000);
        var maxRows = Math.Max(batchSize, _options.MaxRowsPerSync);
        var totalSynced = 0;

        while (totalSynced < maxRows && !cancellationToken.IsCancellationRequested)
        {
            var take = Math.Min(batchSize, maxRows - totalSynced);

            IReadOnlyList<StoredPingResult> rows;
            try
            {
                rows = await _storage.GetPingResultsAfterAsync(checkpoint, take, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Remote sync could not read local rows.");
                return totalSynced;
            }

            if (rows.Count == 0)
            {
                break;
            }

            var statements = BuildStatements(rows);
            var ok = await _client.ExecutePipelineAsync(statements, cancellationToken);
            if (!ok)
            {
                // Leave the checkpoint untouched so the same rows retry next time.
                return totalSynced;
            }

            checkpoint = rows[^1].Id;
            await WriteCheckpointAsync(checkpoint, cancellationToken);
            totalSynced += rows.Count;

            if (rows.Count < take)
            {
                break; // Local backlog drained.
            }
        }

        if (totalSynced > 0)
        {
            if (!_firstSyncLogged)
            {
                _logger.LogInformation(
                    "Remote sync active: pushed {Count} row(s) to the remote database.",
                    totalSynced);
                _firstSyncLogged = true;
            }
            else
            {
                _logger.LogDebug("Remote sync pushed {Count} row(s).", totalSynced);
            }
        }

        return totalSynced;
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

    private List<HranaStatement> BuildStatements(IReadOnlyList<StoredPingResult> rows)
    {
        var noArgs = Array.Empty<object?>();

        var statements = new List<HranaStatement>
        {
            new(
                $"CREATE TABLE IF NOT EXISTS {_table} (" +
                "machine TEXT NOT NULL, " +
                "id INTEGER NOT NULL, " +
                "target TEXT NOT NULL, " +
                "target_name TEXT, " +
                "target_type TEXT NOT NULL, " +
                "success INTEGER NOT NULL, " +
                "roundtrip_ms INTEGER, " +
                "packet_loss REAL NOT NULL, " +
                "timestamp TEXT NOT NULL, " +
                "error_message TEXT, " +
                "synced_at TEXT NOT NULL, " +
                "PRIMARY KEY (machine, id))",
                noArgs),
            new(
                $"CREATE INDEX IF NOT EXISTS idx_{_table}_timestamp ON {_table}(timestamp)",
                noArgs),
        };

        var syncedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

        for (var offset = 0; offset < rows.Count; offset += RowsPerInsertStatement)
        {
            var count = Math.Min(RowsPerInsertStatement, rows.Count - offset);
            var valuesSql = new StringBuilder();
            var args = new List<object?>(count * 11);

            for (var i = 0; i < count; i++)
            {
                if (i > 0)
                {
                    valuesSql.Append(", ");
                }

                valuesSql.Append("(?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)");

                var row = rows[offset + i];
                args.Add(_machine);
                args.Add(row.Id);
                args.Add(row.Target);
                args.Add(row.TargetName);
                args.Add(row.TargetType);
                args.Add(row.Success ? 1L : 0L);
                args.Add(row.RoundtripMs);
                args.Add(row.PacketLossPercent);
                args.Add(row.Timestamp.ToString("O", CultureInfo.InvariantCulture));
                args.Add(row.ErrorMessage);
                args.Add(syncedAt);
            }

            var sql =
                $"INSERT OR IGNORE INTO {_table} " +
                "(machine, id, target, target_name, target_type, success, roundtrip_ms, packet_loss, timestamp, error_message, synced_at) " +
                $"VALUES {valuesSql}";

            statements.Add(new HranaStatement(sql, args));
        }

        return statements;
    }

    /// <summary>
    /// Reduces a configured table name to a safe SQL identifier. Falls back to
    /// <c>ping_results</c> when the input is empty or would start with a digit.
    /// </summary>
    private static string SanitizeTableName(string? name)
    {
        const string fallback = "ping_results";

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
