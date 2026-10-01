#!/usr/bin/env bash
# Iterate-until-converged step (plan B section 5). Run from Git Bash.
#   hud_iterate.sh <tag> [--only REGEX] [--gates LIST] [--skip-build] [--extra "..."] [--pairs AB|A|B] [--note TEXT]
#
# 1. ONE lock hold covers the build (unless --skip-build) and the shots of the selected pairs; the inner hud_build.sh /
#    hud_shot.sh LOCK calls are re-entrant (UE_LOCK_HOLDER, ue_lock.sh v2). Pair A = flat #14161A, pair B = world_layer.png.
# 2. Outside the lock: hud_compare.py per pair into H/HudRef/out/<tag>-<n>/<pair>/, one row appended to
#    results/convergence.csv (iteration, time, failing per pair, worst region, badness, HUD MAD, SSIM, source tree hash, note).
# 3. When every selected pair PASSES: one more shot of pair A (its own hold) must equal the previous one (MAD 0): DETERMINISM OK.
# 4. Prints ESCALATE after 15 iterations of <tag>, or when the last 3 iterations improved worst badness by < 5% each without
#    the failing count dropping (plan B 5.5). Runtime switches (-HudHinting, -HudKern, -HudIcons, -HudText) go in --extra with
#    --skip-build. Never edits thresholds.json, regions.json, calibration.json or the references (plan B 5.6).
# Exit: 0 all selected pairs PASS (and determinism OK), 1 a pair FAILS, 2 usage, 3 build/shot failure.
set -u
HERE=$(cd "$(dirname "$0")" && pwd)
H=${H:-D:/Projects/Chimera-Unreal/ChimeraHud}
UELOCK=${UELOCK:-D:/Projects/Chimera-Unreal/ue_lock.sh}
export H   # never export LOCK: ue_lock.sh uses that name for its lock directory
export UE_LOCK_TAG=${UE_LOCK_TAG:-b/hud_iterate}

if [ "${1:-}" = "--inner" ]; then
  # inside the lock: build (optional) and shots; prints the SHOT lines
  shift; SKIP=$1; PAIRS=$2; PREFIX=$3; EXTRA=$4
  if [ "$SKIP" != 1 ]; then
    bash "$HERE/hud_build.sh" > "$H/HudRef/logs/${PREFIX}_build.log" 2>&1
    RC=$?
    grep -q "Result: Succeeded" "$H/HudRef/logs/${PREFIX}_build.log" || { echo "BUILD FAIL rc=$RC (see $H/HudRef/logs/${PREFIX}_build.log)"; exit 3; }
    echo "BUILD OK"
  fi
  case "$PAIRS" in *A*) bash "$HERE/hud_shot.sh" --backdrop '#14161A' --tag "${PREFIX}_A" --extra "$EXTRA" | tail -1;; esac
  case "$PAIRS" in *B*) bash "$HERE/hud_shot.sh" --backdrop "$H/HudRef/ref/world_layer.png" --tag "${PREFIX}_B" --extra "$EXTRA" | tail -1;; esac
  exit 0
fi

TAG=${1:-}; [ -n "$TAG" ] && [ "${TAG#--}" = "$TAG" ] || { echo "usage: hud_iterate.sh <tag> [--only REGEX] [--gates LIST] [--skip-build] [--extra \"...\"] [--pairs AB|A|B] [--note TEXT]"; exit 2; }
shift
ONLY=""; GATES=""; SKIP=0; EXTRA=""; PAIRS=AB; NOTE=""
while [ $# -gt 0 ]; do
  case "$1" in
    --only) ONLY=$2; shift 2;;
    --gates) GATES=$2; shift 2;;
    --skip-build) SKIP=1; shift;;
    --extra) EXTRA=$2; shift 2;;
    --pairs) PAIRS=$2; shift 2;;
    --note) NOTE=$2; shift 2;;
    *) echo "bad argument $1"; exit 2;;
  esac
done
OUTROOT="$H/HudRef/out"; mkdir -p "$OUTROOT" "$H/HudRef/logs"
N=1; while [ -d "$OUTROOT/$TAG-$N" ]; do N=$((N + 1)); done
IT="$TAG-$N"; mkdir -p "$OUTROOT/$IT"
PREFIX=$(echo "$IT" | tr -c 'A-Za-z0-9_\n-' '_')

# 1. one hold: build + shots
SHOTS=$(bash "$UELOCK" bash "$HERE/hud_iterate.sh" --inner "$SKIP" "$PAIRS" "$PREFIX" "$EXTRA")
echo "$SHOTS"
echo "$SHOTS" | grep -q "^BUILD FAIL" && exit 3
for P in A B; do
  case "$PAIRS" in *$P*) echo "$SHOTS" | grep -q "^SHOT OK .*${PREFIX}_$P.png" || { echo "ITERATE FAIL shot $P"; exit 3; };; esac
done

# 2. compare outside the lock
ARGS=(); [ -n "$ONLY" ] && ARGS+=(--only "$ONLY"); [ -n "$GATES" ] && ARGS+=(--gates "$GATES")
declare -A LINE RES
ALLPASS=1
for P in A B; do
  case "$PAIRS" in *$P*) ;; *) continue;; esac
  if [ "$P" = A ]; then REF="$H/HudRef/ref/ref_hudonly.png"; else REF="$H/HudRef/ref/ref_backdrop.png"; fi
  OUT=$(python "$HERE/hud_compare.py" --pair "$P" --ref "$REF" --shot "$OUTROOT/${PREFIX}_$P.png" --out "$OUTROOT/$IT/$P" --tag "$IT" "${ARGS[@]}" 2>&1)
  RC=$?
  echo "$OUT"
  LINE[$P]=$(echo "$OUT" | grep "^PAIR $P:" | tail -1)
  [ $RC -eq 0 ] || ALLPASS=0
  [ $RC -eq 2 ] && { echo "ITERATE FAIL compare $P (usage or calibration hash)"; exit 3; }
done

# convergence.csv row
TREE=$( (cd "$H/Source" && find . -type f | LC_ALL=C sort | xargs sha256sum) | sha256sum | cut -c1-16)
field() { echo "$1" | sed -n "$2"; }
fail_of() { field "$1" 's/.*RESULT [A-Z]* \([0-9]*\)\/[0-9]* regions.*/\1/p'; }
WL="${LINE[A]:-${LINE[B]:-}}"
WORST=$(field "$WL" 's/.*WORST \([^ ]*\) badness.*/\1/p'); BAD=$(field "$WL" 's/.*badness \([^ ]*\) (.*/\1/p')
MAD=$(field "$WL" 's/.*HUD MAD \([0-9.]*\);.*/\1/p'); SSIM=$(field "$WL" 's/.*HUD SSIM \([0-9.]*\);.*/\1/p')
FA=$(fail_of "${LINE[A]:-}"); FB=$(fail_of "${LINE[B]:-}")
echo "${LINE[A]:-}" | grep -q "alignment" && FA=align; echo "${LINE[B]:-}" | grep -q "alignment" && FB=align
CSV="$HERE/results/convergence.csv"
[ -f "$CSV" ] || echo "iteration,time,failing_A,failing_B,worst_region,badness,hud_mad,hud_ssim,tree_hash,note" > "$CSV"
NOTE_CSV=$(echo "only=${ONLY:-all} gates=${GATES:-all} extra=${EXTRA:-none} ${NOTE}" | tr ',' ';')
echo "$IT,$(date '+%Y-%m-%d %H:%M:%S'),${FA:-},${FB:-},${WORST:-},${BAD:-},${MAD:-},${SSIM:-},$TREE,$NOTE_CSV" >> "$CSV"

# 4. escalation rule
python - "$CSV" "$TAG" <<'PY'
import csv, sys
rows = [r for r in csv.DictReader(open(sys.argv[1], encoding="utf-8")) if r["iteration"].rsplit("-", 1)[0] == sys.argv[2]]
def num(v):
    try: return float(v)
    except Exception: return float("inf")
if len(rows) >= 15:
    print(f"ESCALATE: {len(rows)} iterations of {sys.argv[2]} (plan B 5.5)")
elif len(rows) >= 4:
    last = rows[-4:]
    stalled = all(num(b["badness"]) > 0.95 * num(a["badness"]) and num(b["failing_A"]) >= num(a["failing_A"]) for a, b in zip(last, last[1:]))
    if stalled:
        print(f"ESCALATE: last 3 iterations of {sys.argv[2]} improved worst badness < 5% without fewer failing regions (plan B 5.5)")
PY

if [ $ALLPASS -ne 1 ]; then
  echo "ITERATE $IT: FAIL (see $OUTROOT/$IT)"
  exit 1
fi

# 3. determinism shot (own hold), MAD 0 against the previous pair-A capture
case "$PAIRS" in *A*) ;; *) echo "ITERATE $IT: PASS (no pair A, determinism not checked)"; exit 0;; esac
D=$(bash "$HERE/hud_shot.sh" --backdrop '#14161A' --tag "${PREFIX}_det" --extra "$EXTRA" | tail -1)
echo "$D"
echo "$D" | grep -q "^SHOT OK" || { echo "ITERATE FAIL determinism shot"; exit 3; }
python "$HERE/hud_compare.py" --pipeline "$OUTROOT/${PREFIX}_A.png" "$OUTROOT/${PREFIX}_det.png" --max 0 | sed 's/^PIPELINE OK/DETERMINISM OK/; s/^PIPELINE FAIL/DETERMINISM FAIL/'
python "$HERE/hud_compare.py" --pipeline "$OUTROOT/${PREFIX}_A.png" "$OUTROOT/${PREFIX}_det.png" --max 0 > /dev/null || exit 1
echo "ITERATE $IT: PASS"
exit 0
