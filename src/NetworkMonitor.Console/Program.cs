using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NetworkMonitor.Core;
using NetworkMonitor.Core.Exporters;
using NetworkMonitor.Core.Models;

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

// Logging levels are driven by appsettings.json / environment variables.
// No hardcoded overrides here — the config file is the single source of truth.
// Default appsettings.json ships with Error level so the console stays quiet.

// Register Network Monitor services
builder.Services.AddNetworkMonitor(builder.Configuration);

// Read QuietConsole to decide whether to enable the OTel console exporter.
// When QuietConsole is true (default), only file export is active —
// no histogram spam on the console. Set QuietConsole=false to get raw
// OpenTelemetry output on stdout alongside the status display.
var monitorSection = builder.Configuration.GetSection(MonitorOptions.SectionName);
var quietConsole = monitorSection.GetValue("QuietConsole", defaultValue: true);

builder.Services.AddNetworkMonitorTelemetry(
    fileExporterOptions,
    enableConsoleExporter: !quietConsole);

var host = builder.Build();

// Handle Ctrl+C gracefully
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Console.WriteLine("\n\n⏹️  Shutting down...");
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
    Console.WriteLine("👋 Network Monitor stopped. Goodbye!");
}
