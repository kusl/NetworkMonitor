#!/usr/bin/env bash
set -uo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$ROOT_DIR"

SOLUTION="src/NetworkMonitor.slnx"
CONFIG="Release"
STRUCT_RESULT="not run"
RESTORE_RESULT="not run"
BUILD_RESULT="not run"
TEST_RESULT="not run"
OVERALL=0

line() { printf '%s\n' "------------------------------------------------------------"; }

if ! command -v dotnet >/dev/null 2>&1; then
    echo "error: 'dotnet' was not found on PATH. Install the .NET 11 SDK (RC3 or later) first." >&2
    exit 127
fi

echo ">> .NET SDK version: $(dotnet --version 2>/dev/null || echo 'unknown')"

echo ">> Checking source tree layout"
EXPECTED=(
    "$SOLUTION"
    "global.json"
    "src/Directory.Build.props"
    "src/Directory.Packages.props"
    "src/NetworkMonitor.Core/NetworkMonitor.Core.csproj"
    "src/NetworkMonitor.Console/NetworkMonitor.Console.csproj"
    "src/NetworkMonitor.Tests/NetworkMonitor.Tests.csproj"
    "src/NetworkMonitor.Console/appsettings.json"
    "src/NetworkMonitor.Core/Storage/SqliteStore.cs"
    "src/NetworkMonitor.Core/Monitoring/NetworkMonitorService.cs"
    "src/NetworkMonitor.Core/Sync/RollupSyncer.cs"
    "src/NetworkMonitor.Tests/Storage/SqliteStoreTests.cs"
    "src/NetworkMonitor.Tests/Sync/RollupSyncerTests.cs"
)
RETIRED=(
    "src/NetworkMonitor.Core/Services"
    "src/NetworkMonitor.Core/RemoteSync"
    "src/NetworkMonitor.Core/Exporters"
    "src/NetworkMonitor.Core/Logging"
    "src/NetworkMonitor.Core/Storage/SqliteStorageService.cs"
    "src/NetworkMonitor.Tests/Services"
    "src/NetworkMonitor.Tests/RemoteSync"
    "src/NetworkMonitor.Tests/Models"
)
PROBLEMS=()
for f in "${EXPECTED[@]}"; do [[ -e "$f" ]] || PROBLEMS+=("MISSING: $f"); done
for f in "${RETIRED[@]}"; do [[ -e "$f" ]] && PROBLEMS+=("SHOULD BE DELETED: $f"); done

if [[ ${#PROBLEMS[@]} -eq 0 ]]; then
    STRUCT_RESULT="PASS"
    echo "   layout OK"
else
    STRUCT_RESULT="FAIL"
    OVERALL=1
    printf '   %s\n' "${PROBLEMS[@]}"
fi

line
echo ">> dotnet restore"
if dotnet restore "$SOLUTION"; then RESTORE_RESULT="PASS"; else RESTORE_RESULT="FAIL"; OVERALL=1; fi

line
echo ">> dotnet build ($CONFIG)"
if [[ "$RESTORE_RESULT" == "PASS" ]]; then
    if dotnet build "$SOLUTION" -c "$CONFIG" --no-restore; then BUILD_RESULT="PASS"; else BUILD_RESULT="FAIL"; OVERALL=1; fi
else
    BUILD_RESULT="skipped (restore failed)"; OVERALL=1
fi

line
echo ">> dotnet test ($CONFIG)"
if [[ "$BUILD_RESULT" == "PASS" ]]; then
    if dotnet test --solution "$SOLUTION" -c "$CONFIG" --no-build; then TEST_RESULT="PASS"; else TEST_RESULT="FAIL"; OVERALL=1; fi
else
    TEST_RESULT="skipped (build failed)"; OVERALL=1
fi

line
echo "NetworkMonitor verification summary"
line
printf '  %-22s %s\n' "source layout:" "$STRUCT_RESULT"
printf '  %-22s %s\n' "restore:" "$RESTORE_RESULT"
printf '  %-22s %s\n' "build ($CONFIG):" "$BUILD_RESULT"
printf '  %-22s %s\n' "tests ($CONFIG):" "$TEST_RESULT"
printf '  %-22s %s\n' "C# source files:" "$(find src -name '*.cs' -not -path '*/obj/*' -not -path '*/bin/*' | wc -l | tr -d ' ')"
line
if [[ $OVERALL -eq 0 ]]; then echo "RESULT: OK - build and tests passed."; else echo "RESULT: FAILURE - see the steps above."; fi
line

exit $OVERALL
