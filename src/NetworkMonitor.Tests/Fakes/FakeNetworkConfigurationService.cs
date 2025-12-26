using NetworkMonitor.Core.Services;

namespace NetworkMonitor.Tests.Fakes;

/// <summary>
/// Fake network configuration service for testing.
/// </summary>
public sealed class FakeNetworkConfigurationService : INetworkConfigurationService
{
    private string? _routerAddress = "192.168.1.1";
    private string _internetTarget = "8.8.8.8";

    public FakeNetworkConfigurationService WithRouterAddress(string? address)
    {
        _routerAddress = address;
        return this;
    }

    public FakeNetworkConfigurationService WithInternetTarget(string target)
    {
        _internetTarget = target;
        return this;
    }

    public Task<string?> GetRouterAddressAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_routerAddress);

    public Task<string> GetInternetTargetAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_internetTarget);

    public Task InitializeAsync(CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
