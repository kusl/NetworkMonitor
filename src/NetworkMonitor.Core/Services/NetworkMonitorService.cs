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

    private static readonly Histogram<double> JitterHistogram = Meter.CreateHistogram<double>(
        "network_monitor.jitter_ms",
        unit: "ms",
        description: "Intra-cycle ping jitter distribution");

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

        var targetResults = new List<TargetCheckResult>();

        // ---- Router (sequential, first) ----
        var routerEnabled = !string.IsNullOrEmpty(routerAddress) && !_options.IsCheckDisabled("Router");
        PingResult? routerResult = null;
        double routerPacketLoss = 0;

        if (routerEnabled)
        {
            var agg = await PingWithMetricsAsync(routerAddress!, cancellationToken);
            routerResult = agg.Result;
            routerPacketLoss = agg.PacketLossPercent;

            if (routerResult is { Success: true, RoundtripTimeMs: not null })
            {
                RouterLatencyHistogram.Record(routerResult.RoundtripTimeMs.Value);
            }
            else
            {
                FailureCounter.Add(1, new KeyValuePair<string, object?>("target_type", "router"));
            }

            PacketLossHistogram.Record(routerPacketLoss, new KeyValuePair<string, object?>("target", "router"));
            RecordJitter(agg.JitterMs, "router");

            targetResults.Add(new TargetCheckResult(
                new MonitorTarget("Router", routerAddress!, TargetCategory.Router),
                routerResult, null, null, routerPacketLoss, DateTimeOffset.UtcNow,
                agg.MinMs, agg.MaxMs, agg.JitterMs, ResolvedAddress: null));

            // Feed reachability back so config can re-detect a stale/changed
            // gateway (roaming laptops) or one that only came up after startup.
            _configService.ReportRouterCheckResult(routerResult?.Success == true);
        }

        // ---- Internet (sequential, second) ----
        var internetEnabled = !_options.IsCheckDisabled("Internet");
        PingResult? internetResult = null;
        double internetPacketLoss = 0;

        if (internetEnabled)
        {
            var agg = await PingWithMetricsAsync(internetTarget, cancellationToken);
            internetResult = agg.Result;
            internetPacketLoss = agg.PacketLossPercent;

            if (internetResult is { Success: true, RoundtripTimeMs: not null })
            {
                InternetLatencyHistogram.Record(internetResult.RoundtripTimeMs.Value);
            }
            else
            {
                FailureCounter.Add(1, new KeyValuePair<string, object?>("target_type", "internet"));
            }

            PacketLossHistogram.Record(internetPacketLoss, new KeyValuePair<string, object?>("target", "internet"));
            RecordJitter(agg.JitterMs, "internet");

            DnsResult? internetDns = null;
            if (_options.EnableDnsChecks && _dnsResolver != null && !IPAddress.TryParse(internetTarget, out _))
            {
                internetDns = await _dnsResolver.ResolveAsync(internetTarget, cancellationToken);
                DnsResolutionHistogram.Record(internetDns.ResolutionTimeMs,
                    new KeyValuePair<string, object?>("target", internetTarget));
            }

            targetResults.Add(new TargetCheckResult(
                new MonitorTarget("Internet", internetTarget, TargetCategory.PublicDns),
                internetResult, null, internetDns, internetPacketLoss, DateTimeOffset.UtcNow,
                agg.MinMs, agg.MaxMs, agg.JitterMs, FirstResolved(internetDns)));
        }

        // ---- Custom targets (bounded parallelism) ----
        var enabledCustom = _options.CustomTargets
            .Where(t => t.Enabled && !_options.IsCheckDisabled(t.Name))
            .ToList();

        var customResults = await CheckCustomTargetsAsync(enabledCustom, cancellationToken);
        targetResults.AddRange(customResults);

        // ---- Overall health ----
        var (health, message) = ComputeHealth(
            routerResult, routerEnabled, routerPacketLoss,
            internetResult, internetEnabled, internetPacketLoss,
            customResults,
            _options);

        var status = new NetworkStatus(
            health,
            routerResult,
            internetResult,
            DateTimeOffset.UtcNow,
            message,
            targetResults);

        activity?.SetTag("health", health.ToString());
        activity?.SetTag("router.success", routerResult?.Success ?? false);
        activity?.SetTag("internet.success", internetResult?.Success ?? false);
        activity?.SetTag("target_count", targetResults.Count);

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

    private async Task<TargetCheckResult[]> CheckCustomTargetsAsync(
        List<CustomTargetConfig> targets,
        CancellationToken cancellationToken)
    {
        if (targets.Count == 0)
        {
            return [];
        }

        var maxConcurrency = Math.Max(1, _options.MaxConcurrentChecks);
        using var gate = new SemaphoreSlim(maxConcurrency, maxConcurrency);

        // Task.WhenAll preserves ordering by task position, so results line up
        // with the configured target order regardless of completion order.
        var tasks = targets.Select(async target =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await CheckCustomTargetAsync(target, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        });

        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task<PingAggregate> PingWithMetricsAsync(
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

            return ComputeAggregate(results);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error pinging {Target}", target);
            return new PingAggregate(PingResult.Failed(target, ex.Message), 100.0, null, null, null);
        }
    }

    private async Task<TargetCheckResult> CheckCustomTargetAsync(
        CustomTargetConfig target,
        CancellationToken cancellationToken)
    {
        DnsResult? dnsResult = null;
        var agg = new PingAggregate(PingResult.Failed(target.Address, "not checked"), 100, null, null, null);

        try
        {
            if (_options.EnableDnsChecks && _dnsResolver != null && !IPAddress.TryParse(target.Address, out _))
            {
                dnsResult = await _dnsResolver.ResolveAsync(target.Address, cancellationToken);
                DnsResolutionHistogram.Record(dnsResult.ResolutionTimeMs,
                    new KeyValuePair<string, object?>("target", target.Name));
            }

            agg = await PingWithMetricsAsync(target.Address, cancellationToken);
            PacketLossHistogram.Record(agg.PacketLossPercent,
                new KeyValuePair<string, object?>("target", target.Name));
            RecordJitter(agg.JitterMs, target.Name);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error checking custom target {Name} ({Address})", target.Name, target.Address);
            agg = new PingAggregate(PingResult.Failed(target.Address, ex.Message), 100, null, null, null);
        }

        return new TargetCheckResult(
            new MonitorTarget(target.Name, target.Address, TargetCategory.Custom),
            agg.Result, null, dnsResult, agg.PacketLossPercent, DateTimeOffset.UtcNow,
            agg.MinMs, agg.MaxMs, agg.JitterMs, FirstResolved(dnsResult));
    }

    private static void RecordJitter(long? jitterMs, string target)
    {
        if (jitterMs is { } j)
        {
            JitterHistogram.Record(j, new KeyValuePair<string, object?>("target", target));
        }
    }

    private static string? FirstResolved(DnsResult? dns) =>
        dns is { Success: true } d && d.ResolvedAddresses.Count > 0 ? d.ResolvedAddresses[0] : null;

    /// <summary>
    /// Reduces the pings sent this cycle to a single representative result plus
    /// packet loss and intra-burst latency statistics (min, max, jitter).
    /// </summary>
    /// <remarks>
    /// The representative latency is the MEDIAN of successful pings, which is
    /// stable and dampens the "first ping in a Wi-Fi burst is slow" artifact.
    /// Jitter is the mean absolute difference between successive successful pings
    /// (classic ping jitter), computed in the order the pings were sent.
    /// </remarks>
    private static PingAggregate ComputeAggregate(IReadOnlyList<PingResult> results)
    {
        if (results.Count == 0)
        {
            return new PingAggregate(PingResult.Failed("unknown", "No ping results"), 100.0, null, null, null);
        }

        var target = results[0].Target;
        var packetLoss = (double)(results.Count - results.Count(r => r.Success)) / results.Count * 100;

        var successfulLatencies = results
            .Where(r => r.Success && r.RoundtripTimeMs.HasValue)
            .Select(r => r.RoundtripTimeMs!.Value)
            .ToList();

        if (successfulLatencies.Count == 0)
        {
            return new PingAggregate(
                PingResult.Failed(target, results[0].ErrorMessage ?? "All pings failed"),
                packetLoss, null, null, null);
        }

        var sorted = successfulLatencies.OrderBy(l => l).ToList();
        var median = sorted[sorted.Count / 2];
        long min = sorted[0];
        long max = sorted[^1];

        long? jitter = null;
        if (successfulLatencies.Count >= 2)
        {
            long sumAbsDiff = 0;
            for (var i = 1; i < successfulLatencies.Count; i++)
            {
                sumAbsDiff += Math.Abs(successfulLatencies[i] - successfulLatencies[i - 1]);
            }

            jitter = (long)Math.Round((double)sumAbsDiff / (successfulLatencies.Count - 1), MidpointRounding.AwayFromZero);
        }

        return new PingAggregate(PingResult.Succeeded(target, median), packetLoss, min, max, jitter);
    }

    /// <summary>
    /// Computes overall network health.
    /// </summary>
    /// <remarks>
    /// DESIGN NOTE - why internet latency is the primary signal:
    ///
    /// A consumer router answers ICMP echo on its control-plane CPU, which is
    /// commonly rate-limited and de-prioritized, while it forwards real traffic
    /// on a hardware fast path. So the gateway can legitimately reply SLOWER
    /// than a distant server like 8.8.8.8 or 1.1.1.1 without anything being
    /// wrong with the local link. Other causes of a slow gateway ping that do
    /// NOT indicate a problem: Wi-Fi power-save wake-up on the first packet
    /// (the router is pinged first each cycle), ARP resolution on the first
    /// gateway ping, and NAT/CPU churn under load.
    ///
    /// Therefore high router latency, on its own, is treated as informational
    /// and never degrades health. What actually matters for the LAN is whether
    /// the gateway is REACHABLE (loss / reachability), not how fast it answers
    /// pings. High latency only counts against health when the INTERNET is also
    /// slow, which points at the local link rather than the router's CPU.
    /// </remarks>
    private static (NetworkHealth Health, string Message) ComputeHealth(
        PingResult? routerResult,
        bool routerEnabled,
        double routerPacketLoss,
        PingResult? internetResult,
        bool internetEnabled,
        double internetPacketLoss,
        IReadOnlyList<TargetCheckResult> customResults,
        MonitorOptions options)
    {
        var routerReachable = routerResult?.Success == true;
        var routerDown = routerEnabled && routerResult is { Success: false };
        var routerLatency = routerResult?.RoundtripTimeMs ?? 0;
        var routerSlow = routerReachable && routerLatency > options.GoodLatencyMs;

        // Primary path: judge by the internet result when we have one.
        if (internetEnabled && internetResult != null)
        {
            if (!internetResult.Success)
            {
                if (routerReachable)
                {
                    return (NetworkHealth.Poor, "Router OK but cannot reach internet");
                }

                return routerDown
                    ? (NetworkHealth.Offline, "Cannot reach router or internet")
                    : (NetworkHealth.Offline, "Cannot reach internet");
            }

            // Internet is up.
            if (internetPacketLoss >= options.DegradedPacketLossPercent)
            {
                return (NetworkHealth.Degraded, $"High internet packet loss: {internetPacketLoss:F0}%");
            }

            // A reachable gateway that suddenly stops answering while the
            // internet still works is a real local signal (stale gateway after
            // roaming, LAN issue), so it degrades health.
            if (routerDown)
            {
                return (NetworkHealth.Degraded, "Internet OK but cannot reach router");
            }

            var routerNote = routerSlow
                ? $" (router replies slowly: {routerLatency}ms - likely ICMP de-prioritization, not a path problem)"
                : string.Empty;

            var internetLatency = internetResult.RoundtripTimeMs ?? 0;

            if (internetLatency <= options.ExcellentLatencyMs)
            {
                return (NetworkHealth.Excellent, $"Excellent - Internet: {internetLatency}ms{routerNote}");
            }

            if (internetLatency <= options.GoodLatencyMs)
            {
                return (NetworkHealth.Good, $"Good - Internet: {internetLatency}ms{routerNote}");
            }

            // Internet latency itself is high. If the local hop is ALSO slow,
            // the local link is the likely culprit; otherwise blame upstream.
            return routerSlow
                ? (NetworkHealth.Degraded,
                    $"High latency on internet ({internetLatency}ms) and router ({routerLatency}ms) - possible local Wi-Fi/link issue")
                : (NetworkHealth.Poor,
                    $"High internet latency: {internetLatency}ms - likely upstream/ISP");
        }

        // Internet check disabled/unavailable: fall back to the router.
        if (routerEnabled && routerResult != null)
        {
            if (!routerResult.Success)
            {
                return (NetworkHealth.Offline, "Cannot reach router (internet check disabled)");
            }

            if (routerPacketLoss >= options.DegradedPacketLossPercent)
            {
                return (NetworkHealth.Degraded,
                    $"Router packet loss: {routerPacketLoss:F0}% (internet check disabled)");
            }

            return (NetworkHealth.Good, $"Router reachable: {routerLatency}ms (internet check disabled)");
        }

        // Neither router nor internet available to judge: derive a coarse health
        // from custom targets if any, else report a neutral, honest state.
        return DeriveHealthFromCustom(customResults);
    }

    private static (NetworkHealth Health, string Message) DeriveHealthFromCustom(
        IReadOnlyList<TargetCheckResult> customResults)
    {
        if (customResults.Count == 0)
        {
            return (NetworkHealth.Good, "Router and internet checks are disabled");
        }

        var reachable = customResults.Count(r => r.PingResult?.Success == true);
        var total = customResults.Count;

        if (reachable == 0)
        {
            return (NetworkHealth.Offline, $"No custom targets reachable (0/{total})");
        }

        if (reachable == total)
        {
            return (NetworkHealth.Good, $"All custom targets reachable ({reachable}/{total})");
        }

        return (NetworkHealth.Degraded, $"Some custom targets unreachable ({reachable}/{total})");
    }

    /// <summary>
    /// Representative outcome of a cycle's ping burst for one target: the median
    /// result plus packet loss and intra-burst min/max/jitter (nullable when no
    /// ping succeeded, or when fewer than two succeeded for jitter).
    /// </summary>
    private readonly record struct PingAggregate(
        PingResult Result,
        double PacketLossPercent,
        long? MinMs,
        long? MaxMs,
        long? JitterMs);
}
