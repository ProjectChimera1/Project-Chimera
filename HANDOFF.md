# HANDOFF — Project Chimera

_Updated 2026-10-02 19:00 · branch `master` · last commit: scatter S4 (see `git log -1`)_

## Where we stopped

The Unreal trial runs `docs/unreal-move/trial-checks/EXECUTION.md` (§7 Phase 0-1, §8 Phase 2 and after). **Phases 0-2 are done and
committed**, then the terrain ground look pass and scatter S0-S4a-S2-S3-S4 (all committed). Every task passed two independent Opus
xhigh verifiers (results; code and rules).

- **(a) NativeAOT sim:** one 1,440-tick checksum sequence for `trial_1000` from the .NET CLI, the C++ harness, Godot (.NET 8 and
  10), the full MainScene replay and Unreal `-game`. Sim tests failed=0 throughout; no golden or AlgoVersion moved. DW-681 stays open.
- **(b) HUD:** calibration frozen (`bbfda161`, positives 5/5, negatives 8/8); text route chosen (T4a); D10 accepted by Alec.
- **(c) Terrain:** C4, C5, C7, C8, C10 passed. Ground look round 4 committed (art director 5.5/10 vs Manor Lords, terrain GPU
  2.907 ms of 3.0).
- **Scatter** (plan of record `docs/unreal-move/trial-checks/plan-c-scatter.md`, bespoke C++): **S4 runtime scatter committed
  2026-10-02** after two main-session rulings (EXECUTION §8 "S4 rulings": image bars use `s1_s4a` via `--img-ref`, hashes stay on
  `s1_a`; per-component SX8 proxy categories). SXSMOKE: 243k instances read back exactly, live = reference, 70/70 tests. First image of
  scatter in Unreal: `evidence/c/c-S4-sxsmoke-shown-hidden.jpg`. Hand-off notes: `D:/Projects/Chimera-Unreal/TrialOut/checkpoints/`
  (`S4.json` = run 2 pass; `S4-run1.json`; full round-2 report `TrialOut/c_s4/r2/s4_r2_report.json`).

## Next step

**S5 → S6 are running** in workflow `wf_c7e98308-d2d` (`tools/unreal-trial/workflows/scatter-build-s4v.js`), started 18:58 in the
session that committed S4. If that session is gone, the run is gone too (workflows resume only in their own session): start a fresh
small workflow for S5 then S6 from `scatter-build-s4v.js`'s task notes (drop the S4 verify step). Watch markers with
`tools/unreal-trial/checkpoints/wait_marker.sh` (background) and commit each passed task with `checkpoints/ckpt.py <task>` by explicit
path in both repos (README there). Send Alec each S6 `lookx_r<n>-phone.jpg`.
Then Phase 3 for the other checks (A11 renderer + reviewer `visual_check.json`; HUD T4b → T8 via `tools/unreal-hud/hud_iterate.sh`),
C9 (needs only S4a), C11 packaging (+S7). Building foundations (`docs/unreal-move/briefs/building-foundations.md`) after scatter.

## Waiting on Alec

1. **Epic's reply on the editor asset licence.** Alec sent the question to legal@epicgames.com from projectchimeraue5@gmail.com on
   2026-10-02 (text: `docs/unreal-move/trial-checks/research/r9-epic-licence-email.md`). Until Epic answers, the map editor ships only
   CC0, CC-BY 4.0 and project-original content. Paragon is now Fab Standard content (r9 §5 flag 11), so the answer gates it too.
2. **Optional:** add the free CC-BY packs to his Fab library (KV Beech Tree 01, HighPoly Tree Model, European Forest Environment)
   for the tree bake-off; check Quixel Bridge / quixel.com and the old Marketplace for legacy UE-Only claims (incl. Paragon).
3. D2 (performance bar) before Phase 4. Contingent: D8, D9. Carried: RAM upgrade, DW-1029, DW-1025, `cinderhand_thrall` colour.

## How to run and check it

- Sim tests: `UE_LOCK_TAG=x bash D:/Projects/Chimera-Unreal/ue_lock.sh dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj`
  (~2 min; LLM generation-timeout tests flake under load). Parity: `python tools/sim-trial/compare_traces.py --set main|ai name=trace…`.
- Terrain: `ChimeraTerrain/Tools/run_terrain.ps1 -Script S1|C1|LOOK|MOUSE|SXSMOKE …`, `parse_terrain.py` (`--scatter`, `--s1l … --img-ref`,
  `--teardown`), `look_measure.py`; scatter assets: `Tools/make_scatter_meshes.py`, `fetch_scatter_assets.py`, commandlet
  `Scripts/make_scatter_assets.py` (from a deleted `Content/Terrain/Scatter`, one hold). HUD: `tools/unreal-hud/hud_iterate.sh <tag>`.
- Any Unreal build/run: through the lock, from Git Bash only, in the background (`bash -c '<cmd> > L 2>&1; echo EXIT=$? > L.exit'`)
  then `bash D:/Projects/Chimera-Unreal/wait_for.sh --file L.exit` in the foreground. Builds take 4-18 min under contention.
- NativeAOT publish needs `C:\Program Files (x86)\Microsoft Visual Studio\Installer` on PATH; `NAT/publish.ps1` exit 5 = DLL in use.

## In flight / known issues

- A workflow task that reports `blocked` ends the run without re-verifying; the main session rules, then starts a fresh small
  workflow that verifies from the saved report (pattern: `scatter-build-s4v.js`). Resuming parallel-lane workflows re-runs agents.
- `wait_marker.sh` tracks marker names: before re-running a task, rename its old marker (as `S4-run1.json`) and drop it from
  `.seen_markers.txt`, or the new marker is missed.
- Editor `-game` still compiles shaders mid-run (73 editor-compile proxy recreates in SXSMOKE); S5 waits for compile idle before shots.
- Terrain GPU margin is thin (2.907 of 3.0 ms); scatter cost is reported separately (S5/S8). Unit GPU cost unmeasured until C9.
- Manor Lords reference images live only in `ChimeraTerrain/Out/refs/manor_lords/` (git-ignored, third-party): never commit them.
- A context-warning hook over-reports tokens; Alec: ignore it. Free RAM is often 3-6 GB (other sessions).
- `ProjectChimera/Config/DefaultEngine.ini` holds a SecurityToken (local only). Godot: Terrain3D brush stale operation ints (r7),
  DW-681 frame writer, both unfiled.

## Key context

- Architecture of record (Unreal Spec, https://claude.ai/code/artifact/5b9ecf08-3568-4a8f-833a-f21c1112b395): NativeAOT sim
  library, thin C++ Unreal shell (no widget Blueprints), plain .NET match server. World look: Manor Lords style, Lumen off.
- Terrain is sculpted only in the map editor / custom-game creation, never in a match (Alec, 2026-10-01).
- Asset routes and licences: `research/r9-free-asset-routes.md` (Fab EULA 6(b)(iii) bars Standard content in level-editing tools).
- `D:/Projects/Chimera-Unreal` (local git, no remote): ProjectChimera, ChimeraHud, ChimeraTerrain, `ue_lock.sh`, `wait_for.sh`;
  mirrors here: `tools/unreal-sim`, `tools/unreal-hud/ChimeraHud-src`, `tools/unreal-terrain`.
- PC: Ryzen 5 5600, RTX 3060 12 GB, 16 GB RAM. History: `docs/history/Snapshot-through-2026-09-21.md`.
