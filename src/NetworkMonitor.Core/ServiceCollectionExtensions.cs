using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NetworkMonitor.Core.Configuration;
using NetworkMonitor.Core.Display;
using NetworkMonitor.Core.Monitoring;
using NetworkMonitor.Core.Storage;
using NetworkMonitor.Core.Sync;
using NetworkMonitor.Core.Sync.Databricks;
using NetworkMonitor.Core.Sync.Turso;
using NetworkMonitor.Core.Telemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

namespace NetworkMonitor.Core;

public static class ServiceCollectionExtensions
{
    public static string Version { get; } =
        typeof(ServiceCollectionExtensions).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    extension(IServiceCollection services)
    {
        public IServiceCollection AddNetworkMonitor(IConfiguration configuration)
        {
            services.Configure<MonitorOptions>(configuration.GetSection(MonitorOptions.SectionName));
            services.Configure<StorageOptions>(configuration.GetSection(StorageOptions.SectionName));
            services.Configure<RemoteSyncOptions>(configuration.GetSection(RemoteSyncOptions.SectionName));
            services.Configure<DatabricksSyncOptions>(configuration.GetSection(DatabricksSyncOptions.SectionName));

            services.TryAddSingleton(TimeProvider.System);
            services.TryAddSingleton<LiveConsole>();
            services.AddMetrics();
            services.AddSingleton<NetworkMetrics>();

            services.AddSingleton<IPingService, IcmpPingService>();
            services.AddSingleton<IDnsResolver, SystemDnsResolver>();
            services.AddSingleton<IGatewayDetector, GatewayDetector>();
            services.AddSingleton<ITargetProber, TargetProber>();
            services.AddSingleton<INetworkConfigurationService, NetworkConfigurationService>();
            services.AddSingleton<INetworkMonitor, NetworkMonitorService>();
            services.AddSingleton<IStatusDisplay, ConsoleStatusDisplay>();

            services.AddSingleton<SqliteStore>();
            services.AddSingleton<IStatusStore>(sp => sp.GetRequiredService<SqliteStore>());
            services.AddSingleton<IRollupStore>(sp => sp.GetRequiredService<SqliteStore>());

            services.AddSingleton<IHranaClient, HranaClient>();
            services.AddSingleton<IDatabricksClient, DatabricksClient>();
            services.AddSingleton<IRollupSink, TursoRollupSink>();
            services.AddSingleton<IRollupSink, DatabricksRollupSink>();

            services.AddHostedService<MonitorBackgroundService>();
            services.AddHostedService<RollupSyncService>();
            return services;
        }

        public IServiceCollection AddNetworkMonitorTelemetry(FileExporterOptions fileOptions)
        {
            ArgumentNullException.ThrowIfNull(fileOptions);
            services.AddOpenTelemetry()
                .ConfigureResource(resource => resource.AddService(serviceName: "NetworkMonitor", serviceVersion: ServiceCollectionExtensions.Version))
                .WithMetrics(metrics => metrics
                    .AddMeter(NetworkMetrics.MeterName)
                    .AddRuntimeInstrumentation()
                    .AddReader(new PeriodicExportingMetricReader(
                        new FileMetricExporter(fileOptions, TimeProvider.System),
                        exportIntervalMilliseconds: 10_000)));
            return services;
        }
    }
}
