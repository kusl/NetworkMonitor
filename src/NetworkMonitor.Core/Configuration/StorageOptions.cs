namespace NetworkMonitor.Core.Configuration;

public sealed class StorageOptions
{
    public const string SectionName = "Storage";
    public const string DefaultDatabaseFileName = "network-monitor.db";

    public string ApplicationName { get; set; } = "NetworkMonitor";
    public string DatabaseFileName { get; set; } = DefaultDatabaseFileName;
    public string? DataDirectoryOverride { get; set; }
    public int RetentionDays { get; set => field = Math.Max(1, value); } = 30;
    public int PruneEveryNSaves { get; set => field = Math.Max(0, value); } = 200;
}
