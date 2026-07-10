namespace NetworkMonitor.Core.RemoteSync;

/// <summary>
/// Minimal client for a libSQL / Turso-compatible remote database, used only by
/// the optional remote sync feature.
/// </summary>
/// <remarks>
/// Implementations must be fault tolerant: a malformed configuration means the
/// client reports <see cref="IsConfigured"/> = false and never acts, and any
/// network or protocol failure is reported as a failed (but non-throwing)
/// pipeline execution so that monitoring is never affected.
/// </remarks>
public interface IRemoteDatabaseClient
{
    /// <summary>
    /// True when the client has a usable endpoint and credentials. When false,
    /// the client is a no-op and the sync service should not attempt to use it.
    /// </summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Executes an ordered batch of statements against the remote database in a
    /// single round trip.
    /// </summary>
    /// <returns>
    /// True if every statement executed without error; false on any HTTP,
    /// protocol, or statement-level error. Never throws for expected failures.
    /// </returns>
    Task<bool> ExecutePipelineAsync(
        IReadOnlyList<HranaStatement> statements,
        CancellationToken cancellationToken = default);
}
