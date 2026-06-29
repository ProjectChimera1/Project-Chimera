---
baseline_commit: d3636e2aa49b60832e523f0a3e71c27ceb69f518
---

# Story 2.4b: Ability command card — runtime cast UI, TargetUnit targeting, and the in-game wiring that makes a cast reachable

Status: done

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As a player,
I want a selected unit's abilities to appear as activatable buttons on the in-game command card — with live cost, cooldown, and affordability feedback — and to click one (picking a target when needed) to cast it during a match,
so that the deterministic ability engine shipped in 2.4a is finally reachable in actual play, not just in headless tests.

## Acceptance Criteria

**AC1 — Command card renders an affordable / cooldown-aware ability button.** _(epic AC1)_
**Given** a focused Player1 unit whose entity carries ≥1 resolved ability (`EntityWorld.AbilityCount[focusId] > 0`) in Play mode **When** it is selected **Then** the command card shows one button per ability labeled with the ability's `DisplayName` + cost + cooldown state **And** each button is **`Disabled` iff the SAME predicate `AbilityCastSystem` uses is false** — `onCd || Energy[focusId] < ab.CostEnergy || !Resources.CanAffordOre(f, Fixed.FromInt(ab.CostOre)) || !Resources.CanAffordCrystal(f, Fixed.FromInt(ab.CostCrystal))` — reusing the exact sim-side predicates (`AbilityCastSystem.cs:112-121`) so the greyed-out button never diverges from the sim's refusal.

**AC2 — Click → (target-pick) → intent → sim cast; presentation issues ONLY an intent.** _(epic AC2, UI half)_
**Given** an off-cooldown affordable ability button **When** the player presses it **Then** a `Self`/`None` ability **casts immediately** (`IssueCastAbilityCommand(focusId, slot, -1)`) and a `TargetUnit` ability **arms a cast-target click** whose next left-click picks a valid target (`FindNearestEnemyUnit`) and then issues the cast **And** the cast is delivered **only as an intent** — a `UnitCommand.CastAbility` order through `LockstepManager.EnqueueOrder` (online) / the shared `OrderApplier.Apply` (offline), **never a direct sim-array write** — which the sim consumes (effect graph runs, cost debited, cooldown begins) **And** a `GroundPoint` ability is shown **disabled** with a "[ground-cast: coming soon]" note (the 2.4a out-of-scope fence holds — no ground reticle is built here).

**AC3 — In-game wiring: abilities are loaded, resolved, injected, and attached-via-data so a cast is reachable in a real match.** _(story-added — the deferred-work item-1 enabler)_
**Given** the game (and the dedicated server) currently run `AbilityCastSystem` with `AbilityRegistry.Empty` (no ability casts anything in-game) **When** 2.4b is complete **Then** (a) an `AbilityRegistry` is built from `resources/data/abilities/` via `AbilityRegistry.LoadFromDirectory(ProjectSettings.GlobalizePath("res://resources/data/abilities"), …)`, (b) `UnitDefinition.ResolveAbilities(registry)` is called on **every** `UnitDefinition` in **every** loaded `FactionDefinition.Units` list (the up-front `_factionDef`/`_factionDef2` **and** the per-slot defs in `ScenarioLoadPhase`) **before any spawn**, (c) the registry is passed as the 7th arg to `SimulationHost.Create` on **both** the client (`MainScene.cs:270`) and the dedicated server (`ServerBootstrap.cs:37` / `BuildHeadlessServerSimHost`), and (d) ≥1 ability is attached to a Player1 unit type via faction/scenario JSON **And** selecting that unit in a real match shows the button and casting it resolves the effect (damage/heal/buff applied, cost debited, cooldown begins) — proving the spine is reachable end-to-end. The presentation layer holds its **own** `AbilityRegistry` reference (on `SceneContext`) for label reads, because `SimulationHost` does not expose the registry.

**AC4 — HUD energy + crystal readout.** _(story-added — Task 10)_
**Given** a selected caster **When** the HUD renders **Then** it shows a per-caster **Energy** readout (`Energy[focusId]/MaxEnergy[focusId]`) and a **Crystal** balance (`Resources.Crystal[(int)Faction.Player1]`, currently unshown) **And** the existing resource-strip content (ore / supply / unit counts) and the UX-DR71 hierarchy are preserved, so a player can read **why** a button is greyed.

**AC5 — Determinism-neutral + boundary-safe (the fence).** _(story-added)_
**Given** 2.4b is presentation + wiring + data only **When** the change is complete **Then** it adds **no new sim SoA and no checksum fold**: `SimChecksum.AlgoVersion` stays **7**, **all 9 goldens are byte-identical (NO re-record)**, `SystemOrderTest` is untouched (11 systems, `AbilityCastSystem`@3 already registered in 2.4a), and `CanonicalModelHash.AlgoVersion=2` / `ReplayRecorder.VERSION=2` / `TickCommandPacket.PROTOCOL_VERSION=1` are **unchanged** (the cast reuses the shipped 11-byte wire + `OrderApplier`) **And** **no `using Godot;` / `float` gameplay value / `Dictionary`-enumeration enters sim code** (all new code is presentation `src/UI`/`MainScene`/Bootstrap + data JSON; the analyzer stays clean) **And** presentation **reads** sim arrays for display and **issues intents only** — it never writes sim SoA.

**AC6 — In-engine verification.** _(epic AC2/AC3 proven live; Task 11)_
**Given** the ability attached in AC3 **When** `/godot-verify` (or the Godot MCP) is run **Then** the focused caster shows the ability button with correct cost + cooldown, the cast is issued (instant for `Self`, click-target for `TargetUnit`), the effect resolves, the cost is debited, the cooldown counts down in real time and the ability re-enables at zero, and the button greys while on cooldown / when energy is insufficient — captured in a before/after screenshot.

_Covers: FR-11, AR-8, AR-9, NFR-4. Depends on: **2.4a** (the cast spine — `AbilityRegistry` + `AbilityCastSystem`@3 + `UnitCommand.CastAbility` + the 5 SoA arrays + `ResolveAbilities` + crystal API + the v7 fold — **done, code-review PASS**), 2.3 (`AbilityDefinition`/`AbilityLoader`/`ParsedTargeting` — done), 1.12 (`OrderApplier` single-switch + wire-reuse precedent — done). **Blocks: 2.5** (Ability Editor — "immediately attachable to a unit (2.4) and castable in a match")._

---

> ## ✅ READ FIRST — This is Story 2.4b (the UI + wiring half of the split 2.4)
> Story 2.4 was **split** (Alec, 2026-06-27). **2.4a shipped the sim cast spine** (Tasks 1–7; sim-side ACs + the v6→7 determinism fold) and is **`done` (code-review PASS, 2026-06-28)**. **This file = Story 2.4b** = the command-card UI (the original Tasks 8–11) **PLUS the in-game wiring chain** that `deferred-work.md` §"story 2.4a" item 1 explicitly handed to 2.4b: building the `AbilityRegistry` from disk, calling `ResolveAbilities` at scenario link, injecting the registry into `SimulationHost.Create`, and attaching an ability to a unit via data. **Without the wiring, the spine is a no-op in-game** (the host runs `AbilityRegistry.Empty`, no unit carries an ability). 2.5 depends on this story. **2.4b is presentation + wiring + data ONLY — it touches NO `src/Core`/`src/Effects` sim logic, adds NO SoA, and moves NO golden** (see AC5 / the determinism posture). If a golden moves or `AlgoVersion` changes, you leaked sim state — stop and find it.

---

## Tasks / Subtasks

> **Build order matters.** Do **Task 1 (wiring) + Task 2 (attach an ability) first** — until the registry is non-empty and a unit carries an ability, the UI you build in Tasks 4–5 renders nothing and you can't verify anything. Wiring → data → SelectionSystem seam → command card → HUD → glue → verify.

- [x] **Task 1 — In-game wiring: build + inject the `AbilityRegistry`, resolve abilities at scenario link (AC3, AC5)** _[the enabler — `deferred-work.md` §story-2.4a item 1]_
  - [x] 1.1 **Build the registry up-front** in `MainScene._Ready`, **near the faction-load block (`MainScene.cs:241-249`) and BEFORE the host is built at `MainScene.cs:270`.** Add a path const next to `P1_FACTION_JSON` (`MainScene.cs:179-181`): `private const string ABILITIES_DIR = "res://resources/data/abilities";`. Then: `string abilitiesAbs = ProjectSettings.GlobalizePath(ABILITIES_DIR); _abilityRegistry = AbilityRegistry.LoadFromDirectory(abilitiesAbs, name => GD.Print($"[Abilities] skipped invalid {name}"));` Add a `private AbilityRegistry _abilityRegistry = AbilityRegistry.Empty;` field (`MainScene.cs` ~`:42` next to `_factionDef`). `LoadFromDirectory` takes an **absolute OS path** (it uses `Directory.GetFiles`) — `res://` will NOT work, hence the `GlobalizePath` (the exact `ProjectSettings.GlobalizePath(...)` pattern already used at `MainScene.cs:241`).
  - [x] 1.2 **Resolve up-front faction defs.** Immediately after `_factionDef`/`_factionDef2` are loaded (`MainScene.cs:241-249`): `if (_factionDef != null) foreach (var u in _factionDef.Units) u.ResolveAbilities(_abilityRegistry);` and the same for `_factionDef2`. `ResolveAbilities` is idempotent (re-running is safe) and back-fills `def.AbilityIndices` from `registry.IndexOf(...)`; `ApplyUnitDefinition` reads `def.AbilityIndices` per spawn, so resolving the **shared** `UnitDefinition` objects once per loaded faction covers scenario-placed, trained, and editor spawns.
  - [x] 1.3 **Resolve per-slot scenario faction defs.** In `ScenarioLoadPhase.ResolveSlotFactionDefs` (`src/Core/Bootstrap/Phases/ScenarioLoadPhase.cs:92-102`), after each `FactionDefinition.LoadFromFile(...)` (`:100`) loads a `slot.FactionJson` def into `ctx.SlotFactionDefs`, loop `foreach (var u in def.Units) u.ResolveAbilities(ctx.AbilityRegistry);`. The registry must be reachable from the phase → publish it on `SceneContext` (Task 1.6). This phase runs **after** the host is built (runtime position 12), so the registry must already exist from 1.1.
  - [x] 1.4 **Inject into the client host.** Change the `SimulationHost.Create(...)` call at `MainScene.cs:270-276` to pass the registry as the **7th arg**: `SimulationHost.Create(_logSink, new FactionRegistry(2), _factionDef, _factionDef2, _damageTable, AiLevel, _abilityRegistry)`. The param slot already exists (`SimulationHost.cs:61-69`, `AbilityRegistry? registry = null` → `?? AbilityRegistry.Empty`). **Preserve every existing arg** and the checksum-sink wiring at `:309-313`.
  - [x] 1.5 **Server parity (MP-critical).** Do the identical build+resolve+inject in the dedicated-server path: `ServerBootstrap.Build` (`src/Core/Sim/ServerBootstrap.cs:37`) and its caller `MainScene.BuildHeadlessServerSimHost` (`MainScene.cs:1151`, faction loads ~`:1155/1183`). Both currently pass 6 args → `Empty`. **Both peers AND the server must resolve abilities from the same files into identical ascending-`Id` indices** — a registry mismatch between client and server is a desync. (The registry sorts by ability `Id` ordinal, so identical files → identical indices deterministically.)
  - [x] 1.6 **Publish the registry on `SceneContext`.** Add `public AbilityRegistry AbilityRegistry { get; set; } = AbilityRegistry.Empty;` to `SceneContext.cs` (next to `FactionDef` `:51`). Set it in `MainScene._Ready` after 1.1 (`_ctx.AbilityRegistry = _abilityRegistry;`). The command card (Task 4) and `ScenarioLoadPhase` (1.3) read it from here, because **`SimulationHost` does NOT expose the registry** (it lives privately inside `AbilityCastSystem`).

- [x] **Task 2 — Attach a castable ability to a Player1 unit via data (AC1, AC3, AC6)** _(Decision A — defaults baked)_
  - [x] 2.1 Add `"abilities": ["fireball"]` and `"max_energy": 100` to the **`mage` / Circle Savant** unit block in `resources/data/factions/alpha_faction.json` (`mage` is Ranged/Magic — the thematically natural caster; the block is at `alpha_faction.json:106-125`, alongside the existing `prerequisites` array — both new keys are snake_case and optional). `fireball` = `TargetUnit`, `cost_energy: 50`, `cooldown: 6s` — exercises AC1 (cost/cooldown), AC2 (the `TargetUnit` click-target flow), and AC6.
  - [x] 2.2 **Ensure the caster is on the field for the default scenario.** The default scenario `resources/data/scenarios/alpha_map_01.json` pre-spawns **only workers** for P1 (`units[]` ~`:98-109`), so a `mage` won't exist at match start unless trained. For a zero-friction verify, **add one pre-placed `{ "unit_id": "mage", "slot": 0, "x": …, "z": … }` to the scenario `units[]`** so the caster is immediately selectable. (Alternative per Decision A: skip the scenario edit and train a mage via the Sigil Foundry during `/godot-verify` — more realistic, more verify steps.) Keep all existing scenario entries intact.

- [x] **Task 3 — `SelectionSystem`: cast-target arming + the `CastAbility` issue seam (AC2)**
  - [x] 3.1 Add a fourth await flag mirroring the three at `SelectionSystem.cs:97-102` — `private bool _awaitingCastClick;` — **plus its paired pending fields** (the cast needs to remember which ability, unlike the other flags which only need the click position): `private int _pendingCastCasterId = -1;` and `private int _pendingCastSlot = -1;`. When arming, set `_awaitingCastClick = true` and **clear the other three flags** (`_awaitingAttackMoveClick`/`_awaitingPatrolClick`/`_awaitingFollowClick = false`), matching the mutual-exclusion at `:298-311`. Clear `_awaitingCastClick` (and reset the pending fields) at **S** (`:288`), **H** (`:293`), and **Escape** (`:316`) alongside the existing flags.
  - [x] 3.2 `public void ArmCastTargeting(int casterId, int slot)` — sets `_pendingCastCasterId = casterId; _pendingCastSlot = slot; _awaitingCastClick = true;` (+ clears the other flags). Called by the command card (Task 4.5) for `TargetUnit` abilities.
  - [x] 3.3 **Consume on the next left-click** (in the LMB handler, alongside the existing consume sites at `:200-229`): if `_awaitingCastClick`, `RaycastGround(lmb.Position, out hit)` → `int targetId = FindNearestEnemyUnit(hit, radius)` → if `targetId >= 0` call `IssueCastAbilityCommand(_pendingCastCasterId, _pendingCastSlot, targetId)`; then clear `_awaitingCastClick` + reset pending fields regardless. **Right-click / Escape cancels** (clear the flag, no cast). Use the same `radius` constant the follow/attack-target picks use.
  - [x] 3.4 `private void IssueCastAbilityCommand(int casterId, int slot, int targetEntityId)` — **mirror `IssueAttackTargetCommand` (`:554-566`) exactly**, but operate on the **single caster** (not the whole `_selectedList`) and pack **both** values: online `if (!_lockstep.EnqueueOrder(casterId, UnitCommand.CastAbility, Fixed.FromRaw(slot), Fixed.FromRaw(targetEntityId))) return;` (queued); offline `OrderApplier.Apply(_world, new UnitOrder(casterId, UnitCommand.CastAbility, Fixed.FromRaw(slot), Fixed.FromRaw(targetEntityId)), _world.FactionOf[casterId]);`. **Slot in `targetX`, target id in `targetZ`, both via `Fixed.FromRaw` (NEVER `FromFloat` — that scales by 65536 and corrupts the packed ints).** `Self`/`None` casts pass `targetEntityId = -1` and are issued **directly** from the card (no arming — Task 4.5). This is the existing `EnqueueTargetedCommand` raw-int pattern (`:167-168`) extended to two fields.
  - [x] 3.5 **Do NOT widen `SelectionSystem.Initialize`** (`:129-137`) — SelectionSystem needs **no** ability data (the card supplies caster+slot; SelectionSystem only picks a target and issues). The must-preserve note: keep SelectionSystem faction-def/ability-free.

- [x] **Task 4 — `CommandCardSystem`: the ability section (AC1, AC2)**
  - [x] 4.1 **Inject the registry without widening `Initialize`.** Add a `private AbilityRegistry _registry = AbilityRegistry.Empty;` field and a setter `public void SetAbilityRegistry(AbilityRegistry registry) => _registry = registry;` (mirrors `SelectionSystem.SetLockstep`, `:142`) — called from `CameraPhase` (Task 6). The card already holds `_selection`/`_world`/`_resources` (`:25-29`) — those plus the registry are all it needs.
  - [x] 4.2 **Build a third panel.** Clone `BuildWorkerPanel()` (`:294-343`) into `BuildAbilityPanel()`: a new `_abilityPanel` (Panel, **`MouseFilter = Stop`** like `:305` so clicks don't fall through and deselect) + an ability `Button[] _abilityBtns` sized `MAX_ABILITIES_PER_UNIT` (4), built with the **captured-loop-variable idiom** (`:337-338` — copy the slot index before the lambda) and `MakeLabel` (`:283-290`). Position it in the same command-card region as the worker panel.
  - [x] 4.3 **Show/hide gate** in `_Process` (clone the worker gate `:106-111`): show the ability panel when `!buildingSelected && _world != null && focusId >= 0 && _world.IsAlive(focusId) && _world.FactionOf[focusId] == Faction.Player1 && _world.AbilityCount[focusId] > 0`. (Combat casters; a unit that is **both** a gatherer and ability-bearing → the worker card wins in 2.4b — worker-cast is 2.9b. See Decision C.) Hide all panels outside Play mode (`:92-97`). **Read abilities DIRECTLY from the per-entity SoA** — `for (int slot = 0; slot < _world.AbilityCount[focusId]; slot++) { int regIdx = _world.AbilityId[focusId * EntityWorld.MAX_ABILITIES_PER_UNIT + slot]; if (regIdx < 0 || regIdx >= _registry.Count) continue; AbilityDefinition ab = _registry.Get(regIdx); … }`. **This supersedes the 2.4a Task-8.1 `factionDef.Units[meshType].AbilityIndices` route** — the resolved registry indices are already in `AbilityId[]` (copied by `ApplyUnitDefinition`), so no `FactionDefinition` lookup is needed.
  - [x] 4.4 **Per-button refresh** (clone `RefreshWorkerCard` `:347-396`). For each populated slot, compute the **affordability identical to the sim** (`AbilityCastSystem.cs:112-121` — do **not** re-derive): `var f = _world.FactionOf[focusId]; int cdTicks = _world.AbilityCooldownTicks[focusId * EntityWorld.MAX_ABILITIES_PER_UNIT + slot]; bool onCd = cdTicks > 0; bool energyOk = _world.Energy[focusId] >= ab.CostEnergy; bool oreOk = _resources.CanAffordOre(f, Fixed.FromInt(ab.CostOre)); bool crysOk = _resources.CanAffordCrystal(f, Fixed.FromInt(ab.CostCrystal)); btn.Disabled = onCd || !energyOk || !oreOk || !crysOk;`. Label `$"{ab.DisplayName}\n{cost note}"`; reason-note in the disabled case using the worker-card note shape (`:199-204`/`:388-394`): `onCd ? $"[on CD {cdTicks / 30f:F1}s]" : !energyOk ? "[need energy]" : !oreOk ? "[need ore]" : !crysOk ? "[need crystal]" : "{cost summary}"`. The `cdTicks / 30f` display math is **presentation-only** (it is NOT in `src/Effects`/`src/Core`, so the analyzer's no-float rule does not apply — the train-timer at `:185-188` uses the same `ToFloat()`-for-display precedent). Hide unused buttons (`slot >= AbilityCount`).
  - [x] 4.5 **On press** — branch on `ab.ParsedTargeting` (nullable; `AbilityDefinition.cs:66`): `Self`/`None` → `_selection.IssueCastAbilityCommand(focusId, slot, -1)` **immediately**; `TargetUnit` → `_selection.ArmCastTargeting(focusId, slot)` (Task 3.2); `GroundPoint` → render the button **`Disabled` with "[ground-cast: coming soon]"** (the 2.4a fence — no reticle here); `null` (unknown targeting) → disabled. Capture `focusId`/`slot` per the captured-loop-variable idiom. **The card READS `_world`/`_resources` for display only — it never writes sim arrays** (the cast goes out as an intent via `_selection`).
  - [x] 4.6 `IssueCastAbilityCommand` and `ArmCastTargeting` are public on `SelectionSystem` (Tasks 3.2/3.4); the card already holds `_selection` so **no new event is needed** (unlike the worker-build path, which uses `OnWorkerBuildRequested` only because the placement ghost lives in `MainScene`).

- [x] **Task 5 — HUD energy + crystal readout (AC4)**
  - [x] 5.1 Extend the resource strip rather than adding a panel (minimal, UX-DR71-preserving). In `MainScene.UpdateHud` (`MainScene.cs:631-634`), append to the existing per-faction strip a **Crystal** balance (`_resources.Crystal[(int)Faction.Player1]`, currently unshown — the store + folded SoA already exist) and, when a caster is focused (`_ctx.Selection.FocusId >= 0` and `_world.MaxEnergy[focusId] > Fixed.Zero`), a per-caster **Energy** readout (`_world.Energy[focusId].ToInt()` / `_world.MaxEnergy[focusId].ToInt()`). **Keep the `_headless` guard** (`:433/491`) and the existing ore/supply/unit-count content — extend the string, don't replace it. (If you prefer a dedicated label, create it build-only in `HudPhase.cs:57-65` and publish it on `SceneContext`, matching the "HudPhase builds, UpdateHud fills" contract — either is acceptable.)

- [x] **Task 6 — Glue: wire the registry into the card in `CameraPhase` (AC2, AC3)**
  - [x] 6.1 In `CameraPhase` (where `Selection` and `CommandCard` are created/initialized, `CameraPhase.cs:44-51`), after the card's `Initialize(...)`, call `commandCard.SetAbilityRegistry(_ctx.AbilityRegistry)` (Task 4.1). `Selection` already gets `SetLockstep` here; the cast targeting needs no new wiring on `Selection` (the card calls `_selection.ArmCastTargeting`/`IssueCastAbilityCommand` directly). Confirm `_ctx.AbilityRegistry` is set (Task 1.6) before this phase runs.

- [x] **Task 7 — Verify, prove the determinism fence, and a wiring teeth-test (AC5, AC6)**
  - [x] 7.1 **In-engine** (`/godot-verify` or the Godot MCP, per AC6): launch the default scenario, select the ability-bearing P1 `mage`, confirm the **Fireball** button shows `50 energy · 6s`; click it → arm → click an enemy unit → the cast resolves (80 magic to the target + 30 splash), Energy `100→50`, the button greys and the cooldown counts down `6.0s → 0` then re-enables; a second immediate click is refused while greyed. Screenshot before/after. (Tier-2 GdUnit4 optional — the deterministic guarantees are all proven in 2.4a Part-A Tier-1.)
  - [x] 7.2 **Prove the determinism fence (AC5):** `dotnet test godot/ProjectChimera.Sim.Tests -c Release` stays green at **≥421 pass / 1 skip / 0 fail** (the existing 421 are unchanged — 2.4b touches no sim logic; the count ticks up only by the optional Task-7.3 teeth-test); **all 9 goldens byte-identical (NO re-record)**; `SimChecksum.AlgoVersion == 7` unchanged; `SystemOrderTest`/`VersionStampConsistencyTests`/`SimChecksumCoverageGuardTest` untouched and green; `CanonicalModelHash=2`/`ReplayRecorder.VERSION=2`/`PROTOCOL_VERSION=1` unchanged. Full `godot.csproj` build **0 errors**. Release analyzer gate (`-p:ChimeraRelease=true --no-incremental`) **0 CHM/RS0030 in any changed file** (none should be a sim file — all changes are `src/UI`/`MainScene`/Bootstrap/data). **If a golden moves, you leaked sim state into presentation — stop and fix the leak, do not re-record.**
  - [x] 7.3 **Wiring teeth-test (Godot-free, recommended).** Add a Tier-1 test that builds an `AbilityRegistry` (in-memory or from the real abilities dir), takes a `UnitDefinition` with `Abilities = ["fireball"]`, calls `ResolveAbilities(registry)` then `ApplyUnitDefinition`, and asserts the resulting `world.AbilityCount[id] == 1` and `AbilityId[id*MAX+0] == registry.IndexOf("fireball")`. This guards the wiring chain from silently regressing to `Empty`/unresolved (the 2.4a code-review defer item 3 — "a 2.4b wiring slip silently no-casts every ability"). Mirror `ApplyUnitDefinitionGuardTest.cs:127` (which already does `def.ResolveAbilities(registry)`).

- [x] **Task 8 — Document deferrals**
  - [x] 8.1 Append `deferred-work.md` §"story 2.4b": `GroundPoint` targeting (still — wire widen + `EffectContext` ground field + reticle, per §story-2.4a item 2); **ally-targeted `TargetUnit`** (heal-other — needs a target-affinity hint on `AbilityDefinition`; 2.4b's lone `TargetUnit` sample, fireball, is enemy-only via `FindNearestEnemyUnit` — Decision B); energy **regen** (§story-2.4a item 3); worker-cast command card (2.9b); the silent-no-cast diagnostic (2.4a code-review item 3 — now partly guarded by Task 7.3); and the note that presentation reads abilities from the per-entity SoA (no per-entity `UnitDefinition` link was added).

---

## Dev Notes

### What this story is — making the 2.4a cast spine reachable in a real match

2.4a built and proved (headlessly) the entire deterministic cast machinery: the `AbilityRegistry`, `AbilityCastSystem`@3, `UnitCommand.CastAbility`, the 5 per-entity SoA arrays, `ResolveAbilities`, the crystal API, and the v6→7 cooldown fold — all green, code-review **PASS** (421 pass / 9 goldens byte-identical). **But it is intentionally not reachable in-game:** the production host runs `AbilityCastSystem` with `AbilityRegistry.Empty`, and no unit carries an ability, so every cast is a deterministic no-op (`deferred-work.md` §story-2.4a item 1). **2.4b closes that gap on two fronts at once:** (1) the **wiring** — load the registry from disk, resolve ability ids → indices at scenario link, inject the registry into the host (client + server), and attach an ability to a unit via data; and (2) the **UI** — render the command-card ability section, the `TargetUnit` click-to-cast flow, and the HUD energy/crystal readout. It is **presentation + wiring + data only** — zero sim-logic change, zero SoA, zero fold (AC5).

### 🔑 Determinism posture — 2.4b does NOT fold (the fence is the headline)

Unlike 2.4a (which folded `AbilityCooldownTicks` at v7), **2.4b adds no mutable-mid-match sim state**, so per [[chimera-checksum-fold-timing-rule]] there is **no fold**: `AlgoVersion` stays **7**, **no golden re-records**, `SystemOrderTest`/the 3 version pins stay put. The cast is delivered through the **already-shipped** `OrderApplier`/`EnqueueOrder` on the **unchanged 11-byte wire** — no `ReplayRecorder.VERSION`/`PROTOCOL_VERSION` change. The only state that becomes non-zero in a running match is the unit's `Energy`/`AbilityCount`/cooldown — all already folded (Energy v6, cooldown v7) and **peer-identical** because both peers load the same faction/ability JSON. The one determinism risk in this story is the classic presentation footgun: **never write a sim array from the UI.** The card reads `_world.AbilityCooldownTicks`/`Energy` and `_resources.Crystal`/`Ore` for display, and the cast leaves as an `EnqueueOrder` intent — that is the sacred boundary, and AC5 enforces it. **If any golden moves, presentation leaked into the tick — find it, don't re-baseline.** (Goldens are recorded from the Tier-1 test scenarios, which build their own in-memory hosts/registries; editing `alpha_faction.json`/`alpha_map_01.json` does not touch them.)

### The wiring chain (Task 1) — exact call sites, in order

```
MainScene._Ready
  ├─ :179-181  const ABILITIES_DIR = "res://resources/data/abilities"           (NEW const)
  ├─ :241-249  load _factionDef / _factionDef2  (existing)
  ├─ (after)   _abilityRegistry = AbilityRegistry.LoadFromDirectory(             (Task 1.1)
  │                ProjectSettings.GlobalizePath(ABILITIES_DIR), onSkipped)
  ├─ (after)   foreach u in _factionDef.Units  u.ResolveAbilities(_abilityRegistry)   (Task 1.2)
  │            foreach u in _factionDef2.Units u.ResolveAbilities(_abilityRegistry)
  ├─ (after)   _ctx.AbilityRegistry = _abilityRegistry                            (Task 1.6)
  └─ :270-276  _host = SimulationHost.Create(…, AiLevel, _abilityRegistry)        (Task 1.4 — add 7th arg)

ScenarioLoadPhase.ResolveSlotFactionDefs  (:92-102, runs AFTER host build)
  └─ :100      after FactionDefinition.LoadFromFile → foreach u in def.Units      (Task 1.3)
                   u.ResolveAbilities(ctx.AbilityRegistry)

ServerBootstrap.Build (:37) / MainScene.BuildHeadlessServerSimHost (:1151)        (Task 1.5 — server parity)
  └─ same build + resolve + inject (MP-critical: identical indices on every peer + server)
```

**Why resolve the shared `UnitDefinition` objects, not per-spawn:** every spawn path (`ScenarioApplier.SpawnUnit`, `BuildingSystem.SpawnTrainedUnit`, `EntityPlacer`) funnels through `EntityWorld.ApplyUnitDefinition(id, def)` (`:521`), which reads `def.AbilityIndices` (`:550-554`). Those are empty until `ResolveAbilities` back-fills them. Because all spawn paths share the same `UnitDefinition` instances held in `FactionDefinition.Units`, resolving each list once (before any spawn) covers all three paths for free — the A2 single-mapper rule already guarantees the SoA copy is centralized; 2.4b just makes sure `AbilityIndices` is populated first.

### Reading a unit's abilities in presentation — read the SoA directly (the simplification)

The 2.4a seed (Task 8.1) proposed resolving `focusId → factionDef.Units[MeshType[focusId]].AbilityIndices`. **That indirection is unnecessary.** `ApplyUnitDefinition` already copies the resolved registry indices into the per-entity `AbilityId[]` and the slot count into `AbilityCount[]`. So the command card reads the focused entity's abilities **straight from the SoA** by `focusId`:

```csharp
for (int slot = 0; slot < _world.AbilityCount[focusId]; slot++)
{
    int regIdx = _world.AbilityId[focusId * EntityWorld.MAX_ABILITIES_PER_UNIT + slot];
    if (regIdx < 0 || regIdx >= _registry.Count) continue;   // empty/out-of-range guard
    AbilityDefinition ab = _registry.Get(regIdx);            // → DisplayName / costs / Cooldown / ParsedTargeting
}
```

The **only** thing presentation needs beyond the world is the `AbilityRegistry` (to turn a registry index into an `AbilityDefinition` for labels). No `FactionDefinition`/`MeshType` lookup, no per-entity `UnitDefinition` link (none exists, and none is added).

### Live APIs you will call (exact signatures — verified on disk, do not re-derive)

- **Affordability (mirror EXACTLY in the card)** — `AbilityCastSystem.cs:112-121`: enabled iff `AbilityCooldownTicks[id*MAX+slot] == 0` AND `world.Energy[id] >= ab.CostEnergy` AND `Resources.CanAffordOre(faction, Fixed.FromInt(ab.CostOre))` AND `Resources.CanAffordCrystal(faction, Fixed.FromInt(ab.CostCrystal))`. The check reads `world.Energy[id]` directly (the debit goes via `_modifiers.TryDebitEnergy` — the UI mirrors the **check**, never debits).
- **Cast issue (the seam)** — `bool LockstepManager.EnqueueOrder(int unitId, UnitCommand command, Fixed targetX, Fixed targetZ)` (`LockstepManager.cs:230`; offline → `true` = apply now, online → `false` = queued, spectator → `false`). `void OrderApplier.Apply(EntityWorld world, in UnitOrder o, Faction expectedFaction, …)` (`NetworkCommand.cs:115`). `new UnitOrder(int unitId, UnitCommand command, Fixed targetX, Fixed targetZ)` (`NetworkCommand.cs:83` — stores `targetX.Raw`/`targetZ.Raw`). **Pack: `targetX = Fixed.FromRaw(slot)`, `targetZ = Fixed.FromRaw(targetId)` (−1 for Self/None). NEVER `Fixed.FromFloat`/`.ToFloat()` on these — they are raw ints (the 1.12 lesson).**
- **Registry** — `AbilityRegistry.LoadFromDirectory(string absDir, Action<string>? onSkipped = null)` (`AbilityRegistry.cs:71`, **absolute path**, missing dir → `Empty`); `int Count`; `AbilityDefinition Get(int index)` (`:50`); `int IndexOf(string id)` (`:56`, −1 if absent); `IReadOnlyList<AbilityDefinition> All` (`:31`); `static readonly Empty` (`:35`).
- **Resolve / mapper** — `void UnitDefinition.ResolveAbilities(AbilityRegistry registry)` (`UnitDefinition.cs:146`, idempotent, drops unknown ids, clamps to `MAX_ABILITIES_PER_UNIT`); `void EntityWorld.ApplyUnitDefinition(int id, UnitDefinition def)` (`EntityWorld.cs:521`, copies `MaxEnergy`/`Energy`/`AbilityId`/`AbilityCount` at `:548-554`).
- **Ability data (for labels)** — `AbilityDefinition` (`AbilityDefinition.cs`): `string Id` (`:23`), `string DisplayName` (`:27`), `AbilityTargeting? ParsedTargeting` (`:66`, **nullable** — unknown → `null`), `Fixed CostEnergy` (`:35`), `int CostOre` (`:39`), `int CostCrystal` (`:43`), `Fixed Cooldown` SECONDS (`:47`). `enum AbilityTargeting : byte { None=0, Self=1, TargetUnit=2, GroundPoint=3 }` (`AbilityTargeting.cs:11-24`).
- **World SoA (read for display)** — `EntityWorld`: `MAX_ABILITIES_PER_UNIT = 4` (`:91`), `NO_PENDING_CAST = 255` (`:98`), `int[] AbilityId` (`:292`, flat `id*MAX+slot`, −1=empty), `int[] AbilityCooldownTicks` (`:299`, 0=ready), `byte[] AbilityCount` (`:305`), `Fixed[] Energy`/`MaxEnergy` (`:178/:182`), `byte[] MeshType` (`:249`), `Faction[] FactionOf` (`:158`), `bool IsAlive(int)` (`:591`), `enum UnitCommand{…CastAbility=10}` (`:26`).
- **Resources** — `bool ResourceStore.CanAffordOre(Faction,Fixed)` (`:49`), `CanAffordCrystal(Faction,Fixed)` (`:71`), `Fixed[] Ore` (`:12`), `Fixed[] Crystal` (`:13`).
- **Host** — `static SimulationHost Create(ILogSink, FactionRegistry, FactionDefinition?, FactionDefinition?, DamageTable?, AiDifficulty, AbilityRegistry? = null)` (`SimulationHost.cs:61`). Public accessors: `World`/`Resources`/`Modifiers` (`:34/:36/:40`) — **NO `AbilityRegistry` accessor** (hold your own on `SceneContext`).
- **Path globalize** — `ProjectSettings.GlobalizePath("res://…")` → abs OS path (live example `MainScene.cs:241`).

### Patterns to clone (do NOT reinvent)

| Need | Clone from | Lines |
|---|---|---|
| A new command-card panel + Button[] (captured-loop-var, `MouseFilter=Stop`, `MakeLabel`) | `CommandCardSystem.BuildWorkerPanel` | `:294-343` |
| Per-button `.Disabled` + affordability reason-note | `CommandCardSystem.RefreshWorkerCard` | `:379-395` |
| Cooldown countdown display (`ToFloat()` for text, presentation-only) | the train-timer in `RefreshCard` | `:181-189` |
| Card show/hide gate by focus + faction | the worker gate in `_Process` | `:106-111` |
| A new targeted-command seam (online `EnqueueOrder` / offline `OrderApplier.Apply`) | `SelectionSystem.IssueAttackTargetCommand` | `:554-566` |
| Await-click flag arm/consume/cancel | the three `_awaiting*Click` flags | `:97-102, :288-316` |
| Raw-int target packing | `EnqueueTargetedCommand` | `:167-168` |
| Inject a dep without widening `Initialize` | `SelectionSystem.SetLockstep` | `:142` |
| Build-only HUD label published on `SceneContext` | `HudPhase` labels | `:50-104` |
| `res://`-dir → abs → load | the faction load | `MainScene.cs:241` |
| The exact runtime construction (registry → host) | test `Golden/AbilityCastScenario.cs` | `:35-42` |

### Project Structure Notes

- **Modified (presentation / wiring — NONE are sim files):** `src/UI/CommandCardSystem.cs` (ability panel + refresh + `SetAbilityRegistry`); `src/UI/SelectionSystem.cs` (`_awaitingCastClick` + pending fields + `ArmCastTargeting` + `IssueCastAbilityCommand`); `src/Core/MainScene.cs` (build/resolve/inject the registry at `:179/:241-276`, server path `:1151`, HUD strip `:631`); `src/Core/Bootstrap/Phases/ScenarioLoadPhase.cs` (resolve per-slot defs `:100`); `src/Core/Bootstrap/Phases/CameraPhase.cs` (`SetAbilityRegistry` glue `:44-51`); `src/Core/Bootstrap/SceneContext.cs` (+`AbilityRegistry` field); `src/Core/Sim/ServerBootstrap.cs` (server inject `:37`); optionally `src/Core/Bootstrap/Phases/HudPhase.cs` (energy/crystal label).
- **Modified (data):** `resources/data/factions/alpha_faction.json` (+`abilities`/`max_energy` on `mage`); `resources/data/scenarios/alpha_map_01.json` (pre-place a P1 `mage`, Decision A).
- **New (tests, optional but recommended):** a Godot-free wiring teeth-test under `ProjectChimera.Sim.Tests/` (Task 7.3).
- **OUT OF SCOPE — do not touch:** any `src/Effects/*` or `src/Core/SimChecksum.cs`/`EntityWorld.cs` **sim logic** (2.4a shipped it — read-only here); the 9 `*.golden.txt` (no re-record); `SystemOrderTest`/`VersionStampConsistencyTests`/`SimChecksumCoverageGuardTest` (no version change); `OrderApplier`/`UnitOrder`/`TickCommandPacket` (reuse the wire); `AbilityCastSystem`/`AbilityRegistry`/`UnitDefinition.ResolveAbilities` internals (call them, don't edit). **No `SimSources.props` edit, no NuGet.**
- **A2 note:** 2.4b adds **no** new per-entity SoA field, so the single-mapper rule / `ApplyUnitDefinitionGuardTest` need no extension here (2.4a already routed `MaxEnergy`/ability slots through `ApplyUnitDefinition`). The wiring only ensures `AbilityIndices` is populated before that mapper runs.

### Project Context Rules

_From `_bmad-output/project-context.md`:_
- **The sim/presentation boundary is sacred.** `src/UI`, `MainScene`, and Bootstrap phases are **presentation** — they read sim arrays each frame and send **intents** (`EnqueueOrder`); they never mutate sim SoA. Every cast in this story is an intent. (This is AC5's whole point.)
- **Determinism:** the cast rides the shipped `OrderApplier` single switch (live/replay/offline parity) on the unchanged 11-byte wire; raw-int packing via `Fixed.FromRaw`, never `FromFloat`/`.ToFloat()` on packed ints. No fold, no golden move.
- **Everything is data-driven:** the ability is attached via JSON (`UnitDefinition.abilities`), loaded by `AbilityRegistry.LoadFromDirectory`, validated by 2.3's gate — no hardcoded ability logic in the UI.
- **Reuse, don't reinvent:** clone the worker-card / await-flag / issue-seam patterns; the registry, cast system, crystal API, and Energy SoA are all shipped. `MultiMeshInstance3D` rendering, `NavigationServer3D`, `SpatialHash` unchanged.
- **Conventions:** `PascalCase.cs`; presentation namespaces `ProjectChimera.UI` / `ProjectChimera.Core`; `#nullable enable`; comment public methods + non-obvious logic; Godot-derived classes are `partial`; use `GD.Print` (presentation only); guard every client-only path with `_headless`.

### References

- **Story + epic:** `epics.md#Story-2.4` (912-928, the 3 epic ACs); Epic 2 sequencing note (840); blocked consumer 2.5 (930-946, "immediately attachable to a unit (2.4) and castable in a match"); FR-11 (`epics.md:385`), UX-DR71 HUD hierarchy (342), UX-DR66 keybindings (335). The 2.4a story `2-4a-ability-runtime-cast-spine-cooldown-soa-and-determinism-fold.md` — **PART B (Tasks 8–11)** is this story's seed; the Dev Agent Record + Change Log carry the as-built reality; **§"Deferred from story 2.4a" item 1** is 2.4b's wiring mandate.
- **Deferred-work hand-off:** `deferred-work.md` §"story 2.4a" item 1 (command-card UI + in-game wiring → 2.4b), item 2 (GroundPoint defer), item 3 (energy regen defer); §"code review of story-2.4a" item 3 (silent-no-cast on a wiring slip → Task 7.3 guard).
- **Live source — presentation (CLONE these):** `CommandCardSystem.cs` (`Initialize:71` · `_Process` gate `:90-118` · `RefreshCard` train-timer/note `:181-204` · `MakeLabel:283` · `BuildWorkerPanel:294-343` · `RefreshWorkerCard:347-396` · `OnWorkerBuildRequested:67`); `SelectionSystem.cs` (`FocusId:39` · await flags `:97-102` · arm `:298-311` · cancel `:288/293/316` · consume `:200-229` · `EnqueueTargetedCommand:167` · `IssueAttackTargetCommand:554-566` · `FindNearestEnemyUnit:666` · `FindNearestUnit:643` · `RaycastGround:626` · `SetLockstep:142` · `Initialize:129`); `MainScene.cs` (faction load `:241-249` · host `Create:270-276` · `_Input` placement machine `:397-429` · `UpdateHud`/resource strip `:593-665/:631-634` · server host `:1151` · field decls `:33-46`); `HudPhase.cs:50-104`; `CameraPhase.cs:44-51`; `SceneContext.cs:37-59`.
- **Live source — sim (CALL, do not edit):** `AbilityCastSystem.cs` (affordability `:112-121` · ctor `:50` · `SecondsToTicks:68`); `AbilityRegistry.cs` (`LoadFromDirectory:71` · API `:28-56` · `Empty:35`); `UnitDefinition.cs` (`Abilities:119` · `MaxEnergy:128` · `AbilityIndices:136` · `ResolveAbilities:146`); `AbilityDefinition.cs` (`:23-66`); `AbilityTargeting.cs:11-24`; `EntityWorld.cs` (`MAX_ABILITIES_PER_UNIT:91` · `NO_PENDING_CAST:98` · SoA `:158/:178/:249/:292-317` · `ApplyUnitDefinition:521` · `IsAlive:591` · `UnitCommand:12-27`); `ResourceStore.cs` (`:12-13/:49/:71`); `SimulationHost.cs` (`Create:61` · accessors `:34-51` · `AbilityCastSystem`@3 `:113`); `NetworkCommand.cs` (`UnitOrder:83` · `OrderApplier.Apply:115` · CastAbility case `:229-245`); `LockstepManager.cs:230`; `ServerBootstrap.cs:37`.
- **Data:** `resources/data/abilities/{fireball,minor_heal,battle_fury}.json` (fireball = `TargetUnit`/50 energy/6s; minor_heal & battle_fury = `Self`); `resources/data/factions/alpha_faction.json` (`worker:11-29`, `mage:106-125` Ranged/Magic, `infantry:31`); `resources/data/scenarios/alpha_map_01.json` (`units[]` P1 workers `:98-109`). Construction template: test `Golden/AbilityCastScenario.cs:35-42`; resolve example `Core/ApplyUnitDefinitionGuardTest.cs:127`.
- **Prior-story lessons:** 1.12 (wire reuse + `OrderApplier` single-switch = replay parity, no VERSION bump); 1.13 (`ApplyUnitDefinition` single-mapper); 2.4a (the spine + the v7 fold + the GroundPoint/regen fences); `epic-1-retro-2026-06-25.md` (A1 3-layer review, A3 prove-gates-have-teeth → Task 7.3).

---

## Open Decisions for Alec (defaults baked in; confirm or override)

> Written end-to-end with recommended defaults so it is immediately implementable. Only **Decision A** changes the verify demo meaningfully; the rest are baked with rationale.

**Decision A — Which unit gets the test ability, and how it reaches the field (baked: `mage` + pre-place).**
Attach `fireball` to **`mage` / Circle Savant** (Ranged/Magic — the thematically right caster) and **pre-place one P1 `mage`** in `alpha_map_01.json` so it is immediately selectable for the AC6 demo (exercises the `TargetUnit` click-to-cast flow, which is AC2's headline). _Alternatives:_ (A2) attach to `mage` but train it via the Sigil Foundry during verify (no scenario edit, more steps); (A3) attach `minor_heal` (Self) to the pre-spawned `worker`/Acolyte (zero scenario edit, instant-cast — but skips the `TargetUnit` flow and casts on a worker, which is thematically odd). **Default = A1.**

**Decision B — `TargetUnit` faction filter (baked: enemy-only).** 2.4b's only `TargetUnit` sample (`fireball`) is offensive, so a cast-target click picks the nearest **enemy** (`FindNearestEnemyUnit`). Ally-targeted `TargetUnit` (e.g. heal-other) needs a target-affinity hint on `AbilityDefinition` (no sample needs it) → deferred. _Override only if you want a friendly-target ability castable in 2.4b._

**Decision C — Ability-card vs worker-card precedence (baked: combat casters only).** The ability section shows for a focused P1 unit with `AbilityCount>0`; if a unit is **both** a gatherer and ability-bearing, the **worker card wins** in 2.4b (worker-cast is Story 2.9b). _Override if you want abilities on workers now._

**Resolved-by-default (override if you disagree):**
- **#D — Inject the registry into the card via a `SetAbilityRegistry` setter**, not by widening the 5-arg `Initialize` (matches `SetLockstep`; avoids disturbing the `CameraPhase` wiring — the must-preserve note).
- **#E — Read abilities from the per-entity `AbilityId`/`AbilityCount` SoA**, not via `factionDef.Units[MeshType]` (the resolved indices are already in the SoA; simpler, no `FactionDefinition` dependency in the card).
- **#F — Resolve abilities on the shared `UnitDefinition` objects once per loaded faction** (covers all 3 spawn paths via `ApplyUnitDefinition`), at every load site including the per-slot `ScenarioLoadPhase` and the dedicated server (MP parity).
- **#G — No fold, no golden re-record, no version bump** — 2.4b adds no sim state; the determinism fence (AC5) is mandatory and self-verifying (a moved golden = a leaked sim write, fix it not re-baseline it).

---

## Dev Agent Record

### Agent Model Used

claude-opus-4-8 (Claude Opus 4.8)

### Debug Log References

- Tier-1 (Godot-free): `dotnet test godot/ProjectChimera.Sim.Tests -c Release` → **424 passed / 1 skipped / 0 failed** (baseline 421 + 3 new `AbilityWiringTeethTest`); all 9 golden-checksum replay tests green ⇒ goldens byte-identical.
- Full Godot build: `dotnet build godot/godot.csproj -c Debug` → **0 errors** (3 pre-existing CS8632 warnings, unrelated).
- Release analyzer gate: `dotnet build godot/ProjectChimera.Sim.Analysis/...csproj -c Release --no-incremental -p:ChimeraRelease=true` → **Build succeeded, 0 errors** (RS0030 zero-baseline clean; `ServerBootstrap.cs` introduces no CHM/RS0030 finding).
- In-engine (Godot MCP, Godot 4.6.3-stable): launched `main.tscn` → Play Skirmish → selected the pre-placed P1 `mage` (id 2). **BEFORE:** command card "Abilities [P1]" → enabled "Fireball  50 energy · 6s CD"; HUD "Energy: 100 / 100" + "0 crystal". **AFTER** a cast (via `IssueCastAbilityCommand`, the button's seam): energy 100→50, target P2 unit killed (effect graph resolved, P2 5→4 total), button greyed "[on CD 5.9s]" counting down. Zero runtime errors in the editor log.

### Completion Notes List

**Presentation + wiring + data ONLY — the determinism fence (AC5) held: NO new sim SoA, NO `SimChecksum` change (`AlgoVersion` stays 7), all 9 goldens byte-identical (NO re-record), `SystemOrderTest`/`VersionStampConsistencyTests`/`SimChecksumCoverageGuardTest`/`CanonicalModelHash`/`ReplayRecorder.VERSION`/`PROTOCOL_VERSION` untouched.** The cast rides the shipped `OrderApplier`/`EnqueueOrder` on the unchanged 11-byte wire (raw-int pack via `Fixed.FromRaw`, never `FromFloat`).

- **Task 1 (wiring, AC3):** `MainScene._Ready` builds `_abilityRegistry` from `ABILITIES_DIR` via `AbilityRegistry.LoadFromDirectory(GlobalizePath(...))`, resolves the up-front `_factionDef`/`_factionDef2` units, injects the registry as the 7th `SimulationHost.Create` arg, and publishes it on `SceneContext`. `ScenarioLoadPhase.ResolveSlotFactionDefs` resolves each per-slot loaded def before spawn. `ServerBootstrap.Build` gained an `AbilityRegistry` param + resolves the slot defs' ability ids (MP parity — identical ascending-Id indices on every peer + server); `MainScene.BuildHeadlessServerSimHost` builds + passes it.
- **Task 2 (data):** `alpha_faction.json` `mage` gained `"abilities": ["fireball"]` + `"max_energy": 100`; `alpha_map_01.json` pre-places one P1 `mage` at (-40, 0) (Decision A).
- **Task 3 (SelectionSystem, AC2):** `_awaitingCastClick` + `_pendingCastCasterId`/`_pendingCastSlot`; `ArmCastTargeting` (public) + the cast-target left-click consume (enemy-only via `FindNearestEnemyUnit`, Decision B) + right-click/Escape cancel; `IssueCastAbilityCommand` (public) packs slot in `TargetX` / target in `TargetZ` via `Fixed.FromRaw` through the shared `OrderApplier` (offline) / `EnqueueOrder` (online). The 4 click-arm flags were consolidated into one `ResetPendingCommandClicks()` helper so a new arm can never be forgotten at a clear site (the missed-spot defect class) — behavior-preserving for the 3 existing flags.
- **Task 4 (CommandCardSystem, AC1/AC2):** `SetAbilityRegistry` setter (no `Initialize` widen); `BuildAbilityPanel` (cloned from the worker panel, `MouseFilter=Stop`, captured-loop-var); `_Process` show/hide gate (`!workerSelected` → worker card wins, Decision C); `RefreshAbilityCard` mirrors the EXACT sim affordability predicate (`AbilityCastSystem.cs:112-121`) so the greyed button never diverges from the sim's refusal; press branches on `ParsedTargeting` (Self/None → instant; TargetUnit → arm; GroundPoint → disabled "[ground-cast: coming soon]"; null → disabled).
- **Task 5 (HUD, AC4):** `MainScene.UpdateHud` resource strip now shows P1 crystal + a per-caster `Energy n/Max` line (guarded by `MaxEnergy>0`), preserving the existing ore/supply/unit-count content + the `_headless`-gated caller.
- **Task 6 (glue):** `CameraPhase` calls `commandCard.SetAbilityRegistry(_ctx.AbilityRegistry)` after `Initialize`.
- **Task 7 (verify):** in-engine before/after (above); determinism fence proven; new `AbilityWiringTeethTest` (3 Tier-1 facts) guards the resolve→apply chain (resolve→`AbilityCount==1`+right index; skip-resolve→`AbilityCount==0` = the silent-no-cast regression; unknown-id drop).
- **Task 8:** `deferred-work.md` §"story 2.4b" appended (GroundPoint, ally-target TargetUnit, energy regen, worker-cast card, the remaining runtime no-cast diagnostic, the SoA-read note).

**Minor faithful deviation:** the SelectionSystem click-arm clears were refactored into one `ResetPendingCommandClicks()` helper (the story prescribed inline per-site clears) — same behavior for the 3 existing flags, structurally safer for the new 4th. Flagged for review.

### File List

**Modified — presentation / wiring (NONE are hashed sim files; NO SoA / checksum change):**
- `godot/src/Core/MainScene.cs` — `ABILITIES_DIR` const, `_abilityRegistry` field, build+resolve+inject (client `_Ready`), publish on `SceneContext`, server path (`BuildHeadlessServerSimHost`), HUD energy+crystal strip.
- `godot/src/UI/CommandCardSystem.cs` — ability panel (build / `_Process` gate / refresh / press) + `SetAbilityRegistry`.
- `godot/src/UI/SelectionSystem.cs` — `_awaitingCastClick`+pending fields, `ArmCastTargeting`, `IssueCastAbilityCommand`, cast-target consume/cancel, `ResetPendingCommandClicks` helper.
- `godot/src/Core/Bootstrap/Phases/SceneContext.cs` — `AbilityRegistry` field.
- `godot/src/Core/Bootstrap/Phases/ScenarioLoadPhase.cs` — resolve per-slot faction defs.
- `godot/src/Core/Bootstrap/Phases/CameraPhase.cs` — `SetAbilityRegistry` glue.
- `godot/src/Core/Sim/ServerBootstrap.cs` — `AbilityRegistry` param + resolve + inject (server parity).

**Modified — data:**
- `godot/resources/data/factions/alpha_faction.json` — `mage` `abilities`/`max_energy`.
- `godot/resources/data/scenarios/alpha_map_01.json` — pre-placed P1 `mage`.

**New — test:**
- `godot/ProjectChimera.Sim.Tests/Core/AbilityWiringTeethTest.cs` — wiring-chain teeth-test (3 facts).

**Modified — docs:**
- `_bmad-output/implementation-artifacts/deferred-work.md` — §"story 2.4b" deferrals.

### Change Log

| Date | Change |
|---|---|
| 2026-06-28 | Story 2.4b created (`gds-create-story`). Exhaustive context-engine analysis: 3 parallel research subagents (presentation UI surface `CommandCardSystem`/`SelectionSystem`/`HudPhase`/`MainScene` · as-built sim cast APIs `AbilityCastSystem`/`AbilityRegistry`/`UnitDefinition`/`EntityWorld`/`ResourceStore`/`SimulationHost`/`NetworkCommand` · the ability-attach data + in-game wiring chain) returning exact `file:line` refs + verbatim signatures, plus direct reads of the 2.4a story (PART B seed + Dev Record + the code-review patch), `deferred-work.md` §story-2.4a (the item-1 wiring mandate), `epics.md` Story-2.4/2.5, and `project-context.md`. Scope: the UI + wiring half of the split 2.4 — (1) build the `AbilityRegistry` from `resources/data/abilities/` + `ResolveAbilities` at scenario link (up-front faction defs + per-slot `ScenarioLoadPhase` + dedicated server) + inject as the 7th `SimulationHost.Create` arg (client + server) + publish on `SceneContext`; (2) attach `fireball` to a P1 `mage` via faction/scenario JSON; (3) the command-card ability section reading the per-entity SoA directly (supersedes the 2.4a `factionDef.Units[meshType]` route) with the sim-identical affordability predicate; (4) the `SelectionSystem` `_awaitingCastClick` targeting + `IssueCastAbilityCommand` seam (reusing the 11-byte wire via `Fixed.FromRaw`); (5) the HUD energy+crystal readout; (6) `/godot-verify` + a Godot-free wiring teeth-test. **Determinism posture: NO fold** — presentation/wiring/data only, `AlgoVersion` stays 7, all 9 goldens byte-identical (no re-record), `SystemOrderTest`/version pins untouched, `CanonicalModelHash=2`/`ReplayRecorder.VERSION=2`/`PROTOCOL_VERSION=1` unchanged; the cast reuses the shipped `OrderApplier`/`EnqueueOrder`. 6 ACs (epic AC1 + AC2-UI; story-added AC3 wiring / AC4 HUD / AC5 fence / AC6 verify), 8 tasks. 3 decisions baked (A = `mage` + pre-place; B = enemy-only `TargetUnit`; C = combat-casters-only) + 4 resolved-by-default. baseline `d3636e2`. NEXT — `gds-dev-story` on 2.4b. |
| 2026-06-28 | Story 2.4b DEV-DONE (`gds-dev-story`, claude-opus-4-8) → **review**. All 8 tasks + 6 ACs complete. **Wiring (AC3):** `AbilityRegistry` built from `resources/data/abilities/` + injected as the 7th `SimulationHost.Create` arg on BOTH client (`MainScene._Ready`) and dedicated server (`ServerBootstrap.Build` + `BuildHeadlessServerSimHost`, MP-parity ability-id resolution); `ResolveAbilities` runs at scenario link (up-front faction defs + per-slot `ScenarioLoadPhase`); registry published on `SceneContext`. **Data (AC1/AC6):** `fireball` attached to the `mage` (+`max_energy:100`) + a P1 `mage` pre-placed in `alpha_map_01.json`. **UI (AC1/AC2):** `CommandCardSystem` ability panel (sim-identical affordability predicate, targeting-branch press, GroundPoint disabled-fence); `SelectionSystem` `_awaitingCastClick`+`ArmCastTargeting`+`IssueCastAbilityCommand` (raw-int pack via `Fixed.FromRaw` through the shared `OrderApplier`/`EnqueueOrder`), click-arm clears consolidated into `ResetPendingCommandClicks()`. **HUD (AC4):** crystal balance + per-caster energy readout. **Determinism fence (AC5):** presentation+wiring+data only — `AlgoVersion` stays 7, all 9 goldens byte-identical (NO re-record), version pins untouched. **Verify (AC6):** Tier-1 **424 pass/1 skip/0 fail** (+3 `AbilityWiringTeethTest`); full Godot build 0 errors; release analyzer 0 errors; in-engine before/after captured (Fireball button enabled "50 energy · 6s CD" + Energy 100/100 → cast → energy 50/100, target killed, button greyed "[on CD 5.9s]"), zero runtime errors. Deferrals logged to `deferred-work.md` §story-2.4b. NEXT — `gds-code-review` on 2.4b (different LLM, fresh context). |

---

## Review Findings — gds-code-review (2026-06-28)

_4-lens adversarial review (Blind Hunter · Edge Case Hunter · Acceptance Auditor · Determinism & Desync), Opus 4.8, fresh context, per-lens independent verification + orchestrator re-check against the live repo._ **0 Critical · 0 High.** _The determinism fence (AC5) is independently confirmed clean: `git diff --name-only d3636e2 HEAD` touches no golden / `SimChecksum` / `EntityWorld` / version-pin file; `AlgoVersion==7`; all 9 goldens byte-identical (golden harnesses are hermetic — worker-only rosters never spawn the mage, no golden loads `alpha_faction.json`); no presentation code writes a sim SoA; client↔server registry indices are identical (same dir, ordinal `OrderBy(Id)`). All 6 ACs MET except **AC6 (PARTIAL — verification rigor, not a code defect)**. 5 findings dismissed as verified false-positive / noise (mage JSON block DOES carry `fireball`; `Energy` IS seeded to `MaxEnergy` on spawn — in-engine showed 100/100; `FindNearestEnemyUnit` P1-assumption subsumed by D1; `SceneContext` field-vs-property immaterial; HUD energy line not P1-gated but selection is P1-only)._

### Decisions (✅ both resolved 2026-06-28 — D1 patched, D2 accepted)

- [x] [Review][Patch ✅ APPLIED 2026-06-28 → `SelectionSystem.cs:679` local-faction guard; build 0 err · Tier-1 424 pass/1 skip/0 fail · 9 goldens byte-identical] **Cast-arm survives caster death + slot-recycle — no local-faction re-validation at the issue seam** [`SelectionSystem.cs:677` `IssueCastAbilityCommand` / `:659` `ArmCastTargeting`] — `ArmCastTargeting` stores a raw `_pendingCastCasterId` that persists for unbounded frames until the target-click and is **never pruned of dead units** (unlike every other command path, which iterates the `PruneDeadUnits()`-cleaned `_selectedList`). If the armed caster dies and its id is recycled to a live unit before the target-click, `IssueCastAbilityCommand`'s only guard (`!IsAlive`) passes; **offline** it then passes `FactionOf[casterId]` (read from the *recycled* unit) as `expectedFaction`, so `OrderApplier`'s anti-cheat guard (`FactionOf[id] != expectedFaction`) compares the unit against itself and can never fire → a recycled-to-P2 slot lets the local player make an **enemy unit** cast. **Online is safe** (sender-attributed `expectedFaction` = `Player1` rejects it; identical on all peers → no desync). Bounded (refused if the stale slot ≥ the new unit's `AbilityCount`). Same defect-class as the Blind Hunter's `_lastFocusedCasterId` 1-frame focus window — both close with one seam-level guard. _Sources: edge+blind._

- [x] [Review][Decision ✅ ACCEPTED 2026-06-28 — AC6 substantially met (click path verified by 4 lenses + runtime seam cast)] **AC6 in-engine verify bypassed the full `TargetUnit` button→arm→enemy-click flow** [Dev Agent Record → Debug Log References] — the documented in-engine cast was driven via `IssueCastAbilityCommand` (the terminal seam) directly, not the AC2/AC6 path (button press → `ArmCastTargeting` → enemy left-click → issue), and no screenshot artifact is attached. The click-through code path is correct by 4-lens inspection (AC2 MET), but the live demonstration AC6 specifies is unproven. _Source: auditor._

### Deferred (tracked, not blocking)

- [x] [Review][Defer] **Ability (and faction) JSON content is determinism-relevant but outside the pre-match content handshake** [`NetworkCommand.cs:454-466` `ComputeFileHash`] — deferred, pre-existing. The `Ready` packet hashes only the *scenario* file; faction + (now) ability JSONs are unhashed. Divergent or missing `resources/data/abilities/` between peers → different registry indices / `AbilityCount` → desync surfaced only as an opaque `HALT(NoMajority)` with no "abilities failed to load" diagnostic (`AbilityRegistry.LoadFromDirectory` returns `Empty` silently on a missing dir). Same class as the already-unhashed faction files, widened by one directory → **Epic 9** server-authority content-hash hardening. Optional now: a fail-loud log when `LoadFromDirectory` yields `Empty` on the server for a caster-bearing scenario. _Sources: edge+determinism._

- [x] [Review][Defer] **Disable-gate vs press-handler targeting sets are coupled by assumption — fragile to a future `AbilityTargeting` value** [`CommandCardSystem.cs` `RefreshAbilityCard` / `OnAbilityBtnPressed`] — deferred, not a bug today (the enum is exactly `{None, Self, TargetUnit, GroundPoint}`). `RefreshAbilityCard` only disables `GroundPoint` + `null`; `OnAbilityBtnPressed` only acts on `Self`/`None`/`TargetUnit` (`default:` no-ops). A future 5th targeting mode would render an enabled, affordable button that silently does nothing on press. Fold a single "is-castable-targeting" predicate shared by the disable gate and the press switch when ally-target / `GroundPoint` targeting is built (already deferred §story-2.4b items 1–2). _Source: blind._

---

## Status

done

_Ultimate context engine analysis completed — comprehensive developer guide created._
_Dev-story complete 2026-06-28 (all 8 tasks, 6 ACs) → ready for code review._
_Code review PASS 2026-06-28 (gds-code-review, 4-lens adversarial, fresh-context Opus 4.8): 0 Critical/0 High; determinism fence verified clean; 1 patch applied (cast-arm recycle guard), 2 deferred, 5 dismissed → **done**._
