using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NetworkMonitor.Core;
using NetworkMonitor.Core.Exporters;
using NetworkMonitor.Core.Logging;
using NetworkMonitor.Core.Models;
using NetworkMonitor.Core.Services;

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
//
// Whatever the level, log records and the live status line are serialized
// through a single synchronized console sink (LiveConsole), so they never
// interleave: every timestamped line appears on its own line.
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

// Replace the default (asynchronous, uncoordinated) console logger with the
// LiveConsole provider. The default logger writes from a background thread with
// no ordering guarantee, which is what mangled the status line. Routing logging
// through LiveConsole makes it share one lock with the status display.
// ClearProviders() removes provider instances only; the config-driven log
// levels in appsettings.json still apply.
builder.Logging.ClearProviders();
builder.Logging.AddLiveConsole();

// Register Network Monitor services
builder.Services.AddNetworkMonitor(builder.Configuration);

// Read QuietConsole to decide whether to enable the OTel console exporter.
// When QuietConsole is true (default), only file export is active —
// no histogram spam on the console. Set QuietConsole=false to get raw
// OpenTelemetry output on stdout alongside the status display.
var quietConsoleValue = builder.Configuration
    .GetSection(MonitorOptions.SectionName)["QuietConsole"];
var quietConsole = !string.Equals(quietConsoleValue, "false", StringComparison.OrdinalIgnoreCase);

builder.Services.AddNetworkMonitorTelemetry(
    fileExporterOptions,
    enableConsoleExporter: !quietConsole);

var host = builder.Build();

// The shared console sink; used so shutdown messages also clear any parked
// status line and land on their own lines.
var live = host.Services.GetRequiredService<LiveConsole>();

// Handle Ctrl+C gracefully
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    live.WriteBlock("\n⏹️  Shutting down...");
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
    live.WriteBlock("👋 Network Monitor stopped. Goodbye!");
}
