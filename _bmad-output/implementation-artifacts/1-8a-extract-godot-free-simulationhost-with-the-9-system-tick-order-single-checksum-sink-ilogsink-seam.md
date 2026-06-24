---
baseline_commit: 2ebf704
---

# Story 1.8a: Extract Godot-free SimulationHost with the 9-system tick order + single checksum sink + ILogSink seam

Status: review

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As a solo developer decomposing the 2,234-LOC MainScene god-object,
I want a net-new Godot-free `SimulationHost` owning the canonical 9-system tick order (ModifierSystem reserved at index 3 before CombatSystem, single checksum sink), with a net-new injected `ILogSink` so sim/Godot-free code never calls `GD.Print`/Console, and `StressTest.cs` relocated out of `src/Core`,
so that the sim spine is testable headless and runs without perturbing the tick, ready to be reused **verbatim** by the server.

> **This is migration Step 1 of the "Shrinking Composition Root + Sim-Spine Strangler" (`game-architecture.md` §Step 6, sub-decision 2; AR-6 + AR-4), plus Step 0's `ILogSink`/`GodotLogSink` seam and the `StressTest.cs` relocation folded in (epics.md L654, L492).** It is a **mechanical lift**: the store construction + `SimulationLoop` assembly + `EnableChecksums` currently inline in `MainScene` (`MainScene.cs:262-296`) move **verbatim** into a net-new Godot-free `SimulationHost`. **Three facts from the codebase reshape the architecture text and are settled below:** (1) **`SimulationLoop.cs` already exists and is already Godot-free** — `SimulationHost` *wraps/owns* it (composition); the loop's internals are **NOT** rewritten (byte-identical insurance). (2) The architecture's `MainScene.cs:NNN` citations have **drifted** (the doc was authored against a ~2,223-LOC snapshot; the live file is 2,234 LOC) — **locate every seam by symbol/content, never by the doc's literal line number.** (3) The sim folders (`Core/Combat/Economy/Navigation/AI`) are **already 100% Godot-free** (zero `GD.*`, zero `using Godot` except the two `<Compile Remove>`'d files `MainScene.cs` + `StressTest.cs`) — so `ILogSink`'s job here is to give the **new** host a Godot-free logging seam, not to clean up existing sim code.

## Acceptance Criteria

1. **(SimulationHost is the Godot-free single owner of the canonical 9-system order)** **Given** the tick construction currently inside `MainScene` **When** it is extracted into a net-new `SimulationHost` (`src/Core/Sim/SimulationHost.cs`) **Then** `SimulationHost` has **no `using Godot`** / no Godot type / no `GD.*`, owns the canonical 9-system tick order **byte-identical** to the as-built order (`BuildingSystem → GatheringSystem → MovementSystem → CombatSystem → ProjectileSystem → SupplySystem → FogOfWarSystem → AiOpponentSystem → ScenarioDirector`), with a **documented reserved slot at 0-based index 3 (immediately before `CombatSystem`) for the future `ModifierSystem`** (reserved by comment + pinned by a test — **`ModifierSystem` is NOT built in this story**), and a `SystemOrderTest` asserts the order and **FAILS** if a system is reordered, added, or removed.

2. **(Single checksum sink)** **Given** the as-built `OnChecksum` is set in **three** competing places (the inline HUD-log assignment, the multiplayer overwrite, and the golden harness's capture) **When** `SimulationHost` is wired **Then** it exposes a **single** `SetChecksumSink(Action<uint, uint>)` owner called **exactly once** per caller, the two `MainScene` `OnChecksum` assignments are **consolidated into one** `host.SetChecksumSink(...)` call (offline → log; online → also `_lockstep.SendChecksum`), and the golden harness sets its capture sink through the same single method.

3. **(Net-new injected ILogSink deterministic logging seam)** **Given** the net-new `ILogSink` (`src/Core/Sim/ILogSink.cs`) **When** `SimulationHost` or any Godot-free sim-spine code logs **Then** it logs **only** through the injected `ILogSink` (never `GD.Print`/`Console`), a Godot-free `NullLogSink` (`src/Core/Sim/`) is injected by the test project + future server, a presentation `GodotLogSink` (`src/UI/`, calls `GD.Print`) is injected by `MainScene`, and `SimulationHost` itself contains zero `GD.*`/`Console` calls.

4. **(StressTest.cs relocated out of `src/Core`)** **Given** `StressTest.cs` is the second `using Godot;` file polluting `src/Core` **When** it is relocated out of `src/Core` (to `tools/`) **Then** `scenes/stress_test.tscn`'s script `path=` is repointed to the new location, the Tier-1 `<Compile Remove="..\src\Core\StressTest.cs" />` line is removed (it is no longer under the `..\src\Core\**` glob), the file still compiles in `godot.csproj`, and `src/Core` contains **only** `MainScene.cs` as a `using Godot;` file.

5. **(Goldens byte-identical — the regression gate — and Godot-free compile)** **Given** both golden harnesses (`GoldenScenario.Build`, `MultiFactionScenario.Build`) switched to construct the sim via `SimulationHost` **When** the full Tier-1 suite runs **Then** `golden-scenario.golden.txt` and `golden-multifaction.golden.txt` verify **green and UNCHANGED** (`git status` clean on both), `SimulationHost` compiles + runs in the Godot-free `ProjectChimera.Sim.Tests` project (`GodotFreeBoundaryTest` green), and `dotnet build godot/godot.csproj` is green. **If a golden moves, behavior leaked — fix the extraction; do NOT re-record.**

_Covers: FR-39 (LAN determinism / desync-free MP — the sim spine the server reuses verbatim), FR-44 (deterministic-sim test coverage — headless `SimulationHost` + `SystemOrderTest`), AR-6 (net-new `SimulationHost`: single owner of the 9-system order, ModifierSystem reserved at index 3, single checksum sink, constructed by both the test project and `ServerBootstrap`), AR-4 (net-new `ILogSink` deterministic logging seam; relocate `StressTest.cs` out of `src/Core`). Depends on: 1.7 (DONE — `ScenarioValidator`/`Validated<T>`/`CanonicalModelHash` shipped; 1.8a does NOT consume `Validated<T>` — that type-gating is 1.8b). Independent of 1.8b/1.8c (this story builds the host they consume)._

> Split from former 1.8. Net-new `SimulationHost` (AR-6) = architecture migration **Step 1** (mechanical lift). AR-4 `ILogSink` seam + `StressTest.cs` relocation folded here (epics.md coverage note L492/L654) — both are headless-server/test prerequisites that belong with the sim spine. `ModifierSystem` is **reserved** at index 3 but built in Epic 2. Strangles tick logic out of `MainScene.cs` (2,234 LOC). **Golden checksum must stay byte-identical.**

---

## Developer Context

**You (the dev agent) have ONLY this file. Read this whole section before editing anything.** The work is **one net-new Godot-free composition class (`SimulationHost`)** that *wraps the existing, unchanged `SimulationLoop`* + constructs the stores and the 9 ordered systems; a **net-new `ILogSink` seam** (interface + a no-op + a Godot impl); **switching three construction sites** (two golden harnesses + `MainScene`) to build the host; a **single checksum-sink consolidation** in `MainScene`; and a **file relocation** (`StressTest.cs`). The host + seam live in `src/Core/Sim/` (Godot-free, **auto-globbed** into Tier-1 — no `.csproj` edit). The byte-identical golden suite is the safety net for the whole thing.

### The traps (each of these has bitten a prior story or is the architecture's stated failure mode)

1. **Moving a golden.** This story is a **behavior-preserving extraction** — it must change **zero** sim ticks. The goldens (`golden-scenario.golden.txt`, `golden-multifaction.golden.txt`) are `<EmbeddedResource>` and must stay **byte-identical**. There is **no intentional re-baseline in 1.8a** — `CHIMERA_GOLDEN_RECORD` must **never** be set. If a golden moves, you changed behavior during the lift (wrong system order, a dropped/extra ctor arg, a changed seed/`FactionRegistry`, a touched `SimChecksum`) — **find it and fix it.** (AC5)
2. **Rewriting `SimulationLoop.cs`.** `SimulationLoop` (`src/Core/SimulationLoop.cs`, 147 LOC) is **already Godot-free and golden-proven**. `SimulationHost` **composes** it (`new SimulationLoop(world, …9 systems…)`) — it does **NOT** replace, fold-in, or refactor it. The duplicated checksum block in `StepOnce`/`Update` stays as-is; de-duplicating it is **not this story** (it risks the golden and the "single sink" goal is about the *external* `OnChecksum` owner, not the loop's internals). (D1)
3. **Over-reaching into 1.8b / 1.8c / 1.9a.** Do **NOT** extract `ScenarioApplier`, move `ApplyScenario`/`SpawnScenarioUnit`/`ParseBuildingType`/`ApplyFallbackScenario`, or hoist Godot path resolution (**all 1.8b**). Do **NOT** build `ISetupPhase`/`ScenePhaseRunner`/`PhaseOrderTest` or do the MainScene-shrink diff (**all 1.8c**). Do **NOT** build `ServerBootstrap` or a server checksum collector (**1.9a**). 1.8a builds the host; later stories consume it. (D9)
4. **Widening `SimChecksum` or generalizing factions.** Do **NOT** touch `SimChecksum.cs`, bump `checksum_algo_version`, or change `Mix` visibility — generalizing the checksum is a deliberate **hash-changing re-baseline** (architecture Step 13 / AR-15), **not 1.8a**. Preserve `new FactionRegistry(2)` for `MainScene` and the 2-faction golden exactly (the 4-faction golden passes its own registry — see Verify-in-code). N-faction generalization is Step 11. (D4)
5. **Building `ModifierSystem`.** Index 3 is a **reserved, documented slot only** (a code comment at the `CombatSystem` registration + the `SystemOrderTest`). `ModifierSystem` is Epic 2 (AR-9). Inserting *anything* at index 3 now — even a no-op — is out of scope and would invite a golden move. The host registers the **9 live systems**; the 10th position is reserved by contract. (D2)
6. **The `DamageTable` ctor-arity divergence.** `MainScene` constructs `CombatSystem`/`ProjectileSystem` with **4 args** (passing `_damageTable`); both goldens construct them with **3 args** (no table). Both are equivalent because the ctor defaults `DamageTable? table = null` and a null table falls back to `DamageTable.Default`. `SimulationHost.Create` must reproduce **each caller's** current construction — i.e. accept a `DamageTable?` defaulting to `null`/`Default` so the goldens (passing nothing) stay byte-identical while `MainScene` passes its loaded table. (D4)
7. **Putting `GodotLogSink` or `StressTest.cs` where Tier-1 globs them.** Tier-1 compiles `..\src\{Core,Combat,Economy,Navigation,AI}\**\*.cs`. Any file with `using Godot;` in those folders pulls `GodotSharp` into the test assembly and **fails `GodotFreeBoundaryTest`** (a reflection test asserting the sim assembly references no `GodotSharp`). So `GodotLogSink` goes in **`src/UI/`** (not globbed) and `StressTest.cs` moves **out** of `src/Core` (to `tools/`, not globbed). The Godot-free `ILogSink`/`NullLogSink`/`SimulationHost` go in `src/Core/Sim/` (globbed — that's the point). (D6, D7, D8)
8. **Trusting the architecture's `MainScene.cs:NNN` numbers.** They drifted (doc snapshot was 2,223 LOC; live is 2,234). The verified live citations are in "Pre-flight facts" below; locate the `OnChecksum` sites and the construction block by **symbol** (`OnChecksum =`, `new SimulationLoop(`, `EnableChecksums(`), not by the doc's literal lines.

### The shape of the work (1 net-new host + 1 net-new log seam + 3 construction-site switches + 1 sink consolidation + 1 file move; goldens UNCHANGED)

1. **Net-new `ILogSink` seam** — `src/Core/Sim/ILogSink.cs` (interface, Godot-free) + `src/Core/Sim/NullLogSink.cs` (no-op, Godot-free — used by tests + future server) + `src/UI/GodotLogSink.cs` (presentation impl calling `GD.Print`/`GD.PrintErr`). (AC3)
2. **Net-new `SimulationHost`** — `src/Core/Sim/SimulationHost.cs` (Godot-free): a factory (`Create(...)`) that constructs the stores + the **9 systems in canonical order** (with the reserved index-3 `ModifierSystem` slot documented before `CombatSystem`) + a `SimulationLoop` + `EnableChecksums(...)`, takes an injected `ILogSink` + the presentation-originated inputs (faction defs, `DamageTable?`, AI difficulty, the checksum `FactionRegistry`), exposes the stores/systems/tick API as properties/pass-throughs, and owns the single `SetChecksumSink`. (AC1, AC2, AC3)
3. **Net-new `SystemOrderTest`** — `ProjectChimera.Sim.Tests/Sim/SystemOrderTest.cs`: assert the host's ordered systems are exactly the 9 (by type, in order) and that the reserved slot contract holds (`CombatSystem` is immediately preceded by `MovementSystem` today). (AC1)
4. **Switch both golden harnesses** — `GoldenScenario.Build` (`GoldenScenario.cs:108-117`) + `MultiFactionScenario.Build` (`MultiFactionScenario.cs:78-87`) construct the loop via `SimulationHost` (injecting `NullLogSink`); populate the host's stores; tick via `host.StepOnce()`. Re-run → **byte-identical** (no re-record). (AC1, AC5)
5. **Switch `MainScene`** — construct `SimulationHost` (injecting `new GodotLogSink()`); read `_host.World` / `_host.Buildings` / `_host.ScenarioDirector` / etc. in place of the former local fields; consolidate the two `OnChecksum` assignments into **one** `_host.SetChecksumSink(...)`. (AC1, AC2, AC3)
6. **Relocate `StressTest.cs`** — `src/Core/StressTest.cs` → `tools/StressTest.cs`; repoint `scenes/stress_test.tscn` script `path=`; remove the Tier-1 `<Compile Remove>` line; verify `godot.csproj` still compiles it. (AC4)
7. **Prove AC5** — full Tier-1 suite green, both goldens `git`-clean, `GodotFreeBoundaryTest` green, `dotnet build godot/godot.csproj` green; optional in-engine smoke.

### Key design decisions (settled here — do NOT re-derive)

**D1 — `SimulationHost` *composes* the existing `SimulationLoop`; the loop is unchanged.** `SimulationHost` holds a private `SimulationLoop _loop` constructed with the 9 systems, and exposes pass-throughs (`StepOnce()`, `Update(float)`, `CurrentTick`, `LastChecksum`, `InterpolationAlpha`, `SetChecksumSink`). **Do not modify `SimulationLoop.cs`.** _(Rejected: folding the loop's tick logic into `SimulationHost` — unnecessary churn on golden-proven code, higher byte-identical risk. The "net-new" value of `SimulationHost` is that it becomes the **single composition root** for the sim — stores + systems + loop + checksum sink + log seam — that MainScene, the goldens, and `ServerBootstrap` all construct, replacing the scattered inline construction in `MainScene`.)_

**D2 — The canonical 9-system order + the reserved index-3 slot.** Register, in this exact order (verified at `MainScene.cs:262-296`; identical in both goldens):

| 0-based idx | System | Construction note |
|---|---|---|
| 0 | `BuildingSystem` | `_buildSys` (held as a field — MainScene/AI reference it directly) |
| 1 | `GatheringSystem` | |
| 2 | `MovementSystem` | |
| **3** | **— RESERVED: `ModifierSystem` —** | **Epic 2 (AR-9). NOT built in 1.8a. A `// SimulationHost contract: ModifierSystem inserts HERE (index 3, before CombatSystem) — Epic 2` comment marks the slot.** |
| 4 | `CombatSystem` | reads `Effective*` stats (which `ModifierSystem` will recompute the same tick) |
| 5 | `ProjectileSystem` | `Combat.ProjectileSystem` |
| 6 | `SupplySystem` | |
| 7 | `FogOfWarSystem` | `_fog` |
| 8 | `AiOpponentSystem` | runs after supply/construction are updated |
| 9 | `ScenarioDirector` | **LAST** — triggers see the fully-updated world |

The table shows the **forward** (post-`ModifierSystem`) indexing. **Today's live `_systems` array has only the 9 filled rows** — `CombatSystem` sits at live array index **3** until `ModifierSystem` is inserted before it (shifting Combat to 4). The reserved row is a documented contract (a comment between `MovementSystem` and `CombatSystem`), **not** a registered no-op. _Why ScenarioDirector runs last:_ triggers must observe the same tick's fully-updated supply caps, construction states, and combat outcomes (`architecture.md:87`). _Why ModifierSystem reserves index 3:_ `CombatSystem` must read recomputed `Effective*` stats the **same** tick, or it lags one tick versus a correctly-ordered peer → desync (`game-architecture.md:2053-2054`).

**D3 — `SimulationHost` constructs + owns the stores; callers read them back.** The host's factory constructs `EntityWorld`, `ResourceNodeStore`, `ResourceStore(Fixed.Zero)`, `BuildingStore`, `Combat.ProjectileStore`, `Combat.CombatEventQueue`, `FogOfWarSystem`, `MatchStats`, and the 9 systems — absorbing `MainScene.cs:262-296`. It **exposes** them as read-only properties so the existing consumers keep working by reading `_host.X`: `World`, `Nodes`, `Resources`, `Buildings`, `Projectiles`, `CombatEvents`, `MatchStats`, `BuildSys` (BuildingSystem), `ScenarioDirector`, `Fog`. The goldens construct the host, **then** populate `host.World`/`host.Buildings`/`host.Nodes`/`host.Resources` with their synthetic `Fixed` data (the systems hold references, so populate-after-construct is byte-identical to today's populate-then-construct), then `director.LoadScenario(...)` via `host.ScenarioDirector`, then `host.StepOnce()`.

**D4 — Preserve every byte-identical input exactly.** The golden gate enforces this, but get it right by construction:
- **`FactionRegistry`:** `Create` takes the checksum `FactionRegistry` as a parameter (the caller constructs it). `MainScene` + the 2-faction golden pass `new FactionRegistry(2)`; the 4-faction golden passes its own (see Verify-in-code #3). Do **not** hardcode `(2)` inside the host (it would break the multifaction golden).
- **`DamageTable`:** `Create` takes `DamageTable? damageTable = null`; the host passes it through to `CombatSystem`/`ProjectileSystem` (which default `null → DamageTable.Default`). Goldens pass nothing (→ Default); `MainScene` passes its loaded `_damageTable`.
- **RNG seed:** construct `new EntityWorld()` exactly as today (it seeds `SimRng` with `DEFAULT_RNG_SEED` internally). Do **not** add seed plumbing in 1.8a (the architecture's `Create(matchSeed:…)` is forward-looking; preserving the default-seeded `new EntityWorld()` is what keeps the golden byte-identical).
- **Do not touch `SimChecksum.cs`** or the systems' bodies.

**D5 — Single checksum sink = one `SetChecksumSink` owner, called once per caller.** The as-built `SimulationLoop.OnChecksum` (`Action<uint,uint>?`) is the single delegate slot; the bug is that `MainScene` assigns it **twice** (inline HUD-log, then the multiplayer path overwrites it) and the golden harness assigns it separately. `SimulationHost` exposes `void SetChecksumSink(Action<uint,uint> sink) => _loop.OnChecksum = sink;`. In `MainScene`, replace the two assignments with **one** call whose body does both jobs:
```csharp
_host.SetChecksumSink((tick, checksum) =>
{
    _logSink.Info($"[Checksum] tick={tick} hash=0x{checksum:X8}");   // GodotLogSink → GD.Print
    if (_lockstep.IsOnline) _lockstep.SendChecksum(tick, checksum);   // online path, formerly the overwrite
});
```
The golden harness sets its capture sink via the same method: `host.SetChecksumSink((tick, hash) => seq.Add(new Sample(tick, hash)));`. _(Deleting the now-redundant inline assignment fully is architecture Step 10; here it's naturally subsumed — there is exactly one `SetChecksumSink` call site per caller after the switch.)_

**D6 — `ILogSink` shape: minimal, injected, no in-tick string interpolation on a hot path.** Recommended:
```csharp
// src/Core/Sim/ILogSink.cs  (Godot-free)
public interface ILogSink
{
    void Info(string message);
    void Warn(string message);
}
```
`NullLogSink` (no-op, `src/Core/Sim/`) for tests + future server; `GodotLogSink` (`src/UI/`) maps `Info→GD.Print`, `Warn→GD.PrintErr`. Injected via `SimulationHost`'s constructor — **not** a static ambient singleton (the server/test must inject their own). The host's **only** logging in 1.8a is low-frequency/diagnostic (e.g. a misconfiguration `Warn`); the checksum line is logged by the **sink delegate** (presentation side, D5), not by the host. **Do not add any per-tick, per-entity logging through `ILogSink`** — the architecture's structured-`Fixed.Raw`-arg refinement is deferred; a simple string interface is correct for the diagnostic frequency here. _(The broad migration of `MainScene`'s ~50 `GD.*` calls happens as their logic blocks move into the spine in 1.8b/1.8c — not 1.8a.)_

**D7 — Folder = `src/Core/Sim/` (auto-globbed; no `.csproj` edit for the host).** `SimulationHost.cs`, `ILogSink.cs`, `NullLogSink.cs` go in **`src/Core/Sim/`**. The Tier-1 csproj already globs `..\src\Core\**\*.cs`, so these **auto-compile into the Godot-free test project with no csproj change** — which is exactly what AC5's "compiles in the Godot-free test project" needs. _This settles the `src/Sim/` (project-context.md L40) vs `src/Core/Sim/` (canonical architecture tree, `game-architecture.md:1608-1616`) discrepancy in favor of `src/Core/Sim/`: it matches the canonical tree AND avoids a new `<Compile Include>` glob (a top-level `src/Sim/` is **not** globbed and would require one)._ Namespace: `ProjectChimera.Core.Sim` (follows the `ProjectChimera.<System>` convention).

**D8 — `StressTest.cs` → `tools/StressTest.cs`.** Move the file out of `src/Core`. Consequences (all required):
- **Repoint the scene:** `scenes/stress_test.tscn` line 3, `path="res://src/Core/StressTest.cs"` → `path="res://tools/StressTest.cs"` (the class stays `partial StressTest : Node3D`; the scene's node binding is unchanged).
- **Remove the Tier-1 exclusion:** delete `<Compile Remove="..\src\Core\StressTest.cs" />` from `ProjectChimera.Sim.Tests.csproj` — once the file leaves `src/Core`, it is no longer matched by `..\src\Core\**`, so the Remove is dead (and would error if it points at a missing path).
- **Verify `godot.csproj` still compiles it:** Godot's .NET SDK default-globs project `.cs` files, so `tools/StressTest.cs` should compile into `godot.csproj` automatically. **Confirm** (Verify-in-code #2) — if `godot.csproj` uses explicit `<Compile Include>` items, add `tools/`. `StressTest` keeps its `using Godot;` (it's a `Node3D` benchmark backing a scene) — that's fine **outside** `src/Core` (the `BannedSimApiAnalyzer` scopes to the sim folders; moving it out is the whole point).
- `StressTest`'s deps (`MultiMeshBridge` in `src/UI`, sim types) are unaffected by the move. `MainScene` does **not** reference `StressTest`.

**D9 — Scope boundary vs 1.8b/1.8c/1.9a.** `SimulationHost` is the dependency those stories consume: 1.8b's `ScenarioApplier` *takes a `SimulationHost`* and becomes the sole sim-truth writer (moving `ApplyScenario` & friends); 1.8c wraps the `_Ready` Setup* sequence as `ISetupPhase[]` and shrinks `MainScene`; 1.9a's `ServerBootstrap` constructs `SimulationHost` with **no presentation**. 1.8a's obligation: leave the host **constructible with zero presentation/Godot dependency** (so 1.9a can reuse it verbatim) — but **build none of those three.** The mutation path (`ApplyScenario`/`SpawnScenarioUnit`/`ParseBuildingType`/`ApplyFallbackScenario`) stays in `MainScene` this story; only its *stores and tick loop* relocate into the host.

### Pre-flight facts you MUST NOT re-derive (verified against the codebase at `2ebf704`)

- **`SimulationLoop` is already Godot-free** (`godot/src/Core/SimulationLoop.cs`, 147 LOC, only `using System;`). `public interface ISimSystem { void Tick(EntityWorld world, Fixed dt); }` (`:8-11`). `public SimulationLoop(EntityWorld world, params ISimSystem[] systems)` (`:63`) stores `private readonly ISimSystem[] _systems` (`:55`). `public void StepOnce()` (`:87`) — lockstep path: `SnapshotPositions` → `for (i<_systems.Length) _systems[i].Tick(World, FixedDt)` → `CurrentTick++` → checksum block. `public int Update(float realDelta)` (`:110`) — accumulator free-run path (same system loop + checksum block, **duplicated** at `:121-139`). `public void EnableChecksums(BuildingStore, ResourceStore, FactionRegistry)` (`:75`). `public Action<uint,uint>? OnChecksum;` (`:42`). `public uint CurrentTick { get; private set; }` (`:47`). `public uint LastChecksum { get; private set; }` (`:36`). `public int ChecksumInterval { get; set; } = 60;` (`:33`). `public const int TICKS_PER_SECOND = 30;` (`:21`); `FixedDt = Fixed.FromRaw(Fixed.ONE/30)` (`:24`). `public float InterpolationAlpha` (`:53`, presentation-only). **Leave this file unchanged.** [Source: SimulationLoop.cs]
- **The as-built tick construction to lift verbatim** (`MainScene.cs:262-296` — the architecture's drifted "246-266/268/269-270"): constructs the stores, then `_simLoop = new SimulationLoop(_world, _buildSys, new GatheringSystem(_nodes,_resources,_matchStats), new MovementSystem(), new CombatSystem(_projectiles,_combatEvents,_matchStats,_damageTable), new Combat.ProjectileSystem(_projectiles,_combatEvents,_matchStats,_damageTable), new SupplySystem(_resources), _fog, new AiOpponentSystem(_buildings,_resources,_buildSys,AiLevel), _scenarioDirector);` then `_simLoop.EnableChecksums(_buildings, _resources, new FactionRegistry(2));` then the inline `_simLoop.OnChecksum = (tick,checksum) => GD.Print($"[Checksum] tick={tick} hash=0x{checksum:X8}");`. Field-held systems: `_buildSys = new BuildingSystem(_buildings,_resources,_factionDef,_factionDef2,_matchStats)` (~`:279`), `_fog = new FogOfWarSystem(Faction.Player1)` (~`:277`), `_scenarioDirector = new ScenarioDirector(_buildings,_resources)` (~`:280`). [Source: MainScene.cs:262-296 — locate by symbol, lines approximate]
- **The 9 `ISimSystem` classes** (grep-confirmed exhaustive, exactly 9 `: ISimSystem`): `BuildingSystem` (Economy), `GatheringSystem` (Economy), `MovementSystem` (Navigation), `CombatSystem` (Combat), `ProjectileSystem` (Combat), `SupplySystem` (Economy), `FogOfWarSystem` (Core), `AiOpponentSystem` (AI), `ScenarioDirector` (Core). **Leave all 9 bodies unchanged.** [Source: grep @ 2ebf704]
- **MainScene drives the tick** in `_Process(double delta)` (~`MainScene.cs:428`; no `_PhysicsProcess` for the sim), guarded by `Mode == Play && !_gameOver`: **replay** branch (`_replayPlayer.Flush(_simLoop.CurrentTick); _simLoop.StepOnce();`), **online** branch (`if (_lockstep.Flush(_simLoop.CurrentTick)) _simLoop.StepOnce();`), **offline** branch (`_simLoop.Update((float)delta);`). After the switch these read `_host.CurrentTick` / `_host.StepOnce()` / `_host.Update(...)`. The headless early-return branch (~`:236-244`, constructs `DedicatedServer` and `return`s before building the loop) is the gap `ServerBootstrap` fills in **1.9a** — do not touch it here. [Source: MainScene.cs:428-457, 236-244 — locate by symbol]
- **`SimChecksum`** (`godot/src/Core/SimChecksum.cs`, 116 LOC, Godot-free, static): `public static uint Compute(EntityWorld, BuildingStore, ResourceStore, FactionRegistry)` (`:43`). `AlgoVersion = 3` (`:37`). Already folds entity Position/Health, building Alive/Health/ConstructionTimer, per-faction Ore/Crystal/SupplyUsed/SupplyCap/FactionBase for `factions.ActiveFactions` ascending, and `Rng.State`. **Do NOT modify it** (widening is Step 13/AR-15, a separate re-baseline). [Source: SimChecksum.cs]
- **The three checksum set-sites (the "single sink" target):** (1) inline HUD-log at `MainScene.cs:295-296` (`OnChecksum = (tick,checksum) => GD.Print("[Checksum]…")`); (2) multiplayer overwrite at `MainScene.cs:~1879` (`GD.Print(...)` **+** `_lockstep.SendChecksum(tick,hash)`; the doc's stale "MainScene.cs:1761") plus the desync receiver `_lockstep.OnDesync += … GD.PrintErr("[DESYNC]…")` at `~:1885`; (3) golden capture at `GoldenChecksumReplay.cs:57` (`OnChecksum = (tick,hash) => seq.Add(...)`). [Source: MainScene.cs (locate by `OnChecksum =` and `SendChecksum`); GoldenChecksumReplay.cs:57]
- **Golden harness** (`godot/ProjectChimera.Sim.Tests/Golden/`): `GoldenScenario.Build()` builds the loop **inline** at `GoldenScenario.cs:108-117` (same 9 order, **3-arg** Combat/Projectile — no DamageTable), `EnableChecksums(buildings, resources, new FactionRegistry(2))` (`:121`), `ChecksumInterval = 1` (`:122`), `director.LoadScenario(new ScenarioData())` (`:127`). `MultiFactionScenario.Build()` mirrors at `:78-87` (4-faction). `GoldenChecksumReplay.RunAndRecord` ticks via `harness.Loop.StepOnce()` (300 ticks default) and captures via `OnChecksum` (`:51-66`). Goldens are `<EmbeddedResource>` (`.csproj:42-46`), `checksum_algo_version: 3`. `CHIMERA_GOLDEN_RECORD=1` is the re-record toggle (`GoldenChecksumReplay.cs:31`) — **not used in 1.8a**. **Switch the two `Build` methods to construct `SimulationHost`; the inline 9-system block is what relocates.** [Source: GoldenScenario.cs; MultiFactionScenario.cs; GoldenChecksumReplay.cs]
- **`DamageTable.Default` fallback** makes the 3-vs-4-arg goldens/MainScene equivalent: `CombatSystem`/`ProjectileSystem` ctors default `DamageTable? table = null` and use `DamageTable.Default` when null (`CombatSystem.cs:34-35`, `ProjectileSystem.cs:27-28`, `DamageTable.cs:47`); `MainScene` also falls back to `Default` when the JSON is missing (`MainScene.cs:~273-275`). [Source: CombatSystem.cs; ProjectileSystem.cs; DamageTable.cs; MainScene.cs]
- **`StressTest.cs`** (`godot/src/Core/StressTest.cs`, 169 LOC): `public partial class StressTest : Node3D`, `using Godot;` (`:1`). Backs `scenes/stress_test.tscn` (script `path="res://src/Core/StressTest.cs"`, `.tscn:3`). Deps: `MultiMeshBridge` (`src/UI`), sim types. `<Compile Remove="..\src\Core\StressTest.cs" />` at `ProjectChimera.Sim.Tests.csproj:35`. Not referenced by `MainScene`. [Source: StressTest.cs; stress_test.tscn:3; ProjectChimera.Sim.Tests.csproj:35]
- **Tier-1 csproj** (`godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj`): `net8.0` (`:4`), `xunit` 2.9.2 (`:52-57`), **not** Godot-SDK. Globs `..\src\Core\**\*.cs` (`:13`), `..\src\Combat\**`, `..\src\Economy\**`, `..\src\Navigation\**`, `..\src\AI\**`, + 3 named `src/Multiplayer` files. `<Compile Remove>`: `..\src\Core\MainScene.cs` (`:34`) + `..\src\Core\StressTest.cs` (`:35`). ⇒ **new `src/Core/Sim/` files auto-compile; no glob add. Remove the StressTest `<Compile Remove>` once relocated.** [Source: ProjectChimera.Sim.Tests.csproj]
- **`GodotFreeBoundaryTest`** (`godot/ProjectChimera.Sim.Tests/GodotFreeBoundaryTest.cs`, 24 LOC) is a **reflection** test: asserts the sim assembly (`typeof(Fixed).Assembly`) references no `GodotSharp`/`GodotSharpEditor`. A `src/Core/Sim/` file with `using Godot;` would pull `GodotSharp` in and fail it ⇒ this is the compile-time guard that `SimulationHost`/`ILogSink`/`NullLogSink` are Godot-free. [Source: GodotFreeBoundaryTest.cs]
- **All net-new types confirmed ABSENT from source** (grep @ 2ebf704): `SimulationHost`, `ILogSink`, `NullLogSink`, `GodotLogSink`, `ScenarioApplier`, `ISetupPhase`, `ScenePhaseRunner`, `ServerBootstrap`. (`DedicatedServer` exists at `src/Multiplayer/DedicatedServer.cs` — a Godot `Node` relay shell, **not** `ServerBootstrap`; do not touch it.) No existing logging abstraction (`ILogSink`/`ILogger`) exists. [Source: grep @ 2ebf704]

### Verify-in-code before you wire (do NOT guess — read these exact sites)

1. **Each system's exact ctor args at every call site.** Read `MainScene.cs:262-296`, `GoldenScenario.cs:108-117`, and `MultiFactionScenario.cs:78-87` and capture the **exact** arguments each passes to `BuildingSystem`, `GatheringSystem`, `AiOpponentSystem`, `FogOfWarSystem`, `ScenarioDirector` (esp. the faction defs `BuildingSystem` takes, and the AI difficulty `AiOpponentSystem` takes). `SimulationHost.Create` must be parameterized so **each caller reproduces its current construction exactly**. The byte-identical golden is the proof.
2. **`godot.csproj` compile model** — confirm it default-globs project `.cs` (so `tools/StressTest.cs` compiles) or, if it has explicit `<Compile Include>`, add `tools/`.
3. **The 4-faction golden's `EnableChecksums` arg** — `MultiFactionScenario.Build` must pass a `FactionRegistry` covering its 4 factions (not `(2)`). Confirm the exact registry it constructs and thread it through `Create(... checksumFactions ...)` so the multifaction golden stays byte-identical.
4. **The live `OnChecksum` / `SendChecksum` lines in `MainScene`** — find them by symbol (the doc's `:1761` is stale) to do the D5 consolidation.

### Scope fence — do NOT, in this story

- **Do NOT** move/re-record either golden, or set `CHIMERA_GOLDEN_RECORD`. 1.8a is behavior-preserving ⇒ goldens byte-identical. A moved golden = a real leak; fix it. (AC5)
- **Do NOT** modify `SimulationLoop.cs`, `SimChecksum.cs`, or any of the 9 system bodies; do NOT bump `checksum_algo_version` or change `Mix` visibility (Step 13/AR-15). (D1, D4)
- **Do NOT** build `ModifierSystem` or register anything at index 3 — reserve it by comment + `SystemOrderTest` only. (D2)
- **Do NOT** extract `ScenarioApplier`, move `ApplyScenario`/`SpawnScenarioUnit`/`ParseBuildingType`/`ApplyFallbackScenario`, or hoist Godot path resolution — **1.8b**. (D9)
- **Do NOT** build `ISetupPhase`/`ScenePhaseRunner`/`PhaseOrderTest` or shrink `MainScene` beyond the mechanical store/loop relocation — **1.8c**. (D9)
- **Do NOT** build `ServerBootstrap` or a server checksum collector, and do NOT touch the headless `DedicatedServer` branch — **1.9a**. Just keep the host presentation-free so 1.9a can reuse it. (D9)
- **Do NOT** generalize `FactionRegistry`/the 2-faction checksum surface to N>2 (Steps 5/11). Preserve `new FactionRegistry(2)` for MainScene + the 2-faction golden. (D4)
- **Do NOT** put `GodotLogSink` or `StressTest.cs` in a Tier-1-globbed folder (`src/{Core,Combat,Economy,Navigation,AI}`) — `GodotFreeBoundaryTest` would fail. (D6, D7, D8)
- **Do NOT** add per-tick/per-entity logging through `ILogSink`. (D6)

---

## Tasks / Subtasks

- [x] **Task 1 — Net-new `ILogSink` seam (AC: 3)**
  - [x] Create `godot/src/Core/Sim/ILogSink.cs` (`#nullable enable`, namespace `ProjectChimera.Core.Sim`, **no `using Godot`**): `public interface ILogSink { void Info(string message); void Warn(string message); }` (D6).
  - [x] Create `godot/src/Core/Sim/NullLogSink.cs` (Godot-free): `public sealed class NullLogSink : ILogSink` with no-op methods (a `static readonly NullLogSink Instance` is convenient for tests/server).
  - [x] Create `godot/src/UI/GodotLogSink.cs` (`using Godot;` — presentation): `public sealed class GodotLogSink : ILogSink` mapping `Info → GD.Print`, `Warn → GD.PrintErr`.
  - [x] `dotnet build godot/godot.csproj` → green.

- [x] **Task 2 — Net-new `SimulationHost` (AC: 1, 2, 3)**
  - [x] Create `godot/src/Core/Sim/SimulationHost.cs` (`#nullable enable`, namespace `ProjectChimera.Core.Sim`, **no `using Godot`/`GD.*`/`Console`**). Constructor/factory takes the injected `ILogSink` + the presentation-originated inputs verified in "Verify-in-code #1/#3" (faction defs, `DamageTable? = null`, AI difficulty, the checksum `FactionRegistry`).
  - [x] Construct the stores (`EntityWorld`, `ResourceNodeStore`, `ResourceStore(Fixed.Zero)`, `BuildingStore`, `Combat.ProjectileStore`, `Combat.CombatEventQueue`, `MatchStats`, `FogOfWarSystem`), the 9 systems **in canonical order** with the **reserved index-3 comment** before `CombatSystem` (D2), the `SimulationLoop`, and `EnableChecksums(...)` with the caller's `FactionRegistry` (D3, D4).
  - [x] Expose read-only properties: `World`, `Nodes`, `Resources`, `Buildings`, `Projectiles`, `CombatEvents`, `MatchStats`, `BuildSys`, `ScenarioDirector`, `Fog`, `CurrentTick`, `LastChecksum`, `InterpolationAlpha`; and pass-throughs `StepOnce()`, `Update(float)`, `SetChecksumSink(Action<uint,uint>)`, plus `ChecksumInterval` get/set (the goldens set it to 1).
  - [x] Use the injected `ILogSink` (never `GD.*`) for any host-side diagnostic. Do **not** log the checksum here — that's the sink delegate (D5/D6).
  - [x] `dotnet build godot/godot.csproj` → green.

- [x] **Task 3 — `SystemOrderTest` (AC: 1)**
  - [x] New `godot/ProjectChimera.Sim.Tests/Sim/SystemOrderTest.cs`: construct a `SimulationHost` (with `NullLogSink` + test inputs) and assert its ordered systems are exactly `[BuildingSystem, GatheringSystem, MovementSystem, CombatSystem, ProjectileSystem, SupplySystem, FogOfWarSystem, AiOpponentSystem, ScenarioDirector]` by runtime type, in order; assert the reserved-slot contract (`CombatSystem` is immediately preceded by `MovementSystem` today). To enable this, expose the ordered systems for inspection (e.g. an internal `IReadOnlyList<ISimSystem> Systems` on the host, or a `string[] SystemOrder` of type names) — keep it Godot-free. The test must FAIL on any reorder/add/remove.
  - [x] `dotnet test --filter FullyQualifiedName~SystemOrder` → green.

- [x] **Task 4 — Switch both golden harnesses to `SimulationHost` (AC: 1, 5)**
  - [x] `GoldenScenario.Build` (`GoldenScenario.cs:108-117`): replace the inline `new SimulationLoop(...)` 9-system block with `SimulationHost.Create(... NullLogSink.Instance ...)`; construct/populate the host's stores; keep `ChecksumInterval = 1`; `director.LoadScenario(new ScenarioData())` via `host.ScenarioDirector`. The `GoldenHarness.Loop`/checksum capture uses `host.SetChecksumSink(...)` (or expose the host on the harness and capture through it).
  - [x] `MultiFactionScenario.Build` (`MultiFactionScenario.cs:78-87`): same switch, passing its 4-faction `FactionRegistry` (Verify-in-code #3).
  - [x] `dotnet test --filter FullyQualifiedName~Golden` → green, **goldens UNCHANGED** (`git status` clean on both `.golden.txt`). A moved golden = a real leak; fix the extraction, do NOT re-record.

- [x] **Task 5 — Switch `MainScene` to `SimulationHost` + consolidate the checksum sink (AC: 1, 2, 3)**
  - [x] Replace the `MainScene.cs:262-296` store/loop/`EnableChecksums`/`OnChecksum` block with: build the presentation-side inputs (loaded `_factionDef`/`_factionDef2`, `_damageTable`, `AiLevel`), then `_host = SimulationHost.Create(... new GodotLogSink() ...)`. Repoint the former local fields to `_host.World`/`_host.Buildings`/`_host.Resources`/`_host.Nodes`/`_host.Projectiles`/`_host.CombatEvents`/`_host.BuildSys`/`_host.ScenarioDirector`/`_host.Fog`/`_host.MatchStats` (keep field aliases if that minimizes churn — but sim truth now lives on the host).
  - [x] Update the `_Process` tick paths (~`:428-457`) to `_host.CurrentTick` / `_host.StepOnce()` / `_host.Update((float)delta)`.
  - [x] Consolidate the two `OnChecksum` assignments (inline `~:296` + MP `~:1879`) into **one** `_host.SetChecksumSink((tick,checksum) => { _logSink.Info($"[Checksum] tick={tick} hash=0x{checksum:X8}"); if (_lockstep.IsOnline) _lockstep.SendChecksum(tick, checksum); });` (D5). Keep the `OnDesync` → `GD.PrintErr`/`GodotLogSink.Warn` wiring.
  - [x] `dotnet build godot/godot.csproj` → green.

- [x] **Task 6 — Relocate `StressTest.cs` out of `src/Core` (AC: 4)**
  - [x] Move `godot/src/Core/StressTest.cs` → `godot/tools/StressTest.cs` (keep `using Godot;` + the class; namespace may stay or move to `ProjectChimera.Tools` — confirm nothing references it by namespace).
  - [x] Repoint `godot/scenes/stress_test.tscn` line 3: `path="res://src/Core/StressTest.cs"` → `path="res://tools/StressTest.cs"`.
  - [x] Remove `<Compile Remove="..\src\Core\StressTest.cs" />` from `ProjectChimera.Sim.Tests.csproj` (`:35`).
  - [x] Verify `godot.csproj` compiles `tools/` (Verify-in-code #2); add an include if it uses explicit items.
  - [x] `dotnet build godot/godot.csproj` → green; confirm `src/Core` now has **only** `MainScene.cs` using Godot.

- [x] **Task 7 — Prove AC5: full suite green, goldens byte-identical, Godot-free boundary (AC: 5)**
  - [x] `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` → ALL green, including `GodotFreeBoundaryTest` and both golden suites, with both `.golden.txt` **UNCHANGED** (`git status` clean).
  - [x] `dotnet build godot/godot.csproj` → green (only pre-existing CS8632 warnings).
  - [x] Grep `src/Core/Sim/*.cs`: zero `using Godot`/`GD.`/`Console.`/float gameplay math/`System.Random`. Confirm `SimulationHost`/`ILogSink`/`NullLogSink` are Godot-free.
  - [x] `git diff` shows no change to `SimulationLoop.cs`, `SimChecksum.cs`, the 9 system bodies, or `ISimSystem`.

- [x] **Task 8 — In-engine smoke (AC: 1, 2) — optional but recommended**
  - [x] Run the game (`/godot-verify` or Godot MCP run): a normal skirmish loads + plays through `_host`; the console still prints `[Checksum] tick=… hash=0x…` (now via the single `SetChecksumSink`); run the `stress_test.tscn` scene to confirm the relocated `StressTest` still loads. _(MainScene is excluded from Tier-1, so this is the only check of the production wiring.)_

---

## Dev Notes

### `ILogSink` seam (Task 1) — full
```csharp
// src/Core/Sim/ILogSink.cs   (Godot-free)
#nullable enable
namespace ProjectChimera.Core.Sim
{
    /// <summary>Deterministic logging seam: sim/Godot-free code logs ONLY through this (never GD.Print/Console),
    /// so the headless server/test project runs without perturbing the tick. Inject; never a static ambient sink.</summary>
    public interface ILogSink
    {
        void Info(string message);
        void Warn(string message);
    }

    public sealed class NullLogSink : ILogSink   // tests + future ServerBootstrap
    {
        public static readonly NullLogSink Instance = new();
        public void Info(string message) { }
        public void Warn(string message) { }
    }
}
```
```csharp
// src/UI/GodotLogSink.cs   (presentation — NOT under a Tier-1 glob)
#nullable enable
using Godot;
using ProjectChimera.Core.Sim;
namespace ProjectChimera.UI
{
    public sealed class GodotLogSink : ILogSink
    {
        public void Info(string message) => GD.Print(message);
        public void Warn(string message) => GD.PrintErr(message);
    }
}
```

### `SimulationHost` (Task 2) — shape (confirm the exact ctor args against the call sites; the golden is the proof)
```csharp
// src/Core/Sim/SimulationHost.cs   (Godot-free — NO using Godot / GD.* / Console / float gameplay math)
#nullable enable
using ProjectChimera.Core;
using ProjectChimera.Combat;
using ProjectChimera.Economy;
using ProjectChimera.Navigation;
using ProjectChimera.AI;
using ProjectChimera.Core.Definitions;   // FactionDefinition, DamageTable

namespace ProjectChimera.Core.Sim
{
    /// <summary>Net-new Godot-free composition root for the sim: owns the stores, the canonical 9-system
    /// tick order (ModifierSystem reserved at index 3, Epic 2), the SimulationLoop, and the single checksum sink.
    /// Constructed identically by MainScene, the golden harnesses, and (1.9a) ServerBootstrap.</summary>
    public sealed class SimulationHost
    {
        private readonly SimulationLoop _loop;
        private readonly ILogSink _log;
        private readonly ISimSystem[] _systems;   // held so SystemOrderTest asserts order without touching SimulationLoop

        // Stores/systems exposed so MainScene + goldens read host truth (D3):
        public EntityWorld World { get; }
        public ResourceNodeStore Nodes { get; }
        public ResourceStore Resources { get; }
        public BuildingStore Buildings { get; }
        public ProjectileStore Projectiles { get; }
        public CombatEventQueue CombatEvents { get; }
        public MatchStats MatchStats { get; }
        public BuildingSystem BuildSys { get; }
        public ScenarioDirector ScenarioDirector { get; }
        public FogOfWarSystem Fog { get; }

        public uint CurrentTick => _loop.CurrentTick;
        public uint LastChecksum => _loop.LastChecksum;
        public float InterpolationAlpha => _loop.InterpolationAlpha;
        public int ChecksumInterval { get => _loop.ChecksumInterval; set => _loop.ChecksumInterval = value; }

        // Factory: takes the injected log sink + the presentation-originated inputs the systems need.
        // damageTable == null → DamageTable.Default (preserves the goldens' 3-arg construction, D4).
        public static SimulationHost Create(
            ILogSink log,
            FactionRegistry checksumFactions,      // caller passes new FactionRegistry(2) (MainScene + 2-fac golden) or its 4-fac registry
            FactionDefinition? factionDef1 = null,
            FactionDefinition? factionDef2 = null,
            DamageTable? damageTable = null,
            AiDifficulty aiLevel = AiDifficulty.Normal)   // confirm enum + default vs the call sites
            => new SimulationHost(log, checksumFactions, factionDef1, factionDef2, damageTable, aiLevel);

        private SimulationHost(ILogSink log, FactionRegistry checksumFactions,
            FactionDefinition? f1, FactionDefinition? f2, DamageTable? damageTable, AiDifficulty aiLevel)
        {
            _log = log;
            World = new EntityWorld();                       // default DEFAULT_RNG_SEED — do NOT add seed plumbing (D4)
            Nodes = new ResourceNodeStore();
            Resources = new ResourceStore(Fixed.Zero);
            Buildings = new BuildingStore();
            Projectiles = new ProjectileStore();
            CombatEvents = new CombatEventQueue();
            MatchStats = new MatchStats();
            Fog = new FogOfWarSystem(Faction.Player1);
            BuildSys = new BuildingSystem(Buildings, Resources, f1, f2, MatchStats);
            ScenarioDirector = new ScenarioDirector(Buildings, Resources);

            _systems = new ISimSystem[]
            {
                BuildSys,                                                                 // [0] BuildingSystem
                new GatheringSystem(Nodes, Resources, MatchStats),                        // [1]
                new MovementSystem(),                                                     // [2]
                // RESERVED — ModifierSystem inserts HERE (before CombatSystem) in Epic 2 (AR-9). NOT built in 1.8a.
                new CombatSystem(Projectiles, CombatEvents, MatchStats, damageTable),     // [3] today; null table → DamageTable.Default
                new ProjectileSystem(Projectiles, CombatEvents, MatchStats, damageTable), // [4]
                new SupplySystem(Resources),                                              // [5]
                Fog,                                                                      // [6] FogOfWarSystem
                new AiOpponentSystem(Buildings, Resources, BuildSys, aiLevel),            // [7]
                ScenarioDirector,                                                         // [8] runs LAST
            };
            _loop = new SimulationLoop(World, _systems);
            _loop.EnableChecksums(Buildings, Resources, checksumFactions);
        }

        public void StepOnce() => _loop.StepOnce();
        public int Update(float realDelta) => _loop.Update(realDelta);
        public void SetChecksumSink(System.Action<uint, uint> sink) => _loop.OnChecksum = sink;   // the SINGLE sink owner (D5)

        // SystemOrderTest reads this. In Tier-1 the src is compiled INTO the test assembly (and in godot.csproj
        // MainScene shares the assembly), so `internal` is visible without InternalsVisibleTo. SimulationLoop is untouched.
        internal System.Collections.Generic.IReadOnlyList<ISimSystem> Systems => _systems;
    }
}
```
> The exact `Create` parameter list **must** match what each call site passes today (Verify-in-code #1/#3). If a system takes an arg you can't source cleanly into `Create`, add it as a parameter — do not invent a default that changes behavior. The byte-identical golden suite is the contract that this construction equals the old inline construction.

### Golden-harness switch (Task 4) — before/after
```csharp
// GoldenScenario.cs  — BEFORE (inline, :108-117)
var loop = new SimulationLoop(world, buildSys, new GatheringSystem(nodes, resources, stats),
    new MovementSystem(), new CombatSystem(projectiles, combatEvents, stats),       // 3-arg → DamageTable.Default
    new Combat.ProjectileSystem(projectiles, combatEvents, stats),
    new SupplySystem(resources), fog, new AiOpponentSystem(buildings, resources, buildSys, diff), director);
loop.EnableChecksums(buildings, resources, new FactionRegistry(2));
loop.ChecksumInterval = 1;

// AFTER — construct the host; populate its stores; tick via the host
var host = SimulationHost.Create(NullLogSink.Instance, new FactionRegistry(2) /* 4-fac golden: its registry */,
    /* faction defs / diff as the inline block passed them */);
host.ChecksumInterval = 1;
PopulateScenario(host.World, host.Buildings, host.Nodes, host.Resources, ...);   // same synthetic Fixed data, now on host stores
host.ScenarioDirector.LoadScenario(new ScenarioData());
// capture: host.SetChecksumSink((tick, hash) => seq.Add(new Sample(tick, hash)));  then loop host.StepOnce()
```
> Confirm `GoldenHarness` exposes whatever `GoldenChecksumReplay.RunAndRecord` needs (it sets `harness.Loop.OnChecksum` / calls `harness.Loop.StepOnce()` today). Easiest: give `GoldenHarness` a `SimulationHost Host` and have `RunAndRecord` use `host.SetChecksumSink(...)` + `host.StepOnce()`. Keep the public test surface minimal.

### Constraints & gotchas
- **`dotnet build` / `dotnet test` are authoritative** for C# correctness; the Godot MCP `run` does not rebuild the test assembly. Build + test before declaring done. [Source: 1.1–1.7 Dev Notes / LEARNINGS]
- **Byte-identical golden is the gate.** No intentional re-baseline exists in 1.8a — never set `CHIMERA_GOLDEN_RECORD`. A moved `.golden.txt` means the extraction changed behavior (wrong order, dropped ctor arg, changed seed/`FactionRegistry`/`DamageTable`, or a touched `SimChecksum`/`SimulationLoop`). [Source: GoldenChecksumReplay.cs; D1/D4]
- **Leave `SimulationLoop.cs` byte-for-byte unchanged.** The host *wraps* it. The duplicated `StepOnce`/`Update` checksum block is not refactored here. [Source: SimulationLoop.cs; D1]
- **C# nested-type access is one-directional (1.7 lesson).** If you reach for any "only this class can mint" trick, recall 1.7's `Proof` private-ctor attempt was a `CS0122` compile error (an enclosing type cannot call a nested type's *private* ctor). 1.8a needs no such pattern — but don't reintroduce it. [Source: 1.7 Debug Log]
- **`src/Core/Sim/` auto-globs into Tier-1; `GodotLogSink`/`StressTest` must stay out of globbed folders.** A `using Godot;` file under `src/{Core,Combat,Economy,Navigation,AI}` fails `GodotFreeBoundaryTest` (it pulls `GodotSharp` into the sim assembly). [Source: ProjectChimera.Sim.Tests.csproj:13-35; GodotFreeBoundaryTest.cs]
- **Locate `MainScene` seams by symbol, not the architecture doc's line numbers** (they drifted; file is 2,234 LOC). Search `new SimulationLoop(`, `EnableChecksums(`, `OnChecksum =`, `SendChecksum`. [Source: both research agents]
- **Pre-existing CS8632** nullable warnings are not this story's bug — leave them. [Source: 1.7 Dev Notes]
- **No new NuGet/dependency, no second build target.** AOT project-split is deferred post-1.0 — this story delivers only the Godot-free discipline (host compiles in `ProjectChimera.Sim.Tests`). [Source: project-context.md; AR-2; game-architecture.md scope call 3]

### Project Structure Notes
- **NEW (sim, Godot-free — `godot/src/Core/Sim/`):** `SimulationHost.cs`, `ILogSink.cs`, `NullLogSink.cs` — auto-globbed into Tier-1 via `..\src\Core\**`, **no `.csproj` glob edit**.
- **NEW (presentation — `godot/src/UI/`):** `GodotLogSink.cs` (`using Godot;` — deliberately outside the Tier-1 globs).
- **NEW (test — `godot/ProjectChimera.Sim.Tests/Sim/`):** `SystemOrderTest.cs`.
- **MOVED:** `godot/src/Core/StressTest.cs` → `godot/tools/StressTest.cs`.
- **EDIT:** `godot/src/Core/MainScene.cs` (construct `_host`, read `_host.X`, single `SetChecksumSink`, inject `GodotLogSink`); `godot/ProjectChimera.Sim.Tests/Golden/GoldenScenario.cs` + `MultiFactionScenario.cs` (construct via `SimulationHost`); `godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` (remove the `StressTest` `<Compile Remove>`); `godot/scenes/stress_test.tscn` (repath script); possibly `godot/ProjectChimera.Sim.Tests/Golden/GoldenChecksumReplay.cs` / `GoldenScenario.cs` `GoldenHarness` (expose the host for capture).
- **UNCHANGED (must stay so):** both `*.golden.txt`; `SimulationLoop.cs`; `SimChecksum.cs`; `ISimSystem`; all 9 system class bodies; the 1.7 validator types (`Validated.cs`/`ScenarioValidator.cs`/`CanonicalModelHash.cs`/`ScenarioGate.cs`); `DedicatedServer.cs`.
- **No** `ScenarioApplier`/`ISetupPhase`/`ScenePhaseRunner`/`ServerBootstrap`/`ModifierSystem` — later stories.

### Project Context Rules
_Extracted from `_bmad-output/project-context.md` + `game-architecture.md` — these govern every edit here:_
- **The sim/presentation boundary is sacred.** `src/Core/Sim/` (host + seam) is sim: **no `using Godot;`, no Node, no `GD.Print`, no `float` gameplay state.** Presentation (`GodotLogSink`, `MainScene`, `StressTest`) owns all Godot. Data flows sim→presentation; presentation reads host stores each frame and sends commands back. [Source: project-context.md "The One Architectural Rule"]
- **Determinism:** the 9-system **registration order IS the contract** — preserve it byte-identical and process entities ascending-id. No wall-clock, no `System.Random`, `Fixed` (16.16) only; `Fixed.FromFloat` is load-time only (none added here). [Source: project-context.md "Determinism"; architecture.md §2]
- **Reuse existing systems; compose, don't subclass.** `SimulationHost` *composes* the existing `SimulationLoop` + the 9 existing systems + the existing stores (`EntityWorld`, `BuildingStore`, `ResourceStore`, …) — it builds **no** parallel subsystem. [Source: project-context.md "Data layout" / "composition over inheritance"]
- **Engine/runtime:** Godot 4.6.3 target, .NET 8 (`net8.0`); assembly/namespace `ProjectChimera.*` (`ProjectChimera.Core.Sim` for the new host); project files `godot.csproj`/`godot.sln`; Tier-1 `ProjectChimera.Sim.Tests` (xUnit 2.9.2, Godot-free). [Source: project-context.md "Technology Stack"]
- **Godot C# gotchas:** classes inheriting a Godot type are `partial` (only `GodotLogSink`/`StressTest` touch Godot here; `SimulationHost` inherits nothing). `#nullable enable` per file. Use `GD.Print` only on the presentation side (now behind `ILogSink`). [Source: project-context.md "Godot C# gotchas"]

### References
- [Source: epics.md#Story-1.8a (L640-654)] — story statement; the 2 ACs (Godot-free `SimulationHost` owns the 9-system order + reserved index-3 ModifierSystem before Combat + single checksum sink + byte-identical golden; net-new injected `ILogSink` + `StressTest.cs` relocation + Tier-1 Godot-free compile). Covers FR-39/FR-44/AR-6/AR-4; Depends on 1.7.
- [Source: epics.md (L486-492)] — Epic 1 overview + sequencing note (strangler steps; "1.1, 1.5, 1.7, 1.8 are genuinely net-new … SimulationHost/Applier"); the AR-4/AR-41 coverage note folding `ILogSink`+`StressTest` into 1.8a.
- [Source: epics.md AR-6 (L185), AR-4 (L181), AR-3 (L180), AR-7 (L186), AR-38 (L229), AR-36 (L227), AR-15 (L196)] — the canonical AR text 1.8a implements (AR-6/AR-4) and stays clear of (AR-7/AR-3/AR-38/AR-15 → 1.8b/1.8c/1.9a/Step-13).
- [Source: game-architecture.md §Step 6 (L1420-1814)] — "Shrinking Composition Root + Sim-Spine Strangler"; sub-decision 2 (SimulationHost, L1486-1494: single owner of the 9-system order, ModifierSystem at index 3, `SetChecksumSink` kills the double-set, constructed by the test project + ServerBootstrap); sub-decision 9 (ILogSink, L1558-1561); sub-decision 11 (StressTest → `tools/`, L1569-1571); migration sequence (L1685-1746: Step 0 seam, **Step 1 = SimulationHost mechanical lift**, L1694-1696); target tree (`src/Core/Sim/`, L1608-1616).
- [Source: game-architecture.md §Implementation Patterns (L1850-2530)] — S-TEST-1 (L2453-2460: `SimulationHost.Create(matchSeed)`, `SetChecksumSink((uint,uint))`, `StepOnce()`, exact-`uint` golden equality); N3 (L2049-2074: ModifierSystem before CombatSystem rationale); consistency table (L2477: "New ISimSystem … registered in SimulationHost at its contractual slot … test: SystemOrderTest"; L2504: "Sim logging | ILogSink injected; never GD.Print/Console"); deferred fork (L2523: ILogSink `Debug/Info/Warn` with `Fixed.Raw` args).
- [Source: architecture.md §4 (L69-89)] — the authoritative as-built 9-system order + 30 Hz tick + `EnableChecksums`/`OnChecksum` description.
- [Source: SimulationLoop.cs:8-147] · [MainScene.cs:236-296, 428-457, ~1879] · [SimChecksum.cs:37-89] · [GoldenScenario.cs:84-130] · [MultiFactionScenario.cs:78-87] · [GoldenChecksumReplay.cs:31,51-66] · [ProjectChimera.Sim.Tests.csproj:13-57] · [GodotFreeBoundaryTest.cs] · [StressTest.cs; scenes/stress_test.tscn:3] · [CombatSystem.cs:34-35; ProjectileSystem.cs:27-28; DamageTable.cs:47] — the verified current-state citations behind every Pre-flight fact above (located by symbol; doc line numbers drifted — file is 2,234 LOC).

---

## Dev Agent Record

### Agent Model Used

Claude Opus 4.8 (`claude-opus-4-8`)

### Debug Log References

- **Tier-1 suite (authoritative regression gate):** `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` → **142/142 passed** after every milestone (host+seam+test added; goldens switched; StressTest relocated). Includes the new `SystemOrderTest` (×2), `GodotFreeBoundaryTest`, `GoldenChecksumReplayTests`, `MultiFactionGoldenTests`.
- **Goldens byte-identical:** `git status --porcelain` on both `*.golden.txt` → empty (never re-recorded; `CHIMERA_GOLDEN_RECORD` never set). `CompareSequences` green proves the host construction equals the former inline construction.
- **Protected files untouched:** `git diff --stat` on `SimulationLoop.cs`, `SimChecksum.cs`, and the 9 system bodies → empty.
- **Full game build:** `dotnet build godot/godot.csproj` → **Build succeeded, 0 Errors, 7 Warnings** (all pre-existing CS8632 nullable-context; none from new files).
- **Godot-free boundary:** grep of `src/Core/Sim/*.cs` finds `GD.`/`Console` only inside doc-comments (no code); `using Godot;` in `src/Core` → only `MainScene.cs` (AC4).
- **In-engine smoke (Godot 4.6.3):** `main.tscn` boots to the main menu with **zero editor/runtime errors** → MainScene `_Ready` host wiring (construct `_host`, alias 10 fields, single `SetChecksumSink`, `SetupMultiplayer`) runs clean. `stress_test.tscn` runs the relocated `tools/StressTest.cs`: **837 units (P1 401 / P2 436), Sim Tick 235, 94 FPS** → relocation + the `MultiMeshBridge` raw-loop overload validated.

### Completion Notes List

Behavior-preserving extraction (migration Step 1, AR-6 + AR-4). Both goldens stayed **byte-identical** — the extraction changed zero sim ticks.

- **`SimulationHost` (`src/Core/Sim/`, Godot-free)** *composes* the unchanged `SimulationLoop`; owns the stores, the canonical 9-system order, `EnableChecksums`, and the single sink. `SimulationLoop.cs` / `SimChecksum.cs` / the 9 system bodies / `ISimSystem` are byte-for-byte unchanged (verified empty diff).
- **9-system order + reserved slot:** registered exactly `[Building, Gathering, Movement, Combat, Projectile, Supply, FogOfWar, AiOpponent, ScenarioDirector]`; a `// SimulationHost contract: ModifierSystem inserts HERE (index 3, before CombatSystem) — Epic 2` comment reserves the slot (NOT built). `SystemOrderTest` fails on any reorder/add/remove and pins Combat-preceded-by-Movement.
- **Single checksum sink (D5):** MainScene's two `OnChecksum` assignments (inline `_Ready` + `SetupMultiplayer` overwrite) collapsed into one `_host.SetChecksumSink((tick,checksum)=>{ _logSink.Info(...); if (_lockstep.IsOnline) _lockstep.SendChecksum(...); })`. Offline→log, online→log+send — behaviourally identical to the old two-sink scheme. `OnDesync` rerouted to `_logSink.Warn`.
- **`ILogSink` seam (AC3):** `ILogSink` + `NullLogSink` (Godot-free, `src/Core/Sim/`) + `GodotLogSink` (`src/UI/`, `GD.Print`/`GD.PrintErr`). Goldens/tests inject `NullLogSink.Instance`; MainScene injects `new GodotLogSink()`. A single one-shot host `Info` exercises the seam (NullLogSink no-ops it → zero golden impact).
- **`DamageTable`/`FactionRegistry` fidelity (D4):** `Create` takes `DamageTable? = null` (→ `DamageTable.Default`, equal to the goldens' 3-arg combat ctors) and the caller's `FactionRegistry` (goldens/MainScene pass `(2)`, the multifaction golden passes `(4)`). Default-seeded `new EntityWorld()` preserved (no seed plumbing).
- **StressTest relocation (AC4):** `git mv src/Core/StressTest.cs → tools/StressTest.cs` (content unchanged — namespace kept `ProjectChimera.Core` for a pure file move, sanctioned by the story; nothing references it by namespace, the scene binds by path). Scene script repointed to `res://tools/StressTest.cs`; the Tier-1 `<Compile Remove>` deleted; `godot.csproj`'s SDK default glob compiles `tools/` (confirmed). `src/Core` now has only `MainScene.cs` using Godot.
- **Off-script discovery — `MultiMeshBridge` took the raw `SimulationLoop` by argument** (two *bare* `_simLoop` sites the original `_simLoop.` dot-grep missed). StressTest builds a custom **3-system** loop, so it cannot use the host. Resolved by making the bridge capture a stable `World` + a live alpha source (`() => host.InterpolationAlpha`), exposing a `SimulationHost` overload (MainScene) **and keeping** the raw-`SimulationLoop` overload (StressTest). This keeps `host._loop` **private** (honoring AC2's single-owner intent — no `host.Loop` leak) and leaves StressTest a pure relocation. Both paths validated in-engine.
- **Smoke gap (non-blocking):** the in-skirmish `[Checksum]` console line was not visually captured because the main menu is mouse-only and the harness has no absolute mouse-click. That path is covered by the golden suite (`host.StepOnce()` + `SetChecksumSink` over 300 ticks, byte-identical) and the clean `_Ready` sink wiring at boot.

### File List

**New (sim, Godot-free):**
- `godot/src/Core/Sim/ILogSink.cs`
- `godot/src/Core/Sim/NullLogSink.cs`
- `godot/src/Core/Sim/SimulationHost.cs`

**New (presentation):**
- `godot/src/UI/GodotLogSink.cs`

**New (Tier-1 test):**
- `godot/ProjectChimera.Sim.Tests/Sim/SystemOrderTest.cs`

**Moved:**
- `godot/src/Core/StressTest.cs` → `godot/tools/StressTest.cs`

**Modified:**
- `godot/src/Core/MainScene.cs` — `+using ProjectChimera.Core.Sim`; `_simLoop` field → `_host` (+ `_logSink`); `_matchStats` `readonly`→alias; construct `_host` + alias 10 stores/systems; `_simLoop.*`→`_host.*` (tick paths, HUD, bridge init); single `SetChecksumSink`; `SetupMultiplayer` OnChecksum overwrite removed, `OnDesync`→`_logSink.Warn`.
- `godot/src/UI/MultiMeshBridge.cs` — render source captures `World` + alpha; `SimulationHost` overload (MainScene) + raw-`SimulationLoop` overload (StressTest).
- `godot/ProjectChimera.Sim.Tests/Golden/GoldenScenario.cs` — `GoldenHarness` holds `Host`; `Build()` via `SimulationHost.Create`.
- `godot/ProjectChimera.Sim.Tests/Golden/MultiFactionScenario.cs` — `Build()` via `SimulationHost.Create` with `FactionRegistry(4)`.
- `godot/ProjectChimera.Sim.Tests/Golden/GoldenChecksumReplay.cs` — `RunAndRecord` uses `harness.Host.SetChecksumSink(...)` + `harness.Host.StepOnce()`.
- `godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` — removed the `StressTest.cs` `<Compile Remove>` (now in `tools/`).
- `godot/scenes/stress_test.tscn` — script path → `res://tools/StressTest.cs`.
- `_bmad-output/implementation-artifacts/sprint-status.yaml` — 1.8a `ready-for-dev` → `in-progress` → `review`.

**Unchanged (verified byte-identical):** both `*.golden.txt`; `SimulationLoop.cs`; `SimChecksum.cs`; `ISimSystem`; all 9 system bodies.

### Change Log

- **2026-06-24 — Story 1.8a implemented (Status → review).** Extracted a Godot-free `SimulationHost` (canonical 9-system order, reserved index-3 `ModifierSystem` slot, single `SetChecksumSink`) wrapping the unchanged `SimulationLoop`; added the net-new `ILogSink`/`NullLogSink`/`GodotLogSink` seam; switched both golden harnesses + MainScene to construct the host and consolidated MainScene's double checksum-set into one sink; relocated `StressTest.cs` to `tools/`; rerouted `MultiMeshBridge` through the host (raw-loop overload retained for StressTest). Both goldens byte-identical; Tier-1 142/142; `godot.csproj` green; in-engine boot + stress-test smoke clean.
