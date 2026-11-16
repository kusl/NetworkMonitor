using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace NetworkMonitor.Core.Monitoring;

public interface IGatewayDetector
{
    IReadOnlyList<string> CommonGatewayAddresses { get; }

    string? DetectDefaultGateway();
}

public sealed class GatewayDetector(ILogger<GatewayDetector> logger) : IGatewayDetector
{
    private static readonly string[] CommonGateways =
    [
        "192.168.1.1",
        "192.168.0.1",
        "10.0.0.1",
        "192.168.2.1",
        "192.168.1.254",
        "192.168.0.254",
        "10.0.1.1",
        "192.168.10.1",
        "192.168.100.1",
        "172.16.0.1",
    ];

    private static readonly string[] VirtualInterfaceMarkers =
    [
        "vethernet", "veth", "docker", "br-", "virbr", "tun", "tap", "wg",
        "zt", "tailscale", "utun", "ppp", "vbox", "vmnet", "hyper-v", "wsl",
        "loopback", "isatap", "teredo", "bluetooth",
    ];

    public IReadOnlyList<string> CommonGatewayAddresses => CommonGateways;

    public string? DetectDefaultGateway()
    {
        try
        {
            var best = NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => nic.OperationalStatus == OperationalStatus.Up &&
                              nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(nic => nic.GetIPProperties().GatewayAddresses
                    .Select(g => g.Address)
                    .Where(a => a.AddressFamily == AddressFamily.InterNetwork && IsUsable(a))
                    .Select(a => (Nic: nic, Address: a)))
                .OrderBy(x => Score(x.Nic))
                .FirstOrDefault();

            if (best.Address is null)
            {
                logger.LogWarning("No IPv4 default gateway found on any network interface");
                return null;
            }

            logger.LogInformation("Detected default gateway {Gateway} on {Interface}", best.Address, best.Nic.Name);
            return best.Address.ToString();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to detect default gateway");
            return null;
        }
    }

    private static bool IsUsable(IPAddress address) => !address.Equals(IPAddress.Any);

    private static int Score(NetworkInterface nic)
    {
        var name = $"{nic.Name} {nic.Description}";
        if (VirtualInterfaceMarkers.Any(marker => name.Contains(marker, StringComparison.OrdinalIgnoreCase)))
        {
            return 500;
        }

        return nic.NetworkInterfaceType switch
        {
            NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet => 0,
            NetworkInterfaceType.Wireless80211 => 10,
            NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp => 400,
            _ => 100,
        };
    }
}
