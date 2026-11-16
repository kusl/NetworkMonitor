using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetworkMonitor.Core.Configuration;
using NetworkMonitor.Core.Models;

namespace NetworkMonitor.Core.Monitoring;

public interface INetworkConfigurationService
{
    Task<string?> GetRouterAddressAsync(CancellationToken cancellationToken);

    Task<string> GetInternetTargetAsync(CancellationToken cancellationToken);

    void ReportRouterCheckResult(bool reachable);
}

public sealed class NetworkConfigurationService(
    IGatewayDetector gatewayDetector,
    ITargetProber prober,
    IOptions<MonitorOptions> options,
    TimeProvider timeProvider,
    ILogger<NetworkConfigurationService> logger) : INetworkConfigurationService, IDisposable
{
    private const int RouterFailuresBeforeRedetect = 5;
    private static readonly TimeSpan RedetectCooldown = TimeSpan.FromSeconds(60);

    private readonly MonitorOptions _options = options.Value;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string? _routerAddress;
    private string? _internetTarget;
    private bool _initialized;
    private DateTimeOffset _lastRouterResolve = DateTimeOffset.MinValue;
    private int _consecutiveRouterFailures;

    public async Task<string?> GetRouterAddressAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await MaybeRedetectRouterAsync(cancellationToken);
        return _routerAddress;
    }

    public async Task<string> GetInternetTargetAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        return _internetTarget ?? _options.InternetTarget;
    }

    public void ReportRouterCheckResult(bool reachable) =>
        _consecutiveRouterFailures = reachable ? 0 : _consecutiveRouterFailures + 1;

    public void Dispose() => _lock.Dispose();

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
            {
                return;
            }

            _routerAddress = await ResolveRouterAddressAsync(cancellationToken);
            _lastRouterResolve = timeProvider.GetUtcNow();
            _internetTarget = await ResolveInternetTargetAsync(cancellationToken);
            _initialized = true;

            logger.LogInformation(
                "Network configuration initialized. Router: {Router}, Internet: {Internet}",
                _routerAddress ?? "(none)",
                _internetTarget);
        }
        finally
        {
            _lock.Release();
        }
    }

    private bool ShouldRedetectRouter() =>
        _options.IsRouterAutoDetect &&
        (_routerAddress is null || _consecutiveRouterFailures >= RouterFailuresBeforeRedetect) &&
        timeProvider.GetUtcNow() - _lastRouterResolve >= RedetectCooldown;

    private async Task MaybeRedetectRouterAsync(CancellationToken cancellationToken)
    {
        if (!ShouldRedetectRouter())
        {
            return;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (!ShouldRedetectRouter())
            {
                return;
            }

            _lastRouterResolve = timeProvider.GetUtcNow();
            var detected = await ResolveRouterAddressAsync(cancellationToken);
            if (string.IsNullOrEmpty(detected))
            {
                return;
            }

            if (!string.Equals(detected, _routerAddress, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogInformation("Router address changed: {Old} -> {New}", _routerAddress ?? "(none)", detected);
            }

            _routerAddress = detected;
            _consecutiveRouterFailures = 0;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<string?> ResolveRouterAddressAsync(CancellationToken cancellationToken)
    {
        if (!_options.IsRouterAutoDetect)
        {
            return _options.RouterAddress;
        }

        var detected = gatewayDetector.DetectDefaultGateway();
        if (!string.IsNullOrEmpty(detected) && await IsReachableAsync(detected, cancellationToken))
        {
            return detected;
        }

        foreach (var gateway in gatewayDetector.CommonGatewayAddresses)
        {
            if (await IsReachableAsync(gateway, cancellationToken))
            {
                logger.LogInformation("Using fallback gateway: {Gateway}", gateway);
                return gateway;
            }
        }

        logger.LogWarning("No reachable gateway found. Router monitoring is disabled until one appears.");
        return null;
    }

    private async Task<string> ResolveInternetTargetAsync(CancellationToken cancellationToken)
    {
        if (!_options.EnableFallbackTargets)
        {
            return _options.InternetTarget;
        }

        foreach (var target in InternetTargets.Candidates(_options.InternetTarget))
        {
            if (await IsReachableAsync(target, cancellationToken))
            {
                return target;
            }
        }

        logger.LogWarning("No internet target is reachable. Using default: {Target}", _options.InternetTarget);
        return _options.InternetTarget;
    }

    private async Task<bool> IsReachableAsync(string address, CancellationToken cancellationToken) =>
        (await prober.ProbeAsync(MonitorTarget.Internet(address), 1, cancellationToken)).Success;
}
