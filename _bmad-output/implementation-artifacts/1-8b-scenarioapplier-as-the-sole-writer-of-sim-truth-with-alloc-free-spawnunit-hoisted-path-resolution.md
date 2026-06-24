---
baseline_commit: 73dbff7
---

# Story 1.8b: ScenarioApplier as the sole writer of sim truth with alloc-free SpawnUnit + hoisted path resolution

Status: done

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->

## Story

As a solo developer decomposing the 2,349-LOC MainScene god-object,
I want a net-new Godot-free `ScenarioApplier` as the sole writer of sim truth, with allocation-free `SpawnUnit` and all Godot path resolution hoisted to a presentation pre-pass that runs before the Applier,
so that the sim spine has one auditable mutation path that is testable headless and reused verbatim by the server.

> **This is migration Step 2 + Step 3 of the "Shrinking Composition Root + Sim-Spine Strangler" (`game-architecture.md` §Step 6, sub-decision 3; AR-7), built on the 1.8a `SimulationHost`.** It is the **sim-mutating** seam, so it keeps the byte-identical-golden requirement. **Five facts from the codebase reshape the architecture text and are settled below:** (1) **There is no `EntityWorld.SpawnUnit`** — the sim primitive is `EntityWorld.Create(...)` (already 100% alloc-free); the thing the story calls "SpawnUnit" is the **caller wrapper `MainScene.SpawnScenarioUnit`** (`MainScene.cs:625-654`), which this story relocates as `ScenarioApplier.SpawnUnit`. (2) **The four extraction targets are already ~99% Godot-free in their bodies** — they write pure-C# sim stores via `Fixed.FromFloat`; the ONLY Godot path-resolution to hoist is the **per-slot faction JSON** (`ProjectSettings.GlobalizePath` at `MainScene.cs:572`). Mesh/GLB resolution is **already** hoisted (`SetupFactionVisuals`→`MeshLoader.LoadFromGlb`) — leave it alone. (3) **The as-built `ScenarioData` is `float`-typed**; the architecture's "applier does no `Fixed.FromFloat`" presumes a `Fixed`-typed model that does not exist yet — so **1.8b preserves the as-built `Fixed.FromFloat` conversions inside the applier** (behavior-preserving), and "ScenarioData → Fixed end-to-end" is explicitly deferred. (4) The architecture's `MainScene.cs:NNN` citations are **drifted** (doc snapshot ~2,234 LOC; live file is **2,349**) — **locate every seam by symbol, never by the doc's literal line number.** (5) **The applier consumes `Validated<ScenarioData>`** (Validated.cs names 1.8b as the consumer) — and making that coexist with 1.7's shadow-mode "apply-on-fail" is the one place 1.8b touches 1.7 code (settled in D3).

## Acceptance Criteria

1. **(ScenarioApplier is the net-new Godot-free sole writer of sim truth)** **Given** the scenario-mutation code currently inline in `MainScene` (`ApplyScenario`, `SpawnScenarioUnit`, `ParseBuildingType`, `ApplyFallbackScenario`, and the sim-half `FactionBase` write in `MoveStartPosition`) **When** it is extracted into a net-new `ScenarioApplier` (`src/Core/Sim/ScenarioApplier.cs`) **Then** `ScenarioApplier` has **no `using Godot`** / no Godot type / no `GD.*` / no `ProjectSettings` / no `res://`, writes sim truth **only** through the 1.8a host's stores (`World`/`Nodes`/`Resources`/`Buildings`/`BuildSys`/`ScenarioDirector`), exposes `Apply(Validated<ScenarioData>)` + `ApplyFallback()` + `SpawnUnit(UnitDefinition, Faction, float, float)` + `SetFactionBase(Faction, FixedVec3)` + `static ParseBuildingType(string)` (the per-slot `FactionDefinition?[]` is constructor-injected — see D1/D4), and compiles + runs in the Godot-free `ProjectChimera.Sim.Tests` project (`GodotFreeBoundaryTest` green).

2. **(Godot path resolution hoisted to a presentation pre-pass that runs BEFORE the Applier)** **Given** the per-slot faction-JSON resolution that is inline in `ApplyScenario` today (`ProjectSettings.GlobalizePath(slot.FactionJson)` + `File.Exists` + `FactionDefinition.LoadFromFile`, located by symbol around `MainScene.cs:572-575`) **When** a scenario is applied **Then** that resolution happens in a **presentation** resolver (a small `MainScene` helper) that produces an already-loaded `FactionDefinition?[] slotFactionDefs` **before** `ScenarioApplier` runs, the Applier receives the pre-loaded defs (no path work inside it), and the existing mesh/GLB resolution (`SetupFactionVisuals`→`MultiMeshBridge.Initialize`→`MeshLoader.LoadFromGlb`) is **unchanged** (it is already hoisted; the sim stores only the pure-`int` `EntityWorld.MeshType` index).

3. **(`SpawnUnit` allocates zero per call)** **Given** `ScenarioApplier.SpawnUnit(def, faction, x, z)` **When** it is called repeatedly after a JIT warm-up **Then** a Tier-1 test asserts `GC.GetAllocatedBytesForCurrentThread()` delta is **0** across the loop, the method body contains no LINQ / closure / `params` / boxing / string allocation (the failure-path `GD.PrintErr` is replaced by `ILogSink.Warn` and is off the hot path), and this **same** primitive is the single spawn path used by `Apply`, `ApplyFallback`, **and** the `ScenarioDirector.OnSpawnUnit` trigger delegate.

4. **(The Applier consumes only `Validated<ScenarioData>`)** **Given** the 1.7 `ScenarioValidator` gate **When** a scenario is applied on any path (file, AI-generated, fallback) **Then** `ScenarioApplier.Apply` takes a `Validated<ScenarioData>` (reading `.Value`) — a raw `ScenarioData` cannot reach a store — presentation produces the token via `ScenarioValidator.Validate` and applies under the existing `ScenarioGate` shadow/fail-closed decision, and 1.7's **shadow-mode "apply-on-fail" behavior is preserved byte-for-byte** (per D3).

5. **(Byte-identical regression gate + new applier proof — and Godot-free compile)** **Given** the extraction is behavior-preserving **When** the full Tier-1 suite runs **Then** the two existing goldens `golden-scenario.golden.txt` and `golden-multifaction.golden.txt` are **byte-identical / UNCHANGED** (`git status` clean on both), a **new** Godot-free `ScenarioApplierTests` asserts the Applier writes **identical store contents** from a known `Validated<ScenarioData>` (entities/buildings/nodes/ore/bases) plus a stable start-state hash baseline, `dotnet build godot/godot.csproj` is green, and `dotnet test` is all-green. **If an existing golden moves, behavior leaked — fix the extraction; NEVER set `CHIMERA_GOLDEN_RECORD` for the existing two.**

6. **(MainScene routes ALL scenario mutation through the Applier — sets up 1.8c's assertion)** **Given** `MainScene` after 1.8b **When** it loads/applies a scenario (`LoadAndApplyScenario` file + AI-generated paths, fallback) or moves a start position (`MoveStartPosition`) **Then** every sim-truth write flows through `_applier` (`Apply`/`ApplyFallback`/`SpawnUnit`/`SetFactionBase`), the `OnSpawnUnit` trigger delegate calls `_applier.SpawnUnit`, and the four former methods no longer write sim stores directly from `MainScene`. (1.8c later *asserts* this exclusivity via the MainScene diff — 1.8b *establishes* it.)

_Covers: FR-39 (LAN determinism / desync-free MP — the single auditable mutation path the server reuses), FR-44 (deterministic-sim test coverage — Godot-free `ScenarioApplierTests`), AR-7 (net-new `ScenarioApplier`: sole writer of sim truth; Godot path resolution hoisted to a presentation pre-pass; `SpawnUnit` allocation-free). Depends on: 1.8a (DONE — `SimulationHost` + `ILogSink` shipped) and 1.7 (DONE — `ScenarioValidator`/`Validated<T>`/`ScenarioGate` shipped; 1.8b is the story that type-gates the applier to `Validated<ScenarioData>`, the consumption 1.7 explicitly deferred). Independent of 1.8c (ISetupPhase/ScenePhaseRunner/strangle-diff) and 1.9a (ServerBootstrap) — this story builds the applier they consume._

> Split from former 1.8. Net-new `ScenarioApplier` (AR-7), built on the 1.8a `SimulationHost`. The sim-mutating change for this seam ⇒ byte-identical-golden requirement holds. Establishes the single sim-truth writer that 1.8c's MainScene diff asserts mutation flows exclusively through. **`ModifierStore`/`SimWorld`/`ServerBootstrap` are NOT built here.**

---

## Developer Context

**You (the dev agent) have ONLY this file. Read this whole section before editing anything.** The work is **one net-new Godot-free class (`ScenarioApplier`)** that absorbs `MainScene`'s scenario-mutation methods **nearly verbatim** (they are already pure-C# in their bodies); a **presentation faction-resolution pre-pass** (the one Godot path-resolution site that must stay in `MainScene`); **re-pointing `MainScene` + the `OnSpawnUnit` trigger delegate** at the applier; a **type-gate** so the applier consumes `Validated<ScenarioData>`; a **new Godot-free `ScenarioApplierTests`**; and a **zero-alloc `SpawnUnit` assert**. The applier lives in `src/Core/Sim/` (Godot-free, **auto-globbed** into Tier-1 — no `.csproj` edit). The two existing byte-identical goldens are the regression safety net; a new applier-path test is the forward proof.

### The traps (each has bitten a prior story or is the architecture's stated failure mode)

1. **Moving an existing golden.** 1.8b is a **behavior-preserving extraction** — it must change **zero** sim ticks for the existing scenarios. The two goldens (`golden-scenario.golden.txt`, `golden-multifaction.golden.txt`) are `<EmbeddedResource>` and must stay **byte-identical**. **`CHIMERA_GOLDEN_RECORD` must NEVER be set for them.** If one moves, you changed behavior during the lift (a changed apply order, a dropped/added `Fixed.FromFloat`, a touched store/`Create`/`SimChecksum`) — **find it and fix it.** Note: the two existing goldens hand-populate via `world.Create` and do **NOT** exercise `ApplyScenario`/`SpawnScenarioUnit`, so they will only move if you touch a **shared** primitive (`EntityWorld.Create`, the stores, the 9 systems, `SimChecksum`). Leave those untouched and they stay green automatically. (AC5)
2. **Converting `ScenarioData` to `Fixed` end-to-end.** The architecture's `N6` reference impl shows `Apply(Validated<ScenarioModel>)` with already-`Fixed` fields and **no** `Fixed.FromFloat`. **That is the aspirational end-state, NOT 1.8b.** The as-built `ScenarioData` is **`float`-typed** (`StartOre`/`BaseX`/`BaseZ`/`X`/`Z`/`Supply`/`Rate` are `float`), and `ScenarioSerializer` does **not** route them through `FixedJsonConverter`. So 1.8b **keeps the as-built `Fixed.FromFloat(...)` conversions inside the applier** — relocating them verbatim. Do **NOT** change `ScenarioData`'s field types, `ScenarioSerializer`'s options, or add a `ScenarioModel`. (This contradiction was a tracked architecture finding; the as-built float model is the ground truth for 1.8b. See D2.)
3. **Re-resolving meshes / touching the render path.** All `res://`/GLB/`GD.Load` resolution is **already** isolated in `SetupFactionVisuals()`→`MultiMeshBridge.Initialize`→`MeshLoader.LoadFromGlb`, decoupled from the sim by the pure-`int` `EntityWorld.MeshType` (a `byte`, excluded from the checksum). The applier computes `MeshType` with `FactionDefinition.IndexOfUnit` (a list scan — no Godot). **Do NOT move, rewrite, or "hoist" any mesh/GLB code** — it is not in the spawn path. The only thing to hoist is **faction-JSON** resolution. (AC2)
4. **Inventing `SimWorld` / a host apply-method / `ModifierStore`.** 1.8a deliberately exposed stores as **properties on `SimulationHost`** (it did **not** build the architecture-tree's `SimWorld.cs`). The applier reads `host.World`/`host.Buildings`/etc. directly. Do **NOT** introduce `SimWorld`, do **NOT** add an `Apply` method onto `SimulationHost` itself, and do **NOT** build `ModifierStore`/`Energy`/`Mana` (Epic 2). `SimulationHost._loop` stays **private**. (D1)
5. **Breaking 1.7's shadow-mode "apply-on-fail" when you type-gate the applier.** The applier requires a `Validated<ScenarioData>`; but 1.7's `ScenarioGate` shadow mode (the default on master) **applies the model even when validation FAILS** (master never breaks). Those two facts collide: a shadow-fail still needs a token to call `Apply`. The settled fix (D3) is a **minimal, golden-neutral** change to 1.7's `ValidationResult` so it carries the minted token on Fail too. Do **NOT** instead silently change shadow mode to "skip-apply-on-fail" — that contradicts 1.7's shipped `ScenarioGate.ShouldProceed(ok:false, failClosed:false) == true`. (D3)
6. **Trusting the architecture's `MainScene.cs:NNN` numbers.** They drifted badly (doc snapshot ~2,234 LOC; live is **2,349**). Every line cite in "Pre-flight facts" was re-verified at `73dbff7` — but still locate seams by **symbol** (`ApplyScenario(`, `SpawnScenarioUnit(`, `ParseBuildingType(`, `ApplyFallbackScenario(`, `OnSpawnUnit`, `MoveStartPosition`, `ProjectSettings.GlobalizePath`).
7. **Over-reaching into 1.8c / 1.9a.** Do **NOT** build `ISetupPhase`/`ScenePhaseRunner`/`PhaseOrderTest`, do **NOT** do the final MainScene shrink-diff, do **NOT** build the full `ScenarioDelegateBinder`, and do **NOT** build `ServerBootstrap`. 1.8b builds the applier + routes mutation through it; 1.8c asserts exclusivity + shrinks MainScene; 1.9a reuses the applier headless. (D9)
8. **`Fixed.FromFloat` is the determinism-critical conversion — preserve it exactly.** Every spawn/scenario position + stat is `Fixed.FromFloat(theFloat)` with **Y = `Fixed.Zero`** (flat XZ ground). `Fixed.FromFloat(v) => new Fixed((int)(v * 65536))` — a **truncating** cast (`FixedPoint.cs:27`). The applier must reproduce each conversion **bit-for-bit** and in the **same order** (slots → nodes → buildings → units → `LoadScenario`). A reordered loop or a changed conversion is a behavior leak.

### The shape of the work (1 net-new applier + 1 presentation pre-pass + MainScene re-point + type-gate + 1 new test + 1 zero-alloc assert; existing goldens UNCHANGED)

1. **Net-new `ScenarioApplier`** — `src/Core/Sim/ScenarioApplier.cs` (Godot-free, namespace `ProjectChimera.Core.Sim`). Constructed with the 1.8a `SimulationHost` (so it reaches `host.World`/`Nodes`/`Resources`/`Buildings`/`BuildSys`/`ScenarioDirector`) + an injected `ILogSink` + the shared `FactionDefinition?[]` array (the pre-pass fills it). Methods: `Apply(Validated<ScenarioData>)`, `ApplyFallback()`, `int SpawnUnit(UnitDefinition def, Faction faction, float x, float z)`, `void SetFactionBase(Faction faction, FixedVec3 pos)`, `static BuildingType ParseBuildingType(string)`. Bodies are the verbatim `MainScene` logic minus the Godot path-resolution + `GD.*` (→ `ILogSink`). (AC1, AC4)
2. **Presentation faction-resolution pre-pass** — a small `MainScene` helper (e.g. `ResolveSlotFactionDefs(ScenarioData)`) that performs the `ProjectSettings.GlobalizePath` + `File.Exists` + `FactionDefinition.LoadFromFile` per slot and returns/populates `_slotFactionDefs` **before** the applier runs. Also ensures the **default** faction defs are resolved so `ApplyFallback`'s `GetUnitByCategory("Worker")` still works when the scenario file is missing. (AC2)
3. **Type-gate (D3)** — extend 1.7's `ValidationResult` to carry the minted `Validated<ScenarioData>` on Fail as well as Pass (golden-neutral), so shadow-mode apply-on-fail still has a token. `MainScene.ValidateBeforeApply` returns the `ValidationResult`; the caller applies `result.Value` when `ScenarioGate.ShouldProceed(result.Ok, ScenarioGate.IsFailClosed())`. (AC4)
4. **Re-point `MainScene`** — `LoadAndApplyScenario` (file + AI-gen branches) + the fallback branch run the pre-pass then call `_applier.Apply(validated)` / `_applier.ApplyFallback()`; `OnSpawnUnit` trigger delegate calls `_applier.SpawnUnit`; `MoveStartPosition`'s sim-half `FactionBase` write calls `_applier.SetFactionBase`. Delete the four now-empty former methods from `MainScene` (their bodies moved). (AC6)
5. **New Godot-free `ScenarioApplierTests`** — `ProjectChimera.Sim.Tests/Builder/ScenarioApplierTests.cs`: build a known `ScenarioData` in code, validate → `Validated<ScenarioData>`, apply through `ScenarioApplier` into a fresh `SimulationHost`, assert **identical store contents** + a stable start-state (canonical-model) hash baseline. (AC5)
6. **Zero-alloc `SpawnUnit` assert** — a Tier-1 test (in `ScenarioApplierTests` or `Determinism/ZeroAllocInTickTests.cs`) that warms up then asserts `GC.GetAllocatedBytesForCurrentThread()` delta == 0 over a `SpawnUnit` loop. (AC3)
7. **Prove AC5** — full Tier-1 suite green; both existing `.golden.txt` `git`-clean; `GodotFreeBoundaryTest` green; `dotnet build godot/godot.csproj` green; in-engine smoke (alpha_map_01 loads + plays identically).

### Key design decisions (settled here — do NOT re-derive)

**D1 — `ScenarioApplier` *composes* the 1.8a `SimulationHost`; it does not subclass/modify it, and `SimWorld` is NOT introduced.** The applier holds a `SimulationHost _host` + an `ILogSink _log` + the shared `FactionDefinition?[] _slotFactionDefs` (constructor-injected — the same array `MainScene` owns; the pre-pass fills it in place — D4). It reads sim truth via `host.World`/`host.Nodes`/`host.Resources`/`host.Buildings`/`host.BuildSys`/`host.ScenarioDirector` (the exact six targets `ApplyScenario` writes today — all already exposed by 1.8a). `SimulationHost._loop` stays **private**; do **not** add an apply-method to the host. _(Rejected: the architecture tree's `SimWorld.cs` "aggregate store" — 1.8a chose host-properties and shipped that way; a new `SimWorld` would churn 1.8a's surface for zero behavior gain.)_ Namespace `ProjectChimera.Core.Sim`; file `src/Core/Sim/ScenarioApplier.cs` (auto-globbed into Tier-1 — no `.csproj` edit, same as 1.8a's host).

**D2 — Behavior-preserving VERBATIM extraction; keep `Fixed.FromFloat` (as-built `ScenarioData` is `float`).** The architecture's `N6` example (`game-architecture.md:2236`) shows `Apply` with already-`Fixed` model fields and **no conversion** — but that presumes a `Fixed`-typed `ScenarioData` that **does not exist**. The as-built `ScenarioData` fields are `float` (verified — see Pre-flight facts), and conversion happens at apply time via `Fixed.FromFloat`. **1.8b relocates those conversions verbatim into the applier.** Do **NOT** change `ScenarioData` field types, `ScenarioSerializer`, or add a `ScenarioModel`; "Fixed end-to-end ScenarioData" is a separate, later, hash-changing migration. The conversion sites to preserve bit-for-bit (all `float→Fixed`, **Y = `Fixed.Zero`**): slot `StartOre`/`BaseX`/`BaseZ`; node `X`/`Z`/`Supply`/`Rate`; building `X`/`Z`; unit `x`/`z` + `def.Hp`/`Speed`/`VisionRange`/`AttackRange`/`AttackDamage`/`AttackSpeed`/`SplashRadius`; worker `CarryCapacity = Fixed.FromFloat(20f)`. _(The AR-36 analyzer that would ban `FromFloat` in the applier is advisory-on-master and not built; it does not block 1.8b.)_

**D3 — The applier consumes `Validated<ScenarioData>`; to keep 1.7 shadow-mode apply-on-fail, `ValidationResult` carries the token on Fail too.** This is the one place 1.8b touches 1.7 code, and it is **golden-neutral** (no scenario behavior changes; the same model is applied in shadow mode). Concretely:
- `ScenarioApplier.Apply(Validated<ScenarioData> v)` reads the model via **`v.Value`** (the as-built property name — **NOT** the architecture's illustrative `.Model`); the per-slot defs come from the constructor-injected `_slotFactionDefs` (D1/D4).
- Change `ValidationResult.Fail` to also store the minted `Validated<ScenarioData>` (wrap the model). The validator already holds a reusable `_proof`; mint `new Validated<ScenarioData>(m, _proof)` for the wrapped value. (The single `m is null` early-return may keep `default` — that path routes to the fallback, never to `Apply`; see below.)
- `MainScene.ValidateBeforeApply(model, label)` returns the `ValidationResult` (instead of `bool`); the caller does: `if (ScenarioGate.ShouldProceed(r.Ok, ScenarioGate.IsFailClosed())) _applier.Apply(r.Value, slotFactionDefs);`. Shadow (`failClosed:false`) → proceeds even when `r.Ok == false`, applying `r.Value` — **identical to today's "validate, log, apply the model anyway."** Fail-closed → skips. Keep the existing located-error `Info`/`Warn` logging (now via `ILogSink`/`GD.*` on the presentation side).
- **Null/fallback routing is unchanged:** `LoadAndApplyScenario` already sends a null parsed scenario to `ApplyFallback` (never to `Apply`), so the applier never receives a null-model token.
- **Verify-in-code:** the 1.7 `NegativeValidationTests` may assert `ValidationResult.Fail(...).Value == default`. If so, update those expectations to the new "Fail carries the wrapped model, `Ok == false`" contract. The `ValidatedSoleMinterTest` source-scan must stay green (the only `new Validated<` remains inside `ScenarioValidator.cs`).

**D4 — Hoist ONLY faction-JSON resolution to a presentation pre-pass; the applier carries zero path work.** Today `ApplyScenario`'s slot loop does, inline: `ProjectSettings.GlobalizePath(slot.FactionJson)` → `File.Exists` → `FactionDefinition.LoadFromFile(abs)` → `_slotFactionDefs[(int)faction] = def` → `_buildSys.SetFactionDef(faction, def)`. Split it:
- **Presentation pre-pass (`MainScene`):** resolve each slot's `res://` faction path → load the `FactionDefinition` → populate `_slotFactionDefs[]`. (This keeps the only `ProjectSettings.GlobalizePath` in presentation.) `MainScene` keeps `_slotFactionDefs` because the runtime `OnSpawnUnit` trigger delegate reads it.
- **Applier (Godot-free):** receives `FactionDefinition?[] slotFactionDefs` and does the **sim** parts — `_buildSys.SetFactionDef(faction, def)` (BuildingSystem is sim), ore, base, nodes, buildings, units (unit lookup + `MeshType` via the pre-loaded def), `ScenarioDirector.LoadScenario`.
- **Mesh/GLB resolution is untouched** — it already runs post-apply in `SetupFactionVisuals`→`MeshLoader.LoadFromGlb`, keyed off the pure-`int` `MeshType`. Do not move it.

**D5 — `SpawnUnit` is the single alloc-free spawn primitive, shared by `Apply`, `ApplyFallback`, and the trigger delegate.** Move `MainScene.SpawnScenarioUnit` verbatim as `ScenarioApplier.SpawnUnit(UnitDefinition def, Faction faction, float x, float z)` (return `int id` for testability/parity). It is **already alloc-free** in its body: `new FixedVec3(...)`/`Fixed.FromFloat(...)` are value-type structs (no heap), the `string.Equals(def.Category, "Worker", OrdinalIgnoreCase)` does not allocate, and the `FactionDefinition.IndexOfUnit(def.Id)` lookup is a `for` loop over a `List<UnitDefinition>` with ordinal `string ==` (**verified alloc-free** — no LINQ, no dictionary build, struct `List` enumerator). The only allocation in the area is the **failure-path** `GD.PrintErr($"...")` in the trigger delegate — move it to `ILogSink.Warn` (off the hot path). Re-point `ScenarioDirector.OnSpawnUnit` (currently a `MainScene` lambda calling `SpawnScenarioUnit`) to call `_applier.SpawnUnit`. Prove zero-alloc with `GC.GetAllocatedBytesForCurrentThread()` (warm up once to JIT, then measure a loop). _(Note: `EntityWorld.Create`/`BuildingStore.Create`/`ResourceNodeStore.Create` are **already** fixed-capacity + alloc-free — 4096/64/64, never grow, return `-1` when full. There is no store-level allocation to remove; the alloc-free win is keeping the relocated wrapper clean.)_

**D6 — `SetFactionBase` unifies the two `FactionBase` write sites — this *is* "sole writer."** Both `ApplyScenario`'s slot loop (`_resources.FactionBase[(int)faction] = new FixedVec3(...)`) and the editor's `MoveStartPosition` (the sim-half write) route through `ScenarioApplier.SetFactionBase(Faction, FixedVec3)`. After 1.8b, **no `MainScene` code writes `_resources.FactionBase` directly** — it calls the applier. This is the invariant 1.8c's MainScene diff asserts ("sim mutation flows exclusively through ScenarioApplier"). Apply the same discipline to every other store write the four methods did (`AddOre`, `_nodes.Create`, `_buildSys.PlaceBuildingDirect`, `_world.*`, `_scenarioDirector.LoadScenario`): they now live **inside** the applier, reached only via `Apply`/`ApplyFallback`/`SpawnUnit`/`SetFactionBase`.

**D7 — Two existing goldens stay byte-identical (regression gate); the forward proof is a new Godot-free `ScenarioApplierTests`.** The architecture's Step 3 mandate: *"Add `ScenarioApplierTests` (Godot-free, asserts identical store contents + a start-state hash baseline the instant it compiles). Golden checksum identical."* So:
- **Keep `golden-scenario.golden.txt` + `golden-multifaction.golden.txt` UNCHANGED** (they hand-populate via `world.Create`, so they only move if you touch a shared primitive — don't). `git status` clean on both is part of AC5.
- **New `ScenarioApplierTests` (store-contents proof):** build a known `ScenarioData` in code (mirror `alpha_map_01`: 2 slots, ore 200 each, the resource nodes, 2 pre-built `CommandCenter`s, and worker units — with an in-code `FactionDefinition` carrying a `"worker"` `UnitDefinition` so `GetUnit`/`IndexOfUnit` resolve). `Validate` → `Validated<ScenarioData>` → `applier.Apply(validated, slotFactionDefs)` → assert exact store contents: `World.AliveCount`, each unit's `Position`(= `Fixed.FromFloat` of the JSON x/z, Y=0)/`Faction`/`Health`/`GatherState`/`MeshType`, `Buildings.Count`/`Type`/`Position`/`ConstructionTimer==0` for pre-built, `Nodes` count/positions/supply/rate, `Resources.Ore` per faction, `FactionBase` per faction. Plus assert a **stable canonical start-state hash** baseline of the model (1.7's `CanonicalModelHash`).
- **Optional (realizes the harness's own `TODO(1.8b)`):** add an applier-driven golden replay via the existing `GoldenChecksumReplay` engine (a `build:` delegate that constructs a host, applies the in-code `Validated<ScenarioData>` via the applier, and steps N ticks), recording a **new** `golden-applier-scenario.golden.txt` **once**. **This is the ONLY sanctioned `CHIMERA_GOLDEN_RECORD` use in 1.8b, and it applies to the NEW file only** — never the existing two.

**D8 — Folder + namespaces (auto-globbed; no `.csproj` edit for the applier).** `ScenarioApplier.cs` → `src/Core/Sim/` (namespace `ProjectChimera.Core.Sim`); the Tier-1 csproj already globs `..\src\Core\**\*.cs`, so it **auto-compiles into the Godot-free test project** — which is exactly what AC1's "compiles in the Godot-free test project" needs. The test → `ProjectChimera.Sim.Tests/Builder/ScenarioApplierTests.cs` (architecture's named home). `ScenarioData`/`UnitDefinition`/`FactionDefinition`/`Validated<T>`/`ScenarioValidator` are all in `ProjectChimera.Core.Definitions` (already Godot-free — verified) so the applier's `using ProjectChimera.Core.Definitions;` keeps the boundary green (`GodotFreeBoundaryTest`).

**D9 — Scope boundary vs 1.8c / 1.9a / Epic 2.** 1.8b builds the applier + the pre-pass + the type-gate + routes mutation through it. It does **NOT**: build `ISetupPhase`/`ScenePhaseRunner`/`PhaseOrderTest` or do the final MainScene shrink-diff (**1.8c**); build the full `ScenarioDelegateBinder` (1.8c — 1.8b does only the minimal `OnSpawnUnit` re-point forced by moving `SpawnScenarioUnit`); build `ServerBootstrap` (**1.9a** — just leave the applier presentation-free so 1.9a reuses it); build `ModifierStore`/`Energy`/`Mana`/`SimWorld` (**Epic 2 / not chosen**); generalize `FactionRegistry`/widen `SimChecksum` (Steps 11/13). **Optional, behavior-identical:** replacing the inline `(Faction)(slot.Slot + 1)` casts with the existing `FactionRegistry.ToFaction(slot)` helper (the validator already references it) — do this only if it stays byte-identical; if in any doubt, keep the as-built cast.

### Pre-flight facts you MUST NOT re-derive (verified against the codebase at `73dbff7`)

> **Line numbers drifted** (architecture doc snapshot ~2,234 LOC; live `MainScene.cs` is **2,349**). Cites below are live @ `73dbff7` but **locate by symbol**.

- **The four extraction targets** live in `MainScene.cs:553-724` and are **already pure-C# in their bodies** (only Godot couplings: `ProjectSettings.GlobalizePath` at `:572`, and `GD.PrintErr` diagnostics). [Source: MainScene.cs, recon]
  - **`ApplyScenario(ScenarioData scenario)`** (`:557-622`): validate-gate (`:561` `if (!ValidateBeforeApply(scenario, "ApplyScenario")) return;`), then in order — **slots** (`:565-584`: per-slot `ProjectSettings.GlobalizePath(slot.FactionJson)` `:572` + `File.Exists` + `FactionDefinition.LoadFromFile` `:575` + `_slotFactionDefs[(int)faction]=def` + `_buildSys.SetFactionDef` `:577`; `_resources.AddOre(faction, Fixed.FromFloat(slot.StartOre))` `:581`; `_resources.FactionBase[(int)faction]=new FixedVec3(Fixed.FromFloat(slot.BaseX),Fixed.Zero,Fixed.FromFloat(slot.BaseZ))` `:582-583`), **resource nodes** (`:587-592`: `_nodes.Create(pos, Fixed.FromFloat(node.Supply), Fixed.FromFloat(node.Rate), node.MaxGatherers)`), **buildings** (`:595-601`: `_buildSys.PlaceBuildingDirect(ParseBuildingType(b.Type), faction, pos, b.PreBuilt)`), **units** (`:604-618`: `factionDef.GetUnit(u.UnitId)`; `GD.PrintErr` + `continue` on unknown id `:614`; `SpawnScenarioUnit(def, faction, u.X, u.Z)` `:617`), **triggers** (`:621`: `_scenarioDirector.LoadScenario(scenario)`).
  - **`SpawnScenarioUnit(UnitDefinition def, Faction faction, float x, float z)`** (`:625-654`): `new FixedVec3(Fixed.FromFloat(x),Fixed.Zero,Fixed.FromFloat(z))`; `_world.Create(pos, faction, Fixed.FromFloat(def.Hp), Fixed.FromFloat(def.Speed))`; `if (id < 0) return;`; 8 SoA stat writes (`VisionRange`/`AttackRange`/`AttackDamage`/`AttackSpeed`/`DamageTypeOf`/`ArmorTypeOf`/`SplashRadius`/`SupplyCost`); `MeshType` via `fdef?.IndexOfUnit(def.Id) ?? -1` then `(byte)(meshType<0?0:meshType)`; if `Worker` → `GatherState.Idle` + `CarryCapacity = Fixed.FromFloat(20f)`. **No Godot, no `res://`, no `Vector3`, no allocation.**
  - **`ParseBuildingType(string type)`** (`:657-663`): already `private static`, a pure `switch` expression (`"Barracks"`/`"ArcheryRange"`/`"SiegeWorkshop"` → enum, default `CommandCenter`). Move verbatim.
  - **`ApplyFallbackScenario()`** (`:669-724`): builds `_fallbackMirror = BuildFallbackMirror()` (`:676`) + `ValidateBeforeApply(_fallbackMirror, "fallback")` (`:677`, shadow-only — result intentionally ignored, it's the always-applied safety net), then hardcoded literals: 2 bases, 200 ore each, 8 nodes (a `new (float,float,float)[]` tuple array), 2 `CommandCenter`s via `PlaceBuildingDirect`, 2 workers/faction via `SpawnScenarioUnit` using `_slotFactionDefs[...]?.GetUnitByCategory("Worker")`. **Deliberately does NOT call `LoadScenario`** (comment `:673`: rerouting through `ApplyScenario` would newly fire `match_start` triggers). Keep that split.
- **Call sites** — all in `LoadAndApplyScenario()` (`:507-537`), itself called once from `_Ready()` at `:317`. AI-generated branch: `ApplyScenario(generated)` `:515`. File branch: `if (scenario==null) ApplyFallbackScenario() :527; else ApplyScenario(scenario) :532`. Scenario file path resolution `ProjectSettings.GlobalizePath(ScenarioPath)` + `ScenarioSerializer.LoadFromFile` at `:521-522` (this is the loader's own path resolution — stays in presentation; it is not part of the applier). [Source: MainScene.cs:507-537]
- **Trigger spawn delegate** — `_scenarioDirector.OnSpawnUnit = (unitId, slot, x, z, count) => { ... SpawnScenarioUnit(def, faction, x + i*2.5f, z); }` set in `SetupTriggerEditor` (`MainScene.cs:2001-2015`; `SpawnScenarioUnit` call at `:2014`). `ScenarioDirector.OnSpawnUnit` is `Action<string,int,float,float,int>?` (`ScenarioDirector.cs:49`). **Re-point this to `_applier.SpawnUnit`** when `SpawnScenarioUnit` moves. [Source: MainScene.cs:2001-2015; ScenarioDirector.cs:49]
- **`SimulationHost` public surface (1.8a)** — get-only props `World`/`Nodes`/`Resources`/`Buildings`/`Projectiles`/`CombatEvents`/`MatchStats`/`BuildSys`/`ScenarioDirector`/`Fog` (`SimulationHost.cs:32-41`); `CurrentTick`/`LastChecksum`/`InterpolationAlpha`/`ChecksumInterval` (`:44-47`); `Create(ILogSink, FactionRegistry, FactionDefinition?, FactionDefinition?, DamageTable?, AiDifficulty)` (`:57-64`); `StepOnce`/`Update`/`SetChecksumSink` (`:114-123`); `internal IReadOnlyList<ISimSystem> Systems` (`:129`). **`_loop` is private; no apply/mutate entry exists** — the applier is the new mutation surface. In `MainScene`, `_host.World`→`_world` (and the other 9) are already aliased at `:286-295`. [Source: SimulationHost.cs; MainScene.cs:286-295]
- **The sim stores are fixed-capacity + already alloc-free** — `EntityWorld.Create(FixedVec3, Faction, Fixed health, Fixed speed)` (`EntityWorld.cs:205-251`, `MAX_ENTITIES=4096`, free-list pop or `-1`); `BuildingStore.Create(FixedVec3, Faction, BuildingType)` (`BuildingStore.cs:87`, `MAX_BUILDINGS=64`); `ResourceNodeStore.Create(FixedVec3, Fixed, Fixed, int)` (`ResourceNodeStore.cs:39`, `MAX_NODES=64`). None grows; none allocates per call. `EntityWorld.MeshType` is `byte[]` — *"Purely presentational … excluded from the determinism checksum"* (`EntityWorld.cs:114-122`). `EntityWorld` seeds `Rng = new SimRng(DEFAULT_RNG_SEED)` once in its ctor (`:194`); the spawn/apply path never touches `Rng`. **Leave all three stores + `EntityWorld` unchanged.** [Source: EntityWorld.cs; BuildingStore.cs; ResourceNodeStore.cs]
- **`ScenarioData` is `float`/`string`-typed (NOT `Fixed`)** (`ScenarioData.cs`): `ScenarioPlayerSlot{int Slot; string FactionJson (res://); float StartOre; float BaseX; float BaseZ}`; `ScenarioResourceNode{float X; float Z; float Supply; float Rate; int MaxGatherers}`; `ScenarioBuilding{string Type; int Slot; float X; float Z; bool PreBuilt}`; `ScenarioUnit{string UnitId; int Slot; float X; float Z}`; plus `TriggerDefinition[] Triggers`. A real scenario (`alpha_map_01.json`): 2 slots (faction_json `res://resources/data/factions/alpha_faction.json`, start_ore 200), 8 resource_nodes, 2 pre-built CommandCenters, 4 workers. [Source: ScenarioData.cs; alpha_map_01.json]
- **`FactionDefinition` is Godot-free + its lookups are alloc-free** (`FactionDefinition.cs`): `GetUnit(string)`/`GetBuilding(string)`/`GetUnitByCategory(string)` are `foreach` over `List<UnitDefinition>` with `==`/`string.Equals(...Ordinal...)`; `IndexOfUnit(string)` is a `for` loop returning the index or `-1`. **No LINQ, no per-call allocation.** `LoadFromFile(absolutePath)` is pure `File.ReadAllText` + `JsonSerializer` (its doc says *"Pass the absolute OS path … resolved with ProjectSettings.GlobalizePath"*). [Source: FactionDefinition.cs]
- **The 1.7 validation gate (already shipped):** `ScenarioValidator` (`ScenarioValidator.cs`, `public sealed class`, Godot-free, in `Core.Definitions`) — `public ValidationResult Validate(ScenarioData m)`, pure (never throws/logs); mints `ValidationResult.Pass(new Validated<ScenarioData>(m, _proof))` on success, `ValidationResult.Fail(located)` on first failure. Sole minter via `ScenarioValidator.Proof` (public type, **internal** ctor) + the `ValidatedSoleMinterTest` source-scan. `Validated<T>` (`Validated.cs`): `public readonly struct` with `T Value` + a ctor requiring `ScenarioValidator.Proof`. Its docstring: *"Story 1.8b makes the scenario applier consume only `Validated<ScenarioData>` … 1.7 … does NOT yet type-gate the applier (that is 1.8b)."* `ScenarioGate` (`ScenarioGate.cs`): `IsFailClosed()` (env `CHIMERA_VALIDATE_FAILCLOSED`), `ShouldProceed(bool ok, bool failClosed) => ok || !failClosed`. The validator comment confirms **`FactionRegistry.ToFaction(slot)` exists** (defined for slot ≤ 3). [Source: ScenarioValidator.cs; Validated.cs; ScenarioGate.cs]
- **`MainScene.ValidateBeforeApply`** is the presentation call site that runs the gate + does the located-error logging + the `ScenarioGate` decision (referenced around `MainScene.cs:539-561`; called from `ApplyScenario` `:561` and `ApplyFallbackScenario` `:677`). It currently returns `bool` (proceed). 1.8b changes it to return the `ValidationResult` so the caller can pass `result.Value` to the applier (D3). [Source: MainScene.cs; ScenarioGate.cs]
- **The two existing goldens hand-populate via `world.Create` (NOT `ApplyScenario`)** and explicitly flag this story: `GoldenScenario.cs` comment — *"the JSON apply path is Godot-coupled in MainScene, and a Godot-free ScenarioApplier does not exist until Story 1.8b … TODO(1.8b): add an alpha_map_01-loaded golden + start-state hash once ScenarioApplier is Godot-free."* They build via `world.Create(...)`/`buildings.Create(...)`/`nodes.Create(...)` + direct SoA writes, then `host.ScenarioDirector.LoadScenario(new ScenarioData())`. `GoldenChecksumReplay.RunAndRecord(ticks, perturb?, build?)` accepts a `Func<GoldenHarness> build` — the seam for the optional applier-driven golden. [Source: GoldenScenario.cs; MultiFactionScenario.cs; GoldenChecksumReplay.cs:51-66]
- **Net-new types confirmed ABSENT @ `73dbff7`:** `ScenarioApplier`, `SimWorld`, `ServerBootstrap`, `ScenarioDelegateBinder`, `ModifierStore`. `EntityWorld.SpawnUnit` does **not** exist (the primitive is `Create`). [Source: recon greps]

### Verify-in-code before you wire (do NOT guess — read these exact sites)

1. **`MainScene.ValidateBeforeApply` exact body + return** — read it to see today's logging + `ScenarioGate` use, so your `ValidationResult`-returning refactor preserves the located-error log lines and the shadow/fail-closed decision exactly.
2. **`MoveStartPosition`** (architecture cites the sim-half ~`MainScene.cs:1009-1011`; **locate by symbol**) — confirm the exact `FactionBase` write to route through `_applier.SetFactionBase`, and that the rest of the method (camera/marker/presentation) stays in `MainScene`.
3. **The 1.7 `NegativeValidationTests`** — check whether any assert depends on `ValidationResult.Fail(...).Value == default`. If so, update to the D3 "Fail carries the wrapped model, `Ok == false`" contract. Keep `ValidatedSoleMinterTest` green (only `new Validated<` in `ScenarioValidator.cs`).
4. **`BuildFallbackMirror` + the canonical start-state hash site** (architecture cites ~`MainScene.cs:345`; locate by symbol) — confirm whether `_fallbackMirror` / the canonical-hash computation is presentation bookkeeping that stays in `MainScene` (it is — leave it; the applier only does the hardcoded `ApplyFallback` writes).
5. **`_slotFactionDefs` initialization for the fallback path** — confirm where the **default** faction defs get loaded so `ApplyFallback`'s `GetUnitByCategory("Worker")` resolves when the scenario file is missing; the pre-pass must ensure those defaults are resolved before `ApplyFallback` runs.
6. **`UnitDefinition` shape** (`Hp`/`Speed`/`VisionRange`/`AttackRange`/`AttackDamage`/`AttackSpeed`/`SplashRadius`/`Supply`/`ParsedDamageType`/`ParsedArmorType`/`Category`/`Id`) — confirm the field names the moved `SpawnUnit` reads (so the applier's `using ProjectChimera.Core.Definitions;` resolves them, Godot-free).

### Scope fence — do NOT, in this story

- **Do NOT** move/re-record either existing golden, or set `CHIMERA_GOLDEN_RECORD` for them. (The optional NEW applier golden is the only sanctioned record, and only for the new file.) (AC5)
- **Do NOT** change `ScenarioData` field types, `ScenarioSerializer`, or add a `ScenarioModel` — keep `Fixed.FromFloat` in the applier (D2).
- **Do NOT** touch the mesh/GLB path (`SetupFactionVisuals`/`MultiMeshBridge`/`MeshLoader`) — already hoisted (D4).
- **Do NOT** modify `SimulationHost`'s public surface, expose `_loop`, or add an apply-method to the host; do **NOT** introduce `SimWorld` (D1).
- **Do NOT** modify `EntityWorld`/`BuildingStore`/`ResourceNodeStore`/`SimChecksum`/`SimulationLoop`/the 9 system bodies (they are shared with the byte-identical goldens). (Trap 1)
- **Do NOT** build `ISetupPhase`/`ScenePhaseRunner`/`PhaseOrderTest`, the MainScene shrink-diff, the full `ScenarioDelegateBinder`, `ServerBootstrap`, `ModifierStore`, or generalize `FactionRegistry`/`SimChecksum` (D9).
- **Do NOT** add per-tick/per-entity logging through `ILogSink`; the applier's only logging is low-frequency diagnostics (unknown unit_id, located validation errors). (1.8a D6)
- **Do NOT** weaken 1.7's shadow-mode apply-on-fail; preserve it via D3.

---

## Tasks / Subtasks

- [x] **Task 1 — Net-new Godot-free `ScenarioApplier` (AC: 1, 4)**
  - [x] Create `godot/src/Core/Sim/ScenarioApplier.cs` (`#nullable enable`, namespace `ProjectChimera.Core.Sim`, **no `using Godot`/`GD.*`/`ProjectSettings`/`res://`**). Constructor `ScenarioApplier(SimulationHost host, ILogSink log, FactionDefinition?[] slotFactionDefs)` — store all three; `slotFactionDefs` is the **same array** `MainScene` owns (the pre-pass fills it in place; never reassign it — D1/D4). Expose nothing public beyond the methods below.
  - [x] `int SpawnUnit(UnitDefinition def, Faction faction, float x, float z)` — verbatim relocation of `MainScene.SpawnScenarioUnit` (return the id). Use `_host.World` + `_slotFactionDefs[(int)faction].IndexOfUnit(def.Id)` for the `MeshType` lookup. No allocation; failure-path logging via `_log.Warn` only (D5).
  - [x] `void SetFactionBase(Faction faction, FixedVec3 pos)` — `_host.Resources.FactionBase[(int)faction] = pos;` (the unified write site, D6).
  - [x] `static BuildingType ParseBuildingType(string type)` — verbatim move (D2).
  - [x] `void Apply(Validated<ScenarioData> v)` — verbatim `ApplyScenario` body **minus** the `ProjectSettings.GlobalizePath`/`File.Exists`/`LoadFromFile` (those are now in the pre-pass; read `_slotFactionDefs[(int)faction]` + `_host.BuildSys.SetFactionDef`). Read the model via `v.Value`. Preserve order: slots(ore+base+SetFactionDef) → nodes → buildings → units(SpawnUnit) → `_host.ScenarioDirector.LoadScenario(v.Value)`. Keep every `Fixed.FromFloat`/`Fixed.Zero` exactly (D2). Unknown unit_id → `_log.Warn(...)` + continue.
  - [x] `void ApplyFallback()` — verbatim `ApplyFallbackScenario` hardcoded writes (2 bases via `SetFactionBase`, ore, 8 nodes, 2 CommandCenters, 4 workers via `SpawnUnit` using `_slotFactionDefs[...]?.GetUnitByCategory("Worker")`). **No `LoadScenario`** (preserve the split). The `_fallbackMirror`/validation/canonical-hash bookkeeping stays in `MainScene` (Verify-in-code #4).
  - [x] `dotnet build godot/godot.csproj` → green.

- [x] **Task 2 — Type-gate via `Validated<ScenarioData>` (AC: 4) — the one 1.7 touch (D3)**
  - [x] Extend `ValidationResult` (`src/Core/Definitions/Validated.cs`) so `Fail` carries the minted `Validated<ScenarioData>` (wrap the model; `Ok=false`). Mint inside `ScenarioValidator` (`new Validated<ScenarioData>(m, _proof)`) so the sole-minter invariant + `ValidatedSoleMinterTest` stay intact. (The `m is null` early-return may keep `default` — that path routes to fallback.)
  - [x] Update `ScenarioValidator.Validate`'s `Fail` call sites to pass the wrapped model where `m` is non-null.
  - [x] Run + fix `NegativeValidationTests` / `ValidatedSoleMinterTest` (Verify-in-code #3): rejection still located, `Ok==false`, and (now) `Value` carries the wrapped model.
  - [x] `dotnet test --filter FullyQualifiedName~Validation` (and `~ValidatedSoleMinter`) → green.

- [x] **Task 3 — Presentation faction-resolution pre-pass + MainScene re-point (AC: 2, 6)**
  - [x] Add a `MainScene` helper (e.g. `void ResolveSlotFactionDefs(ScenarioData scenario)`) doing the per-slot `ProjectSettings.GlobalizePath(slot.FactionJson)` + `File.Exists` + `FactionDefinition.LoadFromFile`, writing resolved defs into `_slotFactionDefs` **in place** (kept on `MainScene` for the trigger delegate; shared with the applier) and ensuring the **default** defs are present for the fallback path (Verify-in-code #5).
  - [x] Construct `_applier = new ScenarioApplier(_host, _logSink, _slotFactionDefs)` in `_Ready` (after `_host` + `_slotFactionDefs` exist; `_logSink` is the 1.8a `GodotLogSink`). The applier shares the `_slotFactionDefs` array reference — the pre-pass fills it before each apply.
  - [x] Rewrite `ValidateBeforeApply` to return the `ValidationResult` (preserve the located-error logging; D3 / Verify-in-code #1).
  - [x] Rewire `LoadAndApplyScenario`: file + AI-gen branches → `ResolveSlotFactionDefs(scenario); var r = ValidateBeforeApply(scenario, label); if (ScenarioGate.ShouldProceed(r.Ok, ScenarioGate.IsFailClosed())) _applier.Apply(r.Value);`. Fallback branch → resolve defaults into `_slotFactionDefs` + `_applier.ApplyFallback()` (keep the `_fallbackMirror` validate/hash bookkeeping).
  - [x] Re-point the `OnSpawnUnit` trigger delegate (`~:2001-2015`) to call `_applier.SpawnUnit` (move its failure-path `GD.PrintErr` to `_logSink.Warn`).
  - [x] Route `MoveStartPosition`'s sim-half `FactionBase` write through `_applier.SetFactionBase` (Verify-in-code #2).
  - [x] **Delete** the four former methods (`ApplyScenario`/`SpawnScenarioUnit`/`ParseBuildingType`/`ApplyFallbackScenario`) from `MainScene` — their bodies now live in the applier. Confirm no `MainScene` code writes `_resources`/`_nodes`/`_buildings`/`_world`/`_scenarioDirector` sim truth directly for scenario setup.
  - [x] `dotnet build godot/godot.csproj` → green.

- [x] **Task 4 — New Godot-free `ScenarioApplierTests` + zero-alloc proof (AC: 1, 3, 5)**
  - [x] Create `godot/ProjectChimera.Sim.Tests/Builder/ScenarioApplierTests.cs`. Build a known in-code `ScenarioData` (mirror `alpha_map_01`) + an in-code `FactionDefinition` (with a `"worker"` `UnitDefinition`) placed into a `slotFactionDefs` array. Construct `var host = SimulationHost.Create(NullLogSink.Instance, new FactionRegistry(2), ...)` + `var applier = new ScenarioApplier(host, NullLogSink.Instance, slotFactionDefs)`. `var r = new ScenarioValidator().Validate(model)` → assert `r.Ok`; `applier.Apply(r.Value)`.
  - [x] Assert **identical store contents**: `World.AliveCount`; each unit `Position`(=`Fixed.FromFloat(x)`,`Fixed.Zero`,`Fixed.FromFloat(z)`)/`Faction`/`Health`/`GatherState`/`MeshType`; `Buildings.Count`/`Type`/`Position`/`ConstructionTimer==Fixed.Zero` (pre-built); `Nodes` count/positions/`Supply`/`Rate`/`MaxGatherers`; `Resources` ore per faction; `FactionBase` per faction. Assert a stable `CanonicalModelHash` baseline of the model.
  - [x] Add the **zero-alloc** assert: warm up `applier.SpawnUnit(def, faction, x, z)` once (JIT), then assert `GC.GetAllocatedBytesForCurrentThread()` delta == 0 across an N-call loop (place in this file or `Determinism/ZeroAllocInTickTests.cs`).
  - [x] `GodotFreeBoundaryTest` green (proves the applier + test are Godot-free). `dotnet test --filter FullyQualifiedName~ScenarioApplier` → green.

- [x] **Task 5 — (Optional) applier-driven golden replay realizing `TODO(1.8b)` (AC: 5)**
  - [x] Add a `build:` delegate to a new test (e.g. `GoldenApplierScenario.Build`) that constructs a `SimulationHost`, applies the in-code `Validated<ScenarioData>` via `ScenarioApplier`, returns a `GoldenHarness`; run `GoldenChecksumReplay.RunAndRecord(300, build: GoldenApplierScenario.Build)` and record `golden-applier-scenario.golden.txt` **once** (the only sanctioned `CHIMERA_GOLDEN_RECORD`, NEW file only). Wire it as `<EmbeddedResource>`.
  - [x] `dotnet test --filter FullyQualifiedName~GoldenApplier` → green, byte-identical thereafter.

- [x] **Task 6 — Prove AC5: full suite green, existing goldens byte-identical, Godot-free boundary (AC: 5)**
  - [x] `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` → ALL green (incl. `GodotFreeBoundaryTest`, both existing golden suites, `ScenarioApplierTests`, the validation tests).
  - [x] `git status --porcelain` on `golden-scenario.golden.txt` + `golden-multifaction.golden.txt` → **empty** (unchanged). `git diff` shows no change to `EntityWorld`/`BuildingStore`/`ResourceNodeStore`/`SimChecksum`/`SimulationLoop`/the 9 system bodies/`SimulationHost`'s public surface.
  - [x] Grep `src/Core/Sim/ScenarioApplier.cs`: zero `using Godot`/`GD.`/`ProjectSettings`/`res://`/`Console.`/`System.Random`.
  - [x] `dotnet build godot/godot.csproj` → green (only pre-existing CS8632 warnings).

- [x] **Task 7 — In-engine smoke (AC: 2, 6) — recommended**
  - [x] Run the game (`/godot-verify` or Godot MCP run): a normal skirmish loads `alpha_map_01` through `_applier.Apply` (units/buildings/nodes/ore identical to before), the `[Checksum]` line still prints, and a trigger that spawns a unit (`OnSpawnUnit`) still works via `_applier.SpawnUnit`. _(MainScene is excluded from Tier-1, so this is the only check of the production wiring.)_

### Review Findings

_Code review 2026-06-24 (`gds-code-review`) — 3 parallel adversarial layers (Blind Hunter · Edge Case Hunter · Acceptance Auditor) vs baseline `73dbff7`. Result: **2 patch · 2 deferred · 4 dismissed**. The extraction itself is faithful — all 6 ACs structurally met, both existing goldens byte-identical, scope fences honored, sole-minter intact. Both patches are robustness gaps on the shadow-mode / untrusted-input boundary (where AI-generated + creator-authored scenarios live), not defects in the happy path._

#### Patch (action required)

_✅ Both patches APPLIED & verified 2026-06-24 — fix in `godot/src/Core/Sim/ScenarioApplier.cs`: added an `InFactionRange` bounds guard on the `Apply` slot + unit loops (out-of-range slot → `_log.Warn` + skip) and a null-model guard at the top of `Apply`. `SpawnUnit` left untouched to preserve the zero-alloc guarantee. Game build green; Tier-1 **147/147** pass; both existing goldens byte-identical; zero-alloc test still green._

- [x] [Review][Patch] **Shadow-applied invalid scenario crashes the applier** (a `Units`/trigger entry with `slot ≥ 4` → `IndexOutOfRangeException`). The relocated `Apply` units-loop and `SpawnUnit` mesh lookup dropped the `(fIdx >= 0 && fIdx < _slotFactionDefs.Length)` bounds guard the old `MainScene` code had. Because D3 makes shadow mode (the default) apply models that **failed** validation, and `(Faction)(slot.Slot + 1)` is an *unchecked* enum cast, a unit slot ≥ 4 reaches `_slotFactionDefs[(int)faction]` (length 5) and throws where the old code fell back to `_factionDef`. A bad `PlayerSlot` already crashed in both old and new (pre-existing, via `FactionBase[(int)faction]`), so the **net-new** divergence is specifically units/triggers. Diverges from D3/AC4 "shadow preserved byte-for-byte." Fix: restore the bounds guard at the faction-index sites; treat out-of-range as `_log.Warn` + skip. [godot/src/Core/Sim/ScenarioApplier.cs:98 (units), :68 (slot), :189 (mesh)] _(blind+edge; refutes the Acceptance Auditor's "unreachable/harmless" verdict)_
- [x] [Review][Patch] **`Apply` consumes `v.Value` without rejecting a `default`/unproven `Validated<ScenarioData>`** — this is the validation-bypass the 1.7 review **explicitly deferred to story 1.8b** (`deferred-work.md` → "story-1.7" #1), still open. The token-less `Fail(string)` overload (null-model early-out) returns a `default` token whose model is `null`, so `Apply` would NRE on `s.PlayerSlots`. Not reachable from today's two call sites (both pass a non-null scenario), but `Apply` is `public` and slated for verbatim 1.9a `ServerBootstrap` reuse. Fix: guard at the consumption point (e.g. `if (v.Value is null) { _log.Warn(...); return; }`), closing the gap exactly where the 1.7 review intended. [godot/src/Core/Sim/ScenarioApplier.cs:62; godot/src/Core/Definitions/Validated.cs:69] _(blind+edge)_

#### Deferred

- [x] [Review][Defer] `ResolveSlotFactionDefs` never resets `_slotFactionDefs` entries — a scenario re-applied into the same `MainScene`/host after a slot's `faction_json` goes missing keeps the stale def (the array is allocated once in `_Ready` and shared by reference). Behavior-preserving (old code had the identical `if (!IsNullOrEmpty) { if (Exists) … }` shape); latent — only on re-apply into a live instance. [godot/src/Core/MainScene.cs:ResolveSlotFactionDefs] — deferred, behavior-preserving
- [x] [Review][Defer] Scenario with > 64 resource nodes or > 64 buildings silently drops the overflow (`Nodes.Create` / `PlaceBuildingDirect` return -1, unchecked; the validator imposes no count cap). Unchanged from the original inline code. [godot/src/Core/Sim/ScenarioApplier.cs:Apply/ApplyFallback] — deferred, pre-existing

---

## Dev Notes

### `ScenarioApplier` (Task 1) — shape (confirm exact field names against the call sites; the store-contents test is the proof)
```csharp
// src/Core/Sim/ScenarioApplier.cs   (Godot-free — NO using Godot / GD.* / ProjectSettings / res:// / float gameplay math beyond the relocated Fixed.FromFloat load-conversions)
#nullable enable
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;   // ScenarioData, UnitDefinition, FactionDefinition, Validated<T>, BuildingType
using ProjectChimera.Economy;             // (BuildingSystem.PlaceBuildingDirect / SetFactionDef via host.BuildSys)

namespace ProjectChimera.Core.Sim
{
    /// <summary>Net-new Godot-free SOLE WRITER of sim truth (AR-7). Absorbs MainScene's
    /// ApplyScenario/SpawnScenarioUnit/ParseBuildingType/ApplyFallbackScenario + the MoveStartPosition FactionBase
    /// write. Consumes Validated&lt;ScenarioData&gt; (1.7 gate). Path resolution is hoisted to a presentation pre-pass;
    /// the applier receives already-loaded FactionDefinitions. SpawnUnit is allocation-free. Reused headless by ServerBootstrap (1.9a).</summary>
    public sealed class ScenarioApplier
    {
        private readonly SimulationHost _host;
        private readonly ILogSink _log;
        private readonly FactionDefinition?[] _slotFactionDefs;   // SHARED reference with MainScene; the pre-pass fills it IN PLACE (never reassign)

        /// <param name="slotFactionDefs">The SAME array MainScene holds as _slotFactionDefs. The presentation pre-pass
        /// writes resolved defs into it in place before Apply/ApplyFallback; SpawnUnit + the trigger delegate read it.</param>
        public ScenarioApplier(SimulationHost host, ILogSink log, FactionDefinition?[] slotFactionDefs)
        { _host = host; _log = log; _slotFactionDefs = slotFactionDefs; }

        /// <summary>Apply a validated scenario. The Validated&lt;T&gt; gate means a raw model cannot reach a store.
        /// Reads _slotFactionDefs (filled by the pre-pass) — matches the architecture's single-param Apply(Validated&lt;T&gt;).</summary>
        public void Apply(Validated<ScenarioData> v)
        {
            ScenarioData s = v.Value;   // as-built property name (NOT .Model)
            foreach (var slot in s.PlayerSlots ?? System.Array.Empty<ScenarioPlayerSlot>())
            {
                var faction = (Faction)(slot.Slot + 1);
                var def = _slotFactionDefs[(int)faction];          // pre-resolved by the presentation pre-pass
                if (def != null) _host.BuildSys.SetFactionDef(faction, def);
                _host.Resources.AddOre(faction, Fixed.FromFloat(slot.StartOre));
                SetFactionBase(faction, new FixedVec3(Fixed.FromFloat(slot.BaseX), Fixed.Zero, Fixed.FromFloat(slot.BaseZ)));
            }
            foreach (var n in s.ResourceNodes ?? System.Array.Empty<ScenarioResourceNode>())
                _host.Nodes.Create(new FixedVec3(Fixed.FromFloat(n.X), Fixed.Zero, Fixed.FromFloat(n.Z)),
                                   Fixed.FromFloat(n.Supply), Fixed.FromFloat(n.Rate), n.MaxGatherers);
            foreach (var b in s.Buildings ?? System.Array.Empty<ScenarioBuilding>())
                _host.BuildSys.PlaceBuildingDirect(ParseBuildingType(b.Type), (Faction)(b.Slot + 1),
                                   new FixedVec3(Fixed.FromFloat(b.X), Fixed.Zero, Fixed.FromFloat(b.Z)), b.PreBuilt);
            foreach (var u in s.Units ?? System.Array.Empty<ScenarioUnit>())
            {
                var faction = (Faction)(u.Slot + 1);
                var def = _slotFactionDefs[(int)faction]?.GetUnit(u.UnitId);   // pre-pass populates defaults for slots w/o explicit faction_json
                if (def == null) { _log.Warn($"[ScenarioApplier] unit_id '{u.UnitId}' not in faction — skipped."); continue; }
                SpawnUnit(def, faction, u.X, u.Z);
            }
            _host.ScenarioDirector.LoadScenario(s);   // triggers last (same as today)
        }

        // Reads _slotFactionDefs[(int)faction].IndexOfUnit(def.Id) for MeshType — same source as today's SpawnScenarioUnit.
        public int SpawnUnit(UnitDefinition def, Faction faction, float x, float z) { /* verbatim SpawnScenarioUnit; alloc-free */ }
        public void SetFactionBase(Faction faction, FixedVec3 pos) => _host.Resources.FactionBase[(int)faction] = pos;
        public void ApplyFallback() { /* verbatim hardcoded; reads _slotFactionDefs for the worker defs; 2 bases via SetFactionBase, ore, 8 nodes, 2 CCs, 4 workers via SpawnUnit; NO LoadScenario */ }
        public static BuildingType ParseBuildingType(string type) => type switch {
            "Barracks" => BuildingType.Barracks, "ArcheryRange" => BuildingType.ArcheryRange,
            "SiegeWorkshop" => BuildingType.SiegeWorkshop, _ => BuildingType.CommandCenter };
    }
}
```
> **The faction-defs mechanism (settled):** the applier holds the **same `FactionDefinition?[]` array MainScene owns as `_slotFactionDefs`** (constructor-injected). The presentation pre-pass writes resolved defs into it **in place** (do not reassign the array, or the shared reference goes stale). `Apply`/`ApplyFallback`/`SpawnUnit` and the runtime `OnSpawnUnit` trigger delegate all read this one array — so the trigger path (which fires after a scenario is applied) sees the populated defs, exactly as the as-built `SpawnScenarioUnit` reads `_slotFactionDefs` today. This makes `Apply(Validated<ScenarioData>)` single-param (matching `game-architecture.md:1499`). The byte-identical store-contents test is the contract.

### D3 type-gate (Task 2) — the shadow-mode reconciliation
The applier requires a token; 1.7 shadow mode applies on FAIL. Make `ValidationResult` carry the token on Fail too (mint inside the validator), then:
```csharp
// MainScene (presentation) — replaces today's "if (!ValidateBeforeApply(...)) return;" + raw apply
ResolveSlotFactionDefs(scenario);                                     // presentation pre-pass: fills _slotFactionDefs IN PLACE (the one Godot path-resolution)
ValidationResult r = ValidateBeforeApply(scenario, "ApplyScenario");  // logs located error, returns the result
if (ScenarioGate.ShouldProceed(r.Ok, ScenarioGate.IsFailClosed()))
    _applier.Apply(r.Value);           // applier reads the now-filled _slotFactionDefs; shadow proceeds even when r.Ok==false (== today's behavior)
```

### Constraints & gotchas
- **`dotnet build` / `dotnet test` are authoritative** for C# correctness; the Godot MCP `run` does not rebuild the test assembly. Build + test before declaring done. [Source: 1.1–1.8a Dev Notes / LEARNINGS]
- **Existing goldens are byte-identical by NON-action** — they never call the applier; they move only if you touch a shared primitive (`EntityWorld`/stores/systems/`SimChecksum`). Don't. A moved existing golden = a real leak. [Source: GoldenScenario.cs; D7]
- **`Fixed.FromFloat` is pure + Godot-free** (`FixedPoint.cs:27`) — relocating it into the applier keeps the boundary green. **Keep it; do not "Fixed-ify" `ScenarioData`** (D2).
- **`src/Core/Sim/` auto-globs into Tier-1; keep the applier Godot-free.** A `using Godot;` (or `GD.`/`ProjectSettings`) in the applier fails `GodotFreeBoundaryTest`. The faction-JSON resolution + all `GD.*` stay in `MainScene`. [Source: ProjectChimera.Sim.Tests.csproj; GodotFreeBoundaryTest.cs; D4]
- **Locate `MainScene` seams by symbol** (file is 2,349 LOC; doc/line numbers drifted). [Source: recon]
- **`Validated<T>.Value`, not `.Model`** — the architecture example uses `.Model`; the as-built struct exposes `.Value`. [Source: Validated.cs]
- **Pre-existing CS8632** nullable warnings are not this story's bug — leave them. [Source: 1.7/1.8a Dev Notes]
- **No new NuGet/dependency, no second build target.** [Source: project-context.md; AR-2]

### Project Structure Notes
- **NEW (sim, Godot-free — `godot/src/Core/Sim/`):** `ScenarioApplier.cs` — auto-globbed into Tier-1 via `..\src\Core\**`, **no `.csproj` glob edit** (same as 1.8a's host).
- **NEW (test — `godot/ProjectChimera.Sim.Tests/Builder/`):** `ScenarioApplierTests.cs` (+ optionally `Determinism/ZeroAllocInTickTests.cs`, `Golden/GoldenApplierScenario.cs` + `golden-applier-scenario.golden.txt`).
- **EDIT:** `godot/src/Core/MainScene.cs` (pre-pass helper, construct `_applier`, `ValidateBeforeApply`→`ValidationResult`, rewire `LoadAndApplyScenario`/fallback, re-point `OnSpawnUnit` + `MoveStartPosition`, **delete** the 4 former methods); `godot/src/Core/Definitions/Validated.cs` (`ValidationResult.Fail` carries the token); `godot/src/Core/Definitions/ScenarioValidator.cs` (Fail sites pass the wrapped model); possibly the 1.7 `NegativeValidationTests` (D3 contract).
- **UNCHANGED (must stay so):** both existing `*.golden.txt`; `EntityWorld.cs`; `BuildingStore.cs`; `ResourceNodeStore.cs`; `ScenarioData.cs` (still `float`); `ScenarioSerializer.cs`; `SimChecksum.cs`; `SimulationLoop.cs`; `SimulationHost.cs` public surface; the 9 system bodies; `MeshLoader.cs`/`MultiMeshBridge.cs` (mesh path already hoisted); `ScenarioGate.cs`.
- **No** `SimWorld`/`ISetupPhase`/`ScenePhaseRunner`/`ServerBootstrap`/`ScenarioDelegateBinder`/`ModifierStore` — later stories.

### Project Context Rules
_Extracted from `_bmad-output/project-context.md` + `game-architecture.md` — these govern every edit here:_
- **The sim/presentation boundary is sacred.** `src/Core/Sim/ScenarioApplier.cs` is sim: **no `using Godot;`, no Node, no `GD.Print`, no `ProjectSettings`, no `res://`, no `float` gameplay state beyond the relocated load-time `Fixed.FromFloat`.** Presentation (`MainScene`, the pre-pass, `MeshLoader`) owns all Godot. Data flows sim→presentation; presentation calls named commands on the applier and never writes a store directly. [Source: project-context.md "The One Architectural Rule"; game-architecture.md:1658-1663]
- **Determinism:** process entities ascending-id; the apply order (slots→nodes→buildings→units→triggers) IS part of the contract; `Fixed` (16.16) only; `Fixed.FromFloat` is load-time only (the converter + the applier's relocated load-conversions). No wall-clock, no `System.Random`; the spawn path never touches `EntityWorld.Rng`. [Source: project-context.md "Determinism"]
- **Reuse + compose, don't subclass.** The applier *composes* the 1.8a `SimulationHost` + the existing stores/`BuildingSystem`; it builds no parallel subsystem and no `SimWorld`. [Source: project-context.md "Data layout"; D1]
- **Everything data-driven.** No hardcoded balance leaks in — units/buildings/factions come from `ScenarioData` + `FactionDefinition` JSON; `ParseBuildingType` maps authored names. [Source: project-context.md "Everything is data-driven"]
- **Engine/runtime:** Godot 4.6.3 target, .NET 8 (`net8.0`); namespace `ProjectChimera.Core.Sim` (applier) / `ProjectChimera.Core.Definitions` (the model + gate); Tier-1 `ProjectChimera.Sim.Tests` (xUnit, Godot-free). [Source: project-context.md "Technology Stack"]

### References
- [Source: epics.md#Story-1.8b (L656-670)] — story statement; the 2 BDD ACs (sole writer + path-hoist pre-pass + alloc-free SpawnUnit + Godot-free compile; byte-identical golden through SimulationHost). Covers FR-39/FR-44/AR-7; Depends on 1.8a.
- [Source: epics.md AR-7 (L186)] — "Net-new ScenarioApplier: sole writer of sim truth (absorbs ApplyScenario/SpawnScenarioUnit/etc.); Godot path resolution hoisted to a presentation pre-pass; SpawnUnit allocation-free."
- [Source: epics.md#Story-1.8c (L672-686)] — 1.8c asserts (via the MainScene diff) that "sim mutation flows exclusively through ScenarioApplier" — the invariant 1.8b establishes. [Source: epics.md#Story-1.9a (L688-696)] — ServerBootstrap reuses SimulationHost+Validator+Applier.
- [Source: game-architecture.md §Step 6 sub-decision 3 (L1495-1505)] — the full ScenarioApplier spec: absorbs the four methods + sim-half MoveStartPosition; consumes `Validated<ScenarioModel>` + SimulationHost; exposes `SpawnUnit`/`SetFactionBase`; **path resolution hoisted**; "SpawnUnit must be allocation-free (pre-resolved def, no LINQ)."
- [Source: game-architecture.md §Step 6 migration (L1694-1709, 1723-1725)] — Step 1 = SimulationHost (1.8a); **Step 2 = hoist faction-def resolution to a presentation pre-pass**; **Step 3 = extract ScenarioApplier** (Godot-free, taking SimulationHost + pre-resolved defs; "Add ScenarioApplierTests … asserts identical store contents + a start-state hash baseline … Golden checksum identical"); Step 4 = validator shadow-insert; Step 9 = OnSpawnUnit→ScenarioApplier.SpawnUnit re-point.
- [Source: game-architecture.md §Step 6 "Why A" (L1464-1468) + FactionDefinition prereq (L1757-1758)] — the path-pre-pass rationale (makes the Godot-free claim true the instant the applier compiles); FactionDefinition/UnitDefinition must stay Godot-free so the applier's lookup compiles in the sim assembly.
- [Source: game-architecture.md §Implementation Patterns N6 (L2216-2246)] — applier consumes ONLY `Validated<T>`; the `Apply` ref-impl. **⚠ Its "already-Fixed fields / no conversion" form presumes a Fixed-typed model that does not exist as-built (ScenarioData is `float`); 1.8b keeps `Fixed.FromFloat` (D2). This was a tracked, corrected architecture contradiction (L2622, L2650).**
- [Source: game-architecture.md "sole writer" boundary rule (L1658-1665)] — sim-truth writes live only in `src/Core/Sim`|…; presentation calls named commands (`SpawnUnit`/`SetFactionBase`); both FactionBase write sites route through `ScenarioApplier.SetFactionBase`; no raw `Fixed.FromFloat` on external data before `Validate` returns Ok.
- [Source: game-architecture.md target tree (L1583, L1585, L1608-1616)] — `src/Core/Sim/ScenarioApplier.cs`; `ProjectChimera.Sim.Tests/Builder/ScenarioApplierTests.cs`; `Determinism/ZeroAllocInTickTests.cs`; **`SimWorld.cs` exists in the tree but was NOT built by 1.8a — do not introduce it.**
- [Source: 1.8a story (DONE) — Dev Agent Record] — `SimulationHost` exposes stores as get-only properties (no `SimWorld`); `_loop` is private; `ILogSink`/`NullLogSink`/`GodotLogSink` shipped; the two goldens were proven byte-identical via the host; MainScene aliases `_host.X`→local fields at `~:286-295`.
- [Source: MainScene.cs:507-537, 553-724, ~539-561, ~1009-1011, 2001-2015] · [SimulationHost.cs:32-129] · [EntityWorld.cs:62,114-122,194,205-264] · [BuildingStore.cs:87] · [ResourceNodeStore.cs:39] · [ScenarioData.cs] · [ScenarioSerializer.cs] · [FactionDefinition.cs] · [ScenarioValidator.cs] · [Validated.cs] · [ScenarioGate.cs] · [GoldenScenario.cs:48-61(TODO 1.8b),83-113] · [GoldenChecksumReplay.cs:51-66] · [FixedPoint.cs:27,153] — the verified current-state citations behind every Pre-flight fact above (located by symbol; doc line numbers drifted — live file is 2,349 LOC).

---

## Dev Agent Record

### Agent Model Used

claude-opus-4-8 (Claude Code, gds-dev-story workflow)

### Debug Log References

- `dotnet build godot/ProjectChimera.Sim.Tests/...` and `dotnet build godot/godot.csproj` — both green (only pre-existing CS8632 nullable warnings; no new warnings/errors).
- `dotnet test` Tier-1 suite: **147/147 passing** (was 142 pre-story; +3 `ScenarioApplierTests`, +2 `GoldenApplierScenarioTests`).
- Canonical-model hash of the in-code alpha mirror recorded once from a deliberate placeholder-fail: `12401609732849360762UL` (pinned in `ScenarioApplierTests.ExpectedCanonicalHash`).
- Applier golden recorded via `CHIMERA_GOLDEN_RECORD=1 dotnet test --filter ~GoldenApplier` (NEW file only; the two existing goldens stayed git-clean throughout).
- In-engine smoke (Godot 4.6.3 MCP): launched, pressed Play Skirmish; HUD showed `Tick 377 Hash 0xF809EDCF`, `Total: 4` units, `Nodes: 8 Buildings: 3`, P1 200→280 ore — exact alpha_map_01 state, zero editor errors.

### Completion Notes List

**Story 1.8b complete — net-new Godot-free `ScenarioApplier` is the sole writer of sim truth.** Behavior-preserving extraction; both existing goldens byte-identical.

- **AC1 (net-new Godot-free sole writer):** `src/Core/Sim/ScenarioApplier.cs` (namespace `ProjectChimera.Core.Sim`, auto-globbed into Tier-1). No `using Godot`/`GD.*`/`ProjectSettings`/`res://`/`Vector3` in code (grep-clean; `GodotFreeBoundaryTest` green). Writes only through the 1.8a host's stores; exposes `Apply(Validated<ScenarioData>)`, `ApplyFallback()`, `int SpawnUnit(UnitDefinition,Faction,float,float)`, `SetFactionBase(Faction,FixedVec3)`, `static ParseBuildingType(string)`. The per-slot `FactionDefinition?[]` is constructor-injected (shared in place with MainScene).
- **AC2 (path resolution hoisted):** the only `ProjectSettings.GlobalizePath` for the slot faction-JSON now lives in `MainScene.ResolveSlotFactionDefs` (a presentation pre-pass that fills `_slotFactionDefs` IN PLACE before the applier runs). Mesh/GLB path (`SetupFactionVisuals`→`MeshLoader`) left untouched.
- **AC3 (SpawnUnit zero-alloc):** `SpawnUnit_AllocatesZeroBytes_AfterWarmup` asserts `GC.GetAllocatedBytesForCurrentThread()` delta == 0 over 256 calls after JIT warm-up. The failure-path log moved to `ILogSink.Warn`. Same primitive is the single spawn path for `Apply`, `ApplyFallback`, and the `OnSpawnUnit` trigger delegate.
- **AC4 (consumes only `Validated<ScenarioData>`):** `Apply` reads `v.Value`. D3 — `ValidationResult.Fail` gains a token-carrying overload; `ScenarioValidator` mints the proof ONCE after the null-check and threads it through every Fail + the Pass (still the sole `new Validated<` in the file → `ValidatedSoleMinterTest` green). 1.7 shadow-mode apply-on-fail preserved byte-for-byte: `MainScene.ValidateBeforeApply` now returns the `ValidationResult`; the caller applies `r.Value` under `ScenarioGate.ShouldProceed`.
- **AC5 (byte-identical gate + new proof + Godot-free compile):** both `golden-scenario.golden.txt` and `golden-multifaction.golden.txt` git-clean (untouched — no shared primitive modified). New `ScenarioApplierTests` asserts identical store contents (units/buildings/nodes/ore/bases, all SoA stat writes, MeshType via faction-def lookup) + a pinned canonical-hash baseline. `dotnet build godot/godot.csproj` green.
- **AC6 (MainScene routes ALL mutation through the applier):** `LoadAndApplyScenario` (file + AI-gen) and the fallback go through `_applier`; `OnSpawnUnit`→`_applier.SpawnUnit`; `MoveStartPosition`→`_applier.SetFactionBase`. The four former methods (`ApplyScenario`/`SpawnScenarioUnit`/`ParseBuildingType`/`ApplyFallbackScenario`) are deleted — grep confirms zero direct `_resources.FactionBase[…]=`/`AddOre`/`_nodes.Create`/`PlaceBuildingDirect`/`_world.Create` scenario writes remain in MainScene (sets up 1.8c's exclusivity assertion).
- **Task 5 (optional, done):** applier-driven golden `golden-applier-scenario.golden.txt` realizes `GoldenScenario`'s `TODO(1.8b)` — pins the SimChecksum sequence of the alpha mirror applied through the applier over 300 ticks (recorded only this new file; safe targeted record).
- **Scope fences honored:** `ScenarioData` left `float`-typed (kept `Fixed.FromFloat` per D2); no `SimWorld`/`ISetupPhase`/`ScenePhaseRunner`/`ServerBootstrap`/`ModifierStore`; `SimulationHost` public surface + `_loop` privacy untouched; no `FactionRegistry`/`SimChecksum` widening.

### File List

**New:**
- `godot/src/Core/Sim/ScenarioApplier.cs`
- `godot/ProjectChimera.Sim.Tests/Builder/ScenarioApplierTests.cs`
- `godot/ProjectChimera.Sim.Tests/Golden/GoldenApplierScenario.cs`
- `godot/ProjectChimera.Sim.Tests/Golden/GoldenApplierScenarioTests.cs`
- `godot/ProjectChimera.Sim.Tests/Golden/golden-applier-scenario.golden.txt`

**Modified:**
- `godot/src/Core/MainScene.cs` — `_applier` field + construction; `_slotFactionDefs` → `FactionDefinition?[]`; `ResolveSlotFactionDefs` pre-pass; `ValidateBeforeApply`→`ValidationResult`; `ApplyScenarioThroughApplier`/`ApplyFallbackThroughApplier`; rewired `LoadAndApplyScenario`, `OnSpawnUnit`, `MoveStartPosition`; deleted the 4 former methods.
- `godot/src/Core/Definitions/Validated.cs` — token-carrying `ValidationResult.Fail(string, Validated<ScenarioData>)` overload (D3).
- `godot/src/Core/Definitions/ScenarioValidator.cs` — mint the proof once after null-check; thread it through every Fail + Pass.
- `godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` — embed the new applier golden.

### Change Log

- 2026-06-24 — Story 1.8b implemented (gds-dev-story). Extracted MainScene's scenario-mutation methods into the net-new Godot-free `ScenarioApplier` (sole writer of sim truth, alloc-free `SpawnUnit`, hoisted faction-JSON resolution); type-gated to `Validated<ScenarioData>` (D3, golden-neutral shadow-mode preserved); added Godot-free `ScenarioApplierTests` (store-contents + canonical-hash + zero-alloc) and an optional applier-driven golden. 147/147 Tier-1 green; both existing goldens byte-identical; in-engine smoke verified on Godot 4.6.3. Status → review.
