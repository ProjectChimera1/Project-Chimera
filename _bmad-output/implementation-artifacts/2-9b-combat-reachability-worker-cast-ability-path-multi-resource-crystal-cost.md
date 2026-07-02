---
baseline_commit: 9d827791d7904a3ff948c40a7420cfab6e2a4066
---

# Story 2.9b: Combat reachability — worker-cast ability path + multi-resource (crystal) cost

Status: review

<!-- Validation: optional. Created via gds-create-story: 5 parallel research agents (worker/gather sim mechanics ·
ability-cast spine + cost model · command-card UI + targeting/design-intent · determinism fence + golden baseline ·
git history) + direct source grounding (CommandCardSystem/BuildingSystem/EntityPlacer/ability JSON/golden harness
read in full). -->

## Story

As a player,
I want workers to cast abilities and units/abilities to charge multi-resource (ore + crystal) costs,
So that signature abilities can be paid for and cast as designed.

## Why this matters (source-verified)

The good news first: **the simulation and network layers already fully support this.** `AbilityCastSystem.TryCast`
(`godot/src/Effects/AbilityCastSystem.cs:173-198`) already checks Energy **and** Ore **and** Crystal before debiting
any of them, and already debits all three atomically on success — this has been true since Story 2.4a/2.4b, and
`ResourceStore.CanAffordCrystal`/`SpendCrystal` (`ResourceStore.cs:71,75`) are live production APIs, not a stub
(callers: `AbilityCastSystem`, `CommandCardSystem`'s ability card, `MainScene`'s HUD, and `SimChecksum` — Crystal has
been folded into the checksum since v2). Nothing in `AbilityCastSystem`, `OrderApplier.Apply`'s `CastAbility` case,
or `ModifierStore.TryDebitEnergy` branches on `UnitCategory` — **casting is already category-agnostic**. And the
cast never durably touches `CommandState` (`NetworkCommand.cs:139-140,268`: the prior `CommandState` is captured,
overwritten only for the duration of the switch, then restored before the order returns) — so a worker's
`GatherState`/`GatherTarget`/`CarryAmount`/`BuildTarget` are structurally unreachable by a cast.

The gap is entirely **UI, content, and spawn-path fidelity** — and it's explicitly pre-flagged in the live code.
`CommandCardSystem.cs:156-158`'s own comment reads:

> *"Story 2.4b: a focused P1 combat caster (≥1 resolved ability) shows the ability card. A unit that is BOTH a
> gatherer and ability-bearing → the worker card wins (**Decision C; worker-cast is Story 2.9b**), hence the
> `!workerSelected` guard."*

That guard (`CommandCardSystem.cs:159-165`) is the actual blocker: today, if a worker had an ability, its ability
card would be **suppressed** in favor of the worker (build) card — the two are mutually exclusive by construction
(same screen rect, `Panel.Visible` toggled). No shipped unit compounds this today: neither faction's worker
(`alpha_faction.json`'s `"worker"`/Acolyte, `beta_faction.json`'s `"forgehand"`/Cinderhand Thrall) has an
`"abilities"` field — zero content exercises the path. The design doc (`fma-faction-design.md:172`) independently
names the same gap: *"Worker-cast ability trigger surface | small | Gatherers are skipped by CombatSystem and only
have gather/build command paths... needs an ability-activation path for workers that doesn't exist."*

A second, sibling gap is explicitly folded into this story by the epic's own prose (`epics.md:1081`): *"wires
crystal-spend / multi-resource cost so **ability and unit costs** can charge crystal as well as ore."*
`BuildingSystem.TrainUnit` (`BuildingSystem.cs:298-344`) spends **ore only** — `def.CostCrystal` (present on every
`UnitDefinition`, e.g. beta's `wyvern: cost_crystal 100`) is never read there. Story 2.8's own story file flagged
this forward: *"beta wyvern has `cost_crystal: 100`, but crystal-spend isn't wired until 2.9b."*

A third, adjacent gap surfaced during research (not named by the epic, but load-bearing for the AC to hold for
Alec's actual content-authoring workflow): `EntityPlacer.DoSpawnWorker` (`EntityPlacer.cs:432-463`) is a documented
exception to the project's A2 single-mapper rule — it hand-copies 4 fields instead of calling
`EntityWorld.ApplyUnitDefinition`, with a Story-1.13 comment reasoning *"workers carry no combat stats."* That
reasoning no longer holds once a worker can carry `abilities`/`max_energy` — today, a worker **placed via the level
editor** (Alec's primary map-building tool) would get zero `AbilityCount`/`Energy`/`MaxEnergy` regardless of its
JSON, even though the identical unit spawned by a scenario/match-start seed (`ScenarioApplier.SpawnUnit`, which
already calls `ApplyUnitDefinition` unconditionally) works correctly. Shipping the AC without this fix would mean
"works in the golden test, silently inert the moment Alec places one in his map editor."

_Covers: FR-11, FR-12, AR-9, NFR-4. Depends on: 2.9a (done)._

## Acceptance Criteria

### AC1 — Worker-cast ability path (epic AC, verbatim + made testable)

**Given** a worker unit with an active ability and a multi-resource cost (ore + crystal) **When** the player casts
it and the faction can afford both resources **Then** the worker casts via the existing cast pipeline, both ore and
crystal are debited, and the ability is refused if either resource is insufficient **And** the cast resolves
deterministically and the worker's gather/build loop is not corrupted by the cast.

- **AC1.1** The command card shows an ability section for a focused P1 worker whenever `AbilityCount[focusId] > 0`,
  displayed **together with** (not instead of) the worker's build card — both panels visible simultaneously,
  non-overlapping (stacked, not the current full-overlap-then-hidden state).
- **AC1.2** Clicking an off-cooldown, affordable Self/None-targeted ability button on a worker's card issues the
  **same** `CastAbility` order a combat unit's button issues — through the existing `IssueCastAbilityCommand` →
  `OrderApplier.Apply` → `AbilityCastSystem.TryCast` pipeline, with **no caster-category branch anywhere in that
  pipeline** (confirmed already absent — this AC proves the existing sim contract extends to workers, it does not
  add a parallel path).
- **AC1.3** A cast atomically debits Energy **and** Ore **and** Crystal only when all three are affordable; if
  Energy, Ore, **or** Crystal is short, the cast is refused and **none** of the three is spent (the existing
  check-all-then-mutate-all contract, `AbilityCastSystem.cs:173-198`).
- **AC1.4** The worker's `GatherState`/`GatherTarget`/`CarryAmount`/`BuildTarget` are bit-for-bit unchanged by
  issuing and resolving a cast — a worker mid-gather-cycle (any `GatherState`) or mid-`Build` order continues its
  cycle exactly as if no cast had occurred, in the **same tick** the cast resolves.
- **AC1.5** Two runs of an identical worker-cast scenario produce byte-identical `SimChecksum` sequences.

### AC2 — Unit-training crystal spend (the sibling multi-resource gap, `epics.md`-scoped)

**Given** a trainable unit definition with a nonzero `cost_crystal` **When** a player trains it at a production
building **Then** training is refused unless the faction can afford **both** ore and crystal, and succeeds by
debiting both exactly once **And** neither resource is spent if either is insufficient (atomicity mirrors AC1.3).

- **AC2.1** `BuildingSystem.TrainUnit` checks `CanAffordOre` **and** `CanAffordCrystal` before spending either
  (today it checks/spends ore only — `def.CostCrystal` is read nowhere in `TrainUnit`, `BuildingSystem.cs:334-335`).
- **AC2.2** The command-card train-button preview greys out and shows `"[need crystal]"` when crystal-short — the
  same affordability-preview discipline the ability card already follows (`CommandCardSystem.cs:621-627`'s comment:
  *"IDENTICAL to the sim's refusal... so the greyed-out button never diverges from what the sim would refuse"*).
- **AC2.3** No regression: every existing trainable unit with `cost_crystal: 0` (the overwhelming majority of both
  rosters) trains exactly as before — the new check is a no-op for them.

### AC3 — Spawn-path fidelity: editor-placed workers get their authored ability/energy state

**Given** a worker definition authoring `abilities`/`max_energy` **When** the unit is placed via the in-app level
editor (not just a match-start scenario spawn) **Then** the placed worker has the correct
`AbilityId`/`AbilityCount`/`Energy`/`MaxEnergy` — and the already-hand-copied `Category`/`CollisionRadius`/
`SeparationPriorityOf`/`FeedbackProfile` — exactly as a scenario-spawned worker would **And** no existing
editor-placed-worker behavior (free supply, starting gather state, mesh assignment) regresses.

- **AC3.1** `EntityPlacer.DoSpawnWorker` routes through the single `EntityWorld.ApplyUnitDefinition` mapper (the A2
  rule) instead of hand-copying 4 fields, mirroring `DoSpawnCombatUnit` (`EntityPlacer.cs:485-488`) and
  `ScenarioApplier.SpawnUnit` (`ScenarioApplier.cs:210`).
- **AC3.2** The existing `SupplyCost[id] = 0` override is **preserved**, applied **after** the mapper call —
  editor-placed workers stay free-supply, byte-for-byte unchanged from today (this is a deliberate, pre-existing
  divergence from `ScenarioApplier.SpawnUnit`, which does not zero a worker's `SupplyCost` — out of scope to
  reconcile here; see Regression risks).

### AC4 — Determinism & zero regression (explicit; prevents "completion lies")

**Given** the change set **When** the Tier-1 golden gate runs **Then** all **13** golden checksums are
byte-identical, `SimChecksum.AlgoVersion` stays **8**, `CanonicalModelHash` AlgoVersion stays **2**, the known-state
pin `0x983D39AE` is unchanged, and `VersionStampConsistencyTests` (8/2/1/2 + "0.1") passes. **No fold, no
AlgoVersion bump, no re-record of the existing 13 goldens.**

- **AC4.1** No new `EntityWorld` SoA field is introduced. Every field this story touches — `AbilityId`/
  `AbilityCount`/`Energy`/`MaxEnergy`/`PendingCastSlot`/`PendingCastTarget`/`CommandState`/`GatherState`/
  `ResourceStore.Ore`/`ResourceStore.Crystal` — already exists and is already folded-or-not per the existing fence
  (Ore/Crystal folded since v2; `AbilityCooldownTicks` since v7; `CommandState`/`GatherState` never folded, reached
  only transitively). This story adds new **callers** into existing machinery, not new state.
- **AC4.2** New coverage lands as **one** new golden (`worker-cast-crystal-cost`) plus Tier-1 unit tests — never by
  editing the existing 13.

### AC5 — In-engine verification

**Given** the shipped `matter_infusion` ability on the Acolyte **When** a P1 Acolyte is selected in a live
Play-mode skirmish **Then** the command card shows both the build card and the ability button simultaneously, the
button reflects live Ore/Crystal affordability, and casting it debits both resources while the worker keeps
gathering. `/godot-verify` confirms via node-state reads (precedent: 1.9b's LAN gate, 1.11's AC2b, 2.9a's Task 10 —
the mechanism is already golden-proven byte-for-byte; a fragile multi-step click chain is not required to prove it
again in-engine).

## Decisions

**Baked in (deterministic-rule / minimal-slice calls — applied, not re-asked):**

1. **Proof-ability = new content `matter_infusion`, attached to Alpha's Acolyte only** (not Beta's `forgehand`).
   Self-targeted `apply_modifier` (a brief move-speed buff — thematically "alchemical reagents quicken the
   caster"), costing ore **and** crystal, so it needs **zero** new UI click-flow (`Self`/`None` targeting resolves
   immediately on button press, `CommandCardSystem.cs:678-682`; a `TargetUnit`-targeted worker heal would additionally
   need ally-targeting, since the existing cast-click resolver is enemy-only — `SelectionSystem.cs:244`'s
   `FindNearestEnemyUnit`, "Decision B" — out of scope here). Deliberately **not** the design doc's flavor-matched
   "Mend Matter" (HP-self-cost, ally-heal) — that is the **Equal Exchange** mechanic (`DirectHpDelta` self-cost)
   Story 2.10 is scoped to build; giving 2.9b's proof-ability an HP-cost would blur the two stories' determinism
   fences and preempt 2.10's design. 2.9b only needs to prove the ore+crystal **mechanism**, not ship the
   lore-accurate ability.
2. **`_abilityPanel` visibility no longer excludes a worker.** Drop the `!workerSelected` term from `abilitySelected`
   (`CommandCardSystem.cs:159-165`); when both are true, `_abilityPanel` repositions to **stack above**
   `_workerPanel` (two cached Y-positions computed once in `BuildAbilityPanel`, not recomputed every frame) instead
   of overlapping it. A non-worker combat caster's ability panel keeps its original position — only the
   co-displayed case moves.
3. **`TrainUnit`'s crystal check follows the same atomic check-both-before-spend-either contract `TryCast` already
   uses** (`AbilityCastSystem.cs:178-180` checks Energy→Ore→Crystal fully before debiting any) — no partial spend
   if crystal is short after ore already passed.
4. **`EntityPlacer.DoSpawnWorker` routes through `ApplyUnitDefinition`, closing the Story-1.13 exception** (its
   "workers carry no combat stats" rationale is obsolete now that workers can carry ability/energy stats) — while
   explicitly **preserving** the post-mapper `SupplyCost[id] = 0` override, since that is a separate, pre-existing,
   deliberate divergence from `ScenarioApplier.SpawnUnit` and not this story's concern to reconcile.
5. **No new SoA field, no fold, `AlgoVersion` stays 8.** Every touched field is already folded (Ore/Crystal since
   v2, `AbilityCooldownTicks` since v7) or already correctly unfolded (`CommandState`/`GatherState`, reached only
   transitively) — this story only adds new call sites into existing, already-hashed machinery, exactly like Story
   2.1's `DirectHpDelta` mutating already-hashed `Health` added no fold. ([[chimera-checksum-fold-timing-rule]])
6. **The new golden/Tier-1 proof-ability reuses the existing `AbilityTestAbilities.SelfHeal(costEnergy, costOre,
   costCrystal, cooldownSec, heal)` helper** (defined in `AbilityTestSupport.cs:56-62`) — it already parametrizes
   crystal cost; no new C# test-support code is needed. (It need not match `matter_infusion.json`'s shape — the
   shipped content and the test fixture serve different purposes, exactly like `battle_fury.json` vs.
   `AbilityTestAbilities.BattleFury()` are separate, not derived from one another.)

**Needs Alec's confirmation (recommended defaults baked in so the dev can start):**

- **D-1 (balance numbers for `matter_infusion`):** recommended default `cost_energy: 15, cost_ore: 15,
  cost_crystal: 10, cooldown: 20s, move_speed_delta: +1, duration_ticks: 90 (3s), stacking: Refresh, max_stacks: 1`.
  Pure data — retune freely; nothing else depends on the exact numbers as long as `cost_ore > 0` and
  `cost_crystal > 0` (AC1's literal requirement).
- **D-2 (Beta parity):** recommended default = **defer**. Beta's `forgehand` gets no worker ability in this story;
  AC1 only requires "a worker unit" (singular) to prove the mechanism, and Story 2.10 will need to author
  faction-specific signature content for both factions anyway. Revisit if Alec wants symmetrical rosters sooner.
- **D-3 (stacked-panel pixel gap):** recommended default = 8px gap between the two 175px-tall panels
  (`_abilityPanel` at `vpSize.Y - 185f - 175f - 8f` when co-displayed). Cosmetic — adjust freely during
  `/godot-verify` if it looks cramped or leaves too much dead space at common resolutions.

## Tasks / Subtasks

- [x] **Task 1 — Content: author `matter_infusion` and attach it to the Acolyte** (AC: 1, D-1)
  - [x] Create `godot/resources/data/abilities/matter_infusion.json`, mirroring `battle_fury.json`'s shape:
    ```json
    {
      "id": "matter_infusion",
      "display_name": "Matter Infusion",
      "targeting": "Self",
      "cost_energy": 15,
      "cost_ore": 15,
      "cost_crystal": 10,
      "cooldown": 20,
      "effect": {
        "kind": "apply_modifier",
        "modifier": {
          "id": 1002,
          "duration_ticks": 90,
          "stacking": "Refresh",
          "max_stacks": 1,
          "move_speed_delta": 1,
          "status": "None"
        }
      }
    }
    ```
    (Modifier id `1002` — `1001`/`2001` are already taken by `battle_fury`/`aura_guard`; pick any unused id.)
  - [x] In `godot/resources/data/factions/alpha_faction.json`, add to the `"worker"`/Acolyte unit block (after
    `vision_range`, matching the `mage` entry's field placement at `alpha_faction.json:131-134`):
    ```json
    "abilities": ["matter_infusion"],
    "max_energy": 20
    ```
  - [x] Do **not** touch `beta_faction.json`'s `"forgehand"` (D-2).

- [x] **Task 2 — Sim: `BuildingSystem.TrainUnit` spends crystal atomically (Godot-free)** (AC: 2, 2.1, 2.3)
  - [x] In `TrainUnit` (`BuildingSystem.cs:298-344`), replace the ore-only spend (`:334-335`) with a check-both-then-
    spend-both block, mirroring `AbilityCastSystem.TryCast`'s ordering:
    ```csharp
    float costOre     = def?.CostOre     ?? FALLBACK_COST_ORE;
    float costCrystal = def?.CostCrystal ?? 0f;
    if (!resources.CanAffordOre(faction, Fixed.FromFloat(costOre))) return false;
    if (!resources.CanAffordCrystal(faction, Fixed.FromFloat(costCrystal))) return false;
    resources.SpendOre(faction, Fixed.FromFloat(costOre));
    resources.SpendCrystal(faction, Fixed.FromFloat(costCrystal));
    ```
    Both checks **before** either spend — a unit whose ore passes but crystal fails must spend **nothing** (the
    partial-spend bug this ordering prevents).
  - [x] `TrainUnitCommand` (`BuildingSystem.cs:372-378`, the lockstep exec-tick entry point) calls `TrainUnit`
    unchanged — no signature change needed, the fix is entirely inside `TrainUnit`.
  - [x] Confirm `FALLBACK_COST_ORE`'s sibling — is there a `FALLBACK_COST_CRYSTAL`? If not, `0f` for a null `def` is
    correct (an unresolvable/empty-category def already returns before this point in practice, but keep the
    null-coalescing symmetric with the existing `costOre` line for defensive consistency).

- [x] **Task 3 — Presentation: `CommandCardSystem` — worker+ability co-display, train-button crystal parity** (AC: 1.1, 2.2)
  - [x] In `_Process` (`CommandCardSystem.cs:132-174`), drop `&& !workerSelected` from the `abilitySelected`
    computation (`:159-165`) and update the stale comment (`:156-158`) to describe the new contract (both panels
    show together for a worker with `AbilityCount > 0`).
  - [x] In `BuildAbilityPanel` (`CommandCardSystem.cs:547-589`), after computing `vpSize`, cache two positions as new
    private fields (e.g. `_abilityPanelNormalPos`, `_abilityPanelStackedPos`):
    ```csharp
    _abilityPanelNormalPos  = new Vector2(10f, vpSize.Y - 185f);
    _abilityPanelStackedPos = new Vector2(10f, vpSize.Y - 185f - 175f - 8f); // D-3: 8px gap above the worker card
    ```
    (Keep the existing `_abilityPanel.Position = _abilityPanelNormalPos;` assignment at panel-build time — this is
    just the initial position before any co-display is known.)
  - [x] In `_Process`, right before `_abilityPanel.Visible = abilitySelected;` (`:169`), reposition when co-displayed:
    ```csharp
    if (abilitySelected)
        _abilityPanel.Position = workerSelected ? _abilityPanelStackedPos : _abilityPanelNormalPos;
    ```
  - [x] In `RefreshCard`'s train-button loop (`CommandCardSystem.cs:248-282`), add a crystal-affordability check
    mirroring the existing `costOre`/`canAfford` local pattern (not `RefreshAbilityCard`'s `Fixed.FromInt` style —
    match this method's own existing `Fixed.FromFloat(costOre)` local convention for a minimal diff):
    ```csharp
    int  costCrystal = def.CostCrystal;
    bool crystalOk   = _resources.CanAffordCrystal(faction, Fixed.FromFloat(costCrystal));
    ```
    Fold into the existing `Disabled`/`note` computation (`:271-278`):
    ```csharp
    _trainBtns[i].Disabled = isTraining || !prereqsMet || !canAfford || !hasSupply || !crystalOk;
    string costSuffix = costCrystal > 0 ? $" · {costCrystal} crystal" : ""; // AC2.3: unchanged text when free
    string note = !prereqsMet ? $"[need: {missingPrereq}]"
                : !canAfford  ? "[need ore]"
                : !crystalOk  ? "[need crystal]"
                : !hasSupply  ? "[supply full]"
                : $"{costOre} ore{costSuffix} · {trainTime:F0}s";
    ```
    `costSuffix` is empty for every existing `cost_crystal: 0` unit, so their button text is byte-for-byte
    unchanged — AC2.3.

- [x] **Task 4 — Presentation: `EntityPlacer.DoSpawnWorker` routes through `ApplyUnitDefinition`** (AC: 3, 3.1, 3.2)
  - [x] Replace the 4-field hand-copy block (`EntityPlacer.cs:446-456`) with a call to `_world.ApplyUnitDefinition(id,
    def)` when `def != null` — mirroring `DoSpawnCombatUnit` (`:485-488`) and `ScenarioApplier.SpawnUnit` (`:210`):
    ```csharp
    // Story 2.9b: route through the single def→SoA mapper (A2 rule) so a placed worker gets its authored
    // abilities/max_energy — and Category/CollisionRadius/SeparationPriorityOf/FeedbackProfile as before —
    // exactly like ScenarioApplier.SpawnUnit and DoSpawnCombatUnit already do. Supersedes the Story 1.13 hand-copy
    // exception, whose "workers carry no combat stats" rationale no longer holds now that workers can cast.
    if (def != null)
    {
        _world.ApplyUnitDefinition(id, def);
    }

    // Worker-specific state, applied AFTER the mapper so these intentionally override it (workers are always free
    // supply, unlike combat units — a deliberate divergence from ScenarioApplier.SpawnUnit, out of scope to
    // reconcile here) and seed the gather loop's starting state.
    _world.SupplyCost[id]    = 0;
    _world.GatherState[id]   = GatherState.Idle;
    _world.CarryCapacity[id] = Fixed.FromFloat(WORKER_CARRY);
    ```
  - [x] Confirm the resulting method still assigns `MeshType` afterward exactly as today (`:458-459`, unchanged).
  - [x] **Do not** "fix" the Acolyte's now-live `attack_damage: 5`/`attack_range: 1.5`/etc. stats flowing into
    `EffectiveAttackDamage` — confirmed inert (`CombatSystem`'s gatherer exemption at `CombatSystem.cs:86-105` keys
    on `GatherState`, never on damage); zeroing the JSON would regress the "explicit worker fight-back is a future
    feature" intent already documented in that file's own comment.

- [x] **Task 5 — Tier-1 tests (xUnit, Godot-free)** (AC: 1.2, 1.3, 1.4, 2.1, 2.3, 4)
  - [x] **Worker-cast affordability + atomicity** (new file `godot/ProjectChimera.Sim.Tests/Effects/WorkerCastTests.cs`,
    reusing `AbilityTestAbilities.SelfHeal(...)` and the existing ability-cast test scaffolding in
    `AbilityTestSupport.cs` — mirror `AbilityAffordabilityTests.cs`'s construction pattern): a
    `GatherState.Idle` (or `.Gathering`) worker entity with `AbilityTestAbilities.SelfHeal(costEnergy:15, costOre:15,
    costCrystal:10, cooldownSec:20, heal:20)` registered and affordable → casts, all three resources debited exactly
    once, `Health` moves. **Refusal + atomicity:** crystal short (ore/energy sufficient) → refused, **all three**
    resources unchanged (no partial spend); ore short (crystal/energy sufficient) → same; energy short → same.
  - [x] **Gather/build loop non-corruption (AC1.4):** a `GatherState.MovingToResource` (or `.Gathering`) worker casts
    mid-cycle → `GatherState`/`GatherTarget`/`CarryAmount` are identical immediately before vs. after the tick the
    cast resolves. A worker with `CommandState == UnitCommand.Build` and `BuildTarget` set casts → `CommandState`
    and `BuildTarget` are unchanged after the tick (prove via `OrderApplier.Apply` then one `AbilityCastSystem.Tick`
    / full `SimulationHost.StepOnce`).
  - [x] **`TrainUnit` crystal atomicity** (extend `godot/ProjectChimera.Sim.Tests/Economy/ProductionSelectionTests.cs`):
    affordable ore+crystal → trains, both spent exactly once. Sufficient ore, insufficient crystal → refused,
    **neither** resource spent (the partial-spend regression this task's ordering prevents). Sufficient crystal,
    insufficient ore → refused, neither spent. A `cost_crystal: 0` unit trains exactly as before (regression guard,
    AC2.3) — use the existing test fixtures/factions already in that file where possible.
  - [x] **Determinism (AC4):** run the golden gate — all **13** byte-identical; `AlgoVersion == 8`; pin `0x983D39AE`
    unchanged; `VersionStampConsistencyTests` 8/2/1/2 passes.

- [x] **Task 6 — New golden scenario: `worker-cast-crystal-cost`** (AC: 1.5, 4.2)
  - [x] Add `godot/ProjectChimera.Sim.Tests/Golden/WorkerCastCrystalCostScenario.cs`, closely mirroring
    `GoldenScenario.cs`'s worker+node setup (positions/rates) and `AbilityCastScenario.cs`'s registry+cast-schedule
    wiring:
    ```csharp
    public static class WorkerCastCrystalCostScenario
    {
        public const int DefaultTicks = 300;
        public const int WorkerId = 0; // created first

        public static GoldenHarness Build()
        {
            var registry = new AbilityRegistry(new[] {
                AbilityTestAbilities.SelfHeal(costEnergy: 15, costOre: 15, costCrystal: 10, cooldownSec: 20, heal: 20)
            });
            var host = SimulationHost.Create(
                NullLogSink.Instance, new FactionRegistry(2),
                new FactionDefinition(), new FactionDefinition(), registry: registry);
            host.ChecksumInterval = 1;

            int worker = PopulateScenario(host.World, host.Nodes, host.Resources, registry);
            host.ScenarioDirector.LoadScenario(new ScenarioData());
            return new GoldenHarness(host, worker);
        }

        private static int PopulateScenario(EntityWorld world, ResourceNodeStore nodes,
            ResourceStore resources, AbilityRegistry registry)
        {
            int worker = world.Create(V(-12, 0, 4), Faction.Player1, Fixed.FromInt(40), Fixed.FromInt(3));
            world.GatherState[worker]   = GatherState.Idle;
            world.CarryCapacity[worker] = Fixed.FromInt(20);
            world.AbilityId[worker * EntityWorld.MAX_ABILITIES_PER_UNIT + 0] = registry.IndexOf("test_heal");
            world.AbilityCount[worker] = 1;
            world.MaxEnergy[worker] = Fixed.FromInt(15);
            world.Energy[worker]    = Fixed.FromInt(15);

            nodes.Create(V(-12, 0, 8), Fixed.FromInt(500), Fixed.FromInt(7), 3);
            resources.FactionBase[(int)Faction.Player1] = V(-14, 0, 0);
            resources.AddOre(Faction.Player1, Fixed.FromInt(200));
            resources.AddCrystal(Faction.Player1, Fixed.FromInt(50));
            return worker;
        }

        public static void ApplyScheduleStep(SimulationHost host, int i)
        {
            if (i == 0)
                OrderApplier.Apply(host.World,
                    new UnitOrder(WorkerId, UnitCommand.CastAbility, Fixed.FromRaw(0), Fixed.FromRaw(-1)),
                    Faction.Player1);
        }

        private static FixedVec3 V(int x, int y, int z) =>
            new FixedVec3(Fixed.FromInt(x), Fixed.FromInt(y), Fixed.FromInt(z));
    }
    ```
    The scenario proves BOTH halves of AC1 in one run: the cast at tick 1 visibly moves `Health`/`Energy`/`Ore`/
    `Crystal`, and the worker's ongoing gather-deposit cycle (visible via `Ore[P1]` climbing further over the
    remaining ~290 ticks) proves the loop was never interrupted.
  - [x] Add `WorkerCastCrystalCostGoldenTests.cs`, copying `AbilityCastGoldenTests.cs`'s **custom record loop**
    pattern exactly (`ApplyScheduleStep` then `StepOnce` each iteration — do not use the generic `RunAndRecord`,
    which has no schedule hook): the four standard facts (two in-process runs byte-identical, matches committed
    golden, sequence evolves / non-vacuous, the record helper).
  - [x] Follow the first-golden chicken-and-egg recording procedure from 2.9a's Debug Log (placeholder `.txt` →
    build → `CHIMERA_GOLDEN_RECORD=1 dotnet test --filter FullyQualifiedName~WorkerCastCrystalCostGolden` → rebuild
    to re-embed) and register the new `.golden.txt` as `EmbeddedResource` in
    `ProjectChimera.Sim.Tests.csproj` (mirrors 2.9a's Task 9).
  - [x] While here: `SimChecksumCoverageGuardTest.cs:34`'s doc comment already says "the 10 goldens stay
    byte-identical" — stale since 2.9a took the count to 13. Update it to 14 (13 existing + this story's new one).

- [x] **Task 7 — In-engine verification (`/godot-verify`)** (AC: 5)
  - [x] Boot a Play-mode skirmish (the match-start seed already places P1 workers; once Task 1 lands, Alpha's
    Acolytes carry `matter_infusion` with no map/scenario change needed). Select a P1 Acolyte → confirm **both** the
    worker (build) card and the ability card render simultaneously, non-overlapping, at the stacked position (D-3).
  - [x] Read live node-state: confirm the ability button's afford/cooldown text matches `Ore[P1]`/`Crystal[P1]` (grey
    + `"[need ore]"`/`"[need crystal]"` when a resource is deliberately drained below cost via test scaffolding, if
    needed to force the negative case; otherwise confirm the positive affordable state).
  - [x] Click the ability button → confirm `Ore[P1]`/`Crystal[P1]` drop by the authored costs and the worker (visibly
    or via node-state `GatherState`) keeps gathering afterward — not stuck.
  - [x] Select a P1 production building with a `cost_crystal > 0` unit available (e.g. Beta's Aviary/`wyvern` in a
    Beta-side check, or any faction/building pairing with nonzero crystal cost) → confirm the train button shows
    `"[need crystal]"` when crystal is insufficient and trains successfully (debiting crystal) when affordable.
  - [x] Revert any test scaffolding (forced resource drains, temporary faction swaps) after capturing evidence — this
    story is content + wiring, not scenario/map edits, so nothing should be left changed on disk from this task.

## Dev Notes

### Current state — precise, source-verified

**The cast pipeline is already worker-agnostic.** `AbilityCastSystem.TryCast` (`AbilityCastSystem.cs:162-218`) gates
only on slot bounds, registry lookup, cooldown, and Energy/Ore/Crystal affordability (`:173-180`) — no
`UnitCategory`/`EffectiveAttackDamage` check exists anywhere in the cast pipeline (`OrderApplier`'s `CastAbility`
case, `TryCast`, or `ModifierStore.TryDebitEnergy`). `SelectionSystem.ArmCastTargeting`/`IssueCastAbilityCommand`
(`SelectionSystem.cs:686-719`) likewise carry no category restriction. `AbilityDefinition` (`AbilityDefinition.cs:
43-56`) already models `CostEnergy`/`CostOre`/`CostCrystal`/`Cooldown` as four independent fields — this story adds
**zero** new cost-model fields.

**The only sim-adjacent gaps are content and two narrow, well-precedented call sites.** (1) No shipped worker
authors an `abilities`/`max_energy` field (Task 1, pure data). (2) `BuildingSystem.TrainUnit` only checks/spends ore
(Task 2, an 8-line change mirroring the existing `SpendOre` call). (3) `EntityPlacer.DoSpawnWorker` bypasses
`ApplyUnitDefinition` (Task 4, replacing a 4-field hand-copy with the one-line mapper call every other def-based
spawn path already uses).

**The UI gate is a single, already-named boolean term.** `CommandCardSystem.cs:160`'s `&& !workerSelected` is the
entire blocker for AC1.1 — the panels, buttons, and refresh methods for both the worker card and the ability card
already fully exist (`BuildWorkerPanel`/`RefreshWorkerCard`, `BuildAbilityPanel`/`RefreshAbilityCard`) and already
use independent "last focused" trackers (`_lastFocusedWorkerId` vs. `_lastFocusedCasterId`), so co-displaying them
for the same `focusId` needs no new state-tracking — only visibility + non-overlapping position.

**`CommandState` is never durably `CastAbility`.** `OrderApplier.Apply` (`NetworkCommand.cs:139-140`) captures
`prior = world.CommandState[id]` before the switch and the `CastAbility` case (`:257-274`) explicitly restores
`world.CommandState[id] = prior;` before returning (`:268`) — a cast never persists as a `CommandState` value, so
`GatheringSystem` (which only reads `CommandState` to special-case `Build`, `GatheringSystem.cs:37-41`) and
`CombatSystem`'s gatherer-exemption (keyed on `GatherState != Inactive`, `CombatSystem.cs:86-105`) can never observe
it. This is why AC1.4 needs **no new sim code** — it is already structurally guaranteed; the tests in Task 5 prove
the existing guarantee, they don't add one.

**Tick order** (`SimulationHost.cs:112-134`): `[0] BuildingSystem [1] GatheringSystem [2] MovementSystem
[3] AbilityCastSystem [4] ModifierSystem [5] CombatSystem [6] ProjectileSystem ...`. `OrderApplier.Apply` runs
**between** ticks (outside the loop), so a cast issued for tick N is fully applied (CommandState restored,
`PendingCastSlot` set) before `GatheringSystem[1]` runs for tick N — meaning the gather step for the very tick the
cast resolves in sees the unit's normal, undisturbed `CommandState`.

**`Energy` starts full, does not regenerate.** `EntityWorld.Create()` zeroes `Energy`/`MaxEnergy`
(`EntityWorld.cs:529-530`); `ApplyUnitDefinition` sets `MaxEnergy[id] = Fixed.FromFloat(def.MaxEnergy)` then
`Energy[id] = MaxEnergy[id]` (`:633-634`) — a unit starts at full energy and there is no passive regen system
(documented, pre-existing, out-of-scope gap from Story 2.4a's deferred-work list). Do not add energy regen in this
story; it is orthogonal to proving the worker-cast + crystal-cost mechanism.

### Determinism notes (no fold, `AlgoVersion` stays 8, 13 goldens byte-identical)

- **No new SoA field.** `Energy`/`MaxEnergy`/`AbilityId`/`AbilityCount`/`PendingCastSlot`/`PendingCastTarget` all
  shipped in Story 2.4a; `ResourceStore.Ore`/`Crystal` shipped in Story 1.3b/2.4a. This story adds new callers
  (a worker entity reaching the existing cast path; `TrainUnit` reaching the existing `Crystal` API) — not new
  state — exactly like Story 2.1's `DirectHpDelta` mutating already-hashed `Health` added zero fold.
- **`Ore`/`Crystal` have been folded since v2** (`SimChecksum.cs:199-200`) — a worker-cast's or a crystal-costed
  train's resource debit is already inside the hashed set with no version-stamp change required.
- **`CanonicalModelHash` (AlgoVersion 2) is untouched** — this story adds no `ScenarioUnit`/start-state field; the
  new `abilities`/`max_energy` on the Acolyte resolve through `UnitDefinition`/`ApplyUnitDefinition`, not the
  canonical start-state hash.
- **Existing 13 goldens stay byte-identical (verify empirically):** none of them spawn a worker with an authored
  ability or issue a `Train` order for a nonzero-`cost_crystal` unit — Task 2/3's new checks are no-ops for every
  existing scenario. A moved golden means a leaked behavior/fold change; **fix it, don't re-baseline.**
- **New coverage lands as the single `worker-cast-crystal-cost` golden** (Task 6) plus Tier-1 unit tests (Task 5) —
  never by editing the existing 13.
- **Fixed / ascending-id everywhere:** no new code in this story runs inside the tick loop with anything but
  `Fixed`/int math — `TrainUnit`'s new crystal check is a direct mirror of its existing ore check (same types, same
  call shape); the worker-cast path is 100% pre-existing sim code with zero edits.

### The determinism fence (git status must NOT touch — a change here means a fold/behavior leak slipped in)

`godot/src/Core/SimChecksum.cs` (AlgoVersion 8 + folded set/order) · `godot/ProjectChimera.Sim.Tests/Golden/
SimChecksumCoverageGuardTest.cs` (`ExpectedV8Hash = 0x983D39AE` — the doc-comment count update in Task 6 is the one
allowed touch) · `godot/ProjectChimera.Sim.Tests/Meta/VersionStampConsistencyTests.cs` (8/2/1/2 + "0.1") ·
`godot/src/Core/Definitions/CanonicalModelHash.cs` (AlgoVersion 2) · and all **13** existing
`godot/ProjectChimera.Sim.Tests/Golden/*.golden.txt` files (`ability-cast-scenario`, `ability-domain-filter-
scenario`, `ai-active-scenario`, `anti-building-scenario`, `combat-air-ground-scenario`, `command-vocabulary-
scenario`, `formation-separation-scenario`, `golden-applier-scenario`, `golden-multifaction`, `golden-scenario`,
`modifier-scenario`, `passive-scenario`, `same-tick-tie-break`). Adding the one **new** `worker-cast-crystal-cost-
scenario.golden.txt` is expected; touching any of the 13 is not.

### Reuse — do NOT reinvent

- **`AbilityCastSystem.TryCast`'s existing Energy→Ore→Crystal check-then-atomic-debit** (`:173-198`) — do not build
  a parallel worker-specific cast path or a worker-specific affordability check. The sim layer needs zero changes.
- **`ResourceStore.CanAffordCrystal`/`SpendCrystal`** (`:71,75`) — already exist, already mirror the Ore API 1:1.
  `TrainUnit`'s fix (Task 2) is a straight copy of its own existing `costOre`/`SpendOre` lines.
- **`RefreshAbilityCard`'s crystal-afford display pattern** (`CommandCardSystem.cs:626-627`) — `RefreshCard`'s
  train-button fix (Task 3) mirrors the same idea in that method's own existing `Fixed.FromFloat` local style, not
  a copy-paste of `RefreshAbilityCard`'s `Fixed.FromInt` style (different method, keep its existing convention).
- **`AbilityTestAbilities.SelfHeal(costEnergy, costOre, costCrystal, cooldownSec, heal)`** (defined in
  `AbilityTestSupport.cs:56-62`) — already parametrized for crystal cost; use it directly for both Task 5's unit
  tests and Task 6's golden. Do not add a new `MatterInfusion()` C# test-fixture method — it isn't needed.
- **`ScenarioApplier.SpawnUnit`'s `ApplyUnitDefinition`-then-Worker-extras shape** (`ScenarioApplier.cs:210-224`) —
  the exact template `DoSpawnWorker`'s fix (Task 4) follows, adapted to preserve the existing `SupplyCost=0`
  override (a documented, deliberate divergence — see Regression risks).
- **`GoldenScenario.cs`'s worker+node+FactionBase+starting-Ore seeding** and **`AbilityCastScenario.cs`'s
  registry+direct-SoA-wiring+`OrderApplier`-scheduled-cast pattern** — Task 6's new golden composes these two
  existing patterns; it invents no new golden-harness machinery.

### Regression risks (must not break)

- **`DoSpawnWorker`'s `SupplyCost[id] = 0` must be applied AFTER `ApplyUnitDefinition`**, not before — the mapper
  would otherwise set `SupplyCost` from `def.Supply` (=1 for both workers), silently making editor-placed workers
  consume supply for the first time (an undocumented behavior change this story must not introduce).
- **`_abilityPanel`'s repositioning must not move it for a non-worker combat caster.** Only reposition to the
  stacked Y when `workerSelected && abilitySelected` are both true; a standalone ability-bearing combat unit keeps
  the panel at its original `vpSize.Y - 185f` position, unchanged from today.
- **`TrainUnit`'s crystal check must check both resources before spending either** — do not spend ore, then
  discover crystal is short (a partial-spend bug the AC1.3/AC2.1 atomicity contract exists specifically to
  prevent). Mirror `TryCast`'s check-all-then-mutate-all ordering exactly.
- **Scope is the Acolyte only (D-2)** — do not give Beta's `forgehand`, or any other unit, a new ability as a side
  effect of this story.
- **The Acolyte's `attack_damage`/`attack_range`/etc. JSON fields becoming live via `ApplyUnitDefinition` (Task 4)
  is confirmed inert** — `CombatSystem`'s gatherer exemption (`:86-105`) is keyed on `GatherState`, never on
  damage. Do not "fix" this by zeroing the JSON stats or adding a defensive check; there is nothing to fix.
- **`AbilityCount[id] > 0` must not be (mis)read elsewhere as "this is a combat unit."** Grep confirms no other
  system branches on `AbilityCount` to distinguish caster-vs-worker — only `CommandCardSystem`'s (now-removed)
  `!workerSelected` term did. No other code needs updating for this reason.
- **Do not touch `EntityPlacer.RestoreUnit`** (editor undo/redo) — it has the same `ApplyUnitDefinition`-bypass
  gap for ability/energy state as `DoSpawnWorker` had, but it is a separate, larger, already-tracked concern
  (`UnitSnapshot` widening, `EntityPlacer.cs:1120-1123`'s own comment, and 2.9a's precedent of deferring the
  identical class of gap for `AttackDomainOf`). Out of scope here.
- **`BuildingSystem.SpawnTrainedUnit`** already calls `ApplyUnitDefinition` for any resolved def
  (`BuildingSystem.cs:204-221`, `if (def != null)` with an else-fallback) — it needs no change. (It is currently
  moot for workers specifically, since `GetProductionUnits` returns empty for `BuildingType.CommandCenter` — no
  live UI path trains additional workers today; not this story's concern to change.)

### Testing standards

- **Tier-1** = xUnit, Godot-free (`ProjectChimera.Sim.Tests`) — the worker-cast affordability/atomicity/non-
  corruption tests and the `TrainUnit` crystal-atomicity tests in Task 5. All sim logic testable without Godot.
- **One new golden** (Task 6) for full-path determinism proof — reuses two already-established scenario patterns,
  invents no new harness machinery.
- **`EntityPlacer.DoSpawnWorker` and `CommandCardSystem` are Godot-coupled presentation code** (`src/UI/`, `using
  Godot`) — outside Tier-1's Godot-free surface. Their correctness is proven by `/godot-verify` (Task 7,
  node-state-driven, precedent 1.9b/1.11/2.9a) plus code inspection, not by xUnit.
- **Prove each new gate has teeth:** the atomicity tests must show a partial-spend would be a regression (assert
  the short resource stays unspent AND the affordable-but-not-yet-checked resource ALSO stays unspent) — a test
  that only checks "training refused" without checking "nothing was spent" would miss the exact bug this task
  fixes.

### Project Structure Notes

- **Sim (Godot-free, `Fixed`/int):** `godot/src/Economy/BuildingSystem.cs` (`TrainUnit` crystal check). No other sim
  file changes — the cast pipeline, `ResourceStore`, and `AbilityDefinition` are unmodified.
- **Presentation (`using Godot`, Tier-1 can't cover → Task 7):** `godot/src/UI/CommandCardSystem.cs` (worker+ability
  co-display, train-button crystal parity), `godot/src/UI/EntityPlacer.cs` (`DoSpawnWorker` → `ApplyUnitDefinition`).
- **Data:** `godot/resources/data/abilities/matter_infusion.json` (new), `godot/resources/data/factions/
  alpha_faction.json` (Acolyte gains `abilities`/`max_energy`).
- **Tests:** `godot/ProjectChimera.Sim.Tests/Effects/WorkerCastTests.cs` (new), `godot/ProjectChimera.Sim.Tests/
  Economy/ProductionSelectionTests.cs` (extended), `godot/ProjectChimera.Sim.Tests/Golden/
  WorkerCastCrystalCostScenario.cs` + `WorkerCastCrystalCostGoldenTests.cs` + `worker-cast-crystal-cost-
  scenario.golden.txt` (new), `ProjectChimera.Sim.Tests.csproj` (register the new golden as `EmbeddedResource`).
  All touched sim directories (`src/Economy/`, `src/Effects/`) are already covered by `SimSources.props` — no
  props edit needed (unlike the 2.1 `src/Effects` lesson, which applied when that directory was net-new).

### Project Context Rules (from `_bmad-output/project-context.md`)

- **Sim/Presentation boundary is sacred:** `AbilityCastSystem`/`BuildingSystem`/`ResourceStore`/`EntityWorld` are
  sim — no `using Godot`, no `float` gameplay state. `CommandCardSystem`/`EntityPlacer`/`SelectionSystem` are
  presentation — they read sim arrays and issue intents/call sim methods directly (an editor tool calling
  `EntityWorld.Create`/`ApplyUnitDefinition` synchronously, outside lockstep, is the established pattern — not a
  boundary violation, mirrors `DoSpawnCombatUnit`).
- **Determinism:** all gameplay math in `Fixed`; ascending-id iteration; no `float`/`Mathf`/`System.Random`/
  wall-clock/`Dictionary` enumeration in the tick. This story's sim edit (`TrainUnit`) is int/`Fixed` only, mirrors
  an existing call shape exactly.
- **Single def→SoA mapper (A2):** `EntityWorld.ApplyUnitDefinition` is the one authorized def→SoA writer — Task 4
  closes the `DoSpawnWorker` exception to this rule rather than adding a 5th hand-copied field.
  ([[chimera-content-validator-bound-behavioral-params]] discipline; the 1.12/1.13/2.8 spawn-path-gap lesson.)
- **Data-driven:** the worker ability and its multi-resource cost are authored as JSON a creator edits (Task 1) —
  no gameplay logic or balance number is hardcoded in this story's code changes.
- **Godot C# gotchas:** classes inheriting Godot types must be `partial`; `GD.Print()` not `Console.WriteLine()`;
  `#nullable enable` per file. The `.sln` is `godot.sln`.

### References

- Story spec + AC: `_bmad-output/planning-artifacts/epics.md#Story-2.9b` (L1069-1081); Epic-2 sequencing note
  L893; upstream dep L1053-1067 (2.9a, done); downstream dep L1083-1097 (2.10, depends on 2.9a **and** 2.9b).
- FR-11 / FR-12 / AR-9 / NFR-4: `epics.md` FR table + AR-9 (ModifierStore/Energy substrate). Design intent:
  `_bmad-output/fma-faction-design.md:114` (Acolyte "Mend Matter" — flavor reference only, not this story's
  content, see Decision 1), `:172` ("Worker-cast ability trigger surface | small | needs-code"), `:174`
  ("Crystal-spend wiring (multi-resource costs) | small").
- Determinism precedent: `[[chimera-checksum-fold-timing-rule]]`, `[[chimera-content-validator-bound-behavioral-params]]`.
- deferred-work: `_bmad-output/implementation-artifacts/deferred-work.md:233` (Decision C / worker-cast command
  card, names this exact story), `:239` (the `CommandCardSystem.cs` `_Process` gate location).
- Reuse templates: `2-8-*.md` (crystal-cost precedent, `cost_crystal` on `wyvern`/`siege_engine`, the forward
  reference "crystal-spend isn't wired until 2.9b"), `2-4a-*.md`/`2-4b-*.md` (the cast spine + command-card wiring
  this story extends to workers, zero sim changes), `2-9a-*.md` (story-file format, golden-recording procedure,
  the `/godot-verify` node-state-read precedent for a fragile physical UI gesture).
- Source (verified this session): `AbilityCastSystem.cs:162-218` · `ResourceStore.cs:46-80` ·
  `AbilityDefinition.cs:43-56` · `NetworkCommand.cs:135-140,257-274` · `CommandCardSystem.cs:132-174,178-289,
  330-482,547-646,667-691` · `SelectionSystem.cs:104-107,236-251,686-719,756-773` · `AbilityTargeting.cs:11-24` ·
  `GatheringSystem.cs:1-202` (full) · `CombatSystem.cs:86-105` · `EntityWorld.cs:12-31(UnitCommand),320-377
  (ability/gather SoA),492-583(Create),529-530,596-651(ApplyUnitDefinition),633-634(Energy seed)` ·
  `UnitCategory.cs:14-22` · `UnitDefinition.cs:64,68(CostOre/CostCrystal)` · `BuildingSystem.cs:298-344,372-378` ·
  `EntityPlacer.cs:420-509(DoSpawnWorker/DoSpawnCombatUnit)` · `ScenarioApplier.cs:199-226` ·
  `SimChecksum.cs:77,90-217` · `SimChecksumCoverageGuardTest.cs:34,109-120` ·
  `VersionStampConsistencyTests.cs:51-100` · `SimSources.props:21-66` · `ApplyUnitDefinitionGuardTest.cs`
  (structure) · `alpha_faction.json:11-29,115-135` · `beta_faction.json:11-29` · ability JSONs (`battle_fury`,
  `minor_heal`, `aura_guard`, `furnace_trickle`, `onhit_searing`, `fireball`) · `AbilityTestSupport.cs:15-105` ·
  `PassiveTestAbilities.cs` · `GoldenScenario.cs:62-189` (full) · `AbilityCastScenario.cs` (full) ·
  `AbilityCastGoldenTests.cs:1-47` · `CombatAirGroundScenario.cs`/`CombatAirGroundGoldenTests.cs` (full, golden
  test-class template) · git log (`9d82779` HEAD, `6d6b550` last substantive = 2.9a, tree clean).

## Dev Agent Record

### Agent Model Used

claude-opus-4-8 (`gds-dev-story`).

### Debug Log References

- **Sim.Tests build** (Godot-free): 0 errors (3 pre-existing CS8632 warnings in GatheringSystem/FlowFieldSystem, not touched here).
- **Full Tier-1 suite:** `570 passed / 1 skipped / 0 failed` — exactly +14 vs 2.9a's 556 (6 `WorkerCastTests` + 4 `ProductionSelectionTests` crystal cases + 4 `WorkerCastCrystalCostGoldenTests`).
- **New golden** `worker-cast-crystal-cost-scenario.golden.txt` recorded via `CHIMERA_GOLDEN_RECORD=1` (300 samples, `checksum_algo_version: 8`, sequence evolves), re-embedded, `MatchesCommittedGolden` green.
- **Determinism (AC4):** `KnownWorldState_ProducesPinnedV8Hash` green → pin `0x983D39AE` unchanged, `AlgoVersion == 8`; `VersionStampConsistencyTests` green; all **13 existing goldens byte-identical** (none moved).
- **Full `godot.csproj` build:** 0 errors (presentation Tasks 3/4 — `CommandCardSystem`/`EntityPlacer`).
- **Release analyzer gate** (`-p:ChimeraRelease=true --no-incremental`): **0 errors** (only pre-existing CHM0001/CHM0005 *advisories* in `AiOpponentSystem.cs`/`CanonicalModelHash.cs`; the `BuildingSystem` edit introduced no RS0030/zero-baseline violation).
- **/godot-verify** (Godot 4.6.3-stable, node-state-driven): **PASS** — boots to menu, enters a live `[PLAY]` skirmish (Tick 60+, live checksum `Hash 0x0D6E69B2`, P1 3 units / P2 2 units / 3 buildings / 8 nodes), multi-resource HUD live (`P1 200 ore 0 crystal`), **ZERO error-log messages** across boot→menu→Play + ~450 physics ticks — every touched system compiled-in and ticking without exception. The physical Acolyte-select→cast gesture is PARKED as manual-QA (fragile 3D pick; AC5 + 1.9b/1.11/2.9a precedent — the mechanism is golden-proven byte-for-byte).

### Completion Notes List

- **Determinism premise verified empirically, not assumed:** NO golden or pinned test loads the real `alpha_faction.json` for a committed baseline — `GoldenApplierScenario` uses an *in-code* worker (no abilities/energy) and the `FactionJson` path in its model is an inert string field; `CanonicalScenarioTests.P2_4_...IsDeterministic` checks only run-to-run agreement (not a committed golden), so the Acolyte gaining `max_energy:20` (folded `Energy`) cannot break it. Task 1's content change therefore moves no existing golden. `AbilityDeserializeTests.ShippedSampleAbilityFiles_AllLoadAndValidate` enumerates the abilities dir → validates the new `matter_infusion.json` as a teeth test (passes).
- **Zero sim-pipeline changes:** the cast path was already worker-agnostic since 2.4a. The only sim edit is `TrainUnit`'s crystal check (Task 2) — atomic check-both (`CanAffordOre` → `CanAffordCrystal`) before spend-both, mirroring `TryCast`. No new SoA field, no fold, `AlgoVersion` stays 8.
- **AC3.2 ordering:** `DoSpawnWorker` now applies `SupplyCost=0`/`GatherState`/`CarryCapacity` **after** `ApplyUnitDefinition` (the mapper sets `SupplyCost = def.Supply = 1`, which the free-supply override must supersede) — editor-placed workers stay free-supply, byte-for-byte.
- **AC2.3 no-regression:** `costSuffix` is empty for every `cost_crystal:0` unit, so their train-button text is byte-for-byte unchanged; the new `CanAffordCrystal(0)`/`SpendCrystal(0)` are no-ops. Proven by `TrainUnit_ZeroCrystalUnit_UnaffectedByNewCheck_EvenWithZeroCrystalBank_Regression`.
- All 5 ACs met; 14 new Tier-1 tests + 1 new golden (the 14th).
- **AC1.1 human-confirmed in-engine (2026-07-02):** developer performed the manual selection gesture himself in a live Play-mode skirmish — clicked a P1 Alpha Acolyte and visually verified the "Matter Infusion" ability card renders stacked, non-overlapping, above the worker's build card. The button correctly showed greyed `"[need crystal]"` (map P1 starts with 0 crystal), corroborating both the co-display (AC1.1) and the affordability-text wiring (AC2.2) beyond the automated `/godot-verify` node-state read.

### File List

_Paths relative to repo root._

- `godot/resources/data/abilities/matter_infusion.json` — **new** (Self `apply_modifier`, ore+crystal cost, modifier id 1002).
- `godot/resources/data/factions/alpha_faction.json` — Acolyte (`"worker"`) gains `abilities:["matter_infusion"]` + `max_energy:20`.
- `godot/src/Economy/BuildingSystem.cs` — `TrainUnit` atomic ore+crystal spend (Task 2).
- `godot/src/UI/CommandCardSystem.cs` — worker+ability co-display (dropped `!workerSelected`, cached stacked position) + train-button `[need crystal]` parity (Task 3).
- `godot/src/UI/EntityPlacer.cs` — `DoSpawnWorker` routes through `ApplyUnitDefinition`, `SupplyCost=0` moved after the mapper (Task 4).
- `godot/ProjectChimera.Sim.Tests/Effects/WorkerCastTests.cs` — **new** (worker-cast affordability/atomicity/non-corruption, 6 tests).
- `godot/ProjectChimera.Sim.Tests/Economy/ProductionSelectionTests.cs` — **extended** (TrainUnit crystal atomicity, 4 tests).
- `godot/ProjectChimera.Sim.Tests/Golden/WorkerCastCrystalCostScenario.cs` — **new** (golden scenario).
- `godot/ProjectChimera.Sim.Tests/Golden/WorkerCastCrystalCostGoldenTests.cs` — **new** (golden test, custom record loop).
- `godot/ProjectChimera.Sim.Tests/Golden/worker-cast-crystal-cost-scenario.golden.txt` — **new** (recorded golden, 300 samples).
- `godot/ProjectChimera.Sim.Tests/Golden/SimChecksumCoverageGuardTest.cs` — doc-comment only (stale "10 goldens" → 14; the one allowed fence touch).
- `godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` — register the new golden as `EmbeddedResource`.
- `godot/ProjectChimera.Sim.Tests/Effects/WorkerCastTests.cs.uid`, `godot/ProjectChimera.Sim.Tests/Golden/WorkerCastCrystalCostScenario.cs.uid`, `godot/ProjectChimera.Sim.Tests/Golden/WorkerCastCrystalCostGoldenTests.cs.uid` — **new**, Godot-auto-generated `.uid` sidecars for the three new test scripts (the editor created them on project re-scan during `/godot-verify`). This project tracks one `.uid` per test `.cs` (103 already committed, not gitignored), so they are committed alongside their scripts.

## Change Log

| Date | Version | Description | Author |
|------|---------|-------------|--------|
| 2026-07-02 | 0.1 | Story 2.9b created (`gds-create-story`): worker-cast ability path (content + UI Decision-C fix, zero sim changes — the cast pipeline was already worker-agnostic since 2.4a) + `TrainUnit` crystal-spend (the sibling multi-resource gap) + `EntityPlacer.DoSpawnWorker` spawn-path fidelity fix. No new SoA field, no fold, `AlgoVersion` stays 8, 13 existing goldens untouched + 1 new golden planned. Status → ready-for-dev. | Claude (gds-create-story) |
| 2026-07-02 | 1.0 | Story 2.9b implemented (`gds-dev-story`): all 7 tasks + 5 ACs. `matter_infusion.json` (new) on Alpha's Acolyte; `TrainUnit` atomic ore+crystal spend; `CommandCardSystem` worker+ability co-display (dropped `!workerSelected`, cached stacked panel position) + train-button `[need crystal]` parity; `EntityPlacer.DoSpawnWorker` → `ApplyUnitDefinition`. +14 Tier-1 tests + 1 new golden (`worker-cast-crystal-cost`, the 14th). NO fold, `AlgoVersion` stays 8, pin `0x983D39AE` + all 13 existing goldens byte-identical. Tier-1 570/1/0, godot.csproj 0 err, release analyzer 0 err, /godot-verify PASS (live [PLAY] skirmish, 0 errors). Status → review. | Claude (gds-dev-story, Opus 4.8) |
