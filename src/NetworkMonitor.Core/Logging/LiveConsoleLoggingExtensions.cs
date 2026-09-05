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
