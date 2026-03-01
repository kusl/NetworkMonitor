using NetworkMonitor.Core.Models;

namespace NetworkMonitor.Core.Services;

/// <summary>
/// Performs DNS resolution checks.
/// </summary>
public interface IDnsResolverService
{
    /// <summary>
    /// Resolves a hostname to IP addresses.
    /// </summary>
    /// <param name="hostname">Hostname to resolve</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>DNS resolution result</returns>
    Task<DnsResult> ResolveAsync(string hostname, CancellationToken cancellationToken = default);
}
