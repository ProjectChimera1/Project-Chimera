# Epic 15 Context: Deferred-Work Burn-Down & MP Reconnect

<!-- Generated from planning artifacts. Regenerate with compile-epic-context if planning docs change. -->

## Goal

Epic 15 retires the verified deferred-work (DW) backlog that accumulated across Epics 1–14 and delivers the decision-mandated feature builds that fell out of that triage. The bulk of the epic is executed as `bmad-loop sweep` bundle cycles: each named bundle is the work unit, its ledger `DW-<n>` entries are the spec source, and a story is "done" when its bundles are committed and their entries closed. On top of the burn-down sit several genuine builds — v1 MP reconnect, map-size determinism unification, making authored status effects actually read by the sim, the Scenario Settings / New-Scenario surfaces, and Alec's chosen feature increments (ability targeting, energy/stack mechanics, effect-vocabulary completion, a creator-authorable hero-attribute system). It runs after Epic 11.

## Stories

- Story 15.1: MP reconnect v1 — command-log rejoin + fast-forward catch-up (DEFERRED; needs a human, not `bmad-loop`)
- Story 15.2: Map-size determinism unification + raw heightmap read
- Story 15.3: Status effects become real + modifier-period honesty
- Stories 15.4–15.9: RETIRED sweep containers (scope now tracked solely in `deferred-work.md`)
- Story 15.10: Scenario Settings panel + New-Scenario empty-canvas flow
- Story 15.11: Ability targeting increments — ground-target cast + ally-targeted heal-other (DONE)
- Story 15.12: Energy & stack mechanics (DONE 2026-08-07)
- Story 15.13: Effect vocabulary completion — Teleport + presentation leaves
- Story 15.14: Host-side hero identity enforcement + attested-hero deployment (re-scoped to DW-200 alone)
- Story 15.15: MP surfaces & Godot-free MP test extraction (DONE)
- Stories 15.16–15.20: RETIRED sweep containers (scope now tracked solely in `deferred-work.md`)
- Story 15.21: Creator-authorable hero attribute system (net-new feature)
- Story 15.22: Phase C — batched golden re-baseline

## Requirements & Constraints

- **The ledger is the tracker, not the story list.** The burn-down executes DW bundles and closes ledger ids; it never executes stories. Eleven thematic "sweep container" stories were retired as sprint keys because nothing ever wrote to them — `deferred-work.md` (its `DW-<n>` entries with `status:` and `decision:` lines) is the single source of truth for burn-down work. Join progress on DW ids, not bundle names: an authoritative re-cut partition renamed most bundles, so the bundle tables are stale.
- **Headline gap (DW-266):** all five authored `StatusFlags` (Disarmed, Rooted, Stunned, Silenced, Invulnerable) are authorable and hashed but read by no system. Wiring them into Combat/Movement/AbilityCast/DamageResolver is the core of Story 15.3.
- **Godot-free vs Godot-coupled routing.** Godot-free bundles/corrections route to `chimera-dw-burndown` (parallel worktrees + Tier-1 gate). Godot-coupled bundles stay on `bmad-loop sweep` because the single-client editor bridge plus routed in-engine gate can't be parallelized.
- **Two stories carry no DW ids by design** (15.1, 15.10, plus the net-new 15.21) — they are recorded build decisions, not backlog.
- **Blocked-elsewhere:** several DW ids named in the sweep resolve to other epics (e.g. cross-machine save portability and running AI inside lockstep MP both require `AiOpponentSystem` float→Fixed = Story 10.11; live-MP verification debt = the Epic-10 A5-E9 batch). Do not pull those into Epic 15.

## Technical Decisions

- **Checksum-fold timing rule.** Fold a new sim array into `SimChecksum` / re-baseline goldens only when it first becomes mutable mid-match. Each distinct sim-behavior change is a separate `SimChecksum` movement; authored-field additions additionally move `CanonicalModelHash`.
- **Batch rule.** A re-baseline batch (e.g. Story 15.22) takes **bounded corrections only** — the cost amortised is a ~10-minute re-record, so coupling it to a multi-week feature build is wrong. Feature stories that move goldens re-record at the end of their **own** story, isolated from other stories' folds.
- **Byte-for-byte default preservation.** When splitting a closed enum or adding an authored mode (e.g. the `StackRule` split, periodic-stacking, energy regen), the *default* behavior must preserve today's output byte-for-byte so no shipped content changes meaning and no existing golden moves; only opt-in content diverges.
- **Enum-indexed-array touch-site rule.** Adding a member to a closed enum (`StackRule`, `BuildingType`, effect leaves) must patch **both** switch statements **and** any `(int)enum`-indexed arrays — a switch-grep misses the array class, some of which crash.
- **Authoring→sim quantization boundary.** Authored `float` numbers convert to `Fixed` at the single load boundary (the `HeroDefinition` `*_per_level` convention); sim code stays deterministic. Every new definition gets a `Validated<T>` gate; validator warnings ride the existing `Warnings` channel on `AbilityValidationResult`.
- **Seam for future extension.** Story 15.12 must read energy `regen_rate` through a single seam that Story 15.21's attribute system can later drive, rather than hardcoding the flat field at the tick site.
- **MP reconnect reality (Story 15.1).** The Epic-9 server does **not** buffer the whole command stream — `MergedTickBuilder` uses a 64-tick ring; there is no rejoin/resync `PacketType`. v1 must BUILD a full-match command-log buffer, new packet types, a `PROTOCOL_VERSION` bump, an `InGame` connect branch, checksum re-admission, and match-lifecycle resets. Reusable: `MergedTickPacket` is byte-identical to a `.chmr` body frame (a per-tick server buffer of those *is* the downloadable log); `ReplayPlayer.Flush(tick)` + `SimulationHost.StepOnce()` is the frame-wait-free catch-up engine; `MatchAgreementHash.Compute` re-gates content cleanly.

## UX & Interaction Patterns

- **Ability authoring transparency.** New authored modes (stacking, periodic-stacking, energy regen, ground-target vs ally-target casting) surface in the **existing** ability-editor stacking dropdown / targeting UI — one authoring pass, not a new panel.
- **Scenario Settings (15.10):** a unified surface for map name/author, win condition, and per-slot starting resources, plus a Create/New-Scenario flow that originates a blank `ScenarioData` (revisits onboarding step 4 to offer starting from empty).
- **Hero attribute system (15.21):** data-driven, creator-defined attribute sets (not a fixed enum) with shipped ARPG/WC3 presets as editable starting points; editor surfaces live in the Unit Card Editor's hero section (preset picker, attribute list, per-hero values, derived-stat mapping).

## Cross-Story Dependencies

- **15.21 sequences after 15.12** so the energy-regen seam exists for the attribute system to plug into; each contributes its own distinct `SimChecksum`/`CanonicalModelHash` movement (do not batch them together).
- **15.2** owns the pathability-format change (invalidates every stored `pathability_blocked`; moves `CanonicalModelHash` *and* `StartStateHash`) and is a re-baseline that must stay isolated.
- **15.22 (Phase C batch)** collects only bounded Godot-free corrections; it explicitly excludes the feature folds (15.2, 15.11, 15.12, 15.14, 15.17, 15.21), each of which re-records on its own story.
- **15.18** is the natural predecessor to Epic 12 (Import Manager & Content Sync) — every bundle hardens a surface Epic 12 builds on.
- **15.16** (alliance awareness) moves goldens on the AI/projectile hot path and must isolate its re-baseline from 15.3's and 15.4's.
