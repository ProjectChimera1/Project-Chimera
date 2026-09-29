# HANDOFF — Project Chimera

_Updated 2026-09-29 · branch `master` · last commit `16f586a7` (then this handoff commit)_

## Where we stopped

Session 13 (2026-09-29) planned the move from Godot to **Unreal Engine 5.8** and set up for it.
No engine code, tests or goldens were touched. Done this session:

- **Unreal Spec** (Claude Doc, the product source of truth):
  https://claude.ai/code/artifact/5b9ecf08-3568-4a8f-833a-f21c1112b395. Alec reviewed it and
  settled: trial first; rename the repo only after the trial passes; nothing missing; native
  Unreal UI only; OpenAI Codex added as an AI provider; Commanders play every custom game
  Architects publish (any genre, WC3-style); "Law of Equal Exchange" is FMA's term and can't be
  used, so the setting name and faction styles are placeholders until Alec finishes the story.
- **World look:** of six Steam references in the spec, Alec picked **Manor Lords** (near-photoreal,
  UE5), with **Northgard** a close second.
- **UI redo** with Opus 5.5: one integrated shell (Godot's separate windows were the failure), the
  map editor first. The brief for Alec's Claude Design runs:
  https://claude.ai/code/artifact/1a5fc17e-e70b-4d36-bf64-5226ee5e037a (shared brief + directions
  A Arcane Workshop / B Transmutation Lab / C Modern Atelier + review checklist). Direction B's
  reference images are in that doc and in
  `_bmad-output/planning-artifacts/ux-designs/ux-Project_Chimera-2026-06-20/handoff-prompts/`.
- **Cleanup:** BMAD tooling, the bmad-loop hooks and the 2026-06-05 UX run removed; the project's
  `git push` deny rule was lifted at Alec's request.
- **PC setup:** Visual Studio 2026 Community 18.10 installed and verified (all four Epic workloads,
  Unreal installer/IDE/debugger components, MSVC 14.51, Windows SDK 10.0.26100 + 10.0.28000,
  .NET 8/9/10). **UE 5.8.3 was at 86% installing** from the Epic Launcher to `D:\Epic Games`
  with Editor symbols and Engine Source; target platforms Windows only.

## Next step

1. Confirm UE 5.8.3 finished (Launcher → Library). Compile a blank C++ project to prove the
   toolchain. MSVC 14.51 is newer than Epic's recommended 14.50: if UnrealBuildTool rejects it,
   Alec adds the 14.50 toolset in the Visual Studio Installer.
2. Create the trial project at `D:\Projects\Chimera-Unreal-Trial` (outside this repo until the trial
   passes). Enable Epic's Unreal MCP plugin (5.8, experimental; default `http://127.0.0.1:8000/mcp`)
   and connect it to Claude Code.
3. **Look test first** (Alec is waiting on it): one map shot of our units/buildings rendered
   Manor-Lords-style and Northgard-style, sent side by side as images. It judges readability of
   armies, the asset redo cost (photoreal = regenerate all 24 models), player-import clash (idea:
   per-map look preset) and frame rate on this PC.
4. Then the three trial checks: (a) NativeAOT sim library drives 1,000 units in Unreal with the
   same checksum as Godot; (b) one HUD screen in C++ matched to a mockup by screenshot;
   (c) runtime terrain editing (RealtimeMeshComponent vs Errant Landscape runtime).

## Waiting on Alec

- **Claude Design runs** of the brief (three directions); bring back links or screenshots of each
  design system + map editor for side-by-side review.
- **World look** final call after the look test.
- **`.claude/worktrees/wf_bbdf1721-ebb-93/`**: stale leftover of a 2026-08-04 workflow run (89 files,
  duplicates of `_bmad-output` UX files, nothing unique, untracked). Delete when he says OK.
- **Optional RAM upgrade:** 16 GB vs Epic's 32 GB recommendation; 2 of 4 slots free.
- Carried over: DW-1029 (Flux dev licence), HD Model triangle question, DW-1025 (`bulwark_adept`
  palette), `cinderhand_thrall` colour. The Tripo roster may be regenerated photoreal anyway.

## How to run and check it

- Godot build: `dotnet build godot/godot.sln`
- Sim tests: `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj`
  (baseline 6392 / 0 / 1 skipped; `CanonicalModelHashPerf` is a known CPU-contention flake)
- Godot in-engine checks: `godot-verify` skill.
- Commit and push at checkpoints (push allowed as of 2026-09-29).

## In flight / known issues

- Godot asset work (Epic 16) paused pending the Unreal and world-look decisions.
- Sim content loading uses reflection-based System.Text.Json and must move to source generation for
  NativeAOT (trial check a).
- PC: Ryzen 5 5600 (6 cores, below Epic's 12–16 core compile baseline, so builds are slower),
  RTX 3060 12 GB (fine, supports Lumen), 16 GB RAM, D: ~700 GB free.

## Key context

- Architecture of record (in the spec): the ~75k-line engine-free sim (`godot/SimSources.props`)
  compiles with .NET NativeAOT into a native library; Unreal is a thin C++ shell (UI in Slate or
  UMG-from-C++, no widget Blueprints); the match server becomes a plain .NET program, so no
  source-built Unreal. ~58k Godot-coupled lines get rebuilt; the Godot build is the behavioural
  reference.
- Unreal facts checked 2026-09-29 (sources in the spec): UE6 early access aimed at late 2027
  (Verse); Landscape can't be sculpted in a shipped game; glTFRuntime loads .glb at runtime; royalty
  5% above $1M lifetime, Epic Games Store sales exempt.
- Work comes as one-page feature briefs (outcome + why, constraints, proof of done, out of scope).
- History: `docs/history/Snapshot-through-2026-09-21.md`. Ledger:
  `_bmad-output/implementation-artifacts/deferred-work.md`.
