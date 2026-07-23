---
title: 'Deterministic disconnect freeze-and-continue drop policy'
type: 'feature'
created: '2026-07-23'
status: 'done'
baseline_revision: 'ee8fee08694f05fa2481606f79a556e3bdd073c9'
final_revision: '294b148'
review_loop_iteration: 0
followup_review_recommended: false
context:
  - '{project-root}/_bmad-output/project-context.md'
  - '{project-root}/_bmad-output/implementation-artifacts/epic-9-context.md'
  - '{project-root}/godot/src/Multiplayer/NetworkCommand.cs'
  - '{project-root}/godot/src/Multiplayer/DedicatedServer.cs'
  - '{project-root}/godot/src/Multiplayer/ServerTransport.cs'
  - '{project-root}/godot/src/Multiplayer/LockstepManager.cs'
  - '{project-root}/godot/src/Multiplayer/Server/MergedTickBuilder.cs'
  - '{project-root}/godot/src/Multiplayer/Server/DelayController.cs'
  - '{project-root}/godot/src/Multiplayer/Server/ServerChecksumCollector.cs'
  - '{project-root}/godot/src/Multiplayer/Server/ServerHost.cs'
  - '{project-root}/godot/ProjectChimera.Sim.Tests/Golden/MergedTickN2Scenario.cs'
warnings: [oversized]
---

<intent-contract>

## Intent

**Problem:** When a peer disconnects mid-match the server has NO deterministic continue policy — the merge fan-in stalls forever. `MergedTickBuilder.TryBuild` (`MergedTickBuilder.cs:185-186`) only emits a tick once ALL `Expected` players submit it, so once the dropped slot stops submitting no merged packet is ever built, every surviving client's `Flush` gate (`LockstepManager.cs:421-425`) stalls permanently, and the server's checksum quorum silently freezes (`ServerChecksumCollector.Record` never reaches `_expected`, `ServerChecksumCollector.cs:133`). Worse, `DedicatedServer.HandleDisconnect` (`DedicatedServer.cs:213-236`) drops `_state` out of `InGame` (killing `FanInTickCommands` entirely) and emits a terminal MATCH SUMMARY — treating any drop as match-over. There is no freeze-and-continue anywhere (AR-20 gap).

**Approach:** Make the dedicated server dictate a **tick-counted, ACK-gated** freeze-and-continue, mirroring the Story 9.4 `DelayController` directive/ACK machinery. On an in-match disconnect the server (a) keeps `_state == InGame`, (b) broadcasts a new server→client `DropDirective{faction, applyAtTick}` and collects a `DropAck` from every surviving player, and (c) on all-ACK begins **injecting an empty `TickCommandPacket` for the dropped slot into the SAME `MergedTickBuilder` every tick** so merges complete and the sim continues bit-identically — the dropped faction stays in the sim and in `SimChecksum` (its idle units keep folding), only its command stream goes empty. The server also drops the disconnected reporter from its `ServerChecksumCollector` quorum so determinism attestation continues over the surviving peers. `applyAtTick` and the whole freeze are expressed in sim ticks (never wall-clock).

## Boundaries & Constraints

**Always:** All determinism/decision logic lives in Godot-free types — a NEW `Server.DropController` (the ACK-gated drop state machine) and a NEW `Server.FrozenSlotInjector` (the empty-injection drain) under `src/Multiplayer/Server/**` (Tier-1-globbed like `DelayController`/`MergedTickBuilder`), codecs in `NetworkCommand.cs`, never inside `DedicatedServer`/`LockstepManager` (Godot-coupled, excluded from Tier-1). Slot identity is transport-authoritative (the ENet peer→slot callback slot, never a packet byte); the DropAck's faction byte is mapped back to a slot via `SLOT_FACTION`, never trusted as a slot index. `applyAtTick` = `MergedTickBuilder.EmittedThrough + 1` (a tick number), and injection **drains all unemitted ticks from `EmittedThrough+1` up to the frontier `_latestSeenTick`** each pump — never gated on a future margin (that would deadlock: the survivor's frontier plateaus while it stalls, so injection must fill the whole gap). Empty injection reuses `MergedTickBuilder.Submit` unchanged — its idempotent-duplicate guard (`MergedTickBuilder.cs:159`) means a slot's already-in-flight real command still wins over a later injected empty. `DropController` is ACK-gated exactly like `DelayController`: no freeze commits until every surviving player ACKs the same `(faction, applyAtTick)`. The freeze trigger and boundary are tick-counted; NO `Time.GetTicksMsec()` in the freeze path.

**Block If:** Any pre-existing committed golden moves (`golden-merged-n2`, `golden-scenario`, `golden-multifaction`, `golden-applier-scenario`, `hero-start-state.golden.txt`, the `SimChecksumCoverageGuardTest` pin) or `SimChecksum.AlgoVersion` (21) / `StartStateHash.AlgoVersion` (2) needs to change: this story injects EMPTY commands and removes NOTHING from sim/checksum coverage, so a moved golden means an unintended sim-path change — STOP, do not re-baseline. Block if the continue policy is found to require reducing `MergedTickBuilder.Expected` or changing per-faction `SimChecksum` coverage (it must not — injection keeps `Expected` intact and the dropped faction folded).

**Never:** Do NOT implement drop-to-AI takeover or reconnect/rejoin (D4 fast-follow, explicitly out of scope per SD-10). Do NOT force-stop the dropped faction's units (a state mutation that would itself need deterministic replication); "idle" = empty commands + passive sim continues (regen, projectiles, in-flight movement continue bit-identically). Do NOT enable/verify >2 live players or raise `ServerTransport.MAX_PLAYERS` (the drop code is N-shaped, but live N>2 is Story 9.7/9.15). Do NOT add a wall-clock stall timeout. Do NOT change the P2P/LobbyUi path (freeze-and-continue is a dedicated-server feature). Do NOT fold drop/freeze state into `SimChecksum`.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| In-match disconnect | slot s drops, `_state==InGame` | server keeps InGame, `DropController.NotifyDrop(s, applyAtTick=EmittedThrough+1, survivors)` → broadcast `DropDirective{SLOT_FACTION[s], applyAtTick}`; NO MATCH SUMMARY | 0 survivors left → match truly over (emit summary) |
| Survivor ACK | every surviving player sends `DropAck(faction, applyAtTick)` matching the pending directive | `Commit()` → slot marked frozen; injection begins; `ServerHost.DropReporter(s)` | stale/mismatched ACK ignored (`RecordAck` guard) |
| Empty injection | slot s frozen, survivor submits tick t | server injects empty `TickCommandPacket` for s across `(EmittedThrough, _latestSeenTick]`, `TryBuild` emits merged (s = empty sub-bundle), broadcast; survivor unstalls | re-injecting an arrived tick → `Submit` false (idempotent no-op) |
| Frozen faction units | merged tick carries s's empty sub-bundle | s's units receive no new orders → idle units sit motionless, moving units finish their last order; passive sim bit-identical on all peers | — |
| Client receives DropDirective | player (not spectator) | fire `OnPlayerDropped(faction, applyAtTick)` for UI; send `DropAck`; keep consuming merged packets (no client-side seeding) | spectator: fire UI event, do NOT ACK |
| Checksum quorum after drop | disconnected slot stops reporting | `DropExpectedReporter(s)` lowers `_expected`, ignores s's stale reports, re-tallies in-flight buckets; surviving reporters keep producing PASS windows | drop below 1 reporter → match over |
| Lobby-phase disconnect | slot drops, `_state != InGame` | existing lobby state recompute (unchanged) | — |

</intent-contract>

## Code Map

- `godot/src/Multiplayer/NetworkCommand.cs` -- Godot-free, Tier-1. `PacketType` (:13-75; free `0x16` in the server block after `DelayDirective=0x15`, free `0x44` in the latency block after `DelayAck=0x43`) → add `DropDirective = 0x16` (server→client) + `DropAck = 0x44` (client→server). Add codecs `Make/TryReadDropDirective` + `Make/TryReadDropAck`, wire `type(1) + faction(1) + applyAtTick(4 LE)` = 6 bytes, beside the `MakeDelayDirective`/`MakeDelayAck` helpers (:850-898). Reuse `WriteUint`/`ReadUint`.
- `godot/src/Multiplayer/Server/DropController.cs` -- **NEW**, Tier-1 (folder-globbed). Godot-free ACK-gated drop state machine mirroring `DelayController`: `NotifyDrop(int slot, uint applyAtTick, int[] survivorSlots)` (sets one pending directive, resets survivor-ACK set; false if slot already dropped or a directive pending), `RecordAck(int survivorSlot, int droppedSlot, uint applyAtTick)`, `AllAcked()`, `Commit()` (marks slot dropped+frozen at applyAtTick, clears pending), `IsFrozen(int)`, `FrozenApplyTick(int)`, `FrozenSlots` enumerator, `DirectivePending`/`PendingDroppedSlot`/`PendingApplyTick`. One drop directive at a time (N=2 has ≤1 survivor).
- `godot/src/Multiplayer/Server/FrozenSlotInjector.cs` -- **NEW**, Tier-1. Static Godot-free drain shared by the server node AND the golden test (parity): `Drain(MergedTickBuilder builder, IReadOnlyList<int> frozenSlots, Faction[] slotFaction, uint frontier, byte[] scratch, Action<byte[],int> broadcast)` → for `t` in `(builder.EmittedThrough, frontier]` ascending, `Submit(f, emptyPacketFor(slotFaction[f], t), …)` for each frozen f, then `TryBuild(t)` → broadcast. Empty packet = `TickCommandPacket.Write(scratch, t, faction, emptyOrders, 0)`.
- `godot/src/Multiplayer/Server/ServerChecksumCollector.cs` -- Tier-1. Add `DropExpectedReporter(int slot)` → mark `_excluded[slot]`, clear its contribution from any active bucket (`Got/Hash/Count`), lower `_expected` (floor 1; make `_expected` mutable), re-tally now-complete active buckets, return `IReadOnlyList<(uint tick, Verdict v)>`. `Record` (:109) ignores excluded slots. Ctor invariant `[2,MaxSlots]` unchanged (only the drop path reaches 1).
- `godot/src/Multiplayer/Server/ServerHost.cs` -- Tier-1. Extract the verdict-handling body of `OnChecksum` (:79-101) into `ProcessVerdict(uint tick, Verdict v)`; add `DropReporter(int slot)` calling `_collector.DropExpectedReporter` and `ProcessVerdict` per returned verdict (keeps `WindowsCompared`/`Passing` alive over the reduced quorum).
- `godot/src/Multiplayer/DedicatedServer.cs` -- Godot node (adapter). `HandleDisconnect` (:213-236): when `_state==InGame`, do the freeze path (NotifyDrop + broadcast DropDirective) and RETURN — keep InGame, do NOT `EmitSummaryOnce`, do NOT flip state; only end if 0 survivors. `HandlePacket` (:240): add `case PacketType.DropAck` → map faction→slot, `RecordAck`; on `AllAcked` → `Commit` + `_serverHost.DropReporter(s)` + start injecting. `_Process` (:159) and `FanInTickCommands` (:416): after Poll / after a survivor Submit, `FrozenSlotInjector.Drain(_builder, frozen, SLOT_FACTION, _latestSeenTick, _injectBuf, broadcast)`. Construct `_dropController` at InGame alongside `_builder`/`_delayController`.
- `godot/src/Multiplayer/LockstepManager.cs` -- Godot client. `HandlePacket` (:508): add `case PacketType.DropDirective` → `HandleDropDirective` (fire `OnPlayerDropped(faction, applyAtTick)`; if `!IsSpectator`, send `MakeDropAck`; keep consuming merged packets — no ring seeding). Add the `OnPlayerDropped` event.
- `godot/ProjectChimera.Sim.Tests/` -- NEW `Server/DropControllerTests.cs`, `Server/FrozenSlotInjectorTests.cs`, `Server/ServerChecksumCollectorDropTests.cs`, `Golden/MidMatchDropDesyncTests.cs` (+ a `Golden/MidMatchDropScenario.cs` modeled on `MergedTickN2Scenario`), codec round-trips in `Server/ServerPacketTests.cs`; UPDATE `Multiplayer/LoopbackDesyncSelfTest.cs` with a drop-and-continue phase.

## Tasks & Acceptance

**Execution:**
- `godot/src/Multiplayer/NetworkCommand.cs` -- add `DropDirective=0x16`/`DropAck=0x44` + `Make/TryRead` codecs (`type+faction+applyAtTick`) -- the freeze wire.
- `godot/src/Multiplayer/Server/DropController.cs` (NEW) -- ACK-gated drop directive/commit state machine -- the Godot-free freeze authority.
- `godot/src/Multiplayer/Server/FrozenSlotInjector.cs` (NEW) -- empty-injection drain over `(EmittedThrough, frontier]` -- the shared, Tier-1 merge-continuation core.
- `godot/src/Multiplayer/Server/ServerChecksumCollector.cs` -- `DropExpectedReporter` (reduce quorum, re-tally) -- keeps attestation alive over survivors.
- `godot/src/Multiplayer/Server/ServerHost.cs` -- `ProcessVerdict` extract + `DropReporter` -- routes re-tallied verdicts.
- `godot/src/Multiplayer/DedicatedServer.cs` -- wire disconnect→freeze, DropAck→commit+inject, per-frame drain, keep InGame on drop -- the adapter.
- `godot/src/Multiplayer/LockstepManager.cs` -- `HandleDropDirective` (ACK + `OnPlayerDropped`) -- the client half.
- `godot/ProjectChimera.Sim.Tests/**` -- DropController, FrozenSlotInjector, ServerChecksumCollectorDrop, MidMatchDropDesync (drop at tick N, 300+ ticks, two-run equality + drop-vs-no-drop divergence), codec round-trips; LoopbackDesyncSelfTest drop phase -- Tier-1 proof of every I/O-matrix row.

**Acceptance Criteria:**
- Given a live 2-player match, when a peer disconnects, then the server stays InGame, broadcasts one `DropDirective(faction, applyAtTick)`, collects a `DropAck` from the survivor, and thereafter injects an empty command for the dropped slot each tick so merged packets keep flowing; the dropped slot is NOT removed from the sim or from `SimChecksum`; `applyAtTick` and the freeze are tick-counted (no wall-clock timer).
- Given a mid-match drop, when the sim continues 300+ ticks past the drop, then the checksum sequence is byte-identical across two independent runs of the freeze path (remaining peers stay in sync), and diverges from a no-drop control (non-vacuous).
- Given a frozen idle unit, when the freeze is in effect, then it sits motionless and passive sim (regen, projectiles) continues bit-identically on all peers.
- Given the disconnected reporter, when it stops sending checksums, then `ServerHost` drops it from the quorum and keeps completing PASS windows over the survivors (no silent quorum freeze, no false HALT).
- Given the full suite, when it runs, then `SimChecksum.AlgoVersion`(21)/`StartStateHash.AlgoVersion`(2) are unchanged and every pre-existing committed golden is byte-identical (moved golden = Block-If).

## Design Notes

**Why inject empties (not reduce `Expected`).** The AC mandates "empty commands injected for the dropped slot each tick, and the dropped slot is NOT removed." Injecting empties keeps `MergedTickBuilder.Expected=2`, keeps the dropped faction's (empty) sub-bundle in the merged stream, and needs ZERO builder change — `Submit`'s idempotent-duplicate guard means the dropped slot's already-in-flight real commands still win over a later injected empty, so its final pre-drop actions execute, then it goes idle. Nothing about `SimChecksum` changes: the dropped faction's units stay alive and fold exactly as before.

**Drain the whole gap, not a future `applyAtTick`.** In lockstep the survivor's frontier plateaus when it stalls (it only submits `execTick+delay` and stops advancing exec while blocked). So a `DelayController`-style "future `applyAtTick` + margin" would deadlock — the frontier never reaches it. Injection therefore fills every unemitted tick from `EmittedThrough+1` up to the current frontier each pump; `applyAtTick=EmittedThrough+1` is the informational "idle-from" marker for the survivors' ACK/UI, never a determinism dependency (the merged stream, not the marker, drives every peer's sim).

**Client is passive.** Once the server injects and broadcasts, the client's `Flush` merged-arrival gate fills normally and unstalls — the client needs no ring pre-seed (unlike `CommitDelayChange`). It only ACKs (network layer runs even while the sim is stalled) and fires a presentation event.

**Test harness.** `MidMatchDropScenario` mirrors `MergedTickN2Scenario.MergedDriver` but after `dropAtTick` stops submitting Player2's real orders and calls `FrozenSlotInjector.Drain` (the REAL injector) so the test exercises production code. `MidMatchDropDesyncTests` runs it twice → `CompareSequences` equal (peers in sync), and asserts divergence from a no-drop run (non-vacuous).

## Verification

**Commands:**
- `dotnet build godot/godot.csproj` -- expected: compiles clean; determinism analyzer green (no `float`/`Dictionary`-enumeration in the new Godot-free types; empty-order scratch is `System.*`-only).
- `dotnet test godot/ProjectChimera.Sim.Tests` -- expected: all pass incl. new DropController/FrozenSlotInjector/ServerChecksumCollectorDrop/MidMatchDropDesync + codec tests; **every pre-existing golden byte-identical**; `SimChecksum.AlgoVersion`(21)/`StartStateHash.AlgoVersion`(2)/`PROTOCOL_VERSION`(2) unchanged.
- `dotnet test godot/ProjectChimera.Sim.Tests --filter "FullyQualifiedName~Golden|SimChecksumCoverageGuard|VersionStampConsistency"` -- expected: goldens unchanged (moved golden = Block-If, not a re-baseline).

**Manual checks (Godot-side integration, not Tier-1):**
- `godot --headless -- --loopback-test` (`LoopbackDesyncSelfTest`) -- expected: still `RESULT: PASS`; the new drop-and-continue phase disconnects one peer after the clean-PASS window and asserts the survivor keeps advancing (merged ticks keep arriving) and `Host.WindowsCompared` keeps incrementing over the reduced quorum.

## Spec Change Log

_None — no bad_spec loopback occurred; the review pass resolved via patches only._

## Review Triage Log

### 2026-07-23 — Review pass (review_loop_iteration 0)
- intent_gap: 0
- bad_spec: 0
- patch: 3: (high 0, medium 2, low 1)
- defer: 5: (high 0, medium 2, low 3)
- reject: 3
- addressed_findings:
  - `[medium]` `[patch]` **Golden gate weaker than its SD-10 mandate** (verification-gap lens). `MidMatchDropDesyncTests`' `Count >= dropTick+300` bound was vacuous (Count == StepOnce loop iterations, recorded every tick regardless of merges), and a NO-OP injector that stalls the survivor's post-drop command delivery passed all three golden assertions (deterministic across runs, diverges from no-drop, matches pre-drop) — backstopped only at the unit level by `FrozenSlotInjectorTests`. Fixed by adding a `RunDropNoInject()` reference (Player2 drops, NO injection → merge stalls, Player1's post-drop orders never apply) and asserting the real drop-run DIVERGES from it, plus a non-constant post-drop sub-sequence assertion; removed the vacuous Count bounds. Sanity-checked: stubbing the injector makes the new assertion fail while the pre-existing three still pass.
  - `[medium]` `[patch]` **Collector multi-bucket re-tally untested** (verification-gap lens). Every `DropExpectedReporter` test had ≤1 in-flight bucket, leaving the whole-ring multi-completion + `results.Sort` (ascending) path — the common production case — unexercised (a break/return-after-first-bucket or mis-order mutation shipped green). Added `DropReporter_ReTalliesMultipleInFlightBuckets_AscendingByTick` (slot 0 reports ticks 10 AND 11, slot 1 never; one `DropExpectedReporter(1)` returns both verdicts, both Complete, ascending, each tallying slot 0 canonical).
  - `[low]` `[patch]` **Doc-honesty + defensive comments** (adversarial lens, findings 3/4/6/8/10). Clarified that a floor-1 quorum after a 1v1 drop is a rubber-stamp — continued PASS windows are liveness/observability, NOT cross-peer attestation (`ServerChecksumCollector.DropExpectedReporter` + `ServerHost.DropReporter`); corrected the `DropController._frozenSlots` "ascending" claim (only incidental at N=2; no consumer relies on it — the builder re-sorts by faction id); added a comment that `applyAtTick` stays valid because `EmittedThrough` cannot advance during the directive→ACK window; made the null-freeze-machinery-while-InGame case log (`GD.PrintErr`) instead of silently no-op; noted a count-0-after-clear bucket is left Active-but-harmless.
- deferred (see `deferred-work.md`, 5 NEW entries): (1) N≥3 freeze deadlocks — one-directive-at-a-time swallows a concurrent drop, and the `_isSurvivor` snapshot is never reconciled against a survivor that vanishes mid-ACK (unreachable at MAX_PLAYERS=2; owned by the N-player enablement/verification Stories 9.7/9.15); (2) no ACK-timeout/liveness on the drop directive (disconnect-domain continuation of the 9.4-deferred delay-ACK-timeout); (3) the Godot-coupled DedicatedServer freeze adapter has no xUnit coverage + the survivors≤0 branch is unverified (accepted 9.3/9.4 node-wiring boundary; loopback smoke covers survivors==1 only); (4) FactionToSlot injectivity is unasserted + no dropped-slot-led-frontier test; (5) AC3's regen/projectile-in-flight straddle examples not specifically constructed (proven only transitively via the real pipeline + movement + fold).
- rejected (3): the AC1 "checksum" lexical seam (the intent-alignment auditor itself flags it as a non-contradiction — the `SimChecksum` fold vs the cross-peer quorum are two different structures, and the spec's Boundaries/Design Notes already distinguish them); "sits motionless vs empty commands" (reconciled by SD-10 and stated in the spec's Never/Design Notes — a standing order finishes before idling, correct passive-sim continuation); `OnPlayerDropped` presentation stub with no UI subscriber (scoped presentation-only in this story; lobby/UI wiring is Story 9.7).

### 2026-07-23 — Follow-up review pass (review_loop_iteration 0, followup_review_recommended)
- intent_gap: 0
- bad_spec: 0
- patch: 0
- defer: 4: (high 0, medium 3, low 1)
- reject: 11
- addressed_findings:
  - none
- deferred (4 NEW ledger entries appended; no existing entry modified per orchestrator constraint):
  - `[medium]` Post-drop floor-1 quorum reports PASS rather than INCONCLUSIVE on the human-facing MATCH SUMMARY — the prior pass fixed the doc-comments (liveness != attestation) but the observable verdict surface still over-claims determinism enforcement after any 1v1 drop (adversarial lens; distinct from the prior doc-honesty patch).
  - `[medium]` The disconnect-driven checksum re-tally is tested only through a clean lone-survivor PASS; the re-tally-to-DESYNC/HALT branch (ServerChecksumCollector.DropExpectedReporter -> ServerHost.DropReporter -> ProcessVerdict HALT/alert) is unexercised — latent behind MAX_PLAYERS=2 (needs two remaining reporters to disagree), tied to the N-player enablement work (verification-gap lens).
  - `[medium]` The mid-match-drop golden gate is entirely relative (two-run + divergence-from-noinject + divergence-from-nodrop) with no committed golden tail or positive idle-equivalence baseline, so it cannot distinguish the correct idle-but-folded state from any other deterministic-but-wrong injector output; the test comment's "faction dropped from sim would be caught" claim is unfounded (verification-gap lens).
  - `[low]` The surviving CLIENT Flush-gate unstall — the literal center of the intent's problem statement — is verified only transitively (golden bypasses LockstepManager.Flush; loopback smoke counts packets, does not run the stall gate); the "no ring pre-seed needed, unlike a delay change" claim is asserted by comment, not by a client-side test that stalls and resumes a real Flush (intent-alignment auditor; distinct from the deferred SERVER-adapter-no-xUnit entry).
- rejected (11, dropped silently): five findings that merely re-surface the prior pass's already-open ledger entries — the N>=3 concurrent-drop / `_isSurvivor`-not-pruned deadlock, no ACK-timeout, FactionToSlot injectivity + dropped-slot-led-frontier, and the DedicatedServer-freeze-adapter-no-xUnit gap; plus six genuine noise / out-of-scope items — `_dropped`/`_excluded` never cleared on reconnect (reconnect explicitly out of scope per SD-10 Never), the removed mid-match `Hello(Faction.Neutral)` notification (by-design: the dropped faction now stays alive+idle rather than being reassigned to Neutral, clients get `OnPlayerDropped` instead), `Commit()` leaving stale `_pendingApplyTick`/`_isSurvivor` + `PendingApplyTick` 0u-sentinel ambiguity (latent, no consumer at N=2), `_resolvedThrough` advance stranding an out-of-order lower bucket (mitigated by the reliable-ordered ENet checksum channel the collector already relies on), the double `PumpFrozenInjection` per frame (functionally harmless — `Drain` is idempotent over `(EmittedThrough, frontier]`), and the multi-frozen-slot injector test gap (N>=3 only, owned by the N-player enablement story's own suite).

## Auto Run Result

Status: done (follow-up review pass — no code changes)

### Summary of implemented change
No new code was produced this pass. Story 9.6 (deterministic disconnect freeze-and-continue drop policy) was already implemented and committed (`9098ee5`). This run is the follow-up review the prior pass flagged (`followup_review_recommended: true`): a fresh four-lens adversarial re-review of the committed diff against baseline `ee8fee0`. It found no new in-scope (MAX_PLAYERS=2) correctness defect, no intent gap, and no spec defect — so the implementation stands as committed.

### Files changed this pass
- `_bmad-output/implementation-artifacts/deferred-work.md` — appended 4 NEW defer entries (no existing entry touched, per the orchestrator constraint in the invocation args).
- `_bmad-output/implementation-artifacts/spec-9-6-...md` — this file: added the follow-up triage-log entry + Auto Run Result; set `followup_review_recommended: false`.

### Review findings breakdown
- **Patches applied:** 0.
- **Deferred (4 new):** 3 medium + 1 low — (1) post-drop floor-1 quorum reports PASS instead of INCONCLUSIVE on the human-facing MATCH SUMMARY; (2) the re-tally-to-DESYNC/HALT branch is untested (latent behind N=2); (3) the drop golden gate is relative-only with no positive idle-equivalence baseline; (4) the surviving-client Flush-gate unstall is verified only transitively.
- **Rejected (11):** 5 re-surfaced findings already covered by the prior pass's open ledger entries (N>=3 deadlock, no-ACK-timeout, FactionToSlot injectivity, DedicatedServer-adapter-no-xUnit) + 6 noise/out-of-scope (reconnect resurrection — out of scope; Hello(Neutral) removal — by-design; Commit stale-field cleanliness — latent; `_resolvedThrough` out-of-order — transport-invariant-mitigated; double pump — harmless idempotent; multi-frozen-slot test gap — N>=3).

### Follow-up review recommendation
`false`. This pass triaged 0 findings as `patch`. Score = 3×(medium patches=0) + 1×(low patches=0) = 0 (< 5), 0 high-severity patches → `followup_review_recommended: false`.

### Verification performed
No code changed, so the story's `## Verification` commands were not re-run — the committed revision was already built/tested green in the implementing pass and this pass introduced no code delta. The four review lenses (adversarial / edge-case / verification-gap / intent-alignment) ran as blocking subagents over the `ee8fee0..HEAD` godot/ diff.

### Residual risks
The 4 newly-deferred items are all real but out of the shipped N=2 scope or below the AC bar: the freeze-and-continue path is correct and deterministic for the 2-player case the story targets. The principal residual is observability, not correctness — a human reading a post-1v1-drop MATCH SUMMARY sees "PASS" windows that are liveness-only, not cross-peer attestation (deferred item 1). The N-player robustness (deadlocks, desync-verdict testing, multi-slot injection) is owned by Stories 9.7/9.15 per the epic sequence.

