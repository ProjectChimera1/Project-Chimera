---
title: 'Game Architecture'
project: 'Project_Chimera'
date: '2026-06-20'
author: 'Alec'
version: '1.0'
stepsCompleted: [1, 2]
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

**Steps Completed:** 2 of 9 (Project Context)

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
