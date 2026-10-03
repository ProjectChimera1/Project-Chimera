# HANDOFF — Project Chimera

_Updated 2026-10-03 10:40 · branch `master` · last commit: terrain C9 (see `git log -1`)_

## Where we stopped

The Unreal trial runs `docs/unreal-move/trial-checks/EXECUTION.md` (§7 Phase 0-1, §8 Phase 2 and after). **Phases 0-2 are done and
committed**, then the terrain ground look pass and scatter S0-S4a-S2-S3-S4-S5-S6 (all committed; scatter's in-editor work is complete). Every task passed two independent Opus
xhigh verifiers (results; code and rules).

- **(a) NativeAOT sim:** one 1,440-tick checksum sequence for `trial_1000` from the .NET CLI, the C++ harness, Godot (.NET 8 and
  10), the full MainScene replay and Unreal `-game`. Sim tests failed=0 throughout; no golden or AlgoVersion moved. DW-681 stays open.
- **(b) HUD:** calibration frozen (`bbfda161`, positives 5/5, negatives 8/8); text route chosen (T4a); D10 accepted by Alec; **T4b top
  strip PASS 0/8 on both pairs** (7 text regions sit exactly at the G5 limit from D10's lighter weight: no margin left there).
- **(c) Terrain:** C4, C5, C7, C8, C10 passed. Ground look round 4 committed (art director 5.5/10 vs Manor Lords, terrain GPU
  2.907 ms of 3.0).
- **Scatter** (plan of record `docs/unreal-move/trial-checks/plan-c-scatter.md`, bespoke C++): **S4 runtime, S5 harness and
  determinism, S6 look rounds committed** (2026-10-02/03), each after main-session rulings in EXECUTION §8 ("S4 rulings", "S5 record",
  "S6 ruling"). Every SX gate passes (SX1-SX10, SX18). Look (reported, D4): rounds 1-3 and the shipped defaults all score rts80 6.0 vs
  Manor Lords (target 7.5); the limit is the broadleaf tree (CC0 scan reads as savanna and costs 3.7-4.7 ms; L0 crowns read as stylised),
  then distance haze and shrubs. Shipped defaults: L0, grass ends 70/45 m, `config_fnv` 0x2e5eca117dd40fb2; one unmeasured smoke
  `scatter_gpu_ms` 1.44, 8,043 drawn at rts80. Markers: `D:/Projects/Chimera-Unreal/TrialOut/checkpoints/` (`S4`, `S5`, `S6`,
  `S6-r1..r3`, `S6-final`; `*-run1.json` are earlier runs). Composites sent to Alec: `ChimeraTerrain/Out/look/lookx_r1..r3|final-phone.jpg`.

## Next step

**Phase 3 (converge) is running** in two workflows started 08:55 on 2026-10-03 in the session that committed S6:
`tools/unreal-trial/workflows/phase3-b.js` (HUD T4b → T5 → T6 → T7 → T8; **T4b and T5 passed and are committed**) and `phase3-ac.js`
(A11 renderer + separate reviewer's `visual_check.json`, beside C9 units and video (**C9 passed, committed**) → C11 packages → S7 SX16). If that session is gone,
the runs are gone too: start fresh small workflows for the unfinished tasks from those scripts' task notes (`TASKS3B`, `TASKS3`).
Watch markers with `checkpoints/wait_marker.sh`, commit each passed task with `checkpoints/ckpt.py <task>` by explicit path (HUD: /
SimTrial: / Terrain: prefixes). Send Alec: A11 contact sheet + verify numbers, `c-C9-sculpt.gif`, T8 scorecard previews.
Then Phase 4 (A12, B T8 `hud_cost`, C12 + S8, all under `LOCK --measure`; needs D2), then Phase 5 (A13, T10, C13 + S9, C14).
**Scatter look follow-up** (when Alec adds the CC-BY tree packs): a tree-only bake-off (CC-BY packs vs L0 at rts80/oblique, trees
layer GPU row beside it) plus aerial perspective (distance haze) in TerrainLighting, judged by the art director as in S6.
Building foundations (`docs/unreal-move/briefs/building-foundations.md`) after scatter.

## Waiting on Alec

1. **Epic's reply on the editor asset licence.** Alec sent the question to legal@epicgames.com from projectchimeraue5@gmail.com on
   2026-10-02 (text: `docs/unreal-move/trial-checks/research/r9-epic-licence-email.md`). Until Epic answers, the map editor ships only
   CC0, CC-BY 4.0 and project-original content. Paragon is now Fab Standard content (r9 §5 flag 11), so the answer gates it too.
2. **Recommended:** add the free CC-BY packs to his Fab library (KV Beech Tree 01, HighPoly Tree Model, European Forest Environment):
   the tree is what holds scatter at 6/10. Optional: check Quixel Bridge / quixel.com and the old Marketplace for legacy UE-Only claims.
3. D2 (performance bar) before Phase 4. Contingent: D8, D9. Carried: RAM upgrade, DW-1029, DW-1025, `cinderhand_thrall` colour.

## How to run and check it

- Sim tests: `UE_LOCK_TAG=x bash D:/Projects/Chimera-Unreal/ue_lock.sh dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj`
  (~2 min; LLM generation-timeout tests flake under load). Parity: `python tools/sim-trial/compare_traces.py --set main|ai name=trace…`.
- Terrain: `ChimeraTerrain/Tools/run_terrain.ps1 -Script S1|C1|LOOK|MOUSE|SXSMOKE …`, `parse_terrain.py` (`--scatter`, `--s1l … --img-ref`,
  `--teardown`), `look_measure.py`; scatter assets: `Tools/make_scatter_meshes.py`, `fetch_scatter_assets.py`, commandlet
  `Scripts/make_scatter_assets.py` (from a deleted `Content/Terrain/Scatter`, one hold); unit content (git-ignored): `cp -r` of
  `crucible_mortar` + `Scripts/fix_unit_usage.py` (EXECUTION §8 'C9 record'). HUD: `tools/unreal-hud/hud_iterate.sh <tag>`.
- Any Unreal build/run: through the lock, from Git Bash only, in the background (`bash -c '<cmd> > L 2>&1; echo EXIT=$? > L.exit'`)
  then `bash D:/Projects/Chimera-Unreal/wait_for.sh --file L.exit` in the foreground. Builds take 4-18 min under contention.
- NativeAOT publish needs `C:\Program Files (x86)\Microsoft Visual Studio\Installer` on PATH; `NAT/publish.ps1` exit 5 = DLL in use.

## In flight / known issues

- A workflow task that reports `blocked` ends the run without re-verifying; the main session rules, then starts a fresh small
  workflow that verifies from the saved report (patterns: `scatter-build-s4v.js`, `-s5v`, `-s6v`). Resuming parallel-lane workflows re-runs agents.
- `wait_marker.sh` keys markers on name + modification time (2026-10-03), so a rewritten `<task>.json` is caught. Implementers
  sometimes write their own note into `checkpoints/` (C9 did): rename it (`C9-implnote.json`) so the real marker stands out.
- **Scatter performance (reported until S8):** S6's grass ends cut drawn instances to 8,043 at rts80 (cap 24k) and scatter GPU to about
  1.44 ms (one unmeasured smoke); the LOD0 triangle bound is still 3.57 M vs 1.5 M, and S5's cross-build smoke showed GT p99 +2.2 ms /
  RT p99 +4.3 ms vs SX11's 1.5 ms. S8's interleaved reps decide.
- Check (c)'s committed evidence cap is 12 MB (`evidence.py` `CHECK_CAP`, EXECUTION §2.3); about 2 MB is free.
- Editor `-game` still compiles shaders mid-run; scatter image pairs are shot frozen (`temporal_freeze`), compared within one window.
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
