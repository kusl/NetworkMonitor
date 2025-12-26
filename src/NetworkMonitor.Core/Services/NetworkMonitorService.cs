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
        if (!string.IsNullOrEmpty(routerAddress))
        {
            routerResult = await PingWithAggregationAsync(routerAddress, cancellationToken);
            if (routerResult is { Success: true, RoundtripTimeMs: not null })
            {
                RouterLatencyHistogram.Record(routerResult.RoundtripTimeMs.Value);
            }
            else
            {
                FailureCounter.Add(1, new KeyValuePair<string, object?>("target_type", "router"));
            }
        }

        // Ping internet target
        var internetResult = await PingWithAggregationAsync(internetTarget, cancellationToken);
        if (internetResult is { Success: true, RoundtripTimeMs: not null })
        {
            InternetLatencyHistogram.Record(internetResult.RoundtripTimeMs.Value);
        }
        else
        {
            FailureCounter.Add(1, new KeyValuePair<string, object?>("target_type", "internet"));
        }

        // Compute overall health
        var (health, message) = ComputeHealth(routerResult, internetResult, _options);

        var status = new NetworkStatus(
            health,
            routerResult,
            internetResult,
            DateTimeOffset.UtcNow,
            message);

        activity?.SetTag("health", health.ToString());
        activity?.SetTag("router.success", routerResult?.Success ?? false);
        activity?.SetTag("internet.success", internetResult.Success);

        // Fire event if status changed
        if (_lastStatus?.Health != status.Health)
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

    private async Task<PingResult> PingWithAggregationAsync(
        string target,
        CancellationToken cancellationToken)
    {
        try
        {
            var results = await _pingService.PingMultipleAsync(
                target,
                _options.PingsPerCycle,
                _options.TimeoutMs,
                cancellationToken);

            return AggregateResults(results);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error pinging {Target}", target);
            return PingResult.Failed(target, ex.Message);
        }
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

        // Return median latency of successful pings for stability
        var sortedLatencies = successful
            .Where(r => r.RoundtripTimeMs.HasValue)
            .Select(r => r.RoundtripTimeMs!.Value)
            .OrderBy(l => l)
            .ToList();

        var medianLatency = sortedLatencies.Count > 0
            ? sortedLatencies[sortedLatencies.Count / 2]
            : 0;

        return PingResult.Succeeded(target, medianLatency);
    }

    /// <summary>
    /// Computes network health based on ping results.
    /// </summary>
    /// <remarks>
    /// This method is static as it does not access instance data (CA1822).
    /// </remarks>
    private static (NetworkHealth Health, string Message) ComputeHealth(
        PingResult? routerResult,
        PingResult internetResult,
        MonitorOptions options)
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

        if (internetLatency <= options.ExcellentLatencyMs &&
            routerLatency <= options.ExcellentLatencyMs)
        {
            return (NetworkHealth.Excellent,
                $"Excellent - Router: {routerLatency}ms, Internet: {internetLatency}ms");
        }

        if (internetLatency <= options.GoodLatencyMs &&
            routerLatency <= options.GoodLatencyMs)
        {
            return (NetworkHealth.Good,
                $"Good - Router: {routerLatency}ms, Internet: {internetLatency}ms");
        }

        // High latency somewhere
        if (routerLatency > options.GoodLatencyMs && routerResult != null)
        {
            return (NetworkHealth.Degraded,
                $"High local latency: Router {routerLatency}ms - possible WiFi interference");
        }

        return (NetworkHealth.Poor,
            $"High internet latency: {internetLatency}ms - possible ISP issues");
    }
}
