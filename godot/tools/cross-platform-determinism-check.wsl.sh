#!/usr/bin/env bash
# =============================================================================
# cross-platform-determinism-check.wsl.sh — WSL/Linux worker for the
# Windows<->Linux golden-checksum gate (Story 1.10c, AR-37).
#
# Runs the Tier-1 Godot-free golden-checksum suite on LINUX and VERIFIES it
# against the committed (Windows-recorded) goldens. A green run proves, tick by
# tick, that Linux computes byte-identical Fixed checksums to Windows.
#
# ISOLATION: builds inside a WSL-NATIVE clone of the repo (committed HEAD), NOT
# the shared /mnt/d working tree. This avoids reusing Windows obj/bin
# intermediates (CS0579 duplicate-AssemblyInfo / stale-output hazards) and never
# disturbs the Windows build state. The 9p /mnt/d mount is used only to clone from.
#
# HARD RULES (AR-37 / AR-2 / AR-35):
#   * VERIFY MODE ONLY — never sets CHIMERA_GOLDEN_RECORD (recording on Linux would
#     overwrite the committed Windows baseline and the gate would prove nothing).
#   * Targets the Godot-free test csproj BY PATH; installs no Godot/GodotSharp.
#   * --locked-mode restore against the committed packages.lock.json (no dep drift).
#   * Exits NON-ZERO on ANY divergence; GoldenChecksumReplay.CompareSequences prints
#     the first diverging tick (expected vs actual) — that IS the cross-platform diff.
#
# Usage:  bash cross-platform-determinism-check.wsl.sh <source-repo-wsl-path>
#   e.g.  bash cross-platform-determinism-check.wsl.sh /mnt/d/Projects/Project_Chimera
# =============================================================================
set -euo pipefail

SRC="${1:?usage: cross-platform-determinism-check.wsl.sh <source-repo-wsl-path>}"
CLONE="$HOME/chimera-xplat-check"
CSPROJ="godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj"

# --- dotnet on PATH (independent of login-profile state) ----------------------
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$HOME/.dotnet:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1     # AR-41: no telemetry/beacon
if ! command -v dotnet >/dev/null 2>&1; then
  echo "[wsl] ERROR: dotnet not found on PATH. Run godot/tools/wsl-dotnet-setup.sh first." >&2
  exit 3
fi
echo "[wsl] dotnet $(dotnet --version)  ($(uname -s -m))"

# --- CRITICAL: never record on the Linux leg ---------------------------------
unset CHIMERA_GOLDEN_RECORD 2>/dev/null || true

# --- Sync a WSL-native clone of the source repo's committed HEAD --------------
if [ ! -d "$SRC/.git" ]; then
  echo "[wsl] ERROR: source repo not found at '$SRC' (expected a git work tree)." >&2
  exit 4
fi
if [ -d "$CLONE/.git" ]; then
  echo "[wsl] syncing existing clone -> source HEAD ($CLONE)"
  git -C "$CLONE" fetch --depth 1 "file://$SRC" HEAD
  git -C "$CLONE" reset --hard FETCH_HEAD
  git -C "$CLONE" clean -fdx          # drop prior obj/bin/trx so the build is fresh
else
  echo "[wsl] cloning (shallow) $SRC -> $CLONE"
  rm -rf "$CLONE"
  git clone --depth 1 "file://$SRC" "$CLONE"
fi

cd "$CLONE"
echo "[wsl] HEAD=$(git rev-parse --short HEAD)  — verifying committed goldens on Linux"

# --- Restore + run the Tier-1 golden-checksum suite (verify mode) ------------
echo "[wsl] restore (--locked-mode) ..."
dotnet restore "$CSPROJ" --locked-mode
echo "[wsl] test (-c Release, verify mode) ..."
# No hardcoded test count: xUnit exits non-zero on ANY failure, which fails this script.
dotnet test "$CSPROJ" -c Release --no-restore --logger "trx;LogFileName=tier1-wsl.trx"

echo "[wsl] PASS — Tier-1 golden-checksum suite is GREEN on Linux (committed goldens verified)."
