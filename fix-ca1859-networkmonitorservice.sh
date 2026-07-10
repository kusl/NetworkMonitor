#!/usr/bin/env bash
#
# fix-ca1859-networkmonitorservice.sh
#
# Fixes the single build error:
#
#   src/NetworkMonitor.Core/Services/NetworkMonitorService.cs(202,43):
#   error CA1859: Change type of parameter 'targets' from
#   'IReadOnlyList<CustomTargetConfig>' to 'List<CustomTargetConfig>'
#   for improved performance
#
# WHY THIS IS SAFE:
#   CheckCustomTargetsAsync is a private method. Its only caller, CheckNetworkAsync,
#   passes 'enabledCustom', which is the result of .ToList() -> a List<CustomTargetConfig>.
#   Inside the method 'targets' is used only via .Count and .Select. The analyzer can
#   therefore prove the concrete type is always available and asks us to drop the
#   interface to remove interface-dispatch overhead. Behavior is unchanged.
#
# Idempotent: safe to run more than once. Aborts loudly if the file has drifted
# from what this fix expects, rather than making a blind edit.
#
# Run from the repository root (the folder that contains 'src/').

set -euo pipefail

FILE="src/NetworkMonitor.Core/Services/NetworkMonitorService.cs"

OLD_LINE='        IReadOnlyList<CustomTargetConfig> targets,'
NEW_LINE='        List<CustomTargetConfig> targets,'

# Fixed-state detector. The new string is a SUBSTRING of the old one
# ("...IReadOnly" + "List<CustomTargetConfig> targets,"), so we only count it as
# "already fixed" when 'List' is a standalone token, i.e. NOT preceded by a letter.
FIXED_RE='(^|[^A-Za-z])List<CustomTargetConfig> targets,'

# ---------------------------------------------------------------------------
# 0. Sanity checks
# ---------------------------------------------------------------------------
if [[ ! -f "$FILE" ]]; then
    echo "ERROR: '$FILE' not found." >&2
    echo "Run this script from the repository root (the folder that contains 'src/')." >&2
    exit 1
fi

echo "Target file: $FILE"

# ---------------------------------------------------------------------------
# 1. Apply the fix (surgical, unique line, idempotent)
# ---------------------------------------------------------------------------
if grep -qF "$OLD_LINE" "$FILE"; then
    occ="$(grep -cF 'IReadOnlyList<CustomTargetConfig>' "$FILE")"
    if [[ "$occ" -ne 1 ]]; then
        echo "ERROR: expected exactly one 'IReadOnlyList<CustomTargetConfig>' occurrence," >&2
        echo "       but found $occ. Aborting to avoid an unintended edit." >&2
        exit 1
    fi

    # Literal, exact whole-line replacement via perl (\Q...\E quotes any metachars).
    OLD_LINE="$OLD_LINE" NEW_LINE="$NEW_LINE" perl -i -pe \
        'BEGIN { $o = $ENV{OLD_LINE}; $n = $ENV{NEW_LINE}; } s/\Q$o\E/$n/' "$FILE"

    # Verify the edit took and the old form is gone.
    if grep -qF "$OLD_LINE" "$FILE" || ! grep -Eq "$FIXED_RE" "$FILE"; then
        echo "ERROR: replacement did not apply as expected. Aborting." >&2
        exit 1
    fi

    echo "Applied fix:"
    echo "  -${OLD_LINE}"
    echo "  +${NEW_LINE}"
elif grep -Eq "$FIXED_RE" "$FILE"; then
    echo "Fix already present — parameter is already List<CustomTargetConfig>. Nothing to change."
else
    echo "ERROR: could not find the expected line to change:" >&2
    echo "  $OLD_LINE" >&2
    echo "The file may have been edited since this script was written. Aborting." >&2
    exit 1
fi

# ---------------------------------------------------------------------------
# 2. Show the method signature after the edit (quick eyeball)
# ---------------------------------------------------------------------------
echo
echo "Signature now reads:"
grep -n -A2 'private async Task<TargetCheckResult\[\]> CheckCustomTargetsAsync' "$FILE" || true

# ---------------------------------------------------------------------------
# 3. Build
# ---------------------------------------------------------------------------
echo
echo "=== dotnet build ==="
dotnet build

# ---------------------------------------------------------------------------
# 4. Test
# ---------------------------------------------------------------------------
echo
echo "=== dotnet test ==="
dotnet test

# ---------------------------------------------------------------------------
# 5. Summary
# ---------------------------------------------------------------------------
echo
echo "=========================================================="
echo "SUMMARY"
echo "=========================================================="
echo "File   : $FILE"
echo "Method : CheckCustomTargetsAsync"
echo "Change : parameter 'targets'"
echo "           IReadOnlyList<CustomTargetConfig>  ->  List<CustomTargetConfig>"
echo "Why    : CA1859. Private method; its only caller passes a List (.ToList()),"
echo "         and 'targets' is used only via .Count and .Select, so the concrete"
echo "         type is always available. Dropping the interface removes interface-"
echo "         dispatch overhead. No behavioral change."
echo "=========================================================="
