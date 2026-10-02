#!/usr/bin/env bash
# Waits for a new Phase 2 checkpoint marker (one JSON per finished task, written by the workflow).
# Prints the new marker names and exits 0; prints TIMEOUT after MAX seconds (default 6900).
DIR=/d/Projects/Chimera-Unreal/TrialOut/checkpoints
SEEN="${SEEN_FILE:-$(dirname "$0")/.seen_markers.txt}"
MAX=${1:-6900}
mkdir -p "$DIR"; touch "$SEEN"
start=$(date +%s)
while true; do
  new=$(cd "$DIR" && ls -1 *.json 2>/dev/null | grep -vxF -f "$SEEN")
  if [ -n "$new" ]; then
    sleep 5   # let the writer finish
    echo "$new"; echo "$new" >> "$SEEN"; exit 0
  fi
  if [ $(( $(date +%s) - start )) -ge "$MAX" ]; then echo TIMEOUT; exit 0; fi
  sleep 20
done
