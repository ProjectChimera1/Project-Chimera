# Epic 15 Context: Deferred-Work Burn-Down & MP Reconnect

<!-- Generated from planning artifacts. Regenerate with compile-epic-context if planning docs change. -->

## Goal

Epic 15 retires the verified deferred-work (DW) backlog and lands the decision-mandated feature builds that fell out of that backlog. Most burn-down work is executed as DW bundle cycles and tracked entirely in `deferred-work.md` — the eleven original thematic "sweep container" stories were retired as sprint keys on 2026-08-06 because nothing ever wrote to them (the burn-down closes ledger ids, it never executes stories). What remains as live stories are the named deliverables: v1 MP reconnect, map-size determinism unification, making authored status effects actually do something (the headline gap — all five StatusFlags were authorable and hashed yet read by no system), the Scenario Settings / New-Scenario surfaces, ability-targeting and energy/stack feature increments, effect-vocabulary completion, host-side hero attestation, MP test extraction, a creator-authorable hero attribute system, and a batched golden re-baseline. Epic 11 is done; this epic runs next rather than interleaved.

## Stories

- Story 15.1: MP reconnect v1 — command-log rejoin + fast-forward catch-up
- Story 15.2: Map-size determinism unification + raw heightmap read
- Story 15.3: Status effects become real + modifier-period honesty
- Story 15.10: Scenario Settings panel + New-Scenario empty-canvas flow
- Story 15.11: Ability targeting increments — ground-target cast + ally-targeted heal-other
- Story 15.12: Energy & stack mechanics
- Story 15.13: Effect vocabulary completion — Teleport + presentation leaves
- Story 15.14: Host-side hero identity enforcement + attested-hero deployment (re-scoped to DW-200)
- Story 15.15: MP surfaces & Godot-free MP test extraction (done record — 11/11 closed)
- Story 15.21: Creator-authorable hero attribute system
- Story 15.22: Phase C — batched golden re-baseline

_Retired as sprint keys (tracked solely in `deferred-work.md`): §§ 15.4–15.9, 15.16–15.20._

## Requirements & Constraints

- **Determinism is the governing constraint.** Any change that folds new state into `SimChecksum`, or adds authored definition fields (`CanonicalModelHash`), or alters stored map format (`StartStateHash`), moves goldens and requires a deliberate re-baseline. Fold a new array only when it first becomes mutable mid-match (checksum-fold timing rule).
- **Batch rule:** a re-baseline batch takes **bounded corrections only** — the cost amortised is a ~10-minute re-record, so coupling it to a multi-week feature keeps the branch open and queues every other golden-moving fix behind it. **Feature stories that move goldens re-record at the end of their own story**, isolated, never folded into a batch. Batching corrections is correct; batching builds is not.
- **Default-preserving behavior change:** where a feature adds a mode (stacking, periodic-stacking, MaxHealth-to-zero death), the default path must preserve today's behavior byte-for-byte so no shipped content changes meaning and no existing golden moves — only opt-in content diverges.
- Untrusted input (scenarios, map packages, MP payloads) must fail closed with upstream byte/size and element-count guards, not parse-then-gate.
- Every system must stay data-driven and creator-extensible; new definitions carry a `Validated<T>` gate, JSON round-trip, and authoring warnings surfaced via the existing `Warnings` channel on `AbilityValidationResult`.
- Godot-free bundles route to `chimera-dw-burndown` (parallel worktree + Tier-1 gate); Godot-coupled bundles stay on `bmad-loop sweep` (single-client editor bridge + routed in-engine gate).

## Technical Decisions

- **Map size:** four sim grids parameterize from one map-size truth source; +128 is the intended playable ceiling. `border_extent` on `ScenarioData` is visual/camera only and **excluded** from `CanonicalModelHash`; `map_bounds ≤ MapSizes.MaxHalfExtent` enforced fail-closed in `ScenarioValidator`. A `ScenarioType`/GameMode registry (enum + per-type clamp preset table + Map Generator selection UI) makes the inert `MapGeneratorContext` clamps load-bearing.
- **Elevation grid** must read raw per-region heightmap cells — no Godot float interpolation in the sim path.
- **Stacking** (`StackRule`, `godot/src/Effects/Modifier.cs`) is a closed enum: three requested modes already map to `Refresh`/`Ignore`/`Stack`; only per-stack expiry is new. Split `Stack` into a grouped variant (byte-identical to today) and an independent per-stack variant. Patch both switches and any `(int)`-enum-indexed arrays (enum-indexed-array touch-site rule).
- **Energy regen** (15.12) builds a flat authored `regen_rate` on `UnitDefinition` read through a **single seam** that Story 15.21's attribute system can later drive — do not hardcode the field at the tick site.
- **Hero attributes** (15.21) are data-driven and creator-defined (not a fixed enum); ship ARPG/WC3 presets as editable starting points, with a derived-stat mapping (first consumer: Intelligence → max energy/regen; WC3 primary → attack damage proves generality). Authoring `float` numbers convert to `Fixed` at the single load boundary, per the `HeroDefinition` convention. There are no hero primary attributes in the codebase today — this story creates them.
- **Ground-target casting** (15.11) widens `UnitOrder` 11→12 and bumps `ReplayRecorder.VERSION`; adds an `EffectContext` ground-point field; card disable-gate and press-handler targeting fold into one shared is-castable predicate.
- **Re-baseline procedure (15.22):** bound the fold so untouched scenarios stay byte-identical (Phase B cut 28 golden files to 5); re-record on Windows so the float-AI `ai-active` golden stays current; diff with `grep -v '^#'` (the recorder rewrites the algo-version header on every file); verify with `--logger trx` (console logger truncates); when a deliberate halt gate fires, never re-freeze its control.

## Cross-Story Dependencies

- **15.21 sequences after 15.12** — the attribute system plugs into the regen seam 15.12 is required to leave open.
- **15.2 depends on** the A5-E11 map-size decision (Route C) which unblocked it; it also invalidates every stored `pathability_blocked` cell (moves `CanonicalModelHash` and `StartStateHash`), so it is explicitly excluded from 15.22's batch.
- **15.3, 15.12, 15.21** each carry independent, isolated re-baselines — status-flag fold; regen fold and stacking fold (two separate movements); attribute fold plus `CanonicalModelHash` (a third and fourth movement). None ride 15.22.
- **15.14** (unbuilt feature) moves no existing golden and must not be folded into a batch; it re-records at its own story end only if it ends up moving one.
- **15.18** (retired container, ledger-tracked) is the natural predecessor to Epic 12 (Import Manager & Content Sync).
- **DW-466** (cross-machine save portability) resolves to Story 10.11 (`AiOpponentSystem` float→Fixed), not Epic 15 — and is the standing hard prerequisite for running AI inside lockstep MP.
