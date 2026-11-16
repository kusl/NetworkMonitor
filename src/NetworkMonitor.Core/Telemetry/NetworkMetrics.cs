using System.Diagnostics.Metrics;
using NetworkMonitor.Core.Models;

namespace NetworkMonitor.Core.Telemetry;

public sealed class NetworkMetrics
{
    public const string MeterName = "NetworkMonitor.Core";

    private readonly Counter<long> _checks;
    private readonly Counter<long> _failures;
    private readonly Histogram<double> _routerLatency;
    private readonly Histogram<double> _internetLatency;
    private readonly Histogram<double> _dnsResolution;
    private readonly Histogram<double> _packetLoss;
    private readonly Histogram<double> _jitter;

    public NetworkMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        var meter = meterFactory.Create(MeterName);
        _checks = meter.CreateCounter<long>("network_monitor.checks", description: "Number of network health checks performed");
        _failures = meter.CreateCounter<long>("network_monitor.failures", description: "Number of ping failures by target type");
        _routerLatency = meter.CreateHistogram<double>("network_monitor.router_latency_ms", unit: "ms", description: "Router ping latency distribution");
        _internetLatency = meter.CreateHistogram<double>("network_monitor.internet_latency_ms", unit: "ms", description: "Internet ping latency distribution");
        _dnsResolution = meter.CreateHistogram<double>("network_monitor.dns_resolution_ms", unit: "ms", description: "DNS resolution latency distribution");
        _packetLoss = meter.CreateHistogram<double>("network_monitor.packet_loss_percent", unit: "%", description: "Packet loss percentage distribution");
        _jitter = meter.CreateHistogram<double>("network_monitor.jitter_ms", unit: "ms", description: "Intra-cycle ping jitter distribution");
    }

    public void RecordCycle(IReadOnlyList<TargetCheckResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        _checks.Add(1);

        foreach (var result in results)
        {
            Record(result);
        }
    }

    private void Record(TargetCheckResult result)
    {
        var label = result.Target.Category switch
        {
            TargetCategory.Router => "router",
            TargetCategory.Internet => "internet",
            _ => result.Target.Name,
        };
        Histogram<double>? latency = result.Target.Category switch
        {
            TargetCategory.Router => _routerLatency,
            TargetCategory.Internet => _internetLatency,
            _ => null,
        };
        var tag = new KeyValuePair<string, object?>("target", label);

        if (latency is not null)
        {
            if (result is { Success: true, LatencyMs: { } ms })
            {
                latency.Record(ms);
            }
            else
            {
                _failures.Add(1, new KeyValuePair<string, object?>("target_type", label));
            }
        }

        _packetLoss.Record(result.PacketLossPercent, tag);

        if (result.JitterMs is { } jitter)
        {
            _jitter.Record(jitter, tag);
        }

        if (result.Dns is { } dns)
        {
            _dnsResolution.Record(dns.ElapsedMs, tag);
        }
    }
}
