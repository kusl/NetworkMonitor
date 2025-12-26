namespace NetworkMonitor.Core.Services;

/// <summary>
/// Provides resolved network configuration for monitoring.
/// </summary>
/// <remarks>
/// This service handles the complexity of:
/// - Auto-detecting the default gateway
/// - Falling back to common gateway addresses
/// - Finding a reachable internet target
/// - Caching resolved addresses
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
}
