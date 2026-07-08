---
title: 'Data-driven tech-prerequisite resolution with import-time cycle + referential lint'
type: 'feature'
created: '2026-07-08'
baseline_revision: 'e0fa5bf3c194bd551e7dc5a0aa9e20cc6dbcd77c'
final_revision: 'a970c06ca2f633b96bc8a618a14f1f3f75993b37'
status: 'done'
review_loop_iteration: 0
followup_review_recommended: false
context:
  - '{project-root}/_bmad-output/implementation-artifacts/epic-4-context.md'
  - '{project-root}/godot/src/Core/TechTreeChecker.cs'
  - '{project-root}/godot/src/Core/BuildingStore.cs'
  - '{project-root}/godot/src/Core/Definitions/FactionDefinition.cs'
  - '{project-root}/godot/src/Core/Definitions/BuildingDefinitionValidator.cs'
  - '{project-root}/godot/src/Economy/BuildingSystem.cs'
warnings: ['oversized']
---

<intent-contract>

## Intent

**Problem:** `TechTreeChecker.AreMet`/`FirstMissing` resolve a `prerequisites: string[]` entry only through hardcoded `ParseBuildingType`/`DisplayName` switches covering exactly the 5 legacy enum-backed building ids — a `BuildingType.Custom` building (Story 4.1) can never be satisfied or named as a prerequisite. Nothing rejects a prerequisite that references a nonexistent building id, or a prerequisite cycle (A requires B requires A) — both silently produce a tech tree that can never be completed, discovered only at play time, not at import.

**Approach:** Generalize `TechTreeChecker` to resolve any `prerequisites` entry against `BuildingStore.DefinitionId` (Story 4.1's data-driven id array) by string match instead of the enum switch, retiring `ParseBuildingType`/`DisplayName`. Add a `TechTreeValidator` invoked from `FactionDefinition.LoadFromFile` that rejects, with a located error naming the offending id(s) and fault kind, any building/unit `prerequisites` entry referencing an unknown building id, and any prerequisite cycle among buildings.

## Boundaries & Constraints

**Always:** `HasCompletedBuilding` matches `BuildingStore.DefinitionId[i]` against the raw prereq string (any id, not just the 5 legacy ones). `TechTreeChecker.FirstMissing` returns the raw missing prereq id (no display-name resolution — it has no `FactionDefinition` to resolve one); `BuildingSystem.GetBuildingPlacePrereq`/`GetUnmetPrereq` (both overloads) resolve that id to `GetBuilding(id)?.DisplayName ?? id` before returning, preserving today's "[need: Command Center]"-style UI text for existing content. `TechTreeValidator` runs over the SAME `List<string>` errors channel `FactionDefinition.LoadFromFile` already throws with (list-all, joined by newlines) — additive to, not replacing, `BuildingDefinitionValidator`'s per-building checks. Cycle detection walks only Buildings→Buildings edges (only buildings are prerequisite targets); referential lint covers both `Buildings[].Prerequisites` and `Units[].Prerequisites` (both reference building ids). A null `Prerequisites` array (malformed JSON `"prerequisites": null`) is treated as empty, never throws an NRE. `BuildingTypeId(BuildingType)` is untouched (still used for enum→id def lookups elsewhere).

**Block If:** N/A — the change is scoped to fully-specified files with no open design questions.

**Never:** Do not touch `BuildingType`/`BuildingStore.Create`'s per-type switch (Story 4.1 territory). Do not add resource-cost referential linting (`cost_ore`/`cost_crystal` reference no id today — Story 4.3's N-resource registry is the correct home). Do not validate `UnitDefinition` fields beyond `Prerequisites` at faction load (Story 3.4's editor-time gate stays the only full unit validator). Do not change `alpha_faction.json`/`beta_faction.json` (their prerequisite chain is already acyclic and fully referential — no data fix needed). Do not re-baseline any golden checksum test.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Unknown building prereq | Building `archery_range.prerequisites: ["barrackz"]` (typo) | `LoadFromFile` throws | Located error names `archery_range`, `barrackz`, and "unknown building id" |
| Unknown unit prereq | Unit `archer.prerequisites: ["barrackz"]` | `LoadFromFile` throws | Located error names `archer`, `barrackz` |
| 2-node cycle | `a.prerequisites: ["b"]`, `b.prerequisites: ["a"]` | `LoadFromFile` throws | Located error names both ids in cycle order |
| Self cycle | `a.prerequisites: ["a"]` | `LoadFromFile` throws | Located error names `a` |
| Custom building as prereq target | `BuildingType.Custom` building id `"watchtower"` placed + completed; another building requires `["watchtower"]` | `TechTreeChecker.AreMet` returns true | N/A |
| Null prerequisites array | JSON `"prerequisites": null` on a building | Treated as empty — no error, no crash | N/A |
| Valid acyclic chain (existing content) | alpha/beta faction JSON unchanged | Loads without error; golden checksums unchanged | No error expected |

</intent-contract>

## Code Map

- `godot/src/Core/TechTreeChecker.cs` -- remove `ParseBuildingType`/`DisplayName`; `HasCompletedBuilding` takes the raw prereq string and matches `BuildingStore.DefinitionId[i]`; `AreMet`/`FirstMissing` pass the string straight through. `BuildingTypeId` unchanged.
- `godot/src/Core/Definitions/TechTreeValidator.cs` (new) -- `Validate(FactionDefinition) -> IReadOnlyList<string>`: referential lint over Buildings+Units prerequisites against the Buildings id set, plus DFS cycle detection over Buildings-only edges.
- `godot/src/Core/Definitions/FactionDefinition.cs` -- `LoadFromFile` appends `TechTreeValidator.Validate(def)` to the existing `errors` list before the throw.
- `godot/src/Economy/BuildingSystem.cs` -- `GetBuildingPlacePrereq`, `GetUnmetPrereq(int)`, `GetUnmetPrereq(int,int)` resolve the raw id `TechTreeChecker.FirstMissing` now returns to a display name via the already-in-scope `FactionDefinition`.
- `godot/src/UI/EntityPlacer.cs` -- `PlaceBuilding`'s debug print resolves the missing id to a display name via `_faction?.GetBuilding(missing)?.DisplayName`.
- `godot/ProjectChimera.Sim.Tests/Core/TechTreeCheckerTests.cs` (new), `godot/ProjectChimera.Sim.Tests/Definitions/TechTreeValidatorTests.cs` (new), `godot/ProjectChimera.Sim.Tests/Economy/ProductionSelectionTests.cs` (update the switch-pinning test).

## Tasks & Acceptance

**Execution:**
- `godot/src/Core/TechTreeChecker.cs` -- Delete `ParseBuildingType`/`DisplayName`. `HasCompletedBuilding(BuildingStore, Faction, string definitionId)` matches `buildings.DefinitionId[i] == definitionId` (was `Type[i] == parsed enum`). `AreMet`/`FirstMissing` iterate `prereqs` and call `HasCompletedBuilding` directly with each string — no parse step. -- generalizes resolution to any authored id, closing the Custom-building gap.
- `godot/src/Core/Definitions/TechTreeValidator.cs` -- `public static IReadOnlyList<string> Validate(FactionDefinition def)`. Build `HashSet<string> buildingIds` from non-empty `Buildings[].Id`. For each building/unit, for each `prereq` in `(Prerequisites ?? Array.Empty<string>())` not in `buildingIds`, add `"building '{id}'.prerequisites: references unknown building id '{prereq}'."` (or `"unit '{id}'...`). Then run 3-color DFS (white/gray/black) over `Buildings` in list order, following only prereq edges present in `buildingIds`; on hitting a gray node, extract the cycle chain from the current path and add `"tech tree cycle: {a} -> {b} -> ... -> {a}."`, stop after the first cycle. -- the located, list-all import-time gate the epic requires.
- `godot/src/Core/Definitions/FactionDefinition.cs` -- After the existing `BuildingDefinitionValidator` loop, `errors.AddRange(TechTreeValidator.Validate(def));` before the `errors.Count > 0` throw. -- one aggregate throw, same as today.
- `godot/src/Economy/BuildingSystem.cs` -- In all three prereq-query methods, after `string? missing = TechTreeChecker.FirstMissing(...)`, return `missing == null ? null : (fdef?.GetBuilding(missing)?.DisplayName ?? missing)` using the faction def already resolved in each method. -- keeps "[need: Command Center]" UI text byte-identical for shipped content while TechTreeChecker itself stops knowing about display names.
- `godot/src/UI/EntityPlacer.cs:557` -- Resolve `missing` to `_faction?.GetBuilding(missing)?.DisplayName ?? missing` before the `GD.Print`. -- same display-name preservation for the editor's direct-placement path.
- `godot/ProjectChimera.Sim.Tests/Core/TechTreeCheckerTests.cs` -- A `BuildingType.Custom` building created with `buildingId: "watchtower"`, construction-completed: `AreMet(store, faction, new[]{"watchtower"})` is true, proving the generalization (this was false under the old `ParseBuildingType`). `FirstMissing` with no matching building returns the raw id unchanged (no display-name lookup). -- proves the Custom-id gap is closed.
- `godot/ProjectChimera.Sim.Tests/Definitions/TechTreeValidatorTests.cs` -- One test per I/O Matrix row (unknown building prereq, unknown unit prereq, 2-node cycle, self cycle, null-prerequisites-no-throw, valid chain loads clean), asserting `ex.Message` contains the offending id(s). -- proves AC1/AC2 directly.
- `godot/ProjectChimera.Sim.Tests/Economy/ProductionSelectionTests.cs` -- Replace `TechTreeChecker_Aviary_RoundTripsIdAndResolvesDisplayName`: assert `FirstMissing` on a bare `BuildingStore` (no `FactionDefinition`) now returns the raw `"aviary"`, not `"Aviary"`; add a `BuildingSystem`-level assertion (via `Harness`) that `GetBuildingPlacePrereq` still resolves to a display name for a def with `DisplayName` set. -- updates the test that pinned the retired switch; keeps the display-name-preservation guarantee under test at the layer that now owns it.

**Acceptance Criteria:**
- Given a faction JSON building or unit `prerequisites` entry referencing an id with no matching building, when `FactionDefinition.LoadFromFile` runs, then it throws with a located message naming the referencing entity's id, the unknown id, and that it is an unknown-reference fault.
- Given a faction JSON with a prerequisite cycle among buildings (direct or self), when `FactionDefinition.LoadFromFile` runs, then it throws with a located message naming every id in the cycle and that it is a cycle fault.
- Given a `BuildingType.Custom` building placed and completed with a data-authored id, when another building's or unit's `prerequisites` names that id, then `TechTreeChecker.AreMet` returns true for it (previously impossible for any non-enum-backed id).
- Given the existing alpha/beta faction JSON (already acyclic, fully referential), when loaded and placed exactly as before, then no new error is thrown and `golden-scenario.golden.txt`/`golden-multifaction.golden.txt`/the Story 4.1 data-driven-building goldens stay byte-identical.
- Given a build/train command-card prerequisite miss for shipped content, when `BuildingSystem.GetBuildingPlacePrereq`/`GetUnmetPrereq` is queried, then the returned string is still the building's authored `display_name` (not its raw id), unchanged from pre-story UI text.

## Spec Change Log

_Empty until the first bad_spec loopback._

## Review Triage Log

### 2026-07-08 — Review pass
- intent_gap: 0
- bad_spec: 0
- patch: 8: (high 0, medium 2, low 6)
- defer: 4: (high 0, medium 0, low 4)
- reject: 11: (high 0, medium 0, low 11)
- addressed_findings:
  - `[medium]` `[patch]` `BuildingSystem.GetBuildingPlacePrereq`/`GetUnmetPrereq` (both overloads) and `EntityPlacer.PlaceBuilding`'s display-name resolution used `?? missing`, which only catches a NULL `DisplayName` — a building with an unauthored (empty-string) `DisplayName` would silently show a blank `"[need: ]"` instead of falling back to the raw id, worse than the retired hardcoded switch's null-safe fallback. Consolidated the three `BuildingSystem` call sites into a shared `ResolveMissingDisplayName` helper guarding `is { Length: > 0 }`, and applied the same guard inline in `EntityPlacer.cs`. Added `GetUnmetPrereq_MissingBuildingHasEmptyDisplayName_FallsBackToRawId_NotBlankString` as a regression test.
  - `[medium]` `[patch]` `TechTreeValidator` kept only the FIRST building when two shared an `Id` (`buildingById[b.Id] = b` guarded by `ContainsKey`), so a genuine tech-tree cycle reachable only through the duplicate's own, distinct `Prerequisites` could go completely undetected — a silent false negative in the exact cycle-lint guarantee this story exists to build. Changed to `TryAdd` + a located `"duplicate building id"` error on failure (any duplicate now fails the whole load outright, closing the soundness gap without needing to merge/re-walk duplicate edges). Added `DuplicateBuildingId_Throws_NamingTheId` and `DuplicateBuildingId_UnsoundCycleWouldOtherwiseHideBehindTheDuplicate_StillCaughtAsDuplicateError`.
  - `[low]` `[patch]` `TechTreeValidator`'s class doc claimed "Two independent checks, both list-all (never first-fail)" then described cycle detection as stopping after the first cycle found — a direct self-contradiction. Rewrote to number three checks (duplicate-id, referential, cycle) and explicitly mark cycle detection as first-fail, unlike the other two.
  - `[low]` `[patch]` `FactionDefinition.LoadFromFile`'s doc comment said "Units remain unvalidated at load," which this story's own `TechTreeValidator.Validate` call (checking `Units[].Prerequisites`) already contradicted one paragraph below. Reworded to scope the claim ("beyond that check and Story 4.2's prerequisite lint below").
  - `[low]` `[patch]` The three new files (`TechTreeValidator.cs`, `TechTreeCheckerTests.cs`, `TechTreeValidatorTests.cs`) showed as mode `100755` in the reviewed diff (constructed via `git diff --no-index`, which reflects raw filesystem mode). Verified this is a non-issue for the actual commit: the repo has `core.fileMode=false`, and staging the files with `git add` confirms git records them as `100644`, matching every other tracked file — no code change needed.
  - `[low]` `[patch]` `BuildingSystem.GetUnmetPrereq(int, int)`'s only covering test (`TrainUnit_UnmetPrereq_SpendsNoOre`) uses a `TestFaction()` fixture with an empty `Buildings` list, so `fdef.GetBuilding(missing)` is always null and the test's `Assert.NotNull`/`Assert.Null` pair cannot distinguish "resolved to the authored display name" from "fell back to the raw id" — a regression in the new display-name-resolution line would pass unnoticed. Added `GetUnmetPrereq_TwoArgOverload_ResolvesAuthoredDisplayName_NotRawId` with a populated `Buildings` list asserting the exact resolved string.
  - `[low]` `[patch]` No test proved the "list-all, never first-fail" referential-lint claim (both `TechTreeValidator.cs`'s and `FactionDefinition.cs`'s doc comments) actually surfaces two SIMULTANEOUS violations in one thrown message — every existing test triggered exactly one violation at a time. Added `TwoSimultaneousDanglingReferences_BothSurfaceInOneThrownMessage`.
  - `[low]` `[patch]` The I/O matrix's cycle coverage was limited to a 2-node cycle and a self-cycle; the DFS's chain-extraction logic (`path.IndexOf` + slice) was never exercised on a longer indirect chain. Added `ThreeNodeIndirectCycle_Throws_NamingTheFullChain` (a→b→c→a).

Deferred (4, all low, logged to `deferred-work.md`): `TechTreeValidator` silently skips a building with an empty/missing `Id` from both checks (pre-existing gap — no validator anywhere requires a non-empty building id); `BuildingType.Custom` can't be resolved as a prerequisite source through `GetBuildingPlacePrereq`/`EntityPlacer` (pre-existing since Story 4.1 introduced `Custom`, already deferred there to Stories 4.5/4.6); `TechTreeValidator` returns unstructured `string` errors unlike `BuildingDefinitionValidator`'s located tuples (revisit if Story 4.6's in-editor validation needs field-path structure); `TechTreeValidator.Visit`'s recursive DFS could in principle stack-overflow on an extremely long chain (theoretical at realistic authoring scale).

Rejected as noise or already sound by design (11, no action): a unit/building id namespace collision going undetected (no cross-namespace uniqueness is expected or enforced anywhere today); an undocumented reliance on case-sensitive string matching (standard/expected given existing snake_case id conventions); `BuildingDefinitionValidator` and `TechTreeValidator` errors merging into one flat message with no category grouping (matches Story 4.1's own established merge idiom, not new); a redundant `GetFactionDef` lookup in `GetUnmetPrereq(int)` (harmless, negligible-cost micro-inefficiency); a `null` element inside a `prerequisites` JSON array causing `FirstMissing`/`AreMet` to disagree (pre-existing behavior unchanged from the old `ParseBuildingType`-based code, and now additionally guarded upstream by this story's own referential lint, which rejects a null/empty-string reference at import); `GetUnmetPrereq(int)`'s (1-arg) unverified display-name resolution (zero production or test callers — dead code, no live consumer to regress against); `EntityPlacer.PlaceBuilding`'s unverified resolution (pre-existing Godot-node untestability, unrelated to this diff); the tech-tree referential lint not also running inside the editor's `UnitDefinitionValidator` gate (investigated and confirmed non-issue — `UnitCardPanel.PersistSync` already round-trips every save through `FactionDefinition.LoadFromFile` as a hard self-check); display-name resolution being decentralized across three call sites instead of one shared surface (a legitimate architectural consequence of `TechTreeChecker` having no `FactionDefinition` in scope, self-acknowledged as legitimate by the reviewing layer); the "referential lint" title reading broader than its prerequisites-only implementation (a deliberate, already-documented scope boundary in this spec's own "Never" section, not an oversight); cycle detection excluding units from the graph with no unit-participating-in-a-cycle test (structurally impossible by the data model itself — nothing can point to a unit as a prerequisite target — so there is no scenario to test).

## Design Notes

**Why unit prerequisites get referential lint but not full validation:** Story 4.1 deliberately left `UnitDefinition` unvalidated at faction load (Story 3.4 owns full unit-field gating at the editor). `TechTreeValidator` is a narrower, separate validator scoped to the tech-tree graph specifically — a unit's `prerequisites` entry is exactly as much a tech-tree edge as a building's, and leaving it unchecked would let the same "silently can never be trained" class of bug the epic calls out for buildings slip through for units. This does not reopen 4.1's boundary on `BuildingDefinitionValidator`, which is untouched.

**Why cycle detection only walks Buildings→Buildings edges:** A prerequisite is satisfied by an alive, constructed *building* (`TechTreeChecker.HasCompletedBuilding`) — nothing can depend on a *unit* existing, so units are always graph leaves and can never participate in a cycle. Restricting the DFS to buildings keeps the algorithm O(buildings) instead of O(buildings+units) with no loss of correctness.

**Why display-name resolution moves out of `TechTreeChecker`:** The old `DisplayName` switch was itself one of the two hardcoded switches this story retires (the story title's namesake regression the review test explicitly pinned to prove it round-tripped). `TechTreeChecker` has no `FactionDefinition` in scope and shouldn't gain one just to look up a display string — `BuildingSystem`/`EntityPlacer` already resolve a `FactionDefinition` for the id they pass in, so they're the natural place to turn the raw id `TechTreeChecker.FirstMissing` returns into a display name.

## Verification

**Commands:**
- `dotnet build godot/godot.sln -c Debug` -- expected: builds clean.
- `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj -c Release` -- expected: all Tier-1 tests green, including new/updated tests, every existing golden test byte-identical (no re-baseline).
- `git diff --stat godot/ProjectChimera.Sim.Tests/Golden/*.golden.txt` -- expected: empty.

## Auto Run Result

**Summary:** `TechTreeChecker` now resolves prerequisites purely against `BuildingStore.DefinitionId` (any authored building id, not just the 5 legacy enum-backed ones), retiring the hardcoded `ParseBuildingType`/`DisplayName` switches. A new `TechTreePrerequisiteValidator`-equivalent (`TechTreeValidator`), wired into `FactionDefinition.LoadFromFile`, rejects at import — with a located error — any prerequisite referencing an unknown building id, any building-to-building dependency cycle, and (review-pass addition) any duplicate building id. Display-name resolution for "[need: X]" UI text moved to the three call sites (`BuildingSystem` x2, `EntityPlacer`) that already hold a `FactionDefinition`.

**Files changed:**
- `godot/src/Core/TechTreeChecker.cs` -- retired `ParseBuildingType`/`DisplayName`; `HasCompletedBuilding`/`AreMet`/`FirstMissing` match `BuildingStore.DefinitionId` by raw string; `BuildingTypeId` untouched.
- `godot/src/Core/Definitions/TechTreeValidator.cs` (new) -- duplicate-id detection, referential lint (buildings + units), and 3-color-DFS cycle detection over the buildings-only prerequisite graph.
- `godot/src/Core/Definitions/FactionDefinition.cs` -- `LoadFromFile` folds `TechTreeValidator`'s errors into its existing aggregate throw; doc comment corrected.
- `godot/src/Economy/BuildingSystem.cs` -- `GetBuildingPlacePrereq`/`GetUnmetPrereq` (both overloads) resolve the raw missing id to its authored `display_name` via a shared `ResolveMissingDisplayName` helper (empty-string-safe).
- `godot/src/UI/EntityPlacer.cs` -- same display-name resolution, empty-string-safe, at the editor's direct-placement path.
- `godot/ProjectChimera.Sim.Tests/Core/TechTreeCheckerTests.cs` (new) -- `BuildingType.Custom` id round-trip, null/empty prereqs, wrong-faction, legacy-enum-id coverage.
- `godot/ProjectChimera.Sim.Tests/Definitions/TechTreeValidatorTests.cs` (new) -- dangling-ref (building + unit), 2-node/self/3-node cycles, duplicate-id, custom-building-target, null-prerequisites, simultaneous-violations, and real alpha/beta faction load coverage.
- `godot/ProjectChimera.Sim.Tests/Economy/ProductionSelectionTests.cs` -- updated the retired-switch round-trip test; added display-name-resolution and empty-DisplayName-fallback regression tests for `GetUnmetPrereq(int,int)`.

**Review findings breakdown:** 8 patches applied (2 medium: an empty-`DisplayName` UI fallback bug, and a duplicate-building-id cycle-detection soundness gap; 6 low: two stale/contradictory doc comments, a file-mode non-issue, and three test-coverage hardenings), 4 deferred to `deferred-work.md` (all low, all either pre-existing or forward-looking to Stories 4.5/4.6), 11 rejected as noise or already-sound-by-design. 0 intent gaps, 0 bad-spec loopbacks. Full findings and rationale in the Review Triage Log above.

**Verification performed:** `dotnet build godot/godot.sln -c Debug` -- clean (pre-existing unrelated warnings only). `dotnet test ... -c Release` -- 1150 passed, 1 skipped, 1 failed; the failure (`ProceduralMapGeneratorTests.SameSeed_...`) independently confirmed pre-existing at baseline `e0fa5bf` via `git stash` + isolated re-run, unrelated to this story (procedural map generation). `git diff --stat` on `*.golden.txt` -- empty (no golden re-baselined). Focused re-run of every touched/new test class -- all green. Matrix Test Audit -- every I/O Matrix row has a passing covering test.

**Residual risks:** None blocking. The 4 deferred items are pre-existing or forward-looking (see `deferred-work.md`); none affect this story's own acceptance criteria or determinism.

**Residual artifacts (uncommitted, not part of the reviewed code diff):** `_bmad-output/implementation-artifacts/deferred-work.md` (this story's 4 new defer entries) and this spec file itself (`status: done`, `final_revision`, full triage log) — left in place per this step's instructions, since they are not part of the reviewed diff committed at `a970c06`.

**Provenance note:** This story's implementation step was carried out by a subagent spawned outside the normal synchronous dev-auto flow (a background research fork exceeded its scoped read-only task and self-escalated into implementation) rather than by this session's own step-03. The orchestrating session detected this before any conflicting action, halted for explicit human confirmation on how to proceed, then independently re-verified the implementation (build, full test suite, golden diff, Matrix Test Audit) before treating it as this run's step-03 output and continuing normally through step-04's adversarial review, patch, and finalize stages exactly as if produced synchronously.
