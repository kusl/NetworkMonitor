using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetworkMonitor.Core.Models;

namespace NetworkMonitor.Core.RemoteSync;

/// <summary>
/// libSQL / Turso client that speaks the HTTP "Hrana" pipeline protocol
/// (<c>POST {base}/v2/pipeline</c>) with bearer-token authentication.
/// </summary>
/// <remarks>
/// Registered as a singleton so it can own a single long-lived
/// <see cref="HttpClient"/> (no <c>Microsoft.Extensions.Http</c> dependency).
/// It is deliberately fault tolerant:
/// <list type="bullet">
///   <item>A missing or malformed URL/token leaves <see cref="IsConfigured"/> false.</item>
///   <item>HTTP, protocol, and statement errors are logged at debug and reported
///         as a failed pipeline execution rather than thrown.</item>
/// </list>
/// Works with any provider exposing the same endpoint shape, not just Turso.
/// </remarks>
public sealed class TursoHranaClient : IRemoteDatabaseClient, IDisposable
{
    private readonly ILogger<TursoHranaClient> _logger;
    private readonly HttpClient _http;
    private readonly Uri? _endpoint;
    private readonly string _authToken;

    public TursoHranaClient(
        IOptions<RemoteSyncOptions> options,
        ILogger<TursoHranaClient> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger;

        var opts = options.Value;
        _authToken = opts.AuthToken ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(_authToken) &&
            TryBuildPipelineEndpoint(opts.Url, out var endpoint))
        {
            _endpoint = endpoint;
        }

        var handler = new HttpClientHandler { CheckCertificateRevocationList = true };
        _http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(Math.Clamp(opts.RequestTimeoutSeconds, 5, 300)),
        };
    }

    /// <inheritdoc />
    public bool IsConfigured => _endpoint is not null;

    /// <inheritdoc />
    public async Task<bool> ExecutePipelineAsync(
        IReadOnlyList<HranaStatement> statements,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(statements);

        if (_endpoint is null || statements.Count == 0)
        {
            return false;
        }

        try
        {
            var payload = BuildPipelinePayload(statements);

            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _authToken);

            using var response = await _http.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug(
                    "Remote database returned HTTP {StatusCode} for pipeline request.",
                    (int)response.StatusCode);
                return false;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return AllStatementsSucceeded(body);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Any failure here is non-fatal: log quietly and let the caller retry later.
            _logger.LogDebug(ex, "Remote database pipeline request failed.");
            return false;
        }
    }

    /// <summary>
    /// Normalizes a database URL to an absolute HTTP(S) pipeline endpoint.
    /// Accepts <c>libsql://</c>, <c>wss://</c>, <c>ws://</c>, <c>https://</c>,
    /// and <c>http://</c> schemes. Returns false for anything unusable.
    /// </summary>
    public static bool TryBuildPipelineEndpoint(string? url, out Uri? endpoint)
    {
        endpoint = null;

        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        var normalized = url.Trim();

        // Map libSQL / websocket schemes onto their HTTP equivalents.
        if (normalized.StartsWith("libsql://", StringComparison.OrdinalIgnoreCase))
        {
            normalized = string.Concat("https://", normalized.AsSpan("libsql://".Length));
        }
        else if (normalized.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
        {
            normalized = string.Concat("https://", normalized.AsSpan("wss://".Length));
        }
        else if (normalized.StartsWith("ws://", StringComparison.OrdinalIgnoreCase))
        {
            normalized = string.Concat("http://", normalized.AsSpan("ws://".Length));
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

        var basePath = parsed.AbsolutePath.TrimEnd('/');
        var builder = new UriBuilder(parsed)
        {
            Path = string.Concat(basePath, "/v2/pipeline"),
            Query = string.Empty,
            Fragment = string.Empty,
        };

        endpoint = builder.Uri;
        return true;
    }

    private static string BuildPipelinePayload(IReadOnlyList<HranaStatement> statements)
    {
        var requests = new JsonArray();

        foreach (var statement in statements)
        {
            var args = new JsonArray();
            foreach (var arg in statement.Args)
            {
                args.Add(EncodeArgument(arg));
            }

            requests.Add(new JsonObject
            {
                ["type"] = "execute",
                ["stmt"] = new JsonObject
                {
                    ["sql"] = statement.Sql,
                    ["args"] = args,
                },
            });
        }

        // Close the implicit stream so the server does not keep it open.
        requests.Add(new JsonObject { ["type"] = "close" });

        var root = new JsonObject { ["requests"] = requests };
        return root.ToJsonString();
    }

    private static JsonObject EncodeArgument(object? value)
    {
        return value switch
        {
            null => new JsonObject { ["type"] = "null" },
            bool b => new JsonObject { ["type"] = "integer", ["value"] = b ? "1" : "0" },
            long l => new JsonObject
            {
                ["type"] = "integer",
                ["value"] = l.ToString(CultureInfo.InvariantCulture),
            },
            int i => new JsonObject
            {
                ["type"] = "integer",
                ["value"] = ((long)i).ToString(CultureInfo.InvariantCulture),
            },
            double d => new JsonObject { ["type"] = "float", ["value"] = JsonValue.Create(d) },
            string s => new JsonObject { ["type"] = "text", ["value"] = s },
            _ => new JsonObject
            {
                ["type"] = "text",
                ["value"] = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
            },
        };
    }

    private bool AllStatementsSucceeded(string responseBody)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(responseBody);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not parse remote database response.");
            return false;
        }

        if (root?["results"] is not JsonArray results)
        {
            // No structured results to inspect; treat as a soft failure so we retry.
            _logger.LogDebug("Remote database response contained no results array.");
            return false;
        }

        foreach (var result in results)
        {
            var type = result?["type"]?.GetValue<string>();
            if (string.Equals(type, "error", StringComparison.OrdinalIgnoreCase))
            {
                var message = result?["error"]?["message"]?.GetValue<string>() ?? "unknown error";
                _logger.LogDebug("Remote database statement error: {Message}", message);
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _http.Dispose();
    }
}
