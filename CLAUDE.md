# Project Chimera — CLAUDE.md (L1 Router)

## Identity
Project Chimera is an RTS creation platform built in Godot 4.6.3 with C#.
Solo developer. AI-assisted at every layer.

## Key Files — Read These First
- `Snapshot.md` — Current session briefing AND implementation tracker. Read this EVERY session start; the newest dated "Current State" block at the top supersedes every block below it.
- `_bmad-output/implementation-artifacts/deferred-work.md` — the deferred-work ledger (`DW-<n>` entries). The live record of known defects and what has been closed.
- `_bmad-output/implementation-artifacts/sprint-status.yaml` — epic/story status. Edit as TEXT, never round-trip it through a YAML parser (it does not strictly parse).
- `Project_Chimera_GDD.md` — Full Game Design Document. Design INTENT; may describe targets not yet built.
  Where it and the code disagree, the code plus `Snapshot.md` is the as-built truth.
- Godot/C# learnings are auto-injected each session from the vault at
  `D:\Brain\20_Reference\GameDev\godot-csharp\LEARNINGS.md` — append there, not to the repo.

## Sub-Routers
- `godot/CLAUDE.md` — Godot-specific coding rules, architecture patterns, naming conventions.

## Architecture Summary
- **Engine:** Godot 4.6.3 stable (.NET/mono build) — bumped from 4.6.2 in story 1-1
- **Language:** C# targeting .NET 8+
- **Pattern:** ECS-inspired simulation (pure C# structs/arrays) + Godot scene presentation (MultiMesh)
- **Simulation** is separated from **Presentation** — no Godot Nodes per entity in the sim layer.
- All game data is **data-driven** via JSON definitions (units, buildings, factions, triggers).

## Session Protocol
- **Starting a session:** User runs `/start` — Claude reads the context files above and begins working.
- **Ending a session:** User runs `/save` — Claude auto-derives all progress and updates `Snapshot.md`
  (add a new dated "Current State" block rather than editing an older one), the deferred-work ledger,
  and the vault LEARNINGS file.
- No manual context-setting required. `Snapshot.md`'s newest "Current State" block plus
  `sprint-status.yaml` determine what's next.

## Rules
- All C# source files go in `godot/src/` organized by system
- Use PascalCase for classes, camelCase for locals, SCREAMING_CASE for constants
- Prefer composition over inheritance
- No Godot Nodes in the simulation layer — simulation is pure C# data
- Every system must be data-driven and creator-extensible
- Use FixedPoint math in any simulation code that will be multiplayer-deterministic
- Use MultiMeshInstance3D for unit rendering, never individual MeshInstance3D per unit
- Comment all public methods and complex logic