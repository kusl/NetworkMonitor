# Network Monitor

A cross-platform network monitoring application built with .NET 10 that provides real-time, at-a-glance network health status with historical trendline capabilities.

> **⚠️ AI-Assisted Development Notice**  
> This project was developed with extensive assistance from Large Language Models (LLMs), specifically Claude by Anthropic. The entire codebase—including architecture decisions, implementation details, documentation, and even this README—was generated through collaborative AI-human interaction. This represents a modern approach to software development where AI serves as a powerful coding assistant.

## Features

- **At-a-Glance Network Status** - Real-time visual health indicator with color-coded status (Excellent/Good/Degraded/Poor/Offline)
- **Auto-Detected Targets** - Router/gateway and internet targets are detected automatically out of the box; manual overrides remain available for edge cases
- **Dual Target Monitoring** - Simultaneously monitors local network (router) and internet connectivity, plus any number of custom targets
- **Cross-Platform** - Runs on Windows, macOS, and Linux with native self-contained executables
- **Persistent Storage** - SQLite-based historical data storage (WAL journalling) with automatic retention management
- **Optional Remote Sync** - Replicate history to a libSQL / Turso-compatible database; fully opt-in and fault-tolerant
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
git clone https://github.com/kusl/NetworkMonitor.git
cd NetworkMonitor

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
NetworkMonitor/
├── .github/
│   ├── workflows/
│   │   ├── build-and-test.yml     # CI: Build + test on all platforms
│   │   ├── release.yml            # CD: Create platform binaries
│   │   └── dependency-check.yml   # Weekly report of available updates
│   └── dependabot.yml             # Automated dependency PRs
├── src/
│   ├── NetworkMonitor.Core/      # Core library (business logic)
│   │   ├── Models/               # Domain models
│   │   ├── Services/             # Service interfaces and implementations
│   │   ├── Storage/              # SQLite persistence layer
│   │   ├── RemoteSync/           # Optional libSQL/Turso replication
│   │   └── Exporters/            # OpenTelemetry file exporters
│   ├── NetworkMonitor.Console/   # Console application entry point
│   ├── NetworkMonitor.Tests/     # xUnit 3 unit tests
│   │   └── Fakes/                # Manual test doubles (no mocking frameworks)
│   ├── Directory.Build.props     # Shared build configuration
│   ├── Directory.Packages.props  # Central package management
│   └── NetworkMonitor.slnx       # Solution file
├── export.sh                     # Source export utility (with SHA-256)
└── README.md
```

### Project Structure

| Project | Purpose |
|---------|---------|
| **NetworkMonitor.Core** | Core library with all business logic, models, service interfaces, storage abstractions, optional remote sync, and OpenTelemetry exporters. Has no UI dependencies. |
| **NetworkMonitor.Console** | Thin console application entry point that wires up hosting and runs the monitor. |
| **NetworkMonitor.Tests** | xUnit 3 unit tests with manual fake implementations (no mocking frameworks). |

## Configuration

Configuration is done via `appsettings.json` or environment variables. Out of the box, `RouterAddress` is set to `auto` so the local gateway is detected automatically, and the internet target set is chosen for you; you only need to change these for special cases (e.g. a region where a given public DNS is blocked).

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Error",
      "NetworkMonitor": "Error"
    }
  },
  "NetworkMonitor": {
    "RouterAddress": "auto",
    "InternetTarget": "8.8.8.8",
    "TimeoutMs": 3000,
    "IntervalMs": 5000,
    "PingsPerCycle": 3,
    "ExcellentLatencyMs": 20,
    "GoodLatencyMs": 200,
    "DegradedPacketLossPercent": 10,
    "EnableFallbackTargets": true,
    "EnableIPv6": true,
    "EnableDnsChecks": true,
    "QuietConsole": true,
    "MaxConcurrentChecks": 6
  },
  "Storage": {
    "ApplicationName": "NetworkMonitor",
    "DatabaseFileName": "network-monitor.db",
    "DataDirectoryOverride": "",
    "RetentionDays": 30
  },
  "RemoteSync": {
    "Url": "",
    "AuthToken": "",
    "Mode": "rollup",
    "BucketMinutes": 60,
    "SyncIntervalMinutes": 60,
    "InitialDelaySeconds": 60,
    "BatchSize": 500,
    "MaxRowsPerSync": 25000,
    "RequestTimeoutSeconds": 30,
    "TableName": "check_rollups"
  }
}
```

> `DataDirectoryOverride` is normally left empty so the XDG-compliant per-user data directory is used; set it only to force a specific location (for example, in tests). The database file name defaults to `network-monitor.db`.

### Configuration Options

| Option | Default | Description |
|--------|---------|-------------|
| `RouterAddress` | `auto` | Local router/gateway IP to ping. `auto` detects the default gateway; set an explicit IP to override. |
| `InternetTarget` | `8.8.8.8` | Primary internet target. Fallback targets are used automatically when enabled. |
| `TimeoutMs` | `3000` | Timeout per ping in milliseconds |
| `IntervalMs` | `5000` | Interval between monitoring cycles (measured start-to-start) |
| `PingsPerCycle` | `3` | Number of pings per target per cycle. Higher values make packet-loss percentages more meaningful. |
| `ExcellentLatencyMs` | `20` | Latency threshold for "Excellent" status |
| `GoodLatencyMs` | `200` | Latency threshold for "Good" status |
| `DegradedPacketLossPercent` | `10` | Packet loss percentage that counts as degraded |
| `EnableFallbackTargets` | `true` | Try additional public targets if the primary fails |
| `EnableIPv6` | `true` | See "IPv6 behavior" below |
| `EnableDnsChecks` | `true` | Also measure DNS resolution for hostname targets |
| `QuietConsole` | `true` | Only surface problematic targets (see "Console output") |
| `MaxConcurrentChecks` | `6` | Maximum custom targets pinged in parallel per cycle |
| `RetentionDays` | `30` | How long to keep historical data |

#### IPv6 behavior

For a hostname that resolves to both IPv4 and IPv6 addresses, the monitor **prefers IPv4** so latency is stable and comparable across cycles. IPv6 is used only as a fallback, and only when `EnableIPv6` is `true` and no IPv4 address is available. With `EnableIPv6` set to `false`, a target that resolves to IPv6 only is skipped rather than pinged.

#### Console output

With `QuietConsole` set to `true` (the default), healthy cycles overwrite a single status line in place, and cycles with a problem print full details that scroll and are preserved, so the terminal keeps a readable history of incidents. Set `QuietConsole` to `false` for a verbose mode that prints every target (router, internet, and all custom targets) each cycle.

## Network Health States

The application reports one of five health states:

| State | Symbol | Description |
|-------|--------|-------------|
| **Excellent** | 🟢 ◉ | Internet responding with latency ≤ `ExcellentLatencyMs` (20ms) |
| **Good** | 🟢 ◯ | Internet responding with latency ≤ `GoodLatencyMs` (200ms) |
| **Degraded** | 🟡 ◍ | High packet loss, or high latency on both internet and the local link |
| **Poor** | 🔴 ◔ | Local network reachable but internet is slow or unreachable |
| **Offline** | 🔴 ◯ | Cannot reach the internet (and, if applicable, the router) |

### How health is determined (and why a slow router is OK)

Internet latency is the **primary** signal. A consumer router answers ICMP echo on its control-plane CPU, which is commonly rate-limited and de-prioritized, while it forwards real traffic on a hardware fast path. As a result the gateway can legitimately reply **slower** than a distant server like `8.8.8.8` or `1.1.1.1` without anything being wrong on the local link. Other benign causes of a slow gateway ping include Wi-Fi power-save wake-up on the first packet, ARP resolution on the first ping, and NAT/CPU churn under load.

Because of this, a high **router** latency on its own is treated as informational and never lowers health — it is annotated (e.g. "router replies slowly: 300ms — likely ICMP de-prioritization") rather than penalized. What matters for the LAN is whether the gateway is **reachable** (loss / down), not how fast it answers pings. Router latency only counts against health when the **internet** is also slow, which points at the local link rather than the router's CPU.

## Data Storage

### Storage Locations (XDG Compliant)

- **Linux**: `$XDG_DATA_HOME/NetworkMonitor` or `~/.local/share/NetworkMonitor`
- **Windows**: `%LOCALAPPDATA%\NetworkMonitor`
- **macOS**: `~/Library/Application Support/NetworkMonitor`
- **Fallback**: Current directory with timestamp

### Database Schema

The SQLite database (`network-monitor.db`) uses a small **normalized** schema and is opened in WAL (write-ahead logging) mode so a reader (such as a trendline or rollup query) can run concurrently with the writer. It contains four tables:

- `targets` — one row per monitored target (`id`, `name`, `address`, `category`), with `UNIQUE(address, category)`. Each hostname/friendly-name/category is stored **once** and referenced by integer id, rather than being repeated on every measurement row.
- `monitor_cycles` — one row per monitoring cycle (`id`, `ts_ms`, `health`, `message`). The timestamp is 64-bit Unix milliseconds; health is the enum's integer value.
- `check_results` — one measurement per target per cycle (`cycle_id`, `target_id`, `success`, `rtt_ms`, `rtt_min_ms`, `rtt_max_ms`, `jitter_ms`, `dns_ms`, `loss_pct`, `resolved_ip`, `error_message`), keyed by `(cycle_id, target_id)`. Every measurement links to the exact cycle that produced it, and all measurements in a cycle share the cycle's single timestamp, so cycle/measurement joins are exact.
- `sync_state` — small key/value table used by the optional remote sync feature to track its replication checkpoint.

This shape keeps full per-cycle fidelity — DNS resolution time, the pinged IP, and the intra-burst min/max/jitter are all retained — while eliminating the string duplication and write-only tables of the previous flat layout. Latency is stored once (on the measurement), not duplicated across a status table.

**In-place upgrade.** On first run against a database created by an earlier version, the incompatible legacy tables (`ping_results`, `network_status`) and the old sync checkpoint key are dropped and the normalized tables are created — the file upgrades itself, so there is nothing to delete by hand. Legacy per-cycle rows are *not* migrated; they are discarded, which is acceptable because the data is transient telemetry. Historical data is automatically pruned based on the `RetentionDays` setting (measurements first, then their cycles), and freed pages are reclaimed with incremental vacuum.

### Telemetry Files

OpenTelemetry metrics are exported to JSON files in the `telemetry` subdirectory:
- Files are named with run ID and date: `metrics_20251226_080000.json`
- Automatic file rotation at 25MB
- Daily file rotation

## Remote Sync (optional)

The monitor can replicate its local check history to a remote [libSQL](https://github.com/tursodatabase/libsql) / [Turso](https://turso.tech/)-compatible database. This is entirely **opt-in**: with no URL and token configured, the feature does nothing.

**What gets synced: rollups, not raw rows.** The local database keeps every raw measurement, but replicating them one-for-one does not scale — at dozens of targets on a few-second cadence that is hundreds of thousands of rows per day, i.e. *millions* of remote row-writes per month, which permanently outruns any reasonable free-tier budget. So instead of shipping raw rows, sync ships one compact **rollup** per target per closed time bucket: a single aggregate row summarizing all of that target's cycles in the bucket (sample count, successes, average/min/max latency, average jitter, average DNS time, average packet loss). At the default hourly bucket that is at most *(number of targets)* rows per hour per machine — a few thousand rows a day rather than a million — while preserving the shape of the data for dashboards and trend queries. Only **fully-elapsed** buckets are shipped; the current, still-open bucket is held back until it closes.

Configure it under the `RemoteSync` section of `appsettings.json`, or via environment variables (`RemoteSync__Url`, `RemoteSync__AuthToken`):

| Option | Default | Description |
|--------|---------|-------------|
| `Url` | *(empty)* | Remote database URL. Accepts `libsql://`, `wss://`, `ws://`, `https://`, or `http://`; the scheme is normalized to HTTP(S) internally. Empty disables the feature. |
| `AuthToken` | *(empty)* | Bearer token for the remote database. Empty disables the feature. |
| `Mode` | `rollup` | Replication mode. `rollup` ships per-target, per-bucket aggregates (the only supported mode). |
| `BucketMinutes` | `60` | Width of a rollup bucket in minutes (minimum 1). Smaller buckets give finer remote resolution at the cost of more rows. |
| `SyncIntervalMinutes` | `60` | Minimum minutes between sync attempts (clamped to a minimum of 5). |
| `InitialDelaySeconds` | `60` | Delay before the first sync after startup, giving the network stack time to come up. |
| `BatchSize` | `500` | Rollup rows read from the local database per batch. |
| `MaxRowsPerSync` | `25000` | Upper bound on rollup rows pushed in a single run so a large backlog can't monopolize the process. |
| `RequestTimeoutSeconds` | `30` | HTTP timeout for a single pipeline request. |
| `TableName` | `check_rollups` | Remote table name (sanitized to a safe SQL identifier). |

The remote table `check_rollups` is keyed by `(machine, target_address, bucket_start)` and written with `INSERT OR REPLACE`, so re-sending a bucket is idempotent. Any provider that exposes the libSQL HTTP "Hrana" pipeline endpoint (`/v2/pipeline`) with bearer-token auth works, not just Turso.

**Design guarantees:**

- Absent or malformed configuration is a no-op, never an error.
- If the network or the remote is down, the attempt is skipped silently and retried on the next interval.
- The remote schema (table + index) is created **once per process run** with `IF NOT EXISTS` — not on every batch — and only rollup rows are shipped thereafter.
- The local checkpoint (the next un-synced bucket) only advances to the last **complete** bucket that the remote confirms, so nothing is lost or duplicated across restarts; upserts make any re-send idempotent regardless.
- **No failure in remote sync can ever interrupt network monitoring** — it runs independently and swallows all non-cancellation errors.
- Rows are tagged with the machine name, so several machines can safely share one remote database.

## Design Principles

### Minimal Dependencies

Only essential, permissively-licensed packages are used:

| Package | License | Purpose |
|---------|---------|---------|
| `Microsoft.Extensions.Hosting` | MIT | Dependency injection and lifecycle |
| `Microsoft.Data.Sqlite` | MIT | SQLite database access |
| `OpenTelemetry.*` | Apache 2.0 | Observability and metrics |
| `xunit.v3` | Apache 2.0 | Unit testing |

The SQLite native provider is pinned to `SQLitePCLRaw.bundle_e_sqlite3` 3.x, which bundles a current SQLite build. This resolves the high-severity advisory GHSA-2m69-gcr7-jv3q (CVE-2025-6965, affecting SQLite < 3.50.2) at the source rather than suppressing it.

### Banned Packages

The following packages are explicitly banned:

- **FluentAssertions** - Restrictive license
- **MassTransit** - Restrictive license  
- **Moq** - Controversial maintainer history

### Code Quality

- **Async-first**: All I/O operations are async with proper `CancellationToken` support
- **Testable**: Interface-based design with dependency injection
- **Cross-platform**: Uses `System.Net.NetworkInformation.Ping` for native ICMP (a fresh `Ping` instance per call, since a shared instance does not support concurrent async operations)
- **Graceful degradation**: Monitoring continues even if storage — or remote sync — fails
- **Code analysis**: Comprehensive code analysis with `AnalysisLevel=latest-recommended` and warnings treated as errors

## GitHub Actions

### Build and Test Workflow

Triggers on every push and pull request to any branch:
- Builds on Ubuntu, Windows, and macOS
- Runs all unit tests
- Uploads test results as artifacts

### Release Workflow  

Triggers on every push and creates self-contained executables. Every run publishes a full GitHub release (never a pre-release), tied to the exact commit:

| Platform | Architecture | Artifact Name |
|----------|--------------|---------------|
| Linux | x64 | `network-monitor-linux-x64` |
| Linux | ARM64 | `network-monitor-linux-arm64` |
| Windows | x64 | `network-monitor-win-x64` |
| Windows | ARM64 | `network-monitor-win-arm64` |
| macOS | x64 | `network-monitor-osx-x64` |
| macOS | ARM64 (Apple Silicon) | `network-monitor-osx-arm64` |

Each release includes a `SHA256SUMS.txt` for verification.

### Dependency Check Workflow

Runs weekly (and on demand) to report **all** available updates — outdated packages (including prerelease), known vulnerabilities, and deprecations — straight to the run summary. It does not build the solution, so warnings-as-error can't hide anything, and every step is non-blocking.

### Dependabot

`.github/dependabot.yml` opens automated PRs for NuGet packages (central management under `/src`) and for the GitHub Actions used by the workflows.

Actions are pinned to **major** version tags (e.g. `@v7`) so they track the latest compatible release without a version bump on every patch.

## OpenTelemetry Metrics

The application exposes the following metrics:

| Metric | Type | Description |
|--------|------|-------------|
| `network_monitor.checks` | Counter | Number of health checks performed |
| `network_monitor.router_latency_ms` | Histogram | Router ping latency distribution |
| `network_monitor.internet_latency_ms` | Histogram | Internet ping latency distribution |
| `network_monitor.failures` | Counter | Number of ping failures by target type |

Additionally, runtime instrumentation provides standard .NET metrics.

> **Note on the 0% packet-loss bucket:** because OpenTelemetry histogram buckets use an exclusive lower bound, a 0% packet-loss sample correctly lands in the `(-Infinity, 0]` bucket. This is expected behavior, not a bug.

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
    if (args.CurrentStatus.Health == NetworkHealth.Offline)
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

By default `RouterAddress` is `auto`, so the gateway is detected for you. If detection picks the wrong interface (for example on a machine with many virtual adapters), set an explicit IP in `appsettings.json`:
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

**SQLite "database is locked"**

The database is opened in WAL mode with a busy timeout, which allows a reader and the writer to run at the same time, so this should not normally occur. If you still see it, make sure only one instance of the application is running against the same data directory.

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

- [x] Optional remote database sync (libSQL / Turso-compatible)
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
