using NetworkMonitor.Core.Models;

namespace NetworkMonitor.Core.Monitoring;

public readonly record struct PingStatistics(
    bool Success,
    long? MedianMs,
    long? MinMs,
    long? MaxMs,
    long? JitterMs,
    double PacketLossPercent,
    string? Error)
{
    public static PingStatistics From(IReadOnlyList<PingSample> samples)
    {
        if (samples.Count == 0)
        {
            return new(false, null, null, null, null, 100, "No ping results");
        }

        long[] latencies = [.. samples.Where(s => s.Success && s.RoundtripMs.HasValue).Select(s => s.RoundtripMs!.Value)];
        var loss = (double)(samples.Count - samples.Count(s => s.Success)) / samples.Count * 100;

        if (latencies.Length == 0)
        {
            return new(false, null, null, null, null, loss, samples[0].Error ?? "All pings failed");
        }

        long[] sorted = [.. latencies.Order()];
        return new(true, sorted[sorted.Length / 2], sorted[0], sorted[^1], Jitter(latencies), loss, null);
    }

    private static long? Jitter(long[] latencies)
    {
        if (latencies.Length < 2)
        {
            return null;
        }

        long sum = 0;
        for (var i = 1; i < latencies.Length; i++)
        {
            sum += Math.Abs(latencies[i] - latencies[i - 1]);
        }

        return (long)Math.Round((double)sum / (latencies.Length - 1), MidpointRounding.AwayFromZero);
    }
}
