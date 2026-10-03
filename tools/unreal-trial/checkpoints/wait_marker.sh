#!/usr/bin/env bash
# Waits for a new or rewritten checkpoint marker (one JSON per finished task, written by the workflow).
# A marker counts as new when its name and modification time have not been seen, so a task re-run that rewrites
# <task>.json is caught too. Prints the new marker names and exits 0; prints TIMEOUT after MAX seconds (default 6900).
DIR=/d/Projects/Chimera-Unreal/TrialOut/checkpoints
SEEN="${SEEN_FILE:-$(dirname "$0")/.seen_markers.txt}"
MAX=${1:-6900}
mkdir -p "$DIR"; touch "$SEEN"
start=$(date +%s)
while true; do
  new=$(cd "$DIR" && for f in *.json; do case "$f" in *-implnote.json) continue;; esac; [ -e "$f" ] && echo "$f|$(stat -c %Y "$f")"; done | grep -vxF -f "$SEEN")
  if [ -n "$new" ]; then
    sleep 5   # let the writer finish
    new=$(cd "$DIR" && for f in *.json; do case "$f" in *-implnote.json) continue;; esac; [ -e "$f" ] && echo "$f|$(stat -c %Y "$f")"; done | grep -vxF -f "$SEEN")
    echo "$new" | cut -d'|' -f1; echo "$new" >> "$SEEN"; exit 0
  fi
  if [ $(( $(date +%s) - start )) -ge "$MAX" ]; then echo TIMEOUT; exit 0; fi
  sleep 20
done
