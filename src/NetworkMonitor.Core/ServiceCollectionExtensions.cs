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
