using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NetworkMonitor.Core.Configuration;
using NetworkMonitor.Core.Models;
using NetworkMonitor.Core.Monitoring;
using NetworkMonitor.Tests.Fakes;
using Xunit;

namespace NetworkMonitor.Tests.Monitoring;

public sealed class NetworkMonitorServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    private readonly FakePingService _ping = new();
    private readonly FakeDnsResolver _dns = new();
    private readonly FakeNetworkConfigurationService _config = new();
    private readonly MonitorOptions _options = new() { PingsPerCycle = 1 };

    private NetworkMonitorService Create() => new(
        _config,
        Probe.Create(_ping, _dns, _options),
        Probe.Metrics(),
        Options.Create(_options),
        new ManualTimeProvider(Now),
        NullLogger<NetworkMonitorService>.Instance);

    [Fact]
    public async Task CheckAsync_AllHealthy_IsExcellentAndOrdersTargets()
    {
        _options.CustomTargets.Add(new CustomTargetConfig { Name = "Svc", Address = "svc.example" });

        var status = await Create().CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(NetworkHealth.Excellent, status.Health);
        Assert.Equal(Now, status.Timestamp);
        TargetCategory[] expected = [TargetCategory.Router, TargetCategory.Internet, TargetCategory.Custom];
        Assert.Equal(expected, status.Targets.Select(t => t.Target.Category));
        Assert.True(Assert.Single(_config.RouterReports));
    }

    [Fact]
    public async Task CheckAsync_InternetFails_IsPoor()
    {
        _ping.Responder = a => a.Equals(IPAddress.Parse("8.8.8.8")) ? PingSample.Failed("TimedOut") : PingSample.Succeeded(3);

        var status = await Create().CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(NetworkHealth.Poor, status.Health);
        Assert.False(status.Internet!.Success);
    }

    [Fact]
    public async Task CheckAsync_RouterFails_ReportsFailureToConfiguration()
    {
        _ping.Responder = a => a.Equals(IPAddress.Parse("192.168.1.1")) ? PingSample.Failed("TimedOut") : PingSample.Succeeded(3);

        var status = await Create().CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(NetworkHealth.Degraded, status.Health);
        Assert.False(Assert.Single(_config.RouterReports));
    }

    [Fact]
    public async Task CheckAsync_NoRouterDetected_ChecksInternetOnly()
    {
        _config.RouterAddress = null;

        var status = await Create().CheckAsync(TestContext.Current.CancellationToken);

        Assert.Null(status.Router);
        Assert.NotNull(status.Internet);
        Assert.Empty(_config.RouterReports);
    }

    [Fact]
    public async Task CheckAsync_DisabledChecks_AreSkipped()
    {
        _options.DisabledChecks.AddRange(["router", "Internet", "Svc"]);
        _options.CustomTargets.Add(new CustomTargetConfig { Name = "Svc", Address = "svc.example" });
        _options.CustomTargets.Add(new CustomTargetConfig { Name = "Off", Address = "off.example", Enabled = false });
        _options.CustomTargets.Add(new CustomTargetConfig { Name = "On", Address = "on.example" });

        var status = await Create().CheckAsync(TestContext.Current.CancellationToken);

        var only = Assert.Single(status.Targets);
        Assert.Equal("On", only.Target.Name);
        Assert.Equal(NetworkHealth.Good, status.Health);
    }

    [Fact]
    public async Task CheckAsync_CustomDnsFailure_IsRecordedOnTarget()
    {
        _dns.Responder = _ => DnsResult.Failed(10, "NXDOMAIN");
        _options.CustomTargets.Add(new CustomTargetConfig { Name = "Svc", Address = "svc.example" });

        var status = await Create().CheckAsync(TestContext.Current.CancellationToken);

        var svc = Assert.Single(status.Custom);
        Assert.False(svc.Success);
        Assert.False(svc.Dns!.Success);
        Assert.Equal(NetworkHealth.Excellent, status.Health);
    }

    [Fact]
    public async Task CheckAsync_Cancelled_Throws()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create().CheckAsync(cts.Token));
    }
}
