using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NetworkMonitor.Core;
using NetworkMonitor.Core.Display;
using NetworkMonitor.Core.Storage;
using NetworkMonitor.Core.Telemetry;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

builder.Logging.ClearProviders();
builder.Logging.AddLiveConsole();

var telemetry = new FileExporterOptions();
builder.Services
    .AddNetworkMonitor(builder.Configuration)
    .AddNetworkMonitorTelemetry(telemetry);

var host = builder.Build();
var live = host.Services.GetRequiredService<LiveConsole>();
var database = host.Services.GetRequiredService<SqliteStore>().DatabasePath;

live.WriteBlock($"""
    ╔══════════════════════════════════════════════════════════════╗
    ║           Network Monitor - Cross-Platform Edition           ║
    ║                  Press Ctrl+C to stop                        ║
    ╚══════════════════════════════════════════════════════════════╝
    🏷️  Version:   {ServiceCollectionExtensions.Version}
    💾 Database:  {database}
    📁 Telemetry: {telemetry.Directory}
    🆔 Run ID:    {telemetry.RunId}


    """);

host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping
    .Register(() => live.WriteBlock("\n⏹️  Shutting down..."));

try
{
    await host.RunAsync();
}
finally
{
    live.WriteBlock("👋 Network Monitor stopped. Goodbye!");
}
