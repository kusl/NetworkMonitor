#!/usr/bin/env bash
#
# Convenience runner for NetworkMonitor.
#
#   ./run.sh              Build (Release) and run the console app
#   ./run.sh --no-build   Run the console app without rebuilding
#   ./run.sh --test       Build and run the test suite, then exit
#   ./run.sh --help       Show this help
#
# Run this from the "src" directory (where NetworkMonitor.slnx lives).
# Any extra arguments after the flag are forwarded to the console app, e.g.:
#
#   ./run.sh -- --some-console-flag
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

SOLUTION="NetworkMonitor.slnx"
CONSOLE_PROJECT="NetworkMonitor.Console/NetworkMonitor.Console.csproj"
CONFIG="Release"

usage() {
    sed -n '2,17p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
}

if ! command -v dotnet >/dev/null 2>&1; then
    echo "error: 'dotnet' was not found on PATH. Install the .NET 10 SDK first." >&2
    exit 127
fi

mode="run"
case "${1:-}" in
    --help|-h)
        usage
        exit 0
        ;;
    --test)
        mode="test"
        shift
        ;;
    --no-build)
        mode="run-no-build"
        shift
        ;;
    --)
        shift
        ;;
esac

# Drop a leading "--" separator if present so remaining args pass through cleanly.
if [[ "${1:-}" == "--" ]]; then
    shift
fi

case "$mode" in
    test)
        echo ">> dotnet test ($CONFIG)"
        dotnet test "$SOLUTION" -c "$CONFIG"
        ;;
    run-no-build)
        echo ">> dotnet run --no-build ($CONFIG)"
        dotnet run --project "$CONSOLE_PROJECT" -c "$CONFIG" --no-build -- "$@"
        ;;
    run)
        echo ">> dotnet build ($CONFIG)"
        dotnet build "$SOLUTION" -c "$CONFIG"
        echo ">> dotnet run ($CONFIG)"
        dotnet run --project "$CONSOLE_PROJECT" -c "$CONFIG" --no-build -- "$@"
        ;;
esac
