# HANDOFF — Project Chimera

_Updated 2026-10-01 · branch `master` · Phase 2 workflow `wf_0d1c858e-bd3` running (see `git log -1`)_

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

**Phase 2 is running** as workflow `wf_0d1c858e-bd3` (script `tools/unreal-trial/workflows/phase2.js`: implement → two Opus xhigh
verifiers per task, one re-running results and one reviewing code and rules → up to 2 Opus fix rounds; agents never commit). Each
finished task writes `D:/Projects/Chimera-Unreal/TrialOut/checkpoints/<task>.json`; the main session commits verified tasks by path
from those markers, rewrites this file and pushes.
- (a) A3 SimSession → A4 scenario/CLI/goldens → A5 NativeAOT library + C ABI → {A6 AOT goldens, A7 C++ harness, A10 Unreal module}
  beside the Godot chain A8 → A8b MainScene probe (gating) → A14 (measured). Send `a-A8-parity.png`.
- (b) T2 calibration (positives 4/4 since P2 is dropped) → T4a fonts/text/icon routes → T2r recalibration only if T4a picks a
  hinting other than None.
- (c) C4 director + S1/S1L/C1 → C5 collision + soak → C7 ground material (send composite) → C8 mouse; C10 sim-grid parity beside.
- **Progress (passed both verifiers and committed):** A3 (SimSession, PreTickHashes, WorldDigest, OrderScript; T1 7147/7146/0/1, +11 tests; CHM0001 394→409, CHM0005 280→284, all attributed); A4 (trial_1000: units=1000 orders=2077 verdict=0, ticks 1/60/300/1440 = F19, body byte-identical to the prototype; two new goldens + meta + trailer sidecars; compare_traces.py with --final); T2b (HUD calibration frozen after the main-session ruling: positives 5/5 incl. P6 vector AA model, negatives 8/8 per pair, thresholds sha256 bc82e341…); C4 (terrain director, S1/S1L/C1; fps-independent hashes, undo/redo exact, P7 and P11 pass); C10 (C# ElevationGrid from the edited height.r32 equals the C++ side: sim_grid_fnv 0x53a3de06, 16/16 probes); A5 (NativeAOT ChimeraSim.dll: 21 exports == header == allow-list, 0 IL warnings, ILCompiler pinned [8.0.25]; publish exit 5 = DLL in use); A7 (C++ harness: cycles=3 identical, selftests pass, sha 3/3, cpp and cpp_ai 1440/1440 with digests equal to the CLI); T4a (HUD text route: hinting None, kern on, frac, PNG icons, default coverage, so no recalibration; D10 raised); A8 (Godot leg 1440/1440 on main and ai, on .NET 8.0.25 pinned AND on .NET 10.0.12, which plain Godot 4.6.3 actually hosts: plan fact F7 was wrong; parity image sent); A6 (AOT golden replay: jit_equal=37/37 golden_equal=31/31 trial 1440/1440 main and ai, unknown_effect=fail_closed; 4 goldens excluded because their scenarios use xUnit, listed in GOLDENS.md); **A10 (NativeAOT sim inside Unreal -game: main, 24 fps, hitch and ai all 1440/1440 against the goldens; error path and selftests pass in UE)**; **A8b (gating: the real MainScene start state equals SimSession's, probe_match=6/6, on .NET 8 and 10)**; C5 (terrain collision P5 correct at all 4 checkpoints in 4/4 runs; soak0 growth 79.5 MB after GC; sparse undo).
- **Carry into Phase 3 (terrain):** add 30-frame settles before S1's pre_last2, undo and redo shots before C11/C13 (P7 margin is thin: worst16 0.0061-0.0076 vs 0.0078 bar; 0.0029 with settles); C12 must keep stale soak folders out (P6 now gates every gate-config SOAK).
- If this session dies mid-run: resume with `Workflow({scriptPath: <phase2.js copy in the session dir>, resumeFromRunId:
  "wf_0d1c858e-bd3"})` from the same session, or read the markers and re-run the unfinished tasks from a new one.

## Waiting on Alec

- **D10 (check b, raised by T4a):** Slate text is 5.5-31% lighter in ink than the grayscale mockup on all 12 top-strip runs, and no engine setting changes it. Recommended: accept for the trial (layout, size and colour match). Alternative: one global coverage boost in SChimeraText plus a recalibration. Sent with `b-T4a-chosen-clock-crop.png`. Does not block Phase 2.
- Nothing else blocks Phase 2. D6 answered 2026-10-01: grayscale re-render ("the text looks exactly the same, so it's a go").
- Contingent only: D8 (Errant trial form), D9 (mouse injection blocked), D10 (text residual). D2 (performance bar) is needed before
  Phase 4. Carried over: optional RAM upgrade; DW-1029, HD Model triangle question, DW-1025, `cinderhand_thrall` colour.

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
