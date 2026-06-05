# Project Overview — Project Chimera

## What It Is

Project Chimera is a **real-time strategy (RTS) creation platform** built in Godot 4.6.2 with C#. It is not a single fixed game — it is a platform on which creators author RTS content (units, buildings, factions, tech trees, maps, win conditions, scripted triggers) entirely through data, with AI assistance at the authoring layer (LLM-driven trigger scripting and map generation).

It is a **solo-developer** project, AI-assisted at every layer.

## Design Pillars (the "why" behind the code)

1. **Everything is data-driven.** No gameplay logic, balance number, counter, or rule may be hardcoded where a creator can't reach it. Units/buildings/resources/tech-trees/factions/win-conditions/triggers all live as JSON in `resources/data/`.
2. **Composition over inheritance.** A "healer" = ranged unit + heal ability + support AI, not a subclass. The 6 archetypes (Worker, Melee, Ranged, Siege, Air, Structure) are the only types; everything else composes.
3. **Layered complexity / progressive disclosure.** Every creator-facing system should have a simple mode (presets/wizards) and an advanced mode (raw JSON / scripting).
4. **Deterministic simulation.** The sim is fixed-point and lockstep-deterministic so multiplayer and replays work by re-simulating from inputs.
5. **Three-question filter.** Every feature must serve **Create**, **Share**, or **Discover** — serving none means cut it.

## Technology Summary

| Category | Choice |
|---|---|
| Engine | Godot **4.6.2 stable**, .NET build, Forward+ renderer |
| Physics | Jolt Physics (presentation only — sim uses its own `SpatialHash`) |
| Renderer backend | D3D12 (Windows) |
| Language | C# targeting **.NET 8** (`net9.0` only on android target condition) |
| Assembly | `ProjectChimera` |
| Networking SDK | NakamaClient 3.13.0 (matchmaking / auth / lobby) |
| LLM | Claude API (Anthropic) primary, Ollama local fallback |
| Editor addons | `godot_mcp` (inspect/run/screenshot), `terrain_3d` |
| Platform | PC desktop (Windows primary; Linux dedicated/headless server). **No web, no mobile/console.** |

See [data-models.md](./data-models.md) for the JSON schemas and [development-guide.md](./development-guide.md) for the full dependency/version notes.

## Architecture Classification

- **Repository type:** Monolith (one cohesive codebase under `godot/`).
- **Runtime architecture:** Two-layer — a pure-C# deterministic **Simulation** layer and a Godot-node **Presentation** layer, with a strict one-way data flow (sim → presentation) and command/intent flow back (presentation → sim). See [architecture.md](./architecture.md).
- **Entity model:** ECS-inspired Struct-of-Arrays (`EntityWorld`), no per-entity Godot nodes; rendering via `MultiMeshInstance3D`.

## Current Status (as of 2026-06-05)

- **Phase:** Phase 5 — Polish & 1.0 (Months 25–31 of the GDD roadmap).
- **Code-complete:** Phases 0–4 (core sim, combat, economy, navigation, multiplayer/lockstep, creation suite, UGC).
- **In progress / needs testing:** Utility AI, adaptive input delay (RTT-negotiated), LLM trigger system, AI map generator.
- **Remaining toward 1.0:** audio drop-in, mod.io inspector setup, LAN P2P checksum test, Iron Pact art (external), terrain texture painting, balance-analysis tooling, performance pass, Linux export.

For the live, detailed status and smoke-test checklists, see [`Snapshot.md`](../Snapshot.md) and [`STATUS.md`](../STATUS.md).

## Map of Systems (by folder)

| Folder | Responsibility | Layer |
|---|---|---|
| `src/Core` | Entity world (SoA), sim loop, FixedPoint math, fog of war, scenario/trigger data, MainScene wiring | Simulation + bootstrap |
| `src/Combat` | Damage resolution, damage matrix, projectiles, combat events | Simulation |
| `src/Economy` | Resource gathering, building construction, supply cap | Simulation |
| `src/Navigation` | Flow-field pathfinding, movement/steering, spatial hash | Simulation |
| `src/AI` | Utility-AI opponent, LLM service (triggers + map gen) | Simulation + service |
| `src/Multiplayer` | Lockstep, command serialization, ENet/Nakama transport, replay, dedicated server | Mixed (lockstep is pure C#) |
| `src/CreationSuite` | Editor tools: terrain brush, trigger editor, map generator panel, history | Presentation/editor |
| `src/UGC` | mod.io integration | Service |
| `src/UI` | HUD, selection, minimap, camera, and **bridges** that read sim arrays into Godot rendering | Presentation |

See [component-inventory.md](./component-inventory.md) for the per-class breakdown.
