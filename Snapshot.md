---
project: Project Chimera
last_touched: 2026-04-16
phase: Phase 5 — Polish & 1.0
status: Active
---

# Project Chimera — Snapshot

**Last Touched:** `2026-05-01`

## Current Phase
**Phase 5 — Polish & 1.0** (Months 25-31 of GDD roadmap)

Phases 0–4 are code-complete. Phase 5 is underway. Session 20 shipped worker-placed buildings + UI bug sweep.

## Next Action
Drop in audio assets. Place `.ogg` files at `res://resources/audio/sfx/`: `melee_hit.ogg`, `ranged_hit.ogg`, `explosion.ogg`, `unit_killed.ogg`, `building_placed.ogg`, `training_complete.ogg`, `ui_click.ogg`. AudioManager loads them automatically — no code change needed.

## What's In Progress
Nothing actively in-progress — last session ended cleanly with worker construction system shipped.

## Phase 5 Remaining Items
| Item | Status | Notes |
|------|--------|-------|
| Drop in audio .ogg files | 📋 | `res://resources/audio/sfx/` — AudioManager already wired |
| mod.io Inspector setup | 📋 | Select MainScene → set `Mod Io Game Id` + `Mod Io Api Key`; walkthrough at `docs/modio-setup-guide.md` |
| P2.4 LAN test (P2P mode) | 📋 | FlowFieldBridge active, verify checksums stay in sync through 300+ ticks |
| P0.3 Iron Pact art | 📋 | Hunyuan3D or Tripo — 8 GLBs to replace box placeholders (external work) |
| Terrain texture painting | 📋 | Set Terrain3D textures via Godot Inspector (Terrain3D → Assets) — procedural via ClassDB doesn't persist |
| Utility AI decision system | 📋 | Upgrade rule-based AI skeleton to utility AI |
| AI build order + attack timing logic | 📋 | Phase 5 AI improvements |
| Adaptive input delay | 📋 | Scale INPUT_DELAY dynamically based on real RTT data |
| LLM trigger scripting | 📋 | Phase 5 GDD item — AI-powered trigger authoring |
| AI-assisted map generation | 📋 | Phase 5 GDD item |
| AI balance analysis tools | 📋 | Phase 5 GDD item |
| Performance optimization pass | 📋 | Phase 5 GDD item |
| Advanced editor features | 📋 | Particles, sound triggers |
| Linux export | 📋 | Export template only — no code changes |
| 1.0 release | 📋 | Final milestone |

## Mental RAM
- **Current stack**: Godot 4.6.2 stable, C# / .NET 8, ECS-inspired simulation (custom SoA arrays, not a framework)
- **Rendering**: MultiMeshInstance3D for all unit rendering; two MultiMesh nodes per faction (separate colors)
- **Pathfinding**: `FlowFieldBridge` is the live path bridge (replaced `PathRequestSystem`). `PathRequestSystem` stays unused as fallback. Flow fields are deterministic — required for lockstep.
- **Networking**: Deterministic lockstep complete. `INPUT_DELAY=4` ticks (133ms at 30Hz). `LockstepManager.Flush(tick)` gates sim advancement. FlowFieldBridge eliminates NavServer desync.
- **Worker construction**: workers walk to site (`UnitCommand.Build` + `BuildTarget[]` SoA), building ticks its own construction timer autonomously, worker arrival clears command + resumes gathering.
- **`CommandCardSystem` worker card** fires `OnWorkerBuildRequested` → `MainScene` owns placement mode. `_Input` (not `_UnhandledInput`) for placement intercept — beats SelectionSystem.
- **`SettingsPanel`** uses intermediate `anchorRoot` Control (MouseFilter=Stop) for full-screen input blocking; Escape in `_Input`.
- **Terrain brush**: panel at (10,155) below HUD; `IsOverPanel()` guard stops paint on slider clicks; `ApplyBrushSettings()` in `ContinuePaint()` for live slider updates.
- **Supply cap**: dynamic — base 10 + 10 per alive CommandCenter. `TrainUnit()` supply-gates before deducting ore.
- **`AiDifficulty`**: Easy(8 units/40s), Normal(5/25s), Hard(3/15s). `[Export] AiLevel` on MainScene.
- **Assembly name**: `ProjectChimera` (csproj + project.godot must match or scripts won't load)
- **`PathRequestSystem` owns Move→Stop transition** (NOT Move→Idle) — Move→Idle caused stutter bug (TickIdleCombat re-wrote MoveTarget on very next sim tick)

## Open Design Decisions
- **AI art tool**: Hunyuan3D vs Tripo vs other — P0.3 Iron Pact art still pending
- **Utility AI**: upgrade rule-based FSM to utility AI system

## Performance Baseline
| Configuration | FPS |
|---|---|
| Movement only, 500 units | ~1150 |
| Combat O(n²), 500 units | ~300 |
| Combat O(n²), 1000 units | ~50 |
| Combat + SpatialHash, 1000 units | ~350 |

## Key Architecture Decisions
- ECS-inspired simulation: SoA arrays, free list, no framework. Pure C# sim layer — no Godot types.
- NavigationServer3D direct API (no NavigationAgent3D nodes). FlowFieldBridge for deterministic multiplayer.
- Fog of war: 128×128 byte grid, R8 ImageTexture uploaded each frame by FogOfWarBridge.
- Buildings use `BuildingStore` SoA (not EntityWorld) — buildings don't move or attack.
- `PathRequestSystem` lives in presentation layer; sim layer only reads MoveTarget.
- `AiOpponentSystem` runs LAST in SimulationLoop — sees fully-updated supply caps and construction states.
- Tech tree: `prerequisites` string[] on `UnitDefinition`; checked by `TechTreeChecker.AreMet()`.
- Scenario system: `[Export] string ScenarioPath` on MainScene — map swappable from Inspector.
- Lockstep: `LockstepManager` pure C# (no Godot dep); bridges via `OnRequestPath/OnRequestAttackMove/OnCancelPath` delegates.
- Replay: `.chmr` binary format. Auto-starts on `OnMatchStart()`. `ReplayPlayer` re-applies stored orders.
- Nakama matchmaking: `NakamaService.FindMatchAsync()` — 2-player, `game=chimera_1v1`. Faction assigned by server.

## Reference
- GDD: `GDD_Project_Chimera.md`
- Implementation status (archived): `D:\Obsidian Brain\Brain\30_Archive\Chimera_STATUS_archived_2026-04-16.md`
- Godot/C# patterns (live, auto-injected each session): `D:\Obsidian Brain\Brain\20_Reference\GameDev\godot-csharp\LEARNINGS.md`
- Godot project: `D:\Obsidian Brain\Brain\10_Active_Projects\Project_Chimera\godot\`
- Server deploy: `godot/docs/server-deploy/`
- mod.io setup: `godot/docs/modio-setup-guide.md`
