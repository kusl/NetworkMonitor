namespace NetworkMonitor.Core.Models;

public sealed record CheckRollup(
    long BucketStartMs,
    int BucketMinutes,
    string TargetName,
    string TargetAddress,
    string TargetCategory,
    int Samples,
    int Ok,
    double? AvgRttMs,
    long? MinRttMs,
    long? MaxRttMs,
    double? AvgJitterMs,
    double? AvgDnsMs,
    double AvgLossPct);
