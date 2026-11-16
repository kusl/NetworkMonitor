using NetworkMonitor.Core.Models;

namespace NetworkMonitor.Core.Sync;

public static class RollupRow
{
    public static IReadOnlyList<string> Columns { get; } =
    [
        "machine", "bucket_start", "target_name", "target_address", "target_category",
        "samples", "ok", "avg_rtt_ms", "min_rtt_ms", "max_rtt_ms", "avg_jitter_ms",
        "avg_dns_ms", "avg_loss_pct", "bucket_minutes", "synced_at",
    ];

    public static SqlValue[] Values(CheckRollup row, string machine, long syncedAtMs)
    {
        ArgumentNullException.ThrowIfNull(row);
        return
        [
            Sql.Of(machine),
            Sql.Of(row.BucketStartMs),
            Sql.Of(row.TargetName),
            Sql.Of(row.TargetAddress),
            Sql.Of(row.TargetCategory),
            Sql.Of((long)row.Samples),
            Sql.Of((long)row.Ok),
            Sql.Of(row.AvgRttMs),
            Sql.Of(row.MinRttMs),
            Sql.Of(row.MaxRttMs),
            Sql.Of(row.AvgJitterMs),
            Sql.Of(row.AvgDnsMs),
            Sql.Of(row.AvgLossPct),
            Sql.Of((long)row.BucketMinutes),
            Sql.Of(syncedAtMs),
        ];
    }
}
