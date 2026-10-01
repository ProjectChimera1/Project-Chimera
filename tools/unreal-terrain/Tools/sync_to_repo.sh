#!/usr/bin/env bash
# Mirror ChimeraTerrain text files (Source, Config, Tools, Scripts, uproject, VENDOR.md) into the Chimera repo at tools/unreal-terrain.
# Never copies Binaries, Intermediate, Saved, Content, Plugins, Out. Runs secret_scan.py first. Run from Git Bash.
set -euo pipefail
T="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
RT="${RT:-D:/Projects/Project_Chimera/tools/unreal-terrain}"
SCAN="${SCAN:-D:/Projects/Project_Chimera/tools/unreal-trial/secret_scan.py}"
ITEMS=(Source Config Tools Scripts ChimeraTerrain.uproject VENDOR.md)
EXCL=(--exclude=Binaries --exclude=Intermediate --exclude=Saved --exclude=Content --exclude=Plugins --exclude=Out --exclude=__pycache__ --exclude='*.pyc')
cd "$T"
present=()
for i in "${ITEMS[@]}"; do [ -e "$i" ] && present+=("$i"); done
python "$SCAN" "${present[@]}"
mkdir -p "$RT"
for i in "${present[@]}"; do
  if [ -d "$i" ]; then
    rm -rf "$RT/$i"; mkdir -p "$RT/$i"
    tar -c "${EXCL[@]}" -f - "$i" | tar -x -f - -C "$RT"
  else
    cp -f "$i" "$RT/$i"
  fi
done
echo "SYNC OK -> $RT ($(find "$RT" -type f -not -name README.md | wc -l) files)"
