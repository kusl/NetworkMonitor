namespace NetworkMonitor.Core.RemoteSync;

/// <summary>
/// Minimal client for a Databricks SQL warehouse, used only by the optional
/// Databricks sync feature.
/// </summary>
/// <remarks>
/// Implementations must be fault tolerant, mirroring
/// <see cref="IRemoteDatabaseClient"/>: a missing or malformed configuration
/// means <see cref="IsConfigured"/> is false and the client never acts, and any
/// network, authentication, or statement failure is reported as a failed (but
/// non-throwing) execution so that monitoring is never affected.
/// </remarks>
public interface IDatabricksClient
{
    /// <summary>
    /// True when the client has a usable workspace endpoint, a warehouse ID, and
    /// a credential. When false the client is a no-op and the sync service should
    /// not attempt to use it.
    /// </summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Executes an ordered list of statements against the warehouse, one request
    /// per statement, stopping at the first failure.
    /// </summary>
    /// <returns>
    /// True only if every statement reached the <c>SUCCEEDED</c> state; false on
    /// any authentication, HTTP, protocol, or statement-execution error. Never
    /// throws for expected failures (only honors cancellation).
    /// </returns>
    Task<bool> ExecuteAsync(
        IReadOnlyList<DatabricksStatement> statements,
        CancellationToken cancellationToken = default);
}
