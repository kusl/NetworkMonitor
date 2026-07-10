using System.Net;
using NetworkMonitor.Core.Services;
using Xunit;

namespace NetworkMonitor.Tests.Services;

/// <summary>
/// Tests for PingService.SelectAddress, which decides which resolved IP a
/// hostname ping should target. The policy: prefer IPv4 for stable, comparable
/// latency; use IPv6 only when it is enabled and no IPv4 address is available.
/// </summary>
public sealed class PingAddressSelectorTests
{
    [Fact]
    public void SelectAddress_PrefersIPv4_WhenBothPresent()
    {
        var addresses = new[]
        {
            IPAddress.Parse("2606:4700:4700::1111"),
            IPAddress.Parse("1.1.1.1"),
        };

        var chosen = PingService.SelectAddress(addresses, enableIPv6: true);

        Assert.Equal(IPAddress.Parse("1.1.1.1"), chosen);
    }

    [Fact]
    public void SelectAddress_PrefersIPv4_EvenWhenIPv6Disabled()
    {
        var addresses = new[]
        {
            IPAddress.Parse("2606:4700:4700::1111"),
            IPAddress.Parse("1.1.1.1"),
        };

        var chosen = PingService.SelectAddress(addresses, enableIPv6: false);

        Assert.Equal(IPAddress.Parse("1.1.1.1"), chosen);
    }

    [Fact]
    public void SelectAddress_ReturnsFirstIPv4_WhenMultiple()
    {
        var addresses = new[]
        {
            IPAddress.Parse("8.8.8.8"),
            IPAddress.Parse("8.8.4.4"),
        };

        var chosen = PingService.SelectAddress(addresses, enableIPv6: true);

        Assert.Equal(IPAddress.Parse("8.8.8.8"), chosen);
    }

    [Fact]
    public void SelectAddress_FallsBackToIPv6_WhenEnabledAndNoIPv4()
    {
        var addresses = new[]
        {
            IPAddress.Parse("2001:4860:4860::8888"),
            IPAddress.Parse("2001:4860:4860::8844"),
        };

        var chosen = PingService.SelectAddress(addresses, enableIPv6: true);

        Assert.Equal(IPAddress.Parse("2001:4860:4860::8888"), chosen);
    }

    [Fact]
    public void SelectAddress_ReturnsNull_WhenOnlyIPv6AndIPv6Disabled()
    {
        var addresses = new[]
        {
            IPAddress.Parse("2001:4860:4860::8888"),
        };

        var chosen = PingService.SelectAddress(addresses, enableIPv6: false);

        Assert.Null(chosen);
    }

    [Fact]
    public void SelectAddress_ReturnsNull_WhenEmpty()
    {
        var chosen = PingService.SelectAddress([], enableIPv6: true);

        Assert.Null(chosen);
    }
}
