namespace NetworkMonitor.Core.Services;

/// <summary>
/// Detects the default gateway (router) IP address.
/// </summary>
/// <remarks>
/// The default gateway is advertised by DHCP and can be read from the OS
/// network configuration. This allows the application to work "out of the box"
/// without requiring users to manually configure their router IP.
/// </remarks>
public interface IGatewayDetector
{
    /// <summary>
    /// Attempts to detect the default gateway IP address.
    /// </summary>
    /// <returns>
    /// The IP address of the default gateway, or null if it cannot be detected.
    /// </returns>
    /// <remarks>
    /// On most systems, this returns the router IP (e.g., 192.168.1.1, 192.168.0.1, 10.0.0.1).
    /// Returns null if:
    /// - No network interfaces are available
    /// - No default gateway is configured (e.g., disconnected)
    /// - The system doesn't support gateway detection
    /// </remarks>
    string? DetectDefaultGateway();

    /// <summary>
    /// Gets a list of common gateway addresses to try as fallbacks.
    /// </summary>
    /// <remarks>
    /// If auto-detection fails, these are the most common gateway addresses
    /// used by consumer routers. The list is ordered by popularity.
    /// </remarks>
    IReadOnlyList<string> GetCommonGatewayAddresses();
}
