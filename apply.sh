#!/usr/bin/env bash
#
# apply-databricks-sync.sh
#
# Idempotently adds the optional Databricks SQL-warehouse sync sink to
# NetworkMonitor. Run from the repository root (the directory containing
# `src/NetworkMonitor.slnx`). Safe to re-run: it overwrites the managed files
# with the versions below and leaves everything else untouched.
#
# The .NET SDK is required only for the optional build/test at the end; the file
# writes themselves need only bash + coreutils.

set -euo pipefail

# --- preconditions ----------------------------------------------------------

if [[ ! -f "src/NetworkMonitor.slnx" ]]; then
  echo "ERROR: run this from the repository root (src/NetworkMonitor.slnx not found)." >&2
  exit 1
fi

echo "==> Applying Databricks sync files"

mkdir -p \
  src/NetworkMonitor.Core/Models \
  src/NetworkMonitor.Core/RemoteSync \
  src/NetworkMonitor.Console \
  src/NetworkMonitor.Tests/Fakes \
  src/NetworkMonitor.Tests/RemoteSync

# Each file is written from a quoted heredoc so nothing is expanded.

write() {
  # $1 = destination path; stdin = contents
  local dest="$1"
  cat > "$dest"
  echo "    wrote $dest"
}


write "src/NetworkMonitor.Core/Models/DatabricksSyncOptions.cs" << 'APPLY_FILE_EOF'
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
APPLY_FILE_EOF

write "src/NetworkMonitor.Core/RemoteSync/DatabricksStatement.cs" << 'APPLY_FILE_EOF'
namespace NetworkMonitor.Core.RemoteSync;

/// <summary>
/// A single named parameter for a Databricks SQL statement. Named parameters use
/// <c>:name</c> markers in the SQL (no leading colon in <see cref="Name"/>).
/// </summary>
/// <param name="Name">The parameter name, without the leading colon.</param>
/// <param name="Value">
/// The value in its string form (all Databricks parameter values are sent as
/// strings). <c>null</c> binds a typed SQL NULL (the <c>value</c> field is
/// omitted from the request).
/// </param>
/// <param name="Type">
/// The Databricks SQL type, e.g. <c>STRING</c>, <c>BIGINT</c>, <c>DOUBLE</c>.
/// Supplying a type lets NULLs carry a definite type, which is required inside a
/// multi-row VALUES clause.
/// </param>
public sealed record DatabricksParameter(string Name, string? Value, string Type);

/// <summary>
/// A single SQL statement plus its named parameters, executed against a
/// Databricks SQL warehouse through the Statement Execution REST API.
/// </summary>
/// <param name="Sql">The SQL text with <c>:name</c> parameter markers.</param>
/// <param name="Parameters">One entry per marker referenced in <paramref name="Sql"/>.</param>
public sealed record DatabricksStatement(string Sql, IReadOnlyList<DatabricksParameter> Parameters);
APPLY_FILE_EOF

write "src/NetworkMonitor.Core/RemoteSync/IDatabricksClient.cs" << 'APPLY_FILE_EOF'
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
APPLY_FILE_EOF

write "src/NetworkMonitor.Core/RemoteSync/DatabricksSqlClient.cs" << 'APPLY_FILE_EOF'
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetworkMonitor.Core.Models;

namespace NetworkMonitor.Core.RemoteSync;

/// <summary>
/// Client for the Databricks SQL Statement Execution REST API
/// (<c>POST {workspace}/api/2.0/sql/statements</c>) with bearer-token auth.
/// </summary>
/// <remarks>
/// Registered as a singleton so it can own a single long-lived
/// <see cref="HttpClient"/> and cache the OAuth token (no
/// <c>Microsoft.Extensions.Http</c> dependency, consistent with the Turso
/// client). It is deliberately fault tolerant:
/// <list type="bullet">
///   <item>A missing/malformed workspace URL, warehouse ID, or credential leaves
///         <see cref="IsConfigured"/> false and the client inert.</item>
///   <item>Authentication, HTTP, protocol, and statement errors are logged at
///         debug and reported as a failed execution rather than thrown.</item>
/// </list>
///
/// Authentication accepts either a personal access token (used directly) or an
/// OAuth machine-to-machine client ID + secret, which is exchanged for a
/// short-lived access token at <c>{workspace}/oidc/v1/token</c> and refreshed
/// automatically shortly before expiry.
///
/// Each statement is submitted with a bounded server-side wait
/// (<c>on_wait_timeout=CONTINUE</c>); if the warehouse is still resuming or the
/// statement is still running when the wait elapses, the client polls
/// <c>GET .../statements/{id}</c> until the statement reaches a terminal state
/// or an overall deadline is hit.
/// </remarks>
public sealed class DatabricksSqlClient : IDatabricksClient, IDisposable
{
    private readonly ILogger<DatabricksSqlClient> _logger;
    private readonly HttpClient _http;
    private readonly DatabricksSyncOptions _options;

    private readonly Uri? _statementsEndpoint;
    private readonly Uri? _tokenEndpoint;
    private readonly bool _hasCredential;

    // OAuth token cache (only used when no PAT is configured).
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _cachedToken;
    private DateTimeOffset _tokenExpiresAt = DateTimeOffset.MinValue;

    public DatabricksSqlClient(
        IOptions<DatabricksSyncOptions> options,
        ILogger<DatabricksSqlClient> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger;
        _options = options.Value;

        _hasCredential = _options.HasToken || _options.HasOAuthCredentials;

        if (_hasCredential &&
            !string.IsNullOrWhiteSpace(_options.WarehouseId) &&
            TryBuildWorkspaceBase(_options.WorkspaceUrl, out var baseUri) &&
            baseUri is not null)
        {
            _statementsEndpoint = new Uri(baseUri, "api/2.0/sql/statements");
            _tokenEndpoint = new Uri(baseUri, "oidc/v1/token");
        }

        var handler = new HttpClientHandler { CheckCertificateRevocationList = true };
        _http = new HttpClient(handler)
        {
            // Per-request timeout. Kept comfortably above the server-side
            // wait_timeout (<= 50s) so a single blocking call never trips it.
            Timeout = TimeSpan.FromSeconds(Math.Clamp(_options.RequestTimeoutSeconds, 60, 600)),
        };
    }

    /// <inheritdoc />
    public bool IsConfigured => _statementsEndpoint is not null;

    /// <inheritdoc />
    public async Task<bool> ExecuteAsync(
        IReadOnlyList<DatabricksStatement> statements,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(statements);

        if (_statementsEndpoint is null || statements.Count == 0)
        {
            return false;
        }

        var token = await GetBearerTokenAsync(cancellationToken);
        if (token is null)
        {
            return false;
        }

        foreach (var statement in statements)
        {
            var ok = await ExecuteOneAsync(statement, token, cancellationToken);
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Normalizes a workspace URL to an absolute HTTPS base URI ending in a
    /// single trailing slash (so relative API paths resolve correctly). Accepts a
    /// bare host, or an <c>http(s)://</c> URL. Returns false for anything
    /// unusable.
    /// </summary>
    public static bool TryBuildWorkspaceBase(string? url, out Uri? baseUri)
    {
        baseUri = null;

        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        var normalized = url.Trim();

        if (!normalized.Contains("://", StringComparison.Ordinal))
        {
            normalized = string.Concat("https://", normalized);
        }

        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var parsed))
        {
            return false;
        }

        var isHttp =
            string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(parsed.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);

        if (!isHttp || string.IsNullOrEmpty(parsed.Host))
        {
            return false;
        }

        // Use only scheme + authority as the base; any path on the workspace URL
        // is dropped so "api/2.0/..." resolves against the root.
        var builder = new UriBuilder(parsed.Scheme, parsed.Host, parsed.IsDefaultPort ? -1 : parsed.Port)
        {
            Path = "/",
            Query = string.Empty,
            Fragment = string.Empty,
        };

        baseUri = builder.Uri;
        return true;
    }

    private async Task<bool> ExecuteOneAsync(
        DatabricksStatement statement,
        string token,
        CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.StartNew();
        var overallTimeout = TimeSpan.FromSeconds(Math.Clamp(_options.RequestTimeoutSeconds, 30, 600));

        try
        {
            var payload = BuildStatementPayload(statement);

            using var request = new HttpRequestMessage(HttpMethod.Post, _statementsEndpoint)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await _http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug(
                    "Databricks returned HTTP {StatusCode} when submitting a statement.",
                    (int)response.StatusCode);
                return false;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var (state, statementId, error) = ParseStatus(body);

            while (true)
            {
                switch (state)
                {
                    case "SUCCEEDED":
                        return true;

                    case "FAILED":
                    case "CANCELED":
                    case "CLOSED":
                        _logger.LogDebug(
                            "Databricks statement ended in state {State}: {Error}",
                            state,
                            error ?? "no error detail");
                        return false;

                    case "PENDING":
                    case "RUNNING":
                        break; // Fall through to poll below.

                    default:
                        _logger.LogDebug("Databricks returned an unrecognized statement state '{State}'.", state ?? "null");
                        return false;
                }

                if (statementId is null || deadline.Elapsed >= overallTimeout)
                {
                    _logger.LogDebug("Databricks statement did not complete before the deadline; will retry next interval.");
                    if (statementId is not null)
                    {
                        await TryCancelAsync(statementId, token, cancellationToken);
                    }

                    return false;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(1500), cancellationToken);

                (state, _, error) = await GetStatusAsync(statementId, token, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Databricks statement execution failed.");
            return false;
        }
    }

    private async Task<(string? State, string? StatementId, string? Error)> GetStatusAsync(
        string statementId,
        string token,
        CancellationToken cancellationToken)
    {
        try
        {
            var statusUri = new Uri(_statementsEndpoint!, Uri.EscapeDataString(statementId));

            using var request = new HttpRequestMessage(HttpMethod.Get, statusUri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await _http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug(
                    "Databricks returned HTTP {StatusCode} when polling a statement.",
                    (int)response.StatusCode);
                return (null, statementId, null);
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return ParseStatus(body);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Databricks statement poll failed.");
            return (null, statementId, null);
        }
    }

    private async Task TryCancelAsync(string statementId, string token, CancellationToken cancellationToken)
    {
        try
        {
            var cancelUri = new Uri(_statementsEndpoint!, string.Concat(Uri.EscapeDataString(statementId), "/cancel"));

            using var request = new HttpRequestMessage(HttpMethod.Post, cancelUri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await _http.SendAsync(request, cancellationToken);
            _ = response.StatusCode;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Databricks statement cancel request failed (ignored).");
        }
    }

    private (string? State, string? StatementId, string? Error) ParseStatus(string responseBody)
    {
        try
        {
            var root = JsonNode.Parse(responseBody);
            var state = root?["status"]?["state"]?.GetValue<string>();
            var statementId = root?["statement_id"]?.GetValue<string>();
            var error = root?["status"]?["error"]?["message"]?.GetValue<string>();
            return (state?.ToUpperInvariant(), statementId, error);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not parse Databricks statement response.");
            return (null, null, null);
        }
    }

    private string BuildStatementPayload(DatabricksStatement statement)
    {
        var waitSeconds = Math.Clamp(_options.StatementWaitSeconds, 5, 50);

        var root = new JsonObject
        {
            ["warehouse_id"] = _options.WarehouseId,
            ["catalog"] = SanitizeIdentifier(_options.Catalog, "workspace"),
            ["schema"] = SanitizeIdentifier(_options.Schema, "default"),
            ["statement"] = statement.Sql,
            ["wait_timeout"] = string.Concat(waitSeconds.ToString(CultureInfo.InvariantCulture), "s"),
            ["on_wait_timeout"] = "CONTINUE",
            ["format"] = "JSON_ARRAY",
            ["disposition"] = "INLINE",
        };

        if (statement.Parameters.Count > 0)
        {
            var parameters = new JsonArray();
            foreach (var p in statement.Parameters)
            {
                var node = new JsonObject
                {
                    ["name"] = p.Name,
                    ["type"] = p.Type,
                };

                // Omitting "value" binds a typed NULL.
                if (p.Value is not null)
                {
                    node["value"] = p.Value;
                }

                parameters.Add(node);
            }

            root["parameters"] = parameters;
        }

        return root.ToJsonString();
    }

    private async Task<string?> GetBearerTokenAsync(CancellationToken cancellationToken)
    {
        // A personal access token is used verbatim.
        if (_options.HasToken)
        {
            return _options.Token;
        }

        if (!_options.HasOAuthCredentials || _tokenEndpoint is null)
        {
            return null;
        }

        // Fast path: a cached token that is still valid.
        if (_cachedToken is not null && DateTimeOffset.UtcNow < _tokenExpiresAt)
        {
            return _cachedToken;
        }

        await _tokenLock.WaitAsync(cancellationToken);
        try
        {
            if (_cachedToken is not null && DateTimeOffset.UtcNow < _tokenExpiresAt)
            {
                return _cachedToken;
            }

            return await FetchOAuthTokenAsync(cancellationToken);
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private async Task<string?> FetchOAuthTokenAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _tokenEndpoint);

            var basic = Convert.ToBase64String(
                Encoding.UTF8.GetBytes(string.Concat(_options.ClientId, ":", _options.ClientSecret)));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);

            var scope = string.IsNullOrWhiteSpace(_options.OAuthScope) ? "all-apis" : _options.OAuthScope;
            request.Content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("grant_type", "client_credentials"),
                new KeyValuePair<string, string>("scope", scope),
            });

            using var response = await _http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug(
                    "Databricks OAuth token endpoint returned HTTP {StatusCode}.",
                    (int)response.StatusCode);
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var root = JsonNode.Parse(body);

            var accessToken = root?["access_token"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                _logger.LogDebug("Databricks OAuth response did not contain an access token.");
                return null;
            }

            var expiresIn = 3600;
            var expiresNode = root?["expires_in"];
            if (expiresNode is not null && int.TryParse(
                    expiresNode.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                expiresIn = parsed;
            }

            _cachedToken = accessToken;
            // Refresh a minute early to avoid using a token that expires mid-request.
            _tokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(30, expiresIn - 60));
            return accessToken;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Databricks OAuth token request failed.");
            return null;
        }
    }

    /// <summary>
    /// Reduces a configured catalog/schema/table name to a safe SQL identifier
    /// (letters, digits, underscore). Falls back to <paramref name="fallback"/>
    /// when the input is empty or would start with a digit.
    /// </summary>
    internal static string SanitizeIdentifier(string? name, string fallback)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return fallback;
        }

        var builder = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            if (char.IsLetterOrDigit(ch) || ch == '_')
            {
                builder.Append(ch);
            }
        }

        var cleaned = builder.ToString();
        if (cleaned.Length == 0 || char.IsDigit(cleaned[0]))
        {
            return fallback;
        }

        return cleaned;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _http.Dispose();
        _tokenLock.Dispose();
    }
}
APPLY_FILE_EOF

write "src/NetworkMonitor.Core/RemoteSync/DatabricksSyncService.cs" << 'APPLY_FILE_EOF'
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetworkMonitor.Core.Models;
using NetworkMonitor.Core.Storage;

namespace NetworkMonitor.Core.RemoteSync;

/// <summary>
/// Background service that replicates local check history to a Databricks SQL
/// warehouse as compact per-target, per-bucket rollups, using the Databricks SQL
/// Statement Execution REST API.
/// </summary>
/// <remarks>
/// This is the Databricks counterpart of <see cref="RemoteSyncService"/> and is
/// intentionally structured the same way, but it is completely independent: it
/// has its own options, its own client, and - crucially - its own sync
/// checkpoint (<see cref="CheckpointKey"/>), so the two sinks advance separately
/// and one can be enabled without the other. The same design guarantees apply:
/// <list type="bullet">
///   <item>Not configured or malformed config: the loop logs one debug line and
///         exits. Pure no-op, never an error.</item>
///   <item>Network or warehouse unavailable: the attempt is skipped and retried
///         next interval. Nothing is thrown.</item>
///   <item>The remote schema is created once per process run, then only rollup
///         rows are shipped.</item>
///   <item>The checkpoint advances only after the warehouse confirms success, so
///         nothing is lost or double-counted across restarts. Rows are upserted
///         with a MERGE keyed on (machine, target_address, bucket_start), so
///         re-sends are idempotent regardless.</item>
///   <item>No failure here can interrupt network monitoring - it runs
///         independently and swallows all non-cancellation exceptions.</item>
/// </list>
/// Rows are tagged with <see cref="Environment.MachineName"/> so several machines
/// can safely share one warehouse table.
/// </remarks>
public sealed class DatabricksSyncService : BackgroundService
{
    // Distinct from the Turso checkpoint so the two sinks are independent.
    private const string CheckpointKey = "databricks_rollup_next_bucket_ms";

    // 15 columns per rollup row. 30 rows per MERGE keeps parameter counts small
    // (450 markers) - well within Databricks limits and easy on statement size.
    private const int MergeRowsPerStatement = 30;

    private static readonly string[] ColumnNames =
    {
        "machine", "bucket_start", "target_name", "target_address", "target_category",
        "samples", "ok", "avg_rtt_ms", "min_rtt_ms", "max_rtt_ms", "avg_jitter_ms",
        "avg_dns_ms", "avg_loss_pct", "bucket_minutes", "synced_at",
    };

    private readonly IDatabricksClient _client;
    private readonly IStorageService _storage;
    private readonly DatabricksSyncOptions _options;
    private readonly ILogger<DatabricksSyncService> _logger;
    private readonly string _machine;
    private readonly string _qualifiedTable;
    private readonly int _bucketMinutes;
    private readonly long _bucketMs;

    private bool _schemaEnsured;
    private bool _firstSyncLogged;

    public DatabricksSyncService(
        IDatabricksClient client,
        IStorageService storage,
        IOptions<DatabricksSyncOptions> options,
        ILogger<DatabricksSyncService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _client = client;
        _storage = storage;
        _options = options.Value;
        _logger = logger;
        _machine = Environment.MachineName;
        _bucketMinutes = Math.Max(1, _options.BucketMinutes);
        _bucketMs = _bucketMinutes * 60_000L;

        var catalog = DatabricksSqlClient.SanitizeIdentifier(_options.Catalog, "workspace");
        var schema = DatabricksSqlClient.SanitizeIdentifier(_options.Schema, "default");
        var table = DatabricksSqlClient.SanitizeIdentifier(_options.TableName, "network_monitor_rollups");
        _qualifiedTable = string.Concat(catalog, ".", schema, ".", table);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.IsConfigured || !_client.IsConfigured)
        {
            _logger.LogDebug("Databricks sync is not configured; the feature is disabled.");
            return;
        }

        _logger.LogInformation(
            "Databricks sync enabled (every {Minutes} min, {Bucket}-min rollups) to table '{Table}'.",
            Math.Max(5, _options.SyncIntervalMinutes),
            _bucketMinutes,
            _qualifiedTable);

        try
        {
            await Task.Delay(
                TimeSpan.FromSeconds(Math.Max(0, _options.InitialDelaySeconds)),
                stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var interval = TimeSpan.FromMinutes(Math.Max(5, _options.SyncIntervalMinutes));
        using var timer = new PeriodicTimer(interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SyncOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Databricks sync attempt failed; will retry next interval.");
            }

            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken))
                {
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Performs a single sync pass: computes rollups for fully-elapsed buckets
    /// that have not been replicated yet and pushes them to Databricks, advancing
    /// the checkpoint only on confirmed success. Public to allow direct testing.
    /// </summary>
    /// <returns>The number of rollup rows successfully pushed in this pass.</returns>
    public async Task<int> SyncOnceAsync(CancellationToken cancellationToken)
    {
        if (!_options.IsConfigured || !_client.IsConfigured)
        {
            return 0;
        }

        if (!_schemaEnsured)
        {
            if (!await EnsureRemoteSchemaAsync(cancellationToken))
            {
                return 0;
            }

            _schemaEnsured = true;
        }

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var currentBucketStart = nowMs - (nowMs % _bucketMs);

        var checkpoint = await ReadCheckpointAsync(cancellationToken);
        var batchSize = Math.Clamp(_options.BatchSize, 1, 5000);
        var maxRows = Math.Max(batchSize, _options.MaxRowsPerSync);
        var totalSynced = 0;

        while (totalSynced < maxRows && !cancellationToken.IsCancellationRequested)
        {
            var from = checkpoint;
            var to = currentBucketStart;

            if (from >= to)
            {
                break; // No fully-elapsed, un-synced buckets remain.
            }

            IReadOnlyList<CheckRollup> rows;
            try
            {
                rows = await _storage.GetRollupsAsync(from, to, _bucketMinutes, batchSize + 1, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Databricks sync could not read local rollups.");
                return totalSynced;
            }

            if (rows.Count == 0)
            {
                checkpoint = to;
                await WriteCheckpointAsync(checkpoint, cancellationToken);
                break;
            }

            var truncated = rows.Count > batchSize;
            var page = truncated ? rows.Take(batchSize).ToList() : rows;

            List<CheckRollup> toPush;
            long newCheckpoint;

            if (truncated)
            {
                var lastBucket = page[^1].BucketStartMs;
                var completeThrough = page
                    .Where(r => r.BucketStartMs < lastBucket)
                    .Select(r => r.BucketStartMs)
                    .DefaultIfEmpty(long.MinValue)
                    .Max();

                if (completeThrough == long.MinValue)
                {
                    toPush = page.ToList();
                    newCheckpoint = lastBucket + _bucketMs;
                }
                else
                {
                    toPush = page.Where(r => r.BucketStartMs <= completeThrough).ToList();
                    newCheckpoint = completeThrough + _bucketMs;
                }
            }
            else
            {
                toPush = page.ToList();
                newCheckpoint = page[^1].BucketStartMs + _bucketMs;
            }

            var statements = BuildMergeStatements(toPush);
            var ok = await _client.ExecuteAsync(statements, cancellationToken);
            if (!ok)
            {
                // Leave the checkpoint untouched so the same buckets retry next time.
                return totalSynced;
            }

            checkpoint = newCheckpoint;
            await WriteCheckpointAsync(checkpoint, cancellationToken);
            totalSynced += toPush.Count;

            if (!truncated)
            {
                break; // Backlog drained.
            }
        }

        if (totalSynced > 0)
        {
            if (!_firstSyncLogged)
            {
                _logger.LogInformation(
                    "Databricks sync active: pushed {Count} rollup row(s) to the warehouse.",
                    totalSynced);
                _firstSyncLogged = true;
            }
            else
            {
                _logger.LogDebug("Databricks sync pushed {Count} rollup row(s).", totalSynced);
            }
        }

        return totalSynced;
    }

    private async Task<bool> EnsureRemoteSchemaAsync(CancellationToken cancellationToken)
    {
        var ddl =
            $"CREATE TABLE IF NOT EXISTS {_qualifiedTable} (" +
            "machine STRING NOT NULL, " +
            "bucket_start BIGINT NOT NULL, " +
            "target_name STRING NOT NULL, " +
            "target_address STRING NOT NULL, " +
            "target_category STRING NOT NULL, " +
            "samples BIGINT NOT NULL, " +
            "ok BIGINT NOT NULL, " +
            "avg_rtt_ms DOUBLE, " +
            "min_rtt_ms BIGINT, " +
            "max_rtt_ms BIGINT, " +
            "avg_jitter_ms DOUBLE, " +
            "avg_dns_ms DOUBLE, " +
            "avg_loss_pct DOUBLE NOT NULL, " +
            "bucket_minutes BIGINT NOT NULL, " +
            "synced_at BIGINT NOT NULL" +
            ") USING DELTA";

        var statements = new List<DatabricksStatement>
        {
            new(ddl, Array.Empty<DatabricksParameter>()),
        };

        try
        {
            return await _client.ExecuteAsync(statements, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Databricks sync could not ensure the warehouse schema.");
            return false;
        }
    }

    private async Task<long> ReadCheckpointAsync(CancellationToken cancellationToken)
    {
        try
        {
            var raw = await _storage.GetSyncStateAsync(CheckpointKey, cancellationToken);
            if (!string.IsNullOrWhiteSpace(raw) &&
                long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read Databricks sync checkpoint; starting from 0.");
        }

        return 0;
    }

    private async Task WriteCheckpointAsync(long value, CancellationToken cancellationToken)
    {
        try
        {
            await _storage.SetSyncStateAsync(
                CheckpointKey,
                value.ToString(CultureInfo.InvariantCulture),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not persist Databricks sync checkpoint.");
        }
    }

    /// <summary>
    /// Builds one or more idempotent MERGE statements from the rollup rows. Each
    /// statement carries at most <see cref="MergeRowsPerStatement"/> rows as a
    /// parameterized VALUES source and upserts on
    /// (machine, target_address, bucket_start).
    /// </summary>
    private List<DatabricksStatement> BuildMergeStatements(List<CheckRollup> rows)
    {
        var statements = new List<DatabricksStatement>();
        var syncedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var columnList = string.Join(", ", ColumnNames);

        for (var offset = 0; offset < rows.Count; offset += MergeRowsPerStatement)
        {
            var count = Math.Min(MergeRowsPerStatement, rows.Count - offset);
            var valuesSql = new StringBuilder();
            var parameters = new List<DatabricksParameter>(count * ColumnNames.Length);
            var marker = 0;

            for (var i = 0; i < count; i++)
            {
                if (i > 0)
                {
                    valuesSql.Append(", ");
                }

                var row = rows[offset + i];

                // One (type, value) pair per column, in ColumnNames order.
                var cells = new (string Type, string? Value)[]
                {
                    ("STRING", _machine),
                    ("BIGINT", Int(row.BucketStartMs)),
                    ("STRING", row.TargetName),
                    ("STRING", row.TargetAddress),
                    ("STRING", row.TargetCategory),
                    ("BIGINT", Int(row.Samples)),
                    ("BIGINT", Int(row.Ok)),
                    ("DOUBLE", Dbl(row.AvgRttMs)),
                    ("BIGINT", Int(row.MinRttMs)),
                    ("BIGINT", Int(row.MaxRttMs)),
                    ("DOUBLE", Dbl(row.AvgJitterMs)),
                    ("DOUBLE", Dbl(row.AvgDnsMs)),
                    ("DOUBLE", Dbl(row.AvgLossPct)),
                    ("BIGINT", Int(row.BucketMinutes)),
                    ("BIGINT", Int(syncedAt)),
                };

                valuesSql.Append('(');
                for (var c = 0; c < cells.Length; c++)
                {
                    if (c > 0)
                    {
                        valuesSql.Append(", ");
                    }

                    var name = string.Concat("p", marker.ToString(CultureInfo.InvariantCulture));
                    valuesSql.Append(':').Append(name);
                    parameters.Add(new DatabricksParameter(name, cells[c].Value, cells[c].Type));
                    marker++;
                }

                valuesSql.Append(')');
            }

            var sql =
                $"MERGE INTO {_qualifiedTable} AS t " +
                $"USING (SELECT * FROM (VALUES {valuesSql}) AS raw({columnList})) AS s " +
                "ON t.machine = s.machine AND t.target_address = s.target_address " +
                "AND t.bucket_start = s.bucket_start " +
                "WHEN MATCHED THEN UPDATE SET * " +
                "WHEN NOT MATCHED THEN INSERT *";

            statements.Add(new DatabricksStatement(sql, parameters));
        }

        return statements;
    }

    private static string Int(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string? Int(long? value) =>
        value is null ? null : value.Value.ToString(CultureInfo.InvariantCulture);

    private static string Dbl(double value) => value.ToString(CultureInfo.InvariantCulture);

    private static string? Dbl(double? value) =>
        value is null ? null : value.Value.ToString(CultureInfo.InvariantCulture);
}
APPLY_FILE_EOF

write "src/NetworkMonitor.Core/ServiceCollectionExtensions.cs" << 'APPLY_FILE_EOF'
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NetworkMonitor.Core.Exporters;
using NetworkMonitor.Core.Models;
using NetworkMonitor.Core.RemoteSync;
using NetworkMonitor.Core.Services;
using NetworkMonitor.Core.Storage;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

namespace NetworkMonitor.Core;

/// <summary>
/// Extension methods for registering Network Monitor services.
/// Encapsulates all the DI wiring in one place.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers all Network Monitor services with the DI container.
    /// </summary>
    public static IServiceCollection AddNetworkMonitor(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Bind options from configuration
        services.Configure<MonitorOptions>(
            configuration.GetSection(MonitorOptions.SectionName));
        services.Configure<StorageOptions>(
            configuration.GetSection(StorageOptions.SectionName));
        services.Configure<RemoteSyncOptions>(
            configuration.GetSection(RemoteSyncOptions.SectionName));
        services.Configure<DatabricksSyncOptions>(
            configuration.GetSection(DatabricksSyncOptions.SectionName));

        // Single synchronized owner of stdout, shared by the status display and
        // the LiveConsole logger provider. TryAdd so it stays a singleton even
        // if AddLiveConsole() already registered it during logging setup.
        services.TryAddSingleton<LiveConsole>();

        // Register core services
        services.AddSingleton<IPingService, PingService>();
        services.AddSingleton<IGatewayDetector, GatewayDetector>();
        services.AddSingleton<IInternetTargetProvider, InternetTargetProvider>();
        services.AddSingleton<INetworkConfigurationService, NetworkConfigurationService>();
        services.AddSingleton<IDnsResolverService, DnsResolverService>();
        services.AddSingleton<INetworkMonitorService, NetworkMonitorService>();
        services.AddSingleton<IStatusDisplay, ConsoleStatusDisplay>();
        services.AddSingleton<IStorageService, SqliteStorageService>();

        // Optional remote sync sinks. Both are no-ops unless their own section is
        // configured, and both run independently of each other and of monitoring.
        //   - Turso / libSQL:  RemoteSync:Url + RemoteSync:AuthToken
        //   - Databricks:      Databricks:WorkspaceUrl + Databricks:WarehouseId + credential
        services.AddSingleton<IRemoteDatabaseClient, TursoHranaClient>();
        services.AddSingleton<IDatabricksClient, DatabricksSqlClient>();

        // Register background services
        services.AddHostedService<MonitorBackgroundService>();
        services.AddHostedService<RemoteSyncService>();
        services.AddHostedService<DatabricksSyncService>();

        return services;
    }

    /// <summary>
    /// Adds OpenTelemetry metrics with file export (always) and console export (opt-in).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="fileOptions">File exporter options.</param>
    /// <param name="enableConsoleExporter">
    /// When false (default), OpenTelemetry metrics are only written to files.
    /// When true, metrics are also dumped to the console (noisy with many targets).
    /// This does NOT affect the status display or database - only the raw
    /// OpenTelemetry histogram/counter output on stdout.
    /// </param>
    public static IServiceCollection AddNetworkMonitorTelemetry(
        this IServiceCollection services,
        FileExporterOptions? fileOptions = null,
        bool enableConsoleExporter = false)
    {
        fileOptions ??= FileExporterOptions.Default;

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(
                    serviceName: "NetworkMonitor",
                    serviceVersion: "1.0.0"))
            .WithMetrics(metrics =>
            {
                metrics
                    .AddMeter("NetworkMonitor.Core")
                    .AddRuntimeInstrumentation()
                    .AddFileExporter(fileOptions);

                // Only add the console exporter when explicitly requested.
                // With dozens of targets, the histogram output every 10 seconds
                // drowns out the actual status display.
                if (enableConsoleExporter)
                {
                    metrics.AddConsoleExporter();
                }
            });

        return services;
    }
}
APPLY_FILE_EOF

write "src/NetworkMonitor.Console/appsettings.json" << 'APPLY_FILE_EOF'
{
  "Logging": {
    "LogLevel": {
      "Default": "Error",
      "Microsoft": "Error",
      "NetworkMonitor": "Error"
    }
  },
  "NetworkMonitor": {
    "RouterAddress": "auto",
    "InternetTarget": "8.8.8.8",
    "TimeoutMs": 3000,
    "IntervalMs": 5000,
    "PingsPerCycle": 3,
    "ExcellentLatencyMs": 20,
    "GoodLatencyMs": 200,
    "DegradedPacketLossPercent": 10,
    "EnableFallbackTargets": true,
    "EnableIPv6": true,
    "EnableDnsChecks": true,
    "QuietConsole": true,
    "MaxConcurrentChecks": 6,
    "CustomTargets": [

      { "Name": "Cloudflare-DNS-2",        "Address": "1.0.0.1",                          "Enabled": true },
      { "Name": "Cloudflare-DNS",          "Address": "1.1.1.1",                          "Enabled": true },
      { "Name": "Quad9",                   "Address": "9.9.9.9",                          "Enabled": true },
      { "Name": "Quad9-Secondary",         "Address": "149.112.112.112",                  "Enabled": true },
      { "Name": "Google-DNS-Secondary",    "Address": "8.8.4.4",                          "Enabled": true },
      { "Name": "OpenDNS-1",               "Address": "208.67.222.222",                   "Enabled": true },
      { "Name": "OpenDNS-2",               "Address": "208.67.220.220",                   "Enabled": true },
      { "Name": "Verisign-DNS-1",          "Address": "64.6.64.6",                        "Enabled": true },
      { "Name": "Verisign-DNS-2",          "Address": "64.6.65.6",                        "Enabled": true },
      { "Name": "Level3-1",                "Address": "4.2.2.1",                          "Enabled": true },
      { "Name": "Level3-2",                "Address": "4.2.2.2",                          "Enabled": true },
      { "Name": "Level3-3",                "Address": "4.2.2.3",                          "Enabled": true },
      { "Name": "CleanBrowsing",           "Address": "185.228.168.9",                    "Enabled": true },
      { "Name": "Control D (Unfiltered)",  "Address": "76.76.2.0",                        "Enabled": true },
      { "Name": "AdGuard-DNS",             "Address": "94.140.14.14",                     "Enabled": true },
      { "Name": "NextDNS",                 "Address": "45.90.28.0",                       "Enabled": true },

      { "Name": "Cloudflare-DNS-Host",     "Address": "one.one.one.one",                  "Enabled": true },
      { "Name": "Google-DNS-Host",         "Address": "dns.google",                       "Enabled": true },
      { "Name": "Quad9-Host",              "Address": "dns.quad9.net",                    "Enabled": true },
      { "Name": "OpenDNS-Host",            "Address": "resolver1.opendns.com",            "Enabled": true },
      { "Name": "Cloudflare-Host",         "Address": "cloudflare.com",                   "Enabled": true },
      { "Name": "Fastly-CDN",              "Address": "fastly.com",                       "Enabled": true },

      { "Name": "MS-Teams",                "Address": "teams.microsoft.com",              "Enabled": true },
      { "Name": "MS-Azure-Management",     "Address": "management.azure.com",             "Enabled": true },
      { "Name": "MS-Office365",            "Address": "outlook.office365.com",            "Enabled": true },
      { "Name": "MS-OneDrive",             "Address": "onedrive.live.com",                "Enabled": true },
      { "Name": "MS-SharePoint",           "Address": "sharepoint.com",                   "Enabled": true },
      { "Name": "MS-Bing",                 "Address": "www.bing.com",                     "Enabled": true },

      { "Name": "Google-Host",             "Address": "www.google.com",                   "Enabled": true },
      { "Name": "Google-Workspace",        "Address": "mail.google.com",                  "Enabled": true },
      { "Name": "Google-Cloud",            "Address": "cloud.google.com",                 "Enabled": true },
      { "Name": "Google-APIs",             "Address": "googleapis.com",                   "Enabled": true },
      { "Name": "YouTube",                 "Address": "www.youtube.com",                  "Enabled": true },

      { "Name": "AWS-Host",                "Address": "aws.amazon.com",                   "Enabled": true },
      { "Name": "AWS-S3",                  "Address": "s3.amazonaws.com",                 "Enabled": true },

      { "Name": "GitHub",                  "Address": "github.com",                       "Enabled": true },
      { "Name": "GitHub-API",              "Address": "api.github.com",                   "Enabled": true },
      { "Name": "GitLab",                  "Address": "gitlab.com",                       "Enabled": true },
      { "Name": "NPM-Registry",            "Address": "registry.npmjs.org",               "Enabled": true },
      { "Name": "PyPI",                    "Address": "pypi.org",                         "Enabled": true },
      { "Name": "DockerHub",               "Address": "hub.docker.com",                   "Enabled": true },

      { "Name": "Cloudflare-WARP",         "Address": "engage.cloudflareclient.com",      "Enabled": true },
      { "Name": "Akamai-Host",             "Address": "akamai.com",                       "Enabled": true },

      { "Name": "Zoom",                    "Address": "zoom.us",                          "Enabled": true },
      { "Name": "Zoom-CDN",               "Address": "cdn.zoom.us",                       "Enabled": true },
      { "Name": "Dropbox",                 "Address": "www.dropbox.com",                  "Enabled": true },

      { "Name": "Cloudflare-Radar",        "Address": "radar.cloudflare.com",             "Enabled": true },
      { "Name": "Internet-NL",             "Address": "internet.nl",                      "Enabled": true }

    ],
    "DisabledChecks": []
  },
  "Storage": {
    "ApplicationName": "NetworkMonitor",
    "DatabaseFileName": "network-monitor.db",
    "DataDirectoryOverride": "",
    "RetentionDays": 30
  },
  "RemoteSync": {
    "Url": "",
    "AuthToken": "",
    "Mode": "rollup",
    "BucketMinutes": 60,
    "SyncIntervalMinutes": 60,
    "InitialDelaySeconds": 60,
    "BatchSize": 500,
    "MaxRowsPerSync": 25000,
    "RequestTimeoutSeconds": 30,
    "TableName": "check_rollups"
  },
  "Databricks": {
    "WorkspaceUrl": "",
    "WarehouseId": "",
    "Catalog": "workspace",
    "Schema": "default",
    "TableName": "network_monitor_rollups",
    "Token": "",
    "ClientId": "",
    "ClientSecret": "",
    "OAuthScope": "all-apis",
    "BucketMinutes": 60,
    "SyncIntervalMinutes": 60,
    "InitialDelaySeconds": 90,
    "BatchSize": 200,
    "MaxRowsPerSync": 25000,
    "RequestTimeoutSeconds": 120,
    "StatementWaitSeconds": 30
  }
}
APPLY_FILE_EOF

write "src/NetworkMonitor.Tests/Fakes/FakeDatabricksClient.cs" << 'APPLY_FILE_EOF'
using NetworkMonitor.Core.RemoteSync;

namespace NetworkMonitor.Tests.Fakes;

/// <summary>
/// In-memory fake of <see cref="IDatabricksClient"/> for testing the Databricks
/// sync service without any network access. It records every batch of statements
/// it is asked to execute and can be toggled to report "not configured" or to
/// fail its calls (simulating a network, auth, or statement error).
/// </summary>
internal sealed class FakeDatabricksClient : IDatabricksClient
{
    // Each rollup MERGE statement carries 15 named parameters per row.
    private const int ParametersPerRollupRow = 15;

    private readonly List<IReadOnlyList<DatabricksStatement>> _batches = new();

    /// <inheritdoc />
    public bool IsConfigured { get; set; } = true;

    /// <summary>
    /// When false, <see cref="ExecuteAsync"/> returns false without recording
    /// anything, simulating a failed call.
    /// </summary>
    public bool SucceedCalls { get; set; } = true;

    /// <summary>Total batches this client was asked to execute, including failures.</summary>
    public int CallCount { get; private set; }

    /// <summary>Every batch that was executed successfully, in order.</summary>
    public IReadOnlyList<IReadOnlyList<DatabricksStatement>> ExecutedBatches => _batches;

    public Task<bool> ExecuteAsync(
        IReadOnlyList<DatabricksStatement> statements,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(statements);
        CallCount++;

        if (!SucceedCalls)
        {
            return Task.FromResult(false);
        }

        _batches.Add(statements);
        return Task.FromResult(true);
    }

    /// <summary>
    /// Total number of rollup rows merged across every successful batch. Each
    /// MERGE statement carries 15 named parameters per row, so the row count is
    /// the parameter count divided by 15.
    /// </summary>
    public int TotalMergedRows => _batches
        .SelectMany(b => b)
        .Where(s => s.Sql.StartsWith("MERGE", StringComparison.OrdinalIgnoreCase))
        .Sum(s => s.Parameters.Count / ParametersPerRollupRow);
}
APPLY_FILE_EOF

write "src/NetworkMonitor.Tests/RemoteSync/DatabricksSyncServiceTests.cs" << 'APPLY_FILE_EOF'
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NetworkMonitor.Core.Models;
using NetworkMonitor.Core.RemoteSync;
using NetworkMonitor.Tests.Fakes;
using Xunit;

namespace NetworkMonitor.Tests.RemoteSync;

/// <summary>
/// Tests for DatabricksSyncService.SyncOnceAsync, driven with in-memory fakes so
/// no network access is required. The service is the Databricks counterpart of
/// RemoteSyncService and behaves identically at the sync-loop level: it
/// replicates per-target, per-bucket ROLLUPS for fully-elapsed buckets only, so
/// tests seed measurements into past buckets and expect one rollup row per
/// (bucket, target). It uses its OWN checkpoint key, independent of the Turso
/// sink.
/// </summary>
public sealed class DatabricksSyncServiceTests
{
    private const string CheckpointKey = "databricks_rollup_next_bucket_ms";
    private const long BucketMs = 60L * 60L * 1000L; // 60-minute buckets (the default)

    private readonly FakeStorageService _storage = new();
    private readonly FakeDatabricksClient _client = new();

    private static DatabricksSyncOptions ConfiguredOptions() => new()
    {
        WorkspaceUrl = "https://dbc-abc123.cloud.databricks.com",
        WarehouseId = "abc123warehouse",
        Catalog = "workspace",
        Schema = "default",
        TableName = "network_monitor_rollups",
        Token = "test-pat",
        InitialDelaySeconds = 0,
        BucketMinutes = 60,
        BatchSize = 200,
        MaxRowsPerSync = 25000
    };

    private DatabricksSyncService CreateService(DatabricksSyncOptions options)
    {
        return new DatabricksSyncService(
            _client,
            _storage,
            Options.Create(options),
            NullLogger<DatabricksSyncService>.Instance);
    }

    private static long CurrentBucketStart()
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return nowMs - (nowMs % BucketMs);
    }

    private async Task SeedStatusAsync(long tsMs, long routerLatency, long internetLatency)
    {
        var ts = DateTimeOffset.FromUnixTimeMilliseconds(tsMs);
        var results = new List<TargetCheckResult>
        {
            new(
                new MonitorTarget("Router", "192.168.1.1", TargetCategory.Router),
                PingResult.Succeeded("192.168.1.1", routerLatency),
                null, null, 0, ts, routerLatency, routerLatency, 0),
            new(
                new MonitorTarget("Internet", "8.8.8.8", TargetCategory.PublicDns),
                PingResult.Succeeded("8.8.8.8", internetLatency),
                null, null, 0, ts, internetLatency, internetLatency, 0),
        };

        var status = new NetworkStatus(
            NetworkHealth.Good, results[0].PingResult, results[1].PingResult,
            ts, "seed", results);

        await _storage.SaveStatusAsync(status, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Seeds 2 cycles in one past bucket and 1 in another, giving 2 buckets ×
    /// 2 targets = 4 rollup rows once both buckets are closed.
    /// </summary>
    private async Task SeedTwoClosedBucketsAsync()
    {
        var current = CurrentBucketStart();
        var bucketA = current - (3 * BucketMs); // 3 buckets ago
        var bucketB = current - (2 * BucketMs); // 2 buckets ago

        await SeedStatusAsync(bucketA + (10 * 60_000L), 5, 10);
        await SeedStatusAsync(bucketA + (20 * 60_000L), 6, 11);
        await SeedStatusAsync(bucketB + (15 * 60_000L), 7, 12);
    }

    [Fact]
    public async Task SyncOnceAsync_PushesClosedBucketRollups_AndAdvancesCheckpoint()
    {
        await SeedTwoClosedBucketsAsync();
        var service = CreateService(ConfiguredOptions());

        var synced = await service.SyncOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(4, synced);
        Assert.Equal(4, _client.TotalMergedRows);

        var checkpoint = await _storage.GetSyncStateAsync(CheckpointKey, TestContext.Current.CancellationToken);
        Assert.NotNull(checkpoint);
    }

    [Fact]
    public async Task SyncOnceAsync_SecondPass_PushesNothingNew()
    {
        await SeedTwoClosedBucketsAsync();
        var service = CreateService(ConfiguredOptions());

        var first = await service.SyncOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(4, first);

        var second = await service.SyncOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, second);
        Assert.Equal(4, _client.TotalMergedRows);
    }

    [Fact]
    public async Task SyncOnceAsync_WhenRemoteFails_LeavesCheckpointUnchanged()
    {
        await SeedTwoClosedBucketsAsync();
        _client.SucceedCalls = false;
        var service = CreateService(ConfiguredOptions());

        var synced = await service.SyncOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, synced);
        Assert.Empty(_client.ExecutedBatches);

        var checkpoint = await _storage.GetSyncStateAsync(CheckpointKey, TestContext.Current.CancellationToken);
        Assert.Null(checkpoint);
    }

    [Fact]
    public async Task SyncOnceAsync_ThenSucceeds_RetriesSameRollups()
    {
        // Fail first (even ensuring the schema fails), then recover.
        await SeedTwoClosedBucketsAsync();
        _client.SucceedCalls = false;
        var service = CreateService(ConfiguredOptions());

        var failed = await service.SyncOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, failed);

        _client.SucceedCalls = true;

        var recovered = await service.SyncOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(4, recovered);
        Assert.Equal(4, _client.TotalMergedRows);
    }

    [Fact]
    public async Task SyncOnceAsync_WhenNotConfigured_ReturnsZeroAndDoesNothing()
    {
        await SeedTwoClosedBucketsAsync();
        var options = new DatabricksSyncOptions { WorkspaceUrl = "", WarehouseId = "", Token = "" };
        var service = CreateService(options);

        var synced = await service.SyncOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, synced);
        Assert.Equal(0, _client.CallCount);
    }

    [Fact]
    public async Task SyncOnceAsync_WhenClientNotConfigured_ReturnsZero()
    {
        // Options look configured, but the client reports otherwise (e.g. the URL
        // failed to parse into a valid endpoint).
        await SeedTwoClosedBucketsAsync();
        _client.IsConfigured = false;
        var service = CreateService(ConfiguredOptions());

        var synced = await service.SyncOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, synced);
        Assert.Equal(0, _client.CallCount);
    }

    [Fact]
    public async Task SyncOnceAsync_WithNoRows_ReturnsZero()
    {
        var service = CreateService(ConfiguredOptions());

        var synced = await service.SyncOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, synced);
        Assert.Equal(0, _client.TotalMergedRows);
    }

    [Fact]
    public async Task SyncOnceAsync_EnsuresRemoteSchemaOnce()
    {
        await SeedTwoClosedBucketsAsync();
        var service = CreateService(ConfiguredOptions());

        await service.SyncOnceAsync(TestContext.Current.CancellationToken);

        var allStatements = _client.ExecutedBatches.SelectMany(b => b).ToList();
        Assert.Contains(allStatements, s => s.Sql.Contains("CREATE TABLE IF NOT EXISTS", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(allStatements, s => s.Sql.Contains("USING DELTA", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SyncOnceAsync_DoesNotResendSchema_OnSecondPass()
    {
        await SeedTwoClosedBucketsAsync();
        var service = CreateService(ConfiguredOptions());
        await service.SyncOnceAsync(TestContext.Current.CancellationToken);

        var createAfterFirst = _client.ExecutedBatches
            .SelectMany(b => b)
            .Count(s => s.Sql.Contains("CREATE TABLE IF NOT EXISTS", StringComparison.OrdinalIgnoreCase));

        await service.SyncOnceAsync(TestContext.Current.CancellationToken);

        var createAfterSecond = _client.ExecutedBatches
            .SelectMany(b => b)
            .Count(s => s.Sql.Contains("CREATE TABLE IF NOT EXISTS", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(1, createAfterFirst);
        Assert.Equal(createAfterFirst, createAfterSecond);
    }

    [Fact]
    public async Task SyncOnceAsync_MergeUsesNamedParameters_AndIsIdempotentUpsert()
    {
        await SeedTwoClosedBucketsAsync();
        var service = CreateService(ConfiguredOptions());

        await service.SyncOnceAsync(TestContext.Current.CancellationToken);

        var merge = _client.ExecutedBatches
            .SelectMany(b => b)
            .First(s => s.Sql.StartsWith("MERGE", StringComparison.OrdinalIgnoreCase));

        // Upsert semantics on the composite key.
        Assert.Contains("WHEN MATCHED THEN UPDATE SET *", merge.Sql, StringComparison.Ordinal);
        Assert.Contains("WHEN NOT MATCHED THEN INSERT *", merge.Sql, StringComparison.Ordinal);
        Assert.Contains("t.target_address = s.target_address", merge.Sql, StringComparison.Ordinal);

        // Every parameter is named and referenced by a :marker in the SQL, and
        // every marker has a matching parameter definition.
        Assert.NotEmpty(merge.Parameters);
        foreach (var p in merge.Parameters)
        {
            Assert.False(string.IsNullOrWhiteSpace(p.Name));
            Assert.Contains(":" + p.Name, merge.Sql, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(p.Type));
        }

        // 15 columns per row, and the row count divides evenly.
        Assert.Equal(0, merge.Parameters.Count % 15);
    }

    [Fact]
    public void TryBuildWorkspaceBase_NormalizesSchemelessHostToHttps()
    {
        var ok = DatabricksSqlClient.TryBuildWorkspaceBase("dbc-abc123.cloud.databricks.com", out var baseUri);

        Assert.True(ok);
        Assert.NotNull(baseUri);
        Assert.Equal("https", baseUri!.Scheme);
        Assert.Equal("/", baseUri.AbsolutePath);
    }

    [Fact]
    public void TryBuildWorkspaceBase_RejectsEmptyOrGarbage()
    {
        Assert.False(DatabricksSqlClient.TryBuildWorkspaceBase("", out _));
        Assert.False(DatabricksSqlClient.TryBuildWorkspaceBase("   ", out _));
        Assert.False(DatabricksSqlClient.TryBuildWorkspaceBase("ftp://nope", out _));
    }

    [Fact]
    public void SanitizeIdentifier_StripsUnsafeCharsAndFallsBack()
    {
        Assert.Equal("my_table", DatabricksSqlClient.SanitizeIdentifier("my-table!", "fallback"));
        Assert.Equal("fallback", DatabricksSqlClient.SanitizeIdentifier("", "fallback"));
        Assert.Equal("fallback", DatabricksSqlClient.SanitizeIdentifier("123", "fallback"));
    }
}
APPLY_FILE_EOF

# --- optional build + test --------------------------------------------------

echo ""
echo "==> Files applied."

if command -v dotnet >/dev/null 2>&1; then
  echo "==> Building solution"
  if dotnet build src/NetworkMonitor.slnx; then
    echo "==> Running tests (.NET 10 MTP mode)"
    dotnet test --solution src/NetworkMonitor.slnx || {
      echo "WARN: tests reported failures; review output above." >&2
    }
  else
    echo "WARN: build failed; review output above." >&2
  fi
else
  echo "NOTE: dotnet SDK not found on PATH; skipped build/test."
  echo "      Build locally with:  dotnet build src/NetworkMonitor.slnx"
  echo "      Test locally with:   dotnet test --solution src/NetworkMonitor.slnx"
fi

# --- summary ----------------------------------------------------------------

cat << "SUMMARY"

============================================================
 Databricks sync applied.
============================================================
 New files:
   src/NetworkMonitor.Core/Models/DatabricksSyncOptions.cs
   src/NetworkMonitor.Core/RemoteSync/DatabricksStatement.cs
   src/NetworkMonitor.Core/RemoteSync/IDatabricksClient.cs
   src/NetworkMonitor.Core/RemoteSync/DatabricksSqlClient.cs
   src/NetworkMonitor.Core/RemoteSync/DatabricksSyncService.cs
   src/NetworkMonitor.Tests/Fakes/FakeDatabricksClient.cs
   src/NetworkMonitor.Tests/RemoteSync/DatabricksSyncServiceTests.cs
 Modified files:
   src/NetworkMonitor.Core/ServiceCollectionExtensions.cs
   src/NetworkMonitor.Console/appsettings.json

 To enable: set Databricks:WorkspaceUrl, Databricks:WarehouseId, and
 either Databricks:Token (PAT) or Databricks:ClientId + Databricks:ClientSecret.
 Prefer environment variables over committing secrets:
   Databricks__WorkspaceUrl, Databricks__WarehouseId,
   Databricks__ClientId, Databricks__ClientSecret
 Leave them blank and the sink stays dormant (no-op).
============================================================
SUMMARY
