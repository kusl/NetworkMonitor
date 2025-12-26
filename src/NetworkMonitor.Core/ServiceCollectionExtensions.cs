using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NetworkMonitor.Core.Exporters;
using NetworkMonitor.Core.Models;
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
        
        // Register services
        services.AddSingleton<IPingService, PingService>();
        services.AddSingleton<INetworkMonitorService, NetworkMonitorService>();
        services.AddSingleton<IStatusDisplay, ConsoleStatusDisplay>();
        services.AddSingleton<IStorageService, SqliteStorageService>();
        
        // Register background service
        services.AddHostedService<MonitorBackgroundService>();
        
        return services;
    }
    
    /// <summary>
    /// Adds OpenTelemetry metrics with file and console export.
    /// </summary>
    public static IServiceCollection AddNetworkMonitorTelemetry(
        this IServiceCollection services,
        FileExporterOptions? fileOptions = null)
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
                    .AddConsoleExporter()
                    .AddFileExporter(fileOptions);
            });
        
        return services;
    }
}
