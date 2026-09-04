using NetworkMonitor.Core.Models;

namespace NetworkMonitor.Core.Storage;

/// <summary>
/// Abstraction for persisting network status data to the local, normalized
/// SQLite store and reading it back for trend display and remote replication.
/// </summary>
public interface IStorageService
{
    /// <summary>
    /// Persists a network status snapshot as one cycle row plus one measurement
    /// row per target. Implementations must never throw - storage failures are
    /// swallowed so a disk hiccup can never interrupt monitoring.
    /// </summary>
    /// <param name="status">The status to save.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SaveStatusAsync(NetworkStatus status, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves time-bucketed historical data for trendline display, aggregated
    /// in the database rather than in memory.
    /// </summary>
    /// <param name="from">Start of time range.</param>
    /// <param name="to">End of time range.</param>
    /// <param name="granularity">Time granularity for aggregation.</param>
    /// <param name="targetAddress">
    /// Optional target address filter. When null, all targets are aggregated
    /// together (useful only for a coarse overview). For meaningful trends pass
    /// a specific target so LAN, DNS, and distant-service latencies are not
    /// averaged into one meaningless number.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<HistoricalData>> GetHistoricalDataAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        TimeGranularity granularity,
        string? targetAddress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets recent raw ping results (most recent first) for detailed analysis.
    /// </summary>
    /// <param name="count">Number of results to retrieve.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<PingResult>> GetRecentPingsAsync(
        int count,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Computes per-target rollups for fully-elapsed buckets in the half-open
    /// range [<paramref name="fromBucketStartMsInclusive"/>, <paramref name="toExclusiveMs"/>),
    /// ordered by bucket then target. Used by the remote sync feature to
    /// replicate a compact aggregate instead of raw rows.
    /// </summary>
    /// <param name="fromBucketStartMsInclusive">Inclusive lower bound (bucket-aligned Unix ms).</param>
    /// <param name="toExclusiveMs">Exclusive upper bound (usually the start of the current, still-open bucket).</param>
    /// <param name="bucketMinutes">Bucket width in minutes.</param>
    /// <param name="limit">Maximum number of rollup rows to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<CheckRollup>> GetRollupsAsync(
        long fromBucketStartMsInclusive,
        long toExclusiveMs,
        int bucketMinutes,
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
