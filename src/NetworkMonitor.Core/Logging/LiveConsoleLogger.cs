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
