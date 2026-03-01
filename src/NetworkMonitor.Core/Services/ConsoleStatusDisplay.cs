using NetworkMonitor.Core.Models;

namespace NetworkMonitor.Core.Services;

/// <summary>
/// Console-based status display with ANSI colors.
/// Provides "at a glance" network status visualization.
/// Shows extended info for custom targets and packet loss.
/// </summary>
public sealed class ConsoleStatusDisplay : IStatusDisplay
{
    private readonly Lock _lock = new();

    // ANSI color codes
    private const string Reset = "\x1b[0m";
    private const string Bold = "\x1b[1m";
    private const string Green = "\x1b[32m";
    private const string Yellow = "\x1b[33m";
    private const string Red = "\x1b[31m";
    private const string Cyan = "\x1b[36m";
    private const string Magenta = "\x1b[35m";

    /// <inheritdoc />
    public void UpdateStatus(NetworkStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);

        lock (_lock)
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

            Console.Write($"\r{color}{Bold}{symbol} {status.Health,-10}{Reset} ");
            Console.Write($"{Cyan}Router:{Reset} ");

            if (status.RouterResult?.Success == true)
            {
                Console.Write($"{Green}{status.RouterResult.RoundtripTimeMs,4}ms{Reset} ");
            }
            else
            {
                Console.Write($"{Red}FAIL{Reset}   ");
            }

            Console.Write($"{Cyan}Internet:{Reset} ");

            if (status.InternetResult?.Success == true)
            {
                Console.Write($"{Green}{status.InternetResult.RoundtripTimeMs,4}ms{Reset} ");
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
                    Console.Write($"{Cyan}Custom:{Reset} {customColor}{ok}/{total}{Reset} ");
                }
            }

            Console.Write($"{Magenta}[{status.Timestamp:HH:mm:ss}]{Reset}");

            // Pad to clear any previous longer text
            Console.Write("          ");
        }
    }

    /// <inheritdoc />
    public void Clear()
    {
        lock (_lock)
        {
            Console.Write("\r" + new string(' ', Console.WindowWidth - 1) + "\r");
        }
    }
}
