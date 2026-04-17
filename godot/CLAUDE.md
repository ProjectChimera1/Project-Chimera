# Project Chimera — Godot CLAUDE.md (L2 Sub-Router)

## Godot MCP
This project has the @satelliteoflove/godot-mcp addon installed.
Use MCP tools to: inspect scene tree, read/write scripts, run the project, 
capture debug output, and take editor screenshots.
Always use MCP tools to verify changes compile and run correctly.

## Build & Run
- Build C#: Use the MCP `editor` tool or run `dotnet build` in this folder
- Run project: Use the MCP `run_project` tool or press F5 in Godot
- The .sln file is `ProjectChimera.sln`

## C# Architecture Rules

### Simulation Layer (`src/Core/`, `src/Combat/`, `src/Economy/`, `src/Navigation/`)
- Pure C# only. No Godot Node types. No `using Godot;` in sim code.
- Use Struct-of-Arrays (SoA) pattern for entity data
- All math uses FixedPoint (not float) for determinism
- Process entities by ascending ID for deterministic order
- Target: 500-2000 entities at 30 ticks/sec simulation rate

### Presentation Layer (`src/UI/`, scenes/)
- Godot Nodes live here — MultiMeshInstance3D, Control, Camera3D, etc.
- Reads from simulation arrays each frame to update visuals
- Interpolates between sim ticks for smooth rendering

### Data Definitions
- JSON files in `resources/data/` define units, buildings, factions, tech trees, triggers
- C# data classes in `src/Core/Definitions/` deserialize from JSON
- Never hardcode game balance values — always load from data

### File Organization
src/
Core/           # Entity system, simulation loop, FixedPoint math
Combat/         # Damage resolution, projectiles, combat feedback
Economy/        # Resource system, gathering, building construction
Navigation/     # Pathfinding, flow fields, steering behaviors
Multiplayer/    # Networking, lockstep, command serialization
CreationSuite/  # Editor tools, terrain editing, trigger editor
UI/             # HUD, menus, selection, minimap

### Naming Conventions
- Files: `PascalCase.cs` matching class name
- Namespaces: `ProjectChimera.Core`, `ProjectChimera.Combat`, etc.
- Scene files: `snake_case.tscn`
- Resource files: `snake_case.tres`

### Testing
- Use GdUnit4 for unit tests in `tests/`
- All simulation logic must be testable without running Godot

## Common Gotchas
- Godot 4.6 C# requires `partial` keyword on all classes inheriting from Godot types
- Export properties: `[Export] public float Speed { get; set; } = 5.0f;`
- Use `GD.Print()` not `Console.WriteLine()` for Godot console output
- NavigationServer3D: call `MapGetPath()` directly, do NOT use NavigationAgent3D nodes
- MultiMesh transforms: set via `Multimesh.SetInstanceTransform()` each frame