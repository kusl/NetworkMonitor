namespace NetworkMonitor.Core.Models;

/// <summary>
/// Configuration options for the network monitor.
/// Bound from appsettings.json or environment variables.
/// </summary>
public sealed class MonitorOptions
{
    /// <summary>
    /// Configuration section name in appsettings.json
    /// </summary>
    public const string SectionName = "NetworkMonitor";
    
    /// <summary>
    /// Router/gateway IP address to ping for local network health.
    /// Default: 192.168.1.1 (common home router)
    /// </summary>
    public string RouterAddress { get; set; } = "192.168.1.1";
    
    /// <summary>
    /// Internet target to ping for WAN connectivity.
    /// Default: 8.8.8.8 (Google DNS - highly reliable)
    /// </summary>
    public string InternetTarget { get; set; } = "8.8.8.8";
    
    /// <summary>
    /// Timeout for each ping in milliseconds.
    /// Default: 3000ms (3 seconds)
    /// </summary>
    public int TimeoutMs { get; set; } = 3000;
    
    /// <summary>
    /// Interval between monitoring cycles in milliseconds.
    /// Default: 5000ms (5 seconds)
    /// </summary>
    public int IntervalMs { get; set; } = 5000;
    
    /// <summary>
    /// Number of pings per target per cycle.
    /// Default: 3 (for statistical significance)
    /// </summary>
    public int PingsPerCycle { get; set; } = 3;
    
    /// <summary>
    /// Latency threshold (ms) below which is considered "excellent".
    /// Default: 20ms
    /// </summary>
    public int ExcellentLatencyMs { get; set; } = 20;
    
    /// <summary>
    /// Latency threshold (ms) below which is considered "good".
    /// Default: 100ms
    /// </summary>
    public int GoodLatencyMs { get; set; } = 100;
    
    /// <summary>
    /// Packet loss percentage above which network is "degraded".
    /// Default: 10%
    /// </summary>
    public int DegradedPacketLossPercent { get; set; } = 10;
}
