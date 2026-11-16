namespace NetworkMonitor.Core.Models;

public enum TargetCategory
{
    Router,
    Internet,
    Custom,
}

public sealed record MonitorTarget(string Name, string Address, TargetCategory Category)
{
    public const string RouterName = "Router";
    public const string InternetName = "Internet";

    public static MonitorTarget Router(string address) => new(RouterName, address, TargetCategory.Router);

    public static MonitorTarget Internet(string address) => new(InternetName, address, TargetCategory.Internet);

    public string StorageCategory => Category switch
    {
        TargetCategory.Router => "router",
        TargetCategory.Internet => "internet",
        _ => "custom",
    };
}
