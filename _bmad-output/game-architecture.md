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

**In progress:** Step 4 (Architectural Decisions). **D1 (effects-primitive vocabulary) is decided
(2026-06-20)** — see *Architectural Decisions (Step 4)* below. Next action is **D2 (trigger-DSL
design, consumes D1)**. Full Step 4 working state in **`game-architecture.RESUME.md`**.

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
> **Settled so far:** D1 ✅ (2026-06-20). **Next:** D2 (Trigger-DSL design — consumes D1).

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
