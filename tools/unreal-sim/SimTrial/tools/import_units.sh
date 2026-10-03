#!/usr/bin/env bash
# A9: import the sim-trial unit meshes in one editor session. Run it UNDER THE LOCK, from Git Bash:
#   UE_LOCK_TAG=a/A9 bash D:/Projects/Chimera-Unreal/ue_lock.sh bash D:/Projects/Chimera-Unreal/ProjectChimera/SimTrial/tools/import_units.sh
# Steps: refuse unless every project module has a built editor DLL; prep (python); start the editor with the
# look-test job bridge; queue sim_import.run; ask the editor to quit; wait for it to exit; run check_unit_meshes.py.
# Exit 0 only if all of that held. MSYS_NO_PATHCONV stops Git Bash rewriting /Engine/... (PLAN_DELTA D7).
P=D:/Projects/Chimera-Unreal/ProjectChimera
SIM=$P/SimTrial
LT=$P/LookTest
LOG=$LT/logs/sim_a9.stdout.log
ENGINE="D:/Epic Games/UE_5.8/Engine/Binaries/Win64/UnrealEditor.exe"
TIMEOUT_READY=${A9_READY_SEC:-1500}   # first launch may compile shaders for a long time

# 1. every module listed in the .uproject must have a built DLL (X1 adds ChimeraSimHost; the editor would otherwise
#    ask to rebuild it and block on a dialog).
python - <<'PY' || exit 2
import json, os, sys
P = 'D:/Projects/Chimera-Unreal/ProjectChimera'
mods = [m['Name'] for m in json.load(open(P + '/ProjectChimera.uproject'))['Modules']]
miss = [m for m in mods if not os.path.isfile(f'{P}/Binaries/Win64/UnrealEditor-{m}.dll')]
if miss:
    print(f'import_units: refusing to start, no built editor DLL for module(s) {miss}; build the Editor target first', file=sys.stderr)
    sys.exit(1)
print('import_units: modules built:', mods)
PY

python $SIM/tools/prep_units.py || { echo "import_units: prep failed" >&2; exit 3; }

mkdir -p $LT/logs $LT/queue
rm -f $LT/run/bridge_ready.json
MSYS_NO_PATHCONV=1 "$ENGINE" "$P/ProjectChimera.uproject" /Engine/Maps/Entry \
  -ExecutePythonScript=$SIM/tools/sim_bridge.py \
  -unattended -nosplash -nosound -stdout -FullStdOutLogOutput > $LOG 2>&1 &
EDITOR_PID=$!
echo "import_units: editor started (pid $EDITOR_PID), log $LOG"

# Failure paths must not leave the editor running: the lock is released when this script exits. Only the editor this
# script started is ever killed (its Windows pid, with its child tree).
kill_editor() {
  kill -0 $EDITOR_PID 2>/dev/null || return 0
  WINPID=$(cat /proc/$EDITOR_PID/winpid 2>/dev/null)
  echo "import_units: terminating the editor this script started (pid $EDITOR_PID, winpid ${WINPID:-?})" >&2
  if [ -n "$WINPID" ]; then taskkill //PID $WINPID //T //F >&2; else kill -9 $EDITOR_PID; fi
  for i in $(seq 1 24); do kill -0 $EDITOR_PID 2>/dev/null || break; sleep 5; done
  wait $EDITOR_PID 2>/dev/null
  kill -0 $EDITOR_PID 2>/dev/null && echo "import_units: WARNING editor pid $EDITOR_PID still alive" >&2
  return 0
}

t0=$(date +%s)
while [ ! -f $LT/run/bridge_ready.json ]; do
  if ! kill -0 $EDITOR_PID 2>/dev/null; then echo "import_units: editor exited before the bridge was ready" >&2; tail -20 $LOG >&2; exit 4; fi
  if [ $(( $(date +%s) - t0 )) -gt $TIMEOUT_READY ]; then echo "import_units: bridge not ready after ${TIMEOUT_READY}s" >&2; kill_editor; exit 5; fi
  sleep 5
done
echo "import_units: bridge ready after $(( $(date +%s) - t0 ))s"

python $LT/tools/ue_job.py sim_import run --timeout 1800
RC=$?
python $LT/tools/ue_job.py sim_import quit --timeout 120 >/dev/null 2>&1
# clean exit: wait up to 3 minutes for the editor, never kill it unless it is hung
for i in $(seq 1 36); do kill -0 $EDITOR_PID 2>/dev/null || break; sleep 5; done
if kill -0 $EDITOR_PID 2>/dev/null; then
  echo "import_units: editor did not exit within 180 s of QUIT_EDITOR" >&2; kill_editor; [ $RC -eq 0 ] && RC=6
else
  wait $EDITOR_PID; ERC=$?; echo "import_units: editor exit code $ERC"
  [ $ERC -eq 0 ] || { [ $RC -eq 0 ] && RC=7; }
fi
[ $RC -eq 0 ] || { echo "import_units: import job failed (rc=$RC)" >&2; exit $RC; }
python $SIM/tools/check_unit_meshes.py
RC=$?
[ $RC -eq 0 ] || exit $RC
# A11: freshly created team MIs lack the "Used with Instanced Static Meshes" usage, so -game would draw the default material
# on the renderer's ISMs. Content/ is git-ignored, so the fix must follow every import (same lock hold, a second editor session).
bash $SIM/tools/fix_unit_usage.sh || { echo "import_units: fix_unit_usage failed" >&2; exit 8; }
