namespace NetworkMonitor.Core.Models;

public sealed record TargetCheckResult(
    MonitorTarget Target,
    bool Success,
    long? LatencyMs,
    long? MinLatencyMs,
    long? MaxLatencyMs,
    long? JitterMs,
    double PacketLossPercent,
    DnsResult? Dns = null,
    string? ResolvedAddress = null,
    string? Error = null)
{
    public static TargetCheckResult Failed(MonitorTarget target, string error, DnsResult? dns = null) =>
        new(target, false, null, null, null, null, 100, dns, null, error);
}
