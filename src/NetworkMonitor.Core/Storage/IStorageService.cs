using NetworkMonitor.Core.Models;

namespace NetworkMonitor.Core.Storage;

/// <summary>
/// Abstraction for persisting network status data.
/// Implementations may write to files, SQLite, or both.
/// </summary>
public interface IStorageService
{
    /// <summary>
    /// Persists a network status snapshot.
    /// </summary>
    Task SaveStatusAsync(NetworkStatus status, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Retrieves historical data for trendline display.
    /// </summary>
    Task<IReadOnlyList<HistoricalData>> GetHistoricalDataAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        TimeGranularity granularity,
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Gets recent raw ping results for detailed analysis.
    /// </summary>
    Task<IReadOnlyList<PingResult>> GetRecentPingsAsync(
        int count,
        CancellationToken cancellationToken = default);
}
