# Project Chimera — Documentation Index

> **Generated:** 2026-06-05 · **Mode:** Initial scan (Deep) · **Scan tool:** gds-document-project
> This is the primary entry point for AI-assisted development. Point brownfield PRD/architecture workflows here.

## Project Overview

- **Type:** Monolith — single cohesive game project (`game` project type)
- **Engine:** Godot 4.6.2 stable (.NET build), Forward+ renderer, Jolt Physics, D3D12
- **Language:** C# / .NET 8 (`net9.0` only on the android target condition)
- **Architecture:** ECS-inspired deterministic simulation (custom Struct-of-Arrays) + Godot presentation layer (MultiMesh)
- **Domain:** RTS *creation platform* — data-driven units/buildings/factions/triggers/maps
- **Status:** Phases 0–4 code-complete; **Phase 5 (Polish & 1.0)** in progress
- **Scale target:** 500–2,000 units @ 60 FPS render / 30 ticks-per-second simulation

## Quick Reference

| | |
|---|---|
| **Solution / project** | `godot/godot.sln`, `godot/godot.csproj` |
| **Assembly / namespace** | `ProjectChimera` (root namespace `ProjectChimera.<System>`) |
| **Entry point (code)** | `godot/src/Core/MainScene.cs` (wires all systems) |
| **Entry point (scene)** | `res://scenes/main.tscn` (main scene set in `project.godot`) |
| **Sim loop** | `godot/src/Core/SimulationLoop.cs` — fixed 30 Hz, `ISimSystem[]` |
| **Source code** | `godot/src/<System>/` (Core, Combat, Economy, Navigation, Multiplayer, AI, CreationSuite, UGC, UI) |
| **Game data (JSON)** | `godot/resources/data/` (factions, scenarios) |
| **Build** | `dotnet build` in `godot/`, or Godot editor / `godot_mcp` |
| **Run** | F5 in Godot, or `godot_mcp` `run_project` |
| **Tests** | GdUnit4 (configured by convention; `godot/tests/` currently empty) |

## Generated Documentation

- [Project Overview](./project-overview.md) — purpose, pillars, status, tech summary
- [Architecture](./architecture.md) — the sim/presentation split, determinism, sim-loop order, subsystems
- [Source Tree Analysis](./source-tree-analysis.md) — annotated directory map
- [Component Inventory](./component-inventory.md) — every system/bridge class, by layer
- [State Management](./state-management.md) — where authoritative game state lives and how it flows
- [Data Models](./data-models.md) — JSON definition schemas (units, buildings, factions, scenarios, triggers)
- [Asset Inventory](./asset-inventory.md) — models, audio, scenes, addons
- [Development Guide](./development-guide.md) — setup, build, run, conventions, gotchas

## Existing Project Documentation (authoritative — read these too)

- [`CLAUDE.md`](../CLAUDE.md) — L1 router / project instructions
- [`godot/CLAUDE.md`](../godot/CLAUDE.md) — L2 Godot coding rules
- [`_bmad-output/project-context.md`](./project-context.md) — **critical AI rules** (determinism, layering, data-driven)
- [`Project_Chimera_GDD.md`](../Project_Chimera_GDD.md) — full Game Design Document (source of truth)
- [`Snapshot.md`](../Snapshot.md) — current session state, what's in progress, smoke-test checklists
- [`STATUS.md`](../STATUS.md) — GDD implementation tracker
- [`LEARNINGS.md`](../LEARNINGS.md) — accumulated Godot/C# knowledge
- [`docs/server-deploy/README.md`](../docs/server-deploy/README.md) — dedicated server deploy
- [`docs/modio-setup-guide.md`](../docs/modio-setup-guide.md) — mod.io configuration

## Getting Started

1. Open `godot/` in Godot 4.6.2 (.NET build), or run `dotnet build` in `godot/`.
2. Press **F5** to run the main scene (`scenes/main.tscn`).
3. The single biggest rule before touching code: **the Simulation layer (`src/Core`, `src/Combat`, `src/Economy`, `src/Navigation`) must stay pure C# and deterministic** — no `using Godot;`, no `float` for gameplay state, use the `Fixed` type. See [Architecture](./architecture.md) and `project-context.md`.
