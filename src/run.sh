#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

SOLUTION="NetworkMonitor.slnx"
CONSOLE_PROJECT="NetworkMonitor.Console/NetworkMonitor.Console.csproj"
CONFIG="Release"

usage() {
    cat <<'EOF'
Usage: ./run.sh [--no-build | --test | --help] [-- app-args...]

  (no flag)    Build (Release) and run the console app
  --no-build   Run the console app without rebuilding
  --test       Build and run the test suite
  --help       Show this help
EOF
}

if ! command -v dotnet >/dev/null 2>&1; then
    echo "error: 'dotnet' was not found on PATH. Install the .NET 11 SDK (RC3 or later) first." >&2
    exit 127
fi

mode="run"
case "${1:-}" in
    --help|-h) usage; exit 0 ;;
    --test) mode="test"; shift ;;
    --no-build) mode="run-no-build"; shift ;;
esac

if [[ "${1:-}" == "--" ]]; then
    shift
fi

case "$mode" in
    test)
        dotnet test --solution "$SOLUTION" -c "$CONFIG"
        ;;
    run-no-build)
        dotnet run --project "$CONSOLE_PROJECT" -c "$CONFIG" --no-build -- "$@"
        ;;
    run)
        dotnet build "$SOLUTION" -c "$CONFIG"
        dotnet run --project "$CONSOLE_PROJECT" -c "$CONFIG" --no-build -- "$@"
        ;;
esac
