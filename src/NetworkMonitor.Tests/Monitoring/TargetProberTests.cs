using System.Net;
using NetworkMonitor.Core.Configuration;
using NetworkMonitor.Core.Models;
using NetworkMonitor.Tests.Fakes;
using Xunit;

namespace NetworkMonitor.Tests.Monitoring;

public sealed class TargetProberTests
{
    private readonly FakePingService _ping = new();
    private readonly FakeDnsResolver _dns = new();

    [Fact]
    public async Task ProbeAsync_IpLiteral_SkipsDnsAndPingsRequestedCount()
    {
        var prober = Probe.Create(_ping, _dns);

        var result = await prober.ProbeAsync(MonitorTarget.Internet("8.8.8.8"), 3, TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(0, _dns.CallCount);
        Assert.Equal(3, _ping.Calls.Count);
        Assert.Null(result.Dns);
        Assert.Null(result.ResolvedAddress);
    }

    [Fact]
    public async Task ProbeAsync_Hostname_ResolvesOnceAndPingsResolvedAddress()
    {
        var prober = Probe.Create(_ping, _dns);

        var result = await prober.ProbeAsync(new MonitorTarget("Svc", "svc.example", TargetCategory.Custom), 2, TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(1, _dns.CallCount);
        Assert.All(_ping.Calls, a => Assert.Equal(IPAddress.Parse("203.0.113.10"), a));
        Assert.Equal("203.0.113.10", result.ResolvedAddress);
        Assert.NotNull(result.Dns);
    }

    [Fact]
    public async Task ProbeAsync_DnsFailure_FailsWithoutPinging()
    {
        _dns.Responder = _ => DnsResult.Failed(3000, "DNS timed out after 3000ms");
        var prober = Probe.Create(_ping, _dns);

        var result = await prober.ProbeAsync(new MonitorTarget("Svc", "svc.example", TargetCategory.Custom), 3, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Empty(_ping.Calls);
        Assert.Equal(100, result.PacketLossPercent);
        Assert.Contains("DNS timed out", result.Error, StringComparison.Ordinal);
        Assert.False(result.Dns!.Success);
    }

    [Fact]
    public async Task ProbeAsync_DnsChecksDisabled_OmitsDnsResult()
    {
        var prober = Probe.Create(_ping, _dns, new MonitorOptions { EnableDnsChecks = false });

        var result = await prober.ProbeAsync(new MonitorTarget("Svc", "svc.example", TargetCategory.Custom), 1, TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Null(result.Dns);
    }

    [Fact]
    public async Task ProbeAsync_OnlyIPv6WhenDisabled_Fails()
    {
        _dns.Responder = _ => DnsResult.Succeeded([IPAddress.Parse("2001:db8::1")], 1);
        var prober = Probe.Create(_ping, _dns, new MonitorOptions { EnableIPv6 = false });

        var result = await prober.ProbeAsync(new MonitorTarget("Svc", "svc.example", TargetCategory.Custom), 1, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Empty(_ping.Calls);
    }

    [Fact]
    public async Task ProbeAsync_PingThrows_ReturnsFailure()
    {
        _ping.Responder = _ => throw new InvalidOperationException("boom");
        var prober = Probe.Create(_ping, _dns);

        var result = await prober.ProbeAsync(MonitorTarget.Router("192.168.1.1"), 1, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal("boom", result.Error);
    }

    [Fact]
    public async Task ProbeAsync_Cancelled_Throws()
    {
        var prober = Probe.Create(_ping, _dns);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => prober.ProbeAsync(MonitorTarget.Internet("8.8.8.8"), 1, cts.Token));
    }
}
