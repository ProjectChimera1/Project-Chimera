# Component Inventory — Project Chimera

> Every system/class by layer (deep scan, 2026-06-05). "Component" here means a discrete system, store, bridge, or service class — this is a code/game project, not a UI-widget library, so the inventory is organized by responsibility.

## Simulation Systems (`ISimSystem` — run each tick, in this order)

| # | Class | File | Responsibility |
|---|---|---|---|
| 1 | `BuildingSystem` | `Economy/BuildingSystem.cs` | Construction timers, production queues, tech gating |
| 2 | `GatheringSystem` | `Economy/GatheringSystem.cs` | Worker gather state machine, ore delivery |
| 3 | `MovementSystem` | `Navigation/MovementSystem.cs` | Apply steering/velocity to positions |
| 4 | `CombatSystem` | `Combat/CombatSystem.cs` | Target acquisition + attack resolution |
| 5 | `ProjectileSystem` | `Combat/ProjectileSystem.cs` | Projectile travel, impact, splash |
| 6 | `SupplySystem` | `Economy/SupplySystem.cs` | Recompute dynamic supply cap |
| 7 | `FogOfWarSystem` | `Core/FogOfWarSystem.cs` | Stamp vision into 128×128 fog grid |
| 8 | `AiOpponentSystem` | `AI/AiOpponentSystem.cs` | Utility-AI opponent decisions |
| 9 | `ScenarioDirector` | `Core/ScenarioDirector.cs` | Evaluate triggers last (full world state) |

## Core Simulation Infrastructure (`src/Core/`)

| Class | Responsibility |
|---|---|
| `EntityWorld` | SoA entity storage, free list, per-entity enums |
| `SimulationLoop` | Fixed 30 Hz loop, `ISimSystem` orchestration, checksums, interpolation |
| `Fixed` / `FixedVec3` (`FixedPoint.cs`) | 16.16 fixed-point math — determinism backbone |
| `BuildingStore` | SoA for buildings (don't move/attack → not in EntityWorld) |
| `ResourceStore` | Per-faction resource balances (ore, crystal, supply) |
| `ResourceNodeStore` | World resource nodes (ore deposits) |
| `SimChecksum` | Deterministic world-state hashing for desync detection |
| `MatchStats` | Aggregate match statistics |
| `TechTreeChecker` | `AreMet()` prerequisite checks for units/buildings |
| `StressTest` | Performance harness (paired with `scenes/stress_test.tscn`) |
| `MainScene` | Composition root — builds and wires everything |

## Combat (`src/Combat/`)

| Class | Responsibility |
|---|---|
| `CombatSystem` | Per-tick targeting and attack application |
| `DamageMatrix` | `damageType × armorType` multiplier table (currently hardcoded) |
| `ProjectileStore` | SoA for in-flight projectiles |
| `ProjectileSystem` | Projectile simulation + splash |
| `CombatEventQueue` | Sim→presentation combat event channel (hits, deaths) |

## Navigation (`src/Navigation/`)

| Class | Responsibility |
|---|---|
| `FlowField` | A computed flow field (direction grid) |
| `FlowFieldComputer` | Builds flow fields toward goals |
| `FlowFieldSystem` | Manages/caches flow fields for the sim |
| `MovementSystem` | Steers units along flow fields |
| `SpatialHash` | Deterministic neighbor queries (replaces Godot physics for gameplay) |

## Multiplayer (`src/Multiplayer/`)

| Class | Responsibility | Layer |
|---|---|---|
| `LockstepManager` | Deterministic lockstep, adaptive input delay (RTT-negotiated) | Pure C# |
| `NetworkCommand` | Command (intent) serialization across the wire | Pure C# |
| `ENetTransport` | P2P / LAN transport | Mixed |
| `ServerTransport` | Client-side transport to a dedicated server | Mixed |
| `DedicatedServer` | Headless authoritative/relay server entry | Service |
| `NakamaService` | Matchmaking, auth, lobby (NakamaClient SDK) | Service |
| `LobbyUi` | Lobby UI | Presentation |
| `ReplayRecorder` | Records orders to `.chmr` binary | Mixed |
| `ReplayPlayer` | Re-applies stored orders by re-simulating | Mixed |

## AI & LLM (`src/AI/`)

| Class | Responsibility |
|---|---|
| `AiOpponentSystem` | Utility-scoring AI opponent (sim system) |
| `LLMService` | Claude API + Ollama fallback; trigger generation (5-pass validation) and scenario generation (7-pass validation) |

## Creation Suite / Editor (`src/CreationSuite/`)

| Class | Responsibility |
|---|---|
| `TerrainBrush` | Terrain3D paint/sculpt brush tooling |
| `TriggerEditorPanel` | Edit-mode trigger authoring UI (L key) |
| `MapGeneratorPanel` | Edit-mode AI map generation UI (M key) |
| `EditorHistory` | Undo/redo history for editor actions |

## UGC (`src/UGC/`)

| Class | Responsibility |
|---|---|
| `ModIoService` | mod.io browse/upload/subscribe integration |

## Presentation — Bridges (sim → render, `src/UI/`)

> Bridges are the **only** sanctioned readers of sim arrays for rendering. Each reads sim/store state per frame and updates Godot nodes.

| Bridge | Renders |
|---|---|
| `MultiMeshBridge` | Units via MultiMeshInstance3D (2 nodes/faction), interpolated |
| `MinimapBridge` | Minimap blips |
| `FogOfWarBridge` | 128×128 R8 fog texture uploaded each frame |
| `ProjectileBridge` | Projectile visuals |
| `BuildingBridge` | Building meshes / construction state |
| `ResourceNodeBridge` | Resource node visuals |
| `CombatFeedbackBridge` | Hit flashes / damage floaters (drains `CombatEventQueue`) |
| `StartPositionBridge` | Player start markers |
| `FlowFieldBridge` | Live pathfinding bridge (path requests into the sim) |

## Presentation — HUD / Input / Misc (`src/UI/`)

| Class | Responsibility |
|---|---|
| `SelectionSystem` | Unit selection (box/click) |
| `CommandCardSystem` | Command card (build/train/order buttons) |
| `RtsCameraController` | RTS camera (pan/zoom/edge-scroll) |
| `EntityPlacer` | Placement mode for buildings/units |
| `NavObstacleManager` | Nav obstacle registration |
| `MeshLoader` | Loads unit GLBs (box fallback if missing) |
| `AssetPreviewScene` | Asset preview rendering |
| `AudioManager` | SFX/music buses |
| `SettingsManager` / `SettingsPanel` | Persisted settings + UI |
| `GameState` | High-level game/mode state |
| `MainMenuOverlay` | Main menu (incl. "Generate Map (AI)") |
| `MatchChatOverlay` | In-match chat |
| `ContentBrowserPanel` | Map/content browser |
| `PathRequestSystem` | Legacy fallback path system (owns Move→Stop) |

## Data Definition Classes (`src/Core/Definitions/`)

See [data-models.md](./data-models.md) for full schemas: `UnitDefinition`, `FactionDefinition`, `ScenarioData`, `TriggerDefinition`, `SettingsData`, `ContentPackageManifest`, `ContentPackager`, `ScenarioSerializer`.
