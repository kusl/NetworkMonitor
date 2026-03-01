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
    /// Attempts to detect the default gateway IP address (IPv4).
    /// </summary>
    /// <returns>
    /// The IP address of the default gateway, or null if it cannot be detected.
    /// </returns>
    string? DetectDefaultGateway();

    /// <summary>
    /// Attempts to detect the default gateway IPv6 address.
    /// </summary>
    /// <returns>
    /// The IPv6 address of the default gateway, or null if not available.
    /// </returns>
    string? DetectDefaultGatewayV6();

    /// <summary>
    /// Gets a list of common gateway addresses to try as fallbacks.
    /// </summary>
    IReadOnlyList<string> GetCommonGatewayAddresses();
}
