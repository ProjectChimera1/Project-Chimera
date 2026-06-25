#!/usr/bin/env bash
# =============================================================================
# wsl-dotnet-setup.sh — idempotent .NET 8 SDK installer for WSL/Ubuntu.
#
# Story 1.10c (AR-37) prerequisite: the cross-platform Windows<->Linux golden-
# checksum gate runs the Tier-1 suite inside WSL, which needs a .NET 8 SDK that
# satisfies the repo-root global.json (pins 8.0.419, rollForward: latestFeature).
#
# WHY dotnet-install.sh (not apt):
#   * Ubuntu 24.04's built-in feed ships dotnet-sdk-8.0 at feature band 8.0.1xx
#     (probed: 8.0.128) — BELOW 8.0.419. rollForward:latestFeature rolls UP within
#     8.0, never DOWN, so apt's SDK FAILS global.json resolution. (#1 time-sink.)
#   * apt also needs sudo (probed: password required) — non-interactive-hostile.
#   * dotnet-install.sh installs to ~/.dotnet with NO sudo and always pulls the
#     latest 8.0.x patch (>= 8.0.419), so it satisfies global.json by construction.
#
# Idempotent: re-running is safe. If a satisfying SDK is already present it does
# nothing but re-print the version. Adds ~/.dotnet to PATH in ~/.bashrc once.
#
# Run from WSL:  bash /mnt/d/Projects/Project_Chimera/godot/tools/wsl-dotnet-setup.sh
# =============================================================================
set -euo pipefail

DOTNET_DIR="$HOME/.dotnet"

# Resolve a usable dotnet: prefer our managed ~/.dotnet, else any on PATH.
resolve_dotnet() {
  if [ -x "$DOTNET_DIR/dotnet" ]; then echo "$DOTNET_DIR/dotnet"; return 0; fi
  if command -v dotnet >/dev/null 2>&1; then command -v dotnet; return 0; fi
  return 1
}

# True if the given dotnet has an SDK satisfying global.json (8.0.419, rollForward latestFeature):
# an 8.0 SDK at feature band >= 4 (8.0.4xx..8.0.9xx), OR any newer 8.x (8.1+), OR any major >= 9.
# Deliberately REJECTS 8.0.0xx..8.0.3xx (e.g. Ubuntu's apt 8.0.128) — those fail SDK resolution.
sdk_satisfies_globaljson() {
  local bin="$1"
  [ -x "$bin" ] || return 1
  "$bin" --list-sdks 2>/dev/null | grep -Eq '^8\.0\.[4-9][0-9][0-9]|^8\.[1-9]|^(9|[1-9][0-9]+)\.'
}

# --- Install if needed -------------------------------------------------------
if bin="$(resolve_dotnet)" && sdk_satisfies_globaljson "$bin"; then
  echo "[wsl-dotnet-setup] An SDK satisfying global.json is already installed: $("$bin" --version)"
else
  echo "[wsl-dotnet-setup] No satisfying .NET SDK found. Installing .NET 8 (channel 8.0) to $DOTNET_DIR (no sudo)..."
  tmp="$(mktemp)"
  if command -v curl >/dev/null 2>&1; then
    curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$tmp"
  elif command -v wget >/dev/null 2>&1; then
    wget -qO "$tmp" https://dot.net/v1/dotnet-install.sh
  else
    echo "[wsl-dotnet-setup] ERROR: neither curl nor wget is available to fetch dotnet-install.sh." >&2
    exit 1
  fi
  chmod +x "$tmp"
  # --channel 8.0 always resolves to the latest 8.0.x patch (>= 8.0.419 today).
  "$tmp" --channel 8.0 --install-dir "$DOTNET_DIR"
  rm -f "$tmp"
fi

# --- Persist PATH for future shells -----------------------------------------
# IMPORTANT: `wsl -- bash -lc '...'` is a NON-INTERACTIVE LOGIN shell. Ubuntu's ~/.bashrc
# returns early for non-interactive shells, so an export at the end of ~/.bashrc is NEVER
# reached by `bash -lc`. ~/.profile (read by login shells, no interactive guard) is. So we
# persist to BOTH: ~/.profile makes `bash -lc 'dotnet ...'` work; ~/.bashrc covers an
# interactive `wsl` session. (The cross-platform check script ALSO exports PATH explicitly,
# so the automated gate never depends on either file.)
MARKER_OPEN="# >>> chimera dotnet (Story 1.10c) >>>"
MARKER_CLOSE="# <<< chimera dotnet (Story 1.10c) <<<"
persist_path_to() {
  local rc="$1"
  if grep -qF "$MARKER_OPEN" "$rc" 2>/dev/null; then
    echo "[wsl-dotnet-setup] $rc already exports ~/.dotnet on PATH"
    return 0
  fi
  {
    echo ""
    echo "$MARKER_OPEN"
    echo 'export DOTNET_ROOT="$HOME/.dotnet"'
    echo 'export PATH="$HOME/.dotnet:$PATH"'
    echo 'export DOTNET_CLI_TELEMETRY_OPTOUT=1   # AR-41: no telemetry/beacon'
    echo "$MARKER_CLOSE"
  } >> "$rc"
  echo "[wsl-dotnet-setup] Added ~/.dotnet to PATH in $rc"
}
persist_path_to "$HOME/.profile"
persist_path_to "$HOME/.bashrc"

# --- Verify in THIS shell ----------------------------------------------------
export DOTNET_ROOT="$DOTNET_DIR"
export PATH="$DOTNET_DIR:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
echo "[wsl-dotnet-setup] dotnet --version = $(dotnet --version)"
echo "[wsl-dotnet-setup] installed SDKs:"
dotnet --list-sdks
echo "[wsl-dotnet-setup] DONE"
