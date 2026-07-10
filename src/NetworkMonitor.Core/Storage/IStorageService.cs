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

    /// <summary>
    /// Reads stored ping rows with an id strictly greater than <paramref name="afterId"/>,
    /// ordered by id ascending. Used by the remote sync feature to page through
    /// history that has not yet been replicated.
    /// </summary>
    /// <param name="afterId">Exclusive lower bound on the row id.</param>
    /// <param name="limit">Maximum number of rows to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<StoredPingResult>> GetPingResultsAfterAsync(
        long afterId,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads a value from the small key/value sync-state store, or null if the
    /// key is absent.
    /// </summary>
    Task<string?> GetSyncStateAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes (inserts or replaces) a value in the key/value sync-state store.
    /// </summary>
    Task SetSyncStateAsync(string key, string value, CancellationToken cancellationToken = default);
}
