#!/bin/sh
# Rebuild RepairDump against the current runtime source (Debug, outputs under
# artifacts/widescreen, nothing of the player's Release build is touched) and
# dump one level's scene repairs to artifacts/widescreen/repairs_<level>.json.
# usage: tools/widescreen/redump.sh <level>
set -e
here="$(cd "$(dirname "$0")" && pwd)"
out="$here/../../artifacts/widescreen"
mkdir -p "$out"
(cd "$here/RepairDump" && dotnet build -c Debug -o "$out/repairdump" 2>&1 | grep -E " error |rrori|rrors" | head -20)
[ -d "$out/export_$1" ] || python "$here/wide.py" export "$1" "$out/export_$1"
"$out/repairdump/RepairDump.exe" "$out/export_$1" "$out/repairs_$1.json"
