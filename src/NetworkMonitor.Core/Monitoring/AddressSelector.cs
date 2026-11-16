using System.Net;
using System.Net.Sockets;

namespace NetworkMonitor.Core.Monitoring;

public static class AddressSelector
{
    public static IPAddress? Select(IReadOnlyList<IPAddress> addresses, bool enableIPv6)
    {
        ArgumentNullException.ThrowIfNull(addresses);

        var ipv4 = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
        if (ipv4 is not null || !enableIPv6)
        {
            return ipv4;
        }

        return addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetworkV6);
    }
}
