#!/usr/bin/env bash
# One in-process HUD capture (plan B 2.9).
#   hud_shot.sh --backdrop X --tag T [--map M] [--extra "..."] [--warmup]
# Preflight runs OUTSIDE the lock; the Unreal run is under the lock (re-entrant v2). Prints SHOT OK ... or SHOT FAIL <reason> exit=<n>.
# Env: H (project dir), LOCK, UE_LOCK_TAG.
HERE=$(cd "$(dirname "$0")" && pwd)
H=${H:-D:/Projects/Chimera-Unreal/ChimeraHud}
LOCK=${LOCK:-D:/Projects/Chimera-Unreal/ue_lock.sh}
KIT=${KIT:-$HERE/../unreal-trial}
UE="D:/Epic Games/UE_5.8/Engine/Binaries/Win64/UnrealEditor.exe"
BACKDROP='#14161A'; TAG=shot; MAP=/Engine/Maps/Entry; EXTRA=""; WARMUP=0
while [ $# -gt 0 ]; do
  case "$1" in
    --backdrop) BACKDROP=$2; shift 2;;
    --tag) TAG=$2; shift 2;;
    --map) MAP=$2; shift 2;;
    --extra) EXTRA=$2; shift 2;;
    --warmup) WARMUP=1; shift;;
    *) echo "SHOT FAIL bad argument $1 exit=2"; exit 2;;
  esac
done
export UE_LOCK_TAG=${UE_LOCK_TAG:-b/hud_shot}
mkdir -p "$H/HudRef/logs" "$H/HudRef/out"
LOG="$H/HudRef/logs/$TAG.log"; PNG="$H/HudRef/out/$TAG.png"

# 1. desktop check, outside the lock (a Parsec resolution change fails fast without wasting a hold)
PF=$(powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$(cygpath -w "$KIT/preflight.ps1")" 2>&1 | tr -d '\r')
echo "$PF" | head -3
case "$PF" in *"DESKTOP OK 1920x1080 @96"*) ;; *) echo "SHOT FAIL DESKTOP NOT 1920x1080@100% exit=1"; exit 1;; esac

# 2. launch under the lock with a wall-clock kill
COMPILE_WAIT=1800; SHOT_TO=120
case " $EXTRA " in *" -HudCompileWait="*) COMPILE_WAIT=$(echo "$EXTRA" | sed -n 's/.*-HudCompileWait=\([0-9]*\).*/\1/p');; esac
case " $EXTRA " in *" -HudShotTimeout="*) SHOT_TO=$(echo "$EXTRA" | sed -n 's/.*-HudShotTimeout=\([0-9]*\).*/\1/p');; esac
if [ "$WARMUP" = 1 ]; then DEADLINE=1800; MODE="-HudWarmup"; else DEADLINE=$((COMPILE_WAIT + SHOT_TO + 120)); MODE="-HudShot=$PNG"; fi
rm -f "$LOG"; [ "$WARMUP" = 1 ] || rm -f "$PNG"
START_EPOCH=$(date +%s)
OUT=$(bash "$LOCK" bash "$HERE/hud_shot_inner.sh" "$DEADLINE" "$UE" "$H/ChimeraHud.uproject" "$MAP?game=/Script/ChimeraHud.ChimeraHudGameMode" \
  -game -fullscreen -ResX=1920 -ResY=1080 -nosound -unattended -nosplash "-ABSLOG=$LOG" '-LogCmds=LogViewport Verbose' \
  '-ExecCmds=DisableAllScreenMessages,r.ScreenPercentage 100,t.MaxFPS 60' "-HudBackdrop=$BACKDROP" -HudFreezeTime=3.0 $MODE $EXTRA)
RC=$(echo "$OUT" | sed -n 's/^EXIT=//p' | tail -1)

# 3. checks (an -HudUiScale=<s> in --extra makes the geometry check expect that scale; T8's ungated UI-scale shot)
UISCALE=1.0
case " $EXTRA " in *" -HudUiScale="*) UISCALE=$(echo " $EXTRA " | sed -n 's/.* -HudUiScale=\([0-9.]*\).*/\1/p');; esac
python "$HERE/hud_shot_check.py" --log "$LOG" --png "$PNG" --start "$START_EPOCH" --exit "${RC:-NONE}" --backdrop "$BACKDROP" --h "$H" --ui-scale "$UISCALE" $([ "$WARMUP" = 1 ] && echo --warmup)
