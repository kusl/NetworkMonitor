namespace NetworkMonitor.Core.Models;

/// <summary>
/// Represents the overall network health status.
/// This is the primary output of the monitoring system.
/// </summary>
/// <param name="Health">Overall network health classification</param>
/// <param name="RouterResult">Ping result for the default gateway</param>
/// <param name="InternetResult">Ping result for the internet target</param>
/// <param name="Timestamp">When this status was determined</param>
/// <param name="Message">Human-readable status message</param>
/// <param name="TargetResults">Detailed results for all monitored targets</param>
public sealed record NetworkStatus(
    NetworkHealth Health,
    PingResult? RouterResult,
    PingResult? InternetResult,
    DateTimeOffset Timestamp,
    string Message,
    IReadOnlyList<TargetCheckResult>? TargetResults = null)
{
    /// <summary>
    /// Whether the network is usable (Excellent, Good, or Degraded).
    /// </summary>
    public bool IsUsable => Health is NetworkHealth.Excellent
        or NetworkHealth.Good
        or NetworkHealth.Degraded;
}

/// <summary>
/// Network health classifications, from best to worst.
/// </summary>
public enum NetworkHealth
{
    /// <summary>All targets responding with very low latency.</summary>
    Excellent,

    /// <summary>All targets responding with acceptable latency.</summary>
    Good,

    /// <summary>Some issues detected (packet loss, high latency on some targets).</summary>
    Degraded,

    /// <summary>Significant connectivity issues.</summary>
    Poor,

    /// <summary>No network connectivity.</summary>
    Offline
}
