using NetworkMonitor.Core.Services;

namespace NetworkMonitor.Tests.Fakes;

/// <summary>
/// Fake gateway detector for testing.
/// </summary>
public sealed class FakeGatewayDetector : IGatewayDetector
{
    private string? _gatewayToReturn;
    private readonly List<string> _commonGateways = ["192.168.1.1", "192.168.0.1", "10.0.0.1"];

    /// <summary>
    /// Configures the detector to return a specific gateway.
    /// </summary>
    public FakeGatewayDetector WithGateway(string? gateway)
    {
        _gatewayToReturn = gateway;
        return this;
    }

    /// <summary>
    /// Configures the detector to return null (no gateway found).
    /// </summary>
    public FakeGatewayDetector WithNoGateway()
    {
        _gatewayToReturn = null;
        return this;
    }

    /// <summary>
    /// Configures the common gateways list.
    /// </summary>
    public FakeGatewayDetector WithCommonGateways(params string[] gateways)
    {
        _commonGateways.Clear();
        _commonGateways.AddRange(gateways);
        return this;
    }

    public string? DetectDefaultGateway() => _gatewayToReturn;

    public IReadOnlyList<string> GetCommonGatewayAddresses() => _commonGateways;
}
