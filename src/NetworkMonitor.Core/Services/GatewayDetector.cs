using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace NetworkMonitor.Core.Services;

/// <summary>
/// Cross-platform default gateway detector using System.Net.NetworkInformation.
/// Supports both IPv4 and IPv6 gateway detection.
/// </summary>
/// <remarks>
/// This implementation reads the default gateway from the OS routing table,
/// which is populated by DHCP or static configuration. Works on Windows,
/// macOS, and Linux without external dependencies.
///
/// Interfaces are scored so a physical adapter (Ethernet / Wi-Fi) is preferred
/// over virtual ones (VPN tunnels, docker/WSL bridges, hypervisor adapters).
/// This matters on machines running a VPN, where the OS may list the tunnel's
/// gateway first even though the user means their real router.
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

    /// <summary>
    /// Name fragments that identify virtual / non-physical interfaces. Matching
    /// interfaces are de-prioritized so a real router wins when both are present.
    /// </summary>
    private static readonly string[] VirtualInterfaceMarkers =
    [
        "vethernet", "veth", "docker", "br-", "virbr", "tun", "tap", "wg",
        "zt", "tailscale", "utun", "ppp", "vbox", "vmnet", "hyper-v", "wsl",
        "loopback", "isatap", "teredo", "bluetooth",
    ];

    public GatewayDetector(ILogger<GatewayDetector> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public string? DetectDefaultGateway()
    {
        return DetectGateway(AddressFamily.InterNetwork);
    }

    /// <inheritdoc />
    public string? DetectDefaultGatewayV6()
    {
        return DetectGateway(AddressFamily.InterNetworkV6);
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetCommonGatewayAddresses() => CommonGateways;

    private string? DetectGateway(AddressFamily addressFamily)
    {
        var label = addressFamily == AddressFamily.InterNetworkV6 ? "IPv6" : "IPv4";

        try
        {
            _logger.LogDebug("Attempting to detect {Label} default gateway...", label);

            var candidates = NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => nic.OperationalStatus == OperationalStatus.Up)
                .Where(nic => nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(nic => nic.GetIPProperties().GatewayAddresses
                    .Where(g => g.Address.AddressFamily == addressFamily)
                    .Select(g => new { Nic = nic, g.Address }))
                .Where(x => IsUsableGateway(x.Address))
                .OrderBy(x => ScoreInterface(x.Nic))
                .ThenBy(x => IsLinkLocal(x.Address) ? 1 : 0) // prefer global over link-local
                .ToList();

            _logger.LogDebug("Found {Count} candidate {Label} gateway(s)", candidates.Count, label);

            var best = candidates.FirstOrDefault();
            if (best is null)
            {
                _logger.LogWarning("No {Label} default gateway found on any network interface", label);
                return null;
            }

            var address = best.Address.ToString();
            _logger.LogInformation(
                "Detected {Label} default gateway: {Gateway} on interface {Interface}",
                label, address, best.Nic.Name);

            return address;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to detect {Label} default gateway", label);
            return null;
        }
    }

    /// <summary>
    /// Lower score = higher priority. Physical interfaces sort first; virtual /
    /// tunnel interfaces sort last.
    /// </summary>
    private static int ScoreInterface(NetworkInterface nic)
    {
        var name = (nic.Name + " " + nic.Description).ToLowerInvariant();

        if (VirtualInterfaceMarkers.Any(marker => name.Contains(marker, StringComparison.Ordinal)))
        {
            return 500;
        }

        return nic.NetworkInterfaceType switch
        {
            NetworkInterfaceType.Ethernet => 0,
            NetworkInterfaceType.GigabitEthernet => 0,
            NetworkInterfaceType.Wireless80211 => 10,
            NetworkInterfaceType.Tunnel => 400,
            NetworkInterfaceType.Ppp => 400,
            _ => 100,
        };
    }

    private static bool IsUsableGateway(IPAddress address)
    {
        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
        {
            return false;
        }

        // A link-local IPv6 gateway is only usable if it carries a scope id
        // (e.g. fe80::1%eth0); without it, Ping cannot route to it.
        if (address.AddressFamily == AddressFamily.InterNetworkV6 &&
            address.IsIPv6LinkLocal &&
            address.ScopeId == 0)
        {
            return false;
        }

        return true;
    }

    private static bool IsLinkLocal(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetworkV6 && address.IsIPv6LinkLocal;
}
