#!/usr/bin/env bash
# Launch the editor with the MCP server and the job bridge; stdout log goes to LookTest/logs/<tag>.stdout.log.
# MSYS_NO_PATHCONV stops Git Bash rewriting /Engine/... into C:/Program Files/Git/Engine/...
LT=D:/Projects/Chimera-Unreal/ProjectChimera/LookTest
TAG=${1:-editor}
rm -f $LT/run/bridge_ready.json
MSYS_NO_PATHCONV=1 "D:/Epic Games/UE_5.8/Engine/Binaries/Win64/UnrealEditor.exe" \
  "D:/Projects/Chimera-Unreal/ProjectChimera/ProjectChimera.uproject" /Engine/Maps/Entry \
  -ModelContextProtocolStartServer -ExecutePythonScript=$LT/tools/ue_bridge.py \
  -unattended -nosplash -nosound -stdout -FullStdOutLogOutput > $LT/logs/$TAG.stdout.log 2>&1
echo "EDITOR EXIT=$?"
