# Story 2.1: D1 Effect-Graph vocabulary and work-stack executor (pure sim keystone)

Status: ready-for-dev

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As an engine developer,
I want a single closed, typed, statically-bounded Effect-Graph surface in `src/Effects` with a pre-allocated work-stack executor,
so that every ability, the trigger DSL, and AI balance share one deterministic effect vocabulary that cannot recurse, overflow, or call float math.

## Acceptance Criteria

**AC1 — Closed, typed vocabulary, no escape hatch.**
**Given** the closed EffectNode vocabulary defined in `src/Effects` **When** the type set is reviewed **Then** it contains only sealed leaf nodes plus exactly three composition nodes (Sequence, SearchArea, Persistent) and a first-class Modifier, with no open/virtual extension point and no scripting hook **And** no leaf or composition type references Godot, `float`, `double`, `System.Random`, or wall-clock time.

**AC2 — Bounded, non-recursive, zero-alloc execution.**
**Given** an effect graph with composition depth or fan-out exceeding the cap (depth > 8 or fan-out beyond the configured limit) **When** the executor or its load-time bound check runs **Then** it is rejected/clamped at a statically-bounded limit and never recurses or grows the work-stack beyond its pre-allocated size **And** the executor uses a pre-allocated work-stack and performs zero heap allocation per execution.

**AC3 — Deterministic, ascending-id execution.**
**Given** an identical effect graph executed against an identical EntityWorld snapshot twice (and, if it contains a random leaf, with the same SimRng seed) **When** each run completes and a golden checksum is taken of the resulting world state **Then** the two checksums are byte-identical **And** nodes that mutate entities iterate targets in ascending entity-id order.

**AC4 — The Equal-Exchange-shaped non-matrix primitive.**
**Given** a unit-test harness for the executor **When** a Sequence of `{DirectHpDelta -10, Heal +25}` runs on one entity **Then** the non-matrix `DirectHpDelta` applies a flat armor-independent HP change (never routed through DamageMatrix) and the `Heal` applies after it, proving the Equal-Exchange-shaped self-cost primitive works.

_Covers: FR-12, AR-8, AR-13, NFR-4. Depends on: Epic 1 (all green — `SimRng` 1.5, `Validated<T>` 1.7, Godot-free sim spine 1.8a–c, `ModifierSystem` slot reserved at tick index 3)._

---

## Tasks / Subtasks

- [ ] **Task 1 — Scaffold `src/Effects/` and wire it into the build/test/analyzer surface (AC1, AC2)**
  - [ ] 1.1 Create `godot/src/Effects/` with namespace `ProjectChimera.Effects`. Pure C#, `#nullable enable`, no `using Godot;`.
  - [ ] 1.2 **🚨 LOAD-BEARING: add `..\src\Effects\**\*.cs` to `godot/ProjectChimera.Sim.Tests/SimSources.props`.** `src/Effects` is NOT in the existing glob (`Core/Combat/Economy/Navigation`). Without this the new code compiles into **neither** the Tier-1 test project **nor** the `ProjectChimera.Sim.Analysis` determinism analyzer **nor** `GodotFreeBoundaryTest` — a silent coverage hole. `SimSources.props` feeds both projects, so one edit fixes both.
  - [ ] 1.3 **Prove the gate now has teeth (A3):** temporarily drop a throwaway `src/Effects/_AnalyzerSmoke.cs` containing `using Godot;`, a `float`, and `new System.Random()`; build → observe `GodotFreeBoundaryTest` fail + analyzer CHM0001/RS0030 fire; delete the file. Record the observed failures in the Dev Agent Record.
  - [ ] 1.4 Define `EffectCaps` (static class of named constants — never bare literals, CHM0004): `MaxEffectDepth = 8`, `MaxSequenceChildren = 8`, `MaxSearchTargets = 64`, `MaxHitsPerSearch = 64`, and `MaxEffectFrames` **computed from** the worst-case nesting the other caps imply (document the derivation; size so a maximal valid graph never overflows — see Dev Notes §"Work-stack sizing"). Reserve `MaxSpawnCount = 64` / `MaxPersistentPeriods = 256` as named constants for later stories. These are the structural caps that fold into `rulesetHash` (the hash itself is a later/Epic-9 concern; here just name them, don't hardcode).

- [ ] **Task 2 — Define the closed EffectNode vocabulary (AC1)**
  - [ ] 2.1 `EffectNode` base: abstract, **non-extensible outside the assembly** (internal/private-protected ctor; no `public` open extension). Leaves carry an `internal abstract void Apply(in EffectContext ctx)`; composition nodes are dispatched by the executor (do not self-`Apply`). No `virtual` member reachable by data/creators.
  - [ ] 2.2 **Sealed leaf nodes — implement & execute end-to-end in 2.1:**
    - `DirectHpDeltaEffect` — flat `Fixed` HP delta, **armor-independent, never through DamageMatrix** (this is the Equal-Exchange self-cost primitive).
    - `HealEffect` — `Fixed` amount, clamped to `MaxHealth`.
    - `DamageEffect` — `Fixed` amount + `DamageType`, routes through `DamageResolver.Apply` (the one damage path; matrix lookup).
  - [ ] 2.3 **Composition nodes — exactly three, sealed:**
    - `SequenceEffect` — ordered `EffectNode[] Children` (≤ `MaxSequenceChildren`); executes children in authored order.
    - `SearchAreaEffect` — `Fixed Radius` + `TargetFilter` + a single child; fans out one child execution per matched entity in **ascending entity-id order** (≤ `MaxSearchTargets`).
    - `PersistentEffect` — `InitialEffect` / `PeriodEffect` / `ExpireEffect` + `periodTicks`/`periodCount`. **Define the type now** (it is one of the mandated three); its periodic time-axis execution resolves against the ModifierStore and lands in **Story 2.2b** (see Dev Notes §Scope).
  - [ ] 2.4 First-class `Modifier` descriptor (its own type, NOT a leaf): `id`, `durationTicks`, `stackRule {Refresh|Stack|Ignore}`, `maxStacks`, `Fixed` stat deltas, status flags, optional `periodEffect`/`periodTicks`. **Define the type + fields now**; `ApplyModifierEffect` leaf is defined but its store resolution lands in **2.2b**.
  - [ ] 2.5 `TargetFilter` — OR-able flag set. In 2.1 evaluate only `Self` / `Ally` / `Enemy` / `Neutral` / `Alive` (faction comparison + `IsAlive`). Reserve `Air` / `Ground` / `Structure` bits (evaluation lands in **2.9a**); do not wire building targeting here.
  - [ ] 2.6 No scripting hook anywhere: zero `Delegate`/`Func`/`Action`/`dynamic`/`object`-payload/free-text-code fields on any node (the closedness contract — enforced by the Task 5.1 structural test).

- [ ] **Task 3 — Pre-allocated work-stack executor (AC2, AC3)**
  - [ ] 3.1 `EffectContext` — `readonly struct` holding **references** (`EntityWorld World`, `SimRng Rng`) + value fields (`int CasterId`, `int PrimaryTargetId`, `Faction CasterFaction`) + the refs the `Damage` leaf needs (`DamageTable`, optional `CombatEventQueue`, optional `MatchStats`). Add `WithTarget(int id)` returning a copy. **Because the heavy state sits behind class references, copying the struct into a work-stack frame is safe and RNG draws still advance the one shared stream** (do NOT make the context copy SimRng by value — it is a class; never re-seed or clone it mid-run).
  - [ ] 3.2 `EffectExecutor` — sealed class. Pre-allocate **once in the constructor**: `Frame[] _stack = new Frame[EffectCaps.MaxEffectFrames]` and `int[] _hitRing = new int[EffectCaps.MaxEffectDepth * EffectCaps.MaxHitsPerSearch]`. `void Run(EffectNode root, in EffectContext ctx)`: explicit LIFO work-stack, **no recursion**; push children in reverse so they pop in authored order; depth tracked per frame.
  - [ ] 3.3 Leaf dispatch inside `Run` (all guard `IsAlive` + id-bounds at entry):
    - `DirectHpDelta` → `world.Health[t] = Fixed.Clamp(world.Health[t] + delta, Fixed.Zero, world.MaxHealth[t])` — direct, **no `DamageResolver`, no matrix**.
    - `Heal` → `world.Health[t] = Fixed.Min(world.Health[t] + amount, world.MaxHealth[t])`.
    - `Damage` → build `DamageContext` and call `DamageResolver.Apply(in ctx, amount, type)` (handles death/events).
  - [ ] 3.4 `SearchArea` execution: `spatialHash.QueryRadius(world, pos, radius, excludeId, hitBuffer)` → `Array.Sort(hitBuffer, 0, count)` (**ascending-id — QueryRadius returns unordered**) → clamp to `MaxSearchTargets` → push the child per target, in ascending order, using the **per-depth** hit slice (`_hitRing[depth * MaxHitsPerSearch ..]`) so nested searches never clobber a parent's buffer. SpatialHash must be `Rebuild()`-ed for the snapshot before querying (in the unit harness, build it from the test world).
  - [ ] 3.5 Bounds enforcement (AC2): a static **load-time** `EffectBounds.Validate(EffectNode root)` that walks the graph and rejects depth > `MaxEffectDepth` or any `Sequence.Children.Length > MaxSequenceChildren` (returns a located error: which node, which limit). PLUS a defensive **runtime** guard so the stack pointer can never exceed `MaxEffectFrames` (fail-closed: stop pushing / skip past the cap, never resize, never throw OOM). Pin the exact depth semantics by test (depth 8 runs; depth 9 rejected) — do not infer them from the constant.
  - [ ] 3.6 Zero-alloc `Run` (AC2): no `new`, no LINQ, no closures, no boxing inside `Run`; reuse `_stack`/`_hitRing`. (Verify with `GC.GetAllocatedBytesForCurrentThread()` delta == 0 across a warm run in Task 5.2.)
  - [ ] 3.7 `ApplyModifier` / `Persistent` execution is **deferred to 2.2b**: in 2.1 the executor recognizes the node types but must not mutate a (nonexistent) ModifierStore. Make this explicit and fail-closed-friendly — a clearly-commented guard (e.g. throw `NotSupportedException("ApplyModifier/Persistent execution lands in Story 2.2b")` if one reaches `Run`, OR a documented deterministic no-op). The 2.3 validator will keep these off the executor until 2.2b ships; pick the guard that the 2.2b dev will most cleanly replace (recommend the throwing guard so a premature wire-up is loud, not silent).

- [ ] **Task 4 — Equal-Exchange-shaped primitive + non-matrix proof (AC4)**
  - [ ] 4.1 Confirm `DirectHpDelta` is flat/armor-independent (Task 3.3) — explicitly bypasses `DamageResolver`/`DamageTable`.
  - [ ] 4.2 Test (Task 5.4): `Sequence{ DirectHpDelta(-10), Heal(+25) }` on one entity; assert the post-state against **independently-computed `Fixed.Raw`** values; assert ordering (delta then heal); assert armor-independence by running the same graph on a `Heavy`-armor entity and a `Unarmored` entity and observing the **identical** flat delta.

- [ ] **Task 5 — Tier-1 tests in a new `ProjectChimera.Sim.Tests/Effects/` folder (AC1, AC2, AC3, AC4)**
  - [ ] 5.1 `EffectVocabularyTests.cs` (AC1): reflection scan over the `ProjectChimera.Effects` assembly asserting — every concrete `EffectNode` subtype is `sealed`; exactly **three** composition node types; a first-class `Modifier` type exists; **no** node type exposes a `Delegate`/`Func`/`Action`/`dynamic`/`object` field or `using Godot`/`float`/`double` field. Teeth: this fails if anyone adds an open/virtual/scripted node.
  - [ ] 5.2 `EffectExecutorBoundsTests.cs` (AC2): depth-8 graph runs; depth-9 graph **rejected** by `EffectBounds.Validate` (located error); `Sequence` with 9 children rejected; a maximal valid graph (max depth × max fan-out) executes without exceeding `MaxEffectFrames`; **zero-alloc** assertion via `GC.GetAllocatedBytesForCurrentThread()`. Negative control: temporarily raising the cap lets the over-deep graph through (demonstrating the gate is what's stopping it) — document, don't commit.
  - [ ] 5.3 `EffectExecutorDeterminismTests.cs` (AC3): two fresh identical worlds + identical graph → `SimChecksum.Compute(...)` equal across runs (byte-identical); a `SearchArea` over ≥3 entities applies in **ascending-id order** (assert the exact target-id sequence and per-target deltas); if exercising a random selection, same seed → identical, **different seed → diverges** (negative control).
  - [ ] 5.4 `EffectExecutorEqualExchangeTests.cs` (AC4): the Task 4.2 sequence + armor-independence assertions.
  - [ ] All tests: build a bare `EntityWorld` (the `DamageResolverTests.cs` pattern — `w.Create(FixedVec3.Zero, faction, Fixed.FromInt(hp), Fixed.FromInt(speed))`), author state with `Fixed.FromInt` only (no `Fixed.FromFloat` in tests), assert against **independently-derived** raws. No Godot, no `SimulationHost`.

- [ ] **Task 6 — Verify, confirm no regression, document deferrals**
  - [ ] 6.1 `dotnet build godot/ProjectChimera.Sim.Tests -c Release` + `dotnet test` green; full Tier-1 suite still passes (baseline ~283 pass / 1 skip / 0 fail → +N new tests).
  - [ ] 6.2 Confirm **all 7 existing goldens byte-identical** (`git status --short -- '*.golden.txt'` clean) and `SimChecksum.AlgoVersion` stays **5** — 2.1 adds no hashed state, so **no fold, no bump, no re-record**. Confirm `SystemOrderTest` untouched (the executor is NOT a tick system — it is a helper invoked by graph data; precedent: `FormationPlanner`/`DelayMath`/`OrderApplier`).
  - [ ] 6.3 Append to `_bmad-output/implementation-artifacts/deferred-work.md`: `ApplyModifier`/`Persistent` execution → 2.2b; `SetVariable` → Epic 7 (DSL); `FireProjectile`/`SpawnUnit`/`Teleport`/`Victory`/presentation leaves → their owning stories; `Air`/`Ground`/`Structure` `TargetFilter` evaluation → 2.9a.

---

## Dev Notes

### Why this is the keystone — and exactly what it is

This is **AR-8**: the **one and only effect surface** in the entire engine. Abilities (Epic 2), the trigger DSL (Epic 7, a *superset* that embeds these same nodes), and AI-balance all compile down to this vocabulary and run through this one executor. There is **no second effect system, ever, and no scripting escape hatch** (no Lua/JASS/`RunScript`/`customParams`/delegate payloads). Get the closedness and determinism right here and every downstream story inherits them; get them wrong and they leak everywhere.

`src/Effects/` is **net-new** — confirmed absent in live source. Pure C#, `Fixed` 16.16 only, no Godot.

### Scope — what 2.1 BUILDS vs DEFINES vs DEFERS

The full D1 vocabulary is large, but most leaves resolve against systems that don't exist yet. 2.1 is deliberately scoped to the **executable core** while still declaring the **complete closed shape** AC1 demands:

| Node | 2.1 action | Why |
|---|---|---|
| `DirectHpDelta`, `Heal`, `Damage` | **Build + execute** | Mutate already-hashed `Health`; all backing systems exist (`DamageResolver`). |
| `SequenceEffect`, `SearchAreaEffect` | **Build + execute** | The composition + fan-out the executor must prove (AC2/AC3). |
| `PersistentEffect` | **Define type** (3rd composition node, AC1) | Periodic time-axis execution needs ModifierStore → **2.2b**. |
| `Modifier` descriptor + `ApplyModifierEffect` | **Define types** (first-class Modifier, AC1) | Store resolution → **2.2b** (epic note: "the ApplyModifier leaf from 2.1 now resolves against this store"). |
| `SetVariable`, `FireProjectile`, `SpawnUnit`, `Teleport`, `Victory`, presentation (`PlayVfx`/`PlaySound`/`ShakeScreen`) | **Not added** | Added later as new **sealed** types in their owning stories. The set stays *closed to creators* and *sealed in code* — adding a sealed leaf in a future story does not violate AC1's "no open extension point." |

**"Closed" means:** no virtual/open extension reachable by data or creators, and (in 2.3) no JSON `kind` outside the closed registry. It does **not** mean the engine dev can never add a sealed type in a later story.

### The work-stack executor (the AR-8 / Step-7-Pattern-N2 design)

Pre-allocate once; pop LIFO; push children **reversed** so they execute in authored order; explicit depth tracking; **no recursion**:

```csharp
public sealed class EffectExecutor
{
    private readonly Frame[] _stack = new Frame[EffectCaps.MaxEffectFrames];          // allocated ONCE
    private readonly int[]   _hitRing = new int[EffectCaps.MaxEffectDepth * EffectCaps.MaxHitsPerSearch];

    private readonly struct Frame
    {
        public readonly EffectNode Node;
        public readonly EffectContext Ctx;   // readonly struct holding World/Rng REFERENCES (cheap copy)
        public readonly int Depth;
        public Frame(EffectNode n, in EffectContext c, int d) { Node = n; Ctx = c; Depth = d; }
    }

    public void Run(EffectNode root, in EffectContext ctx)
    {
        _stack[0] = new Frame(root, ctx, 0);
        int sp = 1;
        while (sp > 0)
        {
            Frame f = _stack[--sp];
            if (f.Depth >= EffectCaps.MaxEffectDepth) continue;            // defensive runtime backstop
            switch (f.Node)
            {
                case SequenceEffect seq:
                    for (int k = seq.Children.Length - 1; k >= 0; k--)     // reverse-push ⇒ authored-order pop
                        _stack[sp++] = new Frame(seq.Children[k], f.Ctx, f.Depth + 1);
                    break;
                case SearchAreaEffect search:
                    int off = f.Depth * EffectCaps.MaxHitsPerSearch;       // per-depth buffer (no clobber)
                    int n = search.FindTargets(f.Ctx, _hitRing, off);     // QueryRadius + Array.Sort ascending-id
                    for (int i = 0; i < n; i++)
                        _stack[sp++] = new Frame(search.Child, f.Ctx.WithTarget(_hitRing[off + i]), f.Depth + 1);
                    break;
                // PersistentEffect: defined; periodic execution lands in 2.2b (guard here)
                default:
                    f.Node.Apply(in f.Ctx);                                // leaf mutation
                    break;
            }
        }
    }
}
```

**Work-stack sizing (AC2 "never grows beyond pre-allocated size"):** size `MaxEffectFrames` to the static worst case the other caps imply (a `SearchArea` can push up to `MaxSearchTargets` frames at one level; nesting multiplies by depth) **or** fail-closed when `sp` would exceed capacity. Either is acceptable — but it must be *proven by test* (Task 5.2), not assumed. Document the chosen derivation on `EffectCaps.MaxEffectFrames`.

### Live sim APIs you will call (exact signatures — do not re-derive)

**`Fixed` — `ProjectChimera.Core` · `src/Core/FixedPoint.cs`** (16.16; type is named `Fixed`, not `FixedPoint`):
`Fixed.FromInt(int)`, `Fixed.FromRaw(int)`, `.Raw`, `.ToInt()`; constants `Fixed.Zero/One/Half/NegOne/MaxValue/MinValue`; ops `+ - * / %`, all comparisons; helpers `Fixed.Abs/Min/Max/Clamp/Sqrt/Lerp`. **`Fixed.FromFloat` is load-time/authoring only — never in the executor or in tests** (CHM0005).

**`EntityWorld` — `ProjectChimera.Core` · `src/Core/EntityWorld.cs`** (`MAX_ENTITIES = 4096`):
- `Fixed[] Health`, `Fixed[] MaxHealth`, `FixedVec3[] Position`, `Faction[]`/faction accessor, `ArmorType[] ArmorTypeOf`, `DamageType[] DamageTypeOf`, `SimRng Rng { get; }`.
- `bool IsAlive(int id)` — guards `id` bounds + alive flag. **Call it before every read/write.**
- Ascending-id iteration: `for (int i = 0; i < world.HighWaterMark; i++) { if (!world.IsAlive(i)) continue; … }`.
- `int Create(FixedVec3 pos, Faction faction, Fixed health, Fixed speed)` — for test worlds.

**`DamageResolver` — `ProjectChimera.Combat` · `src/Combat/DamageResolver.cs`** (the *one* damage path, used by the `Damage` leaf):
`static bool Apply(in DamageContext ctx, Fixed amount, DamageType type)` → returns `true` if the target died (fires `UnitKilled` event + `RecordKill` + `Destroy`). `DamageContext(EntityWorld world, int targetId, ArmorType targetArmor, Faction killer, DamageTable table, CombatEventQueue? events = null, MatchStats? stats = null)`. **As-built formula is `final = amount × table.Get(type, armor)` with NO flat armor subtraction** — the GDD's `− armorValue` term is not in the live code; use `DamageResolver.Apply` as-is, do not re-derive. `DamageTable.Default` is the canonical fallback grid; `Get(DamageType, ArmorType)` returns the `Fixed` multiplier. `DamageType {Normal=0,Pierce=1,Siege=2,Magic=3,Hero=4}`, `ArmorType {Unarmored=0,Light,Medium,Heavy,Fortified,Hero}` — **stable integer keys, never reorder.**
**`DirectHpDelta` must NOT call this path** — it is flat and armor-independent by design (AC4).

**`SpatialHash` — `ProjectChimera.Navigation` · `src/Navigation/SpatialHash.cs`** (for `SearchArea`):
`int QueryRadius(EntityWorld world, FixedVec3 pos, Fixed radius, int excludeId, int[] resultBuffer)` → count; **results are UNORDERED — you MUST `Array.Sort(buffer, 0, count)` for ascending-id determinism** (AC3). Call `Rebuild(world)` to index the snapshot before querying.

**`SimRng` — `ProjectChimera.Core` · `src/Core/SimRng.cs`** (SplitMix64; the ONLY randomness in sim, AR-13):
`world.Rng.NextInt(int countExclusive)`, `world.Rng.NextFixed()`, `ulong State`. It is a **shared reference** folded into `SimChecksum` (v3) — never `new System.Random()`, never copy/clone it. Any random selection must **collect candidates in ascending-id order *before* drawing** (canonical draw order is part of the contract). 2.1 likely needs no random leaf; if one is added, route it here.

**`SimChecksum` — `ProjectChimera.Core` · `src/Core/SimChecksum.cs`** (`AlgoVersion = 5`):
`static uint Compute(EntityWorld world, BuildingStore buildings, ResourceStore resources, FactionRegistry factions)` — FNV-1a 32-bit over `.Raw` ints, ascending-id. **`Health` is already folded.** The executor mutates `Health` → the checksum already reflects it with **no AlgoVersion bump and no fold change**. AC3 = call `Compute` after two identical runs and assert equality (the "golden checksum" *function* — no persisted `.golden.txt` artifact is required for 2.1).

**`ISimSystem` — `src/Core/SimulationLoop.cs`:** `void Tick(EntityWorld world, Fixed dt)`. **The executor is NOT a system in 2.1** (it is invoked by graph data, not the tick loop) → `SystemOrderTest` stays untouched. `ModifierSystem` is reserved at **tick index 3, before `CombatSystem`** — that wiring is 2.2a's, not yours.

### Determinism rules — and the analyzer that enforces them

`src/Effects/` joins the sim layer, so the Story 1.10b determinism analyzer (`BannedSimApiAnalyzer` + `BannedApiAnalyzers`/RS0030 + `GodotFreeBoundaryTest`) gates it — **but only once you complete Task 1.2** (add it to `SimSources.props`). To stay clean:

- **No `float`/`double`** anywhere — `Fixed` only (CHM0001). `Fixed.FromFloat`/`ToFloat` is load-time only (CHM0005).
- **No `System.Random`/Godot RNG** — only `world.Rng` (RS0030 + rule).
- **No `Dictionary`/`HashSet` enumeration driving sim order** (CHM0002) — arrays/ordered structures; `HashSet` only for membership, never enumerated into hashed/sorted state.
- **No unstable sort** (CHM0003) — `Array.Sort` on `int` ids is a total order and fine; for strings/enums use `StringComparer.Ordinal`. Collect ascending-id.
- **Name every cap** in `EffectCaps` (CHM0004) — no bare numeric literals used as limits.
- **No `using Godot;`** (`GodotFreeBoundaryTest`).

Other determinism gotchas a brand-new executor author must respect:
- **Ascending-id iteration is the contract** — `SearchArea` fan-out, any multi-target effect, all in ascending entity-id.
- **`EffectContext` holds references, never copies sim state by value** — `EntityWorld` and `SimRng` are classes; a `readonly struct` context copies the *references* (cheap, correct). A struct that copied RNG *state* by value would lose draw-advances and desync silently (the 1.5 trap).
- **Guard `IsAlive` + id-bounds + faction/self at every apply entry.** Future callers (DoT/AoE/abilities — i.e. *your* nodes invoked from many sites) will hit dead/recycled targets; `DamageResolver.Apply` already guards `IsAlive` for exactly this reason. Do the same in `DirectHpDelta`/`Heal`.
- **`Fixed` arithmetic `>>` rounds toward −∞** (the classic lockstep desync source) — keep all magnitudes `Fixed`, quantized once at load.
- **Goldens are sacred** — if any `.golden.txt` moves, you leaked new state into the hashed tick; find and fix it, do **not** re-record (2.1 should move none).

### Testing discipline (the house rules the code-review will check)

- **No tautological asserts** (the single most durable Epic-1 review lesson): assert observable outcomes against **independently-derived** expected `Fixed.Raw` values — never by re-running the method and comparing to itself. Precedent: `DamageResolverTests.cs` pins `491_520` / `327_680` computed by hand. The Acceptance Auditor re-derives your pins, so they must be real external numbers.
- **Every gate ships with teeth (A3):** for each new bound/rejection, write a positive case AND a negative control that is demonstrably RED without the gate (depth-9 rejected; different seed diverges; an open node type fails the vocabulary scan). Record the inject-violation→observe-failure→revert evidence in the Dev Agent Record.
- **Located errors** for `EffectBounds.Validate` rejections — name the offending node + the limit, never a bare "invalid."
- **Cover boundaries explicitly** (the Edge Case Hunter will): depth 8 vs 9, `Sequence` 0/1/8/9 children, `SearchArea` with 0 hits / 1 hit / over-cap, dead/recycled target ids, clamp at `MaxHealth` and at `Zero`.
- Test home: new folder `godot/ProjectChimera.Sim.Tests/Effects/`. Mirror `Combat/DamageResolverTests.cs`. Files auto-compile (the folder is under the globbed test project — distinct from the **sources** glob you fixed in 1.2).

### Project Structure Notes

- New code: `godot/src/Effects/*.cs`, namespace `ProjectChimera.Effects`. New tests: `godot/ProjectChimera.Sim.Tests/Effects/*.cs`, namespace `ProjectChimera.Sim.Tests.Effects`.
- One edit to `godot/ProjectChimera.Sim.Tests/SimSources.props` (add `src/Effects` to the shared sources glob — feeds both the test project and `ProjectChimera.Sim.Analysis`).
- No `.csproj` edit needed (no new golden artifact; new test `.cs` files auto-compile under the globbed test folder).
- No NuGet additions (xUnit is test-only; the sim layer stays dependency-free and AOT-eligible — AR-2).
- **Out of scope (do not touch):** `SimChecksum.cs` (no fold/bump), any `.golden.txt`, `SystemOrderTest.cs`, `SimulationHost` registration, `ModifierSystem`/`ModifierStore` (2.2a/2.2b), the JSON `NodeBase` converter + `AbilityDefinition` loader/validator (AR-22 → **2.3**), `CommandCardSystem`/UI (2.4+).
- **Single-mapper SoA rule (retro action item A2):** does **NOT** land in 2.1 — 2.1 adds no persistent per-entity SoA array (its state is a transient work-stack over existing arrays). A2 lands with the first SoA-adding story, **2.2a** (Energy/Mana/Base*/Effective*). *If* your design ends up introducing any persistent per-entity effect/buff array, pull A2 forward and land the `ApplyUnitDefinition` mapper + `Create()` default + guard test with it.

### Project Context Rules

_Extracted from `_bmad-output/project-context.md` — these apply directly to this story:_
- **Sim/Presentation boundary is sacred.** `src/Effects` is simulation: no `using Godot;`, no Node types, no `Vector3`/`float` gameplay state. Data flows sim → presentation only.
- **Determinism (breaks MP silently if violated):** `Fixed` 16.16 for all gameplay math; process entities in ascending id; no wall-clock; `SimRng` is the only randomness; no `Dictionary`/`HashSet` iteration in sim order.
- **Composition over inheritance / data-driven:** the effect graph *is* the composition primitive — a "healer" is a unit + a `Heal`/`SearchArea` graph, not a subclass. Every mechanic must be expressible as data a creator edits (the JSON/editor surface arrives in 2.3/2.5; the vocabulary you build here must be expressible that way).
- **Conventions:** `PascalCase.cs` == class name; `ProjectChimera.Effects` namespace; PascalCase types/methods, camelCase locals, SCREAMING_CASE consts; `#nullable enable`; comment all public methods and non-obvious logic.
- **Brownfield style:** reuse `EntityWorld`/`DamageResolver`/`SpatialHash`/`SimRng` — do not build parallel systems. Small, shippable, always-green.

### References

- **Story + epic scope:** `_bmad-output/planning-artifacts/epics.md#Story-2.1` (lines 842–860) and Epic 2 sequencing note (line 840).
- **AR-8 (Effect-Graph), AR-9 (Modifier subsystem), AR-13 (SimRng), AR-22 (NodeBase converter → 2.3):** `_bmad-output/planning-artifacts/epics.md` (lines 187–194, 207); `_bmad-output/game-architecture.md` §D1 + `game-architecture.Step7-patterns-briefing.md` §N2 (work-stack executor) / §N3 (modifier relationship).
- **Requirements:** FR-12 (`epics.md:79`), NFR-4 (`epics.md:150`), NFR-6 (`epics.md:152`).
- **Live source:** `src/Core/FixedPoint.cs`, `src/Core/EntityWorld.cs`, `src/Core/SimChecksum.cs`, `src/Core/SimRng.cs`, `src/Combat/DamageResolver.cs`, `src/Combat/DamageTable.cs`, `src/Navigation/SpatialHash.cs`, `src/Core/SimulationLoop.cs`, `src/Core/Sim/SimulationHost.cs`.
- **Test patterns:** `ProjectChimera.Sim.Tests/Combat/DamageResolverTests.cs` (pure-sim unit test + independently-pinned raws), `ProjectChimera.Sim.Tests/Golden/GoldenChecksumReplay.cs` (checksum harness), `SimSources.props` (the source glob to extend).
- **Conventions / rules:** `_bmad-output/project-context.md`, `godot/CLAUDE.md` (L2 sub-router).
- **Prior-story lessons:** `epic-1-retro-2026-06-25.md` (A1 3-layer review, A2 single-mapper SoA rule, A3 prove-gates-have-teeth), Stories 1.5/1.6/1.7/1.13 (SimRng, DamageResolver, Validated<T>, formation SoA + analyzer/golden mechanics).

---

## Dev Agent Record

### Agent Model Used

_(populated by dev-story)_

### Debug Log References

### Completion Notes List

### File List
