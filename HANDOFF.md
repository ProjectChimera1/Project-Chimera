# HANDOFF — Project Chimera

_Updated 2026-10-01 09:00 · branch `master` · last commit: the Phase 1 checkpoint (see `git log -1`)_

## Where we stopped

The Unreal trial is executing `docs/unreal-move/trial-checks/EXECUTION.md`. **Phases 0 and 1 are done and committed**; every task
passed an independent Opus verification (results and main-session decisions: EXECUTION.md §7).

- **Check (a) NativeAOT sim:** sim content loading is source-generated System.Text.Json (A1; IL2026/IL3050 62/65 → 0, now
  release-gated; no golden or AlgoVersion moved); `MixUnknownEffect` fails closed under AOT (A2); a NativeAOT DLL runs inside
  `UnrealEditor -game` (X1: throw/catch, NRE, 256 MB + GC, threads all pass, so decision D7 is not triggered); 18 static unit meshes
  imported for ISMs with `P/SimTrial/unit_meshes.json` (A9). Gate: Sim.Tests **7136 total, 7135 passed, 0 failed, 1 skipped**.
- **Check (b) HUD:** grayscale mockup references + 13 controls per pair (T0), the 38-region comparator with self-tests (T1), and the
  ChimeraHud project whose in-game 1920x1080 capture equals the backdrop byte for byte (T3). No HUD panels are built yet.
- **Check (c) terrain:** ChimeraTerrain with vendored RealtimeMeshComponent `b8669a0` (+ collision patch) builds on 5.8.3 (C1),
  data core with 23 automation tests (C2), chunk renderer passes gate G1 in `-game` (C3). RMC #303 reproduces on the 3060 (Static
  draw invisible), so only the Dynamic path exists. CC0 ground textures fetched (C6).
- **Decisions D1-D5 confirmed by Alec 2026-10-01** ("those sound like good decisions"; D1 explained: Unreal must equal the sim run
  inside Godot on every tick; full-MainScene parity is measured, not gated, because of DW-681). Main-session decisions: B's positive
  control P2 (kerning off) dropped; Godot server smoke stays on `map_02_iron_crossing`.
- **Infrastructure:** `D:/Projects/Chimera-Unreal` (local git, no remote) holds ProjectChimera, ChimeraHud, ChimeraTerrain,
  `ue_lock.sh` v2 (one heavy job at a time, re-entrant, waits for 2.5 GB free RAM up to 20 min, hold log `ue_lock.log`) and
  `wait_for.sh`. Text mirrors in this repo: `tools/unreal-sim`, `tools/unreal-hud/ChimeraHud-src`, `tools/unreal-terrain`.
  Shared kit `tools/unreal-trial` (preflight, logscan, secret_scan, evidence). Workflow templates: `tools/unreal-trial/workflows/`.

## Next step

Phase 2 (EXECUTION.md §1.2): one workflow on the `tools/unreal-trial/workflows/phase0-1.js` pattern (implement → Opus xhigh
verify → up to 2 Opus fix rounds; agents never commit; the main session commits per check by path, rewrites this file, pushes).
- (a) A3 SimSession (opus) → A4 scenario/CLI/goldens → A5 NativeAOT library + C ABI → {A6 AOT goldens, A7 C++ harness, A8 Godot
  leg, A8b MainScene probe} → A10 Unreal module (stage, build, main/24fps/hitch/ai runs) → A14 (measured). Send `a-A8-parity.png`.
- (b) T2 calibration (needs D6; Alec has the 4x image as of 2026-10-01, recommendation = grayscale) → T4a fonts/text/icon routes.
- (c) C4 director + S1/S1L/C1 → C5 collision + soak → C7 ground material (send composite) → C8 mouse → C10 sim-grid parity.

## Waiting on Alec

- D6 (check b): confirm the HUD is scored against the grayscale re-render (he was sent `lcd_vs_gray_text_4x.png`). Blocks T2 only.
- Contingent only: D8 (Errant trial form), D9 (mouse injection blocked), D10 (text residual). Carried over: optional RAM upgrade;
  DW-1029, HD Model triangle question, DW-1025, `cinderhand_thrall` colour.

## How to run and check it

- Sim tests: `UE_LOCK_TAG=x bash D:/Projects/Chimera-Unreal/ue_lock.sh dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj`
  (~2 min; LLM generation-timeout tests flake under load: rerun them alone). `python tools/sim-trial/trx_summary.py <dir>`.
- Any Unreal build/run: through the lock, from Git Bash only, in the background (`bash -c '<cmd> > L 2>&1; echo EXIT=$? > L.exit'`)
  then `bash D:/Projects/Chimera-Unreal/wait_for.sh --file L.exit` in the foreground. Builds take 4-18 min under contention.
- NativeAOT publish needs `C:\Program Files (x86)\Microsoft Visual Studio\Installer` on PATH.

## In flight / known issues

- Alec runs other Claude sessions on this PC: free RAM is often 3-6 GB; Windows logged memory pressure from cl.exe during builds.
- `ProjectChimera/Config/DefaultEngine.ini` holds an AndroidFileServer `SecurityToken` (local history only; mirrors scan it out).
- Godot: Terrain3D brush uses stale operation ints (Paint and Smooth are no-ops; r7), DW-681 frame writer, both unfiled.

## Key context

- Architecture of record (Unreal Spec, https://claude.ai/code/artifact/5b9ecf08-3568-4a8f-833a-f21c1112b395): NativeAOT sim
  library, thin C++ Unreal shell (no widget Blueprints), plain .NET match server. World look: Manor Lords style, Lumen off.
- PC: Ryzen 5 5600, RTX 3060 12 GB, 16 GB RAM. History: `docs/history/Snapshot-through-2026-09-21.md`.
