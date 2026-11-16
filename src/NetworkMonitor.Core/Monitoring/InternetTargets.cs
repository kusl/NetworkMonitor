namespace NetworkMonitor.Core.Monitoring;

public static class InternetTargets
{
    public static IReadOnlyList<string> Defaults { get; } =
    [
        "8.8.8.8",
        "1.1.1.1",
        "8.8.4.4",
        "1.0.0.1",
        "9.9.9.9",
        "208.67.222.222",
        "208.67.220.220",
    ];

    public static IReadOnlyList<string> Candidates(string? primary)
    {
        if (string.IsNullOrWhiteSpace(primary))
        {
            return Defaults;
        }

        return [primary, .. Defaults.Where(d => !d.Equals(primary, StringComparison.OrdinalIgnoreCase))];
    }
}
