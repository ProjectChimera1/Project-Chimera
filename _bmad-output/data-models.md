# Data Models — Project Chimera

> Project Chimera is an **RTS creation platform**: its "data models" are the **JSON definition schemas** that creators author, deserialized by C# classes in `godot/src/Core/Definitions/`. There is no SQL database — game data is files on disk under `godot/resources/data/`. Floats in these schemas are authoring values converted to `Fixed` at load time (never inside the tick loop).

## Definition Classes ↔ JSON Files

| C# class (`src/Core/Definitions/`) | JSON location | Purpose |
|---|---|---|
| `FactionDefinition` | `resources/data/factions/*.json` | A faction: units + buildings + color |
| `UnitDefinition` | embedded in faction `units[]` | One unit type |
| (building def, embedded) | embedded in faction `buildings[]` | One building type |
| `ScenarioData` | `resources/data/scenarios/*.json` | A map/scenario |
| `TriggerDefinition` | embedded in scenario `triggers[]` | Event→condition→action scripting |
| `SettingsData` | local user profile | Persisted user settings |
| `ContentPackageManifest` | inside `.chimera.zip` | Packaged content metadata |

Serialization uses `System.Text.Json` with explicit `[JsonPropertyName(...)]` snake_case mapping.

## `FactionDefinition`

```jsonc
{
  "id": "alpha",
  "display_name": "Alpha Faction",
  "color": [0.2, 0.5, 1.0, 1.0],   // RGBA team color
  "units":     [ /* UnitDefinition[] */ ],
  "buildings": [ /* BuildingDefinition[] */ ]
}
```

Fields: `id`, `display_name`, `color` (float[4] RGBA), `units[]`, `buildings[]`.

## `UnitDefinition` (full schema — `UnitDefinition.cs`)

| JSON key | Type | Default | Notes |
|---|---|---|---|
| `id` | string | `""` | Unique unit id within faction |
| `display_name` | string | `""` | |
| `category` | string | `"Melee"` | One of: **Worker, Melee, Ranged, Siege, Air, Structure** (the 6 archetypes) |
| `mesh_path` | string? | null | `res://` path to GLB; box placeholder if missing |
| `hp` | float | 100 | |
| `speed` | float | 4 | |
| `attack_damage` | float | 10 | |
| `attack_range` | float | 5 | |
| `attack_speed` | float | 1 | Seconds between attacks |
| `damage_type` | string | `"Normal"` | Normal / Pierce / Siege / Magic → `ParsedDamageType` |
| `armor_type` | string | `"Unarmored"` | Unarmored / Light / Medium / Heavy / Fortified → `ParsedArmorType` |
| `cost_ore` | int | 50 | |
| `cost_crystal` | int | 0 | Advanced units only |
| `supply` | int | 1 | Supply consumed |
| `mesh_scale` | float | 1 | Import-time visual scale |
| `train_time` | float | 8 | Seconds to train |
| `vision_range` | float | 8 | World units; stamped each tick by FogOfWar |
| `splash_radius` | float | 0 | AoE on projectile hit (Siege); 0 = none |
| `prerequisites` | string[] | `[]` | Building-type ids that must be alive + fully built before training |

> **Brownfield note:** `damage_type`/`armor_type` strings resolve via `ParsedDamageType`/`ParsedArmorType`. The GDD mentions a `Hero` damage/armor class; the current enums do **not** include it (Normal/Pierce/Siege/Magic × Unarmored/Light/Medium/Heavy/Fortified).

## `ScenarioData` (map schema — sample: `alpha_map_01.json`)

```jsonc
{
  "id": "alpha_map_01",
  "display_name": "Alpha Skirmish",
  "terrain_ref": "",
  "map_bounds": 120,                       // playfield half-extent (±120u); generator clamps to this
  "win_condition": "DestroyAllBuildings",  // e.g. DestroyAllBuildings
  "player_slots": [
    {
      "slot": 0,
      "faction_json": "res://resources/data/factions/alpha_faction.json",
      "start_ore": 200,
      "base_x": -38.88,
      "base_z": 0.05
    }
    // ... up to 4 slots
  ],
  "resource_nodes": [
    { "x": -20, "z": -15, "supply": 600, "rate": 5, "max_gatherers": 4 }
    // ...
  ],
  "buildings": [ /* pre-placed buildings (optional) */ ],
  "triggers":  [ /* TriggerDefinition[] (optional) */ ]
}
```

Key elements: `id`, `display_name`, `terrain_ref`, `map_bounds`, `win_condition`, `player_slots[]` (slot, faction_json, start_ore, base position), `resource_nodes[]` (position, supply, gather rate, max_gatherers), optional pre-placed `buildings[]`, optional `triggers[]`.

**AI-generated scenarios** are validated by a 7-pass pipeline in `LLMService.ValidateScenario()` before use: schema → player slots (faction paths forced) → building types → unit ids → position bounds (±map_bounds) → ore node spacing ≥ 15u → ≤ 6 combat units per faction. Saved as `resources/data/scenarios/ai_generated.json`.

## `TriggerDefinition` (LLM trigger scripting)

Event → conditions → actions model (`TriggerDefinition.cs`), evaluated each tick by `ScenarioDirector` (runs last in the sim loop). Examples observed in the design:
- **Events:** `match_start`, `create_timer`, `unit_dies`, …
- **Actions:** `add_resources`, `display_message`, `spawn_unit`, …

LLM-generated triggers pass a 5-pass validation in `LLMService.Validate()` (rejects e.g. faction out of range, count too high, bad operators) before being added.

## Resource Model (gameplay constants — see also Economy)

- **Ore** (abundant) + **Crystal** (scarce) — two default resources, a deliberate ceiling. Creators add more via data.
- **Supply cap:** dynamic — `base 10 + 10 per alive CommandCenter`.

## Persistence Formats

| Format | What | Class |
|---|---|---|
| `.json` | factions, scenarios | `FactionDefinition`, `ScenarioData` / `ScenarioSerializer` |
| `.chimera.zip` | packaged content bundles | `ContentPackager` / `ContentPackageManifest` |
| `.chmr` | binary replays (re-simulated) | `ReplayRecorder` / `ReplayPlayer` |

## Authoring Guidance for AI Agents

- **Never hardcode** a balance number, unit stat, or rule in C# where a creator can't reach it — add it to the JSON schema and a `Definitions` class instead. (The current `DamageMatrix` hardcode is a known exception slated for migration — see [architecture.md](./architecture.md) §5.)
- New mechanics must be expressible as JSON a creator edits without code. If a feature can't be authored as data, reconsider the design before coding it.
- Floats in JSON are fine (authoring), but they must be converted with `Fixed.FromFloat` **at load time only**, never inside the tick loop.
