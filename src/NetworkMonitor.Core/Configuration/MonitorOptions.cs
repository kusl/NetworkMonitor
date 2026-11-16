namespace NetworkMonitor.Core.Configuration;

public sealed class MonitorOptions
{
    public const string SectionName = "NetworkMonitor";
    public const string AutoDetect = "auto";

    public string RouterAddress { get; set; } = AutoDetect;
    public string InternetTarget { get; set; } = "8.8.8.8";
    public int TimeoutMs { get; set => field = Math.Max(100, value); } = 3000;
    public int IntervalMs { get; set => field = Math.Max(250, value); } = 5000;
    public int PingsPerCycle { get; set => field = Math.Clamp(value, 1, 20); } = 3;
    public int ExcellentLatencyMs { get; set; } = 20;
    public int GoodLatencyMs { get; set; } = 200;
    public int DegradedPacketLossPercent { get; set; } = 10;
    public bool EnableFallbackTargets { get; set; } = true;
    public bool EnableIPv6 { get; set; } = true;
    public bool EnableDnsChecks { get; set; } = true;
    public int MaxConcurrentChecks { get; set => field = Math.Max(1, value); } = 6;
    public bool QuietConsole { get; set; } = true;
    public List<CustomTargetConfig> CustomTargets { get; set; } = [];
    public List<string> DisabledChecks { get; set; } = [];

    public bool IsRouterAutoDetect =>
        string.IsNullOrWhiteSpace(RouterAddress) ||
        RouterAddress.Equals(AutoDetect, StringComparison.OrdinalIgnoreCase);

    public bool IsCheckDisabled(string name) =>
        DisabledChecks.Exists(d => d.Equals(name, StringComparison.OrdinalIgnoreCase));
}

public sealed class CustomTargetConfig
{
    public string Name { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
}
