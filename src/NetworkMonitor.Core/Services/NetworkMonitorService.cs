using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetworkMonitor.Core.Models;

namespace NetworkMonitor.Core.Services;

/// <summary>
/// Main network monitoring service.
/// Coordinates ping operations and computes overall network health.
/// Exposes OpenTelemetry metrics for observability.
/// </summary>
public sealed class NetworkMonitorService : INetworkMonitorService
{
    private static readonly ActivitySource ActivitySource = new("NetworkMonitor.Core");
    private static readonly Meter Meter = new("NetworkMonitor.Core");

    // Metrics
    private static readonly Counter<long> _checkCounter = Meter.CreateCounter<long>(
        "network_monitor.checks",
        description: "Number of network health checks performed");

    private static readonly Histogram<double> _routerLatencyHistogram = Meter.CreateHistogram<double>(
        "network_monitor.router_latency_ms",
        unit: "ms",
        description: "Router ping latency distribution");

    private static readonly Histogram<double> _internetLatencyHistogram = Meter.CreateHistogram<double>(
        "network_monitor.internet_latency_ms",
        unit: "ms",
        description: "Internet ping latency distribution");

    private static readonly Counter<long> _failureCounter = Meter.CreateCounter<long>(
        "network_monitor.failures",
        description: "Number of ping failures by target type");

    private readonly IPingService _pingService;
    private readonly MonitorOptions _options;
    private readonly ILogger<NetworkMonitorService> _logger;

    private NetworkStatus? _lastStatus;

    public event EventHandler<NetworkStatus>? StatusChanged;

    public NetworkMonitorService(
        IPingService pingService,
        IOptions<MonitorOptions> options,
        ILogger<NetworkMonitorService> logger)
    {
        _pingService = pingService;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<NetworkStatus> CheckNetworkAsync(CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("NetworkMonitor.CheckNetwork");

        _checkCounter.Add(1);

        _logger.LogDebug("Starting network health check");

        // Ping router and internet in parallel for efficiency
        var routerTask = PingWithStatsAsync(
            _options.RouterAddress,
            "router",
            cancellationToken);

        var internetTask = PingWithStatsAsync(
            _options.InternetTarget,
            "internet",
            cancellationToken);

        await Task.WhenAll(routerTask, internetTask).ConfigureAwait(false);

        var routerResult = await routerTask.ConfigureAwait(false);
        var internetResult = await internetTask.ConfigureAwait(false);

        // Record metrics
        if (routerResult is { Success: true, RoundtripTimeMs: not null })
        {
            _routerLatencyHistogram.Record(routerResult.RoundtripTimeMs.Value);
        }
        else
        {
            _failureCounter.Add(1, new KeyValuePair<string, object?>("target_type", "router"));
        }

        if (internetResult is { Success: true, RoundtripTimeMs: not null })
        {
            _internetLatencyHistogram.Record(internetResult.RoundtripTimeMs.Value);
        }
        else
        {
            _failureCounter.Add(1, new KeyValuePair<string, object?>("target_type", "internet"));
        }

        // Compute overall health
        var (health, message) = ComputeHealth(routerResult, internetResult);

        var status = new NetworkStatus(
            health,
            routerResult,
            internetResult,
            DateTimeOffset.UtcNow,
            message);

        activity?.SetTag("health", health.ToString());
        activity?.SetTag("router.success", routerResult?.Success ?? false);
        activity?.SetTag("internet.success", internetResult?.Success ?? false);

        // Fire event if status changed
        if (_lastStatus?.Health != status.Health)
        {
            _logger.LogInformation(
                "Network status changed: {OldHealth} -> {NewHealth}: {Message}",
                _lastStatus?.Health.ToString() ?? "Unknown",
                status.Health,
                status.Message);

            StatusChanged?.Invoke(this, status);
        }

        _lastStatus = status;

        return status;
    }

    private async Task<PingResult?> PingWithStatsAsync(
        string target,
        string targetType,
        CancellationToken cancellationToken)
    {
        try
        {
            var results = await _pingService.PingMultipleAsync(
                target,
                _options.PingsPerCycle,
                _options.TimeoutMs,
                cancellationToken).ConfigureAwait(false);

            if (results.Count == 0)
            {
                return null;
            }

            // Calculate aggregate result
            var successfulPings = results.Where(r => r.Success).ToList();

            if (successfulPings.Count == 0)
            {
                // All pings failed - return the last failure
                return results[^1];
            }

            // Return a result with median latency for stability
            var sortedLatencies = successfulPings
                .Where(r => r.RoundtripTimeMs.HasValue)
                .Select(r => r.RoundtripTimeMs!.Value)
                .OrderBy(l => l)
                .ToList();

            var medianLatency = sortedLatencies.Count > 0
                ? sortedLatencies[sortedLatencies.Count / 2]
                : 0;

            return PingResult.Succeeded(target, medianLatency);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error pinging {Target}", target);
            return PingResult.Failed(target, ex.Message);
        }
    }

    private (NetworkHealth Health, string Message) ComputeHealth(
        PingResult? routerResult,
        PingResult? internetResult)
    {
        // Priority 1: Check if we can reach the router (local network)
        if (routerResult is not { Success: true })
        {
            return (NetworkHealth.Offline,
                "Cannot reach local network - check WiFi/Ethernet connection");
        }

        // Priority 2: Check if we can reach the internet
        if (internetResult is not { Success: true })
        {
            return (NetworkHealth.Poor,
                $"Local network OK (router: {routerResult.RoundtripTimeMs}ms) but no internet access");
        }

        // Both connected - evaluate latency
        var routerLatency = routerResult.RoundtripTimeMs!.Value;
        var internetLatency = internetResult.RoundtripTimeMs!.Value;

        if (routerLatency <= _options.ExcellentLatencyMs &&
            internetLatency <= _options.ExcellentLatencyMs)
        {
            return (NetworkHealth.Excellent,
                $"Excellent - Router: {routerLatency}ms, Internet: {internetLatency}ms");
        }

        if (routerLatency <= _options.GoodLatencyMs &&
            internetLatency <= _options.GoodLatencyMs)
        {
            return (NetworkHealth.Good,
                $"Good - Router: {routerLatency}ms, Internet: {internetLatency}ms");
        }

        // High latency somewhere
        if (routerLatency > _options.GoodLatencyMs)
        {
            return (NetworkHealth.Degraded,
                $"High local latency: Router {routerLatency}ms - possible WiFi interference");
        }

        return (NetworkHealth.Degraded,
            $"High internet latency: {internetLatency}ms - possible ISP issues");
    }
}
