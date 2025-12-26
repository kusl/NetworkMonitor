# Network Monitor

A cross-platform network monitoring application built with .NET 10 that provides real-time, at-a-glance network health status with historical trendline capabilities.

> **⚠️ AI-Assisted Development Notice**  
> This project was developed with extensive assistance from Large Language Models (LLMs), specifically Claude by Anthropic. The entire codebase—including architecture decisions, implementation details, documentation, and even this README—was generated through collaborative AI-human interaction. This represents a modern approach to software development where AI serves as a powerful coding assistant.

## Features

- **At-a-Glance Network Status** - Real-time visual health indicator with color-coded status (Excellent/Good/Degraded/Poor/Offline)
- **Dual Target Monitoring** - Simultaneously monitors local network (router) and internet connectivity
- **Cross-Platform** - Runs on Windows, macOS, and Linux with native self-contained executables
- **Persistent Storage** - SQLite-based historical data storage with automatic retention management
- **OpenTelemetry Integration** - Full observability with metrics exported to files and console
- **XDG Compliant** - Follows platform-specific conventions for data storage locations
- **Zero External Dependencies at Runtime** - Self-contained executables require no .NET runtime installation
- **Testable Architecture** - Dependency injection with interface-based design for easy unit testing

## Quick Start

### Prerequisites

- .NET 10 SDK (for building from source)
- Or download pre-built binaries from the Releases page

### Building from Source

```bash
# Clone the repository
git clone https://github.com/yourusername/network-monitor.git
cd network-monitor

# Build and run
cd src
dotnet restore
dotnet build
dotnet run --project NetworkMonitor.Console
```

### Running Tests

```bash
cd src
dotnet test
```

### Using the Convenience Script

```bash
cd src
chmod +x run.sh
./run.sh              # Build and run
./run.sh --test       # Run tests only
./run.sh --no-build   # Run without rebuilding
```

## Architecture

```
network-monitor/
├── .github/
│   └── workflows/
│       ├── build-and-test.yml    # CI: Build + test on all platforms
│       └── release.yml           # CD: Create platform binaries
├── src/
│   ├── NetworkMonitor.Core/      # Core library (business logic)
│   │   ├── Models/               # Domain models
│   │   ├── Services/             # Service interfaces and implementations
│   │   ├── Storage/              # SQLite persistence layer
│   │   └── Exporters/            # OpenTelemetry file exporters
│   ├── NetworkMonitor.Console/   # Console application entry point
│   ├── NetworkMonitor.Tests/     # xUnit 3 unit tests
│   │   └── Fakes/                # Manual test doubles (no mocking frameworks)
│   ├── Directory.Build.props     # Shared build configuration
│   ├── Directory.Packages.props  # Central package management
│   └── NetworkMonitor.slnx       # Solution file
└── README.md
```

### Project Structure

| Project | Purpose |
|---------|---------|
| **NetworkMonitor.Core** | Core library with all business logic, models, service interfaces, storage abstractions, and OpenTelemetry exporters. Has no UI dependencies. |
| **NetworkMonitor.Console** | Thin console application entry point that wires up hosting and runs the monitor. |
| **NetworkMonitor.Tests** | xUnit 3 unit tests with manual fake implementations (no mocking frameworks). |

## Configuration

Configuration is done via `appsettings.json` or environment variables:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Warning",
      "NetworkMonitor": "Information"
    }
  },
  "NetworkMonitor": {
    "RouterAddress": "192.168.1.1",
    "InternetTarget": "8.8.8.8",
    "TimeoutMs": 3000,
    "IntervalMs": 5000,
    "PingsPerCycle": 3,
    "ExcellentLatencyMs": 20,
    "GoodLatencyMs": 100,
    "DegradedPacketLossPercent": 10
  },
  "Storage": {
    "ApplicationName": "NetworkMonitor",
    "MaxFileSizeBytes": 26214400,
    "RetentionDays": 30
  }
}
```

### Configuration Options

| Option | Default | Description |
|--------|---------|-------------|
| `RouterAddress` | `192.168.1.1` | Local router/gateway IP to ping |
| `InternetTarget` | `8.8.8.8` | Internet target (Google DNS) |
| `TimeoutMs` | `3000` | Timeout per ping in milliseconds |
| `IntervalMs` | `5000` | Interval between monitoring cycles |
| `PingsPerCycle` | `3` | Number of pings per target per cycle |
| `ExcellentLatencyMs` | `20` | Latency threshold for "Excellent" status |
| `GoodLatencyMs` | `100` | Latency threshold for "Good" status |
| `RetentionDays` | `30` | How long to keep historical data |

## Network Health States

The application reports one of five health states:

| State | Symbol | Description |
|-------|--------|-------------|
| **Excellent** | 🟢 ◉ | All targets responding with latency ≤ 20ms |
| **Good** | 🟢 ◯ | All targets responding with latency ≤ 100ms |
| **Degraded** | 🟡 ◍ | High latency or some packet loss |
| **Poor** | 🔴 ◔ | Local network OK but no internet access |
| **Offline** | 🔴 ◯ | Cannot reach local network |

## Data Storage

### Storage Locations (XDG Compliant)

- **Linux**: `$XDG_DATA_HOME/NetworkMonitor` or `~/.local/share/NetworkMonitor`
- **Windows**: `%LOCALAPPDATA%\NetworkMonitor`
- **macOS**: `~/Library/Application Support/NetworkMonitor`
- **Fallback**: Current directory with timestamp

### Database Schema

The SQLite database (`network-monitor.db`) contains:

- `ping_results` - Individual ping results with timestamps
- `network_status` - Aggregated health status snapshots

Historical data is automatically pruned based on the `RetentionDays` setting.

### Telemetry Files

OpenTelemetry metrics are exported to JSON files in the `telemetry` subdirectory:
- Files are named with run ID and date: `metrics_20251226_080000.json`
- Automatic file rotation at 25MB
- Daily file rotation

## Design Principles

### Minimal Dependencies

Only essential, permissively-licensed packages are used:

| Package | License | Purpose |
|---------|---------|---------|
| `Microsoft.Extensions.Hosting` | MIT | Dependency injection and lifecycle |
| `Microsoft.Data.Sqlite` | MIT | SQLite database access |
| `OpenTelemetry.*` | Apache 2.0 | Observability and metrics |
| `xunit.v3` | Apache 2.0 | Unit testing |

### Banned Packages

The following packages are explicitly banned:

- **FluentAssertions** - Restrictive license
- **MassTransit** - Restrictive license  
- **Moq** - Controversial maintainer history

### Code Quality

- **Async-first**: All I/O operations are async with proper `CancellationToken` support
- **Testable**: Interface-based design with dependency injection
- **Cross-platform**: Uses `System.Net.NetworkInformation.Ping` for native ICMP
- **Graceful degradation**: Monitoring continues even if storage fails
- **Code analysis**: Comprehensive code analysis with `AnalysisLevel=latest-recommended`

## GitHub Actions

### Build and Test Workflow

Triggers on every push and pull request to any branch:
- Builds on Ubuntu, Windows, and macOS
- Runs all unit tests
- Uploads test results as artifacts

### Release Workflow  

Triggers on every push and creates self-contained executables:

| Platform | Architecture | Artifact Name |
|----------|--------------|---------------|
| Linux | x64 | `network-monitor-linux-x64` |
| Linux | ARM64 | `network-monitor-linux-arm64` |
| Linux (Alpine) | x64 | `network-monitor-linux-musl-x64` |
| Windows | x64 | `network-monitor-win-x64` |
| Windows | ARM64 | `network-monitor-win-arm64` |
| macOS | x64 | `network-monitor-osx-x64` |
| macOS | ARM64 (Apple Silicon) | `network-monitor-osx-arm64` |

## OpenTelemetry Metrics

The application exposes the following metrics:

| Metric | Type | Description |
|--------|------|-------------|
| `network_monitor.checks` | Counter | Number of health checks performed |
| `network_monitor.router_latency_ms` | Histogram | Router ping latency distribution |
| `network_monitor.internet_latency_ms` | Histogram | Internet ping latency distribution |
| `network_monitor.failures` | Counter | Number of ping failures by target type |

Additionally, runtime instrumentation provides standard .NET metrics.

## Testing Approach

### Manual Fakes over Mocking Frameworks

Instead of using mocking frameworks like Moq, this project uses manually implemented test doubles:

```csharp
// FakePingService allows precise control over test scenarios
var fake = new FakePingService()
    .QueueResult(PingResult.Succeeded("router", 10))
    .QueueResult(PingResult.Failed("internet", "Timeout"));
```

Benefits:
- More explicit and readable tests
- No magic or runtime code generation
- Full control over test behavior
- Avoids dependency on controversial packages

### Test Categories

- **Unit Tests**: Test individual components in isolation
- **Model Tests**: Verify domain model behavior (PingResult, NetworkStatus)
- **Service Tests**: Test service logic with fake dependencies
- **Fake Tests**: Ensure test doubles work correctly

## API/SDK Usage

### Service Registration

```csharp
// In Program.cs or Startup.cs
builder.Services.AddNetworkMonitor(builder.Configuration);
builder.Services.AddNetworkMonitorTelemetry();
```

### Direct Service Usage

```csharp
// Inject INetworkMonitorService
public class MyController(INetworkMonitorService monitor)
{
    public async Task<NetworkStatus> GetStatus(CancellationToken ct)
    {
        return await monitor.CheckNetworkAsync(ct);
    }
}
```

### Event Handling

```csharp
monitor.StatusChanged += (sender, args) =>
{
    if (args.Status.Health == NetworkHealth.Offline)
    {
        // Handle offline state
        NotifyUser("Network is offline!");
    }
};
```

## Troubleshooting

### Common Issues

**Ping Permission Errors on Linux**

Raw socket access may require elevated privileges:
```bash
# Option 1: Run with sudo
sudo ./network-monitor

# Option 2: Set capabilities (recommended)
sudo setcap cap_net_raw+ep ./network-monitor
```

**Router Address Detection**

The default router address (`192.168.1.1`) may not match your network. Update `appsettings.json`:
```json
{
  "NetworkMonitor": {
    "RouterAddress": "192.168.0.1"  // Your router's IP
  }
}
```

To find your router's IP:
- **Windows**: `ipconfig` → Default Gateway
- **Linux/macOS**: `ip route | grep default` or `netstat -nr`

**SQLite Database Locked**

If the database appears locked, ensure only one instance is running. The application uses a semaphore to prevent concurrent access.

## Development

### Prerequisites

- .NET 10 SDK
- Any IDE (Visual Studio, VS Code, Rider, etc.)

### Building

```bash
cd src
dotnet build
```

### Running with Hot Reload

```bash
dotnet watch run --project NetworkMonitor.Console
```

### Code Formatting

```bash
dotnet format
```

### Publishing Self-Contained

```bash
# Linux x64
dotnet publish NetworkMonitor.Console -c Release -r linux-x64 --self-contained

# Windows x64  
dotnet publish NetworkMonitor.Console -c Release -r win-x64 --self-contained

# macOS ARM64 (Apple Silicon)
dotnet publish NetworkMonitor.Console -c Release -r osx-arm64 --self-contained
```

## Contributing

1. Fork the repository
2. Create a feature branch (`git checkout -b feature/amazing-feature`)
3. Commit your changes (`git commit -m 'Add amazing feature'`)
4. Push to the branch (`git push origin feature/amazing-feature`)
5. Open a Pull Request

### Contribution Guidelines

- Follow existing code style and patterns
- Write unit tests for new functionality
- Use manual fakes, not mocking frameworks
- Keep dependencies minimal and permissively licensed
- Ensure cross-platform compatibility

## Roadmap

- [ ] Web dashboard for historical data visualization
- [ ] Configurable alerting (email, webhook, system notifications)
- [ ] Multiple target profiles (home, work, etc.)
- [ ] Network quality scoring algorithm improvements
- [ ] OTLP exporter integration for external observability platforms
- [ ] Docker container support

## License

This project is licensed under the **GNU Affero General Public License v3.0 (AGPL-3.0)**.

See the [LICENSE](LICENSE) file for details.

## Acknowledgments

- Built with [.NET 10](https://dotnet.microsoft.com/)
- Observability powered by [OpenTelemetry](https://opentelemetry.io/)
- Testing with [xUnit](https://xunit.net/)
- AI assistance provided by [Claude](https://www.anthropic.com/claude) by Anthropic

---

**Network Monitor** - Know your network health at a glance.
