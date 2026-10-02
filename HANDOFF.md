# HANDOFF — Project Chimera

_Updated 2026-10-01 17:30 · branch `master` · Phase 2 committed; ground-look workflow `wf_ea0485ce-7d1` running (see `git log -1`)_

## Where we stopped

The Unreal trial runs `docs/unreal-move/trial-checks/EXECUTION.md`. **Phases 0, 1 and 2 are done and committed**: every task passed
two independent Opus xhigh verifiers (results; code and rules). Phase 2 results and main-session rulings: EXECUTION.md §7.

- **Check (a) NativeAOT sim: core proven.** SimSession (A3) builds a match the way Godot does; `trial_1000` (1,000 units, 2,077
  frozen orders) has two new goldens (A4). The same 1,440 checksums come out of the .NET CLI, the C++ harness on `ChimeraSim.dll`
  (A5, A7), Godot on .NET 8.0.25 and on 10.0.12 (A8; plain Godot 4.6.3 hosts .NET 10, plan fact F7 was wrong), the full real
  MainScene replay (A14, measured) and **Unreal `-game` at normal pace, 24 fps, with forced hitches and with AI (A10)**. The real
  MainScene start state equals SimSession's, 6/6 (A8b, the D1 gate). AOT equals JIT on 37/37 sequences and 31 goldens (A6).
  Sim tests: T1 failed=0 at every task; no existing golden or AlgoVersion moved. DW-681 stays open (trial_1000 never triggers it).
- **Check (b) HUD:** calibration frozen at `bbfda161` (positives 5/5 incl. the new P6 vector AA model, negatives 8/8 per pair,
  after the T2 ruling). T4a chose the text route (hinting None, kerning on, fractional placement, PNG icons) and built SChimeraText,
  the keycap, icon and top-strip first cut. **D10 answered by Alec 2026-10-01: Slate's lighter text weight (5.5-31% less ink) is
  accepted** ("D10 looks good").
- **Check (c) terrain:** script director and S1/S1L/C1 (C4: fps-independent hashes, exact undo/redo), sim-grid parity with the C#
  ElevationGrid (C10), collision current after every stroke and a 79.5 MB soak (C5), ground material (C7), real OS mouse
  sculpting (C8). Terrain GPU 2.96 ms against the 3.0 ms Phase 4 bar (thin).
- Images sent to Alec: `a-A8-parity.png`, `c-C7-composite-phone.jpg`, `c-C8-mouse.jpg`, the D10 crop.

## In flight

- **Ground look pass (Alec 2026-10-01: "Definitely get the ground closer to Manor Lords level")**, workflow
  `tools/unreal-trial/workflows/ground-look.js` (run `wf_ea0485ce-7d1`): G0 gathers Manor Lords references (git-ignored, under
  `ChimeraTerrain/Out/refs/manor_lords/`) and CC0 texture candidates; G1 reworks textures, material and lighting, judged by an Opus
  art director for up to 4 rounds (good enough = overall ≥ 7, no criterion < 6, scatter out of scope), then the usual two verifiers.
  Each judged round writes `D:/Projects/Chimera-Unreal/TrialOut/checkpoints/G1-r<n>.json`; the main session sends Alec
  `ChimeraTerrain/Out/look/look_r<n>-phone.jpg` (has third-party reference pixels: never commit it) and commits at the end.
- Constraints on it: every C4-C8 bar must still pass and terrain GPU must stay ≤ 3.0 ms or be reported with both numbers.
- **Scatter (Alec approved 2026-10-01: "Go with what you think is best. Just make sure it's implemented well!")**: 3D grass, flowers,
  shrubs and trees that follow runtime edits in a packaged game, presentation only. Design workflow `wf_6722a0c4-e65` writes the
  plan of record `docs/unreal-move/trial-checks/plan-c-scatter.md` (research r8a-d, three designs, judged synthesis, critique).
  Rule from Alec: use Unreal's built-in system (PCG) where it works at runtime on our terrain; otherwise build our own.
- **Building foundations** (Alec 2026-10-01): brief `docs/unreal-move/briefs/building-foundations.md` (flat pad + soft skirt,
  "too steep" refusal, sculpting lifts the whole pad with the building). Map editor first, after scatter; in-match is separate.

## Next step

Phase 3 "Converge" (EXECUTION §1.2), not started: (a) A11 renderer, cameras, shots, verify, then a separate reviewer fills
`visual_check.json`; (b) T4b → T5 → T6 → T7 → T8 convergence with `tools/unreal-hud/hud_iterate.sh` (T4a left: top.clock pill
borders need split coverage; chip text G5 badness is exactly 1.0); (c) C9 unit layer and sculpt video, C11 packaged builds, after
the ground look settles. Workflow templates: `tools/unreal-trial/workflows/` (phase2.js pattern: per-task markers, main-session
commits from them). Carry: C12 must keep stale soak folders out (P6 gates every gate-config SOAK); A13 needs a clean republish.

## Waiting on Alec

- Nothing blocks. D2 (performance bar) is needed before Phase 4. Contingent only: D8 (Errant trial form), D9 (mouse injection).
  Carried over: optional RAM upgrade; DW-1029, HD Model triangle question, DW-1025, `cinderhand_thrall` colour.

## How to run and check it

- Sim tests: `UE_LOCK_TAG=x bash D:/Projects/Chimera-Unreal/ue_lock.sh dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj`
  (~2 min; LLM generation-timeout tests flake under load: rerun them alone). `python tools/sim-trial/trx_summary.py <dir>`.
- Trace parity: `python tools/sim-trial/compare_traces.py --set main|ai name=trace…` (writes compare.json and parity.png).
- Godot leg: `tools/sim-trial/run_godot_leg.ps1 -Variant main|ai -Runtime net8|host` (under the lock). Unreal sim run:
  `P/SimTrial/tools/run_sim.ps1` (takes the lock itself after preflight). HUD: `tools/unreal-hud/hud_iterate.sh <tag>`.
  Terrain: `ChimeraTerrain/Tools/run_terrain.ps1 -Script S1|C1|MOUSE …` and `parse_terrain.py`.
- Any Unreal build/run: through the lock, from Git Bash only, in the background (`bash -c '<cmd> > L 2>&1; echo EXIT=$? > L.exit'`)
  then `bash D:/Projects/Chimera-Unreal/wait_for.sh --file L.exit` in the foreground. Builds take 4-18 min under contention.
- NativeAOT publish needs `C:\Program Files (x86)\Microsoft Visual Studio\Installer` on PATH; `NAT/publish.ps1` exit 5 = DLL in use.

## Known issues

- Alec runs other Claude sessions on this PC: free RAM is often 3-6 GB; Windows logged memory pressure from cl.exe during builds.
- `ProjectChimera/Config/DefaultEngine.ini` holds an AndroidFileServer `SecurityToken` (local history only; mirrors scan it out).
- A context-warning hook reports a wrong token count (608k when the status line showed 273k); Alec: ignore it, don't hunt it.
- Godot: Terrain3D brush uses stale operation ints (Paint and Smooth are no-ops; r7), DW-681 frame writer, both unfiled.

## Key context

- Architecture of record (Unreal Spec, https://claude.ai/code/artifact/5b9ecf08-3568-4a8f-833a-f21c1112b395): NativeAOT sim
  library, thin C++ Unreal shell (no widget Blueprints), plain .NET match server. World look: Manor Lords style, Lumen off.
- `D:/Projects/Chimera-Unreal` (local git, no remote) holds ProjectChimera, ChimeraHud, ChimeraTerrain, `ue_lock.sh` v2 and
  `wait_for.sh`; text mirrors here: `tools/unreal-sim`, `tools/unreal-hud/ChimeraHud-src`, `tools/unreal-terrain`.
- PC: Ryzen 5 5600, RTX 3060 12 GB, 16 GB RAM. History: `docs/history/Snapshot-through-2026-09-21.md`.
