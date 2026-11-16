using System.Net;
using NetworkMonitor.Core.Monitoring;
using Xunit;

namespace NetworkMonitor.Tests.Monitoring;

public sealed class AddressSelectorTests
{
    private static readonly IPAddress V4A = IPAddress.Parse("203.0.113.1");
    private static readonly IPAddress V4B = IPAddress.Parse("203.0.113.2");
    private static readonly IPAddress V6 = IPAddress.Parse("2001:db8::1");

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Select_PrefersFirstIPv4(bool enableIPv6) =>
        Assert.Equal(V4A, AddressSelector.Select([V6, V4A, V4B], enableIPv6));

    [Fact]
    public void Select_FallsBackToIPv6_WhenEnabled() =>
        Assert.Equal(V6, AddressSelector.Select([V6], enableIPv6: true));

    [Fact]
    public void Select_ReturnsNull_WhenOnlyIPv6AndDisabled() =>
        Assert.Null(AddressSelector.Select([V6], enableIPv6: false));

    [Fact]
    public void Select_ReturnsNull_WhenEmpty() =>
        Assert.Null(AddressSelector.Select([], enableIPv6: true));
}
