using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetworkMonitor.Core.Configuration;
using NetworkMonitor.Core.Models;

namespace NetworkMonitor.Core.Storage;

public interface IStatusStore
{
    Task SaveAsync(NetworkStatus status, CancellationToken cancellationToken);
}

public interface IRollupStore
{
    Task<IReadOnlyList<CheckRollup>> GetRollupsAsync(
        long fromBucketStartMsInclusive,
        long toExclusiveMs,
        int bucketMinutes,
        int limit,
        CancellationToken cancellationToken);

    Task<string?> GetSyncStateAsync(string key, CancellationToken cancellationToken);

    Task SetSyncStateAsync(string key, string value, CancellationToken cancellationToken);
}

public sealed class SqliteStore : IStatusStore, IRollupStore, IDisposable
{
    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS targets (
            id       INTEGER PRIMARY KEY AUTOINCREMENT,
            name     TEXT NOT NULL,
            address  TEXT NOT NULL,
            category TEXT NOT NULL,
            UNIQUE (address, category)
        );
        CREATE TABLE IF NOT EXISTS monitor_cycles (
            id      INTEGER PRIMARY KEY AUTOINCREMENT,
            ts_ms   INTEGER NOT NULL,
            health  INTEGER NOT NULL,
            message TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_monitor_cycles_ts
            ON monitor_cycles(ts_ms);
        CREATE TABLE IF NOT EXISTS check_results (
            cycle_id      INTEGER NOT NULL REFERENCES monitor_cycles(id),
            target_id     INTEGER NOT NULL REFERENCES targets(id),
            success       INTEGER NOT NULL,
            rtt_ms        INTEGER,
            rtt_min_ms    INTEGER,
            rtt_max_ms    INTEGER,
            jitter_ms     INTEGER,
            dns_ms        INTEGER,
            loss_pct      INTEGER NOT NULL,
            resolved_ip   TEXT,
            error_message TEXT,
            PRIMARY KEY (cycle_id, target_id)
        );
        CREATE INDEX IF NOT EXISTS idx_check_results_target
            ON check_results(target_id, cycle_id);
        CREATE TABLE IF NOT EXISTS sync_state (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );
        DROP TABLE IF EXISTS ping_results;
        DROP TABLE IF EXISTS network_status;
        DELETE FROM sync_state WHERE key = 'remote_last_synced_id';
        """;

    private readonly StorageOptions _options;
    private readonly ILogger<SqliteStore> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly ConcurrentDictionary<(string Category, string Address), long> _targetIds = new();
    private int _saveCount;
    private volatile bool _initialized;

    public SqliteStore(IOptions<StorageOptions> options, TimeProvider timeProvider, ILogger<SqliteStore> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;

        var directory = DataDirectory.Resolve(_options);
        Directory.CreateDirectory(directory);
        var fileName = string.IsNullOrWhiteSpace(_options.DatabaseFileName)
            ? StorageOptions.DefaultDatabaseFileName
            : _options.DatabaseFileName;
        DatabasePath = Path.Combine(directory, fileName);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = DatabasePath }.ToString();
        _logger.LogInformation("SQLite database path: {DbPath}", DatabasePath);
    }

    public string DatabasePath { get; }

    public async Task SaveAsync(NetworkStatus status, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(status);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

            await using var cycle = connection.Command(
                transaction,
                "INSERT INTO monitor_cycles (ts_ms, health, message) VALUES (@ts, @health, @message) RETURNING id",
                ("@ts", status.Timestamp.ToUnixTimeMilliseconds()),
                ("@health", (int)status.Health),
                ("@message", status.Message));
            var cycleId = Convert.ToInt64(await cycle.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);

            foreach (var result in status.Targets)
            {
                await InsertCheckAsync(connection, transaction, cycleId, result, cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);

            if (_options.PruneEveryNSaves > 0 && Interlocked.Increment(ref _saveCount) % _options.PruneEveryNSaves == 0)
            {
                await PruneAsync(connection, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save status to SQLite");
        }
    }

    public async Task<IReadOnlyList<CheckRollup>> GetRollupsAsync(
        long fromBucketStartMsInclusive,
        long toExclusiveMs,
        int bucketMinutes,
        int limit,
        CancellationToken cancellationToken)
    {
        var minutes = Math.Max(1, bucketMinutes);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.Command(
            null,
            """
            SELECT
                (mc.ts_ms - (mc.ts_ms % @bucket))                    AS bucket,
                t.name, t.address, t.category,
                COUNT(*)                                             AS samples,
                SUM(cr.success)                                      AS ok,
                AVG(CASE WHEN cr.success = 1 THEN cr.rtt_ms END)     AS avg_rtt,
                MIN(CASE WHEN cr.success = 1 THEN cr.rtt_min_ms END) AS min_rtt,
                MAX(CASE WHEN cr.success = 1 THEN cr.rtt_max_ms END) AS max_rtt,
                AVG(cr.jitter_ms)                                    AS avg_jitter,
                AVG(cr.dns_ms)                                       AS avg_dns,
                AVG(cr.loss_pct)                                     AS avg_loss
            FROM check_results cr
            JOIN monitor_cycles mc ON mc.id = cr.cycle_id
            JOIN targets t ON t.id = cr.target_id
            WHERE mc.ts_ms >= @from AND mc.ts_ms < @to
            GROUP BY bucket, t.id
            ORDER BY bucket, t.id
            LIMIT @limit
            """,
            ("@bucket", minutes * 60_000L),
            ("@from", fromBucketStartMsInclusive),
            ("@to", toExclusiveMs),
            ("@limit", limit));

        List<CheckRollup> rollups = [];
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rollups.Add(new CheckRollup(
                BucketStartMs: reader.GetInt64(0),
                BucketMinutes: minutes,
                TargetName: reader.GetString(1),
                TargetAddress: reader.GetString(2),
                TargetCategory: reader.GetString(3),
                Samples: (int)reader.GetInt64(4),
                Ok: reader.IsDBNull(5) ? 0 : (int)reader.GetInt64(5),
                AvgRttMs: reader.IsDBNull(6) ? null : reader.GetDouble(6),
                MinRttMs: reader.IsDBNull(7) ? null : reader.GetInt64(7),
                MaxRttMs: reader.IsDBNull(8) ? null : reader.GetInt64(8),
                AvgJitterMs: reader.IsDBNull(9) ? null : reader.GetDouble(9),
                AvgDnsMs: reader.IsDBNull(10) ? null : reader.GetDouble(10),
                AvgLossPct: reader.IsDBNull(11) ? 0 : reader.GetDouble(11)));
        }

        return rollups;
    }

    public async Task<string?> GetSyncStateAsync(string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.Command(null, "SELECT value FROM sync_state WHERE key = @key LIMIT 1", ("@key", key));
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    public async Task SetSyncStateAsync(string key, string value, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(value);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.Command(
            null,
            "INSERT INTO sync_state (key, value) VALUES (@key, @value) ON CONFLICT(key) DO UPDATE SET value = excluded.value",
            ("@key", key),
            ("@value", value));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public void Dispose() => _initLock.Dispose();

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using (var pragma = connection.Command(
                null,
                "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000; PRAGMA temp_store=MEMORY;"))
            {
                await pragma.ExecuteNonQueryAsync(cancellationToken);
            }

            await EnsureSchemaAsync(connection, cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private async Task EnsureSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
            {
                return;
            }

            await using (var vacuum = connection.Command(null, "PRAGMA auto_vacuum=INCREMENTAL;"))
            {
                await vacuum.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var schema = connection.Command(null, SchemaSql))
            {
                await schema.ExecuteNonQueryAsync(cancellationToken);
            }

            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private async Task InsertCheckAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long cycleId,
        TargetCheckResult result,
        CancellationToken cancellationToken)
    {
        var targetId = await GetTargetIdAsync(connection, transaction, result.Target, cancellationToken);
        var success = result.Success;
        var error = result.Error ?? (result.Dns is { Success: false } dns ? dns.Error : null);

        await using var command = connection.Command(
            transaction,
            """
            INSERT OR REPLACE INTO check_results
                (cycle_id, target_id, success, rtt_ms, rtt_min_ms, rtt_max_ms, jitter_ms, dns_ms, loss_pct, resolved_ip, error_message)
            VALUES
                (@cycle, @target, @success, @rtt, @min, @max, @jitter, @dns, @loss, @resolved, @error)
            """,
            ("@cycle", cycleId),
            ("@target", targetId),
            ("@success", success ? 1 : 0),
            ("@rtt", success ? result.LatencyMs : null),
            ("@min", success ? result.MinLatencyMs : null),
            ("@max", success ? result.MaxLatencyMs : null),
            ("@jitter", success ? result.JitterMs : null),
            ("@dns", result.Dns?.ElapsedMs),
            ("@loss", Math.Clamp((int)Math.Round(result.PacketLossPercent, MidpointRounding.AwayFromZero), 0, 100)),
            ("@resolved", result.ResolvedAddress),
            ("@error", error));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<long> GetTargetIdAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        MonitorTarget target,
        CancellationToken cancellationToken)
    {
        var key = (target.StorageCategory, target.Address);
        if (_targetIds.TryGetValue(key, out var cached))
        {
            return cached;
        }

        await using (var insert = connection.Command(
            transaction,
            "INSERT OR IGNORE INTO targets (name, address, category) VALUES (@name, @address, @category)",
            ("@name", target.Name),
            ("@address", target.Address),
            ("@category", target.StorageCategory)))
        {
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var select = connection.Command(
            transaction,
            "SELECT id FROM targets WHERE address = @address AND category = @category LIMIT 1",
            ("@address", target.Address),
            ("@category", target.StorageCategory));
        var id = Convert.ToInt64(await select.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        _targetIds.TryAdd(key, id);
        return id;
    }

    private async Task PruneAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var cutoffMs = _timeProvider.GetUtcNow().AddDays(-_options.RetentionDays).ToUnixTimeMilliseconds();
        await using (var delete = connection.Command(
            null,
            """
            DELETE FROM check_results
                WHERE cycle_id IN (SELECT id FROM monitor_cycles WHERE ts_ms < @cutoff);
            DELETE FROM monitor_cycles WHERE ts_ms < @cutoff;
            """,
            ("@cutoff", cutoffMs)))
        {
            var deleted = await delete.ExecuteNonQueryAsync(cancellationToken);
            if (deleted > 0)
            {
                _logger.LogDebug("Pruned {Count} old cycle/measurement rows", deleted);
            }
        }

        await using var vacuum = connection.Command(null, "PRAGMA incremental_vacuum;");
        await vacuum.ExecuteNonQueryAsync(cancellationToken);
    }
}

internal static class SqliteCommandExtensions
{
    extension(SqliteConnection connection)
    {
        public SqliteCommand Command(SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            }

            return command;
        }
    }
}
