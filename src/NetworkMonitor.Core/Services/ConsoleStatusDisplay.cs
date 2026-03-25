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
public sealed class ConsoleStatusDisplay : IStatusDisplay
{
    private readonly Lock _lock = new();
    private readonly MonitorOptions _options;
    private int _previousExtraLines;

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
            // Clear any previously written extra lines before rewriting
            ClearExtraLines();

            var (color, symbol) = status.Health switch
            {
                NetworkHealth.Excellent => (Green, "●"),
                NetworkHealth.Good => (Green, "○"),
                NetworkHealth.Degraded => (Yellow, "◐"),
                NetworkHealth.Poor => (Red, "◑"),
                NetworkHealth.Offline => (Red, "○"),
                _ => (Reset, "?")
            };

            Console.Write($"\r{color}{Bold}{symbol} {status.Health,-10}{Reset} ");
            Console.Write($"{Cyan}Router:{Reset} ");

            if (status.RouterResult?.Success == true)
            {
                var routerColor = GetLatencyColor(status.RouterResult.RoundtripTimeMs);
                Console.Write($"{routerColor}{status.RouterResult.RoundtripTimeMs,4}ms{Reset} ");
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

            Console.Write($"{Magenta}[{status.Timestamp:HH:mm:ss}]{Reset}");

            // Pad to clear any previous longer text on this line
            Console.Write("          ");

            // In quiet mode, show only problematic targets below the main line.
            // In verbose mode (!QuietConsole), the summary count above is sufficient.
            if (_options.QuietConsole && status.TargetResults is { Count: > 0 })
            {
                WriteProblematicTargets(status.TargetResults);
            }
        }
    }

    /// <summary>
    /// Writes details for targets that need attention below the main status line.
    /// Only called when QuietConsole is enabled.
    /// </summary>
    private void WriteProblematicTargets(IReadOnlyList<TargetCheckResult> targetResults)
    {
        var problematic = targetResults
            .Where(IsProblematic)
            .ToList();

        _previousExtraLines = 0;

        if (problematic.Count == 0)
        {
            return;
        }

        Console.WriteLine();
        _previousExtraLines++;

        Console.Write($"  {Yellow}{Bold}⚠ {problematic.Count} target(s) need attention:{Reset}");
        _previousExtraLines++;

        foreach (var result in problematic)
        {
            Console.WriteLine();
            _previousExtraLines++;

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
    /// Moves the cursor up and clears any extra lines written
    /// by the previous update cycle (problematic target details).
    /// </summary>
    private void ClearExtraLines()
    {
        if (_previousExtraLines <= 0)
        {
            return;
        }

        for (var i = 0; i < _previousExtraLines; i++)
        {
            // Move cursor up one line and clear it
            Console.Write("\x1b[1A\x1b[2K");
        }

        // Return to the start of the main status line
        Console.Write("\r");
        _previousExtraLines = 0;
    }

    /// <inheritdoc />
    public void Clear()
    {
        lock (_lock)
        {
            ClearExtraLines();
            Console.Write("\r" + new string(' ', Console.WindowWidth - 1) + "\r");
        }
    }
}
