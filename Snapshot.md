---
project: Project Chimera
last_touched: 2026-08-07
phase: Phase 5 — Polish & 1.0
status: Active
---

# Project Chimera — Snapshot

**Last Touched:** `2026-08-07`

## Current Phase
**Phase 5 — Polish & 1.0** (Months 25-31 of GDD roadmap)

Phases 0–4 are code-complete. Phase 5 is underway. Session 20 shipped worker-placed buildings + UI bug sweep. Session 21 (remote, away from computer) shipped Utility AI + Adaptive Input Delay.

## Next Action
**Run `bmad-loop run --epic 15`.** The loop is stopped, the tree is clean and 15-13 is `done`, so a fresh run picks up **15-14 — host-side hero identity enforcement / attested deployment (DW-200)**, the largest remaining Epic-15 item. The other two open keys are **15-21** (creator-authorable hero attribute system — folds BOTH `SimChecksum` and `CanonicalModelHash`, so it re-records at the end of its own story per the batch rule and must not be queued behind anything) and **15-23** (generation-validated entity references, DW-775). 15-1 stays `blocked` by Alec's deferral.

**The in-engine gate is no longer a blocker for Godot-coupled stories** — the DW-882 seam landed 2026-08-07, so a dev session can drive and observe a real match. Two operating notes for whoever runs it: freeze game time before asserting sim state, and the bridge is still single-client, so close idle Claude sessions before an in-engine run.

Read the **2026-08-07** block below first.

<details><summary>Superseded Next Action (Story 15-3 — now done)</summary>

**Run Story 15-3 — status effects become real + modifier-period honesty** (`bmad-loop run --story 15-3`; DW-266, 267, 270, 271, 278, 323). `StatusFlags` is written by the ability system and read by *nothing*: Disarmed, Rooted, Stunned, Silenced and Invulnerable are all authorable today and all do nothing in play. **It moves goldens by design** (the StatusFlags re-baseline) and re-records at the end of its own story per the standing batch rule — do not queue it behind a batch. Decision **DW-325 is already ruled *build***: a modifier collapsing `EffectiveMaxHealth` to 0 raises death rather than pinning a 0-HP "zombie"; that rides the same re-record. It is Godot-coupled (the `Warnings` channel surfaces in the 2.5 ability editor), so it needs the in-engine gate and the MCP bridge free.

Read the **2026-08-06 (later)** block below first — the earlier same-day block's numbers (AlgoVersion 23, 370 open) are superseded. Both precede the 2026-08-01 block, and all three precede the legacy sections, which describe Sessions 20–21 and are far out of date.

</details>

---

*Session type: bmad (prescribed workflow in active execution)*

---

## Current State (2026-08-07) — read this first

**Headline: the in-engine gate stopped being a wall.** Story 15-13 escalated CRITICAL because its diff touched
`src/UI/**` and the behaviour was unobservable over the GDScript-only bridge (DW-882). Alec's call was to BUILD the
seam rather than waive the gate — so `godot/src/Core/MainSceneDebugSeam.cs` landed, the gate was driven and observed
for real, and every future Godot-coupled story inherits the capability.

**Shipped (7 commits, `a6fca507` → `0edc863e`):**
- **Story 15-13 `done`** — the closed effect vocabulary is complete: `TeleportEffect` plus the three checksum-neutral
  presentation leaves, wired through the converter, an explicit `CanonicalFold` arm per kind, the validator and the
  closedness/fold/position guards. No golden re-recorded, no hash `AlgoVersion` moved.
- **DW-882 `done`** — the debug seam: `_mcp_state()`/`DebugSimJson`/`DebugEntityJson` (raw `Fixed`) to READ,
  `DebugCastGround`/`DebugCastTarget` to DRIVE real orders through the production path, `DebugSpawnUnit`/
  `DebugGrantAbility`/`DebugSetHealth`/`DebugSetEnergy` to set up. Debug-build-only and offline-only. Presentation
  taps on `CombatFeedbackBridge` and `AudioManager` report cue drains, flash spawns, shake values and sound routing.
- **Ten bugs from Alec's live editor session** — DW-893…DW-901 filed, then all closed, plus DW-903 and DW-904 found
  while fixing them. All ten VERIFIED BY ALEC in live use.
- **DW-903 was a live crash I had shipped**: 15-13's four leaves were never taught to the ability composer, whose
  converter throws on an unknown node — so opening `blink_strike` took the Ability Editor down.

**Suite: 6340 passed, 0 failed, 1 skipped** (the pre-existing reserved-story skip). Ledger: 904 entries.

**Three lessons worth carrying:**
1. **Three of four ledger hypotheses were WRONG** and a parallel read-only investigation caught all three (the Map Gen
   "busy flag" was really a layout blow-out pushing the ✕ off-screen; Esc was not "lazily armed by Play" but eaten by a
   placer that boots armed; DW-901's `location:` named the wrong directory). Root-cause by reading, not by plausibility.
2. **A closed-vocabulary addition has FIVE consumers, not four.** Sim, JSON converter, canonical fold and validator all
   knew about the new leaves; the authoring composer did not, and it fails at RUNTIME — no build and no Tier-1 run
   catches it. That is DW-903.
3. **A placement ghost is a promise.** Its whole transform must derive from the same rule as the renderer that will
   draw the object. Adding a shadow did not create DW-904, it exposed a misalignment that had been there all along.

**Deliberately NOT done, filed:** DW-902 (the TOOL-tier hint strip still has no table — the reason it has drifted
twice), and widening the over-UI guard in the four paint tools (there the failure mode is unsafe: a wrong guard
silently stops terrain/path painting).

---

## Current State (2026-08-06, later)

Supersedes the earlier 2026-08-06 block below, which was written *before* 15-22 and 15-2 ran. Its ledger counts and AlgoVersion are stale; its correct-course reasoning still stands.

**Master is `3762bd9f`, pushed, tree clean.**

**Ledger:** 878 numbered entries · **387 open** · 484 done.

**Determinism:** `SimChecksum.AlgoVersion` **24** (was 23) · `CanonicalModelHash` 14 · `StartStateHash` 2.

**Tier-1:** **6202 passed / 0 failed / 1 skipped** on Windows. Up from the 6158 Phase C baseline because 15-2 added ~44 tests — use 6202 as the baseline for the next run, not 6158.

**Two stories closed since the earlier block:**
- **15-22 (Phase C batched re-baseline)** — merged as `d973c021`, AlgoVersion 23→24, 17 entries closed, both halt gates held. Post-merge review filed DW-868..874.
- **15-2 (map-size determinism unification + raw heightmap read)** — DW-160/146/162 closed. The `get_height` bilinear blend is gone (now `get_pixel` nearest). Review filed DW-875..878, all Route-C/terrain follow-ons and **all unreachable on shipped content** (every scenario is `terrain_ref:""` flat, so the sculpted path early-returns). No action until sculpted terrain ships.

**DW-874 ANSWERED (Alec, this session) — keep ONE AlgoVersion constant, save gate stays fail-closed.** A pure golden re-record marker does *not* get a separate save-only world-format version; every bump remains a save-break. Reason: while the sim changes every epic, a save that silently *resumes* under corrected combat/AI rules is worse than one that refuses to open. Revisit before 1.0 ships to players. The gate's message no longer claims "simulation format changed" — it names the actual version pairs. Recorded at `SaveGameFile.Read`'s version gate and in `SimChecksum`'s v24 note.

**Epic 15 position:** done = 15-2, 15-15, 15-22. Remaining backlog in file order = **15-1, 15-3, 15-10, 15-11, 15-12, 15-13, 15-14, 15-21, 15-23**. An unattended epic run starts on 15-1 (MP reconnect v1), *not* 15-3 — pass `--story 15-3` explicitly to open there. The one ordering constraint (15-12 before 15-21) is already satisfied by file order.

### ⚠️ Do not run bmad-loop and `chimera-dw-burndown` concurrently without isolating them

Established this session by reading both configs; not previously recorded.

`.bmad-loop/policy.toml` has `isolation = "none"` and `target_branch = ""` — **bmad-loop commits directly to master, in place, in the main checkout.** `dw-burndown.workflow.js` defaults to `integrationPath = D:/Projects/Project_Chimera` and `integrationBranch = 'master'` — **the same tree and the same branch.** Run both as-is and the burn-down's merge/ledger/review phases commit under a live dev session, reproducing the known `manual recovery needed (committed work present)` false-baseline pause.

To run them together, pass `integrationPath` (a dedicated worktree) + `integrationBranch` (non-master) — the option exists for exactly this, and the auto-mode safety classifier already refused merge-to-master on 2026-08-05. Also required: `baseSha` pinned (step 0 does `git reset --hard ${BASE || 'master'}`, so without it the fleet anchors to a *moving* master), `baselineTests` current, and `chunkSize` reduced to 2 — both systems run full Tier-1 suites and the 4-way chunk budget does not account for bmad-loop, which makes the `CanonicalModelHashPerf` CPU-contention flake more likely in both. Even isolated, **never let both waves move goldens** — two independent re-records converging at merge-back fights the batch rule. *Decision 2026-08-06: not worth it; Epic 15 runs on bmad-loop alone.*

**The godot-mcp bridge is single-client and an idle Claude session grabs port 6550 at startup without ever calling a tool.** Confirmed again this session by parent-chain walk (`Get-NetTCPConnection -RemotePort 6550` → pid → `claude.exe`). Close or release idle sessions before any in-engine-gated run.

---

## Current State (2026-08-06, earlier) — superseded by the block above

**Live trackers:** `_bmad-output/implementation-artifacts/sprint-status.yaml` and `deferred-work.md`. Everything below the 2026-08-01 block is legacy history, not guidance.

**Ledger:** 839 numbered entries · **370 open** · 462 done · 0 flat.

**Determinism:** `SimChecksum.AlgoVersion` **23** (was 22) · `CanonicalModelHash` 14 · `StartStateHash` 2.

**Phase B — the batched golden re-baseline — is DONE and committed on branch `rebaseline/phase-b` (`5b1faad1`), gated 6050 passed / 0 failed / 1 skipped on Windows. NOT merged.** Recorded on Windows, so `ai-active` carries a current v23 header instead of going stale.

**⚠️ This REVERSES the 2026-08-01 instruction below ("isolate each re-baseline … do NOT batch them").** Batching was the right call and is now the standing approach: one re-record, one AlgoVersion bump, all golden-moving work landed together. What made it safe was making the fold **bounded** — see below.

**What landed:** the v23 gather fold (DW-78), the `FixedVec3.SqrMagnitude` int32-overflow root fix (DW-688/764/737), and two dead-guard deletions (DW-783/738 — AI waves marched at a hardcoded (−45,0,0) on every map; DW-680 — rally points had never worked for combat units). 7 entries closed; DW-837/838/839 filed.

**Three things worth carrying forward:**
1. **Always measure a bounded fold before accepting an unconditional one.** Skipping the Mix calls entirely for entities at default (the v21 `TriggerEnabledStore` posture) cut the blast radius from 28 golden files to 5, with identical desync coverage.
2. **The re-baseline differential guard had been silently defeated since story 11.6** — its "never re-recorded" control was overwritten when it correctly fired, leaving it byte-identical to `golden-scenario.golden.txt` (proven by matching md5). It is now rebuilt on a gather-free control with a byte-pin so a re-freeze goes RED. **When a deliberate halt gate fires, never re-freeze the control.**
3. **After a record run, every golden shows as modified** — the recorder rewrites the `checksum_algo_version` header. Diff with `grep -v '^#'` to see real movement. And use `--logger trx`: the console test logger truncates its failure list (it showed 6 of 26).

### Correct-course 2026-08-06 — Epic 15 re-shaped, Phase C packaged

`planning-artifacts/sprint-change-proposal-2026-08-06.md`. Approved by Alec; **all artifact edits applied.**

**Epic 15: 21 story keys → 11** (10 actionable + 1 done record). Eleven were thematic multi-DW **sweep containers** — a theme name plus a bundle list, no named deliverable. The burn-down executes DW bundles and closes ledger ids; it never executes stories, so nothing ever wrote to them (zero `spec-15-*.md` were ever written; six `spec-dw-*.md` were). Those eleven (15.4–15.9, 15.16–15.20) are **retired**, and `deferred-work.md` is now the single tracker for burn-down work. **Not deleted wholesale** — eight of the 21 are real feature stories (15.1/15.10/15.21 carry no DW ids *by design*) whose design content exists nowhere else. `epics.md` keeps every retired section marked SUPERSEDED, not deleted. 15.14 is **kept but re-scoped** to DW-200 alone (bundles released); 15.15 stays as a done record.

**Batch rule, now standing:** a re-baseline batch takes **bounded corrections only**. You are amortising a ~10-minute re-record; coupling it to a multi-week feature build keeps the branch open and queues every other golden-moving fix behind it. **Feature stories that move goldens re-record at the end of their own story** (15.2, 15.11, 15.12, 15.14, 15.21).

**Story 15-22 = Phase C**, `AlgoVersion` 23 → 24, **18 entries closed**:
- **14 bounded corrections:** DW-512, 548, 549, 570, 647, 658, 659, 664, 674, 678, 766, 775, 803, **838**.
- **1 answered ruling:** **DW-837 — total wipeout always loses, any faction count** (delete the `ActiveCount < 3` guard at `WinConditionSystem.cs:343`), overriding the Story 7.11 parity concern.
- **3 in-window riders:** DW-514 (shipped-content residue → `CanonicalModelHash`), DW-554 (edits a golden, doesn't move one), DW-839 (comment fix, free).

**Corrections to the old leftover list above:** **+DW-838** (Phase B filed and deferred it), **+DW-146** (the float→Fixed elevation grid — the actual determinism risk of the 15.2 trio, previously missing). **−DW-160/162** → 15.2: DW-160 changes the pathability persist format, invalidating every stored `pathability_blocked` and moving `CanonicalModelHash` *and* `StartStateHash`. **−DW-265** → 15.12/15.21 (feature build). **−DW-346** → 15.17 (fuel accounting; verify before assuming movement).

**DW-272 answered — creator-authored, not an engine ruling.** Alec rejected the multiply/repeat/cap choice: creators pick the periodic-stacking mode, with a system cap as a runaway protector. The default must preserve today's non-scaling pulse byte-for-byte, so **no shipped golden moves** — it becomes a build on Story 15.12, not batch material.

**New ledger field: `goldens: moves | none | verify | …`** on 26 entries. §2.4 of the proposal showed golden-moving status was unrecoverable from the ledger — querying it returned 16 entries, the session's own list had 21, and neither was derivable from the other. That is why Phase B's leftovers needed a hand reconstruction. Populate this field on any new golden-moving entry or Phase D repeats the archaeology.

**Also corrected:** `sprint-status.yaml` action item **A7-E9** claimed the map-size decision was "UNDECIDED for a 3rd epic" — stale. Route C landed; `ScenarioValidator.cs:148` enforces the clamp, but `border_extent` exists nowhere in `godot/src`. True state: **decided, partially built**; the remainder is Story 15.2's scope, and 15.2 is unblocked.

**`bmad-sprint-planning` must NOT regenerate `sprint-status.yaml`** — the file does not strictly parse as YAML, it carries ~270 lines of irreplaceable hand-written reconciliation and action-item state, and the 2026-08-04 mechanical `backlog`→`in-progress` flip made 15 stories invisible to the loop and had to be reverted. Hand-edit as text; verify with the read-only `bmad-sprint-status` and `bmad-loop status`.

---

## Current State (2026-08-01)

**Everything below this block is legacy** (Sessions 20–21, worker construction, Utility AI smoke tests). It is retained for history, not as guidance. The live trackers are `_bmad-output/implementation-artifacts/sprint-status.yaml` and `deferred-work.md`.

**Position:** Epics 1–11 done. Epic 11 retro complete (`epic-11-retro-2026-07-30.md`). **Epic 15 (Deferred-Work Burn-Down & MP Reconnect) is the current epic**, re-planned 2026-07-30 from 13 → 20 stories.

**Ledger:** 487 numbered entries · 305 open · 175 done · **0 flat**. Every entry is sweepable — action item A1-E11 migrated 160 flat appender bullets (invisible to triage since Epic 7) to DW-325..DW-484 and patched all six appender sites so new defers are born canonical.

**Determinism:** `SimChecksum.AlgoVersion` 22 · `CanonicalModelHash` 14 · `StartStateHash` 2. Goldens will move in stories 15-2, 15-3, 15-4 and 15-16 — isolate each re-baseline per the checksum-fold timing rule, do NOT batch them. **(SUPERSEDED 2026-08-06 — batching proved correct; see the 2026-08-06 block above.)**

**Sweep cadence proven.** Run `20260731-012409-44f9` landed 3/3 bundles clean (0 deferred, 0 escalated, 17.78M weighted). The in-engine gate DOES fire on sweep tasks and produced real artifact blocks. Two fixes shipped from it (`41e8061`): the gate fact now binds sweep bundles and not just "stories", and `max_dev_attempts` is 2 → 3 as insurance. **Watch for `attempt=1` on Godot-coupled bundles next cycle — that is the signal the instruction fix worked, and the cue to drop `max_dev_attempts` back to 2.**

**Before any bmad-loop run:** close idle Claude sessions. The godot-mcp bridge on 127.0.0.1:6550 accepts ONE client, and an idle session grabs it at startup without ever calling a tool — that starves dev agents into a 127 ENV_FAULT operator pause. Check with `Get-NetTCPConnection -RemotePort 6550`.

**Known stale pointer:** ~~`CLAUDE.md` tells each session to read `CONTEXT.md`, but that file is deprecated and redirects here.~~ **FIXED 2026-08-06** — `CLAUDE.md` now points at `Snapshot.md`, the ledger and `sprint-status.yaml`; `CONTEXT.md`/`STATUS.md`/`LEARNINGS.md` were deleted from the repo (archived in the vault, and in git history).

---

## Needs Testing — Written This Session

### ✅ Utility AI (`src/AI/AiOpponentSystem.cs`)

**VERIFIED 2026-06-20 (in-engine, alpha_map_01/Normal, ~290s game time, frozen-step):** All 4 deadlock-fix ACs PASS. P1 ore 200→540→660→900→1060 (monotonic rise); P2 gathered + reinvested (nodes 8→6, army 2→5→7→10→14, no solo-trickle); AI teched CC→Barracks→ArcheryRange (buildings 3→4); P2 wave reached P1 base and eliminated both P1 workers (P1 units 2→0) ~tick 5760–8700; **5 distinct sim hashes** (0x104E51CE→0xF2F66B7A→0x56FA1DEA→0x1774681A→0x5D07F97A — no fixed point); 0 errors. Earlier 2026-06-09 /godot-verify FAIL (deadlock) is resolved by `e3e48bc`. Not exercised this run: SiegeWorkshop tier, supply-expansion CC, Easy/Hard difficulty deltas, destroyed-Barracks recovery.

Full replacement of the rigid 3-phase FSM with utility scoring. All public API unchanged — `MainScene` needs no changes.

**Smoke test (single machine, Play mode):**
- [ ] Open any skirmish map in Play mode. Watch the P2 AI.
- [ ] **Early game**: AI should build a Barracks within ~20s of having 100 ore.
- [ ] **Tech progression**: after the Barracks is complete, AI should eventually build an ArcheryRange (requires Barracks complete), then SiegeWorkshop (requires ArcheryRange complete). Watch Godot Output for `[Lockstep]` / AI build logs to confirm order.
- [ ] **Supply expansion**: when AI supply headroom ≤ 4, it should build a CommandCenter before queuing more units (score 0.95 = highest priority).
- [ ] **Double production**: after the expansion CC is complete, AI should build a second Barracks.
- [ ] **Attack waves**: P2 combat units should periodically attack-move toward P1 base. Easy = fewer waves (threshold 8), Hard = more frequent (threshold 3).
- [ ] **Scenario pre-placed buildings**: load `map_06_contested_peaks` (pre-placed Barracks). AI should immediately train from it — verify units appear without AI needing to build its own Barracks first.
- [ ] **Destroyed Barracks recovery**: destroy P2's Barracks in-game. AI should score `BuildBarracks = 0.85` and rebuild without getting stuck in a reset loop.

**Difficulty smoke test:**
- [ ] Set `AiLevel = Easy` in Inspector → AI attacks late, small waves.
- [ ] Set `AiLevel = Hard` → AI teches up fast, attacks early and often.

---

### ✅ Adaptive Input Delay (`src/Multiplayer/LockstepManager.cs` + `NetworkCommand.cs`)

RTT measurement via Ping/Pong + negotiated delay changes via DelayProposal packets. `INPUT_DELAY = 4` is still the starting value; the constant is preserved for documentation.

**Build check:**
- [ ] `dotnet build` — 0 errors, 0 new warnings.

**Offline smoke test (single machine):**
- [ ] Launch game in Play mode (offline). No pings should be sent (only fires when `IsOnline`). No errors in Output.

**LAN smoke test (two machines required — do alongside P2.4 LAN test):**
- [ ] Host + join on LAN. Watch Godot Output on both machines.
- [ ] Within 2s of match start: both machines should log `[Lockstep] RTT sample: Xms` and a smoothed RTT.
- [ ] On LAN (~1-5ms RTT): target delay = `ceil(2.5ms / 33ms) + 1 = 2`. Both machines should log `[Lockstep] Delay: 4 → 2 ticks` within ~5s.
- [ ] Play for 300+ ticks. Checksums must stay in sync (same HUD hash on both machines). The delay reduction must NOT cause desync.
- [ ] Optionally: to test high-latency path, add artificial latency (e.g. `tc netem` on Linux) and verify delay increases toward MAX_DELAY=12.

**HUD wiring (optional, low priority):**
- The `CurrentDelay` property is now public. You can display it in the HUD stall indicator: e.g. `"Delay: {_lockstep.CurrentDelay} ticks"` alongside the "Waiting for peer…" banner. Not required for correctness — just a nice debug display.

---

## What's In Progress
- Utility AI + Adaptive Input Delay (written, needs smoke test — see checklist)
- LLM Trigger System (written session 22, needs smoke test — see checklist below)
- AI Map Generator (written session 23, needs smoke test — see checklist below)

### /godot-verify results (2026-06-09 — automated, full report: `D:\Brain\Reports\godot-checks\Project_Chimera-2026-06-09\verify-report.md`)
- **LLM Trigger System: PASS (core).** Panel opens, generator section works, no-API-key path fails gracefully ("Ollama unreachable" — message differs from spec'd "Both Claude and Ollama are unavailable"). Inline triggers verified in Play mode: match_start→add_resources (ore 200→700 tick 1) and create_timer→display_message (toast at ~5s) both fired. Not verified: unit_dies→spawn_unit, Validate() rejection, physical L key.
- **AI Map Generator: PASS (core).** Main-menu button enters Edit + toggles panel; panel renders left side; auto-hides on Play mode. Not verified: Load/Save flows + 7-pass validation (need API key or Ollama), physical M key.
- **Utility AI: FAIL — match deadlocks.** Barracks built fast (tick 45 ✓), but on Normal/alpha_map_01: a single early P2 unit killed both P1 workers, P2 income flatlined (25 ore, sim hash identical across ticks 1680→3180), no tech progression, no further attack waves. Needs investigation: worker gathering stops after AI build/train; no AI recovery path with no workers + <50 ore.
- **Adaptive delay (offline only): no errors observed** in ~110s offline play. LAN test still pending.
- Cosmetic: long status text stretches both AI panels across the screen (no autowrap/max width); possible shortcut leak (Grid Snap toggled while typing "G" in a text field — may be synthetic-input artifact, recheck manually).

---

### ✅ LLM Trigger System (session 22)

**New files:**
- `src/Core/Definitions/TriggerDefinition.cs` — JSON data model (events, conditions, actions)
- `src/Core/ScenarioDirector.cs` — ISimSystem; evaluates triggers every tick; runs last in sim loop
- `src/AI/LLMService.cs` — Claude API (+ Ollama fallback) + 5-pass validation pipeline
- `src/CreationSuite/TriggerEditorPanel.cs` — Edit-mode UI panel (L key toggle)

**Changes:** `ScenarioData.cs` +`Triggers[]`, `MainScene.cs` wired.

**Smoke test (single machine, Edit mode):**
- [ ] Open any map in Edit mode, press **L** → TriggerEditorPanel should open on the right side.
- [ ] Click "**+ New Trigger (via AI)**" → generator section appears.
- [ ] **(No API key):** Type any description and click Generate → status shows "Both Claude and Ollama are unavailable." or an Ollama response if Ollama is running locally.
- [ ] **(With API key):** Set `AnthropicApiKey` in Godot Inspector on MainScene → Generate produces a JSON preview → Accept adds the trigger to the list.
- [ ] **Inline trigger test (no API needed):** Add a trigger JSON manually to a scenario file (e.g. `alpha_map_01.json`), reload, enter Play mode:
  - match_start event → add_resources action → P1 ore should jump by the specified amount on tick 1.
  - create_timer (5s) → display_message action → toast label should appear after 150 ticks (~5 seconds).
  - unit_dies event → spawn_unit action → new units should appear at the specified position.
- [ ] **Validation test:** Manually craft invalid JSON (faction=5, count=200, bad operator) → `LLMService.Validate()` should reject with a clear message.

---

---

### ✅ AI Map Generator (session 23)

**New files:**
- `src/CreationSuite/MapGeneratorPanel.cs` — Edit-mode panel (M key toggle, CanvasLayer layer=13, left side)

**Changed files:**
- `src/AI/LLMService.cs` — `MapGeneratorContext` class, `GenerateScenarioAsync()`, `ValidateScenario()` (7-pass), `BuildMapSystemPrompt()`, `CancelScenario()`. `TryClaudeAsync`/`TryOllamaAsync` refactored to accept full `userMessage` string.
- `src/UI/MainMenuOverlay.cs` — `OnGenerateMap` event + "Generate Map (AI)" button (after Browse).
- `src/Core/MainScene.cs` — `_mapGenPanel` field, `_pendingGeneratedScenario` static field, `SetupMapGenerator()` after `SetupTriggerEditor()`, `LoadGeneratedScenario(ScenarioData)`, M key in `_UnhandledInput`, `_mapGenPanel.Update()` in `_Process`, `_mainMenu.OnGenerateMap` wired, `LoadAndApplyScenario()` checks `_pendingGeneratedScenario` before disk load.

**How it works:**
1. Press **M** in Edit mode (or click "Generate Map (AI)" in main menu) → `MapGeneratorPanel` opens (left side).
2. Type a map brief → **Generate ✦** → Claude API (or Ollama fallback) generates `ScenarioData` JSON.
3. 7-pass validation: schema → player slots (faction paths forced) → building types → unit IDs → position bounds → ore spacing ≥15u → ≤6 combat units per faction.
4. Preview shows: name, win condition, bounds, node/building/unit counts.
5. **↗ Load (no save)**: sets `_pendingGeneratedScenario` static field → `GetTree().ReloadCurrentScene()` → `LoadAndApplyScenario` reads the static field (no disk write).
6. **💾 Save & Load**: writes to `res://resources/data/scenarios/ai_generated.json` first, then same reload.

**Smoke test (single machine, Edit mode):**
- [ ] Open any map in Edit mode, press **M** → `MapGeneratorPanel` should open on the left side.
- [ ] **(No API key):** Type a brief → Generate → status shows "Both Claude and Ollama are unavailable." or Ollama response if running.
- [ ] **(With API key):** Set `AnthropicApiKey` in Inspector → Generate → stats preview appears (name, win condition, node/building/unit counts).
- [ ] Click **↗ Load (no save)** → scene reloads with the generated scenario; no JSON written to `res://resources/data/scenarios/` (check file browser).
- [ ] Click **💾 Save & Load** → `ai_generated.json` appears in `res://resources/data/scenarios/`; scene loads correctly.
- [ ] **Validation test:** The system should reject: positions outside ±120u, ore nodes closer than 15u, >6 combat units per faction, unknown unit_id, unknown building type.
- [ ] **Main menu button:** Open main menu → "Generate Map (AI)" button → menu closes, Edit mode entered, panel opens.
- [ ] Panel hides automatically when switching to Play mode (F5).

---

## Phase 5 Remaining Items
| Item | Status | Notes |
|------|--------|-------|
| Drop in audio .ogg files | 📋 | `res://resources/audio/sfx/` — AudioManager already wired |
| mod.io Inspector setup | 📋 | Select MainScene → set `Mod Io Game Id` + `Mod Io Api Key`; walkthrough at `docs/modio-setup-guide.md` |
| P2.4 LAN test (P2P mode) | 📋 | FlowFieldBridge active, verify checksums stay in sync through 300+ ticks |
| P0.3 Iron Pact art | 📋 | Hunyuan3D or Tripo — 8 GLBs to replace box placeholders (external work) |
| Terrain texture painting | 📋 | Set Terrain3D textures via Godot Inspector (Terrain3D → Assets) — procedural via ClassDB doesn't persist |
| Utility AI decision system | ✅ | VERIFIED in-engine 2026-06-20 (alpha_map_01/Normal, ~290s) — all 4 deadlock ACs pass, no deadlock. `e3e48bc` resolves the 2026-06-09 FAIL. |
| AI build order + attack timing logic | ✅ | Covered by utility scoring (tech tree, supply, aggression weights) |
| Adaptive input delay | 🔨 | Written — needs LAN test (see checklist above) |
| LLM trigger scripting | 🔨 | Written — needs smoke test (see checklist below) |
| AI-assisted map generation | 🔨 | Written session 23 — needs smoke test |
| AI balance analysis tools | 📋 | Phase 5 GDD item |
| Performance optimization pass | 📋 | Phase 5 GDD item |
| Advanced editor features | 📋 | Particles, sound triggers |
| Linux export | 📋 | Export template only — no code changes |
| 1.0 release | 📋 | Final milestone |

## Mental RAM
- **Current stack**: Godot 4.6.2 stable, C# / .NET 8, ECS-inspired simulation (custom SoA arrays, not a framework)
- **Rendering**: MultiMeshInstance3D for all unit rendering; two MultiMesh nodes per faction (separate colors)
- **Pathfinding**: `FlowFieldBridge` is the live path bridge (replaced `PathRequestSystem`). `PathRequestSystem` stays unused as fallback. Flow fields are deterministic — required for lockstep.
- **Networking**: Deterministic lockstep complete. `_currentDelay` starts at 4 and adapts via Ping/Pong RTT measurement + `DelayProposal` negotiation. Target delay = `ceil(OWL/33ms) + 1`, clamped [2, 12]. Both peers must agree before a change applies (`CommitDelayChange` pre-seeds gap ticks on delay increase). `INPUT_DELAY=4` is preserved as the start value constant. `CurrentDelay` property is public for HUD display.
- **Worker construction**: workers walk to site (`UnitCommand.Build` + `BuildTarget[]` SoA), building ticks its own construction timer autonomously, worker arrival clears command + resumes gathering.
- **`CommandCardSystem` worker card** fires `OnWorkerBuildRequested` → `MainScene` owns placement mode. `_Input` (not `_UnhandledInput`) for placement intercept — beats SelectionSystem.
- **`SettingsPanel`** uses intermediate `anchorRoot` Control (MouseFilter=Stop) for full-screen input blocking; Escape in `_Input`.
- **Terrain brush**: panel at (10,155) below HUD; `IsOverPanel()` guard stops paint on slider clicks; `ApplyBrushSettings()` in `ContinuePaint()` for live slider updates.
- **Supply cap**: dynamic — base 10 + 10 per alive CommandCenter. `TrainUnit()` supply-gates before deducting ore.
- **`AiDifficulty`**: Easy(8 units/40s), Normal(5/25s), Hard(3/15s). `[Export] AiLevel` on MainScene.
- **Assembly name**: `ProjectChimera` (csproj + project.godot must match or scripts won't load)
- **`PathRequestSystem` owns Move→Stop transition** (NOT Move→Idle) — Move→Idle caused stutter bug (TickIdleCombat re-wrote MoveTarget on very next sim tick)

## Open Design Decisions
- **AI art tool**: Hunyuan3D vs Tripo vs other — P0.3 Iron Pact art still pending

## Performance Baseline
| Configuration | FPS |
|---|---|
| Movement only, 500 units | ~1150 |
| Combat O(n²), 500 units | ~300 |
| Combat O(n²), 1000 units | ~50 |
| Combat + SpatialHash, 1000 units | ~350 |

## Key Architecture Decisions
- ECS-inspired simulation: SoA arrays, free list, no framework. Pure C# sim layer — no Godot types.
- NavigationServer3D direct API (no NavigationAgent3D nodes). FlowFieldBridge for deterministic multiplayer.
- Fog of war: 128×128 byte grid, R8 ImageTexture uploaded each frame by FogOfWarBridge.
- Buildings use `BuildingStore` SoA (not EntityWorld) — buildings don't move or attack.
- `PathRequestSystem` lives in presentation layer; sim layer only reads MoveTarget.
- `AiOpponentSystem` runs LAST in SimulationLoop — sees fully-updated supply caps and construction states.
- Tech tree: `prerequisites` string[] on `UnitDefinition`; checked by `TechTreeChecker.AreMet()`.
- Scenario system: `[Export] string ScenarioPath` on MainScene — map swappable from Inspector.
- Lockstep: `LockstepManager` pure C# (no Godot dep); bridges via `OnRequestPath/OnRequestAttackMove/OnCancelPath` delegates.
- Replay: `.chmr` binary format. Auto-starts on `OnMatchStart()`. `ReplayPlayer` re-applies stored orders.
- Nakama matchmaking: `NakamaService.FindMatchAsync()` — 2-player, `game=chimera_1v1`. Faction assigned by server.

## Reference
- GDD: `GDD_Project_Chimera.md`
- Implementation status (archived): `D:\Obsidian Brain\Brain\30_Archive\Chimera_STATUS_archived_2026-04-16.md`
- Godot/C# patterns (live, auto-injected each session): `D:\Obsidian Brain\Brain\20_Reference\GameDev\godot-csharp\LEARNINGS.md`
- Godot project: `D:\Obsidian Brain\Brain\10_Active_Projects\Project_Chimera\godot\`
- Server deploy: `godot/docs/server-deploy/`
- mod.io setup: `godot/docs/modio-setup-guide.md`
