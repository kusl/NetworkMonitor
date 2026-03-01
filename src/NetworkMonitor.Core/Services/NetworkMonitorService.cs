using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetworkMonitor.Core.Models;

namespace NetworkMonitor.Core.Services;

/// <summary>
/// Main network monitoring service.
/// Coordinates ping operations across multiple targets and computes overall network health.
/// Supports IPv4, IPv6, DNS resolution, packet loss tracking, and custom targets.
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

    private static readonly Histogram<double> DnsResolutionHistogram = Meter.CreateHistogram<double>(
        "network_monitor.dns_resolution_ms",
        unit: "ms",
        description: "DNS resolution latency distribution");

    private static readonly Histogram<double> PacketLossHistogram = Meter.CreateHistogram<double>(
        "network_monitor.packet_loss_percent",
        unit: "%",
        description: "Packet loss percentage distribution");

    private readonly IPingService _pingService;
    private readonly INetworkConfigurationService _configService;
    private readonly IDnsResolverService? _dnsResolver;
    private readonly IInternetTargetProvider _internetTargetProvider;
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
        IInternetTargetProvider internetTargetProvider,
        IOptions<MonitorOptions> options,
        ILogger<NetworkMonitorService> logger,
        IDnsResolverService? dnsResolver = null)
    {
        _pingService = pingService;
        _configService = configService;
        _internetTargetProvider = internetTargetProvider;
        _options = options.Value;
        _logger = logger;
        _dnsResolver = dnsResolver;
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

        // Collect all target check results
        var targetResults = new List<TargetCheckResult>();

        // Ping router (if we have one and it's not disabled)
        PingResult? routerResult = null;
        if (!string.IsNullOrEmpty(routerAddress) && !_options.IsCheckDisabled("Router"))
        {
            var (pingResult, packetLoss) = await PingWithMetricsAsync(routerAddress, cancellationToken);
            routerResult = pingResult;

            if (routerResult is { Success: true, RoundtripTimeMs: not null })
            {
                RouterLatencyHistogram.Record(routerResult.RoundtripTimeMs.Value);
            }
            else
            {
                FailureCounter.Add(1, new KeyValuePair<string, object?>("target_type", "router"));
            }

            PacketLossHistogram.Record(packetLoss, new KeyValuePair<string, object?>("target", "router"));

            targetResults.Add(new TargetCheckResult(
                new MonitorTarget("Router", routerAddress, TargetCategory.Router),
                routerResult, null, null, packetLoss, DateTimeOffset.UtcNow));
        }

        // Ping internet target (if not disabled)
        PingResult? internetResult = null;
        double internetPacketLoss = 0;
        if (!_options.IsCheckDisabled("Internet"))
        {
            (internetResult, internetPacketLoss) = await PingWithMetricsAsync(internetTarget, cancellationToken);

            if (internetResult is { Success: true, RoundtripTimeMs: not null })
            {
                InternetLatencyHistogram.Record(internetResult.RoundtripTimeMs.Value);
            }
            else
            {
                FailureCounter.Add(1, new KeyValuePair<string, object?>("target_type", "internet"));
            }

            PacketLossHistogram.Record(internetPacketLoss, new KeyValuePair<string, object?>("target", "internet"));

            // DNS check for internet target (if it's a hostname)
            DnsResult? internetDns = null;
            if (_options.EnableDnsChecks && _dnsResolver != null && !IPAddress.TryParse(internetTarget, out _))
            {
                internetDns = await _dnsResolver.ResolveAsync(internetTarget, cancellationToken);
                DnsResolutionHistogram.Record(internetDns.ResolutionTimeMs,
                    new KeyValuePair<string, object?>("target", internetTarget));
            }

            targetResults.Add(new TargetCheckResult(
                new MonitorTarget("Internet", internetTarget, TargetCategory.PublicDns),
                internetResult, null, internetDns, internetPacketLoss, DateTimeOffset.UtcNow));
        }
        else
        {
            // Need a non-null internetResult for health computation
            internetResult = PingResult.Failed(internetTarget, "Check disabled");
        }

        // Check custom targets
        foreach (var customTarget in _options.CustomTargets)
        {
            if (!customTarget.Enabled || _options.IsCheckDisabled(customTarget.Name))
                continue;

            var customResult = await CheckCustomTargetAsync(customTarget, cancellationToken);
            targetResults.Add(customResult);
        }

        // Compute overall health
        var (health, message) = ComputeHealth(routerResult, internetResult, internetPacketLoss, _options);

        var status = new NetworkStatus(
            health,
            routerResult,
            internetResult,
            DateTimeOffset.UtcNow,
            message,
            targetResults);

        activity?.SetTag("health", health.ToString());
        activity?.SetTag("router.success", routerResult?.Success ?? false);
        activity?.SetTag("internet.success", internetResult.Success);
        activity?.SetTag("target_count", targetResults.Count);

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

    private async Task<(PingResult Result, double PacketLossPercent)> PingWithMetricsAsync(
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

            var packetLoss = results.Count > 0
                ? (double)(results.Count - results.Count(r => r.Success)) / results.Count * 100
                : 100.0;

            var aggregated = AggregateResults(results);
            return (aggregated, packetLoss);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error pinging {Target}", target);
            return (PingResult.Failed(target, ex.Message), 100.0);
        }
    }

    private async Task<TargetCheckResult> CheckCustomTargetAsync(
        CustomTargetConfig target,
        CancellationToken cancellationToken)
    {
        PingResult? pingResult = null;
        DnsResult? dnsResult = null;
        double packetLoss = 0;

        try
        {
            // DNS resolution for hostnames
            if (_options.EnableDnsChecks && _dnsResolver != null && !IPAddress.TryParse(target.Address, out _))
            {
                dnsResult = await _dnsResolver.ResolveAsync(target.Address, cancellationToken);
                DnsResolutionHistogram.Record(dnsResult.ResolutionTimeMs,
                    new KeyValuePair<string, object?>("target", target.Name));
            }

            // Ping
            (pingResult, packetLoss) = await PingWithMetricsAsync(target.Address, cancellationToken);
            PacketLossHistogram.Record(packetLoss,
                new KeyValuePair<string, object?>("target", target.Name));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error checking custom target {Name} ({Address})", target.Name, target.Address);
            pingResult = PingResult.Failed(target.Address, ex.Message);
            packetLoss = 100;
        }

        return new TargetCheckResult(
            new MonitorTarget(target.Name, target.Address, TargetCategory.Custom),
            pingResult, null, dnsResult, packetLoss, DateTimeOffset.UtcNow);
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
    private static (NetworkHealth Health, string Message) ComputeHealth(
        PingResult? routerResult,
        PingResult internetResult,
        double packetLossPercent,
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

        // Check packet loss
        if (packetLossPercent >= options.DegradedPacketLossPercent)
        {
            return (NetworkHealth.Degraded,
                $"High packet loss: {packetLossPercent:F0}%");
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
