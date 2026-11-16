namespace NetworkMonitor.Core.Configuration;

public sealed class RemoteSyncOptions() : RollupSyncOptions(initialDelaySeconds: 60, batchSize: 500, requestTimeoutSeconds: 30)
{
    public const string SectionName = "RemoteSync";

    public string Url { get; set; } = string.Empty;
    public string AuthToken { get; set; } = string.Empty;
    public string TableName { get; set; } = "check_rollups";

    public override bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Url) && !string.IsNullOrWhiteSpace(AuthToken);
}
