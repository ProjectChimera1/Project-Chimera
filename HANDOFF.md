# HANDOFF — Project Chimera

_Updated 2026-10-01 00:45 · branch `master` · last commit: the look-test checkpoint (see `git log -1`)_

## Where we stopped

Session 14 (2026-09-30 → 10-01) took the Unreal trial from "installed" to "look test delivered". No Godot engine
code, tests or goldens were touched.

- **Trial project compiles.** `D:\Projects\Chimera-Unreal\ProjectChimera` (UE 5.8.3, Blank C++, not a git repo,
  outside this repo until the trial passes) builds with `Build.bat` in ~2 min with UBA; the 16–32 GB page file fixed
  the earlier failure. Ray tracing is off in its `DefaultEngine.ini` (originals kept as `*.bak-20260930`).
- **Unreal MCP is set up.** Plugins ModelContextProtocol, ToolsetRegistry, AllToolsets, EditorToolset, Python,
  EditorScriptingUtilities and PCGBiomeSample are enabled in the `.uproject`. Server: `http://127.0.0.1:8000/mcp`,
  registered in Claude Code (`unreal-mcp`, local scope for this repo, ✔ Connected, 52 toolsets). It only answers while
  the editor runs with `-ModelContextProtocolStartServer`.
- **Look test delivered** (sent to Alec as images): Manor-Lords-style (A) vs Northgard-style (B), same Tripo roster,
  layout and camera. A 62.9 fps with Lumen / 76.2 without (looks the same), B 107.9 fps, RTX 3060 at 1080p. Results,
  composites and caveats: `docs/unreal-move/look-test-2026-10-01/README.md`. Tooling and plan: `tools/unreal-looktest/`.
- **UI port rule (Alec, 2026-09-30):** Unreal builds the approved **Round 2** set in `docs/ui-redesign/`, never the Godot
  or June UI as it looks (Unreal Spec updated to say so). The Map Editor board is Round 1 geometry plus Round 2 levers.
- 110 Windows Firewall allow rules added for the Unreal binaries at Alec's request (group "Unreal 5.8 (Claude)";
  remove with `Remove-NetFirewallRule -Group 'Unreal 5.8 (Claude)'`).

## Next step

1. Act on Alec's world-look call (below). If A: record it in the Unreal Spec ("The world's look" paragraph), drop
   Lumen as the default (no visible gain here, −2.7 ms GPU), and plan a closer default camera.
2. Trial check (a): the NativeAOT sim library drives 1,000 units in Unreal with the same checksum as Godot (sim
   content loading must move from reflection System.Text.Json to source generation first).
3. Trial check (b): Match HUD board 3.1 (`docs/ui-redesign/Match.dc.html`, Round 2) built in C++ and matched by screenshot.
4. Trial check (c): runtime terrain editing (RealtimeMeshComponent vs Errant Landscape runtime).

## Waiting on Alec

- **World look** — A, B or neither, after the look test (images sent 2026-10-01; findings in the look-test README).
- **`.claude/worktrees/wf_bbdf1721-ebb-93/`** — stale 2026-08-04 workflow leftover (untracked, nothing unique). Delete?
- Optional RAM upgrade (16 GB; editor idles at 4.2 GB, so not blocking). Carried over: DW-1029 (Flux dev licence),
  HD Model triangle question, DW-1025 (`bulwark_adept` palette), `cinderhand_thrall` colour, and a Tripo version of
  `covenant_transmuter` (alpha infantry is the last Hunyuan model and renders as white blocks).

## How to run and check it

- Unreal editor + MCP + job bridge: `bash tools/unreal-looktest/start_editor.sh <logtag>` (the scripts expect to live
  in `D:\Projects\Chimera-Unreal\ProjectChimera\LookTest\tools\`, where the working copy is). Wait for
  `LookTest/run/bridge_ready.json`; first launch compiles shaders (~12 min), later ones ~2 min.
- Run an editor job: `python ue_job.py <module> [func] '<json args>'`. MCP from the shell: `python mcp_call.py call
  EditorToolset.EditorAppToolset CaptureViewport '<json>' --save-images DIR`. Close cleanly: `python ue_job.py lt_admin quit`.
- Frame rate: `powershell -File run_fps.ps1 -Look A|A_noLumen|B -Rep n` (editor closed), then `python parse_csv.py`.
- Godot build `dotnet build godot/godot.sln`; sim tests `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj`
  (baseline 6392 / 0 / 1 skipped).

## In flight / known issues

- Unreal gotchas found live (all handled in the tooling, details in `tools/unreal-looktest/PLAN_DELTA.md` D7–D9):
  launch from Bash with `MSYS_NO_PATHCONV=1` or `/Engine/...` args get mangled; `CaptureViewport` needs
  `captureTransform` and `annotations` and returns the PNG inside a text block; MaterialEditingLibrary setters return
  False on success; Interchange names clips `<asset><clip>` with no separator; skeletal meshes need
  `AlwaysTickPoseAndRefreshBones` + `set_update_animation_in_editor` or off-screen units T-pose; `-game` CSVs land in
  `%LOCALAPPDATA%\UnrealEngine\5.8\Saved\Profiling\CSV`.
- The first `Start-Process` launch of the editor from the PowerShell tool hung with no log; later launches through
  `run_fps.ps1` worked. Cause not found; prefer `start_editor.sh`.
- Godot asset work (Epic 16) paused pending the world-look decision.

## Key context

- Architecture of record (Unreal Spec, https://claude.ai/code/artifact/5b9ecf08-3568-4a8f-833a-f21c1112b395): the
  engine-free sim compiles with NativeAOT into a native library; Unreal is a thin C++ shell (no widget Blueprints);
  the match server is a plain .NET program. The Godot build is the behavioural reference.
- Tripo (`D:\tripo-out`) is the production asset route: 12 rigged units share one 41-bone rig with 6–11 clips each;
  static Tripo GLBs carry `KHR_materials_volume`, which must be stripped before import (`prep_roster.py` does it).
- PC: Ryzen 5 5600 (6 cores), RTX 3060 12 GB, 16 GB RAM, D: ~650 GB free.
- History: `docs/history/Snapshot-through-2026-09-21.md`. Ledger: `_bmad-output/implementation-artifacts/deferred-work.md`.
