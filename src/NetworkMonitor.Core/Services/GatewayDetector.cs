using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace NetworkMonitor.Core.Services;

/// <summary>
/// Cross-platform default gateway detector using System.Net.NetworkInformation.
/// </summary>
/// <remarks>
/// This implementation reads the default gateway from the OS routing table,
/// which is populated by DHCP or static configuration. Works on Windows,
/// macOS, and Linux without external dependencies.
/// </remarks>
public sealed class GatewayDetector : IGatewayDetector
{
    private readonly ILogger<GatewayDetector> _logger;

    /// <summary>
    /// Common gateway addresses used by consumer routers, ordered by popularity.
    /// These are used as fallbacks if auto-detection fails.
    /// </summary>
    private static readonly string[] CommonGateways =
    [
        "192.168.1.1",   // Most common (Linksys, TP-Link, many ISP routers)
        "192.168.0.1",   // Second most common (D-Link, Netgear, some ISPs)
        "10.0.0.1",      // Apple AirPort, some enterprise networks
        "192.168.2.1",   // Belkin, SMC
        "192.168.1.254", // Some ISP-provided routers (BT, etc.)
        "192.168.0.254", // Some ISP-provided routers
        "10.0.1.1",      // Apple AirPort alternate
        "192.168.10.1",  // Some business routers
        "192.168.100.1", // Some cable modems
        "172.16.0.1",    // Private network range (less common for home)
    ];

    public GatewayDetector(ILogger<GatewayDetector> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public string? DetectDefaultGateway()
    {
        try
        {
            _logger.LogDebug("Attempting to detect default gateway...");

            // Get all network interfaces that are up and have IPv4 connectivity
            var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => nic.OperationalStatus == OperationalStatus.Up)
                .Where(nic => nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .Where(nic => nic.Supports(NetworkInterfaceComponent.IPv4))
                .ToList();

            _logger.LogDebug("Found {Count} active network interfaces", interfaces.Count);

            foreach (var nic in interfaces)
            {
                var ipProps = nic.GetIPProperties();
                var gateways = ipProps.GatewayAddresses;

                foreach (var gateway in gateways)
                {
                    // Skip IPv6 gateways and 0.0.0.0 (no gateway)
                    if (gateway.Address.AddressFamily != AddressFamily.InterNetwork)
                        continue;

                    var address = gateway.Address.ToString();
                    if (address == "0.0.0.0")
                        continue;

                    _logger.LogInformation(
                        "Detected default gateway: {Gateway} on interface {Interface}",
                        address, nic.Name);

                    return address;
                }
            }

            _logger.LogWarning("No default gateway found on any network interface");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to detect default gateway");
            return null;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetCommonGatewayAddresses() => CommonGateways;
}
