using Microsoft.Extensions.Options;
using NetworkMonitor.Core.Models;

namespace NetworkMonitor.Core.Services;

/// <summary>
/// Console-based status display with ANSI colors.
/// Provides "at a glance" network status visualization.
///
/// TWO DISPLAY MODES:
///
///   Quiet mode (QuietConsole = true, the default):
///
///     Healthy cycle (no problematic targets):
///       The status line is overwritten in place so the console stays clean.
///       Uses ANSI save/restore cursor to avoid stale text.
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
///     overwritten — the user explicitly asked to see everything, so a full
///     history is kept. Disabled/unmeasured targets render as "--".
///
/// In quiet mode the terminal looks like:
///   ● Excellent  Router: 1ms Internet: 16ms Targets: 48/48 [08:40:00]   ← overwrites itself
///   ● Excellent  Router: 1ms Internet: 16ms Targets: 48/48 [08:40:05]   ← same line
///   ... wifi goes down ...
///   ○ Offline    Router: FAIL  Internet: FAIL  Targets: 0/48 [08:41:00]
///     ⚠ 50 target(s) need attention:
///       ✗ Router     FAIL: TimedOut
///       ...
///   ● Excellent  Router: 1ms Internet: 16ms Targets: 48/48 [08:42:00]   ← overwrites itself again
///
/// All timestamps are rendered in local time for readability, even though the
/// underlying data is stored in UTC.
/// </summary>
public sealed class ConsoleStatusDisplay : IStatusDisplay
{
    private readonly Lock _lock = new();
    private readonly MonitorOptions _options;
    private bool _cursorSaved;

    // ANSI escape sequences
    private const string Reset = "\x1b[0m";
    private const string Bold = "\x1b[1m";
    private const string Dim = "\x1b[2m";
    private const string Green = "\x1b[32m";
    private const string Yellow = "\x1b[33m";
    private const string Red = "\x1b[31m";
    private const string Cyan = "\x1b[36m";
    private const string Magenta = "\x1b[35m";

    // Cursor control (ECMA-48 / ANSI X3.64)
    private const string SaveCursor = "\x1b[s";
    private const string RestoreCursor = "\x1b[u";
    private const string EraseToEndOfScreen = "\x1b[J";

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
    /// No in-place overwrite.
    /// </summary>
    private void WriteVerbose(NetworkStatus status)
    {
        // If we previously left a saved cursor from quiet mode, drop it so we
        // don't clobber earlier verbose output.
        if (_cursorSaved)
        {
            Console.Write(RestoreCursor);
            Console.Write(EraseToEndOfScreen);
            _cursorSaved = false;
        }

        WriteStatusLine(status);

        if (status.TargetResults is { Count: > 0 })
        {
            WriteAllTargets(status.TargetResults);
        }

        // Blank line so consecutive cycles are visually separated.
        Console.WriteLine();
        Console.WriteLine();
    }

    /// <summary>
    /// Quiet mode: overwrite the status line while healthy, but print and
    /// preserve details whenever a target needs attention.
    /// </summary>
    private void WriteQuiet(NetworkStatus status)
    {
        // If we saved a cursor from a previous clean cycle, jump back and erase
        // so we overwrite the old healthy status in place.
        if (_cursorSaved)
        {
            Console.Write(RestoreCursor);
            Console.Write(EraseToEndOfScreen);
        }

        var problematic = status.TargetResults is { Count: > 0 }
            ? status.TargetResults.Where(IsProblematic).ToList()
            : [];

        bool hasProblems = problematic.Count > 0;

        if (hasProblems)
        {
            // Don't save cursor — we want this output preserved permanently.
            // The next cycle will start on a fresh line below.
            _cursorSaved = false;

            WriteStatusLine(status);
            WriteProblematicTargets(problematic);

            // End with a newline so the next cycle starts cleanly below.
            Console.WriteLine();
        }
        else
        {
            // Save cursor position so the next cycle can overwrite this line.
            Console.Write(SaveCursor);
            _cursorSaved = true;

            WriteStatusLine(status);
        }
    }

    /// <summary>
    /// Writes the main one-line status summary.
    /// </summary>
    private void WriteStatusLine(NetworkStatus status)
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

        Console.Write($"{color}{Bold}{symbol} {status.Health,-10}{Reset} ");
        Console.Write($"{Cyan}Router:{Reset} ");

        if (status.RouterResult?.Success == true)
        {
            var routerColor = GetLatencyColor(status.RouterResult.RoundtripTimeMs);
            Console.Write($"{routerColor}{status.RouterResult.RoundtripTimeMs,4}ms{Reset} ");
        }
        else if (status.RouterResult is null)
        {
            Console.Write($"{Dim}  --  {Reset}");
        }
        else
        {
            Console.Write($"{Red}FAIL{Reset}   ");
        }

        Console.Write($"{Cyan}Internet:{Reset} ");

        if (status.InternetResult?.Success == true)
        {
            var internetColor = GetLatencyColor(status.InternetResult.RoundtripTimeMs);
            Console.Write($"{internetColor}{status.InternetResult.RoundtripTimeMs,4}ms{Reset} ");
        }
        else if (status.InternetResult is null)
        {
            Console.Write($"{Dim}  --  {Reset}");
        }
        else
        {
            Console.Write($"{Red}FAIL{Reset}   ");
        }

        // Show custom target summary if any
        if (status.TargetResults is { Count: > 0 })
        {
            var customResults = status.TargetResults
                .Where(r => r.Target.Category == TargetCategory.Custom)
                .ToList();

            if (customResults.Count > 0)
            {
                var ok = customResults.Count(r => r.PingResult?.Success == true);
                var total = customResults.Count;
                var customColor = ok == total ? Green : ok > 0 ? Yellow : Red;
                Console.Write($"{Cyan}Targets:{Reset} {customColor}{ok}/{total}{Reset} ");
            }
        }

        // Timestamps are stored in UTC; show them in local time for humans.
        Console.Write($"{Magenta}[{status.Timestamp.ToLocalTime():HH:mm:ss}]{Reset}");
    }

    /// <summary>
    /// Writes one line per target (verbose mode). Every target is shown,
    /// including healthy ones and any that were not measured.
    /// </summary>
    private void WriteAllTargets(IReadOnlyList<TargetCheckResult> results)
    {
        foreach (var result in results)
        {
            Console.WriteLine();
            WriteTargetLine(result);
        }
    }

    private void WriteTargetLine(TargetCheckResult result)
    {
        Console.Write("    ");
        WriteResultCell(result);
        Console.Write($" {result.Target.Name}");

        if (result.PacketLossPercent > 0)
        {
            var lossColor = result.PacketLossPercent >= _options.DegradedPacketLossPercent ? Yellow : Dim;
            Console.Write($" {lossColor}loss {result.PacketLossPercent:F0}%{Reset}");
        }

        if (result.DnsResult is { Success: false })
        {
            Console.Write($" {Red}[DNS FAIL]{Reset}");
        }
        else if (result.DnsResult is { Success: true } dns)
        {
            Console.Write($" {Dim}dns {dns.ResolutionTimeMs}ms{Reset}");
        }
    }

    /// <summary>
    /// Writes the fixed-width result cell for a target:
    /// latency when it succeeded, FAIL when it failed, or "--" when there was
    /// nothing to measure.
    /// </summary>
    private void WriteResultCell(TargetCheckResult result)
    {
        var ping = result.PingResult;

        if (ping is null)
        {
            Console.Write($"{Dim}  --  {Reset}");
            return;
        }

        if (ping.Success)
        {
            var color = GetLatencyColor(ping.RoundtripTimeMs);
            Console.Write($"{color}{ping.RoundtripTimeMs,4}ms{Reset}");
        }
        else
        {
            Console.Write($"{Red} FAIL {Reset}");
        }
    }

    /// <summary>
    /// Writes details for targets that need attention below the main status line.
    /// </summary>
    private void WriteProblematicTargets(List<TargetCheckResult> problematic)
    {
        Console.WriteLine();
        Console.Write($"  {Yellow}{Bold}⚠ {problematic.Count} target(s) need attention:{Reset}");

        foreach (var result in problematic)
        {
            Console.WriteLine();

            var name = result.Target.Name;

            if (result.PingResult?.Success != true)
            {
                var error = result.PingResult?.ErrorMessage ?? "No response";
                Console.Write($"    {Red}✗ {name,-28}{Reset} {Dim}FAIL: {error}{Reset}");
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
                Console.Write($"    {targetColor}▲ {name,-28}{Reset} {Dim}{detail}{Reset}");
            }

            if (result.DnsResult is { Success: false })
            {
                Console.Write($" {Red}[DNS FAIL]{Reset}");
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

    /// <inheritdoc />
    public void Clear()
    {
        lock (_lock)
        {
            if (_cursorSaved)
            {
                Console.Write(RestoreCursor);
                Console.Write(EraseToEndOfScreen);
                _cursorSaved = false;
            }
        }
    }
}
