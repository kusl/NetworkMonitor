namespace NetworkMonitor.Core.Models;

/// <summary>
/// Represents the result of a single ping operation.
/// Immutable record for thread safety and easy comparison.
/// </summary>
/// <param name="Target">The hostname or IP address that was pinged</param>
/// <param name="Success">Whether the ping succeeded</param>
/// <param name="RoundtripTimeMs">Round-trip time in milliseconds (null if failed)</param>
/// <param name="Timestamp">When the ping was performed (UTC)</param>
/// <param name="ErrorMessage">Error message if the ping failed</param>
public sealed record PingResult(
    string Target,
    bool Success,
    long? RoundtripTimeMs,
    DateTimeOffset Timestamp,
    string? ErrorMessage = null)
{
    /// <summary>
    /// Creates a successful ping result.
    /// </summary>
    public static PingResult Succeeded(string target, long roundtripTimeMs) =>
        new(target, true, roundtripTimeMs, DateTimeOffset.UtcNow);
    
    /// <summary>
    /// Creates a failed ping result.
    /// </summary>
    public static PingResult Failed(string target, string errorMessage) =>
        new(target, false, null, DateTimeOffset.UtcNow, errorMessage);
}
