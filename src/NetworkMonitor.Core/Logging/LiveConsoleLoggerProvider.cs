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
