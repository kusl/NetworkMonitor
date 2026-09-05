#!/usr/bin/env bash
#
# apply-console-fix.sh
#
# Fixes the console-output interleaving between the live status line and the
# logger, and makes redirected output (piping to a file) clean. Idempotent:
# it writes complete files, then builds and tests. Run from the REPO ROOT.
#
set -euo pipefail

if [[ ! -f "src/NetworkMonitor.slnx" ]]; then
  echo "ERROR: run this from the repository root (src/NetworkMonitor.slnx not found)." >&2
  exit 1
fi

write_file() {
  mkdir -p "$(dirname "$1")"
  cat > "$1"
  echo "  wrote $1"
}

echo "==> Writing files"

write_file "src/NetworkMonitor.Core/Services/LiveConsole.cs" <<'___NM_EOF___'
namespace NetworkMonitor.Core.Services;

/// <summary>
/// The single, synchronized owner of every write to standard output.
///
/// WHY THIS EXISTS:
///
/// The status display parks a "live" status line on screen WITHOUT a trailing
/// newline so the next healthy cycle can overwrite it in place. Separately, the
/// logging pipeline writes log records. If those two writers touch the console
/// independently there is no ordering guarantee, and the default console logger
/// writes from its own background thread. The result is mangled output such as:
///
///     ● Excellent  Router: 2ms Internet: 18ms [20:09:09]info: NetworkMonitor…
///     ● Excellent  Router: 2ms Internet: info: NetworkMonitor…   ← cut mid-line
///
/// Routing BOTH the status display and the logger through this one object fixes
/// that at the source: a single lock serializes every write, each status line
/// is emitted as one atomic string, and whenever a log record (or any permanent
/// block) is about to print, the parked status line is erased first so the log
/// always lands on its own fresh line. The status line is then redrawn by the
/// next cycle. Every timestamped line ends up on its own line.
///
/// REDIRECTED OUTPUT:
///
/// When standard output is not a terminal (piped to a file, captured by CI, …)
/// cursor movement and colors are meaningless and would just be garbage in the
/// file. In that case ANSI is disabled: transient lines become ordinary
/// newline-terminated lines, so a captured log stays clean and greppable.
/// </summary>
public sealed class LiveConsole
{
    // Cursor control (ECMA-48 / ANSI X3.64). Only emitted for real terminals.
    private const string SaveCursor = "\x1b[s";
    private const string RestoreCursor = "\x1b[u";
    private const string EraseToEndOfScreen = "\x1b[J";

    private readonly Lock _gate = new();
    private readonly TextWriter _out;
    private readonly bool _ansiEnabled;

    // True while a transient status line is parked on screen without a trailing
    // newline (ANSI mode only). Guarded by _gate.
    private bool _transientParked;

    /// <summary>
    /// Production constructor: writes to <see cref="Console.Out"/> and enables
    /// ANSI cursor control and colors only when stdout is an interactive
    /// terminal (never when redirected to a file or pipe).
    /// </summary>
    public LiveConsole()
        : this(Console.Out, !Console.IsOutputRedirected)
    {
    }

    /// <summary>
    /// Test/advanced constructor allowing an explicit writer and ANSI mode.
    /// </summary>
    public LiveConsole(TextWriter output, bool ansiEnabled)
    {
        ArgumentNullException.ThrowIfNull(output);
        _out = output;
        _ansiEnabled = ansiEnabled;
    }

    /// <summary>
    /// Whether ANSI cursor control and colors should be emitted. False when
    /// output is redirected; consumers use this to strip color codes.
    /// </summary>
    public bool AnsiEnabled => _ansiEnabled;

    /// <summary>
    /// Writes a status line intended to be overwritten by the next cycle.
    /// In a terminal the cursor is saved so the line can be erased later; when
    /// redirected the line is simply terminated with a newline.
    /// </summary>
    public void WriteTransientLine(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        lock (_gate)
        {
            ClearParkedLine();

            if (_ansiEnabled)
            {
                _out.Write(SaveCursor);
                _out.Write(text);
                _out.Flush();
                _transientParked = true;
            }
            else
            {
                _out.Write(text);
                _out.Write('\n');
                _out.Flush();
            }
        }
    }

    /// <summary>
    /// Writes a block of permanent, scrolling output (problem details, verbose
    /// per-target lines, or a log record). Any parked transient line is erased
    /// first so this block starts on its own line, and a trailing newline is
    /// guaranteed so the following output also starts cleanly.
    /// </summary>
    public void WriteBlock(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        lock (_gate)
        {
            ClearParkedLine();
            _out.Write(text);

            if (!EndsWithNewline(text))
            {
                _out.Write('\n');
            }

            _out.Flush();
        }
    }

    /// <summary>
    /// Erases any parked transient line and clears the parked state. Used on
    /// shutdown so the final messages are not overwritten.
    /// </summary>
    public void Reset()
    {
        lock (_gate)
        {
            ClearParkedLine();
        }
    }

    private void ClearParkedLine()
    {
        if (_transientParked)
        {
            _out.Write(RestoreCursor);
            _out.Write(EraseToEndOfScreen);
            _transientParked = false;
        }
    }

    private static bool EndsWithNewline(string text) =>
        text.Length > 0 && text[^1] == '\n';
}
___NM_EOF___

write_file "src/NetworkMonitor.Core/Logging/LiveConsoleLogger.cs" <<'___NM_EOF___'
using System.Text;
using Microsoft.Extensions.Logging;
using NetworkMonitor.Core.Services;

namespace NetworkMonitor.Core.Logging;

/// <summary>
/// An <see cref="ILogger"/> that renders each record as a single string and
/// writes it through <see cref="LiveConsole"/>, so log output is serialized
/// with the status display and never interleaves mid-line.
///
/// The format mirrors the default console formatter for familiarity:
///
///     info: Category.Name[0]
///           The log message
///
/// Colors are applied only when the sink is an interactive terminal
/// (<see cref="LiveConsole.AnsiEnabled"/> is true).
/// </summary>
public sealed class LiveConsoleLogger : ILogger
{
    private const string Reset = "\x1b[0m";

    private readonly string _category;
    private readonly LiveConsole _console;

    public LiveConsoleLogger(string category, LiveConsole console)
    {
        ArgumentNullException.ThrowIfNull(console);
        _category = category ?? string.Empty;
        _console = console;
    }

    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    /// <inheritdoc />
    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        ArgumentNullException.ThrowIfNull(formatter);

        var message = formatter(state, exception);
        if (string.IsNullOrEmpty(message) && exception is null)
        {
            return;
        }

        var ansi = _console.AnsiEnabled;
        var (label, color) = Describe(logLevel);

        var header = ansi
            ? $"{color}{label}{Reset}: {_category}[{eventId.Id}]"
            : $"{label}: {_category}[{eventId.Id}]";

        var sb = new StringBuilder(header, header.Length + 64);

        if (!string.IsNullOrEmpty(message))
        {
            AppendIndented(sb, message);
        }

        if (exception is not null)
        {
            AppendIndented(sb, exception.ToString());
        }

        _console.WriteBlock(sb.ToString());
    }

    /// <summary>
    /// Appends each line of <paramref name="text"/> on its own line, indented by
    /// six spaces to match the default console formatter's continuation indent.
    /// </summary>
    private static void AppendIndented(StringBuilder sb, string text)
    {
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.EndsWith('\r') ? rawLine[..^1] : rawLine;
            sb.Append('\n').Append("      ").Append(line);
        }
    }

    private static (string Label, string Color) Describe(LogLevel level) => level switch
    {
        LogLevel.Trace => ("trce", "\x1b[37m"),
        LogLevel.Debug => ("dbug", "\x1b[37m"),
        LogLevel.Information => ("info", "\x1b[32m"),
        LogLevel.Warning => ("warn", "\x1b[33m"),
        LogLevel.Error => ("fail", "\x1b[31m"),
        LogLevel.Critical => ("crit", "\x1b[31m"),
        _ => ("info", "\x1b[32m")
    };
}
___NM_EOF___

write_file "src/NetworkMonitor.Core/Logging/LiveConsoleLoggerProvider.cs" <<'___NM_EOF___'
using Microsoft.Extensions.Logging;
using NetworkMonitor.Core.Services;

namespace NetworkMonitor.Core.Logging;

/// <summary>
/// Logger provider that funnels every log record through the shared
/// <see cref="LiveConsole"/> so logging cannot corrupt the live status line.
///
/// The provider alias "LiveConsole" allows provider-specific log-level rules in
/// configuration, e.g. "Logging": { "LiveConsole": { "LogLevel": { … } } }.
/// </summary>
[ProviderAlias("LiveConsole")]
public sealed class LiveConsoleLoggerProvider : ILoggerProvider
{
    private readonly LiveConsole _console;

    public LiveConsoleLoggerProvider(LiveConsole console)
    {
        ArgumentNullException.ThrowIfNull(console);
        _console = console;
    }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) =>
        new LiveConsoleLogger(categoryName, _console);

    /// <inheritdoc />
    public void Dispose()
    {
        // The LiveConsole is a shared singleton owned by the DI container.
        // This provider owns no disposable state of its own.
    }
}
___NM_EOF___

write_file "src/NetworkMonitor.Core/Logging/LiveConsoleLoggingExtensions.cs" <<'___NM_EOF___'
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using NetworkMonitor.Core.Services;

namespace NetworkMonitor.Core.Logging;

/// <summary>
/// Registration helpers for the <see cref="LiveConsole"/>-backed logger.
/// </summary>
public static class LiveConsoleLoggingExtensions
{
    /// <summary>
    /// Adds the LiveConsole logger provider and ensures a single shared
    /// <see cref="LiveConsole"/> is registered. Call this after
    /// <c>ClearProviders()</c> so it is the only stdout logger, guaranteeing
    /// log records are serialized with the status display.
    /// </summary>
    public static ILoggingBuilder AddLiveConsole(this ILoggingBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Shared with ConsoleStatusDisplay so both write through the same lock.
        builder.Services.TryAddSingleton<LiveConsole>();
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<ILoggerProvider, LiveConsoleLoggerProvider>());

        return builder;
    }
}
___NM_EOF___

write_file "src/NetworkMonitor.Core/Services/ConsoleStatusDisplay.cs" <<'___NM_EOF___'
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
          .Append($"{status.Health,-10}").Append(Ansi(Reset)).Append(' ');

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
                  .Append(Ansi(customColor)).Append($"{ok}/{total}").Append(Ansi(Reset)).Append(' ');
            }
        }

        // Timestamps are stored in UTC; show them in local time for humans.
        sb.Append(Ansi(Magenta)).Append($"[{status.Timestamp.ToLocalTime():HH:mm:ss}]").Append(Ansi(Reset));

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
            sb.Append(Ansi(color)).Append($"{result.RoundtripTimeMs,4}ms").Append(Ansi(Reset)).Append(' ');
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
            sb.Append(' ').Append(Ansi(lossColor)).Append($"loss {result.PacketLossPercent:F0}%").Append(Ansi(Reset));
        }

        if (result.DnsResult is { Success: false })
        {
            sb.Append(' ').Append(Ansi(Red)).Append("[DNS FAIL]").Append(Ansi(Reset));
        }
        else if (result.DnsResult is { Success: true } dns)
        {
            sb.Append(' ').Append(Ansi(Dim)).Append($"dns {dns.ResolutionTimeMs}ms").Append(Ansi(Reset));
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
            sb.Append(Ansi(color)).Append($"{ping.RoundtripTimeMs,4}ms").Append(Ansi(Reset));
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
          .Append($"⚠ {problematic.Count} target(s) need attention:").Append(Ansi(Reset));

        foreach (var result in problematic)
        {
            sb.Append('\n');

            var name = result.Target.Name;

            if (result.PingResult?.Success != true)
            {
                var error = result.PingResult?.ErrorMessage ?? "No response";
                sb.Append("    ").Append(Ansi(Red)).Append($"✗ {name,-28}").Append(Ansi(Reset))
                  .Append(' ').Append(Ansi(Dim)).Append($"FAIL: {error}").Append(Ansi(Reset));
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

                sb.Append("    ").Append(Ansi(targetColor)).Append($"▲ {name,-28}").Append(Ansi(Reset))
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
___NM_EOF___

write_file "src/NetworkMonitor.Core/ServiceCollectionExtensions.cs" <<'___NM_EOF___'
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NetworkMonitor.Core.Exporters;
using NetworkMonitor.Core.Models;
using NetworkMonitor.Core.RemoteSync;
using NetworkMonitor.Core.Services;
using NetworkMonitor.Core.Storage;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

namespace NetworkMonitor.Core;

/// <summary>
/// Extension methods for registering Network Monitor services.
/// Encapsulates all the DI wiring in one place.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers all Network Monitor services with the DI container.
    /// </summary>
    public static IServiceCollection AddNetworkMonitor(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Bind options from configuration
        services.Configure<MonitorOptions>(
            configuration.GetSection(MonitorOptions.SectionName));
        services.Configure<StorageOptions>(
            configuration.GetSection(StorageOptions.SectionName));
        services.Configure<RemoteSyncOptions>(
            configuration.GetSection(RemoteSyncOptions.SectionName));

        // Single synchronized owner of stdout, shared by the status display and
        // the LiveConsole logger provider. TryAdd so it stays a singleton even
        // if AddLiveConsole() already registered it during logging setup.
        services.TryAddSingleton<LiveConsole>();

        // Register core services
        services.AddSingleton<IPingService, PingService>();
        services.AddSingleton<IGatewayDetector, GatewayDetector>();
        services.AddSingleton<IInternetTargetProvider, InternetTargetProvider>();
        services.AddSingleton<INetworkConfigurationService, NetworkConfigurationService>();
        services.AddSingleton<IDnsResolverService, DnsResolverService>();
        services.AddSingleton<INetworkMonitorService, NetworkMonitorService>();
        services.AddSingleton<IStatusDisplay, ConsoleStatusDisplay>();
        services.AddSingleton<IStorageService, SqliteStorageService>();

        // Optional remote sync (no-op unless RemoteSync:Url and :AuthToken are set)
        services.AddSingleton<IRemoteDatabaseClient, TursoHranaClient>();

        // Register background services
        services.AddHostedService<MonitorBackgroundService>();
        services.AddHostedService<RemoteSyncService>();

        return services;
    }

    /// <summary>
    /// Adds OpenTelemetry metrics with file export (always) and console export (opt-in).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="fileOptions">File exporter options.</param>
    /// <param name="enableConsoleExporter">
    /// When false (default), OpenTelemetry metrics are only written to files.
    /// When true, metrics are also dumped to the console (noisy with many targets).
    /// This does NOT affect the status display or database - only the raw
    /// OpenTelemetry histogram/counter output on stdout.
    /// </param>
    public static IServiceCollection AddNetworkMonitorTelemetry(
        this IServiceCollection services,
        FileExporterOptions? fileOptions = null,
        bool enableConsoleExporter = false)
    {
        fileOptions ??= FileExporterOptions.Default;

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(
                    serviceName: "NetworkMonitor",
                    serviceVersion: "1.0.0"))
            .WithMetrics(metrics =>
            {
                metrics
                    .AddMeter("NetworkMonitor.Core")
                    .AddRuntimeInstrumentation()
                    .AddFileExporter(fileOptions);

                // Only add the console exporter when explicitly requested.
                // With dozens of targets, the histogram output every 10 seconds
                // drowns out the actual status display.
                if (enableConsoleExporter)
                {
                    metrics.AddConsoleExporter();
                }
            });

        return services;
    }
}
___NM_EOF___

write_file "src/NetworkMonitor.Core/Models/MonitorOptions.cs" <<'___NM_EOF___'
namespace NetworkMonitor.Core.Models;

/// <summary>
/// Configuration options for the network monitor.
/// Bound from appsettings.json or environment variables.
/// </summary>
public sealed class MonitorOptions
{
    /// <summary>
    /// Configuration section name in appsettings.json
    /// </summary>
    public const string SectionName = "NetworkMonitor";

    /// <summary>
    /// Special value indicating auto-detection should be used.
    /// </summary>
    public const string AutoDetect = "auto";

    /// <summary>
    /// Router/gateway IP address to ping for local network health.
    /// </summary>
    /// <remarks>
    /// Set to "auto" (default) to automatically detect the default gateway.
    /// The gateway is advertised by DHCP and can be read from the OS.
    ///
    /// If auto-detection fails, common gateway addresses will be tried:
    /// 192.168.1.1, 192.168.0.1, 10.0.0.1, etc.
    ///
    /// Set to a specific IP address to override auto-detection.
    /// </remarks>
    public string RouterAddress { get; set; } = AutoDetect;

    /// <summary>
    /// Internet target to ping for WAN connectivity.
    /// </summary>
    /// <remarks>
    /// Default: 8.8.8.8 (Google DNS - highly reliable)
    ///
    /// If this target is unreachable, fallback targets will be tried:
    /// 1.1.1.1 (Cloudflare), 9.9.9.9 (Quad9), etc.
    ///
    /// This is useful for networks that block specific DNS providers.
    /// </remarks>
    public string InternetTarget { get; set; } = "8.8.8.8";

    /// <summary>
    /// Timeout for each ping in milliseconds.
    /// Default: 3000ms (3 seconds)
    /// </summary>
    public int TimeoutMs { get; set; } = 3000;

    /// <summary>
    /// Interval between monitoring cycles in milliseconds.
    /// Default: 5000ms (5 seconds)
    /// </summary>
    /// <remarks>
    /// This is the time between the START of one cycle and the start of the
    /// next, not the gap after a cycle finishes. If a cycle takes longer than
    /// this interval (common with a large custom target list), the next cycle
    /// starts immediately and a one-time warning is logged.
    /// </remarks>
    public int IntervalMs { get; set; } = 5000;

    /// <summary>
    /// Number of pings per target per cycle.
    /// Default: 3 (for statistical significance)
    /// </summary>
    /// <remarks>
    /// Keep this at 3 or higher. At 2 pings per cycle a single dropped packet
    /// registers as 50% loss, which massively inflates alert frequency.
    /// </remarks>
    public int PingsPerCycle { get; set; } = 3;

    /// <summary>
    /// Latency threshold (ms) at or below which the internet is considered "excellent".
    /// Default: 20ms
    /// </summary>
    public int ExcellentLatencyMs { get; set; } = 20;

    /// <summary>
    /// Latency threshold (ms) at or below which the internet is considered "good".
    /// Default: 200ms
    /// </summary>
    /// <remarks>
    /// Set to 200ms (not 100ms) so legitimate, geographically distant targets —
    /// overseas DNS resolvers and CDN endpoints — are not repeatedly flagged as
    /// high-latency. The shipped appsettings.json uses this same value; keeping
    /// the code default in sync means a run without that file behaves identically.
    /// </remarks>
    public int GoodLatencyMs { get; set; } = 200;

    /// <summary>
    /// Packet loss percentage above which network is "degraded".
    /// Default: 10%
    /// </summary>
    public int DegradedPacketLossPercent { get; set; } = 10;

    /// <summary>
    /// Whether to use fallback targets if primary fails.
    /// Default: true
    /// </summary>
    public bool EnableFallbackTargets { get; set; } = true;

    /// <summary>
    /// Whether to allow IPv6 addresses when resolving hostnames for pings.
    /// Default: true
    /// </summary>
    /// <remarks>
    /// When resolving a hostname, IPv4 is always preferred for stable,
    /// comparable latency numbers. This flag only decides what happens when a
    /// host resolves to IPv6 ONLY: if true, the IPv6 address is pinged; if
    /// false, the target is reported as failed with a clear message instead of
    /// silently pinging over IPv6. Explicit IPv6 literal targets are always
    /// pinged regardless of this flag.
    /// </remarks>
    public bool EnableIPv6 { get; set; } = true;

    /// <summary>
    /// Whether to perform DNS resolution checks on hostnames.
    /// Default: true
    /// </summary>
    public bool EnableDnsChecks { get; set; } = true;

    /// <summary>
    /// Maximum number of custom targets to check concurrently within a cycle.
    /// Default: 6
    /// </summary>
    /// <remarks>
    /// The router and internet checks always run sequentially and first, so
    /// their latency measurements stay clean. Custom targets - which are mostly
    /// reachability checks - run with this bounded concurrency so a large list
    /// (dozens of hosts) does not push the cycle far past <see cref="IntervalMs"/>.
    ///
    /// Keep this modest on Wi-Fi: too much parallel ICMP causes airtime
    /// contention that inflates the very latencies you are trying to measure.
    /// Values are clamped to at least 1.
    /// </remarks>
    public int MaxConcurrentChecks { get; set; } = 6;

    /// <summary>
    /// When true, console output only shows targets that need attention:
    /// failed pings, latency exceeding GoodLatencyMs, or packet loss
    /// exceeding DegradedPacketLossPercent.
    ///
    /// When false, every target is printed on every cycle.
    ///
    /// All data is still written to the database and telemetry files
    /// regardless of this setting. This only controls what appears
    /// on the console display.
    ///
    /// Default: true (opt everyone in, but user can set to false
    /// to see all targets on every cycle).
    /// </summary>
    /// <remarks>
    /// With dozens of custom targets configured, printing a status line
    /// for every single one every few seconds creates noise that drowns out
    /// the information that actually matters. This flag ensures the console
    /// only surfaces problems that need human attention right now.
    ///
    /// Can also be set via environment variable:
    ///   NetworkMonitor__QuietConsole=true
    /// </remarks>
    public bool QuietConsole { get; set; } = true;

    /// <summary>
    /// Custom targets to monitor (services, private IPs, hostnames).
    /// Each can be individually enabled/disabled at runtime.
    /// </summary>
    public List<CustomTargetConfig> CustomTargets { get; set; } = [];

    /// <summary>
    /// Names of checks to disable at runtime.
    /// Matches against target names (case-insensitive).
    /// Examples: "GoogleDNS", "CloudflareDNS", "Router", "Teams"
    /// </summary>
    public List<string> DisabledChecks { get; set; } = [];

    /// <summary>
    /// Checks if router address should be auto-detected.
    /// </summary>
    public bool IsRouterAutoDetect =>
        string.IsNullOrWhiteSpace(RouterAddress) ||
        RouterAddress.Equals(AutoDetect, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Checks if a named check is disabled.
    /// </summary>
    public bool IsCheckDisabled(string name) =>
        DisabledChecks.Exists(d => d.Equals(name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Configuration for a custom monitoring target.
/// </summary>
public sealed class CustomTargetConfig
{
    /// <summary>
    /// Human-readable name for this target.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Address to monitor. Can be an IP (v4/v6) or hostname.
    /// </summary>
    public string Address { get; set; } = string.Empty;

    /// <summary>
    /// Whether this target is currently enabled.
    /// Can be toggled at runtime.
    /// </summary>
    public bool Enabled { get; set; } = true;
}
___NM_EOF___

write_file "src/NetworkMonitor.Console/Program.cs" <<'___NM_EOF___'
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NetworkMonitor.Core;
using NetworkMonitor.Core.Exporters;
using NetworkMonitor.Core.Logging;
using NetworkMonitor.Core.Models;
using NetworkMonitor.Core.Services;

// =============================================================================
// Network Monitor Console Application
// =============================================================================
// A cross-platform network monitoring tool that provides:
// - At-a-glance network health status (PRIMARY GOAL)
// - Historical trendlines via SQLite storage
// - OpenTelemetry metrics exported to files
//
// Usage:
//   dotnet run                          # Run with defaults
//   dotnet run -- --help                # Show help (future)
//   Ctrl+C                              # Graceful shutdown
//
// Configuration via appsettings.json or environment variables.
//
// Logging levels are controlled entirely by appsettings.json (or env vars).
// By default everything is set to Error so the console stays clean —
// only the status display and problematic targets are shown.
// To see verbose output, change the log levels in appsettings.json:
//   "NetworkMonitor": "Information"  — startup info, status changes
//   "NetworkMonitor": "Debug"        — every ping, DNS lookup, etc.
//
// Whatever the level, log records and the live status line are serialized
// through a single synchronized console sink (LiveConsole), so they never
// interleave: every timestamped line appears on its own line.
// =============================================================================

Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
Console.WriteLine("║           Network Monitor - Cross-Platform Edition           ║");
Console.WriteLine("║                  Press Ctrl+C to stop                        ║");
Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");
Console.WriteLine();

var fileExporterOptions = new FileExporterOptions();
Console.WriteLine($"📁 Telemetry: {fileExporterOptions.Directory}");
Console.WriteLine($"🆔 Run ID: {fileExporterOptions.RunId}");
Console.WriteLine();

var builder = Host.CreateApplicationBuilder(args);

// Replace the default (asynchronous, uncoordinated) console logger with the
// LiveConsole provider. The default logger writes from a background thread with
// no ordering guarantee, which is what mangled the status line. Routing logging
// through LiveConsole makes it share one lock with the status display.
// ClearProviders() removes provider instances only; the config-driven log
// levels in appsettings.json still apply.
builder.Logging.ClearProviders();
builder.Logging.AddLiveConsole();

// Register Network Monitor services
builder.Services.AddNetworkMonitor(builder.Configuration);

// Read QuietConsole to decide whether to enable the OTel console exporter.
// When QuietConsole is true (default), only file export is active —
// no histogram spam on the console. Set QuietConsole=false to get raw
// OpenTelemetry output on stdout alongside the status display.
var quietConsoleValue = builder.Configuration
    .GetSection(MonitorOptions.SectionName)["QuietConsole"];
var quietConsole = !string.Equals(quietConsoleValue, "false", StringComparison.OrdinalIgnoreCase);

builder.Services.AddNetworkMonitorTelemetry(
    fileExporterOptions,
    enableConsoleExporter: !quietConsole);

var host = builder.Build();

// The shared console sink; used so shutdown messages also clear any parked
// status line and land on their own lines.
var live = host.Services.GetRequiredService<LiveConsole>();

// Handle Ctrl+C gracefully
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    live.WriteBlock("\n⏹️  Shutting down...");
};

try
{
    await host.RunAsync().ConfigureAwait(false);
}
catch (OperationCanceledException)
{
    // Normal shutdown
}
finally
{
    live.WriteBlock("👋 Network Monitor stopped. Goodbye!");
}
___NM_EOF___

write_file "src/NetworkMonitor.Tests/Services/LiveConsoleTests.cs" <<'___NM_EOF___'
using NetworkMonitor.Core.Services;
using Xunit;

namespace NetworkMonitor.Tests.Services;

/// <summary>
/// Tests for <see cref="LiveConsole"/>, the single synchronized owner of stdout.
/// These verify the core guarantee: a log record (or any permanent block) never
/// shares a line with the live status line, in both redirected and terminal modes.
/// </summary>
public sealed class LiveConsoleTests
{
    [Fact]
    public void WriteTransientLine_WhenRedirected_TerminatesEachLineWithNewline()
    {
        using var writer = new StringWriter();
        var console = new LiveConsole(writer, ansiEnabled: false);

        console.WriteTransientLine("status one");
        console.WriteTransientLine("status two");

        Assert.Equal("status one\nstatus two\n", writer.ToString());
    }

    [Fact]
    public void WriteBlock_AfterTransient_WhenRedirected_KeepsLinesSeparate()
    {
        using var writer = new StringWriter();
        var console = new LiveConsole(writer, ansiEnabled: false);

        console.WriteTransientLine("● Excellent  Internet: 16ms [20:09:09]");
        console.WriteBlock("info: NetworkMonitor[0]\n      Network status changed");

        var lines = writer.ToString().Split('\n');

        // The status line and the log record must never share a line.
        Assert.Equal("● Excellent  Internet: 16ms [20:09:09]", lines[0]);
        Assert.Equal("info: NetworkMonitor[0]", lines[1]);
        Assert.Equal("      Network status changed", lines[2]);
    }

    [Fact]
    public void WriteBlock_AfterTransient_WhenAnsi_ErasesParkedLineBeforeWriting()
    {
        using var writer = new StringWriter();
        var console = new LiveConsole(writer, ansiEnabled: true);

        console.WriteTransientLine("STATUS");
        console.WriteBlock("LOG");

        // Save cursor + STATUS (parked), then restore + erase-to-end-of-screen
        // before the log, so LOG is not appended to the status line.
        Assert.Equal("\x1b[sSTATUS\x1b[u\x1b[JLOG\n", writer.ToString());
    }

    [Fact]
    public void WriteBlock_AlreadyEndingWithNewline_DoesNotDoubleTerminate()
    {
        using var writer = new StringWriter();
        var console = new LiveConsole(writer, ansiEnabled: false);

        console.WriteBlock("already terminated\n");

        Assert.Equal("already terminated\n", writer.ToString());
    }

    [Fact]
    public void Reset_WhenAnsiParked_RestoresAndErases()
    {
        using var writer = new StringWriter();
        var console = new LiveConsole(writer, ansiEnabled: true);

        console.WriteTransientLine("STATUS");
        console.Reset();

        Assert.Equal("\x1b[sSTATUS\x1b[u\x1b[J", writer.ToString());
    }

    [Fact]
    public void AnsiEnabled_ReflectsConstructorArgument()
    {
        using var w1 = new StringWriter();
        using var w2 = new StringWriter();

        Assert.False(new LiveConsole(w1, ansiEnabled: false).AnsiEnabled);
        Assert.True(new LiveConsole(w2, ansiEnabled: true).AnsiEnabled);
    }
}
___NM_EOF___

echo ""
echo "==> dotnet format (optional, non-fatal)"
dotnet format src/NetworkMonitor.slnx || echo "  (dotnet format skipped or reported changes)"

echo ""
echo "==> dotnet build (Release)"
dotnet build src/NetworkMonitor.slnx -c Release

echo ""
echo "==> dotnet test (Release)"
dotnet test src/NetworkMonitor.slnx -c Release

echo ""
echo "======================================================================"
echo " DONE. Summary:"
echo "  - LiveConsole: single synchronized owner of stdout (new)"
echo "  - LiveConsole logger provider replaces the async console logger (new)"
echo "  - ConsoleStatusDisplay now builds atomic lines via LiveConsole"
echo "  - ANSI/colors auto-disabled when stdout is redirected (clean log files)"
echo "  - GoodLatencyMs default aligned to 200ms (matches appsettings + README)"
echo "  - Added LiveConsoleTests"
echo "======================================================================"
