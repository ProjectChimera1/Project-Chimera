---
baseline_commit: 7e7f3b25e5db77932a4e9800f9d0ea1095de8979
---

# Story 2.8: Combat Reachability I — Per-Unit Production Selection UI and Air Production Building/Category

Status: review

<!-- Validation: optional. Created via gds-create-story [ultracode]: 6-analyst parallel codebase+artifact recon + direct source grounding + a 3-validator adversarial panel. -->

## Story

As a player,
I want to choose **which** unit a production building trains (not just the first of its category) and to build/train **Air** units via an Air production building,
So that the full showcase rosters are actually reachable through the command card and the factions can be played as designed.

## Why this matters (verified)

Today each producer trains **only the first unit of its category** (`FactionDefinition.GetUnitByCategory` returns the first match), and **no building produces the `Air` category at all**. Source-verified result: **4 of every faction's 8 units are unreachable** through the command card:

| Faction | Reachable today | **Locked-out (dead data)** |
|---|---|---|
| **Alpha — Crucible Covenant** | Acolyte (Worker), Covenant Transmuter (Melee), Pierce Marksman (Ranged), Crucible Mortar (Siege) | Quicksilver Runner (Melee), Bulwark Adept (Melee), **Circle Savant (Ranged — carries `fireball` + energy)**, **Greycrest griffin (Air)** |
| **Beta — Sanguine Court** | Cinderhand Thrall (Worker), Maul-Fused Wretch (Melee), Bolt Penitent (Ranged), Render Crawler (Siege) | Slag Bulwark (Melee), Pride Colossus (Melee), Cinder Cantor (Ranged), **Envy wyvern (Air)** |

The locked set includes **both factions' signature casters** (Circle Savant / Cinder Cantor) and **immortal anchors** — i.e. the units that carry the Epic-2 ability/passive mechanics (2.4–2.7). Until 2.8 lands, those abilities cannot be exercised on a *trained* army, and the air-combat stories **2.9a** (anti-air/anti-building) and **2.10** (signature mechanics) are blocked from being playtested on a real composition. The air units (`griffin`/`wyvern`) and the three category siblings already exist fully-statted in JSON — this story makes them **reachable**, it does not author new units.

_Covers: FR-11, AR-8. Depends on: 2.4 (ability command-card spine — done)._

## Acceptance Criteria

### AC1 — Per-unit production selection (epic AC1, verbatim + made testable)

**Given** a production building whose category has more than one unit defined in the faction **When** the player selects the building **Then** the command card lists **each** unit in that category (one button per unit, in faction `Units` JSON order) and the player can pick which one to train **And** `TrainUnit` queues and later spawns the **selected** unit (not just the first) **And** the existing cost, supply, and prerequisite checks remain enforced for the chosen unit (in the same prereq → supply → ore order; ore is spent only after all gates pass).

- **AC1.1** Selecting the 2nd (or 3rd) unit of a category trains that exact unit: assert the spawned entity's `CategoryOf`/identity matches the chosen def, not first-of-category.
- **AC1.2** A train request whose chosen index is out-of-range, or whose chosen unit's `Category` ≠ the building's produced category, is **hard-rejected** (`TrainUnit` returns `false`, no ore spent, nothing queued) — no cross-category training via a crafted/stale index.
- **AC1.3** A failed prereq or supply check spends **no** ore (spend stays last).

### AC2 — Air production building + category (epic AC2, verbatim + made testable)

**Given** an Air production building placed and an Air-category unit defined in the faction **When** the player selects the Air building **Then** the Air unit appears as a trainable option and trains correctly (spawns with `CategoryOf == Air`) **And** no previously-trainable Melee/Ranged/Siege unit regresses.

- **AC2.1** The new `BuildingType` is **appended** to the enum (existing values 0–3 unchanged) and maps to the `"Air"` category in `CategoryForBuilding`; its id round-trips through `TechTreeChecker.BuildingTypeId`/`ParseBuildingType` and resolves a non-null `DisplayName`.
- **AC2.2** The Air building is **worker-buildable** (appears in the build palette, costs ore, honours its prerequisite) and is placeable from a scenario (the scenario string→enum parser **and** the `ScenarioValidator` allowlist accept it).
- **AC2.3** Both showcase factions can train their Air unit (alpha `griffin`, beta `wyvern`) end-to-end via the new building.

### AC3 — Determinism & zero regression (explicit; prevents "completion lies")

**Given** the change set **When** the Tier-1 golden gate runs **Then** all **10** golden checksums are **byte-identical**, `SimChecksum.AlgoVersion` stays **8**, `CanonicalModelHash` AlgoVersion stays **2**, and the known-state pin `0x983D39AE` is unchanged. **And** the AI-active golden (AI builds a Barracks and trains) is unchanged — the default/unchosen path (`chosenUnitIndex == -1`) resolves to the **same** unit `GetProductionUnit` returns today. **And** `ApplyUnitDefinitionGuardTest` still passes (no new per-unit SoA field is introduced).

### AC4 — UX conformance (command-card contract)

**Given** the production picker **When** it renders **Then** it follows the as-built HUD contract: one button per unit with name + cost + train-time (tabular-nums so labels don't jitter), per-unit disable with the existing `[need: <prereq>]` / `[need ore]` / `[supply full]` note, prereq-locked options **dimmed** (not hidden), a tooltip on every button (NFR-2), and the production card still shares the HUD region with the worker/ability cards without leaking buttons across them.

### AC5 — MP-deterministic training (D-1: training moves onto the lockstep wire)

**Given** two peers (or a live match and its replay) **When** a player trains a chosen unit **Then** the action rides the deterministic command stream as a new `UnitCommand.Train` through the shared `OrderApplier`, the ore/supply spend happens exactly **once at exec-tick** (never at click-time), and the same unit trains identically on every peer and in `.chmr` replay. **And** a player can train only at a building of their **own** faction (building-ownership guard replaces the entity guard for this command). **And** offline single-player still trains immediately (the `_lockstep == null` path). **And** no wire/replay format or version bump is introduced (rides the existing 11-byte `UnitOrder`; `ReplayRecorder.VERSION`/`ProtocolVersion` unchanged). The AI training path is unchanged (in-tick, already deterministic).

## Decisions

**Baked in (deterministic-rule / minimal-slice calls — applied, not re-asked):**

1. **Storage = reuse the existing `ProductionQueue` byte.** Store the chosen unit as `ProductionQueue[buildingId] = (byte)(unitsIndex + 1)` (0 stays "idle"). Its doc comment is literally "Entity archetype being trained." This adds **no new SoA array** and `ProductionQueue` is **not folded** into `SimChecksum` (only `Alive`/`Health`/`ConstructionTimer` are) → **no fold, no AlgoVersion bump, no golden re-baseline** (fold-timing rule: a divergent choice is still caught transitively when the differing unit spawns and its folded `Position`/`Health`/`Effective*` enter the entity hash — the same argument the codebase already uses for the unfolded `CategoryOf`/`TrainedCount`). Index is into the faction's **full `Units` list** (the same coordinate as `IndexOfUnit`/`MeshType`), so spawn resolves `Units[idx]` and reuses the mesh coordinate. (Spawn must **bounds-check** before indexing `Units`, and a reserved `255` sentinel preserves today's empty-category FALLBACK path — see Task 2.)
2. **Signature = `TrainUnit(int buildingId, ResourceStore resources, int chosenUnitIndex = -1)`.** `-1` = first-of-category (today's behavior) → the AI caller (`AiOpponentSystem.cs:107`) and any 2-arg call compile and behave **byte-identically**. `SpawnTrainedUnit` **reads the stored index** instead of re-deriving via `GetProductionUnit`.
3. **Guards = bounds-check + category-match.** A chosen index outside `[0, Units.Count)` or whose `def.Category != CategoryForBuilding(bType)` (case-insensitive) → `TrainUnit` returns `false` (the 1.12 faction-guard lesson — never train cross-category from a crafted/stale order; never throw inside the tick).
4. **Air building mapping = a hardcoded `CategoryForBuilding` switch arm** (`Aviary => "Air"`), matching the existing closed-enum pattern. (A fully data-driven `produces_category` JSON field is noted as a future cleanup below, **out of scope** — the `BuildingType` enum is already a closed code enum, so making only the mapping data-driven is a half-measure that belongs with a larger Creation-Suite building arc.)
5. **Selection is one-shot per click.** Each unit button queues that unit immediately; the single production slot already enforces one in-flight job (`TrainUnit` refuses if `ProductionQueue != 0`). No persisted "default unit," no multi-item queue (out of scope).

**Needs Alec's confirmation (surfaced after this file — recommended defaults are baked so the dev can start):**

- **D-1 (MP routing) — DECIDED (Alec, 2026-06-30): route training through the lockstep command stream in this story (Task 9 is CORE, not optional).** Today training is a direct presentation→sim call (`OnTrainBtnPressed` → `TrainUnit`), **not** on the lockstep wire (`OrderApplier` has no Train case) — a pre-existing gap shared with worker-build that desyncs human-vs-human MP. 2.8 closes it: a new `UnitCommand.Train = 11` carrying `{buildingId, chosenUnitIndex}` through the shared `OrderApplier` (the 2.4a `CastAbility=10` precedent), with the ore-spend moved to exec-tick (the UI keeps only a predictive grey-out — no double-spend). Per-unit production + Air training become **MP-safe now**. The AI path is unchanged (it trains in-tick, already deterministic); only the human command-card path routes onto the wire. **This builds the infrastructure, not the LAN test** — the physical two-machine LAN *verification* (FR-39) stays parked until a 2nd box exists. **Still NO fold / version bump** (see determinism notes): the command rides the existing 11-byte `UnitOrder` + the appended cmd byte (like `CastAbility`), and the chosen-unit state stays in the unfolded `ProductionQueue`.
- **D-2 (Air building identity / balance) — DECIDED (Alec, 2026-06-30): prerequisite `["siege_workshop"]`** (late / top-tier gate — air sits behind the full Barracks → Archery Range → Siege Workshop chain). Baked: enum `BuildingType.Aviary = 4`, JSON id `"aviary"`, ore cost ~200; display names alpha **"Bonded Aerie"** (Covenant no-"The" pattern, ties to *Greycrest, the Bonded*), beta **"The Wraithwing Brood"** (Court "The X" pattern, ties to *Envy Wraithwing*); `BuildingStore.Create` defaults ~350 HP / 12 s construction (mirrors Siege Workshop). Name/cost/HP remain easy to tweak during dev.

## Tasks / Subtasks

- [x] **Task 1 — Plural category lookup (sim data, no behavior change to existing callers)** (AC: 1)
  - [x] Add `FactionDefinition.GetUnitsByCategory(string category)` returning every unit in `Units` whose `Category` matches (case-insensitive) **in list order**, as `(int unitsIndex, UnitDefinition def)` pairs (or a parallel index list). Keep the existing singular `GetUnitByCategory` for the first-of-category default. Deterministic ascending-index iteration — no `Dictionary`/`HashSet`, no sort.
  - [x] Optionally add `BuildingSystem.GetProductionUnits(BuildingType, Faction)` so the command card stays `FactionDefinition`-free (it only holds `_buildSys` today).

- [x] **Task 2 — `TrainUnit` accepts a chosen unit; persist it; spawn it** (AC: 1, 1.1, 1.2, 1.3, 3)
  - [x] Change signature to `TrainUnit(int buildingId, ResourceStore resources, int chosenUnitIndex = -1)` (`BuildingSystem.cs:242`). `-1` → `GetProductionUnit(bType, faction)` (unchanged first-of-category). **Caller/timing note (Task 9):** `TrainUnit` is now invoked from the `OrderApplier.Train` case at exec-tick, not from the UI directly — its body (gates, spend, storage) is unchanged; only *when* and *from where* it runs changes. The AI still calls it directly in-tick.
  - [x] For `chosenUnitIndex >= 0`: resolve `def = GetFactionDef(faction)?.Units[chosenUnitIndex]` with a **bounds check** and a **category-match guard** (`def.Category` equals `CategoryForBuilding(bType)`, case-insensitive). On failure return `false` (no spend, no queue).
  - [x] Keep the gate **order and methods verbatim**: prereq `TechTreeChecker.AreMet(_buildings, faction, def.Prerequisites)` (`:255`) → supply `resources.HasSupply` (`:259-260`) → ore `resources.SpendOre` (`:262-263`). Ore spent **last**.
  - [x] **Persist the concrete chosen index, with an explicit empty-category fallback.** Resolve the final def first (the chosen unit, or `GetProductionUnit(...)` when `chosenUnitIndex == -1`). If a def resolved, store `ProductionQueue[buildingId] = (byte)(IndexOfUnit(def.Id) + 1)`. If the default path resolves to **`null`** (a producer whose category has zero units — a legal data-driven input that **today** trains a graceful FALLBACK unit), preserve that: store a reserved sentinel `PRODUCTION_FALLBACK = byte.MaxValue` (255), **not 0** — 0 must keep meaning "idle" or the "already-training" guard (`:249`) breaks and the player can re-queue and double-spend. `ProductionTimer` unchanged. (Index range is safe: the category guard bounds real indices to `< Units.Count` ≪ 254, so `IndexOfUnit+1` never collides with the 255 sentinel.)
  - [x] In `SpawnTrainedUnit` (`BuildingSystem.cs:141`), replace the re-derivation at `:144` with an **explicitly bounds-checked** read — do **not** rely on `?.`: the null-conditional guards a null `FactionDefinition`, **not** the `List<T>` indexer, which throws `ArgumentOutOfRangeException` on an out-of-range index inside the tick (violating "never throw in the tick"):

    ```csharp
    byte q = _buildings.ProductionQueue[buildingId];
    var fdef = GetFactionDef(faction);
    UnitDefinition? def = null;
    if (q != PRODUCTION_FALLBACK && q != 0 && fdef != null)
    {
        int idx = q - 1;
        if (idx >= 0 && idx < fdef.Units.Count) def = fdef.Units[idx];
    }
    // def == null (the 255 sentinel, OR a stale index after a SetFactionDef swap) → the existing FALLBACK branch (:177-189)
    ```
    Keep the `world.ApplyUnitDefinition(id, def)` route (`:174`) and `meshType` (`:192`, now `idx` when a def resolved) unchanged.
  - [x] Generalize the prereq helper for the picker: add a per-candidate `GetUnmetPrereq(int buildingId, int unitIndex)` (the existing `GetUnmetPrereq(int)` at `:408` re-derives first-of-category) so each unit button can show its own `[need: X]`.

- [x] **Task 3 — Per-unit production picker UI** (AC: 1, 4)
  - [x] In `CommandCardSystem.cs`, replace the single `Button _trainBtn` (`:38`) with a `Button[] _trainBtns` grid, cloning the **2.4b ability panel** (`BuildAbilityPanel`/`RefreshAbilityCard`, `:442+`) or the sibling **worker grid** (`_buildBtns`, `RefreshWorkerCard` `:381-430`): `Panel` + `MouseFilter=Stop`, **captured-loop-variable** lambda per button.
  - [x] In `RefreshCard` (`:156-241`): enumerate `GetUnitsByCategory` for the building's **actual faction** (`_buildings.FactionOf[bId]`, not the P1 default at `:209`); one button per unit with name + cost + train-time (**tabular-nums**); per-button `.Disabled` computed from the **same** sim predicate `TrainUnit` uses (prereq via the new per-unit helper, `CanAffordOre`, `HasSupply`) with the `[need: X]`/`[need ore]`/`[supply full]` note; **dim** prereq-locked options (≈0.6 modulate) rather than hiding; set a tooltip on each button (NFR-2). Show the in-flight unit's `Training… Xs` state (read `ProductionQueue[bId] != 0`).
  - [x] Replace `OnTrainBtnPressed()` (`:245-250`) with a per-button handler that **issues a `Train` command** for `{bId, chosenUnitsIndex}` via the new lockstep seam (Task 9) — online `EnqueueOrder` / offline `OrderApplier.Apply` — **not** a direct `TrainUnit` call (which would spend locally and desync online). See Task 9.
  - [x] Resize `_panel` (currently `420×140` at `_panel.Position = vpSize.Y - 150f`, `:263-264`) to fit the grid (≈`420×175`) **and** move its Position to `vpSize.Y - 185f` (`:264`) to match the worker/ability panels (they sit at `-185f` for their 175 px height); lay the train buttons below the HP/construction/supply labels (`10 + i*102, y≈74`, `98×70`, mirroring the worker grid). Preserve the one-card-at-a-time show/hide gating (`_panel`/`_workerPanel`/`_abilityPanel`).

- [x] **Task 4 — Add the Air `BuildingType` + category mapping (sim)** (AC: 2, 2.1, 3)
  - [x] `BuildingStore.cs:6-12`: **append** `Aviary = 4` (never renumber 0–3 — byte-serialized into replays/scenarios).
  - [x] `BuildingStore.Create` switch (`:102-133`): add an explicit `Aviary` case (Health/MaxHealth ≈350, `SupplyBonus = 0`, `ConstructionDuration` ≈12 s). **Note:** building HP/supply/construction come from **here**, not the JSON (`hp` in building JSON is vestigial) — do not rely on the JSON for these.
  - [x] `BuildingSystem.CategoryForBuilding` (`:213-220`): add `Aviary => "Air"`. **Critical** — the `_ => "Melee"` default would otherwise make the Air building silently train Melee. Update the stale doc-comment at `:16-20`.

- [x] **Task 5 — Wire the Air building through every `BuildingType` consumer (the regression surface — see the two-class touch-site map)** (AC: 2.1, 2.2, 2.3)
  - [x] **Class A (switches):** `TechTreeChecker.cs` — add `Aviary` to `BuildingTypeId` (`:49`, → `"aviary"`), `ParseBuildingType` (`:73`, `"aviary" =>`), `DisplayName` (`:82`). `ScenarioApplier.ParseBuildingType` (`:237-242`, **PascalCase** keys, separate from TechTreeChecker) — add `"Aviary" => BuildingType.Aviary` (default `CommandCenter` else silently mis-places). `CommandCardSystem.cs` — `WORKER_BUILD_TYPES` (`:65-71`), `canProduce` (`:191-194`), `typeName` (`:163-170`), `BuildingTypeName` (`:590`). `EntityPlacer.cs` — cycle switch (`:630-634`) + palette (`:823-826`). `MainScene.cs` — both display-name switches (`:677-680`, `:731-734`).
  - [x] **`ScenarioValidator` needs NO edit** — its allowlist is `Enum.GetNames(typeof(BuildingType))` (`:52`), so the Task-4 enum append covers it; just confirm an Aviary-placing scenario passes validation.
  - [x] **Class B (enum-indexed arrays — the CRASH/invisible sites; NOT caught by Tier-1 → verify in Task 8):** append a 5th element to **`NavObstacleManager.TYPE_SIZE`** (`:34-40`) and **`BuildingBridge.TYPE_FALLBACK`** (`:46-51`) using the **same** footprint box (their `:33` contract requires it, e.g. `new(5f, 3f, 7f)`) **and** bump **`BuildingBridge.TYPE_COUNT` 4 → 5** (`:52`); append a 5th element (~200, matching the JSON cost) to **`EntityPlacer.BUILDING_COSTS`** (`:43`). Without these, placing an Aviary throws `IndexOutOfRangeException` (nav every frame + editor place/delete) or renders nothing (bridge).
  - [x] **Widen the worker card for its new 5th build button:** `_workerPanel` is 420 px (`CommandCardSystem.cs:336`) and lays buttons at `10 + i*102` width 98, so the 5th overflows to x ≈ 516 (past the 420 border). Widen `_workerPanel` to ≥ 530 (or reduce the build-button pitch/width) so all 5 fit inside the panel.
  - [x] **Out of scope (note, don't build):** `AiOpponentSystem` build-order/producer-detection (`:154-166`, `:365`) and the private `LLMService` `BuildingType` copy (`:429`) — the AI keeps training first-of-category Melee/Ranged/Siege and will neither build nor train from the Aviary. Acceptable for 2.8.

- [x] **Task 6 — Author the Air producer in faction data** (AC: 2.2, 2.3, D-2)
  - [x] `alpha_faction.json` + `beta_faction.json`: add a 5th `buildings[]` entry (copy the `siege_workshop` entry's key-set): `id: "aviary"`, `category: "Structure"` (the **building** is a Structure; only the **unit** it produces is Air), `armor_type: "Fortified"`, `cost_ore` (~200), `prerequisites: ["siege_workshop"]` (D-2, Alec — top-tier gate), themed `display_name` (alpha "Bonded Aerie", beta "The Wraithwing Brood"), `mesh_path`. Do **not** reorder or remove any existing unit (legacy first-of-category consumers — `PrimaryUnit`, worker lookups, scenario spawns — depend on list order).
  - [x] Verify the air units keep `category: "Air"` exactly (alpha `griffin`, beta `wyvern`) so `CategoryForBuilding(Aviary) → "Air"` matches `GetUnitByCategory("Air")`.

- [x] **Task 7 — Tier-1 tests (xUnit, Godot-free)** (AC: 1.1, 1.2, 1.3, 2.1, 2.3, 3)
  - [x] Per-unit selection: `TrainUnit(b, res, idxOf2ndMelee)` → `SpawnTrainedUnit` spawns the 2nd Melee unit (assert def id / `CategoryOf`); `chosenUnitIndex = -1` spawns the same unit as today.
  - [x] Guards: out-of-range index → `false`; wrong-category index → `false`; both leave ore + queue untouched.
  - [x] Gate preservation: unmet prereq / over-supply / insufficient ore each → `false` with **no ore debited**.
  - [x] Air path: with an `Aviary` + an Air unit, `TrainUnit(aviary, res, airIdx)` trains it → spawns `CategoryOf == Air`.
  - [x] `TechTreeChecker`: `"aviary"` round-trips `BuildingTypeId`↔`ParseBuildingType`; `DisplayName(Aviary)` non-null.
  - [x] Determinism: run the golden gate — all 10 byte-identical; `AlgoVersion == 8`; known-state pin `0x983D39AE` unchanged; AI-active golden unchanged. (No new fold; `ApplyUnitDefinitionGuardTest` unaffected.)

- [x] **Task 8 — In-engine verification (`/godot-verify`)** (AC: 1, 2, 4)
  - [x] Select a Barracks → the card lists all **3** Melee units → pick the 2nd → that unit trains and spawns. Select an **Archery Range** → it now lists **both** Ranged units → pick the 2nd, the signature **caster** (alpha *Circle Savant* / beta *Cinder Cantor* — the exact unit 2.10 depends on) → it trains and spawns. Build an **Aviary** → train the griffin/wyvern → it spawns **and is visible** (proves the Class-B nav/bridge fixes — no crash, no invisible building). The only genuine single-unit producers are the **Siege Workshop** and the **Aviary** (one Siege / one Air unit per faction today). Confirm tooltips show, locked options dim, and **no `IndexOutOfRangeException`** appears in the log on Aviary place/delete. Capture screenshots + read runtime state.

- [x] **Task 9 — Route training through the lockstep command stream (CORE — D-1, Alec): MP-safe training** (AC: 5)
  - [x] Append `UnitCommand.Train = 11` (`EntityWorld.cs:12-27`, after `CastAbility=10` — frozen/append-only). Pack `{buildingId, chosenUnitIndex}` into a `UnitOrder`: `UnitId = buildingId` (ushort holds 0-63), `TargetX = Fixed.FromRaw(chosenUnitIndex)` (raw int; read back as the raw int, **never** `.ToFloat()`). Rides the existing 11-byte wire + `.chmr` format with **zero** format change (no `ReplayRecorder.VERSION` / `ProtocolVersion` bump — the 2.4a `CastAbility` precedent).
  - [x] Add a `Train` case to `OrderApplier.Apply` (`NetworkCommand.cs:115`). The top entity-ownership guard (`:120-122`, `IsAlive`/`FactionOf` on `UnitId`) is **wrong** for a building id → for `Train`, skip it and check **building** ownership: `buildings.Alive[bId] && !IsUnderConstruction(bId) && buildings.FactionOf[bId] == expectedFaction` (anti-cheat: only train at your own building). The case body calls the existing `BuildingSystem.TrainUnit(bId, resources, chosenUnitIndex)` so all cost/supply/prereq logic is reused verbatim.
  - [x] Widen `OrderApplier.Apply` with `BuildingSystem`/`BuildingStore`/`ResourceStore` (nullable — **null on headless/golden paths**, where `Train` no-ops since goldens never train via the wire) and update **all 3** call sites identically — `LockstepManager.ApplyOrders` (`:650-659`), `ReplayPlayer.ApplyOrders` (`:168-176`), and the offline UI apply — so live == replay == offline stay ONE switch (the AR-17 structural-parity invariant; updating only some re-opens the desync class the shared applier exists to kill).
  - [x] **Move the ore-spend into the deterministic apply.** `TrainUnit` now runs at exec-tick inside `OrderApplier` (reading the hashed `ResourceStore`), NOT at button-click. `CommandCardSystem.RefreshCard` keeps its predictive `canAfford`/`hasSupply`/prereq grey-out (local prediction only, allowed to differ harmlessly), but `OnTrainBtnPressed` **issues the command, does not spend** — **no double-spend** (a click-time spend + an apply-time spend = ore charged twice + a desync).
  - [x] Give `CommandCardSystem` a `SetLockstep(LockstepManager?)` setter (mirror `SelectionSystem.cs:147`), wired from the same `CameraPhase` site that calls `SetAbilityRegistry`. `OnTrainBtnPressed` issues **online** via `EnqueueOrder` (deferred to exec-tick) / **offline** via `OrderApplier.Apply` (apply now) — the exact dual-path of `SelectionSystem.IssueCastAbilityCommand` (`:677-692`). **Offline single-player must still train** (the `_lockstep == null` branch applies immediately, or the train button silently does nothing in skirmish).
  - [x] **AI path unchanged:** `AiOpponentSystem` keeps calling `TrainUnit(id, _resources)` directly in-tick (already deterministic) — do NOT route it through the wire.
  - [x] **Still NO fold / version bump:** the chosen-unit state stays in the unfolded `ProductionQueue`; the command stream guarantees identical inputs on every peer, so nothing new folds (AlgoVersion 8, 10 goldens byte-identical, `ReplayRecorder.VERSION` 2, `ProtocolVersion` 1 unchanged). Add a Tier-1 parity test: a `Train` `UnitOrder` applied via the offline `OrderApplier` path trains the chosen unit identically to a direct `TrainUnit` call, and spends ore exactly once.

### Review Findings

_Code review 2026-07-01 (`gds-code-review`, 3-layer adversarial: Blind Hunter · Edge Case Hunter · Acceptance Auditor, all at Opus 4.8; Edge Case Hunter re-run twice after two 0-tool-use no-ops). **PASS — no Critical/High/Medium survived triage.** All three layers independently verified the determinism/MP surface as SOUND: the wire `-1` round-trip (`TargetX` is a 4-byte raw `int`, `-1` survives → first-of-category), `idx+1`/`255` sentinel byte-packing (no collision with `0`-idle; real indices ≤7 ≪ 254), `Train`-branch-before-entity-guard ordering, single exec-tick ore spend (no double-spend), all Class-A switch arms + all three Class-B enum-indexed arrays (`NavObstacleManager.TYPE_SIZE`, `BuildingBridge.TYPE_FALLBACK`/`TYPE_COUNT`, `EntityPlacer.BUILDING_COSTS`) extended, and the unfolded-`ProductionQueue` checksum decision (goldens byte-identical, AlgoVersion 8). 1 decision, 1 patch, 3 deferred, 4 dismissed. Blind Hunter's lone Medium (stale train grid on a CommandCenter) was VERIFIED as a false positive — `RefreshCard:215` `if (isCC)` does not early-return; a CommandCenter has `canProduce == false` → hits the `else` → `HideTrainButtons()` at `:271`._

- [ ] [Review][Decision] Production picker hard-caps a category at 4 units (`MAX_TRAIN_OPTIONS = 4`) — flagged by ALL 3 layers. `BuildingSystem.GetProductionUnits` returns every unit in the category, but the fixed 4-slot `_trainBtns` grid renders only `options[0..3]`; a creator faction defining 5+ units in one category silently loses the 5th+ button (no crash, no determinism impact). Inert for 2.8 (every shipped alpha/beta category is ≤3 units, so all ACs + both factions are fully satisfied) but bites Chimera's creator-extensible identity. Options: (a) make the grid dynamic like the sibling worker grid (`_buildBtns = new Button[WORKER_BUILD_TYPES.Length]`) + handle HUD overflow (widen/wrap/scroll); (b) emit a content-validation warning when a category exceeds the cap; (c) accept + document the cap for the Creation-Suite arc. [godot/src/UI/CommandCardSystem.cs:42,233]

- [ ] [Review][Patch] AC4 tabular-nums not applied — the ticking `Training…  {t:F1}s` countdown label uses proportional figures, so the digit can horizontally jitter as it counts down (AC4 requires tabular-nums "so labels don't jitter"). Cosmetic only, no functional/determinism impact; AC4 was marked MET without this sub-requirement. [godot/src/UI/CommandCardSystem.cs:231]

- [x] [Review][Defer] Local player hardcoded to `Faction.Player1` in `IssueTrainCommand` (blind+edge) [godot/src/UI/CommandCardSystem.cs:~1109] — deferred, pre-existing project-wide convention (mirrors `SelectionSystem` select/move/cast); not a regression, unreachable while the local human is always P1; revisit when P2-local / >2-player assignment lands.
- [x] [Review][Defer] `ProductionQueueValue` clamps a stored index ≥254 to the `255` fallback sentinel → ore spent but the fallback unit spawns (blind) [godot/src/Economy/BuildingSystem.cs `ProductionQueueValue`] — deferred, accepted byte-reuse design boundary (Decision 1 assumes index ≪ 254; requires ≥255 units in one faction category to reach — absurd vs ~8-unit rosters).
- [x] [Review][Defer] `OrderApplier.Apply` `Train` no-ops silently when `BuildingSystem` is null — a determinism footgun if a future peer/replay is ever constructed without wiring `Buildings` (blind) [godot/src/Multiplayer/NetworkCommand.cs] — deferred, not reachable today (wiring is unconditional at `MatchLifecycleController` for both `LockstepManager` + `ReplayPlayer`; null is intentional only on headless/golden paths that never train via the wire); track as a fail-loud hardening idea.

_Dismissed as noise/false-positive (4): stale train grid on a CommandCenter (verified false positive — `if (isCC)` doesn't early-return, `else` hides the grid); Aviary "inert" (false positive — both factions define an Air unit: alpha `griffin`, beta `wyvern`); `HideTrainButtons` doesn't reset `_trainUnitIndices` (no reachable exploit — hidden Godot buttons emit no `Pressed`, and `RefreshCard` rewrites every index before showing a button); `GetProductionUnits` 1-arg default `Faction.Player1` (defensive smell only — `RefreshCard` passes the building's actual faction; no non-P1 1-arg caller exists)._

## Dev Notes

### Current state — precise, source-verified

**Production (sim).** A producer trains exactly one unit, resolved **twice** from category, never from a stored choice:
- `BuildingSystem.CategoryForBuilding` (`BuildingSystem.cs:213-220`): `Barracks→Melee, ArcheryRange→Ranged, SiegeWorkshop→Siege, CommandCenter→Worker, _→Melee`. **No Air; the default is `"Melee"` (a trap for a new type).**
- `GetProductionUnit` (`:229-234`) → `GetFactionDef(faction).GetUnitByCategory(CategoryForBuilding(type))` → **first** match (`FactionDefinition.cs:63-69`). Returns `null` for `CommandCenter`.
- `TrainUnit(buildingId, resources)` (`:242-269`): guards (alive `:245`, not-under-construction `:246`, not-CommandCenter `:248`, `ProductionQueue != 0` "already training" `:249`); resolves first-of-category def; checks **prereq `:255` → supply `:259-260` → ore `:262-263`**; on success sets `ProductionQueue[id] = 1` (a 0/1 **flag**) + `ProductionTimer`.
- `TickProduction` (`:120-139`, ascending-id) counts the timer down; on expiry `SpawnTrainedUnit` **re-derives** the unit via `GetProductionUnit` at `:144` (it never reads `ProductionQueue`), then routes the def through `world.ApplyUnitDefinition` (`:174`, the single A2 mapper — already correct) and tags `MeshType` via `IndexOfUnit` (`:192`).
- The **other** `TrainUnit` caller is the AI (`AiOpponentSystem.cs:104-108`), in-tick and deterministic.

**Command card (presentation).** `CommandCardSystem.cs` is built entirely in code (no `.tscn`). For a selected producer, `RefreshCard` (`:156-241`) shows the **single** `_trainBtn` (`:38`) for the first-of-category unit; `OnTrainBtnPressed` (`:245-250`) calls `_buildSys.TrainUnit(bId, _resources)` **directly**. Reusable in-file templates: the worker build grid `_buildBtns[]` (`:381-430`) and the 2.4b ability grid `_abilityBtns[]` (`:442-541`) — both are `Button[]` grids with per-button disable + reason-note + captured-loop-var lambdas. New deps are injected via a setter (`SetAbilityRegistry` `:99`, wired in `CameraPhase.cs:50`), not by widening `Initialize`.

**Air today.** `BuildingType` (`BuildingStore.cs:6-12`) has only `CommandCenter=0, Barracks=1, ArcheryRange=2, SiegeWorkshop=3`. `UnitCategory` (`UnitCategory.cs:14-22`) **already** includes `Air=4` and is **not** folded into `SimChecksum` (presentation-read, like `MeshType`). Both factions define one Air unit; **no building produces Air**, so air units are reachable only by scenario placement.

**Determinism baseline (verified).** `SimChecksum.Compute` folds only `buildings.Alive`/`Health`/`ConstructionTimer` (`SimChecksum.cs:181-188`) — `ProductionQueue`/`ProductionTimer`/`TrainedCount`/`RallyPoint` are **not** folded. `AlgoVersion = 8`; `CanonicalModelHash` AlgoVersion `= 2` (hashes `ScenarioBuilding.Type` as a **string** by name, so appending an enum value shifts no existing scenario hash); known-state pin `0x983D39AE`; `VersionStampConsistencyTests` expects 8/2/1/2. `SimChecksumCoverageGuardTest` reflects only `ResourceStore` — **no** `BuildingStore` coverage guard, so reusing/adding a `BuildingStore` field trips nothing. Predecessor 2.7 left **Tier-1 501 pass / 1 skip / 0 fail, 10 goldens**.

### The Air-building touch-site map — TWO classes (miss one = CRASH or silent degradation)

`BuildingType` is consumed two ways and **both** must be extended. A `switch`-grep finds only Class A; **Class B — arrays indexed by `(int)BuildingType` — is where the dangerous misses hide**: two of them throw `IndexOutOfRangeException` the instant an Aviary is placed, and all three live in `using Godot` files the **Godot-free Tier-1 gate cannot catch** (the goldens stay green while the feature crashes). **The Class-B sites are only provable in Task 8 (`/godot-verify`).**

**Class A — `switch`/`=>` sites (a missed `default` silently degrades; does not crash):**

| Site | File:line | Needs `Aviary` because… | Default if missed |
|---|---|---|---|
| enum (**append =4**) | `BuildingStore.cs:6` | the type itself | — |
| `Create` stats | `BuildingStore.cs:102` | HP/supply/construction | 200 HP / 10 s |
| `CategoryForBuilding` | `BuildingSystem.cs:213` | **maps to `"Air"`** | trains **Melee** |
| `BuildingTypeId` | `TechTreeChecker.cs:49` | enum→json id | `""` → no cost/prereq |
| `ParseBuildingType` | `TechTreeChecker.cs:73` | json id→enum (prereq) | `null` → prereq no-ops |
| `DisplayName` | `TechTreeChecker.cs:82` | `[need: X]` text | `null` |
| `ParseBuildingType` (scenario) | `ScenarioApplier.cs:237` | scenario placement | mis-places as `CommandCenter` |
| `WORKER_BUILD_TYPES` | `CommandCardSystem.cs:65` | worker can build | unbuildable |
| `canProduce` | `CommandCardSystem.cs:191` | train card shows | no train UI |
| `typeName` / `BuildingTypeName` | `CommandCardSystem.cs:163, 590` | card label | "Building" |
| display-name switches | `MainScene.cs:677, 731` | HUD labels | fallthrough |
| editor cycle + palette | `EntityPlacer.cs:630, 823` | editor placement | absent from editor |

**Class B — arrays indexed by `(int)BuildingType` (a missed entry CRASHES or renders invisible — NOT caught by Tier-1):**

| Array | File:line | Indexed at | If missed |
|---|---|---|---|
| `NavObstacleManager.TYPE_SIZE` (4 elems) | `NavObstacleManager.cs:34-40` | `:138` (`AddObstacle` ← `_Process:77`, every alive building each frame) | **CRASH** `IndexOutOfRangeException` on **any** Aviary placement (a building is `Alive` the instant it's placed, even under construction) |
| `EntityPlacer.BUILDING_COSTS` (`{150,100,120,200}`) | `EntityPlacer.cs:43` | `:556`, `:573` (place + undo), `:1019-1020` (delete) | **CRASH** on editor Aviary place/delete (Task 5 enables this path) |
| `BuildingBridge.TYPE_COUNT=4` + `TYPE_FALLBACK` (4 elems) + `[TYPE_COUNT,2]` arrays | `BuildingBridge.cs:46-52, 75-78` | build `:83/:90`, skip-guards `:197/:217/:268` | **Invisible**: Aviary renders no mesh + no construction bar (player builds it, sees empty ground, units appear from nowhere) |

`ScenarioValidator` is **NOT** a touch-site: its building-type allowlist is auto-derived `Enum.GetNames(typeof(BuildingType))` (`ScenarioValidator.cs:52`), so the Task-4 enum append covers it automatically — no edit, and it can never reject the Aviary. `NavObstacleManager.TYPE_SIZE` and `BuildingBridge.TYPE_FALLBACK` carry a "must match exactly" contract (`NavObstacleManager.cs:33`) — give the Aviary the **same** footprint box in both (e.g. `new(5f, 3f, 7f)`). `MeshLoader`'s box-fallback renders a placeholder when the aviary GLB is missing, so no new art is required.

### Determinism notes (the whole story is presentation + wiring + data + one unfolded byte)

- **No fold, no AlgoVersion bump, no golden re-baseline** (see Decision 1). The chosen-unit index lives in the **unfolded** `ProductionQueue`; a hypothetical divergence is caught transitively when the differing unit spawns into the folded entity loop and the ore-spend hits the folded resource loop. Verify the 10 goldens stay byte-identical after the change (they must).
- Carry the chosen unit as the **ascending `Units`-list index** (`IndexOfUnit`/`MeshType` coordinate) — never a float, string-hash, `Dictionary`/`HashSet` enumeration, or transient UI button index. Enumerate categories in `Units` list order (deterministic JSON order).
- `TickProduction` stays ascending-id; the spawn already flows through `ApplyUnitDefinition` (A2). **No new per-unit SoA field** → `ApplyUnitDefinitionGuardTest` is untouched. (`ProductionQueue` is a per-**building** field; if Option B — a dedicated `int[]` — were ever chosen instead, it must be reset in `BuildingStore.Create` next to `ProductionQueue[id]=0` at `:98` for recycle-safety.)
- **MP routing (D-1, DECIDED — Task 9):** training now rides the lockstep command stream (new `UnitCommand.Train=11` through the shared `OrderApplier`, spend at exec-tick), closing the pre-existing human-vs-human gap. This adds **no** fold and **no** version bump — the command rides the existing 11-byte `UnitOrder` (like `CastAbility`), and the chosen-unit state stays in the unfolded `ProductionQueue`; the command stream guarantees identical inputs, and `SimChecksum` still HALTs any residual desync. Physical 2-machine LAN verification (FR-39) stays parked.

### Previous-story intelligence (2.7, done 2026-06-30)

- **A2 single-mapper rule held** in 2.7: per-unit fields flow through `ApplyUnitDefinition`, null-reset on `Create()`/recycle, guarded by `ApplyUnitDefinitionGuardTest`. 2.8 inherits this for free — the **chosen** def passes through the same mapper at `BuildingSystem.cs:174`, so the air/sibling unit gets its `Category`/abilities/`CombatFeedback`/`collision_radius` correctly with zero new spawn plumbing. See [[chimera-content-validator-bound-behavioral-params]] context for the spawn-completeness discipline.
- **Dual-path content DTO** ([[chimera-dual-path-content-dto-constraint]]) does **not** apply here — the Aviary building entry rides only the lenient faction loader (`FactionDefinition.JsonOptions`), not the strict ability path; no new DTO is introduced.
- 2.7 asserted its determinism fence via `git status` (SimChecksum/CanonicalModelHash/SystemOrderTest/goldens untouched). 2.8 should do the same.

### Reuse — do NOT reinvent

- **2.4b ability card** (`BuildAbilityPanel`/`RefreshAbilityCard`) and the **worker build grid** (`BuildWorkerPanel`/`RefreshWorkerCard`) are the two in-file `Button[]`-grid templates — clone one, swap ability/build affordability for train cost/prereq/supply.
- `GetUnitByCategory` extends naturally to a plural enumerator over the existing `Units` list (deterministic order; no new ordering logic).
- `TrainUnit` already enforces prereq/supply/cost and is type-agnostic except the explicit `CommandCenter` block — a new producer reuses it for free once `CategoryForBuilding` maps it.
- Buildings reuse `UnitDefinition` (no `BuildingDefinition` class) — author the Aviary exactly like the `siege_workshop` entry.
- `2.4a CastAbility` + `OrderApplier` + `Fixed.FromRaw` raw-int packing is the **core template for Task 9** — `UnitCommand.Train` mirrors `CastAbility`'s shape (append the enum value, pack two small ints into `TargetX`/`TargetZ`, validate owner at the issue seam, online-enqueue vs offline-apply). It rode the existing wire with no version bump — replicate that exactly.

### Regression risks (must not break)

- Keep the 2-arg/`chosenUnitIndex = -1` path **byte-identical** to today (AI-active golden trains first-of-category) — `TrainUnit`'s signature change must not break `AiOpponentSystem.cs:107`.
- Preserve `TrainUnit`'s gate **order** and the spend-last invariant; selection only changes **which def** feeds the gates.
- `ProductionQueue` semantics: `0` = idle, non-zero = busy must hold for every reader (`:126`, `:135`, `:249`, `CommandCardSystem.cs:215`).
- Append the enum value (`=4`); never renumber.
- Don't set the Aviary's **own** `category` to `"Air"` — it's a `"Structure"`; only the unit it produces is Air.
- **Known limitation to document, not fix:** beta `wyvern` has `cost_crystal: 100`, but crystal-spend isn't wired until **2.9b**, so it trains at ore-only (200) when 2.8 makes it buildable — consistent with today's "crystal not charged for production" behavior; 2.9b closes it.

### Testing standards

- **Tier-1** = xUnit, Godot-free (`ProjectChimera.Sim.Tests`) — golden-checksum + the unit/negative tests in Task 7. All sim logic must be testable without Godot.
- **Tier-2 / in-engine** = `/godot-verify` (Task 8) for the command-card picker + air production end-to-end.
- Prove each new gate has **teeth** (Epic-1 retro action item): assert the category-guard actually rejects a wrong-category index, and that a missed `CategoryForBuilding` arm would train the wrong unit (the test should fail if the arm is removed).

### Project Structure Notes

- Sim changes (Godot-free, `Fixed`/int only, ascending-id): `godot/src/Economy/BuildingSystem.cs`, `godot/src/Core/BuildingStore.cs`, `godot/src/Core/TechTreeChecker.cs`, `godot/src/Core/Definitions/FactionDefinition.cs`, `godot/src/Core/Sim/ScenarioApplier.cs`. (`ScenarioValidator.cs` needs **no** edit — its allowlist auto-derives from the enum.) Presentation/nav changes (`using Godot`, **Tier-1 can't cover** → Task 8): `godot/src/UI/CommandCardSystem.cs`, `godot/src/UI/EntityPlacer.cs`, `godot/src/UI/NavObstacleManager.cs`, `godot/src/UI/BuildingBridge.cs`, `godot/src/Core/MainScene.cs`. Data: `godot/resources/data/factions/{alpha,beta}_faction.json`. Matches the established folder map; no new directories.
- `BuildingType` has **two** consumer classes: hardcoded `switch`/`=>` sites (degrade on a missed `default`) **and** arrays indexed by `(int)BuildingType` (crash/invisible on a missed entry). A `switch`-grep finds only the first — the dev must extend **both** (see the touch-site map). This enum-threaded-through-N-sites pattern is a known smell (a closed code enum gated by hardcoded switches + parallel arrays) but is the **existing** pattern; 2.8 follows it. A future data-driven building system (author building types + `produces_category` as JSON) is a larger Creation-Suite arc — explicitly **out of scope** here.

### Project Context Rules (from `_bmad-output/project-context.md`)

- **Sim/Presentation boundary is sacred:** `BuildingSystem`/`BuildingStore`/`FactionDefinition`/`TechTreeChecker` are sim — no `using Godot;`, no `float` for gameplay state, no `Vector3`. `CommandCardSystem`/`EntityPlacer` are presentation — they **read** sim arrays and **issue intents**, never mutate sim SoA directly (2.4b's "sacred boundary" lesson).
- **Determinism:** all gameplay math in `Fixed`; ascending-id iteration; no `float`/`Mathf`/`System.Random`/wall-clock/`Dictionary` enumeration in sim. `Fixed.FromFloat` only at authoring/load (the existing `CostOre`/`TrainTime` quantization is load-time, not per-tick).
- **Single def→SoA mapper (A2):** any per-unit field flows through `EntityWorld.ApplyUnitDefinition` — never hand-copied in a spawn path. (2.8 adds no per-unit field; the per-building chosen-index is set by `TrainUnit`, not the mapper.)
- **Data-driven:** building cost/prerequisites/display/mesh and unit rosters are JSON in `resources/data/`; no hardcoded balance in code paths a creator can't reach. (HP/supply/construction currently live in `BuildingStore.Create` — a known data-driven gap; match the existing pattern, don't expand scope to fix it.)
- **Godot C# gotchas:** classes inheriting Godot types must be `partial`; `[Export]` floats are presentation-only; `GD.Print()` not `Console.WriteLine()`; `#nullable enable` per file. The `.sln` is `godot.sln`.

### References

- Story spec + ACs: `_bmad-output/planning-artifacts/epics.md#Story-2.8` (L984-998); downstream deps L1012 (2.9a), L1044 (2.10).
- FR-11 / AR-8: `epics.md` L78 / L187 (AR-8 tie is nominal — 2.8 surfaces the ability-bearing roster, adds no effect-graph code).
- Design intent + Air roadmap: `_bmad-output/fma-faction-design.md` L92 ("Air has no producer at all"), L122/L150 (griffin/wyvern "no producer"), **L167** (the exact Air checklist), L183-184 (the two open product Qs this story resolves).
- UX contract: `_bmad-output/planning-artifacts/ux-designs/ux-Project_Chimera-2026-06-20/EXPERIENCE.md` L50/L62/L65/L85 (HUD hierarchy, tooltip mandate NFR-2, single-select accent ring + dim-locked, hotkey glyphs); `DESIGN.md:152` (tabular-nums); UX-DR61 (P1-only selection), UX-DR71 (non-diegetic HUD).
- Reuse templates: `_bmad-output/implementation-artifacts/2-4b-*.md` (ability command-card panel; "mirror the sim predicate" + "sacred boundary"), `2-4a-*.md` (CastAbility + OrderApplier raw-int packing — the **core template for Task 9**'s `UnitCommand.Train`), `2-7-*.md` (A2 single-mapper + green baseline 501/10-goldens/v8).
- Source (verified this session): `BuildingSystem.cs:141-269` (TrainUnit/SpawnTrainedUnit/GetProductionUnit/CategoryForBuilding), `BuildingStore.cs:6-12,48,87-138` (enum/ProductionQueue/Create), `FactionDefinition.cs:26,55-69` (Units/IndexOfUnit/GetUnitByCategory), `UnitDefinition.cs:22,255-263` (Category/ParsedCategory), `TechTreeChecker.cs:49-89`, `SimChecksum.cs:181-188`, `CommandCardSystem.cs:38,65,156-250,442-541,590`, `NetworkCommand.cs` OrderApplier (no Train case), `alpha_faction.json`/`beta_faction.json` (rosters; griffin/wyvern Air; no aviary).
- Class-B enum-indexed arrays (verified; the crash/invisible sites): `NavObstacleManager.cs:33-40,77,138` (`TYPE_SIZE[4]`, wired `NavigationPhase.cs:87-89`), `EntityPlacer.cs:43,556,573,1019-1020` (`BUILDING_COSTS[4]`), `BuildingBridge.cs:46-52,75-78,83,90,197,217,268` (`TYPE_COUNT=4`/`TYPE_FALLBACK`, wired `FactionVisualsPhase.cs:37-39`). All `using Godot` → invisible to Tier-1; `MeshLoader` box-fallback renders the placeholder Aviary mesh.

## Dev Agent Record

### Agent Model Used

claude-opus-4-8 (Opus 4.8), via `gds-dev-story [ultracode]` (6-agent parallel recon workflow to verify every spec anchor against live source + pin the green baseline, then sequential red-green-refactor TDD).

### Debug Log References

- **`Fixed` 16.16 overflow (test-authoring catch).** `Fixed.FromInt(100000)` OVERFLOWS — the integer part of the 16.16 fixed-point type caps near ±32767. The Tier-1 harness initially seeded ore with `FromInt(100000)`; the 5 spawn-and-inspect tests failed because `SpendOre` saw a garbage/negative balance (the reject tests passed — they short-circuit before the ore gate). Fixed by seeding `Fixed.FromInt(10000)`. Durable: never `FromInt` a value ≥ ~32000 anywhere in sim/test.
- **Determinism fence (AC3) — re-verified green.** Targeted golden/version/known-state run: all **10** goldens byte-identical, `SimChecksum.AlgoVersion == 8`, `CanonicalModelHash` AlgoVersion `== 2`, known-state pin `0x983D39AE` unchanged, AI-active golden unchanged, `ApplyUnitDefinitionGuardTest` passes (no new per-unit SoA field). Confirms Decision 1 (reuse the unfolded `ProductionQueue` → no fold, no re-baseline).
- **In-engine verify (Task 8) method.** godot-mcp has **no absolute-cursor click** — drove selection by constructing an `InputEventMouseButton` (absolute `position` from `Camera3D.unproject_position`) and delivering it via `root.push_input`. The RTS camera was parked off-map and edge-scroll-drifting; framed it deterministically with `RtsCameraController.PanTo(Vector3)` after setting `EdgeScrollEnabled=false` (stale `_mousePos` at (0,0) sat inside the edge margin → continuous pan). Read the real command-card `Button` nodes (text/disabled/modulate/tooltip) + unit-render `MultiMeshInstance3D.instance_count` for spawn proof; `get_stack_trace` as the crash detector. See [[godot-mcp-ui-verify-via-signals]].

### Completion Notes List

**Outcome: all 9 tasks complete; all 5 ACs verified (Tier-1 + in-engine). Ready for code review.**

- **AC1 (per-unit selection) — MET.** `FactionDefinition.GetUnitsByCategory` (plural, ascending list order) + `BuildingSystem.GetProductionUnits` feed a per-unit picker; `TrainUnit(buildingId, resources, chosenUnitIndex = -1)` resolves `Units[chosenUnitIndex]` with a bounds + category-match guard (returns `false`, no spend, on out-of-range or cross-category — AC1.2), gate order preserved prereq→supply→**ore-last** (AC1.3). Chosen index persisted as `ProductionQueue[bId] = idx+1`; `SpawnTrainedUnit` reads it bounds-checked (never throws in-tick). In-engine: trained the **Scout** (slot 1, not the default) at a Barracks → it queued and spawned.
- **AC2 (Air building + category) — MET.** `BuildingType.Aviary = 4` appended (0–3 unchanged); `CategoryForBuilding(Aviary) => "Air"`; `BuildingStore.Create` gives it 350 HP / 12 s. In-engine: Aviary selected → exactly **1 Air button (Greycrest griffin)**; trained → spawned and rendered (unit MultiMesh total 11→12), **zero `IndexOutOfRangeException`** across place/render/nav/select/train — proves the Class-B `NavObstacleManager.TYPE_SIZE[4]` + `BuildingBridge.TYPE_FALLBACK[4]`/`TYPE_COUNT=5` + `EntityPlacer.BUILDING_COSTS[4]` fixes.
- **AC3 (determinism) — MET.** No fold, no version bump (see Debug Log). Reused the unfolded `ProductionQueue` byte; a divergent choice is still caught transitively when the differing unit's folded `Position`/`Health`/`Effective*` enter the entity hash.
- **AC4 (UX contract) — MET (in-engine).** Barracks → 3 per-unit Melee buttons in JSON order (Covenant Transmuter · Quicksilver Runner · Bulwark Adept); Archery Range → 2 Ranged (Pierce Marksman · Circle Savant); each button carries name + cost + train-time and a tooltip (NFR-2); a prereq-locked option renders **dimmed (0.6α) + disabled + `[need: …]`**, not hidden (demonstrated live by temporarily gating heavy_infantry behind an unbuilt siege_workshop — scaffolding since reverted). One card shows at a time; no button leakage across worker/ability cards.
- **AC5 (MP-deterministic training) — MET (Tier-1 + structural).** New `UnitCommand.Train = 11` rides the existing 11-byte `UnitOrder` (buildingId in `UnitId`; chosenUnitIndex as `Fixed.FromRaw` in `TargetX`, read back as raw int) — **no wire/replay/version bump** (`ReplayRecorder.VERSION` 2, `PROTOCOL_VERSION` 1 unchanged). **Design refinement:** encapsulated the building-ownership guard + spend in a new `BuildingSystem.TrainUnitCommand(bId, expectedFaction, idx)` (BuildingSystem already owns its `BuildingStore` + `ResourceStore`), so `OrderApplier.Apply` needed only **one** new optional `BuildingSystem? buildings = null` parameter instead of three — every existing caller compiles unchanged. The `Train` branch runs **before** the entity-ownership guard (a building id is not an entity id) and checks own-faction building ownership. All 3 apply sites (`LockstepManager` / `ReplayPlayer` / offline `CommandCardSystem`) pass the same instance → one switch, structural replay-vs-live-vs-offline parity (AR-17). Spend moved to exec-tick (UI keeps only a predictive grey-out — no double-spend); offline (`_lockstep == null`) still applies immediately. 5 dedicated Tier-1 parity tests (apply-vs-direct identical, spends-once, wrong-faction-rejected, null-buildings-noop, no-CommandState-clobber). AI training path untouched (in-tick, already deterministic).
- **Tier-1: 521 pass / 1 skip / 0 fail** (501 predecessor baseline + **20** new `ProductionSelectionTests`); full `godot.csproj` builds with 0 errors.
- **Out of scope (spec'd in Task 5 "note, don't build" — correctly left untouched):** `AiOpponentSystem` build-order/producer detection and the private `LLMService` `BuildingType` shadow copy (`LLMService.cs:429`). The AI still trains first-of-category Melee/Ranged/Siege and neither builds nor trains from the Aviary — acceptable for 2.8; the LLMService enum copy did not need the `Aviary` member because no AC exercises it. Noted here so review doesn't flag it as a miss.
- **Known limitation (documented, not fixed — per story Regression Risks):** beta `wyvern` has `cost_crystal: 100`, but crystal-spend for production isn't wired until 2.9b, so it trains at ore-only (200) — consistent with today's behavior.
- **Verification scaffolding reverted:** the in-engine test used a temporarily-edited `alpha_map_01.json` (3 pre-built P1 producers + higher ore) and `alpha_faction.json` (temp heavy_infantry prereq for the dim demo). Both restored from backup; `git diff` confirms only the legitimate Task-6 aviary building entry remains in the faction JSON, and the scenario file matches baseline.

### File List

**Sim (Godot-free, `Fixed`/int, ascending-id):**
- `godot/src/Core/Definitions/FactionDefinition.cs` — `GetUnitsByCategory` (plural, list-order) (Task 1)
- `godot/src/Economy/BuildingSystem.cs` — `GetProductionUnits`, `TrainUnit(…, chosenUnitIndex=-1)` + guards, `ProductionQueueValue`, `SpawnTrainedUnit` reads stored index, `GetUnmetPrereq(bId, unitIndex)`, `CategoryForBuilding(Aviary)`, `TrainUnitCommand` (Tasks 1,2,4,9)
- `godot/src/Core/BuildingStore.cs` — `BuildingType.Aviary = 4` + `Create` case (Task 4)
- `godot/src/Core/EntityWorld.cs` — `UnitCommand.Train = 11` (Task 9)
- `godot/src/Core/TechTreeChecker.cs` — `BuildingTypeId`/`ParseBuildingType`/`DisplayName` Aviary (Task 5)
- `godot/src/Core/Sim/ScenarioApplier.cs` — PascalCase `"Aviary"` scenario parse (Task 5)

**Multiplayer (command-stream routing, Task 9):**
- `godot/src/Multiplayer/NetworkCommand.cs` — `OrderApplier.Apply` Train branch + optional `BuildingSystem?` param
- `godot/src/Multiplayer/LockstepManager.cs` — `Buildings` field + pass-through
- `godot/src/Multiplayer/ReplayPlayer.cs` — `Buildings` field + pass-through

**Presentation / nav (`using Godot` — Tier-1 can't cover, verified in Task 8):**
- `godot/src/UI/CommandCardSystem.cs` — per-unit `_trainBtns` grid + `RefreshCard` picker + `SetLockstep` + `IssueTrainCommand` (Tasks 3,5,9)
- `godot/src/UI/NavObstacleManager.cs` — `TYPE_SIZE[4]` Aviary footprint (Class B) (Task 5)
- `godot/src/UI/BuildingBridge.cs` — `TYPE_FALLBACK[4]` + `TYPE_COUNT 4→5` (Class B) (Task 5)
- `godot/src/UI/EntityPlacer.cs` — `BUILDING_COSTS[4]` + cycle/palette (Class B) (Task 5)
- `godot/src/Core/MainScene.cs` — both HUD display-name switches (Task 5)
- `godot/src/Core/Bootstrap/Phases/MatchLifecycleController.cs` — wire `CommandCard.SetLockstep` + `Lockstep.Buildings` + `ReplayPlayer.Buildings` (Task 9)

**Data (Task 6):**
- `godot/resources/data/factions/alpha_faction.json` — `aviary` building (Bonded Aerie)
- `godot/resources/data/factions/beta_faction.json` — `aviary` building (The Wraithwing Brood)

**Tests (NEW, Task 7):**
- `godot/ProjectChimera.Sim.Tests/Economy/ProductionSelectionTests.cs` — 20 tests (per-unit selection, guards, gates, Aviary category, TechTreeChecker round-trip, 5 OrderApplier Train parity tests)

## Change Log

| Date | Version | Description | Author |
|---|---|---|---|
| 2026-07-01 | 0.1 | Implemented all 9 tasks (per-unit production picker, Air building/category, lockstep-routed training). Tier-1 521 pass / 1 skip / 0 fail (+20); 10 goldens byte-identical (AlgoVersion 8, no fold); in-engine `/godot-verify` PASS (per-unit picker + dim-locked + Air production + spawns, zero `IndexOutOfRangeException`). Status → review. | Alec (Opus 4.8 dev) |
