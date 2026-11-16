using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetworkMonitor.Core.Configuration;

namespace NetworkMonitor.Core.Sync.Databricks;

public sealed record DatabricksParameter(string Name, string? Value, string Type);

public sealed record DatabricksStatement(string Sql, IReadOnlyList<DatabricksParameter> Parameters);

public interface IDatabricksClient
{
    bool IsConfigured { get; }

    Task<bool> ExecuteAsync(IReadOnlyList<DatabricksStatement> statements, CancellationToken cancellationToken);
}

public sealed class DatabricksClient : IDatabricksClient, IDisposable
{
    private const string StatementsPath = "api/2.0/sql/statements";
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(1500);

    private readonly DatabricksSyncOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DatabricksClient> _logger;
    private readonly HttpClient _http;
    private readonly Uri? _statements;
    private readonly Uri? _tokenEndpoint;
    private readonly string _catalog;
    private readonly string _schema;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _cachedToken;
    private DateTimeOffset _tokenExpiresAt = DateTimeOffset.MinValue;

    public DatabricksClient(IOptions<DatabricksSyncOptions> options, TimeProvider timeProvider, ILogger<DatabricksClient> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
        _catalog = SqlIdentifier.Sanitize(_options.Catalog, "workspace", dashToUnderscore: true);
        _schema = SqlIdentifier.Sanitize(_options.Schema, "default", dashToUnderscore: true);

        if ((_options.HasToken || _options.HasOAuthCredentials) &&
            !string.IsNullOrWhiteSpace(_options.WarehouseId) &&
            TryBuildWorkspaceBase(_options.WorkspaceUrl, out var baseUri))
        {
            _statements = new Uri(baseUri, StatementsPath);
            _tokenEndpoint = new Uri(baseUri, "oidc/v1/token");
        }

        _http = SyncHttp.CreateClient(TimeSpan.FromSeconds(Math.Clamp(_options.RequestTimeoutSeconds, 60, 600)));
    }

    public bool IsConfigured => _statements is not null;

    private TimeSpan StatementDeadline => TimeSpan.FromSeconds(Math.Clamp(_options.RequestTimeoutSeconds, 30, 600));

    public async Task<bool> ExecuteAsync(IReadOnlyList<DatabricksStatement> statements, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(statements);
        if (_statements is null || statements.Count == 0 || await GetBearerTokenAsync(cancellationToken) is not { } token)
        {
            return false;
        }

        foreach (var statement in statements)
        {
            if (!await ExecuteOneAsync(statement, token, cancellationToken))
            {
                return false;
            }
        }

        return true;
    }

    public void Dispose()
    {
        _http.Dispose();
        _tokenLock.Dispose();
    }

    public static bool TryBuildWorkspaceBase(string? url, [NotNullWhen(true)] out Uri? baseUri)
    {
        baseUri = null;
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        var normalized = url.Trim();
        if (!normalized.Contains("://", StringComparison.Ordinal))
        {
            normalized = "https://" + normalized;
        }

        if (!SyncHttp.TryParseHttpUri(normalized, out var parsed))
        {
            return false;
        }

        baseUri = new UriBuilder(parsed.Scheme, parsed.Host, parsed.IsDefaultPort ? -1 : parsed.Port, "/").Uri;
        return true;
    }

    internal string BuildPayload(DatabricksStatement statement)
    {
        var root = new JsonObject
        {
            ["warehouse_id"] = _options.WarehouseId,
            ["catalog"] = _catalog,
            ["schema"] = _schema,
            ["statement"] = statement.Sql,
            ["wait_timeout"] = string.Create(CultureInfo.InvariantCulture, $"{_options.StatementWaitSeconds}s"),
            ["on_wait_timeout"] = "CONTINUE",
            ["format"] = "JSON_ARRAY",
            ["disposition"] = "INLINE",
        };

        if (statement.Parameters.Count > 0)
        {
            var parameters = new JsonArray();
            foreach (var p in statement.Parameters)
            {
                var node = new JsonObject { ["name"] = p.Name, ["type"] = p.Type };
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

    private async Task<bool> ExecuteOneAsync(DatabricksStatement statement, string token, CancellationToken cancellationToken)
    {
        var started = _timeProvider.GetTimestamp();
        try
        {
            var status = await SendAsync(HttpMethod.Post, _statements!, token, SyncHttp.Json(BuildPayload(statement)), cancellationToken);

            while (true)
            {
                switch (status.State)
                {
                    case "SUCCEEDED":
                        return true;
                    case "PENDING" or "RUNNING":
                        break;
                    default:
                        _logger.LogDebug("Databricks statement ended in state {State}: {Error}", status.State ?? "unknown", status.Error ?? "no error detail");
                        return false;
                }

                if (status.StatementId is not { } id || _timeProvider.GetElapsedTime(started) >= StatementDeadline)
                {
                    _logger.LogDebug("Databricks statement did not complete before the deadline");
                    if (status.StatementId is { } pending)
                    {
                        await SendAsync(HttpMethod.Post, StatementUri(pending, "/cancel"), token, null, cancellationToken);
                    }

                    return false;
                }

                await Task.Delay(PollInterval, _timeProvider, cancellationToken);
                status = await SendAsync(HttpMethod.Get, StatementUri(id, string.Empty), token, null, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Databricks statement execution failed");
            return false;
        }
    }

    private Uri StatementUri(string statementId, string suffix) =>
        new($"{_statements!.AbsoluteUri}/{Uri.EscapeDataString(statementId)}{suffix}");

    private async Task<StatementStatus> SendAsync(HttpMethod method, Uri uri, string token, HttpContent? content, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, uri) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _http.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogDebug("Databricks returned HTTP {StatusCode} for {Method} {Path}", (int)response.StatusCode, method, uri.AbsolutePath);
            return new StatementStatus(null, null, null);
        }

        return StatementStatus.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
    }

    private async Task<string?> GetBearerTokenAsync(CancellationToken cancellationToken)
    {
        if (_options.HasToken)
        {
            return _options.Token;
        }

        if (!_options.HasOAuthCredentials || _tokenEndpoint is null)
        {
            return null;
        }

        await _tokenLock.WaitAsync(cancellationToken);
        try
        {
            if (_cachedToken is null || _timeProvider.GetUtcNow() >= _tokenExpiresAt)
            {
                await RefreshTokenAsync(cancellationToken);
            }

            return _cachedToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private async Task RefreshTokenAsync(CancellationToken cancellationToken)
    {
        _cachedToken = null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _tokenEndpoint)
            {
                Content = new FormUrlEncodedContent(
                [
                    new("grant_type", "client_credentials"),
                    new("scope", string.IsNullOrWhiteSpace(_options.OAuthScope) ? "all-apis" : _options.OAuthScope),
                ]),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_options.ClientId}:{_options.ClientSecret}")));

            using var response = await _http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Databricks OAuth token endpoint returned HTTP {StatusCode}", (int)response.StatusCode);
                return;
            }

            var root = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var accessToken = root?["access_token"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                _logger.LogDebug("Databricks OAuth response did not contain an access token");
                return;
            }

            var expiresIn = int.TryParse(root?["expires_in"]?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) ? seconds : 3600;
            _cachedToken = accessToken;
            _tokenExpiresAt = _timeProvider.GetUtcNow().AddSeconds(Math.Max(30, expiresIn - 60));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Databricks OAuth token request failed");
        }
    }

    private sealed record StatementStatus(string? State, string? StatementId, string? Error)
    {
        public static StatementStatus Parse(string body)
        {
            try
            {
                var root = JsonNode.Parse(body);
                return new StatementStatus(
                    root?["status"]?["state"]?.GetValue<string>()?.ToUpperInvariant(),
                    root?["statement_id"]?.GetValue<string>(),
                    root?["status"]?["error"]?["message"]?.GetValue<string>());
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException)
            {
                return new StatementStatus(null, null, null);
            }
        }
    }
}
