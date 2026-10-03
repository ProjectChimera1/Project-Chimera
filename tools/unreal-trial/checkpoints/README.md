# Checkpoint markers for long trial workflows

Workflows in `../workflows/` write one JSON per finished task (and per judged look round) to
`D:/Projects/Chimera-Unreal/TrialOut/checkpoints/<task>.json` via a small haiku agent. The main session commits from them while
the workflow keeps running.

- `wait_marker.sh [max_s]`: run with the Bash tool in the background; it exits when a marker it has not seen appears, by name and modification time, so a rewritten `<task>.json` counts (prints the
  names) or after `max_s` (default 6900) with `TIMEOUT`. The seen list is `.seen_markers.txt` here (override with `SEEN_FILE`).
- `ckpt.py <task>...`: prints a marker's files split by repo (R, U) with their git status, and writes `ckpt_R.txt` / `ckpt_U.txt`
  (to `CKPT_OUT`, default this folder) for `git add --pathspec-from-file`. Diff the list against `git status` before committing:
  parallel tasks leave their own files in the same trees.
