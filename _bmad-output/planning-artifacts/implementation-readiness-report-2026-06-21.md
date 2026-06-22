---
stepsCompleted: ['step-01-document-discovery', 'step-02-gdd-analysis', 'step-03-epic-coverage-validation', 'step-04-ux-alignment', 'step-05-epic-quality-review', 'step-06-final-assessment']
readinessStatus: 'NEEDS WORK'
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
**Assessor:** Claude (Game Producer / Scrum Master)

> ## 🟠 Overall Readiness: **NEEDS WORK** (strong foundation, surgical fixes required)
> FR coverage is **100% (60/60, independently confirmed)**, cross-epic independence holds, and the architecture is excellent below the UI line. But ship-blocking refinements remain: ~~**2 critical epic-structure defects**~~ (✅ **fixed 2026-06-21**), **2 HIGH unscoped GDD design intentions** (RTS command-vocabulary *behaviors*; formation movement) the PRD itself flagged as unverified, **2 HIGH architecture presentation gaps** (edit→play transition/measurement; editor-shell + NFR-3 mode separation), and a **stale GDD** (missing FR-7/FR-26 + the FMA pivot). None are correctness/coverage blockers — but they must be decided before production. **See §6 for the prioritized action list.**

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

## 4. UX Alignment

_UX document: **FOUND** — `ux-Project_Chimera-2026-06-20/` (DESIGN.md + EXPERIENCE.md), the finalized run. 14 screens/surfaces, 6 player journeys (mapped to Architect/Tinkerer/Commander), a full design system. Validated UX↔GDD and UX↔Architecture in parallel._

### 4.1 UX ↔ GDD — Verdict: **Substantially aligned** (the GDD is the stale artifact)
All 6 UX journeys map cleanly to GDD personas and core systems (edit→play loop, faction wizard, deterministic lockstep, consolidated WC3 unit editor). The misalignments are **UX/PRD ahead of the GDD**, not contradictions:

**UX surfaces with NO GDD specification (GDD is behind):**
| UX surface | Severity | Note |
|------------|----------|------|
| **FR-7 Persistent heroes** — save/load picker, cross-match XP, server-validated profiles | **HIGH** | GDD has Hero only as an *in-match* archetype; no persistent cross-scenario profile concept anywhere. Largest single UX↔GDD gap. **(Present in PRD + epics + UX — only the GDD lacks it.)** |
| **FR-26 Custom Runtime UI Builder** — WYSIWYG widget canvas, data binding, trigger-driven visibility | **HIGH** | No creator-authored runtime-UI builder in the GDD. **(Present in PRD + epics + UX — only the GDD lacks it.)** |
| NFR-1 ≤2s edit→play budget | MEDIUM | GDD mandates "instant" toggling but never commits to a numeric ≤2s SLA. |
| FR-51 accessibility floor specifics (colorblind/scaling/contrast/subtitles/remap) | MEDIUM | GDD has only one roadmap line ("Accessibility features"); UX is far more concrete. |
| Tooltips-everywhere, per-surface Simple/Advanced, lobby hash readouts, passive abilities + deeper DSL | LOW | Consistent in spirit; UX is ahead in detail. |

**GDD features the UX under-represents (committed in GDD, thin/absent in UX):**
- **Natural-language (Tier-4) trigger authoring UI** — a flagship GDD feature; only a generic "Transmuting…" spinner gestures at it. *(No dedicated generate→preview→confirm/fuzzy-match surface.)*
- **AI map-gen + AI balance-analysis surfaces** — only an "AI Gen" toolbar tab; no dedicated journeys.
- Terrain Editor / Resource-Node / Start-Position surfaces — folded into tool tabs, lightly covered.
- Content browser: **proof-of-play gate, min-quality gate, Play-Now auto-lobby, creator profiles, report-to-moderation** — partial.
- Single-player save/load + campaign mission-select/briefing UI.
- **Replay + spectator UI** — no surface at all.
- Input-delay / game-speed indicator UI; lobby "Update Required" one-click mismatch recovery.

**Contradictions (all LOW):** campaign count UX "/12" vs GDD "5-8 missions" (likely placeholder); Attack-Move bound to Q vs classic "A"; tick-rate framing nuance.

**Theme freshness — UX is CONSISTENT, GDD is STALE.** The UX deliberately kept alchemy minimal (Chimera Seal + transmute spinner) and **shelved** the bio-alchemy "Transmutation Lab" retheme (decision-log D3); the FMA pivot (06-21, one day *after* the UX finalized) is a **world/faction** direction, not a UI retheme, and the UX reserves the 8 colorblind-safe team colors for world units separately from UI accents. **So the UX UI system needs no rework for the pivot.** The genuinely stale artifact is the **GDD** (2026-04-07: pre-pivot, no faction identity, missing FR-7/FR-26). *Correction to a prior assumption: the GDD does **not** name "Alpha/Iron Pact" — those codenames are in the PRD, not the GDD.*

**Recommended GDD reconciliation (not a hard blocker — PRD+epics+UX carry the truth):** update the GDD to (a) add FR-7 persistent heroes + FR-26 custom UI builder (or mark them deferred), (b) reconcile the campaign count, (c) absorb the FMA faction direction.

### 4.2 UX ↔ Architecture — Verdict: **Supported-with-gaps**
The architecture is **excellent and complete below the UI line.** Every UX surface touching the sim / network / content-validation boundary has a precise mechanism: multiplayer determinism gate, lobby multi-hash stateful-authority handshake, **custom runtime UI (both READ + WRITE rails)**, trigger/tech-tree IR, hero-picker persistence (D4), content-browser IO (mod.io), LLM provider/secrets (D6), accessibility-settings baseline (Step 5), and MultiMesh 500–2,000 @ 60/30 perf.

**Gaps concentrate in the presentation / editor-chrome tier** (which the architecture consciously defers as "composes from the existing kit"):
| UX surface | Gap | Severity |
|------------|-----|----------|
| **Edit↔Play round-trip + ≤2s budget (NFR-1 AC2)** | Enabling primitive exists (in-memory edit→Validate→Apply, no restart), but the **transition itself is never designed** (teardown vs incremental on F5? where's the toggle? how is ≤2s met/measured?). Latency is **unowned and unbenchmarked** — the headline "instant loop" is structurally enabled but unproven. | **HIGH** |
| **Creation Suite editor shell + player/creator mode separation (NFR-3)** | The shell hosting all authoring surfaces has no architectural design; **NFR-3 (a pure player never sees an authoring control)** has no screen/state-manager or editor↔player mode-separation pattern. *(Note: Epic 10 Story 10.10 adds an NFR-3 acceptance gate, so it's covered at the epic level — the gap is architectural design depth.)* | **HIGH** |
| Editor panels (Unit Card, Ability, Tech-Tree GraphEdit drag-wiring, Faction wizard) | Data models fully designed (D1–D4); the panels + drag-wiring/inspector-binding interactions are not. | MEDIUM |
| Design-system → Godot Theme mapping (chimera.css `:root` → single Theme resource) | UX hands this to the arch pass; arch doesn't pick it up. | MEDIUM |
| Tooltip-on-every-control + onboarding flow + <15-min target | No tooltip/help data model, no onboarding state machine, no measurement hook. | MEDIUM |
| Title / Mode-Select / screen-navigation state management | Subsumed under one "Partial" row; NFR-3 depends on this routing backbone. | MEDIUM |
| `prefers-reduced-motion` mirror | UX note only; no SettingsData field / animation-gating mechanism. | LOW |

**Perf/responsiveness:** render + multiplayer responsiveness are well-architected (MultiMesh + one-way interpolating bridges, zero-alloc-in-tick CI asserts, clamped adaptive input delay, cross-platform golden-checksum). The one real shortfall is the **edit→play ≤2s loop — enabled but unmeasured** (render FPS is *watched* via profiler, not CI-gated).

### 4.3 Warnings
- ⚠️ **GDD is stale vs the rest of the planning set** — missing FR-7 + FR-26 (both fully designed downstream) and the FMA pivot. Reconcile upward; not a blocker since the PRD is the requirements baseline.
- ⚠️ **Two HIGH architectural presentation gaps** (edit→play transition/measurement; editor-shell + NFR-3 mode separation) — design debt to close in the UX-implementation/epics pass *before those surfaces are built*. Neither is a determinism/correctness blocker.
- ⚠️ Several committed GDD features (NL trigger UI, AI map-gen/balance UI, replay/spectator UI, publish-quality gates) are **thin or absent in the UX** — confirm they're scoped in the epics' UX-DR items (they largely are) and not lost.

---

## 5. Epic Quality Review

_Method: 10 parallel adversarial best-practices auditors (one per epic), each told to **try to disprove** the frontmatter claims, then a cross-epic auditor assembled the dependency graph and audited the frontmatter._

### 5.1 Best-practices scorecard
| Criterion | Result |
|-----------|--------|
| **Cross-epic independence** (Epic N never requires Epic >N) | ✅ **HOLDS across all 10 epics.** Every cross-epic `Depends on` points backward; the only forward references (Epic 2/5 → Epic 7 on-death/event seam) are explicit, AC-proven *deferrals*, not dependencies. The "always-shippable" brownfield pattern done right. |
| **Brownfield handling** | ✅ **Correct.** The deliberate absence of a greenfield starter-template story is the right call (replaced by the documented MainScene "strangler" refactor); verify/integration stories are appropriate and carry testable ACs. |
| **Data-creation timing** | ✅ **Strong** (just-in-time, verified against source) — one mild exception (9.3↔9.11 envelope co-design). |
| **FR traceability** | ✅ Accurate mapping; ⚠️ several Epic-9 `Covers:` lines duplicate their own FR ids (cosmetic copy-paste). |
| **Player/creator value** | ⚠️ 8/10 epics deliver it; **Epics 1 and 9 skew technical-milestone** (Epic 9: ~42% "As a multiplayer engineer" stories). |
| **Story sizing** | ⚠️ ~11 oversized/multi-deliverable stories should be split. |
| **Acceptance-criteria quality** | ⚠️ Recurring non-objective / escape-hatch ACs — several flagged by the epics' *own* ⚠ notes but never applied. |

### 5.2 🔴 Critical violations (must fix before sprint planning)
1. **Epic 1 — within-epic forward-dependency cycle (1.5 ↔ 1.7).** Story 1.5's AC-4 ("reject random effects via the forbidden-until-SimRng rule") can only be enforced by the `ScenarioValidator`, which is **net-new in the later Story 1.7** — and 1.7 already `Depends on: 1.5`. A logical cycle, confirmed against the architecture and the (validator-absent) brownfield code. **This disproves the frontmatter's "no within-epic forward dependencies" claim.** *Fix: move AC-4 into 1.7, or have 1.5 ship a minimal standalone guard that 1.7 absorbs.* — ✅ **RESOLVED 2026-06-21:** AC-4 relocated from 1.5 into 1.7 (which already asserts the rule); 1.5's narrative + the frontmatter validation note updated. The cycle is broken and the claim now holds.
2. **Epic 2 — dangling dependency on a non-existent story (2.10 → "2.9").** Story 2.10 (the capstone) reads `Depends on: 2.5, 2.6, 2.7, 2.9`, but **2.9 was split into 2.9a/2.9b** and the footer was never updated. Its real hard dependency — 2.9b's crystal-spend wiring, required by 2.10's Equal-Exchange self-cost AC — is unlisted. *Fix: `Depends on: …, 2.9a, 2.9b`.* — ✅ **RESOLVED 2026-06-21:** the 2.10 footer and its note both corrected to `2.9a, 2.9b`.

### 5.3 🟠 Major issues (recurring patterns)
- **Technical-milestone framing.** Epic 1 is wholesale enabler-heavy (only 1.9b LAN-zero-desync and arguably 1.11 are end-user-observable); Epic 9's 9.1–9.5 (~42%) are "As a multiplayer engineer" infra; plus front-loaded enablers 2.1–2.3, 7.1a/7.1b, 5.1, 3.1/3.2, 8.3a. *Defensible* as M1/FR-39 seams on a brownfield platform, but should be re-framed as value or explicitly blessed as the verification rail, **and sprint-sequenced as fused enabler blocks immediately followed by their first value consumer** (e.g. 2.1–2.3 → 2.4) so a vertical slice lands early.
- **Oversized stories to split** (~11): 1.8 (god-object strangle + 4 deliverables), 1.10 (CI + 2 analyzers + WSL gate), 2.2 (4 net-new subsystems), 3.1 (Theme + ~20 components), 5.5 (5-step wizard + presets + JSON + colorblind + persistence), 7.1b (IR DTOs + flat→graph migration), 7.8 (DslEventCommand + replay-v2 + 4 apply sites), 8.6 (4 entity-gen kinds, no per-type ACs), 9.3 (5 concerns + hidden chat-spoof fix; highest-risk story), 10.9 (Steam + DRM-free pipelines), 10.2 (new harness + open-ended tuning).
- **Non-objective / escape-hatch ACs — several flagged by the epics' OWN inline ⚠ notes but never folded into the live AC:**
  - 10.1 "Hard observably more aggressive than Easy" — subjective; an objective metric (first-attack tick / army-size delta) was requested but not applied.
  - 10.3 "2000-unit case meets target **OR** any shortfall is documented" — no-floor escape hatch; "representative hardware" undefined → perf gate not reproducible.
  - 5.7 "observably asymmetric" — subjective; the suggested win-rate/composition-delta fix was not applied.
  - 9.6 "routing assumption resolved **OR** a static-endpoint decision is recorded" — a documentation outcome, not a testable AC (and it's a UI story that should be web/in-engine verifiable).
  - 1.11 — per-system smoke-test checklists are undefined (self-flagged).
  - 6.2 "visually identical" terrain fidelity bar (subjective) on the epic's headline persistence story; recommend a hash/delta-equality check.
  - Other subjective/example bars: 2.7 camera-shake (no duration/magnitude), 9.4 "e.g. 4→2" (example-as-assertion), 8.x adjective creep ("curated/actionable/minimal" without thresholds), 10.8 colorblind "reliably distinguishable" (no tool/threshold).
  - **Missing error/edge ACs:** 2.4 invalid-target cast, 2.6 overlapping-aura stacking, 2.10 self-cost lethal/clamp, 2.2 non-commutative modifier ordering, 6.2 save-write failure / missing-corrupt TerrainRef on load, 8.4/8.6 parse-failure/oversize-response, 9.10/9.12 failure UX (mod.io call failure; hero-attestation failure).
- **Incomplete dependency metadata (all backward — no sequencing break, but misleading):** 3.2 cites "Epic 1/1.3" (real provider 1.3b); 3.10 cites "Epic 1" (real 1.8); 10.3→10.2 and 10.5→10.3 undeclared; 9.3↔9.11 envelope co-design coupling; 6.1→1.1 in prose only. Several the doc's own quality notes already flag.

### 5.4 🟡 Minor
- Epic-9 `Covers:` lines duplicate their own FR ids (cosmetic).
- **Faction-naming drift:** FR-49/Story 10.6 say "Iron Pact" and the sequencing note uses "Crucible Covenant / Sanguine Court," while the current direction is the FMA **Rebel Alchemists vs Homunculus Legion** redesign — content-coherence to reconcile before art is wired (not an FR-tag defect).
- Developer-voice framing on ship-infra stories (10.2/10.7/10.9) — add a one-line player-value framing.

### 5.5 Frontmatter claim audit
The epics frontmatter asserts: _"FR coverage 60/60; NFR-1..6 covered; no residual placeholders; no within-epic forward dependencies."_
| Sub-claim | Verdict |
|-----------|---------|
| FR coverage 60/60 | ✅ **Accurate** (independently confirmed, §3). |
| NFR-1..6 covered | ✅ Consistent (cross-cutting; not single-epic-owned). |
| No residual placeholders | ⚠️ **Undermined** — no `[TBD]` tokens, but the dangling `2.9` reference (§5.2 #2) is a residual broken reference. |
| No within-epic forward dependencies | ❌ **FALSE** — the 1.5↔1.7 cycle (§5.2 #1). |

### 5.6 Step-5 Verdict
- ✅ The breakdown is **structurally sound on the highest-stakes rule** (cross-epic independence) and on brownfield handling, data timing, and FR/NFR coverage. Genuinely good planning.
- 🔴 **2 critical, surgically-fixable defects** (1.5↔1.7 cycle; 2.10 dangling ref) must be corrected before sprint planning consumes this file.
- 🟠 A recurring **major** pattern — oversized stories, technical-milestone framing in Epics 1 & 9, and ~7 non-objective/escape-hatch ACs (several already self-flagged) — is **polish, not a coverage/architecture blocker**, but should be cleaned up so stories are independently completable and testable.
- **None of the Step-5 findings are determinism/correctness or coverage blockers.** They are story-craft fixes.

---

## 6. Summary and Recommendations

### Overall Readiness Status: 🟠 **NEEDS WORK**
Not "READY" (real critical defects + unscoped HIGH design intentions + a false planning claim), but emphatically not "NOT READY" — the foundation is genuinely strong. The required work is **concentrated and surgical**, not a structural overhaul. What's solid: **100% FR coverage (60/60, independently verified)**, clean **cross-epic independence**, correct **brownfield handling**, disciplined **data-creation timing**, and an **architecture that rigorously backs everything below the UI line** (determinism, lockstep, content pipeline, persistence, LLM/secrets).

### What this assessment changed vs. the inputs (corrections worth keeping)
- The FR count is **60**, not 58 — the Step-2 extraction undercounted lettered inserts; tracker/epics were right.
- The **GDD is the stale artifact**, not the UX. The UX (06-20) is future-proof against the FMA pivot; the GDD (04-07) lags (no FR-7/FR-26, no faction identity).
- The "hidden cross-epic forward dependency" risk **did not materialize** — independence holds. The real structural defect is *within* Epic 1.

### Critical Issues Requiring Immediate Action (before sprint planning / production)
1. **🔴 Two HIGH design intentions are owned by no FR and unscoped in 1.0** — the **RTS command-vocabulary behaviors** (Patrol/Hold/Follow/Attack-Move, + the disable-not-remove guarantee) and **formation movement + yielding**. The PRD *itself* flags both as "VERIFY before done." They directly determine whether the showcase reads as a real RTS. **Decide per item: add a story/FR, descope to post-1.0, or add a verification story** — do not leave them silently assumed-built. *(§3.4 #1–#2)*
2. **🔴 Two epic-structure defects block clean sprint planning** — the **1.5 ↔ 1.7 forbidden-until-SimRng cycle** (move AC-4 to 1.7 or ship a minimal guard in 1.5) and the **2.10 → "2.9" dangling dependency** (retarget to 2.9a/2.9b). These also make the frontmatter's "no within-epic forward dependencies / no residual placeholders" claim untrue — fix the file, then the claim. *(§5.2)*
3. **🔴 Two HIGH architecture presentation gaps** — the **edit→play transition is enabled but never designed or measured** (no ≤2s budget/benchmark — the headline "instant loop" is unproven), and the **Creation Suite editor shell + the player/creator mode separation NFR-3 depends on has no architectural design.** Close these in the UX-implementation/architecture pass before those surfaces are built. *(§4.2)*

### Recommended Next Steps (in order)
1. **Triage the 2 HIGH + 7 MEDIUM "no-FR" design gaps (§3.4):** for each, record an explicit decision — *in 1.0 (add story/FR)* / *post-1.0 (descope)* / *already-built (add a verification story)*. Pay special attention to the **Fog-of-War subsystem having no verification story at all** despite server-enforced visibility being a security pillar.
2. **Fix the 2 critical epic defects (§5.2)** and **apply the ~7 already-self-flagged ⚠ AC fixes** (10.1, 10.3, 5.7, 9.6, 1.11, 6.2) and **split the ~11 oversized stories** (§5.3) so every story is independently completable and testable.
3. **Reconcile the GDD upward (§4.1):** add FR-7 (persistent heroes) + FR-26 (custom UI builder) or mark them deferred; absorb the FMA faction direction; fix the campaign count (5–8 vs "/12"); reconcile faction naming (Iron Pact / Crucible Covenant / Sanguine Court → Rebel Alchemists / Homunculus Legion) before art is wired.
4. **Add the missing architecture design for the editor/UX-shell tier (§4.2):** edit→play transition + ≤2s measurement hook; editor-shell + screen/state manager + NFR-3 mode separation; design-system → single Godot Theme mapping; pervasive-tooltip/onboarding home.
5. **Then proceed to `gds-sprint-planning`** to sequence the (now-clean) epics — sequencing the technical-milestone enabler blocks (Epic 1; 2.1–2.3; 9.1–9.5) as fused blocks immediately followed by their first value-delivering consumer.

### Final Note
This assessment identified **2 critical, ~6 HIGH-priority, and a larger set of MEDIUM/MINOR** items across **5 dimensions** (requirements baseline, FR coverage, UX↔GDD, UX↔Architecture, epic quality). The headline is reassuring: **coverage and architecture are sound, and nothing here is a determinism/correctness blocker.** The gaps are the *unglamorous-but-real* kind a coverage check alone would miss — design intentions that were never turned into FRs, planning claims that don't quite hold, and presentation-tier architecture that's deferred-but-not-yet-designed. Address the critical issues (and decide the HIGH design gaps) before implementation; the MEDIUM/MINOR items can be folded into sprint refinement. You may also choose to proceed as-is with eyes open — the report makes the risks explicit.

---

*Assessment complete — generated 2026-06-21 by the `gds-check-implementation-readiness` workflow.*
