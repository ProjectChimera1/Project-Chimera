#!/usr/bin/env bash
# MCP preview captures of the currently loaded map from CAM_Gameplay and CAM_Close; prints the two PNG paths.
cd "$(dirname "$0")"
NA='"annotations":{"gridSpacing":0,"gridExtent":0,"gridHeight":0,"maxLabelDistance":0,"classFilter":{"refPath":"/Script/Engine.Actor"},"maxLabels":0},"bShowUI":false'
cap(){ python mcp_call.py call EditorToolset.EditorAppToolset CaptureViewport "{\"captureTransform\":{\"location\":{\"x\":$1,\"y\":$2,\"z\":$3},\"rotation\":{\"pitch\":$4,\"yaw\":90,\"roll\":0},\"scale\":{\"x\":1,\"y\":1,\"z\":1}},$NA}" --save-images ../out/mcp 2>&1 | grep IMAGE | awk '{print $2}'; }
cap 0 -5142.3 6128.4 -50
cap -200 -2098.1 1928.4 -40
