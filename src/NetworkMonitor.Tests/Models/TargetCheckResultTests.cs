using NetworkMonitor.Core.Models;
using Xunit;

namespace NetworkMonitor.Tests.Models;

/// <summary>
/// Tests for TargetCheckResult.
/// </summary>
public sealed class TargetCheckResultTests
{
    [Fact]
    public void Constructor_SetsAllProperties()
    {
        // Arrange
        var target = new MonitorTarget("Test", "1.2.3.4", TargetCategory.PublicDns);
        var ping = PingResult.Succeeded("1.2.3.4", 10);
        var dns = DnsResult.Succeeded("test.com", ["1.2.3.4"], 5);

        // Act
        var result = new TargetCheckResult(target, ping, null, dns, 0.0, DateTimeOffset.UtcNow);

        // Assert
        Assert.Equal("Test", result.Target.Name);
        Assert.NotNull(result.PingResult);
        Assert.Null(result.PingResultV6);
        Assert.NotNull(result.DnsResult);
        Assert.Equal(0.0, result.PacketLossPercent);
    }

    [Fact]
    public void MonitorTarget_Categories()
    {
        // Act & Assert
        Assert.Equal(TargetCategory.Router, new MonitorTarget("R", "1.1.1.1", TargetCategory.Router).Category);
        Assert.Equal(TargetCategory.PublicDns, new MonitorTarget("D", "8.8.8.8", TargetCategory.PublicDns).Category);
        Assert.Equal(TargetCategory.Service, new MonitorTarget("S", "teams.ms.com", TargetCategory.Service).Category);
        Assert.Equal(TargetCategory.Custom, new MonitorTarget("C", "10.0.0.1", TargetCategory.Custom).Category);
    }
}
