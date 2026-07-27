# Brainstorm Intent — Scenario Maps, Map Size & Long-Term Creator Growth

**Source:** `_bmad-output/brainstorming/brainstorm-scenario-maps-2026-07-27/.memlog.md` (65 entries, Creative Partner session, 2026-07-27)
**Supersedes the open questions in:** `_bmad-output/planning-artifacts/map-size-brainstorm-brief.md`
**Handoff target:** `/bmad-correct-course`
**Scope note:** this captures the *platform/architecture* verdict. Pure content ideas raised in session (RPG trinity, ability gating, town-as-UI, connector fiction) are creator-authored content by Alec's explicit ruling and are deliberately excluded.

---

## 1. The requirement, in Alec's words

> "I don't want the creators to feel like the map ever is too small for what they want to do. I'm thinking long-term. If a creator has a series all built under one hood where he wants players to progress and keep progressing their same character, I want them to be able to."

Map size is not the requirement — it is the **unit of content** that determines *when* a creator needs the next map. The requirement is **unbounded creator growth with continuous character progression**.

---

## 2. The verdict

**Growth comes from MORE maps, never a bigger map.** Decided, and structurally confirmed.

`MapSize.Large` = 128 half-extent = 256×256, which is **exactly** the fixed grid limit in `FlowField`, `PathabilityGrid`, and `FogOfWarSystem`. There is zero headroom above Large.

The cost fork is **not size, it is seamlessness**:

| Approach | Verdict | Determinism exposure |
|---|---|---|
| Many fixed-size maps linked by explicit transitions | **ADOPTED** | **None** — grids never re-dimension, no golden moves, no `AlgoVersion` bump |
| Seamless walk-across-the-seam adjacency | **PARKED** | Cross-boundary pathing/fog/acquisition — every golden moves |
| Z-layers inside one map | **PARKED** | Flow field is flat; adding height re-dimensions it |
| Larger single map / coarser cells (the brief's original fork) | **PARKED, possibly forever** | Routed around, not solved |

**Consequence:** nothing in the adopted plan moves a committed golden or bumps `SimChecksum.AlgoVersion`.

### Decided rules

- **Authored maps: unlimited.** They are files on disk.
- **Live maps: capped** at a small number (each needs its own flow field, pathability mask, fog grid, and tick budget).
- **No live-map "budget" system.** Standard sizes, WC3-style. The creator picks from the existing `MapSize` choices per additional map.
- Link semantics (keys, gating, costs, one-way) are **creator content**, not architecture.

---

## 3. Already built — verify before planning

Checked against live source during the session. Materially shrinks the work.

| Capability | Status | Evidence |
|---|---|---|
| Standard map sizes + editor picker | **SHIPPED** | `Core/Definitions/MapSize.cs` (Small 80 / Medium 120 / Large 128) + `MapSizes` helper; picker live in `CreationSuite/MapPropertiesPanel.cs` (Story 6.7) |
| Hero save/load across custom games | **SHIPPED** | Story 3.9 — `LocalProfileSource`, `HeroProfileLoader/Validator`, `HeroPickerOverlay`, `HeroPickerPhase`, `OnlineHeroLaunchGate` |
| Persistence manifest (what carries forward) | **SHIPPED** | FR-7a — per-scenario selection of level/XP, inventory, skill tree, currency into the *next* custom game; WC3 save-code model; applied as deterministic initial state folded into `startStateHash`; anti-tamper server rail FR-7c landed Epic 9 |
| Campaign manifest (a "series under one hood") | **PLANNED, Epic 13** | Story 13.1 — ordered mission list, unlock rules, persisted completion, explicitly data-driven so creators author campaigns from the same file |
| Staged loading screen | **PLANNED, Epic 11** | Epic 11 scope |

**Alec's "take a save from map1 into map2" is a feature he already owns.** Recommend hands-on use before any planning.

---

## 4. Genuinely unbuilt — the actual work

### 4.1 Hero-persistence chaining across a series *(verification, not build)*
Persistence manifest and campaign manifest both exist, but **nothing states that a hero carries mission-to-mission through a campaign**. May be free, may be a gap. Cheap to determine.

### 4.2 Multi-map inside ONE creation *(the real feature — epic-sized)*

**Runtime (build first):**
- Many authored maps in one creation; **one (or few) live at a time**
- Hero/unit transfer between maps
- Load/unload of a map's grids on transition
- Loading screen as the transition surface — creator-authored single image or click-through slideshow with progress bar
- Loading screen doubles as the **lockstep sync barrier** (all peers must arrive on the same tick regardless)
- Player may browse inventory / equipment / skill tree during the load; **gear changes are QUEUED and replayed in order on the first tick of the new map** (never applied live in MP)

**Editor (build second — Alec's spec, verbatim intent):**
1. "Add additional map" → **placement grid**, pick the slot relative to the current map (`+` on a grid)
2. Per-map **`MapSize` picker** (reuse the existing control)
3. **Map switcher** to choose which map you are currently editing
4. Right-click a location → dropdown → "add link to another map"
5. Place one end → **editor zooms to the far map and asks for the other end** (makes a half-wired link structurally impossible)
6. Auto-generates the transfer triggers, including the return trip if wanted
7. **Guided first-time walkthrough** of the above
8. **Referential integrity on delete** — "N links point here"; re-route or offer to delete the linked triggers

**Cheap polish riding along:** "also create the return trip" checkbox (default on) · named links (`Cave Mouth → Deep Cavern`) · link lines drawn on the placement grid (it doubles as a **world atlas**, and gives relative map coordinates for free — forward-compatible if seamless adjacency is ever wanted)

### 4.3 Party continuity when a player leaves
- **Adopt-the-hero** — remaining players take the absent hero, by agreement. Cheap (an RTS engine controls multiple units natively) and preferred by Alec over bots. **Ship this first.**
- **Vote** on whether the hero stays or is removed.
- **Creator-authored AI role specs** — a *priority list* (condition → action, ordered), not human-like AI; 4–5 rules per role. Same shape as the trigger system, so it is a preset library on planned work.
  - **Hard prerequisite:** AI must be deterministic (float → `Fixed`) before any AI runs in lockstep MP.
- Creator toggle: vote / auto-adopt / auto-bot / hero vanishes.

### 4.4 Validator fix *(do now, ~20 minutes)*
`ScenarioValidator` accepts any `map_bounds` below the fixed-point ceiling (32768). The editor normalizes unsupported bounds to Medium on bind, but a **hand-authored or AI-generated** scenario with `map_bounds: 500` still passes validation — units then walk off the flow field and out of the spatial hash near the edges, presenting as an intermittent AI bug. **Fail closed when `map_bounds > WORLD_HALF_EXTENT`.**

---

## 5. Recommended placement

Alec's direction: *"doesn't have to be Epic 11. Wherever it fits in best."*

| Work | Home | Rationale |
|---|---|---|
| §4.4 validator fail-closed | **Epic 14** | Exactly what retro remediation is for; small, self-contained |
| §4.1 verify persistence chaining | **Epic 13** | Already the progression proving ground ("if the editor can't build the campaign, the editor isn't done") |
| §4.2 multi-map in one creation | **Its own feature epic, after Epic 13** | Epic-sized, not story-sized. Epic 10 is release-readiness, Epic 11 is the session shell — either would blow |
| §4.3 adopt-the-hero + vote | Standalone, any time | Cheap and independent |
| §4.3 AI role specs | Blocked on AI determinism | Sequence behind the float→`Fixed` conversion |

**Nothing needs to enter Epic 11.** The two smallest items (§4.4, §4.1) can move immediately.

---

## 6. Walking skeleton (first story of the multi-map epic)

> **Two maps. One link. One loading screen.**
> No editor UI, no placement grid, no guided setup — hand-write the second map's JSON, hard-wire one link, walk a hero through it.

Proves the entire architecture cheaply. Everything after it is UI.

---

## 7. Open questions the spec must answer

1. **Live-map cap — what is the number?** One, or a small few? Decides whether a party can ever split across maps (e.g. one player shopping while four fight).
2. **Instance identity** — if two parties enter the same dungeon, are they in the same instance or separate copies? Not resolved in session.
3. **Entity budget across maps** — `MAX_ENTITIES = 4096` is per-match. Shared pool across live maps, or per-map allocation?
4. **Save/content drift** — an old hero save referencing an ability a newer map no longer defines must fail gracefully through the existing content-validation gate. Confirm current behavior.
5. **Perf reality** — Story 9.15 measured 141 ms/tick at ~4096 entities against a 33 ms budget for 30 Hz. Any multi-live-map design must respect Epic 10's 10-2 perf work.

---

## 8. Explicitly parked

Seamless adjacent maps · Z-layers inside one map · a bigger single map · a live-map budget/allocation system · in-editor AI knowledge-base assistant (good idea, wrong epic) · all link gating/content semantics (creator-authored)
