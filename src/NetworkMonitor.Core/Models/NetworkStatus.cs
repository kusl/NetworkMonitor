namespace NetworkMonitor.Core.Models;

public enum NetworkHealth
{
    Offline = 0,
    Poor = 1,
    Degraded = 2,
    Good = 3,
    Excellent = 4,
}

public sealed record NetworkStatus(
    NetworkHealth Health,
    string Message,
    DateTimeOffset Timestamp,
    IReadOnlyList<TargetCheckResult> Targets)
{
    public TargetCheckResult? Router => Find(TargetCategory.Router);

    public TargetCheckResult? Internet => Find(TargetCategory.Internet);

    public IEnumerable<TargetCheckResult> Custom => Targets.Where(t => t.Target.Category == TargetCategory.Custom);

    private TargetCheckResult? Find(TargetCategory category) =>
        Targets.FirstOrDefault(t => t.Target.Category == category);
}
