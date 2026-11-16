using NetworkMonitor.Core.Configuration;
using NetworkMonitor.Core.Models;

namespace NetworkMonitor.Core.Monitoring;

public static class HealthEvaluator
{
    public static (NetworkHealth Health, string Message) Evaluate(
        TargetCheckResult? router,
        TargetCheckResult? internet,
        IReadOnlyCollection<TargetCheckResult> custom,
        MonitorOptions options)
    {
        ArgumentNullException.ThrowIfNull(custom);
        ArgumentNullException.ThrowIfNull(options);

        if (internet is not null)
        {
            return EvaluateInternet(router, internet, options);
        }

        if (router is not null)
        {
            return EvaluateRouterOnly(router, options);
        }

        return EvaluateCustom(custom);
    }

    private static (NetworkHealth, string) EvaluateInternet(
        TargetCheckResult? router,
        TargetCheckResult internet,
        MonitorOptions options)
    {
        var routerReachable = router is { Success: true };
        var routerDown = router is { Success: false };
        var routerLatency = router?.LatencyMs ?? 0;
        var routerSlow = routerReachable && routerLatency > options.GoodLatencyMs;

        if (!internet.Success)
        {
            return routerReachable ? (NetworkHealth.Poor, "Router OK but cannot reach internet")
                : routerDown ? (NetworkHealth.Offline, "Cannot reach router or internet")
                : (NetworkHealth.Offline, "Cannot reach internet");
        }

        if (internet.PacketLossPercent >= options.DegradedPacketLossPercent)
        {
            return (NetworkHealth.Degraded, $"High internet packet loss: {internet.PacketLossPercent:F0}%");
        }

        if (routerDown)
        {
            return (NetworkHealth.Degraded, "Internet OK but cannot reach router");
        }

        var routerNote = routerSlow
            ? $" (router replies slowly: {routerLatency}ms - likely ICMP de-prioritization, not a path problem)"
            : string.Empty;
        var latency = internet.LatencyMs ?? 0;

        if (latency <= options.ExcellentLatencyMs)
        {
            return (NetworkHealth.Excellent, $"Excellent - Internet: {latency}ms{routerNote}");
        }

        if (latency <= options.GoodLatencyMs)
        {
            return (NetworkHealth.Good, $"Good - Internet: {latency}ms{routerNote}");
        }

        return routerSlow
            ? (NetworkHealth.Degraded, $"High latency on internet ({latency}ms) and router ({routerLatency}ms) - possible local Wi-Fi/link issue")
            : (NetworkHealth.Poor, $"High internet latency: {latency}ms - likely upstream/ISP");
    }

    private static (NetworkHealth, string) EvaluateRouterOnly(TargetCheckResult router, MonitorOptions options)
    {
        if (!router.Success)
        {
            return (NetworkHealth.Offline, "Cannot reach router (internet check disabled)");
        }

        if (router.PacketLossPercent >= options.DegradedPacketLossPercent)
        {
            return (NetworkHealth.Degraded, $"Router packet loss: {router.PacketLossPercent:F0}% (internet check disabled)");
        }

        return (NetworkHealth.Good, $"Router reachable: {router.LatencyMs ?? 0}ms (internet check disabled)");
    }

    private static (NetworkHealth, string) EvaluateCustom(IReadOnlyCollection<TargetCheckResult> custom)
    {
        if (custom.Count == 0)
        {
            return (NetworkHealth.Good, "Router and internet checks are disabled");
        }

        var reachable = custom.Count(r => r.Success);
        var total = custom.Count;

        return reachable switch
        {
            0 => (NetworkHealth.Offline, $"No custom targets reachable (0/{total})"),
            _ when reachable == total => (NetworkHealth.Good, $"All custom targets reachable ({reachable}/{total})"),
            _ => (NetworkHealth.Degraded, $"Some custom targets unreachable ({reachable}/{total})"),
        };
    }
}
