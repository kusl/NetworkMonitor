using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NetworkMonitor.Core.Configuration;
using NetworkMonitor.Core.Models;
using NetworkMonitor.Core.Monitoring;
using NetworkMonitor.Tests.Fakes;
using Xunit;

namespace NetworkMonitor.Tests.Monitoring;

public sealed class NetworkConfigurationServiceTests
{
    private readonly FakePingService _ping = new();
    private readonly FakeGatewayDetector _gateway = new();
    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 11, 16, 0, 0, 0, TimeSpan.Zero));
    private readonly MonitorOptions _options = new();

    private NetworkConfigurationService Create() => new(
        _gateway,
        Probe.Create(_ping, new FakeDnsResolver(), _options),
        Options.Create(_options),
        _time,
        NullLogger<NetworkConfigurationService>.Instance);

    private void Reachable(params string[] addresses) =>
        _ping.Responder = a => addresses.Contains(a.ToString()) ? PingSample.Succeeded(1) : PingSample.Failed("TimedOut");

    [Fact]
    public async Task GetRouterAddress_Explicit_ReturnsConfiguredWithoutProbing()
    {
        _options.RouterAddress = "10.1.1.1";
        using var service = Create();

        Assert.Equal("10.1.1.1", await service.GetRouterAddressAsync(TestContext.Current.CancellationToken));
        Assert.DoesNotContain(_ping.Calls, a => a.Equals(IPAddress.Parse("10.1.1.1")));
    }

    [Fact]
    public async Task GetRouterAddress_AutoDetect_UsesReachableDetectedGateway()
    {
        _gateway.Gateway = "192.168.50.1";
        Reachable("192.168.50.1", "8.8.8.8");
        using var service = Create();

        Assert.Equal("192.168.50.1", await service.GetRouterAddressAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetRouterAddress_DetectedUnreachable_FallsBackToCommonGateway()
    {
        _gateway.Gateway = "192.168.50.1";
        Reachable("192.168.0.1", "8.8.8.8");
        using var service = Create();

        Assert.Equal("192.168.0.1", await service.GetRouterAddressAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetInternetTarget_PrimaryUnreachable_UsesFirstReachableFallback()
    {
        Reachable("9.9.9.9");
        using var service = Create();

        Assert.Equal("9.9.9.9", await service.GetInternetTargetAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetInternetTarget_FallbackDisabled_ReturnsPrimary()
    {
        _options.EnableFallbackTargets = false;
        Reachable();
        using var service = Create();

        Assert.Equal("8.8.8.8", await service.GetInternetTargetAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Results_AreCachedAcrossCalls()
    {
        _gateway.Gateway = "192.168.50.1";
        using var service = Create();
        await service.GetRouterAddressAsync(TestContext.Current.CancellationToken);
        var probes = _ping.Calls.Count;

        await service.GetRouterAddressAsync(TestContext.Current.CancellationToken);
        await service.GetInternetTargetAsync(TestContext.Current.CancellationToken);

        Assert.Equal(probes, _ping.Calls.Count);
    }

    [Fact]
    public async Task RepeatedRouterFailures_TriggerRedetectionAfterCooldown()
    {
        _gateway.Gateway = "192.168.50.1";
        using var service = Create();
        Assert.Equal("192.168.50.1", await service.GetRouterAddressAsync(TestContext.Current.CancellationToken));

        _gateway.Gateway = "192.168.60.1";
        for (var i = 0; i < 5; i++)
        {
            service.ReportRouterCheckResult(false);
        }

        Assert.Equal("192.168.50.1", await service.GetRouterAddressAsync(TestContext.Current.CancellationToken));

        _time.Now += TimeSpan.FromMinutes(2);
        Assert.Equal("192.168.60.1", await service.GetRouterAddressAsync(TestContext.Current.CancellationToken));
    }
}
