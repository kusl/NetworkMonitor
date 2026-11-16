using Microsoft.Extensions.Configuration;
using NetworkMonitor.Core.Configuration;
using Xunit;

namespace NetworkMonitor.Tests.Configuration;

public sealed class OptionsTests
{
    [Theory]
    [InlineData("auto", true)]
    [InlineData("AUTO", true)]
    [InlineData("", true)]
    [InlineData(null, true)]
    [InlineData("192.168.1.1", false)]
    public void IsRouterAutoDetect(string? value, bool expected) =>
        Assert.Equal(expected, new MonitorOptions { RouterAddress = value! }.IsRouterAutoDetect);

    [Fact]
    public void IsCheckDisabled_IsCaseInsensitive()
    {
        var options = new MonitorOptions { DisabledChecks = ["Router"] };

        Assert.True(options.IsCheckDisabled("router"));
        Assert.False(options.IsCheckDisabled("Internet"));
    }

    [Fact]
    public void Setters_ClampOutOfRangeValues()
    {
        var monitor = new MonitorOptions { PingsPerCycle = 0, IntervalMs = 10, MaxConcurrentChecks = -3, TimeoutMs = 1 };
        var sync = new RemoteSyncOptions { BucketMinutes = 0, SyncIntervalMinutes = 1, BatchSize = 999_999, InitialDelaySeconds = -5 };

        Assert.Equal(1, monitor.PingsPerCycle);
        Assert.Equal(250, monitor.IntervalMs);
        Assert.Equal(1, monitor.MaxConcurrentChecks);
        Assert.Equal(100, monitor.TimeoutMs);
        Assert.Equal(1, sync.BucketMinutes);
        Assert.Equal(5, sync.SyncIntervalMinutes);
        Assert.Equal(5000, sync.BatchSize);
        Assert.Equal(0, sync.InitialDelaySeconds);
    }

    [Fact]
    public void SyncOptions_HaveSinkSpecificDefaults()
    {
        var turso = new RemoteSyncOptions();
        var databricks = new DatabricksSyncOptions();

        Assert.Equal((60, 500, 30), (turso.InitialDelaySeconds, turso.BatchSize, turso.RequestTimeoutSeconds));
        Assert.Equal((90, 200, 120), (databricks.InitialDelaySeconds, databricks.BatchSize, databricks.RequestTimeoutSeconds));
        Assert.False(turso.IsConfigured);
        Assert.False(databricks.IsConfigured);
    }

    [Fact]
    public void Databricks_IsConfigured_WithTokenOrOAuth()
    {
        Assert.True(new DatabricksSyncOptions { WorkspaceUrl = "h", WarehouseId = "w", Token = "t" }.IsConfigured);
        Assert.True(new DatabricksSyncOptions { WorkspaceUrl = "h", WarehouseId = "w", ClientId = "c", ClientSecret = "s" }.IsConfigured);
        Assert.False(new DatabricksSyncOptions { WorkspaceUrl = "h", WarehouseId = "w", ClientId = "c" }.IsConfigured);
    }

    [Fact]
    public void Binding_ReadsExistingAppSettingsShape()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["NetworkMonitor:PingsPerCycle"] = "4",
                ["NetworkMonitor:CustomTargets:0:Name"] = "GitHub",
                ["NetworkMonitor:CustomTargets:0:Address"] = "github.com",
                ["NetworkMonitor:DisabledChecks:0"] = "Router",
                ["RemoteSync:BatchSize"] = "250",
                ["Databricks:StatementWaitSeconds"] = "90",
            })
            .Build();

        var monitor = configuration.GetSection(MonitorOptions.SectionName).Get<MonitorOptions>()!;
        var remote = configuration.GetSection(RemoteSyncOptions.SectionName).Get<RemoteSyncOptions>()!;
        var databricks = configuration.GetSection(DatabricksSyncOptions.SectionName).Get<DatabricksSyncOptions>()!;

        Assert.Equal(4, monitor.PingsPerCycle);
        Assert.Equal("github.com", Assert.Single(monitor.CustomTargets).Address);
        Assert.True(monitor.IsCheckDisabled("router"));
        Assert.Equal(250, remote.BatchSize);
        Assert.Equal(60, remote.InitialDelaySeconds);
        Assert.Equal(50, databricks.StatementWaitSeconds);
    }
}
