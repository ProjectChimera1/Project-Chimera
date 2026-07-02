# Project Chimera — 1.0 Gap Analysis vs the WC3 / World Editor Bar

**Date:** 2026-07-01 · **Method:** 9 parallel domain analysts over GDD/PRD/epics(121 stories)/architecture/sprint-status/deferred-work/UX/code + completeness critic + adversarial per-finding verification. 127 candidate gaps found; 13 blocker/major findings were adversarially verified before the verification fleet was cut short by the monthly API spend limit — **all 13 came back CONFIRMED, 0 refuted**. Unverified findings are labeled; each carries the analyst's doc/code evidence. 4 additional high-stakes claims were spot-checked directly in this session (P1-hardcode, no-animation-story, no-pause-story, setup-screen-assumed) — all held.

**User bar:** fully playable game at the end of the epics — no glitches, weird bugs, or unpolished feel; map editor comparable to the WC3 World Editor; fully operational UI. Art/model/texture/sound ASSET production excluded.

---

## Blocker & Major Findings (81)

### 1. [BLOCKER] Hero in-match XP/leveling runtime has no owning story

*Domain:* core-match-gameplay · *Status:* VERIFIED: CONFIRMED (severity: blocker)

**Gap:** Story 3.7 authors leveling curve, XP-gain rule, and signature/ultimate slots but its note says 'Authoring only — XP/leveling runtime is later-epic work' — and no later epic owns it. Grep of Epics 4-10 finds hero mentions only in 8.6b (LLM hero DRAFT generation) and 9.12 (online profile STORAGE). Nothing makes a hero gain XP from kills, level up, raise stats, or unlock abilities during a match. The hero-picker (3.9/UX-DR75) displays level/XP and the persistence rail (FR-7a/b) carries level/XP between matches, but nothing in a match ever changes them — the authored leveling curve is dead data. Story 3.10's AC even references 'hero XP gained during the playtest is discarded', assuming a runtime that no story builds. Also absent: any in-match inventory/item system despite FR-7a listing 'inventory' as a persistable attribute, and any hero death/revival rule.

**WC3 bar:** WC3 heroes are the genre-defining feature: they gain XP from kills, level up mid-match, spend skill points on abilities, carry items from shops, and revive at an Altar. A hero that can be authored with a leveling curve but never levels is visibly broken to any RTS player.

**Evidence:** epics.md Story 3.7 note (line ~1230: 'Authoring only — XP/leveling runtime is later-epic work'); Story 3.10 AC3 (line ~1278 references playtest hero XP); FR-5 (line 65), FR-7a (line 68), UX-DR75 (line 346); grep 'hero' over lines 1660-end shows only 8.6b/9.12; grep 'XP|level' over all epics shows no runtime story; GDD §3 unit framework ('A Hero is a... unit with an XP component, leveling table, and hero abilities').

**Suggested home:** Epic 3 (new sim story after 3.7, e.g. 3.7b 'HeroXpSystem: kill-credit XP, level-up stat/ability application, folded into SimChecksum') — depends on 7.4's killer-attribution death event or the CombatSystem RecordKill path.

**Verifier coverage notes:** Hero-touching stories are exactly: 3.2 (epics.md:1126-1144, HeroStore SoA — note says "builds the data substrate ONLY"), 3.7 (1214-1230, authoring — note verbatim: "Authoring only — XP/leveling runtime is later-epic work"), 3.8 (1232-1246, manifest authoring), 3.9 (1248-1264 — note: "Apply is INIT-TIME ONLY"), 5.5b (1576-1586, wizard carries hero ref/persistence flag as data), 8.6b (2134-2154, LLM hero DRAFT generation), 9.12 (2406-2418, Nakama STORAGE of the profile). None makes a hero gain XP, level up, raise stats, or unlock abilities mid-match. FR coverage table maps FR-5 → Epic 3 authoring only (line 374); FR-53..61 (the DG stories 1.12/1.13/2.11/3.12/4.7/6.5/7.10/9.13/10.11) are unrelated to heroes. Story 3.10 AC (line 1278) references "hero XP gained during the playtest is discarded" — assuming a runtime no story builds. Story 7.10 (1970-1992) even re-specifies the GDD's Assassination preset ("kill a specific hero unit", GDD:206) as "a designated leader entity dies" — sidestepping hero mechanics. No trigger-DSL escape hatch exists either: Epic 7's action vocabulary (7.2-7.8b) has variables/timers/events/loops/custom UI but no hero-XP or unit-stat write leaf, so a creator cannot even hand-build leveling. Sub-claims also hold: "inventory" appears only in FR-7a's persistable-attribute list (line 68) with no in-match item/inventory story; zero hits for revive/resurrect/altar in all of epics.md. deferred-work.md has NO hero XP/leveling entry — the punt is a dangling story note, not a tracked deferral. sprint-status.yaml lists only the same 7 hero stories, all backlog. Code ground truth: grep of godot/src for Hero/Xp/Experience/LevelUp hits only DamageTable.cs (the Hero damage/armor TYPE from 1.6) and AbilityDraft.cs — nothing built.

**Verifier notes:** Searched epics.md with hero/XP/level/experience/leveling/progression/skill point/inventory/item/revive/altar/grant/award (all 121 story headers enumerated to confirm no owner), the PRD + addendum, the GDD, deferred-work.md, sprint-status.yaml, and godot/src. What settles it: (1) Story 3.7's own note punts the runtime to "later-epic work" and the full story inventory shows no later epic owns it; (2) the PRD's M2 milestone line (prd.md:375) says "Hero sim ... land here" and the addendum's gap map (row 16) says "Add hero systems" — so the PRD INTENDED a hero sim that the epics silently dropped, which refutes any "it was never in 1.0 scope" defense; (3) the GDD demands it ("A 'Hero' is a... unit with an XP component, leveling table, and hero abilities" line 166; "hero arenas" as a target genre line 121); (4) the entire FR-7a-e persistence rail (4 stories + hero-picker UI showing level/XP) carries progression state that nothing in any match can ever change — the loop is provably dead, and no DSL action leaf can substitute. Severity check: per the rules, blocker = "no way to do X at all" — correct here. A creator authors a leveling curve (3.7), the picker displays level/XP (3.9/UX-DR75), online storage attests it (9.12), yet no hero can gain a single XP point in any match by any means. Mitigating fact (why one might argue major): the two FMA showcase factions' rosters contain no hero, so the out-of-box skirmish plays fine — but the product IS the WC3-World-Editor-class creation platform, heroes are Epic 3's title feature ("Author Units & Heroes"), and WC3 heroes leveling is the genre-defining feature the platform explicitly benchmarks. Blocker stands.

### 2. [BLOCKER] 3+ player matches cannot conclude: multi-faction victory, elimination, and multi-AI all out of scope

*Domain:* core-match-gameplay · *Status:* VERIFIED: CONFIRMED (severity: blocker)

**Gap:** The plan ships 3-8 player match SETUP (5.6 AC: 'skirmish setup with up to 8 player slots'; UX-DR68 'Skirmish vs AI (1–8, offline)'; Epic 9 ships verified ≤4-player MP with 9.2 expanding the faction model to 8) but no story makes such a match END or PLAY: Story 7.10's scope limit says 'Multi-team (>2 faction) free-for-all resolution beyond the existing P1/P2 two-faction assumption is out of scope', the as-built game-over path is a hardcoded P1/P2 check (MainScene.cs:1157-1174 'if (!p1Alive) ShowGameOver(2)'), there is no per-player elimination flow (defeated player -> observer/exit while the match continues), and Story 10.11's scope limit says 'only the existing two-player P1-vs-AI skirmish path... no multi-opponent' so nothing generalizes AiOpponentSystem (which hardcodes P1_BASE) to fill slots 3-8. Fog-of-war is also single-faction (FogOfWarSystem.cs:44 defaults Faction.Player1). End state: the lobby/skirmish UI offers 3-8 player games that have no AI opponents beyond one, no winner resolution, and no elimination handling.

**WC3 bar:** WC3 melee supports up to 12 players in FFA and team configurations; defeated players are eliminated with a defeat screen while the match continues, and victory resolves for the last player/team standing. AI fills any open slot.

**Evidence:** epics.md 7.10 dev note (line ~1992 explicit out-of-scope); 10.11 dev note (line ~2760 'no multi-opponent or team adaptation'); 5.6 AC3 (line ~1610 8-slot skirmish); UX-DR68 (line 339); Epic 9 stories 9.2/9.6 (lines 2200-2314); godot/src/Core/MainScene.cs:1157-1174; godot/src/Core/FogOfWarSystem.cs:44.

**Suggested home:** Epic 7 (extend 7.10's WinConditionSystem to N-faction last-standing + elimination verdicts) plus Epic 10 (generalize AiOpponentSystem to N instances) — or explicitly cut the 3-8 player promise from 5.6/UX-DR68/9.6.

**Verifier coverage notes:** Setup ships without resolution. Setup side (real): epics.md 5.6 AC3 (line 1610, 8-slot skirmish setup), 3.11 AC2 (line 1294, Mode Select 'Skirmish 1-8 offline' = UX-DR68 line 339), 9.6 (lines 2298-2314, verified 4-player MP join->ready->start; 8 = constant-bump fast-follow SD-8). Conclusion side (explicitly excluded): 7.10 (lines 1970-1992) builds the sim WinConditionSystem but its dev note line 1992 states 'Multi-team (>2 faction) free-for-all resolution beyond the existing P1/P2 two-faction assumption is out of scope' and AC1 only verifies the 2 built-ins reproduce the old P1/P2 presentation switch (as-built MainScene.cs:1141-1174 hardcoded p1Alive/p2Alive -> ShowGameOver(1|2), verified in code). Per-player elimination/defeat-continue flow: ZERO hits across epics.md/PRD/GDD/deferred-work (no observer-on-defeat, surrender, resign, concede anywhere; UX-DR64 line 333 lists MP states with no elimination state). Multi-AI: 10.11 dev note line 2760 'only the existing two-player P1-vs-AI skirmish path... no multi-opponent or team adaptation'; FR-43 (line 126) is singular 'the AI opponent'; 9.5 note line 2296 'Drop-to-AI is a D4 fast-follow, explicitly out of scope'; AiOpponentSystem.cs:50 P1_BASE hardcoded, verified. Partial touches (infrastructure, not the feature): 9.2 (lines 2200-2216) 8-faction model + N=3/N=4 determinism harness; 7.10's slot-shaped FactionWon(slot) verdict + faction-parameterized presets; T3 OnVictory(winnerFactionSlot) escape hatch (creator could hand-wire FFA victory — WC3 has it built-in); 5.7 (lines 1618-1636) + 10.2a run AI-vs-AI with 2 AI instances, so 'no AI beyond one' is marginally overstated, but nothing fills slots 3+ and the float-scoring AI is documented D2 debt barred from lockstep MP. Bonus unclaimed gap found: GDD line 562 promises '1v1 and 2v2 support' — no team/alliance model story exists anywhere (grep team|ally|alliance hits only lobby chat All/Team and team-color tint).

**Verifier notes:** Searched epics.md exhaustively (victory/defeat/eliminate/game-over/win-condition; observer/spectate/surrender/resign/concede; FFA/free-for-all/multi-team/last-standing; AI opponent/multi-AI/AI slot; team/ally/alliance) and read Epic 9 (2176-2447), Epic 10 head + 10.11 (2448-2530, 2738-2760), 7.10 (1970-1992), 5.6/5.7 (1598-1636), 3.11 (1284-1300), requirements inventory 38-439. Searched PRD (only win-condition AUTHORING: FR-22/UJ-1), GDD (promises 1v1+2v2 and 8-player browsing tags; no elimination/FFA design), deferred-work.md (line 25 defers only FactionRegistry array sizing to 9.2), and verified all three code citations (MainScene.cs:1141-1174, AiOpponentSystem.cs:50, FogOfWarSystem.cs:44) — all accurate. What settles it: the two stories that own victory (7.10) and AI (10.11) BOTH carry explicit scope-limit sentences excluding exactly this feature, and no other story, FR, or UX-DR picks it up — while 5.6/3.11/9.6 genuinely ship the 3-8 player setup/lobby surface, so the shipped product offers matches that cannot conclude, have no elimination flow, and (offline) no opponents in slots 3+. Severity blocker stands: 9.6's AC makes a started 4-player MP match a shipped 1.0 feature with undefined ending, and the Mode Select advertises 1-8 offline skirmish that has no AI to fill slots and no winner logic — a reviewer would call that a broken/unfinished game, and WC3 melee FFA+elimination+AI-fill is the explicit quality bar. Minor analyst overstatements that do not change the verdict: 2 AI instances CAN coexist (5.7 AI-vs-AI), and 7.10's verdict enum is slot-shaped plumbing.

### 3. [MAJOR] No team/alliance model: 2v2, allied vision, and allied victory are ungrounded

*Domain:* core-match-gameplay · *Status:* VERIFIED: CONFIRMED (severity: major)

**Gap:** No story anywhere defines teams/alliances: grep for '2v2|alliance|ally|shared vision|team game|FFA' over epics.md returns zero feature hits. The 9.6 lobby has player slots, faction select, and ready pills but no team assignment; there is no allied-victory rule (7.10 is 2-faction), no shared team vision (FogOfWarSystem stamps exactly one faction's vision), no ally-status in combat targeting beyond own-faction checks, and lobby chat 'All/Team' (UX-DR69) references a Team concept that exists nowhere in the sim. The GDD Phase 3 deliverables explicitly promise '1v1 and 2v2 support'.

**WC3 bar:** WC3 team games (2v2/3v3/4v4) with shared/allied vision, allied victory, and ally-only chat are half of the melee multiplayer experience.

**Evidence:** GDD §10 Phase 3 deliverables ('1v1 and 2v2 support'); epics.md grep for team/ally/2v2 (only 'All/Team' chat label in UX-DR69 line 340 and lobby items); 9.6 ACs (lines 2298-2314); FogOfWarSystem.cs:44 single-faction; 7.10 two-faction scope (line 1992).

**Suggested home:** Epic 9 (team assignment in lobby + allied-victory in the 7.10 WinConditionSystem + shared-vision union in FogOfWarSystem), or an explicit PRD decision deferring team games post-1.0.

**Verifier coverage notes:** No story/FR delivers teams, allied vision, or allied victory — none found. Adjacent-but-not-covering items: (1) Epic 9 N-player scaling — 9.1 (epics.md:2182-2198) widens checksum to all factions, 9.2 (:2200-2216) extends Faction enum to Player8, 9.6 (:2298-2314) delivers 4-player matchmaking/lobby/parties — but 'parties' are Nakama pre-matchmaking social groups (GDD:365 'group up before matchmaking'), and the 9.6 lobby AC lists slots/faction/color/ready/ping/chat with NO team assignment. (2) Story 7.10 WinConditionSystem (epics.md:1970-1992) — dev note explicitly punts: 'Multi-team (>2 faction) free-for-all resolution beyond the existing P1/P2 two-faction assumption is out of scope' — so a 9.6 four-player match has ungrounded victory semantics even as FFA, let alone allied victory. (3) TargetFilter.Ally (game-architecture.md:415 'WC3's Targets-Allowed') = same-faction-as-caster only in code (godot/src/Effects/TargetMatcher.cs:57 'ef == casterFaction'), no alliance table. (4) FogOfWarSystem (godot/src/Core/FogOfWarSystem.cs:42-46 ctor + :62 faction filter) stamps exactly one faction; no shared-vision story in any epic. (5) Architecture SD-6 (game-architecture.md:1131) admits it outright: 'decoupling slot from faction deferred until a teams feature exists' — Faction==player for 1.0. (6) Ungrounded UI references: UX-DR69 lobby chat 'All/Team' (epics.md:340, EXPERIENCE.md:39), mockup HUD.html:244 has an 'Alliances (F11)' button, Content Browser mockup tags scenarios '2v2' — none backed by any story. (7) GDD Phase 3 deliverable 'ships 1v1 and 2v2 support', objective 'networked play for 2-8 players' (Project_Chimera_GDD.md:558-562; Phase 5 = 1.0 release, so pre-1.0 promise). 'team' hits elsewhere are cosmetic team COLORS only (FR-51, UX-DR6/DR40, 10.8 epics.md:2598-2614). deferred-work.md 'ally' hits (:128,229,247) are ability heal-ally targeting, not alliances; sprint-status.yaml and STATUS.md have zero team/alliance entries.

**Verifier notes:** Searched: epics.md full-file grep for 2v2/alliance/allied/ally/allies/team/FFA/free-for-all/shared vision/diplomacy/melee/1v1 (all 121 stories incl. requirements inventory lines 38-439, full Epic 9 read at lines 2176-2447, story 7.10 read at 1955-1993, faction-registry story ~1608-1616); PRD dir (prd.md + decision-log — only Steam-substring and team-color hits); GDD (2v2 promised at line 562, 'allies' at :101 is separation steering, parties at :365 are pre-match); UX DESIGN.md/EXPERIENCE.md/mockups (team colors + unbacked All/Team chat, Alliances button, 2v2 tags); game-architecture.md (SD-6 'until a teams feature exists' at :1131 = explicit acknowledgment); deferred-work.md, sprint-status.yaml, STATUS.md; code grep of godot/src for alliance/IsAllied/TeamId/SharedVision (zero hits) plus TargetMatcher.cs and FogOfWarSystem.cs reads. Every 'ally' in the plan means own-faction. What settles it: the architecture itself defers slot!=faction 'until a teams feature exists', 7.10 explicitly excludes >2-faction resolution, and yet 9.6's AC requires shipping 4-player matches — so 1.0 as written ships multi-player lobbies whose matches have no alliance model AND no grounded >2-faction victory rule, while GDD Phase 3 and the UX mockups both promise team play. Severity: 'major' is right — 1v1 melee + the editor bar still function, so not a blocker, but team games are a WC3-parity expectation the GDD itself promises, aggravated by the internal 9.6-vs-7.10 inconsistency (4-player match, ungrounded victory).

### 4. [MAJOR] No research/upgrade system — tech tree gates production only

*Domain:* core-match-gameplay · *Status:* VERIFIED: CONFIRMED (severity: major)

**Gap:** Epic 4's tech tree (4.2/4.6) is building-prerequisite gating of production/placement only. No story lets a creator author a researchable upgrade (e.g. +1 attack, unlock ability), no building can queue research, no command-card research button exists, and nothing applies a faction-wide stat upgrade mid-match (the ModifierStore from 2.2b is the obvious primitive but has no research consumer). The GDD promises it directly: 'Structure — static, produces units or research' (§3 unit framework) and the utility AI's action list includes 'research' (§9). The AI's 'research' action can never fire.

**WC3 bar:** Every WC3 melee game revolves around researched upgrades: Melee/Ranged attack and armor upgrades at the Blacksmith, tier upgrades (Keep/Castle), and unit-unlock research (e.g. Berserker Upgrade) that take effect mid-match. An RTS creation platform whose showcase cannot express 'research +1 armor' would read as unfinished to any RTS player.

**Evidence:** GDD §3 ('produces units or research'), §9 (AI action list includes research); epics.md grep 'upgrade|research' — zero research-system hits (only 'upgraded reskin' FR-20 wording); Epic 4 stories 4.1-4.6 (lines 1332-1438) cover buildings/prereqs/resources/supply only; Epic 2 ModifierStore (2.2b) has no research consumer story.

**Suggested home:** Epic 4 (new story: research/upgrade definitions + building research queue + runtime application via ModifierStore, gated by the 4.2 prerequisite registry) with command-card surface in Epic 2's card system.

**Verifier coverage notes:** none found. Closest touches, none of which deliver research: FR-14 (prd.md:198) mentions "upgrades" as drag-dependency nodes but its runtime clause is production-gating only, and implementing stories 4.2 (epics.md:1350-1366, prerequisites resolved to BUILDING ids gating train/place) + 4.6 (epics.md:1422-1438, visual editor writing those same prerequisites) contain no upgrade node type; Story 2.6 passives (epics.md:936-964, "permanent Modifier on the owning entity" = per-unit, not researched/faction-wide); Story 5.4 signature mechanics (epics.md:1534-1556, passives); Epic 7 triggers embed D1 ApplyModifier subgraphs + FR-26 custom-UI buttons (epics.md:1786-1990) = a clunky creator-side workaround, not a research system (no queue, no command-card button, no faction-wide/future-unit application primitive); 10.11 `_techWeight` (epics.md:2748) only tunes existing AI build scoring.

**Verifier notes:** Searched: epics.md full-file grep for research (ZERO hits in 319KB), upgrade, tech tree/tier/unlock, morph/blacksmith/armory/faction-wide/permanent; read Epic 4 complete (lines 1326-1463 — stories 4.1-4.7 are buildings/prereq-gating/resources/supply/editors only), epic list 440-484, Epic 5 (1500-1556), Epic 7 (1766-1840). PRD+addendum grep: zero research hits; FR-53..61 (DG-1..9) none research. GDD: confirms both analyst citations verbatim (line 164 "produces units or research"; line 504 AI action list incl. research); GDD Tier-2 trigger vocabulary (line 208) has NO upgrade action. Code godot/src: grep Research|Upgrade → zero files; AI research action can never fire. deferred-work.md:281 explicitly calls an "upgrade/morph/tech path" a hypothetical FUTURE path. sprint-status.yaml/STATUS.md: no research entries (STATUS.md:305 tech tree = production gating). Severity stays major, not blocker: showcase factions' designed mechanics are passives so 1.0 is still playable, and Epic 7 triggers give a partial hack-around — but it is a WC3-parity feature every RTS player/creator expects and the GDD promises it twice.

### 5. [MAJOR] No shift-queued command waypoints (move/attack-move/order chaining)

*Domain:* core-match-gameplay · *Status:* VERIFIED: CONFIRMED (severity: major)

**Gap:** Story 1.12's scope limit explicitly excludes it: 'no queued/shift-click command chaining' — and no later story picks it up. The only queuing shipped is patrol-waypoint append (Shift+click while the P command is armed, SelectionSystem.cs:18). A player cannot shift-queue a series of move/attack-move orders, queue a command after a current one finishes, or waypoint a scouting worker. UX-DR66's keybinding list has Shift only for add-to-selection.

**WC3 bar:** Shift-queuing orders is a baseline RTS interaction (WC3, SC, AoE all have it): queue waypoints for scouts, queue a worker to build then return to gold, queue attack-move paths. Its absence is immediately felt by any experienced player.

**Evidence:** epics.md Story 1.12 dev note (line 810: 'no queued/shift-click command chaining'); UX-DR66 (line 335, no queue binding); grep 'waypoint|shift-queue|queued' finds no owning story; godot/src/UI/SelectionSystem.cs:18/213-218 (patrol-only shift append).

**Suggested home:** A new Epic 1-family DG story (sibling to 1.12: per-entity order queue SoA + shift-modifier issue path through OrderApplier, folded into SimChecksum) — the OrderApplier unification from 1.12 makes this structurally cheap.

**Verifier coverage notes:** none found. Closest touchpoints, none of which deliver the feature: Story 1.12 (epics.md:788-810) — dev note line 810 EXPLICITLY excludes it ("no queued/shift-click command chaining"); its only waypoint mechanic is the Patrol loop (AC line 802), which the claim already carves out. FR-53 (prd.md:332) = command vocabulary only, no queuing. GDD line 184 lists the "non-negotiable" commands (Move/Attack-Move/Patrol/Stop/Hold/Follow/Rally) with no queuing — the omission exists in the design docs themselves. UX-DR66 (epics.md:335) + EXPERIENCE.md:92 — Shift bound only to add-to-selection-group. Code: SelectionSystem.cs:18/99/213-218/624 = patrol-append-only Shift; STATUS.md:92 "per-entity waypoint queue" is PathRequestSystem's internal navmesh path points, not player order queuing.

**Verifier notes:** Searched epics.md whole-file (grep -i 'shift|queue|waypoint|chain' + second sweep 'queued|chaining|successive|consecutive|order buffer'; read all hits with context incl. omitted long lines 810, 1366, 1930, 2540): every hit is a different feature (production queues line 47, ability-intent queue 922, tech-tree chains 1360, DSL event queue 1860/1930, AI scoring shift 2741) — no story in Epics 2-10 picks it up. PRD prd.md + addendum (only hits: production queues line 138, FR-53 line 332). GDD (hits 164/186/339/391/566/584 — production queue, idle-units-shift-aside, PRNG, mod.io moderation queue; read §Movement and command system lines 182-189). deferred-work.md (read all omitted lines 35/114/138-139/181/235/287-293 — nothing on order queuing; nearest = rally-point lockstep replication line 8 and Patrol-no-nav-path line 114). sprint-status.yaml, STATUS.md, UX spec dir, and godot/src code (Shift usage = patrol append + edit-mode worker spawn only; Navigation 'waypoint/queue' hits are navmesh internals). Settles it: the exclusion is explicit in 1.12, no FR/story/UX-DR owns shift-queuing, it is not in deferred-work.md (so missing, not deferred), and it is absent even from the GDD's own command list. Severity stays major per rubric: a WC3-parity interaction virtually every experienced RTS player expects (queue scout waypoints, worker build-then-return, chained attack-move paths), immediately felt as unpolished — but the game remains playable, so not a blocker.

### 6. [MAJOR] Production queue is depth-1 with no queue display or cancel/refund

*Domain:* core-match-gameplay · *Status:* VERIFIED: CONFIRMED (severity: major)

**Gap:** BuildingSystem.TrainUnit hard-rejects a second order while one is training (BuildingSystem.cs:305 'if (ProductionQueue[buildingId] != 0) return false; // already training') — the 'queue' is a single byte per building. Story 2.8 added per-unit selection but explicitly preserved this single-slot model. No story adds multi-slot queues, a queue readout on the command card, or click-to-cancel with refund of a queued/training unit. Notably the approved HUD mockup (the shipped Claude-Design UI) promises 'production with build queues' — the requirement was lost in the UX-DR extraction (UX-DR71 lists no queue element).

**WC3 bar:** WC3 production buildings queue 7 units with visible queue slots; clicking a queued icon cancels it and refunds the cost. Single-slot production with no cancel would feel like a pre-1998 RTS.

**Evidence:** godot/src/Economy/BuildingSystem.cs:305,340; epics.md Story 2.8 (lines 984-998, preserves existing checks); UX mockup index.html:158 ('production with build queues') vs UX-DR71 (line 342, no queue item); no cancel/refund story in any epic (grep 'cancel|refund').

**Suggested home:** Epic 2 (extend 2.8: multi-slot ProductionQueue store + queue UI on the command card + cancel-with-refund riding the Train command's lockstep path) or Epic 4 alongside building authoring.

**Verifier coverage notes:** none found that delivers the feature. Partial touches only: epics.md Story 2.8 (lines 984-998) adds per-unit production selection but its AC explicitly preserves the existing single-slot gate (verified live at BuildingSystem.cs:305 'if (ProductionQueue[buildingId] != 0) return false' — done story, code is ground truth); Story 10.10 (lines 2716-2736) re-verifies the HUD against UX-DR71 (line 342), whose information hierarchy contains NO production-queue element — the polish story bakes the queue-less HUD in; Stories 4.1/4.2 (lines 1332-1366) data-drive building defs + tech gating but say nothing about queue depth, queue display, or cancel; Story 1.12 note (line 810) explicitly excludes 'queued/shift-click command chaining'. Paper claims that queues exist: epics.md line 47 + PRD line 138 ('Base building — ... production queues') and GDD line 164 (Structure archetype 'production queue') — all contradicted by the as-built depth-1 byte; STATUS.md:112 marks 'Production queues ✅' while describing a single 8-sec timer slot. No FR (checked FR-11/13/14 + FR-53..61/DG-1..10, epics.md lines 56-260, 238-251), no deferred-work.md entry, no sprint-status.yaml story covers multi-slot queues, a queue readout, or click-to-cancel with refund.

**Verifier notes:** Searched epics.md (grep queue/cancel/refund/production/train/command card across all 121 stories + requirements inventory lines 38-439), PRD prd.md + addendum.md, GDD, deferred-work.md, sprint-status.yaml, STATUS.md, UX dir, and code. Code confirms every analyst cite: BuildingSystem.cs:305 hard-rejects a second order; ProductionQueue is one byte/building encoding chosen-unit-index+1 (lines 340, 353-361); the only 'refund' in src/Economy (line 474) is an internal store-full rollback in QueueWorkerBuild, not player-facing; no CancelTrain exists. The UX-extraction-loss claim also verified: mockup HUD.html has literal .queue-row/.queue-slot/.queue-q ('+2' queued) markup and index.html:158 promises 'production with build queues', but UX-DR71 (epics.md:342) dropped the queue element and Story 10.10 verifies against UX-DR71 only. Not logged in deferred-work.md either — this gap is untracked, not deferred. Severity stays major (not blocker: training works one-at-a-time so the game is playable, but it is the rubric's exact 'WC3-parity feature virtually every RTS player expects' case, aggravated by silent rejection of the second click and by the shipped UI design promising queues).

### 7. [MAJOR] Rally points are not lockstep-replicated (known desync vector) and have no verify story

*Domain:* core-match-gameplay · *Status:* VERIFIED: CONFIRMED (severity: major)

**Gap:** deferred-work.md (2026-06-09 item 2) records that SelectionSystem writes HasRallyPoint/RallyPoint locally — not routed through EnqueueOrder, not in replays — so SpawnTrainedUnit's rally branch can diverge between peers: a live desync vector against the 'Zero desyncs' hard gate. No story owns the fix (grep 'rally' in epics.md hits only the Built-Foundation inventory line 47). Rally is also never verified to the ship bar (it predates the epics and FR-22 covers editor placement, not in-match rally), and rally-on-unit / rally-onto-resource (auto-harvest) doesn't exist.

**WC3 bar:** WC3 rally points work in multiplayer identically for all peers, can target a unit (new units follow it) or a gold mine/tree (workers auto-harvest). A rally desync would be a match-killing bug under the project's own HALT policy.

**Evidence:** deferred-work.md lines 8 ('Rally points are not lockstep-replicated... Route rally-set through the lockstep command stream' — unowned); epics.md grep 'rally' (line 47 built-foundation only, no story); hard gate line 155 ('Zero desyncs'); Epic 9 stories 9.1-9.5 do not mention rally.

**Suggested home:** Epic 9 (fold into 9.3a's command-stream work: a RallySet UnitCommand on the wire through OrderApplier) plus a rally-on-unit polish AC; at minimum it must land before FR-39's zero-desync claim is honest.

**Verifier coverage notes:** none found. Closest touches, none of which cover the gap: epics.md:47 lists rally under "Built Foundation" but the header (line 40) marks that inventory "reference only — NOT 1.0 scope"; Story 1.12 (FR-53/DG-1, epics.md:810) EXPLICITLY scope-excludes rally ("no rally points"); FR-53 (prd.md:332) omits Rally despite GDD:184 naming "Rally Point (per building)" a non-negotiable framework command; §4.10 verify FRs (epics.md:128-132) cover only FR-44 generic suite + FR-45's four named systems (rally not among them); Epic 9 stories 9.1-9.13 (epics.md:2182-2447) never touch rally (9.1 widens SimChecksum to per-faction resources only); deferred-work.md:8 records the defect with no owning story; FR-39 LAN runbook (godot/tools/lan-determinism-runbook.md) has zero rally steps. Rally-on-unit / rally-onto-resource auto-harvest appears in no FR/story/GDD text at all.

**Verifier notes:** Searched epics.md full-text for rally (case-insensitive; hits at 234/242/etc are substring false positives like "structurally"/"behaviorally"), plus EnqueueOrder/OrderApplier/command-stream phrasings and all Epic 9 story titles+ACs; PRD incl. §4.13 FR-53..61 and §4.10 FR-44..47; GDD; deferred-work.md; sprint-status.yaml; FR-39 runbook; and code. Code confirms the mechanism TODAY: SelectionSystem.cs:304-305→SetRallyPoint(:725-734) writes BuildingStore.RallyPoint/HasRallyPoint locally (no EnqueueOrder; godot/src/Multiplayer has zero rally references, no UnitCommand for rally), and BuildingSystem.SpawnTrainedUnit(:228-232) mutates sim state (CommandGoal/MoveTarget) from that local-only store → issuing peer moves the trained unit to rally, other peer spawns it Stop → SimChecksum divergence → fail-closed HALT. Story 2.8 (done 2026-07-01) routed Train=11 through the lockstep wire, so MP training is now real and the rally branch is a LIVE desync path, not latent. Goldens structurally cannot catch it (rally-set is not an order so it can never be recorded in a replay), and the FR-39 LAN runbook never exercises it. With all 121 stories done as written, rally in any MP match = guaranteed desync HALT, violating the project's own hard gate (epics.md:155 "Zero desyncs"). Severity: keep major — arguably blocker-adjacent for the MP surface (a bread-and-butter action kills the match), but rally works offline/solo and the fix is small+detectable during Epic 9 MP hardening; the WC3-parity sub-gap (rally-on-unit / rally-onto-mine auto-harvest, GDD authorizes ground-only "per building") is additionally confirmed missing everywhere.

### 8. [MAJOR] No in-match pause/resume or game-speed control anywhere in the plan

*Domain:* core-match-gameplay · *Status:* VERIFIED: CONFIRMED (severity: major)

**Gap:** Grep of epics.md and the codebase finds no pause capability outside the editor's Edit mode (MainScene.cs:23) and no game-speed setting: no SP pause (opening Settings mid-match does not stop the 30Hz tick), no MP pause command on the lockstep bus, no Slow/Normal/Fast speed option. The GDD §6 even promises 'The system displays the current game speed/delay in the UI'. No UX-DR, FR, or story covers any of it — the only 'pause' hits are hit-freeze docs explicitly saying it never pauses the tick.

**WC3 bar:** WC3 pauses single-player when the F10 menu opens, offers explicit Pause (also in MP with per-player limits), and has a game-speed setting (Slow/Normal/Fast) in options and via hotkeys. A skirmish you cannot pause to answer the door reads as unfinished.

**Evidence:** grep 'pause|game speed|resume' over epics.md (only AR-29 hit-freeze) and godot/src (only CombatFeedbackBridge/CombatFeedbackProfile 'NEVER pauses' + MainScene.cs:23 Edit-mode); GDD §6 input-delay section (game speed display promise); UX-DR73 Settings tabs contain no gameplay-speed item.

**Suggested home:** Epic 10 (10.10 HUD/controls verify story is the natural home for SP pause + speed; MP pause needs a small Epic 9 lockstep command story).

**Verifier coverage notes:** none found. Closest touchpoints, none of which deliver the feature: (1) UX mockup HUD.html:336-384 draws a 'Match Paused' overlay with Resume (+ index.html:159 tags 'Victory / Pause' views) but NO UX-DR/FR/story references it — the HUD story (epics.md:2724, UX-DR71) and shell-restyle Story 3.11 (epics.md:1284-1300, UX-DR67/68/73) enumerate their screens exhaustively and omit it, so it never gets built; (2) AR-29/Story 2.7 hit-freeze explicitly 'never pauses the sim tick' (epics.md:216, 840, 982; CombatFeedbackProfile.cs:43); (3) editor Edit-mode F5 toggle pauses the sim (MainScene.cs:19-28) but is an authoring control gated away from Commanders (UX-DR63, epics.md:332/1592); (4) Story 9.5 freeze-and-continue (epics.md:2288) is disconnect-triggered only; (5) GDD:351 'displays current game speed/delay' = the lockstep input-delay indicator, not a speed control; (6) Settings UX-DR73 tabs + Shell.html Gameplay tab contain only camera 'scroll speed' (Shell.html:341); (7) PRD save/RESUME hits are the [v2] mid-game save feature (prd.md:110/169/413, addendum.md:75-76), unrelated. No pause opcode exists in the lockstep command vocabulary and no Engine.TimeScale/GetTree().Paused usage in godot/src outside the editor toggle.

**Verifier notes:** Searched epics.md in full (pause/resume/game-speed/timescale/slow/fast-forward/Esc/pause menu/surrender/victory/Settings/HUD/delay patterns; read all omitted long-line hits at 840/982 and the UX-DR inventory 271-350), PRD prd.md + addendum.md + decision-log.md, the entire GDD (zero 'pause' hits; verified §6 line 351 context is input-delay display), deferred-work.md (all matched lines read — move-speed buff lag, reconnect, nothing pause-related), sprint-status.yaml, STATUS.md, UX DESIGN.md/EXPERIENCE.md + mockups (found the unplanned 'Match Paused' mockup), and godot/src code grep. What settles it: zero pause/speed hits across FR-1..61, all AR/UX-DR/DG entries, and all 121 stories; the only in-plan 'pause' text is the hit-freeze fence saying the tick is NEVER paused; and the project's own UX mockup contains a pause menu that no story implements — confirming the gap extends to an internal design/coverage mismatch. Severity stays major, not blocker: the game is playable end-to-end without pause, but SP pause + speed control are universal RTS expectations (WC3 F10/pause/speed), so shipping without them reads clearly unpolished.

### 9. [MAJOR] Mid-match save/load of a single-player skirmish is explicitly deferred post-1.0

*Domain:* core-match-gameplay · *Status:* VERIFIED: CONFIRMED (severity: major)

**Gap:** The GDD's persistent-heroes section states: 'This is distinct from a full mid-game single-player save/resume (full-world serializer), which is a separate, post-1.0 capability.' No FR or story covers saving a skirmish mid-match and resuming later — the original GDD Phase-1 deliverable 'Save/load game state' was reconciled away. The deterministic architecture makes a cheap alternative possible (save = command log + seed, resume = fast-forward replay, exactly the Epic 9 reconnect model captured in deferred-work.md) but nobody owns it. Note: this is an explicit, documented deferral — reported per the audit rule that a deferred item the 1.0 bar needs is still a gap.

**WC3 bar:** WC3 lets you save and load any single-player game (campaign or skirmish) mid-match — F10 > Save Game. Long skirmishes with no save option would be a visible regression from the 2002 bar.

**Evidence:** GDD §3 'Persistent heroes & cross-game progression' final paragraph (explicit post-1.0 deferral); GDD §10 Phase 1 deliverables ('Save/load game state'); no FR in PRD §4.x covers it (FR coverage map lines 368-438); deferred-work.md reconnect design (lines 75-89) shows the command-log replay machinery exists.

**Suggested home:** Epic 10 or a post-1.0 decision made explicit; cheapest 1.0 shape is command-log save/resume reusing the .chmr replay machinery (same lift as Epic 9's reconnect v1).

**Verifier coverage notes:** none found for mid-match save/resume. Near-misses that do NOT cover it: FR-7a–e + Story 3.9 (epics.md lines 1248-1260) = hero-profile save/load applied at match INIT only — Story 3.8 AC (epics.md:1242) explicitly forbids "anything that would imply a mid-game snapshot"; FR-21 + Story 6.2 (epics.md:1684) = editor terrain persistence; epics.md:1736 = map-editor save round-trip; FR-40 = watch-only .chmr replays. Explicit deferrals: GDD:180, prd.md:169, prd.md:413, addendum.md:75-76.

**Verifier notes:** Searched epics.md (all epics + requirements inventory) for save/load, save game, resume, quit, suspend, snapshot, fast-forward, pause, F10, in-game/escape menu — zero stories touch mid-match save; only hero-picker (3.9), terrain (6.2), and map save round-trips hit. PRD + addendum both mark mid-game single-player save/resume [v2 — out of 1.0] (prd.md:169, 413; addendum §G:75-76, which also notes the engine has no world serializer). GDD:180 = the analyst's exact quote; GDD:538 (§10 Phase 1 deliverables) confirms the original "Save/load game state" promise that was reconciled away. sprint-status.yaml: only 3-9 and 6-2 mention save. deferred-work.md:75-89 confirms the command-log-replay resume machinery is a captured-but-unscoped direction (reconnect design; v2 snapshot+tail "needs a NEW save/restore of live SoA sim state"). Code check: godot/src has no save-game path (STATUS.md:127 ScenarioSerializer = editor map save only). Severity stays major: WC3's F10 save is a parity feature every RTS player expects — made more visible by the 1.0 5-8 mission tutorial campaign (GDD:538) — but skirmishes remain completable in one sitting, so not a blocker.

### 10. [MAJOR] Mode Select ships a 'Campaign & Tutorial (N/12)' entry with zero campaign/tutorial-mission stories

*Domain:* core-match-gameplay · *Status:* VERIFIED: CONFIRMED (severity: major)

**Gap:** Story 3.11's AC requires Mode Select to render 'Campaign & Tutorial (N/12)' (UX-DR68), and the GDD's 2026-06-21 reconciliation note affirms '5–8 is the canonical campaign scope' — but no story in any epic authors campaign missions, a mission-progress tracker, a campaign flow, or a player-facing tutorial (5.8 is CREATOR onboarding; 10.1 verifies skirmish only). At the end state, the shipped front-end has a top-level menu entry that leads nowhere, and the GDD's promised guided tutorial campaign does not exist.

**WC3 bar:** WC3 ships with campaigns and a tutorial; more importantly, a main-menu button that dead-ends violates the user's own 'fully operational UI' bar even if campaign scope were cut.

**Evidence:** epics.md Story 3.11 AC2 (line 1294) and UX-DR68 (line 339); GDD §10 Phase 1 ('guided tutorial campaign of 5–8 missions' + the 1.0 reconciliation bracket keeping 5-8 canonical); grep 'campaign|tutorial|mission' over epics.md returns only lines 339/1294/2414 — no authoring story.

**Suggested home:** Either a new content story in Epic 10 (author 5-8 missions as scenarios using the Epic 7 trigger DSL + a campaign progress screen) or a deliberate scope cut that removes the Mode-Select entry and amends the GDD.

**Verifier coverage notes:** none found. Nearest misses, none of which cover the claim: Story 3.11 AC2 (epics.md:1294) only RENDERS the 'Campaign & Tutorial (N/12)' Mode Select entry (UX-DR68, epics.md:339) — it never implements a destination; Story 5.8 (epics.md:1640-1656) is CREATOR onboarding ('Your First Scenario', NFR-2), not a player tutorial; Story 10.1 (epics.md:2454-2470, FR-43) verifies skirmish-vs-AI only. No FR in FR-1..FR-61 covers campaign/tutorial missions (only prd.md:169 + addendum.md:76, both marking single-player save/resume [v2]). PRD §5 Non-Goals (344-356) and §6.3 Out of Scope (381-382) do NOT de-scope campaign — never a conscious cut. deferred-work.md, sprint-status.yaml, STATUS.md: zero mentions. godot/src: zero 'campaign' code matches.

**Verifier notes:** Searched epics.md case-insensitively for campaign|tutorial|mission (3 hits: 339, 1294, 2414='permissions' false positive) plus synonyms onboard|guided|single-player|story mode|N/12|scripted scenario (only creator-side hits); read Story 3.11 (1284-1300), Story 10.1 (2454-2470), Epic 10's full story list (10.1-10.11, no content/mission story); grepped PRD+addendum, deferred-work.md, sprint-status.yaml, STATUS.md, godot/src. Settling evidence: GDD line 538's 2026-06-21 reconciliation bracket keeps '5–8 is the canonical campaign scope' and says N/12 'should be bound to the real shipped mission count' — which the 121 stories make zero; and the readiness report itself (line 278) listed 'Single-player save/load + campaign mission-select/briefing UI' as a gap, yet the applied triage (line 420, DG-1..DG-9) authored no campaign story and no formal deferral exists anywhere. End state as written: a top-level Mode Select entry that dead-ends, no player tutorial, no campaign — while the GDD (declared source of truth) keeps it canonical 1.0 scope. Severity: kept at major — blocker-adjacent (dead-end top-level button violates the 'fully operational UI' bar), but 1.0's core loop (skirmish/MP/creation/sharing) is playable without campaign and the UI half is trivially fixable; the content gap matches the 'WC3-parity feature virtually every RTS player expects' = major definition.

### 11. [MAJOR] No human playtest / fun-feel gate on the melee experience anywhere before (or during) Epic 10

*Domain:* core-match-gameplay · *Status:* VERIFIED: CONFIRMED (severity: major)

**Gap:** Every 'playtest' in the plan is automated or functional: 5.7 validates faction asymmetry via AI self-play metrics (composition distance / win-rate band), 10.1 is a pass/fail load-and-complete matrix, 10.2a/b is a headless AI self-play balance harness. FR-42's 'playtest sample' is satisfied by AI self-play. No story ever puts a human in front of the melee game to judge feel, pacing, control responsiveness, or fun — despite the GDD's Phase-1 risk checkpoint: 'If playtesting reveals the core RTS loop is not fun, stop everything and iterate on game design' and success criteria requiring external playtesters in blind tests. Given the user's bar ('no glitches, weird bugs, or an unpolished feel'), nothing in the plan can detect an unpolished feel.

**WC3 bar:** Blizzard's melee polish came from relentless human playtesting; no amount of AI self-play win-rate tuning detects clunky unit response, bad camera feel, or unreadable fights.

**Evidence:** epics.md 5.7 ACs + resolved note (lines 1618-1638, objective-metric substitution); 10.1 (lines 2454-2472); 10.2a/b (lines 2474-2502); FR-42 (line 125 'AI self-play / playtest sample'); GDD §10 Phase 1 risk checkpoint and success criteria (external playtesters, 30-min sessions).

**Suggested home:** A new gate story between Epic 5 and Epic 10 (structured human playtest sessions with a feel-issue triage list feeding 10.x polish), using the existing gds-playtest-plan skill.

**Verifier coverage notes:** none found (as a fun-feel gate). Closest touches, none of which judge feel: (1) Story 10.1 (epics.md:2454-2472) — a human "tester" does play every map×difficulty, but the gate is purely functional (loads, AI builds/attacks, win/loss reachable, no crash/soft-lock) and its AC3 explicitly says "non-blocking polish issues are filed but not necessarily fixed here" — and NO later story consumes those filed issues; its readiness-triage note (line 2472) deliberately replaced subjective judgment with an objective metric (Hard-vs-Easy first-attack tick). (2) Story 5.7 (lines 1618-1638) — the only story titled "playtest"; its resolved note (line 1638) explicitly swapped 'observably asymmetric' for composition-distance/win-rate numbers, i.e. subjective human judgment was engineered OUT. (3) 10.2a/b (lines 2474-2502) — headless sim-only AI self-play, no human. (4) FR-42 (epics.md:125, prd.md:285) — the "playtest sample" clause is satisfiable by AI self-play alone, and 10.2a/b is how the plan satisfies it. (5) Stories 1.9b AC4 / lobby 9.x UX-DR84 (lines 714-720, 2310-2312) — humans on two machines, but the climax metric is "zero desync", a determinism gate. (6) Story 10.10 (lines 2716-2736) — HUD/controls/keybinding correctness verify, functional ACs only. (7) FR-12a "juice" default (line 80; shipped in 2.7) — feedback systems exist but no story has a human judge them.

**Verifier notes:** Searched: epics.md full-file greps for playtest/play-test (all 20 hits read in context: FR-7/19/20/42 inventory lines 67/91/92/125, stories 3.10, 5.6, 5.7, 5.8), fun/feel/human/usability/juice/game-feel (hits only in FR-12a juice, 2.x flavor text, 10.4/10.11 story motivations — none are gates), and external/blind/session-length/friend/volunteer (hits only colorblind palettes, external art pipeline, UX-DR84 LAN-two-friends whose target is zero-desync). Read Epic 10 in full (lines 2448-2761): 10.1 functional matrix, 10.2a/b headless harness, 10.3 perf, 10.4-10.9b assets/accessibility/release, 10.10 HUD verify, 10.11 adaptive AI — no human feel gate anywhere. PRD prd.md: playtest appears only at lines 161/213/214/285/314 (validation, selectability, asymmetry, FR-42 AI-self-play-satisfiable, NFR-1 loop speed); addendum.md: zero matches. GDD: lines 540/542 DO require "External playtesters find the core loop fun in blind tests. Average session length exceeds 30 minutes" + the stop-everything risk checkpoint, and lines 610-614 name design questions that "require iterative playtesting with real Architects" (trigger-editor complexity ceiling, AI quality floor) — none of this was carried into any epic/story/FR; the 2026-06-22 GDD↔epics reconciliation didn't add it. deferred-work.md: no playtest/feel deferral (the 3 grep hits are unrelated code-review items). sprint-status.yaml: only 3-10/5-6/5-7 mention playtest, all covered above. Glob _bmad-output/**/*playtest*: no playtest-plan artifact ever produced (the gds-playtest-plan skill exists but was never run). Settles it: the plan's two explicit chances to encode human judgment (5.7 AC3, 10.1 AC2) were both deliberately converted to objective machine metrics in the 2026-06-21 readiness triage, so the absence is systematic, not an oversight in my search. Severity check: keep MAJOR, not blocker — this is a process/verification gap, not a missing player-facing feature (a reviewer can't point at "no way to do X"; the solo dev also plays hands-on constantly via godot-verify and 10.1 forces ~36 human-played matches), but per the user's own bar ("no unpolished feel") and the GDD's own stop-everything checkpoint, nothing in the 121 stories can detect clunky control response, bad pacing, or unreadable fights before ship — squarely major.

### 12. [MAJOR] Behavior components / auto-cast support runtime has no owner (healers must be fully manual, support units half-broken)

*Domain:* core-match-gameplay · *Status:* unverified (verify agent lost to spend limit)

**Gap:** FR-4/Story 3.6 author 'orthogonal ability/behavior components' (GDD: healer = ranged + heal ability + SUPPORT AI BEHAVIOR) as data only — no story ever executes a behavior component. There is no auto-cast: every active ability requires a manual command-card click per cast (2.4b). Single-target ALLY-targeted abilities (heal-other) are deferred with no owning story ('needs a target-affinity flag... no sample needs it yet', deferred-work 2.4b item 2). Worse, zero-damage units are skipped by CombatSystem's pre-switch guard, so a damageless support unit cannot Patrol, Follow, or Attack-Move at all (deferred-work 1.12 item 2 and 2.9a item 1, both labeled 'Epic-2 concern' but owned by no story). Net: the GDD's own flagship composition example — the healer — cannot be built to a playable standard.

**WC3 bar:** WC3 Priests auto-cast Heal while attack-moving with the army; support units accept all movement orders. Manually clicking every heal on a 3-unit selection is not shippable support gameplay.

**Evidence:** epics.md 3.6 (lines 1196-1212, authoring-data-only note); GDD §3 ('a ranged unit plus a heal ability plus a support AI behavior'); deferred-work.md story-2.4b item 2 (ally-target picker), story-1.12 item 2 + story-2.9a item 1 (zero-damage command class, 'a future fix would normalize the whole zero-damage-non-gatherer command class' — unowned); godot/src/Combat/CombatSystem.cs:111.

**Suggested home:** Epic 2 (new story: behavior-component runtime — auto-cast policy via the deterministic sim + ally target-affinity on AbilityDefinition + lift the zero-damage pre-switch skip into per-command handling).

### 13. [MAJOR] 'Under attack' alerts and minimap ping designed in the approved HUD but dropped from requirements and stories

*Domain:* core-match-gameplay · *Status:* unverified (verify agent lost to spend limit)

**Gap:** The approved, being-shipped HUD mockup contains an alert system ('Under attack! Your base at grid C-4 is taking damage' toast, alert area, Combat Alert demo toggle) and a minimap Ping (G) button — but the UX-DR extraction (UX-DR71 HUD hierarchy) omitted both, and no FR or story implements offscreen-attack alerts, minimap event pings, or player-to-ally pings. In fog-of-war melee, being attacked offscreen with no alert is a functional hole, not just polish.

**WC3 bar:** WC3's 'We're under attack!' voice + minimap flare and allied minimap pings are baseline melee UX; every RTS since Dune 2 warns you when your base is hit.

**Evidence:** ux-Project_Chimera-2026-06-20/mockups/project-chimera/project/Design System.html:290 (Under attack! toast), HUD.html:80-82,249-250,311 (alert area + Ping (G) button), index.html:158 ('minimap, control groups, alerts'); epics.md UX-DR71 (line 342) and grep 'under attack|alert|ping' — no story coverage (only DesyncAlert).

**Suggested home:** Epic 10 (fold into 10.10 HUD verify/harden: attack-event alert toasts driven by the existing CombatEventQueue + minimap ping) — the toast component (UX-DR28) already ships in 3.1c.

### 14. [MAJOR] MP lobby designs include AI slots but AI is float-based and no story makes it lockstep-safe

*Domain:* core-match-gameplay · *Status:* unverified (verify agent lost to spend limit)

**Gap:** UX-DR69 and EXPERIENCE.md specify lobby player slots of type 'host / peer / AI', and 9.6's lobby story renders those slots — but AiOpponentSystem uses float math (13 occurrences; documented as a HARD PREREQUISITE: 'MUST be converted to Fixed before ANY AI runs in lockstep MP'), and Story 10.11 explicitly keeps 'existing Score* float weights unchanged'. No story converts the AI to Fixed or alternatively removes AI slots from the MP lobby. As planned, either MP-with-AI ships desync-broken, or the lobby's designed AI slots silently don't exist — both fail the bar.

**WC3 bar:** WC3 lets you add Computer players to any multiplayer/LAN lobby slot; hosting a 1v1 vs a friend plus an AI is routine.

**Evidence:** epics.md UX-DR69 (line 340 'player slots (host/peer/AI...)'); EXPERIENCE.md:39; 9.6 ACs (lines 2304-2308); deferred-work.md MP-resilience section (lines 81-84, float→Fixed hard prerequisite, 'shared prerequisite with... Story 10.11'); 10.11 dev note (line 2760 'existing Score* float weights are unchanged').

**Suggested home:** Epic 9 (either a 9.x story: AiOpponentSystem float→Fixed + AI-slot injection into the lockstep stream, or an explicit AC in 9.6 hiding AI slots in MP lobbies for 1.0).

### 15. [BLOCKER] No story builds the skirmish setup screen (map pick, per-slot faction/AI/difficulty, start)

*Domain:* game-shell-ui · *Status:* unverified (verify agent lost to spend limit)

**Gap:** The single most-used player screen in an RTS is assumed but never built. As-built, 'Play Skirmish' on MainMenuOverlay drops straight into Play mode on the Inspector-configured ScenarioPath, and AI difficulty is a Godot-Inspector [Export] field (STATUS.md Phase 5). Story 3.11 only restyles Mode Select; Story 5.6's ACs reference 'the skirmish setup with up to 8 player slots' as if it exists (it only adds faction assignment to it); Story 10.1 AC2 says 'Given a difficulty selection in the skirmish setup UI' but 10.1 is explicitly 'a structured playability pass, not new systems'. No AC anywhere requires building map selection, slot open/close, difficulty-per-slot, or a Start flow for Commanders. With all 121 stories done as written, a player still cannot pick a map or difficulty without editor surfaces (the O-key content browser is Edit-mode-only, which NFR-3 forbids for Commanders).

**WC3 bar:** WC3's Custom Game / skirmish screen: map list with preview + player slots (Open/Closed/Computer w/ difficulty), race, team, color, handicap, Start.

**Evidence:** epics.md 3.11 (lines 1284-1300), 5.6 ACs (lines 1598-1616), 10.1 AC2 (lines 2463-2470 'verify-only'); STATUS.md Phase 5 MainMenuOverlay + '[Export] AiLevel'; grep 'skirmish setup|map pick|map select' across epics.md — no owning story

**Suggested home:** New story in Epic 5 (sibling of 5.6, which already touches the surface) with a verify hook in 10.1; needs correct-course to add

### 16. [BLOCKER] No in-match game menu: no pause, no quit-to-menu/concede, no post-match continue

*Domain:* game-shell-ui · *Status:* unverified (verify agent lost to spend limit)

**Gap:** There is no storied way for a Commander to leave a match. Zero hits for 'pause', 'surrender', 'quit to menu', or a post-match Continue anywhere in epics.md or EXPERIENCE.md. As-built: Escape opens the SettingsPanel (no quit option), F5 returns to Edit (an authoring surface NFR-3/UX-DR63 forbids showing Commanders), and the victory/defeat card has no storied Continue/Return-to-menu button. Single-player pause does not exist (the sim runs or the match is over). A skirmish you can only exit by killing the process is a dead-end state, which the user's 'fully operational UI' bar explicitly fails.

**WC3 bar:** WC3 F10 menu: Pause, Save/Load, Options, Restart Mission, End Campaign/Quit Mission with confirm; MP has surrender/leave; score screen has a Continue button back to the shell.

**Evidence:** grep 'pause|surrender|quit' in epics.md (only 'never pauses the sim tick' re hit-freeze, and Title-nav Quit); EXPERIENCE.md Information Architecture/State Patterns (no in-game menu, flows end at HUD); STATUS.md P2.3/P2.6 (Escape=settings, F5->Edit dismisses game-over overlay)

**Suggested home:** New story in Epic 10 alongside 10.10 (in-match shell hardening), leaning on the 3.1c dialog component for confirm-quit

### 17. [BLOCKER] GDD-canonical 5-8 mission tutorial campaign has zero FRs/stories, yet Mode Select ships the button

*Domain:* game-shell-ui · *Status:* VERIFIED: CONFIRMED (severity: blocker)

**Gap:** GDD Phase 1 deliverables include 'A guided tutorial campaign of 5-8 missions' and the 2026-06-21 readiness reconciliation note explicitly kept it canonical ('5-8 is the canonical campaign scope; the UX Mode-Select "Campaign N/12" denominator... should be bound to the real shipped mission count'). Story 3.11's AC then builds a 'Campaign & Tutorial (N/12)' entry on Mode Select per UX-DR68. But the PRD has no campaign FR (grep: only the addendum's save/resume mention), and no epic/story authors a single mission, a mission-select screen, briefings, progression tracking, or the campaign flow. As planned, 1.0 ships a main-nav button to a mode that does not exist — a dead-end screen — or the reconciled GDD promise is silently broken. Either build a minimal tutorial-campaign slice or make an explicit descope decision and remove the button.

**WC3 bar:** WC3's campaign is its tutorial and its front door: mission-select with progress, briefings, mid-mission saves; 'Campaign' is a primary main-menu item that always works.

**Evidence:** Project_Chimera_GDD.md line 538 (deliverable + 1.0 reconciliation note); epics.md UX-DR68 (line 339) + 3.11 AC (line 1294); grep 'campaign' in prd dir (no FR) and in epics.md (only the two Mode-Select lines); STATUS.md Phase 1 has no campaign row (never built)

**Suggested home:** correct-course scope decision: either a new post-Epic-7 story set (missions are trigger-DSL content) or a 3.11 descope removing the button

**Verifier coverage notes:** No FR/story delivers the campaign — "none found" for the feature itself. Touching-but-not-covering items: Story 3.11 AC (epics.md:1294) + UX-DR68 (epics.md:339) build only the Mode Select "Campaign & Tutorial (N/12)" entry; Story 10.8b-3 (epics.md:2682-2684) builds briefing-subtitle plumbing that explicitly "degrades to no-op when no VO plays"; Story 5.8 (epics.md:1640-1656) is creator-suite onboarding (NFR-2), not a player tutorial; Story 10.1 (epics.md:2454-2472) validates skirmish-vs-AI only. PRD: no campaign FR — prd.md:169/addendum.md:76 only mention save/resume "for single-player campaigns" as v2/out-of-1.0.

**Verifier notes:** Searched epics.md (all epics + requirements inventory 38-439 + epic list 440-484) for campaign/tutorial/mission/briefing/onboarding/guided/"story mode"/"learn the ropes"/N-12 — only the two Mode-Select lines (339, 1294), UX-DR43 subtitles (308→10.8b-3), and creator onboarding 5.8. GDD line 538 verified verbatim: "A guided tutorial campaign of 5–8 missions" + the 2026-06-21 reconciliation note keeping 5-8 canonical and requiring N/12 to bind to the real shipped mission count — so the promise was actively reaffirmed, not descoped. deferred-work.md: zero matches (NOT an explicit descope). STATUS.md: zero matches (never built). sprint-status.yaml: only "mission" inside "permissions" in a comment. godot/src: no Campaign/Tutorial symbol. UX mockups design the Mode Select card ("Learn the ropes, then play the story") but no campaign/mission-select screen exists even in UX. Settles it: 1.0 as planned ships a primary main-nav entry to a nonexistent mode, and the game's only tutorial-for-players is absent (5.8 targets creators). Analyst's evidence checked line-for-line and accurate. Severity blocker stands: dead-end top-level button + no player tutorial at all fails "fully playable, no unpolished feel" and the WC3 bar where Campaign is the front door; fix requires either a minimal mission slice + mission-select/progression or an explicit descope decision removing the UX-DR68/3.11 entry and amending GDD:538.

### 18. [MAJOR] Mode Select advertises ranked, MMR, and live online count with no backing systems

*Domain:* game-shell-ui · *Status:* unverified (verify agent lost to spend limit)

**Gap:** UX-DR68/EXPERIENCE.md specify Multiplayer '(ranked / LAN / private + live online count)' and an account chip carrying '(name, level, MMR)'. Story 3.11 builds this screen. No story anywhere implements ranking, MMR, player levels, or an online-presence count (Nakama integration is unranked 1v1 matchmaking; 9.6 parameterizes N and parties only). Implemented as spec'd, the shell shows fake stats and a ranked entry that leads nowhere; player display-name management is also unowned (LAN lobbies have no name source at all).

**WC3 bar:** WC3 Battle.net showed real ladder/rank and profile data; buttons on the shell always led to working modes.

**Evidence:** epics.md line 339 (UX-DR68) + 3.11 AC line 1294; EXPERIENCE.md line 38; grep 'ranked|MMR|online count|profile' — no implementing story in Epics 8/9

**Suggested home:** 3.11 AC amendment (descope to 'LAN / Online' + name-only account chip) via correct-course

### 19. [MAJOR] No video/display settings: resolution, fullscreen/window mode, vsync, quality presets are nowhere

*Domain:* game-shell-ui · *Status:* VERIFIED: CONFIRMED (severity: major)

**Gap:** The Settings overlay has a 'Graphics' tab (UX-DR73, built in 3.11), but no story ever populates it. As-built SettingsData has zero display fields (camera speed, volumes, minimap/FPS toggles, colorblind only — STATUS.md Phase 5). The only graphics-adjacent stories are UI scale + reduced motion (10.8b-1) and light theme (10.8b-2). Grep across epics.md and DESIGN.md finds no resolution/fullscreen/vsync/quality-preset requirement. 10.8b-1 tests at 1080p/1440p/4K, implicitly assuming the player can change resolution — there is no UI to do it. A 2026 PC RTS with an empty Graphics tab and no fullscreen toggle fails the 'fully operational UI' bar.

**WC3 bar:** WC3 Options > Video: resolution, gamma, model/texture/effects quality; every PC RTS since has fullscreen/windowed + vsync at minimum.

**Evidence:** grep 'resolution|fullscreen|vsync|quality preset' in epics.md (only UX-DR46 'UI scale + resolution' as a form-factor note) and DESIGN.md (zero matches); STATUS.md SettingsData field list; 10.8b-1 ACs (epics.md 2634-2650)

**Suggested home:** New 10.8-family story (10.8d 'display settings'), extending versioned SettingsData (AR-5)

**Verifier coverage notes:** none found for the actual feature. Adjacent-only: Story 3.11 (epics.md:1284-1300, AC at :1296) builds the Settings overlay with a named-but-empty Graphics tab (UX-DR73, epics.md:344); Story 10.8b-1 (epics.md:2634-2650) = UI scale 80-150% + reduced-motion; 10.8b-2 (:2652-2666) = light theme; 10.8 (:2598-2614) = colorblind/contrast; 10.3 (:2504-2522) = a dev-side performance measurement pass with no player-facing quality options. UX-DR46 (epics.md:313) and EXPERIENCE.md:125 mention 'UI scale + resolution' only as a form-factor adaptation note. FR-51 (epics.md:139) lists keys/colorblind/UI-scaling/subtitles — no video settings. PRD+addendum: 0 matches. GDD: 0 matches. deferred-work.md: 0 (not even deferred). sprint-status.yaml: 0. Code: SettingsData.cs has zero display fields; no DisplayServer.Window*/VSync call in godot/src; project.godot has no [display] window config.

**Verifier notes:** Grepped epics.md (all 121 stories + requirements inventory 38-439) for resolution/fullscreen/windowed/vsync/borderless/display-mode/quality-preset/gamma/MSAA/graphics; read 3.11, 10.3, 10.8-10.8c ACs in full; grepped the whole PRD dir (incl. addendum), the GDD, both UX docs, deferred-work.md (read the 2 'display' hits — both headless-server detection, unrelated), sprint-status.yaml, STATUS.md Phase 5 settings row, and code ground truth (SettingsData.cs full field list, project.godot, DisplayServer usage across godot/src — only third-party terrain_3d addon touches MODE_FULLSCREEN). Nothing plans or builds resolution/fullscreen/vsync/quality settings; 3.11 guarantees the Graphics tab ships as an empty shell. Severity stays major (top of the band: no fullscreen path at all + a visibly empty tab, but the game is playable windowed so not a 'cannot play' blocker).

### 20. [MAJOR] No 'under attack' alert, minimap event pings, or minimap camera box

*Domain:* game-shell-ui · *Status:* unverified (verify agent lost to spend limit)

**Gap:** No epic, story, UX-DR, or GDD line covers combat alerts: no 'you are under attack' audio+text cue, no minimap flash at the attack site, no ally ping mechanism, no camera-view rectangle on the minimap. The minimap stories/verifies (built + 10.10/UX-DR71) cover only fog + dots + click-to-pan. In a fog-of-war RTS, a player whose base is hit off-screen gets zero notification — that reads as a broken game, not a rough edge; MP lobbies with allies have no ping to communicate with.

**WC3 bar:** WC3: 'Our forces are under attack!' voice + minimap flash + Space-to-jump; Alt-G ally minimap ping; minimap shows the camera frustum box.

**Evidence:** grep 'under attack|alert|ping|idle' in epics.md (only DesyncAlert + network ping) and GDD (zero matches); UX-DR71/10.10 minimap scope (epics.md lines 342, 2724); EXPERIENCE.md HUD section

**Suggested home:** New Epic 10 story adjacent to 10.10 (HUD alerts + minimap events), reusing CombatEventQueue

### 21. [MAJOR] No player-facing 'not enough resources / supply capped' denial feedback (text + sound)

*Domain:* game-shell-ui · *Status:* unverified (verify agent lost to spend limit)

**Gap:** Denied actions fail silently outside the command card. Story 2.4 disables ability buttons when unaffordable and the built train/build buttons show '[need: X]', but nothing owns an error toast + audio cue when a train/build/cast is refused (as-built, BuildingSystem/EntityPlacer log to console). The toast component exists (3.1c, UX-DR28) but no story wires gameplay denial events to it, and no denial sound is in AudioManager's 7-SFX list (10.4). Covered on paper via disabled states; thin in AC for the moment-to-moment feedback every RTS player expects.

**WC3 bar:** WC3: 'Not enough gold' / 'You require more lumber' text + voice line, and 'You have too many units' on supply block — instant, audible denial feedback.

**Evidence:** epics.md 2.4 AC (line 920, disabled-only), 10.4 SFX list (lines 2530-2540, no error sound); grep 'not enough|insufficient' — all hits are sim-side refusal logic; STATUS.md CommandCard/EntityPlacer notes (console logs)

**Suggested home:** Extend 10.10 (HUD harden) or a small Epic 2 follow-up wiring refusal events -> toast + AudioManager

### 22. [MAJOR] No multi-unit selection display and no buff/debuff icon visibility on the unit panel

*Domain:* game-shell-ui · *Status:* unverified (verify agent lost to spend limit)

**Gap:** Box-select and control groups exist in the sim, but every command-card story (2.4 ability buttons, 2.8 production picker, built worker card) is single-entity; no story builds a multi-select panel (group portraits, subgroup tabs, click-to-focus) or shows what a mixed selection contains beyond the as-built 'N selected' label. Worse: Epic 2 ships buffs/auras/DoT/HoT as the headline system, and no UI story ever displays active modifiers on a selected unit — players (and creators playtesting their own auras) cannot see what is affecting a unit. Stats shown are ring + HP only (UX-DR71).

**WC3 bar:** WC3: 12-unit group portrait grid with HP bars, Tab subgroup cycling, and a unit info pane showing stats plus buff/debuff icons with tooltips.

**Evidence:** epics.md 2.4/2.8 ACs (lines 912-998), UX-DR71 (line 342), 10.10 AC1 (line 2724); grep 'buff' — only sim-layer ModifierStore work (Epic 2); STATUS.md SelectionSystem ('N selected' label)

**Suggested home:** New Epic 2 tail story (modifier/buff readout + multi-select card) feeding 10.10's verify

### 23. [MAJOR] Lobby spec includes AI slots, but the AI is float-based and banned from lockstep — dead UI or guaranteed desync

*Domain:* game-shell-ui · *Status:* unverified (verify agent lost to spend limit)

**Gap:** UX-DR69 and EXPERIENCE.md line 39 specify lobby player slots as 'host / peer / AI', and UX-DR64a includes lobby AI states; Story 9.6 renders the lobby per UX-DR69. But AiOpponentSystem uses float/Math.* scoring, deferred-work.md states the HARD PREREQUISITE 'It MUST be converted to Fixed before ANY AI runs in lockstep MP', that conversion is scoped nowhere in 1.0, and Story 10.11 explicitly keeps 'existing Score* float weights unchanged'. So as planned, either the lobby ships AI slot states that can never be used (dead UI), or an AI-filled MP match desyncs across machines. No story resolves the contradiction.

**WC3 bar:** WC3 MP lobbies let the host fill any slot with a Computer player at a chosen difficulty and it just works.

**Evidence:** epics.md UX-DR69/64a (lines 333, 340) + 9.6 AC2 (line 2308); deferred-work.md 'MP disconnect resilience' section (float->Fixed hard prereq, NOT scoped) + 2026-06-09 item 3; 10.11 dev notes (epics.md line 2760)

**Suggested home:** 9.6 scope decision via correct-course: either descope AI slots from the 1.0 lobby UI or schedule the AI float->Fixed conversion as its Epic 9/10 prerequisite

### 24. [MAJOR] 'All content synced' is a gate with no sync: no map transfer to lobby joiners

*Domain:* game-shell-ui · *Status:* unverified (verify agent lost to spend limit)

**Gap:** UX-DR64c and the 9.6 lobby make 'content-synced' an independent gate on Start, but no story implements getting content to a joiner who lacks it — as-built a hash mismatch just blocks with 'MAP MISMATCH' (STATUS.md P3 content hashing). The only distribution path is mod.io publish, which 9.7/9.8 deliberately gate behind proof-of-play + thumbnail + 100-char description + screenshot — hostile to 'play my work-in-progress map with a friend on LAN tonight', which is the platform's core loop (EXPERIENCE.md Key Flow 5). Judged against WC3, the lobby dead-ends for any non-published scenario.

**WC3 bar:** WC3 automatically transfers the map to every lobby joiner with a progress bar — no publishing step, no manual file copy.

**Evidence:** epics.md UX-DR64c (line 333), 9.6 ACs (lines 2304-2314, gate only), 9.8 quality gate (lines 2340-2350); STATUS.md 'Scenario content hashing' (mismatch blocks match); grep 'transfer' — no story

**Suggested home:** New Epic 9 story (host->joiner package push over the existing reliable channel, reusing ContentPackager + 9.9 integrity verify)

### 25. [MAJOR] No teams/alliances anywhere: 4-8 player multiplayer is FFA-only with no team UI

*Domain:* game-shell-ui · *Status:* unverified (verify agent lost to spend limit)

**Gap:** Epic 9 scales matches to a verified N<=4 (8 fast-follow) and 5.6 gives skirmish 8 slots, but no story adds a team/alliance concept: no team assignment in the lobby (UX-DR69 lists faction select + color dots, no team column), no allied victory (7.10 explicitly scopes out 'multi-team resolution beyond the existing P1/P2 two-faction assumption'), no shared vision, no ally-only chat in-match (lobby chat All/Team exists in UX-DR69, but the in-match MatchChatOverlay has no All/Team routing story). Shipping 4-player MP where 2v2 is impossible is a headline WC3-parity miss.

**WC3 bar:** WC3 lobbies have a Team column per slot; teams share victory, allied vision toggles, and Allies chat; 2v2/3v3 are the default MP formats.

**Evidence:** epics.md 9.6 ACs (lines 2298-2314), UX-DR69 (line 340), 7.10 scope note (line 1992 'Multi-team... out of scope'); grep 'alliance|allied|team' — only team COLORS (UX-DR6/40); STATUS.md MatchChatOverlay (single channel)

**Suggested home:** Epic 9 scope decision: minimal team slots in 9.6 + team-aware WinConditionSystem extension in 7.10, or an explicit 1.0='FFA/1v1 only' descope note

### 26. [MAJOR] Replays are 'viewable' with no storied UI to browse, open, or control playback

*Domain:* game-shell-ui · *Status:* unverified (verify agent lost to spend limit)

**Gap:** FR-40 requires replays 'saved AND viewable/shareable' and 9.11 delivers the v2 format + deterministic playback, but its AC just says 'When I open a replay' — no story builds the surface: no replay list/browser (My Content in UX-DR68 lists drafts/published/subscriptions, not replays), no open-from-shell flow (as-built playback = setting an [Export] ReplayPath in the Godot Inspector, a dev-only path), and no playback controls (pause/speed/fog toggle/player perspective). Covered on paper, thin in AC — as written, 9.11 can pass without a player ever being able to watch a replay from the shipped UI.

**WC3 bar:** WC3: replay list in the shell, open/watch with speed controls and player-vision dropdown.

**Evidence:** epics.md FR-40 (line 123), 9.11 ACs (lines 2388-2404); UX-DR68 My Content contents (line 339); STATUS.md '[Export] string ReplayPath — set in Inspector for playback'

**Suggested home:** 9.11 AC extension (replay browser card in My Content + minimal playback controls) or a sibling 9.11b story

### 27. [MAJOR] Mid-match save/load of a single-player game is explicitly deferred out of 1.0

*Domain:* game-shell-ui · *Status:* unverified (verify agent lost to spend limit)

**Gap:** The GDD Phase-1 deliverable list includes 'Save/load game state' (never built — no STATUS.md row), and the PRD explicitly punts it: prd.md:169 marks 'mid-game single-player save/resume (full-world serializer)' as '[v2 — out of 1.0] unless you want quit-and-resume for single-player campaigns at 1.0', with addendum §G noting the engine has no world serializer. No epic/story picks it up. Against the WC3 bar — where saving any mission mid-match is table stakes, especially for longer skirmishes and any campaign content — 1.0 ships with no way to stop and resume a single-player game. This is a documented deferral the 1.0 bar arguably needs, not an oversight; it deserves an explicit go/no-go from Alec.

**WC3 bar:** WC3: F10 > Save Game / Load Game works mid-mission everywhere, including custom games.

**Evidence:** Project_Chimera_GDD.md line 538 ('Save/load game state'); prd.md:169 + addendum.md:76 (explicit deferral); grep 'save.{0,20}game|mid-match' in epics.md — no story; STATUS.md has no save-game row

**Suggested home:** Explicit 1.0 scope decision (addendum §G); if in: new Epic 10 story (single-player only, no MP interaction)

### 28. [MAJOR] Skirmish advertises 1-8 players but no story makes the AI run in more than the one P2 slot

*Domain:* game-shell-ui · *Status:* unverified (verify agent lost to spend limit)

**Gap:** Mode Select (3.11/UX-DR68) sells 'Skirmish vs AI (1-8 players, offline)' and 5.6's skirmish setup has 8 slots, but the AI opponent is architecturally one hardcoded P2 opponent (P1_BASE/P2 constants, single instance in the sim loop) and every AI story preserves that: 10.1 verifies the existing P1-vs-AI path, 10.2a runs 1v1 alpha-vs-beta, and 10.11's scope line reads 'only the existing two-player P1-vs-AI skirmish path... no multi-opponent or team adaptation'. 9.2 widens the faction model to 8 but touches no AI. With all stories done, a 4-slot skirmish has at most one functional AI — the other slots are dead UI.

**WC3 bar:** WC3 skirmish: fill up to 11 slots with Computer players, any mix of difficulties, FFA or teams.

**Evidence:** epics.md UX-DR68 (line 339)/3.11 (line 1294), 5.6 AC3 (line 1610), 10.11 scope (line 2760), 10.2a (lines 2474-2488); STATUS.md AiOpponentSystem (single P2 instance, hardcoded base coords)

**Suggested home:** Epic 10 story (N-instance AI, one per AI slot, data-driven base anchors) or an honest 3.11 descope to 'Skirmish vs AI (1v1)'

### 29. [BLOCKER] Terrain can never block movement (no cliffs/ramps/impassable terrain in the deterministic sim)

*Domain:* map-editor · *Status:* unverified (verify agent lost to spend limit)

**Gap:** No story anywhere lets a creator make terrain impassable. The sim's only pathability truth is FlowFieldSystem's bool[] obstacle map, populated exclusively from buildings (FlowFieldSystem.cs:112-126; the code comment at :99 anticipates 'terrain sculpting that affects passability' but nothing feeds it). Story 6.5 (DG-9) explicitly FENCES elevation OUT of pathfinding: 'elevation feeds the fog/vision radius ONLY and MUST NOT silently alter pathfinding/flow-field results... STOP and call it out' (epics.md:1764). Meanwhile Story 6.2 AC3 bakes the presentation NavMesh from the sculpted Terrain3D geometry (epics.md:1696), so Move orders (NavMesh path) will route around steep slopes while AttackMove/Patrol (flow fields) walk straight over mountains — two divergent pathing truths with no reconciling story (the PathRequestSystem→FlowFieldBridge migration is only a bullet at epics.md:161, storied nowhere). Net effect at 1.0: every authored map is an open field; sculpted hills are cosmetic + vision-bonus only. Chokepoints, ramps, walled expansions, maze/TD layouts — the structural core of RTS map design — cannot be authored.

**WC3 bar:** In the WC3 World Editor, cliffs, water and pathing blockers are the primary map-design tool: raising a cliff creates impassable edges with explicit ramps, and the entire melee map vocabulary (chokes, high-ground expansions, TD mazes) depends on it. A WC3-bar map editor where terrain never affects movement is not comparable.

**Evidence:** Epic 6 read in full (epics.md:1658-1765) — no passability story; grep 'obstacle|impassable|unwalkable|pathab|cliff|ramp' across epics.md returns zero editor/sim hits (only a 'chokepoint' flavor word at :792 and AR-21 'choke point' pun); godot/src/Navigation/FlowFieldSystem.cs:67-126 (buildings-only obstacle map); STATUS.md:178; Story 6.5 scope fence epics.md:1764; NavMesh bake AC epics.md:1696; requirements inventory FR-21/FR-22/FR-61 (epics.md:95-96, 438) are the map editor's entire FR footprint.

**Suggested home:** New Epic 6 story (6.6): deterministic terrain-passability layer — slope/height-threshold or painted-blocking fold into FlowFieldSystem's obstacle map + SimChecksum/golden re-baseline + validator coverage; pairs with 6.5 and should also reconcile the NavMesh-vs-flow-field split.

### 30. [BLOCKER] No doodads/decorations of any kind (designed in UX, promised in GDD, owned by no story)

*Domain:* map-editor · *Status:* unverified (verify agent lost to spend limit)

**Gap:** No FR, no epic, and no story gives creators decorative objects. The placement palette is exactly: P1/P2 unit, Ore Node, Building, Start Pos (Story 6.4 brownfield note epics.md:1740; STATUS.md:141). Yet the canonical UX mockup's terrain panel contains a 'Scatter / Doodads' section with a Tree/Rock/Crystal/Ruin grid (mockups/project-chimera/project/Creation Suite.html:245-246, editor-data.js:191), and the GDD Phase-2 deliverables read 'Terrain editor (sculpt, paint, place props)' (Project_Chimera_GDD.md:550). Neither doodad placement, nor rotation/scale/variation, nor a destructible/neutral-object class (trees as blockers/harvestables) exists in the plan. Every 1.0 map — shipped or community — is a texture-painted heightfield containing only gameplay entities.

**WC3 bar:** Doodads are roughly half the WC3 World Editor terrain experience: a large palette of trees/rocks/props with rotation, scale and variation, plus destructibles (trees that block pathing and can be harvested/destroyed). A map editor with zero decorative objects fails the 'comparable to the World Editor' bar on first open.

**Evidence:** grep 'doodad|decorat|prop|scenery' across epics.md → no functional hits; Epic 6 read in full (1658-1765); UX mockup Creation Suite.html:245 ('Scatter / Doodads') + editor-data.js:191 (Tree/Rock/Crystal/Ruin); GDD:550 'place props'; GDD:291 Entity Placer spec; STATUS.md:141-145 as-built palette; UX-DR70 left palette (epics.md:341) lists no doodad tool — the UX-DR extraction dropped what the mockup contains.

**Suggested home:** New Epic 6 story (6.7): doodad/scatter palette — ScenarioData doodads array + EntityPlacer mode + MultiMesh presentation, with rotation/scale and an optional pathing-blocking flag (ties into the passability story); AR-27 asset ingest already gives custom-model plumbing.

### 31. [BLOCKER] Trigger regions/areas: dangling primitive — 7.10 requires regions, no story ships them, no editor tool draws them

*Domain:* map-editor · *Status:* unverified (verify agent lost to spend limit)

**Gap:** The GDD trigger vocabulary includes 'Region Entered' events and 'Region Enter/Leave' T3 nodes (GDD:208, 309). Story 7.10's King-of-the-Hill preset requires 'a designated region' and its load-validation AC rejects 'King of the Hill referencing an undefined region' (epics.md:1980, 1984, 1986), and its note claims regions were 'shipped earlier in Epic 7 (7.2 variables+timers, regions)' — but Story 7.2's ACs contain only variables and timers (epics.md:1814-1830); no Epic 7 story defines a region data model, region-enter/leave events, or region evaluation, and no Epic 6 story gives the map editor a visual region tool (WC3's Region Palette: draw/name rects on the map). Code ground truth: zero gameplay 'region' exists (grep of godot/src — only Terrain3D/NavigationRegion3D infrastructure). As written, 7.10 cannot be implemented, and creators can never author location-based logic ('when units enter the canyon, spawn ambush') — the single most-used WC3 trigger pattern.

**WC3 bar:** WC3's Region Palette lets creators draw named rectangles directly on the map, then reference them in triggers (Unit Enters Region is among the most-used events in every custom map genre — TD, RPG, escort, KotH).

**Evidence:** epics.md:1980/1984/1986 (7.10 requires regions + claims 7.2 ships them); epics.md:1814-1830 (7.2 ACs: variables/timers only, 'Point' var type is the closest primitive); case-insensitive grep 'region' across all of epics.md → no authoring story; grep 'region|unit_enters|EnterArea' in godot/src → no gameplay region; GDD:208/309.

**Suggested home:** Epic 7 (new story before 7.10: region primitive — ScenarioData region defs + enter/leave events + deterministic containment eval) + an Epic 6 slice for the visual draw/name/edit region tool in the map editor.

### 32. [MAJOR] Editor can only author 2-player maps (no start positions/ownership for slots 3-8, no neutral owner)

*Domain:* map-editor · *Status:* unverified (verify agent lost to spend limit)

**Gap:** The placement palette and start-position tool are hardcoded to P1/P2: 6.4's AC says 'moved both start positions' and the as-built StartPositionBridge has a [P1][P2] toggle (STATUS.md:141-142). No story extends the editor to place entities or start positions for slots 3-8 or the Neutral faction, even though Story 9.2 widens the sim to 8 players (epics.md:2200-2216), Epic 9 verifies N≤4 MP (2178), Story 5.6 gives skirmish setup 'up to 8 player slots' (1610), and UX-DR68 advertises 'Skirmish vs AI (1-8)' (339). Result: 3-4 player multiplayer ships with no way to author a 3-4 player map in the editor; neutral-hostile guards (creeps) are equally unauthorable (Faction.Neutral exists only as an enum slot). Related deferred item: ScenarioLoadPhase crashes on slots 4-7 today (deferred-work.md §1.8c item 1, owned by 9.2 for the SIM only).

**WC3 bar:** WC3's World Editor places units/start locations for 12 players plus Neutral Hostile/Passive from one owner dropdown; multi-player melee maps and creep camps are baseline expectations.

**Evidence:** epics.md:1722-1740 (6.4 ACs + brownfield note: palette = P1/P2 unit); STATUS.md:141-142; epics.md:2200-2216 (9.2 = sim-model only, no editor work); epics.md:1610 (5.6), 339 (UX-DR68); deferred-work.md ScenarioLoadPhase slots 4-7 crash; ScenarioData.cs:24-26 (slot int supports it — the tooling doesn't).

**Suggested home:** Epic 6 extension of 6.4 (owner dropdown driven by FactionRegistry.PLAYER_COUNT + N start-position markers + Neutral owner), sequenced after 9.2; neutral-hostile AI behavior needs its own slice if creeps are wanted.

### 33. [MAJOR] No New-Map flow and no map-size/dimension control anywhere in the plan

*Domain:* map-editor · *Status:* unverified (verify agent lost to spend limit)

**Gap:** No story creates a map from scratch or sets its dimensions. ScenarioData.MapBounds exists (float half-extent, default 120 — ScenarioData.cs:143-144) but no editor UI story exposes it; the Terrain3D region is a fixed 256×256 heightmap regardless (TerrainPhase.cs:41, MinimapBridge HALF_MAP=128); shipped maps vary bounds only via hand-edited JSON (STATUS.md:213-215). Code grep confirms no 'New Map' path exists — maps are born by copying scenario JSON or via the AI generator (MapGeneratorPanel). Story 6.2 AC2 mentions 'a freshly created map' as a fallback case but nothing authors one. A creator opening the suite cannot answer the first question of map making: how big is my map?

**WC3 bar:** WC3's File→New Map dialog (size 32-256, tileset, initial cliff level) is literally step zero of the World Editor; Scenario→Map Size lets you resize/expand later.

**Evidence:** grep 'new map|NewMap|CreateNew|map size|resize' across epics.md and godot/src → zero authoring hits; ScenarioData.cs:140-144; epics.md:1694 (6.2's 'freshly created map' assumes a flow that no story builds); STATUS.md:131-145 (no size control in any panel); UX-DR70 toolbar (epics.md:341) has Save/Publish but no New.

**Suggested home:** Epic 6 (new story or 6.4 extension): New Map dialog (name, bounds/terrain size, default slots) + MapBounds field in a map-settings panel; must define the Terrain3D region-size relationship.

### 34. [MAJOR] No multi-select and no copy/paste of placed content

*Domain:* map-editor · *Status:* unverified (verify agent lost to spend limit)

**Gap:** Every editor operation is single-object: place one, hover-delete one (STATUS.md:145). No story adds marquee/multi-select in Edit mode, group move/delete, or copy/paste of placed units/buildings/nodes (SelectionSystem's box-select is the PLAY-mode unit-command path, UX-DR61 restricts it to player-faction units in match). Building a symmetric 4-base map means placing every entity individually with no way to duplicate a base layout. Grep 'copy|paste|multi-select|marquee' across epics.md returns nothing for the editor.

**WC3 bar:** WC3's World Editor supports drag-selection of multiple placed units/doodads, group move/rotate, and Ctrl+C/Ctrl+V of whole selections — the standard way map makers duplicate base layouts and decoration clusters.

**Evidence:** grep 'copy|paste|multi-select|multiselect|marquee' in epics.md → no editor hits; Story 6.4 ACs (epics.md:1728-1740) are single-place/single-delete; STATUS.md:141-145 as-built EntityPlacer confirms single-object ops.

**Suggested home:** Epic 6 (6.4 extension or new 6.6-class story): editor marquee select + group delete/move + copy/paste through EditorHistory.

### 35. [MAJOR] No trigger camera actions or named-camera authoring (GDD-promised, storied nowhere)

*Domain:* map-editor · *Status:* unverified (verify agent lost to spend limit)

**Gap:** The GDD trigger vocabulary explicitly includes 'Move Camera' (T2, GDD:208) and 'Camera' action nodes (T3, GDD:309), but no Epic 7 story ships any camera action, no presentation leaf exists for it (the 2.1 carve-off list routes PlayVfx/PlaySound/ShakeScreen to 'their owning stories' — none exists for camera), and no Epic 6/editor story places named camera positions. Story 7.8b's local-only presentation-action whitelist (ToggleWidgetVisible/OpenSubPanel/CloseSelf/SetLocalUiVar, epics.md:1948) is the natural rail and conspicuously omits camera control. Cinematic/scripted-camera scenarios (campaign intros, RPG cutscenes, tutorial guidance) — a big slice of WC3 custom maps — cannot be authored.

**WC3 bar:** WC3's Camera Palette places named cameras on the map and triggers pan/apply them ('Camera - Apply Camera Object', 'Pan Camera') — the backbone of every WC3 cinematic and campaign map.

**Evidence:** GDD:208 and :309 (camera in the promised trigger vocabulary); grep 'camera|cinematic' across epics.md → only HUD camera controls, shake accessibility, and keybindings (:335, :2632, :2646, :2730) — zero trigger/editor camera; deferred-work.md story-2.1 item 4 (presentation leaves deferred to 'owning stories' that don't exist for camera); epics.md:1938-1952 (7.8b whitelist).

**Suggested home:** Epic 7 (extend the 7.8b presentation-action whitelist with deterministic camera directives + a named-camera table in ScenarioData) + an Epic 6 slice for placing/naming cameras in the editor.

### 36. [MAJOR] No water — not as terrain visual, not as pathing, not storied, not in the GDD

*Domain:* map-editor · *Status:* unverified (verify agent lost to spend limit)

**Gap:** No story, FR, AR, or GDD gameplay section covers water at all (the only 'water' hits are the alchemical logo triangle and a decorative blob in an HTML mockup). At 1.0 no map can contain rivers, lakes or shores — visually or mechanically. Honest framing: unlike doodads/cameras, the GDD never promised water, so this is a pure WC3-parity delta rather than a broken commitment; Terrain3D also ships no water system, making this real engineering, not verification.

**WC3 bar:** WC3 terrain includes shallow/deep water as first-class tiles affecting pathing (deep water impassable to ground) and enabling naval/amphibious content; virtually every WC3 melee map uses water somewhere.

**Evidence:** grep 'water' across epics.md → only line 1696 false-positive ('walkable surface'); grep across GDD → logo/mockup only (GDD:98, HUD.html:38); Epic 6 read in full (1658-1765).

**Suggested home:** Epic 6 post-passability slice (water plane + painted water cells feeding the same terrain-passability layer as the cliffs fix) — or an explicit, documented 1.0 cut.

### 37. [BLOCKER] Regions do not exist: no region data model, no map-editor region tool, no Region Enter/Leave events

*Domain:* triggers-object-editing · *Status:* unverified (verify agent lost to spend limit)

**Gap:** No story anywhere ships regions. Epic 6's placement palette is units/buildings/resource-nodes/start-positions/win-condition only (6.4); Epic 7 never defines a region object or enter/leave event. Yet Story 7.10's own ACs presuppose regions ('King of the Hill (one faction holds a designated region...)', validator error 'King of the Hill referencing an undefined region') — an internally dangling dependency with no owner. The GDD explicitly promises 'Region Entered' events (§5 line 208), 'Region Enter/Leave' T3 nodes (line 309), region-targeted spawns ('region': 'north_gate', line 249), and load-time validation of 'region names' (line 257). Partial workaround exists (7.2 Point variables + 7.3 distance() built-in polled per tick), but there is no visual way to author even a Point on the map, and named regions the presets/validator reference are unowned. Location-based logic is the backbone of WC3-style scenario authoring; without regions the editor is not 'comparable to the WC3 World Editor'.

**WC3 bar:** WC3 World Editor has a first-class Region layer (draw rects on the map, name them) plus 'Unit enters/leaves region' events and region-targeted actions — the single most-used trigger primitive in custom maps.

**Evidence:** epics.md Epic 6 stories 6.1-6.5 (lines 1658-1765, placement palette at 1730-1740 — no region tool); Epic 7 full read (1766-1993) — 'region' appears only inside 7.10's KotH AC (lines 1980, 1984, 1986); grep 'region' across epics.md returns no authoring story. GDD lines 208, 249, 257, 309 promise regions. 7.2 (line 1817) Point var type + 7.3 (1840) distance() are the only partial substitutes.

**Suggested home:** New Epic 7 story (region data model + enter/leave events on the graph IR, before 7.10) + a sibling Epic 6 story (region draw/name tool in the map editor palette)

### 38. [BLOCKER] No trigger action/effect leaf to ORDER UNITS (move/attack-move/patrol a spawned group)

*Domain:* triggers-object-editing · *Status:* unverified (verify agent lost to spend limit)

**Gap:** The as-built trigger action set is spawn_unit, display_message, victory, defeat, create_timer, add_resources, set_variable, play_sound — no order-issuing action. Epic 7 rebuilds this vocabulary onto the D1 effect graph but the entire planned closed vocabulary (2.1 carve-offs: FireProjectile/SpawnUnit/Teleport/Victory/PlayVfx/PlaySound/ShakeScreen '→ their owning stories') contains no Move/Order leaf, and no Epic 7 story adds one. The GDD's T3 node list explicitly promises a 'Move' action (line 309). Without it, trigger-spawned units just stand at their spawn point (auto-aggro only): TD waves cannot march down a lane, campaign-style attack waves and scripted patrols are unbuildable — yet TD waves are FR-26's named use case and 7.5 names 'TD waves' as its target pattern.

**WC3 bar:** WC3's 'Unit - Issue Order' (attack-move to point, patrol, etc.) is the bread-and-butter action of every TD, defense, and campaign map; spawn-then-order is the canonical wave pattern.

**Evidence:** godot/src/Core/Definitions/TriggerDefinition.cs lines 127-138 (full action list); deferred-work.md 'Deferred from: story 2.1' item 4 (complete deferred-leaf list, no order leaf); Epic 7 full read lines 1766-1993 (no order/move action in any AC); GDD line 309 promises 'Move' action; FR-26 (epics.md line 102) and 7.5 (line 1874) name TD waves.

**Suggested home:** New Epic 7 story (or extend 7.5): 'IssueOrder' effect leaf (move/attack-move/stop to point or region) reusing the 1.12 OrderApplier vocabulary, executed sim-side deterministically

### 39. [BLOCKER] Hero XP-gain / level-up RUNTIME is unowned — hero progression never actually happens in a match

*Domain:* triggers-object-editing · *Status:* unverified (verify agent lost to spend limit)

**Gap:** FR-5 hero authoring (leveling curve, XP-gain rule, signature/ultimate) is Story 3.7, which states 'Authoring only — XP/leveling runtime is later-epic work' — but NO later story builds it. 3.2 (HeroStore) is data substrate only; 3.8/3.9 persist and load profiles as init-time state; 9.12 stores profiles online; 2.4a/2.4b attach/cast abilities with no XP. No story makes a hero gain XP from kills, level up per the authored curve, scale stats, or unlock the ultimate. Story 3.10's own AC presupposes it ('hero XP gained during the playtest is discarded'). Without the runtime, the entire FR-7a-e persistence rail (manifest, hero picker, server-validated online profiles) carries state that can never change — heroes are inert stat blocks.

**WC3 bar:** WC3 heroes gain XP from kills, level 1-10, spend skill points on abilities, and grow stats — the defining hero mechanic the FR-7 'WC3 save-code model' persistence explicitly emulates.

**Evidence:** epics.md 3.7 note line 1230 ('XP/leveling runtime is later-epic work'); grep 'XP|experience|level-up|levels up' across epics.md — zero runtime story (only FR-5 line 65, authoring ACs 1222-1226, picker display 1258, playtest-reset mention 1278); Epic 2 stories 2.1-2.11 (836-1073) and Epic 9 9.12 (2406) contain no XP system; story list lines 486-2760 has no candidate.

**Suggested home:** New Epic 3 sim story (e.g. 3.7b: XP-gain events + level-up application into HeroStore, checksum-folded), must precede 3.9/3.10 and Epic 9's 9.12

### 40. [MAJOR] Built-in trigger EVENT breadth stays at the as-built 6 — no unit-damaged, unit-trained, ability-cast, hero-level, or chat events

*Domain:* triggers-object-editing · *Status:* unverified (verify agent lost to spend limit)

**Gap:** The event vocabulary is match_start, unit_dies (faction-wide), building_completed, timer_expires, resource_threshold, unit_count_threshold. Epic 7 adds creator-defined custom events (7.4) and killer attribution on unit_dies, but no story broadens the BUILT-IN sim event set: nothing fires on unit-takes-damage/is-attacked, unit trained/spawned, ability cast, hero level-up (see XP finding), player chat, or region entry (see regions finding). Custom events can only be raised BY triggers, not by the sim — so a creator cannot react to any sim occurrence outside the 6. Common patterns (boss-phase on damage, on-train counters, chat-command cheats/debug, response-to-cast) are unbuildable.

**WC3 bar:** WC3 offers dozens of built-in events: unit Takes Damage/Is Attacked, Finishes Training a Unit, Spell Effect, Hero Learns Skill/Levels, Player Chat Message, unit enters region, item events — event breadth is what makes 'any game' buildable.

**Evidence:** TriggerDefinition.cs lines 46-56 (as-built events); Epic 7 full read 1766-1993 — 7.4 (1850-1868) adds only creator-registry custom events + unit_dies payload; 7.1b-2 migrates the existing set losslessly (1800-1812); 8.4 (2072-2090) only advertises 'constructs added by earlier epics' to the LLM, adding none.

**Suggested home:** Extend 7.4 (built-in sim event registry: damage-taken, unit-trained, ability-cast, chat) or a new Epic 7 story after 7.4

### 41. [MAJOR] Expression layer cannot READ game state — no accessors for an entity's HP/position/owner etc.

*Domain:* triggers-object-editing · *Status:* unverified (verify agent lost to spend limit)

**Gap:** 7.3's expression sublanguage operates 'over my variables' with bounded built-ins count/distance/min/max/abs only. There is no accessor to query a unit's current HP, max HP, position, owner, or a player's resource amount inside an expression (resource_comparison/unit_count exist only as fixed condition forms). Combined with the missing damage event, the canonical 'boss reaches 50% HP → phase 2' pattern — and any condition over live entity state beyond counts/distances — is inexpressible. EntityRef/FactionRef variable types exist (7.2) but nothing dereferences them.

**WC3 bar:** WC3 GUI conditions/values expose essentially every queryable value ('Life of (unit)', 'Owner of', 'Position of', player properties) for use in any comparison — condition composition over arbitrary game values is core World Editor capability.

**Evidence:** epics.md 7.3 AC (lines 1838-1846: '+ - * / mod, comparisons, && || !, and bounded built-ins count/distance/min/max/abs'); 7.2 value-type set line 1817 (no accessor mechanism); as-built conditions TriggerDefinition.cs 87-96 (only the 5 fixed forms).

**Suggested home:** Extend 7.3 (typed read-only accessor built-ins over EntityRef/FactionRef: hp(e), maxhp(e), pos(e), owner(e), resource(f, id))

### 42. [MAJOR] No upgrades/research system or editor (researchable stat improvements)

*Domain:* triggers-object-editing · *Status:* unverified (verify agent lost to spend limit)

**Gap:** Zero coverage: no FR, no story, no mention of research or upgrades anywhere in the epics ('upgrade' hits only the Iron-Pact-reskin wording). Epic 4's tech tree gates EXISTENCE (prerequisites) only; there is no 'research X at building Y to grant +1 damage to all Footmen' mechanic, no research queue, no upgrade authoring UI — despite ModifierStore (2.2b) being the exact substrate that would drive it. Both showcase RTS factions and any community RTS lack a standard progression axis.

**WC3 bar:** WC3's Object Editor has a dedicated Upgrades tab; every melee map has attack/armor/tech upgrades researched at buildings that modify unit stats — a feature virtually every RTS player expects.

**Evidence:** grep 'upgrade|research' across epics.md — only FR-20 reskin lines (92, 395, 1516) and unrelated matches; Epic 4 full read (1326-1463: prerequisites/costs/supply only); PRD FR-13..16 (lines 83-86) omit research; deferred-work.md has no research punt.

**Suggested home:** New Epic 4 story (research/upgrade definitions + building research queue applying permanent faction-scoped Modifiers) + editor surface in 4.5/4.6

### 43. [MAJOR] No item/inventory system or item editor, though the persistence manifest promises 'inventory/items'

*Domain:* triggers-object-editing · *Status:* unverified (verify agent lost to spend limit)

**Gap:** FR-7a and the GDD (line 176) name 'inventory/items' among the hero attributes the persistence manifest can carry forward — but no story builds items at all: no item definition/authoring editor, no hero inventory (slots, pick-up/drop), no item drops or shops, no item trigger events. The manifest story (3.8) would offer a checkbox for state that cannot exist. Hero-centric custom games (the RPG/DotA-like genre space the platform courts via heroes + persistence) have no item dimension.

**WC3 bar:** WC3 has a full Items object-editor class, 6-slot hero inventories, item drops, shops, and item trigger events — items are half of what makes WC3 hero maps work.

**Evidence:** grep 'item|inventory' across epics.md — only the ItemList UI widget (lines 219, 1909) and FR-7a's own wording (line 68); GDD line 176; Epic 3 full read (1074-1325) has no item story; deferred-work.md contains no item punt.

**Suggested home:** Explicit product decision: either a new Epic 3 story pair (item definitions + hero inventory SoA + editor) or descope 'inventory/items' from FR-7a/GDD and the 3.8 manifest to avoid a dead checkbox

### 44. [MAJOR] Abilities have no levels/ranks and heroes have no skill-learning — hero levels change nothing about abilities

*Domain:* triggers-object-editing · *Status:* unverified (verify agent lost to spend limit)

**Gap:** AbilityDefinition (2.3/2.5a/2.5b as-shipped) has a single flat set of values — no per-level fields (damage/cooldown/cost per rank), no max-level, and no 'hero learns/upgrades ability at level N' mechanism; 3.7 authors signature/ultimate SLOTS only. Even once an XP runtime exists (see blocker), leveling could scale stats via the curve but abilities stay rank-less, flattening hero design space. The ability editor would also need per-level field UI.

**WC3 bar:** WC3 abilities are leveled (typically 3 ranks, ultimates at 6) with per-level data fields in the Object Editor, and heroes spend skill points on level-up — the core hero-build mechanic.

**Evidence:** grep 'level|rank' in godot/src/Core/Definitions/AbilityDefinition.cs — zero matches; Epic 2 stories 2.3/2.5 (epics.md 894-965) author flat abilities; 3.7 (1214-1230) authors slots + curve only; no story adds ranks.

**Suggested home:** Extend 2.3/2.5 (per-level value arrays on AbilityDefinition + validator) paired with the new hero-XP runtime story

### 45. [MAJOR] Victory/defeat resolution for >2 factions (FFA/teams) is explicitly out of scope yet 4-8 player matches ship

*Domain:* triggers-object-editing · *Status:* unverified (verify agent lost to spend limit)

**Gap:** Story 7.10 builds the sim WinConditionSystem but pins 'Multi-team (>2 faction) free-for-all resolution beyond the existing P1/P2 two-faction assumption is out of scope', and no other story owns it: 9.2 expands the faction model to 8 and converts float math, 9.6 ships a 4-player lobby, 10.1 verifies solo skirmish — none defines last-faction-standing, per-player elimination (defeated player continues as observer while the match runs), or team victory. 1.0 therefore ships 4-player MP and '1-8 skirmish' (UX-DR68) whose win logic is only defined for two factions.

**WC3 bar:** WC3 resolves per-player victory/defeat in FFA and team games (defeated players are eliminated, allies share victory), and triggers can defeat/victory individual players.

**Evidence:** epics.md 7.10 scope note line 1992; 9.2 (2200-2216, no win semantics); 9.6 (2298-2314); 10.1 (2454+); UX-DR68 'Skirmish (1-8)' line 1294; WinConditionSystem verdict is per-slot (1978) but the evaluation semantics beyond P1/P2 are unowned.

**Suggested home:** Extend 7.10 (lift the scope limit) or a new Epic 9 story alongside 9.2 (N-faction elimination/team victory in WinConditionSystem)

### 46. [MAJOR] Active buffs/debuffs/status effects are invisible to players — no buff icons or status display

*Domain:* triggers-object-editing · *Status:* unverified (verify agent lost to spend limit)

**Gap:** Epic 2 ships stackable modifiers, DoT/HoT, auras, and StatusFlags (Stunned/Rooted/Silenced/Disarmed), and the showcase Sanguine Furnace HoT runs on every Court unit — but no story renders any of it: no buff-icon row on the selected-unit panel, no status indicator over units, nothing in the UX spec (grep 'buff' in DESIGN.md/EXPERIENCE.md: no status-icon surface). 2.7's CombatFeedbackProfile covers momentary hit/death feedback only. Players cannot see why a unit is regenerating, slowed, or stunned — reads as unpolished/confusing in exactly the ability-driven combat 1.0 showcases.

**WC3 bar:** WC3 shows active buffs/debuffs as icons in the unit status panel (with tooltips) plus persistent overlay effects on the model — every RTS player expects a stun/slow/HoT to be visible.

**Evidence:** Epic 2 (836-1073) — 2.2b/2.6 build modifiers/passives, 2.7 (966-982) is event feedback only; grep 'buff|status icon' in ux-Project_Chimera-2026-06-20/ returns only unrelated mockup strings; Epic 10 10.10 HUD verify (2716) doesn't mention status display.

**Suggested home:** New presentation story in Epic 2 (post-2.7, ModifierStore readback → selected-unit buff row) or fold into 10.10's HUD harden

### 47. [MAJOR] No trigger-driven camera control or cinematic toolkit (GDD promises 'Move Camera')

*Domain:* triggers-object-editing · *Status:* unverified (verify agent lost to spend limit)

**Gap:** The GDD's own trigger vocabulary promises a 'Move Camera' action (line 208) and a 'Camera' T3 node (line 309), but no epic/story ships any camera trigger action, nor cinematic mode, fade in/out, letterbox, or music control (play_sound exists as-built; no stop/music/fade). 7.8b's local presentation-action whitelist is buttons-only (ToggleWidgetVisible/OpenSubPanel/CloseSelf/SetLocalUiVar). Creators cannot script intros, cutscenes, camera reveals, or fixed-camera modes — a large slice of WC3-style scenario/RPG authoring.

**WC3 bar:** WC3 triggers pan/apply/lock cameras, run cinematic mode with transmissions, fade filters, and control music — used by virtually every campaign-style custom map.

**Evidence:** GDD lines 208, 309; grep 'camera|cinematic|fade' across epics.md — only camera-shake feedback (UX-DR51), keybindings, and reduced-motion hits; 7.8b whitelist line 1948; TriggerDefinition.cs action list 127-138.

**Suggested home:** New Epic 7 story (presentation-rail camera/audio trigger actions: PanCameraTo, lock/unlock, fade, play/stop music — deterministic-safe since presentation-only, modeled on the 7.8b whitelist rail)

### 48. [BLOCKER] FR-39 two-machine LAN determinism gate still parked

*Domain:* multiplayer-social · *Status:* unverified (verify agent lost to spend limit)

**Gap:** The PRD's #1 hard gate ('zero desyncs', FR-39: full MP match on separate machines, 300+ ticks, zero desync) has never been physically run. Story 1.9b AC4 is explicitly PARKED because only one machine exists; the runbook (godot/tools/lan-determinism-runbook.md) is ready but no story, milestone, or Epic-9/10 AC re-owns actually executing it before ship. Every Epic 9 story golden-gates at N=2 on one machine — loopback and WSL prove a lot, but real-NIC/real-latency/two-OS-instance behavior (packet loss, socket timing, per-machine locale/env) is exactly what FR-39 exists to prove. If it fails at ship time, all of Epic 9 is built on sand.

**WC3 bar:** WC3 shipped with Battle.net/LAN multiplayer proven on real separate machines; a lockstep RTS that desyncs on real networks is DOA — this is the one gate WC3 could never have skipped.

**Evidence:** epics.md lines 706-720 (Story 1.9b, UX-DR84 gate 'Requires two physical machines'); FR-39 at epics.md line 122 ('never run; #1 risk'); memory/MEMORY.md 1.9b entry ('AC4 PARKED... #1 tracked pre-ship gate'); Epic-1 retro decision 2 in MEMORY.md; grep of Epic 9 (2176-2447) shows all gates are 'golden-gated at N=2' single-machine; sprint-status.yaml epic-9: backlog.

**Suggested home:** A tracked pre-release gate in Epic 10 (alongside 10.9a/10.9b ship stories) or an explicit 1.9b-AC4 completion task in sprint-status.yaml — needs a scheduled second machine, not new code.

### 49. [BLOCKER] Internet matchmaking funnels to a single static single-match server; concurrency and scenario selection undefined

*Domain:* multiplayer-social · *Status:* unverified (verify agent lost to spend limit)

**Gap:** Story 9.6 explicitly ships 'a single configured static endpoint' (dynamic per-match server routing deferred post-1.0), and the as-built DedicatedServer is a one-match state machine (Waiting→...→InGame, MAX_SLOTS=4, 2 players+2 spectators). Nothing defines what happens when a second pair is matchmade while a match is in progress on that endpoint (wedged lobby? joined as spectators? silent failure?) — so 1.0 internet matchmaking supports exactly one concurrent match globally, with undefined failure for everyone else. Additionally, no AC says which SCENARIO a matchmade game plays or who selects it: the GDD promises 'matchmaker groups players with matching scenario ID' but the as-built matchmaker matches on a flat game=chimera_1v1 property, and 9.6's ACs only parameterize counts/parties/endpoint config. Two matchmade strangers with different local scenarios hit the version-mismatch gate with no convergence path. There is also no NAT-traversal/relay story for the Direct (LAN/IP) path across the internet (host must port-forward; the GDD's WebSocket fallback for firewalled environments is unowned) — acceptable only if the 1.0 positioning explicitly says so, which no doc does.

**WC3 bar:** Battle.net hosted arbitrary concurrent custom games, the host chose the map, and joiners browsed a game list. Even a Battle.net-lite must let two matches run at once and define which map a matchmade game plays.

**Evidence:** epics.md 2298-2314 (Story 9.6 AC1: 'GameServerIp/Port... single configured static endpoint — dynamic per-match server routing is explicitly deferred to post-1.0'); GDD line 363 (matchmaker groups by scenario ID — not in any AC); STATUS.md P2.4 (DedicatedServer one-match state machine; NakamaService game=chimera_1v1); GDD line 355 (WebSocket fallback promise, no owning story); FR-40 at epics.md line 123.

**Suggested home:** Story 9.6 (add ACs: defined reject/queue behavior for a busy endpoint + scenario-ID match labels or host-picks flow), or an explicit 1.0-positioning note in the PRD downgrading matchmaking to 'one hosted playlist match at a time' if that is truly accepted.

### 50. [MAJOR] No in-lobby content acquisition — the GDD's 'Update Required' one-click download (WC3 auto map transfer) has no story

*Domain:* multiplayer-social · *Status:* unverified (verify agent lost to spend limit)

**Gap:** The GDD promises, in bold terms, that lobby content sync is 'automated end-to-end: players never manually manage mod versions' — mismatched joiners see 'Update Required' with a one-click mod.io download, and the game can't launch until hashes match. What the epics actually deliver is only the REJECT half: 9.4 hard-rejects mismatched hashes, UX-DR64b/c gate Start, 9.6 shows a version-match check. No story wires mismatch → fetch-correct-version-from-mod.io → re-verify → ready. A joiner who lacks the host's custom map must leave the lobby, manually find the map in the content browser, subscribe, download, and re-join. For maps never published to mod.io (local LAN creations), there is NO acquisition path at all — no peer-to-peer transfer story exists.

**WC3 bar:** WC3 auto-transferred the map to joiners in the lobby with a progress bar — the single feature that made its custom-game ecosystem frictionless. A lobby that just says 'mismatch' fails that bar hard.

**Evidence:** GDD lines 367-371 ('Content verification in multiplayer' — Update Required + one-click download, 'automated end-to-end'); epics.md 2262-2278 (9.4: hard reject only), 2298-2314 (9.6: 'version-match hash check' display only), 2370-2386 (9.10: browser-side download only, not lobby-wired); UX-DR64b/c and UX-DR69 (epics.md 333, 340) specify gates, not acquisition; grep of Epic 9 for transfer/download-in-lobby: none.

**Suggested home:** Story 9.6 (lobby mismatch → ModIoService fetch flow) with a note in 9.10; a P2P lobby transfer for unpublished LAN maps could be an explicit post-1.0 deferral if stated.

### 51. [MAJOR] Pre-match hash handshake does not cover faction and ability JSON — a known, logged desync vector with no owning AC

*Domain:* multiplayer-social · *Status:* unverified (verify agent lost to spend limit)

**Gap:** The 2.4b code review established that the Ready handshake hashes only the scenario file, while faction JSONs and the new resources/data/abilities/ directory drive folded sim state (registry indices, AbilityCount, armor, passives). Divergent or missing ability/faction files between peers produce a mid-match terminal HALT(NoMajority) with no diagnostic. The review's stated fix is 'the Epic 9 server-authoritative content-hash handshake (extend the pre-match agreement to faction + ability content)' — but no Epic 9 AC does this: 9.4's Ready packet carries {scenarioHash, rulesetHash, startStateHash}, where rulesetHash is defined in the architecture as the pinned tick-read CONSTANTS corpus (caps), and startStateHash is {roster+faction-count+initial-delay+rulesetHash+scenarioHash} — none of the three folds faction/ability content bytes or their canonical models. Covered on paper (a handshake exists), thin in AC (the widened surface it must cover is unnamed).

**WC3 bar:** WC3's map file contained ALL custom data (units/abilities/triggers in the .w3x), so the single map hash covered everything. Chimera splits content across scenario + faction + ability files, so a scenario-only hash is strictly weaker than WC3's guarantee.

**Evidence:** deferred-work.md §'code review of story-2.4b' item 1 (explicitly points at Epic 9); epics.md 2262-2278 (9.4 ACs — three hashes, no content-directory coverage); game-architecture.md lines 653/881/929 (rulesetHash = caps corpus); AR-18/AR-23 at epics.md 201-208; sample content already live (fireball attached to mage per 2.4b).

**Suggested home:** Story 9.4 (extend the multi-hash handshake AC to fold faction + ability canonical hashes, or fold them into startStateHash) — cheap to add now, expensive as a wild desync later.

### 52. [MAJOR] No team/alliance model — 2v2 is impossible, making 4-player MP FFA-only

*Domain:* multiplayer-social · *Status:* unverified (verify agent lost to spend limit)

**Gap:** Epic 9 scales the sim, wire, lobby, and matchmaking to N≤4 players, but no story anywhere defines teams: no alliance state in the sim (allied units target each other via the Enemy filter), no allied-victory condition (win conditions are per-faction annihilation/landmark/etc.), no team assignment in the lobby slots (9.6 lists host/peer/AI/faction/ready/ping — no team column), no shared vision, no allied in-match chat channel, no ally minimap pings. The GDD's Phase 3 deliverables explicitly promised '1v1 and 2v2 support', and UX-DR69 even specifies lobby chat channels as 'All/Team' — a Team channel with no team model behind it. As planned, a 4-player match is pure free-for-all and 2v2 cannot be played at all.

**WC3 bar:** WC3 shipped locked teams, allied victory, shared vision, allied chat, and minimap pings — 2v2/3v3 were the dominant Battle.net modes. Team play is a feature virtually every RTS player expects from 'multiplayer at scale'.

**Evidence:** grep of epics.md for team/alliance/allied/2v2: only colorblind team COLORS (UX-DR6/40), lobby 'chat (All/Team)' (UX-DR69, line 340), and accessibility — zero sim/victory/lobby-slot coverage; GDD line 562 (Phase 3: '1v1 and 2v2 support'); Epic 9 stories 9.2/9.3/9.6 (2200-2314) address player COUNT only; Story 7.10 win-condition presets (line 1970) are per-faction.

**Suggested home:** New Epic 9 story (team assignment in lobby + sim alliance mask + allied-victory in WinConditionSystem + Team chat channel), or an explicit PRD note that 1.0 MP is FFA-only — currently the docs contradict (GDD promises 2v2, UX implies Team chat).

### 53. [MAJOR] No multiplayer pause/unpause protocol (and no in-match pause at all)

*Domain:* multiplayer-social · *Status:* unverified (verify agent lost to spend limit)

**Gap:** No story in any epic implements pausing a match. Single-player 'pause' today is only the F5 Edit-mode toggle (invisible to Commanders per NFR-3/UX-DR63); the HUD mockup contains a pause-menu scrim but no story builds it (10.10 verifies HUD/controls, not a pause menu). In MP, pause requires a lockstep protocol (a Pause command on the command bus or server-dictated tick suspension, ACK-gated like 9.4's delay changes) plus etiquette rules (who may pause, count limits, who may unpause) — nothing covers it. A player whose phone rings mid-1v1 has no option but to play on or drop, and dropping triggers the permanent 9.5 freeze.

**WC3 bar:** WC3 gives each player 3 pauses in MP (F10 or Pause key), any player can unpause, and the pause state is announced to all — a baseline courtesy feature in every lockstep RTS since StarCraft.

**Evidence:** grep of epics.md for 'pause': only hit-freeze must-NOT-pause-the-tick notes (AR-29, lines 216/982) and game-architecture.md 1146 ('never indefinite pause' re: stalls); grep of UX DESIGN/EXPERIENCE: pause only as mockup CSS ('paused modal scrims', HUD.html pause-menu); MainScene.cs pause mentions = Edit-mode doc comment only; Epic 9 (2176-2447) has no pause story.

**Suggested home:** Epic 9 (a small story after 9.4 — a server-dictated ACK-gated pause/resume mirrors the delay-change machinery exactly) + the in-match menu for SP.

### 54. [MAJOR] No leave/surrender flow and no victory when the last opponent leaves; drops are not announced

*Domain:* multiplayer-social · *Status:* unverified (verify agent lost to spend limit)

**Gap:** There is no way to intentionally leave or concede a match in any story — no surrender command, no 'Leave Game' flow, no distinction between a rage-quit and a network drop. Combined with 9.5's freeze-and-continue (dropped slot stays in the sim indefinitely), a 1v1 opponent who quits leaves the winner grinding down a frozen, non-responding base to trigger the annihilation win condition — there is no 'last human opponent left → victory' rule in 9.5, 7.10 (win-condition presets), or anywhere else. Nothing surfaces 'Player X has left/disconnected' to remaining players either (9.5's ACs are all sim-level; UX-DR64 covers stall and desync banners only; 9.13 surfaces disconnect reasons only for throttle-kicked slots).

**WC3 bar:** WC3 announces '<Player> has left the game', immediately awards victory when the last opposing player leaves a melee game, and has an explicit quit/concede flow through the F10 menu. Every RTS player expects a quit button and a win when the opponent quits.

**Evidence:** epics.md 2280-2296 (9.5 — sim-only ACs, 'idle = empty commands', no victory/announce AC); 7.10 (line 1970, win-condition presets — no player-left condition); grep for surrender/concede/rematch/leave across epics.md and UX docs: zero; UX-DR64 (line 333) lists stall + desync UX only; deferred-work.md §MP-disconnect memo covers AI-takeover/reconnect but not leave-victory.

**Suggested home:** Story 9.5 (add: drop/leave announced via system chat + a defeat-on-leave / victory-on-last-opponent-left rule dictated by the server like the freeze) + a Leave/Surrender entry in the in-match menu.

### 55. [MAJOR] Player-facing replay experience is thin: no browser, no playback controls, no fog/perspective toggle

*Domain:* multiplayer-social · *Status:* unverified (verify agent lost to spend limit)

**Gap:** Story 9.11 makes replays VIEWABLE ('plays back through the deterministic sim') and shareable, with strong format integrity — but nothing specifies how a player finds, opens, or watches one. There is no replay browser/list UI story (as-built playback is an Inspector-set [Export] ReplayPath — a dev path that would literally satisfy the AC), no playback controls (pause/speed/seek — WC3 offers x0.25–x8), no fog/vision toggle or per-player perspective, no camera-free observation guarantee, and no UX-DR requirement for any replay screen (the Title nav is Play·Create·Browse·Settings·Quit — replays absent). FR-40 requires 'saved AND viewable/shareable'; as written, 'viewable' can be met without any player-reachable UI.

**WC3 bar:** WC3's replay feature = a replay list in the join screen, speed controls, player-perspective vision toggles, and free camera. Watching replays is a core RTS retention loop; a sim-playback-only 'replay' reads as unfinished.

**Evidence:** epics.md 2388-2404 (9.11 ACs — format/integrity + 'VIEWABLE... can be shared', zero UI/controls ACs); FR-40 (line 123); UX-DR67 title nav (line 338) and full UX-DR list (260-360) contain no replay surface; STATUS.md §6 Replay System ([Export] ReplayPath + '▶ REPLAY' label is the entire as-built UX).

**Suggested home:** Story 9.11 (add ACs: in-game replay list from user://replays + pause/speed controls + reveal-all vs per-player vision toggle), with the screen itself possibly a small sibling story.

### 56. [BLOCKER] No unit animation system of any kind (idle/walk/attack/death)

*Domain:* polish-performance-qa · *Status:* unverified (verify agent lost to spend limit)

**Gap:** Zero stories build an animation playback system or per-unit animation hookup. Units are static GLBs rendered via MultiMesh (which precludes per-instance skeletal animation without dedicated work); movement is gliding rigid meshes, attack has no body motion, and death is scale-to-zero plus a white flash. Story 10.6 only imports 8 more static GLBs; 2.7's CombatFeedbackProfile covers flash/sound/shake/hit-freeze, not body animation. Asset production is out of scope, but the SYSTEM + state hookup (even procedural bob/lunge/topple) does not exist and is never planned — reviewers reliably flag a zero-animation RTS as unfinished.

**WC3 bar:** Every WC3 unit has an animation set (stand/walk/attack/death/decay) driven by an engine state machine; sliding statues read as pre-alpha to any RTS player.

**Evidence:** grep of godot/src for AnimationPlayer|AnimationTree|Skeleton3D = 0 hits; full story list epics.md:494-2760 contains no animation story; Story 10.6 (epics.md:2562-2578) is static-GLB import; GDD §3 line 158 defines CombatFeedbackProfile only (particles/sound/shake/freeze); memory note: GLBs are static ~18-30k-vert meshes tinted via material_override.

**Suggested home:** New Epic 10 story before 10.5/10.6 (presentation-only animation state driver: walk bob/attack lunge/death topple or skeletal path decision), or a 2.7-sibling presentation story

### 57. [BLOCKER] Campaign & Tutorial mode is a shipped dead-end; GDD-canonical 5-8 mission tutorial campaign has zero stories

*Domain:* polish-performance-qa · *Status:* unverified (verify agent lost to spend limit)

**Gap:** Story 3.11's AC ships a Mode Select entry 'Campaign & Tutorial (progress N/12)', and the GDD's 2026-06-21 readiness reconciliation confirms '5-8 is the canonical campaign scope' — but no epic or story authors a single campaign/tutorial mission, mission-flow shell, or progress tracking. All 121 stories done as written = a main-menu button that leads nowhere, and no player-facing tutorial at all (5.8 onboards creators only).

**WC3 bar:** WC3 shipped tutorial missions (Prologue) + full campaigns; even judged against the user's leaner bar, a menu mode with no content behind it is 'no way to do X at all'.

**Evidence:** epics.md:1294 (3.11 AC: 'Campaign & Tutorial (N/12)'), UX-DR68 (epics.md:339); GDD line 538 ('A guided tutorial campaign of 5–8 missions' + the 1.0 reconciliation note binding N/12 to 'the real shipped mission count'); epic list epics.md:440-484 and full story list have zero campaign/tutorial-mission stories; grep 'campaign|tutorial' in epics.md returns only the Mode Select UI references.

**Suggested home:** Either a new Epic (campaign/tutorial missions using the Epic 7 trigger DSL as content) or an explicit descope: cut the Mode Select entry in 3.11 and reconcile the GDD

### 58. [BLOCKER] Local-player faction is hardcoded P1 across fog, selection, casting and training — the non-host MP player is blind and cannot command

*Domain:* polish-performance-qa · *Status:* unverified (verify agent lost to spend limit)

**Gap:** FogOfWarSystem stamps vision 'per alive P1 unit' (Story 6.5's VERIFY AC pins this as correct), and SelectionSystem/CommandCardSystem hardcode Faction.Player1 for select/move/cast/train (deferred-work §2.8: 'A client assigned Player2 would be blocked... Fix is a systemic local-faction plumb... not a 2.8 bug' — no story is named). A joining MP client therefore renders the OPPONENT'S vision and cannot select or order its own units. Epic 9 scales MP to 4 players (9.2 audits (int)Faction sim sites, 9.6 does server-side slot assignment) but no story owns re-keying the presentation layer (fog stamping, selection filter, command card, HUD resources) to the locally-assigned slot. 10.10's UX-DR61 AC ('only the player's own faction units are selected') is the lone thin catch-net. Checksum tests cannot catch this — the sim stays identical while the P2 screen is unplayable.

**WC3 bar:** Trivially, every WC3 client sees its own player's vision and commands its own units; a 4-player mode where only slot 1 can play is not a shipped MP game.

**Evidence:** epics.md:1750,1764 (6.5 verify: 'per alive P1 unit'); deferred-work.md §code review of story-2.8 item 1 (SelectionSystem.cs:387,685,738 hardcodes; 'systemic local faction plumb' unowned); epics.md:2200-2216 (9.2 = sim arrays/loops), 2298-2314 (9.6 = lobby/slot assignment, no presentation re-key), 2728 (10.10 UX-DR61).

**Suggested home:** Epic 9 — a dedicated 'local-faction plumb' story between 9.2 and 9.6 (fog per-local-faction stamping + selection/command/HUD re-key), with 10.10 as the verify gate

### 59. [MAJOR] Attack-move and idle units never auto-acquire buildings; the AI never issues AttackBuilding — armies stall at razed-out bases and the AI cannot win DestroyAllBuildings

*Domain:* polish-performance-qa · *Status:* unverified (verify agent lost to spend limit)

**Gap:** 2.9a deliberately made anti-building combat explicit-order-only ('nothing auto-acquires buildings', CombatSystem.cs:284) and the AI has no AttackBuilding path — its waves are AttackMove-only. Combined with the known, unowned AttackMove arrive-threshold defect (waves hover in an equilibrium ring forever and never return to the AI pool, observed in-engine 2026-06-09), an AI assault that kills all defenders then stands inert next to enemy buildings; the AI can never satisfy DestroyAllBuildings, and player a-move armies ignore structures. Story 10.1 requires 'the AI builds and attacks and the match can reach a win or loss' with only 'fix ship-blocking breakage' as scope — the plan discovers this in the last epic with no sized story owning it.

**WC3 bar:** In WC3 (and every RTS), attack-moving units automatically engage buildings when no units remain, and the AI razes bases; a-move that ignores structures fails core RTS muscle memory.

**Evidence:** godot/src/Combat/CombatSystem.cs:284 ('Explicit-order-only: nothing auto-acquires buildings'); grep AiOpponentSystem.cs for AttackBuilding = 0 hits; deferred-work.md §2026-06-09 item 7 (AMOVE_ARRIVE_SQR hover, unowned); epics.md:2454-2472 (10.1 ACs); grep epics.md 'auto-acquir|raze' = 0 owning stories.

**Suggested home:** Epic 2 follow-up story (extend AttackMove/idle acquisition to structures + AI AttackBuilding issuance) or an explicit new story before 10.1

### 60. [MAJOR] BuildingStore is 64 slots, append-only, never recycled — long matches and 4-player MP exhaust building placement mid-game

*Domain:* polish-performance-qa · *Status:* unverified (verify agent lost to spend limit)

**Gap:** BuildingStore.MAX_BUILDINGS=64 with Create() returning -1 at capacity and Destroy() documented 'slot is not reused in Phase 1'. The 64 slots are the cumulative total ever constructed by ALL factions in a match — rebuilds, expansions, and especially Epic 9's 4-player matches (4 players x ~20 buildings) blow past it, after which every placement silently fails for the rest of the match. Story 9.2 resizes per-FACTION arrays (Ore etc. 5->9) but no story anywhere touches MAX_BUILDINGS or slot recycling; 10.3's stress scenario spawns units, not buildings, so nothing in the plan would even hit it.

**WC3 bar:** WC3 matches routinely see hundreds of cumulative structures across a long game; a hidden lifetime cap of 64 total buildings per match is a 'weird bug' at exactly the late-game moment players care about.

**Evidence:** godot/src/Core/BuildingStore.cs:25 (MAX_BUILDINGS=64), :90 (Create returns -1), :149 ('slot is not reused in Phase 1'); deferred-work.md §1.8b item 2 (silent overflow drop, pre-existing); grep epics.md 'MAX_BUILDINGS|building cap|64 buildings' = 0 hits; epics.md:2200-2216 (9.2 scope = per-faction arrays only).

**Suggested home:** Story 9.2 (widen its audit to shared-store capacity + free-list recycling for BuildingStore) or a new Epic 9 story; add a building-churn case to 10.3

### 61. [MAJOR] Order-acknowledgment feedback (selection/ack sounds + order-confirmed marker) promised by the GDD has no story; the audio system has no selection/ack hook

*Domain:* polish-performance-qa · *Status:* unverified (verify agent lost to spend limit)

**Gap:** GDD §6 explicitly designs latency masking: on click 'the client instantly plays a selection sound, shows rally point markers, and displays an order-acknowledged animation' — essential because lockstep input delay is 2-12 ticks (67-400ms adaptive). No story implements a ground-click move marker, order-ack flash, or selection/acknowledgment audio; AudioManager only drains CombatEventQueue (7 combat/UI clips) and 2.7's profiles cover impact/death/cast only — selection is not a combat event, so there is no hook to wire a WC3-style 'yes, commander' response even with assets in hand. 10.4 supplies files for the existing 7 paths; 10.10 verifies HUD/selection rules, not order feedback.

**WC3 bar:** WC3 plays acknowledgment voice lines on select/order and shows the green order-target animation on right-click — the canonical RTS responsiveness signal; without it, adaptive-delay orders feel laggy and dead.

**Evidence:** GDD line 349 (§6 'Input delay and client-side feedback'); grep epics.md 'acknowledg|selection sound|click feedback|move marker' = 0 owning stories; godot/src/UI/AudioManager.cs (CombatEventQueue-only drain); epics.md:2524-2542 (10.4 = the 7 named SFX paths), 2716-2736 (10.10 scope).

**Suggested home:** New Epic 10 story (or extend 10.4/10.10): selection/ack sound hook via a UI-side event + ground-order marker; presentation-only

### 62. [MAJOR] No trigger/effect PlaySound/PlayVfx actions anywhere — creators cannot play a sound or visual effect from triggers or abilities

*Domain:* polish-performance-qa · *Status:* unverified (verify agent lost to spend limit)

**Gap:** Story 2.1 deferred the presentation leaves (PlayVfx/PlaySound/ShakeScreen) 'to their owning stories', but no owning story exists in any epic: Epic 7's presentation-action whitelist (7.8b) is exactly ToggleWidgetVisible/OpenSubPanel/CloseSelf/SetLocalUiVar — no sound, no VFX — and Epic 10.4 only wires the 7 fixed combat clips. The GDD's 1.0 deliverable list includes 'Advanced editor features (particle effects, sound triggers)'. A creation platform where a scenario author cannot trigger a sound or effect fails the platform's own bar.

**WC3 bar:** The WC3 World Editor's trigger actions include Sound - Play Sound and Special Effect - Create; these are among the most-used actions in custom maps.

**Evidence:** deferred-work.md §story 2.1 carve-off item 4 (presentation leaves -> 'their owning stories'); epics.md:1949-1952 (7.8b closed whitelist, no PlaySound); GDD line 580 (1.0 deliverables: 'particle effects, sound triggers'); grep epics.md 'PlaySound|PlayVfx' = 0 hits.

**Suggested home:** Epic 7 (add PlaySound/PlayVfx to the 7.8b local-presentation whitelist + trigger action set) with asset-path plumbing via the 2.7 audio/feedback bus

### 63. [MAJOR] No human playtest or fun/feel gate anywhere in 121 stories — balance and 'plays well' are certified entirely by AI self-play and crash matrices

*Domain:* polish-performance-qa · *Status:* unverified (verify agent lost to spend limit)

**Gap:** The only playtest-shaped gates are 10.1 (solo crash/soft-lock matrix), 10.2a/b (headless AI-vs-AI win-rate band 45-55%), and 5.7 (metric-driven asymmetry: composition distance / win-rate numbers). No story ever puts a human (even Alec, let alone a second player) through an MP match, an editor journey, or a feel assessment as an AC; the GDD's Phase-2 'Friends/Family Playtest' milestone was dropped from the epics entirely. AI-self-play balance is not human balance, and 'no unpolished feel' cannot be certified by checksums — this is the single largest class of bugs (visual jank, UX dead-ends, feel) with zero owning stories.

**WC3 bar:** WC3 shipped after years of human beta balance; its feel bar (responsiveness, readability, pacing) was playtest-driven — no RTS has shipped polished on automated gates alone.

**Evidence:** epics.md:2454-2472 (10.1 ACs = crash/matrix), 2474-2502 (10.2a/b = win-rate only), 1618-1637 (5.7 = 'not a subjective looks different'); grep epics.md 'playtest' — every hit is either the F5 mode, creator onboarding, or these three; GDD line 522+ (Phase 2 friends/family playtest milestone absent from epics).

**Suggested home:** New Epic 10 story: structured human playtest gate (scripted sessions + issue triage) before 10.9a, covering MP-as-P2, editor journeys, and skirmish feel

### 64. [MAJOR] AI float->Fixed debt (D2) has no owning story, and Story 10.7's cross-platform AC is built on AI-driven harness seeds it will collide with

*Domain:* polish-performance-qa · *Status:* unverified (verify agent lost to spend limit)

**Gap:** AiOpponentSystem scores with float/Math.* (13 occurrences; logged 2026-06-09 and re-flagged as the HARD PREREQUISITE for any AI in lockstep). No story converts it: 10.11 explicitly keeps 'existing Score* float weights unchanged' (only the new counters are Fixed), and the AI-active golden is excluded from the WSL gate because of D2. Yet 10.7's AC2 requires 'a fixed match seed from the 10.2 harness' to produce byte-identical SimChecksums on Windows and Linux — 10.2 harness matches are AI-vs-AI, so the plan's own Linux gate runs straight into the documented float divergence with no story owning the fix. Also permanently blocks AI takeover on disconnect and Linux-vs-Windows behavioral parity in skirmish.

**WC3 bar:** WC3's AI runs inside the deterministic sim on all platforms; an RTS whose AI can differ per-platform on the same seed fails the reproducibility bar the project itself set.

**Evidence:** deferred-work.md §2026-06-09 item 3 + §MP-disconnect direction ('HARD PREREQUISITE... 13 occurrences in AiOpponentSystem.cs'); epics.md:2590 (10.7 AC2 uses the 10.2 harness seed), 2760 (10.11: 'existing Score* float weights are unchanged'); memory: D2 AI golden excluded from the WSL gate.

**Suggested home:** A new Epic 10 story before 10.2a (AiOpponentSystem float->Fixed), or amend 10.7 to use a no-AI seed and explicitly accept per-platform AI divergence

### 65. [MAJOR] FR-39 physical two-machine LAN determinism gate is parked with no owning story among the 121

*Domain:* polish-performance-qa · *Status:* unverified (verify agent lost to spend limit)

**Gap:** Story 1.9b AC4 (the real two-machine LAN run — the #1 tracked pre-ship gate per the Epic 1 retro) was parked because only one machine exists; the runbook is written but NO later story re-owns executing it. Epic 9 rebuilds the netcode (merged packets, adaptive delay, 4 players) and Epic 10 exports builds, yet no AC anywhere requires a physical multi-machine determinism pass before ship; 1.9b-era loopback evidence also predates every Epic 9 protocol change. Additionally deferred-work notes the LAN smoke launcher's triggers are #if DEBUG and silently no-op on exported builds (latent until Epic 10, fix homed only as a note).

**WC3 bar:** Shipping lockstep MP verified only via same-machine loopback is the classic desync-on-launch-day failure mode; WC3-era RTSes gated on real multi-machine test passes.

**Evidence:** Memory/retro decision ('FR-39... #1 tracked pre-ship gate, run godot/tools/lan-determinism-runbook.md when a 2nd box exists'); sprint-status.yaml line 2 ('physical 2-machine LAN verify (FR-39) stays parked'); deferred-work.md §1.9b item 1 (DEBUG-gated launcher, 'actionable at Epic 10'); grep epics.md Epic 9/10 for a LAN re-verify story = none (only 1.9b at :706).

**Suggested home:** Epic 10 — a release-gate story (after 10.7, before 10.9a): run the LAN runbook on exported Windows+Linux builds across two physical machines

### 66. [MAJOR] Editor undo/restore (UnitSnapshot) silently drops most authored unit state — an accumulating, unowned fidelity debt across six stories

*Domain:* polish-performance-qa · *Status:* unverified (verify agent lost to spend limit)

**Gap:** EntityPlacer.RestoreUnit rebuilds from a UnitSnapshot that carries none of: collision_radius/separation_priority/category (1.13), Energy/MaxEnergy (2.2a), abilities (2.4a), armor + passives (2.6), attack_domains (2.9a), feedback profile (2.7) — and the 2.2a review adds that restore bakes transient buffs into Base stats permanently. Undoing a delete returns a visibly different unit (no armor, no passives, wrong formation role, no abilities). The 'UnitSnapshot widening' fix is referenced in six deferred-work entries but assigned to no story; Story 6.4 verifies placement round-trips only 'positions, owners, and types'.

**WC3 bar:** World Editor undo is faithful — Ctrl+Z never silently mutates a unit's stats or strips its abilities; creators treat lossy undo as data corruption.

**Evidence:** deferred-work.md §1.13 item 1, §2.2a items 6+review-3, §2.2b item 7, §2.4a item 4, §2.6 item 3, §2.9a item 2 (all: 'the real fix is the deferred UnitSnapshot widening'); epics.md:1736 (6.4 AC round-trips positions/owners/types only); grep epics.md 'UnitSnapshot' = 0 hits.

**Suggested home:** Epic 6 (fold into 6.4 as a widened AC, or a new 6.x story: widen UnitSnapshot + capture Base* + modifiers separately)

### 67. [MAJOR] No pathfinding-quality bar anywhere: group flow through chokes, stuck units, and the known AttackMove-hover defect have no owning story

*Domain:* polish-performance-qa · *Status:* unverified (verify agent lost to spend limit)

**Gap:** No story's AC asserts pathing QUALITY — large groups traversing chokes without wedging, units not stuck on corners/building edges, no overlap oscillation. Known live defects are documented and unowned: the AttackMove arrive-threshold equilibrium hover (units orbit a goal forever; observed in-engine), and 1.12's Patrol/AttackTarget/Follow requesting no nav path ('may clip obstacles in live play... a later presentation pass' — no story named). 10.1 only catches crash/soft-lock; 10.3 measures FPS, not movement quality; 1.13 built formation mechanics but its goldens verify determinism, not feel. These are exactly the visual-jank bugs checksum tests cannot see.

**WC3 bar:** WC3's pathing (for its era) reliably moved 12-unit groups through ramps and chokes; RTS reviewers single out pathing jank harder than almost any other flaw.

**Evidence:** deferred-work.md §2026-06-09 item 7 (AMOVE_ARRIVE_SQR hover, fix candidates listed, unowned); deferred-work.md §1.12 item 1 (Patrol no-path, 'later presentation pass'); grep epics.md 'stuck|choke|ramp' = only flavor text at :792; epics.md:2454-2472 (10.1), 2504-2522 (10.3) contain no movement-quality AC.

**Suggested home:** New Epic 10 story (pathing-quality pass: choke/ramp/group scenarios with observable ACs + fix the AttackMove arrive defect), or widen 10.1's matrix

### 68. [MAJOR] No mid-match save/load of game state — a GDD Phase-1 deliverable with zero stories

*Domain:* polish-performance-qa · *Status:* unverified (verify agent lost to spend limit)

**Gap:** GDD line 538 lists 'Save/load game state' as a core deliverable. All save/load in the plan is hero-profile persistence (3.9), map/terrain authoring persistence (6.2/6.4), and settings — no story lets a player save a skirmish (or the missing campaign mission) mid-match and resume it. For deterministic lockstep this is a real feature (state snapshot or command-log + replay-to-tick) that will not fall out of other work.

**WC3 bar:** WC3 saves any game (campaign, skirmish, even MP) mid-match and resumes; players expect at minimum single-player save/resume in a 1.0 RTS.

**Evidence:** GDD line 538 ('Save/load game state'); grep epics.md 'save' — every hit is hero save/load (3.9, :1248), terrain/map persistence (6.2 :1684, 6.4 :1736), or editor save; no match-state save story in the 121.

**Suggested home:** New story (Epic 9 or 10): single-player match save/resume via command-log replay-to-tick (reuses .chmr machinery), MP save explicitly descoped

### 69. [MAJOR] Sanguine Furnace lifelong HoT dies silently ~43s into a match (256-pulse cap) — Story 2.10's AC is too thin to catch it

*Domain:* polish-performance-qa · *Status:* unverified (verify agent lost to spend limit)

**Gap:** EffectCaps.MaxPersistentPeriods=256 truncates the Court's while_alive regen passive after 256 pulses with nothing re-installing it (at the authored period_ticks=5 that is ~43s), while the modifier's stat bonus lingers — a showcase-faction signature mechanic silently turning off mid-match. Story 2.10's AC only requires regen 'over several ticks... byte-identical across two runs', which passes with the truncation present. The deferred-work note says 'fold into 2.10 planning' but the epic AC as written does not require match-lifetime regen; coverage is on paper, thin in AC.

**WC3 bar:** A racial passive that quietly stops working one minute in is exactly the 'weird bug' class the user bar names; WC3 passives (e.g., Trolls' regeneration) never time out.

**Evidence:** deferred-work.md §code review of story-2.6 item 1 (256-pulse cap, furnace_trickle stops ~43s, 'fold into 2.10 planning'); epics.md:1040 (2.10 AC2: 'regenerates HP per period... over several ticks'); EffectCaps.MaxPersistentPeriods=256 (godot/src/Effects/EffectCaps.cs).

**Suggested home:** Story 2.10 (add an AC: passive regen persists for a full match golden, via renewal-on-expiry or authored period sizing)

### 70. [BLOCKER] Custom audio pipeline nonexistent end-to-end (import → package → ingest → playback all missing)

*Domain:* extra:Custom binary-asset import → packaging → runtime ingest pipeline (WC3 Import Manager parity) · *Status:* unverified (verify agent lost to spend limit)

**Gap:** No story, FR, or UX surface lets a creator import an external .ogg into a scenario, no AC packages audio into the .chimera.zip, Story 9.9's runtime AssetRegistry ingests .glb ONLY, and every runtime playback path resolves sounds exclusively from the shipped res:// PCK — AudioManager.cs:37 pins SFX_ROOT="res://resources/audio/sfx/" and ResolveOverrideStream/TryLoad use ResourceLoader.Load (res:// only), and CombatFeedbackProfile.ImpactSoundId/DeathSoundId are documented as 'key/path under res://resources/audio/sfx/' (CombatFeedbackProfile.cs:35,51). So even if the already-flagged PlaySound trigger leaf (AR-29, epics.md:216 — no owning story) gets built, a published community scenario can NEVER carry or play a creator-supplied sound. In an exported build res:// is sealed inside the PCK, so this is unreachable without a designed user:// ingest path. GDD §7 promises assets/audio/ 'Sound effects (.ogg)' as a first-class package folder (Project_Chimera_GDD.md:422).

**WC3 bar:** WC3's Import Manager is THE defining creation-platform feature for audio: virtually every notable custom map (DotA, TDs, RPGs) imports .wav/.mp3 and plays them via the Sound Editor + PlaySound trigger actions. An 'RTS creation platform' where community scenarios can only ever use the 7 engine-shipped SFX would fail any WC3-parity review outright.

**Evidence:** Grepped ALL of epics.md for audio/sound/.ogg/import: only hits are FR-48 (epics.md:135 — shipped-game audio), FR-12a/AR-29 (:80,:216 — profile references shipped sfx), and Story 10.4 (:2524-2542 — wires 7 fixed res:// paths, creator-import never mentioned). Story 9.9 ACs (:2352-2368) say '.glb' explicitly, never audio. FR inventory 4.1-4.11 (:38-141) has no import FR. UX mockups dir grepped — no import/asset panel. deferred-work.md grepped — no audio/asset deferral (it isn't even tracked as a punt). Code: godot/src/UI/AudioManager.cs:37,184-215; godot/src/Core/Definitions/CombatFeedbackProfile.cs:35,51.

**Suggested home:** New Epic 9 story (extend 9.9's AssetRegistry + ContentPackager to .ogg, and make AudioManager/CombatFeedbackBridge resolve sound ids through the AssetRegistry with user:// fallback) paired with the creator-facing import panel story below.

### 71. [BLOCKER] Author→publish asset bundling is severed: no AC ever copies custom .glb (or any asset) into the .chimera.zip

*Domain:* extra:Custom binary-asset import → packaging → runtime ingest pipeline (WC3 Import Manager parity) · *Status:* unverified (verify agent lost to spend limit)

**Gap:** Story 3.5 lets a creator browse a GLB from an arbitrary disk folder (AR-5 note: 'SettingsData can remember last-used asset folder'), writing that path into mesh_path. But NO story makes ContentPackager copy that file into the package's assets/models/ or rewrite mesh_path to package-relative: Story 9.8's ACs only add token/screenshots/consent to the manifest (epics.md:2342-2350), and code ground truth confirms ContentPackager.Pack bundles ONLY manifest + scenario JSON + faction JSONs (ContentPackager.cs:60-125; terrain gets added by 6.2 AC4). Story 9.9 then presupposes the missing step — 'a published package whose hash now folds in the asset bytes' (:2360) — verifying and ingesting bundled bytes that no story ever bundles, and no 9.9 AC remaps definition mesh_path values to AssetRegistry entries either. Net result at 1.0 as written: publish a scenario with a custom unit model → downloaders get a dangling mesh_path → every custom unit renders as the placeholder box, silently. MeshLoader is also res://-only today (MeshLoader.cs:18).

**WC3 bar:** In WC3 an imported model lives INSIDE the .w3x — the map file is self-contained, and anyone who downloads it sees the custom art. GDD §7 makes the same self-containment promise ('Each scenario is a self-contained .chimera.zip', assets/models/ 'Custom 3D models (.glb)', GDD:399,420).

**Evidence:** Story 3.5 ACs epics.md:1180-1194 (browse/assign/preview only — no copy-into-package). Story 9.8 ACs :2334-2350 (token/quality/consent/upload only). Story 9.9 ACs+note :2352-2368 ('hash NOW folds in the asset bytes' — bundling presumed, unowned; AssetRegistry ingest AC never touches mesh_path resolution). ContentPackager.cs Pack (godot/src/Core/Definitions/ContentPackager.cs:60-125) packs scenario + faction_files only. Terrain-in-package precedent exists (6.2 AC4, epics.md:1698) proving the epics DO spell out packaging when intended. deferred-work.md: not tracked.

**Suggested home:** Story 9.8 (add a ContentPackager AC: collect every referenced asset file into assets/, rewrite refs package-relative) + a matching 9.9 AC (AssetRegistry resolves definition mesh_path for downloaded packages).

### 72. [MAJOR] No import/packaging surface for custom images (.png sprites, portraits, icons)

*Domain:* extra:Custom binary-asset import → packaging → runtime ingest pipeline (WC3 Import Manager parity) · *Status:* unverified (verify agent lost to spend limit)

**Gap:** GDD §7 promises sprites/ 'Custom sprite sheets (.png)' and portraits/ 'Unit/faction portraits (.png)' (GDD:421,423), and the planned UI consumes portraits/icons — UX-DR75 hero slot cards show 'portrait' (epics.md:346, Story 3.9 AC :1258 'hero icon/portrait'), and the HUD mockup has a portrait column — but no story in the 121 lets a creator import a .png, no AC packages one, and Story 9.9's runtime ingest is .glb-only. The icon/portrait ASSIGNMENT half was flagged by another analyst; this is the supply half: even with assignment UI, there is no pipeline to get a custom image into a package or load one from a downloaded package, so all community content is locked to engine-shipped placeholder art for every 2D surface.

**WC3 bar:** WC3 creators imported BTN/DISBTN icons and custom UI textures via the Import Manager as routinely as models — custom icons are baseline object-editor workflow for any custom unit/ability.

**Evidence:** Grepped epics.md for portrait/sprite/icon/import: hits are UX-DR75 (:346), Story 3.9 (:1258), FR-7d (:71) — all consume, none import. Story 9.9 (:2362) names '.glb' only. UX mockups (hero-picker.html, HUD.html) show portrait placeholders with no import affordance. FR inventory :38-141 has no image-import FR.

**Suggested home:** Same new import-panel + packaging story as the audio finding (an 'Import Manager' story covering .ogg/.png/.glb uniformly), plus a .png branch in 9.9's AssetRegistry.

### 73. [MAJOR] Lobby 'content synced' gate (UX-DR64c) has no owning story — no peer content transfer/download in MP

*Domain:* extra:Custom binary-asset import → packaging → runtime ingest pipeline (WC3 Import Manager parity) · *Status:* unverified (verify agent lost to spend limit)

**Gap:** UX-DR64 explicitly splits out '(c) content-synced (independently gates Start)' (epics.md:333) and UX-DR69's lobby footer shows 'All content synced' (:340), but Story 9.6 — the only lobby story — ACs cover version-match hash check, ready pills, ping, chat, and Start-gated-on-ready only (:2304-2310); no AC detects a peer missing the selected community scenario, downloads/transfers it, or gates Start on content-sync. Covered on paper (footer microcopy via UX-DR69 in 9.6's Covers), thin in AC: nothing implements the sync. As written, hosting a community scenario with a friend who hasn't separately subscribed via the 9.10 browser has no defined path — the hash-mismatch reject at Ready (9.4, :2272) turns it into a hard failure with no remediation flow.

**WC3 bar:** WC3 auto-transfers the map to joining players in the game lobby — nobody pre-downloads; this is the mechanism that made custom-map distribution work at all. Battle.net lobbies without map transfer would have killed the custom scene.

**Evidence:** Grep 'UX-DR64|content synced' across epics.md: inventory :333-334,:340 + Story 1.9a :702 (covers only UX-DR64e desync-HALT). Story 9.6 full ACs :2298-2314. Epic 9 story list :2182-2447 scanned — no transfer/sync story exists (9.9/9.10 are browser-side subscribe/download, not lobby-driven). Possible overlap with the multiplayer analyst's sweep — reporting because the content-distribution pipeline is the asset domain's consumer end.

**Suggested home:** Story 9.6 (add a content-sync AC: missing-package detection → auto-download via ModIoService or host transfer → 'All content synced' gates Start), or a new 9.6b.

### 74. [MAJOR] projectiles.json / missile art has no owning story — projectile visuals unauthorable

*Domain:* extra:Custom binary-asset import → packaging → runtime ingest pipeline (WC3 Import Manager parity) · *Status:* unverified (verify agent lost to spend limit)

**Gap:** GDD §7's package schema promises entities/projectiles.json 'Projectile definitions' (GDD:412), but no story among the 121 creates, authors, packages, or loads projectile definitions. Story 3.12 (epics.md:1302-1324) authors only the delivery flag (Hitscan|Projectile) and per-unit projectile_speed; projectile APPEARANCE (model/color/scale — WC3 'missile art') is untouched anywhere, so every projectile in every community scenario renders with the single hardcoded engine visual. Grep for 'projectile' across epics.md confirms 3.12 and sim-plumbing references only.

**WC3 bar:** WC3's object editor exposes 'Art - Missile Art' per unit — swapping arrows for fireballs is one of the most common object-editor edits, and imported models are usable as missiles. A creation platform where a magic faction's projectiles look identical to arrows reads unfinished.

**Evidence:** GDD Project_Chimera_GDD.md:412. Grep 'projectile' epics.md — hits :43,:246,:434,:610,:860-870,:1302-1324,:2292; none author visuals or mention projectiles.json. FR inventory: FR-57/DG-5 (:246) is delivery+speed only. deferred-work.md: absent.

**Suggested home:** Extend Story 3.12 (or a new Epic 3/4 story): per-unit projectile visual field (mesh/color/scale) riding the same UnitDefinition→presentation-bridge path as mesh_path, packaged like other assets.

### 75. [MAJOR] Building model assignment absent from the building editor AC (raw-JSON escape hatch only)

*Domain:* extra:Custom binary-asset import → packaging → runtime ingest pipeline (WC3 Import Manager parity) · *Status:* unverified (verify agent lost to spend limit)

**Gap:** Story 4.5's building-editor ACs enumerate 'stats / construction cost / construction time / supply bonus / produced category' (epics.md:1412) — no model field, no browse, no preview, i.e. no mirror of Story 3.5's unit-model assignment. The data path exists (4.1 buildings reuse 'the existing UnitDefinition shape' :1340, and 5.3 confirms buildings carry mesh_path :1524; 5.2's FactionValidator checks missing mesh_path :1510), so a creator CAN hand-type a path in 4.5's raw-JSON hatch — but the in-panel authoring the WC3 bar demands is unspecified. Covered on paper, thin in AC: a dev implementing 4.5 as written ships a building editor where giving your custom building a look requires raw JSON, directly contradicting the 'build a game without JSON' HARD GATE (epics.md:156).

**WC3 bar:** WC3's object editor exposes 'Art - Model File' identically for units and buildings — assigning a building model is exactly as first-class as a unit model.

**Evidence:** Story 4.5 ACs epics.md:1404-1420 (field list omits model). Story 3.5 :1180-1194 (the unit-side pattern that 4.5 doesn't inherit). 4.1 :1340, 5.3 :1524, 5.2 note :1510. HARD GATE 'Build a game without JSON' :156.

**Suggested home:** Story 4.5 (add one AC: model browse + AssetPreviewScene preview + box-placeholder fallback on the building card, reusing the 3.5 machinery).

### 76. [MAJOR] Particle-effect authoring (GDD Phase-5 deliverable) has no owning story — VFX vocabulary is flash-spheres only

*Domain:* extra:Custom binary-asset import → packaging → runtime ingest pipeline (WC3 Import Manager parity) · *Status:* unverified (verify agent lost to spend limit)

**Gap:** GDD Phase 5 — the 1.0 release phase — lists 'Advanced editor features (particle effects, sound triggers)' as a deliverable (GDD:580). Sound triggers' PlaySound leaf is already flagged by the polish analyst; particle-effect authoring is separately unowned: no story creates a particle/VFX authoring surface or asset type. The entire creator-facing VFX vocabulary at 1.0 is Story 2.7's CombatFeedbackProfile, whose 'hit particle' is a FlashSpec — a single emissive sphere with color/emission/scale/duration (CombatFeedbackProfile.cs:62-79, shipped code). Creators can recolor a flash but cannot author or import any actual particle effect for abilities, deaths, or triggers.

**WC3 bar:** WC3 shipped hundreds of attachable special effects usable via the object editor (art fields) and trigger actions (AddSpecialEffect), and imported models carried custom emitters — spell visuals are the soul of WC3 custom maps. Distinct from the PlayVfx-leaf gap: even if PlayVfx gets built, there are no authorable particle assets for it to reference beyond flash spheres.

**Evidence:** GDD Project_Chimera_GDD.md:580. Grep 'particle|vfx' epics.md — only FR-12a (:80), AR-29 (:216), Story 2.7 (:966-982); all resolve to FlashSpec. Code: godot/src/Core/Definitions/CombatFeedbackProfile.cs:31-79. No Epic 2/3/7 story mentions particle authoring; deferred-work.md silent.

**Suggested home:** A new Epic 2/10 story (curated built-in effect library selectable from CombatFeedbackProfile + a PlayVfx leaf catalog), or an explicit GDD reconciliation deferring the Phase-5 deliverable post-1.0 (currently it silently dangles).

### 77. [MAJOR] NL-trigger assist loop (fuzzy match / Did-you-mean / Fix buttons) has no home

*Domain:* extra:AI-assisted creation (Epic 8) — an entire epic no analyst swept · *Status:* unverified (verify agent lost to spend limit)

**Gap:** Story 8.4 covers only generate -> draft-for-review/edit -> apply with 'located error' rejection. The GDD-specified assist loop — fuzzy entity matching with a 'Did you mean barracks_t1?' one-click accept, inline errors with Fix buttons, and clarifying-question follow-up on ambiguity — appears in NO story AC anywhere in the 121 stories, and the code has zero fuzzy/suggest logic (errors land in a single status label, TriggerEditorPanel.cs:307). The 2026-06-21 readiness report explicitly flagged the missing 'generate→preview→confirm/fuzzy-match surface' as a flagship-feature gap, but the DG-1..9 triage created no Epic 8 story for it. Without the assist loop, a typical failed generation dead-ends at '✘ <error>' text — the headline differentiator reads as broken, violating the no-unpolished-feel bar. Covered on paper (generation + validation), thin in AC (the recovery UX that makes it usable).

**WC3 bar:** N/A to WC3 itself (no AI), but this is Chimera's stated headline differentiator, and the GDD (§4 'Natural language trigger authoring' + §9 technical spec) pins the assist loop precisely because raw LLM output routinely misses entity names; the project's own quality bar for it is the GDD contract, not WC3.

**Evidence:** GDD §4 flow + risks paragraphs (fuzzy-matches entity names, clarifying questions) and §9 'Natural language trigger scripting — technical specification' (fuzzy matching, one-click accept, inline Fix buttons); Story 8.4 ACs epics.md:2072-2091 (none of these appear); grep for fuzzy|Did you mean|Fix button|one-click across _bmad-output hit only implementation-readiness-report-2026-06-21.md:274 flagging the gap; DG stories 1.12/1.13/2.11/3.12/4.7/6.5/7.10/9.13/10.11 contain no Epic 8 item; code: LLMService.cs + TriggerEditorPanel.cs greps show no fuzzy/suggest mechanism, error surface = _statusLabel (TriggerEditorPanel.cs:301-307).

**Suggested home:** Extend Story 8.4's ACs (or add an 8.4b 'NL trigger assist surface') owning fuzzy entity matching + one-click accept + inline Fix-button error rendering; the T3 on-node error routing pattern from 7.9 (epics.md:1962) is the precedent to reuse.

### 78. [MAJOR] Entity-reference validation pass (GDD pass 2) unowned; spawn_unit.unit_id gap is a known deferral with no assigned story

*Domain:* extra:AI-assisted creation (Epic 8) — an entire epic no analyst swept · *Status:* unverified (verify agent lost to spend limit)

**Gap:** The GDD's five validation passes include entity-reference validation: every referenced entity ID / region name / player ref must exist in the scenario registry. The as-built 5-pass Validate checks only faction-slot 0-or-1, BuildingType enum names, operators, and range/safety (LLMService.cs:258-323) — no entity-id registry cross-reference. The 1.11 code review explicitly deferred spawn_unit.unit_id validation 'to a future trigger/unit-type-validation hardening pass' (deferred-work.md:108) and no story owns that pass. Story 8.4 pins 'VERIFY the existing 5-pass trigger Validate still gates output' — i.e., it re-certifies the weaker validator. Story 7.6's authoritative gate ('every construct from the closed registry, statically checkable') plausibly covers dangling DSL-node/variable refs via the graph-linter, but no 7.6 AC pins rejection of a nonexistent unit_id/entity reference — partial coverage suspected, not proven. Failure mode: LLM (or hand-author) outputs unit_id 'barrack' -> passes every gate -> silent dead spawn in a shipped scenario, exactly the hallucinated-entity risk the GDD's pass 2 exists to close.

**WC3 bar:** The WC3 World Editor cannot reference a nonexistent unit type — object references are picked from registries, so dangling references are structurally impossible; any 'located error before display' bar requires the equivalent reference check here.

**Evidence:** GDD §4 ('reference validation ensures all entity IDs, region names, and player references exist') + §9 five-pass spec; code LLMService.cs:250-323 (actual passes) and ScenarioValidator gap; deferred-work.md:106-108 (1.11 review deferral, 'no story owns the hardening pass'); Story 8.4 note epics.md:2090 (pins existing 5-pass); Story 7.6 ACs epics.md:1894-1904 (no entity-ref AC).

**Suggested home:** Story 7.6 (add a reference-resolution AC to the authoritative gate covering unit_id on both trigger spawn actions and pre-placed units, per the deferred-work note) with 8.4 consuming it at generate time.

### 79. [MAJOR] Ollama structured-output enforcement (JSON-Schema format param) pinned nowhere

*Domain:* extra:AI-assisted creation (Epic 8) — an entire epic no analyst swept · *Status:* unverified (verify agent lost to spend limit)

**Gap:** GDD §9 explicitly specifies local generation uses Ollama's native JSON-Schema enforcement via the 'format' parameter ('the request includes the exact schema and the model is constrained to produce only valid JSON'). The current code sends a plain concatenated prompt with no format/schema constraint (LLMService.cs:212-245), and Story 8.3a's ACs pin only endpoint shape (Ollama /api/chat), NormalizedResult, no-SDK, no-fallback — nothing requires schema-constrained decoding; the architecture D6 record is equally silent. Related unpinned GDD params: temperature 0 and response caching for identical prompts appear in no story or AC. Failure mode: an 8B local model free-decoding JSON produces malformed/near-miss output frequently, so every local generation loops through the four-state 'failed validation' message — offline/local AI (an explicit selling point for Tinkerers and keyless creators) ships feeling broken while cloud works, an asymmetry a reviewer will read as unpolished.

**WC3 bar:** No WC3 analogue; the bar is the GDD's own §9 contract ('constrained to produce only valid JSON matching that schema') and the 1.0 'no unpolished feel' bar for a headline feature's offline mode.

**Evidence:** GDD §9 'Local integration' paragraph (format parameter, Llama 3.1 8B, temperature 0 + caching in §4 risks); code LLMService.cs:212-245 (no format/temperature/cache); Story 8.3a ACs + note epics.md:2044-2054; game-architecture.md:1181-1254 D6 sub-decisions (streaming/timeout/hosts covered, structured output absent); grep 'format|temperature|cache' in LLMService.cs and 'structured output|json_schema' in game-architecture.md = no hits.

**Suggested home:** Story 8.3a: NormalizedRequest carries an optional response schema; Ollama adapter maps it to the format parameter (and Anthropic to tool-use/prefill); pin temperature in NormalizedRequest defaults.

### 80. [BLOCKER] No player-facing objective display for win conditions (7.10 presets are invisible to the player)

*Domain:* extra:Scenario objectives / quest-log surface (author → objectives.json → in-match display) · *Status:* unverified (verify agent lost to spend limit)

**Gap:** Story 7.10 ships six win conditions (2 built-in + KotH / Timed Survival / Assassination / Landmark Destruction) but every AC is creator-side or sim-side: picker UI, load-time param validation, sim-layer WinConditionSystem verdict, checksum fold, ShowGameOver wiring. No AC anywhere derives or displays a goal statement to the PLAYER at match start or during play, and no loading/briefing screen exists as a carrier ('loading screen' = 0 hits in epics.md; the UX spec's only scenario-load treatment is a transmute spinner, EXPERIENCE.md:72). Story 10.10's HUD hierarchy (status line → unit counts → resource strip → controls strip → minimap → command card → stall banner) contains no objectives element. Net effect against the 1.0 bar: a player who subscribes to a KotH or Assassination scenario via the FR-37 browse/subscribe/play flow (Story 9.10) spawns into a match with literally no in-game way to learn the goal — victory/defeat just 'happens'. The mod.io description (9.8, ≥100 chars) is browse-card metadata, never surfaced in-match. This breaks the product's core loop (community scenarios with preset win conditions are the whole point of 7.10 + Epic 9) and directly undercuts 7.10's own promise of 'a complete objective in one click' — the objective is complete in the sim but mute on screen. Only mitigation: a creator can hand-build a goal label via FR-26 widgets (7.7), which makes goal visibility optional per-author instead of a platform default.

**WC3 bar:** WC3 tells the player the goal in every mode: melee states the win rule, and every campaign/custom map opens with quest entries in the F9 Quest Log plus 'Quest Update' notifications; the map's loading screen also carries the scenario description. A WC3 custom map where you cannot discover the objective would be rated broken.

**Evidence:** epics.md 1970-1992 (Story 7.10 full ACs — picker/validation/verdict only, no display), 2716-2736 (Story 10.10 HUD hierarchy + keybindings, no objectives panel/hotkey), 2370-2386 (Story 9.10 FR-37 flow ends at 'becoming playable'), 2334-2350 (9.8 description is manifest/browse-only); grep 'loading screen' in epics.md = 0 hits; ux EXPERIENCE.md:72 (spinner is the only load treatment); grep 'objective' in epics.md = only lines 1638/1974/2472 (none a display story); godot/src/Core/ScenarioDirector.cs (victory/defeat actions fire OnVictory with no goal text).

**Suggested home:** Extend Story 7.10 (each preset/built-in auto-derives a default parameterized goal line, e.g. 'Hold the Obsidian Rise for 3:00') + Story 10.10 (a HUD objectives element that renders the active win condition); alternatively a new Epic 10 story owning the match-start goal surface.

### 81. [MAJOR] objectives.json pipeline and quest-log DSL vocabulary entirely absent (authoring → packaging → update → display)

*Domain:* extra:Scenario objectives / quest-log surface (author → objectives.json → in-match display) · *Status:* unverified (verify agent lost to spend limit)

**Gap:** GDD line 418 defines scenario/objectives.json — 'Player-facing objective descriptions' — as part of the shipped .chimera.zip package schema, but no story in any epic writes, packages, validates, updates, or reads it: dead schema at 1.0. No editor story has an objective-text field (6.4's win panel is enum-only; 7.10's picker is parameters-only). The trigger DSL has no objective vocabulary at any tier: GDD:208's Tier-2 actions (Spawn Unit, Display Message, Set Variable, Victory/Defeat, Create Timer, Play Sound, Move Camera) contain nothing objective-shaped; the as-built ScenarioDirector's 8 actions and 6 events match; and Epic 7's stories (7.1a-7.9) rebuild this vocabulary onto the graph IR adding variables/expressions/custom-events/loops/UI-rails without ever adding an Add/Update/Complete-Objective action or a quest panel. So a creator authoring a multi-step scenario ('destroy the gate, then escort the alchemist') has exactly two tools: the transient display_message toast (missable, no recall — a player who alt-tabs past it has no way to re-read the goal) or hand-building a pseudo quest log from FR-26 Label/Panel widgets + trigger visibility per scenario (7.7) — real but manual, unvalidated as a pattern, and giving no platform-standard place players learn to check. Not listed in deferred-work.md — an unplanned blind spot, not a documented punt. Severity is major rather than blocker only because the FR-26 workaround exists and finding 1 covers the win-condition floor; for scripted/campaign-style community scenarios this is the single biggest WC3-parity hole in the creator toolkit.

**WC3 bar:** The WC3 World Editor's trigger vocabulary ships Quest - Create Quest / Quest Message / Mark Quest Completed(Failed/Discovered) plus quest-requirement items, all rendering into the built-in F9 Quest Log with automatic 'Quest Update' pings — the standard mechanism every campaign and virtually every scripted custom map uses to communicate multi-step goals; authors never build their own quest UI.

**Evidence:** Project_Chimera_GDD.md:415-418 (objectives.json in package schema) and :208 (Tier-2 action list); grep 'objectives.json' in epics.md = 0 hits; epics.md 1770-1992 (all Epic 7 DSL stories — no objective action), 1722-1740 (6.4 enum-only win panel), 1906-1920 (7.7 FR-26 widget vocabulary = the workaround), 2334-2350 (9.8 packaging manifest carries token/thumbnail/description/screenshots, no objectives.json); godot/src/Core/ScenarioDirector.cs:272-377 (as-built 6 events + 8 actions) and Bootstrap/Phases/TriggerEditorPhase.cs:46 (display_message = toast label); deferred-work.md grep objective/quest/briefing = 0 relevant hits.

**Suggested home:** New Epic 7 story (fits after 7.7): DSL objective actions (add/update/complete/fail, deterministic — objective state as validated sim-adjacent data folded per the DSL-var pattern) + a default quest-log panel with hotkey; the authoring field rides the trigger editor; packaging of objectives.json belongs in 9.8's manifest work.

---

## Minor / Polish Findings (46) — unverified

- **Selection-group polish is thin: add-to-group/select-army only asserted as keybindings; no subgroup tab-cycling** *(core-match-gameplay)* — Control groups exist as-built (assign Ctrl+1-9, recall 1-9 in SelectionSystem.cs), but Shift+# add-to-group and F2 select-army appear only in UX-DR66's default-binding list and Story 10.10's AC that 'the canonical default keybindings are bound' — a binding-exists check, not a behavior AC (no add-to-group logic exists in code; grep shows Shift only in patrol/selection-add comments). Subgroup tab-cycling (Tab to cycle unit types in a mixed selection, ability keys routing to the right subgroup) appears nowhere; the command card operates on a single 'focused entity' (2.4b deferred item 6). The Built-Foundation inventory itself flags control groups as 'VERIFY before treating as done' but FR-45's verify list never included them.
- **No idle-worker button or worker-management conveniences** *(core-match-gameplay)* — No FR, UX-DR, story, or code covers an idle-worker indicator/button (grep 'idle worker' returns nothing across epics and UX docs). With a gather economy and multi-base play, players must manually hunt idle workers.
- **GDD-promised projectile miss/dodge micro does not exist and 3.12 locks in always-hit tracking** *(core-match-gameplay)* — GDD §3 combat spec promises 'Projectile vs. hitscan flag — projectiles can miss moving targets, adding micro potential.' Story 3.12 ships the hitscan/projectile flag + per-unit speed but its scope limit states 'projectile visuals/tracking behavior are unchanged' — as-built projectiles track their target and always hit, so the dodge micro the GDD sells is impossible and no other story adds it.
- **Known as-built AttackMove arrival deadlock (units hover forever) is unowned by any story** *(core-match-gameplay)* — deferred-work.md (2026-06-09 item 7) documents that AMOVE_ARRIVE_SQR (0.5u²) is unreachable under crowding — converging units hold a separation equilibrium ring at ~1.0u, never 'arrive', and hover in AttackMove forever; AI waves leak from the available pool and can never be re-waved. It was 'paired with the Mechanism-4 building-damage story', but 2.9a shipped without touching it, and no Epic 1-10 story or FR-45 checklist owns it. This is exactly the class of 'weird bug / unpolished feel' the user's bar excludes, and it degrades every AI skirmish (10.1's 'AI builds and attacks' AC could pass while waves still deadlock at the target).
- **WC3-style upkeep model promised as a creator option but unowned** *(core-match-gameplay)* — GDD §3 resource spec: 'An optional upkeep system can be enabled per-scenario, applying income multipliers at configurable population thresholds. This reproduces Warcraft III's gold tax mechanic for creators who want it.' FR-16/Story 4.4 covers the supply/cap model (start cap, per-building bonus, ceiling, disable) but no income-multiplier-by-population-threshold option exists in any story; grep 'upkeep' over epics.md returns nothing.
- **End-of-match and leave-match flow is thin: no score screen, rematch/exit path, or MP concede** *(core-match-gameplay)* — Match end is the as-built ShowGameOver overlay (MainScene.cs:814) which 7.10 re-points to the sim verdict — but no story specifies what the victory/defeat flow contains (stats/score screen, back-to-menu/rematch, replay-save prompt), and there is no surrender/concede action for multiplayer (leaving = a disconnect handled by 9.5's freeze policy, which leaves your frozen army in the match rather than resolving defeat). Grep for 'surrender|forfeit|victory screen' finds nothing beyond 7.10's ShowGameOver reference.
- **Desync HALT and disconnect are dead ends: no next action, and the opt-in desync report is unowned** *(game-shell-ui)* — 1.9a ships the terminal HALT overlay with a clear message (UX-DR64e — good), and 9.5 freezes a dropped player's units, but no story defines what the player can DO next in either case: no return-to-lobby/menu action from the HALT screen, no 'export replay / send desync report' affordance, and AR-41's 'opt-in crash/desync report' exists only as a posture note in 1.10a's dev record ('AR-41 no-analytics doc'), never as a build story. After a desync the only exit is the missing in-match menu (see separate finding) or killing the process.
- **No match loading screen with progress** *(game-shell-ui)* — Match/scenario load feedback is only the generic 'transmute spinner' state pattern (EXPERIENCE.md); no story builds a loading screen, and MP match start has no per-player load/progress surface between 'all ready' and tick 0. Defensible given the <=2s NFR-1 load target, but community scenarios with runtime GLB ingest (9.9) can load slower, and a black-frame or frozen-lobby start reads unpolished.
- **Victory/defeat + score screen is unowned by any restyle/verify story** *(game-shell-ui)* — The end-of-match screen exists as-built (VICTORY/DEFEAT card, duration, kills / units built / ore mined), which meets a minimal WC3-class summary — but no story owns it: 3.11 restyles Title/Mode Select/Settings only, 10.10's HUD hierarchy (UX-DR71) excludes it, and no AC verifies its stats or restyles it to the Theme. It will ship in pre-design-system placeholder styling unless someone remembers it; richer WC3 stats (per-category tabs, resources graph, APM) are absent but optional.
- **No lobby host controls: kick, close/open slot** *(game-shell-ui)* — Story 9.6's lobby covers slots/ready/ping/chat/Start gating but no host moderation: no kick, no closing a slot, no host migration note. For public/LAN lobbies of up to 4-8 players this is standard hygiene; a griefing joiner who never readies can permanently block Start (Start is gated until ALL slots ready).
- **No idle-worker button** *(game-shell-ui)* — Nothing in the UX spec, epics, or GDD provides the idle-worker indicator/button (or any idle-unit surfacing). With a gathering economy and worker-built buildings, players will lose track of idle workers with zero HUD affordance — a small but iconic WC3 QoL feature.
- **Terrain texture palette is 4 hardcoded placeholder layers — not creator-extensible** *(map-editor)* — Story 6.1 verifies exactly the as-built set: Grass/Dirt/Rock/Snow on keys 1-5 with solid-colour placeholder albedos (epics.md:1676; STATUS.md:135). No story lets a creator add/replace terrain layers or import terrain textures (AR-27's runtime asset ingest covers unit GLB/PNG/OGG, not Terrain3D texture slots), violating the project's own 'every system data-driven and creator-extensible' rule for this one surface. Painting WORKS, so this is thin-coverage rather than absence — but 4 fixed layers is a visibly small palette for map variety. (The placeholder textures themselves are asset production = out of scope; the fixed 4-slot SYSTEM is the gap.)
- **Placed entities have no rotation/facing (GDD says placement stores rotation)** *(map-editor)* — GDD's Entity Placer spec says 'Placed entities store position, rotation, owner, and type ID' (GDD:291), but ScenarioUnit/ScenarioBuilding carry only x/z/slot/type (ScenarioData.cs:71-116), EntityPlacer has no rotation control (grep 'rotation' in EntityPlacer.cs → zero), and Story 6.4's round-trip AC covers 'positions, owners, and types' only (epics.md:1736). Buildings/units all face one way; no story adds rotate-before-place.
- **Map properties beyond name+author are unauthorable (description, suggested players, loading-screen text)** *(map-editor)* — The map itself carries only Id/DisplayName (+MapBounds) — ScenarioData.cs:128-144; the as-built export panel adds Map name + Author LineEdits into the package manifest (STATUS.md:245). Description and screenshots exist only at PUBLISH time as mod.io fields (AR-30/Story 9.x, epics.md:2342) — a LAN-shared or locally-loaded .chimera.zip has no authored description, no suggested-players, no loading-screen text, and no story adds a map-properties dialog. Minor because name/author exist and mod.io covers the discovery path.
- **No pathability/walkability view overlay in the editor** *(map-editor)* — No story gives the editor a debug/view overlay showing what is walkable vs blocked (building footprints, future terrain blocking). FlowFieldSystem.GetObstacle exists 'for debug visualization' (FlowFieldSystem.cs:103-104) but nothing renders it in Edit mode. Creators can only discover pathing dead-zones by playtesting. Becomes more important if the impassable-terrain blocker is fixed.
- **No auto-generated minimap preview for map selection/lobby** *(map-editor)* — The in-match MinimapBridge renders live (STATUS.md:289), but no story generates a static minimap/top-down preview for the skirmish map picker, MP lobby, or content browser — UX-DR69's lobby shows a 'scenario header' (text) and browser cards show creator-uploaded thumbnails (epics.md:2378). Players pick maps blind or trust hand-made screenshots.
- **Undo/redo coverage is thin at the AC level for non-placement editor ops** *(map-editor)* — UX-DR59 mandates 'undo/redo everywhere in the editor' (epics.md:328), but the only ACs are terrain strokes (6.3) and placement/delete/move (6.4). Win-condition changes, resource-node supply/rate spinner edits, per-slot start-ore edits, and (future) map-properties edits are not on the undo stack in any AC, and the as-built EditorHistory wraps only the four placement actions (STATUS.md:145). Covered on paper by UX-DR59, thin in AC — a reviewer interleaving a win-condition change between placements will find Ctrl+Z skips it.
- **No editor autosave / crash-recovery for maps in progress** *(map-editor)* — No story autosaves the working scenario or recovers after a crash; saving is manual (Save/Export). The hourly '[AutoSave]' in git history is the dev-repo commit loop, not an editor feature. An hour of sculpting lost to a crash is the classic editor-trust killer. Noting honestly: classic WC3 also lacked autosave (added only in Reforged-era updates), so this is edge-of-parity — but it is on the modern editor-UX floor.
- **No symmetry/mirroring tools for melee map balance** *(map-editor)* — No story offers mirror/rotational-symmetry placement or terrain mirroring for competitive-map authoring; combined with no copy/paste, balanced 1v1/2v2 layouts must be eyeballed entity by entity. Per the task rubric this is minor since WC3 itself lacks built-in mirroring (the community used third-party tools).
- **Height-advantage vision is radius-only — terrain never occludes sight (no WC3 'can't see up the cliff')** *(map-editor)* — Story 6.5 adds a vision-radius BONUS for elevated units, but StampCircle stays a flat 2D circle: low-ground units see up hills exactly as far as on flat ground, and hills never block line-of-sight for vision or attacks (no miss chance, no occlusion). So high ground grants extra radius but provides zero concealment — the tactically meaningful half of WC3 high-ground rules is absent and unowned by any story.
- **No 'Random Choice' / random-roll DSL node despite GDD promise and named random-pool use cases** *(triggers-object-editing)* — GDD line 309 lists 'Random Choice' among T3 Flow nodes, and 7.5 names autochess pools (inherently random) as a target pattern with 'AR-13 SimRng for any loop randomness' — but no Epic 7 AC ships a random node, roll built-in, or random-pick-from-array primitive in the closed registry. SimRng exists sim-side (1.5) yet nothing exposes it to creators; random waves/drops/pools may be unauthorable. Suspicion stated: this could be intended to ride 7.3's built-ins or 7.5, but no AC names it, and in a closed no-escape-hatch grammar an unnamed construct does not exist.
- **Trigger organization ergonomics absent: no copy/paste across scenarios, folders/categories, comments, or search** *(triggers-object-editing)* — 7.2's ECA editor is add/edit/enable/delete; 7.9 adds the graph view with an _editor annotation channel for node positions. No story provides trigger folders/categories, per-trigger or per-node comments, copy/paste of triggers between scenarios/maps, or search — the organization affordances that keep a 50-trigger map manageable. The IR's annotation channel is the natural home but nothing populates it.
- **No runtime trigger observability: no variable watch, fired-trigger log, or in-playtest diagnostics** *(triggers-object-editing)* — Validation is exemplary at LOAD time (7.6), but at RUN time a creator whose trigger doesn't fire has zero tooling: no variable-watch panel (the 7.7 DslVarReadback rail could power one for free), no 'trigger X fired at tick N' log, no fuel-consumption display. The only debug overlay planned in 1.0 is the AI decision-weight overlay (10.11). For a platform whose pitch is fast F5 iteration on logic, silent-failure debugging is trial-and-error.
- **No runtime enable/disable-trigger action (trigger on/off from another trigger)** *(triggers-object-editing)* — TriggerDefinition.Enabled is an authoring-time flag; run_once and cooldown exist (as-built, kept by 7.4), but no action/node lets a trigger switch another trigger (or itself) on/off mid-match — e.g. 'after the boss dies, disable the wave spawner'. Workaround exists (guard every trigger with a Bool variable condition), so this is ergonomics, not capability.
- **No String variable type — dynamic runtime text is limited to pre-authored widget strings + numeric bindings** *(triggers-object-editing)* — 7.2's closed value-type set is Int/Fixed/Bool/EntityRef/FactionRef/Point/TimerRef/Array; strings never enter the tick (deliberate, AR-32). Runtime text therefore = static display_message strings and Label/{variable} numeric formatting. Creators cannot build/concatenate text at runtime (dynamic names, composed quest text). Design-accepted with reasonable workarounds, but it caps the FR-26 'RPG dialog' promise at pre-authored lines with visibility toggles.
- **No icon/portrait authoring for units and abilities (command card + hero picker consume icons nobody assigns)** *(triggers-object-editing)* — The Unit Card Editor authors stats/model/abilities (3.3-3.5: GLB model assignment only); the Ability Editor (2.5x) has no icon field (2.4b's live button renders text '50 energy · 6s CD'); yet UX-DR75 hero slot cards show 'hero icon/portrait' and command-card buttons need imagery. No story adds an icon field, icon browse/assignment, or button-position control. Custom content will ship with text-only or placeholder buttons — a visible polish delta on the 'fully operational UI' bar. (Icon ART is out of scope; the ASSIGNMENT field/system is not.)
- **Trigger spawn_unit.unit_id (and pre-placed unit types) still unvalidated — explicitly deferred, not clearly owned by 7.6** *(triggers-object-editing)* — The 1.11 review deferred validating spawn_unit's unit_id against a known-unit set (mirroring the pre-existing pre-placed-units gap): a bad/LLM-generated unit_id today reaches ScenarioDirector as a silent dead spawn. 7.6 mandates the authoritative load-time gate ('correct specific located error') but its ACs never name unit-id referential checks, and the deferral says 'close it in a future trigger/unit-type-validation hardening pass' — no story claims that pass. Risk: the flagship no-escape-hatch validator ships with a known silent-failure hole in its most-used action.
- **Post-HALT desync recovery UX and the AR-41 opt-in desync report are unowned** *(multiplayer-social)* — UX-DR64e delivers a clear terminal HALT message (shipped in 1.9a/1.9b), but the architecture explicitly leaves 'abort/HALT player-facing recovery policy — recoverable rejoin vs terminal' as an OPEN decision 'tracked with the lobby/UX work' (game-architecture.md:2529), and no Epic 9 story picks it up: after HALT there is no defined path — no return-to-menu button in an AC, no auto-save/flag of the .chmr replay for diagnosis, no rematch/re-lobby. Separately, AR-41's 1.0 posture includes 'an opt-in crash/desync report bundling the .chmr + checksum log', which the coverage note folded into Story 1.10a — but 1.10a shipped only the no-analytics DOC, so the opt-in report now has no owner at all. A wild desync (the exact event these systems exist for) dead-ends the player in an overlay with nothing actionable.
- **No auto-update of subscribed mod.io content** *(multiplayer-social)* — Story 9.10 covers subscribe → download → verify once, but no AC checks subscribed packages for newer versions on launch/browse or re-downloads updates. A creator who patches their published scenario leaves every subscriber on the stale version until they manually notice — and stale local versions then trip the 9.4 hash gate in every lobby (compounding the missing 'Update Required' flow). The GDD's automated-version-management promise ('players never manually manage mod versions') implies update handling that no story delivers.
- **In-app report/moderation button missing from the content browser** *(multiplayer-social)* — The GDD's launch-MVP list for the UGC platform explicitly includes a 'Report button feeding into mod.io moderation', but Story 9.10's delegation list is browse/search/tag-filter/sort/subscribe/rate only — report is absent from the ACs and from UX-DR72. For a platform inviting arbitrary user content at 1.0 (with a month-1 target of ≥50 community scenarios), having no in-app abuse/IP-violation reporting path is a real platform-safety hole, even if mod.io's website technically accepts reports.
- **No LAN game discovery — joining requires typing an IP** *(multiplayer-social)* — The LAN path is manual: the as-built Direct tab is a port SpinBox + IP field + Host/Join buttons, and Story 9.6's LAN journey AC ('join a LAN lobby and chat') never specifies HOW a joiner finds the game — no UDP broadcast/discovery story exists, so the flow remains type-the-host's-IP. Functional, but a friction point WC3 solved in 2002 and the kind of rough edge the 'no unpolished feel' bar calls out.
- **Anti-maphack deferred post-1.0 — honest impact restatement** *(multiplayer-social)* — The GDD originally promised protocol-level anti-maphack ('clients literally never receive data about units they cannot see'); the readiness triage correctly reconciled this — 1.0's lockstep means full state exists on every client, fog is a render mask, and map-hacking is technically possible (DG-10 deferred post-1.0). This is a documented, defensible decision, restated here because Epic 9 ships matchmaking with strangers where the incentive to cheat exists (unlike LAN friends): 1.0's competitive integrity rests entirely on command validation + the small player pool, and nothing in 9.x adds even detection heuristics. Not a planning hole — a risk the marketing/positioning must not contradict (avoid GDD §6's original anti-maphack claims in store copy).
- **Reconnect and AI-takeover explicitly deferred — the 9.5 freeze floor is WC3-comparable but the deferral is undecided, not scheduled** *(multiplayer-social)* — Alec's own captured direction (2026-06-24) wants host-toggleable AI takeover of a dropped faction and player reconnect beyond the 9.5 freeze floor, with the hard prerequisite (float→Fixed AiOpponentSystem) shared with Story 10.11. It was parked 'NOT yet turned into PRD FRs or Epic 9 stories' and remains unowned — the memo even notes the architecture's open rejoin decision (game-architecture.md:2529) is closed by it. The shipped floor (drop → permanent freeze, no rejoin ever, even from a momentary Wi-Fi blip) is acceptable vs 2002-WC3 but below the modern RTS bar (AoE2:DE, BAR, FAF all reconnect), and a 30-second router hiccup permanently ruining a 40-minute match is the kind of experience the 'no weird bugs/unpolished feel' bar gets judged on. Flagged so the 1.0 decision is made deliberately rather than by default.
- **No memory/leak soak or long-match endurance test anywhere** *(polish-performance-qa)* — 10.3 is a spike stress test (spawn 500/1000/2000, profile a heavy frame); no story runs a long match (30-60+ min, or an AFK overnight soak) watching memory growth, pooled-node leaks (48-slot flash pool, audio pool, minimap textures), ring-buffer wrap behavior, or presentation-side allocation creep. Godot C# + per-frame texture streaming is a classic slow-leak surface that only shows up over time — a category the plan's excellent determinism tests structurally cannot catch.
- **Performance is gated only on the single dev 'reference machine' — no min-spec target, no pathing-storm or MP-late-game load case** *(polish-performance-qa)* — 10.3's AC (otherwise strong) names one reference machine — the dev box that currently renders at 580 FPS. No min-spec/mid-range hardware commitment exists anywhere (the GDD's 500-2,000@60FPS has no hardware qualifier), and the stress scenario is spawned-combat only: no whole-army cross-map pathing storm (flow-field churn), no 4-player MP late-game with networking + fog + minimap streaming overhead. Steam release (10.9a) will publish system requirements that nothing in the plan ever measured.
- **No loading-time / startup budget story** *(polish-performance-qa)* — No AC anywhere bounds app startup, scenario load, or match-start time (content-hash verification, registry builds, navmesh bake, and mod.io content ingest all sit on these paths and only grow through Epics 7-9). The edit->play loop has a ≤2s budget (3.10) but cold start and match load have none.
- **No graceful crash handling: the opt-in crash/desync reporter is explicitly NOT built in 1.0 and no story owns an unhandled-exception UX** *(polish-performance-qa)* — AR-41's discharge in 1.10a is a posture DOC: 'an opt-in crash/desync report... is a documented fast-follow, explicitly NOT built in 1.0' — and no other story adds an unhandled-exception guard, a user-facing crash message, or log preservation guidance. An unhandled C# exception in a shipped Godot build is a silent hard-exit; players get nothing and the developer gets no artifact. This is a deliberate deferral, but it's in tension with the 'no glitches' 1.0 bar for a game shipping to Steam.
- **Localization posture is never stated — English-only is undeclared and no string-table exists** *(polish-performance-qa)* — No epic, FR, NFR, or GDD section mentions localization, language, or string tables; all UI text is hardcoded in C# (programmatic UI). English-only for 1.0 is a perfectly defensible solo-dev call, but it is nowhere DECIDED, and the hardcoded-string pattern makes later localization a full rewrite. Subtitles (10.8c) and the microcopy standard (UX-DR65) both bake in English-only assumptions silently.
- **MP reconnect and AI-takeover remain unscoped — a dropped player is frozen out for the rest of the match** *(polish-performance-qa)* — Story 9.5 ships the freeze-and-continue floor only ('Drop-to-AI is a D4 fast-follow, explicitly out of scope'); reconnect appears in no story and the architecture's rejoin decision (game-architecture.md:2529) stays open. Alec's own captured direction (2026-06-24) wants host-toggleable AI takeover + rejoin, but it was parked without PRD FRs or Epic 9 stories. A 1.0 with matchmaking (9.6) where any disconnect permanently removes a player — with their army standing frozen as free kills — is at the floor of acceptability.
- **Custom asset bytes are outside the pre-match MP handshake (download-time hash only)** *(extra:Custom binary-asset import → packaging → runtime ingest pipeline (WC3 Import Manager parity))* — The Ready-packet handshake gates {scenarioHash, rulesetHash, startStateHash} (Story 9.4, epics.md:2272; AR-18 :201), and AR-23 defines those as FNV-64 over the PARSED gameplay model (:208) — binary asset bytes are not part of the parsed model, and Story 9.9's asset-byte hash is verified only once at download (:2360-2364). So post-install tampering with a package's .glb (e.g. replacing an enemy unit's model with an oversized/high-visibility mesh — the classic model-hack) is undetectable at match start; peers can also silently render different art. Presentation-only, cannot desync the sim, hence minor — but it compounds the already-flagged handshake coverage gap and leaves 9.9's integrity promise ('trust downloaded content has not been tampered with', :2356) holding only at download time.
- **Generation-in-flight UX: spinner only — no cancel affordance, no cost/token surfacing in any AC** *(extra:AI-assisted creation (Epic 8) — an entire epic no analyst swept)* — Every generation story's UX AC is exactly one thing: the 'Transmuting...' spinner (UX-DR52, itself just a spinner token, epics.md:321). No AC anywhere adds a cancel button, even though LLMService already exposes Cancel()/CancelScenario() (LLMService.cs:153-154,487-488) that no panel wires (grep of TriggerEditorPanel.cs and MapGeneratorPanel.cs: no cancel UI), and D6-2 plans to RAISE the 30s timeout after measuring Opus 7-pass map-gen latency — i.e., waits get longer, up to 7 surfaces (trigger/map/unit/ability/hero/faction/balance) each an uncancellable 30-60s+ operation after a misclick. Token/cost surfacing for metered cloud keys appears in zero stories. Cheap fix (service plumbing exists) but currently unowned.
- **GDD fallback-chain terminus (template/preset library + cached response) dropped without a home or GDD reconciliation** *(extra:AI-assisted creation (Epic 8) — an entire epic no analyst swept)* — The GDD twice promises a final degradation step: Claude -> Ollama -> 'offer template-based trigger creation from a preset library' / 'return template/cached response' (§4 and §9). The epics deliberately replaced the auto-fallback chain with selected-provider-authoritative (8.3a AC2, D6-5) and degrade-to-manual (8.3b) — a defensible pivot — but no story offers preset trigger templates or cached responses as the AI-down floor, and no story wires 'AI unavailable -> suggest presets'. 7.10's T1 preset templates cover WIN CONDITIONS only (four named presets), not a general trigger preset library. The GDD was reconciled 2026-06-22 for other contradictions (FR-7/FR-26/FMA) but §4/§9's fallback chain was left contradicting the shipped design — same unreconciled-GDD class as the anti-maphack contradiction previously triaged.
- **Tinkerer 'full API access to the LLMService for custom tool building' (GDD §9) has no story** *(extra:AI-assisted creation (Epic 8) — an entire epic no analyst swept)* — GDD §9 'For Tinkerers' promises full API access to the LLMService for custom tool building. No FR (FR-29..34, FR-53..61) and no story exposes any creator-facing LLMService API; the other three Tinkerer promises in that paragraph found homes (Ollama offline = 8.3a, decision-weight overlay = 10.11, Claude Code = external tooling). The drop looks deliberate — it structurally contradicts 7.6's 'no scripting escape hatch' closed-registry design — but it was never reconciled out of the GDD, so at 1.0 the source-of-truth doc promises a capability the platform intentionally forbids.
- **8.5's 'scenario-type parameters' reference an undefined concept — no story defines the ScenarioType schema/registry, risking circular validation** *(extra:AI-assisted creation (Epic 8) — an entire epic no analyst swept)* — Story 8.5's central AC says the map validator 'uses the scenario-type parameters instead of the hardcoded 6-unit / 2-slot / forced-faction-path limits', but 'scenario type' exists NOWHERE else in the plan: no story adds a scenario-type field to ScenarioData, no curated per-type parameter registry, no editor picker, no serialization/validation of the type declaration itself (grep: the term appears only in FR-31's note and 8.5). The AC is unimplementable without inventing the parameter source — and if an implementer sources the limits from the untrusted scenario file itself, the relaxed check validates the file against numbers the file declares (circular), weakening the gate 8.5 is supposed to preserve. MP safety is mostly held elsewhere (7.6 same-gate-all-paths, AR-11 named caps, 8.5 AC3 keeps positions/spacing/bounds), so this is a thin-AC/undefined-dependency defect rather than a security hole.
- **GDD hybrid map-gen pipeline (procedural base + rule-based features + symmetry + LLM refinement) has no story; 8.5 ships pure one-shot LLM gen** *(extra:AI-assisted creation (Epic 8) — an entire epic no analyst swept)* — GDD §9 specifies AI-assisted map generation as a hybrid: procedural noise terrain base -> rule-based feature placement (resources, choke points, SYMMETRY for competitive maps) -> optional LLM refinement pass. The plan delivers only the existing one-shot LLM full-map generation, verified and de-clamped (8.5); 1.11's ProceduralMapGenerator was built as a determinism smoke-test sibling, not a creator-facing feature, and nothing connects it to the LLM path or adds rule-based feature placement/symmetry. Consequence: generated competitive maps have no balance/symmetry mechanism beyond the LLM's guess plus spacing checks — a quality delta on generated-map fairness, though manual map authoring (Epic 6) fully covers the WC3-editor bar.
- **No scenario briefing/description surface at match load — and 10.8c ships subtitles for briefings that have no owning surface** *(extra:Scenario objectives / quest-log surface (author → objectives.json → in-match display))* — Between clicking Play (skirmish, subscribed scenario, or the Mode Select 'Campaign & Tutorial (N/12)' entry from epics.md:1294) and first HUD frame, no story shows the player the scenario's name, description, or setup — there is no loading-screen or pre-match briefing story anywhere, so even the fallback carrier for objective/goal text (how WC3 uses its loading screen) is absent. Corroborating internal inconsistency: UX-DR43 / Story 10.8c ships a subtitle layer explicitly 'for briefings and unit voice' with an acknowledged 'build may ship no VO' escape hatch — the platform ships the caption system for briefings while no epic ever creates a briefing surface to caption. Reported minor from this domain's angle because the loading-screen absence is already flagged by another analyst and findings 1-2 carry the objective-display weight; it rises toward major if the Campaign & Tutorial menu entry ships pointing at mission content with no briefing frame.

---

## What IS Solidly Covered (calibration — 97 items)

- Full command vocabulary Move/Attack/Attack-Move/Patrol/Follow/Hold-distinct-from-Stop — Story 1.12, DONE with structural replay/live parity (OrderApplier)
- Formation movement, priority yielding, per-unit collision radius/push-yield, role-based front/back formations — Story 1.13, DONE
- Fog of war 3-state grid + minimap + spectator reveal (verify) and DG-9 high-ground vision bonus + sim elevation — Story 6.5
- Win-condition sim system + 4 turnkey presets (KotH/Timed Survival/Assassination/Landmark) for 1v1 — Story 7.10 (2-faction scope caveat reported separately)
- Damage/armor type matrix data-driven and extended to 5x6 with Hero types + single DamageResolver — Story 1.6, DONE
- Abilities: active/passive authoring, auras, on-hit, DoT/HoT, modifiers, energy costs, cooldowns, command-card casting — Epic 2 (2.1-2.6 DONE, deep ACs)
- Combat feedback 'juice': profile-driven flash/sound/shake/hit-freeze/death with per-unit/ability overrides — Story 2.7 DEV-DONE, presentation-only fence held
- Per-building production picker + Air production/category + anti-air/anti-building targeting + worker-cast + crystal costs — Stories 2.8/2.9a/2.9b (reachability program)
- Faction asymmetry with signature mechanics (Equal Exchange, Sanguine Furnace) wired and metric-validated — Stories 2.10/5.3/5.4/5.7
- Economy breadth: N-resource registry, sparse cost maps, GATHER/INCOME/STREAMING collection models, requires_structure, Crystal production, data-driven supply model — Stories 4.3/4.4/4.7
- Tech-tree prerequisite gating at runtime with cycle/referential lint + visual editor — Stories 4.2/4.6
- Building definitions data-driven + in-app editor — Stories 4.1/4.5
- Skirmish vs AI at 3 difficulties verified across all shipped maps with objective difficulty metrics — Story 10.1
- Faction balance via deterministic headless self-play harness to 45-55% — Stories 10.2a/10.2b
- Adaptive AI (rush/turtle pattern counters + counter-weighting + debug overlay) — Story 10.11 with strong determinism ACs
- MP core: LAN determinism gate (FR-39), server checksum quorum + HALT UX, merged authoritative tick packets, adaptive delay, freeze-and-continue disconnects, anti-spam throttle — Epic 1 (1.9a/b done) + Epic 9 (9.1-9.5, 9.13)
- Replays: v2 format, DSL-event record, viewable/shareable (FR-40), spectator demux — Stories 7.8b/9.3c/9.11
- Victory/defeat detection moved into deterministic sim (2-faction) — Story 7.10
- Performance pass 500-2000 units @60FPS/30Hz with reference machine + floor — Story 10.3
- Audio event wiring through existing AudioManager + settings buses (asset production itself out of scope) — Story 10.4
- In-match HUD hierarchy, context controls strip, own-faction selection, default keybindings + full remapping — Stories 10.10/10.8a
- Control groups assign/recall exist as-built (Ctrl+1-9 / 1-9) with binding verify in 10.10 (add-to-group/tab-cycling thinness reported separately)
- Death/cleanup, projectiles, splash, cooldown combat model — built foundation, golden-checksum pinned since 1.2
- Edit↔Play F5 round-trip ≤2s with defined match-state reset scope — Story 3.10
- Accessibility baseline: colorblind team colors, remappable keys, UI scaling, reduced motion, subtitles — Stories 10.8-10.8c
- Hotkey rebinding: full remap UI over InputMap with conflict detection, per-binding + reset-all, persistence (10.8a) and canonical default-binding verify (10.10/UX-DR66)
- Settings shell: Gameplay/Graphics/Audio/Controls/Accessibility tabs reachable from both branches (3.11/UX-DR73); Master/SFX/Music sliders wired + persisted (built, verified 10.4)
- Accessibility floor: colorblind team palette + filters + WCAG-AA/contrast-boost (10.8), UI scaling 80-150% + reduced-motion (10.8b-1), warm-paper light theme (10.8b-2), subtitles S/M/L (10.8c)
- Design system: single Godot Theme + full component kit (panel/btn/tooltip/dialog/toast/spinner/etc.) + demo gallery + tooltip-on-every-control mandate (3.1a/3.1b/3.1c, NFR-2, UX-DR53)
- Front-end restyle: Title screen + Mode Select + Settings overlay to the Theme with version footer (3.11/UX-DR67/68/73)
- MP lobby core: slots w/ faction select + colorblind dots/glyphs + ready pills + ping, lobby chat, version-hash and content-synced gates on Start (9.6/UX-DR69); stall banner (UX-DR28/9.4); desync -> terminal HALT with clear message (1.9a/UX-DR64e, already built)
- In-match HUD hierarchy verify/harden + selection rules + NFR-3 no-authoring-UI-leak acceptance gate (10.10/UX-DR71/61/63)
- Command card: ability buttons with cost/cooldown/affordability-disabled states (2.4), per-unit production picker incl. Air (2.8, DONE), worker build buttons with live cost+prereq labels (built)
- Minimap base: fog overlay + unit/building dots + click-to-pan (built; presence verified in 10.10)
- In-match chat overlay with system messages (built) and lobby chat (9.6)
- Edit<->Play F5 round-trip <=2s with a defined match-state reset scope (3.10/NFR-1)
- Hero Save/Load picker with Deploy/Overwrite/Delete + confirm dialogs (3.9/UX-DR75) and online attested rail (9.12)
- Publish flow with proof-of-play + quality gate + IP consent (9.7/9.8) and mod.io content browser browse/search/subscribe/rate (9.10)
- 'Your First Scenario' guided onboarding <15min (5.8/NFR-2)
- Skirmish AI-difficulty application + (map x difficulty) pass/fail matrix verification (10.1)
- Score summary exists as-built: victory/defeat card with kills / units built / ore mined / duration (STATUS.md P2.6)
- Terrain sculpt brush (raise/lower/smooth/flatten ≈ plateau) verify+harden — Story 6.1 (epics.md:1664-1682); only a 'noise' brush is absent (negligible delta)
- Terrain texture painting with 4 blended layers + persistence — Stories 6.1 + 6.2 fix the headline sculpt/paint save-load defect incl. .chimera.zip packaging round-trip (epics.md:1684-1702)
- Terrain stroke undo/redo on the shared editor history + _store_undo error cleanup — Story 6.3 (epics.md:1704-1720)
- Entity/building/resource-node/start-position placement with ghost preview, grid-snap (G), right-click/Esc cancel, delete-undo, and full save/load round-trip of positions/owners/types — Story 6.4 (epics.md:1722-1740), brownfield EntityPlacer verified in STATUS.md:141-145
- Playtest-from-editor (WC3 'Test Map') — Story 3.10 F5 Edit↔Play toggle, ≤2s round-trip, defined match-state reset scope (epics.md:1266-1280, UX-DR62/83)
- Sim-side terrain elevation + height-advantage vision toggle + fog-of-war verify (DG-9/FR-61) — Story 6.5 with Fixed sampling, checksum fold, edge clamps (epics.md:1742-1764)
- Win-condition authoring — 6.4 panel round-trip + Story 7.10 expands to 6 options (2 built-in + KotH/Timed Survival/Assassination/Landmark presets) with load-time parameter validation (epics.md:1970-1992)
- Resource-node configuration (supply/rate/per-slot start ore) — 6.4 spinners + Story 4.7 (DG-3) adds INCOME/STREAMING collection models
- Map file versioning/migration for shared maps — AR-24 schema_version+migration registry and AR-25 min_game_version enforcement, homed in Story 7.6 and Epic 4 stories (epics.md:209-210, 1888-1904)
- Map packaging/export/import + publish quality gate — as-built Export/Import buttons (STATUS.md:245) + Epic 9 mod.io publish with thumbnail/description gate (AR-30, Story 9.x:2337-2342)
- Trigger persistence/round-trip through one canonical graph IR incl. lossless flat-format migration — Stories 7.1b-1/7.1b-2/7.6 (epics.md:1800-1812, 1888-1904)
- Editor camera zoom/pan/orbit + remappable editor hotkeys — as-built RtsCameraController + UX-DR66 keybinding story (epics.md:2724-2730)
- Fast edit→play loop quality bar (NFR-1, no restart, ≤ a few seconds) explicitly storied and AC'd (3.10)
- Trigger determinism + server validation (FR-25/27): 7.1a fixes the as-built nondeterminisms, 7.6 is a genuinely authoritative fail-closed load gate on every path with canonical hashing — stronger than anything WC3 has
- Typed scoped variables incl. arrays + deterministic named timers (7.2, 7.5) — WC3 variable/array parity met
- Condition composition AND/OR/NOT with typed Fixed expressions, load-time type-check and div-zero rejection (7.3)
- Custom events with typed params, acyclic same-tick dispatch, run-once/cooldown, next-tick feedback loops (7.4) — exceeds WC3's run-trigger model
- Bounded ForEach/ForEachBatched loops + fuel seatbelt (7.5) — safe WC3-loop parity with provable termination
- Custom runtime UI (FR-26): closed widget tree (Panel/Label/Counter/ProgressBar/Button/Timer/Leaderboard/FloatingText/ItemList) on a read rail + lockstep-authorized Button write rail + replay v2 (7.7/7.8a/7.8b) — covers WC3 dialogs/leaderboards/multiboards/timer windows
- T3 visual node-graph editor over the single shared IR with persistent-id round-trip and hash-excluded layout annotations (7.9); four-tier parity is realistically scoped BECAUSE it was honestly relaxed (T2 gets a read-only graph fallback; one IR + one validator keeps T4 NL safe) — residual risk is 7.2-7.5 story density, not design
- Win-condition authoring: sim-layer WinConditionSystem + 4 turnkey presets + parameter validation + expanded picker (7.10) — for the 2-faction case
- display_message and play_sound trigger actions exist as-built and migrate losslessly onto the IR (7.1b-2)
- Unit Card Editor breadth: stats/combat/archetype/model preview/duplicate/delete, inline fail-closed validation, undo, simple-advanced disclosure + raw-JSON escape hatch (3.3-3.6), plus delivery/projectile-speed authoring (3.12)
- Hero AUTHORING + persistence rails: Promote-to-Hero fields, persistence manifest through the validate gate, hero-picker save/load as deterministic init state, online server-validated rail (3.7-3.9, 9.12) — authoring/persistence side is solid (the runtime XP gap is the finding)
- Building editor + visual drag-wire tech-tree editor with cycle/referential lint (4.1/4.2/4.5/4.6) — WC3 techtree-requirement parity for buildings
- Economy authoring beyond WC3: N-resource registry with sparse cost maps, per-scenario supply model incl. disable, GATHER/INCOME/STREAMING collection models (4.3/4.4/4.7)
- Faction Definer wizard with validator-gated save, AI-preset assignment, ≤12-min target, and instant selectability in playtest/skirmish; broken factions excluded from the list (5.5a/5.5b/5.6); lobby faction select per slot at N≤4 (9.6, UX-DR69)
- AI-assisted authoring (Epic 8): trigger/map/unit/ability/hero/faction drafts + balance analysis all forced through the same validation gate with float→Fixed quantize, four-state graceful degrade — the T4 tier is properly fenced
- Trigger enable/disable/run-once/cooldown/priority at authoring time + Edit↔Play F5 loop ≤2s with defined reset scope (7.2, 3.10) — the core iteration loop is well specified
- Desync detection/attribution: server-side majority-vote checksum collector, fail-closed HALT, minority naming (9.1; 1.9a/1.9b already DONE and live-verified)
- Adaptive input delay: server-dictated, ACK-gated, [2,12] re-clamp incl. forged-proposal defense, golden-gated (9.4; DelayMath already Tier-1-tested via 1.11)
- Match-start sync: PROTOCOL_VERSION rejection + {scenarioHash, rulesetHash, startStateHash} fail-closed gates, hash==0 hard reject (9.4/AR-18)
- N<=4 scale-up with tamper-resistant server-built merged tick packet, faction re-stamp from transport slot, drop-not-clamp ceilings, N=2 golden regression lock (9.2, 9.3a-c)
- Deterministic disconnect freeze-and-continue floor with a 300+-tick mid-match-drop desync test (9.5) — the sim side of drops is solid
- Command rate-limiting DG-6: per-slot token bucket, escalating penalty, false-positive-proof AC at the legitimate ceiling, flood-vs-no-flood golden equality (9.13) — unusually rigorous
- Lobby UI fundamentals: slots/ready pills/ping/faction select/colorblind glyphs/lobby chat, Start gated on all-ready + version-match (9.6, UX-DR64a-c/69)
- Spectators: demux rewritten onto the authoritative merged output, chat faction spoof fixed, lobby slot allocation acknowledged (9.3c, 9.6)
- In-match chat exists as-built (MatchChatOverlay, system messages) with spoof re-stamp (9.3c) and anti-spam (9.13) planned
- Replay FORMAT integrity: v2 version gate, embedded canonical scenarioHash + algo-version, scenario re-gate on playback, byte-identical double-playback (9.11)
- mod.io publish loop: proof-of-play signed token (tamper/edit invalidation), min-quality gate, explicit IP-ownership consent, end-to-end upload AC (9.7/9.8)
- Download integrity + runtime asset ingest: full content hash folds asset bytes, GLTFDocument runtime ingest with box-placeholder fail-safe (9.9)
- Content browser: browse/search/tag/sort/subscribe/rate delegated to mod.io-native, auth prompt, download wired to 9.9 verification (9.10)
- Online hero persistence FR-7c: Nakama storage Owner-Read/No-Client-Write, validating server RPC only, server attestation gating StartGame (9.12) — anti-tamper posture is right
- Basic victory/defeat overlay with winner, duration, per-faction kill tally already exists as-built (STATUS.md P2.4 'Victory / defeat screen')
- Sim determinism QA is exemplary: golden-checksum harness, 556 Tier-1 tests, CI gate on every push, cross-platform WSL leg, banned-API/AOT analyzers, fold-timing discipline, and inject-violation teeth-proofs (Epic 1 + retro action items)
- Presentation interpolation between 30Hz sim ticks and 60FPS render is built and verified (~350 FPS @ 1000 units, STATUS P0.1 / epics.md:42) — no sim-tick visual stutter risk
- Combat feedback 'juice' is deep and shipped: 2.7 profile-driven per-unit/per-ability flash, impact/death sounds, screen shake, hit-freeze, plus the AbilityCast contract for 2.10
- Story 10.3's perf AC is well-formed for what it covers: named reference machine, 500/1000/2000 tiers, explicit ≥30FPS floor, sim-vs-render budget split, community-scenario case, and a determinism re-check after optimization
- Accessibility baseline (FR-51) has unusually deep AC coverage across four slices: colorblind palettes per vision type, WCAG AA audits per screen, full keybind remap with conflict detection, UI scaling 80-150% at 3 resolutions, reduced-motion with defined target values, light theme, subtitles S/M/L
- Creator onboarding and discoverability are owned: 5.8 'Your First Scenario' <15-min gate, NFR-2 tooltip-on-every-control (UX-DR26/45/53), and the 3.1c component gallery
- Edit↔Play round-trip (NFR-1) has a real story (3.10) with a ≤2s budget and a defined match-state reset scope
- Desync UX is handled: HALT overlay, server quorum verdicts, majority-vote attribution (9.1), and stall banner — a desync ends loudly, never silently diverges
- Content/save format versioning is owned: D3 canonical-model hash + versioning/migration (Epic 7), replay v2 with v1 hard-reject (7.8b/9.11), versioned SettingsData (8.2)
- In-match HUD, default keybindings, selection rules, and the NFR-3 no-authoring-UI-in-play gate have a dedicated verify/harden story (10.10)
- Audio infrastructure (buses, settings persistence, graceful missing-asset silence, pooled players) is built and 10.4 verifies wiring not just assets (music-bus test-stream note)
- Balance measurement is reproducible by construction: 10.2a deterministic headless self-play with seed-stable batches and side-alternation before 10.2b tunes data-only

