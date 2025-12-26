#!/bin/bash
# =============================================================================
# Fix Three Remaining Build Errors
# =============================================================================
# This script fixes only the 3 remaining errors after dotnet format:
#
# 1. CA1305 - StorageOptions.cs line 62:
#    DateTime.ToString(string) needs IFormatProvider
#
# 2. CA1513 - PingService.cs line 29:
#    Use ObjectDisposedException.ThrowIf instead of explicit throw
#
# 3. CA1859 - SqliteStorageService.cs line 231:
#    Change return type of AggregateByGranularity from IReadOnlyList to List
# =============================================================================

set -e
cd ~/src/dotnet/network-monitor/src

echo "=== Fixing 3 Remaining Build Errors ==="
echo ""

# -----------------------------------------------------------------------------
# Fix 1: StorageOptions.cs - CA1305
# Add CultureInfo.InvariantCulture to DateTime.ToString()
# -----------------------------------------------------------------------------
echo "[1/3] Fixing StorageOptions.cs - CA1305 (DateTime.ToString needs IFormatProvider)"

# First, add the using statement if not present
if ! grep -q "using System.Globalization;" NetworkMonitor.Core/Models/StorageOptions.cs; then
    sed -i '1i using System.Globalization;' NetworkMonitor.Core/Models/StorageOptions.cs
    echo "      Added 'using System.Globalization;'"
fi

# Fix the DateTime.ToString call - need to match the exact line
sed -i 's/DateTime\.UtcNow\.ToString("yyyyMMdd_HHmmss")/DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture)/g' \
    NetworkMonitor.Core/Models/StorageOptions.cs

echo "      Fixed DateTime.ToString to use CultureInfo.InvariantCulture"

# -----------------------------------------------------------------------------
# Fix 2: PingService.cs - CA1513
# Replace explicit ObjectDisposedException with ThrowIf
# -----------------------------------------------------------------------------
echo "[2/3] Fixing PingService.cs - CA1513 (Use ObjectDisposedException.ThrowIf)"

# Replace the if block with the modern ThrowIf pattern
# The current code is:
#   if (_disposed)
#   {
#       throw new ObjectDisposedException(nameof(PingService));
#   }
# Replace with:
#   ObjectDisposedException.ThrowIf(_disposed, this);

sed -i '/if (_disposed)/,/^[[:space:]]*}$/c\        ObjectDisposedException.ThrowIf(_disposed, this);' \
    NetworkMonitor.Core/Services/PingService.cs

echo "      Replaced explicit throw with ObjectDisposedException.ThrowIf"

# -----------------------------------------------------------------------------
# Fix 3: SqliteStorageService.cs - CA1859
# Change return type of AggregateByGranularity from IReadOnlyList to List
# -----------------------------------------------------------------------------
echo "[3/3] Fixing SqliteStorageService.cs - CA1859 (Return List instead of IReadOnlyList)"

# Change the method signature
sed -i 's/private static IReadOnlyList<HistoricalData> AggregateByGranularity/private static List<HistoricalData> AggregateByGranularity/g' \
    NetworkMonitor.Core/Storage/SqliteStorageService.cs

echo "      Changed AggregateByGranularity return type to List<HistoricalData>"

# -----------------------------------------------------------------------------
# Verification
# -----------------------------------------------------------------------------
echo ""
echo "=== Verification ==="
echo ""

echo "Building to verify fixes..."
if dotnet build --no-restore 2>&1; then
    echo ""
    echo "✅ BUILD SUCCESSFUL - All 3 errors fixed!"
else
    echo ""
    echo "❌ Build still has errors. Checking individual files..."
    echo ""
    
    # Show the relevant lines for debugging
    echo "StorageOptions.cs - Line with DateTime.ToString:"
    grep -n "DateTime.UtcNow.ToString" NetworkMonitor.Core/Models/StorageOptions.cs || echo "  (not found)"
    
    echo ""
    echo "PingService.cs - Disposal check:"
    grep -n -A2 "ObjectDisposedException" NetworkMonitor.Core/Services/PingService.cs | head -5 || echo "  (not found)"
    
    echo ""
    echo "SqliteStorageService.cs - AggregateByGranularity signature:"
    grep -n "AggregateByGranularity" NetworkMonitor.Core/Storage/SqliteStorageService.cs | head -3 || echo "  (not found)"
fi

echo ""
echo "=== Done ==="
