#!/usr/bin/env bash
#
# verify.sh - idempotent build/test verification for NetworkMonitor.
#
# Run from the repository root:
#
#     ./verify.sh
#
# It performs a structural sanity check on the source tree, then restores,
# builds, and tests the solution and prints a structured summary. It changes
# nothing in the repository (no files are written or modified), so it is safe
# to run repeatedly.
#
# Exit code is 0 only if every step succeeds; non-zero otherwise.
#
set -uo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$ROOT_DIR"

SOLUTION="src/NetworkMonitor.slnx"
CONFIG="Release"

# --- collected results -------------------------------------------------------
STRUCT_RESULT="not run"
RESTORE_RESULT="not run"
BUILD_RESULT="not run"
TEST_RESULT="not run"
OVERALL=0

line() { printf '%s\n' "------------------------------------------------------------"; }

# --- 0. tooling --------------------------------------------------------------
if ! command -v dotnet >/dev/null 2>&1; then
    echo "error: 'dotnet' was not found on PATH. Install the .NET 10 SDK first." >&2
    exit 127
fi

echo ">> .NET SDK version: $(dotnet --version 2>/dev/null || echo 'unknown')"

# --- 1. structural checks ----------------------------------------------------
echo ">> Checking source tree layout"
MISSING=()
EXPECTED=(
    "$SOLUTION"
    "src/Directory.Build.props"
    "src/Directory.Packages.props"
    "src/NetworkMonitor.Core/NetworkMonitor.Core.csproj"
    "src/NetworkMonitor.Console/NetworkMonitor.Console.csproj"
    "src/NetworkMonitor.Tests/NetworkMonitor.Tests.csproj"
    "src/NetworkMonitor.Core/Models/CheckRollup.cs"
    "src/NetworkMonitor.Core/Models/TargetCheckResult.cs"
    "src/NetworkMonitor.Core/Storage/SqliteStorageService.cs"
    "src/NetworkMonitor.Core/Storage/IStorageService.cs"
    "src/NetworkMonitor.Core/RemoteSync/RemoteSyncService.cs"
    "src/NetworkMonitor.Core/Services/NetworkMonitorService.cs"
    "src/NetworkMonitor.Tests/Storage/SqliteStorageServiceTests.cs"
    "src/NetworkMonitor.Tests/RemoteSync/RemoteSyncServiceTests.cs"
    "src/NetworkMonitor.Console/appsettings.json"
)
for f in "${EXPECTED[@]}"; do
    [[ -f "$f" ]] || MISSING+=("$f")
done

# The old flat-schema model must be gone.
RETIRED="src/NetworkMonitor.Core/Models/StoredPingResult.cs"
RETIRED_PRESENT=0
[[ -f "$RETIRED" ]] && RETIRED_PRESENT=1

if [[ ${#MISSING[@]} -eq 0 && $RETIRED_PRESENT -eq 0 ]]; then
    STRUCT_RESULT="PASS"
    echo "   layout OK"
else
    STRUCT_RESULT="FAIL"
    OVERALL=1
    for m in "${MISSING[@]}"; do echo "   MISSING: $m"; done
    [[ $RETIRED_PRESENT -eq 1 ]] && echo "   SHOULD BE DELETED: $RETIRED"
fi

# --- 2. restore --------------------------------------------------------------
line
echo ">> dotnet restore"
if dotnet restore "$SOLUTION"; then
    RESTORE_RESULT="PASS"
else
    RESTORE_RESULT="FAIL"; OVERALL=1
fi

# --- 3. build (warnings are errors via Directory.Build.props) ----------------
line
echo ">> dotnet build ($CONFIG)"
if [[ "$RESTORE_RESULT" == "PASS" ]]; then
    if dotnet build "$SOLUTION" -c "$CONFIG" --no-restore; then
        BUILD_RESULT="PASS"
    else
        BUILD_RESULT="FAIL"; OVERALL=1
    fi
else
    BUILD_RESULT="skipped (restore failed)"; OVERALL=1
fi

# --- 4. test -----------------------------------------------------------------
line
echo ">> dotnet test ($CONFIG)"
if [[ "$BUILD_RESULT" == "PASS" ]]; then
    if dotnet test "$SOLUTION" -c "$CONFIG" --no-build; then
        TEST_RESULT="PASS"
    else
        TEST_RESULT="FAIL"; OVERALL=1
    fi
else
    TEST_RESULT="skipped (build failed)"; OVERALL=1
fi

# --- 5. structured summary ---------------------------------------------------
line
echo "NetworkMonitor verification summary"
line
printf '  %-22s %s\n' "source layout:"  "$STRUCT_RESULT"
printf '  %-22s %s\n' "restore:"        "$RESTORE_RESULT"
printf '  %-22s %s\n' "build ($CONFIG):" "$BUILD_RESULT"
printf '  %-22s %s\n' "tests ($CONFIG):" "$TEST_RESULT"
line
CS_COUNT=$(find src -name '*.cs' | wc -l | tr -d ' ')
printf '  %-22s %s\n' "C# source files:" "$CS_COUNT"
printf '  %-22s %s\n' "projects:"        "3 (Core, Console, Tests)"
line
if [[ $OVERALL -eq 0 ]]; then
    echo "RESULT: OK - build and tests passed."
else
    echo "RESULT: FAILURE - see the steps above."
fi
line

exit $OVERALL
