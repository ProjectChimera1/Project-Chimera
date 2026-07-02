# Sprint Change Proposal — 1.0 Gap Closure ("Make It a Game")

**Date:** 2026-07-01 · **Author:** correct-course (gds-correct-course, Batch mode) · **Approver:** Alec
**Trigger artifact:** `_bmad-output/planning-artifacts/gap-analysis-1.0-vs-wc3-2026-07-01.md`
**Scope classification:** MAJOR (fundamental replan — new epics, +52 stories, PRD/GDD/UX reconciliation)

---

## 1. Issue Summary

A full-plan audit (9 domain analysts + adversarial verification; 13/13 verified findings CONFIRMED, 0 refuted) found that the 121 planned stories deliver an excellent deterministic engine, ability/effect system, and creator-authoring kernel — **but not a game that meets the stated 1.0 bar** ("fully playable, no glitches or unpolished feel, WC3 World Editor-class editor, fully operational UI").

**Issue type:** misunderstanding/erosion of original requirements — multiple GDD-canonical features (hero XP runtime, tutorial campaign, 2v2 teams, trigger verbs `Move Camera`/`Play Sound`, regions, props, asset import, order-ack feedback) were silently dropped between GDD → PRD → epics. The recurring confirmed defect pattern: **UI surfaces are planned/shipped that advertise features no story builds** (Campaign button, 8-player slots, lobby AI slots, "content synced" gate).

**Evidence:** 81 blocker/major findings in the gap register, each with doc/code citations. Headline verified blockers: hero XP/leveling runtime unowned; 3+ player matches cannot conclude; campaign dead-end button. Headline verified majors: no research/upgrades, no pause/speed, no mid-match save, no shift-queue, depth-1 production queue, rally points not lockstep-replicated (desync vector), no team model, no video settings, zero human playtest gates.

---

## 2. Impact Analysis

### Epic impact
- **No completed work is invalidated.** Epics 1–2 (done/in-progress) stand; the plan is incomplete, not wrong. **No rollback.**
- **Epics 2, 3, 4, 6, 7, 9, 10** each gain appended stories (same pattern as the DG-1..9 additions).
- **Three new epics:** Epic 11 (Fully Operational Match & Shell), Epic 12 (Import Manager & Content Sync), Epic 13 (Prologue Campaign).
- Epic ordering survives with one insertion rule: **Epic 11 runs before Epic 9** (MP verification needs the session/shell UX to exist), Epics 12–13 run after 9, Epic 10 remains the ship gate.

### Story impact
- +52 new stories (121 → 173). Sprint entries 141 → ~199 (incl. 3 epic headers + 3 retros).
- 2 existing stories get AC-level edits (5.6, 3.11 — 8-slot → 4-slot honesty trims; Mode Select strip).
- Throughput reality check: Epic 2 pace ≈ 8–10 stories/week of sessions → **the added scope is roughly 5–7 weeks of additional implementation** at current pace.

### Artifact conflicts
| Artifact | Change needed |
|---|---|
| **PRD** | New §4.14 with FR-62..FR-78; MVP statement updated; addendum entry |
| **GDD** | Campaign "5–8 missions" → "3-mission prologue (1.0), grows post-1.0"; mid-match SP save/load moved **back into 1.0** (reverses the 2026-06-21 deferral); 1.0 player count "verified at 4, 8 = fast-follow" |
| **UX** | Mode Select honesty strip (ranked/MMR/live-count removed until backed); new screens: skirmish setup, in-match menu, score screen, loading, save/load, import manager, objectives panel, campaign select. UX-DR addenda authored per-story (3.11 precedent) |
| **Architecture** | 4 additions, designed at story-creation time under existing invariants: order-queue SoA + wire flag (2.12); Region/Alliance/Item/Research/SaveGame sim models (all Fixed, folded, data-driven); **unit animation = VAT (vertex-animation texture) pending the 10.14 spike gate** (MultiMesh cannot do skeletal animation); content-sync rides mod.io (no P2P transfer protocol) |
| **sprint-status.yaml** | +58 entries in the resequenced order below |

### Technical impact
- Multiple new sim arrays → **SimChecksum folds + golden re-baselines** are expected at: hero XP (3.13), items (3.15), research (4.8), regions (6.6), elevation-blocking (6.7), alliances (9.15), order queue (2.12). Apply [[checksum-fold-timing rule]]: fold when first mutable mid-match, one fold per story, re-baseline explicitly.
- Every new per-unit SoA field flows through `EntityWorld.ApplyUnitDefinition` (A2 rule).
- D2 (AI float→Fixed) becomes an owned story (10.13) — closes the last known determinism debt and legalizes AI in MP slots.

---

## 3. Recommended Approach — APPROVED DECISIONS

**Path: Hybrid of Direct Adjustment (Option 1) + MVP scope redefinition (Option 3).** Rollback (Option 2) rejected — nothing built is wrong. Effort: High. Risk: Medium (largest risks: VAT animation spike, save-format stability, N-faction victory generalization). Timeline: +52 stories.

Decisions locked by Alec 2026-07-01:

| # | Decision | Choice |
|---|---|---|
| 1 | Review mode | **Batch** |
| 2 | Heroes & progression | **Everything incl. items** — XP/level runtime, revival, research/upgrades, items/inventory/shops |
| 3 | Match scale | **4-player + teams** (2v2, allied vision/victory, multi-AI, editor 4-player maps; 8 = fast-follow) |
| 4 | Campaign | **3-mission prologue** (GDD edited 5–8 → 3) |
| 5 | Map editor | **Full parity floor** (regions, impassable, doodads, map meta, multi-select/copy-paste, cameras, cheap water, minimap preview) |
| 6 | Triggers | **GDD vocabulary + debug tooling** (incl. objectives/quest log + briefing) |
| 7 | SP save/load | **Build for 1.0** (checksum-verified full-world serializer) |
| 8 | Asset pipeline | **Full incl. MP sync** (import → package → ingest → mod.io "Update Required") |

Implicit (from the stated 1.0 bar, not re-asked): session shell, feel floor, bug-class fixes, and playtest gates are **in**. Descopes: ranked/MMR/live-count UI stripped; anti-maphack stays post-1.0 (documented); AI-takeover/reconnect stays deferred (9.5 freeze floor ships); 8-player stays fast-follow.

---

## 4. Detailed Change Proposals

Numbering continues each epic's existing sequence. Every story below is deterministic-first (Fixed math, ascending-ID, folded state where mutable), data-driven, and honors the sim/presentation boundary. `⚑fold` = expected SimChecksum fold + golden re-baseline.

### 4A. Epic 2 additions — Order Pipeline & Combat Defect Burn-down (2 stories)

- **2.12 — Shift-queued command waypoints + lockstep-replicated rally points** ⚑fold
  Per-entity bounded order queue (SoA ring, cap ~8) executed on completion; Shift+RMB/ability queues through the shared `OrderApplier` (replay/live parity). Rally-point set/changed becomes a wire order (rides `UnitOrder`, no version bump if a spare command id fits) instead of local-only state — closes the logged desync vector. *(FR-74; fixes verified majors "no shift-queue", "rally not replicated")*
- **2.13 — Combat & store defect batch: attack-move acquires buildings · AttackMove hover deadlock · BuildingStore recycle · Modifier pulse-cap**
  Attack-move/idle auto-acquire includes enemy buildings (AI issues AttackBuilding — makes DestroyAllBuildings winnable); fix the known arrival-hover deadlock; BuildingStore gains free-list recycling (64-slot exhaustion in long/4-player matches); lifelong HoTs survive past 256 pulses (re-arm on expiry or widen counter). *(fixes 4 confirmed bug-class findings)*

### 4B. Epic 3 additions — Hero Runtime, Items & Editor Fidelity (5 stories)

- **3.13 — HeroXpSystem: kill-credit XP, leveling, stat growth** ⚑fold
  Sim system awards XP on kill (killer-attribution via CombatSystem), applies the 3.7-authored leveling curve (stat deltas via ModifierSystem), levels folded into checksum; XP share radius data-driven. Closes the verified blocker — the FR-7 persistence rail finally carries live data. *(FR-62)*
- **3.14 — Hero death & revival rule**
  Data-driven revival: per-scenario/per-faction toggle, revive at producing building for cost + scaled time (WC3 Altar model, generalized); death drops items (with 3.15). *(FR-62)*
- **3.15 — Item & inventory sim: pickups, slots, stat effects, charges** ⚑fold
  ItemStore SoA (map-placed + dropped items), per-hero inventory slots (default 6), stat items ride ModifierSystem, charged/consumable items execute Effect-Graph on use; deterministic pickup ordering. *(FR-64)*
- **3.16 — Item authoring + shop buildings + inventory UI**
  Item editor (consolidated card, WC3 Object-Editor model), `sells_items` building flag + shop panel, inventory display on unit panel + hero picker; persistence manifest "inventory" now real (FR-7a). *(FR-64)*
- **3.17 — Editor undo/RestoreUnit fidelity: widen UnitSnapshot**
  UnitSnapshot carries full authored state (armor, passives, feedback profile, tags, attack domain, category…) so editor undo/redo stops silently reverting units; regression-guarded by a field-coverage test. *(closes the accumulating fidelity debt from deferred-work)*

### 4C. Epic 4 additions — Research & Upgrades (2 stories)

- **4.8 — ResearchSystem: faction-wide timed upgrades** ⚑fold
  `ResearchDefinition` JSON (cost, time, repeatable levels, stat deltas); research queued at buildings, spends via the shared order path, completion applies permanent faction-scoped modifiers via ModifierSystem; folded per-faction research state. *(FR-63)*
- **4.9 — Research authoring + command-card research buttons + level display**
  Tech-tree editor gains research nodes (prereq-lintable like 4.2); command card shows research buttons with cost/progress; unit panel shows upgrade level (e.g. "+2 attack"). *(FR-63)*

### 4D. Epic 6 additions — World Editor Parity Floor (6 stories)

- **6.6 — Regions: data model, editor draw tool, trigger integration** ⚑fold
  Sim-side `Region` (Fixed rects, named) in ScenarioData (hash-covered); editor draw/edit/name tool with overlay toggle; DSL gains region refs + `RegionEnter/Leave` events. Unblocks 7.10's own presets. *(FR-69; closes a verified plan inconsistency)*
- **6.7 — Impassable terrain & pathability paint + view overlay** ⚑fold(nav)
  Painted pathability layer (+ optional slope-derived blocking) feeding NavMesh bake AND flow fields deterministically; editor pathability view overlay (WC3 'P' view). Lifts the 6.5 vision-only scope limit deliberately, with golden re-baseline. *(FR-69; closes "terrain can never block movement")*
- **6.8 — Doodads & props: palette, placement, props.json**
  Decorative prop placement (rotate/scale/variation), `props.json` per GDD package schema, optional blocking flag (composes with 6.7), MultiMesh-rendered; ships a starter prop library (placeholder-art rule applies). *(FR-69)*
- **6.9 — Map properties, New-Map flow, 2–4 start positions, minimap preview**
  New Map dialog (name/author/description/suggested players/size); start-position count 2–4 with per-slot config (GDD §5 parity); auto-generated minimap preview saved into the package for lobby/browser. *(FR-69; pairs with 9.15/9.16)*
- **6.10 — Multi-select, copy/paste, entity rotation in the editor**
  Marquee multi-select, group move/delete/duplicate, copy/paste with offset, placement rotation persisted (GDD says rotation is stored — today it isn't authorable); undo-integrated. *(FR-69)*
- **6.11 — Named cameras + water floor**
  Named camera positions authored in-editor (consumed by 7.13 camera actions + campaign cinematics); water = visual plane volumes + auto-impassable paint (cheap-water decision documented). *(FR-69)*

### 4E. Epic 7 additions — Trigger Vocabulary v2, Objectives & Debugging (6 stories)

- **7.11 — N-faction victory & per-player elimination** ⚑fold
  WinConditionSystem generalized to N active factions + alliance-aware resolution (last team standing); per-player defeat verdicts (eliminated player → spectator-view or exit while match continues); removes the P1/P2 hardcoded game-over path. *(FR-65; closes verified blocker "3+ player matches cannot conclude")*
- **7.12 — Expression state-reads, Random Choice, trigger on/off**
  Read-accessors (entity HP/position/owner, player unit-count/resources, region contents — bounded snapshot reads); `RandomChoice` node on SimRng; enable/disable-trigger + run-trigger actions. *(FR-70)*
- **7.13 — Action leaves: OrderUnits, MoveCamera, PlaySound/PlayVfx**
  Deterministic `OrderUnits` (move/attack-move/patrol a spawned/region group via OrderApplier); camera actions (pan/focus/cinematic letterbox — presentation rail, validated no-sim-touch); sound/VFX leaves ride the 2.7 CombatEventQueue bus. GDD Tier-2 vocabulary complete. *(FR-70)*
- **7.14 — Event breadth: UnitDamaged, UnitTrained, AbilityCast, HeroLevel, Chat**
  Five new deterministic event sources with payload params; chat-command events for WC3-style "-commands". *(FR-70)*
- **7.15 — Objectives & quest log + match briefing surface**
  `objectives.json` authoring (per GDD schema) + trigger actions (show/complete/fail objective) + in-match quest-log panel + pre-match briefing screen (also carries 10.8c subtitles). Win conditions become visible to players. *(FR-70)*
- **7.16 — Trigger debugging: variable watch + fired-trigger log in playtest**
  F5-playtest overlay: live variable watch, fired-trigger event log with tick stamps, per-trigger fire counters; zero sim impact (read-only presentation). *(FR-71)*

### 4F. Epic 9 additions — Teams, Local-Faction, Sync & Replay UX (5 stories)

- **9.14 — Local-faction parameterization (kill the P1 hardcodes)**
  Fog, selection, command card, training, alerts all keyed to `LockstepManager.LocalFaction`/assigned slot — the P2+ client fully sees and commands its own faction. **Must land before 9.5/9.6 MP verification.** *(closes the "non-host player is blind" finding; FR-65)*
- **9.15 — Teams & alliances: lobby teams → sim alliance model** ⚑fold
  Lobby team assignment; sim alliance mask (no friendly-fire targeting, shared vision toggle, allied victory with 7.11); FFA = locked teams of 1 for 1.0 (no in-match diplomacy). *(FR-65)*
- **9.16 — 4-player verified: MP + skirmish + editor maps end-to-end**
  2v2 and 4-FFA verified across lobby → match → elimination → victory → score screen on a 4-start-position map (6.9); 8-player remains the documented fast-follow constant-bump. *(FR-65; supersedes the 8-slot claims in 5.6/UX-DR68 — see 4J edits)*
- **9.17 — Pre-match hash handshake covers ALL content (faction/ability/item/research JSON)**
  Widen `{scenarioHash, rulesetHash, startStateHash}` inputs to the full canonical content model; mismatch → the 12.4 Update-Required flow. *(closes the logged known desync vector)*
- **9.18 — Replay UX: browser, playback controls, perspective/fog toggle**
  In-app replay browser (list/rename/delete), playback speed/pause/seek-by-tick(forward), player-perspective + fog toggle, watch-from-lobby entry. FR-38a becomes a real feature. *(FR-77)*

### 4G. Epic 10 additions — AI at Scale, Animation, Pathing Bar & Playtest Gates (6 stories)

- **10.12 — Multi-instance AI: fill any open slot (2–4 players)**
  AiOpponentSystem instanced per AI slot (de-hardcode P1_BASE/P2), per-slot difficulty; offline skirmish 1v3 etc. *(FR-76)*
- **10.13 — AI float→Fixed (close D2): deterministic AI legal in lockstep**
  Utility scoring migrated to Fixed; AI-active golden joins the cross-platform WSL gate; MP lobby AI slots become legal (lockstep-safe). *(FR-76; closes the D2 debt + the "dead lobby AI slots" finding)*
- **10.14 — Animation spike + VAT pipeline (gate story)**
  Spike: validate vertex-animation-texture (VAT) baking for the GLB set on MultiMesh (custom shader, per-instance anim state/time via instance custom data). GATE: if VAT fails quality/perf on reference hardware, fall back to pooled skeletal proxies for on-screen units — decision recorded before 10.15 proceeds. *(FR-75)*
- **10.15 — Animation integration: idle/walk/attack/death driven from sim state**
  Presentation-layer state machine mapping sim state (moving/attacking/dying) to VAT clips; death anim + corpse fade replaces pop-out; zero sim coupling. *(FR-75; closes "no unit animation system")*
- **10.16 — Pathfinding quality bar: chokepoint flow, stuck-unit watchdog, arrival stability**
  Scenario-based quality harness (choke squeeze, 50-unit funnel, cliff-edge (6.7) navigation); stuck-unit detector + unstick rule; formation arrival without orbit/jitter; measurable ACs (max stuck-seconds, funnel throughput). *(closes "no pathfinding-quality bar")*
- **10.17 — Human playtest gates: fun gate + editor usability + balance pass**
  Three structured human gates with protocols + recorded verdicts: (1) melee fun gate after Epic 5 content exists (GDD Phase-1 risk checkpoint, run retroactively at this point), (2) creator usability run of the full editor loop ("Your First Scenario" with a fresh user), (3) human balance validation alongside 10.2's self-play. Written go/no-go criteria; failures spawn correct-course. *(FR-78; closes the verified "zero human playtest gates")*

### 4H. NEW Epic 11 — Fully Operational Match & Shell (12 stories)

_The session layer: everything between "click Play" and "back at the menu with a score screen". All presentation/shell except the save serializer; each story reuses the 3.1x design system._

- **11.1 — Skirmish setup screen (the real one)** — map pick (shipped + subscribed + local maps w/ 6.9 minimap previews), per-slot: open/AI(difficulty)/faction/team/color, 2–4 slots per map's start positions, launch validation. The screen 5.6/10.1/10.11 assume. *(FR-68)*
- **11.2 — In-match menu, SP pause, game-speed control** — Esc/F10 menu (Resume/Settings/Save/Load/Concede/Quit-to-menu); true sim pause in SP; SP speed control (0.5×–3× tick-rate scale, deterministic — replay-stamped). *(FR-66)*
- **11.3 — Concede/surrender + leave-match flow (SP + MP floor)** — concede → defeat verdict → score screen; MP: leave announces to peers, victory when last opponent leaves (rides 7.11 verdicts); MP pause protocol deferred to Epic 9 context if server-authority inversion moves it. *(FR-66)*
- **11.4 — Victory/defeat + score screen** — end-of-match stats from sim counters (units built/lost/killed, resources gathered, army value graph over time), per-player rows, rematch/continue/quit. *(FR-66)*
- **11.5 — Loading screen + match-start flow** — progress-staged loading (validate → terrain → nav bake → spawn), map name/author/loading text (6.9), fail-safe error return to menu. *(FR-68)*
- **11.6 — SP save/load I: full-world serializer** — versioned snapshot of ALL sim state (EntityWorld SoA, stores, modifier/DSL/RNG/tick state); load → resume; **AC: save→load→resume produces byte-identical SimChecksum stream vs uninterrupted run**. *(FR-67)*
- **11.7 — SP save/load II: slots UI, autosave, format guard** — save/load slots in the 11.2 menu, campaign autosave hook, format-version negative tests + forward-compat policy, long-match soak save. *(FR-67)*
- **11.8 — Alerts & minimap events** — "under attack" (throttled) with audio cue + minimap flash, minimap ping (Alt-click, MP-replicated as a presentation event), camera-box on minimap, production/research-complete chimes. *(FR-74)*
- **11.9 — Denial & acknowledgment feedback** — "not enough Ore/Crystal" + supply-capped + invalid-placement denial (text+sound); selection/ack sound hooks per unit (data-driven sound set refs); order-confirmed ground marker (GDD §6 promise). *(FR-74)*
- **11.10 — Buff icons + multi-select panel + subgroup tabs** — active modifier icons w/ duration on unit panel (ModifierStore read); multi-select grid with type-grouped tabs (WC3 subgroups), tab-cycling hotkey. *(FR-74)*
- **11.11 — Production queue: depth-5, queue display, cancel/refund** ⚑fold — building queue widened (folded array), command card queue strip with click-cancel + refund, rally-on-queue preserved. *(FR-74; closes verified "depth-1, no cancel")*
- **11.12 — Video settings + Mode Select honesty strip** — Graphics tab: resolution/window mode/vsync/quality presets/UI scale binding; Mode Select loses ranked/MMR/live-count placeholders; Campaign entry binds to real prologue count (N/3). *(FR-66; UX edits in 4J)*

### 4I. NEW Epic 12 — Import Manager & Content Sync (4 stories) + NEW Epic 13 — Prologue Campaign (4 stories)

- **12.1 — Import UI + validation** — import .glb/.png/.ogg into the scenario package `assets/` (GDD schema dirs) with caps (poly/texture-size/duration/file-size), preview, license-attestation checkbox. *(FR-72)*
- **12.2 — Runtime ingest + assignment wiring** — GLTFDocument runtime model load (arch-specified path) + image/audio ingest; wired into unit/building model assignment (closes the building-model gap), icons/portraits, CombatFeedbackProfile sound refs, projectile visuals (`projectiles.json` authorable). *(FR-72)*
- **12.3 — Packaging + hash coverage** — `assets/` bundled into `.chimera.zip`, checksum.sha256 covers asset bytes, package-size cap + publish-flow surfacing. *(FR-72)*
- **12.4 — MP content sync: "Update Required" one-click flow** — lobby content gate resolves against mod.io (host's published version): mismatch → one-click download → hash re-verify → ready; unpublished custom-asset maps are host-blocked from MP with a "publish first (unlisted OK)" affordance. GDD-canonical flow, no P2P transfer protocol. *(FR-72)*
- **13.1 — Campaign framework: sequence, unlock, briefing/outro, autosave** — campaign manifest (ordered missions, unlock state), Mode Select binding (N/3), briefing (7.15) + outro cards, per-mission autosave (11.7), completion persistence. *(FR-73)*
- **13.2 — Mission 1: "First Exchange" (basics)** — camera/move/select/build/gather tutorialized via triggers + objectives; scripted with 7.x vocabulary as its proving ground. *(FR-73)*
- **13.3 — Mission 2: economy + combat + heroes** — Crystal, production choice, hero XP/items intro (3.13/3.15), first real skirmish beats. *(FR-73)*
- **13.4 — Mission 3: full match + campaign polish pass** — full 1v1 vs AI with objectives; end-to-end campaign playtest gate (feeds 10.17 protocols). *(FR-73)*

### 4J. Edits to existing artifacts (old → new)

**Story 5.6 AC3** *(epics.md:1610)*: "skirmish setup with up to 8 player slots" → "skirmish setup with up to **4** player slots (8 = post-1.0 fast-follow constant-bump; registry API stays PLAYER_COUNT-aware)". *Rationale: decision #3.*
**Story 3.11 Mode Select AC** *(epics.md:1294)*: "Skirmish 1–8 offline" → "Skirmish 1–4 offline"; Campaign entry text bound to real mission count; ranked/MMR/live-count elements removed. *Rationale: decisions #3/#4 + honesty strip.*
**GDD Phase 1** ("guided tutorial campaign of 5–8 missions") → "3-mission prologue campaign at 1.0; grows post-1.0" (+ the existing N/12 reconciliation note updated to N/3).
**GDD §3 Persistent heroes note** ("full mid-game single-player save/resume … post-1.0") → "mid-match SP save/load ships in 1.0 (Epic 11); MP save remains post-1.0".
**GDD §5/§6 player count**: add 1.0-reconciliation note: "verified at 2–4 players 1.0; 5–8 fast-follow".
**PRD**: new §4.14 FR-62..FR-78 (map: 62 hero runtime · 63 research · 64 items · 65 teams+N-faction resolution · 66 session shell · 67 SP save/load · 68 setup+loading · 69 editor parity floor · 70 trigger vocabulary v2+objectives · 71 trigger debugging · 72 import+sync · 73 prologue campaign · 74 match-feedback floor · 75 animation · 76 deterministic multi-AI · 77 replay UX · 78 playtest gates) + MVP statement edit + decision-log entry referencing this proposal.
**UX 2026-06-20 spec**: addendum listing the new screens (11.1/11.2/11.4/11.5/11.7 UI/12.1/13.1) — full UX-DR authoring happens per-story as with 3.11.

### 4K. Sprint re-sequencing (sprint-status.yaml)

Execution order after the change (new items **bold**):
Epic 2 finish (2.9b → 2.10 → 2.11 → **2.12 → 2.13**) → epic-2-retro → Epic 3 (3.1a…3.12, **3.13–3.17**) → Epic 4 (…, **4.8, 4.9**) → Epic 5 → Epic 6 (…, **6.6–6.11**) → Epic 7 (…, **7.11–7.16**) → Epic 8 → **Epic 11 (11.1–11.12)** → Epic 9 (9.1…9.13, **9.14–9.18**; 9.14 ordered before 9.5/9.6) → **Epic 12 (12.1–12.4)** → **Epic 13 (13.1–13.4)** → Epic 10 (10.1…10.11, **10.12–10.17**) → ship.
All new entries `backlog`; epics 11/12/13 get headers + `optional` retros. FR-39 LAN gate remains parked-on-hardware (unchanged, tracked).

---

## 5. Implementation Handoff

**Scope: MAJOR** — but solo-dev context collapses the role split: Claude (as PM/architect/dev under Alec's approval) applies the artifact edits; Alec approves.

| Step | Owner | Deliverable |
|---|---|---|
| 1. Apply epics.md changes (+52 stories, 2 AC edits, 3 new epic sections) | Claude, this session | epics.md updated, zero dangling `Depends on` |
| 2. Apply sprint-status.yaml resequence (+58 entries) | Claude, this session | yaml consistent with epics.md |
| 3. GDD/PRD/UX reconciliation edits (4J) | Claude, this session | docs consistent |
| 4. Resume implementation | `gds-create-story` → `gds-dev-story` per sprint order | next story = 2.9b (unchanged) |
| 5. Story-level design of new systems (VAT, save format, regions, alliances) | at `gds-create-story` time per BMAD flow | per-story tech specs |

**Success criteria:** every gap-register blocker/major maps to a story or a recorded descope; 173-story plan has zero dangling references; sprint-status matches epics; the three honesty edits (Mode Select, 5.6, GDD) leave no UI advertising an unbuilt feature.

---

*Approved by Alec: ______ (pending)*
