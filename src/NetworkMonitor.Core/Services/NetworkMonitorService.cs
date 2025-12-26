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

    // Metrics - use static readonly for performance (CA1859)
    private static readonly Counter<long> CheckCounter = Meter.CreateCounter<long>(
        "network_monitor.checks",
        description: "Number of network health checks performed");

    private static readonly Histogram<double> RouterLatencyHistogram = Meter.CreateHistogram<double>(
        "network_monitor.router_latency_ms",
        unit: "ms",
        description: "Router ping latency distribution");

    private static readonly Histogram<double> InternetLatencyHistogram = Meter.CreateHistogram<double>(
        "network_monitor.internet_latency_ms",
        unit: "ms",
        description: "Internet ping latency distribution");

    private static readonly Counter<long> FailureCounter = Meter.CreateCounter<long>(
        "network_monitor.failures",
        description: "Number of ping failures by target type");

    private readonly IPingService _pingService;
    private readonly MonitorOptions _options;
    private readonly ILogger<NetworkMonitorService> _logger;

    private NetworkStatus? _lastStatus;

    /// <inheritdoc />
    public event EventHandler<NetworkStatusEventArgs>? StatusChanged;

    /// <summary>
    /// Creates a new network monitor service.
    /// </summary>
    public NetworkMonitorService(
        IPingService pingService,
        IOptions<MonitorOptions> options,
        ILogger<NetworkMonitorService> logger)
    {
        _pingService = pingService;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<NetworkStatus> CheckNetworkAsync(CancellationToken cancellationToken = default)
    {
        // Check cancellation immediately before doing any work
        cancellationToken.ThrowIfCancellationRequested();

        using var activity = ActivitySource.StartActivity("NetworkMonitor.CheckNetwork");

        CheckCounter.Add(1);

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
            RouterLatencyHistogram.Record(routerResult.RoundtripTimeMs.Value);
        }
        else
        {
            FailureCounter.Add(1, new KeyValuePair<string, object?>("target_type", "router"));
        }

        if (internetResult is { Success: true, RoundtripTimeMs: not null })
        {
            InternetLatencyHistogram.Record(internetResult.RoundtripTimeMs.Value);
        }
        else
        {
            FailureCounter.Add(1, new KeyValuePair<string, object?>("target_type", "internet"));
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

            StatusChanged?.Invoke(this, new NetworkStatusEventArgs(status));
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
        catch (OperationCanceledException)
        {
            // Let cancellation propagate up
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error pinging {Target}", target);
            return PingResult.Failed(target, ex.Message);
        }
    }

    private static (NetworkHealth Health, string Message) ComputeHealth(
        PingResult? routerResult,
        PingResult? internetResult)
    {
        // Router failure = offline (can't reach local network)
        if (routerResult is null || !routerResult.Success)
        {
            return (NetworkHealth.Offline, "Cannot reach local network");
        }

        // Internet failure = poor (local network OK, but no internet)
        if (internetResult is null || !internetResult.Success)
        {
            return (NetworkHealth.Poor, "Local network OK, no internet access");
        }

        // Both succeed - check latency for quality assessment
        var internetLatency = internetResult.RoundtripTimeMs ?? 0;
        var routerLatency = routerResult.RoundtripTimeMs ?? 0;

        return internetLatency switch
        {
            <= 50 when routerLatency <= 10 => (NetworkHealth.Excellent, "Network is excellent"),
            <= 100 => (NetworkHealth.Good, "Network is good"),
            <= 200 => (NetworkHealth.Degraded, "Network is degraded (high latency)"),
            _ => (NetworkHealth.Poor, "Network is poor (very high latency)")
        };
    }
}
