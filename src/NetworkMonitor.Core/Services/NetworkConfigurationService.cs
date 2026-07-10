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
/// 5. Re-detect the gateway when it goes missing or after a run of failures
///    (handles roaming between networks and resume-from-sleep)
/// </remarks>
public sealed class NetworkConfigurationService : INetworkConfigurationService, IDisposable
{
    private const int RouterFailuresBeforeRedetect = 5;
    private static readonly TimeSpan RedetectCooldown = TimeSpan.FromSeconds(60);

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

    private DateTimeOffset _lastRouterResolveUtc = DateTimeOffset.MinValue;
    private int _consecutiveRouterFailures;
    private bool _redetectRequested;

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
        await MaybeRedetectRouterAsync(cancellationToken);
        return _resolvedRouterAddress;
    }

    /// <inheritdoc />
    public async Task<string> GetInternetTargetAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await EnsureInitializedAsync(cancellationToken);
        return _resolvedInternetTarget ?? _internetTargetProvider.PrimaryTarget;
    }

    /// <inheritdoc />
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await EnsureInitializedAsync(cancellationToken);
    }

    /// <inheritdoc />
    public void ReportRouterCheckResult(bool reachable)
    {
        if (reachable)
        {
            _consecutiveRouterFailures = 0;
            return;
        }

        _consecutiveRouterFailures++;
        if (_consecutiveRouterFailures >= RouterFailuresBeforeRedetect)
        {
            // Ask GetRouterAddressAsync to re-run detection on its next call.
            _redetectRequested = true;
        }
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized) return;

        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized) return;

            _logger.LogDebug("Initializing network configuration...");

            _resolvedRouterAddress = await ResolveRouterAddressAsync(cancellationToken);
            _lastRouterResolveUtc = DateTimeOffset.UtcNow;

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

    /// <summary>
    /// Re-runs gateway detection when the router is unknown, or when a run of
    /// failures has been reported - but only for auto-detected routers and no
    /// more often than <see cref="RedetectCooldown"/>.
    /// </summary>
    private async Task MaybeRedetectRouterAsync(CancellationToken cancellationToken)
    {
        // A user-pinned address is authoritative; never second-guess it.
        if (!_options.IsRouterAutoDetect) return;

        if (!ShouldRedetectRouter()) return;

        await _initLock.WaitAsync(cancellationToken);
        try
        {
            // Re-evaluate under the lock in case another call already handled it.
            if (!ShouldRedetectRouter()) return;

            _lastRouterResolveUtc = DateTimeOffset.UtcNow;
            var previous = _resolvedRouterAddress;

            var detected = await ResolveRouterAddressAsync(cancellationToken);

            if (!string.IsNullOrEmpty(detected))
            {
                if (!string.Equals(detected, previous, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation(
                        "Router address changed: {Old} -> {New}",
                        previous ?? "(none)",
                        detected);
                }

                _resolvedRouterAddress = detected;
                _consecutiveRouterFailures = 0;
                _redetectRequested = false;
            }
            else if (previous is not null)
            {
                // Keep the previous address so its failures stay visible on the
                // display; we'll try to re-detect again after the cooldown.
                _logger.LogDebug("Router re-detection found nothing; keeping {Old}.", previous);
            }
        }
        finally
        {
            _initLock.Release();
        }
    }

    private bool ShouldRedetectRouter()
    {
        var wantsRedetect =
            _resolvedRouterAddress is null ||
            (_redetectRequested && _consecutiveRouterFailures >= RouterFailuresBeforeRedetect);

        if (!wantsRedetect)
        {
            return false;
        }

        return DateTimeOffset.UtcNow - _lastRouterResolveUtc >= RedetectCooldown;
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

    private async Task<string?> ResolveInternetTargetAsync(CancellationToken cancellationToken)
    {
        if (!_options.EnableFallbackTargets)
        {
            var primary = _internetTargetProvider.PrimaryTarget;
            _logger.LogDebug("Fallback targets disabled. Using primary target: {Target}", primary);
            return primary;
        }

        _logger.LogDebug("Finding reachable internet target...");

        foreach (var target in _internetTargetProvider.GetTargets())
        {
            if (await IsReachableAsync(target, cancellationToken))
            {
                _logger.LogInformation("Using internet target: {Target}", target);
                return target;
            }
        }

        _logger.LogWarning("No internet target is reachable. Using default: {Target}", _options.InternetTarget);
        return _options.InternetTarget;
    }

    private async Task<bool> IsReachableAsync(string target, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _pingService.PingAsync(target, _options.TimeoutMs, cancellationToken);
            return result.Success;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Failed to reach {Target}: {Error}", target, ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Disposes the service and its resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _initLock.Dispose();
    }
}
