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
    /// Special value indicating auto-detection should be used.
    /// </summary>
    public const string AutoDetect = "auto";

    /// <summary>
    /// Router/gateway IP address to ping for local network health.
    /// </summary>
    /// <remarks>
    /// Set to "auto" (default) to automatically detect the default gateway.
    /// The gateway is advertised by DHCP and can be read from the OS.
    /// 
    /// If auto-detection fails, common gateway addresses will be tried:
    /// 192.168.1.1, 192.168.0.1, 10.0.0.1, etc.
    /// 
    /// Set to a specific IP address to override auto-detection.
    /// </remarks>
    public string RouterAddress { get; set; } = AutoDetect;

    /// <summary>
    /// Internet target to ping for WAN connectivity.
    /// </summary>
    /// <remarks>
    /// Default: 8.8.8.8 (Google DNS - highly reliable)
    /// 
    /// If this target is unreachable, fallback targets will be tried:
    /// 1.1.1.1 (Cloudflare), 9.9.9.9 (Quad9), etc.
    /// 
    /// This is useful for networks that block specific DNS providers.
    /// </remarks>
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

    /// <summary>
    /// Whether to use fallback targets if primary fails.
    /// Default: true
    /// </summary>
    public bool EnableFallbackTargets { get; set; } = true;

    /// <summary>
    /// Checks if router address should be auto-detected.
    /// </summary>
    public bool IsRouterAutoDetect =>
        string.IsNullOrWhiteSpace(RouterAddress) ||
        RouterAddress.Equals(AutoDetect, StringComparison.OrdinalIgnoreCase);
}
