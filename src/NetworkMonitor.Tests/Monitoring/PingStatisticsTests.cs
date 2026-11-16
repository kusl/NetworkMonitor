using NetworkMonitor.Core.Models;
using NetworkMonitor.Core.Monitoring;
using Xunit;

namespace NetworkMonitor.Tests.Monitoring;

public sealed class PingStatisticsTests
{
    [Fact]
    public void From_AllSucceed_ComputesMedianMinMaxAndJitter()
    {
        var stats = PingStatistics.From([PingSample.Succeeded(10), PingSample.Succeeded(30), PingSample.Succeeded(20)]);

        Assert.True(stats.Success);
        Assert.Equal(20, stats.MedianMs);
        Assert.Equal(10, stats.MinMs);
        Assert.Equal(30, stats.MaxMs);
        Assert.Equal(15, stats.JitterMs);
        Assert.Equal(0, stats.PacketLossPercent);
        Assert.Null(stats.Error);
    }

    [Fact]
    public void From_PartialLoss_SucceedsWithLossPercentage()
    {
        var stats = PingStatistics.From([PingSample.Succeeded(10), PingSample.Failed("TimedOut"), PingSample.Succeeded(12), PingSample.Failed("TimedOut")]);

        Assert.True(stats.Success);
        Assert.Equal(50, stats.PacketLossPercent);
        Assert.Equal(2, stats.JitterMs);
    }

    [Fact]
    public void From_AllFail_ReportsFirstError()
    {
        var stats = PingStatistics.From([PingSample.Failed("TimedOut"), PingSample.Failed("Other")]);

        Assert.False(stats.Success);
        Assert.Equal(100, stats.PacketLossPercent);
        Assert.Equal("TimedOut", stats.Error);
        Assert.Null(stats.MedianMs);
    }

    [Fact]
    public void From_SingleSample_HasNoJitter()
    {
        var stats = PingStatistics.From([PingSample.Succeeded(7)]);

        Assert.Equal(7, stats.MedianMs);
        Assert.Null(stats.JitterMs);
    }

    [Fact]
    public void From_Empty_Fails()
    {
        var stats = PingStatistics.From([]);

        Assert.False(stats.Success);
        Assert.Equal(100, stats.PacketLossPercent);
    }
}
