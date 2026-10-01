#!/usr/bin/env bash
# Builds the ChimeraHud editor target under the UE lock (about 95 s warm). Run from Git Bash.
#   UE_LOCK_TAG=b/T3 bash tools/unreal-hud/hud_build.sh
H=${H:-D:/Projects/Chimera-Unreal/ChimeraHud}
LOCK=${LOCK:-D:/Projects/Chimera-Unreal/ue_lock.sh}
export UE_LOCK_TAG=${UE_LOCK_TAG:-b/hud_build}
mkdir -p "$H/HudRef/logs"
bash "$LOCK" "D:/Epic Games/UE_5.8/Engine/Build/BatchFiles/Build.bat" ChimeraHudEditor Win64 Development -Project="$H/ChimeraHud.uproject" -WaitMutex 2>&1 | tee "$H/HudRef/logs/build.log"
exit "${PIPESTATUS[0]}"
