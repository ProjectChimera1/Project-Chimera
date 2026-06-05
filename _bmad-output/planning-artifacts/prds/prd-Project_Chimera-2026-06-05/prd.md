---
title: Project Chimera
created: 2026-06-05
updated: 2026-06-05
status: drafting
scope_frame: gap-to-1.0
---

# PRD: Project Chimera — Road to 1.0
*Working title — confirm.*

## 0. Document Purpose

This PRD is the **definition-of-done for Project Chimera 1.0** — the bridge from the current
as-built codebase (Phases 0–4 code-complete; Phase 5 in progress) to a complete, shippable game.
Its audience is the solo developer (Alec) and the downstream BMad workflows (UX → architecture →
epics → stories) that turn it into work.

It is **brownfield and gap-framed**: it builds on `Project_Chimera_GDD.md` (design intent) and the
as-built docs under `_bmad-output/` (`architecture.md`, `component-inventory.md`, `data-models.md`,
`state-management.md`). It **does not relitigate GDD design decisions** and **does not re-specify
systems that are already built and working** — those are referenced as *Built Foundation* (§4.0) and
cited to `component-inventory.md`. The Features section (§4) specifies only the **remaining work to
reach 1.0**. Vocabulary is anchored in the Glossary (§3); assumptions are tagged `[ASSUMPTION]`
inline and indexed (§9); deferred items are marked `[v2 — out of 1.0]`; tensions for the developer
are flagged `[NOTE FOR PM]`.

Technical depth (as-built gap map, determinism constraints, DSL-expressiveness target, LLM provider
plumbing) lives in `addendum.md`.

## 1. Vision

Project Chimera 1.0 is a **fully-polished, Warcraft III World Editor–class creation suite — AI-assisted
at every step — that lets one person build *any* custom game.** Terrain, units, hero characters,
abilities and skills, factions, tech trees, economy, win conditions, and game rules are all authored
in-app through intuitive tools, with an AI collaborator helping at each stage: generating triggers
from natural language, drafting maps, proposing units and abilities, and analyzing balance.

The shipped game — two truly asymmetric factions playable in deterministic, server-authoritative
multiplayer — is the **showcase** of that engine, not the destination. The destination is creator
power: an intuitive, efficient workflow that gives a creator ultimate capability to make new games
and creative systems, the way WC3's editor birthed entire genres (DOTA, tower defense, custom RPGs)
from one RTS.

Chimera achieves this without a scripting language: all creator logic is expressed through a **rich
declarative trigger DSL** powerful enough to cover WC3 GUI-trigger range, while remaining
deterministic and server-validatable — the property that makes shared, multiplayer-safe content
possible. The platform ships as a premium, one-time purchase (no free-to-play, no microtransactions),
deliberately scoped to a focused, genre-specific creation tool rather than a Roblox-scale platform.

## 2. Target User

### 2.1 Primary Persona — The Architect/Tinkerer (and the developer himself)
The creator who opens the editor to **build a game**, not just play one. They have an idea — a custom
faction, a hero brawler, a tower-defense mode, an RPG scenario — and want to realize it without
writing code. They expect WC3-World-Editor power with modern, AI-assisted ergonomics. The solo
developer (Alec) is himself the first and most demanding member of this persona: 1.0 is "the editor I
can make any custom game with." `[ASSUMPTION: the developer-as-primary-user is the load-bearing
persona for 1.0; community Architects are the same persona at lower tool-fluency.]`

### 2.2 Secondary Persona — The Commander
The pure player. Never opens the editor. Discovers community scenarios, plays them solo or
multiplayer, rates what they enjoy. The Commander is who the Architect *builds for*, and the reason
Share/Discover must work — but the Commander's needs are satisfied largely by the already-built game
and multiplayer layers.

### 2.3 Jobs To Be Done
- **(Create)** "Let me build the custom game in my head — units, heroes, abilities, rules — without code, with AI doing the tedious parts."
- **(Create)** "Let me iterate fast: edit → play → tweak in seconds, not minutes."
- **(Share)** "Let me publish my scenario so others can find and play it, and trust it won't break or cheat."
- **(Discover)** "Let me find scenarios worth my time and play them with friends."
- **(Play)** "Let me play a polished, fair, desync-free RTS match — the proof the engine is real."

### 2.4 Non-Users (1.0)
- Players seeking a free-to-play / live-service title (Chimera is premium, one-time).
- Creators wanting a general game engine for non-game software or per-game custom engines (Chimera is WC3-editor-scoped, not Roblox/Unity-scale).
- Console / mobile / browser players (PC desktop only).

### 2.5 Key User Journeys
*Named flows, numbered globally for downstream traceability. Detailed flow design is the UX
workflow's job. Features (§4) reference these by ID.*

- **UJ-1 — Build a custom game.** An Architect starts from a blank or AI-generated map and authors terrain → units & heroes → abilities/skills → buildings & tech trees → a faction → win conditions & rules, entirely in-app. *(Create)*
- **UJ-2 — Co-create with AI.** At any authoring step the Architect invokes the AI collaborator: "make a frost mage hero with a slow aura," "generate a 4-player jungle map," "is this faction balanced?" — and gets editable results. *(Create)*
- **UJ-3 — Fast edit→play loop.** The Architect plays their scenario instantly from the editor, finds a problem, returns to the editor, fixes it, replays — in seconds. *(Create)*
- **UJ-4 — Publish a scenario.** The Architect packages their scenario as a `.chimera.zip`, passes the proof-of-play gate, and publishes to the in-app/mod.io content browser with tags and metadata. *(Share)*
- **UJ-5 — Discover & play community content.** A Commander browses the content browser, filters by tag/rating, subscribes to a scenario, and plays it solo or multiplayer. *(Discover)*
- **UJ-6 — Play a multiplayer match.** Two-or-more players matchmake or join via LAN/lobby, play a deterministic lockstep match with in-game chat, and a replay is saved. *(Play — the showcase)*

## 3. Glossary
*Downstream workflows and readers use these terms exactly. No synonyms elsewhere.*

- **Creation Suite** — the in-app set of authoring tools (terrain, unit, ability, hero, building, tech-tree, faction, trigger, win-condition editors) that together let a creator build a game without code.
- **Architect** — a creator who builds scenarios; the primary persona. **Tinkerer** — an Architect who also plays. **Commander** — a pure player who never opens the editor.
- **Scenario** — a self-contained, playable custom game, distributed as a `.chimera.zip` package.
- **`.chimera.zip`** — the canonical UGC content package (manifest + data + assets), content-hashed for integrity.
- **Trigger (ECA)** — an Event → Condition → Action rule; the atomic unit of scenario logic.
- **Trigger DSL** — the rich *declarative* language (variables, arithmetic, arrays, loops, timers, custom events, custom UI) in which all creator logic is expressed. Deterministic and server-validatable. Never arbitrary scripting.
- **Trigger tiers** — T1 presets → T2 ECA editor → T3 visual node graph (GraphEdit) → T4 natural-language (AI). The same DSL, surfaced at four fluency levels.
- **Scenario Director** — the runtime engine that evaluates triggers and drives game flow deterministically.
- **Unit Card Editor** — the consolidated single-panel editor for a unit's stats, abilities, and model.
- **Ability / Skill** — an authorable active or passive effect a unit can have; composed from effect primitives (layered: presets in simple mode, custom composition in advanced).
- **Hero** — a named unit with leveling and signature/ultimate abilities (WC3-hero-style). **Character** is used interchangeably with Hero in this PRD.
- **Faction** — a complete playable side: unit roster, building/tech tree, starting conditions, AI preset. Authored via the **Faction Definer**.
- **Archetype** — one of the 6 base unit types (Worker, Melee, Ranged, Siege, Air, Structure) from which all units compose.
- **Damage/armor matrix** — the data table of combat multipliers (damage type × armor type).
- **AI collaborator / AI-assisted authoring** — LLM-backed help (trigger gen, map gen, unit/ability/faction gen, balance analysis) available throughout the Creation Suite, via a user-supplied API key (OpenRouter / Claude) or local Ollama.
- **Deterministic lockstep** — server-authoritative, command-based multiplayer using fixed-point (`Fixed`) math, deterministic RNG, and a 30 Hz tick, such that all machines compute identical state.
- **Proof-of-play gate** — the requirement that a creator complete (win) their own scenario before publishing it.
- **Persistent artifacts** — creator-selected per-player attributes (hero level/XP, items, skill tree, currency) that carry forward from one custom game to the next, loaded at match init as deterministic initial state (the WC3 save-code / "-load" model). Works online. Distinct from mid-game save/resume. See FR-7a–c.
- **Persistence manifest** — the creator-authored checklist defining which attributes are persistent artifacts for a scenario.
- **Save/Load Interface (Hero Picker)** — the platform-provided UI where a player visually manages saved heroes/profiles (icon + basic info), and saves/loads/overwrites slots. Creator-enabled per scenario. See FR-7d/e.
- **Custom UI element** — a creator-authored runtime UI primitive (text, counter, button, dialog) driven by triggers; required for non-RTS modes (TD wave counters, RPG dialog, scoreboards). See FR-26.
- **Combat-feedback profile** — the per-unit/ability bundle of hit particles, impact sound, screen shake, hit-freeze, and death effect that gives combat "juice." A tuned default ships; creators override it. See FR-12a.
- **Built Foundation** — systems already code-complete and referenced as done (§4.0); not re-specified here.
- **Verification floor** — the testing/hardening work (LAN determinism, smoke tests, automated test suite) that 1.0 requires before the built systems can be trusted.

## 4. Features

> **Reading guide.** §4.0 references what is *already built* (done — not 1.0 work). §4.1–§4.11 specify
> the **remaining 1.0 work**, grouped by area, with globally-numbered FRs describing *capabilities*;
> §4.12 holds cross-cutting NFRs. Implementation detail lives in `addendum.md`. Items marked
> `[v2 — out of 1.0]` are explicitly deferred. **FR numbering:** FR-1..FR-52 are stable global IDs;
> a lettered insert (e.g. FR-7a) is an intentional in-place addition — treat it as its own requirement.
> **Layered complexity applies everywhere:** every editor's *advanced mode* retains direct raw-data
> (JSON) access for power users — the in-app editors augment JSON authoring, they do not remove the
> expert escape hatch (GDD pillar).

### 4.0 Built Foundation *(reference — already done, not 1.0 scope)*
Per `component-inventory.md` / `architecture.md`, these are code-complete and referenced as done. 1.0
work *hardens and verifies* them (§4.7) but does not re-specify them:
- **Deterministic sim core** — `Fixed` 16.16 math, SoA `EntityWorld` (4096), 30 Hz `SimulationLoop`, `SpatialHash`, MultiMesh rendering, interpolation. (~350 FPS @ 1000 units verified single-machine.)
- **Combat** — damage/armor matrix, cooldowns, projectiles, splash, combat feedback, death/cleanup.
- **Economy** — resource nodes, gatherer FSM, dynamic supply cap, resource HUD.
- **Unit framework** — JSON `UnitDefinition`, 6 archetypes, command system, `SelectionSystem` (box/click). `[ASSUMPTION: control groups + formation movement are present — verify against component-inventory before treating as done; if stubbed, they move to a §4.10 verify task.]`
- **Navigation** — flow-field pathfinding (live, deterministic), fog of war, NavServer3D direct API.
- **Base building** — placement, construction, production queues, data-driven tech-tree gating, rally points.
- **AI opponent** — utility-AI, Easy/Normal/Hard.
- **Multiplayer (code-complete, UNVERIFIED on LAN)** — lockstep, adaptive input delay, ENet + dedicated relay, command serialization, checksums/desync detection, `.chmr` replays, spectator, Nakama 1v1 matchmaking, content hashing, match chat.
- **Editor basics** — Terrain3D sculpt/paint, entity placer, start positions, resource-node placer, win-condition setter, undo/redo, NL trigger system, AI map generator.
- **UGC plumbing** — `.chimera.zip` packager, content browser (Local + mod.io), mod.io REST, creator profiles.
- **Content** — 2 factions (Alpha + Iron Pact *reskin*), 12 maps, full HUD/menus/settings/audio system (assets pending).

---

### 4.1 Creation Suite — Unit & Hero Authoring *(headline)*
**Description:** A creator authors units and heroes entirely in-app via a consolidated **Unit Card
Editor** (the WC3 single-panel model): all stats, archetype, model, abilities, and behaviors in one
view. Heroes extend units with leveling, experience, and signature/ultimate abilities. Layered
complexity: a simple mode (presets, dropdowns, archetype templates) and an advanced mode (every data
field exposed). Replaces today's JSON-by-hand authoring. Realizes UJ-1, UJ-2.

**Functional Requirements:**
- **FR-1** — An Architect can create, edit, duplicate, and delete a unit definition in-app without editing JSON, with all changes persisted to the scenario's data.
- **FR-2** — The Unit Card Editor presents stats, archetype, combat type (damage/armor), model assignment, and attached abilities in a single panel.
- **FR-3** — An Architect can assign a 3D model (or box placeholder) to a unit and preview it in-editor.
- **FR-4** — An Architect can compose a unit from the 6 archetypes plus orthogonal ability/behavior components (composition over inheritance — no subclassing).
- **FR-5** — An Architect can designate a unit as a **Hero**, configuring leveling curve, experience gain, and signature/ultimate abilities.
- **FR-6** — Simple mode offers preset/templated units; advanced mode exposes every authorable field.
- **FR-7** — Validation surfaces authoring errors inline before save/playtest, covering at minimum: missing/invalid model, out-of-range or missing required stat, reference to an undefined ability, and invalid archetype composition.
- **FR-7a** — An Architect can author **persistent artifacts** (WC3 save-code / "-load" model): a per-scenario **persistence manifest** where the creator checks off which attributes of each hero/unit/player carry forward to the *next* custom game — e.g. hero level/XP, inventory/items, skill tree, currency. Persisted per player.
- **FR-7b** — At the start of a custom game, players **load** their persisted profile for that scenario (or scenario family), and it is applied as **deterministic initial state** — so persistence works in **multiplayer/online** games, not just single-player (it is init-time data, never a mid-game snapshot). A creator can disable persistence entirely (match-only).
- **FR-7c** — For online persistence, save profiles are **stored/validated server-side** (not raw client save-codes) to prevent tampering, consistent with the server-authoritative model. `[ASSUMPTION: server-side profile storage; confirm vs. signed client save-codes.]`
- **FR-7d** — The platform provides a **Save/Load Interface** (a menu / **hero picker**) — not text codes — through which a player manages their saved heroes/profiles for a scenario or scenario family. Each entry displays the **hero icon and basic info** (e.g. name, level, key progression). The creator enables and configures this interface per scenario; the persistence manifest (FR-7a) drives what each save holds.
- **FR-7e** — From the Save/Load Interface a player can **load** a saved hero into a compatible game, **save** a new profile, and **overwrite** an existing save slot (with confirmation). Multiple saved heroes per player are supported.

**Feature-specific NFRs:** Editing a unit and entering playtest reflects changes without an app restart (supports UJ-3, measured by §4.12). The Save/Load Interface is a reusable platform component creators turn on, not something each creator rebuilds.
**Notes:** `[NOTE FOR PM]` This is the headline mechanic for online-RPG-style custom games. Scope is a **targeted per-player profile serializer driven by the creator's manifest** (selected fields only) — *not* a full `EntityWorld` snapshot — applied at match init. A separate, heavier **mid-game single-player save/resume** (full-world serializer) is a distinct capability — `[v2 — out of 1.0]` unless you want quit-and-resume for single-player campaigns at 1.0. See addendum §G.

### 4.2 Creation Suite — Ability / Skill Authoring *(headline)*
**Description:** A creator builds active and passive abilities ("skills") via an **Ability Editor**
that composes effects from primitives — damage, heal, buff/debuff, summon, teleport, aura, projectile,
status — with targeting (self / unit / point / area / passive), costs, cooldowns, and triggers.
Layered: pick-and-configure preset effects in simple mode; fully custom multi-effect composition in
advanced mode. Realizes UJ-1, UJ-2. `[ASSUMPTION: ability authoring uses the same effect-primitive
set the trigger DSL exposes, so abilities and triggers share one effect vocabulary.]`

**Functional Requirements:**
- **FR-8** — An Architect can create an active ability with a targeting type, cost, cooldown, and one or more effects, in-app without code.
- **FR-9** — An Architect can create a passive ability (auras, on-hit effects, stat modifiers) with trigger conditions.
- **FR-10** — An Architect can compose an ability from multiple effect primitives in advanced mode, and from configurable presets in simple mode.
- **FR-11** — Abilities can be attached to units and heroes (FR-4, FR-5) and appear on the command card at runtime.
- **FR-12** — Ability definitions are data-driven, deterministic, and server-validatable (no float math, no arbitrary scripts).
- **FR-12a** — Abilities and units carry a **combat-feedback profile** (hit particles, impact sound, screen shake, hit-freeze, death effect). A tuned, satisfying **default profile ships** so every Chimera-built game has baseline "juice," and creators can override it per unit/ability (GDD §3 framework guarantee).

**Notes:** `[NOTE FOR PM]` The breadth of effect primitives is the lever that makes "any game" real or not — it warrants its own spec pass with the GDD/architecture phase. The richer the primitive set, the more genres become buildable.

### 4.3 Creation Suite — Building, Tech Tree & Economy Authoring
**Description:** A creator authors buildings (production, construction, prerequisites), assembles
**tech trees** via a drag-built visual editor, and configures the economy — resource types and
gather/income models — as data. The two-resource default (Ore + Crystal) remains the shipped default
*ceiling for the showcase game*, but resources must be **data-driven** so a creator can add/replace
them per scenario (closes a current as-built gap where resources are hardcoded). Realizes UJ-1.

**Functional Requirements:**
- **FR-13** — An Architect can create and edit building definitions (stats, production options, construction cost/time, tech prerequisites) in-app.
- **FR-14** — An Architect can build a faction's tech tree visually (drag dependencies between buildings/units/upgrades) and the runtime gates production by it.
- **FR-15** — An Architect can define a scenario's resource set as data (type, model, starting amount, gather model), beyond the two default resources.
- **FR-16** — An Architect can configure the supply/cap model per scenario.

**Notes:** `[NOTE FOR PM]` FR-15/FR-16 require migrating hardcoded economy assumptions to data — see addendum "Data-driven debt." Scope-check whether full economy-model authoring is 1.0 or partially `[v2]`.

### 4.4 Creation Suite — Faction Definer
**Description:** A guided wizard assembles authored units, heroes, buildings, tech trees, and starting
conditions into a complete, playable **Faction**, plus selects an AI preset. This delivers your
"building blocks for creating factions in creator mode." Targets a 5–12 minute first-faction
experience (GDD). Realizes UJ-1.

**Functional Requirements:**
- **FR-17** — An Architect can create a faction by assembling authored units/heroes/buildings/tech-tree, name, color, and starting conditions through a guided multi-step flow.
- **FR-18** — An Architect can assign an AI preset to a faction so it is playable against/with the AI opponent.
- **FR-19** — A completed faction is immediately selectable in playtest and in skirmish/multiplayer setup.
- **FR-20** — The two shipped factions (Alpha, Iron Pact) are themselves valid Faction Definer outputs, and **Iron Pact is upgraded from a reskin to a genuinely asymmetric faction.** *Acceptance:* Iron Pact has ≥1 unique core mechanic and a roster that differs from Alpha in role/stat profile (not 1:1 renamed units), validated in playtest.

### 4.5 Creation Suite — Map / Terrain Editor *(polish)*
**Description:** The existing Terrain3D sculpt/paint editor is brought to a shippable bar: the
**terrain-texture persistence defect is fixed** (procedural textures don't currently persist), and
the editor is polished for the WC3-editor quality bar. Realizes UJ-1.

**Functional Requirements:**
- **FR-21** — An Architect can sculpt and texture-paint terrain in-app, and the painted textures persist correctly on save/load.
- **FR-22** — An Architect can place entities, start positions, and resource nodes, and set win conditions, on the map (existing — verify to ship bar).

**Notes:** Known runtime noise from `TerrainBrush` (`_store_undo` push_error) is documented non-fatal; confirm it is suppressed or cleaned for 1.0.

### 4.6 Creation Suite — Trigger / Rules System (Rich Declarative DSL) *(headline)*
**Description:** The heart of "build any game": a **rich declarative trigger DSL** surfaced at four
tiers — T1 presets, T2 ECA editor (built), T3 visual node graph, T4 natural-language/AI (built). To
reach WC3-GUI-trigger generality, the DSL gains **variables, arithmetic, arrays/collections, loops,
timers, custom events, and custom UI elements** — while remaining declarative, deterministic, and
server-validatable. This is what makes the platform "general enough to be anything." Realizes UJ-1,
UJ-2.

**Functional Requirements:**
- **FR-23** — A creator can author scenario logic as ECA triggers in-app (built — verify to ship bar).
- **FR-24** — The DSL supports variables (typed, scoped), arithmetic/boolean expressions, arrays/collections, conditional loops, and timers.
- **FR-25** — A creator can define custom events and raise them from triggers (enabling decoupled game-logic modules).
- **FR-26** — A creator can define custom runtime UI elements (text, counters, buttons) driven by triggers (required for non-RTS modes: TD waves, RPG dialog, scoreboards).
- **FR-27** — All DSL constructs are deterministic, fixed-point, and validatable by the server before a scenario runs in multiplayer (no arbitrary scripting escape hatch).
- **FR-28** — A creator can author the same logic at T1 (preset), T2 (ECA), T3 (visual node graph), or T4 (natural language), and the tiers interoperate on one underlying DSL representation.

**Notes:** `[NOTE FOR PM]` T3 **visual node-graph (GraphEdit)** editor: GDD core Phase 4 but §11 lists "advanced visual scripting" as stretch. **Decision needed** — is T3 a 1.0 surface or `[v2]`, given T2 (ECA) + T4 (AI) already exist? `[NOTE FOR PM]` FR-24–FR-26 are a substantial expansion of the trigger engine and the largest single technical risk in the Creation Suite; flag for the architecture phase.

### 4.7 AI-Assisted Creation (cross-cutting) *(headline)*
**Description:** An AI collaborator is available throughout the Creation Suite, backed by a
user-supplied API key (**OpenRouter free tier** or **Claude**) or local **Ollama**. It generates
triggers from natural language (built), maps (built), units/abilities/heroes/factions and their
names/lore, and performs **balance analysis** (new). All AI output is *editable data*, never a black
box — the creator reviews and tweaks. Realizes UJ-2.

**Functional Requirements:**
- **FR-29** — A creator can select and configure an LLM provider (OpenRouter, Claude, or local Ollama) by supplying an API key via settings; keys are never hardcoded or committed. *Note:* this includes migrating key storage from the current single `AnthropicApiKey` Inspector export on `MainScene` into the persisted settings system (`SettingsData`/`SettingsManager`) — it is more than adding a dropdown.
- **FR-30** — A creator can generate triggers from a natural-language prompt and review/edit the result before applying (built — verify, extend to new DSL constructs).
- **FR-31** — A creator can generate a map from a prompt with validation before load (built — verify). *Note:* the existing 7-pass validator hard-clamps output to the showcase (≤6 combat units/faction, ≤6 slots, forced faction paths); these clamps must be **relaxed/parameterized** for non-RTS scenario types (TD/RPG) to honor UJ-1/UJ-2.
- **FR-32** — A creator can generate a unit, ability, hero, or faction draft (stats + name + lore) from a prompt, as editable data. *Note:* generation/validation must not assume RTS-only conventions (see FR-31 note).
- **FR-33** — A creator can request **AI balance analysis** of a faction/scenario and receive actionable, editable suggestions (new build work).
- **FR-34** — When no provider/key is available, AI features degrade gracefully with a clear message and the suite remains fully usable manually.

**Feature-specific NFRs:** AI calls never block the deterministic sim; generation happens in the editor/authoring layer only.

### 4.8 Share & Discover (UGC) *(verify + polish)*
**Description:** A creator packages and publishes scenarios; players discover and play them. The
plumbing exists (`.chimera.zip`, mod.io REST, content browser, creator profiles); 1.0 brings it to a
verified, configured, end-to-end-working bar, including the **proof-of-play gate**. Realizes UJ-4, UJ-5.

**Functional Requirements:**
- **FR-35** — A creator can package a scenario as `.chimera.zip` and publish it to mod.io from in-app (verify end-to-end; requires mod.io Game ID + API key configured).
- **FR-36** — A creator must pass the **proof-of-play gate** (win their own scenario) before publishing.
- **FR-37** — A player can browse, search, tag-filter, sort, subscribe to, and rate scenarios in the content browser.
- **FR-38** — Published packages are content-hashed and integrity-verified on download.
- **FR-38a** — **Creators retain full ownership of their content.** The platform takes only a non-exclusive right to host and distribute published scenarios; it claims no ownership of creator IP (GDD §7 non-negotiable, Reforged cautionary tale). This is surfaced at publish time.

**Notes:** `[NOTE FOR PM]` mod.io integration is wired but never verified end-to-end — treat as verification work, not "done." `[v2 — out of 1.0]` GDD's richer discovery-quality mechanics (weighted rating algorithm — completion rate / unique players / recency, not just average stars; auto-hide on multiple reports) are a conscious deferral; 1.0 uses proof-of-play + star ratings + mod.io-native features.

### 4.9 The Showcase Game & Multiplayer *(verify + harden + content)*
**Description:** The shipped, polished proof that the engine is real: two asymmetric factions playable
solo (vs. AI) and in **deterministic lockstep multiplayer**, with replays, spectator, and chat. The
single largest 1.0 risk lives here — **multiplayer has never been verified on two machines.** Realizes UJ-6.

**Functional Requirements:**
- **FR-39** — Two players can complete a full multiplayer match on **separate machines (LAN)** with checksums in sync for 300+ ticks and zero desync — the **P2.4 LAN determinism test must pass** (currently never run; #1 risk).
- **FR-40** — Players can matchmake (Nakama) or join via LAN/lobby, with in-game chat, and a `.chmr` replay is saved **and viewable/shareable** (not just recorded). *Target:* support **up to 8 players** (GDD ceiling) across the supported modes; `[ASSUMPTION: pre-match party grouping is 1.0 — confirm.]` **Spectator mode** is included in the multiplayer verification set (it is code-complete but unverified).
- **FR-41** — Adaptive input delay verifiably adjusts to RTT on real networks (4→2 on LAN, clamped [2,12]) without desync.
- **FR-42** — The two shipped factions are balanced. *Acceptance:* neither faction's win rate falls outside ~45–55% across a representative AI self-play / playtest sample (informed by FR-33 AI balance analysis).
- **FR-43** — A solo player can play skirmish vs. the AI opponent across difficulties on the shipped maps.

**Notes:** `[NOTE FOR PM]` Nakama matchmaking is **1v1-only** today; GDD intended 2–8 players. Decision needed — is >2-player matchmaking 1.0 or `[v2]`? `[NOTE FOR PM]` NavServer3D is non-deterministic across machines; flow fields are the mitigation but unproven on real LAN — FR-39 is the make-or-break test.

### 4.10 Verification Floor & Quality *(infrastructure)*
**Description:** The testing and hardening that makes the Built Foundation trustworthy for 1.0.
Currently `tests/` is empty and four built systems are unverified. This feature area has no
player-facing surface but gates everything.

**Functional Requirements:**
- **FR-44** — An automated test suite (GdUnit4) covers the deterministic sim, combat formula, economy, and pathfinding, runnable headless without Godot.
- **FR-45** — The four unverified systems (Utility AI, Adaptive Input Delay, LLM Trigger System, AI Map Generator) each pass their smoke-test checklists.
- **FR-46** — A performance pass confirms the 500–2,000 units @ 60 FPS render / 30 Hz target on representative scenarios.
- **FR-47** — `[ASSUMPTION]` Determinism is regression-guarded by a replay/checksum test in CI so future changes can't silently break multiplayer.

### 4.11 Release Readiness *(finishing)*
**Description:** The concrete content and platform work to ship.
**Functional Requirements:**
- **FR-48** — Audio assets (`.ogg`) are present and wired through the existing audio system.
- **FR-49** — Iron Pact's 8 placeholder GLBs are replaced with real art (external Hunyuan3D/Tripo pipeline).
- **FR-49a** — An **art-style consistency layer** keeps AI-generated and creator-supplied assets visually coherent — at minimum a shared material library and a global post-process (e.g. cel-shading) shader, so community content doesn't look incoherent (GDD §8.4 names this the pipeline's hardest challenge). `[NOTE FOR PM]` confirm the 1.0 enforcement depth (post-process shader baseline vs. fuller style-prefix/LoRA pipeline).
- **FR-50** — A Linux export (dedicated-server and/or client) builds and runs.
- **FR-51** — Accessibility meets a 1.0 **baseline**: remappable keys, colorblind-safe team colors, UI scaling, and subtitles for any voice-over. `[NOTE FOR PM: baseline only — expand if you want deeper a11y at 1.0.]`
- **FR-52** — The build ships to **Steam and a direct DRM-free channel** (e.g. your site / Gumroad): both store/build pipelines are release-ready.

### 4.12 Cross-Cutting NFRs
*System-wide quality attributes not owned by a single feature. Downstream UX/architecture must honor these.*

- **NFR-1 — Fast edit→play loop (realizes UJ-3).** The editor→playtest→editor round-trip is near-instant: entering playtest reflects authoring changes **without an app restart**, target **≤ a few seconds**. This is the core creation-loop quality bar; UJ-3 is realized by the playtest capability across §4.1–§4.9, measured here.
- **NFR-2 — Creator-experience / discoverability.** Every editor field, button, and panel has a hover tooltip (the Dreams lesson); a guided **"Your First Scenario"** onboarding flow exists; a creator reaches a basic playable scenario in **< 15 min** (complements the ≤12-min faction metric).
- **NFR-3 — Editor invisible to Commanders.** Players who never create are never exposed to authoring UI/complexity; creation surfaces are opt-in.
- **NFR-4 — Determinism is sacred (system-wide).** No feature — including new editors, hero/save systems, custom UI, AI — introduces float gameplay math, wall-clock dependence, or nondeterministic iteration into the sim layer. AI/LLM runs only in the authoring layer, never in the sim tick.
- **NFR-5 — Performance.** The 500–2,000 units @ 60 FPS render / 30 Hz sim target holds on representative shipped and community scenarios (verified by FR-46).
- **NFR-6 — Server-validatable content.** Every shareable construct (units, abilities, triggers, DSL logic, custom UI) is statically validatable so the server can reject malformed/cheating scenarios before they run — this *bounds* the DSL's expressiveness (anything not statically validatable is out).

## 5. Non-Goals (Explicit)

- **No arbitrary scripting language.** Creator logic is the declarative DSL only — never raw scripts (preserves determinism + server validation). `[NON-GOAL for 1.0 and by design]`
- **Not a per-game engine / Roblox-scale platform.** WC3-World-Editor scope: one RTS engine bent into other genres via data + DSL, not a general game-making tool. `[NON-GOAL by design]`
- **No free-to-play / microtransactions.** Premium one-time purchase ($15–25). Explicit anti-Stormgate stance. `[NON-GOAL by design]`
- **No web / mobile / console.** PC desktop only (Windows primary, Linux for servers). No C# web export.
- **No client-side/kernel anti-cheat.** Server authority + command validation only; not an anti-cheat arms race.
- **No P2P/WebRTC netcode.** Server-authoritative lockstep only.
- **No cross-package dependencies.** Each scenario is fully self-contained for 1.0.
- **3rd faction is post-1.0.** Two asymmetric factions ship; the third is `[v2]`.
- **No real-money creator marketplace at 1.0.** Sharing is free via mod.io; monetized marketplace is `[v2]`.
- **No claim on creator IP.** The platform never takes ownership of creator content — only a non-exclusive host/distribute right (FR-38a). `[NON-GOAL by design]`

## 6. 1.0 Scope & Build Sequencing
*Decision (log #13): **all features in §4.1–§4.11 are in 1.0** — the full vision, no fast-follow tier.
`[NOTE FOR PM]` This is a deliberately maximal 1.0 and matches the GDD's named "ship everything at
once" risk. The agreed mitigation (log #14) is **not** scope cutting but **build sequencing**: ship
nothing until 1.0, but keep the build in an always-working state via internal milestones, so
integration risk surfaces early. Treat the milestones below as the recommended order for the
epics/stories workflow — adjust freely.*

### 6.1 In Scope — All of §4.1–§4.11
Everything specified in §4 is 1.0 scope, including all four formerly-fast-follow items:
T3 visual node-graph triggers (FR-28), custom runtime UI in triggers (FR-26), full economy-model
authoring (FR-15/16), and >2-player matchmaking (FR-40).

### 6.2 Recommended Build Sequencing *(internal milestones — always shippable state)*
- **M0 — Integration hygiene (do alongside M1).** `MainScene.cs` is a ~2,200 LOC composition root (~25 `SetupXxx()`) and the single point every new system wires into — M2/M3 add ~6 editors + hero sim + a major DSL expansion all threading through it. Decompose/modularize the wiring **before** piling on, or it becomes the integration chokepoint. Also: `PathRequestSystem` still owns the Move→Stop transition (dodging a historical stutter bug) — its Move→Stop logic must migrate to `FlowFieldBridge` before it can be removed; don't delete it blindly during FR-39 work.
- **M1 — Foundation trust.** Verification floor: automated test suite (FR-44), smoke-test the 4 unverified systems (FR-45), and **pass the LAN determinism test (FR-39)** ⚠️. *Nothing else is safe to build on until multiplayer determinism is proven.* Also clear the data-driven debt (DamageMatrix→JSON) so later authoring builds on data, not constants.
- **M2 — Core authoring.** Unit Card Editor (FR-1–7), Ability Editor (FR-8–12), Building/Tech-Tree/Economy authoring (FR-13–16), Faction Definer (FR-17–20). Iron Pact → asymmetric (FR-20). Hero sim + the **persistence-manifest authoring + per-player profile serializer** (FR-7a/b) land here; **server-side profile storage** (FR-7c) follows in M5 with the online stack. *Now a creator can build a faction in-app.*
- **M3 — DSL power.** Expand the trigger DSL (FR-24–25 variables/loops/timers/events), custom runtime UI (FR-26), server validation (FR-27), the four tiers incl. T3 node graph (FR-28). *Now "any game" is buildable.*
- **M4 — AI everywhere.** Provider config + OpenRouter (FR-29), extend AI gen across new editors (FR-32), balance analysis (FR-33). *Now creation is AI-assisted end to end.*
- **M5 — Share/Discover + multiplayer breadth.** Verify UGC e2e (FR-35–38), >2-player matchmaking (FR-40). *Now scenarios circulate and play at scale.*
- **M6 — Release.** Content (audio FR-48, Iron Pact art FR-49), perf pass (FR-46), Linux (FR-50), accessibility (FR-51), Steam-ready (FR-52). *Ship.*

### 6.3 Out of Scope (post-1.0 / v2)
3rd faction · creator marketplace · cross-package dependencies · advanced editor extras (particles, sound triggers) · anything in §5 Non-Goals.

## 7. Success Metrics
*Confirmed 2026-06-05. For a solo premium title, the two hard gates matter most; the rest are health signals.*

**Primary (1.0 hard gates — must be true to ship)**
- **Zero desyncs** in ≥95% of multiplayer matches; FR-39 LAN determinism test passes. *(non-negotiable)*
- **A creator can build a complete, novel custom game** (units + heroes + abilities + faction + rules) **in-app without editing JSON** — the North Star, demonstrably true.

**Secondary (health signals)**
- **Steam reviews >80% positive** (GDD 1.0 criterion).
- **≥50 community scenarios published** within the first month of 1.0.
- **First-faction authoring achievable in ≤12 min** (GDD).
- Avg session length / "when can I play more?" qualitative signal (GDD Phase 1 bar).

**Counter-metrics (do NOT optimize)**
- **Don't optimize feature count / "ship everything."** The GDD's named failure mode (Stormgate). A focused, polished, verified 1.0 beats a broad, flaky one.
- **Don't chase higher unit-count benchmarks** once the 500–2,000 target is met — pushing the engine ceiling further trades creation-suite polish for a number nobody asked for.
- **Don't chase non-RTS genre coverage breadth** at the cost of the core RTS showcase feeling unfinished.

## 8. Open Questions
*Resolved (→ decision-log): T3 node graph, custom runtime UI, >2-player matchmaking, economy-model
authoring are all **in 1.0** (log #13). Remaining:*
1. **Effect-primitive breadth** for abilities/triggers — needs a dedicated architecture spec pass; it's the lever for "any game." Deferred to the architecture phase, not a PRD blocker. (FR-10, FR-24)
2. **Accessibility checklist** — define the specific 1.0 bar. (FR-51) — baseline assumed; confirm depth.
3. **Success-metric targets** — confirm the published-scenario N and the Steam-review target. (§7)

## 9. Assumptions Index
- **§2.1** — Developer-as-primary-user is the load-bearing 1.0 persona; community Architects are the same persona at lower fluency.
- **§4.2 / FR-8** — Ability authoring shares the effect-primitive vocabulary with the trigger DSL (one effect language).
- **§4.0 (unit framework)** — Control groups + formation movement are present/working; verify against component-inventory before treating as done.
- **FR-7a–c** — Persistent artifacts use a creator-manifest-driven per-player profile serializer applied at match init (online-capable); profiles are stored/validated server-side (vs. signed client save-codes — confirm). Full mid-game single-player save/resume (full-world serializer) is `[v2]`.
- **FR-40** — Pre-match party grouping is in 1.0; 8-player ceiling is the target.
- **FR-47** — Determinism is CI-regression-guarded via replay/checksum test.
- **§7** — Success-metric targets (Steam >80%, ≥50 scenarios month 1) confirmed 2026-06-05; revisit against actual launch reach.
- **§4.3** — Two-resource default remains the showcase ceiling; resources become data-driven for creators.
- **FR-52** — Steam + a direct DRM-free channel are the 1.0 distribution targets.
