# Development Guide — Project Chimera

## Prerequisites

| Requirement | Version / Notes |
|---|---|
| **Godot** | **4.6.2 stable — .NET build** (the C#/Mono edition, not standard) |
| **.NET SDK** | **8.x** (project targets `net8.0`; `net9.0` only on the android target condition) |
| **OS** | Windows 11 primary (D3D12). Linux supported for dedicated/headless server |
| **GPU/API** | D3D12 on Windows (set in `project.godot`) |
| Optional: **Ollama** | local LLM fallback for trigger/map generation |
| Optional: **Anthropic API key** | for Claude-powered trigger/map generation (set in Inspector, never committed) |

No package manager bootstrap beyond NuGet restore — the only NuGet dependency is `NakamaClient 3.13.0` (restored automatically by `dotnet build`). The project deliberately avoids dependencies; FixedPoint, flow fields, spatial hash, etc. are all hand-rolled.

## Getting the Project Open

1. Clone the repo. The Godot project root is **`godot/`** (not the repo root).
2. Open `godot/project.godot` in Godot 4.6.2 .NET, **or** build from CLI.
3. First open will restore NuGet packages and build the C# assembly (`ProjectChimera`).

## Build

```powershell
# From the godot/ folder:
dotnet build
```

Or use the Godot editor's build button, or the `godot_mcp` `editor` tool. A green build = `ProjectChimera` assembly compiled; scripts won't load if the assembly name in `godot.csproj` and `project.godot` ever drift apart (both must stay `ProjectChimera`).

> Note: the **solution/project files are `godot.sln` / `godot.csproj`**, even though the assembly/namespace is `ProjectChimera`. (`godot/CLAUDE.md` historically referenced a `ProjectChimera.sln` that does not exist on disk.)

## Run

| Mode | How |
|---|---|
| Editor play | Press **F5** in Godot (runs `scenes/main.tscn`) |
| Via MCP | `godot_mcp` `run_project` tool |
| Dedicated/headless server | Run with the headless display server; detected via `DisplayServer.GetName() == "headless"` (entry: `src/Multiplayer/DedicatedServer.cs`) — see `docs/server-deploy/` |

### In-game / editor hotkeys (from current code)
- **F5** — enter Play mode
- **L** — (Edit mode) open Trigger Editor panel
- **M** — (Edit mode) open AI Map Generator panel
- Camera: RTS pan/zoom/edge-scroll via `RtsCameraController`

### Choosing a map
`MainScene` exposes `[Export] string ScenarioPath` — swap maps from the Godot Inspector. AI-generated maps land in `resources/data/scenarios/ai_generated.json`.

## Tests

- **Framework:** GdUnit4, intended location `godot/tests/`.
- **Current state:** the `tests/` directory is **empty** — no committed tests yet.
- **Design intent:** all simulation logic (`src/Core`, `Combat`, `Economy`, `Navigation`) is written to be **unit-testable without running Godot** (a direct consequence of the no-Godot-in-sim rule). New sim work is a good place to start adding GdUnit4 tests.

## Coding Conventions (enforced)

| Aspect | Rule |
|---|---|
| Files | `PascalCase.cs`, filename matches class name; C# under `godot/src/<System>/` |
| Namespaces | `ProjectChimera.<System>` (e.g. `ProjectChimera.Combat`) |
| C# style | PascalCase types/methods/public members; camelCase locals/params; SCREAMING_CASE constants; `#nullable enable` per file |
| Scenes | `snake_case.tscn` · Resources: `snake_case.tres` |
| Comments | Comment all public methods and non-obvious logic |
| Composition | Prefer composition over inheritance; no unit subclasses (compose the 6 archetypes) |

## Critical Gotchas (Godot 4.6 + this project)

1. **Sim layer is pure C#** — no `using Godot;`, no Node types, no `float`/`Vector3` for gameplay state in `src/Core/Combat/Economy/Navigation`. Use `Fixed` / `FixedVec3`.
2. **Determinism:** never call `Fixed.FromFloat` inside the tick loop; iterate entities by ascending ID; no wall-clock time, no unseeded `Random`, no Godot physics for gameplay.
3. **`partial` keyword** required on any class inheriting a Godot type.
4. **`GD.Print()`**, not `Console.WriteLine()`, for console output (presentation/editor only — the sim prints nothing).
5. **Exports:** `[Export] public float Speed { get; set; } = 5.0f;` — presentation only; never `float` exports for sim state.
6. **Navigation:** call `NavigationServer3D.MapGetPath()` directly; never use `NavigationAgent3D` nodes. Route pathing through `FlowFieldBridge`, not the legacy `PathRequestSystem`.
7. **Rendering:** `MultiMesh.SetInstanceTransform()` each frame; never one `MeshInstance3D` per unit.
8. **API keys:** set `AnthropicApiKey` via the Inspector on MainScene — never hardcode or commit. Consult the `claude-api` skill before editing Anthropic code.

## Common Development Tasks

| Task | Where to work |
|---|---|
| Add a unit stat / balance value | `resources/data/factions/*.json` + `UnitDefinition.cs` (add field) — **not** hardcoded in C# |
| Add a new sim behavior | New `ISimSystem` registered in `MainScene.cs` (mind the tick order) + new SoA array(s) in `EntityWorld` |
| Add a renderable | New `*Bridge` in `src/UI/` that reads the relevant store each frame |
| Add a map | New `resources/data/scenarios/*.json` (or generate via M-key AI panel) |
| Add a scripted event | `triggers[]` in a scenario JSON (or L-key AI trigger editor) |
| Networking / replay change | `src/Multiplayer/` — keep `LockstepManager` pure C#; preserve determinism |

## Source-of-Truth Files (read before large changes)

- `_bmad-output/project-context.md` — the critical, unobvious AI rules (determinism, layering, data-driven).
- `_bmad-output/architecture.md` — as-built architecture (this doc set).
- `CLAUDE.md` + `godot/CLAUDE.md` — project + Godot coding rules.
- `Project_Chimera_GDD.md` — design intent (note: may describe future targets not yet in code).
- `Snapshot.md` / `STATUS.md` — current state and what's in progress.
- `LEARNINGS.md` — accumulated Godot/C# pitfalls and solutions.

## Deployment (dedicated server)

See `docs/server-deploy/` (`docker-compose.yml` + `README.md`). The headless server build runs without a display; clients reach it via `ServerTransport`, and matchmaking/lobby go through Nakama (`NakamaService`, `game=chimera_1v1`). mod.io configuration: `docs/modio-setup-guide.md`.
