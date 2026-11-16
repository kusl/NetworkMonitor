using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;
using NetworkMonitor.Core.Configuration;
using NetworkMonitor.Core.Models;

namespace NetworkMonitor.Core.Display;

public interface IStatusDisplay
{
    void Update(NetworkStatus status);

    void Clear();
}

public sealed class ConsoleStatusDisplay(IOptions<MonitorOptions> options, LiveConsole console) : IStatusDisplay
{
    private const string Reset = "\x1b[0m";
    private const string Bold = "\x1b[1m";
    private const string Dim = "\x1b[2m";
    private const string Green = "\x1b[32m";
    private const string Yellow = "\x1b[33m";
    private const string Red = "\x1b[31m";
    private const string Cyan = "\x1b[36m";
    private const string Magenta = "\x1b[35m";

    private readonly MonitorOptions _options = options.Value;

    public void Update(NetworkStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);

        if (!_options.QuietConsole)
        {
            var verbose = new StringBuilder(StatusLine(status));
            foreach (var result in status.Targets)
            {
                verbose.Append('\n');
                AppendTargetLine(verbose, result);
            }

            console.WriteBlock(verbose.Append('\n').ToString());
            return;
        }

        List<TargetCheckResult> problems = [.. status.Targets.Where(IsProblematic)];
        if (problems.Count == 0)
        {
            console.WriteTransientLine(StatusLine(status));
            return;
        }

        var block = new StringBuilder(StatusLine(status));
        AppendProblems(block, problems);
        console.WriteBlock(block.ToString());
    }

    public void Clear() => console.Reset();

    internal string StatusLine(NetworkStatus status)
    {
        var (color, symbol) = status.Health switch
        {
            NetworkHealth.Excellent => (Green, "●"),
            NetworkHealth.Good => (Green, "○"),
            NetworkHealth.Degraded => (Yellow, "◐"),
            NetworkHealth.Poor => (Red, "◑"),
            _ => (Red, "○"),
        };

        var sb = new StringBuilder(96);
        sb.Append(Ansi(color)).Append(Ansi(Bold)).Append(symbol).Append(' ')
          .Append(CultureInfo.InvariantCulture, $"{status.Health,-10}").Append(Ansi(Reset)).Append(' ');
        sb.Append(Ansi(Cyan)).Append("Router:").Append(Ansi(Reset)).Append(' ');
        AppendStatusCell(sb, status.Router);
        sb.Append(Ansi(Cyan)).Append("Internet:").Append(Ansi(Reset)).Append(' ');
        AppendStatusCell(sb, status.Internet);

        List<TargetCheckResult> custom = [.. status.Custom];
        if (custom.Count > 0)
        {
            var ok = custom.Count(r => r.Success);
            var customColor = ok == custom.Count ? Green : ok > 0 ? Yellow : Red;
            sb.Append(Ansi(Cyan)).Append("Targets:").Append(Ansi(Reset)).Append(' ')
              .Append(Ansi(customColor)).Append(CultureInfo.InvariantCulture, $"{ok}/{custom.Count}").Append(Ansi(Reset)).Append(' ');
        }

        sb.Append(Ansi(Magenta)).Append(CultureInfo.InvariantCulture, $"[{status.Timestamp.ToLocalTime():HH:mm:ss}]").Append(Ansi(Reset));
        return sb.ToString();
    }

    private void AppendStatusCell(StringBuilder sb, TargetCheckResult? result)
    {
        if (result is null)
        {
            sb.Append(Ansi(Dim)).Append("  --  ").Append(Ansi(Reset));
        }
        else if (result.Success)
        {
            AppendLatency(sb, result.LatencyMs);
            sb.Append(' ');
        }
        else
        {
            sb.Append(Ansi(Red)).Append("FAIL").Append(Ansi(Reset)).Append("   ");
        }
    }

    private void AppendLatency(StringBuilder sb, long? latencyMs) =>
        sb.Append(Ansi(LatencyColor(latencyMs))).Append(CultureInfo.InvariantCulture, $"{latencyMs,4}ms").Append(Ansi(Reset));

    private void AppendTargetLine(StringBuilder sb, TargetCheckResult result)
    {
        sb.Append("    ");
        if (result.Success)
        {
            AppendLatency(sb, result.LatencyMs);
        }
        else
        {
            sb.Append(Ansi(Red)).Append(" FAIL ").Append(Ansi(Reset));
        }

        sb.Append(' ').Append(result.Target.Name);

        if (result.PacketLossPercent > 0)
        {
            var lossColor = result.PacketLossPercent >= _options.DegradedPacketLossPercent ? Yellow : Dim;
            sb.Append(' ').Append(Ansi(lossColor)).Append(CultureInfo.InvariantCulture, $"loss {result.PacketLossPercent:F0}%").Append(Ansi(Reset));
        }

        switch (result.Dns)
        {
            case { Success: false }:
                sb.Append(' ').Append(Ansi(Red)).Append("[DNS FAIL]").Append(Ansi(Reset));
                break;
            case { Success: true } dns:
                sb.Append(' ').Append(Ansi(Dim)).Append(CultureInfo.InvariantCulture, $"dns {dns.ElapsedMs}ms").Append(Ansi(Reset));
                break;
        }
    }

    private void AppendProblems(StringBuilder sb, List<TargetCheckResult> problems)
    {
        sb.Append('\n').Append("  ").Append(Ansi(Yellow)).Append(Ansi(Bold))
          .Append(CultureInfo.InvariantCulture, $"⚠ {problems.Count} target(s) need attention:").Append(Ansi(Reset));

        foreach (var result in problems)
        {
            sb.Append('\n');
            var name = result.Target.Name;

            if (!result.Success)
            {
                sb.Append("    ").Append(Ansi(Red)).Append(CultureInfo.InvariantCulture, $"✗ {name,-28}").Append(Ansi(Reset))
                  .Append(' ').Append(Ansi(Dim)).Append(CultureInfo.InvariantCulture, $"FAIL: {result.Error ?? "No response"}").Append(Ansi(Reset));
            }
            else
            {
                var latency = result.LatencyMs ?? 0;
                var slow = latency > _options.GoodLatencyMs;
                List<string> parts = [];
                if (slow)
                {
                    parts.Add(string.Create(CultureInfo.InvariantCulture, $"latency {latency}ms"));
                }

                if (result.PacketLossPercent >= _options.DegradedPacketLossPercent)
                {
                    parts.Add(string.Create(CultureInfo.InvariantCulture, $"loss {result.PacketLossPercent:F0}%"));
                }

                sb.Append("    ").Append(Ansi(slow ? Red : Yellow)).Append(CultureInfo.InvariantCulture, $"▲ {name,-28}").Append(Ansi(Reset))
                  .Append(' ').Append(Ansi(Dim)).Append(parts.Count > 0 ? string.Join(", ", parts) : "degraded").Append(Ansi(Reset));
            }

            if (result.Dns is { Success: false })
            {
                sb.Append(' ').Append(Ansi(Red)).Append("[DNS FAIL]").Append(Ansi(Reset));
            }
        }
    }

    private bool IsProblematic(TargetCheckResult result) =>
        result.Dns is { Success: false } ||
        !result.Success ||
        result.LatencyMs > _options.GoodLatencyMs ||
        result.PacketLossPercent >= _options.DegradedPacketLossPercent;

    private string LatencyColor(long? latencyMs) => latencyMs is null ? Red : latencyMs <= _options.GoodLatencyMs ? Green : Yellow;

    private string Ansi(string sequence) => console.AnsiEnabled ? sequence : string.Empty;
}
