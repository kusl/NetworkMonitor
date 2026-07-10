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
    /// <remarks>
    /// This is the time between the START of one cycle and the start of the
    /// next, not the gap after a cycle finishes. If a cycle takes longer than
    /// this interval (common with a large custom target list), the next cycle
    /// starts immediately and a one-time warning is logged.
    /// </remarks>
    public int IntervalMs { get; set; } = 5000;

    /// <summary>
    /// Number of pings per target per cycle.
    /// Default: 3 (for statistical significance)
    /// </summary>
    /// <remarks>
    /// Keep this at 3 or higher. At 2 pings per cycle a single dropped packet
    /// registers as 50% loss, which massively inflates alert frequency.
    /// </remarks>
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
    /// Whether to allow IPv6 addresses when resolving hostnames for pings.
    /// Default: true
    /// </summary>
    /// <remarks>
    /// When resolving a hostname, IPv4 is always preferred for stable,
    /// comparable latency numbers. This flag only decides what happens when a
    /// host resolves to IPv6 ONLY: if true, the IPv6 address is pinged; if
    /// false, the target is reported as failed with a clear message instead of
    /// silently pinging over IPv6. Explicit IPv6 literal targets are always
    /// pinged regardless of this flag.
    /// </remarks>
    public bool EnableIPv6 { get; set; } = true;

    /// <summary>
    /// Whether to perform DNS resolution checks on hostnames.
    /// Default: true
    /// </summary>
    public bool EnableDnsChecks { get; set; } = true;

    /// <summary>
    /// Maximum number of custom targets to check concurrently within a cycle.
    /// Default: 6
    /// </summary>
    /// <remarks>
    /// The router and internet checks always run sequentially and first, so
    /// their latency measurements stay clean. Custom targets - which are mostly
    /// reachability checks - run with this bounded concurrency so a large list
    /// (dozens of hosts) does not push the cycle far past <see cref="IntervalMs"/>.
    ///
    /// Keep this modest on Wi-Fi: too much parallel ICMP causes airtime
    /// contention that inflates the very latencies you are trying to measure.
    /// Values are clamped to at least 1.
    /// </remarks>
    public int MaxConcurrentChecks { get; set; } = 6;

    /// <summary>
    /// When true, console output only shows targets that need attention:
    /// failed pings, latency exceeding GoodLatencyMs, or packet loss
    /// exceeding DegradedPacketLossPercent.
    ///
    /// When false, every target is printed on every cycle.
    ///
    /// All data is still written to the database and telemetry files
    /// regardless of this setting. This only controls what appears
    /// on the console display.
    ///
    /// Default: true (opt everyone in, but user can set to false
    /// to see all targets on every cycle).
    /// </summary>
    /// <remarks>
    /// With dozens of custom targets configured, printing a status line
    /// for every single one every few seconds creates noise that drowns out
    /// the information that actually matters. This flag ensures the console
    /// only surfaces problems that need human attention right now.
    ///
    /// Can also be set via environment variable:
    ///   NetworkMonitor__QuietConsole=true
    /// </remarks>
    public bool QuietConsole { get; set; } = true;

    /// <summary>
    /// Custom targets to monitor (services, private IPs, hostnames).
    /// Each can be individually enabled/disabled at runtime.
    /// </summary>
    public List<CustomTargetConfig> CustomTargets { get; set; } = [];

    /// <summary>
    /// Names of checks to disable at runtime.
    /// Matches against target names (case-insensitive).
    /// Examples: "GoogleDNS", "CloudflareDNS", "Router", "Teams"
    /// </summary>
    public List<string> DisabledChecks { get; set; } = [];

    /// <summary>
    /// Checks if router address should be auto-detected.
    /// </summary>
    public bool IsRouterAutoDetect =>
        string.IsNullOrWhiteSpace(RouterAddress) ||
        RouterAddress.Equals(AutoDetect, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Checks if a named check is disabled.
    /// </summary>
    public bool IsCheckDisabled(string name) =>
        DisabledChecks.Exists(d => d.Equals(name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Configuration for a custom monitoring target.
/// </summary>
public sealed class CustomTargetConfig
{
    /// <summary>
    /// Human-readable name for this target.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Address to monitor. Can be an IP (v4/v6) or hostname.
    /// </summary>
    public string Address { get; set; } = string.Empty;

    /// <summary>
    /// Whether this target is currently enabled.
    /// Can be toggled at runtime.
    /// </summary>
    public bool Enabled { get; set; } = true;
}
