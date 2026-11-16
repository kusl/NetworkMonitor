using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetworkMonitor.Core.Configuration;
using NetworkMonitor.Core.Models;

namespace NetworkMonitor.Core.Monitoring;

public interface ITargetProber
{
    Task<TargetCheckResult> ProbeAsync(MonitorTarget target, int pingCount, CancellationToken cancellationToken);
}

public sealed class TargetProber(
    IPingService pingService,
    IDnsResolver dnsResolver,
    IOptions<MonitorOptions> options,
    ILogger<TargetProber> logger) : ITargetProber
{
    private static readonly TimeSpan PingSpacing = TimeSpan.FromMilliseconds(50);

    public async Task<TargetCheckResult> ProbeAsync(MonitorTarget target, int pingCount, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        var settings = options.Value;

        try
        {
            DnsResult? dns = null;
            string? resolved = null;
            if (!IPAddress.TryParse(target.Address, out var address))
            {
                dns = await dnsResolver.ResolveAsync(target.Address, cancellationToken);
                var reported = settings.EnableDnsChecks ? dns : null;

                if (!dns.Success)
                {
                    return TargetCheckResult.Failed(target, $"DNS resolution failed: {dns.Error}", reported);
                }

                address = AddressSelector.Select(dns.Addresses, settings.EnableIPv6);
                if (address is null)
                {
                    return TargetCheckResult.Failed(target, "Host resolved only to IPv6 but IPv6 is disabled", reported);
                }

                dns = reported;
                resolved = address.ToString();
            }

            var samples = new List<PingSample>(pingCount);
            for (var i = 0; i < pingCount; i++)
            {
                if (i > 0)
                {
                    await Task.Delay(PingSpacing, cancellationToken);
                }

                samples.Add(await pingService.PingAsync(address, settings.TimeoutMs, cancellationToken));
            }

            var stats = PingStatistics.From(samples);
            return new TargetCheckResult(
                target,
                stats.Success,
                stats.MedianMs,
                stats.MinMs,
                stats.MaxMs,
                stats.JitterMs,
                stats.PacketLossPercent,
                dns,
                resolved,
                stats.Error);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error checking {Name} ({Address})", target.Name, target.Address);
            return TargetCheckResult.Failed(target, ex.Message);
        }
    }
}
