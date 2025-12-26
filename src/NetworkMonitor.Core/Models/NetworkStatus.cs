namespace NetworkMonitor.Core.Models;

/// <summary>
/// Represents the overall network health status.
/// This is the "at a glance" view that's our highest priority.
/// </summary>
/// <remarks>
/// Values are ordered from worst (0) to best (4) for natural comparison.
/// This allows: NetworkHealth.Excellent > NetworkHealth.Poor
/// </remarks>
public enum NetworkHealth
{
    /// <summary>No connectivity</summary>
    Offline = 0,

    /// <summary>Significant connectivity issues</summary>
    Poor = 1,

    /// <summary>Some packet loss or high latency</summary>
    Degraded = 2,

    /// <summary>All targets responding but some latency</summary>
    Good = 3,

    /// <summary>All targets responding with good latency</summary>
    Excellent = 4
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
