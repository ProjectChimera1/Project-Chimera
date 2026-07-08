---
title: 'Item authoring, shop buildings, and inventory UI'
type: 'feature'
created: '2026-07-08'
status: 'done'
review_loop_iteration: 0
followup_review_recommended: false
baseline_revision: '9ceacdbbb369e98d803a07b1339b0b99172f5c16'
final_revision: 'ec03a205348a69d1572e262be082edc0cfffc6ec'
context:
  - '{project-root}/_bmad-output/implementation-artifacts/epic-3-context.md'
  - '{project-root}/_bmad-output/implementation-artifacts/spec-3-15-item-inventory-sim-pickups-slots-stat-effects-charges.md'
  - '{project-root}/_bmad-output/implementation-artifacts/3-4-unit-card-editor-edit-create-duplicate-delete-with-inline-validation-persisted-to-faction-data.md'
  - '{project-root}/_bmad-output/implementation-artifacts/spec-3-9-offline-hero-persistence-rail-save-load-hero-picker-deterministic-init-time-apply.md'
warnings: ['multiple-goals', 'oversized']
---

<intent-contract>

## Intent

**Problem:** Story 3.15 shipped the item & inventory *sim* (`ItemStore`, per-hero inventory, pickups, stat effects, charged consumables) but nothing authorable or visible: there is no item editor, no way to sell items, no in-match inventory display, and hero inventory does not survive between matches although the persistence manifest (FR-7a/FR-64) promises an "inventory" attribute. The item sim is invisible and item play is JSON-only.

**Approach:** Three cohesive item-domain deliverables, each reusing an existing pattern verbatim: (1) an **item authoring editor** (`ItemCardPanel`) mirroring the Story 3.4 Unit Card Editor, persisting each item to its own `resources/data/items/<id>.json` through the same fail-closed `Validated<T>` gate; (2) **shop buildings** — a `sells_items` capability flag + authored stock on the building definition (mirroring `revives_heroes`) and a new `BuyItem` command that rides the shared `OrderApplier` exactly as the 2.8 Train / 3.14 Revive building-commands do (ownership + affordability guards, spend + mint atomically at exec-tick); (3) an **in-match inventory grid** on the selection HUD composed from the 3.1x design-system kit, plus making the **`hero.inventory` persistence attribute real** so the 3.9 hero-picker displays saved inventory. All UI reads sim arrays/definitions; the single sim addition (`BuyItem` mint + resource spend) rides the already-folded `ItemStore`/inventory/`ResourceStore` state — **no `SimChecksum` or `StartStateHash` `AlgoVersion` bump** (both folds were pre-wired by 3.15).

## Boundaries & Constraints

**Always:**
- **Presentation/sim separation holds.** The item editor, shop panel, inventory grid, and hero-picker inventory display are Godot `Control` nodes reading sim arrays / definitions only; the sim layer (`ItemStore`, `ItemSystem`, `BuildingStore`, validators, loaders) stays Godot-free and Tier-1 testable.
- **Reuse the established patterns, do not invent new ones.** The item editor mirrors `UnitCardPanel`/`UnitCardPanel.Edit.cs` in `ProjectChimera.CreationSuite` (code-built, Simple/Advanced segmented hosts, per-field `ChimeraValidationBadge` keyed by JSON field, raw-JSON pane where `_paneDirty` wins on Save, `EditorHistory` undo, toolbar New/Duplicate/Delete with `UniqueId` + `ChimeraDialog` confirm, F5 fail-closed `_Input` gate). Shop purchase mirrors `BuildingSystem.ReviveHeroCommand` (building-ownership → capability → affordability → atomic spend at exec). The inventory grid composes only from `ChimeraComponents`/`Chimera*` kit primitives (`Panel`, `IconButton`, `Chip`/`Readout`, `ChimeraTooltip`, `Tag`) — log any missing primitive, do not hardcode a token.
- **Item editor persistence:** serialize each `ItemDefinition` to its own `resources/data/items/<id>.json` via `ContentJson.Options` (indented), atomic `.tmp` write + reload self-check through `ItemLoader.LoadFromFile` (refuse to report "Saved" if it will not reload), then `File.Move` overwrite; Delete removes the file. Every create/edit/duplicate passes the same fail-closed `ItemDefinitionValidator` `Validated<T>` gate; F5/playtest is blocked while the current item is invalid.
- **Located per-field validation.** Extend `ItemDefinitionValidator` to emit keyed `(FieldPath, Message)` tuples (mirror `UnitDefinitionValidator`'s `UnitValidationResult.Errors`) for the editor's badges, while **keeping** the existing single-`Error` `ItemValidationResult` path the sim gate mints from. Dangling/oversized effect graphs (via `EffectBounds`) and **missing icon files** (new: reject a non-empty `icon` whose file does not exist under `res://`) fail closed with an actionable, field-located message.
- **Shop building definition:** add `sells_items` (bool), `shop_stock` (string[] of item-def ids), and `shop_radius` (`Fixed`) to the building definition (`UnitDefinition`, the Structure-category shape), **mirroring `revives_heroes` exactly** — gated to `Structure` in `UnitDefinitionValidator`, round-tripped by `FactionWriter`, and resolved into a parallel `BuildingStore.SellsItems[]` (+ per-building stock/radius) at placement via a `ResolveSellsItems` mirroring `ResolveRevivesHeroes`. A `shop_stock` entry naming an item id absent from the `ItemRegistry` is a located validation error.
- **`BuyItem` command:** `UnitCommand.BuyItem = 18` (stays ≤ `0x3F`); `UnitId` = shop buildingId, `TargetX` = stock index (raw int), buying hero entity id = `TargetZ` (raw int). Dispatched in `OrderApplier.Apply` **before the entity guard** (like Train/Revive/SetRally) and `return`s without persisting a `CommandState`; delegates to a new `BuildingSystem.BuyItemCommand`. Threaded through **all three apply sites** (`LockstepManager`, `ReplayPlayer`, offline `SelectionSystem`/`CommandCardSystem`); `buildings == null` / `items == null` ⇒ deterministic no-op.
- **`BuyItemCommand` guard order at exec-tick** (any failure ⇒ `OrderDenied` event, zero state change): building bounds+`Alive`; `_buildings.FactionOf[building] == expectedFaction`; `SellsItems[building]`; stock index in range; the buyer is a live hero owned by the same faction; the buyer is within `shop_radius` of the shop (`Fixed` squared-distance, the `ItemSystem` pickup-proximity form — anti-cheat, not just a UI gate); `ItemSystem.FirstFreeSlot` has room; `CanAffordOre && CanAffordCrystal`. Only then `SpendOre` + `SpendCrystal` atomically, `ItemStore.Create(defId, defCharges, heroPos)`, and write the ref into the hero's first free inventory slot **reusing the `ItemSystem` claim block** (set `Held`/`CarrierHeroSlot`, write `HeroStore.Inventory[]`, `ApplyStatModifierIfAny`).
- **Inventory persistence is deterministic, init-time-only, offline.** Add `hero.inventory` to `PersistableAttributes.Eligible` (`AttributeScope.Hero`). Extend `PlayerProfile` with an inventory field storing item-def **string ids + charges** (never the volatile packed refs); the `(key,int-raw)` `Values` shape cannot hold it, so add a new serialized list (auto-persisted by `LocalProfileSource`). Save-capture in `HeroProfileLoader.BuildProfile` (resolve `HeroStore.Inventory[]` → `ItemStore.DefId` → `ItemRegistry.Get().Id` + `Charges`) at the picker Save/Overwrite sites. Load-re-mint in `HeroProfileLoader.LoadInto` / `HeroPickerPhase.Launch` **after `Mint`, before `StartStateHash.Compute`**: `ItemRegistry.IndexOf` → `ItemStore.Create` → mark held + `CarrierHeroSlot` → write `Inventory[]`, iterating ascending hero id then ascending slot. Thread `ItemStore` + `ItemRegistry` into the loader.
- **Determinism is sacred.** No `float`/`double`/`System.Random`/wall-clock in any sim code; authored numbers (`shop_radius`, costs) are quantized to `Fixed` only at the `ContentJson.Options`/`FixedJsonConverter` load boundary, never in a tick. The shop mint + spend fold into the already-live `ItemStore`/inventory `SimChecksum` v12 and `ResourceStore` fold; re-minted persisted inventory folds into the already-live `StartStateHash` v2 inventory walk. **Existing goldens must stay byte-identical** (no new fold, no re-baseline).

**Block If:**
- Making shop purchase or persisted inventory reproducible would require a **new** `SimChecksum` or `StartStateHash` `AlgoVersion` bump beyond 3.15's v12/v2. HALT `blocked` — the story premise is that both folds are already live.
- The `hero.inventory` value cannot be expressed through the existing `PlayerProfile`/`PersistenceManifest` gate without a scripting/escape-hatch, or would require persisting a mid-game (non-init-eligible) snapshot. HALT `blocked`.

**Never:**
- No online/networked persistence rail — offline `LocalProfileSource` only (online is Epic 9).
- No folding of item **definitions** into `CanonicalModelHash` and no match-handshake wiring — that is Story 9.1.
- No new effect execution engine, no new effect leaf, no scripting escape hatch, no `float`/`System.Random`/wall-clock in sim.
- No data-driving of the building type system beyond the three shop fields — buildings stay enum-backed; Epic 4 data-drives them. Mirror `revives_heroes`, do not refactor `BuildingStore`.
- No new resource type — costs use the existing `cost_ore`/`cost_crystal` already on `ItemDefinition`; Ore/Crystal only.
- No live inventory in the authoring `UnitCardPanel` — live inventory is runtime state and renders only in the in-match selection HUD.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Author valid item | Editor: name/icon/cost/charges/deltas/effect all valid | Passes `Validated<T>` gate; Save writes `resources/data/items/<id>.json`, reloads clean, badge shows valid | No error |
| Author invalid item | Dangling/oversized effect graph, missing icon file, negative charges, out-of-range delta, or `charges==0` with an effect | Save blocked; the offending field's badge shows a located message; F5 blocked | Located field error |
| Duplicate / delete item | Duplicate an item; delete an item with confirm | Duplicate creates `<id>_copy` unique id; delete removes the JSON file after `ChimeraDialog` confirm; undo via `EditorHistory` | No error |
| Shop panel shown | A `sells_items` building selected with a hero owned by the player within `shop_radius` | Panel lists each `shop_stock` item with icon/name/cost/stock (design-system components) | No error |
| Buy, affordable + room | Player clicks Buy on an in-stock item; hero has a free slot and can afford | `BuyItem` rides `OrderApplier`; at exec-tick resources spent atomically, item minted into the hero's first free inventory slot, stat modifier applied if any; `ItemPickedUp`/purchase event | No error |
| Buy, unaffordable | Same but Ore/Crystal insufficient | Rejected, no spend, no mint, item stays purchasable; `OrderDenied` | Deterministic reject |
| Buy, inventory full | Same but all usable slots occupied | Rejected, no spend, no mint; `OrderDenied` (full-ring template) | Deterministic reject |
| Buy, not owner / not a shop / bad index / out of radius | Command targets an enemy building, a non-`sells_items` building, an out-of-range stock index, or the hero is beyond `shop_radius` | Rejected at exec-tick before any spend/mint (anti-cheat); `OrderDenied` | Deterministic reject |
| Inventory grid render | A hero is selected in-match carrying items | HUD shows a 6-slot grid: each filled slot resolves `Inventory[]`→`ItemStore`→`ItemRegistry` for icon/name/charges, with a `ChimeraTooltip`; per-slot Use and Drop affordances issue `UseItem`/`DropItem` on that exact slot (not hardcoded slot 0) | Empty slots render blank |
| Persist + display inventory | Manifest carries `hero.inventory`; player saves a hero holding items, then loads it | Save stores item-def ids + charges in the profile; the picker slot card shows the saved inventory; on Deploy the items re-mint into `ItemStore` + `Inventory[]` at init and fold into `StartStateHash` v2 | No error |
| Determinism regression guard | Any match with a shop purchase, then a second identical run | Byte-identical `SimChecksum` across runs; **all existing goldens byte-unchanged** (no algo bump) | No error |

</intent-contract>

## Code Map

**New — item authoring editor (mirror Unit Card Editor):**
- `godot/src/CreationSuite/ItemCardPanel.cs` + `ItemCardPanel.Edit.cs` -- **NEW** `partial class ItemCardPanel : Node` (`ProjectChimera.CreationSuite`), code-built (no `.tscn`), mirroring `UnitCardPanel.cs`/`UnitCardPanel.Edit.cs`: Simple/Advanced hosts, per-field `AddText`/`AddNumInt`/`AddNumFloat` + `ChimeraValidationBadge` keyed by JSON field, effect raw-JSON pane, `EditorHistory` undo, toolbar New/Duplicate/Delete via `UniqueId`+`ChimeraDialog`, F5 fail-closed gate. Persists per-item JSON (below).
- `godot/src/Core/Bootstrap/Phases/ItemCardPhase.cs` -- **NEW** mirror `UnitCardPhase.cs`: construct the panel, `AddChild`, `Initialize(...)`, publish on `SceneContext`; bind an Edit-mode hotkey (not `I`, reserved for Inventory — e.g. `K`).

**New — sim command & shop purchase:**
- `godot/src/Economy/BuildingSystem.cs` -- add `BuyItemCommand(buildingId, expectedFaction, stockIndex, heroEntityId, items, resources, events)` mirroring `ReviveHeroCommand` (:437-478): ownership + `SellsItems` capability + stock-index + hero-owner + `shop_radius` proximity + free-slot + affordability guards, then atomic `SpendOre`/`SpendCrystal` + mint into inventory. Add `ResolveSellsItems` mirroring `ResolveRevivesHeroes` (:510).
- `godot/src/Core/EntityWorld.cs` -- add `UnitCommand.BuyItem = 18` (frozen-value wire comment, ≤ `0x3F`).
- `godot/src/Multiplayer/NetworkCommand.cs` -- `OrderApplier.Apply`: new `BuyItem` dispatch block beside `ReviveHero` (:161-165), before the entity guard, delegating to `BuildingSystem.BuyItemCommand`; mirror in the `:178-185` block.

**Modify — building definition & store:**
- `godot/src/Core/Definitions/UnitDefinition.cs` -- add `[JsonPropertyName("sells_items")] bool SellsItems`, `[JsonPropertyName("shop_stock")] string[] ShopStock`, `[JsonPropertyName("shop_radius")] Fixed ShopRadius` mirroring `revives_heroes` (:242).
- `godot/src/Core/Definitions/UnitDefinitionValidator.cs` -- gate the three fields to `Structure` category (mirror `revives_heroes` :242-247); reject a `shop_stock` id absent from the `ItemRegistry`.
- `godot/src/Core/Definitions/FactionWriter.cs` -- round-trip the three fields (mirror `PutBool(obj,"revives_heroes",…)` :239).
- `godot/src/Core/BuildingStore.cs` -- add `bool[] SellsItems` (+ per-building stock ids / `Fixed[] ShopRadius`) mirroring `RevivesHeroes` (declare/alloc/`Create`/`Clear`); write in `Create`.

**Modify — item validator (editor-facing keyed errors + icon check):**
- `godot/src/Core/Definitions/ItemDefinitionValidator.cs` -- collect keyed `(FieldPath, Message)` errors alongside the existing first-fail `Error`; add missing-icon-file rejection. Keep the `Validated<ItemDefinition>` sim-gate mint intact.
- `godot/src/Core/Definitions/ItemValidationResult.cs` -- add an `Errors` keyed list (editor) beside `Error` (sim); `Ok` unchanged.

**Modify — inventory UI (in-match HUD):**
- `godot/src/UI/CommandCardSystem.cs` -- add a hero inventory region shown on hero selection: a 6-slot grid reading `HeroStore.Inventory[heroSlot*6 + s]`→`ItemStore.TryResolveRef`→`DefId`→`ItemRegistry.Get` for icon/name/charges, `ChimeraTooltip`, and per-slot Use/Drop buttons issuing `UseItem`/`DropItem` on the selected slot. Add a shop panel branch on `_buildings.SellsItems[bId]` mirroring the revive-button branch (:336-345) with per-item Buy buttons calling a new `IssueBuyCommand` (mirror `IssueTrainCommand` :411-421).
- `godot/src/UI/SelectionSystem.cs` -- per-slot use now targets the grid-selected slot (retire the slot-0 hotcode); when a mixed selection right-clicks a ground item, still route non-hero units through the normal move/attack-move path (closes the 3.15 "army stops near item" defer).
- `godot/src/UI/EntityPlacer.cs` -- increment `_itemIndex` on Item-mode re-selection so the palette cycles all registry items (closes the 3.15 `_itemIndex`-never-incremented defer).

**Modify — inventory persistence rail:**
- `godot/src/Core/Definitions/PersistableAttributes.cs` -- add `hero.inventory` to `Eligible` (`AttributeScope.Hero`) with a `Tip`.
- `godot/src/Core/Definitions/PlayerProfile.cs` -- add an inventory list field (item-def string id + charges); include in `Clone()`.
- `godot/src/Core/Definitions/HeroProfileLoader.cs` -- widen `BuildProfile` to capture inventory (resolve refs→def-id+charges when the shape carries `hero.inventory`); widen `LoadInto` to re-mint saved inventory after `Mint` (thread `ItemStore`+`ItemRegistry`).
- `godot/src/Core/Bootstrap/Phases/HeroPickerPhase.cs` -- pass `ItemStore`+`ItemRegistry` to the loader; ensure re-mint runs before `StartStateHash.Compute` (:52-58).
- `godot/src/UI/HeroPickerOverlay.cs` -- render the saved inventory on each slot card (`BuildCard` :267, between XP :301 and faction tag :304); pass live inventory to `BuildProfile` at the Save/Overwrite sites (:389/:413).

**Tests (Tier-1 sim unless noted):**
- `godot/ProjectChimera.Sim.Tests/Economy/BuyItemCommandTests.cs` -- **NEW** every buy guard: happy-path mint+spend, unaffordable, full-inventory, enemy building, non-shop building, out-of-range index, out-of-radius, unknown stock id at validation; assert atomic no-spend-on-reject.
- `godot/ProjectChimera.Sim.Tests/Definitions/ItemDefinitionValidatorTests.cs` -- extend: keyed `(FieldPath,Message)` errors, missing-icon-file rejection; existing rules stay green.
- `godot/ProjectChimera.Sim.Tests/Definitions/` -- **NEW/extend** `sells_items`/`shop_stock`/`shop_radius` round-trip through `FactionWriter`; Structure-gating + dangling-stock-id rejection in `UnitDefinitionValidator`.
- `godot/ProjectChimera.Sim.Tests/Persistence/HeroInventoryPersistenceTests.cs` -- **NEW** `BuildProfile` captures inventory as def-ids+charges; `LoadInto` re-mints byte-faithfully (ref→def-id→ref); `StartStateHash` includes the re-minted loadout and is stable across two runs.
- `godot/ProjectChimera.Sim.Tests/Golden/` -- **NEW** shop-purchase golden proving the buy mint is byte-identical across two runs; **assert all existing SimChecksum/StartStateHash goldens are byte-unchanged** (no algo bump).
- `godot/ProjectChimera.Sim.Tests/Multiplayer/CommandApplyParityTests.cs` -- extend: `BuyItem` applies identically through the live and replay paths.

## Tasks & Acceptance

**Execution:**
- `EntityWorld.cs` / `NetworkCommand.cs` -- `BuyItem=18`; dispatch through `OrderApplier.Apply` before the entity guard (Train/Revive pattern), threaded to all three apply sites; `buildings==null`/`items==null` no-op.
- `UnitDefinition.cs` / `UnitDefinitionValidator.cs` / `FactionWriter.cs` / `BuildingStore.cs` -- `sells_items`/`shop_stock`/`shop_radius` fields (mirror `revives_heroes`), Structure-gated + dangling-stock-id reject, round-tripped, resolved into `BuildingStore.SellsItems[]`.
- `BuildingSystem.cs` -- `BuyItemCommand` (full guard order → atomic spend → mint into first free slot reusing the `ItemSystem` claim block) + `ResolveSellsItems`.
- `ItemDefinitionValidator.cs` / `ItemValidationResult.cs` -- keyed field errors for the editor + missing-icon-file reject; sim-gate mint intact.
- `ItemCardPanel.cs` / `ItemCardPanel.Edit.cs` / `ItemCardPhase.cs` -- item card editor mirroring the Unit Card Editor; per-item JSON persistence (atomic `.tmp` + reload self-check via `ItemLoader`); New/Duplicate/Delete; F5 fail-closed gate.
- `CommandCardSystem.cs` -- in-match hero inventory grid (per-slot Use/Drop, tooltips, charges) + shop panel (per-item Buy) gated on `SellsItems`.
- `SelectionSystem.cs` / `EntityPlacer.cs` -- per-slot use, mixed-selection pickup fix, `_itemIndex` cycling (3.15 defer closures).
- `PersistableAttributes.cs` / `PlayerProfile.cs` / `HeroProfileLoader.cs` / `HeroPickerPhase.cs` / `HeroPickerOverlay.cs` -- `hero.inventory` attribute; profile inventory field; save-capture + init-time re-mint (before `StartStateHash.Compute`); picker card display.
- Tests -- `BuyItemCommandTests` (whole matrix), validator keyed-errors + icon + shop-field round-trip/gating, `HeroInventoryPersistenceTests`, shop-purchase golden + existing-goldens-unchanged assertion, `CommandApplyParityTests` `BuyItem`.

**Acceptance Criteria:**
- Given the item card editor, when I create/edit/duplicate/delete an item, then it validates through the same fail-closed `Validated<T>` gate (dangling effect graphs and missing icon files rejected with located, field-anchored messages) and persists to its own `resources/data/items/<id>.json` that reloads clean; an invalid item blocks Save and F5.
- Given a `sells_items` building with an authored stock, when a player's hero is selected within `shop_radius`, then the shop panel lists items with cost/stock and Buy rides the `OrderApplier` command path; at exec-tick the purchase is guarded (building & hero ownership, capability, stock index, proximity, free slot, affordability), spends resources atomically, and lands the item in the hero's first free inventory slot — every guard failure rejects with `OrderDenied` and zero state change.
- Given a hero is selected in-match, when the HUD renders, then a 6-slot inventory grid shows carried items with icons, charges, and tooltips (design-system components) and per-slot Use/Drop affordances issue the correct sim command on the selected slot (not hardcoded slot 0).
- Given a persistence manifest carrying `hero.inventory`, when a hero is saved and reloaded, then the profile stores item-def ids + charges (not volatile refs), the 3.9 hero-picker slot card displays the saved inventory, and on Deploy the items re-mint into `ItemStore`/`Inventory[]` at init-time and fold into `StartStateHash` v2.
- Given the only sim addition is the `BuyItem` mint/spend over already-folded stores, when the suite runs, then a shop-purchase golden is byte-identical across two runs, **all pre-existing SimChecksum and StartStateHash goldens remain byte-identical** (no `AlgoVersion` bump), and `BuyItem` applies identically through the live and replay paths.

## Design Notes

**D1 — Live inventory renders in the in-match HUD, not the authoring editor.** The epic note "the unit panel renders" + "read-only panel (3.3)" refers to the design-system *pattern*, but Story 3.3's panel is the authoring `UnitCardPanel` (keyed off `FactionDef`, static definitions). A hero's *carried items with live charges* only exist at runtime, so the grid attaches to `CommandCardSystem` (the bottom HUD shown on selection), composed from the `ChimeraComponents` kit. This is the only defensible reading — an editor rendering static `UnitDefinition`s cannot show live per-slot charges.

**D2 — Two validation surfaces, one gate.** The sim mints `Validated<ItemDefinition>` from a single first-fail `Error` (unchanged, keeps the `ScenarioValidator.Proof` sole-minter contract). The editor needs per-field badges, so the validator additionally emits keyed `(FieldPath, Message)` tuples (the `key` already exists inside each `Fail(id, key, …)`), mirroring `UnitDefinitionValidator.UnitValidationResult`. Same rules, richer surface.

**D3 — Shop purchase is the Revive command with an item payload.** `BuyItem` names the *building* (`UnitId`=buildingId), carries the stock index (`TargetX` raw int) and buyer hero (`TargetZ` raw int), dispatches before the entity guard, and never persists a `CommandState` — identical structure to `ReviveHeroCommand`. Spend + mint happen atomically at exec-tick; the free-slot and affordability checks run *before* any spend so a rejected buy is a pure no-op. Proximity is enforced in-sim (not just the UI panel gate) to close the anti-cheat surface the 3.15 review cared about.

**D4 — Persistence stores def-ids, not refs; no algo bump.** `HeroStore.Inventory[]` holds volatile packed `ItemStore` refs, so the profile stores item-def *string ids* + charges (new `PlayerProfile` list — the `(key,int-raw)` `Values` shape cannot express it). Re-mint runs after `Mint` (which zeroes inventory) and before `StartStateHash.Compute`, which **already folds** `Inventory[]` at `AlgoVersion` 2 (pre-wired by 3.15 "for a future hero loadout") — so a saved loadout becomes handshake-rejectable with **no** hash bump. `SimChecksum` v12 already folds the runtime `ItemStore`/inventory, so a shop mint needs no bump either. Existing goldens must stay byte-identical; a change signals an unintended fold.

## Verification

**Commands:**
- `dotnet build godot/godot.sln` -- expected: clean build, no determinism-analyzer (CHM*)/banned-float violations in new sim code.
- `dotnet test godot/ProjectChimera.Sim.Tests` -- expected: all green (allow only the pre-existing unrelated `ProceduralMapGeneratorTests` WSL/Windows cross-platform tripwire, documented failing at baseline in the 3.12–3.15 runs), including `BuyItemCommandTests`, the extended `ItemDefinitionValidatorTests`, the shop-field round-trip/gating tests, `HeroInventoryPersistenceTests`, the new shop-purchase golden, and `CommandApplyParityTests`.
- `CHIMERA_GOLDEN_RECORD=1 dotnet test godot/ProjectChimera.Sim.Tests --filter FullyQualifiedName~Golden` then a clean `dotnet test` -- expected: **only** the new shop-purchase golden records; every pre-existing SimChecksum/StartStateHash golden is byte-unchanged (no algo bump), stable across two consecutive normal runs.

**Manual checks (`/godot-verify`):**
- In the creation suite, open the item editor: create a valid item (Save writes its JSON, reloads clean), then force an invalid effect graph / missing icon and confirm the located field badge + F5 block.
- Author a building with `sells_items` + a stock list; in a playtest select a hero near it → the shop panel lists items; buy one (resources drop, item appears in the inventory grid); over-buy past a full inventory or with no resources → denial. Select the hero → the inventory grid shows carried items with charges/tooltips; Use and Drop from a chosen slot behave per-slot.
- With a persistence manifest carrying `hero.inventory`, save a hero holding items, relaunch, and confirm the hero-picker slot card shows the saved inventory and Deploy restores it.

## Spec Change Log

_Empty — no bad_spec loopback occurred; every review finding was patch, defer, or reject._

## Review Triage Log

### 2026-07-08 — Review pass
- intent_gap: 0
- bad_spec: 0
- patch: 9: (high 2, medium 3, low 4)
- defer: 1: (high 0, medium 0, low 1)
- reject: 1: (high 0, medium 0, low 1)
- addressed_findings:
  - `[high]` `[patch]` **Negative shop cost = infinite resources (Edge Case #1).** The sim `ItemDefinitionValidator.Validate` gate (the sole `Validated<T>` minter) checked `charges`/deltas but not cost sign, while only the editor `ValidateFields` rejected negative cost. Since `BuyItemCommand` now *spends* `CostOre`/`CostCrystal`, `SpendOre(faction, -100)` added 100 ore per click and minted the item free. Added a `[0, Range]` cost gate to the sim `Validate` (mirroring `ValidateFields`) + sim-gate oracles (`NegativeCostOre/Crystal_FailsClosed_SimGate`).
  - `[high]` `[patch]` **Persisted stat item was inert on deploy (Blind Hunter #1 / Edge Case #2 / Intent-Alignment).** `HeroProfileLoader.ReMintInventory` minted a saved carried item + wrote `Inventory[]`/`Held` but never applied its stat modifier (and `LoadInto` had no `ModifierStore`), so a reloaded `+50 max_health` ring rendered in the grid but `EffectiveMaxHealth` stayed at base — violating 3.15's carried-item invariant that every other mint path (pickup/buy) honors. Extracted a shared `ItemSystem.ApplyItemStatModifier`, threaded `ModifierStore` + `usableSlots` through `LoadInto`/`ReMintInventory` (all 4 call sites), and now reapply each carried item's modifier deterministically at init (ascending slot, before `StartStateHash.Compute`, same `ItemModifierId(ref)`). Determinism-safe: no golden moved (scenarios without persisted inventory apply no modifier), no algo bump. Test asserts `EffectiveMaxHealth == 150` after re-mint (the buy path's assertion).
  - `[medium]` `[patch]` **Overwrite wiped a different hero's saved inventory (Edge Case #3).** `HeroPickerOverlay.OnOverwritePressed` passed the provider's inventory, which is `null` when overwriting a hero you did not just play; level/xp fell back to the target's values but inventory did not → an empty list replaced the target's saved loadout. Added `?? target.Inventory` fallback, mirroring the level/xp fallback.
  - `[medium]` `[patch]` **Preserve-progress return-to-edit dropped the inventory (Blind Hunter #2).** The `snapProfile` branch in `MainScene` built a profile with only level/xp and no `Inventory`, so return-to-Edit-with-preserve lost the loadout while the ordinary deploy path re-minted it. Populated `snapProfile.Inventory` from the live harvested loadout (`_ctx.HarvestedHeroInventory ?? PendingHeroProfile.Inventory`).
  - `[medium]` `[patch]` **Round-trip not slot-faithful + ignored `UsableSlots` (Blind Hunter #3 / Edge Case #5).** `CaptureInventory` skipped empty slots and `ReMintInventory` re-packed contiguously from slot 0 (items in slots 0,2 came back in 0,1), and both walked all 6 physical slots ignoring the configured usable cap (over-capacity heroes). Added a persisted `Slot` index (legacy `-1` → contiguous fallback), restored items to their exact slots, and rejected slots beyond the threaded `usableSlots` cap. Tests: exact-slot restore + beyond-cap rejection.
  - `[low]` `[patch]` **Unvalidated charges on re-mint (Blind Hunter #4).** A corrupt/hand-edited profile's `charges` was passed straight to `ItemStore.Create`; clamped to `[0, def.Charges]` on re-mint (+test: -5→0, 99→3).
  - `[low]` `[patch]` **`ItemWriter` had no round-trip test (Verification-Gap #1).** The new Godot-free item serializer's output must round-trip through `ItemLoader`/`ContentJson.Options`; a converter drift (e.g. dropping `EffectNodeJsonConverter`) would ship green yet make consumables/priced items un-reloadable. Added `ItemWriterRoundTripTests` pinning a stat item + a charged consumable-with-effect through `ItemLoader.Load(ItemWriter.Serialize(def))`.
  - `[low]` `[patch]` **Selected inventory slot not reset across selection change (Blind Hunter #5).** `_selectedInventorySlot` persisted when switching heroes (T-use mis-targeted the new hero's slot); reset it in the `ClearSelection` chokepoint.
  - `[low]` `[patch]` **Stat-item Use buttons flooded no-op commands (Blind Hunter #6).** Every carried stat item's Use button enqueued a real lockstep order the sim discards; enabled Use only for consumables (Drop stays enabled for all).
  - Deferred (1): stale `UnitCommand.UseItem/DropItem` doc comments in `EntityWorld.cs` say "BEFORE the entity guard" while `NetworkCommand.cs` correctly dispatches them after the ownership guard — pre-existing 3.15 residue (a latent anti-cheat trap), not touched by this diff → `deferred-work.md`.
  - Rejected (1, low): AC1 "persists into scenario/faction data" — items persist to the per-item `resources/data/items/<id>.json` store established by Story 3.15, not embedded in a faction JSON document. The Intent-Alignment layer confirmed this is the stronger, established reading (items were never in faction data); the divergence is nominal, not a defect.

### 2026-07-08 — Follow-up review pass
- intent_gap: 0
- bad_spec: 0
- patch: 3: (high 0, medium 1, low 2)
- defer: 4: (high 0, medium 0, low 4)
- reject: 16: (high 0, medium 0, low 16)
- addressed_findings:
  - `[medium]` `[patch]` **Crystal-cost buy path was entirely untested (Verification-Gap #1).** Every existing shop/BuyItem test bought an ore-only item, so `BuyItemCommand`'s crystal half — the `CanAffordCrystal` pre-check, `SpendCrystal`, and the store-full `AddCrystal` refund — and its "check BOTH before debiting EITHER" atomicity contract only ever ran at `Fixed.Zero`. A regression (e.g. moving `SpendOre` above the crystal check) would drain ore with no item on any crystal-priced item, undetected by the whole suite including the golden and both parity tests. Added a crystal+ore-priced `amulet` to the test registry and two cases: `Buy_CrystalPricedItem_Affordable_SpendsBothAndMints` (both debited, item minted) and `Buy_CrystalShortButOreRich_RejectsWithoutSpendingOre` (ore untouched though affordable — pins the atomicity contract).
  - `[low]` `[patch]` **Under-construction shop buy untested (Verification-Gap #2).** Every shop fixture forced `ConstructionTimer = 0` before buying, so the `IsUnderConstruction` reject guard (the "shop must be built before it sells" rule) was unpinned. Added `Buy_UnderConstructionShop_RejectsWithNoSpend` (`ConstructionTimer = 5` → reject, no spend, no mint).
  - `[low]` `[patch]` **Editor spinner ranges exceeded the fail-closed validator caps (Blind Hunter #2).** The stat-delta spinners allowed ±2000 while `ItemDefinitionValidator` caps deltas at `MAX_ITEM_STAT_DELTA = ±1000`, and the cost spinners allowed 0–99999 while `CheckCost` rejects `> 32767` — so a creator could dial in a value the `Validated<T>` gate would always reject, blocking Save with no in-range escape. Clamped the spinner bounds to `ItemDefinitionValidator.MAX_ITEM_STAT_DELTA` / `short.MaxValue` (new `DeltaCap`/`CostCap` constants) so the control can only offer saveable values.

## Auto Run Result

Status: done
Blocking condition: none

### Summary

Implemented Story 3.16 end-to-end across three cohesive item-domain deliverables, each reusing an established pattern. (1) **Item authoring editor** (`ItemCardPanel` + `.Edit.cs`, `ItemCardPhase`) mirroring the Story 3.4 Unit Card Editor — Simple/Advanced hosts, per-field `ChimeraValidationBadge` (via a new keyed `ItemDefinitionValidator.ValidateFields` + missing-icon check), raw-JSON pane, `EditorHistory` undo, F5 fail-closed gate — persisting each item to its own `resources/data/items/<id>.json` via a new Godot-free `ItemWriter` (atomic `.tmp` + reload self-check through `ItemLoader`). (2) **Shop buildings** — `sells_items`/`shop_stock`/`shop_radius` on the building definition (mirroring `revives_heroes`: Structure-gated validation, dangling-stock-id reject, `FactionWriter` round-trip, resolved into `BuildingStore.SellsItems[]`) and a new `UnitCommand.BuyItem = 18` riding the shared `OrderApplier` (Train/Revive pattern) through all three apply sites, delegating to `BuildingSystem.BuyItemCommand` (ownership → `SellsItems` → stock index → owned-hero → `shop_radius` proximity → free-slot → affordability guards, then atomic spend + mint reusing the `ItemSystem` claim block, with refund on store-full). (3) **Inventory UI** — a 6-slot in-match inventory grid on `CommandCardSystem` (per-slot Use/Drop, charges, tooltips) + shop Buy panel, plus making the `hero.inventory` persistence attribute real (`PersistableAttributes` + `PlayerProfile` inventory list + `HeroProfileLoader` save-capture/init-time re-mint + hero-picker slot-card display). No `SimChecksum`/`StartStateHash` `AlgoVersion` bump — the shop mint/spend and re-minted inventory fold into the already-live v12/v2 folds; all pre-existing goldens are byte-identical.

### Files changed

Sim/command: `EntityWorld.cs` (`BuyItem=18`), `NetworkCommand.cs` (`OrderApplier` BuyItem dispatch), `Economy/BuildingSystem.cs` (`BuyItemCommand`+`ResolveSellsItems`), `Combat/ItemSystem.cs` (public buy surface + shared `ApplyItemStatModifier`), `Core/BuildingStore.cs` (`SellsItems`/`ShopStock`/`ShopRadius`), `Definitions/UnitDefinition.cs` (+`UnitDefinitionValidator.cs`, `FactionWriter.cs`) shop fields, `Definitions/ItemDefinitionValidator.cs` (`ValidateFields` keyed errors + missing-icon + sim-gate cost sign), `Definitions/ItemValidationResult.cs`, `Definitions/ItemWriter.cs` (NEW).
Persistence: `Definitions/PersistableAttributes.cs` (`hero.inventory`), `PlayerProfile.cs` (`Inventory`+`Slot`), `HeroProfileLoader.cs` (capture/re-mint + modifier reapply + slot-faithful + clamp), `Bootstrap/Phases/HeroPickerPhase.cs`, `Bootstrap/Phases/SceneContext.cs`, `MainScene.cs` (LoadInto sites + harvest + item-editor phase/hotkey), `UI/HeroPickerOverlay.cs` (slot-card inventory + overwrite fallback).
UI/editor: `UI/CommandCardSystem.cs` (inventory grid + shop panel), `UI/SelectionSystem.cs` (per-slot use, mixed-selection pickup fix, slot reset), `UI/EntityPlacer.cs` (item palette cycling), `CreationSuite/ItemCardPanel.cs`+`.Edit.cs` (NEW), `Bootstrap/Phases/ItemCardPhase.cs` (NEW), `Bootstrap/{ScenePhaseOrder,CameraPhase}.cs`.
Tests: `Economy/BuyItemCommandTests`, `Definitions/ItemDefinitionValidatorFieldsTests` (+sim-gate cost oracles), `Definitions/ShopFieldRoundTripTests`, `Definitions/ItemWriterRoundTripTests`, `Persistence/HeroInventoryPersistenceTests` (capture/re-mint/modifier-reapply/slot-faithful/cap/charges), `Golden/ShopPurchaseScenario`+`ShopPurchaseGoldenTests`+`shop-purchase-scenario.golden.txt`, `Multiplayer/CommandApplyParityTests` (+BuyItem), updated `PhaseOrderTest` + `PersistenceManifestTests`.

### Review findings

Four parallel Opus review layers (Blind Hunter, Edge Case Hunter, Verification-Gap, Intent-Alignment). Triage: **9 patches (high 2, medium 3, low 4), 1 defer, 1 reject, 0 intent_gap, 0 bad_spec** — see Review Triage Log. The two high patches: a shop-economy exploit (sim validator admitted negative-cost items that `BuyItemCommand` spent into infinite resources) and a persisted stat item being inert on deploy (re-mint minted the item but never reapplied its modifier, violating 3.15's carried-item invariant). Three medium data-integrity patches fixed inventory loss on Overwrite and preserve-progress-return-to-edit, and made the round-trip slot-faithful + `UsableSlots`-aware.

### Verification

- `dotnet build godot/godot.sln` — clean, 0 errors, 0 warnings.
- `dotnet test godot/ProjectChimera.Sim.Tests` — **1096 passed, 1 skipped, 1 failed**. The sole failure is the pre-existing unrelated `ProceduralMapGeneratorTests` WSL/Windows cross-platform tripwire (Expected 3026392010 / Actual 413099587), documented failing at baseline in the 3.12–3.15 runs and untouched by this diff. All new BuyItem/validator/persistence/round-trip/golden/parity tests green, including the two high-patch oracles (`NegativeCost*_FailsClosed_SimGate`, `LoadInto_ReMintedStatItem_AppliesCarriedModifier`).
- Golden discipline: `git status` on `Golden/`+`Validation/` shows only new untracked files — **no pre-existing golden `.txt` modified**, confirming no `AlgoVersion` bump (SimChecksum stays v12, StartStateHash v2). The new `shop-purchase-scenario.golden.txt` is byte-identical across two runs.
- Matrix Test Audit: every sim-observable I/O matrix row is covered by a passing, executed test (buy guards, validator, persistence capture/re-mint, determinism golden). The purely-presentational rows (editor form, shop panel, inventory grid render, duplicate/delete) are Godot `Control` surfaces verified by clean compile + the prescribed `/godot-verify` manual checks, consistent with prior Epic-3 UI stories.

### Follow-up review recommendation

`followup_review_recommended: true`. The final pass applied two high-severity cross-cutting fixes — a shop-economy anti-cheat/exploit closure in the fail-closed content gate, and a determinism-init-path change (threading `ModifierStore` + reapplying carried-item modifiers at init across four `LoadInto` call sites) — plus three medium data-integrity fixes to the persistence round-trip. The breadth (security + determinism-init + persistence data-loss) and the signature changes across the init path warrant an independent confirmatory pass.

### Residual risks

- All presentation surfaces (item editor, in-match inventory grid, shop panel, hero-picker inventory tag, `EntityPlacer` item palette) are verified by clean compile + pattern-consistency, not a live in-engine `/godot-verify` session (headless environment). The determinism-critical spine (BuyItem guards/spend/mint, replay-vs-live parity, persistence capture/re-mint + init-time modifier reapply, StartStateHash fold, shop-purchase golden) is fully Tier-1 covered.
- One deferred item (stale `UseItem/DropItem` enum doc comment, a pre-existing 3.15 residue / latent anti-cheat trap) → `deferred-work.md`.
- The pre-existing `ProceduralMapGeneratorTests` WSL platform tripwire remains (unrelated to Epic 3).

### Follow-up review pass (2026-07-08)

A second, independent four-layer review pass (Blind Hunter, Edge Case Hunter, Verification-Gap, Intent-Alignment; all Opus-4.8) over the same `9ceacdb..HEAD` diff, prompted by the prior pass's `followup_review_recommended: true`. **No intent_gap, no bad_spec.** The determinism-critical spine held up under re-scrutiny: correct `BuyItemCommand` guard order and silent-vs-`OrderDenied` split, both-affordability-before-either-debit atomicity, cross-platform long-widened proximity math, no `SimChecksum`/`StartStateHash` AlgoVersion bump (v12/v2 preserved), re-mint before `StartStateHash.Compute`, and def-id+charges+slot persistence (never packed refs) — all confirmed.

**3 patches applied and verified** (see the follow-up triage-log entry): (1) `[medium]` a real coverage hole — the crystal half of the buy spend and its atomicity contract were never exercised (all shop tests bought ore-only items); added a crystal+ore-priced item and two cases that pin "both-affordable-before-either-debit"; (2) `[low]` added the missing under-construction-shop reject test; (3) `[low]` clamped the item-editor stat/cost spinner ranges to the fail-closed validator caps so the control can't offer a value Save will reject.

**4 findings deferred** (new `deferred-work.md` entries, this pass): item-editor `Id` field path-traversal (mirrors the pre-existing `UnitCardPanel` convention; local authoring tool); STJ ignores the `ProfileInventoryItem.Slot = -1` ctor default so a slot-less legacy profile deserializes to slot 0 (defensive-only — inventory persistence is new in 3.16, no such data exists); shop Buy button lit against a full nearest hero when a farther in-range hero has room (sim correctly denies; buyer-selection UX); and item icon textures not rendered in the inventory grid / shop buttons / hero-picker card (identified by name+charges+tooltip; the "with icons" surface needs a live `/godot-verify` pass). The remaining ~16 findings were rejected as noise, defensible pattern choices (raw-Godot HUD matching the host file's train/revive buttons; auto-nearest-hero shop trigger; event reuse; Core-helper cohesion), or very-low near-impossible paths.

**Verification:** `dotnet build godot/godot.sln` clean (0 errors; 5 pre-existing warnings, none from this pass). `dotnet test godot/ProjectChimera.Sim.Tests` — **1099 passed, 1 skipped, 1 failed**, the sole failure the documented pre-existing `ProceduralMapGeneratorTests` WSL tripwire (Expected 3026392010 / Actual 413099587); the three new tests take passing from 1096 → 1099. No tracked golden `.txt` modified (golden discipline intact — no AlgoVersion bump). The spinner-range change is editor-UI-only and touches no sim/determinism path.

**Follow-up recommendation:** `false`. This pass's changes are two test-only additions plus one editor-UX constant clamp — no production sim/behavior/API/data change, all low-risk and independently verified — so no further independent review is warranted.
