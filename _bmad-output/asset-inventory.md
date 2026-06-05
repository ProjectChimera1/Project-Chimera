# Asset Inventory — Project Chimera

> Scanned 2026-06-05. **Headline finding:** the asset directories exist but are essentially **empty of committed binary art/audio**. The game currently runs on **procedural/box placeholders**, by design for this phase. This matches `Snapshot.md` (Iron Pact art and audio drop-in are open Phase 5 items).

## Asset Directory Structure

```
godot/assets/
├── audio/                      # (empty — no committed .ogg/.wav)
├── models/
│   └── factions/
│       └── alpha/              # (empty — GLBs referenced by data but not present)
├── textures/                   # (empty)
└── ui/                         # (empty)
godot/resources/
└── data/                       # JSON definitions (NOT binary assets — see data-models.md)
    ├── factions/
    └── scenarios/              # includes 123.chimera.zip content package
```

## Current State of Each Asset Category

| Category | Expected location | Committed now | Notes |
|---|---|---|---|
| 3D models (units/buildings) | `assets/models/factions/<faction>/*.glb` | **0 GLBs** | `UnitDefinition.mesh_path` points at GLBs (e.g. `res://assets/models/factions/alpha/worker.glb`); `MeshLoader` falls back to a **box placeholder** when the file is missing — which is the current default |
| Audio (SFX/music) | `assets/audio/` and/or `resources/audio/sfx/` | **0 files** | `AudioManager` is wired; Phase 5 task is to drop in `.ogg` files. Snapshot references `res://resources/audio/sfx/` |
| Textures | `assets/textures/` | **0 files** | Terrain textures to be set via Terrain3D Inspector (procedural-via-ClassDB doesn't persist) |
| UI sprites | `assets/ui/` | **0 files** | |
| Icon | `godot/icon.svg` | 1 | Project icon |
| Content packages | `resources/data/scenarios/*.chimera.zip` | `123.chimera.zip` | Packaged scenario bundle (`ContentPackager`) |

## How Assets Are Referenced (data-driven)

- **Models:** `UnitDefinition.mesh_path` (a `res://...glb` string in faction JSON) + `mesh_scale`. Loaded by `src/UI/MeshLoader.cs`. **If null or missing → box placeholder.** This is why the game is fully playable today with zero GLBs.
- **Audio:** managed by `src/UI/AudioManager.cs` (buses + volumes via `SettingsManager`); files dropped into the audio folder.
- **Terrain:** `terrain_3d` addon; textures assigned through the Godot Inspector, not via code/data (a documented gotcha — procedural assignment doesn't persist).

## Rendering Note (not an asset, but asset-adjacent)

Units are drawn with **`MultiMeshInstance3D`** (two MultiMesh nodes per faction for team colors), not per-unit `MeshInstance3D`. A single shared mesh (placeholder box or loaded GLB) is instanced across all units of a faction. See [architecture.md](./architecture.md) §10.

## Open Asset Work (from `Snapshot.md` / `STATUS.md`)

- **P0.3 Iron Pact art** — 8 GLBs to replace box placeholders (external work; AI art tool TBD: Hunyuan3D vs Tripo).
- **Audio drop-in** — `.ogg` SFX into the audio folder (AudioManager already wired).
- **Terrain texture painting** — assign Terrain3D textures via Inspector.

## Addons (editor tooling, not game assets)

| Addon | Purpose |
|---|---|
| `addons/godot_mcp` | MCP bridge — scene inspection, run, screenshots (autoloaded as `MCPGameBridge`) |
| `addons/terrain_3d` | Terrain3D editor plugin |
