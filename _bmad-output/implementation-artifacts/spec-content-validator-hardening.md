---
title: 'Content-validator hardening (DW-55/89/100/101/103/111/115)'
type: 'bugfix'
created: '2026-07-27'
status: 'done'
baseline_revision: '20e9b28e0b7a35af78a1145254af95f21cadfa01'
final_revision: 'e84eee8e7dc607039b8dd377248e99192b9b8644'
review_loop_iteration: 0
followup_review_recommended: false
context:
  - '{project-root}/godot/CLAUDE.md'
  - '{project-root}/_bmad-output/project-context.md'
warnings:
  - oversized
  - multiple-goals
---

<intent-contract>

## Intent

**Problem:** The `Core/Definitions` content validators have a cluster of latent gaps: `TechTreeValidator`/`ResourceCostValidator` and four `FactionDefinition` unit getters throw `NullReferenceException` on a null list or null element (malformed-but-parseable JSON), `FactionValidator` never checks duplicate building/research ids or a research-id/building-id cross-namespace collision or negative starting resources, and `BuildingDefinitionValidator` cannot tell an omitted `hp` (silently defaults to 100 via inherited `UnitDefinition.Hp`) from an authored `100`.

**Approach:** Add defensive null-list + `if (x is null) continue;` per-element guards to every `def.Units`/`def.Buildings` loop in `TechTreeValidator` and `ResourceCostValidator`, and to the four `FactionDefinition` unit getters (mirroring the research getters that already skip nulls). Add duplicate-building-id, duplicate-research-id, cross-namespace-collision, and `StartingOre`/`StartingCrystal >= 0` checks to `FactionValidator.Validate` (inherited by `ValidateComplete`). Track `hp` JSON-presence with a buildings-only `new`-shadow `Hp` setter so `BuildingDefinitionValidator` emits a located "hp is required but missing" error, and keep the Building Card Editor + `FactionWriter` in sync so hp is always authored/serialized for buildings.

## Boundaries & Constraints

**Always:**
- Sim layer stays Godot-free (`Core/Definitions`, `FactionWriter`): pure C#, no `using Godot;`, no `float` gameplay math introduced (validators only read authoring values and emit strings — unchanged).
- Guards are additive and defensive: a null list is treated as empty and a null element is skipped (`continue`), never a new thrown exception. Do not change the wording of any *existing* located error message; only add new ones.
- New `FactionValidationResult` errors use `(FieldPath, Message)` tuples with `Located(id, path, reason)` wording; field paths: `"buildings"`, `"research"`, `"starting_ore"`, `"starting_crystal"` (the latter two are the field paths DW-114's wizard `StepForError` routing will key on — name them exactly).
- The `hp`-presence flag must be true when hp is set through ANY path (JSON deserialize, object initializer, or a `def.Hp = v` assignment) and false only when hp was never assigned — verified viable on net8 in-box System.Text.Json (`new`-shadow property does not collide).
- Shipped `alpha_faction.json`/`beta_faction.json` and `_buildingcard_sample.json` already author hp on every building (verified) — they must still `LoadFromFile` and pass `ValidateComplete` unchanged.

**Block If:**
- Building the Godot-free Tier-1 test project fails for a reason unrelated to this change (pre-existing red baseline you cannot attribute to your edits) — HALT `blocked` with the failing baseline.
- The `new`-shadow `Hp` mechanism throws an STJ member-collision at deserialize despite the net8 pre-check (i.e. the project pins a non-in-box System.Text.Json that behaves differently) — HALT `blocked` describing it, rather than silently degrading the presence check.

**Never:**
- Do NOT make `UnitDefinition.Hp` nullable or change it in any way (buildings-only fix — the whole point of the `new`-shadow is to avoid the UnitDefinition-wide blast radius that touches unit spawning/validation).
- Do NOT remove or weaken the existing duplicate-building-id check in `TechTreeValidator` or the duplicate-research-id check in `ResearchValidator` (they serve those validators' direct callers); FactionValidator's new within-namespace duplicate checks are intentionally additive/self-contained and may co-report with them (list-all).
- Do NOT edit the deferred-work ledger (`deferred-work.md`) — the orchestrator records resolution.
- Do NOT touch `PrimaryUnit`/`GetBuilding` or add NaN-scrubbing beyond the two starting-resource fields — stay within the seven named DW items.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Null unit element (TechTree) | `def.Units = [null, {valid}]` through `TechTreeValidator.Validate` | Null skipped, valid unit still linted; returns normally | No NRE |
| Null buildings list (ResourceCost) | `def.Buildings = null` through `ResourceCostValidator.Validate` | Treated as empty; returns empty/other errors only | No NRE |
| Null unit element (getters) | `def.Units = [null, {id:"w"}]`, call `GetUnit("w")`/`IndexOfUnit("w")`/`GetUnitByCategory(..)`/`GetUnitsByCategory(..)` | Null skipped, resolves the real unit | No NRE |
| Duplicate building id | two buildings share `"barracks"` | `FactionValidator.Validate` returns a located `"buildings"` error naming the repeated id | Faction invalid |
| Duplicate research id | two research entries share an id | Located `"research"` error naming the repeated id | Faction invalid |
| Cross-namespace collision | a research id equals a building id | Located `"research"` error stating the id collides with a building id | Faction invalid |
| Negative starting ore | `starting_ore = -1` | Located `"starting_ore"` error (`>= 0`); NaN also rejected | Faction invalid; throws through `LoadFromFile` |
| Valid distinct ids + non-negative starts | clean faction | `Validate`/`ValidateComplete` `.Ok` | No error |
| Omitted hp (JSON import) | building JSON with no `hp` key | `LoadFromFile` throws a located `"hp"` "required but missing" error | Load fails |
| Authored hp (any value incl. 100) | building JSON `"hp": 100` or `new BuildingDefinition{Hp=100,...}` | Passes the hp-presence check (still rejects `hp <= 0`) | No error |

</intent-contract>

## Code Map

- `godot/src/Core/Definitions/TechTreeValidator.cs` -- DW-100: null-guard every `def.Buildings`/`def.Units` foreach (`Validate` build+lint loops :52-83, `DetectCycle` :157-175, `ValidateProposedEdge` :120-130).
- `godot/src/Core/Definitions/ResourceCostValidator.cs` -- DW-101: null-guard the two loops in `Validate` (:54-58).
- `godot/src/Core/Definitions/FactionDefinition.cs` -- DW-103: null-guard `GetUnit`/`IndexOfUnit`/`GetUnitByCategory`/`GetUnitsByCategory` (:102-171), mirroring `GetResearch`/`IndexOfResearch`.
- `godot/src/Core/Definitions/FactionValidator.cs` -- DW-111/89/115: add duplicate-building/research, cross-namespace, and starting-resource checks to `Validate` (after the duplicate-unit-id loop, ~:159-177; `ValidateComplete` inherits them).
- `godot/src/Core/Definitions/BuildingDefinition.cs` -- DW-55: add the `new`-shadow `Hp` setter + `[JsonIgnore] HpAuthored`.
- `godot/src/Core/Definitions/BuildingDefinitionValidator.cs` -- DW-55: emit "hp required but missing" when `!def.HpAuthored` (before/besides the existing `Hp <= 0` check, :81-83).
- `godot/src/Core/Definitions/FactionWriter.cs` -- DW-55 keep-in-sync: `ApplyBuildingFields` (:494) must always emit `"hp"` for buildings (it is now required), overriding `PutFloat`'s omit-at-100 (:238).
- `godot/src/CreationSuite/BuildingCardPanel.Edit.cs` -- DW-55 keep-in-sync: `DoCreate` (:813) must author `Hp` so a new building isn't falsely flagged.
- `godot/src/CreationSuite/TechTreePanel.cs` -- DW-89 presentation half: `OnNodeSelected` (:425-436) resolve research/building nodes last-wins (match `RebuildGraph`'s last-wins `researchById`/`buildingById`).
- `godot/ProjectChimera.Sim.Tests/Definitions/{BuildingDefinitionValidatorTests,FactionValidatorTests,TechTreeValidatorTests,ResourceCostValidatorTests,FactionWriteRoundTripTests}.cs` -- update fixtures + add coverage (see Execution).

## Tasks & Acceptance

**Execution:**
- `godot/src/Core/Definitions/TechTreeValidator.cs` -- In `Validate`, `DetectCycle`, and `ValidateProposedEdge`, change each `foreach (X x in def.Buildings)` / `def.Units` to iterate `def.Buildings ?? new List<BuildingDefinition>()` (resp. Units) and add `if (x is null) continue;` as the loop's first statement -- kills the NRE class for every caller of these public methods (DW-100).
- `godot/src/Core/Definitions/ResourceCostValidator.cs` -- Same null-list + `if (u is null) continue;` / `if (b is null) continue;` guards on the two `Validate` loops -- DW-101.
- `godot/src/Core/Definitions/FactionDefinition.cs` -- Add `if (Units == null) return <null/-1/empty>;` and `if (u is null) continue;` (indexed loops: `if (Units[i] is null) continue;`) to the four unit getters, mirroring `GetResearch`/`IndexOfResearch` -- DW-103.
- `godot/src/Core/Definitions/BuildingDefinition.cs` -- Add `private bool _hpAuthored;` + `[JsonPropertyName("hp")] public new float Hp { get => base.Hp; set { base.Hp = value; _hpAuthored = true; } }` + `[JsonIgnore] public bool HpAuthored => _hpAuthored;` -- DW-55 presence tracking (getter returns `base.Hp`, so all existing `def.Hp` reads are unchanged).
- `godot/src/Core/Definitions/BuildingDefinitionValidator.cs` -- Before the `def.Hp <= 0f` check, add `if (!def.HpAuthored) errors.Add(("hp", Located(id, "hp", "is required but missing (a building's HP must be authored)."))); else if (def.Hp <= 0f) { ...existing... }` so an omitted hp is a distinct located error and an authored-but-non-positive hp still reports -- DW-55.
- `godot/src/Core/Definitions/FactionValidator.cs` -- After the duplicate-unit-id loop, add: (a) duplicate-building-id and duplicate-research-id `TryAdd` checks (skip null/blank ids), (b) a cross-namespace check flagging any research id that also exists as a building id, (c) `StartingOre`/`StartingCrystal` `>= 0` checks (also reject NaN) with field paths `starting_ore`/`starting_crystal`. All list-all located errors. Place AFTER the structural pre-check early-return (lists are non-null there); do not add anything to `errors` before that early-return -- DW-111/89/115.
- `godot/src/Core/Definitions/FactionWriter.cs` -- In `ApplyBuildingFields`, after `ApplyFields(obj, d)`, set `obj["hp"] = d.Hp;` unconditionally (buildings require hp; overrides the shared omit-at-100) -- keeps raw-pane/serialize round-trips authored (DW-55).
- `godot/src/CreationSuite/BuildingCardPanel.Edit.cs` -- In `DoCreate`, add `Hp = 100f` to the `new BuildingDefinition { ... }` initializer so a freshly-created building is hp-authored (the SpinBox already seeds/edits from `def.Hp`) -- DW-55.
- `godot/src/CreationSuite/TechTreePanel.cs` -- In `OnNodeSelected`, change the research and building resolutions from `FirstOrDefault` to `LastOrDefault` so a clicked node resolves the same last-declared def `RebuildGraph` rendered -- DW-89 presentation half.
- `godot/ProjectChimera.Sim.Tests/Definitions/BuildingDefinitionValidatorTests.cs` -- Author `Hp` in `Validate_AllFieldsPresent_IsOk`, `Validate_AllThreeMissing_ReturnsThreeLocatedErrors`, and the `ValidBuilding()` helper; add tests: hand-built building omitting Hp returns an `"hp"` "required" error; authored `Hp=100` returns no hp error; and a faction JSON building omitting `"hp"` throws through `LoadFromFile` -- DW-55 coverage.
- `godot/ProjectChimera.Sim.Tests/Definitions/FactionValidatorTests.cs` -- Add tests for duplicate building id, duplicate research id, cross-namespace collision, negative `starting_ore`, negative `starting_crystal`, and their happy paths (`.Ok`); add one `LoadFromFile` throw-through test for a negative starting resource -- DW-111/89/115 coverage.
- `godot/ProjectChimera.Sim.Tests/Definitions/TechTreeValidatorTests.cs` + `ResourceCostValidatorTests.cs` -- Add null-list and null-element tests proving no NRE and correct linting of the surviving elements -- DW-100/101 coverage. Add DW-103 getter null-safety coverage here or in a new `FactionDefinitionGetterNullSafetyTests.cs` (a `FactionDefinition` with a null Units element resolves via all four getters without NRE).
- `godot/ProjectChimera.Sim.Tests/Definitions/FactionWriteRoundTripTests.cs` -- If any assertion expects a building to omit `hp` at the default, update it to expect `hp` always present for buildings; building hp round-trip must be preserved -- DW-55 keep-in-sync.

**Acceptance Criteria:**
- Given a parseable faction with `"units": null`, `"units": [null, {...}]`, `"buildings": null`, or a null buildings element, when it reaches `TechTreeValidator.Validate`, `ResourceCostValidator.Validate`, or any of the four `FactionDefinition` unit getters, then no `NullReferenceException` is thrown and non-null elements are processed normally.
- Given a faction with a duplicate building id, a duplicate research id, or a research id equal to a building id, when `FactionValidator.Validate` runs, then it returns `.Ok == false` with a located error naming the offending id under the correct field path.
- Given a faction with `starting_ore < 0` or `starting_crystal < 0` (or NaN), when validated or loaded via `LoadFromFile`, then it fails with a located `starting_ore`/`starting_crystal` error.
- Given a building JSON that omits `hp`, when `FactionDefinition.LoadFromFile` runs, then it throws an `InvalidOperationException` whose message locates the building id and the required `hp` field; given a building that authors `hp` (including `100`), it loads without an hp error.
- Given the shipped `alpha_faction.json`/`beta_faction.json`, when loaded and `ValidateComplete`-checked, then they still pass unchanged.
- Given a building created in the Building Card Editor's Simple form or round-tripped through its raw-JSON pane, when saved, then it is not falsely rejected for a missing hp.

## Spec Change Log

_No bad_spec loopback occurred; empty._

## Review Triage Log

### 2026-07-27 — Follow-up review pass
- intent_gap: 0
- bad_spec: 0
- patch: 2: (high 0, medium 0, low 2)
- defer: 2
- reject: 11
- addressed_findings:
  - `[low]` `[patch]` `FactionWriter.ApplyBuildingFields` hp write now has the `else obj.Remove("hp");` its sibling fields all have — a never-authored (`HpAuthored=false`) building no longer leaves a stale `"hp"` key in a reconciled `JsonObject`, so the omitted-vs-authored distinction is preserved on the write side even when patching an existing object (edge-case-hunter + verification-gap). Added `SerializeBuildingClean_HpNeverAuthored_OmitsHp` to pin the false branch (previously untested — every writer test authored `Hp=100f`).
  - `[low]` `[patch]` Added `NaNStartingCrystal_Validate_ReturnsLocatedError` — the DW-115 `!float.IsFinite` guard on `StartingCrystal` is a separate code line from ore's and had no NaN/non-finite test (only the negative case); ore's NaN test does not cover the crystal branch (verification-gap coverage asymmetry).
- deferred (real, pre-existing, out of this story's intent-scope → ledger DW):
  - `FactionDefinition.GetBuilding` still NREs on a null `Buildings` list/element (asymmetric with the DW-103 unit-getter hardening) — intent's Boundaries explicitly say "Do NOT touch `PrimaryUnit`/`GetBuilding`", so correctly not fixed here.
  - `TechTreePanel.RebuildGraph`'s building loop dereferences `b.Id` without a null-element guard (its research loop guards; the DW-89-hardened `OnNodeSelected` guards) — Godot-presentation, not Tier-1-testable, outside the seven-DW scope.
- rejected (11): DW-89 units-namespace exclusion (message says exactly "buildings and research" — accurate to what's enforced; units out of DW-89 scope per intent, already rejected in the prior pass); `HpAuthored` non-virtual-shadow fragility (intent deliberately prescribes the `new`-shadow mechanism and documents the caveat; the flag is monotonic and every construction path authors hp — no live defect; the one base-typed write site `BalanceSuggestionApplier.SetField` only mutates an already-authored building); duplicate-id co-reporting with sub-validators (intent-sanctioned list-all — "may co-report with them"); non-deterministic `Dictionary.Keys` error ordering (validator output is authoring-time, not hashed/goldened; tests use `Contains`); omitted-hp load break (the intended DW-55 behavior; shipped factions author hp); `LastOrDefault`↔`RebuildGraph` coupling (edge-case-hunter verified RebuildGraph is last-wins — the change is correct); `TechTreePanel.LastOrDefault` lacking a test (Godot UI → existing Epic-10 live-verification deferral); STJ single-`"hp"`-key not directly serialize-tested (covered indirectly via round-trip + guarded by the spec's Block-If); throwaway `new List<>()` allocations on the null-guard path (perf micro-nit on an editor path); culture-dependent float interpolation in error messages (low i18n nit; no consumer asserts the numeric body); in-code `new BuildingDefinition` blast radius (blind-hunter's own grep + verification-gap confirmed the sole production site `BuildingCardPanel.DoCreate` and `CloneBuilding` both author `Hp` — covered).
- notes: the 4-reviewer panel converged hard on `GetBuilding` (raised independently by blind-hunter and edge-case-hunter) — routed to defer solely because the intent's own Boundaries forbid touching it, which is the only admissible scope authority; it is a real latent NRE and now tracked on the ledger.

### 2026-07-27 — Review pass
- intent_gap: 0
- bad_spec: 0
- patch: 6: (high 0, medium 0, low 6)
- defer: 0
- reject: 2: (low 2)
- addressed_findings:
  - `[low]` `[patch]` `TechTreePanel.OnNodeSelected` building lookup made null-safe (`Buildings?.LastOrDefault(b => b != null && ...)`) to match its sibling research line — the line this story changed to `LastOrDefault`.
  - `[low]` `[patch]` `BuildingDefinitionValidator` authored-hp check now rejects non-finite hp (`!float.IsFinite(def.Hp) || def.Hp <= 0f`), consistent with the bundle's "bounds edge cases" intent and the new starting-resource guard.
  - `[low]` `[patch]` `FactionValidator` starting_ore/starting_crystal checks now use `!float.IsFinite(x) || x < 0f` so +Infinity is rejected, not just NaN/negatives.
  - `[low]` `[patch]` `BuildingDefinition.HpAuthored` docstring corrected — the `new`-shadow is non-virtual, so it tracks BuildingDefinition-typed assignments (JSON deserialize / initializer / `BuildingDefinition`-typed `def.Hp = v`), not base-`UnitDefinition`-typed writes.
  - `[low]` `[patch]` `FactionWriter.ApplyBuildingFields` writes hp only when `d.HpAuthored` (`if (d.HpAuthored) obj["hp"] = d.Hp;`) so a save never silently materializes an omitted hp into 100 — preserving DW-55's omitted-vs-authored distinction on the write side while still always emitting hp for a properly-authored building.
  - `[low]` `[patch]` Added a `TechTreeValidator.ValidateProposedEdge` null-buildings-element test (the DW-100 guards in its two scan loops were previously covered only via `Validate`).
- rejected: unit↔building/research cross-namespace check (intent narrowly scopes DW-89 to research-vs-building; units are not TechTreePanel GraphNodes — not a real gap); duplicate-building-loop null/blank divergence from the unit loop (cosmetic — null can't reach it past the structural pre-check, blank building ids are caught per-building).
- notes (not actionable here): DW-114 wizard `StepForError` routing for the new starting-resource field paths remains DW-114's own scope per the intent (this story only provides the `starting_ore`/`starting_crystal` field paths); the two Godot-Node presentation edits (`TechTreePanel`, `BuildingCardPanel.DoCreate`) are not Tier-1-testable and fall under the project's existing Epic-10 live-verification deferral.

## Design Notes

**DW-55 mechanism (verified on net8 in-box STJ):** `BuildingDefinition : UnitDefinition`; `UnitDefinition.Hp` is a non-nullable `float` defaulting to `100`. A `new`-keyword shadow on `BuildingDefinition` with the same `[JsonPropertyName("hp")]` does NOT collide on net8 — STJ binds the derived member, the setter records presence, the getter returns `base.Hp` (so `BuildingStore.Create`, cloning, and all `def.Hp` reads are unchanged). Confirmed empirically: `{"hp":55}` → `Hp=55, HpAuthored=true`; `{}` → `Hp=100, HpAuthored=false`; serialize emits a single `"hp"` key.

**Why the content-hash goldens are safe:** `ContentHash`/`CanonicalModelHash` are hand-written FNV-64 typed field walks (`CanonicalFold`), reading `u.Hp` by typed access in a fixed order with collections sorted by id — JSON member order is irrelevant, and the folded value is unchanged. No golden re-baseline, no SimChecksum fold (dormant/unchanged sim arrays).

**DW-111 co-reporting is intentional:** `TechTreeValidator` already reports duplicate building ids and `ResearchValidator` duplicate research ids inside the `FactionValidator` pipeline; the new FactionValidator checks make it self-contained and add the cross-namespace collision (DW-89) that no sub-validator covers. A within-namespace duplicate may therefore surface two located errors (both true) — acceptable list-all behavior; tests assert with `Contains`, not exact counts.

**DW-89 two halves:** the validator-side cross-namespace check prevents a colliding id from ever being *saved*; the `OnNodeSelected` `LastOrDefault` fix aligns the in-memory (mid-edit) selection resolution with `RebuildGraph`'s last-wins rendering. `TechTreePanel.cs`/`BuildingCardPanel.Edit.cs` are Godot presentation — compile-checked via the full `godot.csproj` build, not Tier-1.

## Verification

**Commands:**
- `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` -- expected: full Tier-1 suite green (Godot-free); new DW-55/89/100/101/103/111/115 tests pass; existing content-hash/golden/round-trip tests unchanged (no golden re-baseline). A lone `CanonicalModelHashPerf…StaysUnderTheRegressionCeiling` timing flake is not a regression — re-run in isolation to confirm.
- `dotnet build godot/godot.csproj -c Debug` -- expected: compiles clean, proving the `TechTreePanel.cs` / `BuildingCardPanel.Edit.cs` presentation edits build against the Godot .NET refs.

**Manual checks (if no CLI):**
- If the full `godot.csproj` build cannot run headlessly in this environment, inspect the two presentation edits by eye: `DoCreate` authors `Hp`, and `OnNodeSelected` uses `LastOrDefault` for both branches — and note the deferred in-engine confirmation in the Auto Run Result.

## Auto Run Result

Status: done

### Summary
Follow-up review pass on the previously-`done` content-validator hardening change (DW-55/89/100/101/103/111/115). A 4-lens reviewer panel (adversarial / edge-case / verification-gap / intent-alignment) ran against the baseline→final diff. Two low-severity patches were applied and verified; two real pre-existing findings were deferred to the ledger; eleven findings were rejected. No `intent_gap` and no `bad_spec` — the implemented change is the internally-consistent reading of the intent.

### Files changed this pass
- `godot/src/Core/Definitions/FactionWriter.cs` — added `else obj.Remove("hp");` to the DW-55 hp write so a never-authored building drops a stale `"hp"` key on a reconciled object, matching every sibling field's `else Remove` pattern.
- `godot/ProjectChimera.Sim.Tests/Definitions/FactionWriteRoundTripTests.cs` — added `SerializeBuildingClean_HpNeverAuthored_OmitsHp`, pinning the previously-untested false branch of the `HpAuthored` write gate.
- `godot/ProjectChimera.Sim.Tests/Definitions/FactionValidatorTests.cs` — added `NaNStartingCrystal_Validate_ReturnsLocatedError`, closing the NaN/non-finite coverage asymmetry between the ore and crystal DW-115 guards.

### Review findings breakdown
- **Patched (2, both low):** FactionWriter hp `else Remove` + its false-branch test; NaN starting_crystal test.
- **Deferred (2):** `FactionDefinition.GetBuilding` null-safety gap (intent's Boundaries forbid touching `GetBuilding`); `TechTreePanel.RebuildGraph` building-loop null-element NRE (Godot-presentation, out of the seven-DW scope). Both appended as new ledger entries.
- **Rejected (11):** see Review Triage Log for the enumerated rationale (DW-89 units-scope, `HpAuthored` shadow-fragility, co-reporting, key-order nondeterminism, omitted-hp load break, LastOrDefault coupling, UI test absence, STJ single-key test, allocation nit, culture float-format, in-code blast radius).

### Follow-up review recommendation
`false`. Patched findings this pass: 2 × low, 0 × medium, 0 × high. Score = 3×0 + 1×2 = 2 (< 5); no high-severity patch. → `followup_review_recommended: false`.

### Verification performed
- `dotnet test ProjectChimera.Sim.Tests` (full Tier-1): **3502 passed, 1 skipped, 3 failed**. The 3 failures are CPU-contention timing/async flakes — two `CanonicalModelHashPerfTests` perf-ceiling flakes and one `BalanceAnalysisGenerationTests` LLM `generation callback did not fire within the timeout` async flake — none touching `Core/Definitions`. Re-run in isolation: **7/7 passed**, confirming flake-not-regression per the Verification note.
- Touched DW test classes (`FactionValidatorTests`, `FactionWriteRoundTripTests`, `BuildingDefinitionValidatorTests`, `FactionDefinitionGetterNullSafetyTests`, `ResourceCostValidatorTests`, `TechTreeValidatorTests`): **169/169 passed**, including both new patch tests.
- The `dotnet build godot/godot.csproj` presentation-build command was not re-run this pass: the patch is sim-layer + test only (`FactionWriter.cs` compiles under the Tier-1 project, which built and passed); the presentation files (`TechTreePanel.cs`, `BuildingCardPanel.Edit.cs`) were untouched by this pass and remain as validated in the original run.

### Residual risks
- The two deferred items (`GetBuilding` null-safety; `RebuildGraph` null-element) are real latent NREs left untouched by design (intent scope); they now live on the deferred-work ledger for later focused attention.
- The DW-89 `TechTreePanel.OnNodeSelected` `LastOrDefault` behavior and `BuildingCardPanel.DoCreate` hp-authoring remain confirmable only via in-engine live verification (existing Epic-10 deferral) — unchanged by this pass.

