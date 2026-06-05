# Project Intent — Project Chimera

> **Purpose of this file.** This is the BMAD/GDS user-proxy source. When a BMAD or GDS
> workflow asks a question, answer it from this document and continue. Only escalate to
> Alec when a workflow asks something genuinely not covered here AND it is a real blocker —
> then append the answer under "## Session Escalations" before continuing.
>
> **Project type:** BROWNFIELD. The game already exists and is substantially built
> (Phases 0–4 code-complete, Phase 5 underway). New BMAD work plans against the existing
> Godot/C# codebase — it does not green-field from scratch. Prefer `gds-document-project`
> and `gds-investigate` before planning large changes.

- **Project name:** Project Chimera
- **Owner:** Alec (solo developer, AI-assisted at every layer)
- **Engine / stack:** Godot 4.6.2 stable (.NET), C# targeting .NET 8+
- **Assembly name:** `ProjectChimera` (csproj + project.godot must match)
- **Repo:** `D:\Projects\Project_Chimera` — code in `godot/src/`, source-of-truth design in `Project_Chimera_GDD.md`
- **Current snapshot:** `Snapshot.md` (live briefing). `CONTEXT.md`/`STATUS.md` are deprecated archives.

---

## 1. Product vision

Project Chimera is **not a single RTS game — it is an RTS *creation platform*** that ships the
RTS genre as a living, community-owned system. It launches as a compelling single-player RTS and
grows into a creation platform through Early Access iteration.

Every feature must serve at least one of three questions (the "three-question filter"). Features
serving none are cut; features serving two or three are prioritized:
- **Create** — does this make it easier to build a great RTS scenario?
- **Share** — does this make it easier to publish/distribute that scenario?
- **Discover** — does this make it more exciting to find and play scenarios made by others?

**Problem it solves:** accessible RTS creation. The Warcraft III World Editor proved approachable
tools can birth whole genres (DotA, Tower Defense). Chimera aims to be that, modernized and
AI-assisted, owned by its community.

### Design pillars
- **Data-driven everything** — no hardcoded game logic. Units, buildings, resources, tech trees,
  win conditions, combat rules are all data creators edit without code.
- **Layered complexity** — every system has a simple mode (presets/dropdowns/wizards) and an
  advanced mode (data editing, visual scripting, raw JSON). Progressive disclosure.
- **Composition over inheritance** — a "healer" is a ranged unit + heal ability + support AI, not
  a special class. Complexity emerges from combining orthogonal components.

## 2. Target audience

Three player archetypes (intentionally designed for):
- **Commanders** — pure players. Discover, play vs AI/humans, compete, experience stories. Never
  open the editor. The majority (~0.2% of DAU create content, per Fortnite Creative data).
- **Architects** — builders. Paint terrain, place units, define factions, wire triggers, publish.
  The lifeblood of the platform.
- **Tinkerers** — bridge both. Play extensively, then build informed by play. Produce the
  highest-quality content.

Skill level spans casual (soft-counter defaults) to competitive/expert (hard counters, JSON, visual
scripting). Tools must not overwhelm beginners while preserving depth for experts.

## 3. Platform and delivery

- **Target platform:** PC desktop (Windows primary; Linux for dedicated servers and export).
- **Engine:** Godot 4.6.2 stable, .NET build. C# / .NET 8+ (GDD references .NET 9 AOT as a future
  target; current build is .NET 8).
- **No web export** (C# web export unsupported; irrelevant for a PC RTS).
- **Distribution:** Steam (Early Access → 1.0). UGC distribution via **mod.io** content browser.
- **Dedicated servers:** Godot headless export, Linux, Docker; detected via
  `DisplayServer.GetName() == "headless"`.

## 4. Core features

**Shipped / code-complete (Phases 0–4):**
- ECS-inspired simulation (SoA arrays, free list, custom 16.16 FixedPoint, deterministic) +
  MultiMesh presentation. SpatialHash collision. NavigationServer3D pathfinding + FlowFieldBridge
  (deterministic). Combat (damage/armor matrix, projectiles, splash, feedback "juice").
- 6 unit archetypes (Worker, Melee, Ranged, Siege, Air, Structure), command system, selection,
  formations, fog of war, resource/supply economy, base building + construction + tech tree +
  rally points, worker-placed buildings.
- Utility-AI opponent (Easy/Normal/Hard). Data-driven factions/units/scenarios via JSON.
- Creation suite basics: terrain editor (Terrain3D, sculpt/paint), entity placement, Play/Edit
  toggle, scenario JSON save/load, second faction (Iron Pact), multiple skirmish maps.
- Multiplayer: deterministic lockstep, adaptive input delay, replays (`.chmr`), dedicated server,
  spectator mode, Nakama matchmaking, match chat.
- UGC: in-game content browser, mod.io integration, settings system, main menu, audio system.
- AI features: LLM trigger scripting + AI-assisted map generation (Claude API, Ollama fallback).

**In progress / needs testing (Phase 5):** Utility AI smoke test, adaptive input delay LAN test,
LLM trigger system smoke test, AI map generator smoke test. (See `Testme.md` / `Snapshot.md`
checklists.)

**Planned (Phase 5 → 1.0):** audio assets drop-in, mod.io inspector setup, P2.4 LAN test, Iron
Pact art (replace box placeholders), terrain texture painting, AI balance analysis tools,
performance optimization pass, advanced editor features (particle/sound triggers), Linux export,
1.0 release.

**Explicit exclusions / out of scope:**
- Free-to-play / microtransactions — premium one-time purchase only.
- Client-side / kernel-level anti-cheat — out of scope (server-authoritative Tier 1 only).
- Mobile and console — PC-focused.
- More than ~2 default resources — dual resource (Ore + Crystal) + supply is the deliberate sweet
  spot (creators may add more via data).

## 5. Differentiation

- **Platform, not a game** — competitors ship a fixed RTS; Chimera ships the toolset + a good
  default game. Heir to the Warcraft III World Editor lineage, modernized.
- **AI-assisted creation** — LLM-generated triggers and maps, AI 3D asset pipeline (low-poly,
  reliably generatable). Lowers the creation barrier dramatically.
- **Solo-dev "ship small, grow over years"** discipline (Dwarf Fortress / RimWorld / Factorio /
  Mindustry model). The explicit anti-pattern is **Stormgate**, which launched PvP + co-op +
  campaign + editor + F2P simultaneously and failed.

## 6. Technical preferences / constraints

These are firm; honor them in any architecture/story work:
- **Simulation vs presentation separation is sacred** — sim layer is pure C# data (SoA arrays),
  **no Godot Nodes per entity**. Presentation reads sim arrays.
- **MultiMeshInstance3D for unit rendering**, never per-unit MeshInstance3D. Two MultiMesh nodes
  per faction (separate colors).
- **FixedPoint (custom 16.16) math** in any simulation code that must be multiplayer-deterministic.
  Not a NuGet lib.
- **NavigationServer3D direct API** (no NavigationAgent3D nodes). FlowFieldBridge is the live,
  deterministic path bridge; `PathRequestSystem` stays as an unused fallback.
- **Data-driven & creator-extensible** — every new system must be expressible as JSON data, not
  hardcoded. No game logic baked into code paths creators can't reach.
- **Composition over inheritance.**
- C# source in `godot/src/` organized by system. PascalCase classes, camelCase locals,
  SCREAMING_CASE constants. `#nullable enable` per-file. Comment public methods and complex logic.
- LLM features default to **Claude API** (Anthropic) with **Ollama** local fallback. API keys set
  via Godot Inspector on MainScene (`AnthropicApiKey`), never hardcoded.
- Performance target: 500–2,000 units at 60 FPS; hot path can migrate to C++ GDExtension later
  without changing architecture.

## 7. Scope and scale

- **Roadmap:** 19–31 months, six phases (0–5), each ending in a playable build. Currently in
  **Phase 5 — Polish, AI features & 1.0** (months 25–31).
- **MVP already shipped** as Early Access (Phase 2). 1.0 is the remaining milestone.
- **Match scale:** 1v1 the primary competitive format; 500–2,000 units per match.
- **Solo developer** — favor scope discipline, incremental shippable slices, and reuse of existing
  systems over large rewrites.

## 8. Monetization / business model

- **Premium one-time purchase**, $15–25 Early Access price. No microtransactions, no F2P.
- One-time purchase funds continued solo development.
- Success criteria at 1.0: Steam reviews >80% positive, stable concurrent base, active creation
  community, revenue sustaining continued development.

## 9. Design / UX

- **Art direction:** stylized **low-poly** — clean geometric shapes, soft colors, slight
  cel-shading. Chosen because AI 3D tools generate low-poly most reliably, it stays consistent
  across hundreds of generated assets, renders efficiently at RTS unit counts, and reads clearly
  when zoomed out. Reference point: between Mindustry's clarity and Northgard's painterly warmth.
- **UX principle:** layered complexity / progressive disclosure (simple presets up front, raw data
  and visual scripting underneath).
- **AI art tooling:** Hunyuan3D or Tripo AI for GLB generation (final tool choice is an open
  decision — see below).

## 10. Domain knowledge / terminology

- **Roles:** Commanders (play), Architects (build), Tinkerers (both).
- **Scenario:** a creator-authored map + rules + triggers, the core UGC unit.
- **Scenario Director:** trigger/event system (ECA pattern; LLM-assisted authoring).
- **Archetypes:** Worker, Melee, Ranged, Siege, Air, Structure (all else is composition).
- **Combat:** damage-type × armor-type matrix; `final = base × matrix[dmg][armor] − armor_value`.
  Damage types: Normal, Pierce, Siege, Magic, Hero. Armor: Unarmored, Light, Medium, Heavy,
  Fortified, Hero. Soft counters (0.7–1.3) default; hard counters (0.25–3.0) for competitive.
- **Economy default:** Ore (abundant) + Crystal (scarce) + dynamic supply cap (base 10 + 10 per
  alive CommandCenter).
- **Factions:** Alpha (baseline) and Iron Pact (beta — +HP/+armor/-speed reskin of the same 7
  roles, asymmetric via stats).
- **Sim cadence:** 30 ticks/sec fixed timestep. Lockstep input delay starts at 4 ticks, adapts
  via RTT (Ping/Pong + DelayProposal), clamped [2, 12].
- **Key data formats:** faction/unit JSON, scenario JSON (`[Export] ScenarioPath` on MainScene),
  `.chmr` replay binary, trigger JSON.
- **External services:** Steam (distribution), mod.io (UGC), Nakama (matchmaking), Anthropic
  Claude API + Ollama (LLM features).
- **Reference docs:** `Project_Chimera_GDD.md` (source of truth), `Snapshot.md` (current state),
  `Testme.md` (smoke tests), `docs/` (architecture, art-style-guide, server-deploy, modio-setup).

---

## Workflow proxy defaults (how to answer common BMAD/GDS questions)

- **Greenfield vs brownfield?** Brownfield. Plan against the existing codebase.
- **Primary platform / engine?** Godot (C#/.NET), PC desktop.
- **User skill level / game-dev experience?** Intermediate.
- **Project knowledge location?** `docs/` plus root design docs (GDD, Snapshot).
- **Output location?** `_bmad-output/` (planning-artifacts, implementation-artifacts).
- **When choosing a menu option:** pick the one consistent with the design pillars (data-driven,
  composition, layered complexity, the three-question filter) and the technical constraints in §6.
- **When asked to add a feature:** prefer data-driven/creator-extensible implementations and reuse
  of existing systems (EntityWorld SoA, BuildingStore, ScenarioData, FlowFieldBridge, etc.) over
  new bespoke subsystems.
- **Determinism:** any sim/gameplay logic touched by multiplayer MUST use FixedPoint and avoid
  Godot types in the sim layer.

## Open design decisions (escalate only if a workflow forces a choice)
- **AI 3D art tool:** Hunyuan3D vs Tripo vs other — not yet chosen. Iron Pact art (8 GLBs) still
  uses box placeholders pending this.

## Session Escalations
<!-- Append workflow questions not covered above, with Alec's answers, here. -->
