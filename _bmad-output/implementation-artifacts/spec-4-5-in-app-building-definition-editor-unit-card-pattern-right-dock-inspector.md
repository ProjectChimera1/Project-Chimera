---
title: 'In-app building definition editor (Unit-Card pattern, right-dock inspector)'
type: 'feature'
created: '2026-07-09'
baseline_revision: '5ad509795d24682c8cbc2298e6536e7de7842052'
status: 'done'
review_loop_iteration: 0
followup_review_recommended: false
context: []
warnings: ['oversized']
---

<intent-contract>

## Intent

**Problem:** Creators can't author `BuildingDefinition`s in-app — only hand-edited faction JSON — and even Story 4.3's sparse `cost` map has no persistence path at all today: `FactionWriter.ApplyFields` never writes the `cost` key for units OR buildings, so an authored sparse cost map is silently dropped on any Save/round-trip.

**Approach:** Mirror the Unit-Card editor (Stories 3.3/3.4) into a new `BuildingCardPanel`/`BuildingCardPanel.Edit.cs`, reusing `BuildingDefinition : UnitDefinition`'s inherited field builders (`AddText`/`AddNumFloat`/`AddSelect`/`AddModelRow`) wholesale, adding the three building-only fields plus a new sparse cost-map composite control. Extend `FactionWriter` with a `buildings[]` counterpart to `SyncFactionUnits`/`PatchFactionJson` (and fix the `cost`-map write gap for both units and buildings). Extend building validation to the same id/dup-id/cost-range coverage `UnitDefinitionValidator` already gives units, by reusing it over the building list via `IReadOnlyList<T>` covariance instead of duplicating ~20 checks.

## Boundaries & Constraints

**Always:** Presentation-layer only (`CreationSuite`/`Bootstrap` Control code) — never touches `EntityWorld`/sim arrays/`BuildingSystem`/checksums. All writes go through `FactionWriter`'s `JsonNode` DOM-patch reconciler (never a reflection re-serialize), and Save self-checks by reloading the freshly-written `.tmp` file through `FactionDefinition.LoadFromFile` before the atomic `File.Move`, exactly like `UnitCardPanel.Edit.cs:1148-1171`. Reuse the existing 3.1 kit (`ChimeraComponents`/`ChimeraTabs`/`ChimeraValidationBadge`/`ChimeraDialog`/`ChimeraTooltip`) and the field-builder helpers already proven on `UnitDefinition`. Invalid input (blank id, duplicate id, out-of-range/negative cost, an unauthored required building field) shows a located per-field badge and disables Save; nothing invalid ever reaches disk. The sparse `cost` map is authored as resource-id→amount rows restricted to `{"ore","crystal"}` — the only ids with `ResourceStore` backing today (Story 4.3's Design Notes fence, not `ScenarioData.Resources`); legacy `cost_ore`/`cost_crystal` stay the fallback when `cost` is absent.

**Block If:** If `godot/src/Economy/BuildingSystem.cs`/`BuildingStore.cs`'s current consumption of `ConstructionCost`/`ResolvedCost`/`ConstructionTime`/`SupplyBonus`/`ProducesCategory` has diverged from the Story 4.1/4.3 baseline this spec assumes (re-verify before relying on the AC4 runtime-round-trip claim) — HALT with blocking condition `runtime consumption diverged from 4.1/4.3 baseline` rather than inventing new sim-layer behavior (out of this story's presentation-only scope).

**Never:** Never wire `produces_category`/prerequisites into runtime gating systems (`BuildingSystem`/`TechTreeChecker`) — out of scope, owned by prior/later stories. Never build the tech-tree graph UI (Story 4.6) — only the card list + inspector. Never extend the cost-map's known-resource set beyond `{"ore","crystal"}` or cross-reference `ScenarioData.Resources` (4.3's explicit scope fence). Never let `BuildingCardPanel` edit `_faction.Units` — units stay `UnitCardPanel`'s exclusive surface.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Blank id | Creator clears a building's id field | Save disabled | Badge on `id`: "must be a non-empty id." |
| Duplicate id | Creator sets a building's id to match a sibling building's id | Save disabled | Badge on `id`: "is a duplicate…" |
| Negative/overflow cost | `cost["ore"] = -10` or `>= 32768` | Save disabled | Badge on `cost`, located range message |
| Unknown resource id (raw JSON) | Raw pane authors `"cost": {"gas": 5}` | Raw-pane Save rejected | `ShowError` with located unknown-id message; nothing written |
| Missing required field | New building's `construction_time`/`supply_bonus`/`produces_category` left unauthored | Save disabled | Badge on the missing field |
| Valid full edit | Stats + cost map + construction fields all valid, Save pressed | `SyncFactionBuildings` patches only `buildings[]`; self-check reload passes; atomic file replace | Status line "Saved" |
| Raw-JSON edit | Creator edits the raw pane, Saves | Simple-mode fields (incl. cost rows) rebuild from the folded object on `Refresh()` | None |
| Existing content unaffected | `alpha_faction.json`/`beta_faction.json` buildings (no `cost` map, only legacy `cost_ore`/`cost_crystal`) loaded and placed | Byte-identical behavior; golden checksums unchanged | None (no sim-layer change) |

</intent-contract>

## Code Map

- `godot/src/Core/Definitions/UnitDefinitionValidator.cs:99-331,430-433` -- terminal `Validate` overload + `Located`/`CheckCost`: add a `kind` parameter and a sparse-cost-map check.
- `godot/src/Core/Definitions/BuildingDefinitionValidator.cs` -- add a siblings-aware overload merging the above.
- `godot/src/Core/Definitions/FactionWriter.cs:194-247,298-333` -- `ApplyFields` (add `cost`-map write) + new buildings-array writer surface.
- `godot/src/CreationSuite/UnitCardPanel.cs` / `UnitCardPanel.Edit.cs` -- the pattern to mirror (read-only reference, no edits).
- `godot/src/CreationSuite/BuildingCardPanel.cs` (new) -- shell.
- `godot/src/CreationSuite/BuildingCardPanel.Edit.cs` (new) -- editable fields + persistence + new cost-map control.
- `godot/src/Core/Bootstrap/Phases/UnitCardPhase.cs` / `ItemCardPhase.cs` -- bootstrap pattern to mirror.
- `godot/src/Core/Bootstrap/Phases/BuildingCardPhase.cs` (new).
- `godot/src/Core/Bootstrap/SceneContext.cs:110-111` -- publish point.
- `godot/src/Core/MainScene.cs:408-409,580-591` -- phase registration + Edit-mode hotkey.
- `godot/resources/data/factions/_unitcard_sample.json` -- fixture pattern to mirror.
- `godot/ProjectChimera.Sim.Tests/Definitions/BuildingDefinitionValidatorTests.cs` -- extend.
- `godot/ProjectChimera.Sim.Tests/Definitions/FactionWriteRoundTripTests.cs` -- extend (or a sibling file if cleaner).

## Tasks & Acceptance

**Execution:**
- `godot/src/Core/Definitions/UnitDefinitionValidator.cs` -- Add a `string kind = "unit"` parameter to the terminal 5-arg `Validate(...)` overload (the one with the real body, `:126-132`) and thread it into `Located` (`:430-433`, `$"{kind} '{id}'.{path}: {reason}"`); the thinner 3-/4-arg overloads keep calling it with the default `"unit"`, so every existing unit call site is unaffected. Also add a new private `CheckCostMap(errors, kind, id, Dictionary<string,int>? cost)` rule invoked from the terminal `Validate` alongside the existing `CheckCost(errors, id, "cost_ore", ...)`/`CheckCost(..., "cost_crystal", ...)` calls (`:211-212`): for each authored `(key,value)` in `def.Cost` (skip when null), if `key` not in `{"ore","crystal"}` add a located `("cost", ...)` unknown-id error (mirrors `ResourceCostValidator.ValidateEntry`'s message), else apply the same `>=0 && <32768` range rule keyed `"cost"`. -- lets `BuildingDefinitionValidator` reuse id/dup-id/enum/cost-range checks with accurate "building '<id>'…" messages instead of duplicating ~20 checks, and closes a latent gap where the sparse `cost` map had zero per-field editor validation (only whole-faction `ResourceCostValidator`, bare strings, no badge target) for units too.
- `godot/src/Core/Definitions/BuildingDefinitionValidator.cs` -- Add `Validate(BuildingDefinition def, IReadOnlyList<BuildingDefinition>? siblings)`: run the existing 4 building-only checks (hp/construction_time/supply_bonus/produces_category, `:43-77`) AND merge `new UnitDefinitionValidator().Validate(def, registry: null, behaviorRegistry: null, itemRegistry: null, siblings, kind: "building").Errors` (covariant `IReadOnlyList<BuildingDefinition>` → `IReadOnlyList<UnitDefinition>`) into one `BuildingValidationResult`. Keep the existing 1-arg `Validate(def)` as `Validate(def, null)` so `FactionDefinition.LoadFromFile` (`:134-140`) compiles unchanged. -- gives the editor the same id/dup-id/category/cost-range coverage units get.
- `godot/src/Core/Definitions/FactionWriter.cs` -- In `ApplyFields` (`:194-247`), add a new `PutCostMap(obj, "cost", d.Cost)` helper (write the dictionary only when it differs, key-set-and-value comparison, from the on-disk `"cost"` object; omit the key entirely when `d.Cost` is null) and call it alongside the existing `PutNullableInt`/`PutStringArray` calls. -- closes the confirmed gap where `SyncFactionUnits`/`PatchFactionJson`/`SerializeUnitClean` never persist Story 4.3's sparse cost map for units OR buildings; required before this story's cost-map AC can round-trip at all.
- `godot/src/Core/Definitions/FactionWriter.cs` -- Add the buildings-array counterpart to the units machinery: `BuildingEditKind`/`BuildingEdit` (mirrors `UnitEditKind`/`UnitEdit`), `PatchFactionBuildingJson(string factionJson, BuildingEdit edit)` (mirrors `PatchFactionJson`, `:82-149`, operating on `root["buildings"]`), `SerializeBuildingClean(BuildingDefinition def)` (mirrors `SerializeUnitClean`, `:158-163`), `SyncFactionBuildings(string factionJson, IReadOnlyList<BuildingDefinition> buildings)` (mirrors `SyncFactionUnits`, `:298-333`, reconciling `root["buildings"]` only — never touches `root["units"]`), and a private `ApplyBuildingFields(JsonObject obj, BuildingDefinition d)` that calls `ApplyFields(obj, d)` then unconditionally writes `construction_time`/`supply_bonus`/`produces_category` (all three are required-nullable per `BuildingDefinitionValidator` — write directly, no omit-at-default). -- the writer surface without which buildings have no save path at all.
- `godot/src/CreationSuite/BuildingCardPanel.cs` (new) -- Shell mirroring `UnitCardPanel.cs`: `CanvasLayer`(Layer 13) → `PanelContainer` (`ChimeraComponents.Panel`, `CenterRight`, 480×700) → title row (heading "Building Editor" + ◀/▶ browse + counter + Close) → `ScrollContainer`/`_bodyHost`. No `SubViewport`/`Camera3D`/turntable subtree (the epic's UX section scopes this editor to stats/cost/inspector, not a live 3D preview — `AddModelRow`'s mesh-path text field is still reused, just without the render). Browse cursor over `_faction.Buildings`+`_index` (never `_faction.Units`). `Initialize(FactionDefinition? faction, GameState gameState, string factionJsonPath)` — no ability/behavior registry params (buildings don't author `abilities[]`/`behaviors[]`). `Toggle()`/`Close()`/`OnModeChanged` mirror `UnitCardPanel`'s hide-in-Play behavior. -- the read-only browse/shell half.
- `godot/src/CreationSuite/BuildingCardPanel.Edit.cs` (new) -- Editable-fields partial mirroring `UnitCardPanel.Edit.cs`: reuse `AddText`(id/display_name), `AddModelRow`, `AddSelect`(category), and the inherited stat rows (`hp`/`attack_*`/`armor`/etc.) verbatim, plus new rows `AddNumFloat`("Construction Time", "construction_time", ...)`, `AddNumInt`("Supply Bonus", "supply_bonus", ...)`, `AddSelect`("Produces", "produces_category", items: Categories + "None", ...)`, and a new `AddCostMapRow(Control parent, Func<Dictionary<string,int>?> get, Action<Dictionary<string,int>?> set, BuildingDefinition def)` composite (chip-per-resource + per-chip `SpinBox` amount + remove, "+ Add resource" `OptionButton` restricted to `{"ore","crystal"}`) mirroring `AddComponentPicker`'s chip+add-select shape (`UnitCardPanel.Edit.cs:493-594`) keyed by resource id with a numeric value instead of a bare id list. `RevalidateAndReflect()` calls the new `BuildingDefinitionValidator.Validate(_current, _faction?.Buildings)` overload + the same `MeshError` check (mesh_path is inherited, rule is identical). `DoCreate`/`DoDuplicate`/`DoDelete`/`UniqueId`/`IdExists` mirror the unit editor 1:1 against `_faction.Buildings`, reusing `UnitDefinitionValidator.SanitizeId`. `PersistSync` calls `FactionWriter.SyncFactionBuildings` with the identical read-current/write-`.tmp`/self-check-`LoadFromFile`/atomic-`File.Move` sequence as `UnitCardPanel.Edit.cs:1148-1171`. `BuildRawPane` seeds from `FactionWriter.SerializeBuildingClean`; `SaveFromRawPane` deserializes into a `BuildingDefinition` and validates via the same siblings-aware validator. Ctrl+Z/Y gated on `_panel.Visible` + `SetInputAsHandled()` (mirrors `UnitCardPanel.Edit.cs:50-51`, avoiding a double-fire with other open editors). -- the editable-fields + persistence half.
- `godot/src/Core/Bootstrap/Phases/BuildingCardPhase.cs` (new) -- `ISetupPhase` mirroring `UnitCardPhase.cs`: constructs `BuildingCardPanel`, adds to `_ctx.Scene`, resolves `factionPath` with the same slot-0-scenario-else-P1-alpha logic `UnitCardPhase.cs:31-34` uses (independent copy, matching `ItemCardPhase`'s own precedent — minimal blast radius), calls `Initialize(_ctx.FactionDef, _ctx.GameState, factionPath)`, publishes `_ctx.BuildingCardPanel`. -- bootstrap wiring; shares the SAME `_ctx.FactionDef` instance `UnitCardPhase` binds, so both panels' in-memory edits stay consistent without either reloading.
- `godot/src/Core/Bootstrap/SceneContext.cs:110-111` -- Add `public CreationSuite.BuildingCardPanel BuildingCardPanel = null!;  // Story 4.5 (BuildingCard phase)` alongside `UnitCardPanel`/`ItemCardPanel`. -- publish point.
- `godot/src/Core/MainScene.cs:408-409` -- Add `new BuildingCardPhase(_ctx),` to the phase list, after `ItemCardPhase`. -- registers the phase.
- `godot/src/Core/MainScene.cs:580-591` -- Add `else if (key.Keycode == Key.C) { _ctx.BuildingCardPanel.Toggle(); GetViewport().SetInputAsHandled(); }` with a comment noting `C` is unused (verified: no `Key.C` check anywhere in `src/`, and no InputMap action binds physical key C in `project.godot`; `B` is taken by `EntityPlacer`'s building-placement-mode toggle). -- Edit-mode-only open hotkey, mirroring every other editor panel.
- `godot/resources/data/factions/_buildingcard_sample.json` (new) -- A small isolated faction JSON (2-3 buildings) mirroring `_unitcard_sample.json`'s role for the `/godot-verify` manual pass — never referenced by production code. -- isolated fixture so `alpha_faction.json`/`beta_faction.json` are never mutated by manual verification.
- `godot/ProjectChimera.Sim.Tests/Definitions/BuildingDefinitionValidatorTests.cs` -- One test per new check on the siblings-aware overload: blank id, duplicate id (two buildings sharing an id via `siblings`), unknown `category`, negative `cost["ore"]`, `cost["crystal"] >= 32768`, unknown cost-map resource id, and an existing valid `alpha_faction.json` building still passing with `siblings: null` (the `LoadFromFile` call path, unaffected by this story). -- proves the new validator surface (Tier-1, no Godot).
- `godot/ProjectChimera.Sim.Tests/Definitions/FactionWriteRoundTripTests.cs` (extend) -- Round-trip tests for `SyncFactionBuildings`/`PatchFactionBuildingJson`/`SerializeBuildingClean`: an edited building's `construction_time`/`supply_bonus`/`produces_category`/`cost` map persist and reload identically via `FactionDefinition.LoadFromFile`; an untouched building/every unit/every faction-level key stays byte-for-byte identical; a `cost` map on a UNIT also now round-trips (proves the `PutCostMap` fix generalizes to the existing unit path). -- proves the new writer surface plus the pre-existing cost-map persistence gap fix.

**Acceptance Criteria:**
- Given the Building Card Editor (opened with `C` in Edit mode), when the creator adds a building via New, edits its stats/cost map/construction time/supply bonus/produced category, and presses Save, then a valid `BuildingDefinition` is written via `FactionWriter.SyncFactionBuildings` and `FactionDefinition.LoadFromFile` on that same file returns an equivalent definition (same stats/cost/construction_time/supply_bonus/produces_category).
- Given invalid input (blank id, duplicate id, out-of-range/negative cost amount, or an unauthored required building field), when the creator attempts Save, then the offending field(s) show a located badge, Save stays disabled, and no file write occurs.
- Given the raw-JSON escape hatch, when the creator edits the JSON and Saves, then the simple-mode fields rebuild to reflect the edit on `Refresh()`, and no code in `BuildingCardPanel*.cs` references `ProjectChimera.Economy`/`ProjectChimera.Combat`/any sim array.
- Given a building saved through the editor, when a scenario loads that faction and the building is placed, then it places with exactly the authored stats (unchanged runtime path — this story only fixes the writer so already-consumed data is now actually persisted).
- Given the existing `alpha_faction.json`/`beta_faction.json` buildings (no `cost` map authored), when loaded and placed exactly as before, then golden checksums stay byte-identical (`PutCostMap` only writes a key when `d.Cost` is non-null).

## Spec Change Log

_Empty until the first bad_spec loopback._

## Review Triage Log

### 2026-07-09 — Review pass
- intent_gap: 0
- bad_spec: 0
- patch: 4: (high 2, medium 0, low 2)
- defer: 9: (high 0, medium 3, low 6)
- reject: 10: (high 0, medium 0, low 10)
- addressed_findings:
  - `[high]` `[patch]` `UnitCardPanel.Edit.cs`'s `CloneUnit` never copied `UnitDefinition.Cost` (the Story 4.3 sparse map) — inert before this story since `FactionWriter.ApplyFields` never persisted `cost` at all, but now that this story's `PutCostMap` fix makes `cost` actually round-trip, `CloneUnit`'s omission silently drops a duplicated unit's authored cost-map override on the next Save. Added `Cost = s.Cost is null ? null : new Dictionary<string,int>(s.Cost)`, mirroring `CloneBuilding`'s own (already-correct) handling. Flagged by the Verification-Gap review layer.
  - `[high]` `[patch]` `BuildingCardPanel.Edit.cs`'s `CloneBuilding` was not a comprehensive field-by-field clone of every inherited `UnitDefinition` field the way `CloneUnit` is — missing `Abilities`/`Behaviors`/`AttackDomains`/`Tags`/`IsHero`/`XpBounty`, and, materially, `RevivesHeroes`/`Hero`/`SellsItems`/`ShopStock`/`ShopRadius`/`CombatFeedback` — fields Stories 3.14/3.16 make genuinely relevant to a Structure building (a hero-revival structure or a shop building). Duplicating such a building via the panel's toolbar silently stripped its revive/shop capability. Expanded `CloneBuilding` to copy every field `CloneUnit` copies, matching this spec's own "mirror the unit editor 1:1" directive. Flagged independently by the Blind Hunter and Edge Case Hunter review layers.
  - `[low]` `[patch]` `BuildingCardPanel.Edit.cs`'s `RevalidateAndReflect` (and the identical pre-existing `UnitCardPanel.Edit.cs`) painted one badge per validation error via a plain per-error loop — the new `CheckCostMap` check can raise multiple simultaneous errors under the SAME `"cost"` field-path key (one per bad resource entry), so the last error silently overwrote every earlier one on the shared cost-map badge while the status line's error count still counted both. Changed both editors to group errors by `FieldPath` before badging, joining every message for a shared key into one badge. Flagged by the Blind Hunter review layer.
  - `[low]` `[patch]` The `{"ore","crystal"}` known-resource-id set was independently hardcoded in three places (`ResourceCostValidator`, the new `UnitDefinitionValidator.CheckCostMap`, and `BuildingCardPanel.Edit.cs`'s `CostResourceIds`) with no shared source of truth. Promoted `ResourceCostValidator`'s set to an internal `KnownResourceIds` array and pointed both `UnitDefinitionValidator` and `BuildingCardPanel.Edit.cs` at it, so the validator's accepted set and the UI's offered set can never silently drift apart. Flagged by the Blind Hunter review layer.

## Design Notes

**Why reuse `UnitDefinitionValidator` via a `kind` parameter instead of duplicating checks or string-replacing messages:** `BuildingDefinition : UnitDefinition` already carries every field `UnitDefinitionValidator` checks (id, category, costs, etc.); hand-duplicating ~20 rules would drift the moment either validator changes. `IReadOnlyList<T>`'s covariance lets a `List<BuildingDefinition>` pass directly as `IReadOnlyList<UnitDefinition>` for the sibling-uniqueness check with zero copying. The one real mismatch — every message hardcoded `"unit '<id>'…"` — is fixed at the source (a threaded `kind` parameter defaulting to `"unit"`) rather than patched after the fact, so a `"building"`-kinded call gets an accurate message with no string-matching fragility.

**Why the cost-map resource set stays `{"ore","crystal"}`:** Matches Story 4.3's Design Notes exactly — `ScenarioData.Resources` is scenario-declared metadata with no `ResourceStore` backing beyond ore/crystal today; no story in this epic introduces a third spendable resource. Widening this is a future story's job once a third resource has runtime storage.

**Why no 3D preview in `BuildingCardPanel`:** The epic's UX section (`UX-DR74`) and both epics.md ACs describe "stats/cost/construction time/supply/category in the right-dock inspector" — no preview requirement, unlike the Unit Card Editor's showcase role. Skipping the `SubViewport`/`Camera3D`/turntable subtree keeps the shell smaller with no lost acceptance coverage; a future story can add one by copying `UnitCardPanel.BuildPreviewHost` if ever needed.

## Verification

**Commands:**
- `dotnet build godot/godot.sln -c Debug` -- expected: builds clean.
- `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj -c Release` -- expected: all Tier-1 tests green, including new/updated tests.
- `git diff --stat godot/ProjectChimera.Sim.Tests/Golden/*.golden.txt` -- expected: empty (presentation-only story, zero sim/checksum touch).

**Manual checks (if no CLI):**
- `/godot-verify` against `_buildingcard_sample.json`: open with `C`, create/edit/duplicate/delete a building, trigger each I/O-matrix edge case (blank id, dup id, negative cost, unknown cost-map key, missing required field), confirm badges + Save gating, confirm raw-JSON round trip, then `git checkout` the fixture to restore it.
