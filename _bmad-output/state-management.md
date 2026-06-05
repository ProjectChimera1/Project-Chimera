# State Management — Project Chimera

> For a game project, "state management" means: where authoritative game state lives, who may mutate it, and how it flows to the screen and across the network. (This is distinct from web "state libraries" — there is no Redux/MobX here; state is hand-managed SoA stores.)

## The Golden Rule

**All authoritative gameplay state lives in the Simulation layer and is owned by the simulation stores. Presentation never holds gameplay truth — it only reads and renders.**

```
            ┌─── authoritative state (sim, deterministic) ───┐
            │  EntityWorld   BuildingStore   ResourceStore    │
            │  ResourceNodeStore   ProjectileStore   Fog grid │
            └──────────────▲───────────────────────┬─────────┘
                  read each frame          mutated ONLY by
                  (one-way)                ISimSystems on tick
                           │                        │
        ┌──────────────────┴────┐        ┌──────────┴──────────┐
        │  Presentation reads    │       │  Commands / intents  │
        │  via *Bridge classes   │       │  enter via lockstep  │
        └────────────────────────┘       └─────────────────────┘
```

## Where State Lives

| State | Owner (store) | Mutated by | Read by |
|---|---|---|---|
| Per-unit position/health/orders/gather/combat | `EntityWorld` (SoA arrays) | sim systems (Movement/Combat/Gathering) | `MultiMeshBridge`, `MinimapBridge`, `SelectionSystem` |
| Buildings (placement, construction, production) | `BuildingStore` | `BuildingSystem`, `ScenarioDirector` | `BuildingBridge` |
| Resource balances (ore, crystal, supply cap) | `ResourceStore` | `GatheringSystem`, `SupplySystem`, `BuildingSystem` | HUD, `CommandCardSystem` |
| World resource nodes | `ResourceNodeStore` | `GatheringSystem` | `ResourceNodeBridge` |
| Projectiles | `ProjectileStore` | `ProjectileSystem` | `ProjectileBridge` |
| Fog of war (128×128 byte grid) | `FogOfWarSystem` | `FogOfWarSystem` (per tick) | `FogOfWarBridge` |
| Combat events (transient) | `CombatEventQueue` | `CombatSystem`/`ProjectileSystem` | `CombatFeedbackBridge` (drains) |
| Match statistics | `MatchStats` | combat/economy systems | HUD / end screen |
| Simulation tick + checksum | `SimulationLoop` | the loop itself | lockstep desync check, HUD |

## Mutation Discipline (who is allowed to write)

1. **Only `ISimSystem.Tick()` implementations mutate sim stores**, and only during a tick. The system execution order (see [architecture.md](./architecture.md) §4) defines a deterministic write sequence.
2. **Entities are processed in ascending ID order.** Iterating a `Dictionary`/`HashSet` in sim code is forbidden — order is part of the deterministic contract.
3. **Presentation issues intents, not mutations.** A click does not move a unit; it produces a command (`UnitCommand`/`NetworkCommand`) that, through lockstep, becomes a sim input on a future tick.

## Command / Intent Flow (presentation → sim)

```
player click ─▶ SelectionSystem / CommandCardSystem
            ─▶ FlowFieldBridge.OnRequestPath / OnRequestAttackMove (delegates)
            ─▶ LockstepManager schedules the command at (currentTick + inputDelay)
            ─▶ on that tick, command applied to EntityWorld (CommandState[], CommandGoal[], MoveTarget[])
            ─▶ sim systems act on it deterministically
```

- Input delay starts at 4 ticks and adapts to RTT, clamped [2, 12]. In offline/replay mode the same path is used with delay = 0 / replay-driven.
- Because both peers apply identical commands on identical ticks to identical starting state, the simulations stay bit-identical — verified by periodic checksums.

## Cross-Frame Rendering State (presentation-only)

The presentation layer keeps its **own** non-authoritative state purely for smooth rendering:
- `EntityWorld.PrevPosition[]` + `SimulationLoop.InterpolationAlpha` let bridges interpolate unit transforms between 30 Hz sim ticks for 60 FPS visuals.
- Camera, selection highlights, UI panel open/closed, audio bus volumes (`SettingsManager`) — all presentation-local, never fed back into the sim.

## Persisted State (outside the tick)

| What | Where | Class |
|---|---|---|
| User settings (graphics, audio, controls) | local profile | `SettingsManager` + `SettingsData` |
| Scenarios / maps | `resources/data/scenarios/*.json` (+ `ai_generated.json`) | `ScenarioData` / `ScenarioSerializer` |
| Factions | `resources/data/factions/*.json` | `FactionDefinition` |
| Content packages | `.chimera.zip` | `ContentPackager` / `ContentPackageManifest` |
| Replays | `.chmr` binary | `ReplayRecorder` / `ReplayPlayer` |
| LLM API key | Godot Inspector export on MainScene (`AnthropicApiKey`) — **never** committed | `MainScene` / `LLMService` |

## Anti-Patterns to Avoid (enforced by this architecture)

- ❌ Storing gameplay state in a Godot Node and treating it as truth.
- ❌ Mutating a sim array directly from a UI/input handler.
- ❌ Using `float`/`Vector3` for any value that affects gameplay outcome (use `Fixed`/`FixedVec3`).
- ❌ Reading wall-clock time or unseeded `Random` in the sim.
- ❌ Introducing a per-entity class instead of a new parallel SoA array.
