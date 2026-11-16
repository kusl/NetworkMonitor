using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetworkMonitor.Core.Configuration;

namespace NetworkMonitor.Core.Sync.Turso;

public sealed record HranaStatement(string Sql, IReadOnlyList<SqlValue> Args);

public interface IHranaClient
{
    bool IsConfigured { get; }

    Task<bool> ExecuteAsync(IReadOnlyList<HranaStatement> statements, CancellationToken cancellationToken);
}

public sealed class HranaClient : IHranaClient, IDisposable
{
    private static readonly (string From, string To)[] SchemeAliases =
    [
        ("libsql://", "https://"),
        ("wss://", "https://"),
        ("ws://", "http://"),
    ];

    private readonly ILogger<HranaClient> _logger;
    private readonly HttpClient _http;
    private readonly Uri? _endpoint;
    private readonly string _authToken;

    public HranaClient(IOptions<RemoteSyncOptions> options, ILogger<HranaClient> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        var settings = options.Value;
        _logger = logger;
        _authToken = settings.AuthToken;
        _endpoint = !string.IsNullOrWhiteSpace(_authToken) && TryBuildPipelineEndpoint(settings.Url, out var endpoint) ? endpoint : null;
        _http = SyncHttp.CreateClient(TimeSpan.FromSeconds(Math.Clamp(settings.RequestTimeoutSeconds, 5, 300)));
    }

    public bool IsConfigured => _endpoint is not null;

    public async Task<bool> ExecuteAsync(IReadOnlyList<HranaStatement> statements, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(statements);
        if (_endpoint is null || statements.Count == 0)
        {
            return false;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
            {
                Content = SyncHttp.Json(BuildPipeline(statements)),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _authToken);

            using var response = await _http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Remote database returned HTTP {StatusCode}", (int)response.StatusCode);
                return false;
            }

            return AllSucceeded(await response.Content.ReadAsStringAsync(cancellationToken));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Remote database pipeline request failed");
            return false;
        }
    }

    public void Dispose() => _http.Dispose();

    public static bool TryBuildPipelineEndpoint(string? url, out Uri? endpoint)
    {
        endpoint = null;
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        var normalized = url.Trim();
        foreach (var (from, to) in SchemeAliases)
        {
            if (normalized.StartsWith(from, StringComparison.OrdinalIgnoreCase))
            {
                normalized = string.Concat(to, normalized.AsSpan(from.Length));
                break;
            }
        }

        if (!SyncHttp.TryParseHttpUri(normalized, out var parsed))
        {
            return false;
        }

        endpoint = new UriBuilder(parsed)
        {
            Path = string.Concat(parsed.AbsolutePath.TrimEnd('/'), "/v2/pipeline"),
            Query = string.Empty,
            Fragment = string.Empty,
        }.Uri;
        return true;
    }

    internal static string BuildPipeline(IReadOnlyList<HranaStatement> statements)
    {
        var requests = new JsonArray();
        foreach (var statement in statements)
        {
            var args = new JsonArray();
            foreach (var arg in statement.Args)
            {
                args.Add(Encode(arg));
            }

            requests.Add(new JsonObject
            {
                ["type"] = "execute",
                ["stmt"] = new JsonObject { ["sql"] = statement.Sql, ["args"] = args },
            });
        }

        requests.Add(new JsonObject { ["type"] = "close" });
        return new JsonObject { ["requests"] = requests }.ToJsonString();
    }

    private static JsonObject Encode(SqlValue value) => value switch
    {
        long l => new JsonObject { ["type"] = "integer", ["value"] = l.ToString(CultureInfo.InvariantCulture) },
        double d => new JsonObject { ["type"] = "float", ["value"] = d },
        string s => new JsonObject { ["type"] = "text", ["value"] = s },
        null => new JsonObject { ["type"] = "null" },
    };

    private bool AllSucceeded(string body)
    {
        try
        {
            if (JsonNode.Parse(body)?["results"] is not JsonArray results)
            {
                _logger.LogDebug("Remote database response contained no results array");
                return false;
            }

            foreach (var result in results)
            {
                if (string.Equals(result?["type"]?.GetValue<string>(), "error", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogDebug("Remote database statement error: {Message}", result?["error"]?["message"]?.GetValue<string>() ?? "unknown error");
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            _logger.LogDebug(ex, "Could not parse remote database response");
            return false;
        }
    }
}
