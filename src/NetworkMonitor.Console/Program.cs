using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NetworkMonitor.Core;
using NetworkMonitor.Core.Exporters;

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

// Configure logging
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Logging.AddFilter("NetworkMonitor", LogLevel.Information);

// Register Network Monitor services
builder.Services.AddNetworkMonitor(builder.Configuration);
builder.Services.AddNetworkMonitorTelemetry(fileExporterOptions);

var host = builder.Build();

// Handle Ctrl+C gracefully
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Console.WriteLine("\n\n⏹️  Shutting down...");
};

try
{
    await host.RunAsync();
}
catch (OperationCanceledException)
{
    // Normal shutdown
}
finally
{
    Console.WriteLine("👋 Network Monitor stopped. Goodbye!");
}
