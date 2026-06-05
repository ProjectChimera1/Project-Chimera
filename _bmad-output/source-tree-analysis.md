# Source Tree Analysis — Project Chimera

> Annotated map of the repository as scanned 2026-06-05. `L` = approximate lines of C# in that system.

```
Project_Chimera/
├── CLAUDE.md                     # L1 router — project instructions for AI
├── CONTEXT.md                    # Session briefing
├── STATUS.md                     # GDD implementation tracker
├── LEARNINGS.md                  # Accumulated Godot/C# knowledge
├── Snapshot.md                   # Current state, in-progress work, smoke tests
├── Project_Chimera_GDD.md        # Full Game Design Document (intent / source of truth)
├── Testme.md
├── docs/                         # Operational docs
│   ├── modio-setup-guide.md      #   mod.io inspector setup walkthrough
│   ├── server-deploy/            #   dedicated server: docker-compose.yml + README
│   └── archive/                  #   LEARNINGS archive
├── _bmad/                        # BMAD/GDS tooling (config, scripts, custom overrides)
├── _bmad-output/                 # ← THIS documentation set + project-context.md
└── godot/                        # ════ THE GODOT PROJECT ════
    ├── project.godot             # Engine config; main_scene = scenes/main.tscn; autoload MCPGameBridge
    ├── godot.csproj              # .NET 8; AssemblyName=ProjectChimera; NakamaClient 3.13.0
    ├── godot.sln                 # Solution (note: file is godot.sln, assembly is ProjectChimera)
    ├── CLAUDE.md                 # L2 router — Godot coding rules
    ├── Node3d.cs                 # (stray top-level script)
    ├── icon.svg
    ├── addons/
    │   ├── godot_mcp/            # MCP bridge: scene inspection, run, screenshots (autoload)
    │   └── terrain_3d/           # Terrain3D editor plugin
    ├── assets/                   # Art/model assets (GLBs, textures) — see asset-inventory.md
    ├── resources/
    │   └── data/                 # ════ GAME DATA (JSON) — creator-editable truth ════
    │       ├── factions/         #   alpha_faction.json, beta_faction.json
    │       └── scenarios/        #   map_*.json + alpha_map_01.json + .chimera.zip packages
    ├── scenes/
    │   ├── main.tscn             #   Main scene (entry point)
    │   └── stress_test.tscn      #   Perf stress harness
    ├── tests/                    #   GdUnit4 (currently EMPTY — no committed tests)
    └── src/                      # ════ C# SOURCE (~19.5k LOC, 74 files) ════
        ├── Core/        (L~5180) # ◆ SIM — entity world, sim loop, fixed math, bootstrap
        ├── Combat/      (L~596)  # ◆ SIM — damage, matrix, projectiles, events
        ├── Economy/     (L~629)  # ◆ SIM — gathering, building construction, supply
        ├── Navigation/  (L~693)  # ◆ SIM — flow fields, movement, spatial hash
        ├── Multiplayer/ (L~2718) # ◑ MIXED — lockstep (pure C#) + transport/replay/server
        ├── AI/          (L~1038) # ◑ utility AI (sim) + LLM service (Claude/Ollama)
        ├── CreationSuite/(L~1284)# ○ PRES/editor — terrain brush, trigger editor, map gen panel
        ├── UGC/         (L~540)  # ○ service — mod.io integration
        └── UI/          (L~6801) # ○ PRES — HUD, bridges, selection, camera, minimap
```

Legend: ◆ pure simulation · ◑ mixed/service · ○ presentation/editor

## Critical Directories Explained

### `godot/src/Core/` — the heart of the simulation (largest sim folder)
The entity system and bootstrap. Key files:
- `EntityWorld.cs` — Struct-of-Arrays entity storage (`MAX_ENTITIES=4096`), free list, all per-entity enums (`UnitCommand`, `GatherState`, `EntityFlags`, `Faction`).
- `SimulationLoop.cs` — fixed 30 Hz loop, `ISimSystem` interface, checksum hooks, interpolation alpha.
- `FixedPoint.cs` — the `Fixed` 16.16 type (+ `FixedVec3`). **Determinism backbone.**
- `MainScene.cs` — **composition root** (~2,200 LOC). Constructs everything, declares sim-system order, ~25 `Setup*()` methods. Start here to understand wiring.
- `BuildingStore.cs`, `ResourceStore.cs`, `ResourceNodeStore.cs` — SoA stores for non-entity game state.
- `FogOfWarSystem.cs`, `SimChecksum.cs`, `MatchStats.cs`, `TechTreeChecker.cs`, `ScenarioDirector.cs`, `StressTest.cs`.
- `Definitions/` — JSON-deserialization data classes (`UnitDefinition`, `FactionDefinition`, `ScenarioData`, `TriggerDefinition`, `SettingsData`, `ContentPackageManifest`/`ContentPackager`, serializers). See [data-models.md](./data-models.md).

### `godot/src/UI/` — presentation (largest folder overall, 24 files)
HUD and the **bridge** classes that read sim arrays into Godot rendering each frame. Includes `MainScene`'s visual collaborators: `MultiMeshBridge`, `MinimapBridge`, `FogOfWarBridge`, `ProjectileBridge`, `BuildingBridge`, `ResourceNodeBridge`, `CombatFeedbackBridge`, `StartPositionBridge`, `FlowFieldBridge`, plus `SelectionSystem`, `CommandCardSystem`, `RtsCameraController`, `EntityPlacer`, `AudioManager`, `SettingsManager`/`SettingsPanel`, `GameState`, menus and overlays. `PathRequestSystem` (legacy fallback path system) also lives here.

### `godot/src/Multiplayer/` — networking
`LockstepManager` (pure C#), `NetworkCommand` (command serialization), `ENetTransport`/`ServerTransport`/`DedicatedServer`, `NakamaService`, `LobbyUi`, `ReplayRecorder`/`ReplayPlayer` (`.chmr` format).

### `godot/resources/data/` — the data-driven truth
- `factions/*.json` — a faction = id + display name + color + `units[]` + `buildings[]`.
- `scenarios/*.json` — maps: bounds, win condition, player slots, resource nodes, pre-placed buildings, (optional) triggers. `.chimera.zip` are packaged content bundles.

## Entry Points (where execution begins)

| Path | Role |
|---|---|
| `godot/scenes/main.tscn` | Main scene loaded by the engine |
| `godot/src/Core/MainScene.cs` → `_Ready()` | Builds stores, sim loop, all Setup* wiring |
| `godot/src/Core/MainScene.cs` → `_Process()` | Drives sim each frame (replay / lockstep / live) |
| `godot/src/Multiplayer/DedicatedServer.cs` | Headless server entry (no display) |

## Notable Absences / Cleanup Targets
- `godot/tests/` is empty (no committed GdUnit4 tests despite the stated convention).
- `godot/Node3d.cs` is a stray top-level script outside `src/`.
- `DamageMatrix` constants are hardcoded rather than data-driven (see [architecture.md](./architecture.md) §5).
