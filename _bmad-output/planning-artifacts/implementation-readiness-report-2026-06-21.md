---
stepsCompleted: ['step-01-document-discovery', 'step-02-gdd-analysis', 'step-03-epic-coverage-validation']
documentsUnderReview:
  - type: GDD
    path: 'Project_Chimera_GDD.md'
  - type: PRD
    path: '_bmad-output/planning-artifacts/prds/prd-Project_Chimera-2026-06-05/prd.md'
  - type: Architecture
    path: '_bmad-output/game-architecture.md'
  - type: Epics
    path: '_bmad-output/planning-artifacts/epics.md'
  - type: UX
    path: '_bmad-output/planning-artifacts/ux-designs/ux-Project_Chimera-2026-06-20/'
date: '2026-06-21'
project: 'Project_Chimera'
---

# Implementation Readiness Assessment Report

**Date:** 2026-06-21
**Project:** Project_Chimera

## 1. Document Inventory

Document set confirmed by user (Alec) on 2026-06-21.

| # | Type | File | Size | Modified | Role in assessment |
|---|------|------|-----:|----------|--------------------|
| 1 | GDD | `Project_Chimera_GDD.md` | 63.8 KB | 2026-04-07 | Vision / design source of truth |
| 2 | PRD | `…/prds/prd-Project_Chimera-2026-06-05/prd.md` | 40.8 KB | 2026-06-05 | **Requirements baseline — 60 numbered FRs (§4.1–§4.11) + 6 NFRs (§4.12)** |
| 3 | Architecture | `_bmad-output/game-architecture.md` | 230 KB | 2026-06-21 | Forward 1.0 technical architecture |
| 4 | Epics & Stories | `_bmad-output/planning-artifacts/epics.md` | 261 KB | 2026-06-21 | 10 epics / 97 stories; FR references = 328 |
| 5 | UX | `…/ux-designs/ux-Project_Chimera-2026-06-20/` | — | 2026-06-20 | Finalized UX run (DESIGN.md + EXPERIENCE.md) |

### Duplicates resolved
- **Architecture:** chose `game-architecture.md` (230 KB, 2026-06-21) over the stale `_bmad-output/architecture.md` (12.7 KB, 2026-06-05). Old file excluded, not deleted.
- **UX:** chose `ux-Project_Chimera-2026-06-20` over the thin `ux-Project_Chimera-2026-06-05` run. Old run excluded, not deleted.

### Notes / risks carried forward
- The **GDD has zero numbered FRs**; all FRs live in the **PRD** (60 distinct: 52 base `FR-1…FR-52` + 8 lettered inserts `FR-7a/b/c/d/e, FR-12a, FR-38a, FR-49a`). Requirements traceability for this assessment runs against the **PRD**, cross-referenced to the GDD for vision alignment.
- ✅ **Count reconciled:** tracker/memory and the epics both say "60 FRs" and that is correct (52 + 8 lettered). The Step-2 extraction *verifier* miscounted the lettered inserts as 6 (→ reported 58); the true count is **60**. The FR list in §2.1 below is complete.
- The **PRD predates the FMA theme pivot** (PRD 2026-06-05; pivot decided 2026-06-21). Pivot is reportedly a content/vibe reskin with zero structural change — to be sanity-checked during analysis, not assumed.

---

## 2. Requirements Analysis (GDD + PRD)

_Extracted and verified 2026-06-21 via parallel extract → completeness-critic pass. Verifier verdict: **FAITHFUL AND COMPLETE** (confidence: high). All FR text below is verbatim from the PRD._

### 2.1 Functional Requirements — the coverage measuring stick (60 total: 52 base + 8 lettered)

**§4.1 Creation Suite — Unit & Hero Authoring** *(headline)*
- **FR-1** — An Architect can create, edit, duplicate, and delete a unit definition in-app without editing JSON, with all changes persisted to the scenario's data.
- **FR-2** — The Unit Card Editor presents stats, archetype, combat type (damage/armor), model assignment, and attached abilities in a single panel.
- **FR-3** — An Architect can assign a 3D model (or box placeholder) to a unit and preview it in-editor.
- **FR-4** — An Architect can compose a unit from the 6 archetypes plus orthogonal ability/behavior components (composition over inheritance — no subclassing).
- **FR-5** — An Architect can designate a unit as a **Hero**, configuring leveling curve, experience gain, and signature/ultimate abilities.
- **FR-6** — Simple mode offers preset/templated units; advanced mode exposes every authorable field.
- **FR-7** — Validation surfaces authoring errors inline before save/playtest (missing/invalid model, out-of-range/missing required stat, undefined ability reference, invalid archetype composition).
- **FR-7a** — Author **persistent artifacts** (WC3 save-code model): a per-scenario **persistence manifest** selecting which hero/unit/player attributes carry to the next custom game (level/XP, inventory, skill tree, currency). Per player.
- **FR-7b** — At game start players **load** their persisted profile as **deterministic initial state** (works in multiplayer; init-time data, never a mid-game snapshot). Creator can disable persistence (match-only).
- **FR-7c** — Online persistence: save profiles **stored/validated server-side** (not raw client save-codes) to prevent tampering. `[ASSUMPTION: confirm vs signed client save-codes.]`
- **FR-7d** — A **Save/Load Interface** (menu / **hero picker**), not text codes, to manage saved heroes; each entry shows hero icon + basic info. Creator enables/configures per scenario.
- **FR-7e** — From the Save/Load Interface a player can load/save/overwrite (with confirmation) profiles; multiple saved heroes per player.

**§4.2 Creation Suite — Ability / Skill Authoring** *(headline)*
- **FR-8** — Create an active ability with targeting type, cost, cooldown, and one+ effects, in-app without code.
- **FR-9** — Create a passive ability (auras, on-hit effects, stat modifiers) with trigger conditions.
- **FR-10** — Compose an ability from multiple effect primitives (advanced) and from configurable presets (simple).
- **FR-11** — Abilities attach to units/heroes (FR-4, FR-5) and appear on the command card at runtime.
- **FR-12** — Ability definitions are data-driven, deterministic, server-validatable (no float math, no arbitrary scripts).
- **FR-12a** — Abilities/units carry a **combat-feedback profile** (hit particles, impact sound, screen shake, hit-freeze, death effect); a tuned default ships, overridable per unit/ability.

**§4.3 Creation Suite — Building, Tech Tree & Economy Authoring**
- **FR-13** — Create/edit building definitions (stats, production options, cost/time, tech prerequisites) in-app.
- **FR-14** — Build a faction's tech tree visually (drag dependencies); runtime gates production by it.
- **FR-15** — Define a scenario's resource set as data (type, model, starting amount, gather model), beyond the two defaults.
- **FR-16** — Configure the supply/cap model per scenario.

**§4.4 Creation Suite — Faction Definer**
- **FR-17** — Create a faction by assembling authored units/heroes/buildings/tech-tree, name, color, starting conditions via a guided multi-step flow.
- **FR-18** — Assign an AI preset to a faction so it's playable against/with the AI.
- **FR-19** — A completed faction is immediately selectable in playtest and skirmish/multiplayer setup.
- **FR-20** — The two shipped factions (Alpha, Iron Pact) are valid Faction Definer outputs; **Iron Pact upgraded from reskin to genuinely asymmetric.** *Acceptance:* ≥1 unique core mechanic + roster differing in role/stat profile, validated in playtest.

**§4.5 Creation Suite — Map / Terrain Editor** *(polish)*
- **FR-21** — Sculpt and texture-paint terrain in-app; painted textures persist on save/load.
- **FR-22** — Place entities, start positions, resource nodes; set win conditions on the map (existing — verify to ship bar).

**§4.6 Creation Suite — Trigger / Rules System (Rich Declarative DSL)** *(headline)*
- **FR-23** — Author scenario logic as ECA triggers in-app (built — verify to ship bar).
- **FR-24** — The DSL supports variables (typed, scoped), arithmetic/boolean expressions, arrays/collections, conditional loops, and timers.
- **FR-25** — Define custom events and raise them from triggers (decoupled game-logic modules).
- **FR-26** — Define custom runtime UI elements (text, counters, buttons) driven by triggers (TD waves, RPG dialog, scoreboards).
- **FR-27** — All DSL constructs are deterministic, fixed-point, server-validatable before MP run (no scripting escape hatch).
- **FR-28** — Author the same logic at T1 (preset), T2 (ECA), T3 (visual node graph), or T4 (natural language); tiers interoperate on one underlying DSL representation.

**§4.7 AI-Assisted Creation (cross-cutting)** *(headline)*
- **FR-29** — Select/configure an LLM provider (OpenRouter, Claude, local Ollama) via settings API key; never hardcoded/committed. *Note: includes migrating key storage from the single `AnthropicApiKey` MainScene export into `SettingsData`/`SettingsManager`.*
- **FR-30** — Generate triggers from a natural-language prompt; review/edit before applying (built — verify, extend to new DSL constructs).
- **FR-31** — Generate a map from a prompt with validation before load (built — verify). *Note: relax/parameterize the 7-pass validator's RTS-only clamps for TD/RPG.*
- **FR-32** — Generate a unit/ability/hero/faction draft (stats+name+lore) from a prompt, as editable data. *Note: generation must not assume RTS-only conventions.*
- **FR-33** — Request **AI balance analysis** of a faction/scenario; receive actionable, editable suggestions (new build).
- **FR-34** — With no provider/key, AI features degrade gracefully with a clear message; suite remains fully usable manually.

**§4.8 Share & Discover (UGC)** *(verify + polish)*
- **FR-35** — Package a scenario as `.chimera.zip` and publish to mod.io from in-app (verify end-to-end).
- **FR-36** — Pass the **proof-of-play gate** (win your own scenario) before publishing.
- **FR-37** — Browse, search, tag-filter, sort, subscribe to, and rate scenarios in the content browser.
- **FR-38** — Published packages are content-hashed and integrity-verified on download.
- **FR-38a** — **Creators retain full ownership of their content** (platform takes only a non-exclusive host/distribute right; no IP claim). Surfaced at publish time.

**§4.9 The Showcase Game & Multiplayer** *(verify + harden + content)*
- **FR-39** — Two players complete a full MP match on **separate machines (LAN)** with checksums in sync 300+ ticks, zero desync — the **P2.4 LAN determinism test must pass** (never run; #1 risk).
- **FR-40** — Matchmake (Nakama) or join via LAN/lobby, in-game chat, `.chmr` replay saved **and viewable/shareable**. *Target up to 8 players;* `[ASSUMPTION: party grouping is 1.0]`. **Spectator mode** in the verification set.
- **FR-41** — Adaptive input delay verifiably adjusts to RTT on real networks (4→2 on LAN, clamped [2,12]) without desync.
- **FR-42** — The two shipped factions are balanced. *Acceptance:* neither win rate outside ~45–55% across AI self-play/playtest.
- **FR-43** — A solo player can play skirmish vs the AI across difficulties on the shipped maps.

**§4.10 Verification Floor & Quality** *(infrastructure)*
- **FR-44** — Automated test suite (GdUnit4) covers deterministic sim, combat formula, economy, pathfinding; runnable headless without Godot.
- **FR-45** — The four unverified systems (Utility AI, Adaptive Input Delay, LLM Trigger System, AI Map Generator) each pass smoke-test checklists.
- **FR-46** — A performance pass confirms 500–2,000 units @ 60 FPS render / 30 Hz on representative scenarios.
- **FR-47** — `[ASSUMPTION]` Determinism regression-guarded by a replay/checksum test in CI.

**§4.11 Release Readiness** *(finishing)*
- **FR-48** — Audio assets (`.ogg`) present and wired through the audio system.
- **FR-49** — Iron Pact's 8 placeholder GLBs replaced with real art (external Hunyuan3D/Tripo pipeline).
- **FR-49a** — An **art-style consistency layer** (≥ shared material library + global post-process/cel-shading shader) keeps AI/creator assets coherent. `[NOTE FOR PM: confirm 1.0 enforcement depth.]`
- **FR-50** — A Linux export (dedicated-server and/or client) builds and runs.
- **FR-51** — Accessibility 1.0 **baseline**: remappable keys, colorblind-safe team colors, UI scaling, subtitles for VO.
- **FR-52** — Ships to **Steam and a direct DRM-free channel**; both pipelines release-ready.

### 2.2 Non-Functional Requirements

**Canonical PRD NFRs (§4.12) — exactly 6:**
- **NFR-1 — Fast edit→play loop** (realizes UJ-3): editor→playtest round-trip near-instant (≤ a few seconds), no app restart.
- **NFR-2 — Creator-experience / discoverability:** hover tooltip on every field/button/panel; a guided "Your First Scenario" onboarding; basic playable scenario in < 15 min.
- **NFR-3 — Editor invisible to Commanders:** players who never create are never exposed to authoring UI; creation surfaces opt-in.
- **NFR-4 — Determinism is sacred (system-wide):** no feature introduces float gameplay math, wall-clock dependence, or nondeterministic iteration into the sim; AI/LLM runs only in the authoring layer.
- **NFR-5 — Performance:** 500–2,000 units @ 60 FPS render / 30 Hz sim holds on shipped + community scenarios (verified by FR-46).
- **NFR-6 — Server-validatable content:** every shareable construct is statically validatable so the server rejects malformed/cheating scenarios before they run (bounds DSL expressiveness).

> ⚠️ **Verifier caveat:** the broader extraction bundled **75** quality-attribute entries (success-metric hard gates, non-goals, feature-specific NFRs, and ~30 GDD-derived constraints). That is a useful *constraints inventory*, but downstream must treat the **true PRD NFR count as 6**. The full 75-entry inventory (by area: determinism, performance, security, reliability, platform, usability, scalability, compliance) is preserved in the Step-2 workflow output and the GDD §6/§8.

### 2.3 GDD Design Pillars (verbatim)
1. **Data-driven everything** — no game logic hardcoded; units/buildings/resources/tech/win-conditions/combat are data creators modify without code.
2. **Layered complexity** — every system has a simple mode (presets/wizards) and an advanced mode (raw JSON / visual scripting); progressive disclosure.
3. **Composition over inheritance** — a "healer" = ranged unit + heal ability + support AI; complexity emerges from orthogonal components.
4. **The three-question filter (Create / Share / Discover)** — every feature must serve one; serving none gets cut.
5. **Ship small and grow** — a compelling standalone RTS first; grow into a platform over Early Access (explicit anti-Stormgate).

### 2.4 GDD Design Requirement Areas (mechanics spec — full text in GDD §3/§6/§8)
Resource & Economy · Combat (damage/armor matrix) · Unit Framework (6 archetypes) · Movement & Commands · Fog of War · Win Conditions & Scenario Logic (T1–T4) · Scenario Director / Trigger Engine · Natural-Language Trigger Authoring · Creation Suite MVP Tools · Creation Suite UX Principles · Entity Editor UX · Multiplayer Network Model · Determinism Requirements · Input Delay & Feedback · Transport & Server Infrastructure · Lobby & Matchmaking (Nakama) · Content Verification & Anti-Cheat · UGC Pipeline (mod.io) · Content Package Schema · Community Features & Discovery · Asset Pipeline (AI 3D) · Asset Pipeline (AI 2D) & Style Consistency · AI Integration / LLMService · AI Features by Player Type · Runtime Editor / Edit-Play Loop · Pathfinding Architecture.

### 2.5 GDD ↔ PRD Coverage-Gap Candidates (the key finding)
Design intentions present in the GDD that are **not owned by any numbered PRD FR** — candidate coverage gaps for Step-3 to test against the epics. (Severity = how much it undercuts a design pillar / showcase feel.)

| # | Gap candidate | Severity | Note |
|---|---------------|----------|------|
| 1 | **Full RTS command vocabulary** — Patrol, Hold Position, Follow, Attack-Move (beyond Move/Stop/Attack) as a framework guarantee | **HIGH** | PRD itself flags formation/control-groups as an *unverified assumption*; no FR owns the individual commands |
| 2 | **Formation movement** (line/box/wedge) + priority-based yielding | **HIGH** | Most under-pinned "Built Foundation" claim; PRD admits it may be stubbed |
| 3 | Optional **upkeep / population-tax** economy model (WC3 gold-tax) | MEDIUM | FR-15/16 cover resources + supply but never upkeep — narrows "any RTS economy" |
| 4 | Per-resource **collection models** (GATHER / INCOME / STREAMING) + node fields (max_gatherers, requires_structure) | MEDIUM | FR-15 says "gather model" generically; passive-income/streaming economies silently unbuildable |
| 5 | Authorable **unit tags** (organic/mechanical/magical) on UnitDefinition | MEDIUM | Substrate for ability targeting/counters; FR-1..7 never mention tags |
| 6 | **Projectile-vs-hitscan flag** + per-unit **splash radius** as creator-authorable fields | MEDIUM | Simulated but not exposed in editor — partial regression of no-JSON North Star |
| 7 | **Command rate-limiting** (anti-spam) + Tier-2 anti-cheat (replay review, anomaly detection) | MEDIUM | GDD names rate-limiting as Tier-1 must-have for a UGC abuse surface |
| 8 | **AI adaptivity** — player-pattern tracking + Tinkerer decision-weight debug overlay | MEDIUM | PRD has static utility-AI only; may affect FR-42 balance & single-player fun |
| 9 | Explicit **win-condition preset set** (Annihilation, Landmark Destruction, Score, KotH, Assassination, Timed Survival) | LOW | FR-22/28 say "presets" generically; could ship only Annihilation and still claim done |
| 10 | **Height-advantage vision** toggle in fog of war | LOW | Classic RTS lever; not referenced in PRD |
| 11 | **Patch PCK delta-encoding** for small UGC update downloads | LOW | Not in UGC FRs; 1.0 may ship full re-downloads |
| 12 | Richer **discovery-quality** (weighted rating algorithm, auto-hide-on-reports) | LOW | **Conscious [v2] deferral** in PRD — not an oversight; flagged for deliberate sign-off (Discover is a pillar) |

**Freshness flag (not a coverage gap):** the GDD (2026-04-07) uses pre-pivot example content (orc_warrior/Footman templates, generic fantasy) and the PRD's factions are codenamed **Alpha / Iron Pact** — both predate the **FMA-vibe pivot** (Rebel Alchemists vs Homunculus Legion, 2026-06-21). Mechanics/role-skeleton unaffected (zero structural change), but FR-20/FR-49 faction naming and any lore/example assets should be re-read against the new theme before art/content work.

### 2.6 GDD Completeness Assessment
- **PRD as requirements baseline: complete and well-formed.** 60 FRs (52 contiguous base `FR-1…FR-52` + 8 lettered inserts), no gaps/dupes, every FR labelled and verbatim-extractable; 6 clean NFRs. The PRD is explicitly brownfield/gap-framed ("does not re-specify systems already built"), so many GDD mechanics are intentionally absent from the FRs because §4.0 claims them as built.
- **Principal risk:** several "Built Foundation" claims (command vocabulary, formation movement, unit tags, projectile/splash authoring) are **vague labels the PRD never decomposes into verifiable FRs** — and the PRD itself flags some as assumptions. These are the highest-value items for Step-3 epic-coverage and later code-verification.
- **Count reconciled:** tracker, epics, and PRD all agree on **60** FRs (the Step-2 verifier's "58" was a lettered-insert miscount, corrected here).
- **Verdict:** the requirement set is **faithful and complete enough to serve as the Step-3 coverage measuring stick** (verifier confidence: high).

---

## 3. Epic Coverage Validation

_Method: extracted the epics' declared FR Coverage Map, then **independently** re-audited all 10 epics (one auditor per epic, blind to the coverage claim) to confirm each FR is substantively delivered, and probed the 12 Step-2 design-gap candidates against the actual story ACs._

### 3.1 Coverage Statistics
- **Total PRD FRs:** 60 (52 base + 8 lettered)
- **FRs covered in epics:** 60
- **FR coverage: 100% (60/60)** — independently confirmed. The epics' declared "FR coverage 60/60" claim **holds** under blind re-audit. Every FR is delivered by ≥1 story with concrete acceptance criteria; **no FR is missing, and none is merely name-dropped without a story attempting it.**
- Stories: ~95–97 across 10 epics. NFR-1…NFR-6 are each substantively touched somewhere (though often enforced via AC content rather than tagged by NFR id).

### 3.2 Coverage Matrix (by epic — all FRs substantiated)

| Epic | FRs owned | Status |
|------|-----------|--------|
| 1 — Trustworthy Foundation & Desync-Free MP | FR-39, FR-44, FR-45, FR-47 | ✓ all delivered (FR-44/45 caveats below) |
| 2 — Living Combat (Effect Engine, Abilities) | FR-8, FR-9, FR-10, FR-11, FR-12, FR-12a | ✓ strong |
| 3 — Author Units & Heroes (Save/Load) | FR-1…FR-7, FR-7a, FR-7b, FR-7d, FR-7e | ✓ strong (offline rail; FR-7c → Epic 9) |
| 4 — Buildings, Tech Trees & Economy | FR-13, FR-14, FR-15, FR-16 | ✓ strong (FR-15 scope-narrowed, see §3.4) |
| 5 — Faction Definer & Showcase Factions | FR-17, FR-18, FR-19, FR-20 | ✓ (FR-20 single-point-of-failure on Epic 2) |
| 6 — Map & Terrain Editor | FR-21, FR-22 | ✓ strong |
| 7 — Rich Trigger DSL & Custom UI | FR-23…FR-28 | ✓ (FR-28 T1/T4 cross-epic, see below) |
| 8 — AI-Assisted Creation | FR-29…FR-34 | ✓ strong |
| 9 — Share, Discover & Multiplayer | FR-35, FR-36, FR-37, FR-38, FR-38a, FR-7c, FR-40, FR-41 | ✓ (FR-40 spectator/parties thin) |
| 10 — Release Readiness | FR-42, FR-43, FR-46, FR-48, FR-49, FR-49a, FR-50, FR-51, FR-52 | ✓ (FR-43 verify-only; perf/a11y AC caveats) |

### 3.3 Missing Requirements (strict FR sense)
**None.** All 60 PRD FRs trace to at least one story. The epics' coverage map is accurate and the lettered inserts are all placed.

### 3.4 ⭐ The Real Finding — GDD design intentions owned by NO FR
The PRD declares whole subsystems as "Built Foundation" without decomposing them into FRs. So these GDD requirements are **invisible to an FR-coverage check** — yet a blind probe of the stories shows they are **absent or only adjacent** in the epics. These are *not* FR-coverage gaps; they are **requirements that were never turned into FRs.** This is the highest-value output of the readiness check.

| # | GDD design intention (no owning FR) | In stories? | Severity | Finding |
|---|--------------------------------------|-------------|----------|---------|
| 1 | **Full RTS command vocabulary** — Patrol, Hold Position, Follow, Attack-Move *behaviors*, + the "creators may disable per-unit but never remove" framework guarantee | **Partial** | **HIGH** | Only **keybindings** exist (UX-DR66 default binds; Story 10.8a remap UI binds keys to actions). **No story implements the command behaviors** (Patrol waypoint-loop, Hold no-chase, Attack-Move acquire-en-route). "Follow" absent even at the keybinding layer. The framework-guarantee/disable-not-remove contract has zero representation. PRD inventory itself flags this **VERIFY**. |
| 2 | **Formation movement** (line/box/wedge) + **priority-based yielding** | **No** | **HIGH** | Appears only in "Built Foundation reference — NOT 1.0 scope" with "VERIFY before treating as done — *may become a §4.10 task*" — **but no §4.10 story ever picked it up.** Story 1.11's smoke-tests do not include it. Zero ACs for shapes or yielding. |
| 3 | **Per-resource collection MODELS** (GATHER / INCOME / STREAMING) + node fields (max_gatherers, requires_structure) | **Partial** | MEDIUM | FR-15/Story 4.3 delivers the data-driven resource *set* + cost maps, but the three collection behaviors and node fields are **name-dropped only** — INCOME/STREAMING/max_gatherers/requires_structure appear **nowhere**. Passive-income / streaming economies silently unbuildable. |
| 4 | **Authorable unit tags** (organic / mechanical / magical) | **No** | MEDIUM | Fully absent. The damage/armor matrix is a *separate* axis the GDD lists alongside tags, not in place of them. Removes a whole ability-targeting/counter dimension. |
| 5 | **Projectile-vs-hitscan flag** + per-unit **splash radius** as authorable fields | **Partial** | MEDIUM | Engine *has* projectiles+splash; ability effect-graph has FireProjectile/SearchArea leaves. But **no per-unit authorable projectile/hitscan toggle or first-class splash field**; "hitscan" never appears. Authorable only via generic effect-graph composition. |
| 6 | **Command rate-limiting** (Tier-1 anti-spam) | **No** | MEDIUM | GDD lists it as **must-have** Tier-1 anti-cheat; **no AC throttles the command bus.** (Tier-2 measures — replay review, anomaly detection — are *correctly* deferred by design; no action.) |
| 7 | **AI adaptivity** — player-pattern tracking (rush/turtle counters) + Tinkerer decision-weight debug overlay | **No** | MEDIUM | Both halves absent; AI opponent stories treat Utility AI as static. *Note: GDD itself flags pattern-tracking as an unresolved open question — may be an intended descope, but no epic records that decision.* |
| 8 | **Win-condition preset set** (Annihilation, Landmark Destruction, Score, KotH, Assassination, Timed Survival) | **No** | MEDIUM | Only a 2-option setter (EliminateAllUnits / DestroyAllBuildings, Story 6.4) is scoped. The 6 named presets appear nowhere; would have to be hand-built via the DSL. Starves the zero-code Create on-ramp. |
| 9 | **Height-advantage vision** toggle in fog of war | **No** | MEDIUM | Absent. **Bigger structural flag:** there is **no dedicated Fog-of-War story at all** — the entire FoW subsystem (incl. *server-enforced visibility*, a stated anti-cheat/security guarantee) is treated as built with no verification story. |
| 10 | Optional **upkeep / population-tax** economy | **No** | LOW | Supply/cap (FR-16) ≠ income-scaling tax. Constructible via DSL but not a first-class authorable model. |
| 11 | **PCK delta-encoding** for incremental UGC updates | **No** | LOW | mod.io full-file download model scoped — the opposite of incremental. 1.0 may ship full re-downloads. |
| 12 | Richer **discovery-quality** (weighted rating, auto-hide-on-reports) | **No** | LOW | **Conscious [v2] deferral** — epics explicitly delegate browse/rate to mod.io-native only. No action; flagged for deliberate sign-off (Discover is a pillar). |

### 3.5 AC-quality / sufficiency caveats (carry to Step 5 — Story Quality)
The FRs are *covered*, but several have thin or non-objective acceptance criteria worth tightening:
- **FR-44** — Epic 1 delivers sim + combat-formula tests but **no explicit economy or pathfinding unit-test AC** (PRD names all four domains). FR-44 is ~50% substantiated against its own wording. **Recommend a named economy + pathfinding test slice.**
- **FR-45** — Story 1.11 leans on undefined "smoke-test checklist" artifacts and vague "produces decisions deterministically" for 2 of 4 systems (self-flagged).
- **Validator staging** — the fail-closed `ScenarioValidator` ships **shadow/log-only on master**; the actual fail-closed flip is only "a release-branch toggle." Within M1 the server-rejection (NFR-6) guarantee is **staged, not delivered**.
- **FR-28** — Epic 7 delivers the shared IR + T2 + T3, but the **T1 preset tier is thin** (no dedicated ACs) and **T4 (natural language) is Epic 8** — "all four tiers exist" closes only cross-epic.
- **FR-20** — strong in aggregate but a **single point of failure**: depends on Epic 2's modifier system; Story 5.4 is ambiguous (build vs verify) and 5.7's "observably asymmetric" AC is subjective.
- **FR-43 / FR-42 / FR-46 / FR-51** — verify-only or time-based/subjective ACs ("Hard observably more aggressive", "<12 min faction", perf "shortfall documented" with no hard floor, "representative hardware" unspecified). Need objective metrics.
- **FR-40** — spectator-mode verification clause and full parties UI are thin/deferred-to-fast-follow.

### 3.6 Step-3 Verdict
- ✅ **FR coverage is genuinely 100% (60/60)** and the epics' map is accurate — strong planning hygiene.
- ⚠️ **2 HIGH-severity design gaps** (RTS command-vocabulary *behaviors*; formation movement + yielding) sit outside the FR system and are unscoped in 1.0 — and both directly affect whether the showcase reads as a *real* RTS. The PRD itself flagged both as "VERIFY before done." **These are the must-resolve items before production.**
- ⚠️ **7 MEDIUM gaps** narrow the "build *any* RTS" creation promise (collection models, unit tags, projectile/splash authoring, win-condition presets, height-advantage/Fog-of-War verification, AI adaptivity, command rate-limiting).
- The AC-quality caveats (§3.5) are real but belong to the later story-quality step.

**Recommended action:** decide per HIGH/MEDIUM gap whether to (a) add a story/FR for 1.0, (b) explicitly descope to post-1.0, or (c) confirm it's genuinely already-built and add a *verification* story. Right now several are silently assumed-built with no verification.

---
