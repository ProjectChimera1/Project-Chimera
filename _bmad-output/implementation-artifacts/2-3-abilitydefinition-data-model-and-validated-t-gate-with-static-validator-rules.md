---
baseline_commit: 57dd610ab781b830cf35889faecbe0b79eb092b5
---

# Story 2.3: AbilityDefinition data model and Validated<T> gate with static validator rules

Status: ready-for-dev

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As a creator-platform developer,
I want a data-driven `AbilityDefinition` in `src/Core/Definitions` that deserializes its effect-graph payload through a closed-registry polymorphic JSON converter, compiles to a 2.1 `EffectNode` graph, and passes through the `Validated<T>` gate with located error messages,
so that ability definitions are JSON-authored, deterministic, and server-validatable before any tick — with no float math and no scripts ever reaching the sim.

## Acceptance Criteria

**AC1 — Valid ability → deserialize + compile + Validated, and deterministic.** _(epic)_
**Given** a valid ability JSON (targeting type + cost + cooldown + ≥1 effect node) **When** it is loaded through the single `ContentJson.Options` and passed through the ability validation gate **Then** it deserializes into an `AbilityDefinition`, its `effect` payload compiles **directly into a 2.1 runtime `EffectNode` graph** via the closed-registry `EffectNodeJsonConverter`, and the gate returns `Validated<AbilityDefinition>` with `Ok == true` **And** executing the compiled graph twice against two identical `EntityWorld` snapshots yields a **byte-identical `SimChecksum.Compute`** (the two-run determinism proof — a unit test, **no persisted golden artifact**, no `AlgoVersion` change).

**AC2 — Invalid graph → located rejection; nothing runnable escapes.** _(epic)_
**Given** an ability JSON containing a non-finite / over-16.16-range gameplay number, an **unknown effect `kind`**, a `Sequence` with more than `MaxSequenceChildren` children, or a graph nested past `MaxEffectDepth` **When** it is loaded and validated **Then** the gate returns a failure whose **located error names the ability id and the offending node-kind / field-path** (e.g. `ability 'fireball'.effect.children[2]: unknown kind 'lua'`), the loader **never returns null and never throws past its boundary**, **And** no rejected definition yields a runnable `EffectNode` graph (the failure carries no usable `Validated<AbilityDefinition>`).

**AC3 — No script payload ever; AR-13 random-effect rule owned + reserved.** _(epic)_
**Given** the closed effect registry and the unconditionally-present `SimRng` (Story 1.5; `EntityWorld.Rng` is non-null) **When** an ability is validated **Then** no registered `kind` is a scripting/eval/delegate/`object` payload or an open extension point — closedness is structural (2.1's `EffectVocabularyTests` already proves no node carries a `Delegate`/`Func`/`object` field; the registry only maps the 2.1 sealed types) — so **no ability definition can carry executable script or arbitrary code** **And** the AR-13 "a random effect requires `SimRng`" rule is owned here and **discharged by reservation**: the 2.1 vocabulary contains **no random leaf type**, so a random effect is not authorable today (its `kind` is unknown → rejected by AC2); the mature accept-if-present / reject-if-absent enforcement is reserved for the story that first adds a random leaf (the Story 1.7 precedent). _(Do **NOT** invent a random `EffectNode` or a fake "SimRng-absent" path for 2.3.)_

**AC4 — Total-work budget cap (closes the 2.1-review 64⁸ hang).** _(story-added)_
**Given** an ability graph that passes the depth (≤8) and per-`Sequence` (≤8) caps **but** whose worst-case execution count is unbounded — e.g. a chain of nested `SearchAreaEffect` nodes (8 nodes, up to 64⁸ leaf executions) — **When** it is validated **Then** the gate **rejects** it with a located error, enforced by **two new named caps in `EffectCaps`** (CHM0004-clean): `MaxSearchAreaDepth` (max `SearchArea` nodes on any root→leaf path) and `MaxTotalEffectNodes` (graph node-count ceiling) **And** the caps are reserved to fold into the Epic-9 `rulesetHash` later. _(Teeth: a graph at each cap passes; one `SearchArea` deeper — or one node over the total — is rejected.)_

**AC5 — Node-kind admissibility + re-entrancy safety (closes 2.1-review #4, 2.2b-review W1, 2.2b carve-off #1).** _(story-added)_
**Given** an ability graph containing **(a)** an `ApplyModifierEffect` or a nested `PersistentEffect` **inside any `PersistentEffect`'s** `InitialEffect`/`PeriodEffect`/`ExpireEffect` subtree, or **(b)** a `SearchAreaEffect` inside a `PersistentEffect.PeriodEffect` subtree **When** it is validated **Then** the gate **rejects each with a located error** (the store's dedicated executor would re-enter/clobber on a nested install; period effects are direct-target only — no per-tick spatial rebuild exists) **And** a **top-level** `ApplyModifierEffect`/`PersistentEffect` (both executable since 2.2b) is **accepted**. _(Teeth: the nested-install graph is RED; the identical install at top level is GREEN.)_

**AC6 — One canonical options object, fail-closed loader, determinism posture preserved.** _(story-added)_
**Given** ability loading **When** the loader runs **Then** all (de)serialization uses ONE `static readonly ContentJson.Options` carrying `FixedJsonConverter` + the new `EffectNodeJsonConverter` + `JsonStringEnumConverter` (enums by **NAME**) + `UnmappedMemberHandling.Disallow`; `[JsonPolymorphic]`/`[JsonDerivedType]` are **not** used; the loader returns a **located result** (never null, never throws past the boundary) **And** the determinism posture holds: `SimChecksum.AlgoVersion` stays **6**, **all 8 goldens are byte-identical**, `SystemOrderTest` is untouched (no new tick system), and no `float` / `Dictionary`-enumeration / `using Godot;` enters the new sim code (analyzer clean).

_Covers: FR-12, FR-10, AR-8, AR-13, AR-22, AR-39, NFR-4, NFR-6. Depends on: 2.1 (the `EffectNode` vocabulary + `EffectBounds.Validate` + `EffectExecutor` — done), 2.2b (`ApplyModifier`/`Persistent` now execute against `ModifierStore` — done), 1.7 (`Validated<T>` + `ScenarioValidator.Proof` + the sole-minter source scan — done), 1.4 (`FixedJsonConverter` — done), 1.5 (`SimRng` — done)._

---

## Tasks / Subtasks

- [ ] **Task 1 — `AbilityDefinition` model + targeting enum + the canonical `ContentJson.Options` (AC1, AC6)**
  - [ ] 1.1 New `godot/src/Core/Definitions/AbilityDefinition.cs`, `namespace ProjectChimera.Core.Definitions`, `public class AbilityDefinition`. **Mirror `UnitDefinition`** exactly (PascalCase auto-props + `[JsonPropertyName("snake_case")]`, `#nullable enable`, no `using Godot;`). Fields: `string Id` (`id`), `string DisplayName` (`display_name`), `string Targeting` (`targeting`, default `"Self"`) + a computed `ParsedTargeting`, `Fixed CostEnergy` (`cost_energy`, default `Fixed.Zero`), `int CostOre` (`cost_ore`, default 0), `int CostCrystal` (`cost_crystal`, default 0), `Fixed Cooldown` (`cooldown`, seconds; default `Fixed.Zero`), and `EffectNode EffectGraph` (`effect`) — the compiled root (deserialized by Task 2's converter). It is **net-new** (`ls src/Core/Definitions/Ability*` → nothing).
  - [ ] 1.2 New `public enum AbilityTargeting { None, Self, TargetUnit, GroundPoint }` (closed set; `ProjectChimera.Core.Definitions`). `ParsedTargeting` switches the string → enum, mirroring `UnitDefinition.ParsedCategory`/`ParsedSeparationPriority`. An unknown targeting string is rejected by the validator (Task 3), not silently defaulted. Story 2.4 drives target-select vs instant-cast vs ground-cast off this enum.
  - [ ] 1.3 New `godot/src/Core/Definitions/ContentJson.cs`: `public static class ContentJson { public static readonly JsonSerializerOptions Options = new() { ... }; }` carrying `ReadCommentHandling = Skip`, `AllowTrailingCommas = true`, `UnmappedMemberHandling = Disallow` (**fail-closed: an unknown JSON field is a located error**), and `Converters = { new JsonStringEnumConverter(), new FixedJsonConverter(), new EffectNodeJsonConverter() }`. **This is the single ability-loading options object (the architecture's `ContentJson.Options` seed).** Do **NOT** migrate `ScenarioSerializer`/`FactionDefinition` to it — that consolidation is deferred (D3; see Dev Notes §Scope).
  - [ ] 1.4 **No `SimSources.props` edit** — `src/Core/**` (incl. `Definitions/`) and `src/Effects/**` are already globbed into both the Tier-1 test project and the determinism analyzer (verified). The converter in `Definitions` may reference `ProjectChimera.Effects` types (same assembly, both sim-layer, both globbed).

- [ ] **Task 2 — The closed-registry `EffectNodeJsonConverter` (AR-22) (AC1, AC2, AC6)**
  - [ ] 2.1 New `godot/src/Core/Definitions/EffectNodeJsonConverter.cs`: `public sealed class EffectNodeJsonConverter : JsonConverter<EffectNode>`. A **hardcoded `kind` → constructor registry** over the 8 runtime types (exact ctors in Dev Notes §"The 2.1 effect vocabulary"): `"direct_hp_delta"`→`DirectHpDeltaEffect`, `"heal"`→`HealEffect`, `"damage"`→`DamageEffect`, `"apply_modifier"`→`ApplyModifierEffect`, `"sequence"`→`SequenceEffect`, `"search_area"`→`SearchAreaEffect`, `"persistent"`→`PersistentEffect`. **No reflection, no `[JsonPolymorphic]`/`[JsonDerivedType]`** (forbidden project-wide, AR-22). Build the runtime sealed types directly via their public ctors.
  - [ ] 2.2 `Read`: parse the object, read the `kind` discriminator (string), dispatch to the per-kind reader that pulls the type's fields (`Fixed` fields flow through the registered `FixedJsonConverter`; `DamageType`/`StackRule`/`StatusFlags`/`TargetFilter` enums by **name** via `JsonStringEnumConverter`; child `effect`/`children` recurse through this converter). **Unknown `kind` → located `JsonException`** naming the kind. **Missing required field → located `JsonException`** naming the field. **🚨 The converter must reject UNKNOWN PROPERTIES itself** — `UnmappedMemberHandling.Disallow` governs only the POCO (reflection) layer (`AbilityDefinition`'s own fields); it does **NOT** reach inside a custom `JsonConverter`, so a stray property on an effect-node object would be silently skipped unless the per-kind reader explicitly fails on any token it doesn't recognize (fail-closed per AR-22's "no scripting/eval escape hatch" intent).
  - [ ] 2.3 **Parse-depth guard (anti-stack-overflow):** the recursive `Read` must not blow the C# stack on a maliciously deep JSON **before** `EffectBounds.Validate` ever runs. Track a depth counter in the converter and reject past `EffectCaps.MaxEffectDepth` at parse with a located error (belt-and-suspenders alongside `JsonSerializerOptions.MaxDepth`). Pin by test (a 9-deep JSON object throws at parse, not stack-overflows).
  - [ ] 2.4 **Modifier deserialization** (needed by `apply_modifier` and by `persistent`/`modifier` period effects): read the `Modifier` descriptor fields — `id`, `duration_ticks` (`int`; `<0` = permanent, `0` = one-shot), `stacking` (`StackRule` by name), `max_stacks` (`int`), `max_health_delta`/`attack_damage_delta`/`move_speed_delta` (`Fixed`), `status` (`StatusFlags` by name), `period_effect` (nested `EffectNode?`), `period_ticks` (`int`). Fold this into `EffectNodeJsonConverter` or a sibling `JsonConverter<Modifier>` registered in `ContentJson.Options` — pick one, document. `Modifier` has a **public ctor** (10 args, in Dev Notes).
  - [ ] 2.5 `Write` (authoring round-trip) is **optional in 2.3** (the editor is Story 2.5). Recommend implementing `Read` fully and a minimal/throwing `Write` (or a faithful `Write` if cheap) — note the choice; `save→reload` equality is asserted via `Fixed.Raw` if `Write` is implemented. (Logged as a deferral if skipped.)

- [ ] **Task 3 — The content-validator: caps + admissibility + minting (AC2, AC4, AC5)**
  - [ ] 3.1 Add two named caps to `godot/src/Effects/EffectCaps.cs` (CHM0004-clean; reserved for the Epic-9 `rulesetHash`): `public const int MaxSearchAreaDepth = 2;` (max `SearchArea` nodes on any root→leaf path → worst case ≤ `MaxSearchTargets²` = 4096 executions/cast) and `public const int MaxTotalEffectNodes = 64;` (total graph node-count ceiling). Document the 64⁸-hang rationale on both.
  - [ ] 3.2 New `godot/src/Core/Definitions/AbilityValidator.cs`, `public sealed class AbilityValidator` with `public AbilityValidationResult Validate(AbilityDefinition def)` (pure C#, no `using Godot;`, located errors, **never throws**). Rules, each → `AbilityValidationResult.Fail("ability '<id>'.<path>: <reason>")`:
    - **a.** `def` / `def.Id` non-null/non-empty; `ParsedTargeting` resolves (unknown targeting string → reject).
    - **b.** Costs ≥ 0 (`CostEnergy`/`CostOre`/`CostCrystal`); `Cooldown` ≥ 0. (`FixedJsonConverter` already rejected NaN/Inf/over-range at parse; this guards sign + the int costs.)
    - **c.** `def.EffectGraph` non-null **(≥1 effect node — AC1's floor)**.
    - **d.** `EffectBounds.Validate(def.EffectGraph)` — depth ≤ 8, per-`Sequence` ≤ 8 (reuse the 2.1 gate verbatim; surface its `EffectBoundsResult.Error` with the ability id prefixed). **Do not re-derive depth/fan-out.**
    - **e.** **Total-work (AC4):** an iterative graph walk (explicit stack, like `EffectBounds`) that (i) counts total nodes ≤ `MaxTotalEffectNodes`, and (ii) tracks the running `SearchArea`-nesting count on each path ≤ `MaxSearchAreaDepth`.
    - **f.** **Re-entrancy + period-shape (AC5):** within **any** `PersistentEffect`'s `InitialEffect`/`PeriodEffect`/`ExpireEffect` subtree, reject any `ApplyModifierEffect` or nested `PersistentEffect` (install-leaf re-entrancy); within a `PersistentEffect.PeriodEffect` subtree specifically, reject any `SearchAreaEffect` (no per-tick spatial rebuild). A top-level `ApplyModifier`/`Persistent` is allowed.
  - [ ] 3.3 New `public readonly struct AbilityValidationResult { bool Ok; string? Error; Validated<AbilityDefinition> Value; }` + `Pass`/`Fail` factories — **a parallel of `ValidationResult`** (which is hardcoded to `Validated<ScenarioData>`). Do **not** generalize/retype `ValidationResult` (zero blast radius on the ScenarioData gate; see Decision #2).
  - [ ] 3.4 **Mint `Validated<AbilityDefinition>` via the shared `ScenarioValidator.Proof`** (`new Validated<AbilityDefinition>(def, new ScenarioValidator.Proof())`). 🚨 **This `new Validated<` will FAIL the build** under `ValidatedMintingTests.NewValidated_AppearsOnlyInScenarioValidator` (a source-scan that allow-lists only `ScenarioValidator.cs`). **Extend that test's allow-list to `{ ScenarioValidator.cs, AbilityValidator.cs }`** in the SAME change, and update its failure message — the sole-minter guarantee becomes "only the validator files mint," a documented allow-list (see Dev Notes §Validated). _(Alternative if Decision #2 = fold-in: put the method on `ScenarioValidator` and no allow-list edit is needed — Decision #2.)_

- [ ] **Task 4 — The fail-closed ability loader (AC1, AC2, AC6)**
  - [ ] 4.1 New `godot/src/Core/Definitions/AbilityLoader.cs` (or a static method): `public static AbilityValidationResult Load(string json, string sourceLabel)` (+ optional `LoadFromFile(absPath)` mirroring `ScenarioSerializer.LoadFromFile`). Flow: `try { def = JsonSerializer.Deserialize<AbilityDefinition>(json, ContentJson.Options); } catch (JsonException ex) { return Fail("ability '<id?>'.effect: " + ex.Message); }` → then `new AbilityValidator().Validate(def)`. **Never return null; never let a `JsonException` escape the loader** (architecture rule — loaders fold parse errors into located results). If the id is unparseable from a malformed doc, locate by `sourceLabel`.
  - [ ] 4.2 Resource home: create `godot/resources/data/abilities/` and ship **1–2 sample valid ability JSONs** (e.g. a targeted `damage` + a self `heal`, or an `apply_modifier` buff) for Story 2.4 to consume. The bulk of 2.3's coverage is **test fixtures** (valid + the AC2/AC4/AC5 negatives), authored as JSON **string literals** in the test files (negatives need raw JSON the converter rejects).

- [ ] **Task 5 — Tier-1 tests — new `godot/ProjectChimera.Sim.Tests/Definitions/` (AC1–AC6)** _(mirror `Validation/NegativeValidationTests.cs` + `Effects/EffectExecutorDeterminismTests.cs`; bare worlds via `EntityWorld.Create`, `Fixed.FromInt` only, **independently-derived raws**, no Godot, no `SimulationHost`)._
  - [ ] 5.1 `AbilityDeserializeTests.cs` (AC1): a valid ability JSON → `AbilityDefinition` with expected fields (pin `Fixed.Raw` independently); the `effect` compiles to the expected `EffectNode` tree (assert concrete types + field values, e.g. a `SequenceEffect` whose `Children[0]` is a `DamageEffect` with `Amount.Raw == Fixed.FromInt(25).Raw` and `Type == DamageType.Magic`).
  - [ ] 5.2 `EffectNodeConverterTests.cs` / closed-registry (AC2, AC6): every registered `kind` round-trips to its sealed type (the `EffectRegistryCoverageTest` analogue — each of the 8 kinds parses); **unknown `kind` → located `JsonException`** (teeth); **missing required field → located** (teeth); **two unknown-property teeth at BOTH layers** — an unknown field on the `AbilityDefinition` POCO (`Disallow`) AND an unknown property inside an effect-node object (the converter's own rejection, Task 2.2 — RED if the per-kind reader skips unrecognized tokens); 9-deep JSON → located parse error, **not** a stack overflow (Task 2.3 teeth); assert no source file uses `[JsonPolymorphic]`/`[JsonDerivedType]` (cheap regex guard).
  - [ ] 5.3 `NegativeAbilityValidationTests.cs` (AC2, AC4, AC5): NaN/Inf/over-range number → located reject; depth-9 graph → reject; `Sequence` 9 children → reject; **3-nested-`SearchArea` → reject, 2-nested → pass** (AC4 teeth); **65-node graph → reject, 64 → pass** (AC4 teeth); **`ApplyModifier` inside a `Persistent` phase → reject; the same `ApplyModifier` at top level → pass** (AC5 teeth); `SearchArea` inside a `PeriodEffect` → reject; negative cost / negative cooldown / unknown targeting → reject. Each asserts the located error `Contains` the ability id **and** the offending path/kind.
  - [ ] 5.4 `AbilityDeterminismTests.cs` (AC1): load a JSON ability → compile → `EffectExecutor.Run` on two fresh identical worlds → `SimChecksum.Compute` **equal** across runs; **negative control**: a structurally different graph yields a **different** hash (proves the test isn't vacuous). Use the dormant-state `Compute` (no `ModifierStore` cast needed — pass an empty `new ModifierStore(world)` for the 6-arg `Compute` per the 2.2b signature).
  - [ ] 5.5 `AbilityMintingTests.cs` (AC2): the updated sole-minter allow-list holds (`new Validated<` appears only in `{ScenarioValidator.cs, AbilityValidator.cs}`); a failed `Validate` returns `Ok==false` with a `default`/unusable `Value` (no runnable graph escapes).
  - [ ] 5.6 `AbilityScriptReservationTests.cs` (AC3): assert the registry contains **no** script/delegate/random kind (reuse/extend 2.1's `EffectVocabularyTests` reflection scan reasoning — no node type exposes a `Delegate`/`Func`/`object` field); `EntityWorld.Rng` is non-null (SimRng present); a hypothetical `"kind":"random_pick"` JSON is rejected as unknown (the reservation has teeth via the closed registry). Document the AR-13 reservation in-test.

- [ ] **Task 6 — Verify, confirm determinism posture, document deferrals**
  - [ ] 6.1 `dotnet build godot/ProjectChimera.Sim.Tests -c Release` + `dotnet test` green (baseline **346 pass / 1 skip / 0 fail** post-2.2b → +N new). Full `godot.csproj` (production, Godot SDK) build: **0 errors**. `ProjectChimera.Sim.Analysis` analyzer build: **0 CHM/RS0030 findings** in the new files.
  - [ ] 6.2 **Determinism posture (the load-bearing invariant — 2.3 is a loader/validator, like 2.1/2.2a it does NOT fold):** confirm `SimChecksum.AlgoVersion` stays **6**, `SimChecksum.cs` **untouched**, **all 8 goldens byte-identical** (`git status --short -- '*.golden.txt'` clean), `SystemOrderTest.cs` untouched (no new tick system — the validator/loader/converter are invoked by data, not the tick loop; precedent `EffectBounds`/`EffectExecutor`). 2.3 adds **no new mutable per-entity SoA array** (abilities aren't cast until 2.4) → no fold, no bump, no re-record. [[chimera-checksum-fold-timing-rule]]
  - [ ] 6.3 **Prove every new gate has teeth (A3 — inject → observe RED → revert; record in the DAR):** unknown-kind RED without the registry reject; total-work RED without the `MaxSearchAreaDepth`/`MaxTotalEffectNodes` checks; install-in-`Persistent` RED without the AC5 rule; mint allow-list RED if a stray `new Validated<` is added elsewhere; `Disallow` RED if an unknown field is silently ignored.
  - [ ] 6.4 Append `deferred-work.md` §"story 2.3": converter `Write`/authoring round-trip (editor = 2.5) if skipped; migrating `ScenarioSerializer`/`FactionDefinition` → `ContentJson.Options` (the D3 single-choke-point consolidation); the runtime ability-cast `EffectContext`+`ModifierStore` wiring + per-ability cooldown SoA + Energy/ore/crystal debit (2.4); folding `MaxSearchAreaDepth`/`MaxTotalEffectNodes`/structural caps into the Epic-9 `rulesetHash`; and re-surface the now-JSON-reachable 2.2b content concerns (stacked-DoT-not-scaled-per-stack, `DurationTicks==0` one-shot semantics, 256-pulse truncation) as **content-authoring** items the ability validator may later warn on.

---

## Dev Notes

### What this story is — the AR-22 keystone

This is the **JSON authoring surface for the closed effect graph**: the data model (`AbilityDefinition`) + the polymorphic **closed-registry converter** (`EffectNodeJsonConverter`, AR-22) + the **static content-validator** (`AbilityValidator` → `Validated<AbilityDefinition>`, AR-39) + a **fail-closed loader**. It is the first point an **effect schema authored as data** exists — every later ability (2.4/2.5/2.6), the showcase signature mechanics (2.10), and (reused unchanged) the trigger DSL's embedded effect subgraphs (Story 7.1b-1) deserialize through exactly this converter. **2.3 builds only the model + converter + validator + loader — NOT the editor UI (2.5) and NOT the runtime cast path (2.4).**

> **AR-22 (`epics.md:207`):** Custom `JsonConverter<NodeBase>` + closed type registry (discriminator `kind`); `[JsonPolymorphic]`/`[JsonDerivedType]` **forbidden project-wide**; unknown kind / dangling ref / missing required field is a hard fail-closed error.
> **Architecture rule (`game-architecture.md:2042`):** "The `EffectNode` JsonConverter dispatches on a closed `kind` discriminator against a hardcoded registry … `[JsonPolymorphic]` is incompatible with `UnmappedMemberHandling.Disallow` (dotnet/runtime #100057) and throws at runtime on the first real node."

**Scope boundary vs Story 7.1b-1 (settled):** 2.3 owns the **D1 `EffectNode`** converter (the 8 sealed effect types). 7.1b-1 later builds the **DSL `NodeBase`** graph converter (persistent node ids + exec/data edges) which **embeds 2.3's D1 effect subgraphs unchanged — no second converter, no second executor** (`epics.md:1786-1798`). Build the converter targeting the as-built runtime type `EffectNode` (the architecture's planned `EffectDef.cs` name is superseded — 2.1 made the runtime `EffectNode` immutable/pure-data, so it IS the serialization target).

### Scope — BUILD vs REUSE vs DEFER

| Item | 2.3 action | Why |
|---|---|---|
| `AbilityDefinition` model + `AbilityTargeting` enum | **BUILD** | The FR-12 data model; mirrors `UnitDefinition`. |
| `EffectNodeJsonConverter` (closed `kind` registry over the 8 types) | **BUILD** | AR-22; AC1 "compiles to a 2.1 graph" + AC2 "reject unknown node type" are impossible without it. |
| `ContentJson.Options` (one options object, abilities only) | **BUILD** | AC6; the architecture's single-choke-point seed. |
| `AbilityValidator` + `AbilityValidationResult` + caps | **BUILD** | AR-39/AR-13; the located-error gate + the AC4/AC5 carve-offs. |
| `AbilityLoader` (located, never-null/throw) | **BUILD** | AC2/AC6 loader contract. |
| `EffectBounds.Validate`, `EffectExecutor`, the 8 `EffectNode` types, `Modifier`, `EffectCaps` | **REUSE verbatim** | 2.1/2.2b shipped them; do **not** reinvent or re-derive depth/fan-out semantics. |
| `Validated<T>`, `ScenarioValidator.Proof`, `FixedJsonConverter`, `JsonStringEnumConverter`, `SimRng` | **REUSE** | 1.4/1.5/1.7 shipped them. |
| Runtime ability **cast** path (`EffectContext`+store wiring, cooldown SoA, cost debit, command card) | **DEFER → 2.4** | 2.3 validates the model; nothing casts yet. |
| Editor UI (presets / multi-effect / raw-JSON) | **DEFER → 2.5/2.6** | "model + validator + loader, not the editor UI." |
| Migrate `ScenarioSerializer`/`FactionDefinition` to `ContentJson.Options` | **DEFER → D3 consolidation** | Out of scope; 1.7 explicitly fenced "do not unify `JsonSerializerOptions`." |
| A random/script effect leaf | **DO NOT BUILD** | No random `kind` exists; AR-13 discharged by reservation (AC3). |

### 🔑 Determinism posture — 2.3 does NOT fold (same as 2.1/2.2a, OPPOSITE of 2.2b)

2.3 adds **no new mutable sim state**. The converter/validator/loader run at **load time**; the only sim mutation is the AC1 two-run test executing a compiled graph (transiently mutating already-hashed `Health`, exactly like 2.1's executor tests). Therefore: **`SimChecksum.AlgoVersion` stays 6, `SimChecksum.cs` is untouched, all 8 goldens stay byte-identical, no new golden is added, `SystemOrderTest` is untouched.** [[chimera-checksum-fold-timing-rule]] — fold only when an array first goes mutable mid-match; abilities don't mutate state until they're **cast** (Story 2.4). **If any golden moves, you leaked state into the tick — find and fix it; do NOT re-record.**

### The 2.1 effect vocabulary — exact runtime types the converter builds (copy-accurate; do NOT re-derive)

All in `ProjectChimera.Effects` (`godot/src/Effects/`). All ctors **public**. Discrimination in code is by **concrete type** (there is NO `kind` field on the C# types — `kind` lives only in JSON, mapped by your registry).

```csharp
// Bases (EffectNode.cs): abstract EffectNode (private protected ctor — no external subclass)
//   → abstract LeafEffect (internal Apply) ; → abstract CompositionEffect
sealed DirectHpDeltaEffect(Fixed delta)                               // flat, armor-independent (Equal-Exchange)
sealed HealEffect(Fixed amount)                                       // clamps to EffectiveMaxHealth
sealed DamageEffect(Fixed amount, DamageType type)                    // via DamageResolver (matrix)
sealed ApplyModifierEffect(Modifier modifier)                        // executes vs ctx.ModifierStore (2.2b)
sealed SequenceEffect(params EffectNode[] children)                   // ≤ MaxSequenceChildren
sealed SearchAreaEffect(Fixed radius, TargetFilter filter, EffectNode child)   // fan-out ascending-id ≤ MaxSearchTargets
sealed PersistentEffect(EffectNode? initial, EffectNode? period, EffectNode? expire, int periodTicks, int periodCount)
// First-class descriptor (Modifier.cs), public ctor:
sealed Modifier(int id, int durationTicks, StackRule stacking, int maxStacks,
                Fixed maxHealthDelta, Fixed attackDamageDelta, Fixed moveSpeedDelta,
                StatusFlags status, EffectNode? periodEffect, int periodTicks)
// Enums (by NAME in JSON): DamageType {Normal,Pierce,Siege,Magic,Hero} (ProjectChimera.Combat);
//   StackRule {Refresh,Stack,Ignore}; [Flags] StatusFlags {None,Stunned,Rooted,Silenced,Disarmed,Invulnerable};
//   [Flags] TargetFilter {None,Self,Ally,Enemy,Neutral,Alive, Air,Ground,Structure(reserved→2.9a)}
```

**`EffectCaps` (`src/Effects/EffectCaps.cs`) — existing:** `MaxEffectDepth=8`, `MaxSequenceChildren=8`, `MaxSearchTargets=64`, `MaxHitsPerSearch=64`, `MaxEffectFrames=505`, `MaxSpawnCount=64`, `MaxPersistentPeriods=256`, `MaxModifiersPerEntity=8`. **Add (Task 3.1):** `MaxSearchAreaDepth=2`, `MaxTotalEffectNodes=64`.

**`EffectBounds.Validate` (`src/Effects/EffectBounds.cs`) — REUSE verbatim:**
```csharp
public static EffectBoundsResult Validate(EffectNode? root);   // iterative; null → Valid
public readonly struct EffectBoundsResult { public readonly bool IsValid; public readonly string? Error; }
```
It caps **depth and per-Sequence width only** — it does **NOT** count total nodes, does **NOT** bound `SearchArea` nesting, and **descends into** `Persistent`/`ApplyModifier` (it returns `IsValid=true` for graphs your AC4/AC5 rules must still reject). Your `AbilityValidator` composes on top of it.

### The validator rules — and which carve-off each closes

| Rule | AC | Closes |
|---|---|---|
| Unknown `kind` rejected (closed registry) | AC2 | AR-22; also makes un-built kinds (SetVariable/FireProjectile/…) unauthorable = the 2.1-review **#4** "node-kind admissibility" (those `kind`s simply aren't registered). |
| NaN/Inf/over-range number rejected | AC2 | `FixedJsonConverter` at parse — the "no float gameplay value" rule (numbers are quantized to `Fixed`; only non-finite/over-range is a "float" violation). |
| Depth ≤8, Sequence ≤8 (`EffectBounds`) | AC2 | 2.1's structural gate, reused. |
| `MaxSearchAreaDepth` + `MaxTotalEffectNodes` | AC4 | 2.1-review **#2** (the 64⁸ execution hang — bound worst-case work, not just size). |
| No `ApplyModifier`/nested `Persistent` inside a `Persistent` phase | AC5 | 2.1-review **#4** (deferred-node admissibility now that they execute) + 2.2b-review **W1** (the dedicated-executor re-entrancy hazard). |
| No `SearchArea` inside a `PeriodEffect` | AC5 | 2.2b carve-off **#1** ("restrict periods to direct-target at the validator" — no per-tick `SpatialHash` rebuild exists). |
| Cost ≥0, Cooldown ≥0, targeting in closed set, ≥1 effect | AC1/AC2 | The FR-12 model floor. |

> **Why top-level `ApplyModifier`/`Persistent` is now ACCEPTED (changed since the 2.1 deferral was written):** the 2.1-review carve-off said "the 2.3 validator must reject `Persistent`/`ApplyModifier` **until 2.2b ships their execution**." **2.2b shipped** (both resolve against `ctx.ModifierStore`). So 2.3 accepts them at top level and only rejects the **nested-in-Persistent-phase** re-entrancy case. Don't blanket-reject them.

### `Validated<T>` + the sole-minter guard (exact — `src/Core/Definitions/Validated.cs`, Story 1.7)

```csharp
public readonly struct Validated<T> { public T Value { get; }
    public Validated(T value, ScenarioValidator.Proof proof) { Value = value; } }   // requires a Proof token
// inside ScenarioValidator: public sealed class Proof { internal Proof() { } }     // assembly-internal ctor
```
The **real** sole-minter guarantee is a **source-scan test**: `ValidatedMintingTests.NewValidated_AppearsOnlyInScenarioValidator` regex-scans every `*.cs` for `new\s+Validated\s*<` and fails the build if any file **other than `ScenarioValidator.cs`** matches. **Task 3.4 adds a second mint (`Validated<AbilityDefinition>`) → you MUST extend that allow-list to `{ScenarioValidator.cs, AbilityValidator.cs}`** (and the failure message) in the same change, or the build goes RED. `Validated<T>` is already generic, so `Validated<AbilityDefinition>` needs **no new type** — just the mint + the allow-list edit. (The legacy `default(Validated<T>)` bypass noted in 1.7's deferral is irrelevant here: the loader always routes through `Validate` and reads `Value` only on `Ok`.)

`ValidationResult` (same file) is **hardcoded** to `Validated<ScenarioData>` (`Pass(Validated<ScenarioData>)`, `Fail(string)`), so abilities get a **parallel** `AbilityValidationResult` (Decision #2) — do not retype the existing one.

### `FixedJsonConverter` (exact — `src/Core/Definitions/FixedJsonConverter.cs`, Story 1.4)

```csharp
public override Fixed Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o) {
    if (r.TokenType != JsonTokenType.Number) throw new JsonException("Expected a JSON number for Fixed…");
    double d = r.GetDouble();
    if (double.IsNaN(d) || double.IsInfinity(d)) throw new JsonException($"Fixed value must be finite; got {d}.");
    float f = (float)d;                                   // range-check the POST-CAST float (sign-flip guard)
    if (f >= 32768f || f < -32768f) throw new JsonException($"Fixed value {d} is out of 16.16 range…");
    return Fixed.FromFloat(f);                            // the SOLE allow-listed FromFloat on external data
}
```
This is "reject float gameplay values" (AC2): it throws a `JsonException` for non-finite/over-range numbers; your loader's `try/catch` (Task 4.1) folds that into a **located** `AbilityValidationResult.Fail` naming the ability + path. Register it in `ContentJson.Options` so every `Fixed` field on `AbilityDefinition`/`Modifier`/leaves quantizes once at parse — **never** call `Fixed.FromFloat` anywhere else (CHM0005).

### AR-13 / SimRng — the honest reading (do not over-build)

`SimRng` (`src/Core/SimRng.cs`, Story 1.5) is **unconditionally present** — `EntityWorld.Rng` is a non-null `class`, no "absent" state exists. The 2.1 vocabulary has **no random leaf type**. So AC3's "random effect leaf" clause is satisfied by **reservation** (the 1.7 precedent, which already discharged AR-13 this way and named **Story 2.3** as the home for the mature enforcement): a `"kind":"random_*"` is rejected as unknown today, and the accept-if-SimRng-present / reject-if-absent check lands with the story that first adds a random leaf. The **testable** half of AC3 is the **no-script guarantee** — structural, free from the closed registry + 2.1's `EffectVocabularyTests` (no node carries a `Delegate`/`Func`/`object`/scripting field). **Do not fabricate a random node or a SimRng-absent code path.**

### Live APIs you will call (exact signatures — do not re-derive)

- **Deserialize:** `JsonSerializer.Deserialize<AbilityDefinition>(json, ContentJson.Options)` (the ONLY deserialize site for abilities).
- **Bounds:** `EffectBounds.Validate(EffectNode?) → EffectBoundsResult{bool IsValid; string? Error}`.
- **Execute (AC1 test):** `var ex = new EffectExecutor(); ex.Run(EffectNode? root, in EffectContext ctx);` — `EffectContext(EntityWorld world, int casterId, int primaryTargetId, Faction casterFaction, DamageTable damageTable, SpatialHash? spatial=null, CombatEventQueue? events=null, MatchStats? stats=null, ModifierStore? modifierStore=null)`; `WithTarget(int)`.
- **Checksum (AC1 test):** `SimChecksum.Compute(EntityWorld, BuildingStore, ResourceStore, FactionRegistry, ModifierStore)` — `AlgoVersion=6`; pass an empty `new ModifierStore(world)` for a no-modifier world. **Do not modify `SimChecksum`.**
- **World (tests):** `int EntityWorld.Create(FixedVec3, Faction, Fixed health, Fixed speed)`; `bool IsAlive(int)`; `SimRng Rng`; `MAX_ENTITIES=4096`.
- **Fixed:** `Fixed.FromInt(int)`, `Fixed.FromRaw(int)`, `.Raw`, `Fixed.Zero` — `Fixed.FromFloat` **only** inside `FixedJsonConverter` (CHM0005). Tests author with `Fixed.FromInt` only.
- **Mirror loaders:** `ScenarioSerializer.LoadFromFile(absPath)` (the load shape), `FactionDefinition.LoadFromFile` (per-type options precedent).

### Testing discipline (the code-review will check)

- **No tautological asserts** — pin observable outcomes against **independently-derived** `Fixed.Raw` (precedent `DamageResolverTests`/`NegativeValidationTests`). The Acceptance Auditor re-derives your pins.
- **Every gate ships with teeth (A3):** each reject rule needs a positive case AND a negative control that is demonstrably RED without the rule (unknown-kind; 3-nested-SearchArea; 65-node; ApplyModifier-in-Persistent; unknown field under `Disallow`). Record inject→observe→revert in the DAR.
- **Located errors** — every reject names the ability id + the node-kind/field path; assert with `Assert.Contains` on both halves (mirror `Assert.Contains("player_slots[0].start_ore", r.Error!)`).
- **Cover boundaries** — depth 8 vs 9; Sequence 0/1/8/9 children; SearchArea nesting 2 vs 3; total nodes 64 vs 65; empty `effect` (0 nodes → reject); top-level vs nested `ApplyModifier`/`Persistent`; cost 0 vs −1.
- **Test home:** new `godot/ProjectChimera.Sim.Tests/Definitions/` (namespace `ProjectChimera.Sim.Tests.Definitions`). Negatives use JSON **string literals** (the converter must see raw bytes). Auto-compiles (globbed test folder).

### Project Structure Notes

- **New (sim):** `src/Core/Definitions/{AbilityDefinition.cs, AbilityTargeting enum, ContentJson.cs, EffectNodeJsonConverter.cs, AbilityValidator.cs, AbilityValidationResult, AbilityLoader.cs}`; `src/Effects/EffectCaps.cs` gains 2 consts.
- **New (data):** `resources/data/abilities/` + 1–2 sample ability JSONs.
- **New (tests):** `ProjectChimera.Sim.Tests/Definitions/*.cs`.
- **Modified (test):** `ProjectChimera.Sim.Tests/Validation/ValidatedMintingTests.cs` — allow-list `+= AbilityValidator.cs`.
- **No `SimSources.props` edit, no `.csproj` edit** (Definitions + Effects already globbed; new test `.cs` auto-compile; no embedded golden artifact). **No NuGet** (sim stays dependency-free / AOT-eligible).
- **Out of scope (do not touch):** `SimChecksum.cs` / any `.golden.txt` / `SystemOrderTest.cs` (no fold, no new system); `EffectExecutor.cs` / `ModifierStore.cs` / the 8 `EffectNode` types / `Modifier.cs` (reuse, don't modify); `ScenarioSerializer`/`FactionDefinition` options (no migration); `EntityWorld` / `UnitDefinition` (no authored `MaxEnergy`/abilities-list — that's 2.4/2.2b-deferred); `CommandCardSystem`/UI (2.4); the DSL `NodeBase` converter (7.1b-1).
- **Single-mapper SoA rule (A2):** does **not** apply — 2.3 adds **no per-entity SoA array** (its output is a validated data object, not entity state). The first per-entity ability state (cooldown SoA) lands in **2.4** and must flow through `ApplyUnitDefinition` then.

### Project Context Rules

_From `_bmad-output/project-context.md`:_
- **Sim/Presentation boundary is sacred.** `src/Core/Definitions` + `src/Effects` are sim: **no `using Godot;`**, no Node types, no `float` gameplay state. The converter/validator are Godot-free, AOT-eligible (`GodotFreeBoundaryTest` fails the build on `using Godot`).
- **Everything is data-driven (the platform rule).** The effect graph IS the composition primitive — an ability is JSON a creator edits, validated before it can break a match; **no scripting escape hatch ever** (no Lua/JASS/`RunScript`/`customParams`/delegate payloads).
- **Determinism:** content carries `Fixed` end-to-end, quantized once via `FixedJsonConverter`; the closed registry + ascending-id execution + `SimRng`-only randomness keep two peers building & running the identical graph.
- **Composition over inheritance:** sealed closed set; a new effect is a new **sealed** type in its owning story (does not violate AR-8's closedness), never an open/virtual node.
- **Conventions:** `PascalCase.cs` == class; `ProjectChimera.Core.Definitions`/`ProjectChimera.Effects`; PascalCase types/methods, camelCase locals, SCREAMING_CASE consts; `#nullable enable`; comment public methods + non-obvious logic.
- **Brownfield:** small, shippable, always-green; reuse `EffectBounds`/`EffectExecutor`/`Validated<T>`/`FixedJsonConverter` — build no parallel system.

### References

- **Story + epic scope:** `epics.md#Story-2.3` (894-910); Epic 2 sequencing note (840); consumers 2.4 (912-928), 2.5/2.6; the boundary story 7.1b-1 (1786-1798).
- **Requirements:** FR-12 (`epics.md:79`), FR-10 (`:77`), NFR-4 (`:150`), NFR-6 (`:152`); AR-8 (`:187`), AR-13 (`:194`), AR-22 (`:207`), AR-39 (`:232`).
- **Architecture:** `game-architecture.md` — AR-22 converter rule (`:2042-2045`), the `ContentJson.Options`/`ContentLoader` single-choke-point rule (`:2220-2221`), `FixedJsonConverter` + `NodeBaseJsonConverter` rule + code (`:2248-2263`), `Validated<T>`/`ScenarioValidator` consumption rule (`:2223-2231`), the `ContentError`/`LoadResult` never-null rule (`:2265`), the file layout (`:1617-1629` — `EffectDef.cs`/`NodeBaseJsonConverter.cs`/`ContentLoader.cs`/`ChimeraJsonContext.cs` in `Definitions`), the enforcement tests `ClosedRegistryTest`/`EffectRegistryCoverageTest`/`NegativeValidationTest` (`:2045`, `:2290`).
- **Live source (REUSE):** `src/Effects/{EffectNode,DirectHpDeltaEffect,HealEffect,DamageEffect,ApplyModifierEffect,SequenceEffect,SearchAreaEffect,PersistentEffect,Modifier,EffectCaps,EffectBounds,EffectExecutor,EffectContext,ModifierStore}.cs`; `src/Core/Definitions/{UnitDefinition,Validated,ScenarioValidator,FixedJsonConverter,ScenarioSerializer,FactionDefinition}.cs`; `src/Core/{SimRng,SimChecksum,EntityWorld}.cs`; `src/Combat/{DamageResolver,DamageTable}.cs`.
- **Carve-offs this story discharges:** `deferred-work.md` §"story 2.1" item 1 (Persistent/ApplyModifier now executable — accept top-level) + §"code review of story-2.1" items 2 & 4 (total-work cap; node-kind admissibility) + §"code review of story-2.2b" item 1 / W1 (re-entrancy guard) + §"story 2.2b" carve-off 1 (period direct-target).
- **Test patterns:** `Validation/NegativeValidationTests.cs` (positive + located-negative), `Validation/ValidatedMintingTests.cs` (the source-scan to extend), `Effects/EffectExecutorDeterminismTests.cs` (two-run `Compute` equality), `Golden/GoldenChecksumReplay.cs` (`CHIMERA_GOLDEN_RECORD`, only if a golden were needed — it is **not**), `Combat/DamageResolverTests.cs` (independently-pinned raws).
- **Prior-story lessons:** `epic-1-retro-2026-06-25.md` (A1 3-layer review, A2 single-mapper, A3 prove-gates-have-teeth); Stories 1.4/1.5/1.7 (converter/RNG/Validated), 2.1/2.2b (the vocabulary + executor + store this consumes).

---

## Open Decisions for Alec (defaults baked in; confirm or override)

> Per the create-story protocol, the story is written end-to-end with recommended defaults so it is immediately implementable. Three forks genuinely benefit from your call; the rest are resolved-by-default with rationale.

**Decision #1 — Effect-graph deserialization target (NEEDS YOUR CALL; default baked).**
- **Option A (recommended, baked in):** the `EffectNodeJsonConverter` deserializes JSON **directly into the 2.1 runtime `EffectNode` types** (their public ctors). "Compiles to a 2.1 effect graph" = the converter's output IS the runtime graph. **Pro:** zero parallel hierarchy, reuses 2.1 verbatim, fewest moving parts; the immutable runtime types already ARE pure data. **Con:** mildly diverges from the architecture's separate-`EffectDef`-DTO file name (superseded — see Dev Notes).
- **Option B:** a separate serializable `EffectDef` DTO tree + a `Compile()` step to the runtime `EffectNode`. **Pro:** literal match to the architecture's `EffectDef.cs`; wire-format decoupled from runtime. **Con:** a second parallel node hierarchy to keep in lock-step with the 8 sealed types (maintenance + drift risk), more code for no 2.3 behavioral gain. _Recommendation: **A** — the runtime types are the IR; B's indirection earns nothing until a wire/runtime split is actually needed._

**Decision #2 — Validator home + the sole-minter allow-list (NEEDS YOUR CALL; default baked).**
- **Option A (recommended, baked in):** a **new `AbilityValidator.cs`** mints `Validated<AbilityDefinition>` via the shared `ScenarioValidator.Proof`, and `ValidatedMintingTests`' allow-list is extended to `{ScenarioValidator.cs, AbilityValidator.cs}`. **Pro:** a focused, testable validator that owns ability rules; the mint guarantee becomes a documented multi-file allow-list. **Con:** one deliberate test edit (the build goes RED until the allow-list is updated — flagged loudly in Task 3.4).
- **Option B:** put `ValidateAbility(...)` **as a method on `ScenarioValidator`** (the mint stays inside `ScenarioValidator.cs` → **no allow-list edit**). **Pro:** zero change to the sole-minter test. **Con:** `ScenarioValidator` accretes non-scenario responsibilities (conceptual smell). _Recommendation: **A** — keep validators cohesive; the allow-list extension is the honest, auditable way to add a second proof-of-validation type._

**Decision #3 — The two anti-hang cap values (NEEDS YOUR CALL; tuning, default baked).** `MaxSearchAreaDepth = 2` (≤ 64² = 4096 executions/cast — chain-lightning fits; 3-deep area cascades are rejected) and `MaxTotalEffectNodes = 64`. _Default: 2 / 64 — safe and generous for authored abilities; raise either if real content needs deeper area cascades or larger graphs (they're named constants, trivially tunable, and reserved for the Epic-9 `rulesetHash`)._

**Resolved-by-default (override if you disagree):**
- **#4 — Field types:** `Cooldown` + `CostEnergy` are `Fixed` (content-is-Fixed rule; energy matches the `Fixed` Energy SoA); `CostOre`/`CostCrystal` are `int` (mirror `UnitDefinition`). 2.4 owns Fixed-seconds→tick conversion + the debit.
- **#5 — No new golden, no fold:** AC1's "golden checksum" is met by a **two-run `SimChecksum.Compute` equality unit test** (2.1 precedent), not a persisted `.golden.txt`. `AlgoVersion` stays 6, all 8 goldens byte-identical (2.3 mutates no persistent sim state). [[chimera-checksum-fold-timing-rule]]
- **#6 — AR-13 by reservation:** no random leaf is built; AC3 is met by the closed-registry no-script guarantee + the documented reservation (the 1.7 precedent). No SimRng-absent path is fabricated.
- **#7 — `ContentJson.Options` scope:** introduced for abilities only; `ScenarioSerializer`/`FactionDefinition` are NOT migrated to it (deferred D3 consolidation).

---

## Dev Agent Record

### Agent Model Used

_(to be filled by `gds-dev-story`)_

### Debug Log References

### Completion Notes List

### File List

### Change Log

| Date | Change |
|---|---|
| 2026-06-26 | Story 2.3 created (`gds-create-story`). Exhaustive context-engine analysis: 2 parallel research subagents (Effects vocabulary surface + validation/definitions/JSON/SimRng/test surface) + direct line-level reads of `EffectBounds.cs`, the 2.1 keystone story, the 2.2b ModifierStore story, the full `deferred-work.md` ledger, `game-architecture.md` (AR-8/13/22/39 + the `ContentJson.Options`/`FixedJsonConverter`/`NodeBaseJsonConverter` rules + file layout), `epics.md` (Story 2.3 + 7.1b-1 boundary + FR/NFR/AR texts), and `project-context.md`. Scope: net-new `AbilityDefinition` model + `AbilityTargeting` enum + the **closed-registry `EffectNodeJsonConverter`** (AR-22, deserializes JSON directly into the 8 runtime `EffectNode` types — Decision #1=A) + one canonical `ContentJson.Options` (Disallow + 3 converters) + `AbilityValidator`→`Validated<AbilityDefinition>` (Decision #2=A, allow-list extended) composing `EffectBounds.Validate` with **3 new gate families** that discharge the 2.1/2.2b review carve-offs: AC4 total-work caps (`MaxSearchAreaDepth`/`MaxTotalEffectNodes`, the 64⁸-hang fix), AC5 re-entrancy/period-shape admissibility (install-leaf-in-Persistent + SearchArea-in-period), AC6 fail-closed loader. **Determinism posture: NO fold** (AlgoVersion stays 6, all 8 goldens byte-identical, no new system) — like 2.1/2.2a. AR-13 discharged by reservation (no random leaf built). 6 ACs (3 epic + 3 story-added), 6 tasks. 3 decisions need Alec (defaults baked: #1 deserialize-into-runtime-EffectNode, #2 new-AbilityValidator+allow-list, #3 cap values 2/64); 4 resolved-by-default. baseline `57dd610`. Status → ready-for-dev. NEXT — `gds-dev-story` on 2.3 (then 2.4 attaches abilities + builds the runtime cast path on top). |
