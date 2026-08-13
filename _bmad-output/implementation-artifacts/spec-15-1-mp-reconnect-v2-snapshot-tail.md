# Spec 15-1 — MP reconnect, v2-first: snapshot + tail (FR-79, DW-2)

**Status:** SERVER + PROTOCOL PLUMBING BUILT + GREEN (2026-08-12, same session; 15-1b in-engine client bootstrap remains)
**Supersedes:** the v1 replay-from-start scoping. The 2026-08-06 "v2 needs a new save/restore — bigger
lift" premise is RETIRED: `SaveGameFile` v7 round-trips the complete live sim, stream-agnostic.

## The shape (one paragraph)

The server is a relay — the sim lives on clients — so the rejoin state comes from a LIVE PEER: on an
authorized rejoin request the server asks one surviving client for a snapshot (SaveGameState.CaptureFrom at
a tick boundary → SaveGameFile.WriteBody to memory → chunked upload), retains every merged tick it emits
into a new server-side MergedTickLog from the moment a slot freezes (cheap: ~tens of bytes/tick), relays
snapshot + the command tail (snapshotTick..now) to the rejoiner, who restores, fast-forwards the tail
headless, then enters the live stream at a server-dictated resume tick with an all-survivor-ACKed
ResumeDirective — the exact dual of the Story 9.6 DropDirective. Determinism makes the snapshot trustworthy:
any peer's capture at tick T is byte-identical, and the post-catch-up checksum quorum re-proves it.

## Design decisions

- **D-1 — Snapshot donor = any surviving player client** (lowest connected player slot). The snapshot body
  carries SaveGameFile's own integrity checksum; the rejoiner's first checksum window after resume re-proves
  state agreement END-TO-END. A malicious/buggy donor cannot corrupt silently — it desyncs loudly.
- **D-2 — Server MergedTickLog** starts retaining at freeze-commit (not tick 0 — bounded memory, and only
  frozen-slot matches need it). Tail = log[snapshotTick+1 .. EmittedThrough]. If the log doesn't reach back
  to the snapshot tick (snapshot older than freeze), refuse and re-request a fresher snapshot.
- **D-3 — Capture boundary:** the donor captures BETWEEN ticks (the save path's own legality window — the
  DeathLog-drained assert), at a tick T with T > EmittedThrough at request time, tagged with T.
- **D-4 — The thaw sequence** (the recon's derived inverse of the freeze-commit seam, all tick-counted):
  identity-verified InGame connect branch (closes DW-599's state-flip; the match NEVER leaves InGame) →
  snapshot+tail transfer → ResumeDirective(faction, resumeAtTick) with the DropController ACK discipline
  (all surviving players) → injection handoff at the boundary (injector owns ticks < resumeAtTick, the
  rejoiner owns >=; never a same-tick race — Submit is first-wins) → checksum-quorum re-admit
  (AddExpectedReporter, the dual ServerHost.cs:208-209 anticipates, keyed to the first window at/after
  resumeAtTick) + DelayController.ReactivateSlot → normal fan-in.
- **D-5 — Rejoin identity (v1 of DW-200's rail, LAN-grade):** the rejoiner presents a per-match RejoinToken
  the server minted and sent to every player at StartGame (random 64-bit, per-slot). Proves "the same
  person who held this slot", with zero Nakama dependency — LAN-safe. Story 15-14 upgrades the token to a
  Nakama-account bind for public matches (the same seam, stronger mint).
- **D-6 — Verdict gate at the door:** faction latched VERDICT_LOST → admit as loser-in-place (the latch is
  checksum-folded and monotone; nothing to forgive); match IsFullyResolved → refuse (it's over).
- **D-7 — AI plan trap (recon):** the rejoiner re-establishes `SceneContext.OnlineAiPlan` (None) — NEVER the
  SP-load `SetControlPlan(OfflineDefault)` path, which would re-arm the AI on one machine and desync.
- **D-8 — Delay handoff:** the resume payload carries the CURRENT dictated delay (the rejoiner missed every
  DelayDirective; delay state is not in the save).
- **D-9 — Wire:** new PacketTypes (RejoinRequest, RejoinAccept/Refuse, SnapshotRequest, SnapshotChunk,
  TailChunk, ResumeDirective, ResumeAck) + PROTOCOL_VERSION 5→6. Chunked reliable transfer (ENet reliable
  channel; ~32KB chunks; snapshot ≈ tens of KB at realistic entity counts).
- **D-10 — Harness first (DW-879):** extend the LoopbackPeerSim pattern (per-peer independent
  SimulationHosts + a scripted in-memory relay) into a Godot-free two-sim rejoin harness: run N ticks, drop
  peer B, keep peer A running with injected empties, capture A's snapshot, restore into a FRESH host,
  fast-forward the tail, assert byte-equal SimChecksum sequences from resumeAtTick on. This is the story's
  acceptance criterion, runnable in Tier-1 — the LAN rig then only confirms transport.
- **D-11 — DW-410 designed-in:** both directive quorums (Drop AND Resume) get the missing ACK timeout →
  refuse/rollback instead of wedging forever. (Closes DW-410 as a rider.)

## Ledger interactions

Closes DW-2 (the direction entry), DW-599 (InGame connect branch), DW-879 (harness), DW-410 (rider).
15-14 (DW-200) follows immediately on the D-5 seam. DW-435 stays open (needs live Nakama).

## Out of scope (recorded)

AI-takeover-while-disconnected (needs Epic 10's AI float→Fixed, DW-204); Nakama-relay topology (DW-404);
mid-match NEW spectator join (different feature); reconnect across a server restart (the log/tokens are
in-memory per-match).

## Implementation record (2026-08-12)

Everything below D-1..D-11 is BUILT and Tier-1-verified in one pass; suite 6951/0/1, release analyzer clean.

- **Wire (D-9):** `PacketType` 0x50-0x59 + `RejoinRefuseReason` + `HELLO_FLAG_INGAME_REJOIN`; codecs in
  `TickCommandPacket` (all round-trip + malformed-reject tested). PROTOCOL_VERSION 5→6.
- **Server:** `Server/RejoinCoordinator.cs` — the whole state machine (request→refusals, donor pick, verbatim
  chunk relay, stop-and-wait tail, resume quorum, D-11 deadlines, disconnect unwinds). `DedicatedServer` is a
  thin adapter: DW-599 InGame connect branch, token mint at StartGame, MergedTickLog armed at freeze-commit and
  fed the exact merged broadcast bytes, mid-thaw disconnect RevertThaw + quorum re-drop, delay directives
  withheld while a rejoin is in flight.
- **The thaw seam (D-4, sharpened):** the injection bound is `resumeAtTick + delay` and is scheduled at
  ResumeDirective ISSUE — the injector owns ticks < bound, the rejoiner's own submissions own >= bound (its
  first submission is exec R at delay D → tick R+D exactly). No first-wins race is ever contested. The client
  must not submit below `FirstOwnedTick` (exposed on `RejoinClient`).
- **Quorum re-admit:** `ServerChecksumCollector.AddExpectedReporter(slot, fromTick)` + per-window expected
  counts — windows below the boundary quorum over survivors (stray catch-up reports dropped); at/after need
  everyone, so the first live window re-proves the donor snapshot end-to-end (D-1). `ServerHost.AddReporter`.
- **Delay re-admit (D-8):** `DelayController.ReactivateSlot` — fresh RTT history; excused from a directive
  that predates its return (the ResumeDirective carried the current delay).
- **Client protocol half:** `RejoinClient` + `SnapshotTransfer` (Godot-free, in SimSources) — request/accept/
  refuse, chunk assembly (fail-closed on disorder), tail apply + ACK cursor, resume handoff. `LockstepManager`
  stores its RejoinToken (`RejoinToken`/`RejoinTokenSlot`), survivors ACK ResumeDirective + fire
  `OnPlayerResumed`.
- **Proofs:** `RejoinCatchUpHarnessTests` (DW-879 — byte-equal checksums through drop→snapshot→tail→live),
  `RejoinProtocolTests` (codecs, thaw sequence, quorum re-admit, injector bound), `RejoinCoordinatorTests`
  (full coordinator↔client interlock over scripted seams + every refusal/timeout/disconnect arm).

**Remaining — story 15-1b (in-engine, human at the rig):** MainScene's reconnect bootstrap (detect the REJOIN
Hello → `RejoinClient` → restore the snapshot into a FRESH SimulationHost with `OnlineAiPlan=None` (D-7) →
headless fast-forward loop → enter live lockstep at `FirstOwnedTick` with the handed-over delay → suppress own
submissions below it), the "Rejoining…" UX, checksum-send suppression during catch-up, and the two-machine LAN
confirmation (transport only — D-10 says everything else is already proven).
