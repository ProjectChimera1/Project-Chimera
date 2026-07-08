---
title: 'Item & inventory sim — pickups, slots, stat effects, charges'
type: 'feature'
created: '2026-07-08'
status: 'done'
review_loop_iteration: 0
followup_review_recommended: false
baseline_revision: '04022cc12eda5f72f68a44d083fc4d74230620e2'
final_revision: 'ffd0dff97e8eb961aa8b363fcecc24447a0e4dfe'
context:
  - '{project-root}/_bmad-output/implementation-artifacts/epic-3-context.md'
  - '{project-root}/_bmad-output/implementation-artifacts/spec-3-14-hero-death-revival.md'
  - '{project-root}/_bmad-output/implementation-artifacts/spec-3-13-heroxpsystem-kill-credit-xp-leveling-stat-growth-runtime.md'
warnings: ['oversized']
---

<intent-contract>

## Intent

**Problem:** Heroes level and revive (3.13/3.14) but cannot carry anything — there is no item or inventory system, though the persistence manifest already promises "inventory/items" (FR-64/FR-7a) and Story 3.14 left a binding obligation that any hero inventory must survive death/revival rather than being silently dropped. There is no `ItemStore`, no per-hero inventory, no way to place items on the map, pick them up, gain their stat bonuses, or fire charged consumables.

**Approach:** Add a new recycle-guarded `ItemStore` SoA of item instances (on-ground or held) plus a fixed-stride per-hero inventory that lives on the persisted `HeroStore` row (so it survives revival by construction — the 3.14 obligation). Author items as content-gated `ItemDefinition` data through the SAME `Validated<T>` gate as abilities. A hero ordered onto a ground item resolves the pickup deterministically (move-to + ascending-id proximity claim) into the first free slot, or is denied when full; a carried stat item applies its authored modifier deltas via the existing `ModifierSystem` (removed on drop); a charged consumable fires its authored Effect-Graph through the SAME `EffectExecutor` abilities use (no new execution engine), decrements a charge, and deletes at zero. On hero death, carried items drop to the ground (WC3-style) at the death position. The mutable `ItemStore` + inventory fold into `SimChecksum` in one `AlgoVersion` 11→12 bump (goldens re-baselined explicitly), and the initial placed-item + inventory state folds into `StartStateHash` (1→2) so peers with mismatched item loadouts are rejected at the handshake.

## Boundaries & Constraints

**Always:**
- Sim-layer determinism is sacred: `Fixed` (16.16) only, **no `float`/`double`/`Mathf`/`Math.*`/`System.Random`/wall-clock** in any new sim code. Authored item numbers (modifier deltas, charges, costs) are quantized to `Fixed` at the single load boundary (`FixedJsonConverter` via `ContentJson.Options`), never inside a tick.
- **Inventory lives on the persisted `HeroStore` row**, never on the recycled `EntityWorld` entity — it survives death→revival by construction (discharges the Story 3.14 Intent-Alignment binding obligation in `deferred-work.md`).
- Iterate/claim in **ascending id order** with strict-`<` tie-break (the `CombatSystem.cs:575` convention): when two heroes reach the same item the same tick, the lower-id claimant wins and the other's pickup order voids. `ItemStore` folds by ascending stable ref.
- **Consumables execute through the shared `EffectExecutor.Run(root, ctx)`** with an `EffectContext` built exactly as `AbilityCastSystem` does (`AbilityCastSystem.cs:211-214`) — RNG taken from `world.Rng`, never a second generator. **No new execution engine.**
- **Item definitions pass the same `Validated<T>` content gate** as abilities: deserialize through `ContentJson.Options`, validate fail-closed in a new `ItemDefinitionValidator` (mirror `AbilityValidator`), load via an `ItemLoader`/`ItemRegistry` (mirror `AbilityLoader`/`AbilityRegistry`). Dangling effect graphs, negative charges, and out-of-range/non-finite modifier deltas fail closed with a located, actionable error that blocks load.
- **SoA recycle contract:** every new `ItemStore`/inventory field is written on `Create`/`Mint` and reset on recycle + `Clear()` — a recycled item slot must never carry the prior occupant's `DefId`/`Charges`; a re-minted hero row must never carry a prior inventory (mirror the A2 rule and the `HeroStore.Mint`/`Clear` contract).
- **One `SimChecksum` fold, one bump:** the mutable `ItemStore` + per-hero inventory fold into `SimChecksum` under a single `AlgoVersion` **11→12** bump; re-baseline all 20 SimChecksum goldens explicitly, re-pin the coverage-guard known-state hash (`ExpectedV11Hash`→`ExpectedV12Hash`) with new item-store teeth, and update `ExpectedSimChecksumAlgoVersion` (11→12).
- **Initial item state folds into `StartStateHash`** (`AlgoVersion` **1→2**): per-hero inventory (in the hero-row loop) + placed map-items (`model.Items`, sorted by a total order like the `CanonicalModelHash` placement walks). Re-record `hero-start-state.golden.txt` + the independent-FNV pin, and update `ExpectedStartStateHashAlgoVersion` (1→2). `CanonicalModelHash` stays v3 (seed unchanged).
- Pickup/use/drop ride the shared `OrderApplier.Apply` so **all three apply sites** (live `LockstepManager`, `ReplayPlayer`, offline `CommandCardSystem`) get identical behavior; `items == null` is a deterministic no-op (replay/golden safety), exactly like `buildings == null`.
- Presentation/sim separation holds: the `EntityPlacer` item palette mode and any command-card/hotkey affordance live in the UI layer; the sim (`ItemStore`, `ItemSystem`, definitions) stays Godot-free and Tier-1 testable.

**Block If:**
- Per-hero inventory cannot be expressed on the `HeroStore` row and would force inventory onto the entity (losing items on every revival, contradicting the 3.14 obligation). HALT `blocked`.
- A consumable behavior cannot be expressed in the existing Story 2.1 `EffectNode` vocabulary and would need a new leaf/execution engine, OR it requires the reserved-but-unimplemented random effect leaf (`SimRng` random-selection enforcement). HALT `blocked` — do not add a new engine or an unvalidated random leaf.
- Folding item state would require a SECOND `SimChecksum` `AlgoVersion` bump beyond 11→12. HALT `blocked` (the story premise is one fold).

**Never:**
- No item authoring editor panel, no `sells_items` shop buildings, no full inventory-grid UI with tooltips/use-hotkeys — that is **Story 3.16**. This story ships the sim + content model + a minimal `EntityPlacer` placement mode and the sim commands to exercise it.
- No cross-match inventory **persistence** through `PlayerProfile`/`PersistenceManifest` ("inventory" manifest attribute) — that is **Story 3.16**; hero inventory starts empty each match in 3.15.
- No `CanonicalModelHash` bump and no folding of item **definitions** into the canonical/pre-match content-model hash — that is **Story 9.1** ("consumes 3.15 content models as they land").
- No new effect execution engine, no scripting escape hatch, no `float`/`System.Random`/wall-clock in sim.
- No third resource; item purchase cost fields may exist on the definition for 3.16 but are not spent anywhere in 3.15.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Pickup, room available | Hero ordered onto a ground item; a free inventory slot exists within the configured count | Hero moves to the item; on proximity it claims: the `ItemStore` instance transitions ground→held into the first free slot on the hero row; a stat item applies its modifier to the carrier; `ItemPickedUp` event | No error |
| Pickup, inventory full | Same, but all `inventory_slot_count` slots occupied | Order rejected, item stays on ground, hero does not consume it; `OrderDenied` event (the full-ring reject template) | Deterministic reject, no state change |
| Two heroes race one item | Both reach proximity the same tick | Lower-id claimant takes it (strict-`<` tie-break); the other hero's pickup order voids (target gone) with no crash | No error |
| Stat item carried → dropped | Carried stat item; hero drops it (manual `DropItem` or death) | On carry: modifier applied via `ModifierStore`; on drop: modifier removed via targeted remove-by-id and the item respawns on the ground at the carrier/death position | No error |
| Consumable used, charges > 1 | Hero uses a charged consumable from a slot | Effect graph runs through the shared `EffectExecutor` (self/graph-targeted); charges decrement by 1; item remains in the slot | No error |
| Consumable used, last charge | Same, charges == 1 | Effect runs; item deleted (freed from `ItemStore`, slot cleared, any modifier removed) | No error |
| Use on empty/invalid slot | `UseItem` on a slot with no item, or a non-consumable (0 charges/no effect) | No-op, no charge change, no crash | Deterministic no-op |
| Hero dies with items | Live hero carrying items is killed | Each carried item drops to the ground at the death position (`KillEntity`, pre-`Destroy`); inventory slots cleared; modifiers removed; revived hero returns empty and can re-collect | No error |
| Configured slot cap < stride | `inventory_slot_count = 3` (stride ceiling 6) | The 4th pickup is denied though physical stride has room | Deterministic reject |
| Invalid item authoring | Dangling/oversized effect graph, negative charges, non-finite/out-of-range modifier delta, or `inventory_slot_count` outside `[1,6]` | Validator fails closed with a located error; item/scenario does not load | Located field error |
| Checksum fold + replay | Match with placement, pickups, drops, consumable use | `ItemStore` + inventory fold under `AlgoVersion` 12; a replay reproduces byte-identical checksums; the 20 existing goldens re-baseline once and are stable across two runs | No error |

</intent-contract>

## Code Map

**New — content model & validation (mirror abilities):**
- `godot/src/Core/Definitions/ItemDefinition.cs` -- **NEW** POCO mirroring `AbilityDefinition.cs:20-93`: `[JsonPropertyName]` snake_case, `Fixed` gameplay numbers. Fields: `id`/`display_name`/`icon` (string), `charges` (int; `0` ⇒ non-consumable stat item), `Fixed` modifier deltas (`max_health_delta`/`attack_damage_delta`/`move_speed_delta`/`armor_delta` — reuse the four `Modifier.cs:59-66` fields), optional `[JsonPropertyName("effect")] EffectNode? EffectGraph` (the consumable graph, deserialized by the existing `EffectNodeJsonConverter`). Optional `cost_ore`/`cost_crystal` `Fixed` for 3.16 shops (unspent here).
- `godot/src/Core/Definitions/ItemLoader.cs` -- **NEW** mirror `AbilityLoader.cs:22-59`: `Load(json,label)`/`LoadFromFile(path)` through `ContentJson.Options`, catch `JsonException`→located `Fail`, then `new ItemDefinitionValidator().Validate(def)`.
- `godot/src/Core/Definitions/ItemRegistry.cs` -- **NEW** mirror `AbilityRegistry.cs:71-84`: `LoadFromDirectory(absDir)` (ordinal walk of `res://resources/data/items/*.json`), keep `Ok` only, `IndexOf(id)` ascending; runtime references an `int` index.
- `godot/src/Core/Definitions/ItemDefinitionValidator.cs` -- **NEW** mirror `AbilityValidator.cs`: id non-empty; `charges >= 0`; each modifier delta finite & within `Range=32768` (`UnitDefinitionValidator.cs:52`); if `EffectGraph != null`, run `EffectBounds.Validate(root)` (depth/total caps) verbatim; located `"item '<id>'.<path>: <reason>"` errors; mint `Validated<ItemDefinition>` via `ScenarioValidator.Proof` (add to the sole-minter allow-list) or a parallel `ItemValidationResult` (mirror `AbilityValidationResult.cs`).
- `resources/data/items/*.json` -- **NEW** at least two sample items (one stat item, one charged consumable) for the golden + manual verification.

**New — sim stores & system:**
- `godot/src/Core/ItemStore.cs` -- **NEW** SoA store (mirror `HeroStore.cs`/`BuildingStore.cs`): `MAX_ITEMS`; parallel arrays `DefId[]`, `Charges[]`, ground position `PosX[]`/`PosZ[]` (`Fixed`), `Held[]` (bool) + `CarrierHeroSlot[]` (int, for held); free-list + `_freeCount` LIFO, `Generation[]` ABA, `PackRef`/`TryResolveRef`, monotonic `Count`; `Create(defId, charges, pos)`→ref, `Destroy(ref)`, `Clear()` (Array.Clear all + free-list). A `FoldOrder()`/count-driven ascending enumeration for the checksum. **NOT** an `EntityWorld` entity (mirrors the `ResourceNodeStore` non-unit-map-object precedent).
- `godot/src/Combat/ItemSystem.cs` -- **NEW** tick system, owns an `EffectExecutor` + `SpatialHash` (like `AbilityCastSystem.cs:41-43`). Per tick, ascending-id: (a) resolve `PickupItem` command-states — move-toward + proximity claim into the first free slot or `OrderDenied` when full; apply the item's stat modifier on claim; (b) process pending `UseItem` — run the consumable graph via `EffectExecutor.Run`, `Charges--`, delete at zero; (c) `DropItem`/`DropAll(heroSlot,pos)` — spawn ground item, remove the stat modifier (`ModifierStore` remove-by-id), clear the slot. Injected: `EntityWorld`, `HeroStore`, `ItemStore`, `ModifierStore`, `ItemRegistry`, `CombatEventQueue`, `DamageTable`.

**Modify — commands, death, checksum, hashes, scenario, wiring:**
- `godot/src/Core/EntityWorld.cs` -- add `UnitCommand.PickupItem = 15`, `UseItem = 16`, `DropItem = 17` (frozen-value replay comments; enum stays ≤ `0x3F`, `EntityWorld.cs:12-50`). Pickup reuses `CommandTarget[id]` (or a new `PickupTargetRef[]`) to hold the target item ref; a `PickupItem` case in the `ApplyActiveOrder` switch (`NetworkCommand.cs:201`) sets `CommandState`, `MoveTarget`, `Flags |= Moving`.
- `godot/src/Multiplayer/NetworkCommand.cs` -- `OrderApplier.Apply`: dispatch `UseItem`/`DropItem` **before the entity guard** delegating to `items?.UseItemCommand(id, slot)` / `items?.DropItemCommand(id, slot)` (the Train/Revive building-command pattern, `:139-164`), and route `PickupItem` through `ApplyActiveOrder`. Add an `ItemSystem? items = null` param threaded to all three apply sites. `items == null` ⇒ no-op.
- `godot/src/Core/HeroStore.cs` -- add `const int INVENTORY_SLOTS = 6`; `int[] Inventory` sized `MAX_HEROES * INVENTORY_SLOTS` (holds `ItemStore` refs, `-1` empty); reset in `Mint` (`:194-217`) and `Clear` (`:229-248`). Folded (see SimChecksum + StartStateHash).
- `godot/src/Combat/DamageResolver.cs` -- in `KillEntity` (`:88-101`), before `world.Destroy(id)`: if `HeroIndex[id] != HERO_NONE`, call `items?.DropAll(heroSlotOf(id), world.Position[id])` (position still valid pre-Destroy). Thread `HeroStore`/`ItemSystem` (or a drop delegate) into `KillEntity`.
- `godot/src/Core/SimChecksum.cs` -- `AlgoVersion 11→12` (`:111` + changelog `:66-110`); add an `ItemStore? items = null` trailing param to `Compute` (`:117-118`) and a null-guarded fold block (mirror the hero block `:290-313`: count then per-item `DefId`/`Charges`/`PosX.Raw`/`PosZ.Raw`/`Held`/`CarrierHeroSlot`); extend the per-hero loop to fold the `INVENTORY_SLOTS` refs. Empty item store folds `Mix(0)`.
- `godot/src/Core/Definitions/StartStateHash.cs` -- `AlgoVersion 1→2` (`:42` + doc `:40`); fold the `INVENTORY_SLOTS` refs in the hero-row loop (`:66-71`) and a sorted `model.Items` walk (mirror `CanonicalModelHash` placement walks). `CanonicalModelHash.Compute` seed unchanged (v3).
- `godot/src/Core/Definitions/ScenarioData.cs` -- add `[JsonPropertyName("items")] ScenarioItem[] Items = Array.Empty<>()` (mirror `Units` `:170`) and `[JsonPropertyName("inventory_slot_count")] int? InventorySlotCount` (`WhenWritingNull`, NULL⇒`HeroStore.INVENTORY_SLOTS`, the `revival_rule` nullable-block pattern `:203-205`).
- `godot/src/Core/Definitions/ScenarioItem.cs` -- **NEW** small class mirroring `ScenarioUnit.cs`(`ScenarioData.cs:112-127`): `item_id` (string), `x`, `z`.
- `godot/src/Core/Sim/ScenarioApplier.cs` -- add an item-placement loop (mirror the resource-node loop `:111-116`): for each `model.Items`, resolve `item_id` via the injected `ItemRegistry` and `ItemStore.Create(defId, defCharges, pos)`.
- `godot/src/Core/Sim/SimulationHost.cs` -- construct `ItemStore` + `ItemSystem`; wire `ItemSystem` into the tick order at a **new slot** after `CombatSystem` (update `SystemOrderTest`); pass `ItemStore` into every `SimChecksum.Compute` call site; inject `ItemRegistry` + `ItemStore` into `ScenarioApplier`, `DamageResolver`, and the apply sites.
- `godot/src/Effects/ModifierStore.cs` -- add a public `bool RemoveByModifierId(int hostId, int modifierId)` that reverts one slot's contribution (extract/reuse the private `RemoveSlot` revert `:302-323`) so a stat item's modifier can be removed on drop without destroying the entity. Stat-item modifiers use a deterministic `Id` derived from the item ref (stable while held) so removal targets exactly that item's modifier.
- `godot/src/Combat/CombatEventQueue.cs` -- append `ItemPickedUp`, `ItemUsed`, `ItemDropped` to `CombatEventType` (`:8-32`); append-only, golden-safe (not folded). Reuse `OrderDenied` (`:25`) for the full-inventory reject.
- `godot/src/UI/EntityPlacer.cs` -- add `PlacementMode.Item` (`:28`, `MODE_ORDER` `:46-47`) + a spawn branch creating a ground `ItemStore` instance — the minimal in-game placement surface (full authoring is 3.16).
- `godot/src/UI/CommandCardSystem.cs` / `godot/src/UI/SelectionSystem.cs` -- minimal affordance: right-click a ground item issues `PickupItem`; a hotkey issues `UseItem` on a slot (rides `OrderApplier.Apply(..., items: _itemSys)` offline). Full inventory grid is 3.16.

**Tests (Godot-free Tier-1 unless noted):**
- `godot/ProjectChimera.Sim.Tests/Combat/ItemStoreTests.cs` -- **NEW** recycle-guard (a recycled slot carries no prior `DefId`/`Charges`), free-list, `PackRef`/`TryResolveRef` ABA, `FoldOrder` ascending.
- `godot/ProjectChimera.Sim.Tests/Combat/ItemSystemTests.cs` -- **NEW** oracles for every I/O matrix row: pickup-into-free-slot (+ stat modifier materializes in `Effective*`), full-inventory denial, two-hero ascending-id claim, drop removes modifier + respawns ground item, consumable charges>1 decrement, last-charge delete, use-on-empty no-op, configured-slot-cap-below-stride reject, hero-death drops all items at death pos + revived-hero-empty.
- `godot/ProjectChimera.Sim.Tests/Definitions/ItemDefinitionValidatorTests.cs` -- **NEW** dangling/oversized effect graph, negative charges, non-finite/out-of-range delta, `inventory_slot_count` out of `[1,6]` all fail closed with located errors; a valid item passes.
- `godot/ProjectChimera.Sim.Tests/Golden/ItemScenario.cs` + `ItemGoldenTests.cs` + `item-scenario.golden.txt` + csproj `<EmbeddedResource>` -- **NEW** fixed-seed scenario: place items, a hero picks up a stat item and a consumable, uses a charge, dies and drops — proving the fold end-to-end, byte-identical across two runs.
- `godot/ProjectChimera.Sim.Tests/Golden/SimChecksumCoverageGuardTest.cs` -- re-pin `ExpectedV11Hash`→`ExpectedV12Hash` (`:114/:123`, rename `KnownWorldState_ProducesPinnedV12Hash`), add `AssertItemStoreFoldedIntoChecksum` teeth + inventory-slot teeth in `AssertHeroStoreFoldedIntoChecksum` (`:397`).
- `godot/ProjectChimera.Sim.Tests/Meta/VersionStampConsistencyTests.cs` -- `ExpectedSimChecksumAlgoVersion` 11→12 (`:54`), `ExpectedStartStateHashAlgoVersion` 1→2 (`:65`).
- `godot/ProjectChimera.Sim.Tests/Validation/StartStateHashTests.cs` -- re-record `hero-start-state.golden.txt` + the independent-FNV pin for v2; add an inventory/placed-item fold assertion.
- Re-baseline all 20 SimChecksum `.txt` goldens once under `CHIMERA_GOLDEN_RECORD=1` (the item/inventory fold adds `Mix` terms even to item-free scenarios).
- `godot/ProjectChimera.Sim.Tests/Definitions/` scenario round-trip: `ScenarioData.Items` + `inventory_slot_count` serialize/deserialize (omit-when-empty/null → byte-identical).

## Tasks & Acceptance

**Execution:**
- `EntityWorld.cs` / `NetworkCommand.cs` -- `PickupItem=15`/`UseItem=16`/`DropItem=17`; dispatch through `OrderApplier.Apply` (pickup→`ApplyActiveOrder`; use/drop→pre-guard delegate) across all three apply sites; `items == null` no-op.
- `ItemDefinition.cs` / `ItemLoader.cs` / `ItemRegistry.cs` / `ItemDefinitionValidator.cs` / `resources/data/items/*.json` -- content model + loader/registry + fail-closed `Validated<T>` gate (mirror abilities); two sample items.
- `ItemStore.cs` -- recycle-guarded SoA (ground/held), free-list, `PackRef`/`TryResolveRef`, `FoldOrder`.
- `HeroStore.cs` -- `INVENTORY_SLOTS=6` + `Inventory[]` on the row; reset in `Mint`/`Clear`.
- `ItemSystem.cs` -- pickup proximity-claim (ascending-id, full→`OrderDenied`), consumable execution via the shared `EffectExecutor` (charges--/delete-at-zero), stat-modifier apply-on-carry / remove-on-drop, `DropAll` for death.
- `ModifierStore.cs` -- public `RemoveByModifierId`; stable per-item modifier `Id`.
- `DamageResolver.cs` -- drop carried items at the death position before `Destroy`.
- `SimChecksum.cs` -- `AlgoVersion 11→12`; fold `ItemStore` + inventory (one bump).
- `StartStateHash.cs` -- `AlgoVersion 1→2`; fold inventory + placed items (`CanonicalModelHash` untouched, v3).
- `ScenarioData.cs` / `ScenarioItem.cs` / `ScenarioApplier.cs` -- `items[]` + `inventory_slot_count`; placement loop → `ItemStore`.
- `SimulationHost.cs` -- construct + wire `ItemStore`/`ItemSystem` (new tick slot, `SystemOrderTest` updated); thread deps.
- `EntityPlacer.cs` / `CommandCardSystem.cs` / `SelectionSystem.cs` -- minimal item placement + pickup/use affordance (full UI = 3.16).
- Tests -- `ItemStoreTests`, `ItemSystemTests` (whole matrix), `ItemDefinitionValidatorTests`, `ItemScenario` golden pair, coverage-guard re-pin + item/inventory teeth, `VersionStampConsistencyTests` (12/2), `StartStateHashTests` re-record, scenario round-trip; re-baseline the 20 SimChecksum goldens once.

**Acceptance Criteria:**
- Given a ground item and a hero with a free inventory slot within the configured count, when the hero is ordered onto it, then it resolves deterministically (move-to + ascending-id proximity claim) into the first free slot on the persisted hero row and a stat item's modifier materializes in the carrier's `Effective*` stats; when the inventory is full, then the pickup is rejected with an `OrderDenied` event and the item stays on the ground.
- Given a carried charged consumable, when used from a slot, then its authored Effect-Graph executes through the SAME `EffectExecutor` abilities use, the charge count decrements, and the item is deleted (slot cleared, instance freed) when it reaches zero; a stat item's modifier is removed when the item leaves the inventory (manual drop or death).
- Given a hero carrying items is killed, then every carried item drops to the ground at the death position (before the entity is destroyed) and the inventory clears; when the hero is later revived it returns empty and can re-collect the dropped items (inventory-on-row + WC3 drop discharges the 3.14 obligation — no item is silently lost).
- Given the `ItemStore` and per-hero inventory now mutate mid-match, when `SimChecksum` computes, then they fold under a single `AlgoVersion` 11→12 bump, the 20 existing SimChecksum goldens are re-baselined once and reproduce byte-identically across two runs, a new item scenario golden is byte-identical across two runs, and the coverage-guard known-state hash + `VersionStampConsistencyTests` are re-pinned to v12.
- Given item content and initial state, when definitions load, then each passes the same fail-closed `Validated<T>` gate (dangling/oversized effect graph, negative charges, out-of-range delta, `inventory_slot_count` ∉ `[1,6]` blocked with located errors); and the initial placed-item + inventory state folds into `StartStateHash` (1→2, `hero-start-state.golden.txt` re-recorded) so a mismatched item loadout is rejectable at the handshake — `CanonicalModelHash` staying v3.

## Spec Change Log

_Empty — no bad_spec loopback occurred; all review findings were patch/defer/reject._

## Review Triage Log

### 2026-07-08 — Review pass
- intent_gap: 0
- bad_spec: 0
- patch: 10: (high 4, medium 3, low 3)
- defer: 8: (high 0, medium 1, low 7)
- reject: 5: (high 0, medium 0, low 5)
- addressed_findings:
  - `[high]` `[patch]` **UseItem/DropItem bypassed the faction-ownership guard (anti-cheat hole; Edge Case #1).** The two commands dispatched in `OrderApplier.Apply` *before* the entity guard, and `ItemSystem.ResolveHeroSlot` never checked faction — a player could force an ENEMY hero to consume a charge or drop a carried item. Since `UnitId` names the hero ENTITY (not a building), moved both dispatch blocks to AFTER the `IsAlive(id) && FactionOf[id]==expectedFaction` guard (where PickupItem already sits), closing the hole; `NetworkCommand.cs` comment corrected. Owned-hero use unchanged (golden/oracles still green).
  - `[high]` `[patch]` **Item use & drop were dead online and in replay (Verification-Gap #1).** `LockstepManager.ApplyOrders` and `ReplayPlayer.ApplyOrders` called `OrderApplier.Apply` without the new `items:` argument → both defaulted null → silent no-op; only the offline site was wired (pickup masked it, being a CommandState). Added `ItemSystem? Items` to both, threaded through both call sites + bootstrap wiring. +parity test driving UseItem/DropItem identically through the live and replay paths.
  - `[high]` `[patch]` **`inventory_slot_count` was sim-affecting but folded into no handshake hash (desync-not-rejectable; Blind Hunter F1).** Two peers with mismatched slot counts diverge on a full-inventory pickup yet the load-time `StartStateHash` matched. Folded `InventorySlotCount ?? INVENTORY_SLOTS` into `StartStateHash` v2; re-recorded `hero-start-state.golden.txt`; +independent-FNV + slot-count-changes-hash assertions.
  - `[high]` `[patch]` **Carried stat-item modifier could overflow Fixed 16.16 (Edge Case #2).** A single authored delta near the ±32767 cap (×6 stacking worse) wrapped `Effective*` negative. Capped item stat deltas at `MAX_ITEM_STAT_DELTA=1000` in `ItemDefinitionValidator` (6×1000 leaves ample headroom below the 32767 ceiling); +validator oracles. The general unsaturated-effective-stat class (extreme base + growth, not items) remains the pre-existing deferred `ModifierSystem` concern.
  - `[medium]` `[patch]` **Scenario item-placement + slot-cap wiring was production-only, untested (Verification-Gap #2 / Intent-Alignment E).** +`ScenarioApplierTests` applying a `ScenarioData` with a `ScenarioItem` + `InventorySlotCount=3` asserting `Items.Count==1` (def/charges/pos) and `UsableSlots==3`.
  - `[medium]` `[patch]` **Death-drop was coupled to `World.Destroy` with no real-combat test (Blind Hunter F3).** +a test killing a hero carrying items through `DamageResolver.Apply` lethal damage, asserting items drop at the death position and the row clears.
  - `[low]` `[patch]` **`HeroStore.Mint` inventory recycle-guard untested (Verification-Gap #3).** +a mint→destroy→re-mint recycle test asserting the reused row's inventory is all `INVENTORY_EMPTY`.
  - `[low]` `[patch]` **ScenarioApplier silently dropped items past `MAX_ITEMS` (Edge Case #4 / Blind Hunter F10).** Added a warn on `ItemStore.Create()==-1`, mirroring the neighboring `IndexOf<0` diagnostic.
  - `[low]` `[patch]` **`FindNearestGroundItem` tie-break contradicted its doc (Edge Case #6 / Blind Hunter F7).** `<=`→`<` so the lowest-slot item wins on equal distance (presentation pick helper).
  - Deferred (8): (a) `ModifierStore.Apply` silently no-ops for a hero at the per-entity modifier cap → silently inert item [med]; (b) EntityPlacer redo/undo stale-ref leak (editor-only) [low]; (c) consumable effect runs at order-apply time, not the index-9 tick — latent RNG-interleave once a random consumable ships (spec Block-If forbids random leaves today) [low]; (d) placed-item ref assignment is JSON-array-order-dependent while `StartStateHash` canonicalizes order — pre-existing class shared with units/buildings [low]; (e) validator permits a consumable to also carry stat deltas, crossing the documented XOR archetype — behavior self-consistent, a design call [low]; (f) move-to pickup traversal path has no test (items spawn on the hero) [low]; (g) manual `DropItem` has no player-facing UI trigger / no replay-golden coverage (drop primitive is unit-tested) — 3.16 UI [low]; (h) use-hotkey hard-codes slot 0 & `StartStateHash` placed-item byte layout not independently pinned [low]. All → `deferred-work.md`.
  - Rejected (5, all low): item DEFINITIONS folded into no canonical-content hash / `StartStateHash` not yet on the handshake wire (AC3 requires defs→gate + state→hash, both done; folding item content-models into `CanonicalModelHash` + wiring the handshake are explicitly Story 9.1, mirroring the 3.2 `StartStateHash` precedent — Intent-Alignment A); use/drop bound-check against stride not `UsableSlots` (benign — non-usable slots stay empty, F4); `UseItemCommand` trusts the inventory ref without a `Held`/carrier re-check (unreachable without a separate bug, F5); stale `CommandTarget` ref folded for an Idle post-pickup hero (deterministic, unread for Idle, F8); `ItemModifierId` overflow after ~3.5M single-slot recycles (self-consistent apply/remove, F9).

### 2026-07-08 — Follow-up review pass
- intent_gap: 0
- bad_spec: 0
- patch: 3: (high 0, medium 1, low 2)
- defer: 4: (high 0, medium 1, low 3)
- reject: 6: (high 0, medium 0, low 6)
- addressed_findings:
  - `[medium]` `[patch]` **`UseItemCommand` mutated the item slot after running the effect graph with no liveness re-check (Edge Case #1).** A creator-authored self-damaging consumable (a self-targeted `Damage` leaf — the validator permits any bounded graph on a `charges>0` item) can KILL its own carrier during `_executor.Run`; `OnEntityDestroyed`→`DropAll` then already drops the item and clears its slot, after which the post-run `Charges[itemSlot]--` / `Destroy(itemSlot)` corrupts an already-dropped/recycled instance (double-drop, wrong-slot decrement, double-free — a determinism hazard). Added a post-`Run` guard: bail unless the carrier is still alive AND this exact instance still occupies the slot. No-op for the shipped self-heal (golden bytes unchanged; full suite green).
  - `[low]` `[patch]` **The reset-determinism keystone had no teeth on the v12-folded item state (Verification-Gap #1).** `ClearForReset_LeavesEveryStoreEqualToFreshlyConstructed` and the reproduce-run keystone use item-free fixtures, so deleting `Items.Clear()` (or the `HeroStore.Inventory` reset) from `ClearForReset` would still pass green. Extended the store-equality test to populate a live `ItemStore` instance + a non-default inventory ref before the clear, then assert `Items.Count==0`, `Items.Alive`/`Items.Generation`, and `Heroes.Inventory` all equal a fresh boot.
  - `[low]` `[patch]` **Stale attribution comment (Blind Hunter #14).** `SimResetTests.HashAlgoVersions_AreUnchanged` asserted `AlgoVersion==12` under a `// Story 3.13` comment; corrected to Story 3.15 (the ItemStore + inventory 11→12 fold).
  - Deferred (4): (a) `MAX_ITEM_STAT_DELTA=±1000` is applied uniformly to `move_speed_delta`, but base hero speeds are single-digit, so an authored `move_speed_delta=1000` yields ~1003 units/tick (tunneling) and −1000 permanently freezes the hero — the validator green-lights both [med]; (b) death-drop's `OnEntityDestroyed` silently no-ops (orphaning held items) if a hero's `HeroStore` row is torn down before its entity on a permanent non-revivable death — the teardown ordering is un-asserted [low]; (c) a mixed-selection right-click within pickup radius of a ground item issues `PickupItem` to only the first hero and returns, stranding the rest of the selection (presentation UX) [low]; (d) `EntityPlacer`'s item palette can only ever place registry item 0 — `_itemIndex` is never incremented despite its "cycled by re-clicking" doc (editor-only) [low]. All → `deferred-work.md`.
  - Rejected (6, all low): `StartStateHash` folds `InventorySlotCount` unclamped while the runtime clamps to [1,6] (over-rejection only — the safe direction; a tampered out-of-clamp value SHOULD reject); `UseItem`/`DropItem` aren't Shift-queueable (not a 3.15 requirement); `SimChecksum` folds held items' stale-but-deterministic `PosX`/`PosZ` (no desync); `StartStateHash` doesn't fold item `Charges` (definition-content divergence is Story 9.1's `CanonicalModelHash` job per intent); `LockstepManager.ApplyOrders`'s `Items` forwarding is untested (pre-existing structural Tier-1/Godot boundary, mirrors the tested ReplayPlayer + direct-applier parity paths); `ScenarioApplier`'s unknown-`item_id` / store-full skip branches are untested (degenerate, no determinism impact). Additionally ~8 re-surfaced findings duplicated existing story-3.15 `deferred-work.md` entries (modifier-cap inert item, EntityPlacer undo leak, consumable dispatch-timing, placement-order-vs-hash, hybrid consumable, pickup-traversal coverage, manual-drop UI, slot-0 hotkey / `StartStateHash` placed-item oracle) and were NOT re-added, per the append-only ledger constraint.

## Design Notes

**D1 — Inventory on the `HeroStore` row, not the entity.** Story 3.14's `RespawnHero` builds a fresh `EntityWorld` entity; anything hung off the entity is lost on revival (the `deferred-work.md` binding obligation). A fixed-stride `int[MAX_HEROES * INVENTORY_SLOTS]` of `ItemStore` refs on the persisted row survives death→revival by construction and folds naturally alongside `Level`/`Xp`. This is the exact mirror of the 3.13→3.14 `GrowthStacksApplied` fix.

**D2 — Death drops items (WC3), so revival needs no re-attach.** On hero death the carried items drop to the ground at the death position and the inventory clears; the revived hero returns empty and walks back to re-collect. This satisfies the obligation ("no item silently lost") with the simplest correct behavior and sidesteps re-applying stat modifiers onto the fresh entity. The drop happens in `KillEntity` (position + `HeroIndex` both valid pre-`Destroy`), reusing `ItemSystem.DropAll`.

**D3 — One `SimChecksum` fold; `StartStateHash` and `CanonicalModelHash` are distinct hashes.** The mutable `ItemStore` + inventory are mid-match sim truth → `SimChecksum` (11→12, the "one fold ⚑"). The *initial* item state (placed map-items + inventory) is start-state → `StartStateHash` (1→2), which seeds from `CanonicalModelHash` (kept v3, structurally unchanged). Folding item **definitions** into the canonical content-model hash is explicitly Story 9.1 ("consumes 3.15 content models as they land") — out of scope here; the `Validated<T>` gate is what satisfies "definitions pass the gate."

**D4 — Consumables reuse the 2.1 executor verbatim.** An item's `EffectGraph` is the same `EffectNode?` type as `AbilityDefinition.EffectGraph`, deserialized by the same `EffectNodeJsonConverter`, validated by the same `EffectBounds.Validate`, and run by an `EffectExecutor` with an `EffectContext` built like `AbilityCastSystem.cs:211-214` (RNG from `world.Rng`). No new engine, no new leaf. If a consumable needs a random leaf (reserved-not-implemented), HALT rather than adding one.

**D5 — Stable modifier Id for removable stat items.** A stat item applies a permanent `Modifier` (`DurationTicks < 0`) via `ModifierStore.Apply`; its `Id` is derived deterministically from the item's stable `ItemStore` ref so `RemoveByModifierId` on drop reverts exactly that item's contribution (reusing the private `RemoveSlot` revert). Death-drop removes it before `Destroy` (or `ClearEntity` on `Destroy` is a harmless no-op afterward).

**D6 — Fixed inventory stride, configurable usable count.** `INVENTORY_SLOTS = 6` is the SoA stride and default (WC3); the per-scenario `inventory_slot_count ∈ [1,6]` caps the *usable* slots (a fuller-than-configured inventory denies). A ceiling above 6 is a future stride bump, out of scope.

## Verification

**Commands:**
- `dotnet build godot/godot.sln` -- expected: clean build, no determinism-analyzer (CHM*) or banned-float violations in new sim code.
- `dotnet test godot/ProjectChimera.Sim.Tests` -- expected: all green (allow the pre-existing unrelated `ProceduralMapGeneratorTests` cross-platform tripwire, documented failing on baseline in the 3.12–3.14 runs), including `ItemStoreTests`, `ItemSystemTests`, `ItemDefinitionValidatorTests`, `ItemGoldenTests` (byte-identical two runs), `SimChecksumCoverageGuardTest` (v12 pin + item/inventory teeth), `StartStateHashTests` (v2), `VersionStampConsistencyTests` (12/2), `SystemOrderTest`, and every re-baselined `*GoldenTests`.
- `CHIMERA_GOLDEN_RECORD=1 dotnet test godot/ProjectChimera.Sim.Tests --filter FullyQualifiedName~Golden` then `dotnet build` then a clean `dotnet test` -- expected: the 20 SimChecksum goldens re-record once to v12 headers, `hero-start-state.golden.txt` re-records to the v2 StartStateHash, the new `item-scenario.golden.txt` records, and all are stable byte-identical across two consecutive normal runs.

**Manual checks:**
- Open the map editor (`/godot-verify`): the `EntityPlacer` palette offers an Item mode; place a stat item and a consumable. In a playtest, order a hero onto an item → it moves and picks it up (a stat item's stats visibly increase); use the consumable → its effect fires and a charge is consumed, deleting the item at zero; fill the inventory → a further pickup is denied; kill the hero → its items drop on the ground and a revived hero can re-collect them.

## Auto Run Result

Status: done
Blocking condition: none

### Summary

Implemented Story 3.15 (item & inventory sim) end-to-end. A new recycle-guarded `ItemStore` SoA holds item instances (on-ground or held); a fixed-stride (`INVENTORY_SLOTS=6`) per-hero inventory lives on the persisted `HeroStore` row so it survives death→revival by construction (discharging the Story 3.14 binding obligation). Items are content-gated `ItemDefinition` data loaded/validated through the same `Validated<T>` gate as abilities (`ItemLoader`/`ItemRegistry`/`ItemDefinitionValidator`). A hero ordered onto a ground item resolves pickup deterministically (move-to + ascending-id proximity claim) into the first free slot or is denied (`OrderDenied`) when full; a carried stat item applies its authored modifier deltas via `ModifierStore` (removed on drop via a new `RemoveByModifierId`); a charged consumable fires its authored Effect-Graph through the shared `EffectExecutor` (no new engine), decrements a charge, and deletes at zero. On hero death the carried items drop to the ground (WC3-style) at the death position via the `EntityWorld.OnDestroy` hook. Mutable `ItemStore` + inventory fold into `SimChecksum` in one `AlgoVersion` 11→12 bump (20 goldens re-baselined); placed items + inventory + `inventory_slot_count` fold into `StartStateHash` 1→2; `CanonicalModelHash` stays v3 (item-definition folding into the pre-match content hash is Story 9.1). New `UnitCommand.PickupItem=15/UseItem=16/DropItem=17` ride the shared `OrderApplier` across all three apply sites; minimal `EntityPlacer` placement mode + `SelectionSystem` pickup/use affordances (full inventory UI + shops = 3.16).

### Files changed

New sim/content: `ItemDefinition.cs`, `ItemValidationResult.cs`, `ItemDefinitionValidator.cs` (mints `Validated<ItemDefinition>`, `MAX_ITEM_STAT_DELTA` overflow cap), `ItemLoader.cs`, `ItemRegistry.cs`, `ItemStore.cs` (recycle-guarded SoA), `Combat/ItemSystem.cs` (pickup/use/drop/death-drop tick + commands), `resources/data/items/ring_of_vigor.json` + `potion_of_healing.json`.
Modified sim: `EntityWorld.cs` (three commands), `CombatSystem.cs` (PickupItem nav case), `NetworkCommand.cs` (`OrderApplier` `items` param + dispatch, use/drop moved past the ownership guard), `ModifierStore.cs` (`RemoveByModifierId`), `HeroStore.cs` (`INVENTORY_SLOTS` + `Inventory[]`), `CombatEventQueue.cs` (item events), `SimChecksum.cs` (v12 fold), `StartStateHash.cs` (v2 fold incl. `inventory_slot_count`), `ScenarioData.cs` + `ScenarioItem`, `ScenarioValidator.cs` (`inventory_slot_count ∈ [1,6]`), `ScenarioApplier.cs` (placement loop + store-full warn), `SimulationHost.cs`/`SimulationLoop.cs` (ItemStore+ItemSystem at tick index 9, checksum, reset).
Modified UI/bootstrap: `EntityPlacer.cs` (Item palette mode), `SelectionSystem.cs` (right-click pickup + `T`-use, strict tie-break), `MainScene.cs`/`CameraPhase.cs` (load `ItemRegistry`, wire host+placer+selection), `LockstepManager.cs`/`ReplayPlayer.cs` (`Items` field threaded into Apply), `MatchLifecycleController.cs` (assign `.Items` on live+replay).
Tests: `ItemStoreTests`, `ItemSystemTests`, `ItemDefinitionValidatorTests`, `ItemScenario`/`ItemGoldenTests`, `ScenarioItemRoundTripTests`, `CommandApplyParityTests` (+item parity), `ScenarioApplierTests` (+placement), `HeroStoreTests` (+inventory recycle), plus re-pins in `SimChecksumCoverageGuardTest` (v12 `0xAFB46F6A` + item/inventory teeth), `VersionStampConsistencyTests` (12/2), `SystemOrderTest` (14 systems), `StartStateHashTests` (v2 + slot-count), `ValidatedMintingTests`; 20 SimChecksum goldens + `hero-start-state` + `item-scenario` re-baselined.

### Review findings

Four parallel Opus-4.8 layers (Blind Hunter, Edge Case Hunter, Verification-Gap, Intent-Alignment). Triage: **10 patches (high 4, medium 3, low 3), 8 defer (medium 1, low 7), 5 reject, 0 intent_gap, 0 bad_spec** — see Review Triage Log. Highlights of the 4 high patches: (1) `UseItem`/`DropItem` bypassed the faction-ownership guard (anti-cheat) → moved past the guard; (2) item use/drop were dead online + in replay (apply sites never forwarded `items`) → threaded + parity-tested; (3) `inventory_slot_count` was sim-affecting but in no handshake hash → folded into `StartStateHash`; (4) a single authored item delta could overflow `Fixed` 16.16 → capped at ±1000. Deferred 8 (silently-inert item at modifier cap; editor undo leak; order-apply RNG interleave; placement-order-vs-hash canonicalization; consumable/stat-delta archetype crossing; move-to traversal coverage; manual-drop UI; slot-0 use hotkey) → `deferred-work.md`. Rejected 5 (definitions-in-canonical-hash is 9.1; four benign/unreachable hardening nits).

### Verification

- `dotnet build godot/godot.sln` — clean, 0 errors, 0 warnings; no CHM/banned-float violations in new sim code.
- `dotnet test godot/ProjectChimera.Sim.Tests` — **1053 passed, 1 skipped, 1 failed**. The single failure is the pre-existing, unrelated `ProceduralMapGeneratorTests` WSL/Windows cross-platform hash tripwire (expected 3026392010, actual 413099587 — documented failing on baseline in the 3.12–3.14 runs; this diff touches no procedural-map/serializer source). All item/fold/parity/validator/recycle tests green.
- Golden discipline: recording under `CHIMERA_GOLDEN_RECORD=1` moved exactly the expected files — the 20 SimChecksum goldens to v12 and `hero-start-state.golden.txt` to StartStateHash v2 (`D657767D4C2AF479`), plus the new `item-scenario.golden.txt`; all byte-identical across two consecutive normal runs. `AlgoVersion` pins: SimChecksum 12, StartStateHash 2, CanonicalModelHash 3; coverage-guard known-state re-pinned to `0xAFB46F6A`.
- Matrix Test Audit: every I/O matrix row is covered by a passing, executed test (see step-03 audit).

### Follow-up review pass (2026-07-08)

An independent follow-up review (recommended by the initial pass) re-ran the four Opus-4.8 layers against the full `04022cc..HEAD` diff. No `intent_gap`, no `bad_spec`; the initial implementation held up (Intent-Alignment confirmed the sim surface is faithfully and thoroughly proven under the loose reading the boundaries support). This pass applied **3 patches** and **deferred 4** new items:

- **Patch (medium) — `ItemSystem.UseItemCommand` post-`Run` liveness guard.** The use path decremented/destroyed the item slot after running the authored effect graph without re-checking that the carrier survived. A creator-authored self-damaging consumable (a self-targeted `Damage` leaf, which the validator permits) could kill its carrier mid-`Run` → the death-drop hook already drops the item and clears the slot → the post-run mutation then corrupts a dropped/recycled instance (a determinism hazard). Added a guard that bails unless the carrier is still alive and this exact instance still occupies the slot. Latent for shipped content (the only consumable is a non-lethal self-heal), so the item golden is byte-unchanged.
- **Patch (low) — reset-keystone item teeth.** `SimResetTests.ClearForReset_LeavesEveryStoreEqualToFreshlyConstructed` used item-free fixtures, so a removed `Items.Clear()` / `HeroStore.Inventory` reset would have passed vacuously. Extended it to populate a live `ItemStore` instance + a non-default inventory ref before the clear and assert both reset to a fresh boot.
- **Patch (low) — stale attribution comment** in `SimResetTests` (`// Story 3.13` → Story 3.15 on the `AlgoVersion==12` assertion).
- **Deferred 4** (→ `deferred-work.md`): uniform `MAX_ITEM_STAT_DELTA` too loose for `move_speed_delta` [med]; death-drop orphan on permanent-death teardown ordering [low]; mixed-selection right-click-on-item strands the rest of the selection [low]; `EntityPlacer` item palette pinned to registry item 0 [low].

**Verification (this pass):** `dotnet build ProjectChimera.Sim.Tests` clean (0 errors; only the pre-existing CS8632 nullable warnings). `dotnet test ProjectChimera.Sim.Tests` → **1053 passed, 1 skipped, 1 failed** — the single failure is the pre-existing, unrelated `ProceduralMapGeneratorTests` cross-platform golden tripwire (Expected 3026392010 / Actual 413099587), confirmed failing **identically at baseline `04022cc`** via a clean worktree (untouched by this story or these patches). All item/reset/golden/checksum/parity/validator tests green, including the newly-toothed reset keystone.

**Residual risks:** the 4 deferred items above (all latent or presentation/editor-only, none a determinism defect in shipped content); the pre-existing `ProceduralMapGeneratorTests` platform tripwire (unrelated to Epic 3).

### Residual risks

- All UI/presentation surfaces (`EntityPlacer` Item mode, `SelectionSystem` right-click-pickup / `T`-use, the minimal use-hotkey) are verified by clean compile + pattern-consistency, not a live in-engine session (headless environment). The determinism-critical spine (place → pickup → stat modifier → consumable/charge → drop → death-drop → SimChecksum v12 / StartStateHash v2 fold, incl. replay-vs-live parity) is fully covered by the golden + direct oracles.
- Follow-up review recommended (`followup_review_recommended: true`): this pass made four high-severity cross-cutting fixes — an anti-cheat command-dispatch reorder, wiring a whole feature (item use/drop) that was dead online/replay across the networking apply sites + bootstrap, a determinism handshake-fold that moved a golden, and a Fixed-overflow validator cap — warranting an independent confirmatory pass.
- A `EffectExecutorBoundsTests.Run_IsZeroAlloc_AfterWarmup` zero-alloc micro-benchmark flaked once under full-suite GC pressure during patch verification; it passes cleanly in isolation and is untouched by this change (pre-existing benchmark sensitivity, not a regression).
