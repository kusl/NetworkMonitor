namespace NetworkMonitor.Core.Configuration;

public sealed class DatabricksSyncOptions() : RollupSyncOptions(initialDelaySeconds: 90, batchSize: 200, requestTimeoutSeconds: 120)
{
    public const string SectionName = "Databricks";

    public string WorkspaceUrl { get; set; } = string.Empty;
    public string WarehouseId { get; set; } = string.Empty;
    public string Catalog { get; set; } = "workspace";
    public string Schema { get; set; } = "default";
    public string TableName { get; set; } = "network_monitor_rollups";
    public string Token { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string OAuthScope { get; set; } = "all-apis";
    public int StatementWaitSeconds { get; set => field = Math.Clamp(value, 5, 50); } = 30;

    public bool HasToken => !string.IsNullOrWhiteSpace(Token);

    public bool HasOAuthCredentials =>
        !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);

    public override bool IsConfigured =>
        !string.IsNullOrWhiteSpace(WorkspaceUrl) &&
        !string.IsNullOrWhiteSpace(WarehouseId) &&
        (HasToken || HasOAuthCredentials);
}
