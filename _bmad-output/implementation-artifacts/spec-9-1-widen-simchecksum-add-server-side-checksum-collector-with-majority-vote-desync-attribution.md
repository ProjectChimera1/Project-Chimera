---
title: 'Widen SimChecksum + add server-side checksum collector with majority-vote desync attribution'
type: 'feature'
created: '2026-07-22'
status: 'blocked'
review_loop_iteration: 0
followup_review_recommended: false
context: []
warnings: [multiple-goals]
---

<intent-contract>

## Intent

**Problem:** Story 9.1 (SD-5 + SD-7) asks to widen `SimChecksum` from Ore-only to all per-faction arrays and to invert the desync checksum path from P2P-local compare to a stateful server-side collector that majority-votes, attributes a minority, and fail-closed HALTs on no majority. **Both halves were already delivered ahead of schedule by Epic 1** — SD-7 by Story 1.3b, SD-5 by Stories 1.9a/1.9b — so there is no remaining implementable work for 9.1 as scoped.

**Approach:** Do NOT fabricate an implementation. Confirm each 9.1 acceptance criterion is already satisfied by existing, tested code, then surface the backlog↔codebase contradiction for a human decision (mark 9.1 done-by-prior-work and note the Epic-9 backlog subsumption). A gratuitous `AlgoVersion` bump / golden re-baseline to "make the story do something" is forbidden.

## Boundaries & Constraints

**Always:** Treat the D5 briefing (SD-5/SD-7) as the canonical 9.1 scope; verify claims against the live codebase, not the epic's stale brownfield hints ("SimChecksum only hashes Ore[Player1]/Ore[Player2]" is false as of 1.3b).

**Block If:** The story's entire scope is already implemented and tested by prior stories, leaving no implementable delta — a dev-auto pass cannot safely either (a) fabricate make-work or a needless golden re-baseline, or (b) unilaterally declare the story `done` and re-scope the Epic-9 backlog. A human/orchestrator must decide. **← This condition is TRUE; see Auto Run Result.**

**Never:** Bump `SimChecksum.AlgoVersion` or re-record any golden without a real new folded field (violates the checksum-fold timing rule). Never widen the 32-bit checksum wire (D12). Never build `TickCommandsMerged` / N-adaptive quorum / disconnect re-base here — those are 9.2/9.3/9.5.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Widened checksum (AC1) | Running sim, 2 active factions | `SimChecksum.Compute` hashes Ore+Crystal+SupplyUsed+SupplyCap+FactionBase per active faction, ascending; `AlgoVersion` pinned; known-state world → fixed committed hash | Already implemented (v2/Story 1.3b; `AlgoVersion=21`); `SimChecksumCoverageGuardTest` |
| Server collector (AC2) | Two clients emit slot-tagged Checksum packets | Server takes slot from transport, buffers per-slot per-window, majority-votes canonical, names minority in DesyncAlert, HALTs+broadcasts on no majority; spectators excluded | Already implemented (Story 1.9a/1.9b); `ServerChecksumCollectorTests`/`ServerHostTests` |
| N=2 golden (AC3) | Existing 2-player golden replay under widened checksum + collector | Byte-identical across two runs; match completes without false desync | Already implemented; `GoldenChecksumReplay` + 1.9b LAN determinism proof |

</intent-contract>

## Code Map

- `godot/src/Core/SimChecksum.cs` -- **AC1 done.** Per-active-faction fold of Ore/Crystal/SupplyUsed/SupplyCap/FactionBase since v2 (Story 1.3b, lines 418-428). `AlgoVersion = 21`.
- `godot/ProjectChimera.Sim.Tests/Golden/SimChecksumCoverageGuardTest.cs` -- **AC1 guard done.** `KnownWorldState_ProducesPinnedV20Hash` (fixed committed hash) + `EveryPerFactionResourceArray_IsFoldedIntoTheChecksum` (reflection differential proving Crystal/Supply are folded).
- `godot/src/Multiplayer/Server/ServerChecksumCollector.cs` -- **AC2 collector done (Story 1.9a).** Slot-tagged per-tick buckets, strict-majority (`> N/2`) canonical, ascending-slot minority attribution, no-majority verdict, stale/duplicate drop; transport-authoritative slot; N-shaped (`MaxSlots=4`).
- `godot/src/Multiplayer/Server/ServerHost.cs` -- **AC2 verdict→wire done.** Minority → `MakeDesyncAlert`; no-majority → broadcast `MakeHalt` + terminal `Halted`; FR-39 PASS/FAIL observability.
- `godot/src/Multiplayer/DedicatedServer.cs` -- **AC2 wired (Story 1.9a, D8).** `HandlePacket` Checksum case consumes into `ServerHost.OnChecksum` (slot from transport, spectators excluded); old opaque relay removed.
- `godot/ProjectChimera.Sim.Tests/Server/ServerChecksumCollectorTests.cs`, `ServerHostTests.cs`, `ServerHostObservabilityTests.cs`, `ServerPacketTests.cs` -- **AC2 tests done.** all-agree / one-minority-N3 / no-majority-N2 / no-majority-N3 / N4-majority / 2-2-split / stale / duplicate.
- `godot/ProjectChimera.Sim.Tests/Golden/GoldenChecksumReplay.cs` -- **AC3 done.** Byte-identical two-run golden replay harness.
- `_bmad-output/game-architecture.D5-briefing.md` -- canonical SD-5/SD-7 scope for 9.1.
- `_bmad-output/implementation-artifacts/deferred-work.md` -- confirms the only residual server-collector work (N-adaptive quorum, disconnect re-base, late-window) is assigned to 9.2/9.3/9.5, NOT 9.1.

## Tasks & Acceptance

**Execution:**
- *(none — no implementable delta; see Auto Run Result).* If a human confirms "done-by-prior-work", the only action is a status/backlog update (mark 9.1 done, note Epic-1 subsumption); no source changes.

**Acceptance Criteria:**
- Given the widened `SimChecksum`, when `Compute` runs on a known 2-faction world, then it folds Crystal/SupplyUsed/SupplyCap (not just Ore) per active faction in ascending order and pins to a fixed committed hash — **already met** (`SimChecksumCoverageGuardTest`).
- Given two clients emitting slot-tagged Checksum packets, when each reports its 60-tick-window checksum, then the server buffers per-slot, majority-votes, names the minority in a DesyncAlert, and HALTs fail-closed + broadcasts on no majority, using the transport slot — **already met** (`ServerChecksumCollector`/`ServerHost`/`DedicatedServer`, unit-tested).
- Given the existing 2-player golden replay, when the widened checksum + collector run at N=2, then the golden is byte-identical across two runs with no false desync — **already met** (`GoldenChecksumReplay` + 1.9b).

## Design Notes

The epic's Story-9.1 brownfield hints ("SimChecksum.cs:53-54 today only hashes Ore[Player1]/Ore[Player2]"; "DedicatedServer uses P2P-local checksum compare") describe a codebase snapshot that predates Epic 1's 1.3b and 1.9a/1.9b. Epic 1 pulled both SD-5 and SD-7 forward because the server-side collector became the #1-ship-risk FR-39 gate (1.9a/1.9b). The 1.9a code review recorded "**All 5 ACs satisfied, scope fence clean**" and explicitly deferred only the N-scale/disconnect hardening to 9.2/9.3/9.5 — none of which is in 9.1's ACs. At N=2 the collector's only reachable divergence verdict is no-majority→HALT (a 1-1 split has no strict majority); the minority-DesyncAlert branch is N≥3 and correctly lives behind 9.2's faction expansion.

## Auto Run Result

Status: blocked
Blocking condition: **story already implemented by prior work — no implementable delta.** Story 9.1's entire scope (SD-7 widen `SimChecksum`; SD-5 server-side checksum collector with strict-majority vote, minority attribution, and no-majority fail-closed HALT) was delivered ahead of schedule by Epic 1 — SD-7 by Story 1.3b (`SimChecksum` v2, `AlgoVersion` now 21, guarded by `SimChecksumCoverageGuardTest`'s known-state hash pin + per-faction coverage guard) and SD-5 by Stories 1.9a/1.9b (`ServerChecksumCollector` + `ServerHost` + `DedicatedServer` Checksum-case wiring, unit-tested for all-agree/one-minority-N3/no-majority-N2/no-majority-N3/N4-majority/2-2-split/stale/duplicate; two-run byte-identical golden via `GoldenChecksumReplay`). All three 9.1 acceptance criteria are already satisfied by existing, passing tests. The only residual server-collector work (N-adaptive quorum re-base on disconnect, late-checksum window) is explicitly charter-assigned to Stories 9.2/9.3/9.5 in `deferred-work.md`, not 9.1.

A dev-auto pass cannot safely resolve this alone: fabricating make-work or a needless `AlgoVersion` bump / golden re-baseline is forbidden (checksum-fold timing rule + no-fabrication posture), and unilaterally declaring the story `done` while re-scoping the Epic-9 backlog is a governance decision, not a code change.

**Recommended human action:** mark Story 9.1 done-by-prior-work (subsumed by Epic-1 Stories 1.3b + 1.9a/1.9b), note the subsumption in the Epic-9 backlog / sprint-status, and proceed to Story 9.2 (faction/player model expansion to 8 — the first story with genuine implementable delta on the D5 backbone). Resolve interactively via `/bmad-loop-resolve 9-1-widen-simchecksum-add-server-side-checksum-collector-with-majority-vote-desync-attribution`.

Note: `warnings: [multiple-goals]` carried from routing (the title joins a sim-layer widen and a server-layer collector); both goals are already satisfied, so the warning is informational only.
