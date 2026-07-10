namespace NetworkMonitor.Core.Services;

/// <summary>
/// Provides resolved network configuration for monitoring.
/// </summary>
/// <remarks>
/// This service handles the complexity of:
/// - Auto-detecting the default gateway
/// - Falling back to common gateway addresses
/// - Finding a reachable internet target
/// - Caching resolved addresses (and re-detecting them when the network changes)
/// </remarks>
public interface INetworkConfigurationService
{
    /// <summary>
    /// Gets the resolved router/gateway address to monitor.
    /// </summary>
    /// <returns>
    /// The router IP address, or null if no router could be found.
    /// When null, router monitoring should be skipped.
    /// </returns>
    Task<string?> GetRouterAddressAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the resolved internet target to monitor.
    /// </summary>
    /// <returns>
    /// The internet target IP address. Always returns a value,
    /// falling back to the configured default if nothing is reachable.
    /// </returns>
    Task<string> GetInternetTargetAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Initializes the service by detecting and verifying targets.
    /// </summary>
    /// <remarks>
    /// This is called automatically on first access, but can be called
    /// explicitly during startup for eager initialization.
    /// </remarks>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Reports the outcome of the most recent router reachability check.
    /// </summary>
    /// <param name="reachable">True if the router responded, false otherwise.</param>
    /// <remarks>
    /// Implementations may use a run of failures as a signal to re-detect the
    /// gateway (for example after roaming to a different network or coming back
    /// from sleep). The default implementation is a no-op, so fakes and simple
    /// implementations need do nothing.
    /// </remarks>
    void ReportRouterCheckResult(bool reachable) => _ = reachable;
}
