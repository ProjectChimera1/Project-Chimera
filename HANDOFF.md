# HANDOFF — Project Chimera

_Updated 2026-10-02 09:30 · branch `master` · last commit: the handoff commit after scatter S3 (see `git log -1`)_

## Where we stopped

The Unreal trial runs `docs/unreal-move/trial-checks/EXECUTION.md` (§7 Phase 0-1, §8 Phase 2 and after). **Phases 0-2 are done and
committed.** Since then: the terrain ground look pass (committed) and runtime scatter tasks S0-S4a-S2-S3 (committed). Every task
passed two independent Opus xhigh verifiers (results; code and rules). Stopped after S3 on purpose (Alec asked for a clean stop);
nothing is running and both trees are clean.

- **(a) NativeAOT sim:** one 1,440-tick checksum sequence for `trial_1000` from the .NET CLI, the C++ harness, Godot (.NET 8 and
  10; plain Godot 4.6.3 hosts .NET 10), the full MainScene replay and Unreal `-game` (normal, 24 fps, hitch, AI). MainScene start
  state = SimSession's, 6/6. Sim tests failed=0 throughout; no existing golden or AlgoVersion moved. DW-681 stays open.
- **(b) HUD:** calibration frozen (`bbfda161`, positives 5/5 incl. P6, negatives 8/8); text route chosen (T4a). D10 accepted by
  Alec: Slate's lighter text weight is fine.
- **(c) Terrain:** C4, C5, C7, C8, C10 passed. Ground look toward Manor Lords (Alec: floor "way better than Warcraft 3 Reforged",
  target Manor Lords): round 4 committed (R 23efc90f, U 1c2f459), art director 5.5/10, terrain GPU 2.907 ms (bar 3.0).
- **Scatter** (Alec: "Go with what you think is best. Just make sure it's implemented well!"): plan of record
  `docs/unreal-move/trial-checks/plan-c-scatter.md` (bespoke C++, not PCG). Committed: S0 (85fed6b7), S4a terrain change event
  (5370d043), S2 generator + 43 tests (0299c07b), S1 meshes + CC0 assets (930c09ae), S3 import + 65 materials (09357041); U commits
  920bc27, a1eba0d, e49e79a, 04f5ddb. Hand-off notes per task: `D:/Projects/Chimera-Unreal/TrialOut/checkpoints/{S1,S2,S3b,S4a}.json`.

## Next step

Run `tools/unreal-trial/workflows/scatter-build-s4.js` (S4 runtime scatter → S5 harness and determinism → S6 up to 3 look rounds
judged against Manor Lords). Watch with `tools/unreal-trial/checkpoints/wait_marker.sh` (background) and commit each passed task
with `checkpoints/ckpt.py <task>` by explicit path in both repos (README there). Send Alec each S6 `lookx_r<n>-phone.jpg`.
Then Phase 3 for the other checks (A11 renderer + reviewer `visual_check.json`; HUD T4b → T8 via `tools/unreal-hud/hud_iterate.sh`),
C9 (needs only S4a), C11 packaging (+S7). Building foundations (`docs/unreal-move/briefs/building-foundations.md`) after scatter.

## Waiting on Alec

1. **Editor asset licence rule** (`docs/unreal-move/trial-checks/research/r9-free-asset-routes.md`): recommended, ship only CC0,
   CC-BY 4.0 and our own content in the map editor until Epic answers whether Fab Standard content (all Megascans; Fab EULA
   6(b)(iii) bars "world- or level-editing tools ... that allow works to be exported") may be placed in maps that run only in
   our game. I can draft the email to legal@epicgames.com.
2. **Optional:** add the free CC-BY packs to his Fab library (KV Beech Tree 01, HighPoly Tree Model, European Forest
   Environment) for the tree and shrub bake-off; check Quixel Bridge / quixel.com for legacy Unreal-plan claims (different terms).
3. D2 (performance bar) before Phase 4. Contingent: D8, D9. Carried: RAM upgrade, DW-1029, DW-1025, `cinderhand_thrall` colour.

## How to run and check it

- Sim tests: `UE_LOCK_TAG=x bash D:/Projects/Chimera-Unreal/ue_lock.sh dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj`
  (~2 min; LLM generation-timeout tests flake under load). Parity: `python tools/sim-trial/compare_traces.py --set main|ai name=trace…`.
- Terrain: `ChimeraTerrain/Tools/run_terrain.ps1 -Script S1|C1|LOOK|MOUSE …`, `parse_terrain.py`, `look_measure.py`; scatter assets:
  `Tools/make_scatter_meshes.py`, `fetch_scatter_assets.py` (`--refresh-l0` after regenerating L0), commandlet
  `Scripts/make_scatter_assets.py` (from a deleted `Content/Terrain/Scatter`, one hold). HUD: `tools/unreal-hud/hud_iterate.sh <tag>`.
- Any Unreal build/run: through the lock, from Git Bash only, in the background (`bash -c '<cmd> > L 2>&1; echo EXIT=$? > L.exit'`)
  then `bash D:/Projects/Chimera-Unreal/wait_for.sh --file L.exit` in the foreground. Builds take 4-18 min under contention.
- NativeAOT publish needs `C:\Program Files (x86)\Microsoft Visual Studio\Installer` on PATH; `NAT/publish.ps1` exit 5 = DLL in use.

## Known issues

- Resuming a workflow whose lanes run in parallel re-runs agents live (the cached prefix breaks); start a fresh small workflow
  for follow-ups instead. Workflows resume only inside the session that started them.
- Terrain GPU margin is thin (2.907 of 3.0 ms); scatter cost is reported separately (S5/S8). Unit GPU cost is unmeasured until C9.
- Manor Lords reference images live only in `ChimeraTerrain/Out/refs/manor_lords/` (git-ignored, third-party): never commit them.
- A context-warning hook over-reports tokens (608k shown vs 273k real); Alec: ignore it.
- Free RAM is often 3-6 GB (other Claude sessions); `ProjectChimera/Config/DefaultEngine.ini` holds a SecurityToken (local only).
- Godot: Terrain3D brush stale operation ints (r7), DW-681 frame writer, both unfiled.

## Key context

- Architecture of record (Unreal Spec, https://claude.ai/code/artifact/5b9ecf08-3568-4a8f-833a-f21c1112b395): NativeAOT sim
  library, thin C++ Unreal shell (no widget Blueprints), plain .NET match server. World look: Manor Lords style, Lumen off.
- Terrain is sculpted only in the map editor / custom-game creation, never in a match (Alec, 2026-10-01).
- Research this session: `research/r8a-d` (scatter: PCG runtime, assets, perf, integration), `r9-free-asset-routes.md`.
- `D:/Projects/Chimera-Unreal` (local git, no remote): ProjectChimera, ChimeraHud, ChimeraTerrain, `ue_lock.sh`, `wait_for.sh`;
  mirrors here: `tools/unreal-sim`, `tools/unreal-hud/ChimeraHud-src`, `tools/unreal-terrain`.
- PC: Ryzen 5 5600, RTX 3060 12 GB, 16 GB RAM. History: `docs/history/Snapshot-through-2026-09-21.md`.
