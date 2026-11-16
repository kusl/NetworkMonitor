# Network Monitor

Cross-platform network health monitor built with .NET 11 / C# 15. Pings your router, an internet target, and any number of custom hosts; stores every cycle in SQLite; optionally replicates hourly rollups to Turso/libSQL and Databricks.

> **AI-assisted development notice:** this codebase, including its architecture and documentation, was produced through extensive collaboration with Claude by Anthropic.

## Features

- At-a-glance health: Excellent / Good / Degraded / Poor / Offline
- Auto-detected gateway and internet target, with manual overrides
- Per-lookup DNS timeout, so a dead resolver cannot stall the cycle
- SQLite history (WAL, normalized schema, automatic retention)
- Optional, fault-tolerant rollup sync to Turso/libSQL and Databricks
- OpenTelemetry metrics written to rotating JSON files
- Self-contained single-file binaries for Windows, macOS, Linux (x64, ARM64)

## Quick Start

Prerequisite: .NET 11 SDK. `global.json` accepts `11.0.100-rc.3` or any later 11.0 SDK, so RC3 works locally while CI uses the latest GA.

```bash
git clone https://github.com/kusl/NetworkMonitor.git
cd NetworkMonitor/src
./run.sh            # build + run
./run.sh --test     # tests
../verify.sh        # layout check + restore + build + test summary
```

Tests run on Microsoft.Testing.Platform: `dotnet test --solution src/NetworkMonitor.slnx`.

## Layout

```
src/
├── NetworkMonitor.Core/
│   ├── Configuration/   options bound from appsettings.json
│   ├── Models/          immutable records
│   ├── Monitoring/      DNS, ping, probing, gateway discovery, health evaluation, cycle loop
│   ├── Display/         synchronized console sink, status display, console logger
│   ├── Storage/         SQLite store, data directory resolution
│   ├── Sync/            rollup sync engine + Turso and Databricks sinks
│   └── Telemetry/       metrics and rotating JSON file exporter
├── NetworkMonitor.Console/  host entry point and appsettings.json
└── NetworkMonitor.Tests/    xUnit v3, manual fakes only
```

## Configuration

`appsettings.json` next to the executable, or environment variables (`NetworkMonitor__IntervalMs=10000`). The file is resolved from the executable's directory, not the working directory.

| `NetworkMonitor` option | Default | Notes |
|---|---|---|
| `RouterAddress` | `auto` | Explicit IP disables auto-detection |
| `InternetTarget` | `8.8.8.8` | Primary internet target |
| `TimeoutMs` | `3000` | Per ping and per DNS lookup (min 100) |
| `IntervalMs` | `5000` | Start-to-start cycle interval (min 250) |
| `PingsPerCycle` | `3` | 1–20 |
| `ExcellentLatencyMs` | `20` | |
| `GoodLatencyMs` | `200` | |
| `DegradedPacketLossPercent` | `10` | |
| `EnableFallbackTargets` | `true` | Try other public resolvers if the primary is unreachable |
| `EnableIPv6` | `true` | IPv4 is always preferred; IPv6 only when no IPv4 address exists |
| `EnableDnsChecks` | `true` | Record DNS timing for hostname targets |
| `QuietConsole` | `true` | Healthy cycles overwrite one line; problems scroll |
| `MaxConcurrentChecks` | `6` | Parallel target probes per cycle |
| `CustomTargets` | list | `{ "Name", "Address", "Enabled" }` |
| `DisabledChecks` | `[]` | Names to skip, e.g. `Router`, `Internet`, a custom name |

| `Storage` option | Default |
|---|---|
| `DatabaseFileName` | `network-monitor.db` |
| `DataDirectoryOverride` | empty |
| `RetentionDays` | `30` |

## Health

Internet latency is the primary signal. A slow router reply alone never lowers health (routers de-prioritize ICMP on their control plane); it is annotated instead, and only counts when the internet is also slow.

| State | Meaning |
|---|---|
| Excellent | Internet ≤ `ExcellentLatencyMs` |
| Good | Internet ≤ `GoodLatencyMs` |
| Degraded | Packet loss, router down while internet up, or slow internet and slow router |
| Poor | Router up but internet down or slow |
| Offline | Internet unreachable |

## Data

Location: `$XDG_DATA_HOME/NetworkMonitor`, else `%LOCALAPPDATA%\NetworkMonitor` / `~/.local/share/NetworkMonitor`.

SQLite tables (unchanged since the normalized schema was introduced):

- `targets(id, name, address, category)` unique on `(address, category)`; category is `router`, `internet`, or `custom`
- `monitor_cycles(id, ts_ms, health, message)`
- `check_results(cycle_id, target_id, success, rtt_ms, rtt_min_ms, rtt_max_ms, jitter_ms, dns_ms, loss_pct, resolved_ip, error_message)`
- `sync_state(key, value)` holding `remote_rollup_next_bucket_ms` and `databricks_rollup_next_bucket_ms`

`resolved_ip` is the address that was actually pinged for hostname targets.

Telemetry: `<data>/telemetry/metrics_<runId>.json`, rotating to `metrics_<runId>_001.json`, `_002`, … at 25 MB or UTC midnight.

## Remote Sync

Both sinks ship one rollup row per target per closed bucket, keyed by `(machine, target_address, bucket_start)`, so re-sends are idempotent. Each is a no-op when unconfigured, retries silently, and can never interrupt monitoring.

Shared options for `RemoteSync` and `Databricks`: `BucketMinutes` (60), `SyncIntervalMinutes` (60, min 5), `InitialDelaySeconds`, `BatchSize`, `MaxRowsPerSync` (25000), `RequestTimeoutSeconds`.

**Turso / libSQL** (`RemoteSync`): `Url` (`libsql://`, `https://`, `wss://`), `AuthToken`, `TableName` (`check_rollups`). Defaults: delay 60 s, batch 500, timeout 30 s.

**Databricks** (`Databricks`): `WorkspaceUrl`, `WarehouseId`, `Catalog` (`workspace`), `Schema` (`default`), `TableName` (`network_monitor_rollups`), and either `Token` or `ClientId` + `ClientSecret` (OAuth M2M). `StatementWaitSeconds` 5–50. Defaults: delay 90 s, batch 200, timeout 120 s. Rows are written with `MERGE INTO` in batches of 30.

Prefer environment variables for secrets: `RemoteSync__AuthToken`, `Databricks__ClientSecret`.

## Metrics

Meter `NetworkMonitor.Core`: `network_monitor.checks`, `.failures`, `.router_latency_ms`, `.internet_latency_ms`, `.dns_resolution_ms`, `.packet_loss_percent`, `.jitter_ms`, plus .NET runtime metrics.

## Engineering Notes

- `net11.0`, C# 15 (union types, collection expression arguments), C# 14 extension members and `field`-backed option validation
- `TreatWarningsAsErrors`, `AnalysisLevel=latest-recommended`, `InvariantGlobalization`
- Central package management; dependencies: `Microsoft.Extensions.Hosting`, `Microsoft.Data.Sqlite`, `OpenTelemetry.Extensions.Hosting`, `OpenTelemetry.Instrumentation.Runtime`, `xunit.v3`
- Banned: Moq, FluentAssertions, MassTransit
- `TimeProvider` and `IMeterFactory` injected everywhere time or metrics are involved

## CI

- **Build and Test**: Ubuntu, Windows, macOS on every push and PR
- **Build and Release**: tests, then self-contained single-file binaries for six RIDs with `SHA256SUMS.txt`; binaries carry the release version
- **Dependency Check**: weekly outdated/vulnerable/deprecated report
- **Dependabot**: NuGet and Actions

## Troubleshooting

- Linux ICMP permission errors: `sudo setcap cap_net_raw+ep ./NetworkMonitor.Console`
- Wrong gateway on machines with many virtual adapters: set `RouterAddress` explicitly
- `.NET 11` requires x86-64-v2 on x64 and LSE on Windows Arm64

## License

AGPL-3.0. See [LICENSE](LICENSE).
