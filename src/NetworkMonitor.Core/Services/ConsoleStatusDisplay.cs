using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;
using NetworkMonitor.Core.Models;

namespace NetworkMonitor.Core.Services;

/// <summary>
/// Console-based status display with ANSI colors.
/// Provides "at a glance" network status visualization.
///
/// All output is written through <see cref="LiveConsole"/>, the single
/// synchronized owner of standard output. Each status line is built as one
/// atomic string and either parked as a transient (overwrite-in-place) line or
/// emitted as a permanent block. Because the logger writes through the same
/// LiveConsole, log records can never cut into or trail a status line — every
/// timestamped line lands on its own line.
///
/// TWO DISPLAY MODES:
///
///   Quiet mode (QuietConsole = true, the default):
///
///     Healthy cycle (no problematic targets):
///       The status line is overwritten in place so the console stays clean.
///
///     Problematic cycle (any target failing, high latency, packet loss,
///     DNS failure):
///       The status line AND full problem details are printed permanently,
///       then the cursor advances past them. This preserves a scrollable
///       history of every incident in the terminal.
///
///   Verbose mode (QuietConsole = false):
///
///     Every cycle prints the summary line followed by ONE line per target
///     (router, internet, and every custom target), then scrolls. Nothing is
///     overwritten. Disabled/unmeasured targets render as "--".
///
/// When standard output is redirected (piped to a file or captured by CI),
/// LiveConsole disables ANSI: colors are dropped and every line — including the
/// otherwise-transient healthy status line — is terminated with a newline, so a
/// captured log is clean and greppable.
///
/// All timestamps are rendered in local time for readability, even though the
/// underlying data is stored in UTC.
///
/// FORMATTING / CULTURE:
///
/// Every numeric, enum, and timestamp value that ends up in a displayed string
/// is formatted with <see cref="CultureInfo.InvariantCulture"/>. This keeps the
/// output stable and greppable regardless of the operating system locale (e.g.
/// a machine set to de-DE will not render "50,0%" or locale-specific digit
/// grouping in latency values). Culture is specified explicitly on every
/// interpolated <see cref="StringBuilder.Append(IFormatProvider, ref StringBuilder.AppendInterpolatedStringHandler)"/>
/// call and on the two <see cref="string.Create(IFormatProvider, ref DefaultInterpolatedStringHandler)"/>
/// helpers below, so no formatting silently depends on the ambient culture.
/// </summary>
public sealed class ConsoleStatusDisplay : IStatusDisplay
{
    private readonly Lock _lock = new();
    private readonly MonitorOptions _options;
    private readonly LiveConsole _console;
    private readonly bool _ansi;

    // ANSI escape sequences (emitted only when the sink is a real terminal).
    private const string Reset = "\x1b[0m";
    private const string Bold = "\x1b[1m";
    private const string Dim = "\x1b[2m";
    private const string Green = "\x1b[32m";
    private const string Yellow = "\x1b[33m";
    private const string Red = "\x1b[31m";
    private const string Cyan = "\x1b[36m";
    private const string Magenta = "\x1b[35m";

    public ConsoleStatusDisplay(IOptions<MonitorOptions> options, LiveConsole console)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(console);

        _options = options.Value;
        _console = console;
        _ansi = console.AnsiEnabled;
    }

    /// <inheritdoc />
    public void UpdateStatus(NetworkStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);

        lock (_lock)
        {
            if (!_options.QuietConsole)
            {
                WriteVerbose(status);
                return;
            }

            WriteQuiet(status);
        }
    }

    /// <summary>
    /// Verbose mode: print the full picture every cycle and let it scroll.
    /// </summary>
    private void WriteVerbose(NetworkStatus status)
    {
        var sb = new StringBuilder();
        sb.Append(BuildStatusLine(status));

        if (status.TargetResults is { Count: > 0 } results)
        {
            foreach (var result in results)
            {
                sb.Append('\n');
                AppendTargetLine(sb, result);
            }
        }

        // Trailing blank line so consecutive cycles are visually separated.
        sb.Append('\n');

        _console.WriteBlock(sb.ToString());
    }

    /// <summary>
    /// Quiet mode: overwrite the status line while healthy, but print and
    /// preserve details whenever a target needs attention.
    /// </summary>
    private void WriteQuiet(NetworkStatus status)
    {
        var problematic = status.TargetResults is { Count: > 0 }
            ? status.TargetResults.Where(IsProblematic).ToList()
            : [];

        if (problematic.Count > 0)
        {
            // Permanent: keep a scrollable history of the incident.
            var sb = new StringBuilder();
            sb.Append(BuildStatusLine(status));
            AppendProblematicTargets(sb, problematic);
            _console.WriteBlock(sb.ToString());
        }
        else
        {
            // Transient: overwritten in place next healthy cycle.
            _console.WriteTransientLine(BuildStatusLine(status));
        }
    }

    /// <summary>
    /// Builds the main one-line status summary (no trailing newline).
    /// </summary>
    private string BuildStatusLine(NetworkStatus status)
    {
        var (color, symbol) = status.Health switch
        {
            NetworkHealth.Excellent => (Green, "●"),
            NetworkHealth.Good => (Green, "○"),
            NetworkHealth.Degraded => (Yellow, "◐"),
            NetworkHealth.Poor => (Red, "◑"),
            NetworkHealth.Offline => (Red, "○"),
            _ => (Reset, "?")
        };

        var sb = new StringBuilder(96);

        sb.Append(Ansi(color)).Append(Ansi(Bold)).Append(symbol).Append(' ')
          .Append(CultureInfo.InvariantCulture, $"{status.Health,-10}").Append(Ansi(Reset)).Append(' ');

        sb.Append(Ansi(Cyan)).Append("Router:").Append(Ansi(Reset)).Append(' ');
        AppendLatencyCell(sb, status.RouterResult);

        sb.Append(Ansi(Cyan)).Append("Internet:").Append(Ansi(Reset)).Append(' ');
        AppendLatencyCell(sb, status.InternetResult);

        // Show custom target summary if any.
        if (status.TargetResults is { Count: > 0 } results)
        {
            var customResults = results
                .Where(r => r.Target.Category == TargetCategory.Custom)
                .ToList();

            if (customResults.Count > 0)
            {
                var ok = customResults.Count(r => r.PingResult?.Success == true);
                var total = customResults.Count;
                var customColor = ok == total ? Green : ok > 0 ? Yellow : Red;

                sb.Append(Ansi(Cyan)).Append("Targets:").Append(Ansi(Reset)).Append(' ')
                  .Append(Ansi(customColor)).Append(CultureInfo.InvariantCulture, $"{ok}/{total}").Append(Ansi(Reset)).Append(' ');
            }
        }

        // Timestamps are stored in UTC; show them in local time for humans.
        sb.Append(Ansi(Magenta)).Append(CultureInfo.InvariantCulture, $"[{status.Timestamp.ToLocalTime():HH:mm:ss}]").Append(Ansi(Reset));

        return sb.ToString();
    }

    /// <summary>
    /// Appends the fixed-width latency cell for the router/internet summary:
    /// latency when it succeeded, FAIL when it failed, or "--" when there was
    /// nothing to measure.
    /// </summary>
    private void AppendLatencyCell(StringBuilder sb, PingResult? result)
    {
        if (result?.Success == true)
        {
            var color = GetLatencyColor(result.RoundtripTimeMs);
            sb.Append(Ansi(color)).Append(CultureInfo.InvariantCulture, $"{result.RoundtripTimeMs,4}ms").Append(Ansi(Reset)).Append(' ');
        }
        else if (result is null)
        {
            sb.Append(Ansi(Dim)).Append("  --  ").Append(Ansi(Reset));
        }
        else
        {
            sb.Append(Ansi(Red)).Append("FAIL").Append(Ansi(Reset)).Append("   ");
        }
    }

    /// <summary>
    /// Appends one line for a single target (verbose mode). Every target is
    /// shown, including healthy ones and any that were not measured.
    /// </summary>
    private void AppendTargetLine(StringBuilder sb, TargetCheckResult result)
    {
        sb.Append("    ");
        AppendResultCell(sb, result);
        sb.Append(' ').Append(result.Target.Name);

        if (result.PacketLossPercent > 0)
        {
            var lossColor = result.PacketLossPercent >= _options.DegradedPacketLossPercent ? Yellow : Dim;
            sb.Append(' ').Append(Ansi(lossColor)).Append(CultureInfo.InvariantCulture, $"loss {result.PacketLossPercent:F0}%").Append(Ansi(Reset));
        }

        if (result.DnsResult is { Success: false })
        {
            sb.Append(' ').Append(Ansi(Red)).Append("[DNS FAIL]").Append(Ansi(Reset));
        }
        else if (result.DnsResult is { Success: true } dns)
        {
            sb.Append(' ').Append(Ansi(Dim)).Append(CultureInfo.InvariantCulture, $"dns {dns.ResolutionTimeMs}ms").Append(Ansi(Reset));
        }
    }

    /// <summary>
    /// Appends the fixed-width result cell for a target: latency when it
    /// succeeded, FAIL when it failed, or "--" when there was nothing to measure.
    /// </summary>
    private void AppendResultCell(StringBuilder sb, TargetCheckResult result)
    {
        var ping = result.PingResult;

        if (ping is null)
        {
            sb.Append(Ansi(Dim)).Append("  --  ").Append(Ansi(Reset));
            return;
        }

        if (ping.Success)
        {
            var color = GetLatencyColor(ping.RoundtripTimeMs);
            sb.Append(Ansi(color)).Append(CultureInfo.InvariantCulture, $"{ping.RoundtripTimeMs,4}ms").Append(Ansi(Reset));
        }
        else
        {
            sb.Append(Ansi(Red)).Append(" FAIL ").Append(Ansi(Reset));
        }
    }

    /// <summary>
    /// Appends details for targets that need attention below the main status line.
    /// </summary>
    private void AppendProblematicTargets(StringBuilder sb, List<TargetCheckResult> problematic)
    {
        sb.Append('\n');
        sb.Append("  ").Append(Ansi(Yellow)).Append(Ansi(Bold))
          .Append(CultureInfo.InvariantCulture, $"⚠ {problematic.Count} target(s) need attention:").Append(Ansi(Reset));

        foreach (var result in problematic)
        {
            sb.Append('\n');

            var name = result.Target.Name;

            if (result.PingResult?.Success != true)
            {
                var error = result.PingResult?.ErrorMessage ?? "No response";
                sb.Append("    ").Append(Ansi(Red)).Append(CultureInfo.InvariantCulture, $"✗ {name,-28}").Append(Ansi(Reset))
                  .Append(' ').Append(Ansi(Dim)).Append(CultureInfo.InvariantCulture, $"FAIL: {error}").Append(Ansi(Reset));
            }
            else
            {
                var latency = result.PingResult.RoundtripTimeMs ?? 0;
                var loss = result.PacketLossPercent;
                var parts = new List<string>();

                if (latency > _options.GoodLatencyMs)
                {
                    parts.Add(string.Create(CultureInfo.InvariantCulture, $"latency {latency}ms"));
                }

                if (loss >= _options.DegradedPacketLossPercent)
                {
                    parts.Add(string.Create(CultureInfo.InvariantCulture, $"loss {loss:F0}%"));
                }

                var detail = parts.Count > 0 ? string.Join(", ", parts) : "degraded";
                var targetColor = latency > _options.GoodLatencyMs ? Red : Yellow;

                sb.Append("    ").Append(Ansi(targetColor)).Append(CultureInfo.InvariantCulture, $"▲ {name,-28}").Append(Ansi(Reset))
                  .Append(' ').Append(Ansi(Dim)).Append(detail).Append(Ansi(Reset));
            }

            if (result.DnsResult is { Success: false })
            {
                sb.Append(' ').Append(Ansi(Red)).Append("[DNS FAIL]").Append(Ansi(Reset));
            }
        }
    }

    /// <summary>
    /// Determines whether a target check result indicates a problem
    /// that warrants console display in quiet mode.
    /// </summary>
    private bool IsProblematic(TargetCheckResult result)
    {
        // A DNS failure is always worth surfacing.
        if (result.DnsResult is { Success: false })
        {
            return true;
        }

        // A target that was never actually pinged (no ping result at all) is
        // not a "problem" to report — there is nothing meaningful to show.
        if (result.PingResult is null)
        {
            return false;
        }

        if (!result.PingResult.Success)
        {
            return true;
        }

        if (result.PingResult.RoundtripTimeMs > _options.GoodLatencyMs)
        {
            return true;
        }

        if (result.PacketLossPercent >= _options.DegradedPacketLossPercent)
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Returns an ANSI color based on latency relative to thresholds.
    /// </summary>
    private string GetLatencyColor(long? latencyMs)
    {
        if (latencyMs == null) return Red;
        if (latencyMs <= _options.ExcellentLatencyMs) return Green;
        if (latencyMs <= _options.GoodLatencyMs) return Green;
        return Yellow;
    }

    /// <summary>
    /// Returns the ANSI sequence when the sink is a terminal, or an empty string
    /// when output is redirected (so captured logs contain no escape codes).
    /// </summary>
    private string Ansi(string sequence) => _ansi ? sequence : string.Empty;

    /// <inheritdoc />
    public void Clear()
    {
        lock (_lock)
        {
            _console.Reset();
        }
    }
}
