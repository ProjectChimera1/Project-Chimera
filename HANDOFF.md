# HANDOFF — Project Chimera

_Updated 2026-10-01 · branch `master` · last commit: the trial plans of record (see `git log -1`)_

## Where we stopped

The Unreal trial's three checks are planned and the plans are committed; execution has started (ultracode workflows).
No Godot engine code, tests or goldens have been touched yet.

- **Plans of record** in `docs/unreal-move/trial-checks/`: `plan-a-native-sim.md` (NativeAOT sim, 1,000 units, checksums vs Godot),
  `plan-b-match-hud.md` (Round 2 Match HUD board 3.1a in C++ Slate, scored against the mockup), `plan-c-runtime-terrain.md`
  (RealtimeMeshComponent terrain sculpt + paint in the running game), and `EXECUTION.md` (one schedule, the lock rules, the evidence
  policy, 23 cross-plan conflicts resolved, the trial done-gate). Each plan went through two adversarial critiques (57-64 issues each).
- **Research** in `docs/unreal-move/trial-checks/research/` (r1-r7) plus `proto-a/` (prototypes: the STJ source-gen migration diffs,
  the `trial_1000` scenario generator, JIT-vs-AOT spike outputs). Key facts found: the sim's only NativeAOT blocker is reflection
  System.Text.Json (65 sites); JIT, Release and NativeAOT already give byte-identical 1,000-unit checksums in a scratch run; the real
  test baseline is 7,124 tests (7,122 pass, 1 stale BMAD guard fails, 1 skipped), not 6,392.
- **Decisions D1-D5 (EXECUTION.md §5)** were adopted on their recommendations on 2026-10-01 and sent to Alec to override if he wants:
  D1 "same checksum as Godot" = SimSession run in Godot's own runtime + a gating MainScene start-state probe (full MainScene replay is
  measured, not gated, because DW-681 likely diverges it); D2 the fps bars; D3 terrain scope = edit in the running (packaged) game,
  sim reads heights at F5; D4 looks gate only (b); D5 Unreal projects keep local git + text mirrors into this repo.
- **Machine fixes:** NativeAOT publish needs `C:\Program Files (x86)\Microsoft Visual Studio\Installer` on PATH (VS 2026 vcvarsall
  calls `vswhere` by bare name). `D:/Projects/Chimera-Unreal` is now a local git repo (no remote) with `ue_lock.sh`, the global
  one-heavy-Unreal-job-at-a-time lock (`bash D:/Projects/Chimera-Unreal/ue_lock.sh <cmd>`; Git Bash only, from PowerShell `bash` is WSL).

## Next step

Execute `EXECUTION.md` phase by phase: Phase 0 shared pieces (lock v2, ChimeraHud/ChimeraTerrain scaffolds, `tools/unreal-trial` kit),
then Phase 1 first light (X1 NativeAOT inside Unreal, C1/C3 RMC on 5.8.3, T3 Slate capture 1:1, A0-A2 baseline + STJ migration,
B T0/T1 references + comparator). Only the main session commits; prefixes `SimTrial:`, `HUD:`, `Terrain:`, `Trial:`.

## Waiting on Alec

- D6 (check b, after T0): what "pixel-faithful" means for text; he gets `lcd_vs_gray_text_4x.png`. Recommendation: grayscale re-render.
- Contingent only: D7 (NativeAOT fails in Unreal), D8 (Errant trial form), D9 (mouse injection blocked), D10 (text residual).
- Carried over: optional RAM upgrade; DW-1029 (Flux dev licence), HD Model triangle question, DW-1025, `cinderhand_thrall` colour.

## How to run and check it

- Godot build `dotnet build godot/godot.sln`; sim tests `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj`
  (measured baseline 7,124 total, 7,122 pass, 1 fail = the stale `ToolingGateGuardTests.BurnDownDispatcher` guard, 1 skipped).
- Unreal: build `Build.bat <Target> Win64 Development -Project=<uproject> -WaitMutex` under the lock; look-test tools in
  `tools/unreal-looktest/` (gotchas PLAN_DELTA.md D7-D9). Unreal MCP answers only while the editor runs with `-ModelContextProtocolStartServer`.

## In flight / known issues

- Godot terrain: the Terrain3D brush uses stale operation ints, so Paint and Smooth are no-ops (r7; root cause of backlog 10-16, not filed yet).
- Godot `PathRequestSystem` per-frame Move→Stop writer (DW-681) is outside the sim; it is why full-MainScene checksum parity is not gated.
- Godot asset work (Epic 16) stays paused: the roster moves to Unreal; Tripo remains the asset route.

## Key context

- Architecture of record (Unreal Spec, https://claude.ai/code/artifact/5b9ecf08-3568-4a8f-833a-f21c1112b395): the engine-free sim
  compiles with NativeAOT into a native library; Unreal is a thin C++ shell (no widget Blueprints); the match server is plain .NET.
  World look decided 2026-10-01: Manor-Lords-style near-photoreal, Lumen off. UI: build the approved Round 2 set (`docs/ui-redesign/`).
- PC: Ryzen 5 5600 (6 cores), RTX 3060 12 GB, 16 GB RAM (often under 3 GB free), D: ~650 GB free.
- History: `docs/history/Snapshot-through-2026-09-21.md`. Ledger: `_bmad-output/implementation-artifacts/deferred-work.md`.
