using Microsoft.Extensions.Logging.Abstractions;
using NetworkMonitor.Core.Services;
using Xunit;

namespace NetworkMonitor.Tests.Services;

/// <summary>
/// Tests for DnsResolverService.
/// Note: These tests run against real DNS, so results depend on the test environment.
/// </summary>
public sealed class DnsResolverServiceTests
{
    private readonly DnsResolverService _resolver;

    public DnsResolverServiceTests()
    {
        _resolver = new DnsResolverService(NullLogger<DnsResolverService>.Instance);
    }

    [Fact]
    public async Task ResolveAsync_WithIpAddress_ReturnsItDirectly()
    {
        // Act
        var result = await _resolver.ResolveAsync("8.8.8.8", TestContext.Current.CancellationToken);

        // Assert
        Assert.True(result.Success);
        Assert.Contains("8.8.8.8", result.ResolvedAddresses);
    }

    [Fact]
    public async Task ResolveAsync_WithIpv6Address_ReturnsItDirectly()
    {
        // Act
        var result = await _resolver.ResolveAsync("2001:4860:4860::8888", TestContext.Current.CancellationToken);

        // Assert
        Assert.True(result.Success);
        Assert.Contains("2001:4860:4860::8888", result.ResolvedAddresses);
    }

    [Fact]
    public async Task ResolveAsync_SupportsCancellation()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => _resolver.ResolveAsync("example.com", cts.Token));
    }
}
