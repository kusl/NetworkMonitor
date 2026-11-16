using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetworkMonitor.Core.Configuration;
using NetworkMonitor.Core.Models;
using NetworkMonitor.Core.Telemetry;

namespace NetworkMonitor.Core.Monitoring;

public interface INetworkMonitor
{
    Task<NetworkStatus> CheckAsync(CancellationToken cancellationToken);
}

public sealed class NetworkMonitorService(
    INetworkConfigurationService configuration,
    ITargetProber prober,
    NetworkMetrics metrics,
    IOptions<MonitorOptions> options,
    TimeProvider timeProvider,
    ILogger<NetworkMonitorService> logger) : INetworkMonitor
{
    private NetworkHealth? _lastHealth;

    public async Task<NetworkStatus> CheckAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var targets = await BuildTargetsAsync(settings, cancellationToken);
        var results = new TargetCheckResult[targets.Count];

        await Parallel.ForEachAsync(
            Enumerable.Range(0, targets.Count),
            new ParallelOptions { MaxDegreeOfParallelism = settings.MaxConcurrentChecks, CancellationToken = cancellationToken },
            async (i, ct) => results[i] = await prober.ProbeAsync(targets[i], settings.PingsPerCycle, ct));

        var router = results.FirstOrDefault(r => r.Target.Category == TargetCategory.Router);
        var internet = results.FirstOrDefault(r => r.Target.Category == TargetCategory.Internet);
        TargetCheckResult[] custom = [.. results.Where(r => r.Target.Category == TargetCategory.Custom)];

        if (router is not null)
        {
            configuration.ReportRouterCheckResult(router.Success);
        }

        metrics.RecordCycle(results);

        var (health, message) = HealthEvaluator.Evaluate(router, internet, custom, settings);
        LogTransition(health, message);

        return new NetworkStatus(health, message, timeProvider.GetUtcNow(), results);
    }

    private async Task<List<MonitorTarget>> BuildTargetsAsync(MonitorOptions settings, CancellationToken cancellationToken)
    {
        var targets = new List<MonitorTarget>();

        if (!settings.IsCheckDisabled(MonitorTarget.RouterName) &&
            await configuration.GetRouterAddressAsync(cancellationToken) is { Length: > 0 } routerAddress)
        {
            targets.Add(MonitorTarget.Router(routerAddress));
        }

        if (!settings.IsCheckDisabled(MonitorTarget.InternetName))
        {
            targets.Add(MonitorTarget.Internet(await configuration.GetInternetTargetAsync(cancellationToken)));
        }

        targets.AddRange(settings.CustomTargets
            .Where(t => t.Enabled && !string.IsNullOrWhiteSpace(t.Address) && !settings.IsCheckDisabled(t.Name))
            .Select(t => new MonitorTarget(t.Name, t.Address, TargetCategory.Custom)));

        return targets;
    }

    private void LogTransition(NetworkHealth health, string message)
    {
        if (_lastHealth == health)
        {
            return;
        }

        var level = health <= NetworkHealth.Poor ? LogLevel.Warning : LogLevel.Information;
        logger.Log(level, "Network status changed: {OldHealth} -> {NewHealth}: {Message}", _lastHealth?.ToString() ?? "Unknown", health, message);
        _lastHealth = health;
    }
}
