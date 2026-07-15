---
title: 'Story 6.8: Custom building placement — thread an authored building id through BuildingSystem/ScenarioApplier + retire the enum gate'
type: 'feature'
created: '2026-07-15'
status: 'done'
baseline_revision: '18301b5f44b0d13d762730b7b3b1a29615a38315'
final_revision: '8a2c83f'
review_loop_iteration: 0
followup_review_recommended: false
context:
  - '{project-root}/_bmad-output/implementation-artifacts/epic-6-context.md'
  - '{project-root}/godot/CLAUDE.md'
warnings: [oversized]
---

<intent-contract>

## Intent

**Problem:** The in-app building editor (Story 4.5) already mints custom buildings by free string id with no `BuildingType` enum tie, and `BuildingStore`/`TechTreeChecker` are already string-id-driven — but nothing lets an authored custom building be *placed* end-to-end. A custom id is rejected at the Tier-1 gate (`ScenarioValidator.IsKnownBuildingType` matches only `Enum.GetNames(BuildingType)`), silently collapsed to `CommandCenter` (`ScenarioApplier.ParseBuildingType`'s `_ => CommandCenter`), unresolvable through `BuildingSystem` (every def lookup is enum→id via `TechTreeChecker.BuildingTypeId`, which returns `""` for `Custom`), and crashes or vanishes at three `(int)BuildingType`-indexed presentation sites (`NavObstacleManager.TYPE_SIZE[5]` hard IndexOutOfRange, `EntityPlacer.BUILDING_COSTS[5]` hard crash, `BuildingBridge`'s `[TYPE_COUNT=5,2]` MultiMesh grid silently drops index 5).

**Approach:** Reinterpret the existing `ScenarioBuilding.Type` string as **"legacy enum name for the built-in five, OR an authored building-def id for anything else"** — no new field, so a custom id folds through the *existing* `CanonicalModelHash` `MixStr(b.Type)` fold with no algorithm change. Thread the authored id through the applier and `BuildingSystem` by resolving the `BuildingDefinition` directly by id (placing a `Custom`-typed building carrying `DefinitionId` + resolved stats, which `BuildingStore.Create` already supports), open the validator gate to authored ids, generalize the editor palette to place any authored building, and fix the three audited presentation touch-sites to resolve footprint/cost/mesh from the def instead of a fixed enum-indexed array.

## Boundaries & Constraints

**Always:**
- Building identity flows as the authored string id via `BuildingStore.DefinitionId` (the Story-4.1 data-driven identity); `BuildingType.Custom` (=5) is the enum slot for any building with no dedicated enum member. The id↔enum mapping lives in ONE place — a `TechTreeChecker.BuildingTypeFromId(string) → BuildingType?` reverse helper paired with the existing `BuildingTypeId`.
- Authored ids are lowercase `[a-z0-9_]` (editor-enforced) and enum names are PascalCase → they can never collide. The applier resolves `b.Type` by trying `Enum.TryParse<BuildingType>` first (legacy names, byte-identical behavior) and only then treating it as a def id.
- Sim layer stays Godot-free / `Fixed`-only and deterministic: `ScenarioApplier`, `BuildingSystem`, `BuildingStore`, `ScenarioValidator`, `TechTreeChecker` take no `using Godot;`; `ScenarioApplier.Apply` stays the single float→`Fixed` boundary.
- Custom building stats (Health/SupplyBonus/ConstructionTime) come only from the resolved `BuildingDefinition` through `BuildingStore.Create`'s resolved-stats params (the `:208` short-circuit) — never from the per-type stat `switch`. A custom producer's production category resolves from `BuildingDefinition.ProducesCategory` via the placed slot's `DefinitionId`, never the enum switch's `_ => "Melee"` default.
- `CanonicalModelHash.AlgoVersion` stays **7**, `SimChecksum.AlgoVersion` stays **15**, `StartStateHash.AlgoVersion` stays **2**; the 23 per-tick goldens and all `CanonicalModelHash`/`StartStateHash` fixtures are UNCHANGED. Any existing scenario (all built-in, enum-name `Type` strings) serializes and hashes byte-identically.
- The two hard-crash presentation sites (`NavObstacleManager`, `EntityPlacer` costs) must resolve their per-building value from the `BuildingDefinition` (by `DefinitionId`) with a guarded fallback — a `Custom`/out-of-enum-range building must never throw.

**Block If:**
- Keeping legacy scenarios byte-identical proves impossible under the reinterpret-`Type` design and threading a custom id genuinely requires a new hash-folded field, an `AlgoVersion` bump, or re-recording any existing golden → HALT `blocked` `building-id hash re-baseline required` (escalate via correct-course; do NOT silently move a golden).
- Routing a custom producer's production category cannot be done by resolving `ProducesCategory` from the def and genuinely requires a new per-building `BuildingStore` SoA field folded into per-tick `SimChecksum` (which would re-baseline the 23 goldens) → HALT `blocked` `per-tick building-store fold required`.

**Never:**
- Never add a new hash-folded field or bump any `AlgoVersion`; never re-record a golden; never add a per-entity `BuildingStore` SoA field folded into `SimChecksum` in this story.
- Never implement the in-match worker build card for custom buildings (`CommandCardSystem.WORKER_BUILD_TYPES` closed 5-button grid) — that is a separate in-match construction UX, out of scope here (this story is editor/scenario placement + the sim pipeline).
- Never touch the trigger-DSL building parse (`ScenarioDirector` `Enum.TryParse<BuildingType>`) — Epic 7 territory.
- Never change the byte-serialized `BuildingType` enum values or drop the `Custom` sentinel.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Custom building placed | `ScenarioBuilding.Type = "watchtower"`, faction authors a `watchtower` `BuildingDefinition` | Validator accepts (id known in owner faction); applier places a `BuildingType.Custom` building with `DefinitionId="watchtower"` and Health/SupplyBonus/ConstructionTime from the def; no crash | — |
| Legacy built-in | `Type = "CommandCenter"` (enum name) | Unchanged path: `Enum.TryParse` succeeds → `PlaceBuildingDirect(CommandCenter,…)`; serialization + `CanonicalModelHash` byte-identical to pre-feature | — |
| Unknown id | `Type = "nonexistent_bldg"`, no such enum name and no faction building-def | `ScenarioValidator.Validate` fails closed with a clear message; never reaches the applier | Fail-closed error |
| Determinism fold | Two identical scenarios each with a `watchtower` | Identical `CanonicalModelHash` (id folds through existing `MixStr(b.Type)`); a scenario with `watchtower` vs one with `command_center` at the same slot/pos → different hashes | — |
| Built-in referenced by snake_case id | `Type = "barracks"` (authored id, not `"Barracks"`) | `BuildingTypeFromId("barracks") == Barracks` → placed as the proper `Barracks` enum with correct production category (round-trip robust) | — |
| Custom producer | Custom building with `produces_category = "Air"` | `BuildingSystem` routes its production via `ProducesCategory` (Air), not the enum switch's Melee default | — |
| Editor place custom | Author selects `watchtower` from the palette, left-clicks | Nav-obstacle footprint + placement cost resolve from the def (no `TYPE_SIZE`/`BUILDING_COSTS` IndexOutOfRange); `watchtower` written into `ScenarioData.Buildings[].Type`; building renders | Missing/invalid def → guarded default, no throw |
| Render authored building | Live `Custom` building with `DefinitionId="watchtower"` | `BuildingBridge` renders it via a `DefinitionId`-keyed MultiMesh bucket (mesh/scale from the def; `Rot` applied) instead of dropping enum index 5 | Unknown id → skipped, never throws |

</intent-contract>

## Code Map

- `godot/src/Core/BuildingStore.cs:6-25` — the `BuildingType : byte` enum (`Custom = 5` sentinel); `Create(…, buildingId, health, supplyBonus, constructionDuration)` (:153) + resolved-stats short-circuit (:208) already honor an arbitrary authored id. **No structural change** — the store is ready; this story feeds it a custom `buildingId`.
- `godot/src/Core/TechTreeChecker.cs:54-62` — **ADD** `BuildingType? BuildingTypeFromId(string id)` (reverse of `BuildingTypeId`: the 5 built-in ids → enum, else `null`), the single id↔enum source. `BuildingTypeId` unchanged.
- `godot/src/Core/Sim/ScenarioApplier.cs:188-194, 427-434` — replace the `ParseBuildingType`-only building loop: resolve `b.Type` → `Enum.TryParse` legacy enum name (existing `PlaceBuildingDirect`) OR by-id (`BuildingSystem` by-id placement). `ParseBuildingType` kept for the legacy names it already maps; its `_ => CommandCenter` default no longer swallows custom ids (they route to the by-id path). Stays Godot-free.
- `godot/src/Economy/BuildingSystem.cs:592-618, 273-281, 740-751` — **ADD** `PlaceBuildingDirectById(string buildingId, Faction, FixedVec3, bool preBuilt)`: `GetFactionDef(faction)?.GetBuilding(buildingId)` → enum = `BuildingTypeFromId(id) ?? Custom` → `Create(…, buildingId: bdef.Id, health, supplyBonus, constructionDuration)`. Generalize `CategoryForBuilding` so a `Custom`/authored building resolves `ProducesCategory` from the def (via the slot's `DefinitionId`), not the `_ => "Melee"` default.
- `godot/src/Core/Definitions/ScenarioValidator.cs:296, 403, 421, 700-706` — `IsKnownBuildingType` accepts an enum name OR a building-def id present in the owner faction's `Buildings`; a truly unknown id fails closed (message names the offending id). This is the retired gate.
- `godot/src/Core/Definitions/ScenarioData.cs:148-154` — **DOC-ONLY**: rewrite the `ScenarioBuilding.Type` comment to "legacy `BuildingType` enum name for built-ins, OR an authored `BuildingDefinition.Id` for custom buildings; folds through `CanonicalModelHash` via the existing `MixStr(Type)`." No field/structural change.
- `godot/src/UI/EntityPlacer.cs:59, 130, 845-912, 971-981, 1168-1174` — selected building becomes an authored id (string) sourced from the owner faction's `Buildings`; palette/`CycleBuildingType` enumerate authored buildings (id + `DisplayName`); **replace** `BUILDING_COSTS[5]` indexing with a def-resolved cost (kills the `Custom` crash); write the selected id through the sync closure. Presentation (godot-verify surface).
- `godot/src/Core/MainScene.cs:815, 834-838` — `SyncBuilding` writes the enum name for a built-in id (byte-identical legacy) and the authored id for a custom id (via `BuildingTypeFromId`). Presentation.
- `godot/src/UI/NavObstacleManager.cs:34-41, 138-139` — **replace** `TYPE_SIZE[(int)Type[id]]` (hard IndexOutOfRange for `Custom`) with a def-resolved footprint (by `DefinitionId`) + guarded default. Footprint must be deterministic (def data). Presentation.
- `godot/src/UI/BuildingBridge.cs:46-99, 189-235` — **re-key** the render buckets from the fixed `[TYPE_COUNT=5,2]` enum-indexed MultiMesh arrays to `DefinitionId`-keyed buckets discovered from the loaded faction defs at `Initialize`; `Rebuild` routes by `_buildings.DefinitionId[i]`; keep mesh/scale-by-id resolution and `Rot`. Removes the silent index-5 drop. Presentation.
- `godot/ProjectChimera.Sim.Tests/**` — NEW Godot-free tests (see Tasks) covering applier/validator/BuildingSystem/reverse-helper + the determinism invariants.

## Tasks & Acceptance

**Execution:**
- `TechTreeChecker.cs` — add `BuildingTypeFromId(string) → BuildingType?` reverse helper; keep `BuildingTypeId` unchanged.
- `ScenarioValidator.cs` — `IsKnownBuildingType` accepts enum name OR an owner-faction building-def id; unknown id fails closed with a message naming it.
- `ScenarioApplier.cs` — building loop resolves `b.Type` via `Enum.TryParse` (legacy) else by-id placement; custom ids no longer collapse to `CommandCenter`.
- `BuildingSystem.cs` — add `PlaceBuildingDirectById`; generalize production `CategoryForBuilding` to honor a custom building's `ProducesCategory` via `DefinitionId`.
- `ScenarioData.cs` — doc-only re-description of `ScenarioBuilding.Type` (dual meaning; folds through existing `MixStr`).
- `EntityPlacer.cs` (+ `MainScene.cs` sync) — palette enumerates the faction's authored buildings; selection carries the authored id; placement cost resolved from the def (remove `BUILDING_COSTS[5]` crash); `SyncBuilding` writes enum-name-for-built-in / authored-id-for-custom.
- `NavObstacleManager.cs` — def-resolved, guarded footprint (no IndexOutOfRange for `Custom`).
- `BuildingBridge.cs` — `DefinitionId`-keyed render buckets; render authored buildings (mesh/scale/`Rot` from def); guard unknown id.
- Tests — `CustomBuildingPlacementTests` (a custom-id scenario validates, applies to a `Custom`-typed building with `DefinitionId` + resolved Health/SupplyBonus/ConstructionTime; unknown id fails closed; a snake_case built-in id round-trips to the proper enum via `BuildingTypeFromId`); `CustomBuildingDeterminismTests` (identical custom-building scenarios → identical `CanonicalModelHash`; differing custom ids → differing hash; a legacy all-built-in scenario hashes byte-identically and `CanonicalModelHash.AlgoVersion==7`/`SimChecksum==15`/`StartStateHash==2` + the 23 per-tick goldens are unchanged); `BuildingTypeFromIdTests` (round-trips against `BuildingTypeId` for the 5, `null` for a custom/empty id); production-category resolution for a custom producer.

**Acceptance Criteria:**
- Given a faction that authors a custom building `watchtower`, when a scenario places it and is validated, applied, and simulated, then it survives the Tier-1 validator gate, spawns as a `BuildingType.Custom` building carrying `DefinitionId="watchtower"` with stats from its `BuildingDefinition`, and never collapses to `CommandCenter`.
- Given the same authored-building scenario, when it is serialized and hashed, then the custom id folds through the existing `CanonicalModelHash` `MixStr(Type)` (identical scenarios → identical hash, differing custom ids → differing hash) with `CanonicalModelHash.AlgoVersion` still 7 and no golden moved.
- Given any existing all-built-in scenario (enum-name `Type` strings), when it is loaded, serialized, hashed, and simulated, then bytes, `CanonicalModelHash`/`StartStateHash`, and all 23 per-tick goldens are byte-identical to pre-feature.
- Given the editor with a faction's authored buildings, when the author selects and places a custom building, then it appears in the palette, places without a `NavObstacleManager`/`EntityPlacer`-cost IndexOutOfRange, writes its authored id into `ScenarioData`, and renders in-world via `BuildingBridge`.
- Given a custom producer building with `produces_category`, when it produces, then its production routes through that category, not the Melee default.

## Design Notes

**Why reinterpret `Type` instead of adding a `building_id` field.** `CanonicalModelHash` already folds `b.Type` (`CanonicalModelHash.cs:197-202`, `.ThenBy(Type, Ordinal)` then `MixStr(h, b.Type)`). Adding a parallel `building_id` field that must also be folded would change the hash algorithm → an `AlgoVersion` bump → every `CanonicalModelHash`/`StartStateHash` fixture re-recorded (the memory "CanonicalModelHash TerrainRef determinism landmine"). Reusing the `Type` slot as "enum name OR authored id" folds a custom id through the *unchanged* `MixStr`, needs **no** new fold, keeps `AlgoVersion` at 7, and leaves every legacy scenario byte-identical (its `Type` strings never change). The snake_case-vs-PascalCase disjointness (editor enforces `[a-z0-9_]`; enum names are PascalCase) guarantees the applier's `Enum.TryParse`-first resolution is unambiguous.

**The store is already custom-ready.** `BuildingStore.Create` takes `buildingId` + resolved stats and short-circuits the per-type stat switch (`:208`); `DefinitionId[]` (`:78`) carries the authored id; `TechTreeChecker.HasCompletedBuilding` already matches prereqs by `DefinitionId` for any authored id. The unthreaded seam is purely upstream (validator/applier/`BuildingSystem` def-resolution) and downstream presentation (`NavObstacleManager`/`EntityPlacer`/`BuildingBridge`) — the sim store itself needs no change, so no per-tick `SimChecksum` field and no golden movement.

**Determinism scope.** `SimChecksum` folds building *stats* (Health/ConstructionTimer/…) but not `Type`/`DefinitionId` (like `MeshType`); a custom building is per-tick checksum-safe as long as it resolves to identical `Fixed` stats on every peer, which it does (stats come from the shared validated `BuildingDefinition`). Only the start-state `CanonicalModelHash` observes identity, and it does so through the existing `Type` fold.

## Verification

**Commands:**
- `dotnet build godot.sln` — expected: clean compile, 0 new errors; no new banned-API/determinism analyzer warnings on the sim path (`ScenarioApplier`, `BuildingSystem`, `ScenarioValidator`, `TechTreeChecker`, `BuildingStore` stay `float`→`Fixed`-boundary-only, no `using Godot;`).
- `dotnet test godot/ProjectChimera.Sim.Tests` — expected: new custom-building tests green; the **23 per-tick goldens + `KnownWorldState` pin + `CanonicalModelHash.AlgoVersion==7` + `SimChecksum.AlgoVersion==15` + `StartStateHash.AlgoVersion==2` UNCHANGED** (any movement = a wrongly-folded field → STOP and fix). Note the 2 pre-existing `PersistenceManifestTests` baseline failures (`ShippedScenario_ValidatesOk_AndSerializesWithoutManifest`, `ScenarioValidator_NullManifest_Passes`) — confirm unrelated via `git stash` if seen; any OTHER new failure is real.

**Manual checks (godot-mcp / godot-verify — no xUnit surface for the palette, placement cost, nav-obstacle stamp, or MultiMesh render):**
- Author a custom building in the Building editor, then place it in the map editor: confirm it appears in the placement palette, places without a crash, and renders in-world with the def's mesh.
- Load an existing all-built-in scenario and confirm the built-in buildings still place, render, and produce exactly as before (no visual/behavior regression from the `BuildingBridge` re-key).

## Spec Change Log

_No bad_spec loopback — the reinterpret-`Type` design held through review._

## Review Triage Log

### 2026-07-15 — Review pass 1 (post-implementation adversarial review: 4 layers — blind / edge-case / verification-gap / intent-alignment)

- intent_gap: 0
- bad_spec: 0
- patch: 2: (high 0, medium 0, low 2)
- defer: 6
- reject: 6
- addressed_findings:
  - `[low]` `[patch]` **The bare `"Custom"` sentinel enum name was an accepted, placeable scenario `Type` that applied as a stat-less, unrendered ghost (Blind + Edge).** `_buildingTypeNames = Enum.GetNames(BuildingType)` includes `"Custom"`, so `IsKnownBuildingType("Custom")` returned true and the applier routed it to `PlaceBuildingDirect(Custom)` → `GetBuilding("")` = null → switch-default stats + empty `DefinitionId` (never bucketed → invisible). 6.8 changed this input's behavior (pre-6.8 it collapsed to a CommandCenter). Excluded the `Custom` sentinel from the `IsKnownBuildingType` enum-name match so a bare `"Custom"` now fails closed; a lowercase authored id such as `custom` still resolves through the owner faction. Belt-and-braces `!= BuildingType.Custom` added to the applier's direct-path guard. New test `CustomSentinelName_FailsClosed_NotAnInvisibleGhost`.
  - `[low]` `[patch]` **An all-digit authored id ("5", valid `[a-z0-9_]`) mis-routed through `Enum.TryParse`'s numeric parse (Edge).** `Enum.TryParse<BuildingType>("5")` yields `Custom` (`IsDefined` true) → the direct, def-less path, silently dropping the authored def, id, and stats. Tightened the applier's direct-path gate with a name-round-trip guard (`bType.ToString() == b.Type`) so any numeric spelling routes by-id and resolves the authored def with real stats. New test `NumericAuthoredId_RoutesById_NotThroughEnumNumericParse` (+ a numeric-id building in the test faction).
- deferred (6, in `deferred-work.md`): custom-producer in-match command card is enum-only (`canProduce` gate + `GetProductionUnits` roster) so a `Custom` producer shows no train buttons — the sim `TrainUnit` routing IS def-aware + tested; in-match operation UI is out of the placement intent (Blind HIGH + VG); custom nav footprint is a fixed 5×3×5 regardless of mesh size (consistent with existing fixed built-in footprints; Blind + Edge); a placed custom building can't be referenced in a trigger (validator trigger check stays enum-only — Epic 7; Blind); `BuildingBridge` render buckets freeze at `Initialize` so a mid-session-authored or third-faction building renders invisibly (Blind + Edge + VG); the def→`Create` stat-threading is hand-copied in `PlaceBuildingDirectById` + `CreateEditorBuilding` (drift risk; Blind + VG); group-move undo doesn't restore def-derived stats (pre-existing, exposed for varied custom stats; Edge).
- rejected (6): editor placement cost now data-driven from the def's `cost` map (spec-intended, platform-rule-aligned — Blind); the two coupled `CUSTOM_FOOTPRINT`/`CUSTOM_FALLBACK` constants linked by comment (mirrors the existing accepted `TYPE_SIZE`/`TYPE_FALLBACK` duplication — Blind); "determinism tests don't run the goldens" (moot — the full suite incl. the 23 per-tick goldens ran and passed in verification — Blind); `SimChecksum` can't distinguish two `Custom` buildings by type (not a desync; pre-existing non-fold of `Type`/`DefinitionId`; production diverges deterministically — Blind); loader/server `slotFactionDefs` pass-through untested (fail-closed if wrong, not silent — VG); `MainScene.ScenarioTypeString` untested (correct-by-delegation to the tested `BuildingTypeFromId`; noted as a godot-verify residual — VG).

## Auto Run Result

Status: **done** (implemented, reviewed across 4 adversarial layers, 2 patches applied, 6 deferred, 6 rejected, 0 spec loopbacks, committed)

### Implemented change
Story 6.8 threads an authored (custom) building id through the full placement pipeline and retires the `BuildingType` enum as the sole gate, using the reinterpret-`Type` design (no new hash-folded field, no `AlgoVersion` bump). `ScenarioBuilding.Type` now means "legacy PascalCase enum name for the built-in five, OR a snake_case authored `BuildingDefinition.Id` for a custom building" — disjoint vocabularies the applier resolves unambiguously (case-sensitive `Enum.TryParse` first, else by-id). A custom id folds through the EXISTING `CanonicalModelHash` `MixStr(b.Type)` fold, so every legacy all-built-in scenario serializes and hashes byte-identically and no golden moves. New sim path: `TechTreeChecker.BuildingTypeFromId` (the one id↔enum reverse source), `BuildingSystem.PlaceBuildingDirectById` (by-id def resolution threading Hp/SupplyBonus/ConstructionTime into `BuildingStore.Create`'s resolved-stats params), a def-aware production `CategoryForBuilding` (a custom producer trains its authored `produces_category`, not the Melee default), and `ScenarioValidator.Validate(model, slotFactionDefs)` accepting an owner-faction building-def id (the retired enum gate). Presentation: `EntityPlacer` palette now places any authored building (def-resolved cost, id-carrying undo/copy-paste), `MainScene.SyncBuilding` serializes built-ins as their enum name / customs verbatim, `NavObstacleManager` resolves footprint by `DefinitionId` (guarded — no `TYPE_SIZE[5]` IndexOutOfRange), and `BuildingBridge` renders through `DefinitionId`-keyed buckets instead of the fixed 5-slot enum-indexed arrays (Custom no longer dropped at index 5).

### Files changed (one line each)
- `godot/src/Core/TechTreeChecker.cs` — `BuildingTypeFromId(string?) → BuildingType?`, the single id↔enum reverse source.
- `godot/src/Core/Definitions/ScenarioData.cs` — doc-only: `ScenarioBuilding.Type` dual meaning (enum name OR authored id; folds through existing `MixStr`).
- `godot/src/Core/Definitions/ScenarioValidator.cs` — `Validate(model, slotFactionDefs?)`; `IsKnownBuildingType` accepts an owner-faction building-def id; the bare `Custom` sentinel is rejected (review patch).
- `godot/src/Core/Sim/ScenarioApplier.cs` — building loop resolves enum-name (byte-identical direct path) vs authored id (by-id path); hardened direct-path gate rejects numeric spellings + the `Custom` sentinel (review patch).
- `godot/src/Economy/BuildingSystem.cs` — `PlaceBuildingDirectById`; def-aware `CategoryForBuilding(type, faction, definitionId)` so a custom producer routes its authored category.
- `godot/src/Core/MainScene.cs` — `SyncBuilding` takes the authored id; `ScenarioTypeString` serializes built-ins as enum name / customs verbatim.
- `godot/src/Core/Bootstrap/Phases/ScenarioLoadPhase.cs`, `godot/src/Core/Sim/ServerBootstrap.cs` — thread the resolved per-slot faction defs into `Validate` (the two production load/serve validate sites).
- `godot/src/UI/EntityPlacer.cs` — palette enumerates the faction's authored buildings; def-resolved cost (no `BUILDING_COSTS[5]` crash); undo/copy-paste carry the authored `DefinitionId`.
- `godot/src/UI/NavObstacleManager.cs` — `FootprintFor` resolves footprint by `DefinitionId` with a guarded `CUSTOM_FOOTPRINT` (no IndexOutOfRange for `Custom`).
- `godot/src/UI/BuildingBridge.cs` — `DefinitionId`-keyed render buckets discovered at `Initialize`; renders authored buildings instead of dropping enum index 5.
- Tests (NEW): `CustomBuildingPlacementTests` (validate/apply/Custom-typed/resolved-stats, unknown-fails-closed, snake_case + numeric-id by-id round-trip, `Custom`-sentinel fails closed, custom-producer category routing), `BuildingTypeFromIdTests`, `CustomBuildingDeterminismTests` (id folds through `MixStr`, AlgoVersion pins) — 28 new tests, all green.

### Review findings breakdown (review pass 1, 4 layers)
- Patches applied: 2 (0 high, 0 medium, 2 low) — the `Custom`-sentinel ghost + numeric-id mis-route hardening, each locked by a new test.
- Deferred: 6 — custom-producer command card (in-match operation UI, out of placement intent), custom nav-footprint sizing, trigger reference of custom buildings (Epic 7), mid-session render-bucket freeze, dup def→`Create` mapping, group-move undo stat restore.
- Rejected: 6 — data-driven editor cost (intended), coupled footprint constants (existing pattern), goldens-unverified (they ran + passed), `SimChecksum` Custom-indistinct (not a desync), loader slotDefs pass-through (fail-closed), `ScenarioTypeString` (correct-by-delegation).

### Verification
- `dotnet build godot.sln` → Build succeeded, **0 errors** (11 pre-existing CS8632 nullable-context warnings in untouched files — `FlowFieldSystem`/`ResourceNodeStore`/`ResourceStore` — surfaced by the test project's sim-source globbing, not introduced here). Sim path stays `float`→`Fixed`-boundary-only, no `using Godot;`.
- `dotnet test ProjectChimera.Sim.Tests` → **1737 passed, 2 failed, 1 skipped**. The 2 failures are the documented pre-existing `PersistenceManifestTests` baseline pair (`ShippedScenario_ValidatesOk_AndSerializesWithoutManifest`, `ScenarioValidator_NullManifest_Passes`), **git-stash-verified** against the pristine baseline (Failed: 2, Passed: 0 without this change). Determinism pins green: `CanonicalModelHash.AlgoVersion==7`, `SimChecksum.AlgoVersion==15`, `StartStateHash.AlgoVersion==2`, 23 per-tick goldens unchanged.
- Matrix Test Audit: sim rows (custom placement, legacy byte-identical, unknown-fails-closed, determinism fold, snake_case built-in round-trip, custom producer) are each covered by a ran-and-passed xUnit test. The two presentation rows (editor place, MultiMesh render) have no xUnit surface in this project's sim/presentation split; they were launch-verified — the project boots error-free with all changed files loaded (`BuildingBridge`/`NavObstacleManager`/`EntityPlacer` included) — with the full in-engine custom-authoring/render flow left to godot-verify.
- godot-mcp: launched the project (editor 4.6.3, addon 4.1.0) → boots to the main menu with **zero editor/game errors**, confirming no init/compile/autoload regression from the 11 changed files.

### Follow-up review recommendation: **false**
This review pass made only 2 localized, low-consequence hardening patches (each test-locked); the four adversarial layers already covered the large cross-cutting diff thoroughly, so a fifth code-review pass over the same (now-hardened) diff would add little. The genuinely valuable outstanding verification is an in-engine **godot-verify** of the custom-building editor/placement/render flow (below), not another adversarial code review.

### Residual risks
- **Custom-building presentation path is unverified by automated tests** (`EntityPlacer` palette/cost/undo/copy-paste, `BuildingBridge` render-bucket re-key, `NavObstacleManager` footprint) — `src/UI` is excluded from the Godot-free suite. Built-ins are byte-identical through every path (a built-in's `DefinitionId` is always its canonical id) and the project launches clean, but the CUSTOM path in each was not driven in-engine. A godot-verify of "author a custom building → place it → see it render → undo/redo/copy-paste" is the recommended follow-up.
- **Custom producers are placeable but not operable in-match** (no train buttons — deferred); the sim production routing is correct + tested.
- **Custom nav footprint is a fixed 5×3×5** regardless of mesh size (deferred); **mid-session-authored custom buildings may render invisibly** until a reload re-discovers the render buckets (deferred).
- **`MainScene.ScenarioTypeString`** (the editor→scenario write side of the round-trip) is covered only transitively via `BuildingTypeFromId`; a regression making an editor-placed built-in serialize as `command_center` instead of `CommandCenter` would move that saved scenario's start-state hash and is caught by no test driving the Godot editor write path.
