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
public sealed class NetworkConfigurationService : INetworkConfigurationService
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
        await EnsureInitializedAsync(cancellationToken);
        return _resolvedRouterAddress;
    }

    /// <inheritdoc />
    public async Task<string> GetInternetTargetAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        return _resolvedInternetTarget ?? _internetTargetProvider.PrimaryTarget;
    }

    /// <inheritdoc />
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
                return;

            _logger.LogInformation("Initializing network configuration...");

            // Resolve router address
            _resolvedRouterAddress = await ResolveRouterAddressAsync(cancellationToken);
            if (_resolvedRouterAddress != null)
            {
                _logger.LogInformation("Router address resolved to: {Address}", _resolvedRouterAddress);
            }
            else
            {
                _logger.LogWarning("Could not resolve router address - router monitoring will be skipped");
            }

            // Resolve internet target
            _resolvedInternetTarget = await ResolveInternetTargetAsync(cancellationToken);
            _logger.LogInformation("Internet target resolved to: {Target}", _resolvedInternetTarget);

            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (!_initialized)
        {
            await InitializeAsync(cancellationToken);
        }
    }

    private async Task<string?> ResolveRouterAddressAsync(CancellationToken cancellationToken)
    {
        // If user specified a specific address, use it directly
        if (!_options.IsRouterAutoDetect)
        {
            _logger.LogDebug("Using configured router address: {Address}", _options.RouterAddress);
            return _options.RouterAddress;
        }

        // Try auto-detection first
        _logger.LogDebug("Attempting router auto-detection...");
        var detected = _gatewayDetector.DetectDefaultGateway();
        if (detected != null)
        {
            // Verify it's reachable
            if (await IsReachableAsync(detected, cancellationToken))
            {
                _logger.LogDebug("Auto-detected gateway {Address} is reachable", detected);
                return detected;
            }
            _logger.LogWarning("Auto-detected gateway {Address} is not reachable", detected);
        }

        // Fall back to common addresses
        _logger.LogDebug("Trying common gateway addresses...");
        foreach (var address in _gatewayDetector.GetCommonGatewayAddresses())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await IsReachableAsync(address, cancellationToken))
            {
                _logger.LogInformation("Found reachable gateway at common address: {Address}", address);
                return address;
            }
        }

        return null;
    }

    private async Task<string> ResolveInternetTargetAsync(CancellationToken cancellationToken)
    {
        var targets = _internetTargetProvider.GetTargets();

        // If fallback is disabled, just use the primary
        if (!_options.EnableFallbackTargets)
        {
            _logger.LogDebug("Fallback targets disabled, using primary: {Target}", targets[0]);
            return targets[0];
        }

        // Try each target until one responds
        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await IsReachableAsync(target, cancellationToken))
            {
                if (target != targets[0])
                {
                    _logger.LogInformation(
                        "Primary target {Primary} unreachable, using fallback: {Fallback}",
                        targets[0], target);
                }
                return target;
            }

            _logger.LogDebug("Internet target {Target} is not reachable", target);
        }

        // If nothing is reachable, use the primary anyway (might come back online)
        _logger.LogWarning(
            "No internet targets are reachable, defaulting to: {Target}",
            targets[0]);
        return targets[0];
    }

    private async Task<bool> IsReachableAsync(string target, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _pingService.PingAsync(
                target,
                _options.TimeoutMs,
                cancellationToken);
            return result.Success;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Ping to {Target} failed", target);
            return false;
        }
    }
}
