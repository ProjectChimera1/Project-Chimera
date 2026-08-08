---
project: Project Chimera
last_touched: 2026-08-08
phase: Phase 5 — Polish & 1.0
status: Active
---

# Project Chimera — Snapshot

**Last Touched:** `2026-08-08`

## Current Phase
**Phase 5 — Polish & 1.0** (Months 25-31 of GDD roadmap)

Phases 0–4 are code-complete. Phase 5 is underway. Session 20 shipped worker-placed buildings + UI bug sweep. Session 21 (remote, away from computer) shipped Utility AI + Adaptive Input Delay.

## Next Action
**RUN THE TWO-MACHINE FR-39 GATE. DW-908 is built, tested and closed — nothing else is in the way.**

The blocker named in the previous Next Action is gone. `AiOpponentSystem` no longer plays a faction a human
occupies: AI control is now a per-match `AiControlPlan` (a faction bitmask) resolved from slot OCCUPANCY, it
is folded into `MatchAgreementHash` (**AlgoVersion 3 → 4**) so peers that disagree are rejected at the
handshake instead of desyncing, and a single-machine loopback run confirmed both peers logging
`AI active this match: False` and the identical `Match-agreement hash (algo v4): 0xD9E709768053087C`.
**Tier-1 6355 passed / 0 failed / 1 skipped; no golden moved; `StartStateHash` stayed at v2.**

**Do this, on both machines:**
1. `git pull` on the laptop, then `dotnet build godot/godot.csproj` on BOTH — the hash algo version moved, so
   a stale peer will now be REJECTED at the handshake with a start-state-mismatch message rather than
   desyncing. That rejection is the fix working; it is not a new bug.
2. Run `godot/tools/lan-desync-smoke.ps1` per `lan-determinism-runbook.md` — server + client on the PC,
   client on the laptop.
3. **Play the match for 300+ ticks and read the WINDOW COUNT, not the PASS.** Expect sustained clean windows
   **past tick 660** (where it HALTed last time) with `0 desync`. Close the CLIENTS before the server or the
   `MATCH SUMMARY` line is lost.
4. Record that summary in story **1-9b**'s Change Log — that closes **FR-39**, the #1 pre-ship gate, carried
   since Epic 1.

**What a clean run past 660 proves and does not prove.** It proves the AI is no longer co-piloting a human
and no longer injecting float nondeterminism into a live match. It does NOT close **DW-204** (the AI's
scorer is still float) — that debt is now *contained* rather than fixed: the AI simply does not run online.
DW-204 must land before an AI may ever fill a vacant slot in a lockstep match.

**If it still desyncs**, the AI is exonerated by construction — both clients log `AI active this match:
False`, so the cause is elsewhere. Grep both client logs for `Placement mode` (DW-405) before anything else.

**Residuals filed, not forgotten:** **DW-909** (the offline AI seat is still the constant {Player2}; the AI
is still a singleton, so a 3rd AI player is unbuildable — this is the feature Alec said he *wants*) and
**DW-910** (the `.chmr` header carries no AI plan, so an online replay plays back with the AI armed).

**Also available, unblocked, no code needed:** A5-E9 leg (b), **live Nakama** — the same two-machine rig
plus `docs/server-deploy/docker-compose.yml` on the LAN. Highest-value target there is **DW-435**, the
flagged soft-lock risk that could lock every player out of every online match.

**bmad-loop is stopped**, tree clean. A fresh `bmad-loop run --epic 15` picks up the remaining Epic-15
keys (**15-21**, **15-23**); **15-14** is `blocked` (DW-200, needs Alec's trust-mechanism decision) and
**15-1** stays `blocked` by Alec's deferral.

Read the **2026-08-08** block below first.

<details><summary>Superseded Next Action (Story 15-3 — now done)</summary>

**Run Story 15-3 — status effects become real + modifier-period honesty** (`bmad-loop run --story 15-3`; DW-266, 267, 270, 271, 278, 323). `StatusFlags` is written by the ability system and read by *nothing*: Disarmed, Rooted, Stunned, Silenced and Invulnerable are all authorable today and all do nothing in play. **It moves goldens by design** (the StatusFlags re-baseline) and re-records at the end of its own story per the standing batch rule — do not queue it behind a batch. Decision **DW-325 is already ruled *build***: a modifier collapsing `EffectiveMaxHealth` to 0 raises death rather than pinning a 0-HP "zombie"; that rides the same re-record. It is Godot-coupled (the `Warnings` channel surfaces in the 2.5 ability editor), so it needs the in-engine gate and the MCP bridge free.

Read the **2026-08-06 (later)** block below first — the earlier same-day block's numbers (AlgoVersion 23, 370 open) are superseded. Both precede the 2026-08-01 block, and all three precede the legacy sections, which describe Sessions 20–21 and are far out of date.

</details>

---

*Session type: bmad (prescribed workflow in active execution)*

---

## Current State (2026-08-08) — read this first

**Headline: FR-39 ran for the first time, and it caught a real desync.** The #1 pre-ship gate — two
physical machines in lockstep, carried un-run since Epic 1 and named in A5-E9 as the top accepted risk —
was executed end to end on a PC (RTX 3060, server + Player1) and a laptop (GTX 1650, Player2) over LAN,
on `map_02_iron_crossing`.

```
[Determinism] tick  60 .. 600: all 2 peers matched, windows #1-#10
[Determinism] tick 660: GLOBAL DESYNC - no canonical hash. Broadcasting terminal HALT.
[Determinism] MATCH SUMMARY: 11 windows compared, 1 desync, 0 abandoned - FAIL.
```

**Both AC4 halves are demonstrated.** The clean-PASS half outright — 10 consecutive matching windows over
600 ticks, double the required ≥5/≥300, across a committed delay change, both HUD hashes reading
`0x75F90131` at tick 588. The terminal-HALT half by a **real** divergence rather than the F9 injection:
"MATCH HALTED — Simulation desync detected at tick 660" appeared on **both** machines simultaneously
(Alec confirmed the laptop). That is stronger evidence than the synthetic drill, which remains unfired.

**FR-39 is NOT closed, and the blocker is DW-908.** The desync is the float AI, which makes **DW-204**
(severity high, open since 2026-06-09, *"illegal in lockstep MP until converted"*) **proven live rather
than predicted**. Attribution is solid: the AI is the only float system in the sim; the human placed
nothing (zero `Placement mode` lines in the client log, which also rules out DW-405); P2 is
`AiOpponentSystem.AI_FACTION` and is the faction that diverged. **Decisive:** the divergence point MOVES
between runs on identical inputs — windows #1–#9 hashed byte-identically across two separate runs, yet one
diverged before tick 600 and the other stayed clean at 600 and went by 660. A deterministic logic bug
reproduces at the same tick; float nondeterminism does not.

**DW-908 (high) — Alec's framing, and it is the right one.** Not "the AI runs online": the AI is bound to
a **constant** (`AI_FACTION = Faction.Player2`) instead of to **who occupies the slot**, so it co-pilots a
human's faction. Offline the pairing coincidentally holds (`ActiveSlotsInLaunchOrder` sorts the lone Human
to Player1); online `AssignedRoster` seats peers by arrival with no Human/AI concept at all. An AI filling
a genuinely vacant slot *should* play it — that is why an "online off-switch" is the wrong shape and would
have to be undone to support 2 humans + 1 AI. **No story or epic covers this**; Story 10.11 / DW-204 is
the float→Fixed migration and is silent on ownership.

**Six defects found in two evenings, five of them impossible to find from one machine:**
- **DW-905** (closed) — Terrain3D's GDExtension binaries were excluded from the repo by a `bin/`
  .gitignore rule, so a fresh clone had **no terrain**; Godot disabled the addon and rewrote the tracked
  `project.godot`. Would have desynced for reasons unrelated to determinism. Residual → **DW-907**.
- **DW-906** (closed) — the LAN launcher hid its own verdict (`Start-Process` detached the server, so the
  `[Determinism]` output went nowhere) and leaked a port-holding orphan every run.
- **DW-907** (open) — nothing verifies that a fresh clone produces a working build. Two instances in two
  subsystems (Terrain3D, the gitignored `nakama-modules/build`) makes it a class.
- **DW-908** (open, high) — the AI ownership bug above.
- **DW-204 / DW-739** — now carry `proven-live:` evidence.

**Tooling now in place for the next run:** the launcher is ASCII-only (it would not parse under
`powershell -File`, i.e. Windows PowerShell 5.1, because UTF-8 arrows decoded to `’` — a string
delimiter); logs are UTF-8 in `lan-logs/`; cleanup is role-aware and on by default; there is a port
pre-flight that names the owning PID; paths auto-derive. The runbook is rewritten against what actually
happens, including a new §10 recording what is already proven so the next run does not re-derive it.

**Two operating lessons worth carrying:**
1. **`PASS` is not the number to read — the window count is.** The server prints PASS on zero desyncs even
   when it compared almost nothing; an earlier run logged `1 windows compared … PASS`, which is truthful
   and worthless. <5 windows is an aborted run.
2. **Close the CLIENTS before the server.** `MATCH SUMMARY` is emitted on match end, not server stop —
   stopping the server first cost this session the verdict line of a 9-window run.

**Not verified:** anything requiring a remote session. A minimised/backgrounded remote desktop stops the
client's `_Process` and the server drops that peer as a timeout — that ended both 2026-08-07 attempts, at
ticks 94 and 114.

---

## Current State (2026-08-07)

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

**LAN smoke test (two machines required — do alongside P2.4 LAN test):** _partially run 2026-08-07 (PC + laptop, dedicated-server topology, `alpha_map_01`). Full record: story 1-9b Change Log + `godot/tools/lan-determinism-runbook.md` §10._
- [x] Host + join on LAN. Watch Godot Output on both machines. — 2026-08-07
- [ ] Within 2s of match start: both machines should log `[Lockstep] RTT sample: Xms` and a smoothed RTT. — **not observed**; no `RTT sample` line appeared in either client log, though the delay controller clearly acted on RTT. Check whether that log line still exists before re-testing.
- [x] On LAN (~1-5ms RTT): target delay = `ceil(2.5ms / 33ms) + 1 = 2`. Both machines should log `[Lockstep] Delay: 4 → 2 ticks` within ~5s. — 2026-08-07, observed on BOTH machines, and server-side as `Dictating → 2 ticks, applyAtTick 40` + `committed (all 2 players ACKed)`
- [x] Play for 300+ ticks. Checksums must stay in sync (same HUD hash on both machines). The delay reduction must NOT cause desync. — **2026-08-08: 10 consecutive matching windows over 600 ticks** on `map_02_iron_crossing`, across a committed delay change at tick 57; both HUD hashes read `0x75F90131` at tick 588. Then a **REAL desync at tick 660** → terminal HALT on both machines. Summary: `11 windows compared, 1 desync, 0 abandoned — FAIL`. The 600-tick clean stretch satisfies the ≥300-tick bar; the desync is the **float AI** (DW-204 proven live, DW-908 filed) and is why FR-39 is not yet closed.
- [x] Optionally: to test high-latency path, add artificial latency (e.g. `tc netem` on Linux) and verify delay increases toward MAX_DELAY=12. — 2026-08-07, reached MAX_DELAY=12 (`applyAtTick 99`) — **not** via `tc netem` but via a genuinely stalled peer, which is the same code path and arguably a better test.

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
- **Adaptive delay (offline only): no errors observed** in ~110s offline play. ~~LAN test still pending.~~ **LAN-verified 2026-08-07**: `4 → 2` on both machines, and `→ 12` (MAX_DELAY) then `→ 11` under a stalled peer, all with full server-side ACK round-trips. The *determinism-across-a-delay-change* half is still unproven — the run never reached a second comparison window.
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
| P2.4 LAN test | 🔨 | **RUN 2026-08-08 on two machines. 10 clean windows / 600 ticks, then a REAL desync at tick 660 → terminal HALT on both.** Summary `11 windows compared, 1 desync, 0 abandoned — FAIL`. Both AC4 halves demonstrated (clean-PASS outright; terminal-HALT by a genuine desync rather than the F9 injection). NOT closed: the desync is the **float AI** — DW-204 proven live, **DW-908** filed (the AI cannot be switched off, so any long online match desyncs). Close FR-39 by gating the AI off online and re-running. Note the old "(P2P mode)" label was **stale** — the pinned topology is the **dedicated server** (runbook §2 / 1-9b Resolved Decision #2). See story 1-9b Change Log. |
| P0.3 Iron Pact art | 📋 | Hunyuan3D or Tripo — 8 GLBs to replace box placeholders (external work) |
| Terrain texture painting | 📋 | Set Terrain3D textures via Godot Inspector (Terrain3D → Assets) — procedural via ClassDB doesn't persist |
| Utility AI decision system | ✅ | VERIFIED in-engine 2026-06-20 (alpha_map_01/Normal, ~290s) — all 4 deadlock ACs pass, no deadlock. `e3e48bc` resolves the 2026-06-09 FAIL. |
| AI build order + attack timing logic | ✅ | Covered by utility scoring (tech tree, supply, aggression weights) |
| Adaptive input delay | 🔨 | Written. **LAN-verified 2026-08-07** for the adaptation itself (`4→2`, `→12`, `→11`, all ACK-committed on both machines). Still owed: proof that determinism HOLDS across a delay change — the run never reached a second comparison window. |
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
