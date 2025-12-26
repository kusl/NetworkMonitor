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
    /// <param name="status">The status to save.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SaveStatusAsync(NetworkStatus status, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves historical data for trendline display.
    /// </summary>
    /// <param name="from">Start of time range.</param>
    /// <param name="to">End of time range.</param>
    /// <param name="granularity">Time granularity for aggregation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<HistoricalData>> GetHistoricalDataAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        TimeGranularity granularity,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets recent raw ping results for detailed analysis.
    /// </summary>
    /// <param name="count">Number of results to retrieve.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<PingResult>> GetRecentPingsAsync(
        int count,
        CancellationToken cancellationToken = default);
}
