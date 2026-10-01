#!/usr/bin/env bash
# Runs UnrealEditor -game for one capture under a hard deadline; called by hud_shot.sh INSIDE the lock (re-entrant).
# usage: hud_shot_inner.sh <deadline_s> <exe> <args...>   -> writes the exit code to stdout as "EXIT=<n>" ("EXIT=KILLED" past the deadline)
DEADLINE=$1; shift
MSYS_NO_PATHCONV=1 "$@" >/dev/null 2>&1 &
PID=$!
WINPID=$(cat /proc/$PID/winpid 2>/dev/null || echo "$PID")
END=$((SECONDS + DEADLINE))
while kill -0 "$PID" 2>/dev/null; do
  if [ "$SECONDS" -ge "$END" ]; then
    taskkill //PID "$WINPID" //T //F >/dev/null 2>&1
    wait "$PID" 2>/dev/null
    echo "EXIT=KILLED"
    exit 0
  fi
  sleep 1
done
wait "$PID"
echo "EXIT=$?"
