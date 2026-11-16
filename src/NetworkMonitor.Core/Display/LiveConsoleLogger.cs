using System.Globalization;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace NetworkMonitor.Core.Display;

public sealed class LiveConsoleLogger(string category, LiveConsole console) : ILogger
{
    private const string Reset = "\x1b[0m";

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        if (!IsEnabled(logLevel))
        {
            return;
        }

        var message = formatter(state, exception);
        if (string.IsNullOrEmpty(message) && exception is null)
        {
            return;
        }

        var (label, color) = logLevel switch
        {
            LogLevel.Trace => ("trce", "\x1b[37m"),
            LogLevel.Debug => ("dbug", "\x1b[37m"),
            LogLevel.Warning => ("warn", "\x1b[33m"),
            LogLevel.Error => ("fail", "\x1b[31m"),
            LogLevel.Critical => ("crit", "\x1b[31m"),
            _ => ("info", "\x1b[32m"),
        };

        var sb = new StringBuilder();
        if (console.AnsiEnabled)
        {
            sb.Append(color).Append(label).Append(Reset);
        }
        else
        {
            sb.Append(label);
        }

        sb.Append(": ").Append(category).Append('[').Append(eventId.Id.ToString(CultureInfo.InvariantCulture)).Append(']');
        AppendIndented(sb, message);
        if (exception is not null)
        {
            AppendIndented(sb, exception.ToString());
        }

        console.WriteBlock(sb.ToString());
    }

    private static void AppendIndented(StringBuilder sb, string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        foreach (var line in text.Split('\n'))
        {
            sb.Append("\n      ").Append(line.TrimEnd('\r'));
        }
    }
}

[ProviderAlias("LiveConsole")]
public sealed class LiveConsoleLoggerProvider(LiveConsole console) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new LiveConsoleLogger(categoryName, console);

    public void Dispose()
    {
    }
}

public static class LiveConsoleLoggingExtensions
{
    extension(ILoggingBuilder builder)
    {
        public ILoggingBuilder AddLiveConsole()
        {
            builder.Services.TryAddSingleton<LiveConsole>();
            builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<ILoggerProvider, LiveConsoleLoggerProvider>());
            return builder;
        }
    }
}
