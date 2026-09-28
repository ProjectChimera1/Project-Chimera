# HANDOFF — Project Chimera

_Updated 2026-09-28 · branch `master` · last commit `bbf55e62 docs(16): session 12 save — the roof rule is proven, and a local plate route`_

## Where we stopped

Session 12 (2026-09-21) proved the roof-identity rule for building plates: amending a block
so identity lives on the roof ridge (not the front wall) flips the metrics — `thrall_yards`
went from 0.0% to 62.4% crimson above the 42% line, roof detail now beats wall detail. Picked
`thrall_yards` variant B. Also stood up a local ComfyUI + Flux concept-plate route as an
*evaluation* alongside the production Tripo route (`tools/asset-gen/scripts/comfy/`). No
engine code, tests, or goldens touched this session. Buildings: `sigil_foundry` textured and
landed; `thrall_yards` plate picked; `covenant_sanctum`, `sanguine_furnace`, `render_works`
already carried roof-borne identity. Units unchanged since session 11.

## Next step

Batch `bolt_sanctum` on Tripo — its amended block (roof-identity fix applied) has never been
run. It is the one unvalidated edit outstanding. Then apply the same proven roof-identity form
to the remaining five buildings. Everything needed is in `D:\tripo-input\TRIPO_RUNBOOK.md`
(session 12 preamble + prompt blocks).

Queued after that:
1. Settle DW-1029 before any further local (ComfyUI/Flux) plating — see Waiting on Alec.
2. Once buildings are done, Epic 16 slice 4 (terrain/world) is next, then slice 5 (unit
   facing, motion, buildings on the ground).
3. Resolve the HD Model triangle-count question (Waiting on Alec) before committing the other
   six buildings to it.
4. Epic 15's burn-down stays PAUSED; no `bmad-loop` run is needed for Epic 16 work.

## Waiting on Alec

- **DW-1029 (needs Alec):** the local ComfyUI plate route runs on `flux1-dev`, which is
  non-commercial. `FLUX.1 [schnell]` (Apache-2.0) is a one-dropdown, ~7 GB swap using the same
  encoders/VAE. Decide before the local route is used for anything beyond evaluation.
- **HD Model for buildings (open since session 11):** Alec liked a 20,000-tri HD Model result
  on v3.1 Best Quality, but the runbook's settings table claims HD Model has no real polycount
  control and produces million-poly sculpts. Needs settling with `tripo_batch_gate.py` on one
  asset before committing the other six buildings — a wrong assumption costs six buildings of
  credits.
- **DW-1025:** `bulwark_adept` reads brass/gold, not the intended prussian-blue. Palette call
  pending.
- **`cinderhand_thrall` colour:** reads brown, not crimson (crimson is a layer under the outer
  apron). Accept as intentional differentiation, or recolour and re-roll? Left unamended
  pending the call.

## How to run and check it

- Build: `dotnet build godot/godot.sln`
- Tests (Tier-1, Godot-free): `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj`
  — baseline is 6392 passed / 0 failed / 1 skipped (`CanonicalModelHashPerf` is a known
  CPU-contention flake; re-run in isolation if it's the lone failure).
- In-engine verification: use the `godot-verify` skill (builds, runs the scene via the Godot
  MCP, screenshots, checks against acceptance criteria). Needed for any change touching
  `src/UI/**`, `src/Core/Bootstrap/**`, `MainScene.cs`, or `scenes/**`.
- Asset roster gating: `tools/asset-gen/scripts/qa/tripo_batch_gate.py` (batch-gates
  `D:/tripo-out` against the engine profile: triangle count, `max_materials: 1`, `inside_out`).
- Manual run: F5 in the Godot editor (4.6.3, .NET/mono build), or `godot_mcp` `run_project`.

## In flight / known issues

- `bolt_sanctum`'s amended (roof-identity) prompt block is written but has never been batched
  — see Next step.
- Local ComfyUI + Flux route is an evaluation only; Tripo remains production. Do not use the
  local route for shipped assets until DW-1029 is resolved.
- Epic 16 slices 4 and 5 (terrain/world; unit facing, motion, buildings on the ground) are
  outstanding.
- Epic 15's burn-down is deliberately paused.

## Key context

- Full multi-session history (sessions 1–12, back to 2026-08-01) is archived at
  `docs/history/Snapshot-through-2026-09-21.md` — unchanged, just moved. Read it for the roof-
  identity lesson's origin (session 11), the ten prompt-authoring lessons, and earlier
  engine-side work (FR-39 LAN determinism, stat pipeline, veterancy).
- Asset generation is a two-route system: Tripo 3D (paid cloud image-to-3D, production) and a
  local ComfyUI + Flux concept-plate pipeline (evaluation). Prompt blocks live in
  `D:\tripo-input\TRIPO_RUNBOOK.md`, not in this repo.
- Deferred-work ledger (`DW-<n>` entries, the live defect record):
  `_bmad-output/implementation-artifacts/deferred-work.md`. Epic/story status:
  `_bmad-output/implementation-artifacts/sprint-status.yaml` — edit both as text, never
  round-trip `sprint-status.yaml` through a YAML parser (it doesn't strictly parse).
- Triangle budgets (session 9/16 work): unit 8,000 / hero 25,000 / building 20,000 / prop
  1,000 — set in three places that must stay in sync: `godot_chimera.json` (`tri_budget`),
  `build_manifest.py` (`tri_target`), `chimera_assets.json` (`tri_target`).
- Where the GDD and code disagree, the code plus this file is the as-built truth.
