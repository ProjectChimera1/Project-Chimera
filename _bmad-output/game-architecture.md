---
title: 'Game Architecture'
project: 'Project_Chimera'
date: '2026-06-20'
author: 'Alec'
version: '1.0'
stepsCompleted: [1, 2, 3]
status: 'in-progress'

# Source Documents
gdd: 'Project_Chimera_GDD.md'
prd: '_bmad-output/planning-artifacts/prds/prd-Project_Chimera-2026-06-05/prd.md'
ux: '_bmad-output/planning-artifacts/ux-designs/ux-Project_Chimera-2026-06-20/'
brownfield_architecture: '_bmad-output/architecture.md'
epics: null
brief: null
---

# Game Architecture

## Document Status

This is the **forward-looking** technical architecture for Project Chimera — the decisions
that keep AI agents implementing consistently on the path to 1.0. It is created through the
GDS Architecture Workflow and is informed by, but distinct from, the brownfield
`architecture.md` (which documents the code **as-built**, deep scan 2026-06-05).

**Steps Completed:** 3 of 9 (Engine & Framework)

**In progress:** Step 4 (Architectural Decisions). **D1 (effects-primitive vocabulary), D2
(trigger-DSL design), and D3 (data-driven definition schema & loader) are all decided (2026-06-20)** —
see *Architectural Decisions (Step 4)* below. The deep-dive trio (D1→D2→D3) is complete. Next action is
**batch D4–D6 (Hero persistence · >2-player lockstep + matchmaking · LLM provider abstraction) as
recommend-and-confirm**. Full Step 4 working state in **`game-architecture.RESUME.md`**.

---

## Project Context

### Game Overview

**Project Chimera** is an RTS *creation platform* — "the platform that ships the RTS
genre as a living, community-owned system" — built in Godot 4.6.2 stable (.NET) with C#.
It serves three archetypes (Commanders play, Architects build, Tinkerers do both) and
filters every feature through three questions: does it make an RTS easier to **Create**,
**Share**, or **Discover**?

The shipped game (a polished 2-faction single-player + multiplayer RTS) is the **showcase**;
the **Warcraft III World Editor–class, AI-assisted Creation Suite is the North Star**
(PRD Decision #5). 1.0 is scoped **maximal** — full creation suite, AI-assisted authoring,
multiplayer, and UGC ship together, gated by always-shippable internal milestones M0–M6.

### Technical Scope

- **Platform:** PC desktop — **Windows primary, Linux** (dedicated server + client). No web,
  mobile, console, VR, or gamepad.
- **Engine / Language:** Godot **4.6.2 stable** (Forward+, Jolt, D3D12) · **C# / .NET 8**
  desktop (".NET 9 AOT" is a future aspiration, not 1.0). Sole NuGet dependency: `NakamaClient`.
- **Genre:** Real-time strategy + in-game creation toolset (editor-as-product).
- **Project Level:** **4 — Maximum.** Multiplayer-deterministic simulation + full data-driven
  creation suite + LLM-assisted authoring, on a **brownfield codebase at Phase 5 (→1.0)**.
  Phases 0–4 code-complete; the creation-suite editors, trigger-DSL expansion, hero save/load,
  and >2-player multiplayer are net-new.
- **Distribution:** Premium one-time purchase (no F2P/live-service); **Steam + direct DRM-free**.

### Core Systems

Status: ✅ Built · 🟡 Partial · ⬜ To-build · ❓ Unresolved (architecture must decide)

| System | Layer | Complexity | Status | Source |
|---|---|---|---|---|
| Entity model — SoA `EntityWorld` (4096 cap), `BuildingStore`/`ResourceStore` | Sim | Med | ✅ | arch §3 |
| Determinism — `Fixed` 16.16, ascending-ID iteration, seeded RNG | Sim | High | ✅ | arch §2, GDD §6 |
| Simulation loop — 30 Hz fixed tick, ordered `ISimSystem`s | Sim | Med | ✅ | arch §4 |
| Combat — damage×armor matrix, projectiles, splash | Sim | Med→High | 🟡 hardcoded; add `Hero` type, → JSON | arch §5, GDD §3 |
| Navigation — flow-field, `SpatialHash`, NavServer3D direct API | Sim | High | ✅ | arch §6, GDD §2 |
| Economy — N resources, dynamic supply cap | Sim | Med | 🟡 2-resource ceiling → data-driven | arch §7, GDD §3 |
| Fog of war — 128² grid, server-enforced | Sim | Med | ✅ | arch §10, GDD §3 |
| Lockstep + command serialization | Sim/Net | High | ✅ built, ❗never LAN-verified | arch §8, GDD §6 |
| Matchmaking — Nakama | Net | Med | 🟡 1v1 only → up to 8 players | arch §8, GDD §6 |
| Replays (`.chmr`) + spectator | Net | Med | 🟡 | arch §8 |
| Content hash verification (MP) | Net | Med | ⬜ | GDD §6 |
| Utility-AI opponent | Sim | Med | ✅ | arch §9 |
| LLMService — OpenRouter/Claude/Ollama + validation pipelines | Authoring | High | 🟡 +balance analysis, provider→settings, relax clamps | arch §9, PRD §4.7 |
| Editor shell — toolbar/palette/dock, Simple↔Advanced, Edit↔Play (≤2s) | Pres | Med | 🟡 | UX DESIGN; PRD §4 |
| Map/terrain editor + entity/start/resource placers | Pres | Med | 🟡 terrain brush built | GDD §5 |
| Unit Card Editor (consolidated WC3 model) | Pres | Med | ⬜ | PRD §4.1 |
| Ability authoring (active/passive, effect primitives) | Pres | High | ⬜ | PRD §4.2 |
| Building + visual Tech-Tree editor (`GraphEdit`) | Pres | High | ⬜ | PRD §4.3 |
| Faction Definer wizard | Pres | Med | ⬜ | GDD §5, PRD §4.4 |
| Hero system + Save/Load picker (persistent artifacts, init-time deterministic) | Sim+Pres | High | ⬜ | PRD §4.8, Dec #19/#20 |
| **Trigger DSL expansion** — variables/loops/arrays/events/custom UI | Sim | **Very High** | ⬜ | PRD §4.6, Dec #12 |
| Custom runtime UI builder (bound to DSL vars) | Pres | High | ⬜ | PRD §4.6 |
| **Effects-primitive vocabulary** (shared abilities + triggers) | Sim | High | ❓ **architecture lever** | PRD addendum §C |
| MultiMesh rendering + `*Bridge` readers | Pres | Med | ✅ | arch §10 |
| Screen/state mgmt (Title/Mode/Lobby/HUD/Editor/Browser/Settings) | Pres | Med | 🟡 | UX EXPERIENCE |
| Claude Design System → Godot `Theme` (faceted `StyleBox`) | Pres | Med | ⬜ designed, impl pending | UX DESIGN |
| Accessibility baseline (remap, colorblind, UI scale, subtitles) | Pres | Med | ⬜ | PRD §4.11 |
| `.chimera.zip` packaging + manifest | Cross | Med | 🟡 | GDD §7 |
| mod.io integration (REST) + content browser | Pres/Net | Med | ⬜ | GDD §7, PRD §4.10 |
| Data-driven JSON definitions (`resources/data` + `Definitions`) | Cross | High | 🟡 mandate; `DamageMatrix`→JSON is #1 | GDD §1, arch §5 |
| Static validation (schema/reference/range/safety, server-side) | Cross | High | 🟡 | GDD §4, PRD NFR-6 |
| Testing — GdUnit4, sim testable headless | Cross | High | ⬜ **zero tests today** | project-context; PRD FR-44/47 |

### Technical Requirements

**Performance:** 500–2,000 units @ **60 FPS render / 30 Hz sim tick**, verified on representative
shipped + community scenarios (NFR-5, FR-46). Hard entity cap **4096** — do not raise for benchmark
reasons (explicit counter-metric).

**Creation-suite UX:** Edit↔Play round-trip **≤ 2s**, no restart/export (NFR-1). First faction
authored **≤ 12 min**, first scenario **< 15 min**, no JSON required (NFR-2, FR-4).

**Networking:** Server-authoritative **deterministic lockstep**, command-based (~20 B/command);
adaptive input delay clamped **[2, 12]** ticks; **up to 8 players**; **≥ 95% zero-desync**; LAN
**300+ ticks checksummed** in lockstep is a hard ship gate (FR-39); checksums every 60 ticks.
Transport: ENet (LAN/P2P) + dedicated Linux server; Nakama matchmaking.

**Determinism:** 16.16 fixed-point throughout sim; no float gameplay math, no wall-clock,
ascending-ID iteration, seeded RNG. (NavigationServer3D paths are non-deterministic cross-machine —
flow-field mitigation is in place but **unproven on real LAN**.)

**Validation:** every shared construct (units, abilities, triggers, DSL logic, custom UI) must be
**statically server-validatable** before multiplayer execution — this directly **bounds DSL
expressiveness** (NFR-6).

**Security:** server authority + command validation + content hashing. No client-side anti-cheat.

### Complexity Drivers

**High complexity**
- Deterministic lockstep MP that has **never been LAN-verified** (NavServer3D nondeterminism risk).
- **Trigger-DSL expansion** bounded by static validation — the expressiveness lever for "build any game."
- ~6 net-new editors threading through the **2,200-LOC `MainScene`** composition root (integration chokepoint).
- Hero persistent artifacts as **init-time deterministic** state with server validation.

**Novel concepts (no off-the-shelf pattern)**
- ❓ **Effects-primitive vocabulary** shared by abilities AND triggers — deferred to this phase; breadth determines buildable genres.
- A runtime, data-driven, **multiplayer-deterministic** creation suite where creator content is server-validated.
- LLM-assisted authoring (NL→trigger, AI map gen, AI balance) as a sandboxed authoring layer that **never touches the sim tick**.
- Creator-authored **custom runtime UI** bound to DSL variables.

### Technical Risks

- **#1 ship risk:** LAN determinism verification (FR-39) — code-complete, unproven.
- **DSL Goldilocks:** too narrow → "any game" fails; too broad → unvalidatable / breaks determinism.
- **Zero tests** vs. a determinism-regression-guard requirement — test infra is an M1 prerequisite.
- **Data-driven debt:** `DamageMatrix` hardcoded (→ JSON before creator balance authoring); resources hardcoded; tech-tree only string-arrays.
- **AI clamps:** map generator hard-clamps to RTS conventions — must parameterize for TD/RPG.
- **`GraphEdit` is "Experimental"** (tech-tree + trigger graphs) — needs an abstraction layer.
- **Terrain3D runtime modification** not stress-tested under rapid brush + collider updates.

## Engine & Framework

### Selected Engine

**Godot 4.6.x stable** (.NET build) · **C# / .NET 8** — locked (brownfield).

**Rationale (GDD §2, confirmed as-built):** C# runs the RTS sim hot path ~2–3× faster than
GDScript JIT; Godot's runtime introspection (`GetPropertyList`/`Get`/`Set`, runtime
`PackedScene.Pack()`, `GraphEdit` at runtime) is *the* reason an in-game creation suite is
viable; MIT license (no royalties/seat fees); native headless/dedicated-server export.

**Version decision (2026-06-20):** Adopt **Godot 4.6.3** for 1.0 (patch on the 4.6 line,
released 2026-05-20 — low-risk bugfixes over 4.6.2). **Defer 4.7** (released 2026-06-17) to
post-1.0: a days-old minor carries regression + addon-compat (Terrain3D, godot-mcp) risk not
worth taking mid-release. Engine stays pinned to a known-good line.

### Project Initialization

Existing project — no scaffolding. Entry: `project.godot` → `res://scenes/main.tscn` →
`MainScene.cs._Ready()`. Solution `godot/godot.sln` (.NET 8). Sole NuGet dependency: `NakamaClient`.

### Engine-Provided Architecture

| Component | Solution | Notes |
|---|---|---|
| Rendering | Forward+; `MultiMeshInstance3D` for all units | 1 draw call/unit-type/faction; sim→transform via `*Bridge` |
| Physics | Jolt (engine default) | **NOT for gameplay** — sim uses deterministic `SpatialHash`; Jolt for editor/raycast/presentation only |
| Audio | `AudioServer` / `AudioStreamPlayer` | Presentation layer only |
| Input | `Input` + `InputMap` | Presentation only; maps to sim *command intents*, never mutates sim |
| Scene mgmt | Scene tree + `PackedScene`; `MainScene` composition root; autoload `MCPGameBridge` | Runtime `PackedScene.Pack()` powers editor save/load |
| UI | `Control` nodes + `Theme` | Claude Design System → faceted `StyleBox` theme |
| Networking transport | `ENetMultiplayerPeer` (+ WebSocket fallback) | Custom deterministic lockstep layered on top |
| Build / export | .NET 8 MSBuild + Godot export templates; `--headless` / `dedicated_server` (Linux) | NativeAOT server build is an open question |
| Serialization | `System.Text.Json` (data) + custom binary (`.chmr`, terrain) | .NET, not engine-provided |

### AI Tooling (MCP Servers)

- **godot-mcp (already in use, keep)** — rich Godot MCP via the `MCPGameBridge` autoload
  (`addons/godot_mcp`): live scene/node/animation/tilemap/gridmap edits, `runtime_state` digests,
  profiler, `validate_meshes`, input injection, frozen game-time stepping, `godot_docs`. The
  AI-assisted-dev backbone. **Dev-time tooling only — not shipped in the 1.0 build.** Hygiene:
  verify the addon still connects after the 4.6.3 bump (patch bumps rarely break editor addons)
  and pull upstream fixes periodically.
- **Context7** (optional, `upstash/context7`) — current .NET/NuGet/library docs (Nakama,
  System.Text.Json): `claude mcp add context7 -- npx -y @upstash/context7-mcp`.

### Remaining Architectural Decisions (→ Step 4)

Engine settles rendering/physics/input/scene/transport. These game-specific decisions remain:

1. **Effects-primitive vocabulary** shared by abilities + triggers (the "buildable genres" lever)
2. **Trigger-DSL design** — variables/loops/arrays/events/custom UI + static-validation model
3. **Data-driven definition schema & loader** — `DamageMatrix`→JSON, N-resources, tech trees
4. **Hero persistence** — init-time deterministic state + server validation
5. **>2-player lockstep** topology + Nakama matchmaking (up to 8)
6. **Test architecture** — GdUnit4 + headless deterministic sim tests (from zero)
7. **`MainScene` decomposition** — taming the 2,200-LOC composition root
8. **LLM provider config** migration (Inspector export → settings) + multi-provider

---

## Architectural Decisions (Step 4)

> Step 4 records the game-specific decisions the engine layer does not settle. Approach (confirmed
> 2026-06-20): deep-dive **D1 → D2 → D3** one at a time (novel, coupled, highest-stakes — facilitated,
> user makes each call), then batch **D4–D6** as recommend-and-confirm. Decisions are appended here as
> they are settled. Frontmatter `stepsCompleted` advances to `[…,4]` only once D1–D6 are all recorded.
>
> **Settled so far:** D1 ✅, D2 ✅, D3 ✅ (all 2026-06-20) — the deep-dive trio is complete. **Next:**
> batch D4–D6 (recommend-and-confirm).

### D1 — Effects-Primitive Vocabulary ✅ (decided 2026-06-20)

**Decision — Bounded Effect-Graph (Option C).** Adopt a single shared, **closed, typed** effect
vocabulary: a small set of atomic **leaf effects** + exactly **three composition nodes**
(`Sequence`, `SearchArea`, `Persistent`) + a first-class **Modifier** object, composed as an
**acyclic, depth- and fan-out-bounded graph**. This one vocabulary is the *only* effect surface for
all three consumers — the (net-new) **ability system**, the **trigger DSL** (D2), and **AI balance
analysis** — and it replaces **both** the hardcoded combat-damage path **and** the
`ScenarioDirector` action string-switch. **No scripting escape hatch** (no JASS/Lua/`RunScript`/
`customParams`) — ever. This is the one deliberate divergence from every reference engine (WC3, SC2,
Dota, OpenRA, Spring, Mindustry), all of which reach breadth via a Turing-complete escape hatch or
runtime iteration caps — precisely the two things Chimera's static-validation + lockstep constraints
forbid.

**Why C, not A or B.**
- **A (flat WC3-style preset list)** is the safest and cheapest but caps generality *below* the bar:
  every non-trivial spell needs a bespoke preset, MOBA/RPG composition is unbuildable, and real
  expressiveness gets shoved back into the trigger DSL — recreating the two-vocabulary split the PRD
  addendum §C explicitly forbids.
- **B (SC2-style composable effect-tree)** reaches the breadth but its *native safety model* is wrong
  for Chimera: cycles are permitted, fan-out is guarded by runtime caps (not static rejection),
  search iteration is engine-order, periods are wall-clock seconds, RNG is free, and broken refs fail
  silently. Re-grounding all of that for lockstep + pre-run static validation **is rebuilding C**, less
  cleanly.
- **C** copies what works across all six engines (SC2's `Set`/`SearchArea`/`Persistent` composition
  trio; the Dota/OpenRA/SC2/WC3-convergent universal Modifier; OpenRA's closed-class + lint-the-graph
  discipline; Spring's synced/unsynced split) and rejects what doesn't. It is the **only** option whose
  safety model matches the two non-negotiables — *static server validation before any MP tick* and
  *Fixed-16.16 / 30 Hz lockstep determinism* — while still hitting WC3-parity-and-beyond breadth.
- **Brownfield fit:** the sim/presentation `On*`-delegate seam `ScenarioDirector` already enforces
  becomes the schema's **Domain** tag; the three duplicated damage sites collapse into one `Damage`
  leaf; the whole thing lands as a strangler behind golden-checksum tests.

**Settled sub-decisions:**
1. **Modifier (buff/aura/status/DoT) is in the 1.0 MVP critical path.** It is the keystone primitive —
   Chimera has *no* buff/status concept today, and 3 of 4 target genres (MOBA, TD slows/poisons, RPG
   ailments) are unbuildable without it. Deferring it doesn't reduce total cost, only delays the genres
   that justify the platform.
2. **1.0 vocabulary cut line = MVP + `Switch` + `NamedEffectReference`** — the latter two as the
   *first stretch increment*, added only after the core graph + validator + Modifier are proven.
   `Switch` unlocks data-side branching (executes, conditional heal/smite, bonus-vs-status);
   `NamedEffectReference` unlocks chaining / sub-munitions (chain lightning, cluster bombs, meteor→fire-
   pool) and shared-by-id balance reuse. `ApplyForce`/`Knockback`, `Morph`, `IssueOrder` deferred until
   the validator is battle-tested.
3. **C# representation = sealed `EffectNode` class hierarchy** — one sealed class per effect type with
   `Apply(in EffectContext)` + `Validate(...)`, **allocated once at scenario-load** (no GC in the tick,
   same lifetime as today's `_triggers` array), executed via an **explicit work-stack, not recursion**
   (bounds depth, avoids stack overflow). Rejected: the tagged-struct DU (resurrects the fat-nullable
   `TriggerAction` anti-pattern being retired) and a per-tick JSON interpreter (boxes/re-walks each tick
   — violates no-GC, hardest to statically bound).
4. **Structural caps = named, reviewable constants** (not hardcoded literals), to be validated against a
   concrete WC3/MOBA/TD spell corpus before 1.0 (Psi-Storm needs depth ~3–4; a multi-stage ult ~5–6 —
   real headroom): `MaxEffectDepth = 8`, `MaxSequenceChildren = 8`, `MaxSearchTargets = 64`,
   `MaxSpawnCount = 64`, `MaxPersistentPeriods = 256`.

#### Primitive vocabulary

Domain = **Sim** (gameplay-truth, runs in the 30 Hz tick) or **Pres** (cosmetic, runs via `On*`
delegates only — may *never* read/write replicated state). Tier = **MVP** (1.0 core) or **Stretch**.

| Effect | Category | Domain | Tier | Purpose |
|---|---|---|---|---|
| `Damage` | damage | Sim | MVP | Deal `Fixed` amount of a damageType; reads the now data-driven damage×armor table (the lifted 4×5 `DamageMatrix`). Unifies the 3 inlined combat sites. |
| `Heal` | damage | Sim | MVP | Restore `Fixed` amount, clamped to MaxHealth. |
| `ApplyModifier` | modifier | Sim | MVP | Attach a Modifier by id to target for `durationTicks`. **Keystone.** |
| `RemoveModifier` | modifier | Sim | MVP | Strip a Modifier by id (reference-counted token revoke, OpenRA-style). |
| `ModifyResource` | resource | Sim | MVP | Add/subtract a `Fixed` resource delta for a faction. Generalizes `add_resources`. |
| `SetVariable` | resource | Sim | MVP | Assign a named scenario variable. IS `set_variable` (arithmetic/expressions are D2). |
| `SpawnUnit` | spawn | Sim | MVP | Spawn unit(s) for a faction at a point/caster; count ≤64; **fails closed** at the 4096 cap. Routes mesh registration through the existing `OnSpawnUnit` seam. |
| `Teleport` | movement | Sim | MVP | Instantly relocate target (Blink). |
| `FireProjectile` | spawn | Sim | MVP | Launch a projectile carrying an arbitrary **impact `EffectNode`** (not just raw damage) — skillshots deliver any effect. |
| `StartTimer` | resource | Sim | MVP | Start a named timer (ticks). IS `create_timer` (seconds→ticks at load). |
| `Victory` / `Defeat` | control | Sim | MVP | Declare match outcome for a faction. ARE the existing `victory`/`defeat` (fire via `On*`). |
| `Sequence` | composition | Sim | MVP | Run an ordered list of child effects (≤8). The AND/sequence node. |
| `SearchArea` | composition | Sim | MVP | Find entities in a `Fixed` radius, filter by `TargetFilter`, fire one child per hit in **ascending entity-ID** order; ≤64 targets. The generic AoE/splash node. |
| `Persistent` | composition | Sim | MVP | The **only** time axis: `initialEffect` + `periodEffect` every `periodTicks` for a finite `periodCount` (≤256) + `expireEffect`. Loops/iteration are D2. |
| **`Modifier`** (object) | modifier | Sim | MVP | First-class SoA object — see spec below. Not a leaf; applied/removed by `ApplyModifier`/`RemoveModifier`. |
| `TargetFilter` (flags) | targeting | Sim | MVP | OR-able flag set (Ally/Enemy/Neutral, Air/Ground/Structure, Self, Alive/Dead, Hero/NonHero) — WC3's Targets-Allowed; shared identically by abilities + triggers. |
| `PlayVfx` | presentation | Pres | MVP | Cosmetic; via `On*` delegate; statically forbidden from touching sim state. |
| `PlaySound` | presentation | Pres | MVP | Cosmetic. IS `play_sound`. |
| `DisplayMessage` | presentation | Pres | MVP | Cosmetic. IS `display_message`. |
| `ShakeScreen` | presentation | Pres | MVP | Cosmetic camera shake. |
| `Switch` | composition | Sim | Stretch | Data-side branching: cases of (bounded-boolean validator → child effect). Validator MUST be the bounded grammar (`!` `&&` `||`, comparisons, `count()`) — never arbitrary expression. |
| `NamedEffectReference` | composition | Sim | Stretch | Fire a named effect from the catalog by id (sub-munitions / chaining / reuse). Requires the server to lint the combined graph for cycles + cap depth at load. |
| `ApplyForce` / `Knockback` | movement | Sim | Stretch | Displacement via `Fixed` impulse; needs deterministic motion integration. |
| `Morph` | spawn | Sim | Stretch | Transform a unit into another type; re-validates SoA invariants + cap accounting. |
| `IssueOrder` | control | Sim | Stretch | Make target issue an order; depth-bounded to avoid command re-entrancy. |
| `Sequence` random-subset | composition | Sim | Stretch | `{minCount,maxCount}` selection drawn from the seeded sim RNG in (ascending-ID then draw) order. |

**Modifier object spec (SoA-attached):** `{ id, durationTicks, stackRule (Refresh | Stack | Ignore),
maxStacks, Fixed statDeltas (Speed / AttackDamage / AttackSpeed / MaxHealth / armorBonus), statusFlags
bitmask (Stunned | Silenced | Rooted | Invulnerable | Untargetable), optional periodicEffectId +
periodTicks }`. Buff = positive deltas; debuff = negative; **aura** = a short Modifier re-applied each
tick to units found via `SearchArea`; **DoT** = `periodicEffect = Damage`. Effective stat =
`base + Σ deltas`, **dirty-flagged** (not full recompute), resolved deterministically by `modifierId` +
`stackRule` in ascending entity-ID order. Requires a **new SoA modifier store**, a `ModifierSystem`
(`ISimSystem`), and **`Energy`/`Mana` SoA arrays on `EntityWorld`** (abilities need a cost resource).

#### Composition & targeting model

Composition is an **acyclic, depth/fan-out-bounded graph** of typed nodes — not a flat list, not an
unbounded tree. Leaves do the work; the three structural nodes (`Sequence`, `SearchArea`, `Persistent`)
plus the Modifier are the *only* over-time / stateful / fan-out mechanisms. Targeting is an explicit,
**finite frame of reference points** (`Caster`, `Source`, `Target`, `Point`, `Area`) carried down and
**re-rooted** as the graph is walked — `SearchArea` re-roots each child's `Target` to the found unit;
`Persistent` re-roots `Source` to its host — implemented allocation-free as a `readonly ref struct
EffectContext` copied per child call. *Which* entities are valid is the shared `TargetFilter` flag set,
identical across abilities and triggers. Every gameplay magnitude is an **externalized, named `Fixed`
(or tick-int) field** so the AI balance analyzer can enumerate every number; no inlined magic constants.

#### Bounding & static-validation rules (the server-validator checklist)

1. **Closed class registry.** Effect types are compiled sealed classes; JSON *configures* instances,
   never *defines* types or embeds code. Unknown type or dangling reference → **reject at deserialize**.
   No `RunScript`/Lua/JASS/`customParams` escape hatch — ever.
2. **Proven DAG.** The validator walks inlined children + named-effect references and rejects **any
   cycle** (including `A → Modifier whose periodicEffect → A`) before run. (The explicit fix for
   SC2/Mindustry deferring safety to runtime caps.)
3. **Hard structural caps** enforced at load *and* runtime: depth ≤8, `Sequence` children ≤8,
   `SearchArea` targets ≤64, `Spawn` count ≤64, `Persistent` periods ≤256. **No loop/iterate node** in
   D1 — iteration is D2's bounded problem and must not leak in via self-reference (blocked by the DAG
   rule) or effectively-infinite periods.
4. **Fixed-only in the tick.** All magnitudes authored as float/int in JSON, converted to `Fixed` (or
   tick-int) exactly once at **load** (existing `DamageMatrix.FromFloat` / seconds→ticks pattern); reject
   NaN/Inf pre-conversion. No float ever enters the 30 Hz tick.
5. **Single seeded `SimRng`** — built **now** as first-class sim state (none exists today; `StressTest`'s
   wall-clock `Randomize()` must never be the template). Seeded from the match seed, advanced only in-tick
   in deterministic depth-first authored order, **included in `SimChecksum`**. No effect may construct
   `System.Random`/Godot RNG; random selection sorts candidates ascending-ID *then* draws. **Until
   `SimRng` ships and is checksummed, the validator rejects any random effect.**
6. **Sim/Presentation domain is intrinsic to each node TYPE** (not author-chosen) and validator-enforced.
   Presentation nodes route through the `On*` seam and may never read/write `EntityWorld`/`ResourceStore`/
   building/variable/timer state; reject any presentation node feeding a sim node's input/filter. The sim
   tick must produce an identical `SimChecksum` whether or not the presentation pass runs. **No
   "damage-and-flash" convenience node** — compose a sim leaf + a presentation leaf under a `Sequence`
   (the Simple preset hides this).
7. **`Spawn` respects the 4096 cap + free-list** — spawns what fits, silently drops the rest, never
   throws, never blocks the tick (so every client hits the cap at the identical tick with identical
   free-list state).

#### Two-tier authoring

A **Simple** preset layer (parameterized templates — "AoE damage: radius / amount / filter") **compiles
down to the identical node graph** that **Advanced** mode edits raw. This directly answers SC2's
documented "powerful but overwhelming" cliff and honors the data-driven pillar / NFR-2 (first faction
≤12 min, no JSON required).

#### Module layout

- `godot/src/Effects/` (`ProjectChimera.Effects`, a peer of `Combat`/`Economy` — there is no `src/Sim`):
  the sealed `EffectNode` hierarchy, the leaf set, the three structural nodes, the `readonly ref
  EffectContext` frame, the work-stack executor, and the static validator. **Pure sim — no `using
  Godot`.**
- Serializable `EffectDef` DTOs live in `src/Core/Definitions/` beside `ScenarioData`/`TriggerDefinition`;
  runtime nodes stay in `src/Effects/`. Nodes are allocated **once** at scenario-load.
- `ModifierSystem` registers in the `SimulationLoop` order (before `CombatSystem` so effective stats are
  current when combat reads them; final placement validated in Step 5/6).

#### Migration sequence (strangler — each step golden-checksum-gated)

1. **Stand up the repo's first headless deterministic tests** — a golden-checksum harness that runs a
   fixed scenario N ticks via `SimulationLoop` and records the `SimChecksum` sequence. Pins current
   behavior before any change (zero tests today).
2. **Build `SimRng`** (deterministic PCG/xorshift over `Fixed`/int), thread it through systems by ref,
   include it in `SimChecksum` + `ReplayRecorder`/`Player`. Until then the validator forbids random
   effects.
3. **Data-drive `DamageMatrix`** — lift the hardcoded 4×5 table into `resources/data/damage_table.json`,
   load + `FromFloat` at scenario-apply, keep `DamageType`/`ArmorType` enums as stable keys. *This JSON
   is the artifact D3's balance analyzer consumes.*
4. **Introduce `DamageResolver.Apply(in ctx, amount, type)`** and re-point the three verified call sites
   (`CombatSystem.cs:271`, `ProjectileSystem.cs:76`, `:121`) to it — unify the formula + death/RecordKill/
   event sequence *without yet building the node tree*. Gate on byte-identical checksums.
5. **Create `src/Effects/`** — sealed `EffectNode` hierarchy + leaf set + `EffectContext`. Wrap
   `DamageResolver` as the `Damage` leaf.
6. **Add the three structural nodes + the static validator** (closed-type check, acyclic proof,
   depth/fan-out caps, domain enforcement) with the work-stack executor. Add **negative tests** (cycle,
   unknown type, cosmetic-touches-sim → all rejected).
7. **Build the Modifier subsystem** — reference-counted SoA modifier store + `ModifierSystem` + `Energy`/
   `Mana` arrays; wire `ApplyModifier`/`RemoveModifier`.
8. **Replace `TriggerAction[]` with `EffectDef[]`** in `TriggerDefinition`; rewrite
   `ScenarioDirector.ExecuteActions` to compile + `Apply` effect graphs; **delete the fat nullable
   `TriggerAction` class and the 8-case switch.** Preserve the `On*` delegate seam exactly.
9. **Only then add the `AbilitySystem`** as the third consumer referencing the same `EffectDef` compiler
   (cast/cooldown/cost/targeting block) — proving the single shared vocabulary. Add `Switch` +
   `NamedEffectReference` (with the cycle-linter) as the first stretch increment.

#### Prerequisites surfaced (carry forward)

- **`SimRng` does not exist** and is now a hard prerequisite for random effects and for several Modifier
  patterns. It is also a general determinism asset — fold into the M1 test-infra milestone.
- **`Energy`/`Mana` SoA arrays** are net-new on `EntityWorld` (ability cost resource).
- **Caps must be validated against a real spell corpus** before 1.0 (tuning dial, not a one-way door).

#### Hand-offs

- **→ D2 (Trigger-DSL):** the DSL emits **these same `EffectNode`s** as its action layer (retiring the
  parallel action switch). D2 owns variables/arithmetic/arrays/**bounded loops**/custom events/custom UI
  *around* this vocabulary; D1 owns the effects themselves.
- **→ D3 (Definition schema & loader):** D3 designs the `System.Text.Json` (de)serialization for
  `EffectDef`/`ModifierDef`, the `damage_table.json` schema, the named-effect catalog, and the `Hero`
  damage/armor-type addition. D1 only *constrains* the serialization shape (closed typed nodes, named
  references, Fixed-at-load).

---

### D2 — Trigger-DSL Design ✅ (decided 2026-06-20)

> Full options analysis + adversarial verification (10-agent deep-dive, code-grounded) lives in the
> working sidecar **`game-architecture.D2-briefing.md`**. This record is the canonical decision.

**Decision — Typed Event/Dataflow Graph (Option C).** Adopt a single canonical **typed event/dataflow
graph IR** for all creator logic: nodes connected by **two edge kinds** — *exec* edges (control flow:
"do this, then this") and *data* edges (typed values flowing between nodes, e.g. the killer entity, a
`Fixed` amount). This graph is the **one serialized representation** that all four authoring tiers
(T1 presets, T2 sentence/ECA editor, T3 `GraphEdit` node editor, T4 NL/AI) read and write. Critically,
**the trigger graph is a superset that CONTAINS D1's effect subgraphs**: a trigger's action region *is
literally a D1 `EffectNode` graph* embedded in the larger logic graph, executed by D1's executor
unchanged. D2 therefore **extends D1's validator and executor rather than duplicating them** — one graph
paradigm, one execution model, one static validator across spells + triggers + AI balance. The graph
serialization (persistent node ids + the two typed edge kinds) is **canonical from the very first
migration step**, even while only the T2/T4 front-ends exist; `GraphEdit` (T3) is a later, additive
editor *view* over an IR that was always a graph (no late content migration). The Godot `GraphEdit`
widget — officially "Experimental" — is kept a **replaceable view**: no `GraphEdit`/Godot types ever
enter the serialized IR, so the widget can be swapped without touching saved content, and T1/T2/T4 ship
the capability before the editor exists. **No scripting escape hatch** (inherited from D1) — the DSL's
expressiveness is bounded by exactly what the server can statically validate.

**Why C, not A or B.**
- The IR is **invisible plumbing the creator never touches** — authoring intuitiveness is decided by the
  four editors (identical across all options), so the choice is about build cost, risk, and how cleanly
  the plumbing fits D1 + the mandated visual editor.
- **A (nested statement-tree as data)** is the cheapest (closest to the as-built `ScenarioDirector`), but
  the *mandated* T3 visual editor becomes a permanent lossy list↔graph adapter, and there is a structural
  seam between A's "list of steps" and D1's effect graph — paying forever to save once.
- **B (bounded-imperative bytecode + a tiny deterministic VM)** has the highest theoretical ceiling, but
  that ceiling is *locked away by Chimera's own determinism + no-escape-hatch + static-validation rules*
  (no `while`/recursion regardless of option), so at MVP it exposes **identical** creator capability to A
  and C while costing the most (a mini-language + verifier + VM + four decompilers) and risking the most
  for a solo dev on the critical path. Its real value — scaling toward a general, non-deterministic
  "build any game" engine — directly contradicts decision #11 (WC3-parity bar, explicitly *not*
  Roblox-scale) and the determinism constraints; choosing B would mean reopening those. (Confirmed with
  the user as a product-vision check: Chimera is "Warcraft III reimagined with AI," not a general engine.)
- **C** is the only option where the mandated T3 editor renders the IR **directly** (no decompilation),
  where the action region *is* a D1 graph (cleanest unification), and where D2's static validator is
  D1's graph-linter **plus three rules** (data-edge purity, the bounded-loop node is the only back-edge,
  acyclic event edges) instead of a separate validator. Same creator power as B, better visual-editing
  experience, far smaller build surface.

**Settled sub-decisions (incl. user calls that override the briefing's recommended defaults):**
1. **Records/structs (labeled typed-field bundles) are IN the 1.0 MVP.** *User call, overriding the
   briefing's "first-stretch" recommendation.* Wave tables, quest/dialog definitions, inventory items,
   and autochess unit pools are *naturally* records; for a creation platform the authoring ergonomics
   justify the extra type-system + validator + serialization surface at launch. Parallel-array authoring
   remains available but is no longer the only path.
2. **Custom runtime UI is MVP and fully functional, including the write path with rich payloads.** *User
   call: "we need the systems to be fully functional."* Read path (UI bound to DSL variables) and write
   path (buttons raise DSL events into the sim) **both ship at 1.0**, and button-events carry **typed
   payloads up to a `Fixed` or `Point` argument** (not just small enums) — folded in now because widening
   the event later would force a second wire-format + replay-format change. Runtime-*created* widgets (vs
   pre-declared-and-toggled) remain stretch.
3. **Loop-bounding = the layered hybrid** (the heart of D2): **L0** the grammar cannot express
   non-termination (no `while`/recursion/`goto`; the only loop is `ForEach` over a snapshotted finite
   collection); **L1** custom-event dispatch is proven an acyclic **DAG at load** (legitimate feedback
   must cross a tick boundary); **L2** static cost rejection at load, computed from declared **caps** and
   summed over the event DAG's **transitive closure** (`MaxCascadeOps`, `MaxEventFanOut`); **L3** a
   checksummed per-tick fuel budget (`MaxDslOpsPerTick`) halting at a **whole-trigger boundary** — a
   seatbelt valid content never trips. Doctrine: **"reject at load, assert at runtime — never silently
   clamp."** The honest guarantee is **per-tick bounded cost, not whole-program termination** (timers /
   next-tick events are deliberately unbounded across a match — a *liveness*, not a determinism, concern;
   bounded by `MaxNextTickEventQueue`).
4. **`ForEachBatched` ships at MVP** as the sanctioned answer to ">cap" iteration ("do X to all 200
   enemies" → tick-dripped across frames), so the bounded-loop rule never forces silent truncation; a
   group whose provable max size exceeds a loop cap is a **load-time error** or a loud opt-in
   `ForEachUpTo(cap)`, never a silent `Math.Min` truncation.
5. **Variables = closed types × scopes, dense-index-keyed, checksummed.** Types: `Int`, `Fixed` (16.16,
   the *only* fractional numeric — no float type), `Bool`, `EntityRef` (id+generation), `FactionRef`,
   `Point` (Fixed X,Z), `TimerRef`, `Array<scalar>`, **+ `Record`** (per sub-decision 1). Scopes:
   Global / Per-player (slots 0..7) / Trigger-local (loop counters are *always* lexically-scoped locals —
   kills WC3's `Integer A` reentrancy bug). Stored SoA in a **top-level sim store** (sibling of
   `BuildingStore`/`ResourceStore`, NOT inside `ScenarioDirector`) so `SimulationLoop` can fold it into
   `SimChecksum`. Replaces the as-built `Dictionary<string,int> _variables`.
6. **Expressions = a CEL-shaped pure, typed, side-effect-free, Fixed-only sublanguage** — a strict
   generalization of D1's already-chosen bounded grammar (`! && || comparisons count()`) with arithmetic
   + typed variable reads; two-phase (type-check + cost-estimate at load, evaluate cheap in the tick;
   div-by-zero and NaN/Inf rejected at validation). Conditions are just boolean expressions (retires the
   pure-AND `AllConditionsMet`, gives real OR/NOT/grouping).
7. **Events = engine-emitted typed bus + acyclic custom events.** Sim systems *emit* typed events
   (closed structs, not stringified blobs) into a per-tick bus, replacing today's per-tick polling +
   string round-trip; threshold events support **both** a level (`WhileTrue`) and an edge (`OnCross`)
   form (a declared, migrated behavior change — *not* observably identical to today's level-triggered
   `resource_threshold`). Custom events: closed registry, `RaiseEvent` (same-tick, processed by a
   **work-list drain**) + `RaiseEventNextTick`; **a run-once trigger fires at most once per match even if
   re-raised; cooldown suppresses same-tick re-entry.** Zero per-tick heap allocation in the eval/event
   path (allocate-at-load).
8. **The four-tier *interoperate* promise = one shared IR with full bidirectional editing guaranteed
   only in the IR-native tier**; other tiers provide best-effort projection + a non-destructive fallback
   ("edit in graph view" placeholder). Inherent to any single-IR choice; stated as truth, not a defect.
9. **T4 NL/LLM authoring uses the SAME new server-side validator.** The as-built `LLMService` 5-/7-pass
   checker is a *value-range* check over the flat shape, invoked only during generation — D2 builds a
   **new** type-checker + graph-linter + cost-bounder and **promotes it to an authoritative load-time
   gate** (`LoadScenario.cs` does zero validation today; there is no server-side load path). This is the
   equalizer that makes AI authoring safe-by-construction — a claim no escape-hatch system can make.
   Reconcile the **50-vs-64 spawn-cap discrepancy** (as-built `Math.Min(…,50)` in three places vs D1's
   authoritative `Spawn≤64`) to one named constant during the D0 audit.
10. **Trigger evaluation gets a total deterministic order** `(Priority desc, then declaration-index asc)`
    via an explicit comparator — replaces the as-built **unstable `Array.Sort`** (`ScenarioDirector.cs:192`);
    dense-index var/timer stores replace **`Dictionary` enumeration** (`:149`); simultaneous timer
    expiries fire in declaration order. *(Two live nondeterminisms in shipped code — latent today, desync
    bombs the moment D2 adds shared mutable variables / fuel / cascades. Prerequisite fixes, gated by
    negative tests.)*
11. **Q4 cost/desync posture accepted in full** *(user call)* — DSL fuel + the event queue + the next-tick
    queue all fold into `SimChecksum`; a `rulesetHash` (the caps) is compared at the lobby handshake
    alongside the existing `scenarioHash`; a **checksum-algorithm-version field** ships in the first
    migration step (so a v0 replay never spuriously "desyncs" under a v1 algorithm); and **caps are
    corpus-validated as a gate on D2 before lock** — a *representation* gate, not a tuning dial.

**Named termination/cost constants** (named, reviewable, corpus-validated like D1's caps):
`MaxLoopIterations` (≈256), `MaxLoopNestingDepth` (≈3), `MaxEventCascadeDepth` (≈8), `MaxEventFanOut`,
`MaxCascadeOps`, `MaxDslOpsPerTrigger`, `MaxDslOpsPerTick`, `MaxArrayCapacity` (≈256),
`MaxNextTickEventQueue`, `MaxDslEventsPerTick`, `MaxVariablesPerScenario`, `MaxWidgets` (≈256),
`MaxUiDepth` (≈8), `MaxListRows` (≈64).

#### Custom-UI binding model (FR-26)

UI lives in the **presentation layer** (per-client `Control` nodes, *not* replicated, *not* in
`SimChecksum`) yet must display sim-truth and feed the sim — two rails on existing infrastructure:
- **READ rail (sim → UI), `CustomUiBridge` + versioned `DslVarReadback`** (modeled on `FogOfWarBridge`):
  at the tick boundary the sim publishes a read-only, version-stamped snapshot of the var table; widgets
  pull in `_Process` and re-format only on version change. **Formatting is presentation-side** (int→str,
  Fixed→`mm:ss`); strings never enter the tick. Cannot desync. Ships scoreboards/wave-counters/timers
  with zero command-rail change.
- **WRITE rail (UI → sim), new `DslEventCommand` on the lockstep command bus** (analog of `EnqueueOrder`):
  a button's `Pressed` handler **mutates nothing** — it enqueues an event command that rides the existing
  buffered/serialized/`currentTick+delay` pipeline, so every client applies it at the identical tick.
  Authorization is **net-new sim state** (a per-event allowed-raiser set — the as-built `:601` check is
  unit-ownership, which a UI event lacks). Pinned tick-phase order: apply DSL events → sim systems tick →
  `ScenarioDirector` drains the bus. Wire encoding: a **parallel capped event list** in
  `TickCommandPacket` (`…orderCount+orders[]+eventCount+events[]`), each event `eventId + up to a
  Fixed/Point arg`. **Local-only buttons** (toggle a panel) use a closed presentation-action whitelist,
  statically barred from any DSL var/event (disjoint namespaces). UI is a declarative widget tree in
  `ScenarioData` (covered by `scenarioHash`) from a closed vocabulary
  (`Panel/Label/Counter/ProgressBar/Button/Timer/Leaderboard/FloatingText/ItemList`); every `BindVar`/
  `BindEvent` resolves + type-matches at load.

#### Migration sequence (strangler — golden-checksum-gated, always-shippable)

Preserves the `On*` delegate seam and the "evaluates last on settled state" contract; reuses D1's
golden-checksum harness. **Begins only after D1 steps 1–2 and 8 land** (test harness + `SimRng` +
checksum/replay inclusion; `TriggerAction[]`→`EffectDef[]`, switch deleted). Invariant split: steps
assert **identical observable outcomes**; a `SimChecksum` baseline **re-pin** is a *named, expected
event* at the steps that change what `Compute` hashes (the var-table step and the fuel step).

- **D0** — land on D1's seam + audit `ExecuteActions`: classify every action Sim vs Pres; reconcile the
  runtime clamps (`Math.Min(…,50)` vs D1 `≤64`) and runtime float→Fixed (`add_resources`, `create_timer`)
  against D1's load-time discipline. *Baseline tag; no behavior change.*
- **D1s** — typed `DslVarTable` hoisted to a top-level store + checksum inclusion; change the
  `SimChecksum.Compute` signature + every call site (`SimulationLoop.cs:98/135`, `MainScene.cs:268`);
  establish the **graph-canonical serialization** (node ids + typed edges) in `ScenarioData`; add the
  checksum-algorithm-version field.
- **D2s** — CEL-shaped Fixed-only expression evaluator; conditions → boolean tree; delete the
  float-epsilon `Compare` (`:364`) + `float.TryParse` (`:252`); retype `OnSpawnUnit`/`TriggerDefinition`
  floats → Fixed; install the total `(Priority, decl-index)` trigger order.
- **D3s** — event-driven bus + typed payloads (retire polling + per-tick GC); threshold level/edge forms;
  secure **killer/last-hit attribution** on the death event (combat-layer prerequisite, §below).
- **D4s** — custom events + acyclic-dispatch DAG proof + transitive cascade-cost bound; same-tick
  work-list drain with pinned run-once/cooldown semantics; `RaiseEventNextTick` bounded + checksummed.
- **D5s** — bounded `ForEach`/`ForEachBatched` + in-action `Branch` (D2's own branching — does **not**
  depend on D1's stretch `Switch`); wire per-tick fuel into `SimChecksum` (re-pin). *Makes TD/autochess
  authorable.*
- **D6s** — promote the type-checker + graph-linter + cap/cost validator to the **authoritative
  pre-tick load gate**; reconcile the 50/64 constant; `scenarioHash` covers the larger serialized form.
- **D7s** — T3 `GraphEdit` view (additive only — IR was a graph since D1s, so *no content migration*).
- **D8s** — custom-UI **read path** (`CustomUiBridge` + `DslVarReadback` + closed widget set incl.
  `ItemList`). Pure presentation; no rail change. *Ships scoreboards/wave-counters/timers.*
- **D9s** — custom-UI **write path** (`DslEventCommand`): extend `TickCommandPacket`; add
  `EnqueueDslEvent`; **bump `ReplayRecorder.VERSION → 2` with a DSL-event record kind + a `ReplayPlayer`
  parse/apply branch**; thread DSL-event application through **all four** command-apply sites (live
  `:315`, spectator `:261`, `ReplayPlayer.ApplyOrders`, recorder `:318` — recommend first unifying the
  three `ApplyOrders` copies); net-new per-event authorization. *(The "replays are free" claim was FALSE;
  this is real, scoped engineering.)*

#### Prerequisites surfaced (carry forward)

- **D1's `src/Effects/` + `EffectNode` + graph executor + graph validator** — D2 contains/extends these
  (hard dependency on D1 steps 5–8). *(Confirmed absent today.)*
- **`SimRng`** — required before any random DSL construct; its draw-order determinism *depends on* the
  total-trigger-order fix. *(Confirmed absent.)*
- **`SimChecksum` signature change** to hash vars/timers/event-queue/next-tick-queue/fuel — closes the
  confirmed desync hole (`SimChecksum.cs:26-57` hashes only World/Buildings/Resources).
- **Total trigger order + dense-index var/timer stores** — fixes the unstable `Array.Sort` (`:192`) and
  `Dictionary` enumeration (`:149`). *(Both verified.)*
- **Combat-layer killer/last-hit attribution** on `unit_dies` (carries victim slot only, `:126`) —
  without it, MOBA last-hit gold / kill-credit quests are unbuildable. A D1/combat prerequisite, not a
  DSL feature.
- **A *new* static validator** (type-check + graph-lint + cap/cost) promoted to a **server-side
  load-time gate** — distinct from the as-built generation-time value-range checker.
- **Replay format v2** (`DslEventCommand` record) + four apply-sites; **`Record`-type serialization**
  (new at MVP per sub-decision 1).
- **Caps corpus-validated as a gate on D2** (loop/nesting/cascade/fan-out/fuel/array/widgets) before lock.

#### Residual risks / watch-items

1. **The caps ARE the architecture** (highest residual): "~90% of real content is bounded" is asserted,
   not proven. If the corpus shows common single-tick "do X to every unit on the map," `ForEach`-over-
   finite + `ForEachBatched` won't paper it — reopen the *envelope*, not the constants. Corpus validation
   gates D2.
2. **Cap-product cost narrows the DSL more than "nest freely" suggests** — `MaxArrayCapacity` × nesting
   rejects deep loops at load; document the ceiling as an acceptance criterion.
3. **`GraphEdit` "Experimental"** — mitigated by the editor-agnostic, graph-canonical-from-D1s IR +
   replaceable view + non-graph tiers first; but T3 is MVP, so budget a possible view-swap.
4. **Write-path is a bigger build than it looks** (network + replay-v2 + four apply-sites + new
   authorization) — de-risked by read-path-first, but real engineering.
5. **Level→edge threshold migration** breaks sustained-state maps relying on level semantics — both forms
   supported, but D3s is *not* checksum-identical (declared change).
6. **Runtime strings are permanently out** (no player-named heroes / typed passwords) — inseparable from
   determinism; stated plainly, not hidden.

#### Hand-offs (→ D3)

- **→ D3 (Definition schema & loader):** D3 owns the `System.Text.Json` (de)serialization and must encode
  deterministically into `ScenarioData` (so `scenarioHash` stays meaningful): **the graph IR** (logic
  nodes `ForEach`/`ForEachBatched`/`Branch`/`RaiseEvent`/`SetVariable`/`StartTimer`/get-set/expression,
  two edge types, persistent node ids, embedded D1 `EffectDef` action subgraphs); the **variable schema**
  (name/type/scope/initial, closed types incl. `Array<T>` capacity **and `Record` field shapes**); the
  **custom-event registry** (names + typed params + per-event allowed-raiser set); the **UI-definition
  schema** (closed widget tree incl. `ItemList`, `BindVar`/`BindEvent`/`Format`/layout + named caps);
  **authoring-affordance annotations** (T3 node positions, T1 preset origin, T4 prompt provenance — never
  destroyed); and the **replay-v2** `DslEventCommand` record schema. Constraint on shape: closed typed
  nodes only, named references, **Fixed-at-load** (convert once, reject NaN/Inf). D2 only *constrains* the
  serialization; D3 designs it.

---

### D3 — Data-Driven Definition Schema & Loader ✅ (decided 2026-06-20)

> Full options analysis + adversarial verification (14-agent code-grounded deep-dive; 4 adversarial
> reviewers raised 17 non-minor issues, 7 folded in as design changes) lives in the working sidecar
> **`game-architecture.D3-briefing.md`**. This record is the canonical decision. **Decision = Option B
> (Maximal-now), and Alec pulled ALL FOUR defer-recommended items forward** — source-gen now, fully
> populated migration registry, replay-v2 in lockstep, AOT analyzer as a CI gate — consistent with the
> D1/D2 "build it fully functional now" overrides. D3 is the **last of the deep-dive trio (D1→D2→D3)**.

**Decision — Unified, fail-closed, deterministic schema & loader (Option B).** Replace the as-built
serialization layer — which is *scattered* (5–7 independently-constructed `JsonSerializerOptions` with
three different behaviors), *unvalidated* (`LoadScenario`/`LoadFromFile` trust everything), and
*byte-fragile* (`scenarioHash` is FNV-32 over raw file **bytes**, so whitespace / key-order / `1.0`-vs-`1`
/ a moved editor node spuriously flips it) — with a single, trustworthy pipeline:

1. **One `ContentLoader` choke point** + **one canonical `static readonly JsonSerializerOptions`**
   (`ReadCommentHandling=Skip`, `AllowTrailingCommas`, `WriteIndented`, per-enum
   `JsonStringEnumConverter<T>`, **`AllowDuplicateProperties=false`**). The divergent `FactionDefinition`
   / `ContentPackager` / preview options are unified onto it. A separate lenient case-insensitive ingest
   options exists **only** for the T4 LLM path.
2. **A model-level `Validate(model)` gate at the `ApplyScenario` boundary** — the authoritative pre-tick
   gate. Contract: **no `ScenarioData` reaches `ScenarioDirector.LoadScenario` unvalidated**, on *every*
   path (file-loaded, AI-generated, fallback, editor-in-memory, replay-loaded). Two-stage: STJ → dumb
   transient DTOs (syntax, fail-closed) then pure-C# compile/validate (type-check + graph-lint + cap/cost,
   fail-closed). The DTO stage is transient and **must not survive into the tick**.
3. **Custom `JsonConverter<NodeBase>` + closed type registry** for the polymorphic D1/D2 node IR
   (discriminator `kind`, first property). **Forced**: built-in `[JsonPolymorphic]` is incompatible with
   `UnmappedMemberHandling.Disallow` (the `$type` token is itself "unmapped" and throws — dotnet/runtime
   #100057 **open**), so it cannot give a closed IR both polymorphism *and* strict unknown-property
   rejection. Lookup-miss → located `JsonException`. A `JsonConverter<Fixed>` fronts the verified-unguarded
   `Fixed.FromFloat` and **rejects NaN/Inf** at the load boundary.
4. **Canonical-model hash** (FNV-64) computed over the parsed model's **`Fixed.Raw` integers** (never
   float text), fixed field order, enums as stable **name** (not ordinal — ordinal drifts when `Hero`
   inserts before `COUNT`), sorted collections (nodes by id; edges by `(src,srcPort,dst,dstPort)`; sparse
   maps by key), **authoring annotations excluded**, **covering ALL gameplay files** (scenario + faction
   DTOs + named-effect catalog + N-resource registry). Single-pass canonicalizer feeds both the hash and
   the validator. Same domain as `SimChecksum`. The old byte-FNV is **frozen as algo-1** (a literal-zip
   tamper check, hashed pre-save); the canonical-model hash is **algo-2** (the handshake/equality channel).
5. **Source-gen `JsonSerializerContext` adopted now** (hybrid — converter-driven node/Fixed/graph types
   run in metadata mode; converter-free leaf DTOs get the fast path). All DTOs/converters stay Godot-free
   in `src/Core/Definitions` so they are AOT-eligible. Per-enum **generic** `JsonStringEnumConverter<T>`
   only (the non-generic reflective factory is an AOT trap).
6. **Versioning:** integer `schema_version` on `ScenarioData` (absent ⇒ one-time legacy amnesty to v1);
   a **fully populated** `JsonNode`-DOM migration registry (vN→vN+1 transforms run on the mutable DOM at
   load, *then* deserialize; never silently rewrite subscribed content); strict gameplay region uses
   `Disallow`+throw (**no** extension bag) while only an explicit `_editor`/`_ext` namespace gets
   verbatim round-trip, guarded by a denylist so no registry gameplay-key name can hide in the excluded
   region; `checksum_algo_version` (bootstrap-excluded) so old replays/manifests never spuriously desync.
7. **Enforced `min_game_version`** (verified today: written but **never read**) — a `CurrentGameVersion`
   constant + InvariantCulture semver-prefix compare checked **before** strict deserialize + **auto-stamp**
   from each registry entry's `introduced_in` (creators never hand-maintain it). This is the strict
   region's only forward-compat safety valve.
8. **In-tick determinism invariant (A17).** Eliminate the verified live `Fixed→float→"F2"-string→
   float.TryParse` round-trip that gates triggers every tick (`ScenarioDirector.cs:168/170/252`):
   `FiredEvent` payloads carry **`Fixed.Raw` ints**, all threshold comparisons are **Fixed-vs-Fixed**, and
   `InvariantCulture` is pinned process-wide. A perfect on-disk hash is otherwise defeated at tick N.

**Why B, not C or A.**
- **A (evolve the as-built)** is cheapest but **under-delivers D2**: D2 mandated annotations *round-tripped
  but excluded from the gameplay hash* — impossible on a byte-hash — and a gate that covers all execution
  paths; A leaves the in-tick desync (A17) unaddressed and accrues AOT debt. Below the bar.
- **C (balanced)** was the recommendation for a cost-sensitive solo dev: build everything load-bearing for
  determinism + the D2 hand-off now, defer pure future-proofing with no 1.0 consumer (shipping AOT, full
  migration corpus, exhaustive replay-v2).
- **B (chosen)** = C **plus** the four pull-forwards. Alec's call: pay the larger near-term diff to reach
  the best end-state with the lowest long-term risk, matching the maximal-1.0 posture (decision #13) and
  the D1/D2 precedent. The added cost is real but front-loaded; nothing in B is *wrong*, only *earlier*.
- **Forced regardless of A/B/C:** the custom converter (R1/#100057), the canonical-model hash (D2's
  annotation-exclusion mandate + .NET's shortest-roundtrip float-text drift, R7/R8), A17, and enforced
  `min_game_version` — these are correctness, not preference.

**Settled sub-decisions (the decided calls):**
1. **Polymorphic mechanism = custom `JsonConverter<NodeBase>` + closed registry**, discriminator `kind`
   (avoids collision with the retiring `TriggerDefinition.type` during the migration window), first
   property, registry-backed and **decoupled from the C# class name** (rename-safe; class-name keying
   destroys instances, the Unity `SerializeReference` lesson).
2. **Source-gen now** (hybrid metadata mode); converters hand-written AOT-safe; generic enum converters
   only; `Converters` must resolve via the metadata resolver, never the fast path (else the converter is
   silently bypassed).
3. **Single `ContentLoader` + model-level gate at `ApplyScenario`** (covers in-memory + replay paths).
4. **Canonical-model hash** as in the decision above; **block on hash 0 / short / unknown-algo**, remap a
   legitimate canonical-0 to 1 (FNV-sentinel) — closes the verified 0-hash fail-open in the handshake.
5. **In-tick encoding (A17):** `Fixed.Raw` payloads + Fixed-vs-Fixed compares + pinned `InvariantCulture`.
6. **Versioning:** integer `schema_version` (amnesty), **fully populated** DOM migration registry, strict
   (`Disallow`+throw, no bag) / tolerant (`_editor`/`_ext` verbatim + gameplay-key denylist) split,
   enforced `min_game_version` with auto-stamp, `checksum_algo_version`.
7. **`damage_table.json` = named-key object-of-objects** keyed by enum **name**; **add `Hero` DamageType
   (5th) and `Hero` ArmorType (6th), inserted *before* `COUNT`** (→ 5×6); unspecified cell ⇒ 1.0.
   (Positional arrays corrupt silently on enum reorder; the matrix sizes off `COUNT`.)
8. **N-resources = top-level ordered resource registry**; balances/costs as sparse `{resourceId: amount}`
   maps (generalizes the Ore+Crystal ceiling; scope reconciled to PerPlayer 0..7).
9. **Tech-tree = inline `prerequisites: string[]`** resolved against a data-driven id registry (retires
   the three hardcoded `TechTreeChecker` switches).
10. **Annotation channel = single in-file `_editor`/`_ext` sidecar**, keyed by node id, independently
    versioned, verbatim-preserved; hash excludes it; a tier that cannot render a known node still
    **preserves it byte-faithfully through `JsonNode`** (never a lossy DTO).
11. **Named-effect catalog = top-level id-keyed `EffectDef` map**, referenced by id (never inlined),
    cycle-linted at load, with **cross-file referential-integrity at import** (rejects dangling ids).
12. **Replay-v2 built in lockstep:** new `DslEventCommand` record, per-record discriminator, `Fixed`/
    `EntityRef` raw ints; bump `ReplayRecorder.VERSION` 1→2, **hard-reject v1**; **embed the canonical
    scenarioHash + algo-version in the header** and **re-gate the referenced scenario on playback**,
    asserting recorded-hash == loaded-hash before the first `Flush` (fail closed, never silent desync).
13. **`rulesetHash` corpus pinned to every constant the tick reads** — spawn cap (post-50→64), 4096
    entity cap, 30 Hz, resource-slot count, damage-table dims incl. `Hero`. Compared at the lobby
    handshake alongside `scenarioHash`. **The 50→64 spawn-cap reconciliation is a hard prerequisite.**
14. **Protocol hardening:** add the missing `PROTOCOL_VERSION` mismatch **rejection in the Hello
    handshake** (today exchanged but never compared) and bump it there — a host requiring `rulesetHash`
    **rejects** an old-protocol peer rather than relying on the `Ready` reader's `len>=5` tolerance.
15. **AOT analyzer = CI gate now** over the Godot-free `Definitions`/`Effects` sim source (static
    trim/AOT IL analysis). The full `PublishAot` *build* gate activates with the dedicated-server
    **`.csproj` project-split** — which D3 now elevates to a **near-term engine-section decision**
    (pulled forward by the AOT posture; `PublishAot` is unsupported under `Godot.NET.Sdk` with
    `EnableDynamicLoading=true`, so AOT can only target a separate Godot-SDK-free server project sharing
    the sim source).

#### Migration sequence (strangler — golden-checksum-gated; slots into D1 steps 1–9 and D2 steps D0–D9s)

- **D3.0** (rides D2 **D0**) — `ContentLoader` skeleton + the one canonical options + `FixedConverter`
  (NaN/Inf reject); **unify `FactionDefinition`/`ContentPackager`/preview options onto it**; decouple
  `ExportMapPackage` save from hash (`Pack` hashes pre-save bytes; freeze package byte-hash as algo-1).
  *Gate:* existing scenarios load byte-identical; existing `.chimera.zip` corpus still `Unpack`s; the same
  `UnitDefinition` JSON via scenario-path and faction-path yields byte-identical `Fixed.Raw` fields.
  **Hard invariant: D3.0 options-unification MUST precede D3.2's enum lift**, or the two loaders silently
  bind the same `UnitDefinition` differently.
- **D3.1** (rides D2 **D1s**) — `schema_version` + `checksum_algo_version` + **canonical-model hash
  (algo-2)**; legacy amnesty (absent ⇒ v1/algo-1); **land enforced `min_game_version`**; re-point
  `MainScene.cs:303` from `ComputeFileHash(ScenarioPath)` to the canonical hash over the **in-memory**
  loaded model (fixes the AI-gen stale-file hash). *Gate:* model-hash stable across re-save / key-reorder /
  whitespace / comment-insert; two peers loading the same AI-generated scenario produce matching hashes.
- **D3.2** (rides D1 **step 3**) — `damage_table.json` (5×6, `Hero`×`Hero`) replaces `DamageMatrix._table`;
  enums extended **before `COUNT`**. *Gate:* damage outcomes bit-identical to the hardcoded matrix on all
  legacy 4×5 cells.
- **D3.3** (rides D1 **steps 4–6**) — `EffectDef`/`ModifierDef` DTOs + custom node converter + registry +
  thin stage-2 builder; named-effect catalog with cross-file referential-integrity. *Gate:* every D1
  runtime node round-trips; unknown `kind` and dangling catalog id rejected with located errors.
- **D3.4** (rides D2 **D2s–D4s**) — graph IR + variable schema + custom-event registry serialized;
  `Dictionary<string,int>` stores → dense SoA folded into `SimChecksum`; **A17** (delete the
  float/"F2"/TryParse round-trip); replace the unstable trigger `Array.Sort` with a stable total order
  (Priority desc, ascending persistent node-id tiebreak). *Gate:* graph canonical-hash invariant to node
  reorder; `SimChecksum` widened (vars/timers/Crystal/all slots); a threshold trigger fires identically
  with no float/culture in the path.
- **D3.5** (rides D2 **D5s**) — N-resource registry + data-driven tech-tree. *Gate:* N=2 reproduces legacy
  balances exactly.
- **D3.6** (rides D2 **D6s**) — **promote the validator to the authoritative pre-tick gate** over the
  model on all paths; reconcile **50→64** spawn cap; UI-definition schema + bind-resolution at load;
  **AOT analyzer CI gate activates**. *Gate:* a malformed-scenario corpus (incl. an AI-generated one)
  each rejected with the correct specific error; all valid pass; spawn cap = 64 everywhere.
- **D3.7** (rides D2 **D7s–D8s**) — annotation channel live; hash confirmed to exclude `_editor`; verbatim
  round-trip across tiers + gameplay-key denylist. *Gate:* cosmetic-only edit ⇒ same hash; sim-semantic
  edit ⇒ different hash; a T4 node survives a T2 open+resave with unchanged hash.
- **D3.8** (rides D2 **D1s lobby**) — `rulesetHash` in the Ready packet (corpus = pinned tick-read
  constants; 50→64 already done); **add the Hello `PROTOCOL_VERSION` rejection + bump**; block on hash 0.
  *Gate:* matched caps ready; mismatched blocked with reason; old-protocol Hello rejected.
- **D3.9** (rides D2 **D9s**) — **replay-v2 in lockstep**: `DslEventCommand` record; `VERSION` 1→2; v1
  hard-rejected; embed canonical hash + algo-version; re-gate on playback, assert recorded==loaded before
  first `Flush`. *Gate:* a recorded match replays bit-identically; a migrated-world replay reproduces OR
  fails closed — never silently desyncs.

#### Prerequisites surfaced (carry forward)

- **`Fixed.FromFloat` NaN/Inf reject** (verified unguarded; enforced by `FixedConverter`, D3.0).
- **In-tick float/culture removal (A17)** — verified live; the load hash is defeated at tick N without it.
- **Options unification before any new enum** (verified latent divergence; D3.0 before D3.2).
- **Dense-index var/timer/event-queue store** replacing `Dictionary<string,int>` (for `SimChecksum`
  coverage + deterministic ordering).
- **Ceiling reconciliation** — D2 scope is PerPlayer 0..7 but `ResourceStore.FACTION_COUNT=5`,
  `ScenarioDirector` hardcodes `slot<2`, `Math.Min(count,50)` vs D1's `≤64`. In the `rulesetHash` corpus
  by definition; reconcile before D3.8.
- **NativeAOT requires a dedicated-server `.csproj` project-split** (not just a `JsonSerializerContext`) —
  now **pulled forward** as a near-term engine-section decision by the AOT-CI posture. D3 keeps the source
  AOT-eligible; the split itself is D5/engine-section work D3 surfaces.
- **`min_game_version` is an unbuilt subsystem**, not a field (constant + comparer + gate + auto-stamp).
- **Wire-format / protocol bump** — D3.8 grows the Ready packet *and* adds the missing Hello version check.

#### Residual risks / watch-items

1. **The caps/hash domain is the architecture** — `scenarioHash` and `SimChecksum` must each cover the
   FULL model/state, not mirror each other's current narrow coverage (`SimChecksum` today omits Crystal,
   slots 3+, vars/timers; widened at D3.4).
2. **Replay path is the counter-example to "the gate runs everywhere"** — fixed by embedding the hash +
   re-gating on playback (D3.9); a later-edited scenario at the stored path otherwise replays garbage.
3. **Cross-file/faction content** must be under the content hash + an import-time referential-integrity
   pass (today `Pack` hashes `scenario.json` only).
4. **Two-region leak direction** — a gameplay key emitted into `_editor`/`_ext`, or a future gameplay
   field an old loader can't map, must fail closed (strict `Disallow`+throw, no bag; denylist).
5. **Allocation contract is conditional** — "kind-first" is canonicalizer-enforced so the converter reads
   straight into the concrete node; the transient DTO must not survive into the tick (load-allocation
   budget in the D3.4 gate).
6. **Canonical-walk cost on max-caps UGC** — single-pass, total-order sorts, `Fixed.Raw` as fixed-width
   LE int32; assert worst-case hash completes within the lobby-handshake budget (D3.1 perf gate).
7. **Migration determinism** — pin `InvariantCulture`; keep migrations pure (a culture-sensitive parse or
   dictionary enumeration inside a migration desyncs upgraded bytes across machines).

#### Hand-offs

- **→ D4 (Hero persistence):** the persistent-artifact profile is *more authored content* that flows
  through this same `ContentLoader` gate, canonical hash, versioning, and Fixed-at-load discipline; D4
  decides the init-time-deterministic, server-validated *shape*, D3 already owns *how it serializes*.
- **→ D5 (>2-player lockstep + matchmaking):** D3 delivers `rulesetHash`, the protocol-version rejection,
  the 0-hash block, and PerPlayer 0..7 scoping that D5's N-player handshake/topology builds on. **D3 also
  elevates the dedicated-server `.csproj` project-split (AOT target) into D5/engine-section scope.**
- **→ D6 (LLM provider abstraction):** the T4 lenient ingest options + the authoritative model-level gate
  are the safe seam — AI output is validated by the *same* gate as hand-authored content, never trusted.
- **→ Implementation (Step 6+):** D1+D2+D3 together define the complete serialized contract; the strangler
  steps D3.0–D3.9 interleave with D1 1–9 and D2 D0–D9s as a single migration program.
