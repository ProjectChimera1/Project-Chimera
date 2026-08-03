# `mesh_path` — the content-author convention

Every unit and building definition carries a `mesh_path`. There are exactly **two** valid forms, and anything else
renders the grey box placeholder.

| Form | Looks like | Resolved by | Existence checked by |
| --- | --- | --- | --- |
| **Project resource** | `res://assets/models/factions/alpha/crucible_hall.glb` | Godot's resource loader | `MeshAssetLint` at the authoring Save edge (DW-104) |
| **Package asset id** | `assets/heavy_tank.glb` | the ingested `AssetRegistry` | the package's own manifest integrity hash |

Both forms live in `MeshPathId` (`godot/src/Core/Definitions/MeshPathId.cs`) — that class is the single source of
truth, shared by the renderer, the lint, and the tests.

## Form 1 — a `res://` project resource

Use this for art that ships inside the project (or that you imported into the editor). The path is exactly what the
Godot editor shows in the FileSystem dock.

- Blank / omitted `mesh_path` is legal **while editing**: it means "render the box placeholder". `ValidateComplete`
  rejects it only when a faction claims to be complete/playable.
- A **dangling** path (non-blank, no such file) is rejected by the Save-edge lint with a located error naming the
  unit/building. The wizard jumps to the Roster step for a unit and Buildings & Tech for a building.
- The lint needs a project tree on disk to check against. In an exported build there is none, so it is skipped
  (a caller there should inject Godot's `ResourceLoader.Exists` instead — `TryFinish(..., meshExists:)`).

## Form 2 — a package asset id

Use this for a GLB bundled inside a `.chimera.zip` content package. The id is the **zip-relative path exactly as it
appears in the manifest's `asset_files` list**, e.g.:

```json
"asset_files": ["assets/heavy_tank.glb"]
```

```json
"mesh_path": "assets/heavy_tank.glb"
```

- Keep the `assets/` prefix. A bare `heavy_tank.glb` is *not* the id.
- Do **not** write the `res://` form for a packaged asset — a downloaded package's GLB never lives under `res://`.
- Case, slash direction and stray whitespace are normalized on both sides of the registry
  (`MeshPathId.NormalizeKey`), so `Assets\Heavy_Tank.GLB` resolves. Everything else must match.

If an id does not resolve, the render path now logs (DW-427) the authored value, the normalized key it looked up,
every registered id, and a `Did you mean 'assets/heavy_tank.glb'?` hint when only the folder part was wrong —
instead of silently drawing a grey box.

## Known placeholder: the two `aviary` buildings

`alpha_faction.json` / `beta_faction.json` each have an `aviary` (Air producer) whose real art
(`bonded_aerie.glb` / `wraithwing_brood.glb`) was never generated. Per the recorded decision (DW-102) their
`mesh_path` points at an **existing on-disk placeholder** — the same faction's Ranged-producer building mesh
(`sigil_foundry.glb` / `bolt_sanctum.glb`) — so the cost/prereq-gated build-menu button keeps working and the
content passes the lint.

**When the real art lands**, drop the GLBs into `godot/assets/models/factions/{alpha,beta}/` and repoint those two
`mesh_path` values back to them. `MeshPathResolutionTests.ShippedFaction_EveryAuthoredResMeshPath_ExistsOnDisk`
guards the repoint either way.
