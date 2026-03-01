namespace NetworkMonitor.Core.Models;

/// <summary>
/// Result of a DNS resolution check.
/// </summary>
/// <param name="Hostname">The hostname that was resolved</param>
/// <param name="Success">Whether DNS resolution succeeded</param>
/// <param name="ResolvedAddresses">All resolved IP addresses</param>
/// <param name="ResolutionTimeMs">Time taken for DNS resolution in ms</param>
/// <param name="ErrorMessage">Error message if resolution failed</param>
public sealed record DnsResult(
    string Hostname,
    bool Success,
    IReadOnlyList<string> ResolvedAddresses,
    long ResolutionTimeMs,
    string? ErrorMessage = null)
{
    /// <summary>
    /// Creates a successful DNS result.
    /// </summary>
    public static DnsResult Succeeded(string hostname, IReadOnlyList<string> addresses, long resolutionTimeMs) =>
        new(hostname, true, addresses, resolutionTimeMs);

    /// <summary>
    /// Creates a failed DNS result.
    /// </summary>
    public static DnsResult Failed(string hostname, long resolutionTimeMs, string errorMessage) =>
        new(hostname, false, Array.Empty<string>(), resolutionTimeMs, errorMessage);
}
