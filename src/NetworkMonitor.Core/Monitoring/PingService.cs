using System.Net;
using System.Net.NetworkInformation;
using Microsoft.Extensions.Logging;
using NetworkMonitor.Core.Models;

namespace NetworkMonitor.Core.Monitoring;

public interface IPingService
{
    Task<PingSample> PingAsync(IPAddress address, int timeoutMs, CancellationToken cancellationToken);
}

public sealed class IcmpPingService(ILogger<IcmpPingService> logger) : IPingService
{
    public async Task<PingSample> PingAsync(IPAddress address, int timeoutMs, CancellationToken cancellationToken)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(address, TimeSpan.FromMilliseconds(timeoutMs), cancellationToken: cancellationToken);
            return reply.Status == IPStatus.Success
                ? PingSample.Succeeded(reply.RoundtripTime)
                : PingSample.Failed(reply.Status.ToString());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PingException ex)
        {
            logger.LogDebug(ex, "Ping to {Address} threw", address);
            return PingSample.Failed(ex.InnerException?.Message ?? ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unexpected error pinging {Address}", address);
            return PingSample.Failed($"Unexpected error: {ex.Message}");
        }
    }
}
