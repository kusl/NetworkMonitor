using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using NetworkMonitor.Core.Configuration;
using NetworkMonitor.Core.Models;

namespace NetworkMonitor.Core.Monitoring;

public interface IDnsResolver
{
    Task<DnsResult> ResolveAsync(string hostname, CancellationToken cancellationToken);
}

public sealed class SystemDnsResolver(IOptions<MonitorOptions> options) : IDnsResolver
{
    public async Task<DnsResult> ResolveAsync(string hostname, CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromMilliseconds(options.Value.TimeoutMs);
        var started = Stopwatch.GetTimestamp();
        long Elapsed() => (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(hostname, deadline.Token).WaitAsync(deadline.Token);
            return addresses.Length == 0
                ? DnsResult.Failed(Elapsed(), "No addresses returned")
                : DnsResult.Succeeded(addresses, Elapsed());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return DnsResult.Failed(Elapsed(), $"DNS timed out after {(long)timeout.TotalMilliseconds}ms");
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            return DnsResult.Failed(Elapsed(), ex.Message);
        }
    }
}
