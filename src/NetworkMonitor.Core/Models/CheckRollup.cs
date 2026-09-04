namespace NetworkMonitor.Core.Models;

/// <summary>
/// A per-target, per-time-bucket aggregate of check results, computed from the
/// local database and replicated to the optional remote store.
/// </summary>
/// <remarks>
/// Rollups are the unit of remote replication. Instead of shipping one raw row
/// per target per cycle (tens of rows every few seconds - millions of remote
/// row-writes per month), the sync feature ships one compact aggregate per
/// target per closed time bucket. At the default hourly bucket that is at most
/// (number of targets) rows per hour per machine, which keeps the remote well
/// under free-tier write limits while preserving the shape of the data.
///
/// The local database always retains full per-cycle fidelity; rollups are a
/// derived, replication-friendly view of it.
/// </remarks>
/// <param name="BucketStartMs">Start of the time bucket, Unix time in milliseconds (UTC).</param>
/// <param name="BucketMinutes">Width of the bucket in minutes.</param>
/// <param name="TargetName">Friendly target name.</param>
/// <param name="TargetAddress">Target address (IP or hostname); stable identifier for the target.</param>
/// <param name="TargetCategory">Category: router, internet, service, custom.</param>
/// <param name="Samples">Number of cycles that measured this target in the bucket.</param>
/// <param name="Ok">Number of those cycles in which the target responded.</param>
/// <param name="AvgRttMs">Average of the per-cycle median latencies over successful cycles (null if none succeeded).</param>
/// <param name="MinRttMs">Minimum latency observed across the bucket (null if none succeeded).</param>
/// <param name="MaxRttMs">Maximum latency observed across the bucket (null if none succeeded).</param>
/// <param name="AvgJitterMs">Average intra-cycle jitter over the bucket (null if unavailable).</param>
/// <param name="AvgDnsMs">Average DNS resolution time over the bucket (null if no DNS checks ran).</param>
/// <param name="AvgLossPct">Average per-cycle packet loss percentage over the bucket (0-100).</param>
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
