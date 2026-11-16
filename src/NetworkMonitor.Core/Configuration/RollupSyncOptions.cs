namespace NetworkMonitor.Core.Configuration;

public abstract class RollupSyncOptions
{
    protected RollupSyncOptions(int initialDelaySeconds, int batchSize, int requestTimeoutSeconds)
    {
        InitialDelaySeconds = initialDelaySeconds;
        BatchSize = batchSize;
        RequestTimeoutSeconds = requestTimeoutSeconds;
    }

    public int BucketMinutes { get; set => field = Math.Max(1, value); } = 60;
    public int SyncIntervalMinutes { get; set => field = Math.Max(5, value); } = 60;
    public int InitialDelaySeconds { get; set => field = Math.Max(0, value); }
    public int BatchSize { get; set => field = Math.Clamp(value, 1, 5000); }
    public int MaxRowsPerSync { get; set; } = 25000;
    public int RequestTimeoutSeconds { get; set; }

    public abstract bool IsConfigured { get; }
}
