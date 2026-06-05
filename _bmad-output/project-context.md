---
project_name: 'Project Chimera'
user_name: 'Alec'
date: '2026-06-04'
sections_completed: ['technology_stack', 'critical_rules']
existing_patterns_found: 12
---

# Project Context for AI Agents

_Critical rules and patterns AI agents MUST follow when implementing game code in Project Chimera. This focuses on unobvious, project-specific details agents otherwise miss. For full design rationale see `Project_Chimera_GDD.md`; for current state see `Snapshot.md`._

---

## Technology Stack & Versions

- **Engine:** Godot **4.6.2 stable**, .NET build. Forward+ renderer, Jolt Physics, D3D12 (Windows).
- **Language:** C# targeting **.NET 8** (`net8.0`; `net9.0` only on the android target condition — desktop is net8). GDD's ".NET 9 AOT" is a *future* target, not current.
- **Assembly / namespace:** `AssemblyName` = `ProjectChimera`, `RootNamespace` = `ProjectChimera`. Namespaces follow `ProjectChimera.<System>` (e.g. `ProjectChimera.Core`, `ProjectChimera.Combat`, `ProjectChimera.Core.Definitions`).
- **Project files:** `godot/godot.csproj` and `godot/godot.sln` (NOT `ProjectChimera.sln` — that name in `godot/CLAUDE.md` is stale; the .sln file on disk is `godot.sln`).
- **NuGet:** only `NakamaClient 3.13.0` (matchmaking/auth/lobby). Avoid adding dependencies; prefer custom in-repo implementations (FixedPoint, flow fields, etc. are all hand-rolled).
- **Addons:** `godot_mcp` (scene/script inspection, run, screenshots) and `terrain_3d` (Terrain3D editor). MCPGameBridge is an autoload.
- **Target scale / perf:** 500–2,000 units at 60 FPS render; **30 ticks/sec** fixed-timestep simulation.
- **Platform:** PC desktop (Windows primary, Linux for dedicated/headless servers). **No web export.** No mobile/console.

## The One Architectural Rule Everything Else Serves

**Simulation and Presentation are separate layers, and the boundary is sacred.**

- **Simulation layer** = `src/Core/`, `src/Combat/`, `src/Economy/`, `src/Navigation/` (and sim-side AI/Multiplayer logic). Pure C# data. **No `using Godot;`. No Godot Node types. No `Vector3`/`float` for gameplay state.**
- **Presentation layer** = `src/UI/`, scenes, MultiMesh rendering. Godot Nodes live here. It **reads** sim arrays each frame; it never owns gameplay truth.
- Data flows one way: sim → presentation. Presentation sends *commands* (intents) back into the sim, never mutates sim state directly.

If you are tempted to put a `Node`, a `signal`, `GD.Print`, or a `float` position into `src/Core`, stop — it belongs in presentation, or it must be converted to `Fixed`.

## Critical Implementation Rules

### Determinism (the rule that breaks multiplayer silently if violated)
- All simulation math uses the **`Fixed`** struct (`src/Core/FixedPoint.cs`) — a custom **16.16 fixed-point** type. The type is named `Fixed`, not `FixedPoint`. Use `Fixed.FromInt`, `Fixed.FromRaw`, the static constants (`Fixed.Zero/One/Half`), and its operators. **Never** use `float`/`double`/`Mathf`/`Math.*` for any value that affects gameplay outcome.
- `Fixed.FromFloat` exists but is for **authoring/conversion at load time only** — never call it inside the tick loop (float→fixed at runtime reintroduces nondeterminism across machines).
- **Process entities in ascending ID order.** Iteration order is part of the deterministic contract. Do not iterate a `Dictionary`/`HashSet` or anything with nondeterministic order in sim code.
- No wall-clock time, no `Random` without a seeded deterministic RNG, no Godot physics for gameplay collision — use the sim's own `SpatialHash`.
- Lockstep input delay starts at 4 ticks, adapts via RTT (Ping/Pong + DelayProposal), clamped **[2, 12]**. Replays are `.chmr` binary.

### Data layout
- Entity data uses **Struct-of-Arrays (SoA)**, not arrays-of-objects. New per-entity fields are new parallel arrays in the entity world, indexed by entity id, managed with the existing **free list** for recycling slots. Don't introduce per-entity classes.
- Reuse the existing systems rather than building parallel ones: `EntityWorld` (SoA), `BuildingStore`, `ScenarioData`, `FlowFieldBridge`, `SpatialHash`. Adding a bespoke subsystem when one of these covers it is the wrong call.

### Everything is data-driven (the platform rule)
- Project Chimera is an **RTS *creation platform***, not a fixed game. **No gameplay logic, balance number, counter, or rule may be hardcoded in a path a creator can't reach.** Units, buildings, resources, tech trees, factions, win conditions, triggers, combat matrix → all JSON in `resources/data/`, deserialized via C# data classes in `src/Core/Definitions/`.
- New mechanics must be expressible as JSON a creator edits without code. If a feature can't be authored as data, reconsider the design before coding it.
- **Composition over inheritance.** A "healer" = ranged unit + heal ability + support AI, not a subclass. Build orthogonal components; don't add unit subclasses. The 6 archetypes (Worker, Melee, Ranged, Siege, Air, Structure) are the only "types" — everything else composes.
- **Layered complexity / progressive disclosure:** every creator-facing system needs a simple mode (presets/dropdowns/wizards) AND an advanced mode (raw JSON / visual scripting). Don't ship only the expert path.
- **Three-question filter** for any new feature — it must serve Create, Share, or Discover. Serving none = cut it.

### Combat formula (don't re-derive it)
`final = base × matrix[damageType][armorType] − armorValue`. Damage types: Normal, Pierce, Siege, Magic, Hero. Armor types: Unarmored, Light, Medium, Heavy, Fortified, Hero. Soft counters (0.7–1.3) are the default; hard counters (0.25–3.0) are opt-in for competitive. These values are **data**, never literals in combat code.

### Economy defaults
Ore (abundant) + Crystal (scarce) + dynamic supply cap (`base 10 + 10 per alive CommandCenter`). Two default resources is a deliberate ceiling — don't add a third in core; creators add more via data.

### Rendering
- **`MultiMeshInstance3D` for all unit rendering** — never one `MeshInstance3D` per unit. Two MultiMesh nodes per faction (separate team colors).
- Update instance transforms every frame via `MultiMesh.SetInstanceTransform()`, reading sim positions and **interpolating between the 30Hz sim ticks** for smooth 60 FPS visuals.

### Navigation
- Use **`NavigationServer3D` direct API** — call `MapGetPath()` directly. **Do NOT use `NavigationAgent3D` nodes.**
- `FlowFieldBridge` is the live, deterministic path bridge. `PathRequestSystem` is a kept-but-unused fallback — route new work through FlowFieldBridge.

### LLM / AI features
- Default provider is the **Claude API (Anthropic)** with **Ollama** local fallback. (Used for LLM trigger scripting and AI map generation.)
- **API keys are set via the Godot Inspector on MainScene (`AnthropicApiKey` export), never hardcoded** and never committed. Read them from the exported property, not from source constants.
- When writing/modifying anything Anthropic-related (model ids, params, SDK usage, pricing), consult the `claude-api` skill rather than relying on memory.

### Godot C# gotchas (4.6-specific)
- Classes inheriting any Godot type **must be `partial`**.
- Export pattern: `[Export] public float Speed { get; set; } = 5.0f;` (presentation layer only — never `float` exports for sim state).
- Use **`GD.Print()`**, not `Console.WriteLine()`, for console output (presentation/editor side only — sim layer prints nothing).
- Dedicated/headless server is detected via `DisplayServer.GetName() == "headless"`.

## Conventions

- **Files:** `PascalCase.cs`, filename matches the class name. C# source lives in `godot/src/<System>/`.
- **C#:** PascalCase classes/methods/public members, camelCase locals/params, SCREAMING_CASE constants. `#nullable enable` per file. Comment all public methods and any non-obvious logic.
- **Scenes:** `snake_case.tscn`. **Resources:** `snake_case.tres`.
- **Folder map (`godot/src/`):** `Core` (entity system, sim loop, `Fixed` math), `Combat`, `Economy`, `Navigation`, `Multiplayer` (lockstep, command serialization), `CreationSuite` (editor tools), `UI`, `AI`, `UGC`.
- **Tests:** GdUnit4 in `godot/tests/`. All simulation logic must be unit-testable **without running Godot** (a direct consequence of the no-Godot-in-sim rule).

## Brownfield Working Style

This codebase is substantially built (Phases 0–4 code-complete, Phase 5 in progress). Before planning large changes, **investigate the existing code first** (`gds-investigate` / `gds-document-project`). Favor reuse of existing systems and small shippable slices over rewrites — this is a solo-dev project with strict scope discipline (the explicit anti-pattern is Stormgate's "ship everything at once"). When a choice is ambiguous, pick the option consistent with the design pillars (data-driven, composition, layered complexity) and the determinism constraints above.
