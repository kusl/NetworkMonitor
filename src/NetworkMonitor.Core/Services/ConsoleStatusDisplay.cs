using System.Text;
using Microsoft.Extensions.Options;
using NetworkMonitor.Core.Models;

namespace NetworkMonitor.Core.Services;

/// <summary>
/// Console-based status display with ANSI colors.
/// Provides "at a glance" network status visualization.
/// 
/// When QuietConsole is enabled (default), only targets with problems
/// are shown on the console. Everything is still written to the database
/// and telemetry files — this only controls what appears on screen.
/// 
/// A target is considered "problematic" if any of the following are true:
///   - Ping failed entirely
///   - Latency exceeds GoodLatencyMs threshold
///   - Packet loss exceeds DegradedPacketLossPercent threshold
///   - DNS resolution failed (for hostname targets)
/// </summary>
/// <remarks>
/// Display refresh strategy:
///
/// Each update builds the entire output into a string, writes it with a single
/// Console.Write call, then counts the physical terminal rows it consumed
/// (accounting for ANSI escape codes and line wrapping at the terminal width).
///
/// On the next cycle, the cursor is moved up by exactly that many physical rows
/// and "\x1b[J" (Erase in Display — clear from cursor to end of screen) wipes
/// everything from the status line downward, regardless of wrapping or how many
/// rows the previous output occupied. This avoids the stale-text problem that
/// occurs when clearing by logical line count alone.
///
/// ANSI sequences used:
///   \x1b[{n}A   — Cursor Up by n rows (CUU)
///   \x1b[J      — Erase in Display: cursor to end of screen (ED, mode 0)
///   \r          — Carriage return (move to column 0)
///
/// These are standard ECMA-48 / VT100 sequences supported by all modern
/// terminals on Linux, macOS, and Windows (Terminal + ConHost since Win10 1511).
/// .NET enables virtual terminal processing automatically on Windows.
/// </remarks>
public sealed class ConsoleStatusDisplay : IStatusDisplay
{
    private readonly Lock _lock = new();
    private readonly MonitorOptions _options;

    /// <summary>
    /// Number of physical terminal rows our previous output occupied.
    /// Used to move the cursor back to the start of the status line
    /// before clearing and rewriting.
    /// </summary>
    private int _previousPhysicalRows;

    // ANSI color codes
    private const string Reset = "\x1b[0m";
    private const string Bold = "\x1b[1m";
    private const string Green = "\x1b[32m";
    private const string Yellow = "\x1b[33m";
    private const string Red = "\x1b[31m";
    private const string Cyan = "\x1b[36m";
    private const string Magenta = "\x1b[35m";
    private const string Dim = "\x1b[2m";

    public ConsoleStatusDisplay(IOptions<MonitorOptions> options)
    {
        _options = options.Value;
    }

    /// <inheritdoc />
    public void UpdateStatus(NetworkStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);

        lock (_lock)
        {
            // Move cursor back to the start of our previous output.
            // _previousPhysicalRows tracks how many physical terminal rows
            // (accounting for line wrapping) the last update occupied.
            // The cursor is on the last of those rows, so we go up (n-1).
            if (_previousPhysicalRows > 1)
            {
                Console.Write($"\x1b[{_previousPhysicalRows - 1}A");
            }

            // Column 0, then clear from cursor to end of screen.
            // This single command wipes all stale text regardless of
            // how many physical rows it spanned or whether lines wrapped.
            Console.Write("\r\x1b[J");

            // Build the entire output into a single string so we can
            // measure its physical row count accurately after writing.
            var sb = new StringBuilder(512);
            BuildStatusLine(sb, status);

            if (_options.QuietConsole && status.TargetResults is { Count: > 0 })
            {
                BuildProblematicTargets(sb, status.TargetResults);
            }

            var output = sb.ToString();
            Console.Write(output);

            // Count how many physical terminal rows that output occupied,
            // so the next cycle knows how far to move the cursor back up.
            _previousPhysicalRows = CountPhysicalRows(output, GetTerminalWidth());
        }
    }

    /// <summary>
    /// Builds the main one-line status summary into the StringBuilder.
    /// </summary>
    private void BuildStatusLine(StringBuilder sb, NetworkStatus status)
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

        sb.Append($"{color}{Bold}{symbol} {status.Health,-10}{Reset} ");
        sb.Append($"{Cyan}Router:{Reset} ");

        if (status.RouterResult?.Success == true)
        {
            var c = GetLatencyColor(status.RouterResult.RoundtripTimeMs);
            sb.Append($"{c}{status.RouterResult.RoundtripTimeMs,4}ms{Reset} ");
        }
        else
        {
            sb.Append($"{Red}FAIL{Reset}   ");
        }

        sb.Append($"{Cyan}Internet:{Reset} ");

        if (status.InternetResult?.Success == true)
        {
            var c = GetLatencyColor(status.InternetResult.RoundtripTimeMs);
            sb.Append($"{c}{status.InternetResult.RoundtripTimeMs,4}ms{Reset} ");
        }
        else
        {
            sb.Append($"{Red}FAIL{Reset}   ");
        }

        // Show custom target summary count
        if (status.TargetResults is { Count: > 0 })
        {
            var customResults = status.TargetResults
                .Where(r => r.Target.Category == TargetCategory.Custom)
                .ToList();

            if (customResults.Count > 0)
            {
                var ok = customResults.Count(r => r.PingResult?.Success == true);
                var total = customResults.Count;
                var c = ok == total ? Green : ok > 0 ? Yellow : Red;
                sb.Append($"{Cyan}Targets:{Reset} {c}{ok}/{total}{Reset} ");
            }
        }

        sb.Append($"{Magenta}[{status.Timestamp:HH:mm:ss}]{Reset}");
    }

    /// <summary>
    /// Appends details for targets that need attention below the status line.
    /// Only called when QuietConsole is enabled.
    /// </summary>
    private void BuildProblematicTargets(
        StringBuilder sb,
        IReadOnlyList<TargetCheckResult> targetResults)
    {
        var problematic = targetResults
            .Where(IsProblematic)
            .ToList();

        if (problematic.Count == 0)
        {
            return;
        }

        sb.Append('\n');
        sb.Append($"  {Yellow}{Bold}⚠ {problematic.Count} target(s) need attention:{Reset}");

        foreach (var result in problematic)
        {
            sb.Append('\n');

            var name = result.Target.Name;

            if (result.PingResult?.Success != true)
            {
                var error = result.PingResult?.ErrorMessage ?? "No response";
                sb.Append($"    {Red}✗ {name,-28}{Reset} {Dim}FAIL: {error}{Reset}");
            }
            else
            {
                var latency = result.PingResult.RoundtripTimeMs ?? 0;
                var loss = result.PacketLossPercent;
                var parts = new List<string>();

                if (latency > _options.GoodLatencyMs)
                {
                    parts.Add($"latency {latency}ms");
                }

                if (loss >= _options.DegradedPacketLossPercent)
                {
                    parts.Add($"loss {loss:F0}%");
                }

                var detail = parts.Count > 0 ? string.Join(", ", parts) : "degraded";
                var targetColor = latency > _options.GoodLatencyMs ? Red : Yellow;
                sb.Append($"    {targetColor}▲ {name,-28}{Reset} {Dim}{detail}{Reset}");
            }

            if (result.DnsResult is { Success: false })
            {
                sb.Append($" {Red}[DNS FAIL]{Reset}");
            }
        }
    }

    /// <summary>
    /// Determines whether a target check result indicates a problem
    /// that warrants console display in quiet mode.
    /// </summary>
    private bool IsProblematic(TargetCheckResult result)
    {
        // Ping failed entirely
        if (result.PingResult?.Success != true)
        {
            return true;
        }

        // Latency exceeds the "good" threshold
        if (result.PingResult.RoundtripTimeMs > _options.GoodLatencyMs)
        {
            return true;
        }

        // Packet loss exceeds the degraded threshold
        if (result.PacketLossPercent >= _options.DegradedPacketLossPercent)
        {
            return true;
        }

        // DNS resolution failed for a hostname target
        if (result.DnsResult is { Success: false })
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
    /// Counts the number of physical terminal rows a string occupies,
    /// accounting for embedded newlines and line wrapping at the terminal width.
    /// ANSI escape sequences are stripped before measuring visible length.
    /// </summary>
    /// <param name="text">The output string (may contain ANSI codes and newlines).</param>
    /// <param name="terminalWidth">Current terminal width in columns.</param>
    /// <returns>Total physical rows the te
    /// 