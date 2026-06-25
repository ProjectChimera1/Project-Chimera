---
baseline_commit: 6fb31efff58dad10ab1dd070ddd035ac425c9ae7
---

# Story 1.13: Formation movement — priority yielding + role-based battle formations + push/yield data fields

Status: ready-for-dev

<!-- Context engine analysis completed — comprehensive developer guide. Validation optional: run validate-create-story before dev-story. -->
<!-- 1.13 is the SECOND post-M1 design-gap story and the SIBLING of 1.12. It closes DG-2 / FR-54: the as-built
     movement has a flat ceil(sqrt(N)) grid (no roles) and SYMMETRIC separation (no moving>idle bias, no per-unit
     radius, no push/yield). The work is BROWNFIELD: one sim-behavior change (MovementSystem separation), two new
     authorable per-unit fields (collision_radius + push/yield) riding the EXISTING Speed/SplashRadius data-flow,
     one new presentation-read field (per-entity Category, needed for role formations), a NEW Godot-free
     FormationPlanner extracted from SelectionSystem's float grid, and the SimChecksum AlgoVersion 4→5 re-baseline
     that DG stories 1.12/1.13 were scheduled to do back-to-back. The single biggest HAZARD is, again, the
     SimChecksum fold: it re-baselines EVERY golden (6 now) and trips three version-pin guards ON PURPOSE — do it
     ONCE, LAST, exactly as Task 6 describes. The moving-bias also changes EXISTING goldens' positions (they have
     moving units that separate) — that re-record is expected, NOT a regression. -->

## Story

As a Commander maneuvering armies,
I want moving units to take precedence so idle units shift aside, units to push/yield by their authored radius, and multi-unit moves to form up by combat role,
so that armies advance through their own ranks instead of jamming, and front-line troops lead while ranged/support trail — without ever desyncing multiplayer.

## Acceptance Criteria

**Verbatim from `epics.md` (Story 1.13, lines 812–834; covers DG-2 / FR-54; depends on 1.8c):**

> 1. **Given** a moving unit and an idle unit overlapping inside SEPARATION_RADIUS **When** MovementSystem resolves separation **Then** the separation push is biased by the Moving flag (not the current symmetric split at MovementSystem.cs:72-89) so the idle unit receives the larger displacement and the moving unit's path-following velocity is reduced by no more than a fixed bias fraction, and two moving units (or two idle units) still split symmetrically as a measurable equal-magnitude baseline
> 2. **Given** UnitDefinition with new `collision_radius` and a push-vs-yield flag (creator-authorable per GDD line 163) **When** a faction JSON is loaded **Then** both fields deserialize into new parallel SoA arrays on EntityWorld (`CollisionRadius`, push/yield), separation uses the summed per-unit radii as the contact threshold instead of the flat SEPARATION_RADIUS constant, and a unit flagged "push" is never displaced by a "yield" unit it contacts
> 3. **Given** a unit definition that omits `collision_radius` or sets it <= 0 **When** it loads **Then** it falls back to a single documented default radius (no NaN, no exception, no zero-radius divide) and the unit participates in separation identically to a unit that authored that same default value
> 4. **Given** N selected units issued a move in a facing direction **When** IssueMoveCommand builds the formation (replacing the flat ceil(sqrt(N)) square grid at SelectionSystem.cs:332-374) **Then** Melee/Siege archetypes (from UnitDefinition.Category) are assigned slots forward of the move direction and Ranged/Support slots behind it, slot assignment iterates selected ids in ascending order, and re-issuing the identical move with the identical selection produces the identical per-unit destinations
> 5. **Given** a single-unit move or a selection of one archetype only **When** the formation is built **Then** it degrades to a centered single destination (or a single-row line for a one-archetype group) with no empty front/back gap and no destination placed on top of another selected unit
> 6. **Given** the committed golden scenario after these sim arrays and separation/formation changes land **When** the replay harness runs headless **Then** the run is byte-identical to the RE-BASELINED golden checksum (separation math stays in 16.16 `Fixed`, entities iterate ascending-id, no float/Mathf/wall-clock/unseeded randomness in sim), and removing the Moving-bias OR the per-unit-radius term changes the checksum — proving both fold into SimChecksum

### Decomposed, testable acceptance criteria

**AC1 — Moving-vs-idle separation bias (MovementSystem).**
- **AC1a:** In the separation accumulation (`MovementSystem.cs:83–100`), the per-unit separation displacement is scaled by a bias derived from the unit's own `Moving` flag: a **moving** unit's separation is scaled by `(Fixed.One − MOVING_SEPARATION_BIAS)`; an **idle** unit's by `Fixed.One`. So in a mixed moving/idle contact the idle unit receives the larger displacement, and the moving unit's path-following (seek) velocity is reduced by no more than the fixed `MOVING_SEPARATION_BIAS` fraction. `MOVING_SEPARATION_BIAS` is a named `static readonly Fixed` (default `0.5`).
- **AC1b:** Two moving units split symmetrically (each scaled by `(1−BIAS)` → equal magnitude); two idle units split symmetrically (each `×1.0`). A measurable equal-magnitude baseline in both same-state cases.
- **AC1c:** All separation math stays `Fixed` (16.16), ascending-entity-id iteration, no float/`Mathf`/wall-clock. The 1.12 Hold-anchor (`:48–52`) is preserved unchanged (a `HoldPosition` unit is still never displaced).
- _Asserted:_ a moving+idle overlapping pair → the idle unit's |Δposition| > the moving unit's |Δposition|; a moving+moving pair → equal-magnitude opposite displacement; an idle+idle pair → equal-magnitude opposite displacement.

**AC2 — Per-unit `collision_radius` + push/yield → new SoA arrays; summed-radii contact; push beats yield.**
- **AC2a:** `UnitDefinition` gains `collision_radius` (a C# `float`, JSON `collision_radius`, mirroring `Speed`/`SplashRadius`) and a push/yield flag (`separation_priority`, a JSON string parsed to an enum, mirroring `damage_type`/`ParsedDamageType`). Both deserialize into NEW parallel SoA arrays on `EntityWorld`: `CollisionRadius` (`Fixed[]`) and `SeparationPriorityOf` (`SeparationPriority[]` — note the `*Of` suffix, following the `DamageTypeOf`/`ArmorTypeOf`/`FactionOf` convention so the array field name never collides with the enum TYPE name), populated at spawn in `ScenarioApplier.SpawnUnit` (mirroring `world.SplashRadius[id] = Fixed.FromFloat(def.SplashRadius)`).
- **AC2b:** Separation uses the **summed per-unit radii** (`CollisionRadius[i] + CollisionRadius[j]`) as the per-pair contact threshold and the falloff normalizer, **instead of** the flat `SEPARATION_RADIUS` constant in the weight formula. (The spatial-hash QUERY radius stays a flat bound — see [the query-radius safety note](#the-query-radius-vs-max-radius-safety-rule-read-before-coding-ac2b) — only the contact/weight test becomes per-pair.)
- **AC2c:** A unit whose `SeparationPriorityOf[i] == Push` is **never displaced by** a neighbor whose `SeparationPriorityOf[j] == Yield` it contacts (the push unit skips that neighbor's contribution to its own separation; the yield unit still gets pushed by the push unit when the yield unit computes ITS separation). All other combinations (push/push, yield/yield, normal/anything) separate normally.
- _Asserted:_ two units with radii summing to a contact distance C separate iff their distance < C (and a unit at radius r₁ vs r₂ behaves differently from two default-radius units); a Push unit crowded by a Yield unit keeps its position; the Yield unit moves.

**AC3 — Missing / non-positive `collision_radius` → one documented default; identical participation.**
- A `UnitDefinition` that **omits** `collision_radius` (System.Text.Json leaves the `float` initializer) **or** authors `collision_radius <= 0` falls back at load/spawn to a single named `DEFAULT_COLLISION_RADIUS` (default `1.0` — see rationale below) before it reaches the `Fixed[]` SoA array. No `NaN` (the value is `Fixed`, never float-divided by zero — the weight normalizer is the summed radii, which is ≥ `2 × DEFAULT > 0`), no exception, no zero-radius divide. A unit that fell back to the default participates in separation **identically** to a unit that explicitly authored `collision_radius: 1.0`.
- _Asserted:_ a def with no `collision_radius`, a def with `collision_radius: 0`, a def with `collision_radius: -3`, and a def with `collision_radius: 1.0` all yield the same `CollisionRadius[id]` and the same separation outcome against a fixed neighbor.

**AC4 — Role-based formation (extract `FormationPlanner`; front/back by archetype; deterministic).**
- **AC4a:** Extract the flat `ceil(sqrt(N))` square grid (in BOTH `SelectionSystem.IssueMoveCommand:393–435` AND its twin `IssueAttackMoveCommand:480–519`) into a NEW **Godot-free** `FormationPlanner` (`src/Navigation/FormationPlanner.cs`, `Fixed`/`FixedVec3` only — it is in the Tier-1 SimSources glob and MUST NOT use `using Godot;`). `FormationPlanner.Plan(...)` takes the selected entity ids (ascending), each unit's `UnitCategory`, the move target (`FixedVec3`), and the group facing, and returns the per-unit destinations.
- **AC4b:** Front-line archetypes (`Melee`, `Siege` — per the AC) are assigned slots **forward** of the move direction (toward the target); back-line archetypes (`Ranged`, and `Air`/`Worker` per [the role-mapping decision](#decisions-baked-in-override-before-dev-story-if-you-disagree)) are assigned slots **behind**. Slot assignment iterates the selected ids in **ascending order**.
- **AC4c:** Deterministic — re-issuing the **identical** move (same target + same selection) produces **identical** per-unit destinations. All `FormationPlanner` math is `Fixed`; no float, no `Math.*`, no wall-clock, no RNG.
- **AC4d:** Both `IssueMoveCommand` and `IssueAttackMoveCommand` call `FormationPlanner` — do NOT leave one path on the old grid (they currently duplicate the identical grid; replacing only one would split the formation behavior between Move and Attack-Move).
- _Asserted (Tier-1, on `FormationPlanner` directly — no Godot):_ a mixed Melee+Ranged selection places every Melee destination forward of every Ranged destination relative to the facing; the same inputs twice → identical `FixedVec3[]`.

**AC5 — Degenerate formations: single unit, single archetype, no overlaps.**
- A **single-unit** move → the centered single destination (the target point itself, no offset).
- A **one-archetype** selection (e.g. all Melee, or all Ranged) → a single-row line perpendicular to the facing, with **no empty front/back rank/gap** (do not reserve an empty front row for a group that has no front-line units).
- **No** unit's destination is placed on top of another selected unit's destination (distinct slots).
- _Asserted (Tier-1):_ `Plan` with N=1 returns `[target]`; `Plan` with an all-Ranged group returns a single contiguous row (no front gap); every returned destination in any `Plan` call is unique.

**AC6 — SimChecksum fold + golden re-baseline (AlgoVersion 4→5).**
- **AC6a (fold):** `CollisionRadius[i].Raw` and `(int)SeparationPriorityOf[i]` fold into `SimChecksum.Compute`'s entity loop (after the v4 patrol-ring block, `:87`). `SimChecksum.AlgoVersion` bumps `4 → 5`; add a `v5 — Story 1.13` doc line and update the top-of-file hashed-state summary. **`CategoryOf` is NOT folded** — it is presentation-read (formation planning), constant, and never read in-sim, exactly like `MeshType`; document the exclusion. (Determinism rationale: `CollisionRadius`/`SeparationPriorityOf` ARE read in-sim by `MovementSystem` on every peer, so a content-divergence there must desync detectably → they fold. `CategoryOf` only shapes formation destinations, which are computed once on the issuing machine and transmitted as `Fixed` `MoveTarget` over the wire, so a divergent local `CategoryOf` cannot desync → it does not fold.)
- **AC6b (version-pin guards — they break ON PURPOSE):** update the three pins (see [the re-baseline surface](#the-simchecksum-re-baseline-surface-task-6--do-this-once-last)): `VersionStampConsistencyTests.ExpectedSimChecksumAlgoVersion 4→5` (`:49`); `SimChecksumCoverageGuardTest` `Assert.Equal(5, …)` (`:99`), rename `KnownWorldState_ProducesPinnedV4Hash → …V5Hash` (`:96`) and `ExpectedV4Hash → ExpectedV5Hash` re-pinned to the new constant the failing run prints (`:105`); and ADD new `AssertFieldFoldedIntoChecksum` cases for `CollisionRadius` and `SeparationPriorityOf` to `EntityCommandFields_AreFoldedIntoTheChecksum` (`:120`) proving each new field moves the hash. LEAVE `ExpectedCanonicalModelHashAlgoVersion=2`, `ExpectedReplayFormatVersion=2`, `ExpectedProtocolVersion=1` untouched.
- **AC6c (re-record all 6 goldens):** re-record `golden-scenario`, `golden-multifaction`, `golden-applier-scenario`, `same-tick-tie-break`, `ai-active-scenario`, AND `command-vocabulary-scenario` via the `CHIMERA_GOLDEN_RECORD=1` flow → every header's `checksum_algo_version` line auto-stamps to `5`. (The existing goldens' bodies also change because the moving-bias alters the positions of their moving units — that is the feature, not a regression.)
- **AC6d (new golden):** a NEW `FormationSeparationScenario` exercises the separation changes — a moving unit pushing through idle units, units of DIFFERENT `CollisionRadius`, and a `Push` unit contacting a `Yield` unit — pinned as a golden; two in-process runs are byte-identical and reproduce the committed golden. Because every hashed field is `int`/`Fixed.Raw` and Player2 is left empty (float-AI no-ops), this golden is **cross-platform-safe and NOT Windows-gated** (mirror `CommandVocabularyGoldenTests`, NOT `AiActiveGoldenTests`).
- **AC6e (the "removing the term changes the checksum" proof):** the per-unit-radius term folds **directly** (the AC6b differential proves mutating `CollisionRadius` changes `Compute`); the Moving-bias term folds **transitively via `Position`** — a test runs the `FormationSeparationScenario`'s moving+idle interaction with the bias vs with a symmetric split and asserts the resulting position-checksums differ, proving the bias affects hashed sim truth.

_Covers: **DG-2 / FR-54** (formation movement: priority yielding + role formations + radius/push-yield fields). Depends on: **1.8c** (DONE — the Godot-free `SimulationHost` + 9-system tick order). Sibling of **1.12** (DONE — the other DG checksum-bump story)._

---

## SCOPE — read this before coding

### ✅ IN scope (this story)
1. **Two authorable per-unit fields** — `collision_radius` (`float` on `UnitDefinition`, JSON `collision_radius`) + `separation_priority` (string → `SeparationPriority` enum) → new `EntityWorld` SoA arrays `CollisionRadius` (`Fixed[]`) + `SeparationPriorityOf` (`SeparationPriority[]`), copied at spawn in `ScenarioApplier.SpawnUnit`, with the `<=0`/missing → `DEFAULT_COLLISION_RADIUS` fallback (+ a `MAX_COLLISION_RADIUS` clamp for query safety).
2. **One presentation-read per-unit field** — `CategoryOf` (`UnitCategory[]`), parsed string→enum from `def.Category` like `ParsedDamageType`, populated at spawn, read by the formation planner. NOT folded into the checksum (presentation-read, like `MeshType`).
3. **MovementSystem separation rewrite** — moving-vs-idle bias (AC1) + summed-radii contact threshold (AC2b) + push-beats-yield rule (AC2c). The ONLY sim-behavior change to an existing system. Preserve the 1.12 Hold-anchor.
4. **NEW Godot-free `FormationPlanner`** (`src/Navigation/`, `Fixed`) — role-based front/back layout (AC4) + degenerate cases (AC5). Pure logic, Tier-1-testable.
5. **SelectionSystem (presentation)** — `IssueMoveCommand` + `IssueAttackMoveCommand` both call `FormationPlanner` (read each selected unit's `CategoryOf`, convert the raycast hit to `FixedVec3`, apply the returned destinations). Replaces the flat `ceil(sqrt(N))` grid in BOTH.
6. **SimChecksum fold (`CollisionRadius` + `SeparationPriorityOf`) + AlgoVersion 4→5 + re-baseline ALL 6 goldens** + the new `FormationSeparationScenario` golden + the three version-pin guards.
7. **Tests** — separation bias/radius/push-yield (AC1/2), default-radius (AC3), formation role/determinism/degenerate (AC4/5), checksum-fold differential + the new golden + the bias-vs-symmetric proof (AC6).
8. **STATUS.md** — flip the `Formation movement` row (`STATUS.md:79`) from 🟡 PARTIAL to ✅ (it was downgraded to point at this story by the readiness triage).

### ❌ OUT of scope (do NOT do these here)
- **NO flow-field formations, NO collision-aware pathfinding.** Formation is destination-assignment only (`FormationPlanner` sets goals; `MovementSystem`/`FlowFieldBridge` move toward them). Do NOT route formations through the flow field or add path-aware slotting.
- **NO per-formation morale / cohesion / regroup / facing-hold.** Just the front/back slot layout + the yielding separation. No formation as a persistent group entity, no "stay in formation while moving."
- **NO new command type, NO wire-format change.** Formation rides the EXISTING `Move`/`AttackMove` commands (the planner just computes a different per-unit destination before `EnqueueCommand`). `UnitOrder` (11 bytes), `ReplayRecorder.VERSION` (=2), the `UnitCommand` enum, and `OrderApplier` are UNTOUCHED. (The wire still carries one `FixedVec3` destination per unit, as today.)
- **Do NOT touch `CanonicalModelHash` (=2)** — that is the lobby start-state/scenario-content hash, not runtime separation state. Only `SimChecksum.AlgoVersion` (4→5) changes.
- **Do NOT add a new system to the 9-system tick order.** `FormationPlanner` is NOT a tick system — it is a pure helper called by `SelectionSystem` at order-issue time. `MovementSystem` is modified in place. `SystemOrderTest` must stay green untouched.
- **Do NOT regress the 1.12 Hold-anchor or any command behavior.** `MovementSystem.cs:48–52` (Hold skips seek+separation) stays exactly as-is. The separation rewrite is below that guard.
- **Do NOT call a real LLM, add a NuGet `PackageReference`, or touch the CI gate jobs.** `DependencyHygieneTests` + `--locked-mode` restore stay green.
- **Do NOT leave float in sim.** `CollisionRadius`/`MOVING_SEPARATION_BIAS`/all separation math = `Fixed`; `FormationPlanner` = `Fixed`/`FixedVec3` (no Godot `Vector3`). The ONLY float→Fixed boundary is `Fixed.FromFloat(def.collision_radius)` at spawn (mirrors the ~9 existing `SpawnUnit` conversions — advisory `CHM0005`, build-tolerated) and the raycast-hit→`FixedVec3` conversion in `SelectionSystem` (presentation boundary, like the existing `Fixed.FromFloat(dest.X)`).
- **Do NOT "fix" a red golden by hand-editing a `.golden.txt`.** Re-record via `CHIMERA_GOLDEN_RECORD=1` exactly as Task 6 describes. The header `checksum_algo_version` line is AUTO-stamped from `SimChecksum.AlgoVersion` — never hand-edit it.

### Brownfield reality (what exists vs what to build)
| Area | As-built (VERIFY, don't regress) | BUILD in 1.13 |
|---|---|---|
| `MovementSystem` separation | SYMMETRIC, flat `SEPARATION_RADIUS=2.0`, `SEPARATION_STRENGTH=2.5`, per-unit self-push (`:83–100`), position update `:112`; Hold-anchor `:48–52` | Moving-vs-idle bias + summed-radii contact + push/yield rule |
| `UnitDefinition` | `float Speed=4f` (`:33`), `string Category="Melee"` (`:19`), `float SplashRadius=0f` (`:78`), `ParsedDamageType`/`ParsedArmorType` (`:96–113`) | `float collision_radius` + `string separation_priority` + `ParsedSeparationPriority` + `ParsedCategory` |
| `EntityWorld` SoA | `Fixed[] Speed` (`:99`, alloc `:201`, set `:268`), `Fixed[] SplashRadius` (alloc `:215`, reset `:281`), `DamageTypeOf[]`/`ArmorTypeOf[]` enum SoA (`:109–110`), `byte[] MeshType` presentation-only (`:135`); **NO Category array** | `Fixed[] CollisionRadius` + `SeparationPriority[] SeparationPriorityOf` + `UnitCategory[] CategoryOf` + `DEFAULT/MAX_COLLISION_RADIUS` consts |
| `ScenarioApplier.SpawnUnit` | copies `Speed` via `Create(...)`; post-sets `SplashRadius`/`VisionRange`/… (`:199–230`); reads `def.Category` only for worker branch (`:224`) | copy `collision_radius` (clamped), `separation_priority`, `category` into the new SoA |
| Formation | flat `ceil(sqrt(N))` float grid, `SPACING=2.0`, in `IssueMoveCommand:393–435` AND `IssueAttackMoveCommand:480–519`; reads only `IsAlive` | NEW Godot-free `Fixed` `FormationPlanner` (role-based) called by BOTH |
| `SelectionSystem` access | reads all per-unit data via `_world` SoA (`_world.FactionOf[i]`, `_world.Position[i]`); **cannot see Category** (no array, no faction-def ref) | read `_world.CategoryOf[id]`; convert hit→`FixedVec3`; apply planner output |
| `SimChecksum` | v4: Position+Health+CommandTarget+patrol-ring+buildings+resources+RNG (`:56–123`); does NOT fold any separation/config field | fold `CollisionRadius`+`SeparationPriorityOf`; AlgoVersion 4→5 |
| Goldens | 6 files at `checksum_algo_version: 4` | re-record all 6 to 5 + add `formation-separation-scenario` |

---

## Tasks / Subtasks

- [ ] **Task 1 — New enums + UnitDefinition fields + EntityWorld SoA arrays + defaults (AC: 1, 2a, 3, 4 storage).**
  - [ ] Add a `UnitCategory` enum `{ Worker, Melee, Ranged, Siege, Air, Structure }` and a `SeparationPriority` enum `{ Yield, Normal, Push }` (default member ordering so `Normal` is the safe middle — but `Yield=0` is fine; what matters is the parsed default below). Co-locate near `UnitDefinition`/the `DamageType` precedent (`src/Core/Definitions/` or `src/Combat/`); keep them `Godot`-free (sim-readable).
  - [ ] In `godot/src/Core/Definitions/UnitDefinition.cs`: add `[JsonPropertyName("collision_radius")] public float CollisionRadius { get; set; } = 1.0f;` (mirror `SplashRadius:78–83` — a `float` with a default, NOT `[JsonConverter]`, NOT `Fixed`; the converter is not on this load path). Add `[JsonPropertyName("separation_priority")] public string SeparationPriority { get; set; } = "Normal";`. Add `ParsedSeparationPriority` and `ParsedCategory` computed switch-expression properties mirroring `ParsedDamageType:96–105` (case-insensitive is not required — `ParsedDamageType` uses exact-string `switch` with a `_ => default`; match that, defaulting unknown → `Normal` / `Melee`).
  - [ ] In `godot/src/Core/EntityWorld.cs`: add three SoA arrays sized `MAX_ENTITIES` (=4096): `public readonly Fixed[] CollisionRadius;` (folded v5), `public readonly SeparationPriority[] SeparationPriorityOf;` (folded v5 — `*Of` suffix per the `DamageTypeOf`/`ArmorTypeOf` convention, so the field name doesn't collide with the `SeparationPriority` enum type), `public readonly UnitCategory[] CategoryOf;` (**NOT folded — presentation-read, like `MeshType`; tag the comment accordingly**). Allocate all three in the ctor (beside `Speed = new Fixed[MAX_ENTITIES];` `:201` and `SplashRadius` alloc `:215`). Reset all three in `Create()` to defaults (beside `SplashRadius[id] = Fixed.Zero;` `:281`): `CollisionRadius[id] = DEFAULT_COLLISION_RADIUS; SeparationPriorityOf[id] = SeparationPriority.Normal; CategoryOf[id] = UnitCategory.Melee;`. (A recycled slot must never carry the previous unit's radius/priority/category — the classic SoA bug; `SpawnUnit` overwrites them, but `Create` must default them for any spawn site that forgets.)
  - [ ] **Enum value stability:** `SeparationPriority` is folded as `(int)`, so its integer member values become part of the hashed determinism contract once the golden records. Define it with explicit, never-reordered members (e.g. `Yield = 0, Normal = 1, Push = 2`) and do NOT renumber them later (same back-compat rule as the `UnitCommand` 0–5 freeze in 1.12). `UnitCategory` is NOT folded, so its values are free to reorder.
  - [ ] Add named constants `public static readonly Fixed DEFAULT_COLLISION_RADIUS = Fixed.FromFloat(1.0f);` and `public static readonly Fixed MAX_COLLISION_RADIUS = Fixed.FromFloat(1.0f);` (on `EntityWorld`, beside `MAX_ENTITIES`/`MAX_PATROL_WAYPOINTS`). Document: `DEFAULT=1.0` so two default units sum to a `2.0` contact distance = today's flat `SEPARATION_RADIUS` (so unauthored units keep their current separation contact); `MAX=1.0` so the largest summed contact (`2.0`) never exceeds the unchanged spatial-hash query radius (see Task 3 / the query-radius safety rule). These are `static readonly Fixed` (NOT bare literals — the `CHM0004` advisory flags magic caps).

- [ ] **Task 2 — Spawn/apply wiring: copy the three fields into the SoA, with the default/clamp (AC: 2a, 3, 4 storage).**
  - [ ] In `godot/src/Core/Sim/ScenarioApplier.cs` `SpawnUnit` (`:199–230`), after the existing post-set block (beside `world.SplashRadius[id] = Fixed.FromFloat(def.SplashRadius);` `:213`), add:
    - `Fixed r = Fixed.FromFloat(def.CollisionRadius); if (r <= Fixed.Zero) r = EntityWorld.DEFAULT_COLLISION_RADIUS; if (r > EntityWorld.MAX_COLLISION_RADIUS) r = EntityWorld.MAX_COLLISION_RADIUS; world.CollisionRadius[id] = r;` (AC3 default-on-missing/`<=0` + AC2b/query-safety clamp — comment WHY for each branch).
    - `world.SeparationPriorityOf[id] = def.ParsedSeparationPriority;`
    - `world.CategoryOf[id] = def.ParsedCategory;`
  - [ ] Confirm this is the SINGLE spawn primitive (the agent confirmed `Apply`/`ApplyFallback`/`ScenarioDirector.OnSpawnUnit` all route through `SpawnUnit`) so every spawn path gets the fields. Do NOT touch the `world.Create(...)` signature — these are post-sets like `SplashRadius`, not ctor args.
  - [ ] Verify no OTHER spawn site bypasses `SpawnUnit` writing entities that would carry the `Create()` defaults (acceptable — they get `DEFAULT_COLLISION_RADIUS`/`Normal`/`Melee`, which is the documented fallback).

- [ ] **Task 3 — MovementSystem: moving-vs-idle bias + summed-radii contact + push/yield (AC: 1, 2b, 2c).**
  - [ ] In `godot/src/Navigation/MovementSystem.cs`, add a named constant `private static readonly Fixed MOVING_SEPARATION_BIAS = Fixed.FromFloat(0.5f);` (beside the existing `SEPARATION_*` constants `:16–26`; document: the fraction by which a moving unit's separation displacement — and thus the perturbation to its path-following — is damped).
  - [ ] Keep the spatial-hash query (`:84`) as a flat bound. Rename `SEPARATION_RADIUS` → `SEPARATION_QUERY_RADIUS` (its new role is "how wide to scan for neighbours", NOT the contact distance) OR keep the name and document the role change — either way the QUERY radius stays `2.0` so the neighbour scan and the 32-slot `_neighborBuffer` are UNCHANGED (the brownfield "VERIFY the spatial-hash neighbor query still feeds separation unchanged"). The summed-radii contact happens INSIDE the loop.
  - [ ] Rewrite the separation accumulation (`:85–100`) per [the separation algorithm](#the-separation-algorithm-ac1--ac2--exact-design): for each neighbour `j`, compute `contact = CollisionRadius[i] + CollisionRadius[j]`; `if (neighborDist >= contact) continue;` (per-pair contact, AC2b); `if (SeparationPriorityOf[i] == SeparationPriority.Push && SeparationPriorityOf[j] == SeparationPriority.Yield) continue;` (AC2c — a push unit ignores a yield neighbour); `weight = (contact - neighborDist) / contact;` (falloff normalized by the summed radii). Then apply the moving bias ONCE to the unit's total separation: `Fixed bias = isMoving ? (Fixed.One - MOVING_SEPARATION_BIAS) : Fixed.One; velocity = velocity + separation * SEPARATION_STRENGTH * bias;` (AC1).
  - [ ] Preserve everything else: the `isMoving` seek/arrive block (`:60–81`), the `velocity == Zero` skip (`:103`), the max-speed clamp (`:105–109`), and the `:112` position update. Do NOT alter the 1.12 Hold-anchor (`:48–52`). `isMoving` already exists (`:55`).
  - [ ] Determinism: ascending-id loop (unchanged), all `Fixed`, `neighborDist <= Fixed.Zero` overlap-skip kept. No float, no `Mathf`, no RNG.

- [ ] **Task 4 — NEW Godot-free `FormationPlanner` (AC: 4, 5).**
  - [ ] Create `godot/src/Navigation/FormationPlanner.cs` — **`Fixed`/`FixedVec3` only, NO `using Godot;`** (it lands in the Tier-1 SimSources glob `..\src\Navigation\**\*.cs` and the analyzer covers it). Public static `FixedVec3[] Plan(System.ReadOnlySpan<int> idsAscending, System.ReadOnlySpan<UnitCategory> categories, FixedVec3 target, FixedVec3 facing, Fixed spacing)` (or an equivalent shape the dev finds cleanest — input is the selection in ascending id, each unit's category, the target, the facing direction; output is one destination per input id in the same order).
  - [ ] **Role split (AC4b):** partition the selection (preserving ascending-id order within each group) into FRONT (`IsFrontLine(category)` → `Melee`, `Siege`) and BACK (everything else — `Ranged`, `Air`, `Worker`; `Structure` units don't move but if selected, treat as BACK). Provide `private static bool IsFrontLine(UnitCategory c) => c == UnitCategory.Melee || c == UnitCategory.Siege;` (per the AC; see the role-mapping decision for the un-named archetypes).
  - [ ] **Layout:** lay each group out in rows perpendicular to `facing`, the FRONT group ahead of the centre line (toward `target` along `facing`) and the BACK group behind it, `spacing` apart (reuse `2.0` via a `FORMATION_SPACING` const, or pass it in). Slot assignment iterates ascending ids. Compute `facing` from `target − groupCentroid` (the centroid of the selected units' current positions); if `facing` is ~zero (degenerate), pick a fixed canonical axis (e.g. `+Z`) so the result is deterministic.
  - [ ] **Degenerate cases (AC5):** N=1 → return `[target]` (no offset). A one-archetype selection (all FRONT or all BACK) → a single contiguous row centred on `target` with NO empty opposite rank (don't reserve a front gap for an all-Ranged group). Guarantee no two destinations are equal (distinct slots) — the row/grid indexing already gives this; assert it.
  - [ ] **Determinism (AC4c):** pure `Fixed`; identical inputs → identical `FixedVec3[]`. No float, no `Math.*`, no RNG, no wall-clock, no `Dictionary`/`HashSet` ordering.

- [ ] **Task 5 — SelectionSystem presentation: wire BOTH issue paths to `FormationPlanner` (AC: 4, 5 issue-half).**
  - [ ] In `godot/src/UI/SelectionSystem.cs` `IssueMoveCommand` (`:393–435`): replace the inline `ceil(sqrt(n))` grid (`:399–417`) with a call to `FormationPlanner.Plan(...)`. Build the ascending-id selection + each unit's `_world.CategoryOf[id]`, convert the raycast `target` to `FixedVec3` (`Fixed.FromFloat(target.X)`/`target.Z`, like the existing offline boundary `:427`), compute the facing from the selection centroid → target, call `Plan`, then for each unit `EnqueueCommand(id, UnitCommand.Move, dest)` + the existing path-request / offline-direct-steer split (`:420–433`) using the planner's per-unit destination. (`_selectedList` is already ascending-insertion-ordered; if not guaranteed ascending by ID, sort a copy ascending before calling `Plan` so AC4's ascending-id contract holds.)
  - [ ] Mirror the SAME change in `IssueAttackMoveCommand` (`:480–519`) — it has the identical grid (`:486–500`); route it through `FormationPlanner.Plan` too (AC4d). Keep its `EnqueueCommand(id, UnitCommand.AttackMove, dest)` + `RequestAttackMove`/offline-direct-steer split (`:502–516`).
  - [ ] Presentation only — `FormationPlanner` returns `FixedVec3`; convert back to Godot `Vector3` for `RequestPath`/`RequestAttackMove` if needed (those take `Vector3`), and pass `FixedVec3` straight to the offline-apply path. NO Godot types leak into `FormationPlanner`; NO sim-state mutation beyond the existing offline-apply fallback.
  - [ ] (Optional, nice-to-have, not required: a formation-preview cursor. Skip for 1.13.)

- [ ] **Task 6 — SimChecksum fold + AlgoVersion 4→5 + re-baseline ALL 6 goldens + new golden (AC: 6). ⚠ GLOBAL change — do this ONCE, LAST, after Tasks 1–5 are behavior-complete.**
  - [ ] In `godot/src/Core/SimChecksum.cs`: in the entity loop, after the v4 patrol-ring block (`:87`, inside the `if (!IsAlive) continue;` body), add `hash = Mix(hash, world.CollisionRadius[i].Raw);` and `hash = Mix(hash, (int)world.SeparationPriorityOf[i]);`. Do NOT fold `CategoryOf` (presentation-read; add a one-line comment saying so, mirroring the `MeshType` exclusion note). Bump `AlgoVersion 4 → 5` (`:41`); add `///   v5 — Story 1.13: fold per-entity CollisionRadius + SeparationPriorityOf (separation config is sim truth — a peer divergence in either changes movement and must desync detectably).` after the v4 line (`:39`); add a line to the top-of-file hashed-state summary (`:9–16`).
  - [ ] Update the three version-pin guards (they break ON PURPOSE — that is their job):
    - `godot/ProjectChimera.Sim.Tests/Meta/VersionStampConsistencyTests.cs:49` — `ExpectedSimChecksumAlgoVersion = 4 → 5` (+ the prose line `:48`). LEAVE `:52` (`ExpectedCanonicalModelHashAlgoVersion=2`), `:55` (`ExpectedProtocolVersion=1`), `:58` (`ExpectedReplayFormatVersion=2`), `:61` untouched.
    - `godot/ProjectChimera.Sim.Tests/Golden/SimChecksumCoverageGuardTest.cs` — `Assert.Equal(4, …)` → `Assert.Equal(5, …)` (`:99`); rename method `KnownWorldState_ProducesPinnedV4Hash → …ProducesPinnedV5Hash` (`:96`); rename `ExpectedV4Hash → ExpectedV5Hash` and re-pin (`:105`) to the value the failing assertion prints (`actual 0x…`); update the `V4`/`v4` strings in the comment/message (`:103–109`).
    - ADD to `EntityCommandFields_AreFoldedIntoTheChecksum` (`:120`) two new `AssertFieldFoldedIntoChecksum` cases (the helper `:174–185` is reusable as-is): one mutating `CollisionRadius[e]` and one mutating `SeparationPriorityOf[e]` on a live entity — each must change `Compute` (AC6b differential). (`CategoryOf` is deliberately NOT proven here — it is not folded.)
  - [ ] Re-record ALL 6 goldens via the record flow (Alec is on Windows, so one broad run re-records all — including `ai-active`, which MUST record on Windows):
    ```
    $env:CHIMERA_GOLDEN_RECORD=1
    dotnet test godot/ProjectChimera.Sim.Tests --filter FullyQualifiedName~Golden
    Remove-Item Env:\CHIMERA_GOLDEN_RECORD
    dotnet build godot/ProjectChimera.Sim.Tests   # refreshes the embedded resource copies
    ```
    The header `checksum_algo_version` line auto-stamps to `5` (`GoldenChecksumReplay.FormatGolden:183`). In record mode the verify `[Fact]`s early-return; `SimChecksumCoverageGuardTest` will report its stale-hash failure during the record run (expected/harmless — goldens still write). After re-record, run NORMALLY and confirm green. Also update the cosmetic "v4" prose in `CommandVocabularyGoldenTests.cs:17,29` and `CommandVocabularyScenario.cs:14`.
  - [ ] Add `godot/ProjectChimera.Sim.Tests/Golden/FormationSeparationScenario.cs` (mirror `CommandVocabularyScenario.cs` — NOT `AiActiveScenario.cs`): an in-code, all-`Fixed` scenario built via `SimulationHost.Create(NullLogSink.Instance, new FactionRegistry(2), …)`, `ChecksumInterval=1`, empty `ScenarioData()` director, **Player2 left empty** (so the float-AI no-ops → hash stays float-free). Author directly on the world: at least one MOVING unit pushing through a cluster of IDLE units (exercises AC1 bias via Position), units with DIFFERENT `CollisionRadius` (exercises AC2b summed-radii via Position), and a `Push` unit adjacent to a `Yield` unit (exercises AC2c). Step ~300 ticks.
  - [ ] Add `godot/ProjectChimera.Sim.Tests/Golden/FormationSeparationGoldenTests.cs` (mirror `CommandVocabularyGoldenTests.cs`): `RunsTwiceInProcess_AreByteIdentical`, `MatchesCommittedGolden` (**no `OperatingSystem.IsWindows()` guard** → both CI legs), `Sequence_Evolves_NotVacuous`, and `RecordFormationSeparationBaseline` (record hook with its own `GoldenHeader` whose re-baseline hint names THIS filter). Register `formation-separation-scenario.golden.txt` as a 7th `<None Remove>`/`<EmbeddedResource Include>` pair in `ProjectChimera.Sim.Tests.csproj` (~`:50`, beside the existing six `:27–49`). LF-only (auto via `MaybeRecord`).
  - [ ] Add the AC6e bias-vs-symmetric proof: a Tier-1 test that runs the moving+idle interaction with the bias vs with a symmetric split (`MOVING_SEPARATION_BIAS = 0`) and asserts the resulting `SimChecksum`/position sequences DIFFER (proving the bias affects hashed sim truth). (If `MOVING_SEPARATION_BIAS` is `private`, prove it behaviorally: assert the idle unit's displacement ≠ the moving unit's in the biased run, which a symmetric model cannot produce.)

- [ ] **Task 7 — Behavior unit tests (AC: 1, 2, 3, 4, 5).**
  - [ ] Add `godot/ProjectChimera.Sim.Tests/Navigation/SeparationBiasTests.cs` (new `Navigation/` folder if absent, mirroring `Combat/`/`Multiplayer/`). Build a small `EntityWorld` + a `MovementSystem` directly — no Godot:
    - **AC1 bias:** a moving unit overlapping an idle unit → after `Tick`, `|Δpos(idle)| > |Δpos(moving)|`; two moving units → equal-magnitude opposite Δpos; two idle units → equal-magnitude opposite Δpos.
    - **AC2b summed radii:** two units at distance `d` with radii `r1+r2 = C` separate iff `d < C`; a small-radius pair (e.g. `0.5+0.5=1.0`) at `d=1.5` does NOT separate while a default pair (`1.0+1.0=2.0`) at `d=1.5` does.
    - **AC2c push beats yield:** a `Push` unit crowded by a `Yield` unit → `Push` unit `Position` unchanged across ticks; the `Yield` unit moves. (Push vs Push, Yield vs Yield → both move symmetrically.)
    - **AC3 default radius:** entities created at `Create()` default vs explicitly set to `DEFAULT_COLLISION_RADIUS` separate identically; no exception/NaN with a `0` or negative authored value (proven via the `SpawnUnit` clamp path or by asserting the SoA holds the default).
  - [ ] Add `godot/ProjectChimera.Sim.Tests/Navigation/FormationPlannerTests.cs`:
    - **AC4 role ordering:** a Melee+Ranged mix → every Melee destination is forward of every Ranged destination along the facing; ascending-id slot assignment is stable.
    - **AC4c determinism:** identical inputs twice → identical `FixedVec3[]`.
    - **AC5 degenerate:** N=1 → `[target]`; an all-Ranged group → a single contiguous row, no front gap; all destinations in every call are unique.
  - [ ] All test scenarios authored in `Fixed` (no `Fixed.FromFloat` in the assertion math beyond constructing inputs), ascending-id, no wall-clock — they run on every OS including the WSL leg.

- [ ] **Task 8 — Regression, STATUS.md, code review, sprint status.**
  - [ ] Run the full Windows CI command `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj -c Release` → green (was **264** at 1.12 close; **do not hardcode** — rely on exit code). Confirm: all 6 existing goldens re-recorded to `checksum_algo_version: 5`, the new `formation-separation` golden added, all three version-pin guards green at v5, `SystemOrderTest` untouched/green, the analyzer gate green (advisory on master).
  - [ ] Confirm the cross-platform integer-safety claim: every new hashed field is `Fixed.Raw`/`int` (no float in the hashed path), so the re-recorded goldens stay Win↔Linux byte-identical. If feasible, run the WSL `godot/tools/cross-platform-determinism-check.ps1` (VERIFY-only — it refuses if `CHIMERA_GOLDEN_RECORD` is set) on the committed code to confirm the re-baselined + new goldens match across platforms. (Optional cosmetic: fix the stale "the 4 committed goldens" string at `:119–120` of that script.)
  - [ ] Update `STATUS.md:79` — flip the `Formation movement` row from 🟡 PARTIAL to ✅ (note: role-based formations + priority yielding + per-unit collision-radius/push-yield now built; reference Story 1.13 / DG-2).
  - [ ] Run `gds-code-review` (3-layer adversarial, fresh-context / different LLM). On PASS, set this story `done` in `sprint-status.yaml` and update `last_updated`. (This workflow only sets `review`; code-review flips `done`.)

---

## Dev Notes

### Developer context — why this story exists and the one framing that makes it tractable
DG-2 (readiness triage) found the shipped movement incomplete: the multi-unit formation is a flat `ceil(sqrt(N))` square grid with no role awareness, and separation is fully SYMMETRIC — a moving unit and an idle unit shove each other equally, so armies jam at chokepoints instead of advancing through their own ranks. `UnitDefinition` has `Speed` but no `collision_radius` and no push/yield concept, so every unit separates at the same flat radius. This is the SECOND post-M1 design-gap story and the sibling of 1.12; the two were scheduled as **back-to-back SimChecksum re-baselines** (1.12 bumped `AlgoVersion 3→4`, 1.13 bumps `4→5`). M1's determinism floor is exactly what makes a separation-math change safe to land: the golden harness will catch any nondeterminism you introduce against a trustworthy baseline.

**The framing that makes this tractable** — there is a clean **sim ↔ presentation split** and a clean **storage ↔ behavior split**:
- **Storage (Tasks 1–2):** two authorable per-unit fields (`collision_radius` + push/yield) + one presentation-read field (`category`), all riding the EXISTING `Speed`/`SplashRadius`/`ParsedDamageType` data-flow (JSON float/string → `UnitDefinition` → `SpawnUnit` copy → `EntityWorld` SoA). No new data-flow invented.
- **Sim behavior (Task 3):** ONE system changes — `MovementSystem`'s separation. The moving-bias, summed-radii contact, and push/yield rule are all `Fixed`, ascending-id, Godot-free, Tier-1-testable. The Hold-anchor from 1.12 is untouched.
- **Presentation + the testable seam (Tasks 4–5):** the formation is presentation (it turns a click into per-unit intents), but it must be DETERMINISTIC and TESTABLE — so it is extracted to a Godot-free `Fixed` `FormationPlanner` (the exact move 1.11 made for `DelayMath` and 1.12 made for `OrderApplier`). `SelectionSystem` becomes a thin caller.
- **The checksum (Task 6):** the deliberate, scheduled re-baseline. Treat it as a mechanical, well-trodden procedure (1.5, 1.12 did exactly this) — do it ONCE, LAST.

The three real traps: (1) the SimChecksum bump breaking three guard tests (expected — they are tripwires); (2) the moving-bias changing EXISTING goldens' positions (expected re-record, NOT a regression — existing scenarios have moving units that separate); (3) replacing the grid in only ONE of the two issue paths (`IssueMoveCommand` vs `IssueAttackMoveCommand` share the identical grid — both must move to the planner).

### The separation algorithm (AC1 + AC2) — exact design
Today (`MovementSystem.cs:83–100`) each unit `i` independently sums a push away from every neighbour `j` within the flat `SEPARATION_RADIUS`, weight `= (SEPARATION_RADIUS − dist)/SEPARATION_RADIUS`, then `velocity += separation × SEPARATION_STRENGTH`. It is symmetric because the pair `(i,j)` and `(j,i)` produce equal-magnitude opposite pushes. The rewrite (drop-in for `:85–100`):

```csharp
int neighborCount = _spatialHash.QueryRadius(world, pos, SEPARATION_QUERY_RADIUS, i, _neighborBuffer); // query unchanged (2.0)
if (neighborCount > 0)
{
    FixedVec3 separation = FixedVec3.Zero;
    for (int n = 0; n < neighborCount; n++)
    {
        int j = _neighborBuffer[n];
        FixedVec3 away = pos - world.Position[j];
        Fixed neighborDist = away.Magnitude();
        if (neighborDist <= Fixed.Zero) continue;                     // exactly overlapping — skip (unchanged)

        // AC2b: per-pair contact = summed radii (replaces the flat SEPARATION_RADIUS in the weight)
        Fixed contact = world.CollisionRadius[i] + world.CollisionRadius[j];
        if (neighborDist >= contact) continue;                        // not in contact

        // AC2c: a Push unit is never displaced by a Yield neighbour it contacts
        if (world.SeparationPriorityOf[i] == SeparationPriority.Push &&
            world.SeparationPriorityOf[j] == SeparationPriority.Yield) continue;

        Fixed weight = (contact - neighborDist) / contact;            // falloff normalized by summed radii
        separation = separation + away.Normalized() * weight;
    }
    // AC1: moving-vs-idle bias — damp a moving unit's separation so its path-following is reduced by
    // at most MOVING_SEPARATION_BIAS; an idle unit takes the full push (larger displacement). Same-state
    // pairs (both moving / both idle) stay symmetric (equal magnitude). Applied once to the unit's total.
    Fixed bias = isMoving ? (Fixed.One - MOVING_SEPARATION_BIAS) : Fixed.One;
    velocity = velocity + separation * SEPARATION_STRENGTH * bias;
}
```

Why **per-unit** bias (scale by `i`'s own `Moving` flag) and not per-pair: it satisfies every AC1 clause with the least complexity — mixed pair: idle `×1.0` > moving `×(1−BIAS)` (idle displaced more ✓); both-moving: `×(1−BIAS)` each (equal magnitude ✓); both-idle: `×1.0` each (equal magnitude ✓); the moving unit's separation perturbation (and thus its path-following reduction) is bounded by `BIAS` ✓. A per-pair scheme (scale `i`'s contribution from `j` by `i.moving && !j.moving`) is a valid alternative but is more code for the same observable contract — use per-unit unless review prefers otherwise.

Why **`DEFAULT_COLLISION_RADIUS = 1.0`**: two default units sum to `contact = 2.0`, and the weight `(2.0 − dist)/2.0` is IDENTICAL to today's `(SEPARATION_RADIUS − dist)/SEPARATION_RADIUS` with `SEPARATION_RADIUS = 2.0`. So for unauthored units the ONLY behavioral change is the moving-bias — the radius math is backward-compatible. That keeps the existing goldens' re-record explainable (positions move only where moving units separate).

### The query-radius vs max-radius safety rule (read before coding AC2b)
The spatial-hash query (`QueryRadius(..., SEPARATION_QUERY_RADIUS, ...)`) decides which neighbours are even CONSIDERED. With per-unit radii the actual contact is `r_i + r_j`. If a summed contact could exceed the query radius, those contacts would be silently MISSED (units fail to separate — a subtle, golden-invisible bug if no test exercises large radii). Guard: clamp authored `collision_radius` to `MAX_COLLISION_RADIUS` at spawn (Task 2), and set `MAX_COLLISION_RADIUS` so `2 × MAX ≤ SEPARATION_QUERY_RADIUS`. With `MAX = 1.0` and the query kept at `2.0`, the largest contact (`2.0`) exactly equals the query window — the neighbour scan stays UNCHANGED (the brownfield "VERIFY the spatial-hash neighbor query still feeds separation unchanged"), and the 32-slot `_neighborBuffer` is unaffected. If a future story wants bigger units, it widens BOTH the query radius and `MAX` together (and re-checks the buffer cap) — flag, don't do it here. `[0, 1.0]` still gives 2× radius variety (a `0.5` worker vs a `1.0` siege), enough for 1.13's first cut.

### Formation design (AC4 + AC5) — the new Godot-free planner
The formation is the ONLY place archetype matters, and it is presentation (it computes the per-unit DESTINATIONS that become `MoveTarget`). It is extracted to a Godot-free `Fixed` `FormationPlanner` so AC4/AC5 are Tier-1-testable (SelectionSystem is a Godot Node — untestable in the Godot-free harness). The planner:
- partitions the ascending-id selection into FRONT (`Melee`/`Siege`) and BACK (the rest) by `IsFrontLine`;
- computes a facing from `target − centroid(selected positions)` (canonical `+Z` fallback if degenerate);
- lays each group out in rows perpendicular to the facing, FRONT toward the target, BACK behind, `spacing` apart, ascending-id slotting;
- degrades cleanly: N=1 → `[target]`; one-archetype → a single centred row with no empty opposite rank; no two destinations equal.

Why `Fixed` (not the existing float grid): the planner lives in `src/Navigation` (the Tier-1 SimSources glob — analyzer-covered, must be `using Godot;`-free), so it uses `FixedVec3`, and a `Fixed` planner makes the AC4c determinism test trivially cross-platform-robust. The destinations get quantized to `Fixed` at the sim boundary anyway (today's `Fixed.FromFloat(dest.X)`), and in MP the issuing machine computes the formation ONCE and transmits `Fixed` destinations over the wire — so the formation never needs to fold into the checksum (its OUTPUT folds via `Position`). `SelectionSystem` converts the raycast hit (`Vector3`) → `FixedVec3` at the call boundary and converts back to `Vector3` only for the `RequestPath`/`RequestAttackMove` delegates (which take `Vector3`).

Why `CategoryOf` is a NEW per-entity array and not a faction-def lookup: `SelectionSystem` reads all per-unit data through `_world` SoA and holds no `FactionDefinition` reference; there is NO per-entity category today (the unit's archetype lives only on `UnitDefinition` as a string). Adding `UnitCategory CategoryOf[]` (parsed string→enum at spawn, exactly like `ParsedDamageType → DamageTypeOf[]`) lets the planner read `_world.CategoryOf[id]` with the same access pattern SelectionSystem already uses for `_world.FactionOf[id]` — no new injected dependency, and it rides the same `SpawnUnit` copy you are already adding for `collision_radius`. (The alternative — inject `_slotFactionDefs` into `SelectionSystem` and reach the def via the presentation-only `MeshType` index — is more fragile and couples presentation to faction defs; see Decision #2.)

### SoA design — the new arrays, what to reuse
| Field | Type / default | Role | Hashed? |
|---|---|---|---|
| `CollisionRadius[]` (NEW) | `Fixed`, default `DEFAULT_COLLISION_RADIUS=1.0` | Per-unit separation radius; summed with a neighbour's for the contact threshold. Read in-sim by `MovementSystem`. | **YES** (fold v5 — in-sim read → must agree across peers) |
| `SeparationPriorityOf[]` (NEW) | enum `SeparationPriority {Yield,Normal,Push}`, default `Normal` | Push beats yield in contact (AC2c). Read in-sim by `MovementSystem`. | **YES** (fold v5) |
| `CategoryOf[]` (NEW) | enum `UnitCategory`, default `Melee` | Archetype for role-based formation. Read ONLY by the presentation `FormationPlanner`. | **NO** (presentation-read, constant — like `MeshType`; formation output folds via `Position`) |
| `Speed[]` (reuse) | `Fixed` | Max move speed; ctor arg to `Create`. | No (constant config, as today) |
| `MeshType[]` (precedent) | `byte` | Presentation-only def-index; the precedent for a per-entity array EXCLUDED from the checksum. | No (documented exclusion) |
| `DamageTypeOf[]`/`ArmorTypeOf[]` (precedent) | enum SoA, set from `Parsed*` | The exact template for the `CategoryOf`/`SeparationPriorityOf` enum SoA arrays. | No |

The two folded fields (`CollisionRadius`, `SeparationPriorityOf`) are folded because `MovementSystem` READS them every tick on every peer — a content-divergence (peers loaded different values) must desync detectably. `CategoryOf` is not folded because it only shapes formation destinations, which are computed once on the issuer and transmitted as `Fixed` `MoveTarget` — a divergent local `CategoryOf` cannot desync.

### The SimChecksum re-baseline surface (Task 6 — do this ONCE, last)
Folding `CollisionRadius` + `SeparationPriorityOf` is a GLOBAL algo change: it adds `Mix` calls, so the hash moves for EVERY scenario (even ones that author no radius — the new mixes fire at default values), re-basing all six goldens and tripping three guards. The full surface (verified by the golden-infra research; nothing else stamps `SimChecksum.AlgoVersion`):
1. `src/Core/SimChecksum.cs:41` — `AlgoVersion 4 → 5` + the `v5` doc line (`:39`) + the top-of-file summary (`:9–16`) + the two new `Mix` calls (after `:87`).
2. `…/Meta/VersionStampConsistencyTests.cs:49` — `ExpectedSimChecksumAlgoVersion 4 → 5`. (Leave the four sibling stamps `:52/:55/:58/:61`.)
3. `…/Golden/SimChecksumCoverageGuardTest.cs:96,99,103–109` — rename V4→V5, `Assert.Equal(5,…)`, re-pin the known-state hash to the failing-run value; ADD two `AssertFieldFoldedIntoChecksum` cases (`CollisionRadius`, `SeparationPriorityOf`) to `EntityCommandFields_AreFoldedIntoTheChecksum:120`.
4. Re-record 6 goldens (`golden-scenario`, `golden-multifaction`, `golden-applier-scenario`, `same-tick-tie-break`, `ai-active-scenario`, `command-vocabulary-scenario`) → headers auto-stamp to `5`.
5. Add `formation-separation-scenario.golden.txt` (+ the 7th csproj embed pair) + its scenario/test files.

LEAVE untouched: `CanonicalModelHash.AlgoVersion` (=2; lobby start-state/scenario-content hash, not runtime separation state), `ReplayRecorder.VERSION` (=2; no wire change), `SystemOrderTest` (no system added). The 1.10c LF-only / cross-platform golden guards auto-cover the new golden (`MaybeRecord` writes LF/BOM-free; `CrossPlatformGoldenGuardTests` floor is a minimum, not an exact count).

### Architecture compliance
- **Determinism law (NFR-4 / project-context):** all new separation/radius/bias math in `Fixed` (16.16); ascending-entity-id iteration (kept); no `float`/`double`/`Mathf`/`Math.*` in sim; no wall-clock; no `Dictionary`/`HashSet` enumeration driving sim order; the only RNG is `SimRng` (you need NONE — separation is deterministic over ascending ids). The ONLY float→`Fixed` boundaries are `Fixed.FromFloat(def.collision_radius)` at spawn (mirrors the ~9 existing `SpawnUnit` conversions — advisory `CHM0005`, build-tolerated D2 debt) and the raycast-hit→`FixedVec3` in `SelectionSystem` (presentation boundary).
- **Sim ↔ Presentation boundary is sacred:** `MovementSystem`, `EntityWorld`, `SimChecksum`, `FormationPlanner` are sim (`src/Navigation`, `src/Core` — NO `using Godot;`; the 1.10b analyzer covers them). `SelectionSystem` is presentation (`src/UI`) — it converts input to `FormationPlanner` calls + `EnqueueCommand`, and never mutates sim truth except via the existing offline-apply fallback. `FormationPlanner` taking/returning `FixedVec3` (not Godot `Vector3`) is what keeps it sim-side and testable.
- **AR-17 / lockstep contract:** the formation computes per-unit destinations on the ISSUING machine; each becomes a `Move`/`AttackMove` `UnitOrder` carrying one `FixedVec3` over the existing wire — all peers apply the SAME destinations. Separation runs identically on every peer (it reads only folded sim state), so it stays in lockstep.
- **Data-driven (platform rule):** `collision_radius` + `separation_priority` are now creator-authorable JSON (the point of DG-2). The tuning constants (`MOVING_SEPARATION_BIAS`, `DEFAULT/MAX_COLLISION_RADIUS`, `FORMATION_SPACING`) are named `static readonly Fixed`/consts in sim — no creator knob is required by this story, but they must NOT be bare magic literals (the `CHM0004` advisory flags bare caps).

### File structure requirements
**Create:**
- `godot/src/Navigation/FormationPlanner.cs` — Godot-free `Fixed` role-based formation planner (Task 4).
- `godot/ProjectChimera.Sim.Tests/Navigation/SeparationBiasTests.cs` — AC1/2/3 behavior (Task 7).
- `godot/ProjectChimera.Sim.Tests/Navigation/FormationPlannerTests.cs` — AC4/5 (Task 7).
- `godot/ProjectChimera.Sim.Tests/Golden/FormationSeparationScenario.cs` — all-separation in-code scenario (AC6d).
- `godot/ProjectChimera.Sim.Tests/Golden/FormationSeparationGoldenTests.cs` — AC6d two-run + golden + record hook (NOT Windows-gated).
- `godot/ProjectChimera.Sim.Tests/Golden/formation-separation-scenario.golden.txt` — NEW v5 golden (embedded, LF).
- (Enums) `UnitCategory` + `SeparationPriority` — new small files under `src/Core/Definitions/` (or co-located with `UnitDefinition`).

**Edit (sim):**
- `godot/src/Core/Definitions/UnitDefinition.cs` — `collision_radius` (float) + `separation_priority` (string) + `ParsedSeparationPriority`/`ParsedCategory` (Task 1).
- `godot/src/Core/EntityWorld.cs` — `CollisionRadius`/`SeparationPriorityOf`/`CategoryOf` SoA + ctor alloc + `Create` defaults + `DEFAULT/MAX_COLLISION_RADIUS` consts (Task 1).
- `godot/src/Core/Sim/ScenarioApplier.cs` — copy the three fields in `SpawnUnit` with the default/clamp (Task 2).
- `godot/src/Navigation/MovementSystem.cs` — moving-bias + summed-radii contact + push/yield + `MOVING_SEPARATION_BIAS` const (Task 3).
- `godot/src/Core/SimChecksum.cs` — fold `CollisionRadius` + `SeparationPriorityOf`, `AlgoVersion 4→5` + docs (Task 6).

**Edit (presentation):**
- `godot/src/UI/SelectionSystem.cs` — route `IssueMoveCommand` + `IssueAttackMoveCommand` through `FormationPlanner`, read `CategoryOf` (Task 5).

**Edit (tests / guards / project / status):**
- `…/Meta/VersionStampConsistencyTests.cs`, `…/Golden/SimChecksumCoverageGuardTest.cs` — version pins + new fold cases (Task 6).
- `…/ProjectChimera.Sim.Tests.csproj` — embed the new golden (Task 6).
- 6 existing `*.golden.txt` — re-recorded to v5 (Task 6).
- `STATUS.md:79` — Formation movement 🟡 → ✅ (Task 8).

**Do NOT touch:** `UnitOrder`/`NetworkCommand`/`OrderApplier` wire layout; `ReplayRecorder.VERSION`; `CanonicalModelHash`; `SystemOrderTest`; `GoldenChecksumReplay.cs` engine; `FixedPoint.cs`; `SimRng.cs`; the `UnitCommand` enum; `SimulationHost`/`SimulationLoop` construction; the CI gate jobs; `godot.csproj`; the 1.12 Hold-anchor in `MovementSystem`.

### Testing requirements
- **Tier-1 xUnit, Godot-free** for every assertion. Patterns to mirror: `CommandVocabularyGoldenTests.cs`/`CommandVocabularyScenario.cs` (golden two-run + record hook + header, cross-platform-safe — the RIGHT template, NOT the Windows-gated `AiActive*`), `SimChecksumCoverageGuardTest.cs` (differential fold + known-state pin), and the 1.12 `Combat/CommandVocabularyTests.cs` (small focused behavior units built from a raw `EntityWorld` + system). Build `EntityWorld` + `MovementSystem` directly for separation tests; call `FormationPlanner` directly for formation tests — no `SimulationHost` needed except for the golden.
- **The golden harness:** `GoldenChecksumReplay.RunAndRecord(ticks, perturb, build)` + `CompareSequences` + `MaybeRecord`; `IsRecordMode` gates the verify `[Fact]`s; the new golden's `MatchesCommittedGolden` has NO `OperatingSystem.IsWindows()` guard (→ both CI legs).
- **Never hardcode test counts; never set `CHIMERA_GOLDEN_RECORD` in CI/scripts;** the new golden is LF-only; never "fix" a red gate by re-recording without understanding the delta.
- **After the SimChecksum bump:** confirm `git status --short -- '*.golden.txt'` shows the 6 existing goldens MODIFIED (re-recorded to v5) + the 1 new golden ADDED — and nothing else.

### Previous-story intelligence (1.12 + the M1 chain — all DONE, code-reviewed PASS)
- **1.12** is your direct precedent and sibling: it added enum + SoA arrays, split a `CombatSystem` branch, added the 1.12 Hold-anchor you must PRESERVE in `MovementSystem` (`:48–52`), unified the apply switches behind `OrderApplier`, and did the `AlgoVersion 3→4` re-baseline + new cross-platform-safe golden — the EXACT mechanical surface you repeat for `4→5`. Its review found and fixed a "zombie route" recycled-slot bug (stale SoA on `Create`) — heed the same lesson: reset `CollisionRadius`/`SeparationPriority`/`CategoryOf` in `Create()`. It also routed the offline-apply through the shared production path to avoid drift — your formation has no such split (it's presentation-only intent), but keep `IssueMoveCommand` and `IssueAttackMoveCommand` using the SAME `FormationPlanner` so they can't diverge.
- **1.5** is the other re-baseline precedent (folded `SimRng.State`, bumped `2→3`, re-pinned the known-state guard, re-recorded goldens). **1.3b** added the coverage guard that is the deliberate tripwire.
- **1.8a/1.8c** built the `SimulationHost` 9-system order; `SystemOrderTest` pins it. You add NO system (FormationPlanner is a helper, not a tick system; MovementSystem is modified in place), so that test stays green untouched.
- **Conventions to respect:** brownfield additive slices over rewrites; reuse existing SoA/stores (don't add per-entity classes); comment public methods + non-obvious logic; `#nullable enable` per file; PascalCase/camelCase/SCREAMING_CASE; files match class names under `godot/src/<System>/`.

### Git intelligence
- The repo auto-commits hourly as `[AutoSave] <timestamp>`; story work lands in that stream. The analyzer is advisory on master (a stray-float warning won't block the autosave), but keep sim files float-free regardless. `baseline_commit` for this story: `6fb31ef`.
- Build/CI artifacts you must keep green but NOT edit: `.github/workflows/determinism-gate.yml` (the `tier1-golden-gate` + `tier1-golden-gate-linux` + analyzer jobs), the `DependencyHygieneTests` package guard. `SimSources.props` needs the NEW sim file only if its glob does not already cover `src/Navigation` — it DOES (`..\src\{Core,Combat,Economy,Navigation}\**\*.cs`), so `FormationPlanner.cs` is auto-included; no csproj/props edit for source (only the golden embed pair).

### Project Context Rules (from `_bmad-output/project-context.md`)
- **`Fixed` (16.16) is the only sim numeric type.** New thresholds (`MOVING_SEPARATION_BIAS`, `DEFAULT/MAX_COLLISION_RADIUS`, `FORMATION_SPACING`) are `static readonly Fixed`; `collision_radius` is a `float` ONLY on the JSON DTO (`UnitDefinition`), converted once via `Fixed.FromFloat` at spawn. No `Fixed.FromFloat` in the tick.
- **Process entities in ascending ID order** — the separation loop, the checksum loop, and the formation slot assignment all iterate ascending; preserve it.
- **SoA, not AoO** — the three new per-entity fields are new parallel arrays indexed by id, managed by the existing free list; reset them in `Create()`.
- **No `using Godot;` in sim** — `MovementSystem`/`EntityWorld`/`SimChecksum`/`FormationPlanner` stay pure C# (`FixedVec3`, not `Vector3`). Only `SelectionSystem` (UI) sees Godot.
- **Data-driven / composition** — `collision_radius` + `separation_priority` + `category` are creator-authored JSON; the 6 archetypes are the only "types" (the new `UnitCategory` enum mirrors them exactly).
- **Engine/runtime:** Godot 4.6.3, `net8.0`; project files `godot.csproj`/`godot.sln` (untouched).

### Decisions baked in (override BEFORE dev-story if you disagree)
1. **Per-unit moving-bias scaling** — scale a unit's total separation by `isMoving ? (1−BIAS) : 1.0`. Satisfies every AC1 clause (idle displaced more in a mixed pair; same-state symmetric; moving path-following reduced by ≤ `BIAS`) with the least code. Alternative: per-pair scaling (`i.moving && !j.moving`) — equivalent observable behavior, more code. `MOVING_SEPARATION_BIAS = 0.5` default (tunable).
2. **`CategoryOf` as a NEW per-entity enum SoA array** (parsed string→enum at spawn, read by `FormationPlanner`), NOT a faction-def lookup via `MeshType`. Cleaner, determinism-aligned, rides the same `SpawnUnit` copy as `collision_radius`, and matches the existing `DamageTypeOf`/`ParsedDamageType` pattern. It is NOT folded into the checksum (presentation-read, like `MeshType`). Alternative (Option B): inject `_slotFactionDefs` into `SelectionSystem` and reach the def via the presentation-only `MeshType` index — fewer arrays but couples presentation to faction defs and leans on a "defaults-to-0" presentational field.
3. **`SeparationPriority` is a 3-state enum `{Yield, Normal, Push}` defaulting to `Normal`** (parsed from JSON `separation_priority`, default "Normal" → all existing/unauthored units separate symmetrically, backward-compatible). The AC2c rule is exactly "a `Push` unit is not displaced by a `Yield` neighbour"; everything else separates normally. Alternative: a `bool IsPush` — too weak (can't express an explicit "yield"). Folded into the checksum (in-sim read).
4. **`FormationPlanner` is a NEW Godot-free `Fixed` helper in `src/Navigation`** (taking/returning `FixedVec3`), extracted from the float grid in BOTH issue paths — the only way AC4/AC5 are Tier-1-testable (precedent: 1.11 `DelayMath`, 1.12 `OrderApplier`). It is NOT a tick system (no `SystemOrderTest` change). The formation OUTPUT folds into the checksum via `Position`/the wire; the planner itself is not hashed.
5. **`DEFAULT_COLLISION_RADIUS = 1.0`, `MAX_COLLISION_RADIUS = 1.0`, `SEPARATION_QUERY_RADIUS = 2.0` (unchanged)** — default keeps the legacy flat-2.0 contact for unauthored units; max keeps the largest summed contact (2.0) within the unchanged query window + 32-slot buffer. `[0,1]` radius range is 1.13's first cut; bigger units = a fast-follow that widens the query + re-checks the buffer.
6. **The SimChecksum fold is a real `AlgoVersion 4→5` bump** (the scheduled DG re-baseline), folding `CollisionRadius` + `SeparationPriorityOf` DIRECTLY (the AC6e/AC6b differential proves it). The Moving-bias proof is transitive via `Position`. This matches the 1.12 precedent and the "back-to-back checksum bumps" plan.

### Open questions for Alec (non-blocking — sensible defaults chosen; flag only if you'd decide differently)
- **Role mapping for the un-named archetypes.** The AC names only Melee/Siege = FRONT, Ranged = BACK. This story defaults `Air`/`Worker` → BACK and excludes `Structure` (can't move). If you want Air as its own rank, or Workers excluded from battle formations entirely, say so before dev-story. (Note: Siege-forward is per the AC, though many RTS put siege BEHIND melee — the AC wording wins unless you override.)
- **Tuning values** (`MOVING_SEPARATION_BIAS=0.5`, `DEFAULT/MAX_COLLISION_RADIUS=1.0`, `FORMATION_SPACING=2.0`) are first-pass; all are named constants, trivially tunable later (and tuning them re-baselines the golden, which is cheap here).

### References
- `_bmad-output/planning-artifacts/epics.md:812–834` — Story 1.13 (statement, 6 ACs, "Covers DG-2", the brownfield dev-hint paragraph). `:243` — DG-2 definition. `:431` — FR-54. `:820–830` — the 6 ACs verbatim. `:82` (in the 1.12 story) — the explicit "formations / separation bias / `collision_radius` = Story 1.13" scope fence.
- Source — sim: `godot/src/Navigation/MovementSystem.cs:16–26` (constants), `:48–52` (1.12 Hold-anchor — preserve), `:83–100` (separation — rewrite), `:112` (position update); `godot/src/Core/EntityWorld.cs:67` (`MAX_ENTITIES`), `:99/:201/:268` (`Speed` SoA pattern), `:109–110` (`DamageTypeOf`/`ArmorTypeOf` enum SoA template), `:135` (`MeshType` presentation-only exclusion precedent), `:215/:281` (`SplashRadius` alloc/reset template), `:248` (`Create` sig); `godot/src/Core/Definitions/UnitDefinition.cs:19–21` (`Category` string), `:33–34` (`Speed`), `:78–83` (`SplashRadius` default-on-absence template), `:96–113` (`ParsedDamageType`/`ParsedArmorType` template); `godot/src/Core/Sim/ScenarioApplier.cs:199–230` (`SpawnUnit`; `:213` `SplashRadius` post-set template, `:224` `Category` read); `godot/src/Core/SimChecksum.cs:9–16/:35–41` (summary + AlgoVersion), `:56–88` (entity loop; `:87` insert point), `:131–139` (`Mix`).
- Source — presentation: `godot/src/UI/SelectionSystem.cs:59–62` (deps — no faction defs), `:128–130` (`Initialize`), `:300–318` (P/F key handlers), `:358–362` (per-unit `_world` read pattern), `:393–435` (`IssueMoveCommand` grid — replace), `:480–519` (`IssueAttackMoveCommand` twin grid — replace), `:426–432` (offline-apply boundary); `godot/src/UI/PathRequestSystem.cs:66/:82` (`RequestPath`/`RequestAttackMove` take a single per-unit `Vector3` — no formation logic there; confirmed not a duplicate site); wiring at `godot/src/Core/Bootstrap/Phases/CameraPhase.cs:42–45`.
- Tests to mirror: `godot/ProjectChimera.Sim.Tests/Golden/CommandVocabularyGoldenTests.cs` + `CommandVocabularyScenario.cs` (cross-platform-safe golden template), `SimChecksumCoverageGuardTest.cs:96–110` (known-state pin), `:120/:174–185` (`EntityCommandFields_AreFoldedIntoTheChecksum` + the reusable `AssertFieldFoldedIntoChecksum` helper); `…/Meta/VersionStampConsistencyTests.cs:49` (the pin); `godot/ProjectChimera.Sim.Tests/Golden/GoldenChecksumReplay.cs` (`RunAndRecord`/`MaybeRecord`/`FormatGolden:183` auto-stamp); `…/Meta/CrossPlatformGoldenGuardTests.cs` (LF-only, count floor = minimum); `godot/ProjectChimera.Sim.Tests.csproj:27–49` (golden embeds); `godot/tools/cross-platform-determinism-check.ps1` (VERIFY-only WSL gate).
- `STATUS.md:79` — the `Formation movement` 🟡 PARTIAL row this story flips to ✅. `STATUS.md:92` — PathRequestSystem "formation offset on multi-unit move" note (the offset is computed in SelectionSystem and passed in; PathRequestSystem itself does no formation math).
- `_bmad-output/project-context.md` — determinism law, Sim/Presentation boundary, `Fixed`/SoA/`SimRng` rules.

## Dev Agent Record

### Agent Model Used

{{agent_model_name_version}}

### Debug Log References

### Completion Notes List

### File List

## Change Log

| Date | Version | Change | Author |
|---|---|---|---|
| 2026-06-25 | 0.1 | Story created via `gds-create-story` (exhaustive context-engine analysis: 3 core sim files read line-level + 2 parallel research subagents tracing the UnitDefinition→EntityWorld data-flow and the full SimChecksum re-baseline/version-guard surface). 6 decisions baked in (per-unit moving-bias; `CategoryOf` as a new enum SoA; 3-state `SeparationPriority`; Godot-free `Fixed` `FormationPlanner`; default/max-radius + query-safety constants; the real `AlgoVersion 4→5` fold). Status → ready-for-dev. | Alec (SM) |
