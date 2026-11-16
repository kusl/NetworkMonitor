using System.Diagnostics.Metrics;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NetworkMonitor.Core.Configuration;
using NetworkMonitor.Core.Models;
using NetworkMonitor.Core.Monitoring;
using NetworkMonitor.Core.Telemetry;

namespace NetworkMonitor.Tests.Fakes;

internal sealed class FakePingService : IPingService
{
    public Func<IPAddress, PingSample> Responder { get; set; } = _ => PingSample.Succeeded(10);

    public List<IPAddress> Calls { get; } = [];

    public Task<PingSample> PingAsync(IPAddress address, int timeoutMs, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (Calls)
        {
            Calls.Add(address);
        }

        return Task.FromResult(Responder(address));
    }
}

internal sealed class FakeDnsResolver : IDnsResolver
{
    public Func<string, DnsResult> Responder { get; set; } =
        _ => DnsResult.Succeeded([IPAddress.Parse("203.0.113.10")], 5);

    private int _calls;

    public int CallCount => _calls;

    public Task<DnsResult> ResolveAsync(string hostname, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _calls);
        return Task.FromResult(Responder(hostname));
    }
}

internal sealed class FakeGatewayDetector : IGatewayDetector
{
    public string? Gateway { get; set; }

    public IReadOnlyList<string> CommonGatewayAddresses { get; set; } = ["192.168.1.1", "192.168.0.1", "10.0.0.1"];

    public string? DetectDefaultGateway() => Gateway;
}

internal sealed class FakeNetworkConfigurationService : INetworkConfigurationService
{
    public string? RouterAddress { get; set; } = "192.168.1.1";

    public string InternetTarget { get; set; } = "8.8.8.8";

    public List<bool> RouterReports { get; } = [];

    public Task<string?> GetRouterAddressAsync(CancellationToken cancellationToken) => Task.FromResult(RouterAddress);

    public Task<string> GetInternetTargetAsync(CancellationToken cancellationToken) => Task.FromResult(InternetTarget);

    public void ReportRouterCheckResult(bool reachable) => RouterReports.Add(reachable);
}

internal sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

internal sealed class TestMeterFactory : IMeterFactory
{
    private readonly List<Meter> _meters = [];

    public Meter Create(MeterOptions options)
    {
        var meter = new Meter(options);
        _meters.Add(meter);
        return meter;
    }

    public void Dispose()
    {
        foreach (var meter in _meters)
        {
            meter.Dispose();
        }
    }
}

internal static class Probe
{
    public static TargetProber Create(FakePingService ping, FakeDnsResolver dns, MonitorOptions? options = null) =>
        new(ping, dns, Options.Create(options ?? new MonitorOptions()), NullLogger<TargetProber>.Instance);

    public static NetworkMetrics Metrics() => new(new TestMeterFactory());
}
