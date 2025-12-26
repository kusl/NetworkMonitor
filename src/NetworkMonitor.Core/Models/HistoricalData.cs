namespace NetworkMonitor.Core.Models;

/// <summary>
/// Aggregated historical data for trendline display.
/// </summary>
/// <param name="Period">Time period this data covers</param>
/// <param name="AverageLatencyMs">Average latency in milliseconds</param>
/// <param name="MinLatencyMs">Minimum latency observed</param>
/// <param name="MaxLatencyMs">Maximum latency observed</param>
/// <param name="PacketLossPercent">Percentage of failed pings</param>
/// <param name="SampleCount">Number of pings in this period</param>
public sealed record HistoricalData(
    DateTimeOffset Period,
    double AverageLatencyMs,
    long MinLatencyMs,
    long MaxLatencyMs,
    double PacketLossPercent,
    int SampleCount);

/// <summary>
/// Time granularity for historical data aggregation.
/// </summary>
public enum TimeGranularity
{
    Minute,
    Hour,
    Day
}
