namespace NetworkMonitor.Core.Models;

/// <summary>
/// Configuration for optional replication of local check history to a remote
/// libSQL / Turso-compatible database.
/// </summary>
/// <remarks>
/// This whole feature is opt-in and fault-tolerant by design:
/// <list type="bullet">
///   <item>If <see cref="Url"/> or <see cref="AuthToken"/> is missing, sync is a no-op.</item>
///   <item>If the URL is malformed, sync is a no-op (never an error).</item>
///   <item>If the network or the remote is down, the attempt is skipped silently
///         and retried on the next interval.</item>
///   <item>No failure here ever interrupts network monitoring.</item>
/// </list>
///
/// WHAT IS REPLICATED: compact per-target, per-time-bucket <em>rollups</em>, not
/// raw per-cycle rows. Shipping raw samples (dozens of rows every few seconds)
/// produces millions of remote row-writes per month and permanently outruns any
/// reasonable sync budget. Rollups collapse a whole bucket of cycles for one
/// target into a single row, so at the default hourly bucket the remote receives
/// at most (number of targets) rows per hour per machine. The local database
/// always keeps full per-cycle fidelity; only the replicated view is aggregated.
///
/// Any provider exposing the libSQL HTTP "Hrana" pipeline endpoint
/// (<c>/v2/pipeline</c>) with bearer-token auth works, not just Turso.
///
/// Bind from the <c>RemoteSync</c> section of appsettings.json or via
/// environment variables, e.g. <c>RemoteSync__Url</c> and <c>RemoteSync__AuthToken</c>.
/// </remarks>
public sealed class RemoteSyncOptions
{
    /// <summary>Configuration section name in appsettings.json.</summary>
    public const string SectionName = "RemoteSync";

    /// <summary>
    /// Remote database URL. Accepts <c>libsql://</c>, <c>wss://</c>, <c>ws://</c>,
    /// <c>https://</c> or <c>http://</c>; the scheme is normalized to HTTP(S)
    /// internally. Example: <c>libsql://your-db.aws-us-east-1.turso.io</c>.
    /// Empty disables the feature.
    /// </summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// Bearer auth token for the remote database. Empty disables the feature.
    /// </summary>
    public string AuthToken { get; set; } = string.Empty;

    /// <summary>
    /// Replication mode. Only <c>"rollup"</c> is currently supported; the value
    /// is informational and documents intent. Reserved for future modes.
    /// </summary>
    public string Mode { get; set; } = "rollup";

    /// <summary>
    /// Width of each rollup bucket, in minutes. Default: 60 (hourly). Larger
    /// buckets mean fewer remote rows and coarser resolution. Clamped to at
    /// least 1 minute. Only fully-elapsed buckets are replicated, so the newest
    /// (still-open) bucket is held back until it closes.
    /// </summary>
    public int BucketMinutes { get; set; } = 60;

    /// <summary>
    /// Minimum time between sync attempts, in minutes. Default: 60 (hourly), so
    /// each bucket is pushed shortly after it closes. Clamped to a minimum of 5.
    /// </summary>
    public int SyncIntervalMinutes { get; set; } = 60;

    /// <summary>
    /// Delay before the first sync attempt after startup, in seconds. Default: 60.
    /// Gives the network stack time to come up on freshly booted machines.
    /// </summary>
    public int InitialDelaySeconds { get; set; } = 60;

    /// <summary>
    /// How many rollup rows to read/push per batch. Default: 500.
    /// </summary>
    public int BatchSize { get; set; } = 500;

    /// <summary>
    /// Upper bound on rollup rows pushed in a single sync run so a large backlog
    /// cannot monopolize the process. Default: 25000. The remainder syncs next
    /// interval.
    /// </summary>
    public int MaxRowsPerSync { get; set; } = 25000;

    /// <summary>
    /// HTTP request timeout for a single pipeline call, in seconds. Default: 30.
    /// </summary>
    public int RequestTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Remote table name for synced rollup rows. Default: <c>check_rollups</c>.
    /// Sanitized to a safe SQL identifier before use.
    /// </summary>
    public string TableName { get; set; } = "check_rollups";

    /// <summary>
    /// True when both a URL and an auth token are present. This is a necessary
    /// (not sufficient) condition - the URL must also be a valid absolute URI,
    /// which is validated by the client.
    /// </summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Url) && !string.IsNullOrWhiteSpace(AuthToken);
}
