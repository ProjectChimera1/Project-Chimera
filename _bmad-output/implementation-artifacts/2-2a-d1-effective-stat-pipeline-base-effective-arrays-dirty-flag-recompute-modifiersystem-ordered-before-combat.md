---
baseline_commit: 3c4d27b349a3fa7f38d465069e6ced9b97250d11
---

# Story 2.2a: D1 Effective-stat pipeline — Base*/Effective* arrays, dirty-flag recompute, ModifierSystem ordered before combat

Status: ready-for-dev

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As an engine developer,
I want `Base*`/`Effective*` paired stat arrays for the three modifier-affected stats (attack damage, max health, move speed) plus new `Energy`/`MaxEnergy` SoA arrays and a per-entity dirty-flag recompute driven by a `ModifierSystem` registered strictly before `CombatSystem` and `ProjectileSystem`, with the combat/movement/clamp readers repointed to `Effective*`,
so that combat and movement read a recomputed effective-stat layer that buffs and debuffs can later target — without regressing existing combat or movement.

## Acceptance Criteria

**AC1 — ModifierSystem ordered before combat; live readers read Effective\*.** _(epic)_
**Given** the `SimulationHost` system list **When** systems are registered **Then** `ModifierSystem` appears strictly before `CombatSystem` and `ProjectileSystem` in the tick order (the reserved index-3 slot, immediately before `CombatSystem`) **And** `CombatSystem` reads `EffectiveAttackDamage`, `MovementSystem` reads `EffectiveMoveSpeed`, and the Health-clamp sites read `EffectiveMaxHealth` — never the raw `Base*` arrays.

**AC2 — Dirty-flag recompute is correct, idempotent, and order-independent.** _(epic)_
**Given** an entity with `BaseAttackDamage`/`BaseMaxHealth`/`BaseMoveSpeed` and active modifier deltas **When** `ModifierSystem` ticks with that entity's dirty flag set **Then** each `Effective*` recomputes to its `Base* + delta` and the dirty flag clears; a subsequent tick with no change performs **no recompute** (an externally-corrupted `Effective*` on a clean entity is left untouched — proving the gate) **And** the recompute is order-independent: applying two commutative deltas in either order yields the identical `Effective*`.

**AC3 — No regression, no checksum fold, goldens byte-identical.** _(story-added — makes the implicit contract testable; per the standing checksum-fold rule)_
**Given** the committed golden suite and the determinism guards **When** this story's changes land **Then** all **7** `*.golden.txt` are byte-identical (`git status --short -- '*.golden.txt'` clean), `SimChecksum.AlgoVersion` stays **5**, `SimChecksum.cs` is **untouched**, and `SimChecksumCoverageGuardTest`'s pinned known-state hash (`0x5E7BE3D8`) is unchanged **And** `SystemOrderTest` is updated (intentionally) to assert the new 10-system order with `ModifierSystem` at index 3, and the full Tier-1 suite is green.

**AC4 — Single-mapper SoA rule (retro action item A2) lands with a guard.** _(story-added — this is the first Epic-2 SoA-adding story; A2 lands here)_
**Given** the new definition-derived per-unit array `BaseAttackDamage`/`EffectiveAttackDamage` **When** a unit is spawned through any definition-based spawn path **Then** the def-derived stat is written via `EntityWorld.ApplyUnitDefinition` (never hand-copied in a spawn path), proven by a Tier-1 guard test that fails if a Godot-free def-based spawn path leaves the new field at its `Create()` default (forgot the mapper) **And** the written rule is recorded in `_bmad-output/project-context.md` and `godot/CLAUDE.md`.

_Covers: FR-12, AR-9, NFR-4. Depends on: 2.1 (Effect-Graph vocabulary + the first-class `Modifier` descriptor whose 3 deltas this substrate mirrors — all green)._

---

## Tasks / Subtasks

- [ ] **Task 1 — EntityWorld: three `Base*`/`Effective*` stat pairs + `Energy`/`MaxEnergy` SoA (AC2, AC4)**
  - [ ] 1.1 **Mechanical rename first (behavior-preserving).** Rename the three modifier-affected stat arrays to `Effective*` everywhere they are referenced — `AttackDamage[]`→`EffectiveAttackDamage[]`, `MaxHealth[]`→`EffectiveMaxHealth[]`, `Speed[]`→`EffectiveMoveSpeed[]` (the EntityWorld move-speed array — **NOT** `AttackSpeed`, **NOT** `UnitDefinition.Speed`, **NOT** `ProjectileSystem.PROJECTILE_SPEED`). Update the declarations, ctor allocations, `Create()`, `ApplyUnitDefinition`, the live readers (`CombatSystem`, `MovementSystem`, the `HealEffect`/`DirectHpDeltaEffect` clamp), the presentation readers (`CommandCardSystem`, `SelectionSystem`, `EntityPlacer`, any health-bar bridge), and **every** test/builder/tool reference. The compiler finds every site — that is the safety net. This is a pure rename of arrays the live code already reads → **byte-identical goldens.** Grep `\.AttackDamage\b`, `\.MaxHealth\b`, `\.Speed\b` (the EntityWorld array — exclude `AttackSpeed`/`def.Speed`/`PROJECTILE_SPEED`). **Confirm it compiles (Godot + Tier-1) and goldens stay byte-identical before Task 1.2.**
  - [ ] 1.2 **Add the three `Base*` arrays** (`BaseAttackDamage`, `BaseMaxHealth`, `BaseMoveSpeed` — all `Fixed[MAX_ENTITIES]`, declared next to their `Effective*` sibling, ctor-allocated). The authored, never-mutated-in-tick source.
  - [ ] 1.3 **Add `Energy[]` + `MaxEnergy[]`** (`Fixed[MAX_ENTITIES]`, ctor-allocated). The single ability-cost pool (the AC's "Energy/Mana" is one pool — the architecture sketch is `Energy`+`MaxEnergy`, no separate `Mana`). Not authored yet (UnitDefinition has no energy field) — substrate 2.2b debits.
  - [ ] 1.4 `Create(pos, faction, health, speed)`: set every new array with **`Fixed.Zero` / the ctor args only** (no new `Fixed.FromFloat` — CHM0005): `Health=health` (unchanged); `BaseMaxHealth=EffectiveMaxHealth=health`; `BaseMoveSpeed=EffectiveMoveSpeed=speed`; `BaseAttackDamage=EffectiveAttackDamage=Fixed.Zero`; `Energy=MaxEnergy=Fixed.Zero`. (MaxHealth/MoveSpeed come from the ctor args today — that pattern is preserved; only AttackDamage is mapper-sourced.) A recycled slot must never carry the prior occupant's stats (the 1.12/1.13 SoA-recycle trap).
  - [ ] 1.5 `ApplyUnitDefinition` (the single mapper, for the **mapper-sourced** stat only): set `BaseAttackDamage[id] = Fixed.FromFloat(def.AttackDamage)` then `EffectiveAttackDamage[id] = BaseAttackDamage[id]`. This is the **same `FromFloat` site that already exists** (post-rename it currently sets `EffectiveAttackDamage` directly — split it through `Base`). Do **not** add `MaxHealth`/`MoveSpeed`/`Energy` writes here — `MaxHealth`/`MoveSpeed` flow from the `Create` ctor args (their existing single channel), and `Energy` has no def source yet (stays Zero; authored `MaxEnergy` → 2.2b/2.3, then through this mapper per A2).
  - [ ] 1.6 **Fix the compiler-forced non-mapper AttackDamage write sites** (the rename turns each into a compile error — the strongest A2 enforcement): the two **no-def fallback blocks** (`BuildingSystem.SpawnTrainedUnit:178-186`, `EntityPlacer.DoSpawnCombatUnit:490-497`) and `EntityPlacer.RestoreUnit:1096-1107` hand-set the stat — make each set **both** `BaseAttackDamage` and `EffectiveAttackDamage` (and, where they set MaxHealth/Speed, both Base+Effective for those too). `DoSpawnWorker` writes no attack damage (workers stay Zero) — leave it. The three **def-based** paths (`ScenarioApplier.SpawnUnit`, `BuildingSystem` with-def, `DoSpawnCombatUnit` with-def) route through `ApplyUnitDefinition` and pick up `BaseAttackDamage` automatically.

- [ ] **Task 2 — `ModifierSystem`: the dirty-flag effective-stat recompute (AC1, AC2)**
  - [ ] 2.1 New `godot/src/Effects/ModifierSystem.cs`, `namespace ProjectChimera.Effects`, `public sealed class ModifierSystem : ProjectChimera.Core.ISimSystem`. **Net-new — confirmed absent.** `src/Effects` is **already** in `SimSources.props` (added in 2.1) → covered by Tier-1 + the determinism analyzer + `GodotFreeBoundaryTest` with **no props edit** (unlike 2.1). Pure C#, `#nullable enable`, no `using Godot;`.
  - [ ] 2.2 Private per-entity state (pre-allocated `[EntityWorld.MAX_ENTITIES]`, **NOT** in EntityWorld, **NOT** hashed): `bool[] _dirty` + three `Fixed[]` net-delta accumulators `_flatAttackDamageBonus`/`_flatMaxHealthBonus`/`_flatMoveSpeedBonus` — the seam 2.2b's `ModifierStore` drives via apply/remove. Private + unhashed is deliberate: the dirty flag is a transient recompute optimization, and the recompute is idempotent (`Effective = Base + bonus` regardless of prior `Effective` or dirty timing), so a peer dirty-timing difference cannot diverge `Effective`. See Dev Notes §Determinism.
  - [ ] 2.3 `Tick(EntityWorld world, Fixed dt)`: ascending-id, `for i in [0, HighWaterMark): if (!world.IsAlive(i) || !_dirty[i]) continue;` then recompute all three: `EffectiveAttackDamage[i] = BaseAttackDamage[i] + _flatAttackDamageBonus[i]; EffectiveMaxHealth[i] = BaseMaxHealth[i] + _flatMaxHealthBonus[i]; EffectiveMoveSpeed[i] = BaseMoveSpeed[i] + _flatMoveSpeedBonus[i]; _dirty[i] = false;`. Zero-alloc, no float, no LINQ. In 2.2a production **nothing sets `_dirty`** → `Tick` is a no-op → every `Effective* == Base*` → combat & movement byte-identical.
  - [ ] 2.4 Internal seam (test- and 2.2b-visible — sim source compiles INTO the Tier-1 assembly, so `internal` needs no `InternalsVisibleTo`): `internal void AccumulateBonus(int id, Fixed attackDamageDelta, Fixed maxHealthDelta, Fixed moveSpeedDelta)` → `+=` each accumulator + `_dirty[id] = true`. The signature mirrors the 2.1 `Modifier` descriptor's three deltas (`AttackDamageDelta`/`MaxHealthDelta`/`MoveSpeedDelta`). Summation makes the recompute commutative (AC2). The AC2 test and 2.2b's `ModifierStore.Apply`/`Remove` (delta / −delta) both call this.
  - [ ] 2.5 Guard `id` bounds + `IsAlive` at the recompute entry (future callers hit dead/recycled slots). Parameterless ctor (no `ModifierStore` dependency in 2.2a — that's 2.2b).

- [ ] **Task 3 — Insert `ModifierSystem` at index 3; repoint the live readers to `Effective*` (AC1)**
  - [ ] 3.1 `SimulationHost` (`:88-102`): insert `new ModifierSystem()` at **index 3**, immediately before `new CombatSystem(...)` (shifts Combat 3→4, Projectile 4→5, Supply 5→6, Fog 6→7, Ai 7→8, ScenarioDirector 8→9). Add `using ProjectChimera.Effects;`. Update the "9-system" comments to **10** and convert the reserved-slot comment (`:93-95`) into the now-filled slot.
  - [ ] 3.2 Confirm the live readers point at `Effective*` (the Task 1.1 rename already did this — verify, don't double-apply): `CombatSystem` damage reads (`:93` combatant guard, `:468` projectile-spawn arg, `:482` melee arg) → `EffectiveAttackDamage`; `MovementSystem` move-speed reads → `EffectiveMoveSpeed`; `HealEffect`/`DirectHpDeltaEffect` Health-clamp ceilings → `EffectiveMaxHealth`. **`ProjectileSystem` needs NO change** — projectile damage is snapshotted into `ProjectileStore` at spawn (from `Effective`), and Projectile runs at index 5 (after Modifier@3 → Combat@4), so the AC's "before ProjectileSystem" holds structurally.
  - [ ] 3.3 Update `SystemOrderTest` (`Sim/SystemOrderTest.cs`) — **the AC1 enforcer, an intentional update:** add `typeof(ModifierSystem)` at `ExpectedOrder[3]` (10 entries); rename `Systems_AreTheNineCanonicalSystems...`→`...TenCanonicalSystems...`; rewrite `ReservedModifierSlot_CombatSystem_IsImmediatelyPrecededByMovementSystem` → assert `ModifierSystem` is immediately before `CombatSystem` AND immediately after `MovementSystem` (contiguous `Movement, Modifier, Combat`) AND strictly before `ProjectileSystem`. Add `using ProjectChimera.Effects;`.
  - [ ] 3.4 Grep for any OTHER system-count/order pin and update (`"nine"`, `"9 system"`, `systems.Count == 9`). The goldens run through the host and pick up the no-op `ModifierSystem` — confirm byte-identical (Task 6.2).

- [ ] **Task 4 — Single-mapper SoA rule + guard (retro action item A2) (AC4)**
  - [ ] 4.1 **Written rule** — add to `_bmad-output/project-context.md` ("Data layout") AND `godot/CLAUDE.md` ("Simulation Layer"): *"Every new per-unit SoA field that derives from `UnitDefinition` MUST be written via `EntityWorld.ApplyUnitDefinition` (the single def→SoA mapper) — never hand-copied in a spawn path. Stats sourced from the `Create()` ctor args (Health/MaxHealth/MoveSpeed) flow through that single channel; non-def fields are defaulted in `Create()`. This closes the 1.12/1.13 spawn-path/zombie-state defect class."*
  - [ ] 4.2 **Tier-1 guard test** (Godot-free) — `ProjectChimera.Sim.Tests/Core/ApplyUnitDefinitionGuardTest.cs` (or extend `Builder/ScenarioApplierTests.cs`):
    - **(a) Mapper completeness:** build a `UnitDefinition` with all combat fields distinct from the `Create` defaults; `Create()` an entity, call `ApplyUnitDefinition`; assert `BaseAttackDamage == Fixed.FromFloat(def.AttackDamage)`, `EffectiveAttackDamage == BaseAttackDamage`, and the pre-existing def fields (AttackRange/AttackSpeed/VisionRange/SplashRadius/SupplyCost/DamageTypeOf/ArmorTypeOf/CollisionRadius/SeparationPriorityOf/CategoryOf) all moved off their `Create` defaults.
    - **(b) Godot-free spawn-path parity (REQUIRED — `ScenarioApplier.SpawnUnit`):** spawn a known def through `ScenarioApplier.SpawnUnit` (public, simplest Godot-free def-based path); assert the spawned slot's def-derived fields equal `Create()`+`ApplyUnitDefinition(def)` — so a path that forgets a new field (leaving it at the `Create` Zero default) goes RED.
    - **(c) Primary in-match path (`BuildingSystem.SpawnTrainedUnit`) — cover if drivable:** Godot-free but `private`, fired on production completion (needs `BuildingStore` + producing building + faction def + `BuildingSystem.Tick` past `train_time`). It is the **primary in-match spawn source** (the 1.13 defect site), so guard it if a Tier-1 harness or existing `BuildingSystem` test can drive completion; else it is covered by the compiler-forced Base+Effective edit in 1.6 + the written rule, and the dev records that reliance in the DAR.
    - **Out of Tier-1 scope:** `EntityPlacer.{DoSpawnCombatUnit,DoSpawnWorker,RestoreUnit}` are `using Godot;` → covered by the compiler-forced 1.6 edits + the rule (a missed field is a compile error, not a silent gap).
  - [ ] 4.3 **Prove the guard has teeth (A3):** temporarily make a Godot-free spawn path skip `ApplyUnitDefinition` (or skip the `BaseAttackDamage` write) → observe the 4.2 parity test RED → revert. Record inject→observe→revert in the DAR.

- [ ] **Task 5 — Tier-1 tests (AC1, AC2, AC4)**
  - [ ] 5.1 `ModifierSystemTests.cs` (AC2) in `ProjectChimera.Sim.Tests/Effects/`: **(a)** set `Base*` stats, inject deltas via `AccumulateBonus(id, +5dmg, +20hp, +1spd)`, `Tick` → assert each `Effective* == Base* + delta` (independently-derived `Fixed.Raw`) and `_dirty` cleared (re-tick = no-op); **(b) teeth** — on a *clean* entity, externally overwrite an `Effective*` to a sentinel, `Tick`, assert it is **unchanged** (no recompute when not dirty — RED if the dirty gate is removed); **(c) commutativity** — `Accumulate(+5,..)` then `(+3,..)` vs `(+3,..)` then `(+5,..)`, each `Tick` → identical `Effective` (`Base+8`); **(d)** dead/recycled-slot safety — `Tick` skips `!IsAlive` ids without throwing.
  - [ ] 5.2 `SystemOrderTest` updates (3.3) green — `ModifierSystem` at [3], before Combat & Projectile, 10 systems.
  - [ ] 5.3 `CombatReadsEffectiveTests.cs` (AC1 teeth): build a combat unit, inject `+40` damage so `EffectiveAttackDamage != BaseAttackDamage`, `Tick` `ModifierSystem` then `CombatSystem`; assert the melee damage dealt to a known-armor target matches `EffectiveAttackDamage × matrix[type][armor]` (not `Base`). RED if a `CombatSystem` read still points at `Base`. (Reuse the `DamageResolverTests` independently-pinned-raw style.) Optional parallel: a `MovementSystem` read-Effective check if cheaply expressible.
  - [ ] 5.4 A2 guard tests (4.2).
  - [ ] 5.5 All tests: bare worlds via `Create(...)`+`ApplyUnitDefinition`/direct writes (the `DamageResolverTests` pattern), `Fixed.FromInt` only (no `FromFloat` in tests), assert against **independently-derived** raws — **no tautological asserts** (the durable Epic-1 review lesson). No Godot, no `SimulationHost` for the unit tests (`new ModifierSystem()`/`new CombatSystem(...)` directly).

- [ ] **Task 6 — Verify, confirm no regression, document deferrals**
  - [ ] 6.1 `dotnet build godot/ProjectChimera.Sim.Tests -c Release` + `dotnet test` green; full Tier-1 (baseline **~307 pass / 1 skip / 0 fail** post-2.1 → +N new).
  - [ ] 6.2 **Confirm the determinism posture (the load-bearing invariant):** all **7** goldens byte-identical (`git status --short -- '*.golden.txt'` clean); `SimChecksum.AlgoVersion` stays **5**; `SimChecksum.cs` **untouched** (do **NOT** fold the new arrays — Dev Notes §Determinism); `SimChecksumCoverageGuardTest` green incl. the pinned `0x5E7BE3D8` known-state hash. The eight new arrays are **not** hashed in 2.2a (dormant — `Effective*==Base*`, `Energy==0` — exactly like `AttackDamage`/`MaxHealth`/`Speed` were never hashed). If a golden moves, you leaked state (most likely a rename that changed a value, or an accidental fold) — find it, do **not** re-record.
  - [ ] 6.3 Full `godot.csproj` (production, Godot SDK) build: **0 errors** — the rename touches presentation readers (health bars), so the production build is the net for any missed `MaxHealth`/`Speed` reader.
  - [ ] 6.4 Append to `deferred-work.md` (§"Deferred from: story 2.2a"): the **SimChecksum fold** of `EffectiveAttackDamage`/`EffectiveMaxHealth`/`EffectiveMoveSpeed`/`Energy`/ModifierStore (AlgoVersion **5→6**, re-baseline goldens **once**) → **2.2b** (per [[chimera-checksum-fold-timing-rule]]); the **MaxHealth-buff Health semantics** (does applying/removing a `MaxHealthDelta` heal/clamp current `Health`?) → 2.2b design; the **move-speed 1-tick lag** (Movement@2 reads `EffectiveMoveSpeed` before Modifier@3 recompute — see Dev Notes) → revisit in 2.2b if same-tick speed buffs are needed; authored `MaxEnergy` field on `UnitDefinition` (then through `ApplyUnitDefinition` per A2) → 2.2b/2.3; `ModifierStore` must **clear an entity's accumulators/dirty on death/recycle** → 2.2b; `RestoreUnit` carrying the new fields → editor-snapshot widening (folds with the existing 1.13 `RestoreUnit` deferral).

---

## Dev Notes

### What this story is

The **AR-9 effective-stat substrate** — the layer that makes buffs/debuffs/auras possible without irreversibly mutating authored stats. Pattern (architecture §N3): a **`Base*`** array (authored, immutable in-tick) + an **`Effective*`** array (recomputed = `Base + Σ modifier deltas`) + a **per-entity dirty flag** so recompute runs only when something changed, driven by a **`ModifierSystem`** registered **right before `CombatSystem`** so combat reads fresh effective stats the *same* tick. 2.2a builds the **pipeline**; 2.2b builds the **`ModifierStore`** that drives it.

> _Architecture §N3 (game-architecture.md:2049-2076):_ "separating base from effective with a dirty flag makes add/remove/stack/expire reversible and order-independent; direct stat mutation is irreversible and stacks wrong. Registration order IS the design — combat must read recomputed stats the same tick or lag one tick vs a correctly-ordered peer."

**Scope (Decision #1, resolved by Alec = the full three-stat substrate):** the closed `Modifier` descriptor shipped in 2.1 carries exactly three stat deltas — `AttackDamageDelta`, `MaxHealthDelta`, `MoveSpeedDelta` (`src/Effects/Modifier.cs:59-63`). 2.2a builds `Base*`/`Effective*` for **all three** (AttackDamage, MaxHealth, MoveSpeed), so 2.2b is **pure store mechanics** with zero further EntityWorld/mapper surgery. AttackRange/AttackSpeed do **not** get pairs — no `Modifier` delta targets them.

### 🔑 Determinism posture — NO checksum fold in 2.2a (read this twice)

**2.2a adds eight new per-entity arrays (`Base`+`Effective` × {AttackDamage, MaxHealth, MoveSpeed} + `Energy` + `MaxEnergy`) and folds NONE of them. `SimChecksum.cs` is UNTOUCHED. `AlgoVersion` stays 5. All 7 goldens stay byte-identical.** This is correct, not a shortcut — and the thing the code review will scrutinize hardest. (Standing rule: [[chimera-checksum-fold-timing-rule]] — fold only when an array first becomes mutable mid-match; that is 2.2b.)

- **Nothing 2.2a adds is mutable, peer-divergent sim truth.** `Base*` are authored spawn-constants never mutated in-tick — **exactly like `AttackDamage`/`MaxHealth`/`Speed`, which are *not* hashed today** (only `Health` and `Position` are — verify `SimChecksum.cs:67-101`). `Effective* == Base*` for the entire story (no modifier exists → `ModifierSystem.Tick` is a no-op → `Effective` never diverges). `Energy`/`MaxEnergy` are `0` everywhere. A field that can't differ between peers needn't be hashed.
- **Goldens stay byte-identical because the rename is behavior-preserving.** `EffectiveAttackDamage`/`EffectiveMoveSpeed`/`EffectiveMaxHealth` hold the same values the old `AttackDamage`/`Speed`/`MaxHealth` did → identical damage, movement, and clamp → identical `Health`/`Position` (which *are* hashed) → identical golden. The renames + the additive `Base*` + the no-op `ModifierSystem` change **no hashed state**.
- **The coverage guards do not trip.** `SimChecksumCoverageGuardTest` reflects only `ResourceStore` arrays + a hand-maintained list of EntityWorld fields (Position/Health/Command/Patrol/Collision/Separation) — a new EntityWorld array not on that list does not trip it. `KnownWorldState_ProducesPinnedV5Hash` asserts `AlgoVersion == 5` and hash `0x5E7BE3D8`; both hold (new arrays at `Create` defaults, unhashed; algorithm unchanged). **Do not add the new arrays to that hand-list or to `SimChecksum.Compute`.**
- **The fold is 2.2b's, once.** When `ModifierStore` *mutates* an `Effective*` (a buff) and *debits* `Energy`, those become mutable sim truth → 2.2b folds them, bumps `AlgoVersion` **5→6**, re-baselines goldens once.

> ⚠️ **The trap:** "I added per-entity arrays, I should fold them." **NO.** Folding here breaks the byte-identical-golden invariant for zero benefit (values can't differ between peers yet) and steals 2.2b's single intentional re-baseline. Leave `SimChecksum.cs` alone. Precedent: **Story 2.1** added `src/Effects`, mutated already-hashed `Health`, **no fold, AlgoVersion stayed 5, 7 goldens byte-identical.**

### Why `ModifierSystem` is NOT a golden-moving change

It is a real `ISimSystem` in the tick loop, but its `Tick` mutates nothing in 2.2a (no entity is ever dirty). Inserting a no-op system changes the system *array* (`SystemOrderTest`, updated intentionally) but not the world *state* the golden hashes. Both are true at once: `SystemOrderTest` updated; goldens byte-identical.

### The dirty-flag accumulator (architecture §N3 design, three stats)

```csharp
// godot/src/Effects/ModifierSystem.cs  — namespace ProjectChimera.Effects
public sealed class ModifierSystem : ISimSystem
{
    // Private + UNHASHED. The dirty flag is a transient recompute optimization; the bonuses are the net
    // modifier deltas 2.2b's ModifierStore drives. All default false/Zero in 2.2a → Tick is a no-op.
    private readonly bool[]  _dirty                 = new bool[EntityWorld.MAX_ENTITIES];
    private readonly Fixed[] _flatAttackDamageBonus = new Fixed[EntityWorld.MAX_ENTITIES];
    private readonly Fixed[] _flatMaxHealthBonus    = new Fixed[EntityWorld.MAX_ENTITIES];
    private readonly Fixed[] _flatMoveSpeedBonus    = new Fixed[EntityWorld.MAX_ENTITIES];

    public void Tick(EntityWorld world, Fixed dt)
    {
        int cap = world.HighWaterMark;
        for (int i = 0; i < cap; i++)                       // ascending-id (the contract)
        {
            if (!world.IsAlive(i) || !_dirty[i]) continue;  // recompute ONLY when dirty
            world.EffectiveAttackDamage[i] = world.BaseAttackDamage[i] + _flatAttackDamageBonus[i];
            world.EffectiveMaxHealth[i]    = world.BaseMaxHealth[i]    + _flatMaxHealthBonus[i];
            world.EffectiveMoveSpeed[i]    = world.BaseMoveSpeed[i]    + _flatMoveSpeedBonus[i];
            _dirty[i] = false;
        }
    }

    /// <summary>The seam the AC2 test + (Story 2.2b) ModifierStore.Apply/Remove drive. Mirrors Modifier's 3 deltas; summation ⇒ commutative.</summary>
    internal void AccumulateBonus(int id, Fixed attackDamageDelta, Fixed maxHealthDelta, Fixed moveSpeedDelta)
    {
        _flatAttackDamageBonus[id] += attackDamageDelta;
        _flatMaxHealthBonus[id]    += maxHealthDelta;
        _flatMoveSpeedBonus[id]    += moveSpeedDelta;
        _dirty[id] = true;
    }
}
```

**Why dirty/bonuses live in `ModifierSystem`, not `EntityWorld`:** internal recompute machinery, not sim truth — private guarantees they are never accidentally hashed, and the recompute is idempotent, so a peer dirty-timing difference cannot diverge `Effective`. **Recycled-slot handoff to 2.2b:** in 2.2a nothing writes the accumulators/dirty in production (recycled slots are safe). **2.2b's `ModifierStore` MUST clear an entity's accumulators + dirty on death/recycle** (the SoA-recycle trap). Do **not** build death-cleanup in 2.2a (no modifiers to clean — speculative). Flagged in `deferred-work.md`.

### The renames + `Base*` adds (the safe two-pass sequence)

1. **Rename pass (golden-neutral):** `AttackDamage`→`EffectiveAttackDamage`, `MaxHealth`→`EffectiveMaxHealth`, `Speed`→`EffectiveMoveSpeed` everywhere. Compiler finds every site. Live code reads the same values under new names → goldens byte-identical. **Confirm green (Godot + Tier-1) before pass 2.** Watch the `Speed` rename: rename only the EntityWorld move-speed array — **leave `AttackSpeed`, `UnitDefinition.Speed`, `ProjectileSystem.PROJECTILE_SPEED`.**
2. **Add pass (additive):** introduce `BaseAttackDamage`/`BaseMaxHealth`/`BaseMoveSpeed`; `Create` sets `Base*` from the ctor args / Zero; `ApplyUnitDefinition` sets `BaseAttackDamage` (then `Effective=Base`); the no-def fallbacks + `RestoreUnit` set both; `ModifierSystem` recompute writes `Effective = Base + bonus`. `Base*` is read **only** by the recompute, so unset-`Base` on a direct-set test unit is harmless (never dirty).

**Spawn-path map (the A2 surface):**

| Path | Godot-free? | Through `ApplyUnitDefinition`? | 2.2a action |
|---|---|---|---|
| `ScenarioApplier.SpawnUnit` (`src/Core/Sim`) | ✅ | ✅ (`:210`) | auto; A2 parity-tested |
| `BuildingSystem.SpawnTrainedUnit` (`src/Economy`) | ✅ | ✅ with-def (`:174`); **fallback hand-sets** (`:178-186`) | fallback → Base+Effective (compiler-forced); A2 parity-test if drivable |
| `EntityPlacer.DoSpawnCombatUnit` (`src/UI`) | ❌ `using Godot;` | ✅ with-def (`:486`); **fallback** (`:490-497`) | fallback → Base+Effective (compiler-forced) |
| `EntityPlacer.DoSpawnWorker` (`src/UI`) | ❌ | ❌ (1.13 fields only) | no attack-damage write — leave |
| `EntityPlacer.RestoreUnit` (`src/UI`) | ❌ | ❌ (snapshot) | `:1096-1107` → Base+Effective (compiler-forced); new-field snapshot deferred |

The rename making the non-mapper sites **compile-error** *is* the A2 enforcement at its strongest.

### Live-reader repoints + two semantics notes for 2.2b

- **`CombatSystem`** (`EffectiveAttackDamage`): `:93` combatant guard, `:468` projectile-spawn arg, `:482` melee arg. **2.2b note (not a 2.2a task):** model "can't attack" with `StatusFlags.Disarmed` (already on `Modifier`), not by debuffing damage to 0 — so a temporarily-debuffed soldier still counts as a combatant.
- **`MovementSystem`** (`EffectiveMoveSpeed`): the move-speed read(s). **⚠ Move-speed 1-tick lag:** `MovementSystem` is at index **2**, `ModifierSystem` at **3** — so a speed delta applied tick T is recomputed at T (index 3) but read by Movement at T+1. Deterministic (all peers lag identically) and **invisible in 2.2a** (`Effective==Base`). The reserved-slot contract (Modifier immediately before Combat) takes precedence; if same-tick speed buffs are wanted, 2.2b decides between accepting the lag or revisiting the slot. Flagged in `deferred-work.md`.
- **`HealEffect`/`DirectHpDeltaEffect`** Health-clamp ceiling → `EffectiveMaxHealth`. **2.2b note:** decide whether applying/removing a `MaxHealthDelta` heals current `Health` (to full / proportionally) or clamps it down — irrelevant in 2.2a (`Effective==Base`).
- **`ProjectileSystem`**: no change (reads the `ProjectileStore` snapshot; freshness via Modifier@3 → Combat@4 spawn → Projectile@5).
- **Presentation readers** (`CommandCardSystem`/`SelectionSystem`/health-bar bridges) → `Effective*` (display the current/buffed stat). Compiler-caught by the rename; verified by the full Godot build (6.3).

### Energy/Mana = one pool (`Energy`/`MaxEnergy`)

The AC's "Energy/Mana SoA arrays" is the single ability-cost pool (architecture: `Energy`+`MaxEnergy`, no separate `Mana`). Substrate only in 2.2a; `UnitDefinition` has no energy field, so `Create`-defaulted to `Zero` and untouched by `ApplyUnitDefinition`. 2.2b debits `Energy` (refuse-when-insufficient); 2.2b/2.3 add an authored `MaxEnergy` (then through the mapper per A2).

### Live sim APIs (exact signatures — do not re-derive)

- **`EntityWorld` — `src/Core/EntityWorld.cs`** (`MAX_ENTITIES=4096`): `public readonly T[]` SoA, ctor-allocated, defaulted in `Create(FixedVec3 pos, Faction faction, Fixed health, Fixed speed)`. `bool IsAlive(int id)`; `int HighWaterMark`; `void ApplyUnitDefinition(int id, UnitDefinition def)` (`:372-389`) — add the `BaseAttackDamage` write here.
- **`ISimSystem` — `src/Core/SimulationLoop.cs:8`**: `void Tick(EntityWorld, Fixed)`. Systems tick in array order (`:91-92`). Constructed only in `SimulationHost`.
- **`SimulationHost` — `src/Core/Sim/SimulationHost.cs:88-102`**: the `_systems` array — insert `new ModifierSystem()` at index 3.
- **`Fixed` — `src/Core/FixedPoint.cs`** (16.16): `FromInt`/`FromRaw`/`.Raw`; `Zero/One/Half`; ops/comparisons; `Abs/Min/Max/Clamp`. **`FromFloat` is load-time only** (CHM0005) — `ApplyUnitDefinition` only, never `Tick`/tests.
- **`DamageResolver.Apply` — `src/Combat/DamageResolver.cs`** (AC1-teeth test): `static bool Apply(in DamageContext, Fixed amount, DamageType)`; `final = amount × DamageTable.Get(type, armor)`. `DamageTable.Default`. Precedent: `Combat/DamageResolverTests.cs` (pinned raws).
- **`SimChecksum` — `src/Core/SimChecksum.cs`** (`AlgoVersion=5`): **DO NOT EDIT.** `Health`/`Position` folded; the stat arrays were never folded; the new arrays stay unfolded in 2.2a.

### Determinism rules (analyzer gates `src/Effects`)

No `float`/`double` (CHM0001), no `FromFloat` in `Tick` (CHM0005), no `System.Random`/Godot RNG, no `using Godot;`, ascending-id `Tick`, name any cap (CHM0004 — `ModifierSystem` needs none), zero-alloc `Tick` (reuse the pre-allocated arrays; no `new`/LINQ/closures).

### Testing discipline (code-review checks)

- **No tautological asserts** — independently-derived `Fixed.Raw` (precedent `DamageResolverTests`). The Auditor re-derives your pins.
- **Every gate ships with teeth (A3)** — the dirty-gate test RED without the gate; the AC1 read-Effective test RED if combat reads `Base`; the A2 parity test RED if a path skips the mapper. Record inject→observe→revert in the DAR.
- **Cover boundaries** — dirty set/clear, no-recompute-when-clean, two-delta commutativity, dead/recycled-slot skip, a unit at `Effective==Base` deals/moves the same as pre-story.
- **Test homes:** `ProjectChimera.Sim.Tests/{Effects/ModifierSystemTests.cs, Effects/CombatReadsEffectiveTests.cs, Core/ApplyUnitDefinitionGuardTest.cs}`. Mirror `Combat/DamageResolverTests.cs`. Auto-compile under the globbed test project.

### Project Structure Notes

- New: `godot/src/Effects/ModifierSystem.cs` (`ProjectChimera.Effects`); new EntityWorld arrays in `src/Core/EntityWorld.cs`; new tests in `ProjectChimera.Sim.Tests/{Effects,Core}/`.
- **No `SimSources.props` edit** (`src/Effects` already globbed, 2.1). **No `.csproj` edit** (no new golden artifact; test `.cs` auto-compile). **No NuGet** (sim layer dependency-free / AOT-eligible).
- **Out of scope (do not touch):** `SimChecksum.cs` (no fold/bump — §Determinism), any `.golden.txt`, `ModifierStore` (2.2b), the `ApplyModifier`/`Persistent` executor guards (2.2b), authored `MaxEnergy` on `UnitDefinition` (2.2b/2.3), the `AbilityDefinition` loader/validator (2.3), `AttackRange`/`AttackSpeed` arrays (no `Modifier` delta targets them).
- **Single-mapper SoA rule (retro A2) lands here** (Task 4) — the first Epic-2 story to add per-unit SoA state, per the retro ("best landed with the first Epic 2 story that adds per-unit SoA state").

### Project Context Rules

_Extracted from `_bmad-output/project-context.md`:_
- **Sim/Presentation boundary is sacred.** `src/Effects`/`src/Core` are simulation: no `using Godot;`, no Node types, no `float` gameplay state. `ModifierSystem` is pure sim.
- **Determinism:** `Fixed` 16.16 for all stat math; ascending-id; `SimRng` only randomness; no `Dictionary`/`HashSet` iteration in sim order; platform-independent (`Fixed.Raw`).
- **Data layout:** new per-entity fields are new parallel SoA arrays via the free list — **and (A2) def-derived fields flow through `ApplyUnitDefinition`, never hand-copied in a spawn path.** Reuse `EntityWorld`; no parallel store.
- **Composition over inheritance / data-driven:** modifiers are data (the 2.1 `Modifier` descriptor) applied to orthogonal stat arrays — a "buffed unit" is a unit + an active modifier, not a subclass.
- **Conventions:** `PascalCase.cs` == class; `ProjectChimera.Effects`/`Core`; PascalCase types/methods, camelCase locals, SCREAMING_CASE consts; `#nullable enable`; comment public methods + non-obvious logic.
- **Brownfield:** small, shippable, always-green; reuse `EntityWorld`/`CombatSystem`/`MovementSystem`/`SimulationHost`; the substrate now, the store (2.2b) next.

### References

- **Story + epic scope:** `epics.md#Story-2.2a` (862-876); Epic 2 sequencing note (840); 2.2b (878-892).
- **AR-9 + §N3 design:** `game-architecture.md:429-434` (formula/dirty/Energy-Mana), **§N3 `:2049-2076`** (the `ModifierSystem` sketch + ENFORCEMENT tests), `:491-494` (register before CombatSystem), `:1601-1602` / `:1865-1879` (`+Energy/Mana`, `+ModifierSystem slot`), `:2526` (Energy regen fork → 2.2b).
- **Reserved slot:** `SimulationHost.cs:93-95` + `SystemOrderTest.cs`.
- **Live source:** `EntityWorld.cs` (SoA/`Create`/`ApplyUnitDefinition`), `Sim/SimulationHost.cs`, `Combat/CombatSystem.cs` (repoint), `Navigation/MovementSystem.cs` (repoint), `Effects/{HealEffect,DirectHpDeltaEffect}.cs` (clamp repoint), `Combat/ProjectileSystem.cs` (no change), `Core/SimChecksum.cs` (do-not-touch), `Effects/Modifier.cs` (the 3 deltas), `Definitions/UnitDefinition.cs` (no energy field).
- **Test patterns:** `Combat/DamageResolverTests.cs`, `Sim/SystemOrderTest.cs`, `Golden/SimChecksumCoverageGuardTest.cs`, `Builder/ScenarioApplierTests.cs`.
- **Prior-story lessons + rules:** `epic-1-retro-2026-06-25.md` (A1/A2/A3); Story **2.1** (no-fold precedent + the `Modifier` descriptor); 1.12/1.13 (the SoA-recycle/spawn-path defect class A2 closes); memory [[chimera-checksum-fold-timing-rule]] (the standing fold-timing rule).

---

## Dev Agent Record

### Agent Model Used

### Debug Log References

### Completion Notes List

### File List

### Change Log

| Date | Change |
|---|---|
| 2026-06-25 | Story 2.2a created (`gds-create-story`). Scope Decision #1 = full three-stat substrate (AttackDamage+MaxHealth+MoveSpeed); Decision #2 = no checksum fold (standing rule — data dormant until 2.2b). Status → ready-for-dev. |
