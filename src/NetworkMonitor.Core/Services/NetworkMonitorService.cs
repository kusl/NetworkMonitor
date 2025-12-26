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
    private readonly INetworkConfigurationService _configService;
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
        INetworkConfigurationService configService,
        IOptions<MonitorOptions> options,
        ILogger<NetworkMonitorService> logger)
    {
        _pingService = pingService;
        _configService = configService;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<NetworkStatus> CheckNetworkAsync(CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("CheckNetwork");

        cancellationToken.ThrowIfCancellationRequested();

        CheckCounter.Add(1);

        // Get resolved targets
        var routerAddress = await _configService.GetRouterAddressAsync(cancellationToken);
        var internetTarget = await _configService.GetInternetTargetAsync(cancellationToken);

        // Ping router (if we have one)
        PingResult? routerResult = null;
        if (routerAddress != null)
        {
            var routerResults = await _pingService.PingMultipleAsync(
                routerAddress,
                _options.PingsPerCycle,
                _options.TimeoutMs,
                cancellationToken);

            routerResult = AggregateResults(routerResults);

            if (routerResult.Success && routerResult.RoundtripTimeMs.HasValue)
            {
                RouterLatencyHistogram.Record(routerResult.RoundtripTimeMs.Value);
            }
            else
            {
                FailureCounter.Add(1, new KeyValuePair<string, object?>("target_type", "router"));
            }

            activity?.SetTag("router.success", routerResult.Success);
            activity?.SetTag("router.latency_ms", routerResult.RoundtripTimeMs);
        }
        else
        {
            _logger.LogDebug("No router address configured, skipping router ping");
        }

        // Ping internet
        var internetResults = await _pingService.PingMultipleAsync(
            internetTarget,
            _options.PingsPerCycle,
            _options.TimeoutMs,
            cancellationToken);

        var internetResult = AggregateResults(internetResults);

        if (internetResult.Success && internetResult.RoundtripTimeMs.HasValue)
        {
            InternetLatencyHistogram.Record(internetResult.RoundtripTimeMs.Value);
        }
        else
        {
            FailureCounter.Add(1, new KeyValuePair<string, object?>("target_type", "internet"));
        }

        activity?.SetTag("internet.success", internetResult.Success);
        activity?.SetTag("internet.latency_ms", internetResult.RoundtripTimeMs);

        // Compute overall health
        var (health, message) = ComputeHealth(routerResult, internetResult);

        var status = new NetworkStatus(
            health,
            routerResult,
            internetResult,
            DateTimeOffset.UtcNow,
            message);

        activity?.SetTag("health", health.ToString());

        // Fire event if status changed
        if (_lastStatus == null || _lastStatus.Health != status.Health)
        {
            _logger.LogInformation(
                "Network status changed: {OldHealth} -> {NewHealth}: {Message}",
                _lastStatus?.Health.ToString() ?? "Unknown",
                status.Health,
                status.Message);

            StatusChanged?.Invoke(this, new NetworkStatusEventArgs(status, _lastStatus));
        }

        _lastStatus = status;
        return status;
    }

    private static PingResult AggregateResults(IReadOnlyList<PingResult> results)
    {
        if (results.Count == 0)
        {
            return PingResult.Failed("unknown", "No ping results");
        }

        var successful = results.Where(r => r.Success).ToList();
        var target = results[0].Target;

        if (successful.Count == 0)
        {
            return PingResult.Failed(target, results[0].ErrorMessage ?? "All pings failed");
        }

        // Return average latency of successful pings
        var avgLatency = (long)successful.Average(r => r.RoundtripTimeMs ?? 0);
        return PingResult.Succeeded(target, avgLatency);
    }

    private (NetworkHealth Health, string Message) ComputeHealth(
        PingResult? routerResult,
        PingResult internetResult)
    {
        // If we have a router configured and it's not responding, that's significant
        if (routerResult != null && !routerResult.Success)
        {
            return !internetResult.Success
                ? (NetworkHealth.Offline, "Cannot reach router or internet")
                : (NetworkHealth.Degraded, "Cannot reach router but internet works");
        }

        // If internet is down
        if (!internetResult.Success)
        {
            return routerResult?.Success == true
                ? (NetworkHealth.Poor, "Router OK but cannot reach internet")
                : (NetworkHealth.Offline, "Cannot reach internet");
        }

        // Both are up - check latency
        var internetLatency = internetResult.RoundtripTimeMs ?? 0;
        var routerLatency = routerResult?.RoundtripTimeMs ?? 0;

        return internetLatency switch
        {
            <= 50 when routerLatency <= 10 => (NetworkHealth.Excellent, "Network is excellent"),
            <= 100 => (NetworkHealth.Good, "Network is good"),
            <= 200 => (NetworkHealth.Degraded, "Network is degraded (high latency)"),
            _ => (NetworkHealth.Poor, "Network is poor (very high latency)")
        };
    }
}
