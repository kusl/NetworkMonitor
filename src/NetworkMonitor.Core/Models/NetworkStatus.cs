namespace NetworkMonitor.Core.Models;

/// <summary>
/// Represents the overall network health status.
/// This is the "at a glance" view that's our highest priority.
/// </summary>
public enum NetworkHealth
{
    /// <summary>All targets responding with good latency</summary>
    Excellent,
    
    /// <summary>All targets responding but some latency</summary>
    Good,
    
    /// <summary>Some packet loss or high latency</summary>
    Degraded,
    
    /// <summary>Significant connectivity issues</summary>
    Poor,
    
    /// <summary>No connectivity</summary>
    Offline
}

/// <summary>
/// Comprehensive network status at a point in time.
/// Aggregates multiple ping results into a single status view.
/// </summary>
/// <param name="Health">Overall health assessment</param>
/// <param name="RouterResult">Result of pinging the local router/gateway</param>
/// <param name="InternetResult">Result of pinging an internet target (e.g., 8.8.8.8)</param>
/// <param name="Timestamp">When this status was computed</param>
/// <param name="Message">Human-readable status message</param>
public sealed record NetworkStatus(
    NetworkHealth Health,
    PingResult? RouterResult,
    PingResult? InternetResult,
    DateTimeOffset Timestamp,
    string Message)
{
    /// <summary>
    /// Quick check if network is usable (Excellent, Good, or Degraded).
    /// </summary>
    public bool IsUsable => Health is NetworkHealth.Excellent 
                            or NetworkHealth.Good 
                            or NetworkHealth.Degraded;
}
