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

---
---

# VERDICT — recorded 2026-07-30 (Alec, in the Epic-11 retrospective session)

**Status:** this section **supersedes** the brief above where they disagree. The brief's constraint data was gathered 2026-07-26; four facts have changed or were missing, corrected in §V0 below. The decision itself is in §V2.

This closes the "decide" half of **A7-E9 → A5-E11**, which had gone unmade for three epics and was blocking Epic 15's story **15-2** (map-size determinism unification, DW-160/146/162).

---

## V0. Corrections to the brief above

Four things the original brief got wrong or missed. Recorded rather than silently edited, so the reasoning trail stays honest.

**(a) `MapSize.cs` already exists — the brief never mentions it.** Story 6.7 shipped `godot/src/Core/Definitions/MapSize.cs`: a Godot-free enum + helper defining the supported authored ladder as **Small = 80 · Medium = 120 · Large = 128**, with `MapSizes.MaxHalfExtent = 128f` and a `GridDimensionConsistencyTests` build guard that fails if any supported size exceeds the fixed grids. Its own doc comment states the design intent precisely: *"A 'map size' here is the authored PLAYABLE HALF-EXTENT, NOT a variable grid dimension."* **The ladder this brief asks the brainstorm to invent was already built.** The open question was never "what sizes" — it was only "can the world get bigger."

**(b) Shipped `map_bounds` now spans 80 → 160, not 90 → 120.** Live values as of 2026-07-30:

| Map | bounds | |
|---|---|---|
| Blitz | 80 | = `MapSize.Small` |
| The Narrows | 90 | legacy, unmapped to the ladder |
| Alpha Skirmish · Iron Crossing · Scorched Plains · Crossroads · Contested Peaks · Quad Standoff · My New Map · 123 | 120 | = `MapSize.Medium` |
| **Mirror Lake** | **130** | **exceeds `MaxHalfExtent`** |
| **The Frontier** | **160** | **exceeds `MaxHalfExtent` by 32** |

`MapSizes.FromBounds` returns `Medium` for any unrecognised value **by design** — its doc says *"sizing is authoring metadata, never a load gate"* — so 90/130/160 load fine and are simply unlabelled. The build guard only constrains the three *enum* values; a hand-authored `map_bounds` bypasses it entirely, which is exactly how 130 and 160 got in.

**(c) The "latent bug" is real but has NOT produced a defect.** Every unit, building, resource node and start position on both oversized maps sits within **±83** — nothing is placed beyond 128 on either. There is no gameplay defect today. The exposure is entirely prospective (see §V1).

**(d) The brief mispriced the grid-scaling route as if it were a per-tick cost. It is not.** `FlowFieldSystem` computes a field **on demand per unique goal cell** and **caches** it (`Dictionary<int, FlowField>`), discarding the cache only when a building is placed/destroyed or terrain passability changes. So scaling the grid multiplies **memory per cached route** and the **one-off BFS per new destination** — it does *not* multiply the 30 Hz tick cost. The known 141 ms/tick figure from Story 9.15 is entity-count-driven and is barely affected. This materially changes the affordability of Route B below.

---

## V1. What `map_bounds` actually is

Confirmed by grep over every runtime consumer: **`map_bounds` is an AUTHORING extent, not a world extent.** Nothing in movement, combat, or fog reads it.

Its real consumers: camera / placement bounds · `ProceduralMapGenerator` · trigger-region authoring (`TriggerEditorPhase`, `WinConditionPhase`) · the AI map generator (`LLMService`) · `CanonicalModelHash` (folded, `:191`).

The navigable world is pinned separately and independently by `FlowField.WORLD_HALF_INT = 128`, `PathabilityGrid` 128², `FogOfWarSystem` 128² @ 2.0, and `SpatialHash` (±160 coverage). **Zero of these derive from `map_bounds`.**

So a `map_bounds` above 128 does not break anything by itself. It opens a **32-unit authoring band on each side where content can be legally placed but cannot be navigated, seen, or blocked.** Three concrete exposures:

1. **The editor will let you build there.** A resource node at x=150 on The Frontier validates clean — `ScenarioValidator` (`:113-117`) checks `map_bounds` only against the 16.16 ceiling of 32768, never against `MaxHalfExtent`. Units sent there read the *edge* cell's flow vector (`WorldToCell` clamps), sit in permanent fog, and ignore impassable terrain painted out there.
2. **The AI map generator is actively instructed to use the bad number.** `LLMService` builds its prompt from the scenario's `MapBounds`: `"All x/z positions MUST be within ±{ctx.MapBounds} world units."` Generating against The Frontier tells the model to place content out to ±160. This is a live path to broken content requiring no human error at all.
3. **It routes around 6.7's guard**, as (b) describes.

**Not a determinism risk.** The clamp is integer-only and identical on every peer, and `map_bounds` is folded into `CanonicalModelHash`, so peers agree. This is a content-quality and authoring-safety issue, not a desync vector.

---

## V2. THE DECISION

### ✅ Adopt Route C — formalise the playable area vs. the visual border (the WC3 model)

Keep the navigable world at ±128. Add an explicit, non-playable **border** that exists for camera framing and visual scale only. This is precisely what Warcraft III does — a playable area plus a boundary region — and it is what The Frontier is *accidentally* doing right now with 32 units of undefined margin.

**Cost: zero determinism, zero goldens, zero perf.**

### ❌ Route A rejected — coarsen the cells (`CELL_SIZE_WORLD` 2 → 4)

Doubles the world to 512×512 at identical memory and tick cost, paid for in pathing resolution: 4-unit cells instead of 2-unit, so chokepoints get blockier and unit spacing sloppier on **every** map. **Rejected by Alec on feel.** An RTS whose units path clumsily is a worse game than one whose maps are smaller.

### 🟡 Route B held open, not chosen — more cells at the same size (`GRID_SIZE` 128 → 256)

Keeps `CELL_SIZE_WORLD` at 2, so movement precision and feel are **identical to today**, and doubles the world to 512×512. This is the only route that delivers genuinely larger *playable* space without the Route-A feel penalty.

Repriced per §V0(d): **~4× memory per cached route** (~200 KB → ~800 KB each) and **~4× the one-off BFS** when a player picks a new destination — *not* 4× the tick cost. Requires one golden re-baseline and a `SimChecksum.AlgoVersion` bump.

**Prerequisite if B is ever taken:** `FlowFieldSystem._cache` is a `Dictionary` with **no eviction limit**. Every distinct destination cell allocates a field that lives until the next building change. At today's size that is ~200 KB per entry; after B it is ~800 KB — so ~100 distinct move orders between building events goes from ~20 MB to ~80 MB. This is a pre-existing latent issue that B multiplies by four. **A cache cap ships with B, or not at all.**

### Why C now and B held rather than B now

C is free and is the correct framing model regardless of what happens to the navigable extent — if B is taken later, the border concept still applies (a 512 playable area with a border around *that*).

More importantly, **C buys information that cannot currently be bought.** Today it is genuinely hard to tell whether the desire for "bigger maps" is *"this world feels cramped and walled-in"* — which C fixes for free — or *"I need more ground to fight over"* — which only B fixes. Once maps have real borders and stop ending at a hard invisible wall, that distinction becomes obvious from play. Committing to B first would mean paying a determinism cost to answer a question a free change can answer.

**Revisit trigger for B:** if, after Route C ships and has been played, the constraint still reads as *not enough ground to maneuver* — specifically: expansion sites feel crowded, army engagements have no room to flank, or a 4-player map cannot be laid out without bases touching — then B is the answer and should be specced as a determinism story with the cache cap attached.

---

## V3. What Route C actually is, concretely

**Additive, not a redefinition.** `map_bounds` keeps its current meaning — the **playable** half-extent — because every existing map already means that when it says 120, and because `map_bounds` is folded into `CanonicalModelHash`, so redefining it would re-fingerprint all content for no gain.

1. **Add `border_extent`** to `ScenarioData` (float, default `0`, `[JsonPropertyName("border_extent")]`). Absent → deserialises to 0 → today's behaviour exactly. **Excluded from `CanonicalModelHash`** — it is camera/visual only and touches no sim state, the same posture as `CombatFeedbackProfile`. Zero determinism cost.
2. **Enforce `map_bounds ≤ MapSizes.MaxHalfExtent` (128)** in `ScenarioValidator`, fail-closed, with the actionable message naming `border_extent` as the way to get visual scale. This is the guard the brief already recommended "regardless of verdict."
3. **Camera / visual extent = `map_bounds + border_extent`.** Terrain, water and scenery render across the full extent; the camera pans across it.
4. **Placement, AI generation and trigger regions stay bounded by `map_bounds`.** In particular, clamp `LLMService`'s prompt bounds to `map_bounds`, closing exposure V1(2).
5. **Migrate the two outliers — they become the feature's first users, preserving their authors' visual intent exactly:**
   - **The Frontier**: `map_bounds 160` → `map_bounds 128` + `border_extent 32`. Identical on screen; all content (max ±83) untouched.
   - **Mirror Lake**: `map_bounds 130` → `map_bounds 128` + `border_extent 2`.
   - Both are content edits, so both maps' `CanonicalModelHash` changes — expected and correct for an edited map; no `AlgoVersion` moves.
6. **Optionally label The Narrows** (90) — either leave it as a legacy unlabelled value or normalise it to `Small`/`Medium`. Cosmetic; `FromBounds` already handles it.

---

## V4. Answers to the brief's four questions

1. **Bigger world, or different rules on the current world?** → Different rules, plus *visual* scale. No idea on the table currently requires a larger navigable world; the perceived need was framing, which C supplies free. Revisit per the B trigger in §V2.
2. **Scale the grid or coarsen the cells?** → **Neither, for now.** Coarsening (A) is rejected on feel permanently. Scaling (B) is held open, repriced as affordable, and gated behind the play-derived trigger.
3. **Is `ScenarioType` a map-size concept or a rules concept?** → **A rules concept.** Map size is already its own axis, owned by `MapSize`/`MapSizes` since 6.7. `ScenarioType` should stay what 8.3 parameterised — rules and limits (min player slots, max combat units per slot) — and must not absorb sizing. This unblocks the `ScenarioType` registry half of A7-E9 to proceed independently.
4. **Before 1.0 or post-1.0?** → **C before 1.0** (small, free, improves every map). **B post-1.0 unless the trigger fires**, with one caveat that raises the cost of deferring: Story 11.3 shipped saves that fail closed on `SimChecksum.AlgoVersion`, so a future B **breaks every existing save and replay**. Pre-1.0 that is free; post-1.0 it is a support burden. If B is ever likely, doing it before 1.0 is materially cheaper.

---

## V5. Follow-on work

| # | Work | Where | Notes |
|---|---|---|---|
| 1 | **`border_extent` + validator guard + LLM prompt clamp + migrate the two outliers** (§V3 items 1–5) | Epic 15, alongside **15-2** | One story. No determinism cost. Closes V1's three exposures. |
| 2 | **Flow-field cache cap** | Epic 15 sweep or Epic 10's 10-2 | Pre-existing unbounded `Dictionary`; worth fixing on its own merits and a hard prerequisite for B. |
| 3 | **`ScenarioType` registry** (A7-E9's other half) | unblocked by §V4.3 | Rules-and-limits only; 8.3's `MapGeneratorContext` clamps stop being inert. |
| 4 | **Route B, specced but not scheduled** | — | Determinism story: `GRID_SIZE` 128→256, golden re-baseline, `AlgoVersion` bump, cache cap attached. Held behind the §V2 trigger. |

**15-2 is now unblocked** — it can be specced against a decided constraint: the navigable world stays ±128, `map_bounds` is enforced to it, and visual scale is a separate additive field.
