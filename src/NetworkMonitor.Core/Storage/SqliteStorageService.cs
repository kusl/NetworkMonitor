using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetworkMonitor.Core.Models;

namespace NetworkMonitor.Core.Storage;

/// <summary>
/// SQLite-based storage for network monitoring data using a normalized schema.
///
/// SCHEMA (three tables + a small key/value store):
///
///   targets(id, name, address, category)         -- dimension; each target stored ONCE
///   monitor_cycles(id, ts_ms, health, message)   -- one row per monitoring cycle
///   check_results(cycle_id, target_id, ...)       -- one measurement per target per cycle
///   sync_state(key, value)                        -- replication checkpoint etc.
///
/// WHY THIS SHAPE (vs. the previous flat ping_results table):
///
///   * No string duplication. Hostnames, friendly names, and categories live in
///     targets and are referenced by integer id from every measurement, instead
///     of repeating "teams.microsoft.com"/"MS-Teams"/"custom" on every row.
///   * Relational integrity. Every measurement links to the exact cycle that
///     produced it via cycle_id, and all measurements in a cycle share ONE
///     timestamp (the cycle's), so cycle/measurement joins are exact.
///   * Compact types. Timestamps are 64-bit Unix milliseconds (integers), not
///     33-character ISO-8601 strings; packet loss is an integer percent; health
///     is the enum's integer value.
///   * Full fidelity preserved. DNS resolution time, the pinged IP, and the
///     intra-burst min/max/jitter are stored per cycle - detail the old schema
///     silently dropped.
///
/// DESIGN NOTES:
///
///   * Every connection is opened through <see cref="OpenConnectionAsync"/>,
///     which enables WAL journalling, NORMAL synchronous mode, and a busy
///     timeout, so a reader (trendline/rollup query) can run concurrently with
///     the writer instead of hitting "database is locked".
///   * Target ids are cached in memory after first use, so steady-state writes
///     are just one cycle row plus the per-target measurement rows - no target
///     lookups.
///   * Storage failures never propagate: <see cref="SaveStatusAsync"/> swallows
///     and logs exceptions so a disk hiccup can never interrupt monitoring.
///   * On first initialization the incompatible legacy tables (ping_results,
///     network_status) from earlier versions are dropped, so the file upgrades
///     in place. Legacy per-cycle history is discarded (it is telemetry).
/// </summary>
public sealed class SqliteStorageService : IStorageService, IAsyncDisposable
{
    private readonly StorageOptions _options;
    private readonly ILogger<SqliteStorageService> _logger;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly ConcurrentDictionary<string, long> _targetIdCache = new(StringComparer.Ordinal);
    private bool _initialized;

    /// <summary>
    /// Creates a new SQLite storage service.
    /// </summary>
    public SqliteStorageService(
        IOptions<StorageOptions> options,
        ILogger<SqliteStorageService> logger)
    {
        _options = options.Value;
        _logger = logger;

        var dataDir = _options.GetDataDirectory();
        Directory.CreateDirectory(dataDir);

        var fileName = string.IsNullOrWhiteSpace(_options.DatabaseFileName)
            ? "network-monitor.db"
            : _options.DatabaseFileName;

        var dbPath = Path.Combine(dataDir, fileName);
        _connectionString = $"Data Source={dbPath}";

        _logger.LogInformation("SQLite database path: {DbPath}", dbPath);
    }

    /// <summary>
    /// Opens a connection and applies the per-connection pragmas we rely on.
    /// WAL is persisted in the database header, but synchronous and busy_timeout
    /// are per-connection and must be set every time.
    /// </summary>
    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var pragma = connection.CreateCommand();
        pragma.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            PRAGMA busy_timeout=5000;
            PRAGMA temp_store=MEMORY;
            """;
        await pragma.ExecuteNonQueryAsync(cancellationToken);

        return connection;
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized) return;

        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized) return;

            await using var connection = await OpenConnectionAsync(cancellationToken);

            // auto_vacuum only takes effect for a brand-new database, and only if
            // set before any table exists. Harmless (a no-op) on an existing file.
            await using (var av = connection.CreateCommand())
            {
                av.CommandText = "PRAGMA auto_vacuum=INCREMENTAL;";
                await av.ExecuteNonQueryAsync(cancellationToken);
            }

            const string createSql = """
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
                """;

            await using (var create = connection.CreateCommand())
            {
                create.CommandText = createSql;
                await create.ExecuteNonQueryAsync(cancellationToken);
            }

            // Drop the incompatible flat tables from earlier versions and the
            // stale checkpoint key they used. This upgrades the file in place.
            await using (var cleanup = connection.CreateCommand())
            {
                cleanup.CommandText = """
                    DROP TABLE IF EXISTS ping_results;
                    DROP TABLE IF EXISTS network_status;
                    DELETE FROM sync_state WHERE key = 'remote_last_synced_id';
                    """;
                await cleanup.ExecuteNonQueryAsync(cancellationToken);
            }

            _logger.LogDebug("Normalized database schema initialized");
            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task SaveStatusAsync(NetworkStatus status, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(status);

        try
        {
            await EnsureInitializedAsync(cancellationToken);

            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

            long cycleId;
            await using (var cycleCmd = connection.CreateCommand())
            {
                cycleCmd.Transaction = transaction;
                cycleCmd.CommandText = """
                    INSERT INTO monitor_cycles (ts_ms, health, message)
                    VALUES (@ts, @health, @message)
                    RETURNING id
                    """;
                cycleCmd.Parameters.AddWithValue("@ts", status.Timestamp.ToUnixTimeMilliseconds());
                cycleCmd.Parameters.AddWithValue("@health", (int)status.Health);
                cycleCmd.Parameters.AddWithValue("@message", status.Message);

                var scalar = await cycleCmd.ExecuteScalarAsync(cancellationToken);
                cycleId = Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
            }

            if (status.TargetResults is { Count: > 0 })
            {
                foreach (var result in status.TargetResults)
                {
                    await SaveTargetCheckAsync(connection, transaction, cycleId, result, cancellationToken);
                }
            }
            else
            {
                // Snapshot without a per-target breakdown: persist the router and
                // internet results so nothing is lost.
                if (status.RouterResult is not null)
                {
                    await SavePingRowAsync(connection, transaction, cycleId,
                        name: "Router", address: status.RouterResult.Target,
                        category: TargetCategory.Router, ping: status.RouterResult,
                        packetLossPercent: 0, minMs: null, maxMs: null, jitterMs: null,
                        dnsMs: null, resolvedIp: null, cancellationToken);
                }

                if (status.InternetResult is not null)
                {
                    await SavePingRowAsync(connection, transaction, cycleId,
                        name: "Internet", address: status.InternetResult.Target,
                        category: TargetCategory.PublicDns, ping: status.InternetResult,
                        packetLossPercent: 0, minMs: null, maxMs: null, jitterMs: null,
                        dnsMs: null, resolvedIp: null, cancellationToken);
                }
            }

            await transaction.CommitAsync(cancellationToken);

            // Periodically prune old data. Each cycle writes many rows, so keep
            // the cadence low (~1 in 200 saves).
            if (Random.Shared.Next(200) == 0)
            {
                await PruneOldDataAsync(connection, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            // Log but don't throw - storage failures must never stop monitoring.
            _logger.LogWarning(ex, "Failed to save status to SQLite");
        }
    }

    private async Task SaveTargetCheckAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long cycleId,
        TargetCheckResult result,
        CancellationToken cancellationToken)
    {
        // The row represents the aggregate outcome for this target this cycle.
        // Prefer the IPv4 ping; fall back to IPv6 if that is all we have.
        var ping = result.PingResult ?? result.PingResultV6;

        // DNS time is recorded whenever a DNS check ran, regardless of outcome.
        long? dnsMs = result.DnsResult?.ResolutionTimeMs;

        await SavePingRowAsync(
            connection,
            transaction,
            cycleId,
            name: result.Target.Name,
            address: result.Target.Address,
            category: result.Target.Category,
            ping: ping,
            packetLossPercent: result.PacketLossPercent,
            minMs: result.MinLatencyMs,
            maxMs: result.MaxLatencyMs,
            jitterMs: result.JitterMs,
            dnsMs: dnsMs,
            resolvedIp: result.ResolvedAddress,
            cancellationToken: cancellationToken,
            dnsError: result.DnsResult is { Success: false } dns ? dns.ErrorMessage : null);
    }

    private async Task SavePingRowAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long cycleId,
        string name,
        string address,
        TargetCategory category,
        PingResult? ping,
        double packetLossPercent,
        long? minMs,
        long? maxMs,
        long? jitterMs,
        long? dnsMs,
        string? resolvedIp,
        CancellationToken cancellationToken,
        string? dnsError = null)
    {
        var targetId = await GetOrCreateTargetIdAsync(connection, transaction, name, address, category, cancellationToken);

        var success = ping?.Success == true;
        long? roundtrip = success ? ping?.RoundtripTimeMs : null;
        var lossPct = ClampLoss(packetLossPercent);
        var error = ping?.ErrorMessage ?? dnsError;

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR REPLACE INTO check_results
                (cycle_id, target_id, success, rtt_ms, rtt_min_ms, rtt_max_ms, jitter_ms, dns_ms, loss_pct, resolved_ip, error_message)
            VALUES
                (@cycle, @target, @success, @rtt, @min, @max, @jitter, @dns, @loss, @resolved, @error)
            """;

        command.Parameters.AddWithValue("@cycle", cycleId);
        command.Parameters.AddWithValue("@target", targetId);
        command.Parameters.AddWithValue("@success", success ? 1 : 0);
        command.Parameters.AddWithValue("@rtt", (object?)roundtrip ?? DBNull.Value);
        command.Parameters.AddWithValue("@min", (object?)(success ? minMs : null) ?? DBNull.Value);
        command.Parameters.AddWithValue("@max", (object?)(success ? maxMs : null) ?? DBNull.Value);
        command.Parameters.AddWithValue("@jitter", (object?)(success ? jitterMs : null) ?? DBNull.Value);
        command.Parameters.AddWithValue("@dns", (object?)dnsMs ?? DBNull.Value);
        command.Parameters.AddWithValue("@loss", lossPct);
        command.Parameters.AddWithValue("@resolved", (object?)resolvedIp ?? DBNull.Value);
        command.Parameters.AddWithValue("@error", (object?)error ?? DBNull.Value);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<long> GetOrCreateTargetIdAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string name,
        string address,
        TargetCategory category,
        CancellationToken cancellationToken)
    {
        var categoryText = MapCategory(category);
        var key = string.Concat(categoryText, "\u0001", address);

        if (_targetIdCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT OR IGNORE INTO targets (name, address, category)
                VALUES (@name, @address, @category)
                """;
            insert.Parameters.AddWithValue("@name", name);
            insert.Parameters.AddWithValue("@address", address);
            insert.Parameters.AddWithValue("@category", categoryText);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        long id;
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT id FROM targets WHERE address = @address AND category = @category LIMIT 1";
            select.Parameters.AddWithValue("@address", address);
            select.Parameters.AddWithValue("@category", categoryText);
            var scalar = await select.ExecuteScalarAsync(cancellationToken);
            id = Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
        }

        _targetIdCache.TryAdd(key, id);
        return id;
    }

    private static int ClampLoss(double packetLossPercent)
    {
        var rounded = (int)Math.Round(packetLossPercent, MidpointRounding.AwayFromZero);
        return Math.Clamp(rounded, 0, 100);
    }

    private static string MapCategory(TargetCategory category) => category switch
    {
        TargetCategory.Router => "router",
        TargetCategory.PublicDns => "internet",
        TargetCategory.Service => "service",
        TargetCategory.Custom => "custom",
        _ => "custom"
    };

    private async Task PruneOldDataAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var cutoffMs = DateTimeOffset.UtcNow.AddDays(-_options.RetentionDays).ToUnixTimeMilliseconds();

        int deleted;
        await using (var command = connection.CreateCommand())
        {
            // Delete measurements for old cycles first (no FK cascade is enabled,
            // so both tables are pruned explicitly), then the cycles themselves.
            command.CommandText = """
                DELETE FROM check_results
                    WHERE cycle_id IN (SELECT id FROM monitor_cycles WHERE ts_ms < @cutoff);
                DELETE FROM monitor_cycles WHERE ts_ms < @cutoff;
                """;
            command.Parameters.AddWithValue("@cutoff", cutoffMs);
            deleted = await command.ExecuteNonQueryAsync(cancellationToken);
        }

        // Reclaim free pages incrementally (effective on databases created with
        // auto_vacuum=INCREMENTAL; a harmless no-op otherwise).
        await using (var vacuum = connection.CreateCommand())
        {
            vacuum.CommandText = "PRAGMA incremental_vacuum;";
            await vacuum.ExecuteNonQueryAsync(cancellationToken);
        }

        if (deleted > 0)
        {
            _logger.LogDebug("Pruned {Count} old cycle/measurement rows", deleted);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<HistoricalData>> GetHistoricalDataAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        TimeGranularity granularity,
        string? targetAddress = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        var bucketMs = BucketMsFor(granularity);
        var filterByTarget = !string.IsNullOrWhiteSpace(targetAddress);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        var targetJoin = filterByTarget ? "JOIN targets t ON t.id = cr.target_id" : string.Empty;
        var targetFilter = filterByTarget ? "AND t.address = @addr" : string.Empty;

        command.CommandText = $"""
            SELECT
                (mc.ts_ms - (mc.ts_ms % @bucket)) AS bucket,
                AVG(CASE WHEN cr.success = 1 THEN cr.rtt_ms END)     AS avg_rtt,
                MIN(CASE WHEN cr.success = 1 THEN cr.rtt_min_ms END) AS min_rtt,
                MAX(CASE WHEN cr.success = 1 THEN cr.rtt_max_ms END) AS max_rtt,
                COUNT(*)                                             AS samples,
                SUM(CASE WHEN cr.success = 1 THEN 0 ELSE 1 END)      AS failures
            FROM check_results cr
            JOIN monitor_cycles mc ON mc.id = cr.cycle_id
            {targetJoin}
            WHERE mc.ts_ms >= @from AND mc.ts_ms <= @to {targetFilter}
            GROUP BY bucket
            ORDER BY bucket
            """;

        command.Parameters.AddWithValue("@bucket", bucketMs);
        command.Parameters.AddWithValue("@from", from.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("@to", to.ToUnixTimeMilliseconds());
        if (filterByTarget)
        {
            command.Parameters.AddWithValue("@addr", targetAddress!);
        }

        var results = new List<HistoricalData>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var bucket = reader.GetInt64(0);
            var avg = reader.IsDBNull(1) ? 0d : reader.GetDouble(1);
            var min = reader.IsDBNull(2) ? 0L : reader.GetInt64(2);
            var max = reader.IsDBNull(3) ? 0L : reader.GetInt64(3);
            var samples = reader.IsDBNull(4) ? 0 : (int)reader.GetInt64(4);
            var failures = reader.IsDBNull(5) ? 0 : (int)reader.GetInt64(5);

            var loss = samples > 0 ? (double)failures / samples * 100 : 0;

            results.Add(new HistoricalData(
                Period: DateTimeOffset.FromUnixTimeMilliseconds(bucket),
                AverageLatencyMs: avg,
                MinLatencyMs: min,
                MaxLatencyMs: max,
                PacketLossPercent: loss,
                SampleCount: samples));
        }

        return results;
    }

    private static long BucketMsFor(TimeGranularity granularity) => granularity switch
    {
        TimeGranularity.Minute => 60_000L,
        TimeGranularity.Hour => 3_600_000L,
        TimeGranularity.Day => 86_400_000L,
        _ => 60_000L
    };

    /// <inheritdoc />
    public async Task<IReadOnlyList<PingResult>> GetRecentPingsAsync(
        int count,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT t.address, cr.success, cr.rtt_ms, mc.ts_ms, cr.error_message
            FROM check_results cr
            JOIN monitor_cycles mc ON mc.id = cr.cycle_id
            JOIN targets t ON t.id = cr.target_id
            ORDER BY mc.ts_ms DESC, cr.rowid DESC
            LIMIT @count
            """;
        command.Parameters.AddWithValue("@count", count);

        var results = new List<PingResult>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new PingResult(
                Target: reader.GetString(0),
                Success: reader.GetInt32(1) == 1,
                RoundtripTimeMs: reader.IsDBNull(2) ? null : reader.GetInt64(2),
                Timestamp: DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)),
                ErrorMessage: reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        return results;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CheckRollup>> GetRollupsAsync(
        long fromBucketStartMsInclusive,
        long toExclusiveMs,
        int bucketMinutes,
        int limit,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        var bucketMs = Math.Max(1, bucketMinutes) * 60_000L;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                (mc.ts_ms - (mc.ts_ms % @bucket))                   AS bucket,
                t.name, t.address, t.category,
                COUNT(*)                                            AS samples,
                SUM(cr.success)                                     AS ok,
                AVG(CASE WHEN cr.success = 1 THEN cr.rtt_ms END)     AS avg_rtt,
                MIN(CASE WHEN cr.success = 1 THEN cr.rtt_min_ms END) AS min_rtt,
                MAX(CASE WHEN cr.success = 1 THEN cr.rtt_max_ms END) AS max_rtt,
                AVG(cr.jitter_ms)                                   AS avg_jitter,
                AVG(cr.dns_ms)                                      AS avg_dns,
                AVG(cr.loss_pct)                                    AS avg_loss
            FROM check_results cr
            JOIN monitor_cycles mc ON mc.id = cr.cycle_id
            JOIN targets t ON t.id = cr.target_id
            WHERE mc.ts_ms >= @from AND mc.ts_ms < @to
            GROUP BY bucket, t.id
            ORDER BY bucket, t.id
            LIMIT @limit
            """;

        command.Parameters.AddWithValue("@bucket", bucketMs);
        command.Parameters.AddWithValue("@from", fromBucketStartMsInclusive);
        command.Parameters.AddWithValue("@to", toExclusiveMs);
        command.Parameters.AddWithValue("@limit", limit);

        var results = new List<CheckRollup>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new CheckRollup(
                BucketStartMs: reader.GetInt64(0),
                BucketMinutes: Math.Max(1, bucketMinutes),
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

        return results;
    }

    /// <inheritdoc />
    public async Task<string?> GetSyncStateAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        await EnsureInitializedAsync(cancellationToken);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM sync_state WHERE key = @key LIMIT 1";
        command.Parameters.AddWithValue("@key", key);

        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value as string;
    }

    /// <inheritdoc />
    public async Task SetSyncStateAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(value);

        await EnsureInitializedAsync(cancellationToken);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sync_state (key, value)
            VALUES (@key, @value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value
            """;
        command.Parameters.AddWithValue("@key", key);
        command.Parameters.AddWithValue("@value", value);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _initLock.Dispose();
        return ValueTask.CompletedTask;
    }
}
