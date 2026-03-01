namespace NetworkMonitor.Core.Models;

/// <summary>
/// Aggregated check result for a single monitoring target.
/// Includes ping (v4/v6), DNS, and packet loss data.
/// </summary>
/// <param name="Target">The target that was checked</param>
/// <param name="PingResult">IPv4 ping result (or primary ping for IP targets)</param>
/// <param name="PingResultV6">IPv6 ping result (null if IPv6 not applicable)</param>
/// <param name="DnsResult">DNS resolution result (null if target is an IP address)</param>
/// <param name="PacketLossPercent">Percentage of lost packets (0-100)</param>
/// <param name="Timestamp">When this check was performed</param>
public sealed record TargetCheckResult(
    MonitorTarget Target,
    PingResult? PingResult,
    PingResult? PingResultV6,
    DnsResult? DnsResult,
    double PacketLossPercent,
    DateTimeOffset Timestamp);
