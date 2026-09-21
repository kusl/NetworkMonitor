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
    public static string SanitizeIdentifier(string? name, string fallback)
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
