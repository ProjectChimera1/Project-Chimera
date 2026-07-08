---
title: 'Data-drive the building definition + runtime building store'
type: 'feature'
created: '2026-07-08'
baseline_revision: '061b4fc51c7b0eeb3fa9d469b54a50f4d9939f3b'
final_revision: 'da3966599cbb6f5cdd592584eb99298e0edfc0c3'
status: 'done'
review_loop_iteration: 0
followup_review_recommended: false # judged false — patches are small, mechanical, backward-compatible null-safety/validation fixes, verified by dedicated new regression tests
context:
  - '{project-root}/_bmad-output/implementation-artifacts/epic-4-context.md'
  - '{project-root}/godot/src/Core/BuildingStore.cs'
  - '{project-root}/godot/src/Core/TechTreeChecker.cs'
  - '{project-root}/godot/src/Economy/BuildingSystem.cs'
  - '{project-root}/godot/src/Core/Definitions/UnitDefinition.cs'
  - '{project-root}/godot/src/Core/Definitions/FactionDefinition.cs'
  - '{project-root}/godot/src/Core/Definitions/UnitDefinitionValidator.cs'
warnings: ['oversized']
---

<intent-contract>

## Intent

**Problem:** Buildings are locked to the four hardcoded types because `BuildingStore.Create()` bakes HP/SupplyBonus/ConstructionDuration into a per-`BuildingType` switch (`BuildingStore.cs:177-216`) instead of reading them from a loaded definition — faction JSON's `buildings[]` already parses as plain `UnitDefinition` but its `hp` is vestigial (ignored) and there is no `construction_time`/`supply_bonus`/`produces_category` field at all.

**Approach:** Add `BuildingDefinition : UnitDefinition` carrying the new required fields, validate them at faction-JSON import with located errors, and give `BuildingStore.Create()` an additive, backward-compatible resolved-stats path that reads Health/SupplyBonus/ConstructionDuration from a passed-in `BuildingDefinition` instead of the switch (the switch stays only as the no-def fallback so ~50 existing call sites keep compiling). Append a `BuildingType.Custom` sentinel so a building with no matching enum member is still creatable. Correct alpha/beta faction JSON's showcase buildings so def-driven values reproduce today's baked constants bit-for-bit.

## Boundaries & Constraints

**Always:**
- `BuildingStore.Create()`'s existing positional signature and every existing call site (tests, `AiOpponentSystem`, `EntityPlacer`) keep compiling unchanged — new capability is additive-only (new optional trailing params), never a required-param change.
- The four showcase buildings place with byte-identical Health/SupplyBonus/ConstructionDuration via `BuildingSystem.PlaceBuildingDirect`/`QueueWorkerBuild` after this story, matching today's baked switch exactly: command_center 500/10/15s, barracks 300/0/10s, archery_range 300/0/10s, siege_workshop 400/0/12s. Aviary (already enum-backed, baked 350/0/12s) gets the same correction since `BuildingSystem` resolves-and-passes a def for whichever type is placed, not just the four named ones — leaving it uncorrected would be a real regression, not a no-op.
- All new/changed `BuildingStore` state stays `Fixed`/`int`/`bool`/`string`, no `float`, ascending id order, no Godot types.
- `BuildingType` stays append-only (0-4 never renumbered); the new sentinel is appended.
- A building entry missing `construction_time`, `supply_bonus`, or `produces_category` is rejected at `FactionDefinition.LoadFromFile` with a located error naming the building id and the missing field(s) — list-all, mirroring `UnitDefinitionValidator`'s shape.
- `SimChecksum`'s existing `BuildingStore` fold (`Alive`, `Health`, `ConstructionTimer`, `HasRallyPoint`, `RallyPoint.X/Z`) is untouched; `AlgoVersion` does not bump.
- `golden-scenario.golden.txt` / `golden-multifaction.golden.txt` stay byte-identical (they exercise only the no-def switch fallback) — no re-baseline.

**Block If:**
- A faction JSON file other than `alpha_faction.json`/`beta_faction.json` declares showcase-building ids with stats that would need correcting differently — HALT (today's switch is faction-agnostic, so "byte-identical to before" would become ambiguous).
- A `BuildingDefinition` type or a dedicated `ContentLoader` class already exists (missed in planning) — HALT rather than forking the source of truth.

**Never:**
- Never touch `TechTreeChecker`'s `ParseBuildingType`/`BuildingTypeId`/`DisplayName` switches or prerequisite-resolution logic (Story 4.2's job) — `BuildingType` stays the prerequisite gate.
- Never touch `BuildingSystem.CategoryForBuilding`'s switch or `BuildingBridge`'s rendering (presentation, enum-indexed `TYPE_COUNT=5`) — `produces_category` is parsed/validated/available but not wired to replace that switch this story; a `Custom` building has no render bucket yet (expected — sim-only per the epic's 4.1-4.4/4.5-4.6 split).
- Never mint a `Validated<BuildingDefinition>` — no applier consumes one; use the same lightweight located-error-list shape as `UnitDefinitionValidator`.
- Never add a new raw `construction_cost` JSON field — it is a computed map derived from the existing `cost_ore`/`cost_crystal` (Story 4.3 owns the real sparse N-resource schema; its dependency note says it needs 4.1's loader path, not a pre-empted schema).

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Showcase building loads | alpha_faction.json's `command_center` (hp corrected to 500, +construction_time=15, supply_bonus=10, produces_category="Worker") loaded via `LoadFromFile` | `BuildingDefinition` with `Hp=500`, `ConstructionTime=15`, `SupplyBonus=10`, `ProducesCategory="Worker"`, `ConstructionCost={ore:0}`, `MinGameVersion` stamped | No error |
| Missing required field | A building entry omits `supply_bonus` | `LoadFromFile` throws with a located message naming the building id and `supply_bonus` | Rejected at import, no `FactionDefinition` returned |
| Placement reads from data | `PlaceBuildingDirect` places `command_center` for a faction whose def carries Hp=500/SupplyBonus=10/ConstructionTime=15 | `BuildingStore.Health`/`SupplyBonus`/`ConstructionDuration` for the new slot equal 500/10/15s — identical to today's baked switch | No error |
| No-enum-entry building | A `BuildingDefinition` id `"watchtower"` (no `BuildingType` member) `Create()`d with `BuildingType.Custom` + resolved stats | Slot is `Alive`, `Type=Custom`, `DefinitionId="watchtower"`, stats from the def | No error |
| Legacy caller, no def | Existing test/AI code calls `Create(pos, faction, BuildingType.Barracks)` with no new params | Falls back to the existing switch — output identical to before this story | No error |
| Determinism | Two identical runs place the same data-defined showcase buildings via `BuildingSystem` in the same tick order, N ticks pass | Per-tick `SimChecksum` sequences byte-identical between the two runs | No error |

</intent-contract>

## Code Map

- `godot/src/Core/Definitions/UnitDefinition.cs` -- base shape `BuildingDefinition` extends; unchanged.
- `godot/src/Core/Definitions/BuildingDefinition.cs` (new) -- `construction_time`/`supply_bonus`/`produces_category` (required, nullable-no-default) + derived `ConstructionCost` map + stamped `MinGameVersion`.
- `godot/src/Core/Definitions/BuildingDefinitionValidator.cs` (new) -- located-error-list validator mirroring `UnitDefinitionValidator`.
- `godot/src/Core/Definitions/FactionDefinition.cs` -- `Buildings: List<UnitDefinition>` → `List<BuildingDefinition>`; `GetBuilding` return type follows; `LoadFromFile` invokes the new validator and throws on failure.
- `godot/src/Core/BuildingStore.cs` -- append `BuildingType.Custom`; `Create()` gains optional resolved-stats params used instead of the switch when supplied; new non-folded `DefinitionId` per-slot array.
- `godot/src/Economy/BuildingSystem.cs` -- `PlaceBuildingDirect`/`QueueWorkerBuild` already resolve the def by id; thread its Hp/SupplyBonus/ConstructionTime into the new `Create()` params.
- `godot/resources/data/factions/alpha_faction.json`, `beta_faction.json` -- correct all 5 buildings' `hp` to the baked values (500/300/300/400/350) and add `construction_time`/`supply_bonus`/`produces_category` to each.
- `godot/ProjectChimera.Sim.Tests/Golden/GoldenScenario.cs` -- unchanged (stays on the switch fallback; proves it untouched by construction).
- `godot/ProjectChimera.Sim.Tests/Core/BuildingStoreDataDrivenTests.cs` (new), `godot/ProjectChimera.Sim.Tests/Definitions/BuildingDefinitionValidatorTests.cs` (new), `godot/ProjectChimera.Sim.Tests/Golden/DataDrivenBuildingScenario.cs` + `DataDrivenBuildingGoldenTests.cs` (new).

## Tasks & Acceptance

**Execution:**
- `godot/src/Core/Definitions/BuildingDefinition.cs` -- New `class BuildingDefinition : UnitDefinition` with `float? ConstructionTime` (`construction_time`), `int? SupplyBonus` (`supply_bonus`), `string? ProducesCategory` (`produces_category`) — all default `null`, no silent fallback. Computed `IReadOnlyDictionary<string,int> ConstructionCost` built from inherited `CostOre`/`CostCrystal` (keys `"ore"`/`"crystal"`, omitted when 0). `[JsonIgnore] public string MinGameVersion { get; set; } = "0.1";` (matches `ContentPackageManifest`'s default; stamped via property initializer since deserialization only touches JSON-mapped members). -- gives buildings the AC1 shape without touching `UnitDefinition` or unit-only consumers.
- `godot/src/Core/Definitions/BuildingDefinitionValidator.cs` -- `static BuildingValidationResult Validate(BuildingDefinition def)` returning all located `(FieldPath, Message)` errors (message `"building '{id}'.{path}: {reason}"`) when `ConstructionTime`/`SupplyBonus`/`ProducesCategory` is null. -- mirrors `UnitDefinitionValidator`'s list-all shape so multiple bad fields surface at once.
- `godot/src/Core/Definitions/FactionDefinition.cs` -- Retype `Buildings` to `List<BuildingDefinition>`, `GetBuilding` to return `BuildingDefinition?`. In `LoadFromFile`, after deserializing, run the validator over every `Buildings` entry and throw `new System.InvalidOperationException(string.Join("\n", allErrors))` if any errors exist. -- the new "rejected at import" gate; units remain unvalidated at load (unchanged).
- `godot/src/Core/BuildingStore.cs` -- Append `Custom = 5` to `BuildingType` (comment: data-defined building, no dedicated enum member; append-only preserved). Add optional trailing params to `Create`: `string buildingId = null, Fixed? health = null, int? supplyBonus = null, Fixed? constructionDuration = null`. When all three stat params are non-null, use them verbatim instead of the switch (still zero `SupplyBonus` first per the existing Story-2.13 recycle-trap guard). Add non-folded `public readonly string[] DefinitionId`, set to `buildingId ?? TechTreeChecker.BuildingTypeId(type)` on every (re)allocation, reset in `Clear()`. -- the store-level capability AC1/AC2 require; fully additive so existing call sites are untouched.
- `godot/src/Economy/BuildingSystem.cs` -- In `PlaceBuildingDirect` and `QueueWorkerBuild`, after resolving `bdef = GetFactionDef(faction)?.GetBuilding(id)`, pass `bdef?.Id`, `bdef != null ? Fixed.FromFloat(bdef.Hp) : (Fixed?)null`, `bdef?.SupplyBonus`, `bdef != null ? Fixed.FromFloat(bdef.ConstructionTime!.Value) : (Fixed?)null` into the new `Create()` params (null propagation preserves today's switch-fallback behavior when no def resolves). -- the production entry points now read stats from data.
- `godot/resources/data/factions/alpha_faction.json`, `beta_faction.json` -- Set `hp` to 500/300/300/400/350 for command_center/barracks/archery_range/siege_workshop/aviary in both files (overriding today's divergent 1500/800/700/700/700 and 1800/1000/900/900/900). Add `construction_time` (15/10/10/12/12), `supply_bonus` (10/0/0/0/0), `produces_category` ("Worker"/"Melee"/"Ranged"/"Siege"/"Air") to all 5 entries in both files. -- the authored data that becomes the new source of truth.
- `godot/ProjectChimera.Sim.Tests/Core/BuildingStoreDataDrivenTests.cs` -- For each of the 4 showcase types: `Create()` with no def (switch path) vs. `Create()` with the def-resolved values — assert `Health`/`SupplyBonus`/`ConstructionTimer` equal between the two. `Create()` with `BuildingType.Custom` + a synthetic def + `buildingId: "watchtower"` succeeds, `Alive` true, `DefinitionId == "watchtower"`. -- proves AC2 directly.
- `godot/ProjectChimera.Sim.Tests/Definitions/BuildingDefinitionValidatorTests.cs` -- A faction JSON string with `buildings[0]` missing `supply_bonus` (and separately `construction_time`, `produces_category`) throws from `FactionDefinition.LoadFromFile` with a message containing the building id and field name. -- proves AC1's rejection path.
- `godot/ProjectChimera.Sim.Tests/Golden/DataDrivenBuildingScenario.cs` + `DataDrivenBuildingGoldenTests.cs` -- New scenario loading `alpha_faction.json` and placing the 4 showcase buildings via `BuildingSystem.PlaceBuildingDirect` (the data-driven path), stepping N ticks; assert two independent runs produce byte-identical `SimChecksum` sequences (a same-run comparison — no committed golden file needed). -- proves AC3 through the actual data-driven path, since the existing goldens never exercise it.

**Acceptance Criteria:**
- Given alpha/beta faction JSON's 5 buildings corrected to the baked HP/supply/construction values, when loaded via `FactionDefinition.LoadFromFile` and placed via `BuildingSystem.PlaceBuildingDirect`, then `BuildingStore.Health`/`SupplyBonus`/`ConstructionDuration` for each equal today's pre-story baked constants exactly.
- Given a faction JSON building entry missing `construction_time`, `supply_bonus`, or `produces_category`, when `FactionDefinition.LoadFromFile` runs, then it throws with a located message naming the building id and the missing field, and no `FactionDefinition` is returned.
- Given a `BuildingDefinition` whose id has no matching `BuildingType` member, when `BuildingStore.Create` is called with `BuildingType.Custom` and resolved stats, then the slot is created and placed with `DefinitionId` set to the def's id.
- Given the existing call sites that invoke `BuildingStore.Create(position, faction, type)` with no new params, when this story lands, then they compile and behave identically, and `golden-scenario.golden.txt`/`golden-multifaction.golden.txt` stay byte-identical with no re-baseline.
- Given two identical runs placing the same data-defined showcase buildings via `BuildingSystem` in the same tick order, when the sim runs for N ticks, then the per-tick `SimChecksum` sequences are byte-identical between the two runs.

## Spec Change Log

_Empty until the first bad_spec loopback._

## Review Triage Log

### 2026-07-08 — Review pass
- intent_gap: 0
- bad_spec: 0
- patch: 6: (high 1, medium 1, low 4)
- defer: 1: (low 1)
- reject: 8: (low 8)
- addressed_findings:
  - `[high]` `[patch]` `BuildingSystem.PlaceBuildingDirect`/`QueueWorkerBuild` unconditionally force-unwrapped `bdef.ConstructionTime!.Value`, crashing with `InvalidOperationException` for any resolved `BuildingDefinition` built outside `FactionDefinition.LoadFromFile`'s validation gate (already bit this story's own `HeroRevivalTests` during implementation) — changed both sites to a null-safe pattern match (`bdef?.ConstructionTime is float ct ? ... : null`) mirroring the existing safe `bdef?.SupplyBonus`, so an incompletely-populated def degrades gracefully to `Create`'s switch fallback instead of throwing. Added `PlaceBuildingDirect_PartiallyPopulatedDef_FallsBackToSwitch_NoThrow` as a regression guard.
  - `[medium]` `[patch]` No test proved `BuildingSystem`'s def-driven Health/SupplyBonus/ConstructionDuration threading reads the DEF's values rather than coincidentally matching the switch defaults (every existing test, including the new golden test, authors stats that numerically equal the switch) — added `PlaceBuildingDirect_ThreadsDefStats_NotSwitchDefaults`, authoring deliberately-different stats and asserting the def's values (with explicit `NotEqual` teeth against the switch constants) land in the store.
  - `[low]` `[patch]` The store-level parity Theory (`BuildingStoreDataDrivenTests.DefResolvedStats_MatchSwitchFallback_ForEachShowcaseType`) covered only the 4 AC2-named showcase types, not Aviary, even though the spec's own Boundaries require Aviary's correction too (and the JSON edit for it is present and correct) — added an `InlineData` row for Aviary (350/0/12s).
  - `[low]` `[patch]` Stale/now-false comment on `BuildingStore.cs`'s `Aviary` switch case claiming "HP/supply/construction come from HERE, not the JSON (building `hp` is vestigial)" — false once a matching def resolves and the resolved-stats path is taken. Rewrote the comment to describe the switch case as the fallback branch only.
  - `[low]` `[patch]` Misleading doc comment on `BuildingType.Custom` claimed it is "ALWAYS placed via `BuildingStore.Create`'s resolved-stats params (never the per-type switch)" — false for the real production path, since `TechTreeChecker.BuildingTypeId(Custom)` has no case for it (returns `""`), so `BuildingSystem` never resolves a def for `Custom` and it would silently fall to the switch `default:` branch if placed that way today. Rewrote the comment to state `Custom` is reachable with real stats only via a direct `BuildingStore.Create()` call today; end-to-end placement through `BuildingSystem`/the editor is deferred to Stories 4.5/4.6.
  - `[low]` `[patch]` `BuildingDefinitionValidator` didn't reject a non-positive `Hp`, even though `Hp` is now load-bearing for buildings (threaded verbatim into `Health`/`MaxHealth`). Added an `Hp <= 0f` check + `hp` located error, and `ZeroHp_Throws_NamingBuildingIdAndField`/`Validate_ZeroOrNegativeHp_ReturnsLocatedError` tests. (Does not catch a fully *omitted* `hp` silently defaulting to 100 — see deferred-work.md.)

Rejected as noise or already addressed by design (no action): the switch not being fully retired in production (a deliberate, documented Design Notes decision, not a bug); `produces_category` not yet wired to `BuildingSystem.CategoryForBuilding` (explicitly out of scope per the spec's "Never" boundary and the epic's own retirement list); `BuildingType.Custom` having no end-to-end placement route through `BuildingSystem`/the editor yet (explicitly deferred to Stories 4.5/4.6 in the spec's Design Notes); `MinGameVersion`/`ConstructionCost` being unconsumed by production code (both are AC1-required *produced* fields; consumption is later stories' job per Design Notes); the HP JSON correction having no separate STATUS.md/CONTEXT.md record (required by AC2's literal text, already documented in this spec, and STATUS.md/CONTEXT.md updates are `/save`'s job); `DataDrivenBuildingGoldenTests` proving only self-consistency, not absolute correctness (exact-value correctness is proven separately by `BuildingStoreDataDrivenTests`, by design); and the multi-building "list every missing field at once" behavior only being tested within a single building (the validator loop has no per-building special-casing to separately verify).

## Design Notes

**Why additive params on `Create()` instead of retiring the switch outright:** ~50 call sites (tests, `AiOpponentSystem`, `EntityPlacer`) construct buildings via the bare enum overload with no `FactionDefinition` in scope, including tests that deliberately exercise the switch's `default:` branch (`BuildingStoreRecycleTests.cs:91`). AC2's Given clause scopes the requirement to "when a building is placed from a loaded definition" — additive optional params satisfy that for the two production entry points that already resolve a def, without an unrelated rewrite of dozens of combat/recycle unit tests. The switch is provably unreachable from the production path once `BuildingSystem` always resolves and passes a def for known ids; it survives only as the harness's synthetic-building shortcut.

**Why `produces_category` and `CategoryForBuilding`'s switch aren't unified here:** AC2 scopes "previously baked ... switch" to HP/supply/construction-duration; the epic's retirement list doesn't include `CategoryForBuilding`. `produces_category` is authored, validated, and available on `BuildingDefinition` (closing the authoring half); wiring it to replace the switch is left for later to avoid touching production-queue logic un-chartered by this story's ACs.

**Why `ConstructionCost` is computed, not a new JSON field:** Story 4.3 is the epic's designated story for the real authored sparse N-resource map, and its dependency note says it needs 4.1's loader path — not a pre-empted schema. Deriving `ConstructionCost` from `CostOre`/`CostCrystal` satisfies AC1's "construction_cost as a cost map" today without a second, soon-to-be-replaced JSON surface.

## Verification

**Commands:**
- `dotnet build godot/godot.sln -c Debug` -- expected: builds clean.
- `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj -c Release` -- expected: all Tier-1 tests green, including the new tests, and every existing golden test byte-identical (no re-baseline; a moved golden is a regression to fix, not re-record).
- `git diff --stat godot/ProjectChimera.Sim.Tests/Golden/*.golden.txt` -- expected: empty.

## Auto Run Result

Status: done

**Summary:** Buildings are now data-driven. `BuildingStore.Create()` gained an additive, backward-compatible resolved-stats path that reads Health/SupplyBonus/ConstructionDuration from a loaded `BuildingDefinition` instead of the per-`BuildingType` switch, which survives only as the fallback for the ~50 callers with no def in scope. A new `BuildingDefinition : UnitDefinition` carries `construction_time`/`supply_bonus`/`produces_category` (required, located-error-validated at `FactionDefinition.LoadFromFile`) plus a derived `ConstructionCost` map and stamped `MinGameVersion`. `alpha_faction.json`/`beta_faction.json` were corrected so all 5 buildings' `hp` (previously vestigial/divergent) now match the baked constants exactly, plus the three new fields. `BuildingType.Custom` was appended so a building with no dedicated enum member is creatable via the resolved-stats path directly (not yet reachable through `BuildingSystem`/the editor — deferred to Stories 4.5/4.6, per design).

**Files changed:**
- `godot/src/Core/Definitions/BuildingDefinition.cs` (new) — `BuildingDefinition : UnitDefinition` with required-nullable `ConstructionTime`/`SupplyBonus`/`ProducesCategory`, computed `ConstructionCost`, `MinGameVersion`.
- `godot/src/Core/Definitions/BuildingDefinitionValidator.cs` (new) — located-error-list validator (construction_time/supply_bonus/produces_category required; `hp` must be positive, added in review).
- `godot/src/Core/Definitions/FactionDefinition.cs` — `Buildings`/`GetBuilding` retyped to `BuildingDefinition`; `LoadFromFile` validates every building, throws a joined located-error message on failure.
- `godot/src/Core/BuildingStore.cs` — appended `BuildingType.Custom = 5`; `Create()` gained optional `buildingId`/`health`/`supplyBonus`/`constructionDuration` params (used verbatim when all three stats are supplied, else the untouched switch); new non-folded `DefinitionId[]` array; two doc/comment corrections from review (Custom reachability, Aviary switch case).
- `godot/src/Economy/BuildingSystem.cs` — `PlaceBuildingDirect`/`QueueWorkerBuild` resolve the faction's `BuildingDefinition` and thread Hp/SupplyBonus/ConstructionTime into `Create()`; review fix replaced an unconditional `ConstructionTime!.Value` force-unwrap with a null-safe pattern (crash risk for any hand-built def missing the field).
- `godot/resources/data/factions/alpha_faction.json`, `beta_faction.json` — corrected `hp` to baked values (500/300/300/400/350) and added `construction_time`/`supply_bonus`/`produces_category` to all 5 buildings in each file.
- `godot/ProjectChimera.Sim.Tests/Combat/HeroRevivalTests.cs` — 3 call sites switched `new UnitDefinition` to `new BuildingDefinition` (forced by `Buildings`' retype) with the new required fields authored.
- `godot/ProjectChimera.Sim.Tests/Core/BuildingStoreDataDrivenTests.cs` (new) — store-level parity/Custom/legacy tests, plus review additions: Aviary parity row, `BuildingSystem`-level differing-values threading proof, partial-def no-throw fallback proof.
- `godot/ProjectChimera.Sim.Tests/Definitions/BuildingDefinitionValidatorTests.cs` (new) — required-field rejection tests, plus review addition: zero/negative `hp` rejection tests.
- `godot/ProjectChimera.Sim.Tests/Golden/DataDrivenBuildingScenario.cs` + `DataDrivenBuildingGoldenTests.cs` (new) — data-driven placement determinism proof (two independent runs, byte-identical `SimChecksum` sequences).
- `_bmad-output/implementation-artifacts/epic-4-context.md` (new) — compiled Epic 4 planning context (reusable by later Epic 4 stories).
- `_bmad-output/implementation-artifacts/deferred-work.md` — one new entry (Hp omission not caught by the validator; see below).

**Review findings breakdown:** 6 patches applied (1 high: unconditional `ConstructionTime!.Value` crash risk in `BuildingSystem`; 1 medium: no test proved `BuildingSystem`-level threading over switch-coincidence; 4 low: Aviary parity test gap, two stale/misleading doc comments, missing `Hp` positivity check). 1 deferred (Hp omission silently defaults to 100 — no reachable path today, no creator-facing authoring UI exists yet; see `deferred-work.md`). 8 rejected as noise or already addressed by the spec's own deliberate, documented scope decisions (switch not fully retired, `produces_category` not wired to `CategoryForBuilding`, `Custom` not end-to-end placeable yet, `MinGameVersion`/`ConstructionCost` unconsumed-but-AC1-required, HP JSON correction undocumented outside this spec, golden test proving only self-consistency, multi-building validator aggregation untested). No intent_gap or bad_spec loopback — all four review layers (Blind Hunter, Edge Case Hunter, Verification Gap, Intent Alignment) confirmed the implementation faithfully executed the spec's deliberately-scoped design.

**Verification:** `dotnet build godot/godot.sln -c Debug` → 0 errors (both before and after the review-pass patches). `dotnet test ProjectChimera.Sim.Tests -c Release` → 1127 passed, 1 skipped, 1 failed (`ProceduralMapGeneratorTests.SameSeed…`), confirmed pre-existing on baseline `061b4fc` (unrelated cross-platform golden mismatch, not this change) both before implementation and after the review patches. `git diff --stat` of `*.golden.txt` empty both times (no re-baseline). All 6 I/O matrix rows covered by tests that ran and passed.

**Residual risks:** `BuildingDefinitionValidator` cannot catch a fully *omitted* `hp` (silently defaults to 100 via the inherited non-nullable `UnitDefinition.Hp`) — deferred, no current reachable path (see `deferred-work.md`). `BuildingType.Custom` has no placement route through `BuildingSystem`/the editor yet — explicit, by-design deferral to Stories 4.5/4.6. `produces_category` is authored/validated but not yet wired to replace `BuildingSystem.CategoryForBuilding`'s switch — explicit, by-design deferral (out of this story's AC2 scope).
