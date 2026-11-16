00
56
100

Now that dotnet 11 is out, 
lets reimagine this whole project for dotnet 11 
if you were writing this project from scratch today 
what would you do differently? 
do that 
for example, 
we do need to worry about backward compatibility when it comes to our output 
do not break user's data
other than that we have free reign to change things 
it must have a valid justificatiion if we do it
and make our code cleaner and leaner 
there may be new things we could do in dotnet 11 and csharp 15
do them the new way when and where possible 
there might also be code that we won't need with dotnet 11
also there might be nuget packages and github actions 
and other dependencies that we can upgrade to latest stable as well
upgrade them all 
as we do all of this 
please be mindful to use best engineering practices 
and solid principles 
Use your best judgment
If only a few files (under five or there about) need to change, give me the full path of the files that need to change and the FULL file 
if there are a lot of files that need to change 
Delivery
    Whole project as one tar.gz, unambiguous file placement
    do not include any file that has not changed 
    Any files to delete on updates called out explicitly
    No bespoke sed/patch scripts — full files only for anything that changes
    Docs updated alongside code changes
    Low verbosity, no code comments
    I only have the rc3 locally so the CI will need to do everything
    unless you can structure the global.json to work with rc3 or better
    this will improve soon when dotnet 11 stable lands on fedora default repos 
one another fly in the ointment 
my laptop only has rc3 
so this new code will need to work with rc3 locally 
but use the latest stable build on github actions 
```bash
kushal@fedora:~/src/dotnet/MyImapDownloader$ dotnet --info
.NET SDK:
 Version:           11.0.100-rc.3.26522.148
 Commit:            5254d8bcf5
 Workload version:  11.0.100-manifests.2c46f3cc
 MSBuild version:   18.12.0-1.26522.148+5254d8bcf

Runtime Environment:
 OS Name:     fedora
 OS Version:  45
 OS Platform: Linux
 RID:         fedora.45-x64
 Base Path:   /usr/lib64/dotnet/sdk/11.0.100-rc.3.26522.148

.NET workloads installed:
There are no installed workloads to display.
Configured to use workload sets when installing new manifests.
No workload sets are installed. Run "dotnet workload restore" to install a workload set.

Host:
  Version:      11.0.0-rc.3.26522.148
  Architecture: x64
  Commit:       5254d8bcf5

.NET SDKs installed:
  10.0.113 [/usr/lib64/dotnet/sdk]
  11.0.100-rc.3.26522.148 [/usr/lib64/dotnet/sdk]

.NET runtimes installed:
  Microsoft.AspNetCore.App 10.0.13 [/usr/lib64/dotnet/shared/Microsoft.AspNetCore.App]
  Microsoft.AspNetCore.App 11.0.0-rc.3.26522.148 [/usr/lib64/dotnet/shared/Microsoft.AspNetCore.App]
  Microsoft.NETCore.App 10.0.13 [/usr/lib64/dotnet/shared/Microsoft.NETCore.App]
  Microsoft.NETCore.App 11.0.0-rc.3.26522.148 [/usr/lib64/dotnet/shared/Microsoft.NETCore.App]

Other architectures found:
  None

Environment variables:
  Not set

global.json file:
  /home/kushal/src/dotnet/MyImapDownloader/global.json

Learn more:
  https://aka.ms/dotnet/info

Download .NET:
  https://aka.ms/dotnet/download
kushal@fedora:~/src/dotnet/MyImapDownloader$ 

```







# .NET 11 rewrite (2026-11-16)

Not compiled before delivery: the sandbox had no NuGet or .NET SDK access. CI is the first build.

## Compatibility kept
- SQLite schema, file location, category strings, sync checkpoint keys, remote table DDL and SQL, telemetry file names and JSON format, appsettings keys (`RemoteSync:Mode` dropped, it was never read)

## Structure
- Core namespaces: Configuration, Models, Monitoring, Display, Storage, Sync (+Turso, Databricks), Telemetry
- `TargetProber`: resolves DNS once per target with a `TimeoutMs` deadline, then pings the resolved IP
- `HealthEvaluator`: pure static class; messages are unchanged
- `RollupSyncer` + `IRollupSink`: one engine replaces the duplicated Turso and Databricks services
- `SqliteStore` implements `IStatusStore` and `IRollupStore`
- `RollingJsonArrayWriter`: fixes telemetry rotation, which used to reopen and truncate the same file
- Options are clamped in `field`-backed setters

## Language and runtime features used
- C# 15: `union SqlValue(long, double, string)`, `[with(capacity: n)]`
- C# 14: extension blocks, `field`
- `TimeProvider`, `IMeterFactory`, `PeriodicTimer`, `Parallel.ForEachAsync`

## Build
- global.json requires 11.0.100-rc.3 or later with latestFeature and allowPrerelease
- Removed Microsoft.NET.Test.Sdk, xunit.runner.visualstudio (tests run on MTP only), OpenTelemetry.Exporter.Console, and the redundant Options/Logging.Abstractions references
- Release: test job, version job, InformationalVersion stamped, zip includes the RID folder
- Content root is `AppContext.BaseDirectory`

## Rejected
- Native AOT and trimming: would need cross-compile toolchains for 6 RIDs
- The built-in System.Runtime meter: it would rename the runtime metrics in telemetry files
- Runtime-async: still preview





































































Build configuration first.Now models.I rewrote the project for .NET 11 and C# 15. The tarball has the 74 new or changed files, with paths relative to the repo root. **It hasn't been compiled or tested yet.** This workspace couldn't reach NuGet or download a .NET SDK, so CI will be the first build. I only checked the syntax with a parser, and that parser doesn't know C# 15. The C# goes from about 9,200 lines to about 4,700.

**Apply it**
```bash
rm -rf src/NetworkMonitor.Core src/NetworkMonitor.Tests
tar -xzf networkmonitor-dotnet11.tar.gz
./verify.sh
```
Deleting those two folders removes all 66 old files. Every file that belongs in them now is in the tarball. Old folders that are now gone: `Core/Services`, `Core/RemoteSync`, `Core/Exporters`, `Core/Logging`, `Tests/Services`, `Tests/RemoteSync`, `Tests/Models`. `verify.sh` reports any of them that are still there.

**Works with RC3 locally and the stable release in CI:** `global.json` asks for `11.0.100-rc.3.26522.148` or anything newer in 11.0, and allows preview builds. Your laptop uses RC3; CI installs the stable `11.0.x` SDK, which counts as newer.

**Your data is untouched:** the SQLite schema and file location, the sync checkpoint keys, the remote table names and SQL, the telemetry file names and JSON format, and the `appsettings.json` keys all stay the same. I dropped `RemoteSync:Mode`, which nothing ever read.

**What changed, and why**
- **Two bugs fixed:**
  - Telemetry file rotation used to reopen and wipe the same file at UTC midnight and at 25 MB, so earlier metrics were lost. A small new writer class now numbers the files `_001`, `_002`, and so on.
  - Databricks status polling built the wrong URL, so slow statements always counted as failed.
- **DNS timeout:** this is the fix from your notes. Each hostname is now looked up once per cycle with a hard `TimeoutMs` limit, then the resolved IP is pinged. Before, the name was looked up twice with no time limit. The `resolved_ip` column now holds the address that was actually pinged.
- **One sync engine:** the Turso and Databricks sync services repeated about 400 lines of the same logic. Now one engine drives both, and each destination only builds its own SQL.
- **Settings file location:** `appsettings.json` is now read from the folder the program is in. The release notes' `./linux-x64/NetworkMonitor.Console` launch silently ignored the file before.
- **Smaller pieces:** the health rules are now their own self-contained class with the same messages as before. Storage has separate interfaces for saving cycles and for sync. Time and metrics are injected, so the tests no longer depend on the real clock.
- **Deleted dead code:** history queries, the recent-pings query, the IPv6 gateway and target lists, the status-changed event, and the internet target provider class.
- **New language features:**
  - C# 15: a union type for SQL values, so encoding them must cover every case, and collection expression arguments (`[with(capacity: …)]`).
  - C# 14: extension members and validation in `field`-backed setters, which replaced the range checks scattered across the code.
- **Dependencies:**
  - Microsoft.Extensions and Microsoft.Data.Sqlite go to 11.0.0. OpenTelemetry (1.21.0), xunit.v3 (4.0.3) and the GitHub Actions versions were already the latest stable.
  - Removed `Microsoft.NET.Test.Sdk` and `xunit.runner.visualstudio`, since tests run only on Microsoft.Testing.Platform.
  - Removed `OpenTelemetry.Exporter.Console`, because the detailed console mode already shows the same data.
- **CI:**
  - The release workflow now runs the tests before building binaries.
  - Each binary is stamped with its release version, which the startup banner shows.
  - The Windows zip now contains the `win-x64` folder, so it matches the install steps in the release notes.
  - The dependency report uses the newer `dotnet package list` command.
  - `run.sh --test` was missing `--solution` and failed; fixed.
- **Considered and left out:**
  - Native AOT: it needs a cross-compile toolchain for each of the six platforms.
  - The built-in `System.Runtime` metrics: they would rename the runtime metrics in your telemetry files.
  - Runtime async: still a preview feature.

**Most likely to break on the first CI run**
- **Union type syntax:** RC3 and the release compiler could differ slightly.
- **Code analysis warnings treated as errors:** I avoided the risky ones I know of. If one fires, it should be a one-line fix.
- **Test discovery:** removing `Microsoft.NET.Test.Sdk` could stop `dotnet test` finding the test project. If that happens, add the package back.

The README is rewritten to match. I also saved a short summary of these decisions to the project as `claude/dotnet11-rewrite.md`.

Sources: [C# 15 what's new](https://learn.microsoft.com/en-us/dotnet/csharp/whats-new/csharp-15), [Union types](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/builtin-types/union), [.NET 11 overview](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/overview), [Upgrading to .NET 11](https://startdebugging.net/2026/11/dotnet-11-is-generally-available-what-to-change-in-your-project/), [OpenTelemetry](https://www.nuget.org/packages/OpenTelemetry), [OpenTelemetry.Instrumentation.Runtime](https://www.nuget.org/packages/OpenTelemetry.Instrumentation.Runtime), [xunit.v3](https://www.nuget.org/packages/xunit.v3), [Microsoft.Data.Sqlite](https://www.nuget.org/packages/Microsoft.Data.Sqlite), [Microsoft.Extensions.Hosting](https://www.nuget.org/packages/Microsoft.Extensions.Hosting), [SQLitePCLRaw](https://www.nuget.org/packages/SQLitePCLRaw.bundle_e_sqlite3), [actions/checkout](https://github.com/actions/checkout/releases), [actions/setup-dotnet](https://github.com/actions/setup-dotnet/releases), [actions/upload-artifact](https://github.com/actions/upload-artifact/releases), [actions/download-artifact](https://github.com/actions/download-artifact/releases)

30
62
100















































