using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using NetworkMonitor.Core.Models;

namespace NetworkMonitor.Core.Services;

/// <summary>
/// DNS resolution service using built-in System.Net.Dns.
/// No external packages required.
/// </summary>
public sealed class DnsResolverService : IDnsResolverService
{
    private readonly ILogger<DnsResolverService> _logger;

    public DnsResolverService(ILogger<DnsResolverService> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<DnsResult> ResolveAsync(string hostname, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var stopwatch = Stopwatch.StartNew();

        try
        {
            _logger.LogDebug("Resolving DNS for {Hostname}", hostname);

            // Check if hostname is already an IP address
            if (IPAddress.TryParse(hostname, out _))
            {
                stopwatch.Stop();
                return DnsResult.Succeeded(hostname, [hostname], stopwatch.ElapsedMilliseconds);
            }

            var entry = await Dns.GetHostEntryAsync(hostname, cancellationToken);
            stopwatch.Stop();

            var addresses = entry.AddressList
                .Select(a => a.ToString())
                .ToList();

            if (addresses.Count == 0)
            {
                _logger.LogDebug("DNS resolution for {Hostname} returned no addresses", hostname);
                return DnsResult.Failed(hostname, stopwatch.ElapsedMilliseconds, "No addresses returned");
            }

            _logger.LogDebug(
                "DNS resolution for {Hostname} succeeded: {Count} addresses in {ElapsedMs}ms",
                hostname, addresses.Count, stopwatch.ElapsedMilliseconds);

            return DnsResult.Succeeded(hostname, addresses, stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SocketException ex)
        {
            stopwatch.Stop();
            _logger.LogDebug("DNS resolution for {Hostname} failed: {Error}", hostname, ex.Message);
            return DnsResult.Failed(hostname, stopwatch.ElapsedMilliseconds, ex.Message);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogWarning(ex, "Unexpected error resolving {Hostname}", hostname);
            return DnsResult.Failed(hostname, stopwatch.ElapsedMilliseconds, ex.Message);
        }
    }
}
