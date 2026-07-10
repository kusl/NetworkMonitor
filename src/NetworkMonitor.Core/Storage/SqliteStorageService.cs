using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetworkMonitor.Core.Models;

namespace NetworkMonitor.Core.Storage;

/// <summary>
/// SQLite-based storage for network monitoring data.
/// Provides durable storage with efficient querying for trendlines.
///
/// Schema is automatically created/migrated on startup. Old data is
/// automatically pruned based on retention settings.
///
/// DESIGN NOTES:
///
///   * Every connection is opened through <see cref="OpenConnectionAsync"/>,
///     which enables WAL journalling, NORMAL synchronous mode, and a busy
///     timeout. WAL lets a reader (for example a trendline query) run
///     concurrently with the writer instead of hitting "database is locked".
///
///   * A single status snapshot may contain ~50 target results (router,
///     internet, and every custom target). Historically only the router and
///     internet rows were persisted, so custom-target history was silently
///     lost. <see cref="SaveStatusAsync"/> now writes one ping_results row per
///     <see cref="TargetCheckResult"/> inside a transaction. If a snapshot has
///     no per-target breakdown it falls back to persisting the router and
///     internet results only.
///
///   * Storage failures never propagate: <see cref="SaveStatusAsync"/> swallows
///     and logs exceptions so a disk hiccup can never interrupt monitoring.
/// </summary>
public sealed class SqliteStorageService : IStorageService, IAsyncDisposable
{
    private readonly StorageOptions _options;
    private readonly ILogger<SqliteStorageService> _logger;
    private readonly string _connectionString;
    private bool _initialized;
    private readonly SemaphoreSlim _initLock = new(1, 1);

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

        var dbPath = Path.Combine(dataDir, "network-monitor.db");
        _connectionString = $"Data Source={dbPath}";

        _logger.LogInformation("SQLite database path: {DbPath}", dbPath);
    }

    /// <summary>
    /// Opens a connection and applies the per-connection pragmas we rely on.
    /// WAL is persisted in the database header, but synchronous and
    /// busy_timeout are per-connection and must be set every time.
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

            // Create tables. Fresh databases get the full, current schema
            // (including target_name and packet_loss). Older databases are
            // upgraded by MigrateSchemaAsync below.
            const string createTablesSql = """
                CREATE TABLE IF NOT EXISTS ping_results (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    target TEXT NOT NULL,
                    target_name TEXT,
                    success INTEGER NOT NULL,
                    roundtrip_ms INTEGER,
                    packet_loss REAL,
                    timestamp TEXT NOT NULL,
                    error_message TEXT,
                    target_type TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS idx_ping_results_timestamp
                ON ping_results(timestamp DESC);

                CREATE INDEX IF NOT EXISTS idx_ping_results_target_type
                ON ping_results(target_type, timestamp DESC);

                CREATE INDEX IF NOT EXISTS idx_ping_results_id
                ON ping_results(id);

                CREATE TABLE IF NOT EXISTS network_status (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    health TEXT NOT NULL,
                    message TEXT NOT NULL,
                    timestamp TEXT NOT NULL,
                    router_latency_ms INTEGER,
                    internet_latency_ms INTEGER
                );

                CREATE INDEX IF NOT EXISTS idx_network_status_timestamp
                ON network_status(timestamp DESC);

                CREATE TABLE IF NOT EXISTS sync_state (
                    key TEXT PRIMARY KEY,
                    value TEXT NOT NULL
                );
                """;

            await using (var command = connection.CreateCommand())
            {
                command.CommandText = createTablesSql;
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await MigrateSchemaAsync(connection, cancellationToken);

            _logger.LogDebug("Database schema initialized");
            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>
    /// Adds columns introduced after the original schema shipped. SQLite has no
    /// "ADD COLUMN IF NOT EXISTS", so we inspect PRAGMA table_info first and
    /// only ALTER when a column is genuinely missing.
    /// </summary>
    private static async Task MigrateSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await using (var info = connection.CreateCommand())
        {
            info.CommandText = "PRAGMA table_info(ping_results);";
            await using var reader = await info.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                // Column 1 of PRAGMA table_info is the column name.
                existingColumns.Add(reader.GetString(1));
            }
        }

        if (!existingColumns.Contains("target_name"))
        {
            await using var alter = connection.CreateCommand();
            alter.CommandText = "ALTER TABLE ping_results ADD COLUMN target_name TEXT;";
            await alter.ExecuteNonQueryAsync(cancellationToken);
        }

        if (!existingColumns.Contains("packet_loss"))
        {
            await using var alter = connection.CreateCommand();
            alter.CommandText = "ALTER TABLE ping_results ADD COLUMN packet_loss REAL;";
            await alter.ExecuteNonQueryAsync(cancellationToken);
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
            using var transaction = connection.BeginTransaction();

            // Save the summary row.
            await using (var statusCommand = connection.CreateCommand())
            {
                statusCommand.Transaction = transaction;
                statusCommand.CommandText = """
                    INSERT INTO network_status (health, message, timestamp, router_latency_ms, internet_latency_ms)
                    VALUES (@health, @message, @timestamp, @routerLatency, @internetLatency)
                    """;

                statusCommand.Parameters.AddWithValue("@health", status.Health.ToString());
                statusCommand.Parameters.AddWithValue("@message", status.Message);
                statusCommand.Parameters.AddWithValue("@timestamp", status.Timestamp.ToString("O", CultureInfo.InvariantCulture));
                statusCommand.Parameters.AddWithValue("@routerLatency",
                    (object?)status.RouterResult?.RoundtripTimeMs ?? DBNull.Value);
                statusCommand.Parameters.AddWithValue("@internetLatency",
                    (object?)status.InternetResult?.RoundtripTimeMs ?? DBNull.Value);

                await statusCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            // Persist per-target detail when available (this is the common
            // path and captures every custom target). Fall back to the legacy
            // router/internet-only rows for snapshots without a breakdown.
            if (status.TargetResults is { Count: > 0 })
            {
                foreach (var result in status.TargetResults)
                {
                    await SaveTargetCheckAsync(connection, transaction, result, cancellationToken);
                }
            }
            else
            {
                if (status.RouterResult != null)
                {
                    await SavePingResultAsync(connection, transaction, status.RouterResult,
                        "Router", "router", 0, cancellationToken);
                }

                if (status.InternetResult != null)
                {
                    await SavePingResultAsync(connection, transaction, status.InternetResult,
                        "Internet", "internet", 0, cancellationToken);
                }
            }

            transaction.Commit();

            // Periodically prune old data. The cadence is lower (~1 in 200)
            // than the old 1-in-100 because each cycle now writes many more
            // rows, so saves happen effectively as often per unit time.
            if (Random.Shared.Next(200) == 0)
            {
                await PruneOldDataAsync(connection, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            // Log but don't throw - storage failures shouldn't stop monitoring.
            _logger.LogWarning(ex, "Failed to save status to SQLite");
        }
    }

    private static async Task SaveTargetCheckAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TargetCheckResult result,
        CancellationToken cancellationToken)
    {
        // Prefer the IPv4 ping; fall back to the IPv6 result if that is all we
        // have. The row represents the aggregate outcome for this target.
        var ping = result.PingResult ?? result.PingResultV6;

        var success = ping?.Success == true;
        long? roundtrip = success ? ping?.RoundtripTimeMs : null;
        var error = ping?.ErrorMessage
            ?? (result.DnsResult is { Success: false } dns ? dns.ErrorMessage : null);

        await SavePingResultRowAsync(
            connection,
            transaction,
            target: result.Target.Address,
            targetName: result.Target.Name,
            targetType: MapCategory(result.Target.Category),
            success: success,
            roundtripMs: roundtrip,
            packetLoss: result.PacketLossPercent,
            timestamp: result.Timestamp,
            errorMessage: error,
            cancellationToken);
    }

    private static Task SavePingResultAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PingResult result,
        string targetName,
        string targetType,
        double packetLoss,
        CancellationToken cancellationToken)
    {
        return SavePingResultRowAsync(
            connection,
            transaction,
            target: result.Target,
            targetName: targetName,
            targetType: targetType,
            success: result.Success,
            roundtripMs: result.RoundtripTimeMs,
            packetLoss: packetLoss,
            timestamp: result.Timestamp,
            errorMessage: result.ErrorMessage,
            cancellationToken);
    }

    private static async Task SavePingResultRowAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string target,
        string targetName,
        string targetType,
        bool success,
        long? roundtripMs,
        double packetLoss,
        DateTimeOffset timestamp,
        string? errorMessage,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO ping_results
                (target, target_name, success, roundtrip_ms, packet_loss, timestamp, error_message, target_type)
            VALUES
                (@target, @targetName, @success, @roundtripMs, @packetLoss, @timestamp, @errorMessage, @targetType)
            """;

        command.Parameters.AddWithValue("@target", target);
        command.Parameters.AddWithValue("@targetName", (object?)targetName ?? DBNull.Value);
        command.Parameters.AddWithValue("@success", success ? 1 : 0);
        command.Parameters.AddWithValue("@roundtripMs", (object?)roundtripMs ?? DBNull.Value);
        command.Parameters.AddWithValue("@packetLoss", packetLoss);
        command.Parameters.AddWithValue("@timestamp", timestamp.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("@errorMessage", (object?)errorMessage ?? DBNull.Value);
        command.Parameters.AddWithValue("@targetType", targetType);

        await command.ExecuteNonQueryAsync(cancellationToken);
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
        var cutoff = DateTimeOffset.UtcNow.AddDays(-_options.RetentionDays).ToString("O", CultureInfo.InvariantCulture);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM ping_results WHERE timestamp < @cutoff;
            DELETE FROM network_status WHERE timestamp < @cutoff;
            """;
        command.Parameters.AddWithValue("@cutoff", cutoff);

        var deleted = await command.ExecuteNonQueryAsync(cancellationToken);

        if (deleted > 0)
        {
            _logger.LogDebug("Pruned {Count} old records", deleted);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<HistoricalData>> GetHistoricalDataAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        TimeGranularity granularity,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = await OpenConnectionAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT roundtrip_ms, timestamp, success, target_type
            FROM ping_results
            WHERE timestamp >= @from AND timestamp <= @to
            ORDER BY timestamp
            """;

        command.Parameters.AddWithValue("@from", from.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("@to", to.ToString("O", CultureInfo.InvariantCulture));

        var results = new List<(long? LatencyMs, DateTimeOffset Timestamp, bool Success)>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var latencyMs = reader.IsDBNull(0) ? (long?)null : reader.GetInt64(0);
            var timestamp = DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture);
            var success = reader.GetInt32(2) == 1;

            results.Add((latencyMs, timestamp, success));
        }

        return AggregateByGranularity(results, granularity);
    }

    private static List<HistoricalData> AggregateByGranularity(
        List<(long? LatencyMs, DateTimeOffset Timestamp, bool Success)> results,
        TimeGranularity granularity)
    {
        if (results.Count == 0)
        {
            return [];
        }

        var grouped = results.GroupBy(r => TruncateToPeriod(r.Timestamp, granularity));

        return grouped.Select(g =>
        {
            var successfulPings = g.Where(p => p.Success && p.LatencyMs.HasValue).ToList();
            var latencies = successfulPings.Select(p => p.LatencyMs!.Value).ToList();

            return new HistoricalData(
                Period: g.Key,
                AverageLatencyMs: latencies.Count > 0 ? latencies.Average() : 0,
                MinLatencyMs: latencies.Count > 0 ? latencies.Min() : 0,
                MaxLatencyMs: latencies.Count > 0 ? latencies.Max() : 0,
                PacketLossPercent: g.Any() ?
                    (double)(g.Count() - successfulPings.Count) / g.Count() * 100 : 0,
                SampleCount: g.Count());
        }).OrderBy(h => h.Period).ToList();
    }

    private static DateTimeOffset TruncateToPeriod(DateTimeOffset timestamp, TimeGranularity granularity)
    {
        return granularity switch
        {
            TimeGranularity.Minute => new DateTimeOffset(
                timestamp.Year, timestamp.Month, timestamp.Day,
                timestamp.Hour, timestamp.Minute, 0, timestamp.Offset),
            TimeGranularity.Hour => new DateTimeOffset(
                timestamp.Year, timestamp.Month, timestamp.Day,
                timestamp.Hour, 0, 0, timestamp.Offset),
            TimeGranularity.Day => new DateTimeOffset(
                timestamp.Year, timestamp.Month, timestamp.Day,
                0, 0, 0, timestamp.Offset),
            _ => timestamp
        };
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PingResult>> GetRecentPingsAsync(
        int count,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = await OpenConnectionAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT target, success, roundtrip_ms, timestamp, error_message
            FROM ping_results
            ORDER BY timestamp DESC
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
                Timestamp: DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture),
                ErrorMessage: reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        return results;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StoredPingResult>> GetPingResultsAfterAsync(
        long afterId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = await OpenConnectionAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, target, target_name, target_type, success, roundtrip_ms, packet_loss, timestamp, error_message
            FROM ping_results
            WHERE id > @afterId
            ORDER BY id ASC
            LIMIT @limit
            """;
        command.Parameters.AddWithValue("@afterId", afterId);
        command.Parameters.AddWithValue("@limit", limit);

        var results = new List<StoredPingResult>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            // Legacy rows written before the migration have NULL target_name
            // and packet_loss; guard both.
            results.Add(new StoredPingResult(
                Id: reader.GetInt64(0),
                Target: reader.GetString(1),
                TargetName: reader.IsDBNull(2) ? null : reader.GetString(2),
                TargetType: reader.GetString(3),
                Success: reader.GetInt32(4) == 1,
                RoundtripMs: reader.IsDBNull(5) ? null : reader.GetInt64(5),
                PacketLossPercent: reader.IsDBNull(6) ? 0 : reader.GetDouble(6),
                Timestamp: DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture),
                ErrorMessage: reader.IsDBNull(8) ? null : reader.GetString(8)));
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
    public async ValueTask DisposeAsync()
    {
        _initLock.Dispose();
        await Task.CompletedTask;
    }
}
