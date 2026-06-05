# Architecture — Project Chimera

> Grounded in the code as it exists (deep scan, 2026-06-05). Where the codebase deviates from its own stated rules, that is called out as a **brownfield note** rather than hidden.

## 1. The One Rule Everything Serves

**Simulation and Presentation are separate layers, and the boundary is sacred.**

```
┌──────────────────────────────────────────────────────────────┐
│  PRESENTATION LAYER  (Godot Nodes — src/UI, src/CreationSuite,│
│  scenes)                                                       │
│  • MultiMeshInstance3D rendering, HUD, camera, selection      │
│  • Reads sim arrays every frame, interpolates between ticks   │
│  • Sends COMMANDS (intents) down into the sim                 │
└───────────────▲───────────────────────────────┬──────────────┘
        reads sim state (one-way)        commands / intents
                │                                ▼
┌───────────────┴───────────────────────────────────────────────┐
│  SIMULATION LAYER  (pure C# — src/Core, Combat, Economy,       │
│  Navigation)                                                   │
│  • EntityWorld (Struct-of-Arrays), fixed-point math           │
│  • Deterministic, no Godot types, no float gameplay state     │
│  • Owns ALL gameplay truth                                     │
└───────────────────────────────────────────────────────────────┘
```

- **Simulation layer** = `src/Core/`, `src/Combat/`, `src/Economy/`, `src/Navigation/` (plus sim-side AI and lockstep logic). **No `using Godot;`. No Godot Node types. No `Vector3`/`float` for gameplay state.**
- **Presentation layer** = `src/UI/`, scenes, MultiMesh rendering. Godot Nodes live here. It **reads** sim arrays each frame; it never owns gameplay truth.
- **Data flows one way:** sim → presentation. Presentation sends commands back into the sim; it never mutates sim state directly.

If you are tempted to put a `Node`, a `signal`, `GD.Print`, or a `float` position into `src/Core` — stop. It belongs in presentation, or it must be converted to `Fixed`.

## 2. Determinism (the contract that silently breaks multiplayer if violated)

| Rule | Where |
|---|---|
| All sim math uses the **`Fixed`** struct — a custom **16.16 fixed-point** type (named `Fixed`, *not* `FixedPoint`) | `src/Core/FixedPoint.cs` |
| `Fixed.FromFloat` is **load-time authoring only** — never call it inside the tick loop | combat/data init only (e.g. `DamageMatrix` static ctor) |
| **Process entities in ascending ID order** — iteration order is part of the deterministic contract | `EntityWorld` SoA, all `ISimSystem`s |
| No wall-clock time, no unseeded `Random`, no Godot physics for gameplay collision (use `SpatialHash`) | sim layer |
| Lockstep input delay starts at 4 ticks, adapts via RTT, clamped **[2, 12]** | `src/Multiplayer/LockstepManager.cs` |
| World-state **checksums** computed every `ChecksumInterval` ticks (default 60) to detect desync | `SimulationLoop`, `SimChecksum.cs` |

## 3. Entity Model — Struct-of-Arrays (`src/Core/EntityWorld.cs`)

Entity data is **SoA**, not arrays-of-objects. Every per-entity attribute is a parallel array indexed by entity id, sized `MAX_ENTITIES = 4096`, with a **free list** recycling dead slots.

Representative parallel arrays:

```
Flags[]  Position[]  PrevPosition[]  Velocity[]  Speed[]  Health[]  MaxHealth[]
FactionOf[]  MoveTarget[]  AttackTarget[]  AttackCooldown[]  AttackRange[]
AttackDamage[]  AttackSpeed[]  DamageTypeOf[]  ArmorTypeOf[]  VisionRange[]
SplashRadius[]  SupplyCost[]  CommandState[]  CommandGoal[]
GatherState[]  GatherTarget[]  CarryAmount[]  CarryCapacity[]  BuildTarget[]
```

Key per-entity enums (all in `EntityWorld.cs`):
- `UnitCommand` — `Idle, Move, AttackMove, Stop, HoldPosition, Build`
- `GatherState` — `Inactive, Idle, MovingToResource, Gathering, MovingToBase`
- `EntityFlags` — `Alive, Moving, Attacking` (bit flags)
- `Faction` — `Neutral, Player1..Player4`

**Adding a new per-entity field = add a new parallel array** here; do **not** introduce per-entity classes.

**Buildings are not in `EntityWorld`.** They live in `BuildingStore` (separate SoA) because they don't move or attack. Resources live in `ResourceStore` / `ResourceNodeStore`. Reuse these stores rather than building parallel ones.

## 4. The Simulation Loop (`src/Core/SimulationLoop.cs`)

A fixed-timestep loop at **`TICKS_PER_SECOND = 30`**. It accumulates real delta and steps the sim in fixed `FixedDt` (= 1/30) increments, exposing `InterpolationAlpha` for the presentation layer to smooth 30 Hz sim into 60 FPS render.

Systems implement `ISimSystem.Tick(EntityWorld world, Fixed dt)` and are run **in registration order**. The order is itself part of the design (declared in `MainScene.cs`):

```
1. BuildingSystem        — construction timers, production queues
2. GatheringSystem       — worker gather state machine, ore delivery
3. MovementSystem        — apply velocity / steering to positions
4. CombatSystem          — target acquisition, attack resolution
5. ProjectileSystem      — projectile travel + impact / splash
6. SupplySystem          — recompute dynamic supply cap
7. FogOfWarSystem        — stamp vision into the fog grid
8. AiOpponentSystem      — utility-AI decisions (sees post-supply state)
9. ScenarioDirector      — evaluate triggers LAST (sees fully-updated world)
```

> **Why AI and ScenarioDirector run last:** they need the fully-updated supply caps, construction states, and combat outcomes of the same tick.

`SimulationLoop.EnableChecksums(buildings, resources)` wires building + resource stores into the periodic checksum; `OnChecksum(tick, checksum)` is the hook `MainScene` uses to compare with a remote peer.

## 5. Combat Model (`src/Combat/`)

Damage formula (do not re-derive it):

```
final = base × matrix[damageType][armorType] − armorValue
```

`DamageMatrix.Get(DamageType, ArmorType)` returns a `Fixed` multiplier. Damage types: `Normal, Pierce, Siege, Magic`. Armor types: `Unarmored, Light, Medium, Heavy, Fortified` (Fortified = buildings).

Current multiplier table (`src/Combat/DamageMatrix.cs`):

| dmg \\ armor | Unarmored | Light | Medium | Heavy | Fortified |
|---|---|---|---|---|---|
| **Normal** | 1.00 | 1.00 | 0.75 | 0.50 | 0.35 |
| **Pierce** | 1.50 | 1.00 | 0.75 | 0.35 | 0.25 |
| **Siege**  | 0.50 | 0.50 | 1.00 | 1.00 | 1.50 |
| **Magic**  | 1.00 | 1.00 | 1.00 | 1.00 | 0.50 |

> **Brownfield note:** the matrix is currently **hardcoded** in `DamageMatrix.cs`, with an in-code comment that it should be loaded from JSON ("Data-driven: load from JSON in Phase 1. Defaults are hardcoded here for Phase 0"). This is a known deviation from the project's "never hardcode balance" rule and a natural target for the data-driven migration. (The GDD also references a `Hero` damage/armor pair that is **not** present in the current enum — current code has 4 damage × 5 armor.)

Combat events flow through a `CombatEventQueue`; the presentation side drains it via `CombatFeedbackBridge` for hit flashes / floaters. Projectiles are their own SoA (`ProjectileStore`).

## 6. Navigation (`src/Navigation/`)

- **Flow-field pathfinding is the live, deterministic path system.** `FlowFieldComputer` builds fields; `FlowFieldSystem`/`FlowField` integrate them; `MovementSystem` steers units along them.
- The presentation bridge is `src/UI/FlowFieldBridge.cs` — route new pathing work through it.
- `src/UI/PathRequestSystem.cs` is a **kept-but-unused fallback** (it also owns the Move→Stop transition to avoid a historical stutter bug). Do not build new work on it.
- Uses **`NavigationServer3D` direct API** (`MapGetPath()`); **never** `NavigationAgent3D` nodes.
- `SpatialHash` provides deterministic neighbor queries (used by combat target acquisition and steering) instead of Godot physics.

## 7. Economy (`src/Economy/`)

- **Two default resources:** Ore (abundant) + Crystal (scarce). Two is a deliberate ceiling — creators add more via data, not core code.
- **Dynamic supply cap:** `base 10 + 10 per alive CommandCenter` (`SupplySystem`). `TrainUnit()` supply-gates before deducting ore.
- `GatheringSystem` runs the worker state machine; `BuildingSystem` runs construction timers and production queues. Worker-placed construction: worker walks to site (`UnitCommand.Build` + `BuildTarget[]`), the building ticks its own construction timer, and worker arrival resumes gathering.

## 8. Multiplayer & Lockstep (`src/Multiplayer/`)

- **Deterministic lockstep** — peers exchange only commands and re-simulate. `LockstepManager` is **pure C#** (no Godot dependency); it bridges into the scene via delegates (`OnRequestPath`, `OnRequestAttackMove`, `OnCancelPath`).
- **Adaptive input delay:** starts at `INPUT_DELAY = 4`, adjusts from measured RTT (Ping/Pong) via negotiated `DelayProposal` packets; target = `ceil(one-way-latency / 33ms) + 1`, clamped **[2, 12]**. Both peers must agree before a change applies. `CurrentDelay` is public for HUD display.
- **Transport:** `ENetTransport` (P2P/LAN) and `ServerTransport`/`DedicatedServer` (headless). `NakamaService` does matchmaking (2-player, `game=chimera_1v1`; faction assigned by server).
- **Replays:** `.chmr` binary format. `ReplayRecorder` auto-starts on match start; `ReplayPlayer` re-applies stored orders by re-simulating.
- Headless/server detection: `DisplayServer.GetName() == "headless"`.

## 9. AI & LLM (`src/AI/`)

- `AiOpponentSystem` — **utility-AI** opponent (replaced the old 3-phase FSM). Scores candidate actions (build barracks, expand for supply, train, attack-wave) each tick; runs last in the sim loop. Difficulty (`AiLevel` export): Easy / Normal / Hard tune unit-count and timing thresholds.
- `LLMService` — Claude API with Ollama local fallback. Powers **two creator features**: LLM trigger scripting and AI map generation. Includes multi-pass validation pipelines (5-pass for triggers, 7-pass for scenarios) that reject out-of-bounds or schema-invalid output before it touches the game.
- **API keys** are read from the `AnthropicApiKey` **export on MainScene** (set in the Godot Inspector) — never hardcoded, never committed. When editing anything Anthropic-related, consult the `claude-api` skill.

## 10. Rendering & Presentation (`src/UI/`)

- **All unit rendering via `MultiMeshInstance3D`** — never one `MeshInstance3D` per unit. Two MultiMesh nodes per faction (separate team colors). `MultiMeshBridge` sets instance transforms every frame from sim positions, interpolating between 30 Hz ticks.
- The `src/UI/*Bridge.cs` classes are the canonical sim→presentation readers (`MultiMeshBridge`, `MinimapBridge`, `FogOfWarBridge`, `ProjectileBridge`, `BuildingBridge`, `ResourceNodeBridge`, `CombatFeedbackBridge`, `StartPositionBridge`, `FlowFieldBridge`). See [component-inventory.md](./component-inventory.md).
- **Fog of war:** 128×128 byte grid (`FogOfWarSystem`), uploaded each frame as an R8 `ImageTexture` by `FogOfWarBridge`.
- `MainScene.cs` (~2,200 LOC) is the composition root: it constructs every store/system, builds the `SimulationLoop`, and runs ~25 `SetupXxx()` wiring methods. It is the place to look first to understand how anything is connected.

## 11. Bootstrap & Entry Point

- **Scene entry:** `project.godot` → `run/main_scene = res://scenes/main.tscn`.
- **Code entry:** `MainScene.cs` `_Ready()` builds stores → builds `SimulationLoop` → runs all `Setup*()` methods. `_Process` drives the loop: replay flush / lockstep flush / `Update(delta)` depending on mode.
- **Autoload:** `MCPGameBridge` (`addons/godot_mcp`) for editor inspection/run/screenshots.

## Known Architectural Tensions (brownfield reality)

1. **`DamageMatrix` is hardcoded** despite the data-driven mandate (see §5) — flagged in-code for migration.
2. **`tests/` is empty** — GdUnit4 is the stated convention and sim code is designed to be testable without Godot, but no committed tests exist yet.
3. **GDD vs. code drift** — the GDD references future targets (".NET 9 AOT", a `Hero` damage/armor class) not present in current code. Treat `project-context.md` + this doc as the as-built truth; the GDD as intent.
