# Project Chimera — CLAUDE.md (L1 Router)

## Identity
Project Chimera is an RTS creation platform built in Godot 4.6.2 with C#.
Solo developer. AI-assisted at every layer.

## Key Files — Read These First
- `CONTEXT.md` — Current session briefing. Read this EVERY session start.
- `STATUS.md` — GDD implementation tracker. Check what's done, in-progress, and next.
- `LEARNINGS.md` — Accumulated Godot/C# knowledge from past sessions.
- `Project Chimera GDD.md` — Full Game Design Document. The source of truth.

## Sub-Routers
- `godot/CLAUDE.md` — Godot-specific coding rules, architecture patterns, naming conventions.

## Architecture Summary
- **Engine:** Godot 4.6.2 stable (.NET build)
- **Language:** C# targeting .NET 8+
- **Pattern:** ECS-inspired simulation (pure C# structs/arrays) + Godot scene presentation (MultiMesh)
- **Simulation** is separated from **Presentation** — no Godot Nodes per entity in the sim layer.
- All game data is **data-driven** via JSON definitions (units, buildings, factions, triggers).

## Session Protocol
- **Starting a session:** User runs `/start` — Claude reads all context files and begins working.
- **Ending a session:** User runs `/save` — Claude auto-derives all progress and updates STATUS.md and CONTEXT.md.
- No manual context-setting required. STATUS.md determines what's next automatically.

## Rules
- All C# source files go in `godot/src/` organized by system
- Use PascalCase for classes, camelCase for locals, SCREAMING_CASE for constants
- Prefer composition over inheritance
- No Godot Nodes in the simulation layer — simulation is pure C# data
- Every system must be data-driven and creator-extensible
- Use FixedPoint math in any simulation code that will be multiplayer-deterministic
- Use MultiMeshInstance3D for unit rendering, never individual MeshInstance3D per unit
- Comment all public methods and complex logic