---
title: 'ResearchDefinition content model + validation'
type: 'feature'
created: '2026-07-10'
status: 'done'
baseline_revision: 'e991c770c363f4bbb700ce932d3530eb0192d5ad'
final_revision: 'd293e77b28309943f554042d1774feefd3705cfc'
review_loop_iteration: 0
followup_review_recommended: false
context:
  - '{project-root}/_bmad-output/implementation-artifacts/epic-4-context.md'
  - '{project-root}/godot/src/Core/Definitions/TechTreeValidator.cs'
  - '{project-root}/godot/src/Core/Definitions/ResourceCostValidator.cs'
  - '{project-root}/godot/src/Core/Definitions/BuildingDefinitionValidator.cs'
  - '{project-root}/godot/src/Effects/Modifier.cs'
warnings: ['oversized']
---

<intent-contract>

## Intent

**Problem:** Research has no data-driven authoring surface yet — a creator cannot declare a faction-wide, repeatable, timed upgrade with per-level cost/time/modifier deltas, and buildings cannot declare which research they make available. No runtime order path exists yet (4.8b); this story is the content model + import-time validation only.

**Approach:** Add `ResearchDefinition`/`ResearchLevel`/`ResearchModifierDelta` content classes, a `Research: List<ResearchDefinition>` field on `FactionDefinition` (mirroring `Units`/`Buildings`), an `AvailableResearch: string[]` field on `BuildingDefinition` (mirroring `Prerequisites`), and a new `ResearchValidator` wired additively into `FactionDefinition.LoadFromFile`'s existing aggregate-error gate — same shape as `TechTreeValidator`/`ResourceCostValidator`.

## Boundaries & Constraints

**Always:**
- `ResearchValidator` is pure C#: never throws, never logs, returns `IReadOnlyList<string>` located errors exactly like `TechTreeValidator`/`ResourceCostValidator`. `FactionDefinition.LoadFromFile` is the sole place a non-empty error list becomes a thrown `InvalidOperationException`, joined by newlines, failing the WHOLE load.
- Match the ACTUAL existing gate, not the epic's general framing: buildings/tech-tree/cost content today does NOT mint a `Validated<T>` (confirmed — `Validated<T>`'s sole minter is `ScenarioValidator`, unrelated to `FactionDefinition.LoadFromFile`). This story follows that real precedent.
- Every check is list-all (every offending entry reported) EXCEPT the cycle DFS, which is first-fail — mirrors `TechTreeValidator.Visit` exactly, including its `"a -> b -> a."` chain format.
- Numeric content-model fields stay `float`/`int` (authoring values) — no `Fixed` conversion in this story; that's the single load-boundary 4.8b owns.
- Reuse `ResourceCostValidator.KnownResourceIds` (`internal`, currently `{"ore","crystal"}`) as the sole resource-id source of truth for level cost maps — do not invent a second set.
- Null `Prerequisites`/`AvailableResearch`/`Levels` arrays are treated as empty, never NRE — mirrors `TechTreeValidator`'s `?? Array.Empty<string>()` idiom throughout.
- The cycle DFS walks ONLY Research→Research edges (a building referenced in `Prerequisites` is always a graph leaf, exactly like `TechTreeValidator` restricts its walk to Buildings→Buildings and treats units as leaves).

**Block If:** None — no decision here requires human input.

**Never:**
- No `ModifierStore`/`ModifierSystem`/`BuildingSystem` runtime wiring, no order path (4.8b).
- No `SimChecksum` fold, no golden re-baseline (4.8c — this story adds no mid-match-mutable state).
- No command-card UI, no research authoring editor panel (4.9).
- No new `Validated<T>` minting for faction content.
- Don't cross-reference the scenario's N-resource registry (`ScenarioData.Resources`) for cost validation — mirror `ResourceCostValidator`'s existing ore/crystal-only precedent exactly.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Happy path | Faction JSON with one `ResearchDefinition` (2 levels, valid cost/time/modifier/prereqs) | Loads cleanly; `GetResearch`/`IndexOfResearch` resolve it | No error |
| Duplicate research id | Two `Research[]` entries share an id | Located error naming both | List-all |
| Empty Levels | `Research[].levels: []` | Located error | List-all |
| Non-positive level time | A level's `time_ticks <= 0` | Located error | List-all |
| Unregistered level-cost resource id | A level's `cost` key outside `{"ore","crystal"}` | Located error | List-all |
| Out-of-range cancel refund | `cancel_refund_fraction` outside `[0,1]` | Located error | List-all |
| Unknown prerequisite id | `Research[].prerequisites` names an id in neither Buildings nor Research | Located error | List-all |
| Unknown AvailableResearch id | `BuildingDefinition.available_research` names an unknown research id | Located error | List-all |
| Research→research cycle | Two research entries prerequisite each other | Located error naming the chain (`"research cycle: a -> b -> a."`) | First-fail |
| Over-cap research count | A faction authors more than the per-faction cap | Located error | List-all |
| Multiple simultaneous defects | One faction JSON with a duplicate id, a bad level, and an unknown prereq at once | ALL errors in one thrown message, not just the first | Whole load fails atomically |
| AvailableResearch round-trip | `BuildingDefinition.available_research: ["r1"]` | Round-trips through load/save byte-for-byte like `prerequisites` | No error |

</intent-contract>

## Code Map

- `godot/src/Core/Definitions/ResearchDefinition.cs` (new) -- `ResearchDefinition` (`Id`, `CancelRefundFraction`, `Prerequisites`, `Levels`), `ResearchLevel` (`Cost`, `TimeTicks`, `ModifierDelta`), `ResearchModifierDelta` (`MaxHealthDelta`/`AttackDamageDelta`/`MoveSpeedDelta`/`ArmorDelta`, mirroring `Modifier.cs`'s four additive Fixed fields as authoring-time `float`).
- `godot/src/Core/Definitions/FactionDefinition.cs:29` -- add `Research: List<ResearchDefinition>` after `Buildings`; `GetResearch`/`IndexOfResearch` mirroring `GetBuilding`(:34)/`IndexOfUnit`(:55); wire `ResearchValidator.Validate(def)` into `LoadFromFile`'s aggregate `errors.AddRange(...)` chain (~:143), with a new doc-comment paragraph matching the existing per-story convention.
- `godot/src/Core/Definitions/BuildingDefinition.cs` -- add `AvailableResearch: string[]` (`[JsonPropertyName("available_research")]`, default `Array.Empty<string>()`), mirroring `UnitDefinition.Prerequisites`'s declaration exactly.
- `godot/src/Core/Definitions/ResearchValidator.cs` (new) -- whole-faction static validator mirroring `TechTreeValidator`'s shape: duplicate research-id detection; per-level field checks (empty `Levels`, non-positive `TimeTicks`, unregistered cost resource id via `ResourceCostValidator.KnownResourceIds`, out-of-[0,1] `CancelRefundFraction`); referential lint (`Research[].Prerequisites` against building ids ∪ research ids; `Buildings[].AvailableResearch` against research ids); a research→research-only 3-color DFS cycle check (first-fail, `Visit`-style chain message); a per-faction `MaxResearchPerFaction` count cap.
- `godot/ProjectChimera.Sim.Tests/Definitions/ResearchValidatorTests.cs` (new) -- one test per I/O-matrix row, following `TechTreeValidatorTests.cs`'s inline-JSON-fixture-and-helper convention.
- `godot/ProjectChimera.Sim.Tests/Definitions/FactionWriteRoundTripTests.cs` -- extend for `Research`/`AvailableResearch` round-trip (AC2 row).

## Tasks & Acceptance

**Execution:**
- `ResearchDefinition.cs` -- add the three new content classes with snake_case `JsonPropertyName` attributes, non-nullable-typed empty-array/list defaults -- the authoring schema itself.
- `FactionDefinition.cs` -- add `Research` list, `GetResearch`/`IndexOfResearch`, wire `ResearchValidator` into `LoadFromFile` -- the load-time entry point.
- `BuildingDefinition.cs` -- add `AvailableResearch: string[]` -- the building-side authoring half of AC2.
- `ResearchValidator.cs` -- implement all field/referential/cycle/cap checks -- the fail-closed content gate this story exists to deliver.
- `ResearchValidatorTests.cs` (new) -- cover every I/O-matrix row above.
- `FactionWriteRoundTripTests.cs` -- extend for the `AvailableResearch` round-trip row.

**Acceptance Criteria:**
- Given a well-formed `ResearchDefinition` with a repeatable `Levels` ladder authored on `FactionDefinition.Research`, when content loads, then `FactionDefinition.LoadFromFile` accepts it and `GetResearch`/`IndexOfResearch` resolve it exactly like `GetBuilding`/`IndexOfUnit` do for buildings.
- Given any single malformed entry (duplicate/unknown id, empty `Levels`, non-positive level time, unregistered level-cost resource id, out-of-[0,1] cancel-refund fraction, unknown `Prerequisites`/`AvailableResearch` id, or a research→research cycle), when content loads, then the WHOLE load fails with every located error listed (never a partial/silent accept), and a cycle is reported first-fail with the exact chain.
- Given a `BuildingDefinition` declaring `AvailableResearch: string[]`, when content loads and saves, then it round-trips exactly like `Prerequisites`.

## Spec Change Log

_Empty until the first bad_spec loopback._

## Review Triage Log

### 2026-07-10 — Review pass
- intent_gap: 0
- bad_spec: 0
- patch: 8: (high 0, medium 5, low 3)
- defer: 1: (high 0, medium 0, low 1)
- reject: 8: (high 0, medium 0, low 8)
- addressed_findings:
  - `[medium]` `[patch]` A `ResearchDefinition` with a blank/missing `id` loaded without error and was silently excluded from the duplicate-id set, leaving it permanently unreferenceable via `GetResearch`/prerequisites. Added a required-id check to `ResearchValidator` mirroring `UnitDefinitionValidator`'s non-empty-id gate. Flagged by the Blind Hunter and Edge Case Hunter review layers.
  - `[medium]` `[patch]` `ResearchLevel.Cost` values were validated only for unknown resource keys, never for range — a negative amount (which "ADDS that resource each time it is spent" per `ResourceCostValidator`'s own documented footgun) or a value `>= 32768` loaded cleanly. Added the same range check `ResourceCostValidator` already applies to unit/building costs. Flagged independently by the Blind Hunter and Edge Case Hunter review layers.
  - `[medium]` `[patch]` `ResearchModifierDelta`'s four stat-delta floats (`MaxHealthDelta`/`AttackDamageDelta`/`MoveSpeedDelta`/`ArmorDelta`) had no NaN/Infinity guard, unlike every other numeric field this story introduces — a malformed value would silently load and later apply as a real permanent stat delta once 4.8b wires it up. Added a finite-value check. Flagged by the Blind Hunter review layer.
  - `[medium]` `[patch]` `CancelRefundFraction`'s `[0,1]` range check silently passed `NaN` (all comparisons with `NaN` evaluate false). Added an explicit `float.IsNaN` guard ahead of the range check. Flagged by the Edge Case Hunter review layer.
  - `[medium]` `[patch]` `BuildingCardPanel.CloneBuilding` (the in-app "Duplicate" button's clone path) hand-enumerates every authorable `BuildingDefinition` field and omitted the new `AvailableResearch`, silently stripping it from a duplicated building — the same defect class the function's own comment says was already fixed once for `RevivesHeroes`/`Hero`/`ShopStock`. Added `AvailableResearch` to the clone's field list. Flagged independently by the Blind Hunter and Verification Gap review layers.
  - `[low]` `[patch]` No character-set sanitization existed for research ids, unlike every other id-bearing content type (`UnitDefinitionValidator.SanitizeId`), inviting friction for 4.9's future editor (widget keys, JSON-key safety). Added the same `[a-z0-9_]` gate. Flagged by the Blind Hunter review layer.
  - `[low]` `[patch]` `ResearchValidator`/`FactionDefinition` had no defense against a null `Research` list itself, a null element inside `Research`/`Buildings`/`Levels`, or `GetResearch`/`IndexOfResearch` walking a null element — each would throw an NRE instead of a located error or graceful miss, on hand-malformed JSON. Added null-list and null-element guards throughout, mirroring `TechTreeValidator`'s existing `?? Array.Empty<string>()` idiom. Flagged by the Edge Case Hunter review layer.
  - `[low]` `[patch]` `AvailableResearch`'s round-trip was only exercised via `SyncFactionBuildings`/`Create`; the single-target `PatchFactionBuildingJson` Update path was untested for this field. Added a covering test. Flagged by the Blind Hunter review layer.
  - Deferred (1, low): no round-trip test exists anywhere for `Prerequisites` itself, so this story's "round-trips exactly like `Prerequisites`" claim (AC2) can't be checked against a shared assertion — pre-existing gap, not caused by this story. Recorded in `deferred-work.md`. Flagged by the Blind Hunter review layer.
  - Rejected (8, all low): the epic context's general `Validated<T>` framing vs. this story's throw-based gate (Intent Alignment — already resolved with evidence in this spec's Design Notes, favoring the AC's specific wording over the epic's looser paraphrase); zero research entries exist in shipped faction content yet (Intent Alignment — correctly scoped to Story 4.9); research cost validation is pinned to `{"ore","crystal"}` rather than the scenario's N-resource registry (Intent Alignment — deliberately chosen, see Boundaries); `DetectCycle` rebuilding its id-set independently of `Validate` (Blind Hunter — mirrors `TechTreeValidator`'s existing, accepted pattern, not a new defect); no `FactionWriter.SyncFactionResearch` write-back path exists yet (Blind Hunter — explicitly out of scope per this spec's Never section, Story 4.9's job); `MaxResearchPerFaction = 64`'s justification (Blind Hunter — already acknowledged as a judgment call in this spec's Design Notes); dense cross-referencing XML doc comments risking future rot (Blind Hunter — stylistic, no concrete failure scenario); `ResearchDefinition.DisplayName` left unvalidated (Blind Hunter — cosmetic display text, no functional consumer yet).

### 2026-07-10 — Review pass (follow-up)
- intent_gap: 0
- bad_spec: 0
- patch: 5: (high 0, medium 1, low 4)
- defer: 0
- reject: 14: (high 0, medium 0, low 14)
- addressed_findings:
  - `[medium]` `[patch]` `FactionDefinition.GetResearch`/`IndexOfResearch` NRE'd on a null `Research` list (malformed JSON `"research": null`), which `ResearchValidator` deliberately tolerates so the file LOADS without error — the getters only guarded null *elements*, not the null list itself, contradicting their own doc comment. Added a null-list guard to both, plus a covering test (`GetResearchAndIndexOfResearch_NullResearchList_NoThrowNoCrash`). Flagged independently by the Blind Hunter and Edge Case Hunter review layers.
  - `[low]` `[patch]` `ResearchModifierDelta`'s four stat-delta floats were finite-checked but not range-checked, unlike a level's `cost` (which the prior pass range-checked against the 16.16 Fixed ceiling). A finite-but-out-of-range value (e.g. `100000`, valid JSON) passed lint and would overflow when 4.8b quantizes it into the same Fixed field. Added a symmetric ±`Range` check in `CheckFiniteModifier` (deltas may legitimately be negative), plus `FiniteButOutOfFixedRangeModifierDelta_ProducesLocatedError` and `InRangeNegativeModifierDelta_NoError`. Flagged by the Edge Case Hunter review layer.
  - `[low]` `[patch]` The NaN/Infinity guard's `attack_damage_delta`/`move_speed_delta` `CheckFiniteModifier` calls had no covering assertion (the test set only `MaxHealthDelta`/`ArmorDelta`), so a dropped call would have gone unnoticed. Extended `NonFiniteModifierDeltaFields_ProduceLocatedErrors` to set and assert all four fields. Flagged by the Verification Gap review layer.
  - `[low]` `[patch]` The cycle-chain trim (`path.IndexOf(prereq)`) was exercised only by cycles rooted at the DFS entry node (`startIdx == 0`, no trimming), so a broken trim would misreport the chain undetected. Added `CycleWithAcyclicLeadIn_ReportsOnlyTheTrimmedSubChain` (an acyclic lead-in into a `b -> c -> b` cycle). Flagged by the Verification Gap review layer.
  - `[low]` `[patch]` `ResearchValidator.CostRange = 32768` hand-duplicated `ResourceCostValidator`'s `private const Range = 32768`, risking silent drift of the Fixed ceiling — inconsistent with the `KnownResourceIds` single-source-of-truth decision made one field above. Promoted `ResourceCostValidator.Range` to `internal` and pointed `CostRange` at it (now also the source for the new modifier-delta range check). Flagged by the Blind Hunter review layer.
  - Rejected (14, all low/cosmetic/by-design): `BuildingCardPanel.CloneBuilding`'s `AvailableResearch` fix has no automated test (Verification Gap — pre-existing: the whole `Node`-derived class is untestable without a GdUnit4 harness that doesn't exist; already recorded as a residual risk last pass); `time_ticks` has no upper ceiling (Edge Case Hunter — `int` tick count, no Fixed overflow, benign over-long order, beyond the spec's enumerated checks); duplicate/blank-id entries emit doubled field errors (Blind Hunter — cosmetic noise, list-all tolerates over-reporting, load still fails correctly); the per-faction cap counts null/duplicate/blank entries (Blind Hunter — spurious only on already-malformed JSON, negligible); whitespace-only id treated as valid node (Blind Hunter — matches `UnitDefinitionValidator`'s `IsNullOrEmpty` precedent); nondeterministic multi-key cost error ordering (Blind Hunter — display-only strings, pre-existing in `ResourceCostValidator`); clearing `available_research` writes `[]` vs omit-on-fresh (Blind Hunter — deliberately mirrors `prerequisites`); `available_research` lint resolving against de-duplicated ids (Blind Hunter — dominated by the same-load duplicate-id hard error, mirrors `TechTreeValidator`); cycle detection trusting duplicate-id fails elsewhere (Blind Hunter — correct coupling, mirrors `TechTreeValidator`); fractional/overflowing cost throws raw `JsonException` pre-validator (Blind Hunter — pre-existing for `UnitDefinition.Cost`, int-parse boundary); no duplicate-key guard on `available_research`/`prerequisites` arrays (Blind Hunter — matches `TechTreeValidator` permissiveness); the novel `MaxResearchPerFaction=64` cap (Intent Alignment — already a documented judgment call, rejected last pass); the NaN-guard's inline rationale slightly overstates JSON reachability (Intent Alignment/Blind Hunter — cosmetic doc, guard is harmless defense-in-depth); blank-id/char-set gate is stricter at load than the cited `UnitDefinitionValidator` Save-time precedent (Intent Alignment — a defensible correctness position, an unreferenceable id should fail).

## Design Notes

**Why no `Validated<T>` here:** the epic context describes a `Validated<T>`-gated pipeline, but the actual precedent for faction content (buildings/units/tech-tree/cost — Stories 4.1-4.3) is a throw-based aggregate-error gate in `LoadFromFile`, never `Validated<T>` (that type is minted solely by `ScenarioValidator` for scenario data, a different pipeline). The AC's own wording — "the same located-error validation gate as buildings/tech-tree content" — points at the real gate, so this story matches it rather than introducing a new pattern.

**Why cost/time live per-level, not on `ResearchDefinition` itself:** 4.8b's AC says "the next level's cost/time apply" after each completion, so each `ResearchLevel` carries its own `Cost`/`TimeTicks`; `CancelRefundFraction` stays definition-level since 4.8b refunds `CancelRefundFraction × currentLevelCost` — one fraction, applied to whichever level was in progress.

**Per-faction research count cap:** no existing "count cap" precedent exists in this codebase (checked `ScenarioValidator`, `EntityWorld`, `EffectCaps` — all runtime/memory caps, not authoring caps). Introduce `ResearchValidator.MaxResearchPerFaction = 64`, matching the structural-cap convention already used elsewhere in the forward architecture (`MaxSearchTargets`/`MaxSpawnCount` = 64), with a doc-comment justifying the number as a sanity ceiling, not a design-significant limit.

## Verification

**Commands:**
- `dotnet build godot/godot.sln -c Debug` -- expected: builds clean.
- `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj -c Release --filter FullyQualifiedName~Research` -- expected: all new tests green.
- `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj -c Release` -- expected: full Tier-1 suite still green (no regression to existing `FactionDefinition.LoadFromFile` callers/tests).

## Auto Run Result

Status: done

**Summary:** Added the `ResearchDefinition`/`ResearchLevel`/`ResearchModifierDelta` content model and a new `ResearchValidator` wired additively into `FactionDefinition.LoadFromFile`'s existing throw-based aggregate-error gate (never a `Validated<T>` mint — matches the actual buildings/tech-tree/cost precedent, not the epic's looser framing). `BuildingDefinition.AvailableResearch: string[]` mirrors `Prerequisites` and round-trips through load/save/Update/Duplicate. Validator covers: required non-empty/sanitized ids, duplicate-id detection, per-level field checks (non-positive time, unregistered/out-of-range/negative cost), out-of-[0,1] (and non-NaN) cancel-refund fraction, finite modifier-delta floats, referential lint (research prereqs against buildings ∪ research; `AvailableResearch` against research ids), a research→research-only first-fail cycle DFS, a per-faction count cap, and null-safety throughout. No runtime order path, `SimChecksum` fold, or UI — by design (4.8b/4.8c/4.9).

**Files changed:**
- `godot/src/Core/Definitions/ResearchDefinition.cs` (new) — `ResearchDefinition`/`ResearchLevel`/`ResearchModifierDelta` content classes.
- `godot/src/Core/Definitions/ResearchValidator.cs` (new) — whole-faction validator: id/dup-id/charset, per-level field+cost-range+NaN checks, referential lint, cycle DFS, count cap, null-safety (review patches folded in).
- `godot/src/Core/Definitions/FactionDefinition.cs` — `Research: List<ResearchDefinition>`, `GetResearch`/`IndexOfResearch` (null-element-safe), wired `ResearchValidator` into `LoadFromFile`.
- `godot/src/Core/Definitions/BuildingDefinition.cs` — `AvailableResearch: string[]`, mirroring `Prerequisites`.
- `godot/src/Core/Definitions/FactionWriter.cs` — persists `available_research` on building save.
- `godot/src/CreationSuite/BuildingCardPanel.Edit.cs` — `CloneBuilding` now copies `AvailableResearch` (review-patch fix; was silently dropped on Duplicate).
- `godot/ProjectChimera.Sim.Tests/Definitions/ResearchValidatorTests.cs` (new, 37 tests) — full I/O-matrix coverage plus review-patch regression tests.
- `godot/ProjectChimera.Sim.Tests/Definitions/FactionWriteRoundTripTests.cs` — `AvailableResearch` round-trip coverage across Create/Sync/Update/Duplicate paths.
- `_bmad-output/implementation-artifacts/deferred-work.md` — appended DW-82.
- `_bmad-output/implementation-artifacts/epic-4-context.md` — recompiled during planning to reflect the 4.8→4.8a/b/c split (not code; a dev-auto planning artifact).

**Review findings breakdown:** 8 patches applied (5 medium: blank/unreferenceable research ids, missing level-cost range validation matching `ResourceCostValidator`'s existing negative/overflow guard, non-finite `ResearchModifierDelta` floats, `NaN` bypassing the cancel-refund-fraction range check, and `BuildingCardPanel.CloneBuilding` silently dropping `AvailableResearch` on Duplicate — the same defect class the function's own comment says was already fixed once for other fields; 3 low: missing id character-set sanitization, several null-list/null-element NRE gaps, and an untested `PatchFactionBuildingJson` Update-path round-trip for `AvailableResearch`). 1 finding deferred to `deferred-work.md` (DW-82: no round-trip test exists anywhere for `Prerequisites` itself, a pre-existing gap surfaced incidentally, not caused by this story). 8 findings rejected — three from the Intent Alignment audit (the epic's general `Validated<T>` framing vs. this story's throw-based gate, already resolved with evidence in Design Notes; zero shipped research content yet, correctly scoped to 4.9; cost validation pinned to ore/crystal not the N-resource registry, deliberately chosen) and five from Blind Hunter (a redundant-but-precedented id-set rebuild in `DetectCycle`; no `FactionWriter.SyncFactionResearch` write-back path, explicitly out of scope; the `MaxResearchPerFaction=64` sanity-ceiling justification, already acknowledged as a judgment call; dense cross-referencing doc comments, stylistic; `DisplayName` left unvalidated, cosmetic).

**Verification performed:** `dotnet build godot/godot.sln -c Debug` clean (0 warnings/errors) both before and after the patch pass. `dotnet test ... --filter FullyQualifiedName~Research`: 25/25 before patches, 37/37 after. Full Tier-1 suite (`dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj -c Release`): 1319 passed/1 pre-existing skip before, 1331 passed/1 pre-existing skip after — no regressions either pass. Matrix-test audit: all 12 I/O-matrix rows map to named, passing tests. All verification independently re-run by the orchestrating session, not just trusted from subagent reports.

**Residual risks:**
- `BuildingCardPanel.CloneBuilding`'s fix has no automated regression test — the class has zero test coverage anywhere in this codebase (Godot `Node`-derived, not unit-testable without a GdUnit4 harness that doesn't exist yet for it).
- `MaxResearchPerFaction = 64` is a new authoring-cap concept with no prior codebase precedent; its value is a documented judgment call (sanity ceiling), not a tuned balance number.
- No runtime consumer exists yet for `Research`/`AvailableResearch`/`ResearchValidator` (by design — 4.8b/4.9 own that), so this content model is currently inert except at import-time validation.
- `.bmad-loop/policy.toml` was found modified in the working tree at review time (`max_tokens_per_story` 2M→4M) but is unrelated to this story's diff — left uncommitted per protocol, listed below as a residual artifact.

**Follow-up review recommended:** true — 8 patches is a non-trivial volume with 5 at medium severity, one of which (`BuildingCardPanel.CloneBuilding`) has no automated test guarding it going forward, and the null-safety hardening touched a meaningful surface of the new validator. Every fix is narrow, matches existing codebase conventions, and the full suite is green, but the untested UI-layer fix and the volume together warrant an independent second pass.

**Residual artifacts (not part of this change, left in place):** `.bmad-loop/policy.toml` (pre-existing uncommitted modification, unrelated to this story).

---

### Follow-up review pass (2026-07-10)

An independent second review pass (Blind Hunter, Edge Case Hunter, Verification Gap, Intent Alignment) ran over the full baseline→HEAD diff. It confirmed the first pass's fixes and surfaced 5 patch-worthy items — no intent gaps, no spec defects. All 5 were auto-fixed in this pass:

- **[medium]** `FactionDefinition.GetResearch`/`IndexOfResearch` NRE'd on a null `Research` list (`"research": null`), a file the validator deliberately loads without error — the getters guarded null elements but not the null list, contradicting their own doc comment. Added a null-list guard to both, plus a covering test.
- **[low]** Added a symmetric ±`Range` (16.16 Fixed ceiling) check to the four `ResearchModifierDelta` floats — they quantize into the same Fixed fields as level costs (already range-checked), so a finite-but-out-of-range value (`100000`) would have overflowed at 4.8b quantization. Plus over-range and in-range-negative tests.
- **[low]** Extended the non-finite modifier-delta test to set/assert all four fields (`attack_damage_delta`/`move_speed_delta` were previously unverified).
- **[low]** Added a cycle test with a non-cyclic lead-in (`b -> c -> b` reached via `lead`) to exercise the previously-unverified chain-trim (`startIdx != 0`).
- **[low]** Promoted `ResourceCostValidator.Range` to `internal` and pointed `ResearchValidator.CostRange` at it (was a hand-duplicated `32768`), eliminating Fixed-ceiling drift risk — same single-source-of-truth rationale as `KnownResourceIds`.

14 further findings were rejected as cosmetic / by-design / already-documented (see the follow-up triage-log entry above); none deferred.

**Files changed this pass:**
- `godot/src/Core/Definitions/FactionDefinition.cs` — null-list guard in `GetResearch`/`IndexOfResearch`.
- `godot/src/Core/Definitions/ResearchValidator.cs` — modifier-delta range check; `CostRange` now references `ResourceCostValidator.Range`.
- `godot/src/Core/Definitions/ResourceCostValidator.cs` — `Range` promoted `private`→`internal`.
- `godot/ProjectChimera.Sim.Tests/Definitions/ResearchValidatorTests.cs` — 4 new/extended tests (41 total, up from 37).

**Verification:** `dotnet build godot/godot.sln -c Debug` clean (0 errors). `dotnet test ... --filter FullyQualifiedName~Research`: 41/41. Full Tier-1 suite: 1335 passed / 1 pre-existing skip (up from 1331; +4 new tests, no regressions).

**Follow-up review recommended:** false — this second pass produced only narrow, well-localized fixes (one medium null-guard now covered by a test; the rest low-severity range/test/maintainability items), all matching existing conventions and green across the full suite. The review has converged: the pass found only edge-case and consistency items, no new high-severity or broad-surface issues.
