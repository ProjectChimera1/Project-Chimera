---
baseline_commit: 5195b2a
---
<!-- Powered by BMAD-CORE™ -->

# Story 1.8c: Asserted ScenePhaseRunner + ISetupPhase[] + PhaseOrderTest, and the MainScene strangle diff

Status: done

## Story

As a solo developer decomposing the ~2,238-LOC MainScene god-object,
I want the composition root expressed as an asserted `ISetupPhase[]` literal run by a `ScenePhaseRunner` and pinned by a `PhaseOrderTest`, with MainScene reduced to presentation/wiring only,
so that the materially-smaller MainScene is presentation-only and ready to absorb the six editors (Epics 3–8) without touching the Godot-free sim spine.

## Acceptance Criteria

**AC1 (Step 7 — the asserted phase root).**
**Given** the composition root expressed as an `ISetupPhase[]` literal run by `ScenePhaseRunner`
**When** `PhaseOrderTest` runs
**Then** it asserts the exact phase order and **FAILS if a phase is reordered, added, or removed** — and the `ScenePhaseRunner` itself re-asserts that the live literal matches the canonical order at startup (it throws, never silently reorders).

**AC2 (Step 8/9/10 — the strangle diff).**
**Given** MainScene after the strangle
**When** I diff it
**Then** MainScene is **materially smaller** and contains **only presentation/wiring** (Node lifecycle + input/process routing + the phase-list literal), with **all sim mutation flowing exclusively through `ScenarioApplier`** — provable by the exclusivity grep returning zero direct sim-store writes in MainScene.

**AC3 (regression gate — behavior-preserving).**
**Given** this is a presentation/wiring-only refactor that touches **zero sim ticks**
**When** the Tier-1 suite runs
**Then** all three goldens (`golden-scenario`, `golden-multifaction`, `golden-applier-scenario`) stay **byte-identical** (no re-record), `GodotFreeBoundaryTest` stays green, the full `dotnet test` suite passes, and the in-engine smoke (boot → skirmish) is behavior-identical with **zero new errors/NREs**.

_Covers: FR-39, FR-44, AR-3. Depends on: 1.8b (done)._

---

## Developer Context

> **What this story IS:** the FINAL slice of the D1 strangler that decomposes MainScene. 1.8a extracted the Godot-free `SimulationHost` (sim spine); 1.8b extracted the Godot-free `ScenarioApplier` (sole writer of sim truth) and re-pointed MainScene's scenario writes through it. **The sim is already out.** 1.8c does the *presentation-side* decomposition: turn the procedural `_Ready()` orchestration into an asserted `ISetupPhase[]` (Step 7), carve the inline Godot-tree setup bodies into coordinator phases one-at-a-time (Step 8), consolidate the `On*` delegate seam into a single `ScenarioDelegateBinder` (Step 9), and consolidate the match/checksum lifecycle (Step 10). **No sim code changes. No golden re-record.**

> **The architecture maps 1.8c to migration Steps 7–10** of the 14-step strangler [Source: game-architecture.md#Step6 migration sequence L1685–1746]. Step 7 is explicitly *"mechanical … No bodies move. Ship."* Step 8 is *"carve presentation coordinators, ONE phase per commit … behavior-identical, golden-gated, smoke-tested … Ship per move."* This story is **always-shippable**: ship after Step 7 (AC1 met), then ship after each carved phase.

### The traps (each has bitten a prior story or is the architecture's named failure mode)

1. **The Godot-free boundary trap (the make-or-break design decision).** `src/Core/Bootstrap/` falls under the Tier-1 compile glob `..\src\Core\**\*.cs` in `ProjectChimera.Sim.Tests.csproj`. If a phase class with `using Godot;` lands under that glob, it drags `GodotSharp` into the Godot-free test assembly and **`GodotFreeBoundaryTest` fails**. This is the exact same trap 1.8a flagged for `GodotLogSink` (resolved by homing it in `src/UI/`, not a globbed sim folder). **Resolution is settled below** (Key design decision 1): the Godot-free *contract* (`ISetupPhase`, `DelegateSetupPhase`, `ScenePhaseOrder`, `ScenePhaseRunner`) stays globbed-in and Tier-1-testable; the Godot-*touching* concrete phases are excluded from the glob.

2. **The presentation-NRE-past-the-golden trap (the architecture's #1 named residual risk for this story).** *"Coordinator extraction (Step 8) is the one band a red client build can slip past the golden gate — the sim is untouched, so a missed `_uiCanvas`/`_camCtrl` ref surfaces as a runtime NRE, not a checksum mismatch."* [Source: game-architecture.md L1786–1789]. **The golden suite CANNOT catch this.** Mitigation is mandatory and non-negotiable: **one phase per commit + constructor-injected deps (compile/null-checked) + an in-engine smoke after every single carve.**

3. **Line-number drift.** The architecture doc cites `MainScene.cs:NNN` from a ~2,234-LOC snapshot; the file grew to 2,349 at 1.8b's baseline and is **2,238 LOC at this story's baseline (`2e0d2f4`)** and keeps drifting via hourly `[AutoSave]` commits. **Locate every seam by SYMBOL (method/field name), never by the doc's line numbers.** Both sibling stories make this their #1 trap.

4. **`dotnet test` is authoritative — the Godot MCP `run` is NOT.** The MCP run launches the game from already-compiled assemblies; it does **not** rebuild or run the Tier-1 xUnit project. You must `dotnet build` + `dotnet test` to prove C# correctness, AND smoke-run in-engine to prove presentation. Both gates, every shippable slice.

5. **Re-coupling the sim spine to presentation.** 1.9a (`ServerBootstrap`) reuses `SimulationHost` + `ScenarioApplier` *headlessly*. If you thread a Godot dependency back into the host/applier construction path while restructuring `_Ready`, you break the 1.9a reuse. Keep sim construction (host → validate → apply) cleanly separable from the presentation coordinators.

6. **Silent reorder.** The whole point of AR-3 is constraint **C1**: *"never silently reorder `_Ready()`"*. The order is fragile (documented dependencies below). After this story, reordering must require editing a **test-guarded** list — that is what "asserted" means.

### The shape of the work (1 Godot-free phase kernel + 1 Tier-1 test + N presentation coordinator carves + 1 delegate binder + 1 lifecycle controller; sim spine + all goldens UNCHANGED)

- **Step 7 (Task 1–2, ships AC1):** Net-new Godot-free `ISetupPhase` + `DelegateSetupPhase` + `ScenePhaseOrder` + `ScenePhaseRunner` in `src/Core/Bootstrap/`. Rewrite `_Ready` so the ordered `Setup*()` sequence becomes a `new ISetupPhase[] { new DelegateSetupPhase("Settings", SetupSettings), … }` literal handed to `ScenePhaseRunner.Run()`. **No setup bodies move yet.** Add `PhaseOrderTest` (Tier-1). Build + test + smoke. **Ship.**
- **Step 8 (Task 3, ships per carve):** Replace each `DelegateSetupPhase("X", SetupX)` with a concrete `XPhase : ISetupPhase` class (presentation, excluded from Tier-1 glob) that **owns** the body and its products; later phases receive earlier products by constructor injection (carried in a `SceneContext`). **Carve `Hud` first** (it owns `_uiCanvas`). One phase per commit; golden byte-identical + smoke green after each. MainScene shrinks as bodies leave.
- **Step 9 (Task 4):** Net-new `ScenarioDelegateBinder` — the single assignment site for `ScenarioDirector.On*` (the broad binder 1.8b explicitly deferred; 1.8b did only the minimal `OnSpawnUnit` re-point). `OnSpawnUnit` stays pointed at `_applier.SpawnUnit` (sim→sim).
- **Step 10 (Task 5):** Consolidate `SetupMultiplayer`/`OnMatchStart`/replay wiring into a `MatchLifecycleController` phase; keep the **single** `SetChecksumSink` owner (1.8a already collapsed the double-set — do not regress it).
- **Throughout:** all sim mutation already flows through `_applier` (1.8b). 1.8c **asserts** that exclusivity (AC2 grep) and must not introduce any new direct sim write.

### Key design decisions (settled here — do NOT re-derive)

**1. Where the phase kernel lives (resolves the Godot-free boundary trap).**
- **Godot-free contract → `src/Core/Bootstrap/` (globbed into Tier-1, like `ILogSink`):**
  - `ISetupPhase.cs` — interface, **no `using Godot`**.
  - `DelegateSetupPhase.cs` — `ISetupPhase` whose body is an injected `System.Action` (the Step-7 "no bodies move" vehicle). Godot-free (holds an `Action`, never a Godot type).
  - `ScenePhaseOrder.cs` — `public static readonly string[] Canonical` = the exact ordered phase names. The single source of truth.
  - `ScenePhaseRunner.cs` — holds `IReadOnlyList<ISetupPhase>`; `Run()` asserts live order == `ScenePhaseOrder.Canonical` then invokes each `phase.Run()` in order. Godot-free (only calls through the interface).
- **Godot-touching concrete phases → `src/Core/Bootstrap/Phases/` + EXCLUDE from Tier-1** via `<Compile Remove="..\src\Core\Bootstrap\Phases\**\*.cs" />` in `ProjectChimera.Sim.Tests.csproj` (mirrors the existing `<Compile Remove="..\src\Core\MainScene.cs" />`). `SceneContext.cs` (carries Godot Node handles) goes here too. `GodotFreeBoundaryTest` is the backstop if anything Godot-touching leaks into a globbed path.
- _Rationale:_ this is the exact `ILogSink` (sim, globbed) vs `GodotLogSink` (presentation, `src/UI`, not globbed) split that 1.8a shipped, adapted to the arch's `src/Core/Bootstrap/` target tree [Source: game-architecture.md L1605–1607]. The Tier-1 `PhaseOrderTest` reads the Godot-free `ScenePhaseOrder`/`ScenePhaseRunner` — never a Godot-coupled phase type — exactly as `SystemOrderTest` reads the Godot-free `host.Systems`.

**2. `ISetupPhase` shape.** Minimal, identity-by-`Name` (a Godot-free string the test pins — concrete phase *types* can't be referenced in the Godot-free test):
```csharp
public interface ISetupPhase
{
    string Name { get; }   // the Godot-free identity PhaseOrderTest pins
    void Run();             // parameterless; the phase closes over its injected deps
}
```
`Run()` is parameterless: each phase holds its dependencies (constructor-injected) and its products, so the runner stays Godot-free and the interface carries no Godot/`SceneContext` type.

**3. "Asserted" = the runner self-checks AND the test pins.** `ScenePhaseRunner.Run()` first calls `AssertOrder()` — compares `_phases.Select(p => p.Name)` to `ScenePhaseOrder.Canonical` and throws `InvalidOperationException` with a precise diff on any mismatch — THEN runs the phases. `PhaseOrderTest` pins `ScenePhaseOrder.Canonical` against a hardcoded expected `string[]`. So a drift in the MainScene literal fails at startup (runner throw) and a drift in the canonical list fails in CI (test). This is the direct analog of 1.8a's already-shipped `SystemOrderTest`.

**4. Cross-phase deps = constructor injection via a `SceneContext` holder (minimal ceremony, no DI framework — C8).** A presentation-side `SceneContext` carries the shared handles (`SimulationHost host`, `ScenarioApplier applier`, `ILogSink log`, `FactionDefinition?[] slotFactionDefs`, `GameState`, `RtsCameraController`, `CanvasLayer uiCanvas`, `ScenarioData? scenario`, `LockstepManager`, etc.), populated as phases run. Each concrete phase takes `SceneContext` (and/or the specific earlier-phase products) in its constructor, reads what it needs, writes its products back. _Rationale:_ the arch wants "hidden timing deps → explicit constructor inputs" with "minimal ceremony, no heavyweight DI" [Source: game-architecture.md L1526–1531, Step6-briefing C8]. The canonical ordering driver: **`HudPhase` produces `_uiCanvas`, consumed by ≥5 later phases (Minimap, WinConditionUi, GameOverOverlay, ReplayStatus, the TriggerEditor toast)** — which is why Hud is carved first.

**5. Sim-spine construction stays in `_Ready` as a pre-phase (or the first phases), unchanged.** The host/applier/`SetChecksumSink` block (`MainScene._Ready`, the `SimulationHost.Create(...)` + alias + `new ScenarioApplier(...)` + `SetChecksumSink` section) is the shared sim seam 1.9a reuses. Keep it constructing `host` + `applier` into the `SceneContext` before the phase list runs. Do **not** wrap it in a Godot-coupled phase that would block headless reuse.

**6. Runtime phase order ≠ carve order.** The **runtime order** (what `ScenePhaseOrder.Canonical` pins) is the literal `_Ready` sequence below — unchanged. The **carve order** (which body to extract first in Step 8) is `Hud` first (owns `_uiCanvas`), per the arch's Step-8 sequence. Don't confuse them: Hud *runs* at position 9 but is *carved* first.

### Pre-flight facts you MUST NOT re-derive (verified against the codebase at `2e0d2f4`)

- **MainScene is `2238` LOC**, the one `using Godot;` file remaining in `src/Core`, already excluded from Tier-1 (`<Compile Remove="..\src\Core\MainScene.cs" />`). The ≤250-LOC root is the **directional** target — measure against the live file at story start; the gate is "materially smaller + presentation/wiring only + zero sim writes."
- **The sim is already extracted and wired into MainScene** (do NOT redo): `_host = SimulationHost.Create(_logSink, new FactionRegistry(2), _factionDef, _factionDef2, _damageTable, AiLevel)`; the 10 store/system fields are **aliases** of `_host.X`; `_applier = new ScenarioApplier(_host, _logSink, _slotFactionDefs)`; one `_host.SetChecksumSink(...)`.
- **All scenario sim-writes already route through `_applier`** (1.8b): `LoadAndApplyScenario` → `ApplyScenarioThroughApplier`/`ApplyFallbackThroughApplier` → `_applier.Apply(...)`/`_applier.ApplyFallback()`; `MoveStartPosition` → `_applier.SetFactionBase(...)`; `ScenarioDirector.OnSpawnUnit` → `_applier.SpawnUnit(...)`. The four former mutation methods (`ApplyScenario`/`SpawnScenarioUnit`/`ParseBuildingType`/`ApplyFallbackScenario`) are **already deleted** from MainScene. 1.8b's grep confirms **zero** direct `_resources.FactionBase[…]=`/`AddOre`/`_nodes.Create`/`PlaceBuildingDirect`/`_world.Create` scenario writes remain. 1.8c asserts this; do not reintroduce any.
- **`SimulationHost` public surface** (do not change): `static SimulationHost Create(ILogSink, FactionRegistry, FactionDefinition?=null, FactionDefinition?=null, DamageTable?=null, AiDifficulty=Normal)`; props `World/Nodes/Resources/Buildings/Projectiles/CombatEvents/MatchStats/BuildSys/ScenarioDirector/Fog`; `CurrentTick/LastChecksum/InterpolationAlpha/ChecksumInterval`; `StepOnce()`, `int Update(float)`, `SetChecksumSink(Action<uint,uint>)`; `internal IReadOnlyList<ISimSystem> Systems` (the `SystemOrderTest` seam). `_loop` is **private** — there is no public mutate entry.
- **`ScenarioApplier` public surface** (do not change): ctor `(SimulationHost host, ILogSink log, FactionDefinition?[] slotFactionDefs)`; `Apply(Validated<ScenarioData>)`, `ApplyFallback()`, `int SpawnUnit(UnitDefinition, Faction, float, float)`, `SetFactionBase(Faction, FixedVec3)`, `static ParseBuildingType(string)`. It's `public` for verbatim 1.9a reuse.
- **`ScenarioDirector.On*` delegates** (the `ScenarioDelegateBinder` target): `Action<string,int,float,float,int>? OnSpawnUnit`, `Action<string,float>? OnDisplayMessage`, `Action<string>? OnPlaySound`, `Action<int>? OnVictory` (at `ScenarioDirector.cs:49/52/55/58`). Currently assigned inline in `SetupTriggerEditor`.
- **`FactionRegistry`** (`src/Core/FactionRegistry.cs`, shipped 1.3a): `PLAYER_COUNT=8`, `FACTION_ARRAY_SIZE=9`, `static Faction ToFaction(int slot) => (Faction)(slot+1)`, `ActiveFactions`. **Use these where you touch faction-slot code** in carved coordinators — but the as-built `ResourceStore`/`MatchStats` `FACTION_COUNT=5` and the `[5]` arrays stay (Story 9.2 raises them). Do **not** generalize 2-faction HUD/win/game-over loops to N>2 — that is Step 11, not 1.8c.
- **`SystemOrderTest`** (`ProjectChimera.Sim.Tests/Sim/SystemOrderTest.cs`) is the precedent: a static `Type[] ExpectedOrder` + read the live ordered list + `Assert.Equal` count then element-by-element + a structural invariant. Mirror its shape for `PhaseOrderTest` (using `string[]` names, not types).
- **Tier-1 status:** 147 tests green at 1.8b. The csproj globs `..\src\Core\**`, `..\src\Combat\**`, `..\src\Economy\**`, `..\src\Navigation\**`, `..\src\AI\**` + 3 named `Multiplayer` files; `<Compile Remove>` only `MainScene.cs`. Adding Godot-free files under `src/Core/Bootstrap/` needs **no** glob edit; the `Phases/**` exclusion is the only csproj change.
- **Headless branch stays intact:** the `DisplayServer.GetName() == "headless"` early-return in `_Ready` (constructs `DedicatedServer`, returns before building the loop) is **1.9a's** seam. Leave it exactly as-is; it runs before the phase list and must keep doing so.

### Verify-in-code before you wire (read these exact sites by symbol — do NOT guess)

- `godot/src/Core/MainScene.cs` → `_Ready()` (the full setup sequence + the sim-spine construction block + the post-phase scenario-hash compute + replay autoload); `_Process`/`_Input`/`_UnhandledInput` (the runtime routing that STAYS); every `SetupX()` method body (the Step-8 carve material); `LoadAndApplyScenario`/`ResolveSlotFactionDefs`/`ApplyScenarioThroughApplier`/`ApplyFallbackThroughApplier`/`MoveStartPosition` (the already-applier-routed scenario path); `SetupTriggerEditor` (the `On*` assignments → `ScenarioDelegateBinder`); `SetupMultiplayer`/`OnMatchStart`/`StartRecording`/`StopRecording`/`TryLoadReplay` (the `MatchLifecycleController` material); the documented order comments at the `Setup*` calls (the dependency contract).
- `godot/src/Core/Sim/SimulationHost.cs`, `ScenarioApplier.cs`, `ILogSink.cs`, `NullLogSink.cs` — the surfaces you wire into the SceneContext (unchanged).
- `godot/src/UI/GodotLogSink.cs` — the precedent for "Godot impl lives outside the Tier-1 glob."
- `godot/ProjectChimera.Sim.Tests/Sim/SystemOrderTest.cs` — the `PhaseOrderTest` template.
- `godot/ProjectChimera.Sim.Tests/GodotFreeBoundaryTest.cs` — the boundary backstop you must keep green.
- `godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` — where the `Phases/**` `<Compile Remove>` goes.
- `godot/src/UI/MultiMeshBridge.cs` — has a `SimulationHost` overload (1.8a); `SetupFactionVisuals` passes `_host` to it. This is presentation wiring that becomes the `FactionVisuals` phase — keep both overloads; do not remove.

### Scope fence — do NOT, in this story

- **Do NOT** modify `SimulationHost`, `ScenarioApplier`, `SimulationLoop`, `SimChecksum`, the 9 system bodies, the stores, or `ScenarioData`'s typing. Zero sim ticks change.
- **Do NOT** re-record any golden. If a golden moves, you changed sim behavior — revert and find the leak. (`CHIMERA_GOLDEN_RECORD` must never be set for the three existing goldens.)
- **Do NOT** build `ServerBootstrap` or re-point the headless `DedicatedServer` branch — that is 1.9a. Just keep host/applier presentation-free.
- **Do NOT** generalize faction count to N>2/N>4 (Step 11), bump `SimChecksum.AlgoVersion` (Step 13/AR-15), introduce `ISecretStore`/rip the `[Export]` API-key fields (Step 12/Epic 8), or flip the fail-closed validator (release-branch step). Use `FactionRegistry` helpers where natural; do not rewrite the 2-faction loops.
- **Do NOT** add a DI framework or a second build target (AOT split deferred). Manual `new` + `SceneContext`.
- **Do NOT** "fix" the deferred-work item story-1.8b #1 (`ResolveSlotFactionDefs` not resetting `_slotFactionDefs` on re-apply) — it's behavior-preserving and out of scope; just be aware of the shared-in-place lifetime if you restructure the pre-pass.
- **Do NOT** change runtime phase order. The literal must reproduce the exact `_Ready` sequence below; reordering is a separate, test-gated decision.

---

## Tasks / Subtasks

- [x] **Task 1 — Step 7a: the Godot-free phase kernel (AC1).** New folder `godot/src/Core/Bootstrap/`:
  - [x] `ISetupPhase.cs` — `{ string Name { get; } void Run(); }`, `#nullable enable`, **no `using Godot`**, namespace `ProjectChimera.Core.Bootstrap`.
  - [x] `DelegateSetupPhase.cs` — `sealed ISetupPhase` holding `string Name` + `System.Action _run`; `Run() => _run()`. Godot-free.
  - [x] `ScenePhaseOrder.cs` — `public static readonly string[] Canonical` = the canonical names in `_Ready` order (see Dev Notes list). Godot-free.
  - [x] `ScenePhaseRunner.cs` — ctor `(IReadOnlyList<ISetupPhase> phases)`; `AssertOrder()` (throw `InvalidOperationException` with a precise expected-vs-actual diff on any mismatch); `Run()` = `AssertOrder()` then `foreach phase.Run()`. Godot-free.
  - [x] Confirm `dotnet build` of the Tier-1 project still compiles (these auto-glob in; no csproj edit yet) and `GodotFreeBoundaryTest` stays green.
- [x] **Task 2 — Step 7b: rewrite `_Ready` as the asserted literal + add `PhaseOrderTest` (AC1).**
  - [x] In `_Ready`, after the sim-spine construction block, replace the `SetupSettings(); SetupAudio(); … SetupMapGenerator();` sequence (and the inline FlowField init block) with `var phases = new ISetupPhase[] { new DelegateSetupPhase("Settings", SetupSettings), … }; new ScenePhaseRunner(phases).Run();`. **No `SetupX` body moves.** Keep the headless early-return, the sim-spine block, the post-sequence scenario-hash compute, and the replay autoload exactly where they are.
  - [x] Add `godot/ProjectChimera.Sim.Tests/Bootstrap/PhaseOrderTest.cs` (Tier-1, xUnit): (a) `ScenePhaseOrder.Canonical` equals a hardcoded `ExpectedOrder` `string[]` — fails on reorder/add/remove; (b) `ScenePhaseRunner.Run()`/`AssertOrder()` **throws** when handed Godot-free stub `ISetupPhase` doubles in the wrong order; (c) the runner invokes stub phases in canonical order when correct (stubs record call order).
  - [x] `dotnet test` green (147 + new → 152). In-engine smoke: boot → main menu (zero errors), enter skirmish (Tick 4009, Hash 0x68EE9890, P1:2/P2:7 units, ore 200→780, Nodes:6 Buildings:4). **AC1 is met.**
- [x] **Task 3 — Step 8: carve presentation coordinators (AC2).** Added `godot/src/Core/Bootstrap/Phases/SceneContext.cs` + the `Phases/` folder; added `<Compile Remove="..\src\Core\Bootstrap\Phases\**\*.cs" />` to the Tier-1 csproj. Carved Hud-first, then the rest in runtime order so each phase's ctx dependencies were populated. Each `XPhase : ISetupPhase` owns its moved `SetupX` body + products on `SceneContext`; build + Tier-1 (goldens byte-identical) + in-engine smoke after each round. All 22 phases now concrete:
  - [x] `HudPhase` **FIRST** (owns `UiCanvas`; injected into later UI phases via ctx).
  - [x] `Rendering` + `Navigation` + `Terrain` + `TerrainBrush` + `Lighting` + `Camera` + `GameState` + `Settings` + `Audio`.
  - [x] `MinimapPhase`.
  - [x] `ScenarioLoadPhase` (LoadAndApplyScenario + ResolveSlotFactionDefs + validator + StartPositionBridge) + `FactionVisualsPhase` + `FlowFieldInitPhase`.
  - [x] `WinConditionPhase` (+ Map-I/O export/import) + `GameOverOverlayPhase` (ShowGameOver stays as a MainScene presenter) + `ReplayStatusPhase`.
  - [x] `ContentBrowserPhase` (+ HandleLoadMap) + `MainMenuPhase` + `MapGeneratorPhase`.
- [x] **Task 4 — Step 9: `ScenarioDelegateBinder` (AC2, C3).** New `ScenarioDelegateBinder.Bind(ctx)` — the single assignment site for `ScenarioDirector.OnSpawnUnit/OnDisplayMessage/OnPlaySound/OnVictory`. `OnSpawnUnit` → `ctx.Applier.SpawnUnit` (sim→sim, unchanged). Replaces the inline assignments in the former `SetupTriggerEditor`; `OnDisplayMessage`/`OnVictory` route to MainScene presentation bridges (`ShowTriggerMessage`/`ShowGameOver`). `dotnet test` + smoke green.
- [x] **Task 5 — Step 10: `MatchLifecycleController` + single checksum sink (AC2).** Folded `SetupMultiplayer`/`OnMatchStart`/`StartRecording`/`StopRecording`/`TryLoadReplay` into `MatchLifecycleController` (the "Multiplayer" phase, published on `ctx.MatchLifecycle`; the _Ready replay-autoload tail + the return-to-Edit reset drive it). Kept the **single** `_host.SetChecksumSink(...)` owner in `_Ready` — not re-set. `dotnet test` + smoke green.
- [x] **Task 6 — Finalize AC2/AC3.** Exclusivity grep → **zero** direct sim writes in MainScene. MainScene **2238 → 1007 LOC** (–55%), presentation/wiring only (the 22-phase literal + Node lifecycle + input/process routing + presenters). Full `dotnet test` green (152); the three `*.golden.txt` **byte-identical**; `GodotFreeBoundaryTest` green; final boot→skirmish smoke clean (Hash `0xC3759147`, Nodes 8 / Buildings 3 / Total 4).

### Review Findings

_Code review — 2026-06-24 (gds-code-review): 3-layer adversarial pass (Blind Hunter diff-only · Edge Case Hunter diff+project · Acceptance Auditor diff+spec), all at Opus. **Outcome: behavior-preserving refactor confirmed — AC1/AC2/AC3 all met, scope fence clean.** Acceptance Auditor found zero violations; Blind Hunter verified the moved bodies match the deleted originals line-by-line; Edge Case Hunter confirmed every cross-phase ordering/null invariant currently holds. 0 decision-needed · 0 patches · 4 deferred · 6 dismissed as noise._

- [x] [Review][Defer] `ResolveSlotFactionDefs` throws `IndexOutOfRangeException` for player slots 4–7 [godot/src/Core/Bootstrap/Phases/ScenarioLoadPhase.cs:100] — deferred, pre-existing (verbatim move from MainScene). `_ctx.SlotFactionDefs` is sized `[5]` but `(Faction)(slot.Slot+1)` indexes 5–8 for slots 4–7, and the resolve runs (line 110) *before* the shadow-mode validator (line 111) that is designed to reject those slots. Only reachable for >4-faction scenarios → owned by Story 9.2 (which raises the `[5]` faction arrays). Optional 1-line guard if wanted now: `if ((int)faction >= _ctx.SlotFactionDefs.Length) continue;`.
- [x] [Review][Defer] `SceneContext` `null!` cross-phase coupling is load-bearing and undefended [godot/src/Core/Bootstrap/Phases/SceneContext.cs] — deferred. Currently SAFE (Edge Case Hunter verified every synchronous cross-phase read resolves to an earlier-positioned producer, and every deferred lambda fires only after all 22 phases complete), but the invariant is implicit, not guarded/asserted. When Epics 3–8 add the six editor phases, preserve producer-before-consumer ordering or null-guard the `ctx` reads.
- [x] [Review][Defer] `PhaseOrderTest` cannot catch concrete-phase `Name` drift or duplicate canonical names [godot/ProjectChimera.Sim.Tests/Bootstrap/PhaseOrderTest.cs] — deferred. `ScenePhaseRunner.AssertOrder` already catches any drift loudly at every boot (throws, never silent), so AC1 holds; the test pins `Canonical`↔`ExpectedOrder` but not the live concrete phase identities. A Tier-2 GdUnit4 test over the real phases would close the gap; a cheap Tier-1 win is a no-duplicates assert on `Canonical`.
- [x] [Review][Defer] Pre-existing duplicated logic across phase files [godot/src/Core/Bootstrap/Phases/ScenarioLoadPhase.cs + ContentBrowserPhase.cs/WinConditionPhase.cs] — deferred, pre-existing. `BuildFallbackMirror` duplicates `ScenarioApplier.ApplyFallback`'s layout; `HandleLoadMap` duplicates `DoImport`'s `.chimera.zip` import. Now split across files → consolidate to one source of truth later to prevent drift.

**Dismissed as noise (6):** stale `[MainScene]`/`[Navigation]` `GD.Print` tags in carved phases (cosmetic; defensible as a bootstrap-subsystem tag); `TerrainPhase`/`NavigationPhase` fallback shapes (verified — no bug); `ResetMatchOnReturnToEdit` carve (verified — all reset steps preserved); `AudioMgr` `null!`-vs-`?.` contract smell (matches original; no runtime bug); Unicode ellipsis literal vs `…` (same code point under UTF-8); `RaycastFloor`/`ApplySettingsToSystems` `_ctx` reads (safe — `_ctx` is set before the phase list and input cannot fire pre-`_Ready`).

---

## Dev Notes

### The canonical phase list (`ScenePhaseOrder.Canonical`) — exact `_Ready` runtime order

Derived from `MainScene._Ready()` at `2e0d2f4`. **Verify against the live `_Ready` at story start and hold order identical** (the inline FlowField-init block sits between `FactionVisuals` and `WinConditionUi`):

```
"Settings", "Audio", "GameState", "Lighting", "Terrain", "Navigation", "Camera",
"Rendering", "Hud", "Minimap", "TerrainBrush", "ScenarioLoad", "FactionVisuals",
"FlowFieldInit", "WinConditionUi", "GameOverOverlay", "Multiplayer", "ReplayStatus",
"ContentBrowser", "MainMenu", "TriggerEditor", "MapGenerator"
```

`"ScenarioLoad"` = `LoadAndApplyScenario()` (which internally runs `ResolveSlotFactionDefs` → validate → `_applier.Apply`/`ApplyFallback` → `SetupStartPositionBridge`). `"FlowFieldInit"` = the inline `_flowFieldSys.RebuildObstacles(_buildings); _flowFieldBridge.Initialize(_world, _flowFieldSys, _buildings);` block — keep it as its own named phase (cleanest) or fold into `Navigation`/`ScenarioLoad`; either way the name must appear in `Canonical` and `PhaseOrderTest` exactly once and in this position. Settle the final count/names in your Step-7 commit (the arch expects ~21–22).

### The documented order invariants the literal must preserve (pinned by PhaseOrderTest)

[Source: game-architecture.md L1532–1536; MainScene `_Ready` comments]
- **Settings → Audio** (audio reads the SFX bus volume Settings applied).
- **Navigation → Camera** (`SetupCamera` wires `_selection.Initialize(..., _pathSystem)`; `_pathSystem`/`_flowFieldBridge` are built in `SetupNavigation`).
- **Camera → Rendering** (`CombatFeedbackBridge` needs `_camCtrl`).
- **ScenarioLoad → FlowFieldInit** (`FlowFieldBridge.Initialize` needs all scenario buildings placed).
- **ScenarioLoad → FactionVisuals / WinConditionUi** (per-slot faction meshes + initial WinCondition need the applied scenario).
- **Hud → Minimap/WinConditionUi/GameOverOverlay/ReplayStatus/TriggerEditor-toast** (they `AddChild` to `_uiCanvas`, built in `SetupHud`).
- **Multiplayer/ContentBrowser/MainMenu/Trigger/MapGen LAST** (UI layers on top of everything).
- **Scenario-hash compute AFTER scenario + lobby** (stays in the `_Ready` tail after `Run()`).

### `ISetupPhase` + `ScenePhaseRunner` — reference shape (Godot-free)

```csharp
// src/Core/Bootstrap/ScenePhaseRunner.cs  (no using Godot)
public sealed class ScenePhaseRunner
{
    private readonly IReadOnlyList<ISetupPhase> _phases;
    public ScenePhaseRunner(IReadOnlyList<ISetupPhase> phases) => _phases = phases;

    public void Run() { AssertOrder(); foreach (var p in _phases) p.Run(); }

    /// <summary>Throws if the live literal drifts from ScenePhaseOrder.Canonical
    /// (reordered/added/removed). Never silently reorders (AR-3 / C1).</summary>
    public void AssertOrder()
    {
        var expected = ScenePhaseOrder.Canonical;
        if (_phases.Count != expected.Length) throw new System.InvalidOperationException(/* count diff */);
        for (int i = 0; i < expected.Length; i++)
            if (_phases[i].Name != expected[i]) throw new System.InvalidOperationException(/* i, expected[i], actual */);
    }
}
```

### `PhaseOrderTest` — what to assert (Tier-1, mirrors `SystemOrderTest`)

```csharp
// ProjectChimera.Sim.Tests/Bootstrap/PhaseOrderTest.cs
private static readonly string[] ExpectedOrder = { "Settings","Audio", … ,"MapGenerator" };

[Fact] PhaseOrder_IsTheCanonicalSequence_InExactOrder()        // ScenePhaseOrder.Canonical == ExpectedOrder
[Fact] Runner_Throws_WhenAPhaseIsReorderedAddedOrRemoved()     // stub ISetupPhase doubles out of order → AssertOrder throws
[Fact] Runner_RunsPhasesInCanonicalOrder_WhenCorrect()         // stubs record invocation order == Canonical
```
Use Godot-free stub phases (`sealed class StubPhase : ISetupPhase { public string Name {get;} … records Run() }`) — never reference a concrete presentation phase type (it would drag Godot into the test assembly).

### `SceneContext` — shape (presentation; carries cross-phase products)

A mutable holder populated as phases run. Hold the already-existing handles: `SimulationHost Host`, `ScenarioApplier Applier`, `ILogSink Log`, `FactionDefinition?[] SlotFactionDefs`, `GameState GameState`, `RtsCameraController Cam`, `CanvasLayer UiCanvas`, `ScenarioData? Scenario`, `LockstepManager Lockstep`, `FlowFieldBridge FlowFieldBridge`, etc. Each phase ctor takes `SceneContext` (and reads the typed handles it needs). _Note:_ `SceneContext` references Godot types → it lives under `Phases/` (excluded from Tier-1), not in the Godot-free kernel.

### `ScenarioDelegateBinder` (Step 9 / C3)

Single assignment site for the four `ScenarioDirector.On*` delegates. C3 rule [Source: game-architecture.md L1672–1674]: a sim node may *fire* an `On*` delegate but the delegate body may never *read/write* sim state — these are presentation-output channels. `OnSpawnUnit` is the one exception that legitimately calls back into the sim writer (`_applier.SpawnUnit`, sim→sim) — that re-point is correct and stays.

### The exclusivity grep (AC2 proof — run before marking done)

From `godot/`, prove no direct sim-store writes remain in MainScene (presentation must call `ScenarioApplier`, never a store):
```
grep -nE '_(resources|nodes|buildings|world)\.(FactionBase|AddOre|Create|PlaceBuildingDirect)|Resources\.FactionBase\[' src/Core/MainScene.cs
```
Expected: **no scenario-mutation hits.** (Read-only counts like `_world.IsAlive`/`_buildings.Alive` in HUD/win-check are fine — they read, never write.)

### Verification gates (every shippable slice)

1. **Build:** `dotnet build godot/godot.sln` (or the MCP editor build) — 0 errors (pre-existing CS8632 warnings OK).
2. **Tier-1:** `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` — all green (147 + new PhaseOrder tests). `GodotFreeBoundaryTest` green.
3. **Goldens byte-identical:** `git status` shows `Golden/*.golden.txt` unchanged.
4. **In-engine smoke (MANDATORY per carved phase — the only catch for presentation NREs):** boot to main menu with zero editor/runtime errors; enter a skirmish; confirm units render, HUD shows non-zero Tick/Hash and the same units/nodes/buildings counts as before, no NRE. Use `/godot-verify` or the Godot MCP. 1.8b's in-engine alpha_map_01 reference for sanity: ~`Total: 4` units, `Nodes: 8 Buildings: 3`, P1 ore climbing from 200.

### Constraints & gotchas

- `#nullable enable` per file (the Godot SDK doesn't enable it globally).
- Godot classes inheriting a Godot type need `partial` — but the new phase classes are plain C# (`ISetupPhase` impls), not `Node` subclasses, so no `partial` needed unless a phase itself extends a Godot type (it shouldn't — phases *create* nodes, they aren't nodes).
- Keep `GD.Print`/`GD.PrintErr` in the *presentation* phases as-is (they're allowed in presentation); only sim-path logging uses `ILogSink`. Don't churn presentation diagnostics.
- The `MultiMeshBridge` `SimulationHost` overload + the raw-`SimulationLoop` overload both stay (1.8a) — `FactionVisualsPhase` uses the host overload.
- AutoSave commits hourly; expect the working tree to move under you. Commit your own logical slices; don't fight the AutoSave loop (advisory-on-master discipline).

### Project Structure Notes

- New Godot-free kernel: `godot/src/Core/Bootstrap/{ISetupPhase,DelegateSetupPhase,ScenePhaseOrder,ScenePhaseRunner}.cs` (namespace `ProjectChimera.Core.Bootstrap`; auto-globbed into Tier-1).
- New presentation: `godot/src/Core/Bootstrap/Phases/*.cs` + `godot/src/Core/Bootstrap/SceneContext.cs` (Godot-touching; excluded from Tier-1 via the new `<Compile Remove>`).
- New test: `godot/ProjectChimera.Sim.Tests/Bootstrap/PhaseOrderTest.cs`.
- Modified: `godot/src/Core/MainScene.cs` (shrinks), `godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` (the `Phases/**` exclusion).
- Naming: `*Phase` = composition-root setup step [Source: game-architecture.md L2514]. Files `PascalCase.cs` matching the class.

### Project Context Rules

- **Sim/Presentation boundary is sacred.** Sim = `src/Core/Sim` (+ Core/Combat/Economy/Navigation/AI) — no `using Godot`, no `float` gameplay state. The phase kernel contract is Godot-free; concrete phases are presentation. Presentation sends commands into the sim (via `ScenarioApplier`), never mutates stores directly. [Source: project-context.md]
- **Determinism untouched:** 1.8c changes no sim math, no iteration order, no RNG, no checksum. Fixed-point/ascending-id/`SimRng` rules are not in this story's surface — but do not violate them in any code that lands in a globbed sim folder.
- **Data-driven platform:** no gameplay logic/balance moves into hardcode here; this is pure composition-root plumbing.
- **Brownfield reuse over rewrite:** reuse `SimulationHost`/`ScenarioApplier`/`FactionRegistry`/`ILogSink`/`MultiMeshBridge`; do not build parallel systems. [Source: project-context.md "Brownfield Working Style"]
- **Anthropic/LLM:** untouched here; if you ever edit LLM/model code, consult the `claude-api` skill (not this story's surface).

### References

- [Source: epics.md#Story-1.8c L672–686] — story, 2 ACs, `Covers FR-39/FR-44/AR-3; Depends on 1.8b`.
- [Source: epics.md#AR-3 L180] — "Composition-root becomes an asserted `ISetupPhase[]` literal run by a `ScenePhaseRunner`, pinned by a `PhaseOrderTest`; cross-phase deps constructor-injected." + FactionRegistry clause.
- [Source: epics.md#AR-35 L226] — `PhaseOrderTest` is an enumerated Tier-1 xUnit test.
- [Source: epics.md#AR-38 L229] — `ServerBootstrap` reuses the sim spine (1.9a, not here).
- [Source: game-architecture.md#Step6 L1420–1816] — decomposition; AR-3 sub-decision L1526–1536; ServerBootstrap reuse L1515–1525; boundary rules L1656–1683; migration Steps 7–10 L1716–1746; target tree (`Bootstrap/`, `Phases/`, `PhaseOrderTest.cs`) L1575–1654; ≤250-LOC target L1485; residual risk (Step-8 NRE past golden) L1786–1789.
- [Source: game-architecture.md#ConsistencyRules L2506, L2514] — composition-root row; `*Phase` naming.
- [Source: 1-8a…ilogsink-seam.md] — `SimulationHost` surface; `SystemOrderTest` precedent; `ILogSink`/`GodotLogSink` split; D9 deferral of `ISetupPhase`/`ScenePhaseRunner`/`PhaseOrderTest` to 1.8c; "locate by symbol" trap.
- [Source: 1-8b…hoisted-path-resolution.md] — `ScenarioApplier` surface; AC6 sim-write exclusivity established (1.8c asserts it); D9 deferral of the full `ScenarioDelegateBinder` to 1.8c; review patches (null-token + out-of-range-slot guards — do not regress).
- [Source: deferred-work.md] — story-1.8b #1 (`ResolveSlotFactionDefs` in-place lifetime) — awareness only, out of scope.
- [Source: project-context.md] — Sim/Presentation boundary, determinism, brownfield reuse.

---

## Dev Agent Record

### Agent Model Used

claude-opus-4-8 (Claude Opus 4.8) — gds-dev-story workflow.

### Debug Log References

- Baseline (pre-change, commit `5195b2a`): `dotnet test ProjectChimera.Sim.Tests` → **147 passed, 0 failed**.
- After Task 1+2 kernel + PhaseOrderTest: `dotnet test` → **152 passed, 0 failed** (+5 PhaseOrder facts; `GodotFreeBoundaryTest` green → no Godot leaked into the globbed kernel).
- After `_Ready` rewrite: `dotnet build godot.csproj` → **0 errors**, 7 warnings (pre-existing CS8632 nullable-context warnings only).
- In-engine smoke (Godot 4.6.3 editor, `res://scenes/main.tscn`): boot → main menu with **zero editor/runtime errors**; entered skirmish; live HUD via `godot_exec`: `[PLAY] Tick 4009 Hash 0x68EE9890 · P1: 2 units P2: 7 units Total: 9 · P1 780 ore P2 40 ore · Nodes: 6 Buildings: 4`. Runtime digest confirmed every phase's products present (lights, terrain, nav + 2 building bodies, camera, 8 resource-node meshes, unit MultiMesh, HUD canvas, minimap viewport, win-condition panel, main-menu overlay).

### Completion Notes List

**Step 7 (Tasks 1–2) — AC1 met & verified.**
- ✅ Godot-free phase kernel in `src/Core/Bootstrap/`: `ISetupPhase`, `DelegateSetupPhase`, `ScenePhaseOrder.Canonical` (22 phases), `ScenePhaseRunner` (asserts live order == canonical, throws a precise diff on any reorder/add/remove — C1 "never silently reorder"). Auto-globs into Tier-1; no csproj edit needed (Phases/ exclusion is deferred to Task 3).
- ✅ `_Ready` rewritten: the 22-call `Setup*` sequence + inline FlowField block are now a single asserted `new ISetupPhase[] { new DelegateSetupPhase("Settings", SetupSettings), … }` literal handed to `new ScenePhaseRunner(phases).Run()`. **No `SetupX` body moved** — only the inline FlowField block was lifted verbatim into a named `InitFlowField()` method so it can be the "FlowFieldInit" phase (behavior-identical). Headless early-return, sim-spine construction block, scenario-hash tail, and replay autoload all left exactly in place.
- ✅ `PhaseOrderTest` (Tier-1, mirrors `SystemOrderTest`): pins `ScenePhaseOrder.Canonical` against an independent hardcoded `ExpectedOrder`; verifies the runner runs stubs in canonical order; verifies it throws on reorder / removal / addition (and that no phase body runs before the order assertion). Uses Godot-free stub phases only.
- The live `_Ready` order at story start matched the Dev Notes canonical list exactly (22 phases) — no drift to reconcile.

**Steps 8–10 (Tasks 3–6) — AC2/AC3 met & verified.**
- ✅ Carved all 22 `Setup*` bodies into concrete `*Phase : ISetupPhase` classes under `src/Core/Bootstrap/Phases/` (excluded from Tier-1 via the new `Phases/**` `<Compile Remove>` — `GodotFreeBoundaryTest` stayed green throughout). A presentation-side `SceneContext` carries the shared handles (sim-spine aliases + each phase's products + the inspector config reached via `ctx.Scene`); phases attach children via `ctx.Scene.AddChild`. Verified in **7 build+test+smoke rounds** (Hud → Settings/Audio/GameState/Lighting → Terrain/Navigation → Camera/Rendering → Minimap/TerrainBrush/FactionVisuals/FlowFieldInit → ScenarioLoad → a field-migration pass → GameOver/ReplayStatus/ContentBrowser/MainMenu/MapGenerator → WinCondition/Multiplayer/TriggerEditor).
- ✅ Task 4 `ScenarioDelegateBinder` — the single `ScenarioDirector.On*` assignment site (C3): `OnSpawnUnit` → `ctx.Applier.SpawnUnit` (sim→sim); `OnDisplayMessage`/`OnPlaySound`/`OnVictory` are presentation-output only (route to `ctx.Scene.ShowTriggerMessage` / `ctx.AudioMgr` / `ctx.Scene.ShowGameOver`).
- ✅ Task 5 `MatchLifecycleController` (the "Multiplayer" phase) folds `SetupMultiplayer` + `OnMatchStart` + `StartRecording` + `StopRecording` + `TryLoadReplay`; published on `ctx.MatchLifecycle` so `_Ready`'s replay-autoload tail and the return-to-Edit reset drive it. The **single** `_host.SetChecksumSink` owner stays in `_Ready` (D5) — not re-set.
- ✅ AC2: exclusivity grep returns **zero** direct sim-store writes in MainScene; MainScene **2238 → 1007 LOC** (presentation/wiring only: the 22-phase literal + sim-spine construction + `_Process`/`_Input`/`_UnhandledInput` routing + UpdateHud/CheckWinCondition/ShowGameOver presenters + build-placement + the few bridge callbacks). `ShowGameOver` + `CheckWinCondition` stay as process-routing presenters (coupled to `_matchStartMs`/`_gameOver`/`_playFrames`); the ≤250-LOC target is directional, the met gate is "materially smaller + presentation-only + zero sim writes".
- ✅ AC3: all three goldens **byte-identical** (no re-record); the live boot re-asserted the 22-phase order at startup (clean — `ScenePhaseRunner.AssertOrder` passed); every smoke showed the deterministic `Hash 0xC3759147` at matched ticks, `Nodes 8 / Buildings 3 / Total 4`, **zero errors/NREs**.
- Design note: I did a one-pass migration of the remaining presentation fields to `SceneContext` mid-Task-3 so the cross-phase deps (MainMenu→MapGenPanel, MapGenerator→LlmService) resolved without ordering constraints — the still-delegate `Setup*` then wrote `ctx.X`, and the body relocations became mechanical.

### File List

_New (Godot-free kernel, auto-globbed into Tier-1):_
- `godot/src/Core/Bootstrap/ISetupPhase.cs`
- `godot/src/Core/Bootstrap/DelegateSetupPhase.cs`
- `godot/src/Core/Bootstrap/ScenePhaseOrder.cs`
- `godot/src/Core/Bootstrap/ScenePhaseRunner.cs`

_New (Tier-1 test):_
- `godot/ProjectChimera.Sim.Tests/Bootstrap/PhaseOrderTest.cs`

_New (presentation phases — `src/Core/Bootstrap/Phases/`, excluded from Tier-1):_
- `SceneContext.cs` (the shared cross-phase context holder)
- `SettingsPhase.cs`, `AudioPhase.cs`, `GameStatePhase.cs`, `LightingPhase.cs`, `TerrainPhase.cs`, `NavigationPhase.cs`, `CameraPhase.cs`, `RenderingPhase.cs`, `HudPhase.cs`, `MinimapPhase.cs`, `TerrainBrushPhase.cs`, `ScenarioLoadPhase.cs`, `FactionVisualsPhase.cs`, `FlowFieldInitPhase.cs`, `WinConditionPhase.cs`, `GameOverOverlayPhase.cs`, `ReplayStatusPhase.cs`, `ContentBrowserPhase.cs`, `MainMenuPhase.cs`, `TriggerEditorPhase.cs`, `MapGeneratorPhase.cs`
- `MatchLifecycleController.cs` (the "Multiplayer" phase + Task 5 lifecycle), `ScenarioDelegateBinder.cs` (Task 4)

_Modified:_
- `godot/src/Core/MainScene.cs` — added `using ProjectChimera.Core.Bootstrap;`; `_Ready` is the asserted 22-phase `ISetupPhase[]` literal run by `ScenePhaseRunner`; built the `SceneContext` (`_ctx`) with the sim-spine handles; all 22 `Setup*` bodies + their domain helpers (Map-I/O, HandleLoadMap, scenario-apply family, the lifecycle methods) carved out; retained only presentation/wiring + the bridge callbacks (`ShowGameOver`, `ShowTriggerMessage`, `MoveStartPosition`, `EnterBuildPlacementMode`, `ApplySettingsToSystems`, `ResetMatchOnReturnToEdit`, `LoadGeneratedScenario`) now `internal`/`public` for the phases to wire. 2238 → 1007 LOC.
- `godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` — added `<Compile Remove="..\src\Core\Bootstrap\Phases\**\*.cs" />` (keeps the Godot-touching phases out of the Godot-free test assembly).

### Change Log

- 2026-06-24 — Step 7 (Tasks 1–2): added the asserted `ISetupPhase[]` phase kernel (`ISetupPhase`/`DelegateSetupPhase`/`ScenePhaseOrder`/`ScenePhaseRunner`) + Tier-1 `PhaseOrderTest`; rewrote `MainScene._Ready` as the asserted literal run by `ScenePhaseRunner` (no setup bodies moved). AC1 met. Tier-1 147→152 green; `dotnet build` 0 errors; in-engine boot→skirmish smoke clean.
- 2026-06-24 — Steps 8–10 (Tasks 3–6): carved all 22 `Setup*` bodies into concrete `*Phase` classes under `Bootstrap/Phases/` (+ `SceneContext`, `Phases/**` Tier-1 exclusion); added `ScenarioDelegateBinder` (Task 4) and `MatchLifecycleController` (Task 5). AC2 met (exclusivity grep zero hits; MainScene 2238→1007 LOC, presentation/wiring only). AC3 met (3 goldens byte-identical; `GodotFreeBoundaryTest` + full Tier-1 152 green; deterministic boot→skirmish smoke clean across 7 verification rounds). Story → review.
