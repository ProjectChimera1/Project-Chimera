#!/usr/bin/env bash
# Mirrors H/{*.uproject, Config, Source} into tools/unreal-hud/ChimeraHud-src/ (the off-machine copy of project text), redacting
# SecurityToken=/*Key= values. `--check` re-scans the mirror and the source and fails if a secret is unredacted.
#   bash tools/unreal-hud/sync_src.sh           mirror
#   bash tools/unreal-hud/sync_src.sh --check   verify (prints MIRROR OK, 0 secrets)
HERE=$(cd "$(dirname "$0")" && pwd)
H=${H:-D:/Projects/Chimera-Unreal/ChimeraHud}
DEST=${DEST:-$HERE/ChimeraHud-src}
KIT=${KIT:-$HERE/../unreal-trial}
if [ "$1" = "--check" ]; then
  SCAN=$(mktemp); python "$KIT/secret_scan.py" "$DEST" > "$SCAN" 2>&1; RC=$?
  # file-for-file equality with the (redacted) source
  python - "$H" "$DEST" <<'PY'
import os, re, sys
h, dest = sys.argv[1], sys.argv[2]
def red(t):
    return re.sub(r"(?im)^(\s*[+\-.!]?\w*(?:SecurityToken|Key)\s*=).*$", r"\1<redacted>", t)
src = [f for f in os.listdir(h) if f.endswith(".uproject")]
bad = 0
for root in ("Config", "Source"):
    for d, _, fs in os.walk(os.path.join(h, root)):
        for f in fs:
            rel = os.path.relpath(os.path.join(d, f), h)
            src.append(rel)
for rel in src:
    s = os.path.join(h, rel); t = os.path.join(dest, rel)
    if not os.path.isfile(t):
        print("MIRROR MISSING", rel); bad += 1; continue
    if red(open(s, encoding="utf-8", errors="replace").read()) != open(t, encoding="utf-8", errors="replace").read():
        print("MIRROR DIFFERS", rel); bad += 1
sys.exit(1 if bad else 0)
PY
  RC2=$?
  if [ $RC -eq 0 ] && [ $RC2 -eq 0 ]; then echo "MIRROR OK, 0 secrets"; exit 0; fi
  cat "$SCAN" | head; echo "MIRROR FAIL scan=$RC compare=$RC2"; exit 1
fi
rm -rf "$DEST"; mkdir -p "$DEST"
python - "$H" "$DEST" <<'PY'
import os, re, shutil, sys
h, dest = sys.argv[1], sys.argv[2]
def red(t):
    return re.sub(r"(?im)^(\s*[+\-.!]?\w*(?:SecurityToken|Key)\s*=).*$", r"\1<redacted>", t)
files = [f for f in os.listdir(h) if f.endswith(".uproject")]
for root in ("Config", "Source"):
    for d, _, fs in os.walk(os.path.join(h, root)):
        files += [os.path.relpath(os.path.join(d, f), h) for f in fs]
for rel in files:
    t = os.path.join(dest, rel); os.makedirs(os.path.dirname(t), exist_ok=True)
    with open(os.path.join(h, rel), encoding="utf-8", errors="replace", newline="") as f: txt = f.read()
    with open(t, "w", encoding="utf-8", newline="") as f: f.write(red(txt))
print("mirrored", len(files), "files ->", dest)
PY
