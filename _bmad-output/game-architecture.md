---
title: 'Game Architecture'
project: 'Project_Chimera'
date: '2026-06-20'
author: 'Alec'
version: '1.0'
stepsCompleted: [1, 2, 3, 4, 5, 6, 7, 8, 9]
status: 'complete'
engine: 'Godot 4.6.3 (.NET)'
platform: 'PC desktop — Windows primary, Linux dedicated/headless'

# Source Documents
gdd: 'Project_Chimera_GDD.md'
prd: '_bmad-output/planning-artifacts/prds/prd-Project_Chimera-2026-06-05/prd.md'
ux: '_bmad-output/planning-artifacts/ux-designs/ux-Project_Chimera-2026-06-20/'
brownfield_architecture: '_bmad-output/architecture.md'
epics: null
brief: null
---

# Game Architecture

## Executive Summary

**Project Chimera** — an RTS *creation platform* ("Warcraft III reimagined with AI") — is built on
**Godot 4.6.3** (.NET build) with **C# / .NET 8**, targeting **PC desktop** (Windows primary; Linux for
dedicated/headless servers). It is a **brownfield project at Phase 5 → 1.0**: Phases 0–4 are code-complete;
the creation suite, trigger-DSL expansion, hero persistence, and >2-player multiplayer are net-new.

**Key architectural decisions:**

- **One shared, closed, statically-validated Effect-Graph vocabulary** (D1) is the *only* effect surface for
  abilities, triggers, and AI balance — **no scripting escape hatch** — bounded so every shared construct is
  server-validatable before any multiplayer tick.
- **A single typed event/dataflow graph IR** (D2) underlies all four authoring tiers and *contains* D1's
  effect subgraphs — one graph paradigm, one executor, one validator across spells + triggers.
- **Determinism is sacred:** 16.16 `Fixed` end-to-end (convert at parse), ascending-ID iteration, a new
  seeded `SimRng`, and a generalized `SimChecksum` + canonical-model multi-hash handshake (D3/D5) —
  remediating three latent peer-agreement bugs found in the as-built code.
- **The 2,223-LOC `MainScene` god object is strangled in place** (Step 6) into a thin ordered phase-list
  constructing a Godot-free `SimulationHost` + `ScenarioApplier` behind a fail-closed `ScenarioValidator` —
  now headless-testable and reused by a `ServerBootstrap`.
- **Two-tier testing from zero** (Step 5): fast Godot-free **xUnit** golden-checksum tests + in-engine
  GdUnit4, with a banned-API analyzer and a cross-platform check-runner.

**Project structure:** the sacred Simulation/Presentation split, a SoA entity model, `src/<System>/`
organization with net-new `Effects/`, `Dsl/`, and `Sim/` modules — every D1–D6 + Step-5 module has a
definitive home.

**Implementation patterns:** 67 patterns (7 novel + a standard catalog + a Consistency Rules table), each
with a determinism-safe example and analyzer/test/convention enforcement, so AI agents implement
checksum-identically.

**Validation:** PASS — coherent, complete, no determinism or correctness blockers (30 findings →
22 confirmed / 8 refuted; all resolved this pass).

**Ready for:** the epics/stories phase → `gds-check-implementation-readiness` re-run → `gds-sprint-planning`.

---

## Document Status

This is the **forward-looking** technical architecture for Project Chimera — the decisions
that keep AI agents implementing consistently on the path to 1.0. It is created through the
GDS Architecture Workflow and is informed by, but distinct from, the brownfield
`architecture.md` (which documents the code **as-built**, deep scan 2026-06-05).

**Steps Completed:** 9 of 9 (Complete)

**Step 4 COMPLETE (2026-06-20).** All six game-specific decisions are settled: **D1** (effects-primitive
vocabulary), **D2** (trigger-DSL design), **D3** (data-driven definition schema & loader) — the deep-dive
trio — plus **D4** (hero persistence), **D5** (>2-player lockstep + matchmaking), **D6** (LLM provider
abstraction), batched as recommend-and-confirm with Alec's scope calls recorded. See *Architectural
Decisions (Step 4)* below; full D4–D6 options analyses in the `game-architecture.D{4,5,6}-briefing.md`
sidecars.

**Step 5 COMPLETE (2026-06-20).** Cross-cutting concerns recorded — testing/quality architecture is the
headline (two-tier checks, an AI-orchestrated cross-platform check-runner), plus determinism enforcement,
observability/desync-diagnosis, error handling, performance, quality gates, accessibility, and the
completeness additions (config mgmt, UGC safety, migration/replay-compat testing). Alec's calls baked in.
See *Cross-Cutting Concerns (Step 5)* below + the `game-architecture.Step5-cross-cutting-briefing.md`
sidecar.

**Step 6 COMPLETE (2026-06-20).** Project structure + the `MainScene` decomposition recorded — the headline
brownfield problem (the 2,223-LOC `MainScene` god object that is *also* the composition root every D1–D6
system threads through). Decision: **Shrinking Composition Root + Sim-Spine Strangler** — `MainScene` stays
the scene root but stops doing work, becoming a thin ordered phase-list that constructs a Godot-free
`SimulationHost` + `ScenarioApplier` (the sim-mutation path, now testable headless + reused verbatim by a new
`ServerBootstrap`) behind the net-new fail-closed `ScenarioValidator` gate, plus focused presentation
coordinators in the exact preserved `_Ready()` order. Every net-new D1–D6 + Step-5 module gets a definitive
home; a `FactionRegistry` localizes the 2-faction hardcodes for the D5 N≤8 path. Produced via a 16-agent
design+adversarial-verify workflow (winning strategy 93/100). Alec's four scope calls baked in. See
*Step 6 — Project Structure + `MainScene` Decomposition* below + the
`game-architecture.Step6-structure-briefing.md` sidecar.

**Step 7 COMPLETE (2026-06-21).** Implementation-pattern catalog recorded — the canonical "how an AI agent writes
this codebase" so D1–D6 are implemented checksum-identically: **7 Novel Patterns** (the deterministic kernel, the
Effect-Graph executor, Modifier SoA, the DSL event/dataflow graph, the two-rail custom UI, the `Validated<T>`
fail-closed gate, the canonical-model multi-hash handshake) + **Standard Patterns** answering every divergence
point + a **Consistency Rules table**, each with a determinism-safe code example and an analyzer/test/convention
enforcement. Authored via an 11-agent design+adversarial-verify workflow (67 patterns, 49 determinism/API issues
caught and fixed). **Alec's 3 scope calls (✅):** Tier-1 test runner = **xUnit**; hash width = **32-bit wire /
64-bit canonical**; content numeric shape = **`Fixed` end-to-end (convert at parse)**. See *Step 7 —
Implementation Patterns* below + the `game-architecture.Step7-patterns-briefing.md` sidecar.

**Step 8 COMPLETE (2026-06-21).** Validation run as a 6-dimension fan-out with adversarial verification of every
finding (30 raw → 22 confirmed / 8 refuted; 36 agents). Overall **PASS — no determinism or correctness
blockers.** Decision-compatibility, pattern-completeness, and brownfield code-claims (11/12 confirmed exactly)
validated clean; GDD/PRD/document gaps were resolved in-pass — 13 doc-accuracy corrections, a consolidated
Decision Summary + Naming Conventions, and the *Presentation & UGC-Publish Coverage* addendum homing five
1.0-scope gaps (FR-12a/36/37/38a/49a + binary-asset ingest). Full results in *Architecture Validation* at the
end of this document. **Next:** Step 9 (Completion).

---

## Decision Summary

| Category | Decision | Version / Option | Rationale (1-line) |
|---|---|---|---|
| Engine | Godot for 1.0; defer 4.7 | 4.6.3 (patch on 4.6 line; 4.7 post-1.0) | Low-risk bugfixes; avoid days-old-minor regression/addon risk |
| Runtime | .NET desktop | .NET 8 | Stable target; ".NET 9 AOT" is post-1.0 aspiration |
| Sole NuGet dep | NakamaClient | 3.13.0 (pinned) | Only shipped dependency; keeps sim AOT-eligible |
| D1 | Effects vocabulary | Bounded Effect-Graph (Option C) | Closed, typed, statically-bounded shared ability/trigger primitives |
| D2 | Trigger DSL | Typed event/dataflow graph (Option C) | Statically checkable graph IR vs flat-list/escape-hatch |
| D3 | Definition schema & loader | Data-driven JSON + `Validated<T>` fail-closed gate + canonical-model FNV-64 hash | One trustworthy pipeline; safe-by-construction AI authoring |
| D4 | Hero persistence | Two-rail, server-attested | Init-time deterministic state the server validates |
| D5 | >2-player lockstep + matchmaking | N-aware dedicated relay (ship N≤4, 8 fast-follow) | Server-enforced agreement; localized faction bump |
| D6 | LLM provider | Hand-rolled `ILLMProvider` + `ISecretStore` | Sandboxed authoring layer; never touches the sim tick |
| Step 5 | Test runner | Tier-1 xUnit (Godot-free) + Tier-2 GdUnit4 | Fast AOT-portable determinism tests; presentation in-runtime |
| Hash width | Checksum/hash sizing | 32-bit wire / 64-bit canonical | Compact on the wire; collision-safe canonical model hash |
| Content numeric shape | Fixed-point everywhere | Fixed end-to-end, convert at parse | One quantization boundary; no in-tick float |

_Full detail in the per-decision sections below and the sidecar briefings._

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
  desktop (".NET 9 AOT" is a future aspiration, not 1.0). Sole NuGet dependency: `NakamaClient 3.13.0`.
- **Genre:** Real-time strategy + in-game creation toolset (editor-as-product).
- **Project Level:** **4 — Maximum.** Multiplayer-deterministic simulation + full data-driven
  creation suite + LLM-assisted authoring, on a **brownfield codebase at Phase 5 (→1.0)**.
  Phases 0–4 code-complete; the creation-suite editors, trigger-DSL expansion, hero save/load,
  and >2-player multiplayer are net-new.
- **Distribution:** Premium one-time purchase (no F2P/live-service); **Steam + direct DRM-free**.

### Core Systems

Status: ✅ Built · 🟡 Partial · ⬜ To-build · ❓ Unresolved (architecture must decide)

_Note: this is the pre-Step-4 snapshot; ❓ items were resolved in the Step-4 decisions (D1–D6) below._

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
| **Effects-primitive vocabulary** (shared abilities + triggers) | Sim | High | ✅ (resolved in D1) **architecture lever** | PRD addendum §C |
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
- ✅ (resolved in D1) **Effects-primitive vocabulary** shared by abilities AND triggers — deferred to this phase; breadth determines buildable genres.
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
`MainScene.cs._Ready()`. Solution `godot/godot.sln` (.NET 8). Sole NuGet dependency: `NakamaClient 3.13.0`.

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

1. **Effects-primitive vocabulary** shared by abilities + triggers (the "buildable genres" lever) — ✅ (resolved in D1)
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
> user makes each call), then batch **D4–D6** as recommend-and-confirm. **All six are now recorded (Step 4
> complete 2026-06-20).**
>
> **Cross-cutting finding (D4 + D5 + D6 converged — the highest-value output of the batch).** All three
> independently hit the **same unsound peer-agreement hashing** in the as-built code, three different ways:
> `SimChecksum` hashes only `Ore[Player1]`/`Ore[Player2]` (`SimChecksum.cs:53-54`) — Crystal, Supply, and
> factions 3+ are invisible to the 60-tick desync check; the dedicated server is a **pure relay** that
> broadcasts `StartGame` the instant both ready flags flip with **no hash compare** (`DedicatedServer.cs:171-191`),
> and the only `scenarioHash` check is client-side and skips on `hash==0` (`LobbyUi.cs:315`); AI-generated
> in-memory scenarios ship a **stale on-disk file hash** (`MainScene.cs:303`) describing content that is not
> running. Each is a **latent multiplayer-correctness bug in shipped code, independent of the new features.**
> The shared remediation — a **canonical-model start-state hash** (D3 FNV-64 over `Fixed.Raw`),
> **server-enforced agreement** (a trusted host that computes/attests, never a relay that compares
> self-reported hashes), and a **generalized `SimChecksum`** over all active factions — is a single
> prerequisite program, not three separate fixes, and must land before any D4/D5/D6 content reaches the lobby.
>
> **Settled:** D1 ✅, D2 ✅, D3 ✅, D4 ✅, D5 ✅, D6 ✅ (all 2026-06-20). Steps 5, 6, and 7 are now complete;
> Step 8 (validation) is underway. *(Live progress is tracked in the Document Status block above.)*

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
`Persistent` re-roots `Source` to its host — implemented allocation-free as a `readonly struct
EffectContext` (NOT a `ref struct` — see Step 7 N2: a `ref struct` cannot be stored in the executor's `Frame[]` work-stack) copied per child call. *Which* entities are valid is the shared `TargetFilter` flag set,
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
   gate** (`ScenarioSerializer.LoadFromFile` (`ScenarioSerializer.cs:35-40`) does zero validation today; there is no server-side load path). This is the
   equalizer that makes AI authoring safe-by-construction — a claim no escape-hatch system can make.
   Reconcile the **50-vs-64 spawn-cap discrepancy** — a hardcoded spawn cap of 50 at two runtime sites:
   `Math.Min(a.Count, 50)` in `ScenarioDirector.ExecuteActions` (`ScenarioDirector.cs:312`) and
   `Math.Clamp(a.Count, 1, MAX_SPAWN_COUNT)` in `LLMService` validation (`LLMService.cs:309-310`, const at
   `:73`) — plus matching documentation/prompt text — all conflicting with D1's authoritative `Spawn≤64`;
   collapse to one named constant during the D0 audit.
10. **Trigger evaluation gets a total deterministic order** `(Priority desc, then declaration-index asc)`
    via an explicit comparator — replaces the as-built **unstable `Array.Sort`** (`ScenarioDirector.cs:192`);
    dense-index SoA stores replace the **Dictionary-backed sim state** spanning BOTH `_timers` (`:33`,
    enumerated `:149`) AND `_variables` (`Dictionary<string,int>` at `:34`) — both folded into `SimChecksum`; simultaneous timer
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
  spawn cap — a hardcoded 50 at two runtime sites, `Math.Min(a.Count, 50)` in `ScenarioDirector.ExecuteActions`
  (`ScenarioDirector.cs:312`) and `Math.Clamp(a.Count, 1, MAX_SPAWN_COUNT)` in `LLMService` validation
  (`LLMService.cs:309-310`, const at `:73`), plus matching documentation/prompt text, all vs D1 `≤64` — and
  runtime float→Fixed (`add_resources`, `create_timer`) against D1's load-time discipline. *Baseline tag; no
  behavior change.*
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
  the Dictionary-backed sim state: `_timers` (`:33`, enumerated `:149`) AND `_variables`
  (`Dictionary<string,int>` at `:34`), both moving to dense SoA folded into `SimChecksum`. *(Both verified.)*
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

---

### D4 — Hero Persistence Model ✅ (decided 2026-06-20)

> Full options analysis + 4-lens adversarial verification (determinism · static-validation/anti-tamper ·
> brownfield-fit · scope/solo-cost) lives in the working sidecar **`game-architecture.D4-briefing.md`**.
> This record is the canonical decision. Recommend-and-confirm; **Alec's scope call: persistence allowed in
> any scenario including competitive PvP, and the bespoke engine-side normalization-mode enum is CUT** —
> fairness, if wanted, is expressed via D1 Modifiers / D2 graph at match-init.

**Decision — Two-rail persistence, one validation boundary (Option C).** One `PlayerProfile` model, one
`ContentLoader` + `Validate(profile, manifest)` boundary, and one canonical-model **`startStateHash`**, all
designed in **M2** — with an offline `LocalProfileSource` (explicitly *untrusted*, single-player only) that
gives an immediate playable hero-progression loop. The **online rail (M5) is an additive provider drop-in,
not a rewrite**: a server-stored, server-validated, **server-attested** profile becomes the sole source of
truth. The one hard correction from the adversarial review: *server-authoritative* must mean a **trusted
host that COMPUTES/ATTESTS the start-state hash**, never a relay that merely compares self-reported hashes.

**Why C, not signed-save-codes-only or server-only-from-the-start.**
- **Signed client save-codes** (WC3-faithful, lightest) are rejected for the *online* path: any value the
  client can read and apply, it can forge; a signature proves *who signed*, not *that the value is
  legitimate*, and is clonable/replayable across accounts without server custody of the canonical value
  (WC3's classic -load exploit). They remain acceptable only for the offline-untrusted local rail.
- **Server-only-from-the-start** would block the solo dev's M2 single-player hero loop on the entire M5
  online stack. Two-rail lets M2 ship a real feature while keeping the M5 online path a drop-in.

**Settled sub-decisions (the confirmed calls):**
1. **Online anti-tamper = server-stored profiles as the sole online source of truth** (Nakama storage
   object: Owner-Read, No-Client-Write, written only via a validating server RPC). Signed client save-codes
   rejected online; the local `user://` profile is explicitly untrusted and structurally walled off from any
   MP hash. *(D4-A)*
2. **Init-time agreement = server-authoritative delivery + attestation, fail-closed.** A trusted host
   computes/attests the canonical `startStateHash` (or signs the validated profile set it delivers); each
   client verifies its locally-recomputed hash *equals* the attested hash. The dedicated server GATES
   `StartGame` on server-side hash agreement and refuses to broadcast on any mismatch or any peer reporting a
   zero/missing hash. The LAN peer-broadcast fallback is dropped (it violates the no-P2P non-goal): route LAN
   through a local listen-server acting as authority, or carve LAN persisted-profiles out of 1.0.
   *(D4-B — corrects the as-built pure-relay `DedicatedServer.cs:171-191` + client-only check `LobbyUi.cs:315`.)*
3. **A NEW canonical-model `startStateHash`** (D3 FNV-64 over `Fixed.Raw`, algo-2, `_editor`/`_ext`
   excluded) over the full applied initial sim state *including every player's applied profile + the
   manifest*, computed **pre-apply over the canonical model** (never over post-`ApplyScenario` state that
   passed through `Fixed.FromFloat`), server-attested, verified in the handshake beside `scenarioHash` +
   `rulesetHash`. Do **not** extend `ComputeFileHash` (raw bytes) or `SimChecksum` (live-state, wrong scope).
   The `hash==0`-means-skip tolerance (`LobbyUi.cs:315`) becomes a **hard reject**. *(D4-C)*
4. **Hero sim state = a separate sparse `HeroStore` SoA** keyed by a **stable cross-match hero identity**
   (NOT the recycled `EntityWorld` free-list id), Fixed/int only, applied via `Fixed.FromRaw`. It folds into
   **both** `SimChecksum` and `startStateHash` — which **requires generalizing `SimChecksum` from its
   P1/P2-only coverage (`SimChecksum.cs:53-54`) to all active factions first**, or P3/P4 hero state is
   silently dropped from the desync hash (fail-open). *(D4-D — shared prerequisite with D5-SD-7.)*
5. **Identity binding = Nakama account/userId; real-account (email) auth is engine-enforced for online
   persistence.** If a manifest enables online persisted profiles, the lobby path hard-rejects device-auth
   sessions before any profile loads. Global email-auth rule for online persistence in 1.0 (defer the
   per-scenario conditional to v2). Device-auth stays fine for casual/LAN (local-untrusted profiles only).
   `NakamaKey='defaultkey'` (`NakamaService.cs:38`) is a deployment secret — not committed. *(D4-E)*
6. **Manifest granularity = fine-grained declared bounds** (which categories carry + their bounds:
   max_level, currency_cap, allowed item-ids + per-stack caps, skill-point cap). **The manifest itself must
   pass a `Validate(manifest)` engine-ceiling gate** (absolute caps; item-id existence) *before* it can be
   the validation oracle — for online/ranked the effective bound is `min(declared, engine-ceiling)`. Both
   manifest and profile traverse the identical D3 `ContentLoader`+`Validate` choke point with no bypass,
   including the AI-generated path. *(D4-F — closes "the attacker controls the validation oracle.")*
7. **Persistence allowed in any scenario incl. competitive PvP** via the decision-#15 creator toggle +
   manifest bounds; **the bespoke engine-side normalization-mode enum is CUT.** Any hero
   normalization/fairness is expressed through **D1 Modifiers / D2 graph logic at match-init**, never an
   engine capping code path (which would be a creator-unreachable balance path — data-driven-pillar
   violation — and would duplicate D1/D2). *(D4-G — Alec's scope call.)*

**Migration sequence (M2 local rail → M5 online rail; full detail in the sidecar).** **M2:** `PlayerProfile`
+ `PersistenceManifest` DTOs through the D3 loader; sparse `HeroStore`; manifest-authoring UI; init-time
apply; `startStateHash`; the hero-picker Save/Load UI (FR-7d/e) as a reusable platform component. **M5:**
Nakama storage source + validating write-RPC; server attestation + `StartGame` gate; email-auth enforcement.
Each step golden-checksum-gated.

**Prerequisites surfaced (carry forward):**
- **D1 must be frozen first** — `HeroStore` is a subset-serializer of D1 sim state (Modifier SoA,
  Energy/Mana, items/skills-as-Modifiers); a post-M2 D1 shift re-touches the persisted subset + both rails.
- **D3's full loader + the currently-MISSING `Validate(model)` gate at `ApplyScenario`** (`MainScene.cs:499-558`
  has none; float `StartOre` trusted verbatim at `:518`) must exist.
- **Generalize `SimChecksum` to all active factions before the `HeroStore` fold** (`SimChecksum.cs:53-54`).
- **Dedicated server must grow from pure relay → trusted host that gates `StartGame` on hash agreement**
  (`DedicatedServer.cs:171-191`) — a large net-new D5 component, not "add a couple Nakama RPCs."
- **The 5-byte single-hash Ready packet** (`NetworkCommand.cs:213-220`) is redesigned once to a fixed-length
  multi-hash structure (scenario + ruleset + startState) with reject-on-length-mismatch — **shared-owned with
  D2's `rulesetHash` change; sequence after it.**

**Hand-offs:** → **D5** owns the server-attestation/`StartGame`-gate + multi-hash Ready packet + faction
generalization that D4's online rail rides on. → **D3** already owns *how* profile/manifest serialize + hash.
→ **Implementation** (M2 local rail, M5 online rail).

**Residual risks:** persistence-in-PvP balance is now the creator's responsibility (no engine guardrail —
deliberate, per D4-G); the local rail must be provably unable to enter any MP hash; the start-state-hash walk
cost on max-caps profiles must fit the lobby-handshake budget.

---

### D5 — >2-Player Lockstep + Matchmaking ✅ (decided 2026-06-20)

> Full options analysis + 4-lens adversarial verification in **`game-architecture.D5-briefing.md`**. This
> record is canonical. **Alec's scope calls: ship/verify N≤4 in 1.0** (8 as a constant-bump fast-follow);
> **defer the NativeAOT project-split extraction** (keep the D3 AOT-analyzer CI gate now; the
> `RelayCore`/`ITransport` extraction + `PublishAot` build are post-1.0).

**Decision — N-aware dedicated relay (Option A\*).** The server becomes the single fan-in/materialization
point: it collects all N single-faction `TickCommands`, **re-stamps faction from the authoritative slot**,
and broadcasts ONE merged multi-faction `TickCommandsMerged` packet; a **server-side checksum collector**
does majority-vote desync attribution. Build the N-shaped architecture once so **8 players is later a
constant-bump + `Faction`-enum extension, not a rewrite** — but make the **1.0 verification gate N≤4** (the
codebase is already 4-faction-shaped: `Faction.Player4`, `ServerTransport MAX_SLOTS=4`) and defer the 8-peer
soak + parties-lobby UI + full `PublishAot` Linux build. The server-side checksum collector, the
merged-packet faction re-stamp, and the canonical intra-tick application order are **hard, non-deferrable**.

**Why A\*, not client-host or a thin relay tweak.**
- **Client-host-authoritative** conflicts with the server-authoritative mandate and reintroduces trust +
  host-migration problems.
- **"Just add a slot byte to the existing relay"** understates the work: the as-built server is a **pure
  relay with no game knowledge** — no RTT tracking, no hash compare, opaque checksum forwarding. N-player
  determinism + diagnosability require the server to become a **stateful authority**. That inversion *is* the
  decision.

**Settled sub-decisions (load-bearing subset; full 13 in the sidecar):**
1. **Merged multi-faction tick packet**, server-built/broadcast on a NEW distinct type (`TickCommandsMerged`,
   server→client only); clients send only their own single-faction `TickCommands` (client→server only). Server
   **rejects** a merged-shape packet from a client; re-stamps each sub-bundle's faction = `SLOT_FACTION[sourceSlot]`
   (never trusts the client byte); drops on faction mismatch/over-count; sub-bundles **sorted ascending by
   faction id** (analogue of the ascending-entity-ID mandate). *(SD-1 — generalizes the single-peer gate
   `LockstepManager.cs:303`.)*
2. **One tick slot gated on the single merged-packet arrival** (no N-stream ANDing); fixed application order:
   for each faction ascending by id, apply unit orders in wire order, **then** that faction's DSL events.
   Spectator path rewritten to demux the merged packet (`LockstepManager.cs:408-424`). *(SD-2.)*
3. **Ready-COUNT server state machine** — replace `_ready[0]&&_ready[1]` (`DedicatedServer.cs:32,179`) with
   `connectedPlayers==expected && readyCount==expected`, `expected` supplied authoritatively by the loaded
   scenario/lobby. *(SD-3.)*
4. **Server-dictated adaptive input delay (net-new server work):** server-side Ping/Pong + per-slot RTT;
   server computes max-over-N target delay, broadcasts ONE authoritative `DelayProposal` reliably; all N ACK
   `applyAt`, commit only when all ACK before `applyAt` (else re-broadcast/abort, fail-closed). Clients accept
   `DelayProposal` only from the server channel and **re-clamp to [2,12]** on receipt (fixes the unclamped
   `Math.Max` `LockstepManager.cs:495`). The server has **no RTT knowledge today** (`DedicatedServer.cs:133-166`).
   *(SD-4.)*
5. **Server-side checksum collector + majority-vote attribution:** server parses Checksum packets (today
   relayed opaquely `DedicatedServer.cs:148-156`), buffers one slot-tagged hash/slot/60-tick window, declares
   strict-majority canonical, broadcasts a `DesyncAlert` naming the diverged peer(s); on no strict majority →
   "global desync, no canonical" and HALT (fail-closed). Slot is transport-authoritative (`ServerTransport.cs:170`),
   never client-supplied. *(SD-5 — delivers FR-39 diagnosability.)*
6. **Single up-to-8 player model:** extend `Faction` enum to `Player8`, `Faction==player` for 1.0 (D3
   PerPlayer 0..7 → Faction 1..8); raise `FACTION_COUNT`; seed N active factions; audit every `(int)Faction`
   index site + 2-player loop. Convert the `ScenarioDirector` threshold loop (`:165-172`) from float
   (`ore.ToFloat()`/`ToString("F2")`) to **Fixed.Raw integer compares** before fanning out to N. *(SD-6 —
   decoupling slot from faction deferred until a teams feature exists.)*
7. **Fix `SimChecksum` coverage now, broadly:** hash Ore + Crystal + SupplyUsed + SupplyCap (+ any D1
   Energy/Mana/Modifier SoA) for ALL active factions in ascending order; bump `checksum_algo_version` once;
   add a guard test that fails if a per-faction array is added without checksum coverage. Today only
   `Ore[P1]`/`Ore[P2]` are hashed (`SimChecksum.cs:53-54`) — a latent desync hole defeating FR-39/40
   independent of D5. *(SD-7 — shared prerequisite with D4-D.)*
8. **Ship ceiling = 4 now, 8 fast-follow** *(Alec's scope call)* — architecture identical; only verification
   breadth + parties/soak differ, so deferring the shipped ceiling carries no architectural regret. *(SD-8.)*
9. **Pre-match parties:** parameterize the Nakama matchmaker (minCount/maxCount/countMultiple,
   `NakamaService.cs:132`) + `AddMatchmakerParty`/partyId now; **slot/faction assignment stays server-side**
   via the existing Hello mechanism (`DedicatedServer.cs:93-95`), not Nakama's lexicographic pick. The parties
   **lobby UI** is the deferrable slice. Confirm matchmade-server routing (single static `GameServerIp/Port`
   `NakamaService.cs:194` vs allocated instance). *(SD-9.)*
10. **Disconnect = deterministic freeze-and-continue:** server broadcasts "faction K idle at
    `applyTick = currentServerTick + delay + margin`" (reliable, ACK-gated) so all peers pre-seed empty
    command sets for K from the identical tick; K's passive sim continues; never indefinite pause.
    Drop-to-deterministic-AI is a D4/D1-coupled fast-follow. *(SD-10.)*
11. **AOT project-split = defer extraction; CI gate now** *(Alec's call)* — `EnableDynamicLoading=true`
    (`godot.csproj:5`) confirms AOT needs a separate Godot-SDK-free server `.csproj` sharing the pure-sim
    source. FR-39/40 are satisfied by the existing headless JIT server; NativeAOT is a hosting-cost
    optimization with no player-facing 1.0 requirement. Keep D3's AOT-analyzer CI gate + Godot-free discipline
    now; defer the `RelayCore`/`ITransport` extraction + `PublishAot` build to late-M5/post-1.0 (extracting now
    is speculative work touching the FR-39-validated relay). *(SD-11.)*
12. **Replay v2 for N players:** bump `ReplayRecorder.VERSION`→2 (`:25`); header carries player roster +
    active faction count + `rulesetHash` (`:106-114`); **tagged record body** (orderKind discriminator) so unit
    orders + D2 `DslEventCommand`s share one self-describing envelope **mirroring the SD-1 merged packet**.
    **Co-design with D2 before freezing the wire format.** *(SD-12 — converges with D2-D9s / D3.9.)*
13. **Tick-0 start-state agreement:** authoritative initial delay in the `StartGame` packet (server-dictated;
    generalize `SeedInitialTicks` `LockstepManager.cs:579-590`); compare a single start-state hash
    {roster + faction count + initial delay + rulesetHash + scenarioHash (+ D4 `startStateHash`)} across all N
    before any tick — fail-closed. Land the inbound D3 gates the lobby assumes: server-side `PROTOCOL_VERSION`
    reject (`DedicatedServer.cs:135-137` ignores client Hello) + `rulesetHash` compared at the lobby. *(SD-13.)*

**Prerequisites surfaced:** FR-39 2-player LAN green first (M1 golden baseline); the SD-7 `SimChecksum` fix;
a multi-peer (N=3,4) desync harness extended with **adversarial inputs** (faction-spoof sub-bundle,
over-count bundle, merged-from-client, forged `DelayProposal`, forged checksum slot, mid-match drop) asserting
the server fails closed; **D2 envelope co-design** before freezing the packet/replay body; the inbound D3
server-side gates; pin the authoritative expected-player-count + initial-delay sources; a float/locale-leak
audit of every `Faction`→`Player8` site (`ScenarioDirector.cs:168/170`).

**Hand-offs:** → **D4** depends on the server-attestation/`StartGame`-gate + multi-hash Ready built here.
→ **D2/D3** co-own the tagged tick/replay envelope + `rulesetHash`/`PROTOCOL_VERSION` gates. → **Step 5/6**
inherit the (deferred) AOT extraction as a post-1.0 engine-structure item.

**Residual risks:** the server-authority inversion is a real net-new build (not a relay tweak); 8-peer
slowest-peer stalls remain unproven (deliberately deferred); the matchmade-server allocation story is
unconfirmed (SD-9); freeze-and-continue grief/abuse edges.

---

### D6 — LLM Provider Abstraction ✅ (decided 2026-06-20)

> Full options analysis + 4-lens adversarial verification in **`game-architecture.D6-briefing.md`**. This
> record is canonical. **Alec's call: key-at-rest = plaintext floor behind an `ISecretStore` seam**
> (DPAPI/libsecret as drop-in fast-follows).

**Decision — Hand-rolled `ILLMProvider` + adapters (Option A, revised).** A Godot-free `ILLMProvider`
abstraction with a normalized `GenerateAsync(NormalizedRequest, ct) → NormalizedResult` over three adapters
(Anthropic Messages lift, Ollama `/api/chat` migration, new OpenRouter OpenAI-compatible `/chat/completions`);
provider/model/key config moved into the persisted **settings** system; the raw key in a **gitignored
`user://` file behind a 1-method `ISecretStore` seam**. No NuGet/vendor-SDK dependency (keeps the D3
AOT-analyzer clean; the abstraction is ~15 lines over the existing raw-`HttpClient` idiom). **Authoring-layer
only — zero sim coupling** (NFR-4). The decisive coupling: **AI output is validated by the SAME D3
authoritative gate as hand-authored content, with float→Fixed quantization before the canonical-model hash** —
the adversarial review proved AI-generated scenarios currently ship a **stale, byte-domain peer-agreement hash
that silently desyncs MP**.

**Why A, not Microsoft.Extensions.AI/official-SDK or OpenAI-compatible-only.**
- An **SDK** injects NuGet deps + AOT-analyzer friction into the authoring layer for an abstraction
  reproducible in a few lines over the existing raw-`HttpClient` + `System.Text.Json` idiom (`LLMService.cs:53,77`).
- **OpenAI-compatible-only** would force an Anthropic→OpenAI shim and lose Anthropic-native features; thin
  per-provider adapters are cleaner and testable.

**Settled sub-decisions (load-bearing subset; full 10 in the sidecar):**
1. **Hand-rolled `ILLMProvider`**, `IChatClient`-shaped in spirit (mechanical later SDK swap), no NuGet v1. *(D6-1.)*
2. **Blocking v1** (`stream=false`); a streaming overload is left purely additive; **raise `TIMEOUT_MS`**
   (`LLMService.cs:69`) after measuring a representative Opus-4.8 7-pass map-gen latency. *(D6-2 / D6-9 latency.)*
3. **Key-at-rest = plaintext floor behind `ISecretStore`** *(Alec's call)* — raw key in a gitignored
   `user://secrets/llm.key`; DPAPI(Win)/libsecret(Linux) drop in behind the same seam later. Satisfies FR-29
   (never hardcoded/committed/synced); encryption-at-rest is neither determinism- nor spec-load-bearing. *(D6-3.)*
4. **Key in a separate `user://` file, never in `settings.json`**; `SettingsData` stores only
   `AiProvider`/`AiModel`/`AiBaseUrl` + a "key present" flag. Enforced by a **test** asserting the key string
   appears in no `settings.json`, no `res://` write (`MapGeneratorPanel.cs:246`, `ContentPackager`), no
   `main.tscn`. *(D6-4.)*
5. **Selected provider authoritative** (replace the implicit Claude→Ollama auto-fallback
   `LLMService.cs:118-131`); ship as a discrete commit after the abstraction is smoke-tested; **default the
   optional "fall back to local if cloud unreachable" toggle ON until the four-state FR-34 UI ships**, then OFF
   (resolves the D6-5/D6-8 tension without an FR-34 regression for a no-key creator). *(D6-5.)*
6. **Curated static provider/model lists in data-driven JSON + free-text override.** Claude trio:
   `claude-opus-4-8` (premium), `claude-sonnet-4-6` (mid default, preserves `LLMService.cs:62`),
   `claude-haiku-4-5-20251001` (cheap). **The curated entry PINS the provider host.** *(D6-6.)*
7. **Validation/hash pipeline:** provider returns raw text → StripMarkdown → deserialize with the D3 canonical
   options/context → **quantize all floats to Fixed** (canonicalize) → single shared validator → compute the
   **canonical-model hash** (FNV-64 over `Fixed.Raw`) as the peer-comparable artifact. This is **TWO swap
   points** — the deserialize *target type* (legacy `TriggerDefinition`/`ScenarioData` → D2 `NodeBase` graph
   IR) AND the validator. **Prefer sequencing after D3**; if interim, gate with a golden-checksum equivalence
   harness. *(D6-7.)*
8. **FR-34 = four distinct states** ("no provider selected" / "no key" / "unreachable/failed" / "generated
   content failed validation"), suite fully manual-usable in all four, + a "Test connection" button. The
   actionable signal today is the completion-error string (`LLMService.cs:128`), not the dead `_llm==null`
   branch (the service is constructed unconditionally `MainScene.cs:1858`) — two states are unreachable until
   the `[Export]` rip-out. *(D6-8.)*
9. **AI scenarios commit/quantize/hash BEFORE the lockstep Ready packet; a committed scenario is immutable
   thereafter.** The in-memory `_pendingGeneratedScenario` path (`MainScene.cs:137,466,1959`) must
   compute/exchange the **canonical-model hash**, not the stale on-disk `ScenarioPath` hash (`:303-304`).
   Regeneration invalidates + re-sends the hash before Ready. *(D6-9 — closes a verified fail-closed MP
   correctness hole: today two peers load different AI maps, both keep an identical stale hash, agreement
   passes, sim desyncs at tick 0.)*
10. **Provider endpoints/config are untrusted input:** PIN cloud hosts in code via the curated registry; allow
    a custom base URL only for the explicitly-local Ollama provider, validated loopback/private before any
    keyed request; validate `AiProvider`/`AiModel`/`AiBaseUrl` on load, fail-closed to "no provider selected"
    on anything unrecognized; **cap buffered response bytes** before `JsonDocument.Parse` (`LLMService.cs:190,231`
    read the full body unbounded). Closes a one-click key-exfiltration primitive + an oversized-payload surface.
    *(D6-10.)*

**Prerequisites surfaced (binding):** move the cross-peer scenario-agreement hash **off raw bytes**
(`ScenarioSerializer.ComputeFileHash:59-80`, used `MainScene.cs:303-304`) **onto the canonical
Fixed-quantized model**; a float→Fixed **quantization step** (`Fixed.FromFloat FixedPoint.cs:27` —
truncating, no NaN/Inf/overflow guard) in the AI ingest contract with finiteness/magnitude clamps on every
scalar reaching it; the D3 **`Validate(model)` gate at `ApplyScenario`** covering the saved-file round-trip
(`MapGeneratorPanel.cs:246` → `LoadFromFile`, zero validation today) — **prefer sequencing after D3**; the AI
prompt schema (`BuildSystemPrompt LLMService.cs:334-408`, `BuildMapSystemPrompt:600-664`) + deserialize target
(`:264/:507`) are bound to legacy `TriggerDefinition`/`ScenarioData` and must be regenerated against D2's
`NodeBase` IR (isolate behind one "generation contract" to bound the D2 migration); verify `user://` is
outside VCS (don't assume) and remove the `[Export] AnthropicApiKey` path (`MainScene.cs:206/1858/1917`).

**Hand-offs:** → **D3** owns the canonical gate + hash + Fixed converter D6 routes AI output through.
→ **D2** owns the `NodeBase` IR the prompt schema + deserialize target must migrate to. → **M4** (provider
config + OpenRouter) per the milestone plan; the validation reconciliation lands with/after D3.

**Residual risks:** the "two swap points" mean an interim (pre-D3) AI gate needs a golden-checksum equivalence
harness or it diverges from the future `ContentLoader`; the latency of a real Opus-4.8 7-pass run vs the
timeout is unmeasured; the secret-exclusion invariant is only as good as its test.

---

## Cross-Cutting Concerns (Step 5)

> Step 5 records the concerns that span every system rather than belonging to one. **Testing/quality is the
> headline** — the repo has zero tests today (the M1 "foundation trust" gate, FR-44/47) and the same
> infrastructure surfaced as a prerequisite across all of D1–D6. This section consolidates those scattered
> prerequisites into one regime and fills the gaps. Synthesized 2026-06-20 (the parallel research workflow
> was interrupted by a usage cap; authored directly from the Step 1–4 grounding + a manual determinism /
> scope / completeness review). Full per-area depth in **`game-architecture.Step5-cross-cutting-briefing.md`**.
> **Alec's confirmed calls are tagged ✅ inline.** `stepsCompleted` → `[1,2,3,4,5]`.

### The foundation premise
M1 ("foundation trust") must be GREEN before the D1 strangler can start: there are **zero tests today**, and
D1 step 1 is literally "stand up the repo's first headless deterministic tests." Everything in D1–D6 is gated
on a golden-checksum regression harness that does not yet exist. So the testing/determinism/CI architecture
below is not a side concern — it is the **first thing built**, and the regression guard that makes the entire
migration program safe.

### 1. Testing architecture (headline) — two-tier ✅
The simulation is **pure Godot-free C#** (CLAUDE.md mandate, verified — no `using Godot` in
`src/Core`/`Combat`/`Economy`/`Navigation`). That single fact drives a two-tier strategy:
- **Tier 1 — a new Godot-free test project** (e.g. `ProjectChimera.Sim.Tests`, plain .NET test runner): the
  **golden-checksum replay regression harness** (run a fixed scenario N ticks through `SimulationLoop`, record
  the `SimChecksum` sequence, assert byte-identical on replay — reusing the `ReplayRecorder`/`ReplayPlayer`
  `.chmr` path), Fixed-math / `SimRng` / ascending-ID determinism tests, the D1/D2/D3 **negative-validation
  tests** (cycle / unknown-type / cosmetic-touches-sim / malformed-corpus all rejected), the
  **`SimChecksum`-coverage guard test**, and the D6 secret-exclusion invariant. Runs in seconds, no engine
  boot. **This same Godot-free project is the shared-source home the deferred D5 AOT dedicated server compiles
  from** — building it now does double duty.
- **Tier 2 — GdUnit4 in `godot/tests/`** (honors FR-44) for presentation / Godot-API / integration tests
  only (editor panels, bridges, input).
- **Why two-tier, not GdUnit4-only:** GdUnit4 boots the engine for every run; routing the determinism/rules
  suite through it would make the check loop slow and couldn't be reused for the server. FR-44's literal
  "GdUnit4 in `godot/tests/`" predates the Godot-free/AOT insight — **recommend updating FR-44's wording** to
  the two-tier reality (GdUnit4 remains the integration tier).

### 2. Determinism enforcement & cross-platform — mechanical, not hope
Determinism is the spine (NFR-4) but is enforced only by discipline today and is **already violated in spots**:
`StressTest.cs` seeds a wall-clock `RandomNumberGenerator().Randomize()`; `SimChecksum.cs:53-54` covers only
`Ore[Player1/2]`; `ScenarioDirector` runs an in-tick `Fixed→float→"F2"→TryParse` path (A17, :168/170/252) and
uses an unstable `Array.Sort` (:192) + `Dictionary` enumeration (:149).
- **Static guard:** a banned-API analyzer fails the build if sim-layer code uses `using Godot`, `float`
  gameplay math, `System.Random`/Godot RNG, wall-clock, or nondeterministic enumeration (composes with D3's
  AOT analyzer over the same source).
- **Cross-platform gate ✅:** Windows clients and the **Linux** dedicated server must produce **byte-identical**
  Fixed checksums. The golden-checksum harness runs on **both Windows and Linux** and the two checksum
  sequences are diffed — the only real proof Fixed-point holds and no float/culture leaked. *(How it runs: §6.)*
- Generalized `SimChecksum` (all active factions + Crystal/Supply + new D1/D2/D4 SoA stores) + the guard test;
  `InvariantCulture` pinned process-wide (D3). Consolidates every D1–D6 determinism prerequisite.

### 3. Observability & desync diagnosis
FR-39 requires N-player desync to be diagnosable; today there is only `GD.Print` + an opaque relay.
- A **deterministic-safe logging seam** (sim logs through an interface, never `GD.Print`, nothing that affects
  state/order; works in the Godot-free test/server project).
- **Desync bisection** on D5's server-side checksum collector + majority-vote `DesyncAlert`: add **per-system
  sub-checksums** so a divergence narrows to a system/tick, and **replay-diff** between two peers' `.chmr`
  recordings to find the first diverging tick.
- The `.chmr` replay is the primary **record-and-reproduce** artifact (also the opt-in crash/desync report
  payload, §Telemetry).

### 4. Error handling — two worlds, one policy
- **Sim = deterministic, fail-closed, never-throw-mid-tick:** impossible states fail identically on all peers
  (Spawn fails closed at the 4096 cap per D1; a confirmed desync HALTs the match per D5). No exception escapes
  a tick.
- **Authoring/presentation = catch-and-degrade-gracefully:** D3's fail-closed `Validate(model)` gate rejects
  malformed/tampered content before tick 0 with **located** errors; FR-34's four-state AI degradation keeps the
  suite manual-usable; UGC/content-load failures surface a clear message, never a crash.

### 5. Performance & profiling
NFR-5/FR-46: 500–2000 units @ 60 FPS render / 30 Hz sim on representative shipped **and community** scenarios;
the 4096 cap stays an explicit counter-metric.
- Evolve `StressTest.cs` (a non-deterministic Godot demo) into a repeatable benchmark.
- **CI-gateable:** headless **sim throughput** (Godot-free, OS-portable) + **zero-allocation-in-tick asserts**
  (`GC.GetAllocatedBytesForCurrentThread`, enforcing D1's no-GC-in-tick). **Not CI-gateable:** render FPS —
  stays a periodic in-engine measurement via `godot_profiler`.
- Representative **community-scenario fixtures** (a 2000-unit TD stresses differently than 500 brawlers).

### 6. Quality gates & the check-runner ✅
Greenfield: no CI today; the repo auto-commits to `master` hourly (`[AutoSave]`).
- **The checks get built in M1** (the §1 Tier-1 + Tier-2 suites, the golden-checksum harness, the analyzers).
- **The runner is an AI-orchestrated workflow** ✅ *(Alec's call)* — not an always-on cloud service: triggered
  or scheduled, it runs the checks, the saved-battle replays, and the **Windows-vs-Linux comparison**, and
  reports/diagnoses failures.
- **Day-to-day:** the fast Tier-1 rule-checks run locally on the Windows PC in seconds. **When it matters**
  (before a release, or on a schedule): the AI workflow runs the cross-platform comparison using a **Linux
  environment** it can reach.
- This keeps the hourly `[AutoSave]` loop **unblocked** (gates are advisory, not a hard pre-commit block) while
  still catching the #1 multiplayer risk.
- **Prerequisite surfaced (implementation-time):** a **Linux environment** for the cross-platform check.
  **Alec already has WSL/Ubuntu installed** (from his bmad automator on another project) and is comfortable in
  that terminal, so the OS hurdle is gone — M1 work is small: **install .NET inside the existing Ubuntu WSL**,
  then run the golden-checksum harness there and diff against the Windows run. The same WSL Ubuntu **doubles as
  the host for the Linux dedicated-server build**. *(See memory `linux-env-for-crossplatform-check`.)*
- **Versioning ownership:** the check-workflow also verifies the D3 version stamps move together —
  `CurrentGameVersion` / `schema_version` / `checksum_algo_version` / `PROTOCOL_VERSION` + the
  `min_game_version` auto-stamp.

### 7. Accessibility baseline ✅ + languages ✅
PRD §4.11 / decision-log #17 baseline: **input remap** (already clean — Input→command-intents is pure
presentation), **colorblind-safe** faction palette + mode (the current blue/red is a classic confusion pair),
**UI scale** (Theme / content-scale), **subtitles** for built-in audio — all in `SettingsData` /
`SettingsManager` / `SettingsPanel`, designed with the UX pass.
- **Languages ✅ *(Alec's call)*:** English-first for 1.0 with a **translatable seam for the game's own UI**
  (menus translatable later without a rebuild); **player-made (UGC) content is NOT translated in 1.0**.

### 8. Concerns the completeness pass adds
- **Configuration/settings management:** `SettingsData` is growing (AI provider/model + "key present" flag from
  D6; accessibility from §7) — give it the **same schema-versioning discipline as content** (a settings
  `schema_version` + migration), or an old settings file breaks a new build.
- **UGC / content safety:** the safety boundary is **structural, not a scanner** — D1/D2's no-escape-hatch (no
  code in content) + D3's fail-closed validation gate **is** the sandbox; mod.io downloads are gated by content
  hashing. No creator content ever executes as code.
- **Migration & replay-compat testing:** D3's migration registry needs **round-trip tests** (vN→vN+1 preserves
  meaning) and the replay format needs **v1→v2 reject** + cross-version tests (D5-SD-12 / D3.9).
- **Dependency hygiene:** the sole shipped NuGet dep stays `NakamaClient` (pinned 3.13.0); test-only dependencies live **only in
  the Godot-free test project** (never the Godot build), keeping the sim AOT-eligible.

### Telemetry ✅
**No analytics in 1.0** *(Alec's call)*. Dev-only diagnostics (logs, replay capture) + an **opt-in
crash/desync report** the player chooses to send (the `.chmr` replay + checksum log). Fits the premium,
no-live-service model.

### M1 foundation build sequence (build in this order — the D1 strangler depends on it)
1. The Godot-free **test project** + the **golden-checksum replay harness** (D1 step 1).
2. **Generalize `SimChecksum`** (all factions + Crystal/Supply) + the **coverage guard test** (D4-D / D5-SD-7).
3. The **determinism banned-API analyzer** (+ fold in D3's AOT analyzer).
4. The **AI check-workflow runner** + the **Windows↔Linux cross-platform comparison** (needs the Linux env —
   WSL2, deferred-setup).
5. **Negative-validation tests** (D1/D2/D3) + the perf benchmark + zero-alloc asserts.
*(Then the D1 → D2 → D3 strangler steps proceed, each gated by this harness.)*

### Consolidated prerequisites (this step now owns)
Golden-checksum harness · generalized `SimChecksum` + guard · `SimRng` checksummed · banned-API + AOT analyzers ·
cross-platform determinism comparison (Linux env) · negative/validation test corpus · multi-peer (N=3,4)
adversarial desync harness (D5) · canonical-hash determinism tests (D3.1) · zero-alloc-in-tick asserts ·
migration round-trip + replay-compat tests · secret-exclusion test · A17 + unstable-sort/dict-enum fixes gated
by negative tests.

### Hand-offs
→ **M1 implementation** builds this foundation first (the later gds-test-framework / gds-test-automate /
gds-performance-test skills implement what this section architects). → **Step 6** (`MainScene` decomposition)
inherits the test seams (the 2,200-LOC composition root must become testable as it is split). → The **deferred
D5 AOT server** shares the Godot-free test project's source.

### Residual risks / watch-items
- The cross-platform comparison is only as good as having a real Linux environment wired into the runner; until
  then "Win/Linux identical" is asserted, not proven (and FR-39 is the #1 ship risk).
- An AI-orchestrated runner is non-standard; it must be **reliable/unattended enough** to actually run before
  releases (a check that's skipped is no check) — budget a minimal scheduled trigger.
- Zero-alloc-in-tick asserts are brittle to refactors — treat a regression as a real finding, not noise.
- Accessibility is a *baseline*, not full WCAG — stated plainly.

---

## Step 6 — Project Structure + `MainScene` Decomposition

> Full design — three candidate decomposition strategies, the adversarial scoreboard, and the reviewer-found
> flaws that were eliminated — lives in the working sidecar **`game-architecture.Step6-structure-briefing.md`**.
> This record is the canonical decision. Produced via a 16-agent design+adversarial-verify workflow (3 diverse
> designs → 9 constraint/breakage reviews → synthesis); the winning strategy scored **93/100** with the two
> runner-ups' best ideas grafted in and every reviewer-found fatal flaw eliminated. **Recommend-and-confirm;
> Alec's four scope calls are tagged ✅.** Decided 2026-06-20.

#### Scope calls confirmed by Alec (2026-06-20)

1. **Validation-gate flip = warn-first on master, fail-closed on a release branch** after a corpus run proves
   every shipped scenario passes. ✅ The gate is structurally present (log-only) from migration Step 4; **C2 is
   reported *enforcing* only post-flip**, never at land-time. Keeps the hourly `[AutoSave]`→master loop from ever
   bricking a previously-tolerated map.
2. **The Godot-free sim-core (`SimulationHost` + `ScenarioApplier` + `ScenarioValidator`) and `ServerBootstrap`
   are M1-blocking — the spine.** ✅ They must land before any D3/D4/D5/D6 content reaches the lobby; the
   canonical start-state hash, the generalized `SimChecksum`, and the server-shared-sim-path all ride this seam.
3. **AOT server `.csproj` extraction stays deferred (per D5); discipline-only now.** ✅ Land the Godot-free
   shared-source set into `ProjectChimera.Sim.Tests` + the advisory banned-API/AOT analyzers + the
   never-inherit-`EnableDynamicLoading` rule. No second build target for 1.0; ship/verify N≤4.
4. **D6 secrets/config migration lands with the editor/MP coordinator carve** (migration Step 12), since the
   `LLMService`/`ModIoService` key sites are inside the exact coordinators being extracted. ✅ Retires the
   plaintext-`[Export]`-key smell in one coherent presentation-layer pass.

**Decision — Shrinking Composition Root + Sim-Spine Strangler (Option A).** Decompose
`godot/src/Core/MainScene.cs` (2,223 LOC, `partial class MainScene : Node3D`, namespace `ProjectChimera.Core`)
by splitting it along its true fault line — **composition vs. presentation vs. sim-mutation** — without moving
the file. MainScene stays the scene root at `res://src/Core/MainScene.cs` (main.tscn:3 untouched, so there is
no irreversible scene-repath cutover), but it stops *doing* work: it becomes a thin ordered composition root
that constructs (1) one Godot-free `SimulationHost` (the store + 9-system assembly, single owner of the fragile
loop order, shared verbatim by client and server) and (2) a list of focused presentation **coordinators**, in
the *exact present `_Ready()` order*, expressed as an explicit `ISetupPhase[]` literal guarded by a
`PhaseOrderTest`. The load-bearing move is **sim-seam-first**: extract a Godot-free `ScenarioApplier` (the sim
half of `ApplyScenario`/`SpawnScenarioUnit`/`ApplyFallbackScenario`) gated by the currently-**missing**
fail-closed `Validate(model)` boundary, *before* any cosmetic coordinator carving — because D3/D4/D5/C2/C6 all
block on it and it is the only extraction that crosses the sacred sim↔presentation line. No DI framework: the
manual `new` + `Initialize(...)` idiom is kept; `SimulationHost` and the phase list **are** the lightweight
composition seam (C8).

**Why A, not B (Thin Root + SimWorld Sandwich / boundary-purist) or C (Sim Spine First / SimulationHost-centric).**
B and C are excellent and score within a point; A is the synthesis spine because it is the only one whose first
commit takes zero irreversible risk against the hourly `[AutoSave]`→master loop (C4): B's plan ends in a
`main.tscn:3` root-script repath bundled with five extractions (a multi-hour red window if AutoSave fires
mid-edit), and C asserts `ApplyScenario` moves "verbatim, Godot-free" while it still embeds
`ProjectSettings.GlobalizePath` (MainScene.cs:509) + `FactionDefinition.LoadFromFile` (MainScene.cs:512) — a
build-break on master. A keeps MainScene in place and **reorders** the extraction so path resolution is hoisted
to a presentation pre-pass that hands the applier already-loaded `FactionDefinition` objects, making the
Godot-free claim true the instant the applier compiles. From B we **graft** the `ISetupPhase[]`-as-data +
`PhaseOrderTest` (the cleanest C1 treatment — order becomes asserted data, not implicit method-call timing) and
the `SimWorld` aggregate (one container the server and Sim.Tests both build). From C we **graft** the
`Validated<ScenarioModel>` type-enforced gate (makes "no raw `Fixed.FromFloat` on external data outside the
gate" analyzer-checkable, not a convention), the single `SetChecksumSink` owner (retires the verified
OnChecksum double-set trap), and `FactionSlots.ToFaction(slot)` centralizing the `(Faction)(slot+1)` magic. All
three reviewers independently re-derived the same correction set (missing Validate gate, mid-tick OnSpawnUnit
write, the `+1` duplication, the hidden `_uiCanvas` dep, the OnChecksum double-set, the `[5]`/`[2]` faction-array
hardcodes, the server-has-no-shared-sim-path reality, the un-homed `ILogSink`/`StressTest`) — this section
adopts every one.

### Settled sub-decisions

1. **MainScene stays at `src/Core/MainScene.cs`.** It remains `partial class MainScene : Node3D` and the scene
   root (main.tscn:3 unchanged). It keeps: the `[Export]` inspector surface (minus the two secret keys, see §10),
   `_Ready()` as the ordered orchestration script (now a phase-list run), the headless early-return branch
   (delegates to `ServerBootstrap`), and one-line `_Input`/`_UnhandledInput`/`_Process` forwards. It loses every
   inline Godot-tree builder, every sim-mutation body, and both `[Export]` secret fields. Target ≤ 250 LOC.
2. **`SimulationHost` (Godot-free) is the single owner of the fragile 9-system order.** It absorbs the store
   construction (MainScene.cs:246–266) and the `SimulationLoop` assembly with its 9 ordered `ISimSystem`s —
   `BuildingSystem, GatheringSystem, MovementSystem, CombatSystem, ProjectileSystem, SupplySystem,
   FogOfWarSystem, AiOpponentSystem, ScenarioDirector`(last, runs after a fully-updated world, MainScene.cs:266).
   The D1 `ModifierSystem` inserts at **index 3** (before `CombatSystem`). `EnableChecksums` (MainScene.cs:268)
   and the `OnChecksum` hook (MainScene.cs:269–270) move here behind a single `SetChecksumSink` called exactly
   once by the multiplayer phase — eliminating the verified silent double-set (MainScene.cs:269 inline vs.
   MainScene.cs:1761 MP-overwrite). `SimulationHost` is the object that **both** `ProjectChimera.Sim.Tests` and
   `ServerBootstrap` construct.
3. **`ScenarioApplier` (Godot-free) is the sole writer of sim truth.** It absorbs `ApplyScenario`
   (MainScene.cs:499), `SpawnScenarioUnit` (MainScene.cs:562), `ParseBuildingType` (MainScene.cs:587),
   `ApplyFallbackScenario` (MainScene.cs:599), and the sim half of `MoveStartPosition` (MainScene.cs:1009–1011).
   It consumes a `Validated<ScenarioModel>` + `SimulationHost` and writes
   `_resources`/`_nodes`/`_buildSys`/`_world`/`_scenarioDirector`. It exposes `SpawnUnit(def, faction, x, z)`
   (the director calls this directly) and `SetFactionBase(faction, pos)` — the **single** path both
   scenario-load (MainScene.cs:519) and the editor `MoveStartPosition` (MainScene.cs:1010) route through. **Path
   resolution is hoisted out**: the applier receives already-loaded `FactionDefinition` objects from a
   presentation pre-pass (the `GlobalizePath`/`LoadFromFile` at MainScene.cs:509/512 stays in
   presentation/ContentLoader); the applier carries **no** `res://` or Godot call. `SpawnUnit` must be
   allocation-free (pre-resolved def, no LINQ) to satisfy the zero-alloc-in-tick assert.
4. **`ScenarioValidator` is the net-new fail-closed `Validate(model)` gate (C2).** Pure C#, AOT-eligible, in
   `src/Core/Definitions`. `ValidationResult Validate(ScenarioModel)` bounds-checks every value currently
   `Fixed.FromFloat`'d raw — `StartOre`/`BaseX`/`BaseZ` (MainScene.cs:518–520), node `X`/`Z`/`Supply`/`Rate`
   (MainScene.cs:526–528), building `X`/`Z`+slot (MainScene.cs:534–535), unit `X`/`Z`+slot
   (MainScene.cs:543/564); rejects NaN/Inf (via the D3 `JsonConverter<Fixed>`); rejects `slot >= FACTION_COUNT`
   (fixes the length-5 overflow at MainScene.cs:242); and gates trigger/director data, because `ApplyScenario`
   ends in `_scenarioDirector.LoadScenario(scenario)` (MainScene.cs:558). `ScenarioApplier` consumes **only**
   `Validated<ScenarioModel>` — a malformed model cannot reach a store. Every untrusted source (D3 file, D4
   profile/Nakama, D6 AI output, fallback, editor in-memory, replay) funnels through this one gate.
5. **`ServerBootstrap` is a peer composition root, not a client branch (C6).** It builds a `SimulationHost` +
   runs `ScenarioValidator` + `ScenarioApplier` with **no presentation** — the headless branch
   (MainScene.cs:220–228) calls it instead of only constructing `DedicatedServer`. The server holds **zero** sim
   state today; this **creates** the shared path (it does not preserve one). Godot detection
   (`DisplayServer.GetName()`/`OS.HasFeature`, MainScene.cs:220) and `ParsePortArg` (MainScene.cs:2210) stay as a
   thin Godot edge that passes a port `int` down. The server-authority core (checksum collector + majority-vote
   DesyncAlert, `TickCommandsMerged` re-stamp, Ready-COUNT, adaptive delay, PROTOCOL_VERSION compare, multi-hash
   Ready — D5) lives in a Godot-free `ServerHost` extracted out of `DedicatedServer` (which stays the transport
   shell). The server-shared source set `{Core/Sim, Core/Definitions, Effects, Dsl, Combat, Economy, Navigation,
   AI(sim)}` is the future AOT `.csproj` include manifest; `EnableDynamicLoading` (godot.csproj:5) must **not** be
   inherited by it.
6. **The fragile `_Ready()` order becomes an asserted `ISetupPhase[]` literal (C1).** Each former `Setup*()`
   (MainScene.cs:272–299) becomes one small `ISetupPhase` in the **identical** order; a `ScenePhaseRunner`
   executes the array; a `PhaseOrderTest` pins the literal. Hidden dependencies become explicit constructor
   inputs, not implicit timing: `HudPhase` **publicly owns `_uiCanvas`** (built in `SetupHud`, MainScene.cs:1028)
   and injects it into the ≥5 later phases that `AddChild` to it (Minimap, WinConditionUi, GameOverOverlay,
   ReplayStatus, TriggerEditor-toast) — converting a silent NRE-ordering hazard into a compile/null-checked
   contract. The documented order invariants are migrated onto phase entries: Settings→Audio
   (MainScene.cs:272–273), Navigation→Camera (MainScene.cs:277), Camera→Rendering (the `CombatFeedbackBridge`
   needs `_camCtrl`, MainScene.cs:832), LoadAndApplyScenario→FlowFieldBridge.Initialize (MainScene.cs:283→289),
   MP/Browser/Menu/Trigger/MapGen **last** (MainScene.cs:294–299), scenario hash **after** scenario+lobby
   (MainScene.cs:303). Reordering now requires editing a test-guarded list.
7. **The On\* delegate seam is preserved as a growable contract, and the one sim-write is repaired (C3).** A
   single `ScenarioDelegateBinder` (in `src/CreationSuite`) is the sole site that assigns `ScenarioDirector.On*`
   (today MainScene.cs:1879–1899). `OnDisplayMessage`/`OnPlaySound`/`OnVictory` stay presentation; the binder is
   designed to **grow** to the D1-expanded set (PlayVfx/ShakeScreen) without touching MainScene. **Critical
   repair:** `OnSpawnUnit` (today a presentation closure that calls `SpawnScenarioUnit` mid-tick,
   MainScene.cs:1879–1893, executing presentation code *inside* the sim step at director-runs-last
   MainScene.cs:266) is **re-pointed at `ScenarioApplier.SpawnUnit`** so the sim director calls sim code directly
   — removing the determinism hazard while preserving the seam contract.
8. **`FactionSlots`/`FactionRegistry` localizes all faction-count knowledge (C7).** `FactionSlots.ToFaction(slot)`
   centralizes the `(Faction)(slot+1)` `+1` offset duplicated at MainScene.cs:504/534/543 and in the OnSpawnUnit
   closure (MainScene.cs:1881); `FactionRegistry` owns `FACTION_COUNT`, the active-faction list, and the per-slot
   `FactionDefinition` array (replacing `new FactionDefinition[5]` at MainScene.cs:242 and the `[2]`-sized
   `StartPositionBridge` at MainScene.cs:965). `FactionView` is a thin presentation read-struct the
   HUD/win/game-over loops iterate. This covers the three MainScene 2-faction hardcodes (`UpdateHud`
   MainScene.cs:1127–1156, `ShowGameOver` MainScene.cs:1549–1578, `CheckWinCondition` MainScene.cs:2164–2201),
   the missed presentation hardcodes (`StartPositionBridge[2]` MainScene.cs:965, `ExportMapPackage` 2v2/1v1
   MainScene.cs:1443–1446), **and the sim-side hardcode the runner-up designs left un-homed**: `ScenarioDirector`'s
   `OnVictory?.Invoke(1 - a.Faction)` (ScenarioDirector.cs:326) and `slot < 2` threshold loop
   (ScenarioDirector.cs:165) are rewritten to iterate active factions via `FactionRegistry`. Verified at **N≤4**
   for 1.0; raising `FACTION_COUNT`/`Faction` enum (`Player4`→`Player8`, EntityWorld.cs:47–54) is then a localized
   constant bump (D5 fast-follow).
9. **`ILogSink` is the net-new deterministic logging seam (Step 5).** Sim code logs through `ILogSink` (no
   `GD.Print`, no alloc/ordering side-effects), so the Godot-free server/test project runs and the tick is never
   perturbed. The sim-path `GD.Print` sites (e.g. MainScene.cs:551, `ScenarioDirector`) route here; presentation
   injects a `GodotLogSink`; tests/server inject a no-op/buffer sink.
10. **D6 secrets + config (`ISecretStore`).** The two `[Export]` plaintext-secret paths are **ripped out**:
    `AnthropicApiKey` (MainScene.cs:206→1858) and `ModIoApiKey` (MainScene.cs:200→1795). Provider/model/baseUrl
    move into versioned `SettingsData` (schema_version + migration registry, parallel to D3 content migration);
    keys are read via `ISecretStore` over gitignored `user://secrets/llm.key`. `LLMService`/`ModIoService` are
    key-injected, never `[Export]`-fed. The `SecretExclusionTest` enforces no key in build output. The remaining
    `[Export]` config (AiLevel/ScenarioPath/ReplayPath/Nakama*/GameServer*/ModIoGameId) copies into a typed
    `GameConfig` DTO at boot.
11. **`StressTest.cs`** (the *second* `using Godot;` file in `src/Core`, backing `scenes/stress_test.tscn`)
    relocates to `tools/` (or `tests/benchmark`) so the `BannedSimApiAnalyzer` over `src/Core` does not flag
    unrelated code; its deterministic-headless descendant becomes the Step-5 perf benchmark.

### Target directory tree

```
godot/
  godot.csproj                          # client; EnableDynamicLoading=true (NOT inherited by future AOT server)
  godot.sln
  scenes/ main.tscn                     # res:// path -> src/Core/MainScene.cs  (UNCHANGED)
  ProjectChimera.Sim.Tests/             # NET-NEW (Step 5): Godot-free plain-.NET runner; glob-includes the pure-sim folders
    ProjectChimera.Sim.Tests.csproj     #   shared-source home the deferred D5 AOT server compiles from
    Golden/GoldenChecksumReplayTests.cs
    Determinism/{SimRng,AscendingId,ZeroAllocInTick}Tests.cs
    Validation/NegativeValidationTests.cs        # targets ScenarioValidator
    Builder/ScenarioApplierTests.cs              # targets ScenarioApplier — Godot-free proof + start-state hash
    Checksum/SimChecksumCoverageGuardTest.cs
    Migration/MigrationRoundTripTests.cs         # v1->v2 reject
    Secrets/SecretExclusionTest.cs               # D6
    Bootstrap/PhaseOrderTest.cs                  # pins the ISetupPhase[] literal
    Perf/HeadlessStressBenchmark.cs              # from relocated StressTest.cs
  analyzers/                            # NET-NEW (Step 5): advisory-on-master Roslyn
    BannedSimApiAnalyzer/               #   flags `using Godot;` / float gameplay in sim layer
    AotAnalyzer/                        #   D3/D5 AOT-eligibility gate
  tests/                                # NET-NEW (Step 5, greenfield): Tier-2 GdUnit4 presentation/integration
    Integration/MainSceneBootSmokeTest.cs
  tools/
    StressTest.cs                       # MOVED out of src/Core (second Godot-in-sim smell removed)
  src/
    Core/
      MainScene.cs                      # THIN root (2,223 -> <=250 LOC); Node3D; scene root preserved
      FixedPoint.cs  EntityWorld.cs     # EntityWorld: +Energy/Mana SoA (D1), Faction->Player8 (D5 fast-follow)
      SimulationLoop.cs                 # +ModifierSystem slot; OnChecksum threaded from host
      SimChecksum.cs                    # generalized P1/P2 -> active factions; +SimRng/DslVarTable/Modifier/Hero
      ScenarioDirector.cs               # OnSpawnUnit -> ScenarioApplier.SpawnUnit; 1-a.Faction / slot<2 -> FactionRegistry
      Bootstrap/                        # NET-NEW (C1)
        ISetupPhase.cs  ScenePhaseRunner.cs
        Phases/ SettingsPhase.cs ... MapGeneratorPhase.cs  (21 phases, order = the literal)
      Sim/                              # NET-NEW pure-C# spine (C2/C3/C6) — NO using Godot
        SimulationHost.cs               #   stores + 9-system loop; single SetChecksumSink owner
        SimWorld.cs                     #   aggregate of sim-truth stores (host's payload)
        ScenarioApplier.cs              #   ApplyScenario/SpawnUnit/ParseBuildingType/Fallback (Godot-free)
        FactionSlots.cs FactionRegistry.cs  # centralizes (Faction)(slot+1) + FACTION_COUNT + per-slot defs
        DslVarTable.cs                  #   D2 top-level sim store (sibling of BuildingStore)
        HeroStore.cs                    #   D4 sparse SoA
        ILogSink.cs                     #   Step-5 deterministic logging seam
        ServerBootstrap.cs              #   headless shared sim/apply root (C6)
      Definitions/                      # Godot-free DTOs/converters (AOT-eligible)
        ScenarioData.cs ScenarioModel.cs FactionDefinition.cs UnitDefinition.cs SettingsData.cs
        ScenarioValidator.cs            #   NET-NEW Validate(model) gate (C2)
        ContentLoader.cs                #   D3 single choke point + one canonical JsonSerializerOptions
        FixedJsonConverter.cs NodeBaseJsonConverter.cs ChimeraJsonContext.cs   # D3
        MigrationRegistry.cs GameVersion.cs CanonicalModelHash.cs              # D3 (FNV-64 algo-2)
        EffectDef.cs DslGraphDef.cs                                            # D1/D2 serializable defs
        PlayerProfile.cs PersistenceManifest.cs                                # D4
        GameConfig.cs                   #   typed carrier for [Export] config (D6)
    Effects/                            # D1 — ProjectChimera.Effects, pure sim, peer of Combat/Economy
      EffectNode.cs (sealed) Sequence.cs SearchArea.cs Persistent.cs
      EffectContext.cs EffectExecutor.cs EffectValidator.cs
      ModifierStore.cs ModifierSystem.cs (ISimSystem @ index 3) SimRng.cs
    Dsl/                                # D2 — ProjectChimera.Dsl, pure sim
      NodeBase.cs GraphExecutor.cs ExpressionEvaluator.cs EventBus.cs
      CustomEventRegistry.cs DslValidator.cs DslEventCommand.cs (write rail)
    Combat/ Economy/ Navigation/        # existing pure sim, untouched
    AI/                                 # ProjectChimera.AI — AiOpponentSystem (sim) stays
      ILLMProvider.cs AnthropicAdapter.cs OllamaAdapter.cs OpenRouterAdapter.cs   # D6 (Godot-free)
    Multiplayer/
      DedicatedServer.cs                # transport shell; RelayCore/ServerHost extraction shapes deferred AOT split
      Server/ ServerHost.cs ProtocolVersion.cs DelayController.cs                 # D5 authority core (Godot-free)
      MatchLifecycleController.cs       # NET-NEW: SetupMultiplayer/OnMatchStart/replay
      ServerChecksumCollector.cs TickCommandsMerged.cs ReadyStateMachine.cs       # D5
      LockstepManager.cs ENetTransport.cs ReplayRecorder.cs ReplayPlayer.cs       # existing
    CreationSuite/
      EditorToolsController.cs          # NET-NEW: terrain brush/content browser/main menu/trigger+mapgen/build placement
      MapIoController.cs                # NET-NEW: export/import .chimera.zip + reload carrier
      ScenarioDelegateBinder.cs         # NET-NEW: sole On* wiring site (growable; OnSpawnUnit -> applier)
      TriggerEditorPanel.cs MapGeneratorPanel.cs CustomUiBridge.cs DslVarReadback.cs  # D2 read rail
    UI/
      InputRouter.cs                    # NET-NEW: _Input/_UnhandledInput/build-placement/RaycastFloor
      GameLoopDriver.cs                 # NET-NEW: _Process body (replay/online/offline branch dispatch)
      Presenters/ FactionRoster.cs HudPresenter.cs GameOverPresenter.cs WinConditionPresenter.cs FactionView.cs
      Settings/ SettingsManager.cs SettingsPanel.cs ISecretStore.cs LocalFileSecretStore.cs GodotLogSink.cs
      Bridges/ (existing 9 *Bridge readers) CustomUiBridge (D2)
    UGC/                                # ProjectChimera.UGC — mod.io IO (presentation/IO side)
```

### Architectural boundary rules

- **Sim-truth writes** (any `_world`/`_resources`/`_nodes`/`_buildings`/`ModifierStore`/`DslVarTable`/`HeroStore`
  SoA, `_buildSys.PlaceBuildingDirect`, `_scenarioDirector.LoadScenario`, `FactionBase`, `ore`) live **only** in
  `src/Core/Sim`|`Effects`|`Dsl`|`Combat`|`Economy`|`Navigation` — **no `using Godot;`**. Presentation never
  touches a store directly; it calls a named command on `SimulationHost`/`ScenarioApplier` (`SpawnUnit`,
  `SetFactionBase`, `QueueWorkerBuild`). Both `FactionBase` write sites (`ApplyScenario`, `MoveStartPosition`)
  route through `ScenarioApplier.SetFactionBase`.
- **No raw `Fixed.FromFloat` on external data** may occur before `ScenarioValidator.Validate` returns `Ok`.
  `ScenarioApplier` accepts only `Validated<ScenarioModel>` — a type-enforced, analyzer-checkable invariant.
- **All JSON deserialization** goes through `ContentLoader` + the one canonical `JsonSerializerOptions`. New
  `FactionDefinition.LoadFromFile`/bespoke `JsonSerializer` calls are forbidden (the divergent second load path at
  MainScene.cs:512 is folded onto `ContentLoader`). **Ordering invariant (D3):** options-unification (D3.0) must
  precede the enum lift (D3.2) or the two loaders bind `UnitDefinition` divergently.
- **Faction-count knowledge** lives only in `FactionSlots`/`FactionRegistry`. No literal `Player1`/`Player2`, no
  `slot+1`, no `[5]`/`[2]` array sizing, no `slot<2` loop anywhere else (sim or presentation).
- **The On\* delegates are a presentation-output channel**: a sim node may *fire* them but they may never
  *read/write* sim state. `ScenarioDelegateBinder` is the only assignment site and may grow without editing
  MainScene.
- **Sim logging** goes through `ILogSink`; `GD.Print` below the presentation layer is `BannedSimApiAnalyzer`
  territory.
- **Secrets** come from `ISecretStore` only; an `[Export]` string holding a key is a banned pattern
  (`SecretExclusionTest`).
- **The dedicated server** composes `ServerBootstrap` and must never reference `UI`/`Presenters`/`CreationSuite`
  or a MainScene member. Its source set is the future AOT include manifest.
- **Shared-source guard:** the sim folders are **glob-included** into `ProjectChimera.Sim.Tests` (not hand-listed
  `<Compile Include>`), with a CI check that the sim folder set matches — preventing a new sim file from compiling
  in the client but silently missing from the Godot-free/AOT compile until the AOT step.

### Migration sequence (strangler; golden-checksum-gated; always-shippable; advisory-on-master)

> Gates are **advisory on master** (the repo auto-commits hourly via `[AutoSave]`; a hard pre-commit gate would
> fight that loop). Hard enforcement and the one behavior-changing flip happen on a **release branch** only.

- **Step 0 — Gate foundation (no behavior change):** create `ProjectChimera.Sim.Tests.csproj` (net8.0, no Godot)
  + `GoldenChecksumReplayTests` pinning today's `SimChecksum` sequence for `alpha_map_01` over N ticks. Add
  `BannedSimApiAnalyzer` (warn-only; it already flags MainScene + StressTest). Add `ILogSink` + a trivial
  `GodotLogSink`; leave sim `GD.Print`s in place. **Ship.**
- **Step 1 — Extract `SimulationHost`/`SimWorld` (mechanical lift):** move the store + `SimulationLoop`
  construction (MainScene.cs:246–266) + `EnableChecksums` (MainScene.cs:268) verbatim into `SimulationHost`.
  MainScene reads `_host.World` etc. System order byte-identical. Golden checksum must match. **Ship.**
- **Step 2 — Hoist faction-def resolution to a presentation pre-pass:** lift `GlobalizePath`/`LoadFromFile`
  (MainScene.cs:230–239, 509–514) into a small presentation resolver that produces already-loaded
  `FactionDefinition` objects. **No sim move yet** — this is the prerequisite that lets the applier be Godot-free
  at Step 3. Golden checksum unchanged. **Ship.**
- **Step 3 — Extract `ScenarioApplier` (Godot-free):** move
  `ApplyScenario`/`SpawnScenarioUnit`/`ParseBuildingType`/`ApplyFallbackScenario` into `Sim/ScenarioApplier`,
  taking `SimulationHost` + pre-resolved defs. Replace the four `(Faction)(slot+1)` sites with
  `FactionSlots.ToFaction`. Add `ScenarioApplierTests` (Godot-free, asserts identical store contents + a
  start-state hash baseline the instant it compiles). Golden checksum identical. **Ship.**
- **Step 4 — Insert `ScenarioValidator` in shadow/log-only mode (C2 seam present):** add `Validate(model)` called
  before every Apply on all paths; failures **log-only**, still apply, so master never breaks. Add
  `NegativeValidationTests` (NaN ore, `slot>=FACTION_COUNT`, out-of-bounds X/Z) asserting rejection. **Ship.**
  *(Fail-closed flip is a release-branch step — confirmed by Alec; see Scope call 1.)*
- **Step 5 — Centralize `FactionRegistry`:** introduce `FactionRegistry.FactionForSlot` + `FACTION_COUNT`; size
  `_slotFactionDefs`/`StartPositionBridge[]` off it. No behavioral change at N≤2. Golden checksum unchanged.
  **Ship.** *(C7 keystone.)*
- **Step 6 — Stand up `ServerBootstrap` (C6 created, not preserved):** re-point the headless branch
  (MainScene.cs:220–228) to build `SimulationHost` + `Validate` + `Apply` with no presentation. Add a determinism
  test: server start-state checksum == client offline start-state on the same scenario. **Ship; client unaffected.**
- **Step 7 — `ScenePhaseRunner` wrap (mechanical):** wrap the existing `_Ready` Setup* sequence as a named
  `ISetupPhase[]` in the **same** order; add `PhaseOrderTest`. No bodies move. **Ship.**
- **Step 8 — Carve presentation coordinators, ONE phase per commit:** order `HudPresenter` **first** (it owns
  `_uiCanvas`, injected as a ctor dep into later phases) → `WorldView`/`Navigation` → `Minimap` →
  `WinConditionPresenter` + `MapIoController` → `GameOverPresenter` → `ReplayStatus` → `InputRouter` +
  `GameLoopDriver`. Each move is behavior-identical, golden-gated (sim untouched), smoke-tested (build placement
  still beats selection; F5; replay/online/offline branches). **Ship per move.**
- **Step 9 — `ScenarioDelegateBinder` + OnSpawnUnit repair:** make the binder the sole On* site; re-point
  `OnSpawnUnit` at `ScenarioApplier.SpawnUnit` (sim→sim). Re-run the spawn-trigger golden replay to prove
  identical sim output. **Ship.**
- **Step 10 — `MatchLifecycleController` + single `SetChecksumSink`:** extract
  `SetupMultiplayer`/`OnMatchStart`/replay; delete the inline MainScene.cs:269–270 assignment so the host's
  `SetChecksumSink` is the single owner (kills the MainScene.cs:269-vs-1761 double-set). Verify offline print
  **and** online `lockstep.SendChecksum`. **Ship.**
- **Step 11 — Generalize 2-faction surfaces (C7 done at N≤4):** rewrite
  `HudPresenter`/`GameOverPresenter`/`WinConditionPresenter` to loop over `FactionRoster`; generalize
  `ScenarioDirector` `1-a.Faction` (ScenarioDirector.cs:326) + `slot<2` (ScenarioDirector.cs:165); fix
  `ExportMapPackage` tags (MainScene.cs:1443–1446). Add a 3–4-faction golden scenario so the span path is
  exercised deterministically. **Ship.**
- **Step 12 — D6 secrets/config:** introduce `ISecretStore` + `LocalFileSecretStore`; rip `AnthropicApiKey`
  (MainScene.cs:1858) and `ModIoApiKey` (MainScene.cs:1795) out of `[Export]`; move provider/model into versioned
  `SettingsData`; add `SecretExclusionTest`. **Ship.**
- **Step 13 — Generalize `SimChecksum` as its OWN golden re-baseline:** widen `SimChecksum` from P1/P2-only
  (SimChecksum.cs:53–54) to active factions + fold in `SimRng`/`DslVarTable`/`Modifier`/`Hero`. This is a
  **hash-changing event** — it is its own re-baseline step so the intended widening is never confused with a
  regression. Add `SimChecksumCoverageGuardTest`. **Ship.**
- **Step 14+ — Plug-in band:** D1 (`Effects` + `ModifierSystem` @ index 3 + `SimRng`), D2 (`Dsl` + `DslVarTable`
  + read rail), D3 (`ContentLoader` folds the two load paths; canonical model hash replaces the file hash at
  MainScene.cs:303 — fixing the stale-hash-on-generated-scenario bug), D4 (`HeroStore`/profiles through the gate),
  D5 (`ServerChecksumCollector`/`TickCommandsMerged` + `Faction`→`Player8` constant bump). Each lands behind its
  own golden gate; none touches MainScene beyond a `SimulationHost`/coordinator hookup.

### Prerequisites (surfaced)

- **The cross-cutting 3-way hashing bug is the M1 foundation** all of D4/D5/D6 converge on: `SimChecksum` hashes
  only `Ore[Player1]`/`Ore[Player2]` (SimChecksum.cs:53–54); `DedicatedServer` is a pure relay with no hash
  compare (DedicatedServer.cs:171–191); AI-generated in-memory scenarios ship a stale on-disk **file** hash
  (MainScene.cs:303, via `ScenarioSerializer.ComputeFileHash`). The shared remediation — canonical **model-level**
  start-state hash + server-enforced agreement + generalized `SimChecksum` — must land before D4/D5/D6 content
  reaches the lobby. The static `_pendingGeneratedScenario` (MainScene.cs:137) cross-reload path is the concrete
  reason a model-level (not file-level) hash is mandatory.
- **`FactionDefinition`/`UnitDefinition` must stay Godot-free** (they are Definitions DTOs — verified) so the
  applier's per-slot `GetUnit` lookup compiles in the sim assembly.
- **`SettingsData` becomes a versioned subsystem** (its own `schema_version` + migration registry) before §10 can
  land D6 provider fields + Step-5 accessibility fields.
- **Net-new seams the brief's inventory omitted, now homed:** `ILogSink` (deterministic logging, `src/Core/Sim`);
  per-system sub-checksums + a `.chmr` replay-diff tool (D5 desync diagnosis, `src/Multiplayer`/Sim.Tests);
  settings schema-versioning; accessibility baseline (input-remap/colorblind/UI-scale/subtitles in
  `SettingsData`/`SettingsPanel`); a translatable seam for the game's own UI (English-first 1.0; UGC text
  untranslated); the version-stamp coherence check
  (CurrentGameVersion/schema_version/checksum_algo_version/PROTOCOL_VERSION/min_game_version move together);
  zero-alloc-in-tick asserts + migration round-trip/replay-compat tests in `ProjectChimera.Sim.Tests`.
  **Deliberately NOT homed (do not re-derive):** the D4 engine-side hero-normalization mode enum is **CUT**
  (fairness via D1 Modifiers / D2 graph at match-init) — Step 6 creates no code path for it.

### Hand-offs

- **To Step 7 (patterns):** the read-rail/write-rail templates this structure assumes — `FogOfWarBridge` as the
  one-way read-rail template for D2's `CustomUiBridge`/`DslVarReadback`; the lockstep command bus as the sole
  write rail; the `ISetupPhase`/`ScenarioDelegateBinder`/`SimulationHost.SetChecksumSink` single-owner patterns —
  should be lifted into Step 7 as the canonical composition/seam patterns. The `Validated<T>` gate pattern is the
  canonical "untrusted input → fail-closed → sim" pattern.
- **To M1 implementation:** Steps 0–6 are the M1 foundation (the Step-5 hand-off "MainScene decomposition must
  become testable as it is split" is satisfied: the golden harness is stood up at Step 0 and every sim-touching
  step rides it). M1 must also run the deferred **Linux/WSL cross-platform check** (.NET-in-WSL is the only
  missing piece) against `ProjectChimera.Sim.Tests` to prove the Godot-free sim/server source genuinely builds
  off-Godot.

### Residual risks

- **Coordinator extraction (Step 8) is the one band a red client build can slip past the golden gate** — the sim
  is untouched, so a missed `_uiCanvas`/`_camCtrl` ref surfaces as a runtime NRE, not a checksum mismatch.
  Mitigation: one phase per commit, `_uiCanvas` injected as a ctor dependency (compile/null-checked), mandatory
  in-engine smoke after each.
- **C2 is only truly enforcing after the release-branch fail-closed flip** — between Step 4 and the flip, a
  malformed scenario still reaches `Fixed.FromFloat` (it is logged, not rejected). Signing off C2 at Step 4 is
  premature; the flip is the genuine close — **confirmed by Alec: release-branch flip after corpus-verify**
  (Scope call 1).
- **The fail-closed flip can reject a previously-tolerated hand-authored map** — mitigated by corpus-verifying
  every shipped scenario passes on a release branch before flipping, plus a legacy-amnesty migration pass.
- **`SimChecksum` widening (Step 13) is a hash-changing event** — kept as its own re-baseline step so a
  regression is distinguishable from intended widening.
- **N-faction is verified only at N≤2 today** — Step 11 adds a 3–4-faction golden scenario so the span-based path
  is exercised deterministically before claiming C7 closed; N≤8 stays a `FACTION_COUNT` constant bump (D5
  fast-follow), not a 1.0 deliverable.
- **Shared-source `<Compile Include>` drift** between `godot.csproj` and `ProjectChimera.Sim.Tests` could hide a
  Godot leak until the AOT step — mitigated by glob-includes + the CI folder-set match check.

### Open items (non-blocking; carry forward)

- Confirm the relocation target for `StressTest.cs` (`tools/` vs `tests/benchmark`) and whether
  `scenes/stress_test.tscn`'s script-path edit is an isolated commit or the file is left with an analyzer
  suppression. *(Implementation-time detail; Step 0/11.)*
- The deferred Linux/WSL cross-platform build check runs against `ProjectChimera.Sim.Tests` at M1 (only .NET-in-WSL
  is missing per memory) — the cheapest proof the Godot-free sim/server source set genuinely builds off-Godot.
- Per-system sub-checksums + the `.chmr` replay-diff desync tool (D5 diagnosis) are homed structurally but
  **fast-follow, not 1.0-blocking** unless an actual N-player desync forces them earlier.
- Translatable-UI + accessibility-baseline seams are homed in `SettingsData`/`SettingsPanel`; their content rides
  the D6/settings band (migration Step 12) or a later cross-cutting pass.

---

## Step 7 — Implementation Patterns

> Authored via an 11-agent design+adversarial-verify workflow (5 domain pattern-authors → 5 determinism/API
> reviewers → synthesis): **67 patterns, 49 determinism/API/consistency issues caught and fixed** (e.g. the
> as-built `SimChecksum` baseline corrected, `EffectContext` made a non-ref `readonly struct` so it is
> work-stack-storable, `SimRng` shared-not-copied, per-depth `SearchArea` buffers, `FACTION_COUNT`
> cardinality-vs-Neutral pinned). Every code example is grounded in the real as-built API signatures and
> verified determinism-safe. This record is the canonical pattern catalog; deeper material in the working
> sidecar `game-architecture.Step7-patterns-briefing.md`. **Recommend-and-confirm; Alec's three scope calls
> are tagged ✅.** Decided 2026-06-21.

#### Scope calls confirmed by Alec (2026-06-21)

1. **Tier-1 sim test runner = xUnit.** ✅ All three candidates (xUnit/NUnit/MSTest) are free/open-source; xUnit
   is the cleanest for exact `uint`/`ulong` checksum-equality + golden-fixture tests and is first-class in
   `dotnet test`.
2. **Hash width = 32-bit wire, 64-bit canonical.** ✅ The live per-60-tick `SimChecksum` + the wire Ready hashes
   stay 32-bit (the canonical 64-bit model hash is truncated for the wire) — least churn for the brownfield
   strangler; widening everything to `ulong` is deferred.
3. **Content numeric shape = `Fixed` end-to-end (convert at parse).** ✅ `FixedJsonConverter` quantizes + rejects
   NaN/Inf/over-range at deserialize, the validator checks `Fixed.Raw` ranges, and the canonical hash folds
   `.Raw` directly — one quantization boundary, no second conversion, fingerprint taken from the exact run-time
   numbers.

This section is the canonical "how an AI agent writes this codebase." It exists because D1-D6 will be implemented by multiple agents across many sessions, and the only way they produce **compatible, determinism-safe** code is a single written rule for every point where two competent agents would otherwise decide differently. The lens throughout is: *what desyncs, fragments, or breaks the sacred sim/presentation split if left unsaid?*

Three enforcement mechanisms back every pattern below: a **banned-API Roslyn analyzer** over the sim layer (`src/Core`, `src/Combat`, `src/Economy`, `src/Navigation`, `src/Effects`, `src/Dsl`), the **Tier-1 Godot-free test project** `ProjectChimera.Sim.Tests` (golden-checksum replay + coverage-guard + negative-validation + secret-exclusion), and **reviewable conventions** (single construction sites, named-constant corpora). The analyzer and tests are advisory-on-master per the migration strangler.

A note on the as-built baseline, because two reviews caught the grounding overstating it: today's `SimChecksum.Compute` (SimChecksum.cs:26) **already** folds EntityWorld Position/Health, BuildingStore Alive/Health/ConstructionTimer, and `Ore[Player1]`/`Ore[Player2]`. The real coverage hole is narrower and is the canonical target: `Crystal` (all factions), `Ore` for slots 3+, `SupplyUsed`/`SupplyCap`, and every net-new store (Modifier/Energy/DslVarTable). `Mix` is `private` (SimChecksum.cs:62) — it is widened to `internal` (with `InternalsVisibleTo` the test project) so external `FoldInto` methods can call it.

---

## Implementation Patterns

### Novel Patterns

These seven carry full designs because they are net-new subsystems with no as-built precedent to copy.

---

#### N1 — The deterministic kernel (SoA + Fixed + SimRng + generalized SimChecksum)

The spine. Everything else assumes it.

**RULE — Per-entity state is a new `public readonly T[] Name = new T[MAX_ENTITIES]` parallel array on EntityWorld, allocated in the ctor, reset to a zero/sentinel value (never `Fixed.FromFloat`) in `Create()`, and folded into `SimChecksum.Compute` — never a per-entity class, `List`, or `Dictionary`.**
WHY — EntityWorld is Struct-of-Arrays indexed by id with a free-list; a per-entity object breaks cache layout, id-reuse hygiene, and ascending-id iteration, and a store the checksum never sees can desync invisibly.

```csharp
// EntityWorld.cs — declare alongside the existing SoA arrays (Health[], AttackDamage[], …)
public readonly Fixed[] Energy;
public readonly Fixed[] MaxEnergy;

// ctor, next to the other `new T[MAX_ENTITIES]` lines:
Energy    = new Fixed[MAX_ENTITIES];
MaxEnergy = new Fixed[MAX_ENTITIES];

// Create(...) — reset EVERY new field. Authored defaults are PASSED IN by ScenarioApplier;
// never hardcode Fixed.FromFloat here. (EntityWorld.cs:206 VisionRange=Fixed.FromFloat(8f) is a
// pre-existing instance to migrate to a ctor parameter — Create() must use Fixed.Zero/FromInt only.)
Energy[id]    = Fixed.Zero;
MaxEnergy[id] = Fixed.Zero;
```

**RULE — Every per-entity loop in sim is `for (int id = 0; id < world.HighWaterMark; id++) { if (!world.IsAlive(id)) continue; … }` — ascending id, bounded by `HighWaterMark`, dead skipped via `IsAlive`. Never iterate a `List`/`HashSet`/`Dictionary` of ids.**
WHY — Iteration order is part of the determinism contract; ascending-id is the one order both peers agree on regardless of insertion history.

**RULE — `Fixed.FromFloat` runs ONLY at load (inside a converter or `ScenarioApplier`, after the `Validated<T>` gate). Inside `Tick` do only Fixed-vs-Fixed arithmetic, `Fixed.FromInt`, and `Fixed` constants — zero `float`, zero `FromFloat`, zero `ToFloat`.**
WHY — float math is non-deterministic across JIT/platform; `FromFloat` is a lossy `(int)(value*ONE)` quantization (FixedPoint.cs:27). The tick must be a pure integer-`Raw` pipeline.

```csharp
// LOAD (ScenarioApplier, after Validate→Ok) — float→Fixed allowed:
world.Speed[id] = Fixed.FromFloat(def.SpeedFloat);

// TICK — only Fixed; integer/constant literals, never FromFloat:
Fixed reduced   = world.AttackDamage[id] * Fixed.Half;     // NOT Fixed.FromFloat(0.5f)
Fixed threeTiles = Fixed.FromInt(3);
world.AttackCooldown[id] = world.AttackCooldown[id] - dt;  // duration via dt, never DateTime
```

**RULE — The ONLY sim randomness source is `SimRng` (seeded from the match seed, stored in the deterministic sim state, folded into SimChecksum). Draws happen in ONE canonical global order: ascending system-registration order, then ascending faction slot, then ascending entity id; candidates are collected and sorted ascending-id BEFORE `rng.NextInt(count)`.**
WHY — RNG determinism needs an identical seed-advance sequence AND an identical candidate ordering AND an identical *cross-site draw order* on every peer; any of the three diverging desyncs silently. `SimRng` is a single shared mutable instance referenced (never copied) by callers, so a draw in one place advances the stream everyone sees.

```csharp
Span<int> candidates = stackalloc int[MaxCandidates];
int n = 0;
for (int id = 0; id < world.HighWaterMark; id++)        // ascending id ⇒ candidates pre-sorted
{
    if (!world.IsAlive(id)) continue;
    if (world.FactionOf[id] == enemy) candidates[n++] = id;
}
if (n > 0) world.AttackTarget[picker] = candidates[rng.NextInt(n)];  // advances the shared stream
```

**RULE — `SimChecksum.Compute` folds every gameplay-truth SoA array (ascending id) and every per-faction store for ALL faction slots `0..FactionRegistry.FACTION_COUNT`, each as its integer `.Raw` (enums by `(int)`, bools as 1/0). Adding a store is a two-site edit — the store + a `Mix(…)` call — guarded by a reflection coverage test. Intentionally-excluded arrays carry a `[ChecksumExempt]` marker with a one-line justification.**
WHY — the per-60-tick checksum is the only desync tripwire; state it doesn't hash can diverge undetected. `PrevPosition` is exempt (presentation interpolation snapshot written by `SnapshotPositions`, not truth) — it is the canonical example of an exemption.

```csharp
// SimChecksum.cs — fold the FULL as-built gameplay-truth set, ascending id:
for (int i = 0; i < world.HighWaterMark; i++)
{
    if (!world.IsAlive(i)) continue;
    hash = Mix(hash, world.Position[i].X.Raw); hash = Mix(hash, world.Position[i].Y.Raw);
    hash = Mix(hash, world.Position[i].Z.Raw);
    hash = Mix(hash, world.Velocity[i].X.Raw); hash = Mix(hash, world.Velocity[i].Z.Raw);
    hash = Mix(hash, world.Health[i].Raw);     hash = Mix(hash, world.AttackDamage[i].Raw);
    hash = Mix(hash, world.AttackCooldown[i].Raw); hash = Mix(hash, world.AttackTarget[i]);
    hash = Mix(hash, (int)world.CommandState[i]);  hash = Mix(hash, (int)world.GatherState[i]);
    hash = Mix(hash, world.CarryAmount[i].Raw);    hash = Mix(hash, world.Energy[i].Raw); // NEW
    // PrevPosition is [ChecksumExempt] — presentation interpolation only, intentionally omitted.
}
// ALL faction slots, not Player1/Player2 literals. ResourceStore.Ore is indexed by (int)Faction,
// 1-based, slot 0 = Neutral, array length FactionRegistry.FACTION_ARRAY_SIZE.
for (int slot = 0; slot < FactionRegistry.PLAYER_COUNT; slot++)
{
    Faction f = FactionRegistry.ToFaction(slot);   // (Faction)(slot+1), the ONE cast site
    hash = Mix(hash, resources.Ore[(int)f].Raw);
    hash = Mix(hash, resources.Crystal[(int)f].Raw);     // closes the as-built Crystal hole
    hash = Mix(hash, resources.SupplyUsed[(int)f]);
}
hash = Mix(hash, rng.RawState);
```

**ENFORCEMENT** — analyzer: bans `float` locals / `Fixed.FromFloat` / `Fixed.ToFloat` / `System.Random` / `DateTime` / `Stopwatch` / `Time.GetTicksMsec` / `Dictionary`-`.Keys`/`.Values`-enumeration / unstable `Array.Sort` in the sim layer, and allow-lists `Fixed.FromFloat` to `FixedJsonConverter` (and the AI float→Fixed quantize step) ONLY — NOT `ScenarioApplier` (which receives already-`Fixed` model fields) and NOT `EntityWorld.Create`. test: `SimChecksumCoverageTest` (Tier-1, reflection over EntityWorld arrays + the store registry) fails when a non-exempt length-`MAX_ENTITIES` array or a per-faction store is not folded; the golden-checksum replay harness catches any leaked nondeterminism.

---

#### N2 — The Effect-Graph executor

D1, `src/Effects`, pure sim.

**RULE — Every gameplay effect is exactly one `sealed EffectNode` subclass exposing `void Apply(in EffectContext ctx)` + `bool Validate(in EffectValidationCtx v, out string error)`, constructed ONCE at scenario-load and immutable thereafter; `Apply` allocates nothing. No effect ever lands as a case in `ScenarioDirector`'s action switch (that switch is frozen).**
WHY — a closed sealed hierarchy is enumerable, registrable, load-validatable, and exhaustively testable; per-tick allocation introduces nondeterministic GC stalls.

```csharp
namespace ProjectChimera.Effects   // NO 'using Godot;'
{
    public sealed class DamageEffect : EffectNode
    {
        public readonly Fixed BaseAmount;        // authored, frozen at load (no Fixed.FromFloat in Apply)
        public readonly DamageType DamageType;
        public DamageEffect(Fixed baseAmount, DamageType dt) { BaseAmount = baseAmount; DamageType = dt; }

        public override void Apply(in EffectContext ctx)
        {
            int t = ctx.Target;
            if (t < 0 || !ctx.World.IsAlive(t)) return;
            DamageResolver.Apply(ctx.World, t, BaseAmount, DamageType);   // single choke point (see N2.5)
        }
        public override bool Validate(in EffectValidationCtx v, out string error)
        { if (BaseAmount < Fixed.Zero) { error = "DamageEffect.BaseAmount < 0"; return false; }
          error = string.Empty; return true; }
    }
}
```

**RULE (N2.5 — the unified DamageResolver) — ALL damage application (melee, projectile-hit, effect) routes through one `DamageResolver.Apply(world, target, baseAmount, dmgType)` leaf; no other site multiplies by `DamageMatrix.Get`. The canonical formula is `final = baseAmount * DamageMatrix.Get(dmgType, armorType)` (no `- armorValue` term — there is no per-entity ArmorValue array as-built; add one + fold it into SimChecksum only if the design later adopts armor subtraction).**
WHY — D1 exists to kill the three duplicated damage sites (CombatSystem.cs:271, ProjectileSystem.cs:76/:121); a single resolver is the only way that consolidation survives multiple agents. `DamageMatrix.Get(DamageType, ArmorType)` is **static** and returns `Fixed` as-built.

```csharp
public static class DamageResolver
{
    public static void Apply(EntityWorld world, int target, Fixed baseAmount, DamageType dmgType)
    {
        Fixed mult  = DamageMatrix.Get(dmgType, world.ArmorTypeOf[target]); // static, returns Fixed
        Fixed final = baseAmount * mult;                                    // all Fixed, no float
        world.Health[target] = world.Health[target] - final;
        if (world.Health[target] <= Fixed.Zero) world.Destroy(target);
    }
}
```

**RULE — Composite nodes (Sequence/SearchArea/Persistent) NEVER call `child.Apply` directly; one `EffectExecutor.Run` drives an explicit pre-allocated work-stack of `(node, context)` frames, popping/pushing in deterministic order, enforcing `MaxEffectDepth = 8` and `MaxEffectFrames` fuel.**
WHY — an explicit stack gives one canonical, depth-capped, allocation-free traversal; recursion hides order and depth and cannot be fuel-bounded.

**RULE — `EffectContext` is a `readonly struct` (NOT a ref struct) holding the finite reference frame `{World, Caster, Source, Target, Point, Rng, PresentationSink}`; it is never mutated — re-rooting produces a NEW context via `With*` and is passed `in`. `SimRng` and the presentation sink are reference fields shared (not copied) across re-roots.**
WHY — *fix applied:* a ref struct cannot be stored in the executor's `Frame[]` work-stack (mutually incompatible decisions in the source catalog). A plain `readonly struct` is array-storable and still zero-heap; sharing `Rng` by reference means a child's draw advances the stream the parent and siblings see, so draw order = work-stack pop order on every peer.

```csharp
public readonly struct EffectContext
{
    public readonly EntityWorld World;
    public readonly int Caster, Source, Target;   // entity ids, -1 = none
    public readonly FixedVec3 Point;
    public readonly SimRng Rng;                    // shared reference — never copied/advanced-in-isolation
    public readonly IEffectPresentationSink Sink;  // single sim-owned sink (see N6)
    public EffectContext WithTarget(int t) => new(World, Caster, Source, t, Point, Rng, Sink);
    public EffectContext WithPoint(FixedVec3 p) => new(World, Caster, Source, Target, p, Rng, Sink);
    // …full ctor omitted…
}

public sealed class EffectExecutor
{
    public const int MaxEffectDepth = EffectCaps.MaxEffectDepth;     // 8, from the ruleset corpus
    public const int MaxEffectFrames = EffectCaps.MaxEffectFrames;   // 256
    private readonly Frame[] _stack = new Frame[MaxEffectFrames];    // allocated ONCE
    // SearchArea hits live in a per-DEPTH slice so a nested SearchArea never clobbers its parent's buffer:
    private readonly int[] _hitRing = new int[MaxEffectDepth * EffectCaps.MaxHitsPerSearch];
    private readonly struct Frame { public readonly EffectNode Node; public readonly EffectContext Ctx;
        public readonly int Depth; public Frame(EffectNode n, in EffectContext c, int d){ Node=n; Ctx=c; Depth=d; } }

    public void Run(EffectNode root, in EffectContext rootCtx)
    {
        int sp = 0; _stack[sp++] = new Frame(root, rootCtx, 0);
        while (sp > 0)
        {
            Frame f = _stack[--sp];
            if (f.Depth >= MaxEffectDepth) continue;                 // depth cap
            if (f.Node is SequenceEffect seq)
                for (int k = seq.Children.Length - 1; k >= 0; k--)   // reverse push ⇒ authored order
                    _stack[sp++] = new Frame(seq.Children[k], f.Ctx, f.Depth + 1);
            else if (f.Node is SearchAreaEffect area)
            {
                int off = f.Depth * EffectCaps.MaxHitsPerSearch;     // per-depth slice, no clobber
                area.GatherAscendingId(f.Ctx, _hitRing, off, out int n);
                for (int k = n - 1; k >= 0; k--)
                    _stack[sp++] = new Frame(area.Body, f.Ctx.WithTarget(_hitRing[off + k]), f.Depth + 1);
            }
            else f.Node.Apply(f.Ctx);
        }
        f_flushPresentation();   // presentation On* fires are buffered during the drain, flushed once here
    }
}
```

**RULE — The `EffectNode` JsonConverter dispatches on a closed `kind` discriminator against a hardcoded registry; unknown kind, missing required field, or dangling node-id reference is a hard deserialize failure (`Validated<T>` → Err). No scripting/eval escape hatch. `[JsonPolymorphic]`/`[JsonDerivedType]` are forbidden.**
WHY — fail-closed deserialization is the only way two peers provably build the identical graph; `[JsonPolymorphic]` is incompatible with `UnmappedMemberHandling.Disallow` (dotnet/runtime #100057) and throws at runtime on the first real node. (See N4 for the converter shape.)

**ENFORCEMENT** — analyzer: no `using Godot;` in `src/Effects`; new cases in the frozen `ScenarioDirector` action switch flagged; an `EffectNode.Apply` calling another node's `Apply` flagged; `[JsonPolymorphic]` banned project-wide. test: `EffectRegistryCoverageTest` (every sealed subtype registered + round-trips Validate), negative-validation fixtures (unknown kind / dangling id / depth-overflow → Err), an alloc-probe asserting zero managed bytes across an `Apply` batch via `GC.GetAllocatedBytesForCurrentThread`, and an RNG-order test pinning state after a multi-leaf graph.

---

#### N3 — Modifier SoA + ModifierSystem

D1.

**RULE — A Modifier never writes a unit's stat array directly; it is an entry in a Modifier SoA store keyed by entity, and `ModifierSystem : ISimSystem` (inserted immediately BEFORE `CombatSystem` in the single `SimulationHost` registration) recomputes `Effective*` stats from `Base*` for dirty entities each tick. `Base*`/`Effective*` are NET-NEW parallel arrays: the migration renames authored `AttackDamage` → `BaseAttackDamage` and makes `AttackDamage` the recomputed Effective slot — both folded into SimChecksum.**
WHY — separating base from effective with a dirty flag makes add/remove/stack/expire reversible and order-independent; direct stat mutation is irreversible and stacks wrong. Registration order IS the design — `SimulationLoop` ticks systems in array order (`void Tick(EntityWorld, Fixed)`), so combat must read recomputed stats the same tick or lag one tick vs a correctly-ordered peer.

```csharp
public sealed class ModifierSystem : ISimSystem
{
    private readonly Fixed[] _flatDamageBonus = new Fixed[EntityWorld.MAX_ENTITIES];
    private readonly bool[]  _dirty            = new bool[EntityWorld.MAX_ENTITIES];
    public void MarkDirty(int id) => _dirty[id] = true;

    public void Tick(EntityWorld world, Fixed dt)
    {
        for (int i = 0; i < world.HighWaterMark; i++)            // ascending id
        {
            if (!world.IsAlive(i) || !_dirty[i]) continue;
            world.AttackDamage[i] = world.BaseAttackDamage[i] + _flatDamageBonus[i]; // Effective = Base + mods
            _dirty[i] = false;
        }
    }
}
// SimulationHost: new ModifierSystem() is registered BEFORE combatSystem.
```

**ENFORCEMENT** — test: stack/remove fixture (apply N, remove M, assert Effective == Base + remaining); a stat-timing fixture (buff applied this tick affects this tick's combat); the SimChecksum coverage-guard asserts the Modifier SoA and `BaseAttackDamage`/`AttackDamage` fold in; `SystemOrderTest` pins the registration array. convention: `CombatSystem` reads Effective only; systems constructed only in `SimulationHost`.

---

#### N4 — The DSL typed event/dataflow graph

D2, `src/Dsl`, pure sim. Contains D1 effect subgraphs.

**RULE — Every DSL node is a `NodeBase` with a persistent integer `Id` (stable across migrations) and exactly two edge kinds: ordered exec edges (control flow) and typed data edges (value flow, type-checked at load). The action region of an event node IS a D1 `EffectNode` graph. The node store is a DENSE array indexed by a load-resolved compact index (persistent-Id → dense-index map built at load); any whole-graph pass iterates that dense array ascending — never enumerate a `Dictionary<int,NodeBase>`.**
WHY — persistent ids let migrations/replays rewire unambiguously; separating exec from typed-data makes the IR statically checkable; dense ascending iteration avoids the as-built Dictionary-enumeration desync (ScenarioDirector.cs:149).

```csharp
namespace ProjectChimera.Dsl
{
    public abstract class NodeBase
    {
        public int Id { get; init; }                                  // persistent, canonical from migration step 1
        public int[] ExecNext { get; init; } = Array.Empty<int>();    // ordered next exec node ids
        public DataEdge[] DataIn { get; init; } = Array.Empty<DataEdge>();
    }
    public readonly struct DataEdge
    { public readonly int SourceNodeId, SourcePort; public readonly DslType Type;   // closed enum
      public DataEdge(int s, int p, DslType t){ SourceNodeId=s; SourcePort=p; Type=t; } }

    public sealed class OnEventNode : NodeBase
    { public int EventTypeId { get; init; }  public EffectNode Action { get; init; } = default!; } // D1 subgraph
}
```

**RULE — Iteration is expressed ONLY by `ForEach`/`ForEachBatched` over a finite collection SNAPSHOTTED ascending-id into a pre-allocated buffer at loop entry; no `While`/`Repeat`/recursion/`goto` node exists in the closed registry; per-tick fuel caps total iterations. `ForEachBatched` processes a fixed `BatchSize` per tick from the entry snapshot, advancing an integer cursor in the DslVarTable — never resnapshots, never a time budget.**
WHY — a snapshotted ascending-id collection with an integer fuel/batch cap terminates identically on every peer; live iteration or a wall-clock budget desyncs.

```csharp
public sealed class ForEachNode : NodeBase
{
    public int Body { get; init; }
    public const int MaxIterationsPerTick = DslCaps.MaxIterationsPerTick; // 4096
    public void Run(DslContext ctx, EntityWorld world, Faction owner)
    {
        int n = 0;
        for (int i = 0; i < world.HighWaterMark && n < MaxIterationsPerTick; i++)
            if (world.IsAlive(i) && world.FactionOf[i] == owner) ctx.LoopBuffer[n++] = i;  // ascending snapshot
        for (int k = 0; k < n; k++) ctx.RunExec(Body, k, ctx.LoopBuffer[k]);               // body sees frozen set
    }
}
```

**RULE — Expression nodes evaluate a closed, CEL-shaped, pure, side-effect-free grammar whose numeric type is `Fixed` ONLY; comparisons/arithmetic operate on `Fixed` directly. There is NO `Fixed→float→ToString("F2")→TryParse` path (the A17 as-built bug, ScenarioDirector.cs:168/170/252) and no `float`/`double` anywhere in evaluation.**
WHY — Fixed-only integer arithmetic is bit-identical across platforms; the as-built float round-trip rounds per-culture/JIT and silently desyncs.

```csharp
public sealed class CompareExpr : ExprNode
{
    public readonly ExprNode Left, Right; public readonly CompareOp Op;   // closed enum
    public override DslValue Eval(in DslEvalCtx ctx)
    {
        Fixed a = Left.Eval(ctx).AsFixed, b = Right.Eval(ctx).AsFixed;     // NOT .ToFloat()
        return DslValue.Bool(Op switch { CompareOp.Gt => a>b, CompareOp.Ge => a>=b,
            CompareOp.Lt => a<b, CompareOp.Le => a<=b, CompareOp.Eq => a==b, _ => a!=b });
    }
}
```

**RULE — Events are closed typed structs (not strings); custom events register in an ACYCLIC registry checked at load; `RaiseEvent` appends to a same-tick worklist drained FIFO in ascending registered-handler-id order (never Dictionary enumeration) until empty (fuel-capped); `RaiseEventNextTick` defers to a back buffer swapped at the next tick boundary. Any randomness inside an event-triggered effect uses the shared `SimRng` in work-stack order.** [Deferred to implementation (M1): cross-faction same-tick tie-break — ascending faction slot vs command-bus arrival re-stamp — is checksum-relevant and not settled; the worklist is FIFO by insertion until pinned.]
WHY — typed structs remove string-parse desync; an acyclic registry + FIFO worklist guarantees termination and one deterministic delivery order; recursive synchronous re-raise does neither.

**RULE — DSL variables live in a single top-level `DslVarTable` sim store (NOT inside ScenarioDirector), keyed by a dense integer index resolved at load from (closed `VarType` × `Scope`), backed by per-type parallel arrays, and folded into SimChecksum; the table is never enumerated to drive sim order.**
WHY — dense-index arrays give O(1) deterministic access with no enumeration-order dependence and can be checksummed, closing the as-built omission (ScenarioDirector.cs:34).

```csharp
public sealed class DslVarTable
{
    private readonly Fixed[] _fixedVars; private readonly bool[] _boolVars; private readonly int[] _entityVars;
    public Fixed GetFixed(int i) => _fixedVars[i];
    public void  SetFixed(int i, Fixed v) => _fixedVars[i] = v;
    public void FoldInto(ref uint hash)    // SimChecksum.Mix is internal (InternalsVisibleTo this assembly)
    {
        for (int i = 0; i < _fixedVars.Length;  i++) hash = SimChecksum.Mix(hash, _fixedVars[i].Raw);
        for (int i = 0; i < _boolVars.Length;   i++) hash = SimChecksum.Mix(hash, _boolVars[i] ? 1 : 0);
        for (int i = 0; i < _entityVars.Length; i++) hash = SimChecksum.Mix(hash, _entityVars[i]);
    }
}
```

**ENFORCEMENT** — analyzer: closed node registry has no While/Repeat/goto kind; `.ToFloat()`/`float`/`double`/`ToString("F2")` banned in `src/Dsl` evaluation; no `Dictionary<,>` field whose enumeration drives sim order; events must be structs in a closed namespace. test: load-time type-check (DataEdge.Type matches source port), id-uniqueness + dangling-exec fixtures, cycle-detection (a→b→a → Err), ForEach fuel-cap + snapshot fixtures, `DslVarTable.FoldInto` covered by the SimChecksum coverage-guard.

---

#### N5 — The two-rail custom UI

D2. The other half of the sacred one-way split.

**READ rail. RULE — Sim publishes a DOUBLE-BUFFERED, version-stamped numeric `DslVarReadback` snapshot exactly once per tick, at the END of `StepOnce` after all systems tick (same site/cadence as the SimChecksum block, SimulationLoop.cs:~89-100); a `CustomUiBridge` (Godot Node, modeled on `FogOfWarBridge`) reads it in `_Process` and re-formats a widget ONLY when `Version` changes. Formatting and all strings live entirely in presentation. The published buffer is filled, then `Version` bumped — the live-read buffer is never mutated in place.**
WHY — numbers-only at the tick boundary with a version gate keeps the sim string-free and allocation-free and makes the read path incapable of desyncing; in-tick formatting injects culture-dependent strings (the A17 bug). Double-buffering prevents a torn presentation-side read mid-publish.

```csharp
// SIM (src/Dsl): published exactly once at end of StepOnce. Back/front double buffer, atomic version bump.
public sealed class DslVarReadback
{
    public int Version { get; private set; }
    private readonly Fixed[] _front = new Fixed[DslVarTable.MaxPublishedVars];
    private readonly Fixed[] _back  = new Fixed[DslVarTable.MaxPublishedVars];
    public ReadOnlySpan<Fixed> Values => _front;            // presentation reads the stable front buffer
    public void Publish(DslVarTable table)
    { table.CopyPublishedInto(_back); var t = _front; /* swap */ Array.Copy(_back, _front, _back.Length); Version++; }
}

// PRESENTATION (src/UI/CustomUiBridge.cs): read sim, write Godot, never write sim.
public partial class CustomUiBridge : Godot.Control
{
    private DslVarReadback _rb = null!; private Godot.Label _gold = null!; private int _seen = -1;
    public override void _Process(double delta)
    {
        if (_rb.Version == _seen) return;                  // re-format only on change
        _seen = _rb.Version;
        _gold.Text = _rb.Values[DslVarTable.GoldIndex].ToFloat().ToString("N0"); // float/format PRESENTATION-side
    }
}
```

**WRITE rail. RULE — A custom-UI control's `Pressed` handler MUTATES NOTHING in sim — it enqueues a `DslEventCommand` (NET-NEW wire payload) onto `LockstepManager` so it rides the buffered/serialized/`currentTick+delay` pipeline and is raised inside the single shared `ApplyOrders` path on every peer, with per-event allowed-raiser authorization checked against the issuing slot's authoritative faction.**
WHY — only commands applied at an agreed tick on every peer stay in sync; a direct `RaiseEvent` from presentation applies at an arbitrary local tick and desyncs online + corrupts replays.

*The write rail is NET-NEW and does not exist on the bus today.* As-built `EnqueueOrder(int unitId, UnitCommand command, Fixed targetX, Fixed targetZ)` (LockstepManager.cs:215) takes four args and a `UnitOrder` (11 bytes); there is no `NetworkCommand` type and no event path. Implementing it requires: a new `PacketType.DslEvent` + `DslEventOrder` struct serialized in `TickCommandPacket`; a sibling `EnqueueEvent(ushort eventId, int slot)`; and a case in the single shared applier (see S-MP below), which all four live apply sites + `ReplayPlayer` route through.

```csharp
// PRESENTATION: enqueue, never mutate. Slot resolved via FactionRegistry, not a literal.
private void OnAbilityPressed() => _lockstep.EnqueueEvent(_eventId, _localSlot);

// APPLY (shared applier, every peer, same tick):
case NetCommandKind.DslEvent:
    if (!DslEventPolicy.MayRaise(o.EventId, expectedFaction)) break;     // authorization, deterministic drop
    _dslBus.RaiseEvent(o.EventId, raiser: expectedFaction, o.EventPayload); // ascending-handler-id drain
    break;
```

**ENFORCEMENT** — analyzer: presentation namespaces may not call `_dslBus.RaiseEvent` / mutate sim stores; string formatting of `Fixed` banned in the sim layer. test: `DslVarReadback.Version` monotonic + folded into the coverage-guard + a no-string-fields assertion on the snapshot; `DslEventCommand` round-trips serialize/deserialize; authorization fixture (unauthorized raiser dropped identically on all peers); replay re-applies identically.

---

#### N6 — The Validated&lt;T&gt; fail-closed content gate + single-owner seams

D3 + Step 6. The compiler enforces "validation ran."

**RULE — ALL JSON (de)serialization goes through `ContentLoader` using the single `static readonly ContentJson.Options`; never call `JsonSerializer.*` directly and never construct a `JsonSerializerOptions` anywhere else. The options carry `UnmappedMemberHandling.Disallow`, the source-gen context, `JsonStringEnumConverter` (enums by NAME), `FixedJsonConverter`, and `NodeBaseJsonConverter`.**
WHY — the Fixed + NodeBase converters and Disallow must apply uniformly to every byte; three divergent options objects (as-built: ScenarioSerializer.cs:23, FactionDefinition.cs:66, inline) are three parsers and three hashes.

**RULE — `ScenarioApplier` consumes ONLY `Validated<ScenarioModel>`; the wrapper has no public ctor (minted solely by `ScenarioValidator.Validate`, whose internal ctor is assembly-`internal`, not file-scoped); so no `Fixed.FromFloat` on external data can compile before validation returns Ok. The model-level `Validate` gate runs on ALL five entry paths — file, AI-gen, fallback, editor-in-memory, replay.**
WHY — a gate enforced by the compiler makes "forgot to validate one path" a compile error, not a NaN in `Fixed.Raw` or a multiplayer desync. **Decision (Alec ✅): the content model carries `Fixed` end-to-end** — `FixedJsonConverter` quantizes + rejects NaN/Inf/over-range at parse, the validator checks `Fixed.Raw` ranges, and the canonical hash folds `.Raw` directly (one quantization boundary, no second conversion, fingerprint taken from the exact run-time numbers).

```csharp
public readonly struct Validated<T>
{
    public readonly T Model;
    internal Validated(T model) => Model = model;   // assembly-internal; ScenarioValidator is the sole minter
}

// ScenarioApplier.Apply — signatures verified against as-built ResourceStore.AddOre / FactionBase.
// Fixed end-to-end: slot.StartOre / BaseX / BaseZ are ALREADY `Fixed` (deserialized via FixedJsonConverter,
// the single quantization boundary). The applier does NO second conversion — there is no convert-at-apply step:
public void Apply(Validated<ScenarioModel> v)        // cannot be called with a raw model
{
    foreach (var slot in v.Model.PlayerSlots)        // ascending slot order
    {
        Faction f = FactionRegistry.ToFaction(slot.Slot);   // centralized (Faction)(slot+1)
        _resources.AddOre(f, slot.StartOre);                // Fixed in → Fixed out; no FromFloat
        _resources.FactionBase[(int)f] =
            new FixedVec3(slot.BaseX, Fixed.Zero, slot.BaseZ);   // model fields are already Fixed
    }
}
```

**RULE — `FixedJsonConverter.Read` reads the JSON number as `double`, rejects NaN/±Inf/over-16.16-range, and quantizes ONCE via `Fixed.FromRaw`; `NodeBaseJsonConverter` dispatches on a closed `kind` registry and throws on unknown kind / dangling ref. `Write` may use `ToFloat()` for human-readable authoring round-trip ONLY (never reachable from the tick); save-then-reload equality is asserted via `Fixed.Raw`.**

```csharp
public sealed class FixedJsonConverter : JsonConverter<Fixed>
{
    public override Fixed Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
    {
        double d = r.GetDouble();
        if (double.IsNaN(d) || double.IsInfinity(d)) throw new JsonException("Fixed NaN/Infinity");
        double raw = d * Fixed.ONE;
        if (raw > int.MaxValue || raw < int.MinValue) throw new JsonException($"Fixed {d} out of 16.16 range");
        return Fixed.FromRaw((int)raw);                 // single guarded quantization at load
    }
    public override void Write(Utf8JsonWriter w, Fixed v, JsonSerializerOptions o) => w.WriteNumberValue(v.ToFloat());
}
```

**RULE — Every content failure is a `ContentError { Source, JsonPath, Message }` carried in `LoadResult<T>`; loaders/validators NEVER return null, throw past the boundary, or log-and-continue (replaces the as-built null-swallow ScenarioSerializer.cs:35-40 and `GD.PrintErr` MainScene.cs:551). Presentation surfaces it via `ILogSink`.**

**RULE (composition seams) — A new sim→presentation reader is a Godot Node `*Bridge` that READS the sim SoA in `_Process`, interpolates `Prev→Curr` by `SimulationLoop.InterpolationAlpha`, writes ONLY Godot transforms/textures, and NEVER writes sim. A new startup step is an `ISetupPhase` at an explicit index in the `ScenePhaseRunner` order literal (asserted by `PhaseOrderTest`), with cross-phase deps (e.g. `_uiCanvas`) constructor-injected. Sim fires presentation via fire-only `On*` delegates assigned solely by `ScenarioDelegateBinder`, which `Bind`s on every `ApplyScenario` and `Unbind`s (nulls every `On*`) on teardown so a stale delegate never fires into a freed node. Effect-fired presentation goes through ONE sim-owned `IEffectPresentationSink` (carried on `EffectContext`, assigned once by `ScenarioDelegateBinder`) — leaves never declare their own `Action` field.**
WHY — one-way sim→presentation is the sacred rule; centralizing assignment keeps it auditable and Godot out of sim; the single sink preserves the one-binder invariant rather than fragmenting into N delegate slots.

```csharp
public partial class HealthBarBridge : MultiMeshInstance3D   // reader, modeled on MultiMeshBridge.cs
{
    private SimulationLoop _sim = null!;
    public override void _Process(double delta)
    {
        EntityWorld w = _sim.World; float a = _sim.InterpolationAlpha;   // READ ONLY
        int slot = 0;
        for (int i = 0; i < w.HighWaterMark && slot < Multimesh.InstanceCount; i++)
        {
            if (!w.IsAlive(i)) continue;
            Vector3 pos = w.PrevPosition[i].ToGodotVector3().Lerp(w.Position[i].ToGodotVector3(), a);
            float frac = w.Health[i].ToFloat() / w.MaxHealth[i].ToFloat();   // float PRESENTATION-side only
            Multimesh.SetInstanceTransform(slot++, new Transform3D(
                Basis.Identity.Scaled(new Vector3(frac, 1f, 1f)), pos + new Vector3(0, 3, 0)));
        }
    }
}
```

**ENFORCEMENT** — analyzer: `new JsonSerializerOptions(` / direct `JsonSerializer.` outside `src/Core/Content/`; `Fixed.FromFloat` on any non-`Validated` content model; `[JsonPolymorphic]` project-wide; a `*Bridge` assigning an EntityWorld array or calling a sim mutator; sim `On*` delegate types taking a world-state parameter; `GD.Print`/`using Godot;` in sim. test: single-options test; `NegativeValidationTest` feeds NaN/out-of-range/dangling-ref down each of the five paths and asserts Fail (not throw); `FixedConverterRejectTest`; `ClosedRegistryTest` (every sealed NodeBase in the factory + unknown kind throws); `PhaseOrderTest`; a binder test asserting Bind and Unbind cover the identical `On*` field set.

---

#### N7 — The canonical-model multi-hash handshake

D4/D5. Server is a stateful authority, not a relay.

**RULE — Content hashes are FNV-1a over the CANONICAL MODEL, not file bytes (replaces ScenarioSerializer.cs:59-80 byte-hash): enums by NAME, collections sorted by a stable key, `Fixed` by its int `Raw`, presentation-only annotation fields excluded, fields visited in declared order. NEVER hash a file path for the handshake — hash the in-memory bound model on all five paths (fixes the AI-gen stale-file bug where a generated scenario is never written to `ScenarioPath`).**
WHY — the hash must be invariant to formatting/key-order but sensitive to semantics; it is the desync detector in the `{scenarioHash, rulesetHash, startStateHash}` Ready packet.

**RULE — The Ready packet carries THREE hashes; `startStateHash` is computed PRE-apply from the validated model. Per the wire-width scope decision the wire Ready hashes stay 32-bit (the canonical 64-bit model hash truncated for the wire, matching the existing 32-bit `SimChecksum`); if widened, add `WriteUlong`/`ReadUlong` to `NetworkCommand` and size the buffer accordingly — do not silently mix `WriteUint` with a 64-bit claim.** **Decision (Alec ✅): the wire Ready hashes + the live per-60-tick `SimChecksum` stay 32-bit; only the load-time canonical model hash is 64-bit (truncated for the wire) — least churn for the brownfield strangler. Widening everything to `ulong` is deferred.**

**RULE — The server is a STATEFUL AUTHORITY: it maintains its own canonical scenario/ruleset/startState hashes and its own per-tick checksum collector, and gates `StartGame` on agreement against ITS values — never relays a self-reported hash. (As-built `HandleReady` DedicatedServer.cs:171-191 ignores the Ready payload entirely and gates only on both `_ready` flags — the pattern ADDS hash storage + attestation, and the server must first parse Ready via `TryReadReady`.) `PROTOCOL_VERSION` is compared at BOTH handshake endpoints — the client `TryReadHello` path AND the server `HandlePacket` Hello case (DedicatedServer.cs:135) — refusing the connection on mismatch.**

```csharp
private void HandleReady(int slot, in ReadyHashes peer)
{
    _readyHashes[slot] = peer; _ready[slot] = true;          // store, never relay
    if (_ready[0] && _ready[1])
    {
        if (!_authority.Agrees(_readyHashes[0]) || !_authority.Agrees(_readyHashes[1])
            || !_readyHashes[0].Equals(_readyHashes[1]))
        { _transport.BroadcastReliable(TickCommandPacket.MakeAbort(AbortReason.HashMismatch)); return; }
        _transport.BroadcastReliable(TickCommandPacket.MakeStartGame(startTick: 0));
    }
}
```

**RULE — The server collects every peer's checksum for a tick and takes a strict MAJORITY vote: minority peers get a `DesyncAlert`; no strict majority → HALT the match for everyone. The checksum tick is the EXECUTED sim tick (post-`ApplyOrders`), identical on both peers; stale checksums for non-matching ticks are dropped, not compared. Spectators compute the identical checksum and attest read-only (or are explicitly excluded) — never silently divergent.**

```csharp
private void OnChecksum(int slot, uint tick, uint hash)      // checksum is uint as-built (SimChecksum.cs)
{
    var bucket = _collector.Record(tick, slot, hash);
    if (!bucket.AllPeersReported) return;
    if (bucket.TryMajority(out uint canonical, out var minority))
        foreach (int s in minority) _transport.SendReliableTo(s, TickCommandPacket.MakeDesyncAlert(tick, canonical));
    else { _transport.BroadcastReliable(TickCommandPacket.MakeHalt(tick, HaltReason.NoMajority)); _state = State.Halted; }
}
```

**ENFORCEMENT** — test: `HashStabilityTest` (re-serialize/whitespace-shuffle → identical hash) + `HashSensitivityTest`; handshake tests mismatch each of the three hashes + wrong `PROTOCOL_VERSION` at both endpoints + a lying-client hash (no StartGame); collector tests {all-agree, one-minority, no-majority}; a spoof test asserts the merged bundle re-stamps faction from the authoritative slot. convention: forbid re-introducing transparent Checksum relay; HALT is terminal until a defined recovery policy. [Deferred to implementation (M1): abort/HALT player-facing recovery policy — recoverable rejoin vs terminal — is not derivable from the architecture.]

---

### Standard Patterns

The divergence-point answers, grouped by domain. Each: RULE / WHY / correct code / ENFORCEMENT. Patterns whose full design lives above are cross-referenced, not repeated.

#### Sim Core

**S-CORE-1 — No Dictionary enumeration driving sim order.** RULE: never let `Dictionary`/`HashSet` enumeration order influence the tick (event fire, iteration, selection); store such state in a dense ascending-index array. WHY: enumeration order is unspecified and differs across runtimes — the as-built timer bug (ScenarioDirector.cs:149).
```csharp
for (int slot = 0; slot < _timerRemaining.Length; slot++)           // dense, ascending, deterministic
    if (_timerRemaining[slot] > 0 && --_timerRemaining[slot] == 0)
        events.Add(new FiredEvent(EventKind.TimerExpires, -1, _timerName[slot]));
```
ENFORCEMENT: analyzer flags `.Keys`/`.Values`/`foreach` over `IDictionary`/`HashSet` in sim; golden-checksum replay.

**S-CORE-2 — No unstable sort.** RULE: any in-sim sort must be TOTAL — tie-break on ascending id/index so equal keys have a defined order. WHY: `Array.Sort` is an unstable introsort; equal-priority triggers come out in partition-order, desyncing (ScenarioDirector.cs:192).
```csharp
Array.Sort(order, (a, b) => { int p = _triggers[b].Priority.CompareTo(_triggers[a].Priority);
    return p != 0 ? p : a.CompareTo(b); });   // ascending-index tie-break ⇒ total
```
ENFORCEMENT: analyzer flags `Array.Sort`/`List.Sort`/`OrderBy` in sim lacking a total tie-break; replay over a many-equal-priority scenario.

**S-CORE-3 — No float-string round-trip in the tick.** RULE: compare/pass values as `Fixed` (`Raw`); never `ToFloat()`/`ToString("F2")`/parse — strings never enter sim decisions; author thresholds quantized at load, compared Fixed-vs-Fixed. WHY: the A17 triple non-determinism (float, culture, rounding), ScenarioDirector.cs:168/170. ENFORCEMENT: analyzer flags `ToFloat()`/`ToString()`/`float.Parse`/numeric string concat in sim; replay.

**S-CORE-4 — No wall-clock in sim.** RULE: all sim timing derives from the tick — accumulate `Fixed dt` for durations or count `CurrentTick` for periods; never `DateTime`/`Stopwatch`/`Time.GetTicksMsec` for gameplay. WHY: only tick count and `dt` are identical across peers (30 Hz fixed step). ENFORCEMENT: analyzer flags wall-clock types in sim; replay.

**S-CORE-5 — Sim logging through ILogSink.** RULE: sim/Godot-free types (`SimulationHost`, `ScenarioApplier`, every `ISimSystem`, bridge sim-side helpers) log only through an injected `ILogSink`; never `GD.Print`/`Console`/`Debug`. Presentation Godot Nodes may use `GD.Print`. WHY: `GD.Print` requires `using Godot;`, breaking the pure-C# boundary and the AOT server build. ENFORCEMENT: analyzer flags `GD.Print`/`Console`/`using Godot;` in sim; convention: `ILogSink` ctor-injected, never static. [Deferred to implementation (M1): `ILogSink` method set (Debug/Info/Warn) and structured-arg shape — Fixed.Raw ints vs interpolated message — is net-new and undefined.]

**S-CORE-6 — SimChecksum mixes integer `.Raw` only.** RULE: `Mix` consumes one `int` — pass `Fixed` as `.Raw`, enums as `(int)`, bools as 1/0; never a float/`GetHashCode`/string. WHY: `Fixed.Raw` is the platform-independent integer truth. ENFORCEMENT: convention + coverage-guard. [Deferred to implementation (M1): whether the coverage-guard enumerates EntityWorld arrays by reflection or a hand-maintained store registry — pick one as the single source of truth.]

#### Effects / DSL

**S-FX-1 — TargetFilter is OR-able `[Flags]` evaluated against ascending-id candidates.** RULE: `TargetFilter` is a `[Flags] enum`; `SearchArea` gathers candidates ascending-id into a pre-allocated (per-depth) buffer, then admits matches — distance uses `Fixed`, never reorders the set. WHY: OR-able flags compose any predicate; ascending-id admission keeps the hit order deterministic (distance-sort is float + nondeterministic). ENFORCEMENT: convention + filter-composition / candidate-order fixtures.

**S-FX-2 — Persistent effect = a Modifier with an integer-tick lifetime.** RULE: a `Persistent` node registers a Modifier with `remaining = (int)(authoredSeconds * TICKS_PER_SECOND)` computed at load; `ModifierSystem` decrements and removes at zero — never float seconds summed by `dt`. WHY: integer-tick lifetimes are exactly reproducible and align with the 30 Hz step. ENFORCEMENT: test (a 2s buff at 30 tps expires on exactly tick 60); convention (seconds→ticks at load only).

**S-FX-3 — Energy/Mana are new SoA arrays mutated only through effects.** RULE: `Energy`/`MaxEnergy` are parallel `Fixed[]` (the N1 idiom), mutated only via `EffectNode`s, folded into SimChecksum. WHY: parallel arrays match the SoA contract; any unfolded per-entity field is a latent desync. ENFORCEMENT: coverage-guard; convention (no per-entity classes). [Deferred to implementation (M1): Energy regen model — per-tick vs effect-only — decides whether a regen pass belongs in `ModifierSystem`, a new `EnergySystem`, or nowhere.]

**S-FX-4 — Authored float→Fixed conversion is load-time only.** RULE: `Fixed.FromFloat` runs only during deserialization/Validate; by `Apply` every magnitude is a frozen `Fixed` field. WHY: in-tick `FromFloat` reintroduces float into deterministic math (A17-class). ENFORCEMENT: analyzer (FromFloat in tick-reachable `src/Effects`/`src/Dsl` flagged); Validated<T> gate.

**S-FX-5 — Named structural caps live in `EffectCaps`/`DslCaps` and the rulesetHash corpus.** RULE: every cap (`MaxEffectDepth=8`, `MaxEffectFrames`, `MaxIterationsPerTick`, `MaxRaisesPerTick`, `ForEachBatchSize`, `MaxHitsPerSearch`) is a named `const` referenced everywhere and folded into the rulesetHash — never an inline literal. WHY: peers must agree on caps before the match; a literal can't be hashed. ENFORCEMENT: rulesetHash-corpus test; analyzer flags bare cap literals at use sites. _(distinct from `MaxLoopIterations`≈256, the per-loop iteration cap; `MaxIterationsPerTick`≈4096 is the per-tick total-iteration fuel budget)_

[Deferred to implementation (M1): the `Area` representation on `EffectContext` (center+radius `Fixed` vs precomputed candidate buffer); the Modifier stat-coverage set and stacking rule (additive/multiplicative/max); the `DslValue` union shape (tagged struct vs typed ports). These are net-new design forks the type-checker and ModifierSystem depend on.]

#### Multiplayer

**S-MP-1 — Add a network command (struct + apply-at-`currentTick+delay`).** RULE: a command is a fixed-size readonly struct serialized into the `TickCommandPacket` order stream and APPLIED ONLY inside the shared applier at `execTick == currentTick + delay`; the issuer never mutates sim — `EnqueueOrder` buffers (returns false online). WHY: lockstep requires the identical command set at the identical tick on every peer.
```csharp
bool appliedNow = _lockstep.EnqueueOrder(unitId, UnitCommand.AttackMove, targetX, targetZ); // Fixed already
```
ENFORCEMENT: test asserts online `EnqueueOrder` returns false and never mutates `_world`; golden-checksum replay.

**S-MP-2 — One shared `ApplyOrders`; keep all apply sites in sync (the 4-site rule).** RULE: the per-order switch lives in ONE shared, public, Godot-free applier extracted from the private `LockstepManager.ApplyOrders(buf, baseIdx, count, faction)` (LockstepManager.cs:594); all four live sites (online-local/remote, spectator-P1/P2) plus `ReplayPlayer` route through it — never fork per site. EVERY command case re-validates `IsAlive(id)` AND `FactionOf[id] == expectedFaction` (the last line of defense even after server re-stamping). WHY: replays/spectators re-apply the same orders; a forked site breaks the golden checksum.
```csharp
internal static void ApplyOrder(EntityWorld world, in UnitOrder o, Faction expectedFaction, …callbacks)
{
    int id = o.UnitId;
    if (!world.IsAlive(id)) return;
    if (world.FactionOf[id] != expectedFaction) return;        // mandatory anti-cheat guard, every case
    world.CommandState[id] = o.Command;                        // the ONE switch
}
```
ENFORCEMENT: replay-determinism harness (byte-identical per-60-tick checksum stream); spectator-parity test.

**S-MP-3 — Bump `ReplayRecorder.VERSION` on any command-semantics change.** RULE: any change to command serialization, the `UnitCommand` vocabulary, or applier semantics bumps `ReplayRecorder.VERSION`; `ReplayPlayer` refuses a file whose version differs. WHY: a replay is a checksum-gated golden; reinterpreting old bytes under new rules is a silent desync. ENFORCEMENT: CI test (touching `UnitOrder`/`UnitCommand`/applier without a bump fails a semantic-surface hash); fail-closed load.

**S-MP-4 — Input-delay clamp [2,12] committed on an agreed tick.** RULE: delay is `Math.Clamp(ticks+1, MIN_DELAY=2, MAX_DELAY=12)`; a change takes effect only at the negotiated `applyTick` via `DelayProposal`. `BUFFER_SIZE` is a power of two strictly greater than `MAX_DELAY+1` (16 > 13 as-built). WHY: both peers must use the identical `_currentDelay` and switch at the identical tick or the circular buffers alias. ENFORCEMENT: test asserts output in [2,12] across an RTT sweep + a static BUFFER_SIZE > MAX_DELAY+1 assertion; two-peer test asserts same delay at same tick.

**S-MP-5 — Off-tick wall-clock isolation.** RULE: wall-clock and the float RTT EWMA may influence ONLY `ComputeTargetDelay`'s proposed value, which rides the deterministic `DelayProposal` and commits on a tick (`CommitDelayChange`, the sole writer of `_currentDelay`); the float RTT must never index `_localBuf`/`_remoteBuf`, set `_currentDelay` directly, or appear in the applier. WHY: RTT is a presentation-side hint; the schedule is purely tick-based. ENFORCEMENT: analyzer flags `Time.GetTicksMsec`/`DateTime` in `src/Core` sim and the applier; test asserts `CommitDelayChange` is the sole `_currentDelay` writer.

**S-MP-6 — Stall detection is tick-counted, not wall-clock.** RULE: when remote commands never arrive, `Flush` stalls; declare a drop/abort after N stalled `Flush` calls (a tick count), never a wall-clock timeout, and trigger a deterministic Abort — never a silent continue. WHY: a wall-clock timeout fires at different real times per peer. ENFORCEMENT: test simulates a dropped peer and asserts deterministic Abort after N stalls.

**S-MP-7 — Faction→Player8 is a localized bump; reference factions only via `FactionRegistry`.** RULE: add `Player5..Player8` to the enum and bump `FactionRegistry`; size faction-indexed arrays via `FACTION_ARRAY_SIZE` and index by `(int)faction`; iterate slot loops via `FactionRegistry.ToFaction(slot)`. No literal `Player1`/`Player2`, `(slot+1)` cast, `[5]`/`[2]`, or `< 2` loop anywhere else. The server `SLOT_FACTION` map (DedicatedServer.cs:42) and the merged `TickCommandsMerged` RE-STAMP each order's faction from `SLOT_FACTION[fromSlot]` (the client faction byte is advisory). WHY: faction count is part of the deterministic contract (checksum coverage, slot map, sizing); scattered literals leave one site at 2 and drop a faction from the hash; the arrival slot is the only trustworthy identity.

*Critical fix — `FACTION_COUNT` has two meanings as-built and they must not collide.* Today `FACTION_COUNT = 5` (ResourceStore.cs:9, MatchStats.cs:14) is the ENUM CARDINALITY including Neutral, and per-faction arrays are sized to it and indexed by `(int)Faction` (slot 0 = Neutral). `FactionRegistry` exposes BOTH explicitly:
```csharp
public static class FactionRegistry
{
    public const int PLAYER_COUNT = 8;                         // Player1..Player8, for slot loops
    public const int FACTION_COUNT = PLAYER_COUNT;             // alias: number of PLAYABLE factions (excl. Neutral)
    public const int FACTION_ARRAY_SIZE = PLAYER_COUNT + 1;    // +1 for Neutral=0, for (int)Faction-indexed arrays
    public static Faction ToFaction(int slot) => (Faction)(slot + 1);   // the ONE (slot+1) cast
    public static int SlotOf(Faction f) => (int)f - 1;
}
// Per-faction SoA stores: new Fixed[FactionRegistry.FACTION_ARRAY_SIZE], indexed by (int)faction (Neutral=0 reserved).
// Slot iteration: for (slot in 0..PLAYER_COUNT-1) faction = ToFaction(slot).
```
Analyzer rule: ban bare `FACTION_COUNT` in new faction-iteration loops — use `PLAYER_COUNT` (playable, excl. Neutral) or `FACTION_ARRAY_SIZE` (sized incl. Neutral). ENFORCEMENT: analyzer flags literal `Faction.PlayerN`, raw `(slot+1)`, and magic per-faction array sizes outside `FactionRegistry`; test asserts every faction-indexed array length == `FACTION_ARRAY_SIZE` and the checksum/seed/threshold loops cover all `PLAYER_COUNT` slots. [Deferred to implementation (M1): whether the production server runs >2 players — decides true majority quorum vs a 2-peer strict-equality fast path; and the spectator checksum-attestation model.]

#### Content / Config / Tooling

**S-CON-1 — `schema_version` + `min_game_version` + JsonNode-DOM migration chain.** RULE: every top-level document carries integer `schema_version` + `min_game_version`; `ContentLoader` runs the ordered `vN→vN+1` migration chain on the loose DOM up to `CURRENT` before binding, and HARD-REFUSES a document whose `min_game_version` exceeds the engine. WHY: migrations run on the DOM (the typed model only knows CURRENT) in a deterministic chain so every peer lands on identical bytes; `min_game_version` turns "mis-load a future file" into an explicit refusal. ENFORCEMENT: `MigrationChainTest` (golden vN → byte-identical CURRENT; chain length == CURRENT); `MinGameVersionTest`; convention (bumping a model shape requires CURRENT++ + a chain entry in the same commit).

**S-CON-2 — Named caps as the rulesetHash corpus.** RULE: every constant two peers must agree on (`MaxEffectDepth`, `MAX_ENTITIES`, checksum cadence 60, input-delay [2,12], the `DamageMatrix` entries) lives as a named field in `SimConstants` and is folded into `RulesetHash`; literals at call sites are forbidden. The `DamageMatrix` fold iterates the real enum dims — `DamageType.COUNT` (4) × `ArmorType.COUNT` (5) — against the **static** `DamageMatrix.Get`, not invented `TypeCount`/`ArmorCount` members.
```csharp
for (int d = 0; d < (int)DamageType.COUNT; d++)
    for (int a = 0; a < (int)ArmorType.COUNT; a++)
        h.Fold(DamageMatrix.Get((DamageType)d, (ArmorType)a));   // static, returns Fixed → folded by .Raw
```
WHY: a constant not in the corpus is an invisible desync axis. ENFORCEMENT: `RulesetCorpusTest` reflects over `SimConstants` and asserts each field is referenced by `RulesetHash`; analyzer flags magic-number literals matching a known cap.

**S-CON-3 — Referential integrity + combined content hash at import.** RULE: dangling `NamedEffectReference`/`resourceId`/faction ids are rejected at load (Validated<T> → Err); the `scenarioHash` for the handshake folds ALL gameplay files (scenario + faction DTOs + catalog + registry), not a single model. WHY: cross-file dangling refs and partial-corpus hashes are load-bearing for the D4/D5 handshake. ENFORCEMENT: negative fixtures for each dangling-ref class; a combined-hash test over a multi-file corpus.

**S-CON-4 — Two-algorithm hash split.** RULE: keep the package zip BYTE-hash (tamper check, hashed PRE-save) decoupled from the canonical-MODEL hash (handshake/replay); each carries its own `checksum_algo_version`. WHY: unifying options must not break existing `.chimera.zip` unpack, and the handshake needs the format-invariant model hash — the two cannot be the same algorithm. ENFORCEMENT: golden gate — an existing package round-trips its byte-hash while its model hash is computed independently. **Decision: FNV-64 (per D3)** — sufficient for accidental-desync detection; a cryptographic digest (SHA-256) is a post-1.0 option only if the D5 authority later needs tamper-resistance (threat-model call).

**S-CFG-1 — Config placement: GameConfig vs SettingsData vs ISecretStore.** RULE: per-match fixed inputs → the immutable `GameConfig` DTO (part of the hash corpus when sim-affecting); user-tunable persisted prefs → versioned `SettingsData`; secrets (LLM keys) → ONLY `ISecretStore` over gitignored `user://secrets/llm.key`. An `[Export] string` holding a key is banned. WHY: three lifetimes, three homes; a key in an `[Export]`/scene is a leak, a tunable in the hash corrupts agreement.
```csharp
public sealed record GameConfig(int MatchSeed, int InputDelayTicks, string ScenarioId);   // per-match, immutable
public sealed class SettingsData { public int SchemaVersion { get; init; } = 3;
    public string LlmProvider { get; init; } = "anthropic"; public float MasterVolume { get; init; } = 0.8f; } // float OK: presentation
public interface ISecretStore { string? Get(string key); }   // backed by gitignored user://secrets, never [Export]
```
ENFORCEMENT: `SecretExclusionTest` (an `[Export] string` named *key*/*secret*/*token* is banned); `SettingsData` schema-version round-trip; secrets dir gitignored. **Decision: plaintext floor (per D6)** — raw key text in gitignored `user://secrets/llm.key` behind the 1-method `ISecretStore` seam; DPAPI(Win)/libsecret(Linux) drop in behind the same seam post-1.0.

**S-LLM-1 — Godot-free `ILLMProvider`, authoring-layer only.** RULE: `ILLMProvider.GenerateAsync(NormalizedRequest, ct) → NormalizedResult` with 3 adapters; provider/model in versioned `SettingsData`; AI output validated by the SAME D3 gate with float→Fixed quantize BEFORE the canonical hash; ZERO sim coupling. WHY: AI is an authoring tool — it produces content that must pass the identical fail-closed gate, never touches the tick. ENFORCEMENT: analyzer (no LLM types referenced from sim); negative-validation over AI-gen fixtures.

#### Testing

**S-TEST-1 — Tier-1 Godot-free golden-checksum replay.** RULE: sim logic is tested in `ProjectChimera.Sim.Tests` (plain .NET, **xUnit** — Alec's call) by constructing `SimulationHost`, applying a recorded order stream via the shared applier + `StepOnce()`, and asserting the final `SimChecksum` equals a committed golden by EXACT `uint` equality — never float epsilon. The checksum is `uint` as-built (`SimChecksum.Compute` → `uint`, `LastChecksum` is `uint`, `OnChecksum` is `Action<uint,uint>`); if D4 widens the world-state hash to 64-bit, `SimChecksum`, the Checksum packet, and `SendChecksum` move to `ulong` together. The shared applier (S-MP-2) is extracted to a public Godot-free signature so the test, `LockstepManager`, and `ReplayPlayer` all call it.
```csharp
var host = SimulationHost.Create(matchSeed: 12345);
uint captured = 0;
host.SetChecksumSink((uint tick, uint checksum) => captured = checksum);   // uint, matches as-built
var orders = ReplayFixture.Load("fixtures/skirmish.chmr");
for (int t = 0; t < orders.TickCount; t++) { host.ApplyOrders(orders.At(t)); host.StepOnce(); }
Assert.Equal(0xDEADBEEFu, captured);   // exact golden; regen only on intended change
```
ENFORCEMENT: CI `dotnet test`; convention: no `using Godot;`, exact `uint` equality.

**S-TEST-2 — Tier-2 GdUnit4 presentation/integration.** RULE: anything touching Godot types (bridges, phases, scene wiring) is a GdUnit4 test in `godot/tests`; pure-sim determinism is NEVER tested here. WHY: GdUnit4 needs the Godot runtime (the only place presentation runs); determinism must stay in the fast, AOT-portable Tier-1. ENFORCEMENT: convention + CI runs the GdUnit4 suite separately.

---

### Consistency Rules Table

| Pattern | Convention | Enforcement |
|---|---|---|
| Per-entity field | New `T[] = new T[MAX_ENTITIES]`, reset in `Create()` (no FromFloat), folded into SimChecksum | test: SoA coverage-guard |
| Entity iteration | `for (id=0; id<HighWaterMark; id++) if(!IsAlive) continue` ascending | analyzer + convention |
| Fixed conversion | `Fixed.FromFloat` only in `FixedJsonConverter` (+ the AI quantize step); model fields are already `Fixed`, so `ScenarioApplier` does no conversion; tick is Fixed-only | analyzer (allow-list `FixedJsonConverter` only) |
| Randomness | shared `SimRng`; sort candidates ascending-id then draw; global draw order = sys-reg→slot→id | analyzer (bans System.Random/Godot RNG) + replay |
| SimChecksum | fold every truth array (`.Raw`) + all faction slots; `[ChecksumExempt]` for PrevPosition; `Mix` internal | test: coverage-guard |
| New ISimSystem | `Tick(EntityWorld, Fixed)`, registered in `SimulationHost` at its contractual slot (ModifierSystem before CombatSystem) | test: SystemOrderTest |
| Effect leaf | one sealed `EffectNode` + `Apply(in)`/`Validate`, allocated at load, alloc-free | analyzer + EffectRegistryCoverageTest + alloc-probe |
| Effect composition | work-stack executor, depth=8, per-depth SearchArea buffers; no recursion | analyzer + depth/order fixtures |
| EffectContext | `readonly struct` (not ref), re-rooted via `With*`, `in` everywhere; Rng/Sink shared | analyzer + RNG-order test |
| Damage | single `DamageResolver.Apply`; formula `base * DamageMatrix.Get(...)` | analyzer (no other `Get` multiply) |
| Modifier | SoA Base*/Effective* + dirty flag, recomputed before combat | test: stack/remove + timing |
| DSL nodes | dense-index store, ascending iteration, persistent ids, exec+typed-data edges | test: type-check + id-uniqueness |
| DSL iteration | ForEach/ForEachBatched over ascending-id snapshot + fuel; no While/recursion | analyzer (closed registry) + fuel/snapshot fixtures |
| DSL expressions | CEL-shaped, `Fixed`-only, no float/string | analyzer + Fixed-equality fixture |
| DSL events | closed structs, acyclic registry, FIFO ascending-handler-id drain | test: cycle-detect + worklist-order |
| DSL variables | top-level `DslVarTable`, dense-index, folded into SimChecksum | test: coverage-guard |
| UI read rail | double-buffered version-stamped `DslVarReadback`, published once end-of-StepOnce; format presentation-side | analyzer + version/no-string fixtures |
| UI write rail | `Pressed` enqueues `DslEventCommand` on lockstep bus; authorized at apply | analyzer + authz/replay fixtures |
| sim→presentation reader | Godot `*Bridge`, reads SoA, interpolates by `InterpolationAlpha`, never writes sim | analyzer + convention |
| presentation effect seam | fire-only `On*` / single `IEffectPresentationSink`, assigned only by `ScenarioDelegateBinder` (bind+unbind) | test: single-assignment + binder coverage |
| Network command | fixed struct, apply at `currentTick+delay`; shared applier; alive+faction re-check every case | test: replay + spectator parity |
| Replay format | bump `ReplayRecorder.VERSION` on any semantics change; fail-closed load | CI semantic-surface test |
| Input delay | `Clamp(ticks+1,2,12)`, commit on agreed tick; `BUFFER_SIZE > MAX_DELAY+1` | test: clamp sweep + buffer invariant |
| Stall | tick-counted, deterministic Abort, never wall-clock | test: dropped-peer |
| Faction/slot | `FactionRegistry.ToFaction`/`SlotOf`; arrays sized `FACTION_ARRAY_SIZE`, indexed `(int)faction` (Neutral=0); server re-stamps from slot | analyzer + array-length test |
| Handshake | 3-hash Ready, canonical-model hash, server attests/gates, majority-vote HALT, PROTOCOL_VERSION both ends | test: handshake mismatch + collector + spoof |
| Content JSON | one `ContentJson.Options` via `ContentLoader`; Disallow; closed NodeBase registry; no `[JsonPolymorphic]` | analyzer + single-options + ClosedRegistryTest |
| Content gate | `Validated<T>` (internal ctor), all five paths; no FromFloat before Ok | analyzer + NegativeValidationTest |
| Content hash | FNV over canonical model (enums by name, sorted, `.Raw`); hash model not file path; 2-algo split | test: HashStability/Sensitivity + byte-hash golden |
| schema_version | integer + migration chain + `min_game_version` refusal | test: MigrationChainTest + MinGameVersionTest |
| Named caps | `SimConstants`/`EffectCaps`/`DslCaps`, folded into rulesetHash; no literals | test: RulesetCorpusTest + analyzer |
| Config | GameConfig (fixed) / SettingsData (versioned) / ISecretStore (secret); no `[Export]` key | analyzer: SecretExclusionTest |
| Sim logging | `ILogSink` injected; never `GD.Print`/`Console` in sim | analyzer |
| ContentError | `{Source,JsonPath,Message}` in `LoadResult<T>`; never null/throw/swallow | analyzer + convention |
| Composition root | `ISetupPhase[]` + `ScenePhaseRunner` order literal, ctor-injected deps | test: PhaseOrderTest |
| Sim test | Tier-1 golden-checksum replay, exact `uint` equality, Godot-free | CI `dotnet test` |
| Presentation test | Tier-2 GdUnit4 in `godot/tests`; no determinism asserts | convention + CI |

### Naming Conventions

The base C# casing rule lives in `CLAUDE.md` / `project-context.md`: PascalCase for classes/methods/public members; camelCase for locals; SCREAMING_CASE for consts; a `PascalCase.cs` filename equals its class name; sources live under `godot/src/<System>/`.

The doc-specific idioms are consolidated in the Consistency Rules Table above: `*Bridge` (sim→presentation readers), `*System`/`*Store` (sim systems and SoA stores), `*Phase` (composition-root setup steps), paired `Base*` + `Effective*` stat arrays, `Max*`/`MIN_*`/`MAX_*` caps, `*Test` (Tier-1 tests), and the `S-`/`N-` pattern ids.

### Deferred to implementation (M1) — tracked forks

The catalog is complete at the architecture altitude. A handful of NET-NEW design forks are genuine
**implementation-time** leaf choices (they do not change any pattern's *shape*) — recorded here so none is
silently lost; each carries a recommended default consistent with the decided architecture:

- **`SimRng` concrete API/state** — method names (`NextInt(count)`/`NextFixed()`/`NextRaw()`) + where the seed/`RawState` lives (recommend a small `SimState` field exposed to `SimChecksum`).
- **`ILogSink` method set + arg shape** — `Debug/Info/Warn` with `Fixed.Raw`-int structured args (no interpolated strings in sim).
- **`SimChecksum` coverage-guard mechanism** — recommend a hand-maintained explicit store registry as the single source of truth over reflection.
- **`EffectContext.Area` representation** (center+radius `Fixed` vs precomputed candidate buffer); **Modifier stat-coverage set + stacking rule** (D1 already names `Speed/AttackDamage/AttackSpeed/MaxHealth/armorBonus` + `Refresh|Stack|Ignore`); **`DslValue` union shape** (tagged `readonly struct` vs typed ports).
- **Energy/Mana regeneration model** (per-tick rate vs effect-only) — decides `ModifierSystem` vs a new `EnergySystem` vs nowhere.
- **Cross-faction same-tick event tie-break** — ascending faction slot (recommended default; matches the ascending-id mandate) vs command-bus arrival order.
- **Server >2-player quorum** — true majority (D5 ships this) vs a 2-peer strict-equality fast path (optimization only); spectator checksum-attestation model.
- **Abort/HALT player-facing recovery policy** — recoverable rejoin vs terminal (a UX call, tracked with the lobby/UX work).
- **`DslEvent` authorization granularity** (faction-only vs a per-scenario role table); **`DslVarReadback` publish granularity** (recommend a declared `MaxPublishedVars` subset over all-vars); **`ContentJsonContext` source-gen split**; **`Validated<T>` internal-ctor assembly boundary** (`InternalsVisibleTo` the test project).

---

## Presentation & UGC-Publish Coverage (Step 8 Addendum)

> Step-8 validation surfaced five 1.0-scope requirements (FR-12a, FR-36, FR-37/FR-38a, FR-49a, plus
> creator binary-asset import; GDD §7.1/§7.2/§7.3/§8.4) that had **no architectural home**. All are
> **presentation / IO domain** — none enters the deterministic 30 Hz tick or any `SimChecksum` /
> canonical-model hash. They are homed here as concrete decisions (Alec's call 2026-06-21: home all five
> now); depth tuning is deferred to their milestones. Each closes the matching validation finding.

### P1 — Art-style consistency layer (FR-49a / GDD §8.4)

**Decision — two in-engine mechanisms.** (a) A **shared `StandardMaterial3D` preset library** (a
`resources/` `.tres` resource set of a few canonical materials) applied via the **MultiMesh
material-override** path, so every unit/building mesh — first-party or creator-generated — shares lighting,
roughness, and rim characteristics. (b) **One global post-process pass** via Godot `WorldEnvironment` + a
single screen-space shader (cel-shading bands + color-correction/tonemap) so the whole scene reads as one
coherent style regardless of per-asset variation. This is the **in-engine consistency stage that runs
AFTER the external AI-art generation pipeline** (Hunyuan3D/Tripo + LoRA stay out of runtime scope). 1.0
baseline = the post-process shader + the preset library; deeper style-transfer is external. **Home:**
presentation/rendering — preset library is a `resources/` asset; the post-process shader lives with the
main scene's `WorldEnvironment`. **Determinism:** none.

### P2 — Runtime binary-asset ingest (GDD §7.2; `UnitDefinition` model refs)

**Decision — a runtime asset-loading path distinct from the editor import pipeline.** Shipped / dedicated
builds load creator-supplied `.glb` via **`GLTFDocument.AppendFromFile` → `GenerateScene`** (NOT
`GD.Load<PackedScene>`, which only resolves editor-pre-imported `res://` assets), textures via
`Image.LoadFromFile` + `ImageTexture.CreateFromImage`, and audio via `AudioStreamOggVorbis.LoadFromFile`.
Assets are extracted from `.chimera.zip` into a `user://` cache. Each asset is **validated alongside the
content hash**: extension allow-list, max file size, max texture dimensions, mesh vertex/submesh caps; a
missing/invalid asset resolves to the existing **box placeholder** (`MeshLoader` already does this) and
**never crashes the load**. An **`AssetRegistry`** maps a logical asset id → the runtime-loaded mesh and
registers it into the MultiMesh; `UnitDefinition.model` references resolve through it. **Home:**
`ProjectChimera.UGC` (ingest + validation) + a presentation `AssetRegistry`. **Critical:** this path is
required for **any non-editor build** to show custom art — exported first-party builds and Alec's own
locally-generated Iron Pact GLBs included, not just downloaded UGC. **Determinism:** none (assets never
enter the tick); asset bytes **are** folded into the content hash, so peers still verify identical content.

### P3 — `CombatFeedbackProfile` (FR-12a)

**Decision — a `CombatFeedbackProfile` DTO** in `src/Core/Definitions/` (sibling of `UnitDefinition`): a
per-unit/per-ability bundle `{ hitParticleId, impactSoundId, shake{intensity, durationTicks},
hitFreezeFrames, deathEffectId, deathSoundId }`. A **tuned default ships as data**; units/abilities
reference a profile id and may override. It serializes through D3's `ContentLoader` / `Validated<T>` like
every other definition and is flagged **presentation-domain → excluded from `SimChecksum` and the
canonical-model hash** (per the D1 domain rule). At runtime it drives the D1 presentation leaves
(`PlayVfx`/`PlaySound`/`ShakeScreen`) via the `On*` seam. **`hit-freeze` is a presentation-timeline
concern** (a brief render-side pause/scale on the hit reactor) — it is **not** an effect node and must
never pause the sim tick. **Home:** Definitions (DTO) + `CombatFeedbackBridge` (upgraded from hardcoded to
profile-driven). **Determinism:** none.

### P4 — Proof-of-play + pre-publish quality gate (FR-36 / GDD §7.3)

**Decision — capture proof-of-play from the existing D1 `Victory` control leaf.** When a creator wins
their own scenario in a non-multiplayer test run, a **signed completion token** (scenario canonical hash +
outcome + timestamp) is written into the `.chimera.zip` **manifest** (D3 owns the manifest). The mod.io
publish path in `ProjectChimera.UGC` **refuses upload** unless the manifest carries a valid token whose
hash matches the package, plus the GDD §7.3 minimum-quality fields (thumbnail, description ≥ 100 chars,
≥ 1 screenshot). Enforcement is **client-side at publish** (1.0 posture — premium title, low abuse
surface); server-side re-validation is a post-1.0 option. **Home:** `ProjectChimera.UGC` (publish flow) +
D3 manifest. **Determinism:** none.

### P5 — Content browser + IP-ownership surfacing (FR-37 / FR-38a)

**Decision.** (a) Browse / search / tag-filter / sort / subscribe / rate **delegate entirely to
mod.io-native features** — **no parallel rating/search system is built**; the content browser is a
presentation view over mod.io REST responses (`ProjectChimera.UGC.ModIoService`, raw `HttpClient`).
(b) **FR-38a** (creator retains ownership; the platform takes only a **non-exclusive host/distribute
right**) is surfaced as an explicit **consent checkbox in the publish flow**, recorded in the manifest,
and **required before upload**. **Home:** `ProjectChimera.UGC` (REST) + CreationSuite content-browser /
publish UI. This also gives mod.io REST integration the depth GDD §7.1 prescribes (closing the related
minor finding). **Determinism:** none.

### Traceability note — framework command vocabulary (GDD §3.4)

The GDD §3.4 non-negotiable command set (**Move, Attack-Move, Patrol, Stop, Hold, Follow, Rally**) is the
framework command vocabulary carried by the lockstep command bus (`EnqueueOrder`). As-built `UnitCommand`
covers **Move / AttackMove / Stop / HoldPosition / Build**; **Patrol, Follow, and Rally-as-a-unit-command
are pending** — mechanical `enum` + system extensions over the already-architected command seam, no new
architecture required.

---

## Architecture Validation

### Validation Summary

| Check | Result | Notes |
|---|---|---|
| Decision Compatibility | ✅ PASS | D1–D7 cohere; every cross-decision conflict (spawn cap, hash width, SimRng timing, Fixed-end-to-end, ref-vs-readonly `EffectContext`) reconciled to one answer. The one determinism-relevant item — the `ScenarioApplier` example vs the "Fixed end-to-end" call — corrected. |
| GDD Coverage | ✅ PASS | All sim/gameplay systems homed; the 5 visual + UGC-publish gaps homed in the *Presentation & UGC-Publish Coverage* addendum. |
| PRD FR/NFR Coverage | ✅ PASS | All 6 NFRs + headline FR clusters covered; FR-12a / 36 / 37 / 38a / 49a + binary-asset import homed. |
| Pattern Completeness | ✅ PASS | 67 patterns (7 novel + standard + Consistency Rules table), each with a determinism-safe example + analyzer/test/convention enforcement. |
| Document Completeness | ✅ PASS | Decision Summary table + Naming Conventions cross-ref added; stale ❓ / step-counter / version-pin / symbol issues fixed. |
| Brownfield Code-Claims | ✅ PASS | 11/12 as-built `file:line` claims confirmed exactly against `godot/src/`; 2 citation-precision slips fixed. The cross-cutting remediation rests on real bugs. |

**Overall Status: PASS** — the architecture is coherent, complete, and carries no determinism or correctness
blockers. Ready to guide implementation (epics/stories).

### Coverage Report

- **Systems covered:** all GDD core systems + the 5 presentation/UGC-publish gaps now homed.
- **Patterns defined:** 67 (7 novel + standard catalog + Consistency Rules table).
- **Decisions made:** Engine (Godot 4.6.3) + Runtime (.NET 8) + D1–D6 + Step 5/6/7 + 5 homed in Step 8.
- **Findings:** 30 raw → **22 confirmed / 8 refuted** (every finding adversarially refuted before counting).

### Method

Run as a 6-dimension fan-out — decision compatibility, GDD coverage, PRD FR/NFR coverage, pattern
completeness, document completeness, and **brownfield code-claim verification against `godot/src/`** — with
each finding independently refuted before it counted (36 agents). The 8 refuted findings included a
misdiagnosed terrain-texture "gap" (whose proposed fix pointed at the wrong layer), a claim that the Step-7
effect caps weren't corpus-validated (they are, in S-FX-5), and several "missing" items already covered
elsewhere in the doc.

### Issues Resolved (this pass)

- **6 majors.** The `ScenarioApplier` Fixed-end-to-end example contradiction (corrected to the decided
  convert-at-parse boundary); and 5 presentation/UGC-publish coverage gaps — FR-49a art-style layer, runtime
  binary-asset ingest, FR-12a `CombatFeedbackProfile`, FR-36 proof-of-play gate, FR-37/FR-38a content-browser
  + IP-consent — all homed in the *Presentation & UGC-Publish Coverage* addendum.
- **~10 minors / ~6 nits (doc accuracy).** D1 `ref struct` prose corrected to non-ref; `ReplayFormat.REPLAY_VERSION`
  → `ReplayRecorder.VERSION`; spawn-cap claim corrected (two sites — one `Math.Min`, one `Math.Clamp`);
  `LoadScenario.cs` → `ScenarioSerializer.LoadFromFile`; `:149` Dictionary citation clarified (timers vs
  `_variables`); stale `❓` markers flipped to ✅; stale Step-4 step-counter banner updated; `NakamaClient 3.13.0`
  pinned; `MaxIterationsPerTick` vs `MaxLoopIterations` disambiguated; `FACTION_COUNT` slot-loop switched to
  `PLAYER_COUNT` + analyzer rule noted; consolidated **Decision Summary** table and **Naming Conventions**
  cross-ref added.

### Carried Forward (not blockers — for sprint planning)

The M1 implementation-time leaf forks already tracked under *Deferred to implementation (M1)*. Two are
**checksum-relevant** — **cross-faction same-tick event tie-break** and **server >2-player quorum** — and
must be **pinned before their subsystem ships** (each carries a recommended default). Promote both to explicit
story-entry gates in `gds-sprint-planning`.

### Validation Date

2026-06-21

---

## Development Environment

### Prerequisites

- **Godot 4.6.3** (.NET / Mono build) — pinned for 1.0; 4.7 deferred to post-1.0.
- **.NET 8 SDK** (desktop `net8.0`).
- **Addons:** `godot_mcp` (dev-time AI tooling — **not shipped** in the 1.0 build) and `terrain_3d`
  (Terrain3D editor). Verify both still connect after the 4.6.3 bump.
- **Sole NuGet dependency:** `NakamaClient 3.13.0`.

### AI Tooling (MCP Servers)

| MCP Server | Purpose | Install |
|---|---|---|
| **godot-mcp** (in use — keep) | Live scene/node/animation edits, `runtime_state` digests, profiler, `validate_meshes`, input injection, frozen-time stepping, `godot_docs` | `MCPGameBridge` autoload (`addons/godot_mcp`) — already installed |
| **Context7** (optional) | Current .NET / NuGet / library docs (Nakama, System.Text.Json) | `claude mcp add context7 -- npx -y @upstash/context7-mcp` |

Dev-time only — neither ships in the 1.0 build.

### Build / Run

```bash
# Entry: project.godot → res://scenes/main.tscn → MainScene.cs._Ready()
dotnet build godot/godot.sln          # build the C# solution
# Run: open the project in the Godot 4.6.3 .NET editor and press Play (F5), or export + run.
# Dedicated/headless server is detected at runtime via DisplayServer.GetName() == "headless".
```

### First steps toward implementation (M1 foundation — build in this order; the D1 strangler depends on it)

1. Stand up the **Tier-1 golden-checksum harness** (xUnit, Godot-free) — pins current sim behavior before any change.
2. Build **`SimRng`** (seeded, deterministic), the **generalized `SimChecksum`** (all active factions), and the **canonical-model start-state hash**.
3. Add the **banned-API analyzer** (warn-first on master) and the `SimulationHost` + `ScenarioApplier` + fail-closed `ScenarioValidator` spine (Step 6, Steps 0–6).
4. Then strangle the effect / DSL / hero / multiplayer systems behind their individual golden-checksum gates (the D1–D6 migration sequences).
5. Configure MCP servers (above) so AI agents have live `4.6.3` context.

