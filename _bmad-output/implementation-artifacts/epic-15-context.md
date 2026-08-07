# Epic 15 Context: Deferred-Work Burn-Down & MP Reconnect

<!-- Generated from planning artifacts. Regenerate with compile-epic-context if planning docs change. -->

## Goal

Epic 15 retires the verified deferred-work backlog and lands the decision-mandated feature builds that fell out of it. The bulk of the work is a ledger-driven burn-down: hundreds of `DW-<n>` defects (verified against the codebase with file:line evidence) are closed in themed bundles at the `bmad-loop sweep` / `chimera-dw-burndown` cadence. Layered on top are net-new builds Alec approved during the correct-course passes: v1 multiplayer reconnect, map-size determinism unification, making authored status effects actually read by the sim (the headline gap — all five StatusFlags are authorable and hashed but no system honors them), the Scenario Settings and New-Scenario surfaces, and several ability/energy/attribute feature increments. It runs after Epic 11 (done), not interleaved.

## Stories

Real stories with named deliverables (kept):
- Story 15.1: MP reconnect v1 — command-log rejoin + fast-forward catch-up (DEFERRED — needs a human present, not `bmad-loop`-runnable)
- Story 15.2: Map-size determinism unification + raw heightmap read
- Story 15.3: Status effects become real + modifier-period honesty
- Story 15.10: Scenario Settings panel + New-Scenario empty-canvas flow
- Story 15.11: Ability targeting increments — ground-target cast + ally-targeted heal-other (done)
- Story 15.12: Energy & stack mechanics
- Story 15.13: Effect vocabulary completion — Teleport + presentation leaves
- Story 15.14: Host-side hero identity enforcement + attested-hero deployment (re-scoped to DW-200 alone)
- Story 15.15: MP surfaces & Godot-free MP test extraction (done, 11/11)
- Story 15.21: Creator-authorable hero attribute system (net-new feature, no DW ids)
- Story 15.22: Phase C — batched golden re-baseline

Retired as sprint keys on 2026-08-06 (thematic "sweep container" stories; scope unchanged, now tracked solely in `deferred-work.md`):
- Stories 15.4–15.9 and 15.16–15.20

## Requirements & Constraints

- The burn-down executes DW bundles and closes ledger ids; it never executes the container stories. The deferred-work ledger is the single source of truth for burn-down status — join on DW ids, not on stale bundle names.
- MP reconnect v1: server flags a dropped-then-returning peer by its slot identity, streams the match command log, the rejoiner re-runs content/hash gates then fast-forward-simulates to the live tick and resumes input. AC gate is a 2-player LAN mid-match rejoin with post-catch-up checksum agreement; a failed content gate must reject the rejoin without disturbing the live match. Note: the original "server buffers the whole command stream" premise is FALSE — only a 64-tick ring exists, so v1 must BUILD the full-match buffer plus new packet types and a protocol-version bump.
- Status effects: Combat must honor Disarmed, Movement must honor Rooted/Stunned, AbilityCastSystem must honor Silenced, DamageResolver must honor Invulnerable. A modifier that collapses effective max health to 0 must raise death, not pin a 0-HP "zombie."
- Godot-free bundles route to `chimera-dw-burndown`; Godot-coupled bundles stay on `bmad-loop` because of the single-client editor bridge and routed in-engine gate.

## Technical Decisions

- **Determinism / golden discipline:** fold a sim array into `SimChecksum` and re-baseline goldens only when it first becomes mutable mid-match. A re-baseline batch takes **bounded corrections only** — feature builds that move goldens re-record at the end of their **own** story, never inside a batch window. Always measure a bounded fold (byte-identical for scenarios not carrying the new state) before accepting an unconditional one. Re-record on Windows so the float-AI `ai-active` golden stays current.
- **Story 15.22 batch:** one `SimChecksum.AlgoVersion` bump 23→24, 14 bounded sim corrections plus 1 golden-moving ruling (total wipeout always loses — drop the `ActiveCount < 3` guard) and 3 in-window riders. All Godot-free.
- **Authoring→sim boundary:** authored numbers are `float`, quantized to `Fixed` at a single load boundary (the `HeroDefinition` `*_per_level` convention). Simulation stays pure C# with no Godot Nodes.
- **Closed-enum touch rule:** adding a member to a closed enum (e.g. `StackRule`) requires patching both switch statements AND any `(int)`-indexed arrays.
- **Energy/stacking (15.12):** flat authored `regen_rate` with a folded per-tick regen path, read through a seam that 15.21's attribute system can later drive. `StackRule` split into explicit grouped vs. per-stack-expiry variants; periodic-stacking mode is creator-authored (multiply-the-pulse or repeat-the-pulse) with a system-level cap. The **default** mode must preserve today's non-scaling pulse byte-for-byte so no shipped content changes meaning.
- **Map size (15.2):** parameterize the four sim grids from one map-size truth source; read raw per-region heightmap cells (no Godot float interpolation); +128 is the intended playable ceiling. Add `border_extent` to `ScenarioData` (visual/camera only, excluded from `CanonicalModelHash`); enforce `map_bounds ≤ MapSizes.MaxHalfExtent` fail-closed. This changes the pathability persist format — moves `CanonicalModelHash` and `StartStateHash`.
- **Attribute system (15.21):** data-driven and creator-defined attribute set (not a fixed enum), with shipped presets seeding common ARPG/RTS models; per-hero base + per-level growth; creator-authored derived-stat mappings (first consumer: Intelligence → max energy/regen; WC3 primary attribute → attack damage proves the mapping is general). Full `Validated<T>` gate and JSON round-trip. Sequence after 15.12.

## Cross-Story Dependencies

- 15.21 depends on 15.12 leaving a regen seam open, and must sequence after it.
- 15.2 is unblocked by the A5-E11 map-size decision (Route C) and folds in the `editor-map-bounds-guards` bundle.
- 15.3 lost DW-272 to 15.12 (it is an authored stacking mode, not a semantics correction).
- 15.22 explicitly excludes items homed elsewhere (DW-160/146/162→15.2, DW-265→15.12/15.21, DW-272→15.12, DW-200→15.14, DW-280→15.11, DW-346→15.17).
- 15.18 is the natural predecessor to Epic 12 (Import Manager & Content Sync) — every bundle hardens a surface Epic 12 builds on.
- 15.1 and the AI-in-lockstep path depend on `AiOpponentSystem` float→Fixed migration (Story 10.11, DW-466), which is Epic-10 work, not Epic-15.
- 15.14's `post-drop-checksum-honesty` and other live-MP verification items are blocked on the Epic-10 live-verify batch (A5-E9), which requires two physical machines.
