# HANDOFF — Project Chimera

_Updated 2026-09-29 · branch `master` · last commit: BMAD tooling retired (see git log)_

## Where we stopped

Session 13 (2026-09-29) planned a move from Godot to Unreal Engine 5.8, and wrote a new product
spec for it: **Project Chimera — Unreal Spec**
(https://claude.ai/code/artifact/5b9ecf08-3568-4a8f-833a-f21c1112b395). The spec covers the game,
multiplayer, the creator, sharing, UI, the Unreal architecture, the non-negotiables, the build order
and open decisions. Core idea: the ~75k-line engine-free simulation (6,392 tests) is compiled with
.NET NativeAOT into a native library, and Unreal is a thin C++ shell that draws its state and sends
it orders. The match server becomes a plain .NET program, so no source-built Unreal is needed.

BMAD is retired in this repo: `_bmad/`, `.bmad-loop/`, the 85 bmad/gds skills, the dw-burndown
workflows, `tools/verify-in-engine-gate.ps1` and the four bmad-loop hooks in `.claude/settings.json`
are gone (recoverable from git before this commit, except the untracked `.bmad-loop/` run records).
`_bmad-output/` stays: it holds the UI mockups, `DESIGN.md`/`EXPERIENCE.md`, the GDD-era planning
docs and the deferred-work ledger. No engine code, tests or goldens were touched.

## Next step

Alec reviews the spec (inline edits and comments in the doc) and settles its open decisions. Then
run the **1–2 week Unreal trial**, with three pass/fail checks:
1. The NativeAOT simulation library drives 1,000 units in Unreal with the same checksum as Godot.
2. One HUD screen built in C++ and matched to its mockup by screenshot.
3. Terrain edited while the game runs (RealtimeMeshComponent vs Errant Landscape runtime).

Only if all three pass: rename this repo/folder to `Godot-ProjectChimera-Archived` and start the
Unreal repo (simulation carried over with its git history). Features after that get one-page briefs
(outcome + why, constraints, proof of done, out of scope), not BMAD stories.

## Waiting on Alec

- **Spec review + its open decisions:** trial first (recommended), world look (stay stylized
  low-poly recommended), repo names, anything missing.
- **UI technology:** native C++ UI (assumed) vs HTML through Ultralight — question left as a comment
  in the spec doc.
- **Carried over, unchanged:** DW-1029 (local Flux route runs on non-commercial FLUX.1 dev; schnell is
  a one-dropdown swap), the HD Model triangle-count question for the six remaining buildings,
  DW-1025 (`bulwark_adept` reads brass, not prussian blue), `cinderhand_thrall` reads brown not crimson.

## How to run and check it

- Build: `dotnet build godot/godot.sln`
- Tests (Tier-1, Godot-free): `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj`
  — baseline 6392 passed / 0 failed / 1 skipped (`CanonicalModelHashPerf` is a known CPU-contention
  flake; re-run in isolation if it's the lone failure).
- In-engine verification: `godot-verify` skill (Godot MCP bridge).
- Asset gating: `tools/asset-gen/scripts/qa/tripo_batch_gate.py`.
- `git push` is denied in `.claude/settings.json` for this project; Alec pushes.

## In flight / known issues

- Godot asset work (Epic 16) is paused pending the Unreal decision: `bolt_sanctum`'s roof-identity
  block has never been batched; five more buildings need the same form (`D:\tripo-input\TRIPO_RUNBOOK.md`).
  The .glb models carry over to Unreal unchanged.
- Epic 15 burn-down stays paused; bmad-loop no longer exists here.

## Key context

- Simulation source set: `godot/SimSources.props` (Core, Combat, Economy, Navigation, AI, Effects,
  Dsl). Already checked by AOT analyzers (`ProjectChimera.Sim.Analysis`). Content loading uses
  reflection-based System.Text.Json and must move to source generation for NativeAOT.
- Godot-coupled code to rebuild in Unreal: ~58k lines (UI 25k, Creation Suite 15k, bootstrap,
  ENet transport, mesh loading). The Godot build is the behavioural reference for every screen.
- Unreal facts checked 2026-09-29 (sources in the spec): UE 5.8 current, UE6 early access aimed at
  late 2027 (moves gameplay code to Verse); Landscape can't be sculpted in a shipped game; glTFRuntime
  loads .glb at runtime; Epic's Unreal MCP plugin (5.8, experimental) supports Claude Code; royalty
  5% above $1M lifetime, Epic Games Store sales exempt.
- Full history: `docs/history/Snapshot-through-2026-09-21.md`. Ledger:
  `_bmad-output/implementation-artifacts/deferred-work.md`.
