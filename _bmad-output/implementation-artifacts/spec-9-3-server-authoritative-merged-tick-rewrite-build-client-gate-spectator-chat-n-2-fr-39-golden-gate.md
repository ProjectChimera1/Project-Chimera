---
title: 'Server-authoritative merged-tick rewrite — build + client gate + spectator/chat + N=2 FR-39 golden gate'
type: 'feature'
created: '2026-07-23'
status: 'done'
baseline_revision: '2cfb324ac88d1fce5567dd8e4792190dccfa252b'
final_revision: '3a1913c'
review_loop_iteration: 0
followup_review_recommended: false
context:
  - '{project-root}/_bmad-output/project-context.md'
  - '{project-root}/_bmad-output/implementation-artifacts/epic-9-context.md'
  - '{project-root}/godot/src/Multiplayer/NetworkCommand.cs'
  - '{project-root}/godot/src/Multiplayer/DedicatedServer.cs'
  - '{project-root}/godot/src/Multiplayer/LockstepManager.cs'
warnings: [oversized]
---

<intent-contract>

## Intent

**Problem:** The 1v1 relay is hardwired to two players and trusts client-supplied identity. `DedicatedServer.RelayTickCommands` forwards each client's single-faction `TickCommands` verbatim to `other = 1 - fromSlot` and raw to spectators; the client (`LockstepManager`) self-applies its own commands then the one remote stream in a peer-asymmetric order (each peer applies *its own* faction first), demuxes spectator packets by the client-controlled `cmdFaction` byte (collapsing Player3-8 into one stream), and gates ticks on hardcoded two-slot readiness (`p1Ready/p2Ready`, `_remoteArrived` single stream). Chat faction is a decorative, spoofable payload byte the server rebroadcasts raw. There is no authoritative merged-tick truth, no `TickCommandsMerged` type, and the whole merge/apply path is Godot-coupled so it is untestable at Tier-1 — the #1 FR-39 determinism regression risk.

**Approach:** Introduce a server->client-only `TickCommandsMerged` packet and move the determinism-critical logic into **Godot-free cores under `src/Multiplayer/Server/**`** (auto-globbed into the Tier-1 assembly per `SimSources.props:69`): a `MergedTickBuilder` (per-tick fan-in that re-stamps faction from the authoritative slot, sorts sub-bundles ascending by faction id, and drops — never clamps — on faction mismatch / over-count / byte-ceiling, hard-rejecting a merged-shaped packet from a client) and a single `MergedTickApplier` (applies sub-bundles per-faction ascending). Rewire the live Godot nodes to delegate to these cores: the server fans in and broadcasts one merged packet per tick to all peers; the client gates on a single merged-arrival flag and applies the merged packet as its **sole** command source (it no longer self-applies), so every peer and spectator applies byte-identical merged bytes in the same order. Re-stamp chat faction from `SLOT_FACTION[fromSlot]`. Replace the server's `_ready[0]&&_ready[1]` lobby logic with a `connected==expected && ready==expected` count machine. Lock the rewrite with an N=2 golden replay proving byte-identical SimChecksum to the pre-rewrite direct-apply baseline.

## Boundaries & Constraints

**Always:** All determinism-critical logic (re-stamp, sort, drop rules, apply order) lives in Godot-free types under `src/Multiplayer/Server/**` or `NetworkCommand.cs` (both already in the Tier-1 assembly) — never inside `DedicatedServer`/`LockstepManager`/`ServerTransport` (Godot-coupled, un-testable at Tier-1). Slot identity is transport-authoritative: faction is always re-stamped from `SLOT_FACTION[sourceSlot]`, never trusted from a packet byte. Ceilings (`MAX_ORDERS`, a new `MERGED_MAX_BYTES`, a new `MERGED_MAX_SUBBUNDLES`) are enforced as **drop-not-clamp** (follow the `AppendOrder`/read-side-reject precedent, not the silent write-side clamp). Merged sub-bundles are sorted ascending by faction id at build so wire order *is* the canonical apply order. The client applies **only** the server-built merged packet (its own commands included, re-stamped) — one deterministic apply order for players and spectators alike. The merged packet is server->client only; a `PacketType.TickCommandsMerged` received from a client is hard-rejected.

**Block If:** Any existing committed golden moves (`golden-scenario` N=2, `golden-multifaction` N=4, `golden-applier-scenario`, `SimChecksumCoverageGuardTest` pinned `0x1A47DE11`) — the rewrite touches networking, not the sim fold, so every existing golden must stay byte-identical with `SimChecksum.AlgoVersion` unchanged. A moved golden means an unintended sim-path change: STOP, do not re-baseline. Also block if landing the new merged apply as the client's sole command source cannot reproduce the pre-rewrite order semantics under any interpretation (a genuine ambiguity in "unit orders in wire order, then DSL events") — but note the current per-bundle apply already applies orders (DSL events included) in wire order, so preserving that per-faction is the byte-identical target.

**Never:** Do NOT bump `SimChecksum.AlgoVersion` or re-record any existing committed golden (no new folded sim field — the wire path is not folded). Do NOT raise `ServerTransport.MAX_PLAYERS` beyond its current value or enable/verify >2 live players — the count machine and cores become N-shaped (no hardcoded 2), but actually enabling and soak-verifying N=3/N=4 transport is Story 9.7 (Nakama N matchmaking) / 9.15 (four-player e2e). Do NOT touch the presentation slot caps deferred by Story 9.2 (`MatchChatOverlay` Player5-8 labels/colors, map-authoring UI 4-caps) — chat *faction re-stamp* (the spoof fix) is in scope; Player5-8 *display labels* stay deferred to Story 9.5. Do NOT make the server re-run the sim (a re-simulated server vote is a later D3 item) — the server assembles the merged packet; clients run the sim. Do NOT add server-dictated input delay, start-state agreement, or PROTOCOL_VERSION/rulesetHash gates — Story 9.4.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Faction re-stamp | player at slot s submits `TickCommands` claiming faction f | bundle faction set to `SLOT_FACTION[s]` regardless of f | claimed f is ignored, not an error |
| Faction spoof | claimed faction ≠ `SLOT_FACTION[s]` | the whole bundle is DROPPED (not silently re-stamped) | logged, dropped |
| Over-count bundle | order count > `MAX_ORDERS` (32) on read | bundle DROPPED, not clamped | read returns false → drop |
| Merged-from-client | client sends `PacketType.TickCommandsMerged` | hard-rejected, no state change | logged, dropped |
| Byte ceiling | assembling merged would exceed `MERGED_MAX_BYTES` / `MERGED_MAX_SUBBUNDLES` | the overflowing sub-bundle is DROPPED (deterministic, ascending scan), not clamped | logged, dropped |
| Fan-in complete | all expected players submitted tick T | exactly one `TickCommandsMerged` for T emitted, sub-bundles ascending by faction id, broadcast to all peers (players + spectators) | emitted once; late/duplicate submit for T ignored |
| Fan-in incomplete | fewer than expected submitted for T | no merged packet emitted yet | server buffers, waits |
| Chat re-stamp | player at slot s sends `Chat` with faction byte f | rebroadcast with faction = `SLOT_FACTION[s]` (spoof fixed); spectator sender → `Faction.Neutral` | re-encoded via `MakeChat` |
| Client tick gate ready | merged packet for currentTick has arrived | apply all sub-bundles ascending, then advance one tick | single merged-arrival flag |
| Client tick gate stall | merged packet for currentTick absent | stall (`IsStalling=true`), do not advance | no partial apply |
| Lobby start | connected players == expected AND ready == expected | transition InGame, build `MergedTickBuilder(expected, SLOT_FACTION)` | not before both counts meet expected |
| N=2 golden | 2-faction scenario, deterministic per-tick P1+P2 order stream | merged-path SimChecksum sequence byte-identical to the direct-apply baseline AND to a committed golden | divergence → fail with located tick |

</intent-contract>

## Code Map

- `godot/src/Multiplayer/NetworkCommand.cs` -- Godot-free, in Tier-1. `PacketType` (:13-52, free slot `0x14`) → add `TickCommandsMerged = 0x14`. `TickCommandPacket` (:472-829): `HEADER_BYTES=7`, `MAX_ORDERS=32`, faction=payload byte (:503/:531), `UnitOrder.SIZE=11`, LE helpers `WriteUint/ReadUint` (:800-828), chat codec `MakeChat`/`TryReadChat` (:705-734). Add the `TickCommandsMerged` codec + `MERGED_MAX_BYTES`/`MERGED_MAX_SUBBUNDLES` here (co-located with the order/DSL layout it wraps).
- `godot/src/Multiplayer/Server/MergedTickBuilder.cs` -- **NEW.** Godot-free per-tick fan-in: ctor `(int expected, Faction[] slotFaction)`; `Submit(int sourceSlot, byte[] data, int len)` decodes a `TickCommands` (rejecting a merged-shaped packet), re-stamps faction, drops on spoof/over-count, buffers per (tick,slot); `TryBuild(uint tick, out byte[] merged, out int len)` emits once all expected submitted, sub-bundles ascending by faction id, byte-ceiling drop-not-clamp.
- `godot/src/Multiplayer/Server/MergedTickApplier.cs` -- **NEW.** Godot-free static `Apply(byte[] merged, int len, EntityWorld world, DslEventSink dslSink)`: decode `TickCommandsMerged`, iterate sub-bundles in wire order (ascending faction) applying each via existing `OrderApplier.Apply(world, in order, subBundleFaction, dslSink)`. The single apply core for client player path, spectator path, and the golden.
- `godot/src/Multiplayer/DedicatedServer.cs` -- Godot node. `RelayTickCommands` (:267-289) → `Submit` into a `MergedTickBuilder` field, and on `TryBuild` `BroadcastReliable(merged)` to all peers on `CH_COMMANDS`. `HandlePacket` (:175-219): add a hard-reject arm for `TickCommandsMerged` from a client; `Chat` case (:211-217) re-stamp faction from `SLOT_FACTION[slot]`. `HandleReady` (:223-259): replace `_ready[0]&&_ready[1]` with a `connected==expected && ready==expected` count machine; build the `MergedTickBuilder` at InGame. `HandleDisconnect` `int other = 1 - slot` (:162) → notify all other connected players.
- `godot/src/Multiplayer/ServerTransport.cs` -- Godot transport. `MAX_PLAYERS=2`/`MAX_SLOTS=4` (:23-24) unchanged. Merged packet broadcasts to ALL connected peers via `BroadcastReliable` on `CH_COMMANDS` (players + spectators) — `BroadcastCommandsToSpectators` (:146-150) is superseded for the merged path (spectators now receive the merged packet, not raw per-faction).
- `godot/src/Multiplayer/LockstepManager.cs` -- Godot client. `Flush` (:326) + `HandleTickCommands` (:545-577): replace the two-stream (`_localBuf`/`_remoteBuf`) demux, self-apply, and `p1Ready/p2Ready` / single-`_remoteArrived` gating with a single merged-arrival ring (`_mergedArrived[mod]`, `_mergedTickFor[mod]`, stored merged bytes) applied via `MergedTickApplier.Apply`. Client still SENDS its own single-faction `TickCommands` (:unchanged send path) but applies only the server's merged echo. Chat send/`OnChatReceived` (:456-526) unchanged (server does the re-stamp).
- `godot/ProjectChimera.Sim.Tests/Golden/GoldenChecksumReplay.cs` + `GoldenScenario.cs`/`MultiFactionScenario.cs` -- the `RunAndRecord(ticks, perturb, build)` + committed-`.golden.txt` pattern to copy for the N=2 merged golden (mirror `Server/ServerBootstrapDeterminismTests.cs`).

## Tasks & Acceptance

**Execution:**
- `godot/src/Multiplayer/NetworkCommand.cs` -- add `PacketType.TickCommandsMerged = 0x14`; add a `MergedTickPacket` static codec (write/`TryRead`) for wire `type(1) + tick(4 LE) + subBundleCount(1) + [faction(1) + orderCount(1) + orders(orderCount*11)]…`, ascending-faction sub-bundles; add `MERGED_MAX_BYTES` and `MERGED_MAX_SUBBUNDLES` consts; read-side rejects (returns false) on any ceiling breach -- gives an authoritative, deterministic merged wire layout co-designed with the order/DSL layout it wraps.
- `godot/src/Multiplayer/Server/MergedTickBuilder.cs` (NEW) -- per-tick fan-in with re-stamp / spoof-drop / over-count-drop / merged-from-client hard-reject / ascending sort / byte-ceiling drop-not-clamp; emits one merged packet per tick once all expected players submit -- the Godot-free authoritative merge (the SD-1/SD-2 build half).
- `godot/src/Multiplayer/Server/MergedTickApplier.cs` (NEW) -- decode + apply sub-bundles ascending via `OrderApplier.Apply` -- the single deterministic apply order shared by client, spectator, and golden (SD-3 consume half).
- `godot/src/Multiplayer/DedicatedServer.cs` -- wire the builder into `RelayTickCommands`; broadcast merged to all peers; hard-reject merged-from-client; re-stamp chat faction from `SLOT_FACTION[fromSlot]`; replace `_ready[0]&&_ready[1]` with the count machine; generalize the disconnect `1-slot` notify -- makes the relay the single authoritative source of merged tick truth and closes the faction-spoof + chat-spoof + merged-from-client holes.
- `godot/src/Multiplayer/LockstepManager.cs` -- single merged-arrival gate + `MergedTickApplier` as the sole command source (remove two-stream demux, self-apply, peer-asymmetric order, `p1Ready/p2Ready`); spectator path consumes the same merged packet -- one N-scalable deterministic client apply order.
- `godot/src/Multiplayer/ServerTransport.cs` -- broadcast the merged command packet to all connected peers on `CH_COMMANDS` -- spectators and players ride the same authoritative merged path.
- `godot/ProjectChimera.Sim.Tests/Server/MergedTickBuilderTests.cs` + `MergedTickApplierTests.cs` + `godot/ProjectChimera.Sim.Tests/Golden/MergedTickPacketTests.cs` (NEW) -- unit-test every I/O-matrix edge: re-stamp, spoof-drop, over-count drop-not-clamp, merged-from-client hard-reject, ascending sort, byte-ceiling drop, fan-in completion, codec round-trip + read-side ceiling rejects, chat re-stamp round-trip (`TryReadChat`→`MakeChat(SLOT_FACTION[s])` reads back the slot faction).
- `godot/ProjectChimera.Sim.Tests/Golden/MergedTickN2Scenario.cs` + `MergedTickGoldenTests.cs` + `golden-merged-n2.golden.txt` (NEW) -- 2-faction scenario driven by a deterministic per-tick P1+P2 `UnitOrder` stream; record via `GoldenChecksumReplay.RunAndRecord`; assert (a) merged-path sequence == committed golden, (b) two in-process runs `SequenceEqual`, (c) merged-path == direct-apply baseline (same orders applied per-faction ascending without the merge) -- the FR-39 N=2 regression gate proving the rewrite is byte-identical to pre-rewrite semantics.

**Acceptance Criteria:**
- Given client `TickCommands` (single-faction, client->server), when the server fans them in, then it builds a distinct `TickCommandsMerged` (server->client only) with faction re-stamped from `SLOT_FACTION[sourceSlot]`, sub-bundles sorted ascending by faction id, dropping bundles on faction mismatch or over-count, and a merged-shaped packet received from a client is hard-rejected, and `MERGED_MAX_BYTES` / per-sub-bundle `MAX_ORDERS` ceilings are enforced by drop, not clamp.
- Given the merged packet on the client, when the client gates a tick, then it waits on a single merged-arrival flag and applies per faction ascending (unit orders in wire order), the merged packet is its sole command source, and the two-slot `p1Ready/p2Ready` / self-apply logic is gone.
- Given the server lobby, when readiness is evaluated, then start is gated by `connected==expected && ready==expected` (no `_ready[0]&&_ready[1]` literal), N-shaped with expected sourced from the connected player count.
- Given a spectator joins, when it ingests commands, then it consumes only the server-built merged output and renders all factions, and chat faction is re-stamped from `SLOT_FACTION[fromSlot]` (chat spoof fixed).
- Given the N=2 golden replay, when the merged path runs as N=2 of the new format, then its SimChecksum sequence is byte-identical to the committed pre-rewrite direct-apply baseline (FR-39 regression gate), and every pre-existing committed golden stays byte-identical with `AlgoVersion` unchanged.

## Design Notes

**Why the merge core is Godot-free (the load-bearing testability decision):** `DedicatedServer`/`ServerTransport`/`LockstepManager` all `using Godot;` and are excluded from the Tier-1 assembly; `src/Multiplayer/Server/**` is folder-globbed IN (`SimSources.props:69`, explicitly reserved for Epic-9 additions). Putting the merge/apply logic in `Server/` cores makes the FR-39 golden exercise the *real* merge code (not a duplicate), while the Godot nodes become thin adapters. This is the D3 seam the epic-9 context reserved (`DedicatedServer.cs:57` comment).

**Why the client stops self-applying (the determinism invariant):** today each peer applies its own commands locally first, so the two peers apply the same orders in *different* order (P1 applies P1→P2, P2 applies P2→P1) — safe today only because the sim happens to be order-insensitive for the tested cases, a latent hazard. Server-authoritative merged tick means every peer applies the identical merged bytes in identical ascending-faction order. The client's own bundle round-trips through the server and comes back in the merged packet; the existing lockstep input delay already absorbs this. This is what "a single deterministic apply order that scales to N players" means.

**Drop-not-clamp (deterministic overflow):** a clamp silently changes bundle contents differently across peers if inputs differ; a drop is a total, reproducible decision. Follow `EntityWorld.AppendOrder`'s deterministic reject and `TickCommandPacket.TryRead`'s read-side over-count reject (:534), NOT the silent write-side clamp (:489).

**DSL events:** they ride inside a bundle as `UnitOrder`s with `Command==DslEvent` (`OrderApplier.Apply` :201-214, raiser slot derived from `expectedFaction`). The merged applier preserves the existing per-bundle wire order (orders and DSL-event orders in the order sent) per faction — do NOT re-separate or re-order them; the golden locks that this preserves the pre-rewrite sequence.

## Verification

**Commands:**
- `dotnet build godot/godot.csproj` -- expected: compiles clean; determinism banned-API analyzer green (no `float`/`Dictionary`-enumeration in the new Godot-free cores).
- `dotnet test godot/ProjectChimera.Sim.Tests` -- expected: all pass incl. the new `MergedTick*` + N=2 merged golden; **every pre-existing golden byte-identical** (moved golden = Block-If, not a re-baseline).
- `dotnet test godot/ProjectChimera.Sim.Tests --filter FullyQualifiedName~MergedTick` -- expected: builder/applier/packet edge cases + N=2 golden green.
- `dotnet test godot/ProjectChimera.Sim.Tests --filter "FullyQualifiedName~Golden|SimChecksumCoverageGuard"` -- expected: `golden-scenario`/`golden-multifaction`/`golden-applier` unchanged; pinned `0x1A47DE11` unchanged; `AlgoVersion` unchanged.

**Manual checks (Godot-side integration, not Tier-1):**
- `godot --headless -- --loopback-test` (`LoopbackDesyncSelfTest`) -- expected: still `RESULT: PASS` — the real `DedicatedServer` + 2 ENet clients complete the handshake (now via the count machine) and the clean-PASS + HALT phases pass after the rewrite.

## Review Triage Log

### 2026-07-23 — Review pass
- intent_gap: 0
- bad_spec: 0
- patch: 4: (high 0, medium 2, low 2)
- defer: 2: (high 0, medium 2, low 0)
- reject: 1: (high 0, medium 0, low 1)
- addressed_findings:
  - `[medium]` `[patch]` `MergedTickBuilder.Submit` keyed the fan-in ring by the unbounded client-controlled `tick` with no window/ordering guard and no one-submit-per-slot guard — a duplicate submit was last-writer-wins, and an out-of-window/aliased tick (`T` vs `T+RING`) re-keyed a live ring slot and wiped an honest peer's arrived state → permanent match stall (grief/DoS). Fixed by mirroring `ServerChecksumCollector`: a `_resolvedThrough` emitted high-water + `ACCEPT_WINDOW` drop of stale/implausibly-far ticks, re-key only strictly forward, and duplicate `(slot,tick)`-after-arrival is an idempotent no-op (first bundle survives). Added 3 builder tests.
  - `[medium]` `[patch]` The N=2 FR-39 golden scenario was apply-order-insensitive (each faction ordered only its own disjoint units, and plain unit orders proved commutative in this sim), so the golden the AC names as the ordering gate could not fail on a sub-bundle apply-order flip. Rebuilt `MergedTickN2Scenario` around a non-commutative DSL event fold (`g = g*3 + event.v`) so P1-then-P2 ≠ P2-then-P1; re-recorded `golden-merged-n2` for the strengthened scenario (only this new golden re-recorded); added `AscendingVsDescendingDirectApply_Diverges` teeth proving the golden now locks ascending-faction apply order.
  - `[low]` `[patch]` The chat spectator→Neutral re-stamp branch was asserted only in a comment (the one chat test hit a player slot). Extracted `ServerLobbyPolicy.StampChatFaction` (Godot-free) and added a table-driven test over all slots (players → own faction, spectators → Neutral).
  - `[low]` `[patch]` The N-shaped lobby count-machine (`connected==expected && ready==expected`, spectators excluded) that replaced `_ready[0]&&_ready[1]` had no covering test. Extracted `ServerLobbyPolicy.CountConnectedPlayers`/`CountReadyPlayers`/`ShouldStart` (Godot-free) and added tests (at-quorum starts, under-quorum does not, spectator not counted).
- deferred (see `deferred-work.md`): mid-match disconnect does not shrink `MergedTickBuilder.Expected`/force-empty-emit → survivors stall (deterministic freeze-and-continue is Story 9.6, per the epic dependency chain; pre-rewrite also stalled on a missing stream); the client-side merged-arrival ring/gate in `LockstepManager` is exercised by no automated test (Godot-coupled; loopback drives only Ready+Checksum, never the merged ring) — low/fail-loud residual risk (a ring bug stalls rather than desyncs; the applier math is Tier-1-tested), closure = extract the ring/gate into a Godot-free helper + tests.
- rejected: "byte-identical to the pre-rewrite direct-apply baseline" oversells parity with the old per-peer-asymmetric order — the spec's Design Notes deliberately canonicalize ascending apply order as the baseline, and Patch C makes `MergedPath_EqualsDirectApplyBaseline` a meaningful order-locking assertion, so the baseline being ascending-direct-apply is correct-by-intent, not a defect.

### 2026-07-23 — Review pass (follow-up)
- intent_gap: 0
- bad_spec: 0
- patch: 4: (high 0, medium 1, low 3)
- defer: 2: (high 0, medium 1, low 1)
- reject: 12: (high 0, medium 3, low 9)
- addressed_findings:
  - `[medium]` `[patch]` `MergedTickApplier.Apply` forwards a long positional tail of presentation delegates (`onRequestPath`/`onRequestAttackMove`/`onCancelPath`/buildings/items/research) to `OrderApplier.Apply`, but every applier/golden test issued only `Move` orders with no hooks, so a transposition of two adjacent delegates would silently break live Build/attack-move while the whole suite stayed green (Verification-Gap finding). Added `ForwardsPresentationHooks_ToTheRightFactionsUnit`: a P1 `Move` must route to `onRequestPath` for the P1 unit and a P2 `AttackMove` to `onRequestAttackMove` for the P2 unit, with `onCancelPath` never firing — pinning the three most-transposition-prone delegates against a slot swap.
  - `[low]` `[patch]` `MergedTickPacket.TryPeekTick` — the client's merged-ring KEY (`LockstepManager` slots merged bytes by its return) — was defined once, used once, and asserted by zero tests, so a byte-order slip or a too-short accept would mis-key the ring (stall/mis-apply) uncaught. Added `TryPeekTick_ReadsSameTickAsFullDecode`: peek agrees with the full decode's tick and rejects wrong-type / sub-header lengths.
  - `[low]` `[patch]` The one recorder-hook test (`RecorderHook_FiresPerSubBundle_Ascending`) discarded the hook's `buf`/`baseIdx`/`count`, so a hook feeding the replay recorder the wrong order slice or count would pass while the golden (no recorder) stayed green — the exact live-vs-replay divergence class the old dual-`RecordTick` path was meant to close. Added `RecorderHook_ForwardsCorrectSlice_PerSubBundle` asserting each faction's own unit id at its `baseIdx` with the right count.
  - `[low]` `[patch]` The re-stamped chat path dropped an undecodable `Chat` silently (the old relay rebroadcast raw bytes unconditionally), unlike the merged-reject arm which logs. Added an `else` branch logging the drop (slot + byte length) so a malformed chat is observable, not silent.
- deferred (see `deferred-work.md`): `MergedTickApplier.Apply` allocates three scratch arrays per call (per tick, per client + spectator) against the builder's zero-alloc discipline — a perf/GC-pressure hardening, cannot desync; and the client-sent-`TickCommandsMerged` hard-reject logs unthrottled `GD.PrintErr` per packet (soft log-write DoS surface on a server-authoritative posture). Both are new, distinct from the two client/disconnect items already deferred by the prior pass (not re-appended).
- rejected: mid-match disconnect stall and the untested client-side `LockstepManager` merged ring/gate (both already deferred by the prior pass — not re-logged); the ascending-parity "oversells pre-rewrite" reading (already rejected as correct-by-intent); the partial-lobby-can't-start-at-N=8 and N-scaling `Expected`-immutability concerns (enabling N>2 is explicitly Story 9.7/9.15 per intent's Never-list); the client peek-accepts/apply-discards asymmetry (subsumed by the already-deferred client-ring coverage gap; improbable on a reliable-ordered channel); `MergedTickPacket.Write`/`TryRead` write-side-trusts-caller and out-of-range-faction-byte (defensive; the only writer is the ceiling-enforcing builder and the type is server→client only); the unreachable byte-ceiling `continue` (exact-fit proven by `FullCapacity_FitsExactlyInByteCeiling`); the duplicate-faction insertion-sort assert (`SLOT_FACTION` distinct by construction); the `uint` tick wraparound (~4.5-year single-match uptime); and the cross-file `RING`/`ACCEPT_WINDOW`/`MAX_DELAY` coupling (documented by prose, 32-vs-24 comfortable margin).

### 2026-07-23 — Review pass (follow-up 2)
- intent_gap: 0
- bad_spec: 0
- patch: 0
- defer: 2: (high 0, medium 2, low 0)
- reject: 16: (high 0, medium 4, low 12)
- addressed_findings:
  - none
- deferred (see `deferred-work.md`): a client-triggered drop of an intermediate tick's bundle (faction spoof / over-count / malformed) makes that tick's fan-in never complete, so with input delay later ticks may already have emitted — a permanent, non-contiguous, no-HALT match freeze; distinct trigger from the already-deferred *disconnect* freeze (a griefing/robustness vector, closure with 9.6's freeze-and-continue). And the server-side `DedicatedServer` delegation (fan-in `Submit`→`TryBuild`→broadcast wiring, the `Chat` re-stamp branch, the client-sent-merged hard-reject dispatch) is exercised by no automated test — the server-node sibling of the already-deferred client-ring gap; the extracted cores are Tier-1-tested, only the thin Godot-coupled wiring is uncovered. Both are new, distinct from the four items deferred by the prior passes (not re-appended).
- rejected (this pass re-surfaced items already triaged by the two prior passes, plus new defensive noise): the out-of-range enum-byte decode and `Write` trusts-caller (already rejected — server→client only, sole writer is the ceiling-enforcing builder); the unreachable byte-ceiling/sub-bundle-count drop valves (already rejected — exact-fit proven; documented defensive code); the header-only-peek corrupt-body "desync" (already rejected — server-built + reliable channel is fail-consistent for all peers, not a per-peer divergence); the ascending-vs-pre-rewrite "byte-identical oversell" and the golden-is-self-referential framing (already rejected as correct-by-intent — canonical ascending is the deliberately-chosen baseline, per-faction byte-identity is the stated block-if target); the client ring-size vs server ring coupling (already rejected — comfortable margin, prose-documented); the `uint` tick wraparound (already rejected — multi-year single-match uptime); the duplicate-faction/keep-first single-slot assertion (already rejected — `SLOT_FACTION` distinct by construction); the mid-match disconnect freeze, the untested client-side `LockstepManager` merged ring/gate, the per-tick applier scratch allocation, and the unthrottled merged-reject log (all four already deferred by the prior passes — not re-logged); and `MergedTickBuilder` storing the caller's `slotFaction` by reference (defensive — the sole caller passes `DedicatedServer.SLOT_FACTION`, a `static readonly` array never mutated; no current trigger, same trusted-caller class as the already-rejected `Write`).

## Auto Run Result

Status: done (follow-up review pass 2 on already-`done` work — no code change)

### Summary
Third independent review pass over the server-authoritative merged-tick rewrite (Story 9.3), run against the full diff since baseline `2cfb324`. Four adversarial lenses (Blind Hunter, Edge-Case Hunter, Verification-Gap, Intent-Alignment) were run in parallel. Cross-checked every finding against the two prior review passes already recorded in the triage log. The overwhelming majority were re-surfaces of items already rejected (as correct-by-intent / defensive-on-a-trusted-caller / not-practically-reachable) or already deferred (disconnect freeze, client-ring coverage, applier allocation, merged-reject log). Two genuinely new, real findings were deferred; no patch or spec-repair was warranted; verification re-run confirms the reviewed diff still holds green with no goldens moved.

### Files changed this pass
- `_bmad-output/implementation-artifacts/spec-9-3-...-golden-gate.md` — status/followup frontmatter, third triage-log entry, this Auto Run Result (workflow bookkeeping; no code).
- `_bmad-output/implementation-artifacts/deferred-work.md` — appended 2 new defer entries (client-triggered fan-in freeze; untested server-side node delegation).

No source or test files were changed.

### Review findings breakdown
- Patches applied: 0
- Deferred (2, both new/distinct from the four already on the ledger): (1) a client-triggered drop of an intermediate tick's bundle → incomplete fan-in → permanent no-HALT match freeze (griefing/DoS vector, distinct trigger from the deferred *disconnect* freeze); (2) server-side `DedicatedServer` delegation (fan-in wiring / chat re-stamp / merged-reject dispatch) exercised by no automated test (server-node sibling of the deferred client-ring gap).
- Rejected (16): see the triage-log `rejected` line — all either already-triaged by the prior two passes or new defensive noise on a trusted server→client, ceiling-enforced path.

### Follow-up review recommendation
`false`. Patched findings this pass = 0 (none high; score `3×0 + 1×0 = 0 < 5`).

### Verification performed
- `dotnet test godot/ProjectChimera.Sim.Tests --filter "FullyQualifiedName~MergedTick|FullyQualifiedName~Golden|FullyQualifiedName~SimChecksumCoverageGuard"` → **200 passed / 0 failed / 0 skipped**. All `MergedTick*` cores + N=2 merged golden green; `golden-scenario`/`golden-multifaction`/`golden-applier` byte-identical; pinned `0x1A47DE11` and `AlgoVersion` unchanged (no golden moved — the Block-If tripwire held).
- Full suite / build not re-run beyond the targeted filter: this pass changed zero source, so the twice-verified `done` build state (last recorded: 3029 passed / 1 skipped / 0 failed) is unchanged.

### Residual risks
- The two newly-deferred items are real but low-to-medium and sequenced (freeze-and-continue → Story 9.6; the untested Godot-coupled server/client wiring is the intent-accepted "cores are tested, thin nodes are not" architectural consequence, closure = extract the delegation seams or add a loopback command/chat path).
- The FR-39 golden locks the *canonical ascending* apply order (the deliberately-chosen baseline), not a captured pre-rewrite peer-asymmetric checksum — correct by intent, but worth remembering the gate proves determinism + per-faction byte-identity + ascending-order-lock, not literal equality to the retired heterogeneous per-client order.


