using NetworkMonitor.Core.Models;

namespace NetworkMonitor.Core.Services;

/// <summary>
/// Abstraction for ping operations.
/// Allows for easy testing with fake implementations.
/// </summary>
public interface IPingService
{
    /// <summary>
    /// Sends a single ICMP ping to the specified target.
    /// </summary>
    /// <param name="target">Hostname or IP address</param>
    /// <param name="timeoutMs">Timeout in milliseconds</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result of the ping operation</returns>
    Task<PingResult> PingAsync(
        string target, 
        int timeoutMs, 
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Sends multiple pings and returns all results.
    /// Useful for calculating statistics like packet loss.
    /// </summary>
    /// <param name="target">Hostname or IP address</param>
    /// <param name="count">Number of pings to send</param>
    /// <param name="timeoutMs">Timeout per ping in milliseconds</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>All ping results</returns>
    Task<IReadOnlyList<PingResult>> PingMultipleAsync(
        string target,
        int count,
        int timeoutMs,
        CancellationToken cancellationToken = default);
}
