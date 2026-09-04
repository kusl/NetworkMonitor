namespace NetworkMonitor.Core.Models;

/// <summary>
/// Aggregated check result for a single monitoring target within one cycle.
/// Includes ping (v4/v6), DNS, packet loss, and the intra-burst latency
/// statistics computed from the pings sent this cycle.
/// </summary>
/// <param name="Target">The target that was checked.</param>
/// <param name="PingResult">IPv4 ping result (or primary ping for IP targets). Carries the representative (median) latency.</param>
/// <param name="PingResultV6">IPv6 ping result (null if IPv6 not applicable).</param>
/// <param name="DnsResult">DNS resolution result (null if target is an IP address or DNS checks are off).</param>
/// <param name="PacketLossPercent">Percentage of lost packets in this cycle's burst (0-100).</param>
/// <param name="Timestamp">When this check was performed.</param>
/// <param name="MinLatencyMs">Minimum round-trip time across the successful pings this cycle (null if none succeeded).</param>
/// <param name="MaxLatencyMs">Maximum round-trip time across the successful pings this cycle (null if none succeeded).</param>
/// <param name="JitterMs">Mean absolute successive difference of the successful pings this cycle (classic ping jitter). Null when fewer than two pings succeeded.</param>
/// <param name="ResolvedAddress">The IP address the ping actually targeted, when the target was a hostname (null for IP-literal targets).</param>
/// <remarks>
/// The intra-burst statistics (<see cref="MinLatencyMs"/>, <see cref="MaxLatencyMs"/>,
/// <see cref="JitterMs"/>) and <see cref="ResolvedAddress"/> preserve detail the
/// previous schema discarded. They are persisted per cycle and rolled up over
/// time for trend and remote-replication queries.
/// </remarks>
public sealed record TargetCheckResult(
    MonitorTarget Target,
    PingResult? PingResult,
    PingResult? PingResultV6,
    DnsResult? DnsResult,
    double PacketLossPercent,
    DateTimeOffset Timestamp,
    long? MinLatencyMs = null,
    long? MaxLatencyMs = null,
    long? JitterMs = null,
    string? ResolvedAddress = null);
