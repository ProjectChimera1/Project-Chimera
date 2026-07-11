---
title: 'Faction schema extension + validator (AR-39, AR-12, FR-18 data)'
type: 'feature'
created: '2026-07-10'
status: 'done'
baseline_revision: '1d82b53b2c1c9300b585c3bc51a89db064197fda'
final_revision: 'b2ed1f5'
review_loop_iteration: 0
followup_review_recommended: false
context: []
warnings: [oversized]
---

<intent-contract>

## Intent

**Problem:** `FactionDefinition` has no `ai_preset`, signature-mechanic descriptor, or hero/persistence fields, and no `FactionValidator` exists — a malformed faction (dangling prerequisite, unknown AI preset, invalid color, duplicate unit id, missing mesh, incomplete roster) is never rejected before reaching a match.

**Approach:** Add six new optional, backward-compatible fields to `FactionDefinition` (ai_preset, signature-mechanic id/display/effect-slot, hero unit reference, persistence flag). Add `FactionValidator` (static class, `FactionValidationResult` — list-all located errors, matching `BuildingDefinitionValidator`/`PersistenceManifestValidator`'s existing shape) as the ONE canonical faction-validity gate, absorbing `FactionDefinition.LoadFromFile`'s four existing inline validator calls (Building/TechTree/ResourceCost/Research — unchanged) plus five new checks: unknown/empty `ai_preset`, invalid `color`, duplicate unit id, missing `mesh_path` (units + buildings), missing required roles.

## Boundaries & Constraints

**Always:**
- `FactionValidator` is pure C# (no `using Godot`), lives in `godot/src/Core/Definitions/`, mirrors `BuildingValidationResult`/`ManifestValidationResult`'s exact shape: `readonly struct FactionValidationResult { bool Ok; IReadOnlyList<(string FieldPath, string Message)> Errors; static Valid; }`, located-message idiom `"faction '<id>'.<field>: <reason>."`.
- All new `FactionDefinition` fields are optional with defaults that keep `alpha_faction.json`/`beta_faction.json` (unchanged, no `ai_preset`/hero/persistence keys today) loading successfully and passing `FactionValidator` — `AiPreset` MUST default to a valid closed-set member (NOT `""`), since an empty/unknown preset is a FAIL case.
- `ai_preset` validity is a closed string set owned by `FactionValidator` (no existing catalog exists anywhere in the repo — `AiDifficulty {Easy,Normal,Hard}` in `src/AI/AiOpponentSystem.cs` is a match-difficulty knob, an unrelated concept, never reused here). Seed the set with exactly one member, `"balanced"`, as both the closed-set's sole entry and `FactionDefinition.AiPreset`'s default — concrete preset ids for alpha/beta are Story 5.3's job, not this one's. Document this as a deliberately minimal seed, extended in place (no schema change) by later stories.
- Required-roles check: a faction's `Units` must include at least one `Category` (case-insensitive) equal to `"Worker"` AND at least one of `"Melee"|"Ranged"|"Siege"|"Air"`. Missing either half is a distinct located error. Document this exact definition in Design Notes — nothing elsewhere in the repo defines "required roles" more precisely.
- Duplicate-unit-id check and missing-mesh-path check are new, targeted, minimal scans (mirror `TechTreeValidator`'s `buildingById.TryAdd` idiom) — do NOT invoke `UnitDefinitionValidator`'s full per-unit gate over `Units` (that runs stat/cost/enum checks never enforced at faction load before; doing so risks new, out-of-scope load failures on existing content, contradicting AC1).
- `FactionDefinition.LoadFromFile` keeps identical external behavior: aggregate every error, throw `InvalidOperationException` joined by `\n` if any exist.
- New tests live in `godot/ProjectChimera.Sim.Tests/Definitions/FactionValidatorTests.cs`, matching `BuildingDefinitionValidatorTests.cs`'s conventions.

**Block If:** Running the new checks against the current, unmodified `alpha_faction.json`/`beta_faction.json` produces ANY validator error — HALT with status `blocked`, blocking condition `existing showcase faction fails new validator`, and name the offending field/id (this would mean AC1 cannot hold without also touching Story 5.3's content, which is out of scope here).

**Never:**
- Never touch `FactionWriter.cs` (DOM-patch save path) — wiring the wizard/editor to write these new fields is Story 5.5/5.6's job.
- Never touch `alpha_faction.json`/`beta_faction.json` content (landing concrete `ai_preset`/signature/hero values is Story 5.3).
- Never wire the signature-mechanic fields to any D1 modifier/effect execution (Story 5.4) — they are descriptor-only storage here.
- Never invoke `UnitDefinitionValidator`'s full gate over `Units` (see Always above).
- Never touch `FactionRegistry.cs`, `MainScene.cs`, or `ScenarioLoadPhase.cs` (Story 5.1's surface, done).
- Never use `float` for any new field — all six are `string`/`string?`/`bool`.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Unchanged showcase load | alpha/beta JSON as-is (no new keys) | `LoadFromFile` succeeds; `FactionValidator.Validate` returns `Ok:true`; `AiPreset=="balanced"` | No error |
| Dangling building prereq | a building's `prerequisites` names a nonexistent id | `Errors` contains a located entry naming the field + dangling id (existing `TechTreeValidator` wording, unchanged) | FAIL |
| Empty/unknown ai_preset | `ai_preset:""` or `"nonsense"` | located error identifying `ai_preset` as cause | FAIL |
| Invalid color | `color` length != 4, or a component outside 0..1 | located error naming `color` | FAIL |
| Duplicate unit id | two `Units[]` entries share an `id` | located error naming the repeated id | FAIL |
| Missing mesh_path | a unit or building has null/empty `mesh_path` | located error naming the entity id + `mesh_path` | FAIL |
| Missing required role | `Units` has no `Worker`, or no combat-category unit | located error naming the missing role | FAIL |

</intent-contract>

## Code Map

- `godot/src/Core/Definitions/FactionDefinition.cs` -- add `AiPreset` (`"ai_preset"`, default `"balanced"`), `SignatureMechanicId` (`"signature_mechanic"`, default `""` — already present as a bare string in alpha/beta JSON today, currently silently ignored), `SignatureMechanicDisplay`/`SignatureMechanicEffectId` (`string?`, default null), `HeroUnitId` (`string?`, default null), `PersistenceEnabled` (`bool`, default false); refactor `LoadFromFile`'s aggregate-errors block to call `FactionValidator.Validate(def)` once instead of its four inline calls (see Review Loop 2 — `Validate`, not `ValidateComplete`, is the method `LoadFromFile` calls).
- `godot/src/Core/Definitions/FactionValidator.cs` (new) -- `FactionValidationResult` struct + `FactionValidator` static class exposing TWO methods (see Spec Change Log, Review Loop 2): `Validate(def)` -- the four relocated checks (Building-per-item, TechTree, ResourceCost, Research — unchanged calls, just relocated) plus three new structural checks (ai_preset closed-set, color, duplicate-unit-id) that never conflict with an in-progress edit; and `ValidateComplete(def)` -- `Validate(def)`'s errors plus the two roster-completeness checks (missing mesh_path, missing required roles) that only make sense once a faction is meant to be finished/playable. `FactionDefinition.LoadFromFile` calls `Validate`, never `ValidateComplete`.
- `godot/ProjectChimera.Sim.Tests/Definitions/FactionValidatorTests.cs` (new) -- one test per I/O Matrix row (the mesh_path/required-role rows call `ValidateComplete` directly, not `LoadFromFile`), plus a regression test that `alpha_faction.json`/`beta_faction.json` still `LoadFromFile` successfully and pass `ValidateComplete`.

## Tasks & Acceptance

**Execution:**
- `godot/src/Core/Definitions/FactionDefinition.cs` -- add the six new `[JsonPropertyName]`-mapped fields listed in Code Map with the specified defaults -- lands the additive schema (AR-12, FR-18 data).
- `godot/src/Core/Definitions/FactionValidator.cs` -- create `FactionValidationResult` (mirrors `BuildingValidationResult` exactly); `FactionValidator.Validate(FactionDefinition def)`: aggregate the four relocated checks + ai_preset closed-set check + color-array check (`Length==4`, each component `0..1`, reject `NaN`) + duplicate-unit-id scan, each producing a `(FieldPath, Message)` entry with the `"faction '<id>'.<field>: <reason>."` located idiom; `FactionValidator.ValidateComplete(FactionDefinition def)`: calls `Validate(def)` then additionally appends missing-mesh-path scan (units and buildings) + required-roles scan (Worker present AND ≥1 of Melee/Ranged/Siege/Air present) -- see Review Loop 2 Design Notes for why these two checks are split into a second method instead of folded into `Validate`.
- `godot/src/Core/Definitions/FactionDefinition.cs` (`LoadFromFile`) -- replace the four inline validator calls with one `FactionValidator.Validate(def)` call (the narrower method — NOT `ValidateComplete`), iterating its `Errors` into the same `List<string>` before the existing throw -- preserves byte-identical external behavior (same joined-message throw) while keeping `LoadFromFile` as permissive about in-progress content as it always was.
- `godot/ProjectChimera.Sim.Tests/Definitions/FactionValidatorTests.cs` -- add the seven I/O Matrix cases as tests (the mesh_path and required-role rows call `FactionValidator.ValidateComplete` directly — never through `LoadFromFile`), plus `LoadFromFile("alpha_faction.json")`/`beta_faction.json` regression tests confirming unchanged success, plus a `ValidateComplete` regression test confirming both showcase factions are fully complete -- unit coverage for every new check.

**Acceptance Criteria:**
- Given `alpha_faction.json`/`beta_faction.json` unchanged, when deserialized after this change, then both still load successfully, `AiPreset=="balanced"` on each, `FactionValidator.Validate` returns `Ok:true` for both, and `FactionValidator.ValidateComplete` ALSO returns `Ok:true` for both (they are genuinely complete showcase factions).
- Given a faction JSON with a building prerequisite naming a nonexistent building id, when validated, then `FactionValidator.Validate` returns a located error naming the field and the dangling id.
- Given a faction JSON whose `ai_preset` is empty or names an unknown preset, when validated, then it FAILs with a located error identifying `ai_preset` as the cause.
- Given a faction JSON with an invalid `color` array (length != 4 or a component outside 0..1) and, separately, a faction JSON with a duplicate unit id, when each is validated, then each returns FAIL with its own distinct located error message.
- Given `FactionDefinition.cs` and `FactionValidator.cs`, when inspected, then neither contains `using Godot` and all new fields are `string`/`string?`/`bool` (no float gameplay state).
- Given a `FactionDefinition` with a unit or building missing `mesh_path`, or missing a required role, when `FactionValidator.Validate` (not `ValidateComplete`) is called, then it returns `Ok:true` for that axis (these two checks live ONLY in `ValidateComplete`) — and when `ValidateComplete` is called on the same input, it returns the located error. This is the concrete, testable form of Review Loop 2's fix: `LoadFromFile` (and therefore `BuildingCardPanel`/`UnitCardPanel`'s open/save-self-check) must never reject a blank `mesh_path` or an incomplete roster, matching the pre-existing, still-current "blank mesh_path = box placeholder = always valid" contract documented in both panels' own `MeshError` methods.

## Spec Change Log

### 2026-07-10 — Review Loop 1

- **Triggering finding:** `bad_spec` (high) — the Blind Hunter adversarial review found that `FactionDefinition.LoadFromFile` is called by `BuildingCardPanel.cs:117`/`UnitCardPanel.cs:127` on file OPEN, not just Save, and that two pre-existing `/godot-verify` editor fixtures (`_buildingcard_sample.json`, `_unitcard_sample.json`, from Stories 3.3/4.5) would now hard-fail loading under the new required-roles and missing-mesh_path checks: `_buildingcard_sample.json` has an empty `units: []` (no Worker/combat unit), and `_unitcard_sample.json`'s `sample_null_mesh` entry has no `mesh_path` at all. Verified directly (not taken on the reviewer's word): confirmed both call sites via grep and read both fixture files' actual content. This is a real, concrete regression against a previously-working, purpose-built editor verification capability, not a speculative edge case.
- **What was amended:** Added two Tasks (Code Map + Tasks & Acceptance) to migrate both fixture files — add filler Worker+Melee units to `_buildingcard_sample.json`, and replace `_unitcard_sample.json`'s `sample_null_mesh` absent `mesh_path` with a nonexistent-file path (mirroring `sample_missing_mesh`'s existing pattern) so both fixtures pass the new checks without losing their documented test purpose. Added a matching AC and a Design Notes entry explaining why no fallback-UI coverage is lost by this substitution. The `<intent-contract>` block (Boundaries, I/O Matrix) is unchanged — the required-roles and missing-mesh_path checks themselves were correctly specified per epics.md's own AR-39 checklist; the gap was a missing Task, not a wrong Boundary.
- **Known-bad state avoided:** Shipping the new validator checks wired into `LoadFromFile` without migrating the two on-disk editor fixtures that predate this story — which would silently break the Building/Unit Card panels' ability to open `_buildingcard_sample.json`/`_unitcard_sample.json` (both would throw `InvalidOperationException` on open), with no xunit test to catch the regression since these are Godot-editor-only fixtures.
- **KEEP (validated correct by this review, unchanged):** the `FactionValidationResult`/`FactionValidator` struct shape and its mirroring of `BuildingValidationResult`/`ManifestValidationResult`; the six new `FactionDefinition` fields and their defaults (`AiPreset` defaulting to `"balanced"` in particular); the relocation of the four pre-existing checks (Building/TechTree/ResourceCost/Research) into `FactionValidator` unchanged; the `ai_preset` closed-set/`color`/duplicate-unit-id/required-roles checks' existence and general design (certain implementation details were separately flagged as `patch` findings this pass — see Review Triage Log, to be addressed on the next pass, not this amendment); the five xunit test-fixture migrations (`BuildingDefinitionValidatorTests.cs` etc.) adding `mesh_path` + Worker/Melee units; `FactionValidatorTests.cs`'s per-matrix-row test coverage.

### 2026-07-10 — Review Loop 2

- **Triggering finding:** `bad_spec` (high, x2) — the Blind Hunter adversarial review (pass 2) found that Review Loop 1's fix (migrating the two static `/godot-verify` fixture files) papered over a deeper, still-live problem: `UnitCardPanel.Edit.cs`'s `MeshError` (and the identical pattern in `BuildingCardPanel.Edit.cs`) explicitly documents and implements "blank `mesh_path` = box placeholder = always valid" as the panel's own Save-gating rule (`if (mp.Length == 0) return null;   // blank = box placeholder — always valid`). Both panels' Save path calls `FactionDefinition.LoadFromFile` as a post-write self-check. Since Loop 1's fix still wired the missing-mesh_path/required-roles checks into `LoadFromFile` unconditionally, ANY creator who adds a new unit/building through either panel and leaves `mesh_path` blank (the panel's own documented, intended, "always valid" workflow) would now have their Save silently fail with `InvalidOperationException` at the self-check — this is a live, ongoing authoring-workflow regression, not limited to the two static fixture files Loop 1 fixed. The same review also found Loop 1's fixture substitution (`sample_null_mesh` → given the same unresolvable path as `sample_missing_mesh`) destroyed the fixtures' only remaining demonstration of the "blank path, no warning" state. Verified directly: read `UnitCardPanel.Edit.cs:938-947` (`MeshError`) and `BuildingCardPanel.Edit.cs:731-736` (identical pattern) and confirmed both `PersistSync` methods call `FactionDefinition.LoadFromFile(tmp)` as their self-check (`UnitCardPanel.Edit.cs:1170`, `BuildingCardPanel.Edit.cs:985`).
- **What was amended:** Split `FactionValidator` into two methods (Code Map, Tasks & Acceptance — outside `<intent-contract>`; the checks' own definitions in Boundaries/I-O Matrix are unchanged and were never the problem): `Validate(def)` — the four relocated checks + ai_preset/color/duplicate-unit-id (none of which conflict with legitimate in-progress editing states) — is what `LoadFromFile` calls, restoring `LoadFromFile`'s pre-existing permissiveness for both match-loading and CreationSuite panel open/save. `ValidateComplete(def)` — `Validate(def)` plus missing-mesh-path and missing-required-roles — is the "ready to ship/play" superset, exposed for future stories (5.5/5.6 wizard save-gate, 5.7 selectability, 5.8 playtest) to call at THEIR OWN gates, matching epic-5-context.md's own framing ("5.2's validator is the single gate reused by every later authoring story... it must not be duplicated" — reused BY those stories at their own call sites, not silently baked into every `LoadFromFile` call). The Loop 1 fixture migrations (`_buildingcard_sample.json`, `_unitcard_sample.json`) are now unnecessary — `LoadFromFile` no longer enforces mesh_path/roles — so they are reverted to their original pre-story content in this pass, restoring the fixtures' original, correct box-placeholder-fallback test coverage. The AC and Design Notes text this added in Loop 1 are removed/replaced accordingly.
- **Known-bad state avoided:** Shipping a validator that silently breaks the documented, tested "leave mesh_path blank for a box placeholder" authoring workflow for ANY future unit/building a creator adds through the Building/Unit Card editors — not just the two static fixtures Loop 1 already caught. This would have been a much larger-blast-radius regression than Loop 1's, discovered only when a real creator hit a mysterious "Save failed" error with no clear cause.
- **KEEP (validated correct across both review loops, unchanged):** the `FactionValidationResult` struct shape; the six new `FactionDefinition` fields and their defaults; the relocation of the four pre-existing checks into `FactionValidator.Validate` unchanged; the ai_preset closed-set/color/duplicate-unit-id check designs (still inside `Validate`, still `patch`-level implementation details pending — see Review Triage Log); the missing-mesh-path/required-roles check LOGIC itself (still correct per epics.md's AR-39 checklist — only WHERE it's wired changed, not what it checks or how); `FactionValidatorTests.cs`'s per-matrix-row coverage (retargeted to call `ValidateComplete` for the two roster-completeness rows, otherwise unchanged); the five xunit test-fixture migrations from Loop 1 (unaffected by this change, still needed since they exercise `Validate` via `LoadFromFile`, and `Validate` still includes ai_preset/color/duplicate-id which those fixtures' filler content also satisfies harmlessly).

## Review Triage Log

### 2026-07-10 — Review pass 1

- intent_gap: 0
- bad_spec: 1 (high 1, medium 0, low 0)
- patch: 8 (high 0, medium 3, low 5)
- defer: 3 (high 0, medium 1, low 2)
- reject: 5 (high 0, medium 0, low 5)
- addressed_findings:
  - `[high]` `[bad_spec]` `FactionDefinition.LoadFromFile`'s new required-roles/missing-mesh_path checks would break the pre-existing `/godot-verify` editor fixtures `_buildingcard_sample.json`/`_unitcard_sample.json` on file open (via `BuildingCardPanel.cs`/`UnitCardPanel.cs`). Spec amended to add explicit migration tasks for both fixtures plus a covering AC; code reverted for re-derivation.

### 2026-07-10 — Review pass 2

- intent_gap: 0
- bad_spec: 2 (high 2, medium 0, low 0)
- patch: 6 (high 0, medium 3, low 3)
- defer: 0
- reject: 4 (high 0, medium 0, low 4)
- addressed_findings:
  - `[high]` `[bad_spec]` The Loop 1 fix only patched two static fixture files but left `FactionValidator`'s missing-mesh_path/required-roles checks wired into `LoadFromFile`, which is also what `BuildingCardPanel`/`UnitCardPanel`'s Save self-check calls — so any creator leaving `mesh_path` blank on a NEW unit/building (the panels' own documented "always valid" behavior) would now have Save silently fail. Spec amended to split `FactionValidator` into `Validate` (called by `LoadFromFile`, never conflicts with in-progress editing) and `ValidateComplete` (the roster-completeness superset, for future stories' own gates); code reverted for re-derivation.
  - `[high]` `[bad_spec]` The Loop 1 fixture substitution (`sample_null_mesh` given the same unresolvable path as `sample_missing_mesh`) destroyed the fixtures' only demonstration of the "blank path, no warning" state. Resolved by the same amendment: the fixture migrations are no longer needed (since `LoadFromFile` no longer enforces the two roster-completeness checks) and are reverted to original content.

### 2026-07-10 — Review pass 3

- intent_gap: 0
- bad_spec: 0
- patch: 7 (high 0, medium 4, low 3)
- defer: 4 (high 1, medium 2, low 1)
- reject: 7 (high 0, medium 0, low 7)
- addressed_findings:
  - `[low]` `[patch]` `_combatCategories`' doc comment said "five combat-category ... values" over a 4-element array — fixed to "four."
  - `[medium]` `[patch]` `ai_preset` closed-set membership was case-sensitive while the required-roles `Category` match is case-insensitive — made `_knownAiPresets` use `StringComparer.OrdinalIgnoreCase` for consistency.
  - `[medium]` `[patch]` `Validate`/`ValidateComplete` dereferenced `def.Buildings`/`def.Units` with no null-list guard (a malformed `"units": null"`/`"buildings": null"` JSON would NRE instead of a located error) — added `?? new List<T>()` guards on all four loops.
  - `[low]` `[patch]` A null element inside a non-null `Units` list (e.g. `"units": [null, {...}]`) would NRE in the duplicate-id scan — added a per-element null check that reports a located error instead of crashing.
  - `[medium]` `[patch]` Two units both authored with a blank/missing `id` bypassed duplicate detection entirely (silently, no error at all) — added a distinct "unit is missing an id" located error before the dedup check.
  - `[low]` `[patch]` `ValidateComplete`'s mesh_path check used `IsNullOrEmpty`, so a whitespace-only `mesh_path` (e.g. `"   "`) passed as present — changed to `IsNullOrWhiteSpace`.
  - `[medium]` `[patch]` `ValidateComplete`'s building mesh_path loop dereferenced `b.MeshPath` directly with no null-element guard, unlike `Validate`'s building loop (which delegates to the null-tolerant `BuildingDefinitionValidator`) — added the same `if (b is null) continue;` guard used for the unit loops, closing an asymmetry the two-method split introduced.
  - `[high]` `[defer]` `FactionValidator.ValidateComplete` (the check that actually protects against an unplayable "zero starting workers" match) is never called by any shipped code path — deferred as DW-97, not fixed in this story (see reasoning in `deferred-work.md`: zero current exploitability, epic-5-context.md assigns this wiring to Story 5.7/5.8, and the fix touches multiplayer-determinism-critical files outside this story's Boundaries).
  - `[medium]` `[defer]` The `Validate`/`ValidateComplete` naming gives no compiler/analyzer signal against a future call site picking the wrong one — deferred as DW-99.
  - `[low]` `[defer]` `_combatCategories` is now a third independent hardcoded copy of the project's category list — deferred as DW-98.
  - `[medium]` `[defer]` `TechTreeValidator.Validate` (pre-existing, Story 4.2) NREs on a null element inside `Units`, upstream of and masking this story's own null-element guards — deferred as DW-100 (out of this spec's Boundaries to fix directly).
  - 7 additional low-severity findings (roster-definition data-drivenness tension already acknowledged in Design Notes, multi-pass iteration inefficiency, blank-faction-id message readability, forward-looking doc comment naming unshipped stories, ai_preset message phrasing split, NaN/Infinity under-documentation, case-variant duplicate-id non-issue) rejected as cosmetic/non-actionable/already-addressed.

### 2026-07-10 — Review pass 4 (follow-up review)

Independent follow-up review (recommended by Pass 3), 4 adversarial layers re-run against the committed diff. The Review Loop 2 `Validate`/`ValidateComplete` split held up: 0 `bad_spec`, 0 `intent_gap` — the Intent Alignment layer confirmed the diff faithfully implements the spec's Reading B, its only named divergence being the already-tracked, deliberate DW-97.

- intent_gap: 0
- bad_spec: 0
- patch: 5 (high 0, medium 2, low 3)
- defer: 1 (high 0, medium 0, low 1)
- reject: 8 (high 0, medium 0, low 8)
- addressed_findings:
  - `[medium]` `[patch]` The three new checks wired into `LoadFromFile` (ai_preset closed-set, color, duplicate-unit-id) had NO through-`LoadFromFile` throw test — every sibling validator (ResourceCost/TechTree/Research/Building) does — so the load-time wiring could be silently unwired with the suite staying green and a malformed faction loading into a match. Added 5 `LoadFromFile_*_Throws_LocatedError` tests (the 3 new checks + the relocated dangling-prereq check + a valid-minimal-load baseline), using the temp-file + `Assert.Throws` idiom from `ResourceCostValidatorTests`.
  - `[medium]` `[patch]` The story's headline deliverable (six new `FactionDefinition` schema fields) had zero JSON round-trip coverage — the only new-field assertion (`AiPreset=="balanced"`) passes purely via the C# default since the JSON omits the key, proving nothing about binding; a wrong `[JsonPropertyName]` on any descriptor field would ship silently. Added `NewSchemaFields_RoundTripFromJson` (locks all six mappings), `NewSchemaFields_Defaults_WhenJsonOmitsThem`, and `AlphaFaction_SignatureMechanic_DeserializesFromJson` (asserts `alpha.SignatureMechanicId=="equal_exchange"`).
  - `[low]` `[patch]` `FactionValidator.Validate`'s Pass-3 `?? new List<>()` guards and their "a located FAIL, never an NRE" / "never silently invisible" comments claimed an NRE-safety the delegation order defeated: `TechTreeValidator`/`ResourceCostValidator` run FIRST and iterate `def.Units`/`def.Buildings` unguarded, so `"units": null` / `"units": [null]` still NRE'd before the guarded loops. Added a structural null pre-check at the top of `Validate` (catches null lists + null elements, emits located errors, returns early BEFORE delegating) and corrected the misleading comments. Independently flagged by the Blind Hunter (H1/M4) and Edge Case layers.
  - `[low]` `[patch]` The blank-unit-id check used `IsNullOrEmpty`, so a whitespace-only id (`"   "`) passed as a valid unique unit — changed to `IsNullOrWhiteSpace`, matching `ValidateComplete`'s mesh_path check (Edge Case finding).
  - `[low]` `[patch]` `KnownAiPresets` was an exposed mutable `string[]` (the doc invites tests + future preset-picker UI to read it) — a caller could corrupt the closed set process-wide via the indexer; changed to `IReadOnlyList<string>` (Blind Hunter M3).
  - Deferred (1): `[low]` `[defer]` `ResourceCostValidator.Validate` NREs on a null `Units`/`Buildings` list/element — the same class as DW-100 but in a sibling validator DW-100 does not name — recorded as **DW-101** (out of this spec's Boundaries to fix; the Pass-4 structural pre-check already shields the `FactionValidator` path, leaving only the sub-validators' own robustness for direct callers).
  - Rejected (8): ai_preset accepting only `"balanced"` and the new checks gating every `LoadFromFile` (both the spec's explicit, documented intent — and the editor Save path does not author `ai_preset`/color, `FactionWriter` untouched); relocated errors sharing a coarse `FieldPath` (inherent to the spec-mandated relocate-the-string-returning-validators-unchanged constraint; message text preserves the detail); `ValidateComplete` excluding armed Structures from the combat-role check (the spec's own required-roles definition, revisit already deferred to Story 5.3 in Design Notes, no production caller); ordinal duplicate-id case-sensitivity; `beta_faction.json`'s unmodeled `deferred_mechanics` key (outside the spec's six enumerated fields, pre-existing ignored key); `ValidateComplete` having no production caller (already tracked as DW-97); and AC5's `using Godot`/float purity being manual-only (the spec frames it "when inspected" by design).

## Design Notes

**Why `ai_preset`'s closed set seeds at one member (`"balanced"`).** Nothing in `epics.md`, `fma-faction-design.md`, or the codebase defines concrete AI-preset ids — FR-18 only says a faction needs *an* assignable preset. `AiDifficulty` (`Easy`/`Normal`/`Hard`) is a pre-existing but semantically distinct concept (per-match difficulty scaling, wired via `MainScene`'s `AiLevel` export) — reusing it here would conflate two unrelated axes. A single-member seed keeps the schema+validator honest (empty/unknown still rejects) without fabricating unfounded preset names; Story 5.3 lands alpha/beta's real values into this same closed set (additive, no schema change needed to add more members later).

**Why "required roles" = Worker + one combat category.** `epics.md`'s Story 5.2 dev-note lists "missing required roles" as one of six `FactionValidator` checks but its own Given/When/Then AC block never tests it concretely — the definition is left to the implementer. A minimum-viable-playable roster (an economy unit plus at least one thing that can fight) is the smallest defensible reading grounded in the project's own 6-archetype `Category` system; revisit in Story 5.3 if the showcase content's roster shape argues for a different bar.

**Why `FactionValidator` splits into `Validate`/`ValidateComplete` (Review Loop 2).** `UnitCardPanel.Edit.cs`'s `MeshError` and `BuildingCardPanel.Edit.cs`'s `MeshError` both explicitly treat a blank `mesh_path` as valid — "blank = box placeholder — always valid" is a direct code comment in both, backing a Save-button-enabling validation pass a creator relies on for ordinary, expected, art-comes-later authoring. Both panels' Save path calls `FactionDefinition.LoadFromFile` as a post-write self-check. A validator check that makes `LoadFromFile` reject a blank `mesh_path` (or an incomplete roster — the same class of "still being edited" state) therefore doesn't just gate match-loading, it silently breaks ordinary Saving in both editors for any faction with in-progress content — a far larger blast radius than the two static fixture files Review Loop 1 caught. `Validate` (relocated four checks + ai_preset/color/duplicate-unit-id) covers exactly the axes that are NEVER a legitimate mid-edit state (a truly duplicate id, a malformed color, an unrecognized preset are always bugs, not WIP) and is safe to run on every `LoadFromFile` call. `ValidateComplete` (adds missing-mesh_path + required-roles) covers the two axes that ARE a legitimate, common, intentionally-supported mid-edit state, so it is exposed only for callers that actually mean "is this faction finished" — future stories' own wizard-finish/playtest/selectability gates (per epic-5-context.md: "5.2's validator is the single gate reused by every later authoring story" — reused explicitly, not silently inherited via `LoadFromFile`).

**Why the Review Loop 1 fixture migrations are reverted, not kept.** With `ValidateComplete`'s two roster-completeness checks no longer wired into `LoadFromFile`, `_buildingcard_sample.json`'s empty `units: []` and `_unitcard_sample.json`'s `sample_null_mesh` (no `mesh_path`) no longer fail `LoadFromFile` — the migration Review Loop 1 added is no longer necessary. Reverting also restores the fixtures' full original test coverage, including the "blank mesh_path, no warning, still valid" state that Loop 1's substitution had accidentally collapsed into a duplicate of the already-covered "non-empty but unresolvable path" state.

**Known gap, tracked not fixed here: `ValidateComplete` has no production caller yet (`deferred-work.md` DW-97).** The Review Loop 2 split correctly stops `LoadFromFile` from rejecting legitimate mid-edit states, but as a consequence `ValidateComplete` — the check that actually guards against a faction reaching a match with, e.g., no Worker unit (silently zero starting economy, per `ScenarioApplier.cs`) — is exercised only by this story's own tests, not by any shipped match-load path. This is a real, acknowledged gap in FR-18/AR-39's "caught before it reaches a match" promise, deliberately left open: epic-5-context.md's own sequencing assigns "excludes/flags invalid authored factions" to Story 5.7 (and playtest validation to 5.8), there is zero current exploitability (no wizard exists yet to author a bad faction; alpha/beta are hand-verified), and wiring `ValidateComplete` into `ScenarioLoadPhase.cs`/`MainScene.cs`/`ServerBootstrap.cs` reaches into multiplayer-determinism-critical files this spec's Boundaries never authorized touching. See DW-97 for the closure plan (follow the existing `UnitTagValidator` shadow-mode diagnostic idiom already present in those same files).

## Verification

**Commands:**
- `dotnet build godot/godot.csproj` -- expected: 0 errors.
- `dotnet build godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` -- expected: 0 errors.
- `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` -- expected: all tests green, including new `FactionValidatorTests` and unchanged `Golden/*` tests (byte-identical, no re-baseline — this story touches no sim/checksum path).
- `git status --short -- godot/ProjectChimera.Sim.Tests/Golden/golden-scenario.golden.txt godot/ProjectChimera.Sim.Tests/Golden/golden-multifaction.golden.txt` -- expected: empty output.

## Auto Run Result

Status: done

**Summary:** `FactionDefinition` gained six new optional, backward-compatible fields (`ai_preset`, signature-mechanic id/display/effect-slot, hero unit reference, persistence flag). A new `FactionValidator` provides the single canonical faction-validity gate, split into two methods after two review-loop amendments: `Validate` (the four pre-existing relocated checks — Building/TechTree/ResourceCost/Research — plus ai_preset closed-set, color-array, and duplicate-unit-id checks; wired into `FactionDefinition.LoadFromFile`, safe for both match-loading and the Building/Unit Card editors' Save self-check) and `ValidateComplete` (`Validate` plus missing-mesh_path and missing-required-roles checks; deliberately NOT wired into `LoadFromFile`, exposed for future stories' own wizard-finish/playtest/selectability gates instead).

**Files changed:**
- `godot/src/Core/Definitions/FactionDefinition.cs` — six new `[JsonPropertyName]`-mapped fields; `LoadFromFile` now calls `FactionValidator.Validate` once instead of four inline validator calls (byte-identical throw/aggregate behavior).
- `godot/src/Core/Definitions/FactionValidator.cs` (new) — `FactionValidationResult` struct + `FactionValidator` static class (`Validate`/`ValidateComplete`).
- `godot/ProjectChimera.Sim.Tests/Definitions/FactionValidatorTests.cs` (new) — 19 tests: one per I/O Matrix row plus alpha/beta regression tests plus regression tests for the review-loop patch round (blank unit id, case-insensitive ai_preset, whitespace-only mesh_path).
- `_bmad-output/implementation-artifacts/deferred-work.md` — 4 new entries (DW-97 through DW-100).

**Review findings breakdown (3 review passes, 4 adversarial layers each):**
- Pass 1: `bad_spec` (high) — the new required-roles/missing-mesh_path checks would break two pre-existing `/godot-verify` editor fixtures on file open (`BuildingCardPanel`/`UnitCardPanel` call `LoadFromFile` on open, not just Save). Spec amended to migrate the fixtures; code re-derived.
- Pass 2: `bad_spec` (high, x2) — the pass-1 fix was insufficient: the same checks, wired into `LoadFromFile`, also broke the LIVE Save workflow for any creator leaving `mesh_path` blank (both editor panels' own `MeshError` explicitly treats blank as valid). Spec amended to split `FactionValidator` into `Validate`/`ValidateComplete`; fixture migrations reverted (no longer needed); code re-derived.
- Pass 3: 0 `bad_spec`/`intent_gap` — the split held up under independent re-review. 7 `patch` findings applied directly (case-sensitivity, null-list/null-element guards, whitespace mesh_path, blank-unit-id detection — see Review Triage Log for the full list). 4 `defer` findings recorded in `deferred-work.md`, most notably DW-97: `ValidateComplete` has no production caller yet, so a faction missing a Worker unit is not currently caught before a match starts — deliberately deferred to Story 5.7/5.8 per epic-5-context.md's own sequencing (zero current exploitability; no wizard exists yet to author such a faction), not fixed here since closing it would require touching multiplayer-determinism-critical files (`ScenarioLoadPhase.cs`/`MainScene.cs`/`ServerBootstrap.cs`) this spec's Boundaries never authorized. 7 `reject` findings (cosmetic/non-actionable).

**Verification performed:** `dotnet build godot/godot.csproj` (0 errors) and `dotnet build .../ProjectChimera.Sim.Tests.csproj` (0 errors) after every round; `dotnet test` full suite green each round (final run: 1407 passed, 1 pre-existing skip, 0 failed); `git status --short` empty on both golden checksum files after every round (byte-identical, no re-baseline — this story never touches sim/checksum code).

**Follow-up review recommendation:** `true` — three review loops, two `bad_spec` amendments (one involving an architectural split), and a 7-item patch round applied directly after the final adversarial pass (not itself re-reviewed by the 4-layer panel) together justify an independent follow-up pass, especially given `FactionValidator` is documented as the single gate four future stories (5.5/5.6/5.7/5.8) will depend on.

**Residual risks:**
- DW-97 (high): `ValidateComplete` is not wired into any match-load path yet — see above and `deferred-work.md`. Must be closed before Story 5.5/5.7 lets a creator author or select a faction that could omit a Worker unit or mesh_path.
- DW-98/DW-99/DW-100 (medium/low): tracked in `deferred-work.md` — a third hardcoded category-list copy, no compiler guard against a future `Validate`/`ValidateComplete` mixup, and a pre-existing `TechTreeValidator` NRE on a null `Units` list element (Story 4.2's code, out of this spec's Boundaries).
- The JSON key names for `signature_mechanic_display`/`signature_mechanic_effect_id`/`hero_unit_id`/`persistence_enabled` were not specified anywhere in epics.md/fma-faction-design.md — inferred via the codebase's snake_case-of-PascalCase convention; Story 5.3/5.6 should confirm/adjust if a different name is expected. (Pass 4 note: these mappings are now locked by `NewSchemaFields_RoundTripFromJson` — a rename will fail that test loudly instead of shipping silently.)

---

### Follow-up review (Pass 4) — 2026-07-10

An independent 4-layer follow-up review (recommended above) was run against the committed diff. It found **0 `bad_spec` / 0 `intent_gap`** — the Review Loop 2 `Validate`/`ValidateComplete` split held up under fresh adversarial re-review, and the Intent Alignment layer confirmed the diff faithfully implements the spec's intended reading. **5 patches** were applied directly (see Review pass 4 in the Triage Log):

- **2 medium (test-coverage):** added 5 through-`LoadFromFile` throw tests for the new/relocated load-wired checks (closing a gap where the load enforcement could be silently unwired with the suite staying green), and added JSON round-trip tests locking all six new `[JsonPropertyName]` schema-field mappings (previously zero round-trip coverage on the story's headline deliverable).
- **3 low (code hardening):** a structural null pre-check in `FactionValidator.Validate` so a null `units`/`buildings` list or element yields a located error instead of an NRE from the delegated sub-validators (making the Pass-3 guard comments honest); `IsNullOrEmpty` → `IsNullOrWhiteSpace` on the blank-unit-id check; and `KnownAiPresets` `string[]` → `IReadOnlyList<string>`.

**1 defer:** DW-101 (`ResourceCostValidator` null-list/element NRE, sibling to DW-100 — pre-existing, out of Boundaries).

**Files changed this pass:**
- `godot/src/Core/Definitions/FactionValidator.cs` — structural null pre-check before delegation; `IsNullOrWhiteSpace` blank-id check; `KnownAiPresets` immutable; honest comments.
- `godot/ProjectChimera.Sim.Tests/Definitions/FactionValidatorTests.cs` — +11 tests (5 `LoadFromFile` throw tests, valid-minimal load baseline, 3 schema round-trip/default tests, alpha signature-mechanic deserialization).
- `_bmad-output/implementation-artifacts/deferred-work.md` — +1 entry (DW-101).

**Verification (Pass 4):** `dotnet build godot/godot.csproj` (0 errors); `dotnet build .../ProjectChimera.Sim.Tests.csproj` (0 errors); `dotnet test` full suite **1415 passed, 1 pre-existing skip, 0 failed** (FactionValidatorTests now 30, up from 19); `git status --short` on both golden checksum files empty (byte-identical, no re-baseline).

**Follow-up review recommendation (Pass 4):** `false` — this pass made no `bad_spec`/`intent_gap` findings and no architectural change; the fixes are localized, low-consequence, additive (tests + defensive guards + an immutability tweak), and each is directly covered by the very tests added and verified green. The `Validate`/`ValidateComplete` split converged under a second independent adversarial panel. No further review-driven change of consequence remains.
