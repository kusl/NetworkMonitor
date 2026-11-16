using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NetworkMonitor.Core.Configuration;
using NetworkMonitor.Core.Monitoring;
using Xunit;

namespace NetworkMonitor.Tests.Monitoring;

public sealed class EnvironmentTests
{
    [Fact]
    public void GatewayDetector_ReturnsValidIpOrNull()
    {
        var gateway = new GatewayDetector(NullLogger<GatewayDetector>.Instance).DetectDefaultGateway();

        Assert.True(gateway is null || IPAddress.TryParse(gateway, out _));
    }

    [Fact]
    public void GatewayDetector_CommonGatewaysAreValidAddresses() =>
        Assert.All(new GatewayDetector(NullLogger<GatewayDetector>.Instance).CommonGatewayAddresses, a => Assert.True(IPAddress.TryParse(a, out _)));

    [Fact]
    public async Task SystemDnsResolver_ResolvesLocalhost()
    {
        var result = await new SystemDnsResolver(Options.Create(new MonitorOptions())).ResolveAsync("localhost", TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.NotEmpty(result.Addresses);
    }

    [Fact]
    public async Task SystemDnsResolver_InvalidName_FailsWithoutThrowing()
    {
        var result = await new SystemDnsResolver(Options.Create(new MonitorOptions())).ResolveAsync("nonexistent.invalid", TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void InternetTargets_PrimaryFirstWithoutDuplicates()
    {
        var candidates = InternetTargets.Candidates("1.1.1.1");

        Assert.Equal("1.1.1.1", candidates[0]);
        Assert.Single(candidates, c => c == "1.1.1.1");
        Assert.Equal(InternetTargets.Defaults.Count, candidates.Count);
    }

    [Fact]
    public void InternetTargets_CustomPrimaryIsPrepended()
    {
        var candidates = InternetTargets.Candidates("example.net");

        Assert.Equal("example.net", candidates[0]);
        Assert.Equal(InternetTargets.Defaults.Count + 1, candidates.Count);
    }
}
