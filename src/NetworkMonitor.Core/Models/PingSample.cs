namespace NetworkMonitor.Core.Models;

public readonly record struct PingSample(bool Success, long? RoundtripMs, string? Error)
{
    public static PingSample Succeeded(long roundtripMs) => new(true, roundtripMs, null);

    public static PingSample Failed(string error) => new(false, null, error);
}
