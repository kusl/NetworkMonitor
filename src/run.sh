#!/bin/bash
# =============================================================================
# Run Network Monitor
# =============================================================================
# Convenience script to build and run the network monitor.
#
# Usage:
#   ./run.sh                # Build and run
#   ./run.sh --no-build     # Run without building
#   ./run.sh --test         # Run tests only
# =============================================================================

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

case "${1:-}" in
    --no-build)
        echo "Running without build..."
        dotnet run --project NetworkMonitor.Console --no-build
        ;;
    --test)
        echo "Running tests..."
        # .NET 10 SDK runs `dotnet test` on Microsoft.Testing.Platform (see the
        # repo-root global.json). MTP mode wants an explicit target rather than
        # relying on the current directory, so name the solution directly.
        dotnet test --solution NetworkMonitor.slnx --verbosity normal
        ;;
    *)
        echo "Building and running..."
        dotnet build
        dotnet run --project NetworkMonitor.Console
        ;;
esac
