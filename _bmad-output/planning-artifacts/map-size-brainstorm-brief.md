# Map Size & ScenarioType — Brainstorm Prep Brief

**Prepared:** 2026-07-26, from the Epic 9 retro session (closes A7-E9's "decide" half by giving the decision real constraints).
**For:** a `/bmad-brainstorming` session on map sizes / scenario types, followed by `/bmad-correct-course` carrying the brainstorm's verdict.
**Why this exists:** Alec has scenario ideas that "might need different map sizes" and wants to know *now* whether they're possible. Every number below is read from live source, not from planning docs.

---

## The headline

**Smaller maps: free today. Larger maps: not possible without changing three hardcoded grids.**

The playable world is pinned to **256 × 256 world units (−128 … +128)** by compile-time constants. `map_bounds` in a scenario is validated only against the fixed-point ceiling (32768), **not** against the actual navigable world — so the validator will happily accept a map far larger than the engine can path, fog, or spatially index.

**Grep proof of the decoupling:** the number of places where `GRID_SIZE` or `WORLD_HALF_EXTENT` is derived from `MapBounds` is **zero**. They are unrelated constants that happen to line up today because every shipped map is ≤ 120.

---

## The load-bearing constants

| Constant | Value | File | Covers | Scales with `map_bounds`? |
|---|---|---|---|---|
| `FogOfWarSystem.WORLD_HALF_EXTENT` | `128f` | `src/Core/FogOfWarSystem.cs:27` | world spans −128 … +128 | ❌ no |
| `FogOfWarSystem.GRID_SIZE` | `128` | `:26` | 128² fog cells @ 2.0 units | ❌ no |
| `FlowField.GRID_SIZE` | `128` | `src/Navigation/FlowField.cs:28` | 128² flow cells | ❌ no |
| `FlowField.CELL_SIZE_WORLD` | `2` | `:31` | ⇒ 256 × 256 world units of pathing | ❌ no |
| `PathabilityGrid` | 128 × 128 = **16 384** cells | `src/Navigation/PathabilityGrid.cs:40` | blocked mask | ❌ no |
| `SpatialHash.GRID_DIM` | `32` | `src/Navigation/SpatialHash.cs:19` | 32² buckets | ❌ no |
| `SpatialHash.CELL_SIZE_F` | `10.0f` | `:21` | ⇒ 320 × 320 units of combat acquisition | ❌ no |
| `EntityWorld.MAX_ENTITIES` | `4096` | `src/Core/EntityWorld.cs` | hard per-match unit cap | n/a |

**Shipped maps:** `map_bounds` = **90** (`map_03_the_narrows`) to **120** (everything else — `alpha_map_01`, `map_02`, `map_04`, `map_05`, `123`). All comfortably inside ±128.

**Validator reality** (`ScenarioValidator.cs:112-117`): `map_bounds` must be finite, `> 0`, and below the 16.16 range (32768). That's the *only* ceiling. `:1228` warns — non-fatally — when a start position falls outside `map_bounds`, but **nothing** checks `map_bounds` against `WORLD_HALF_EXTENT`.

---

## What this means per idea shape

| Idea shape | Possible today? | What it costs |
|---|---|---|
| **Smaller / tighter maps** (skirmish, arena, duel, puzzle box) | ✅ **Yes, free** | Author a lower `map_bounds`. `map_03` already ships at 90. Nothing to change. |
| **Same footprint, different rules** (tower defense, survival, king-of-the-hill variants, race) | ✅ **Yes** — this is the `ScenarioType` slice 8.3 built and left inert | Needs the type schema + preset table + picker. No engine change. |
| **Larger maps** (grand-strategy, long-march, 8-player sprawl) | ❌ **No, not without engine work** | Three grids must scale or re-anchor: `FlowField` (movement, determinism-critical), `PathabilityGrid` (movement, determinism-critical), `SpatialHash` (combat acquisition, determinism-critical). `FogOfWarSystem` too, but it's presentation-only and not checksummed. |
| **More units on the same map** | ⚠️ **Bounded** | `MAX_ENTITIES = 4096` is the ceiling, and the Story 9.15 perf record measured **141 ms/tick** at ~4096 entities + 64 buildings across 4 factions — well past the 33 ms budget for 30 Hz. Perf work is Epic 10's 10-2. |

---

## The cost of "larger maps", stated honestly

Growing the world is **not** a constant bump. Three of the four grids feed the simulation and therefore the checksum:

- **`FlowField` + `PathabilityGrid`** drive unit movement. Changing their dimensions changes movement outcomes ⇒ **every committed golden moves** and `SimChecksum.AlgoVersion` almost certainly bumps. That is the single most protected invariant in the project — Epic 9 spent 16 stories never moving a golden.
- **`SpatialHash`** drives combat target acquisition. Same exposure.
- **`FogOfWarSystem`** is presentation-only (explicitly excluded from `SimChecksum`), so it's the cheap one.

Two broad approaches worth putting to the brainstorm:

1. **Scale the grids with the map** — grids derived from `map_bounds` at load. Most flexible, largest determinism blast radius, and memory grows O(area) (a 4× wider map = 16× the cells: 16 384 → 262 144).
2. **Keep the grid, change the cell size** — hold 128² cells and raise `CELL_SIZE_WORLD` from 2 to 4/8. Bigger world at the same memory and the same tick cost, paid for in **coarser pathing and blockier chokepoints**. Cheaper, but it changes movement feel on *every* map unless it's per-scenario.

Neither is a small story. Both are a determinism decision, not a tuning decision.

---

## Latent bug found while gathering this

**`map_bounds` is not validated against the navigable world.** A creator can author `map_bounds: 500`, pass validation cleanly, and ship a scenario whose units walk off the flow field, out of the pathability mask, and outside the spatial hash. Symptoms would be stuck units and units that never acquire targets — near the map edges only, so it would read as an intermittent AI bug rather than a bounds bug.

Cheap fix regardless of what the brainstorm decides: fail closed in `ScenarioValidator` when `map_bounds > WORLD_HALF_EXTENT`, or warn at minimum. Worth filing as deferred work.

---

## Questions the brainstorm should answer

1. Which of Alec's ideas actually need a **bigger world**, versus a **different rule set on the current world**? (The second is far cheaper and is what 8.3 already half-built.)
2. If bigger is genuinely needed: **scale the grid** or **coarsen the cells**? Chokepoint fidelity is the thing being traded.
3. Is `ScenarioType` a **map-size** concept, a **rules** concept, or both? 8.3's parameterization treats it as rules-and-limits (min player slots, max combat units per slot) — map size was never part of it.
4. Does any of this land before 1.0, or is it a post-1.0 capability? (Epic 10 is release-readiness; Epic 11 is the session shell.)

---

## Correct-course inputs, once the brainstorm lands a verdict

- **`ScenarioType` registry** (A7-E9 / A4-E8) — schema + per-type preset table + selection UI, so 8.3's `MapGeneratorContext` clamps stop being inert.
- **Map-size support**, if the verdict needs it — a determinism story with an explicit golden/`AlgoVersion` plan, per the checksum-fold timing rule.
- **The validator fix** above, regardless of verdict.
