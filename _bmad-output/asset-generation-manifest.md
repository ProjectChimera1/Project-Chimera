# Project Chimera — Asset Generation Manifest

> **Purpose:** the exact list of art/audio files the game needs, for Alec's **local AI asset-generation
> pipeline**. Generated 2026-06-21 from the live faction definitions (`godot/resources/data/factions/`)
> and the asset inventory scan. **Current state: 0 committed GLBs, 0 audio, 0 textures** — the game runs
> entirely on procedural box/placeholder fallbacks, so this whole list is open work.
>
> **Engine-side context (already architected, see `game-architecture.md`):** assets are loaded data-driven
> via `UnitDefinition.mesh_path`; a missing file → box placeholder (never a crash). The **runtime
> binary-asset ingest path** (addendum P2 — `GLTFDocument`, not `GD.Load<PackedScene>`) is what will load
> these in a shipped/non-editor build, and the **art-style consistency layer** (addendum P1 — shared
> material library + one global cel-shading/post-process shader) is what keeps them visually coherent.
> Per-asset validation caps (file size, texture dims, vertex/submesh caps) are an M1 fork — start
> conservative.

## Format & technical targets

- **3D:** glTF binary (`.glb`), Y-up, real-world-ish scale then tuned by the per-unit `mesh_scale` below.
  RTS camera is distant/top-down → **low-poly is fine and preferred**. Suggested starting budgets
  (finalize at M1): **units ~3k–15k tris, buildings ~5k–30k tris**; single material per asset where
  possible (the shared material library overrides team color). Origin at the base/feet, facing +Z.
- **Textures (terrain):** albedo maps assigned via the **Terrain3D Inspector** (procedural-via-ClassDB
  does not persist — documented gotcha). Tileable, 1k–2k.
- **Audio:** `.ogg` (Vorbis), mono SFX.
- **Target dirs:** GLBs → `godot/assets/models/factions/<faction>/`; audio → `godot/resources/audio/sfx/`;
  terrain textures → assigned in-editor.

---

## 1. Alpha faction — units (8 GLBs)  ·  `godot/assets/models/factions/alpha/`

| File | Unit id | Archetype | mesh_scale | Style hint |
|---|---|---|---|---|
| `worker.glb` | worker | Worker | 1.0 | basic laborer; carries/gathers; non-combat silhouette |
| `scout.glb` | scout | Melee (fast/light) | 0.9 | light, fast, lightly-armed recon |
| `infantry.glb` | infantry | Melee | 1.0 | standard sword/shield line trooper |
| `heavy_infantry.glb` | heavy_infantry | Melee (heavy) | 1.2 | armored elite melee, bulkier |
| `archer.glb` | archer | Ranged | 0.95 | bow ranged unit |
| `mage.glb` | mage | Ranged (caster) | 1.0 | robed spellcaster, staff |
| `siege_engine.glb` | siege_engine | Siege | 1.8 | large wheeled siege weapon (catapult/ballista) |
| `griffin.glb` | griffin | Air | 1.4 | flying mount/beast |

## 2. Alpha faction — buildings (4 GLBs)  ·  `godot/assets/models/factions/alpha/`

| File | Building id | mesh_scale | Style hint |
|---|---|---|---|
| `command_center.glb` | command_center | 3.0 | main base/town hall; produces workers + supply |
| `barracks.glb` | barracks | 2.5 | melee production |
| `archery_range.glb` | archery_range | 2.5 | ranged production |
| `siege_workshop.glb` | siege_workshop | 2.8 | siege production |

---

## 3. Iron Pact faction (beta) — units (8 GLBs)  ·  `godot/assets/models/factions/beta/`  ·  **P0.3 priority**

> Iron Pact identity (from design): **heavier armor (+1 tier), more HP (+20–35%), slower (−15–25%)** than
> Alpha — an industrial / forged-iron aesthetic. This is the faction P0.3 flags first.

| File | Unit id | Archetype | mesh_scale | Style hint |
|---|---|---|---|---|
| `forgehand.glb` | forgehand | Worker | 1.0 | industrial laborer/smith, the Iron Pact worker |
| `footsoldier.glb` | footsoldier | Melee (fast/light) | 0.9 | light armored infantry |
| `bulwark.glb` | bulwark | Melee | 1.0 | shield-bearer line trooper |
| `ironclad.glb` | ironclad | Melee (heavy, **Fortified** — tankiest ground unit) | 1.2 | heavily plated juggernaut |
| `crossbowman.glb` | crossbowman | Ranged | 0.95 | crossbow ranged unit |
| `rune_caster.glb` | rune_caster | Ranged (caster) | 1.0 | rune/forge magic caster |
| `war_machine.glb` | war_machine | Siege (450 HP / 3.5 splash) | 1.8 | massive armored siege engine |
| `wyvern.glb` | wyvern | Air | 1.4 | armored flying beast |

## 4. Iron Pact faction (beta) — buildings (4 GLBs)  ·  `godot/assets/models/factions/beta/`

> Note: Iron Pact building **filenames are themed** (the building *id* still maps to the standard role).

| File | Building id (role) | mesh_scale | Style hint |
|---|---|---|---|
| `forge_citadel.glb` | command_center | 3.0 | fortified main base, forge-citadel |
| `iron_barracks.glb` | barracks | 2.5 | melee production, iron-clad |
| `bolt_foundry.glb` | archery_range | 2.5 | ranged (crossbow) production |
| `war_foundry.glb` | siege_workshop | 2.8 | siege production foundry |

---

## 5. Audio — SFX (7 `.ogg`)  ·  `godot/resources/audio/sfx/`

`AudioManager` auto-loads these by filename; no code change needed once dropped in.

| File | Trigger |
|---|---|
| `melee_hit.ogg` | melee attack impact |
| `ranged_hit.ogg` | projectile impact |
| `explosion.ogg` | splash/siege impact |
| `unit_killed.ogg` | unit death |
| `building_placed.ogg` | building placement/construction start |
| `training_complete.ogg` | unit production complete |
| `ui_click.ogg` | UI button click |

## 6. Terrain textures (4 biome albedo)

Assigned via the **Terrain3D Inspector** (4 layers, tileable): `Grass`, `Dirt`, `Rock`, `Snow`.

---

## 7. Optional / deferred (referenced by the UGC package schema; not required for the core showcase)

- **Unit/hero portraits** (`portraits/*.png`) — for command-card / hero UI.
- **Music tracks** — menu + in-match ambience.
- **UI sprites** — currently programmatic; theme art is a UX-polish item.

---

## Counts & priority

- **24 GLBs total** (Alpha 12 + Iron Pact 12) · **7 SFX** · **4 terrain textures**.
- **Priority order:** (1) **Iron Pact 8 units** (P0.3 headline) → (2) Iron Pact 4 buildings → (3) Alpha
  12 → (4) SFX → (5) terrain. Both factions are currently placeholders, so all 24 GLBs are genuinely open
  despite P0.3 naming only the Iron Pact set.
- **Validation gate (engine side):** every generated asset will pass the runtime ingest validation
  (extension allow-list + size/dimension/vertex caps) alongside the content hash — keep assets within the
  budgets above so they pass cleanly; a rejected asset falls back to the box placeholder.
