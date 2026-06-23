---
baseline_commit: acd948f06680477c157c92449198317b3f2868fe
---

# Story 1.2: Golden-checksum replay harness pinning current sim behavior

Status: review

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As a solo developer protecting determinism during refactors,
I want a golden-checksum harness in the Tier-1 project (`ProjectChimera.Sim.Tests`) that runs a fixed scenario N ticks through `SimulationLoop` and records/asserts the `SimChecksum` sequence against a committed golden file,
so that any future change that alters sim behavior is caught immediately as a checksum diff.

> **This is migration Step 1 — the regression guard the entire strangler rides on.** Story 1.1 stood up the empty Godot-free test project; this story fills its `Golden/` folder with the harness that pins **today's** `SimChecksum` sequence before any sim code is touched. Every later determinism story (1.3b widen + re-baseline, 1.4 negative tests, 1.5 SimRng, 1.8a/b/c decomposition) and the CI wiring (1.10a) and cross-platform gate (1.10c) build on this exact harness. Get the scenario deterministic and the golden re-baseline-able, and the rest of Epic 1 is safe.

## Acceptance Criteria

1. **(In-process determinism + golden match)** **Given** a committed fixed scenario and a recorded golden checksum sequence **When** the harness runs the scenario for 300+ ticks **twice in one process** **Then** both runs produce byte-identical checksum sequences **and** both match the committed golden file.

2. **(No static/mutable-state leakage across processes)** **Given** the same scenario run in two separate process invocations **When** checksum sequences are compared **Then** they are byte-identical (no static/mutable-state leakage between runs).

3. **(Drift is detected and located)** **Given** a deliberate one-tick perturbation injected into a unit's `Fixed` health **When** the harness runs **Then** the test FAILS with a **located tick + expected-vs-actual checksum**, proving the guard detects drift.

_Covers: FR-44 (test infrastructure), FR-47 (record/reproduce), AR-35 (Tier-1 project foundation), AR-15 (golden-checksum replay regression harness). Depends on: 1.1 (the Godot-free `ProjectChimera.Sim.Tests` project — DONE)._

> Migration Step 1 — pins current behavior before any change. Establishes the replay regression harness that Story 1.10a later wires into CI and 1.10c runs cross-platform. **Uses today's `SimChecksum` as-is** (Ore[P1]/Ore[P2] only — a known-narrow hash); Story 1.3b then widens coverage and re-baselines the golden **once**. Make re-baselining a one-command operation.

---

## Developer Context

**You (the dev agent) have ONLY this file. Read this whole section before editing anything.** This story is **test-only**: it adds *zero* production sim code and changes *zero* sim behavior (the architecture's Step 0/1 rule — "no behavior change. Ship."). The single hardest part is not "compute a checksum" — it is **assembling and driving the simulation headlessly (Godot-free) when the only assembly code that exists today lives inside the Godot-coupled `MainScene.cs` (excluded from the test project)**. The pre-flight facts below give you the exact construction block to replicate and every API you need, already verified against the codebase. Follow them and the harness compiles and runs in seconds.

### The shape of the work (test-only; 1 csproj edit + ~4 new test files + 1 committed golden file)

1. Add `..\src\AI\**\*.cs` to the Tier-1 csproj compile glob (the live tick loop includes `AiOpponentSystem`, which lives in `src/AI/` — **not** currently globbed). Verified Godot-free.
2. Build a **deterministic in-code scenario** + headless harness that replicates `MainScene.cs:246–268` Godot-free (9-system loop, stores, `EnableChecksums`).
3. Record the per-tick `SimChecksum` sequence; implement a **first-divergence comparator** and golden-file IO.
4. Generate and commit the golden file (run the harness once in record mode).
5. Write the three AC tests (in-process×2 + golden; cross-process via golden; perturbation-located).
6. *(Inherited 1.1-review deferral)* Add `Fixed` boundary-value determinism tests to the existing `Determinism/` suite.
7. Verify: `dotnet test` green, `dotnet build godot/godot.csproj` green, AC3 fails-as-designed when perturbed.

### Pre-flight facts you MUST NOT re-derive (already verified against the codebase)

**THE construction to replicate — `MainScene.cs:246–268` (verbatim).** Your harness builds this exact 9-system loop, Godot-free. The architecture's migration Step 1 (Story 1.8a) will later move *this same block* verbatim into `SimulationHost` and require "Golden checksum must match" — so replicate it faithfully:

```csharp
// stores (order as in MainScene)
_world        = new EntityWorld();                 // src/Core/EntityWorld.cs
_nodes        = new ResourceNodeStore();           // src/Core/ResourceNodeStore.cs (parameterless)
_resources    = new ResourceStore(Fixed.Zero);     // src/Core/ResourceStore.cs — NO parameterless ctor; pass starting ore
_buildings    = new BuildingStore();               // src/Core/BuildingStore.cs (parameterless)
_projectiles  = new Combat.ProjectileStore();      // parameterless
_combatEvents = new Combat.CombatEventQueue();      // parameterless
var stats     = new MatchStats();                  // src/Core/MatchStats.cs — Godot-free, default ctor
var fog       = new FogOfWarSystem(Faction.Player1);
var p1Def     = new FactionDefinition();           // src/Core/Definitions/FactionDefinition.cs — default ctor (empty roster)
var p2Def     = new FactionDefinition();
var buildSys  = new BuildingSystem(_buildings, _resources, p1Def, p2Def, stats);
var director  = new ScenarioDirector(_buildings, _resources);

// THE 9-system tick order (params ISimSystem[] — executed sequentially each tick):
var loop = new SimulationLoop(_world,
    buildSys,                                                   // 1 BuildingSystem        (Economy)
    new GatheringSystem(_nodes, _resources, stats),             // 2 GatheringSystem        (Economy)
    new MovementSystem(),                                       // 3 MovementSystem         (Navigation)
    new CombatSystem(_projectiles, _combatEvents, stats),       // 4 CombatSystem           (Combat)
    new Combat.ProjectileSystem(_projectiles, _combatEvents, stats), // 5 ProjectileSystem  (Combat)
    new SupplySystem(_resources),                               // 6 SupplySystem           (Economy)
    fog,                                                        // 7 FogOfWarSystem         (Core)
    new AiOpponentSystem(_buildings, _resources, buildSys, AiDifficulty.Normal), // 8 (AI — plays Player2)
    director);                                                  // 9 ScenarioDirector — runs last (Core)

loop.EnableChecksums(_buildings, _resources);                  // REQUIRED before stepping, or no checksum fires
loop.ChecksumInterval = 1;                                     // harness choice: checksum EVERY tick (default is 60)
```

- **All 9 systems and every store are 100% Godot-free** (verified: zero `using Godot`/`Godot.`/`[Export]`/`GD.`/`Mathf` in `Core`/`Combat`/`Economy`/`Navigation`/`AI`, except the `#if GODOT`-guarded bridge in `FixedPoint.cs` which compiles out here). There are **no blockers** to constructing the full loop headlessly.
- **`AiOpponentSystem` is in `src/AI/`, which the Story-1.1 csproj does NOT glob** (it globs only `Core/Combat/Economy/Navigation`). Task 1 adds the `src/AI/**` include. `src/AI/` contains exactly two files — `AiOpponentSystem.cs` and `LLMService.cs` — **both Godot-free** (`LLMService` uses `System.Net.Http.HttpClient`, a BCL type, not Godot). The glob is safe.
- **`AiOpponentSystem` plays `Faction.Player2`** (`AI_FACTION` const, `AiOpponentSystem.cs:31`). Each tick it prunes dead buildings, **trains from every idle *production* building** (Barracks/ArcheryRange/SiegeWorkshop — **not** CommandCenter), scores strategic actions, and executes the highest scorer using **hardcoded `Fixed` costs** (`COST_BARRACKS=100`, etc.) against `_resources.Ore[Player2]`. **Keep it deterministic-and-quiet for a stable golden:** give Player2 **no production buildings and zero/low ore** → it can't afford to build and has nothing to train, so it runs (and is pinned) but mostly no-ops. It *may* command Player2's existing units (move/attack) — that is deterministic and fine, and it does **not** consult `FactionDefinition` (only *training new* units does). This is why the empty `new FactionDefinition()` is safe.
- **`SimChecksum.Compute(world, buildings, resources)` is today's hash, used AS-IS** (`SimChecksum.cs:26`, Godot-free, FNV-1a 32-bit). It hashes: every alive entity's `Position.{X,Y,Z}.Raw` + `Health.Raw` (ascending id); every building slot's `Alive` + `Health.Raw` + `ConstructionTimer.Raw`; and **only** `Ore[Player1].Raw` + `Ore[Player2].Raw`. Crystal/Supply and factions 3–8 are **deliberately not hashed yet** — that is Story 1.3b's widening + one-time re-baseline. **Do not touch `SimChecksum.cs`.**
- **Drive the sim with `SimulationLoop.StepOnce()`, never `Update(float)`.** `StepOnce()` (`SimulationLoop.cs:85`) advances exactly one deterministic tick (snapshots positions → ticks all systems in order → increments `CurrentTick` → fires the checksum on interval). `Update(realDelta)` uses a **`float` wall-clock accumulator** and steps a variable number of ticks — non-deterministic step count, wrong for a golden. `StepOnce` is exactly what `LockstepManager` uses for online determinism.
- **The scenario must be authored IN CODE, not loaded from `alpha_map_01.json`.** The committed scenario JSON exists and the loader (`ScenarioSerializer.LoadFromFile`) is Godot-free, **but the *apply* path that turns `ScenarioData` into spawned entities is `private MainScene.ApplyScenario` — Godot-coupled (`ProjectSettings.GlobalizePath`, `GD.PrintErr`) and excluded from the test project.** Extracting a Godot-free `ScenarioApplier` is explicitly **Story 1.8b (migration Step 3)** — doing it here violates this story's "no behavior change / scaffold-only" scope. So: build a small fixed scenario by calling the Godot-free store APIs directly (recipe in Dev Notes). See "Key design decision" below.
- **The golden file is a committed text file**, not a `.chmr` replay. Despite the story title ("replay harness"), the `.chmr` `ReplayRecorder`/`ReplayPlayer` (`src/Multiplayer/`) record only the **command stream**, not checksums, and live in a Godot-coupled folder excluded from the test project. The architecture's "reusing the `.chmr` path" is aspirational for later (SimRng folds into Replay in 1.5); **for 1.2, "replay" means deterministic re-run + golden-file compare** (classic golden-master testing). Do not pull in `Multiplayer/`.

### THE key design decision: in-code synthetic scenario (not `alpha_map_01.json`)

**Decision:** the harness builds a **fixed, deterministic scenario in code** via the Godot-free store APIs, and pins *that*. It does **not** load `alpha_map_01.json`.

- **Why:** the AC requires "a committed fixed scenario" — not specifically `alpha_map_01`. The scenario-*apply* path is Godot-coupled in `MainScene` and a Godot-free `ScenarioApplier` does not exist until Story 1.8b. Loading the JSON here would force you to **duplicate ~60 lines of `MainScene.ApplyScenario`** into the test (faction-JSON loading, `res://` path stripping, unit-def lookup, SoA field mapping) — creating a drift risk (the test copy silently diverging from `MainScene`) and prematurely doing 1.8b's work. An in-code scenario has **zero production dependencies, zero duplication, and zero drift risk**, and pins exactly what matters: *the deterministic evolution of a fixed world state through the real 9 systems.*
- **What the golden guards:** the strangler migration (Steps 1–14 / Stories 1.8a–c) **moves** sim code without changing behavior. A golden over a fixed in-code state + the real systems is the perfect guard for "did the relocation change any system's output?" — which is this harness's primary mission.
- **Forward note (not your job now):** when Story 1.8b lands the Godot-free `ScenarioApplier`, its `Builder/ScenarioApplierTests.cs` adds the real-scenario (`alpha_map_01`) golden + start-state hash. Leave a one-line `// TODO(1.8b): add alpha_map_01-loaded golden once ScenarioApplier is Godot-free` where the scenario is built.

---

## Tasks / Subtasks

- [x] **Task 1 — Compile `AiOpponentSystem` into the Tier-1 project (enables the full 9-system loop)**
  - [x] In `godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj`, add to the shared-source `<ItemGroup>` (after the `Navigation` include, line ~17):
    `<Compile Include="..\src\AI\**\*.cs" LinkBase="Sim\AI" />`
  - [x] Rationale: the live tick loop's system #8 is `AiOpponentSystem` (`src/AI/AiOpponentSystem.cs`, namespace `ProjectChimera.AI`). `src/AI/` is verified Godot-free (only `AiOpponentSystem.cs` + `LLMService.cs`; `LLMService` uses `System.Net.Http`, not Godot). Glob, don't hand-list (matches the project's glob-preference; the CI folder-set guard lands in 1.10).
  - [x] `dotnet build godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` → still green and still Godot-free (the `GodotFreeBoundaryTest` from 1.1 must still pass).

- [x] **Task 2 — Build the deterministic in-code scenario + headless harness (AC: 1, 2, 3)**
  - [x] Create `godot/ProjectChimera.Sim.Tests/Golden/GoldenScenario.cs`. Expose a method that constructs a **fresh** loop + stores every call (no statics) and returns enough handles to step it and read/perturb state, e.g.:
    `public static GoldenHarness Build()` returning a small record/class holding `SimulationLoop Loop`, `EntityWorld World`, `BuildingStore Buildings`, `ResourceStore Resources`, and the id of a designated "perturbation target" unit.
  - [x] Inside `Build()`, replicate `MainScene.cs:246–268` exactly (the block in Developer Context). Use `AiDifficulty.Normal` for the AI level and `new MatchStats()` / `new FactionDefinition()` for the deps.
  - [x] Populate a fixed, deterministic scenario that makes the checksum **evolve** over 300 ticks (so AC3 has signal). Author all values with `Fixed.FromInt`/`Fixed.FromRaw` — **no `Fixed.FromFloat`** (keep the harness obviously float-free; see Dev Notes recipe). Minimum to exercise every system:
    - **Player1:** 1 melee unit + 1 ranged unit positioned to close on and fight Player2 units (CombatSystem + ProjectileSystem + MovementSystem); 1 worker set to gather (GatheringSystem changes `Ore[P1]` → checksum moves); a pre-built CommandCenter and a building left **under construction** (its `ConstructionTimer` ticks down → BuildingSystem signal); starting ore.
    - **Player2:** 2–3 units as combat fodder, **no production building, zero ore** (keeps `AiOpponentSystem` quiet/deterministic — it runs but can't build/train).
  - [x] Construction-complete a building by setting `Buildings.ConstructionTimer[id] = Fixed.Zero` after `Create` (a fresh `Create` starts under construction). Leave one building's timer > 0 so BuildingSystem has work.
  - [x] Mirror MainScene's director lifecycle: `director.LoadScenario(new ScenarioData())` (Godot-free POCO) to initialize empty trigger state. If `ScenarioDirector.Tick` is safe without it, you may skip — **verify by running**, don't assume.
  - [x] Do **not** call `Fixed.FromFloat`, `System.Random`, `DateTime`, `GD.*`, or enumerate any `Dictionary`/`HashSet` in the harness. Process is deterministic by construction.

- [x] **Task 3 — Checksum-sequence recorder + first-divergence comparator + golden IO (AC: 1, 2, 3)**
  - [x] In `GoldenChecksumReplayTests.cs` (or a small `GoldenHarness` helper), add `RunAndRecord(int ticks, Action<int, EntityWorld>? perturb = null)` that:
    1. `Build()`s a fresh harness, subscribes `Loop.OnChecksum = (tick, hash) => seq.Add((tick, hash));`
    2. loops `for (int i = 0; i < ticks; i++) { perturb?.Invoke(i, World); Loop.StepOnce(); }` — **hook runs BEFORE `StepOnce`** so a perturbation at loop index `K` is reflected in that iteration's checksum (tick `K+1`), giving a clean, off-by-one-free located tick.
    3. returns the recorded `IReadOnlyList<(uint tick, uint hash)>`.
    (With `ChecksumInterval = 1`, `OnChecksum` fires every tick → ticks 1..N captured.)
  - [x] Implement `CompareSequences(expected, actual)` returning the **first** differing entry as `(uint tick, uint expected, uint actual)?` (null = identical). Handle length mismatch explicitly (report it as a divergence at the first missing/extra tick). The failure message MUST name the tick and both hashes, e.g. `$"Checksum drift at tick {t}: expected 0x{exp:X8}, actual 0x{act:X8}"`.
  - [x] Golden file: commit `godot/ProjectChimera.Sim.Tests/Golden/golden-scenario.golden.txt`, one line per sample `tick hashHex` (or `tick,hashHex`), with a header-comment line documenting the format. Add it to the csproj as `<EmbeddedResource Include="Golden\golden-scenario.golden.txt" />`. **MSBuild gotcha:** `Microsoft.NET.Sdk` auto-globs non-code files as `<None>`, so explicitly adding the same file as `<EmbeddedResource>` can trigger a duplicate-item error (NETSDK1022). If you hit it, add `<None Remove="Golden\golden-scenario.golden.txt" />` in the same `ItemGroup` (or it builds clean on your SDK — verify). Read it back via `Assembly.GetExecutingAssembly().GetManifestResourceStream(asm.GetManifestResourceNames().Single(n => n.EndsWith("golden-scenario.golden.txt")))` — robust to namespace and **portable across Windows/Linux** (no file paths; required for the 1.10c cross-platform gate). Read as bytes, split on `\n` trimming `\r`, skip the header/blank lines, parse to `(uint, uint)[]`.
  - [x] Provide a **re-baseline path** (Stories 1.3b/1.4/1.5 will use it): a record mode gated by env var (`CHIMERA_GOLDEN_RECORD=1`) that writes the freshly recorded sequence to the **source** file via `[CallerFilePath]` (locates `Golden/` on the build machine), then the dev rebuilds + commits. Document the exact command in Dev Notes. Do not auto-overwrite the golden in normal runs.

- [x] **Task 4 — Generate and commit the golden (AC: 1)**
  - [x] Run the harness once in record mode (`CHIMERA_GOLDEN_RECORD=1 dotnet test --filter <recorder>`); confirm it emits ≥300 samples.
  - [x] Inspect the sequence: it must **change over time** (early vs late hashes differ — proves the scenario is dynamic, not a static constant). If it's constant, the scenario isn't exercising the systems — fix the scenario (units must actually move/fight/gather), not the golden.
  - [x] Rebuild (so the `<EmbeddedResource>` picks up the new file) and commit `golden-scenario.golden.txt`.

- [x] **Task 5 — The three AC tests (AC: 1, 2, 3)**
  - [x] **AC1** `RunsTwiceInProcess_BothMatchGoldenAndEachOther`: run the scenario twice via `RunAndRecord(300)` (fresh `Build()` each time), assert `seq1.SequenceEqual(seq2)` AND `CompareSequences(golden, seq1) == null`. (Two fresh assemblies + identical results also proves no static mutation between runs.)
  - [x] **AC2** `MatchesGolden_RecordedInSeparateProcess`: a `[Fact]` that runs the scenario in this (fresh) process and asserts it equals the committed golden. **The golden was produced by a *prior* process invocation (Task 4), so asserting against it in a fresh `dotnet test` run IS the two-process comparison.** Add an XML-doc comment stating this explicitly. **Do NOT build a subprocess spawner** — the golden-from-another-process + fresh-process-assert (plus AC1's twice-in-one-process equality) fully satisfies "no static/mutable-state leakage."
  - [x] **AC3** `OneTickPerturbation_IsDetectedAndLocated`: `RunAndRecord(300, perturb: (i, w) => { if (i == K) w.Health[targetId] = Fixed.FromRaw(w.Health[targetId].Raw + 1); })` for a fixed `K` (e.g. 100) and a **non-combat** target (the worker — so the +1 raw persists and isn't overwritten by combat/death). Assert `CompareSequences(golden, perturbedSeq)` is **non-null**, that the located tick is exactly **`K+1`** (the hook runs before `StepOnce`, so index `K`'s perturbation is captured in the tick-`K+1` checksum), and that expected≠actual. This proves the guard FAILS loudly and points at the exact tick.

- [x] **Task 6 — (Inherited from the 1.1 code review) `Fixed` boundary-value determinism tests** *(secondary to the 3 ACs above)*
  - [x] Extend `godot/ProjectChimera.Sim.Tests/Determinism/FixedSmokeTests.cs` (or add `FixedBoundaryTests.cs`) with the edge cases the 1.1 review explicitly deferred here — the classic lockstep desync sources:
    - **Negative-multiply rounding direction** (the #1 cross-machine desync source): assert `Fixed.FromInt(-3) * Fixed.Half` rounds identically and as expected.
    - **Division truncation direction** for negatives.
    - **Overflow behavior at the 16.16 limits** (`Fixed.MaxValue`/`MinValue` raw edges) is well-defined (document actual behavior; pin it).
    - **`Sqrt` of `0`, a negative, and a non-perfect-square** (e.g. `Sqrt(2)`) — pin the deterministic result.
  - [x] These are pure-`Fixed` unit tests (no scenario). Use `Fixed`/raw asserts only — never `float`/`double` asserts (the 1.1 review's `RawRoundTrip` lesson: a tautological assert proves nothing; assert against an independently-derived expected raw value).

- [x] **Task 7 — Verify end-to-end**
  - [x] `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` → all green (the 6 existing 1.1 tests + the new golden + boundary tests), runs headless in seconds, no engine boot.
  - [x] Temporarily corrupt one line of the golden file (or flip the AC3 perturbation on for AC1) and confirm the suite **goes red with a located-tick message** — then revert. (Proves the guard actually guards.)
  - [x] `dotnet build godot/godot.csproj` → green (you added no production code; the game build is unaffected).

---

## Dev Notes

### Reference artifacts (copy/adapt — verified against the current code)

**Scenario authoring recipe (Task 2)** — populate the fresh world via the Godot-free SoA APIs. `EntityWorld.Create(FixedVec3 position, Faction faction, Fixed health, Fixed speed)` returns the id and sets `Alive`; everything else defaults to zero and you set it directly (arrays are `public readonly` references — contents mutable):

```csharp
// --- Player1: a melee attacker that closes on P2 and fights ---
int p1Melee = world.Create(new FixedVec3(Fixed.FromInt(-10), Fixed.Zero, Fixed.Zero),
                           Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
world.AttackDamage[p1Melee] = Fixed.FromInt(10);
world.AttackRange[p1Melee]  = Fixed.FromInt(2);              // <= 2.5 => melee
world.AttackSpeed[p1Melee]  = Fixed.FromInt(1);
world.DamageTypeOf[p1Melee] = DamageType.Normal;
world.ArmorTypeOf[p1Melee]  = ArmorType.Light;
world.MoveTarget[p1Melee]   = new FixedVec3(Fixed.FromInt(10), Fixed.Zero, Fixed.Zero);
world.Flags[p1Melee]       |= EntityFlags.Moving;            // REQUIRED for MovementSystem to move it

// --- Player1: a worker that gathers (drives Ore[P1] => checksum evolves) ---
int worker = world.Create(new FixedVec3(Fixed.FromInt(-12), Fixed.Zero, Fixed.FromInt(4)),
                         Faction.Player1, Fixed.FromInt(40), Fixed.FromInt(3));
world.GatherState[worker]   = GatherState.Idle;             // GatheringSystem will pick up the node
world.CarryCapacity[worker] = Fixed.FromInt(20);
// (the worker is the designated AC3 perturbation target — it never takes combat damage)

// --- Player2: combat fodder, NO production building, ZERO ore (keeps the AI quiet) ---
int p2Unit = world.Create(new FixedVec3(Fixed.FromInt(10), Fixed.Zero, Fixed.Zero),
                         Faction.Player2, Fixed.FromInt(80), Fixed.FromInt(3));
world.ArmorTypeOf[p2Unit] = ArmorType.Medium;

// --- resource node for the worker ---
nodes.Create(new FixedVec3(Fixed.FromInt(-12), Fixed.Zero, Fixed.FromInt(8)),
             Fixed.FromInt(500), Fixed.FromInt(7), 3);       // supply, gatherRate, maxGatherers

// --- buildings: one finished CommandCenter + one left under construction ---
int cc = buildings.Create(new FixedVec3(Fixed.FromInt(-14), Fixed.Zero, Fixed.Zero),
                        Faction.Player1, BuildingType.CommandCenter);
buildings.ConstructionTimer[cc] = Fixed.Zero;                // mark complete
int barracks = buildings.Create(new FixedVec3(Fixed.FromInt(-14), Fixed.Zero, Fixed.FromInt(-6)),
                              Faction.Player1, BuildingType.Barracks);  // stays under construction (timer ticks down)

// --- starting ore (P1 only; P2 stays at 0 to keep the AI quiet) ---
resources.AddOre(Faction.Player1, Fixed.FromInt(200));
```

> Exact positions/stats are yours to finalize — the only hard requirements are: **deterministic (all `Fixed`, no `FromFloat`), and the recorded sequence must change over time** (Task 4 check). Tune until early and late checksums differ.

**Enums you'll reference** (all verified):
- `Faction : byte { Neutral=0, Player1=1, Player2=2, Player3=3, Player4=4 }` — declared in `EntityWorld.cs:47`. `ResourceStore.Ore`/`Crystal` are `Fixed[5]` indexed by `(int)Faction`.
- `BuildingType : byte { CommandCenter=0, Barracks=1, ArcheryRange=2, SiegeWorkshop=3 }` — `BuildingStore.cs:6`.
- `DamageType : byte { Normal=0, Pierce=1, Siege=2, Magic=3, COUNT=4 }`; `ArmorType : byte { Unarmored=0, Light=1, Medium=2, Heavy=3, Fortified=4, COUNT=5 }` — `Combat/DamageMatrix.cs`. (`COUNT` is a sentinel — never assign it to a unit.)
- `EntityFlags : byte [Flags] { None=0, Alive=1, Moving=2, Attacking=4 }`; `GatherState : byte { Inactive=0, Idle=1, ... }`; `UnitCommand : byte { Idle=0, Move=1, AttackMove=2, Stop=3, HoldPosition=4, Build=5 }` — `EntityWorld.cs:10–47`.

**`Fixed` authoring surface** (`Core/FixedPoint.cs`): `Fixed.FromInt(int)` (value), `Fixed.FromRaw(int)` (raw 16.16), constants `Fixed.Zero/One/Half/NegOne`. **Gotcha:** `new Fixed(x)` is the *raw* ctor — `new Fixed(5)` = 5/65536 ≈ 0.0000763, NOT 5.0. Always use `Fixed.FromInt(5)` for the value 5. `FixedVec3(Fixed x, Fixed y, Fixed z)`; components are readable fields (`v.X.Raw`).

**Driver + recorder (Task 3 core):**
```csharp
loop.EnableChecksums(buildings, resources);   // wires the two stores into SimChecksum.Compute
loop.ChecksumInterval = 1;                     // checksum every tick
var seq = new List<(uint tick, uint hash)>();
loop.OnChecksum = (tick, hash) => seq.Add((tick, hash));
for (int i = 0; i < 300; i++) { perturb?.Invoke(i, world); loop.StepOnce(); }  // perturb BEFORE step => located tick = K+1
```

**Re-baseline command (document this for 1.3b/1.4/1.5):**
```
CHIMERA_GOLDEN_RECORD=1 dotnet test godot/ProjectChimera.Sim.Tests --filter FullyQualifiedName~Golden
# (Windows PowerShell: $env:CHIMERA_GOLDEN_RECORD=1; dotnet test ... ; Remove-Item Env:\CHIMERA_GOLDEN_RECORD)
# then: dotnet build (so the EmbeddedResource refreshes) ; git add the golden ; commit "re-baseline golden (intentional: <reason>)"
```

### Constraints & gotchas

- **`dotnet build`/`dotnet test` are authoritative** for C# correctness — the editor's MCP `run` does not rebuild the test assembly. Build/test before declaring done. [Source: LEARNINGS.md:122; 1.1 Dev Notes]
- **This story adds NO production code and changes NO sim behavior.** If you feel the urge to extract `ScenarioApplier`, generalize `SimChecksum`, add `SimRng`, wire CI, or touch `MainScene.cs` — STOP, that is 1.8b / 1.3b / 1.5 / 1.10a respectively. Scope is: csproj glob + test files + golden file. [Source: game-architecture.md Step 0–1, lines 1690–1696]
- **Determinism rules still bind your test setup:** author the scenario with `Fixed` (use `FromInt`/`FromRaw`, never `FromFloat`), no `System.Random`, no `DateTime`, no `Dictionary`/`HashSet` enumeration. The harness is the thing that *proves* determinism — it must not itself introduce a float or unordered source. [Source: project-context.md "Determinism"]
- **Do not add the test project to `godot.sln`** and do not add any NuGet beyond the 1.1 xUnit stack (no Nakama, no `Multiplayer/`). Run via `dotnet test <csproj>`. [Source: 1.1 Dev Notes]
- **Keep nullable at the project default** (1.1 set `ImplicitUsings disable`, no project-wide `<Nullable>`); add `#nullable enable` per test file if you want it locally — matches the shared sim source's per-file opt-in.
- **AC2 has no subprocess machinery.** If you find yourself spawning a process, you've over-built it — re-read Task 5/AC2. The committed golden *is* the other process's output.
- **`SupplySystem`/`FogOfWarSystem` don't change the hash directly** (supply isn't hashed yet; fog is vision state, not hashed) — that's expected. They're in the loop for fidelity to MainScene; the checksum evolution comes from positions/health/buildings/ore.

### Project Structure Notes

- Canonical target path is **`godot/ProjectChimera.Sim.Tests/Golden/GoldenChecksumReplayTests.cs`** — the architecture's target tree names this file explicitly. Put the scenario builder and golden file alongside it under `Golden/`. [Source: game-architecture.md line 1582]
- The 1.1 story pre-declared the per-story folder layout: `Golden/` is **yours** (1.2). `Determinism/` already exists (1.1) — Task 6 extends it. Don't create `Validation/`, `Builder/`, `Checksum/`, `Bootstrap/` (those are 1.7/1.8b/1.3b/1.8c). [Source: 1.1 Project Structure Notes]
- Tier-2 GdUnit4 (`godot/tests/`) is **out of scope** — this is a Tier-1 (Godot-free xUnit) story only. [Source: game-architecture.md §1 two-tier]

### Project Context Rules

_Extracted from `_bmad-output/project-context.md` — these govern every edit in this story:_

- **Simulation/Presentation boundary is sacred.** The whole harness lives on the sim side of the seam (`src/Core, Combat, Economy, Navigation, AI` compiled Godot-free). No `using Godot;`, no Node, no `float` for gameplay/scenario state. This story *proves* the boundary holds end-to-end under a running sim.
- **Determinism is the rule that breaks MP silently if violated:** `Fixed` 16.16 only; ascending-id iteration (the systems already do this — don't reorder); no wall-clock, no unseeded RNG, no unordered enumeration. `Fixed.FromFloat` is load-time-only and you don't even need it here (author with `FromInt`/`FromRaw`).
- **SoA, reuse existing systems.** You construct the existing `EntityWorld`/`BuildingStore`/`ResourceStore`/9 systems — do not introduce parallel types or per-entity objects.
- **Engine/runtime:** Godot 4.6.3, .NET 8 (`net8.0`). Test-only deps (xUnit) stay only in `ProjectChimera.Sim.Tests.csproj`, never `godot.csproj`. Assembly/namespace `ProjectChimera.*`; project files are `godot.csproj`/`godot.sln`.
- **Conventions:** `PascalCase.cs` filename = class name; comment public methods and non-obvious logic; the golden file and its format are part of the contract — document the line format in a header comment.

### References

- [Source: epics.md#Story-1.2 (lines 512–528)] — story statement, the 3 ACs (verbatim), FR/AR coverage, "uses today's SimChecksum as-is; 1.3 re-baselines once" note.
- [Source: epics.md#Epic-1 (lines 486–492) & #Story-1.3b (544–560)] — golden-checksum-gated strangler framing; the SimChecksum widening + one-time re-baseline this golden must survive.
- [Source: game-architecture.md §1 Testing (lines 1284–1300)] — Tier-1 Godot-free golden-checksum replay harness; "run a fixed scenario N ticks through SimulationLoop, record the SimChecksum sequence, assert byte-identical on replay."
- [Source: game-architecture.md migration Step 0 & 1 (lines 1690–1696)] — Step 0 "GoldenChecksumReplayTests pinning today's SimChecksum sequence … no behavior change. Ship."; Step 1 moves `MainScene.cs:246–268` verbatim into `SimulationHost`, "System order byte-identical. Golden checksum must match."
- [Source: game-architecture.md target tree (lines 1580–1590)] — `Golden/GoldenChecksumReplayTests.cs`; per-folder ownership (Validation/Builder/Checksum/Bootstrap are later stories).
- [Source: godot/src/Core/MainScene.cs:246–270] — THE construction block to replicate (stores + 9-system `SimulationLoop` + `EnableChecksums` + `OnChecksum`). Lines 500–560 = `ApplyScenario` (Godot-coupled, the apply path we intentionally do NOT reuse — 1.8b's job).
- [Source: godot/src/Core/SimulationLoop.cs:33,42,62,74,85–101] — `ChecksumInterval` (default 60), `OnChecksum` `Action<uint,uint>`, `params ISimSystem[]` ctor, `EnableChecksums`, `StepOnce()` (the deterministic driver). Avoid `Update(float)` (:108, accumulator).
- [Source: godot/src/Core/SimChecksum.cs:26–57] — today's hash, used as-is: alive entity Position+Health, building Alive+Health+ConstructionTimer, Ore[P1]+Ore[P2] only.
- [Source: godot/src/Core/EntityWorld.cs:47,185] — `Faction` enum; `Create(FixedVec3, Faction, Fixed health, Fixed speed)`; public SoA arrays (Position/Health/AttackDamage/MoveTarget/Flags/GatherState/…); `IsAlive`, `HighWaterMark`, `SnapshotPositions`.
- [Source: godot/src/Core/ResourceStore.cs:29 / BuildingStore.cs:6,87 / ResourceNodeStore.cs:39 / Combat/DamageMatrix.cs:6] — `ResourceStore(Fixed startingOre)` (no parameterless ctor), `AddOre`; `BuildingType` enum + `BuildingStore.Create`; `ResourceNodeStore.Create(pos, supply, rate, maxGatherers)`; Damage/Armor enums.
- [Source: godot/src/AI/AiOpponentSystem.cs:9,27,31] — `AiDifficulty` enum; system #8; `AI_FACTION = Player2`; trains from production buildings only, builds against hardcoded `Fixed` costs vs `Ore[P2]` (hence "no production building + zero ore = quiet").
- [Source: godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj:13–23] — current Godot-free globs (Core/Combat/Economy/Navigation; MainScene+StressTest removed; AI **not yet** included — Task 1 adds it).
- [Source: 1-1-…-scaffold.md (Review Findings → Defer)] — the `Fixed` boundary-value determinism tests (negative-multiply rounding, division truncation, overflow, Sqrt of 0/neg/non-square) were deferred *to this story* → Task 6. Also the `RawRoundTrip` "don't write a tautological assert" lesson.
- [Source: project-context.md] — determinism + sim/presentation rules, engine/runtime targets, NuGet discipline, SoA reuse.
- [Source: LEARNINGS.md:122] — `dotnet build`/`test` authoritative over the editor's cached DLL.

### Latest tech information

- **No new dependencies.** Reuse the 1.1 xUnit stack already pinned in the csproj (`xunit` 2.9.2, `Microsoft.NET.Test.Sdk` 17.11.1, `xunit.runner.visualstudio` 2.8.2). The golden harness needs only `System.*` + the sim source.
- **Golden-file portability for 1.10c:** prefer `<EmbeddedResource>` + manifest-stream read over file-path reads — it survives the Windows↔Linux (WSL) cross-platform gate with no path/line-ending fragility. Read the resource as bytes and split on `\n` with `\r` trimmed so a CRLF/LF checkout difference can't change parsing. The **checksum values themselves must be byte-identical across platforms** (that's the determinism proof) — only the *file transport* needs to be newline-robust.
- **`[CallerFilePath]`** is the clean, cross-platform way to locate the golden in the source tree for the record/re-baseline path (the path is baked at compile time on the build machine, which is where you regenerate).

### Previous Story Intelligence

From **Story 1.1** (done, code-review ACCEPTED 2026-06-22):

- The Tier-1 project `godot/ProjectChimera.Sim.Tests/` exists: `Microsoft.NET.Sdk`, `net8.0`, `ImplicitUsings disable`, shared-source globs of Core/Combat/Economy/Navigation, `<Compile Remove>` for `MainScene.cs` + `StressTest.cs`, **no** `<ProjectReference>` to `godot.csproj`, **not** in `godot.sln`. `FixedPoint.cs`'s Godot bridge is `#if GODOT`-guarded (compiles out here). Build it on, not against.
- `Determinism/FixedSmokeTests.cs` and `GodotFreeBoundaryTest.cs` already exist and pass (6 tests). Your new tests sit beside them; the boundary test must keep passing after Task 1's `src/AI` glob (i.e., `src/AI` must stay Godot-free — it is).
- The 1.1 review **deferred the `Fixed` boundary-value tests to this story** (Task 6) and flagged that **tautological asserts prove nothing** — assert against independently-derived expected raw values, not against an expression that reduces to the same thing on both sides.
- Pre-existing **CS8632** nullable warnings exist in `GatheringSystem.cs`, `SimulationLoop.cs`, `FlowFieldSystem.cs` (identical in game + test builds). Not your bug; don't be alarmed when the build shows them. Don't "fix" them here (deferred cleanup).
- Git history is `[AutoSave]`-only (hourly autocommit to `master`); working tree was clean. No bespoke test pattern to inherit beyond 1.1's — you are establishing the golden-harness pattern that 1.3b/1.4/1.5/1.8/1.10 will extend, so keep the scenario builder, recorder, comparator, and re-baseline path clean and reusable.

---

## Dev Agent Record

### Agent Model Used

claude-opus-4-8 (Claude Opus 4.8)

### Debug Log References

- `dotnet build ProjectChimera.Sim.Tests.csproj` after Task 1 (AI glob) → succeeded; existing 6 tests still green (GodotFreeBoundaryTest confirms `src/AI` is Godot-free).
- `CHIMERA_GOLDEN_RECORD=1 dotnet test --filter ~RecordGoldenBaseline` → wrote `golden-scenario.golden.txt` (300 samples; early hash `4DCB84DB` ≠ late hash `47AD1976` → scenario is dynamic).
- Full suite after embedding golden → 20/20 green (6 prior + 4 Golden + 10 boundary).
- Negative control: corrupted golden tick 1 (`4DCB84DB`→`DEADBEEF`), rebuilt, ran AC2 → FAILED with `Checksum drift at tick 1: expected 0xDEADBEEF, actual 0x4DCB84DB`; restored golden (byte-identical) → 20/20 green again.
- `dotnet build godot/godot.csproj` → succeeded, 0 errors (game build unaffected).
- Sole self-correction: `Sqrt(2)` exact-pin (raw 92681) was right; the squaring corroboration's tolerance was too tight (real deterministic error is 3 raw, 131069 vs 131072) → loosened to ≤8 raw with an accurate comment.

### Completion Notes List

- **Scope honored: test-only, zero production change.** No sim behavior was altered. Production edits = none; the only non-test file touched is the Tier-1 csproj (one `src/AI/**` compile glob + the golden `<EmbeddedResource>`/`<None Remove>`). `dotnet build godot/godot.csproj` stays green.
- **AC1 ✅** `RunsTwiceInProcess_BothMatchGoldenAndEachOther`: two fresh `Build()`s × 300 ticks are byte-identical to each other AND to the golden (proves in-process determinism + no static/shared-state leak).
- **AC2 ✅** `MatchesGolden_RecordedInSeparateProcess`: the committed golden was produced by a prior `dotnet test` process and is asserted by a fresh process — the cross-process comparison, no subprocess spawner.
- **AC3 ✅** `OneTickPerturbation_IsDetectedAndLocated`: +1 raw into the worker's `Fixed` health at loop index K=100 diverges at exactly tick K+1=101 with expected≠actual. Worker chosen because CombatSystem skips gatherers (GatheringSystem.cs:11-12), so the injection persists.
- **Key decision: in-code synthetic scenario** (not `alpha_map_01.json`) — the JSON *apply* path is Godot-coupled in `MainScene.ApplyScenario`; a Godot-free `ScenarioApplier` is Story 1.8b. In-code = zero production deps, zero duplication, zero drift risk. `// TODO(1.8b)` left in `GoldenScenario` doc.
- **AI kept deterministic-and-quiet** by giving Player2 no production building + 0 ore: every `AiOpponentSystem` action is gated to score 0 (3 fodder units < the Normal attack threshold of 5; can't afford to build), so it ticks and is pinned but no-ops. Float scoring is never reached.
- **Faithful to MainScene.cs:246-268**: same 9-system order, same stores, `EnableChecksums`, and `director.LoadScenario(new ScenarioData())` lifecycle — so the Story 1.8a relocation ("Golden checksum must match") has an exact guard. Drove the sim with `StepOnce()` (never `Update(float)`), `ChecksumInterval=1`.
- **Re-baseline path for 1.3b/1.4/1.5**: `CHIMERA_GOLDEN_RECORD=1` → `RecordGoldenBaseline` writes the golden to source via `[CallerFilePath]`; AC tests skip in record mode so a re-baseline never fails against the stale embedded copy. Golden read via embedded manifest stream + `\n`-split/`\r`-trim parse → portable for the 1.10c Windows↔Linux gate.
- **Task 6 (inherited 1.1 deferral)**: 10 `Fixed` boundary tests pin the desync traps — multiply floors toward −∞ (−1.5→−2) vs divide truncates toward zero (−1.5→−1); unchecked wrapping overflow at the 16.16 limits; `Sqrt` of 0/negative→0 and `Sqrt(2)`→raw 92681. Every expected value derived independently from the math (no tautological asserts, per the 1.1 review).
- **No new dependencies**; nothing added to `godot.sln`; no `Multiplayer/` pulled in.

### File List

- `godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` (modified — added `src/AI/**` compile glob; added golden `<EmbeddedResource>` + `<None Remove>`)
- `godot/ProjectChimera.Sim.Tests/Golden/GoldenScenario.cs` (new — `GoldenHarness` + in-code scenario replicating MainScene's 9-system loop)
- `godot/ProjectChimera.Sim.Tests/Golden/GoldenChecksumReplay.cs` (new — `RunAndRecord`, first-divergence `CompareSequences`, golden IO, env-var re-baseline writer)
- `godot/ProjectChimera.Sim.Tests/Golden/GoldenChecksumReplayTests.cs` (new — recorder + the 3 AC tests)
- `godot/ProjectChimera.Sim.Tests/Golden/golden-scenario.golden.txt` (new — committed 300-sample golden baseline)
- `godot/ProjectChimera.Sim.Tests/Determinism/FixedBoundaryTests.cs` (new — 10 `Fixed` boundary-value determinism tests)
- `_bmad-output/implementation-artifacts/sprint-status.yaml` (modified — story status ready-for-dev → in-progress → review)

### Change Log

- 2026-06-22 — Story 1.2 implemented: golden-checksum replay harness pinning current sim behavior. Added `src/AI` to the Tier-1 compile set; built a Godot-free in-code scenario + 9-system headless harness; recorded/committed a 300-tick golden; added recorder + first-divergence comparator + the 3 AC tests; added 10 `Fixed` boundary-value determinism tests (1.1 deferral). 20/20 Tier-1 tests green; `godot.csproj` build green. Status → review.
