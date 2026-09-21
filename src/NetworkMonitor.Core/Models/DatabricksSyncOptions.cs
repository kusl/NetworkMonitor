namespace NetworkMonitor.Core.Models;

/// <summary>
/// Configuration for optional replication of local check history to a Databricks
/// SQL warehouse via the Databricks SQL Statement Execution REST API.
/// </summary>
/// <remarks>
/// This whole feature is opt-in and fault-tolerant by design, exactly like the
/// libSQL/Turso <see cref="RemoteSyncOptions"/> feature, and runs completely
/// independently of it (its own background service, its own sync checkpoint):
/// <list type="bullet">
///   <item>If the workspace URL, warehouse ID, or credentials are missing, the
///         feature is a no-op.</item>
///   <item>If the network or the warehouse is unavailable, the attempt is
///         skipped silently and retried on the next interval.</item>
///   <item>No failure here can ever interrupt network monitoring or the other
///         sync sink.</item>
/// </list>
///
/// WHAT IS REPLICATED: the same compact per-target, per-time-bucket
/// <see cref="CheckRollup"/> rows the Turso sink uses - not raw per-cycle rows.
/// At the default hourly bucket the warehouse receives at most
/// (number of targets) rows per hour per machine, which keeps write volume
/// trivial. The local SQLite database always keeps full per-cycle fidelity;
/// only the replicated view is aggregated. Rows are upserted with a MERGE keyed
/// on (machine, target_address, bucket_start), so re-sends are idempotent.
///
/// AUTHENTICATION (choose one - Databricks accepts either as a bearer token):
/// <list type="bullet">
///   <item><see cref="Token"/> - a Databricks personal access token (PAT). If
///         set, it takes precedence.</item>
///   <item><see cref="ClientId"/> + <see cref="ClientSecret"/> - an OAuth
///         machine-to-machine (M2M) service-principal credential. The client ID
///         is the "key id" and the client secret is the "API key" shown when the
///         OAuth secret is generated. The client exchanges these for a
///         short-lived (~1 hour) OAuth access token at
///         <c>{WorkspaceUrl}/oidc/v1/token</c> and refreshes it automatically.</item>
/// </list>
///
/// Bind from the <c>Databricks</c> section of appsettings.json or via
/// environment variables, e.g. <c>Databricks__WorkspaceUrl</c>,
/// <c>Databricks__WarehouseId</c>, <c>Databricks__ClientId</c>,
/// <c>Databricks__ClientSecret</c>. Prefer environment variables (or user
/// secrets) over committing the secret to appsettings.json.
/// </remarks>
public sealed class DatabricksSyncOptions
{
    /// <summary>Configuration section name in appsettings.json.</summary>
    public const string SectionName = "Databricks";

    /// <summary>
    /// Databricks workspace URL, e.g.
    /// <c>https://dbc-a1b2c3d4-e5f6.cloud.databricks.com</c>. A bare host or a
    /// scheme-less value is accepted and normalized to HTTPS. Empty disables the
    /// feature.
    /// </summary>
    public string WorkspaceUrl { get; set; } = string.Empty;

    /// <summary>
    /// The SQL warehouse ID that runs the statements (a serverless SQL warehouse
    /// on Free Edition). Find it in SQL Warehouses -> your warehouse -> the ID in
    /// the URL or the connection details. Empty disables the feature.
    /// </summary>
    public string WarehouseId { get; set; } = string.Empty;

    /// <summary>
    /// Unity Catalog catalog to write into. On Databricks Free Edition the
    /// default workspace catalog is usually <c>workspace</c>. Sanitized to a safe
    /// SQL identifier before use.
    /// </summary>
    public string Catalog { get; set; } = "workspace";

    /// <summary>
    /// Schema (database) within the catalog. Must already exist; the default
    /// <c>default</c> schema always does. Sanitized to a safe SQL identifier.
    /// </summary>
    public string Schema { get; set; } = "default";

    /// <summary>
    /// Table name for the synced rollup rows. Created with
    /// <c>CREATE TABLE IF NOT EXISTS</c> on first sync. Sanitized to a safe SQL
    /// identifier. Default: <c>network_monitor_rollups</c>.
    /// </summary>
    public string TableName { get; set; } = "network_monitor_rollups";

    /// <summary>
    /// Personal access token (PAT). When set, it is used directly as the bearer
    /// token and OAuth is not attempted. Empty to use OAuth instead.
    /// </summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>
    /// OAuth service-principal client ID (the "key id"). Used with
    /// <see cref="ClientSecret"/> for OAuth M2M when <see cref="Token"/> is empty.
    /// </summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>
    /// OAuth service-principal client secret (the "API key"). Used with
    /// <see cref="ClientId"/> for OAuth M2M when <see cref="Token"/> is empty.
    /// </summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>
    /// OAuth scope requested for the M2M token. Default <c>all-apis</c> is
    /// correct for the Statement Execution API.
    /// </summary>
    public string OAuthScope { get; set; } = "all-apis";

    /// <summary>
    /// Width of each rollup bucket, in minutes. Default 60 (hourly). Only
    /// fully-elapsed buckets are shipped. Clamped to at least 1.
    /// </summary>
    public int BucketMinutes { get; set; } = 60;

    /// <summary>
    /// Minimum time between sync attempts, in minutes. Default 60. Clamped to a
    /// minimum of 5.
    /// </summary>
    public int SyncIntervalMinutes { get; set; } = 60;

    /// <summary>
    /// Delay before the first sync attempt after startup, in seconds. Default 90
    /// (a little longer than the Turso sink's default, since a serverless
    /// warehouse may need to resume from idle on the first statement).
    /// </summary>
    public int InitialDelaySeconds { get; set; } = 90;

    /// <summary>
    /// How many rollup rows to read per batch. Default 200. Each batch is broken
    /// into MERGE statements of at most 30 rows so parameter counts stay small.
    /// </summary>
    public int BatchSize { get; set; } = 200;

    /// <summary>
    /// Upper bound on rollup rows pushed in a single sync run so a large backlog
    /// cannot monopolize the process. Default 25000. The remainder syncs next
    /// interval.
    /// </summary>
    public int MaxRowsPerSync { get; set; } = 25000;

    /// <summary>
    /// Overall deadline for a single statement to reach a terminal state
    /// (including any asynchronous polling), in seconds. Default 120. Clamped to
    /// [30, 600]. Generous enough to absorb a serverless warehouse cold start.
    /// </summary>
    public int RequestTimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// Server-side synchronous wait for each statement, in seconds, before the
    /// call falls back to asynchronous polling. Clamped to [5, 50] by the client.
    /// Default 30.
    /// </summary>
    public int StatementWaitSeconds { get; set; } = 30;

    /// <summary>True when a personal access token is configured.</summary>
    public bool HasToken => !string.IsNullOrWhiteSpace(Token);

    /// <summary>True when an OAuth client ID and secret are both configured.</summary>
    public bool HasOAuthCredentials =>
        !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);

    /// <summary>
    /// True when a workspace URL, a warehouse ID, and some credential are all
    /// present. This is necessary but not sufficient - the URL must also parse
    /// into a valid absolute HTTP(S) endpoint, which the client validates.
    /// </summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(WorkspaceUrl) &&
        !string.IsNullOrWhiteSpace(WarehouseId) &&
        (HasToken || HasOAuthCredentials);
}
