using System.Net;

namespace NetworkMonitor.Core.Models;

public sealed record DnsResult(bool Success, IReadOnlyList<IPAddress> Addresses, long ElapsedMs, string? Error = null)
{
    public static DnsResult Succeeded(IReadOnlyList<IPAddress> addresses, long elapsedMs) => new(true, addresses, elapsedMs);

    public static DnsResult Failed(long elapsedMs, string error) => new(false, [], elapsedMs, error);
}
