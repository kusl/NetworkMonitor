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
/// Schema is automatically created/migrated on startup.
/// Old data is automatically pruned based on retention settings.
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
    
    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized) return;
        
        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized) return;
            
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            
            // Create tables
            const string createTablesSql = """
                CREATE TABLE IF NOT EXISTS ping_results (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    target TEXT NOT NULL,
                    success INTEGER NOT NULL,
                    roundtrip_ms INTEGER,
                    timestamp TEXT NOT NULL,
                    error_message TEXT,
                    target_type TEXT NOT NULL
                );
                
                CREATE INDEX IF NOT EXISTS idx_ping_results_timestamp 
                ON ping_results(timestamp DESC);
                
                CREATE INDEX IF NOT EXISTS idx_ping_results_target_type 
                ON ping_results(target_type, timestamp DESC);
                
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
                """;
            
            await using var command = connection.CreateCommand();
            command.CommandText = createTablesSql;
            await command.ExecuteNonQueryAsync(cancellationToken);
            
            _logger.LogDebug("Database schema initialized");
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
            
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            
            // Save status
            await using var statusCommand = connection.CreateCommand();
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
            
            // Save individual ping results
            if (status.RouterResult != null)
            {
                await SavePingResultAsync(connection, status.RouterResult, "router", cancellationToken);
            }
            
            if (status.InternetResult != null)
            {
                await SavePingResultAsync(connection, status.InternetResult, "internet", cancellationToken);
            }
            
            // Periodically prune old data (roughly every 100 saves)
            if (Random.Shared.Next(100) == 0)
            {
                await PruneOldDataAsync(connection, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            // Log but don't throw - storage failures shouldn't stop monitoring
            _logger.LogWarning(ex, "Failed to save status to SQLite");
        }
    }
    
    private static async Task SavePingResultAsync(
        SqliteConnection connection,
        PingResult result,
        string targetType,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ping_results (target, success, roundtrip_ms, timestamp, error_message, target_type)
            VALUES (@target, @success, @roundtripMs, @timestamp, @errorMessage, @targetType)
            """;
        
        command.Parameters.AddWithValue("@target", result.Target);
        command.Parameters.AddWithValue("@success", result.Success ? 1 : 0);
        command.Parameters.AddWithValue("@roundtripMs", (object?)result.RoundtripTimeMs ?? DBNull.Value);
        command.Parameters.AddWithValue("@timestamp", result.Timestamp.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("@errorMessage", (object?)result.ErrorMessage ?? DBNull.Value);
        command.Parameters.AddWithValue("@targetType", targetType);
        
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
    
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
        
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        
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
    
    private static IReadOnlyList<HistoricalData> AggregateByGranularity(
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
        
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        
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
    public async ValueTask DisposeAsync()
    {
        _initLock.Dispose();
        await Task.CompletedTask;
    }
}
