using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetworkMonitor.Core.Models;

namespace NetworkMonitor.Core.Services;

/// <summary>
/// Resolves network configuration by combining user settings with auto-detection.
/// </summary>
/// <remarks>
/// This service implements the "just works" philosophy:
/// 1. Try to auto-detect the gateway if configured to do so
/// 2. Fall back to common gateway addresses if detection fails
/// 3. Verify targets are reachable before using them
/// 4. Cache resolved addresses to avoid repeated detection
/// </remarks>
public sealed class NetworkConfigurationService : INetworkConfigurationService, IDisposable
{
    private readonly IGatewayDetector _gatewayDetector;
    private readonly IInternetTargetProvider _internetTargetProvider;
    private readonly IPingService _pingService;
    private readonly MonitorOptions _options;
    private readonly ILogger<NetworkConfigurationService> _logger;

    private string? _resolvedRouterAddress;
    private string? _resolvedInternetTarget;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;
    private bool _disposed;

    public NetworkConfigurationService(
        IGatewayDetector gatewayDetector,
        IInternetTargetProvider internetTargetProvider,
        IPingService pingService,
        IOptions<MonitorOptions> options,
        ILogger<NetworkConfigurationService> logger)
    {
        _gatewayDetector = gatewayDetector;
        _internetTargetProvider = internetTargetProvider;
        _pingService = pingService;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<string?> GetRouterAddressAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await EnsureInitializedAsync(cancellationToken);
        return _resolvedRouterAddress;
    }

    /// <inheritdoc />
    public async Task<string> GetInternetTargetAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await EnsureInitializedAsync(cancellationToken);
        return _resolvedInternetTarget ?? _internetTargetProvider.PrimaryTarget;
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized) return;

        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized) return;

            _logger.LogDebug("Initializing network configuration...");

            // Resolve router address
            _resolvedRouterAddress = await ResolveRouterAddressAsync(cancellationToken);

            // Resolve internet target
            _resolvedInternetTarget = await ResolveInternetTargetAsync(cancellationToken);

            _initialized = true;

            _logger.LogInformation(
                "Network configuration initialized. Router: {Router}, Internet: {Internet}",
                _resolvedRouterAddress ?? "(none)",
                _resolvedInternetTarget);
        }
        finally
        {
            _initLock.Release();
        }
    }

    private async Task<string?> ResolveRouterAddressAsync(CancellationToken cancellationToken)
    {
        // If user specified a specific address (not "auto"), use it
        if (!_options.IsRouterAutoDetect)
        {
            _logger.LogDebug("Using configured router address: {Address}", _options.RouterAddress);
            return _options.RouterAddress;
        }

        _logger.LogDebug("Auto-detecting gateway...");

        // Try OS-level detection first
        var detected = _gatewayDetector.DetectDefaultGateway();
        if (!string.IsNullOrEmpty(detected))
        {
            _logger.LogDebug("OS detected gateway: {Gateway}", detected);
            if (await IsReachableAsync(detected, cancellationToken))
            {
                _logger.LogInformation("Using detected gateway: {Gateway}", detected);
                return detected;
            }
            _logger.LogDebug("Detected gateway {Gateway} is not reachable", detected);
        }

        // Fall back to common gateway addresses
        _logger.LogDebug("Trying common gateway addresses...");
        foreach (var gateway in _gatewayDetector.GetCommonGatewayAddresses())
        {
            if (await IsReachableAsync(gateway, cancellationToken))
            {
                _logger.LogInformation("Using fallback gateway: {Gateway}", gateway);
                return gateway;
            }
        }

        _logger.LogWarning("No reachable gateway found. Router monitoring will be disabled.");
        return null;
    }

    private async Task<string> ResolveInternetTargetAsync(CancellationToken cancellationToken)
    {
        var targets = _internetTargetProvider.GetTargets();

        foreach (var target in targets)
        {
            if (await IsReachableAsync(target, cancellationToken))
            {
                _logger.LogDebug("Using internet target: {Target}", target);
                return target;
            }
            _logger.LogDebug("Internet target {Target} is not reachable", target);
        }

        // Return primary target even if not reachable - we'll report the failure
        _logger.LogWarning("No reachable internet targets found. Using primary: {Target}",
            _internetTargetProvider.PrimaryTarget);
        return _internetTargetProvider.PrimaryTarget;
    }

    private async Task<bool> IsReachableAsync(string target, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _pingService.PingAsync(target, 2000, cancellationToken);
            return result.Success;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to ping {Target}", target);
            return false;
        }
    }

    /// <summary>
    /// Disposes the service and releases resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _initLock.Dispose();
    }
}
