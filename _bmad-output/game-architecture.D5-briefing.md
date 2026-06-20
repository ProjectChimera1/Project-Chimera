# D5 Briefing - >2-Player Lockstep + Matchmaking (and the dedicated-server .csproj AOT project-split)

> **Status:** RECOMMEND-AND-CONFIRM. Everything below is a recommendation for Alec to confirm or override. D5 scales the as-built 2-player stack to up-to-8 players, adds the matchmaking/lobby/parties/spectator surface for it, and resolves the dedicated-server NativeAOT `.csproj` split that D3 handed forward.
>
> **Upstream fixed (this same architecture pass):** D1 Bounded Effect-Graph; D2 Typed Event/Dataflow Graph (DSL events on the lockstep command bus, replay v2, rulesetHash@lobby); D3 Unified deterministic schema/loader (Maximal-now), which ELEVATED the dedicated-server `.csproj` project-split into D5/engine-section scope.

---

## 1. Decision (recommended)

**Build the N-shaped merged-relay architecture once (Option A), but ship it at N<=4 (Option C scope) for 1.0** — with three items that the adversarial review proved are *not* the "add a byte" tweaks the original proposal assumed, and which are therefore **non-deferrable parts of the design**:

1. The **merged tick packet must be server-built and server-authoritative on faction** (re-stamped from the sender slot, distinct packet type so a client can't forge a merged packet), with a **fixed ascending-faction intra-tick application order** that also slots D2 DSL events.
2. The **checksum path must be inverted from P2P-local to a stateful server-side collector** (the server holds no hashes today; SD-5 is a topology change, not a slot-id byte).
3. **SimChecksum must be widened to all active factions across all per-faction arrays** (it hashes only `Ore[Player1]/Ore[Player2]` and ignores Crystal/Supply entirely — a latent desync that blinds the very FR-39 gate D5 stands on).

The single headline framing — **"build Option A's architecture, ship Option C's scope"** — is the honest right-size for a solo dev on the M5 critical path: the wire format/buffers/checksum/faction-model are N-shaped from day one (so 8 is a constant-bump with no architectural regret), but the expensive, deferrable parts — the 8-peer soak, the parties lobby UI, the RelayCore/ITransport extraction, and the full PublishAot Linux build — are *verification breadth and hosting optimization*, not 1.0 player-facing requirements, and slip cleanly to a post-1.0 fast-follow.

This is a strangler on top of a relay that already works and is already "dumb": `DedicatedServer` explicitly does not run the sim (DedicatedServer.cs:14-16); clients run identical deterministic sims and exchange checksums through relayed packets. The deepest seam to confront is the **single-opponent assumption** — `int other = 1 - slot` (DedicatedServer.cs:116, 155, 218) and the lone `_remoteBuf/_remoteArrived/_remoteTickFor` stream gated at LockstepManager.cs:303 — and the recommendation *collapses* it into one merged-packet readiness model rather than *multiplying* it into N streams.

---

## 2. Why A*, not B, not plain-A, not plain-C

### Option A (plain) — merged relay, 8-now, do the AOT extraction now
The right *architecture*, wrong *scope posture*. Two corrections from the adversarial pass:
- Recommending "8 unconditionally" while conceding (in the same proposal) that 4-now/8-later is *regret-free* is gold-plating the deferrable surface: the architecture cost is identical either way; only the 8-peer soak + parties UI + PublishAot build differ, and those are exactly what gets deferred. **→ ship at N<=4 (SD-8).**
- "Do the RelayCore/ITransport extraction now" touches the validated relay (FR-39 #1 ship risk) for a post-1.0 payoff, and the proposal itself admits the sockets/managed-ENet `ITransport` impl is unverified until the PublishAot build runs — so the interface boundary would be *guessed*, not validated. **→ defer the extraction with the build (SD-11).**

### Option B — relay-each (keep the per-faction packet, forward to N-1)
Rejected, but its strongest counter-argument survives and is folded in. B's only real win is leaving the packet struct untouched (NetworkCommand.cs:86-154), which lowers FR-39 *packet* regression risk. Against that:
- **B makes the client gate harder, not easier:** it ANDs N-1 separate `_remoteArrived[slot][execMod]` streams exactly where desync bugs hide, whereas the merged packet is one arrival flag per tick.
- **B scales worse toward the 8 ceiling:** N*(N-1) sends per tick vs A's N-in / one-broadcast-out, on the surface where slowest-peer stalls already dominate.
- **B weakens the authoritative posture on the command path:** the server forwards rather than fans in, so it is not the single materialization point — and that materialization point is precisely what makes the checksum collector (SD-5) and faction re-stamp (SD-1) natural.

**Honest concession to B (verifier finding, folded in):** the codebase *already runs a working dual-stream ANDed gate* on the spectator path (LockstepManager.cs:251-253: `p1Ready && p2Ready`). So "N-stream gating is novel/risky" overstates A's edge — the pattern has prior art. The merged-packet *player* gate is still simpler, but A must *also* rewrite the spectator path either way (it demuxes single-faction packets by `cmdFaction`, LockstepManager.cs:408-424), so spectator-ingest rework is in A's change surface regardless and is counted here, not hidden (SD-2).

### Option C (plain) — ship N<=4, defer 5..8 + parties + AOT
C's *scope* is right; C-as-stated under-commits the *architecture* (it risked treating the 4→8 jump as "later"). The recommendation takes C's verification gate and parties/AOT deferral but builds A's N-shaped format/buffers/checksum so 8 is a constant-bump — hence **A* = A architecture + C scope.**

---

## 3. Settled / recommended sub-decisions

All numbered SDs are in the structured `subDecisionsToConfirm` block; summarized here with the code anchors and the ones that materially depend on an Alec call flagged.

- **SD-1 Merged, server-authoritative tick packet** — new `TickCommandsMerged` type (server→client only), client `TickCommands` stays single-faction (client→server only); server **re-stamps** faction = `SLOT_FACTION[sourceSlot]` and **drops** bundles on mismatch/over-count; sub-bundles **sorted ascending by faction id**. *Closes the merged-from-client and faction-spoof tamper holes; the single 0x10 type today (NetworkCommand.cs:102) has no direction discriminator.*
- **SD-2 One-slot buffering + fixed apply order** — gate on the single merged arrival; apply **per faction ascending: unit orders (wire order) then DSL events**; rewrite spectator demux. *Determinism linchpin once DSL events ride along.*
- **SD-3 Ready-count state machine** — `connected==expected && ready==expected` replaces `_ready[0]&&_ready[1]` (DedicatedServer.cs:32,179).
- **SD-4 Server-dictated delay (NET-NEW, ACK-gated, re-clamped)** — verified the server has **no** Ping/Pong/DelayProposal cases (DedicatedServer.cs:133-166), so "server already relays Ping/Pong" is false; this is new server RTT collection + authoritative broadcast + all-N-ACK commit + receipt-side `[2,12]` re-clamp (fixing the unclamped `Math.Max` at LockstepManager.cs:495).
- **SD-5 Server-side checksum collector (TOPOLOGY INVERSION)** — verified the server relays Checksum opaquely to `1-slot` (DedicatedServer.cs:148-156) and the *receiving peer* compares locally (LockstepManager.cs:366-373); SD-5 makes the server parse, buffer per-slot per-60-tick-window, majority-vote, name the minority, and **halt fail-closed on no-majority**. Slot is transport-authoritative (ServerTransport.cs:170), never a client byte. **Second wire/behavior change requiring N=2 golden re-verify.**
- **SD-6 Faction→Player8, Faction==player for 1.0** — extend the enum (stops at Player4, EntityWorld.cs:49-54), raise FACTION_COUNT (=5, ResourceStore.cs:9), audit every `(int)Faction` site; **convert ScenarioDirector threshold loop from float to Fixed** (verified `.ToFloat()` at :168, `ToString("F2")` at :170 — a float+locale leak that scales x8).
- **SD-7 Widen SimChecksum (NOW, broadly)** — verified it hashes only `Ore[Player1]/Ore[Player2]` (SimChecksum.cs:53-54) and **does not hash Crystal/SupplyUsed/SupplyCap at all**; widen to all active factions across all per-faction arrays + D1 SoA stores; one `checksum_algo_version` bump; guard test. (ConstructionTimer *is* already hashed, line 48 — minor correction to the verifier.)
- **SD-8 Shipped ceiling = 4-now / 8-fast-follow** — **ALEC CALL.** Default to 4; opt into 8 if M5 has slack. No architectural regret either way.
- **SD-9 Nakama N + parties; UI deferrable** — **ALEC CALL on parties-UI timing.** Parameterize `minCount/maxCount/countMultiple` (NakamaService.cs:132); parties use a *distinct* API; slot assignment stays server-side (not the lexicographic pick at NakamaService.cs:186-194); resolve the single-static-`GameServerIp/Port` routing assumption (NakamaService.cs:194) for non-LAN matches.
- **SD-10 Deterministic freeze-and-continue** — server-dictated idle-at-applyTick (ACK-gated, like a delay change); "idle" = empty commands injected each tick while K's passive sim continues identically; drop != removal from sim/checksum. Drop-to-AI is a D4 fast-follow.
- **SD-11 AOT analyzer now; extraction + build deferred** — **REVISED** from the original "extract now" per the scope lens; `EnableDynamicLoading=true` (godot.csproj:5) confirms the separate SDK-free project is required, but it is a post-1.0 hosting optimization.
- **SD-12 Replay v2, tagged body** — bump VERSION (ReplayRecorder.cs:25), header gets roster+faction-count+rulesetHash (:106-114), tagged record envelope mirroring SD-1; **co-design with D2 before freezing.**
- **SD-13 Start-state agreement before tick 0** — server-dictated initial delay in StartGame (today hardcodes startTick:0, DedicatedServer.cs:183); all-N seed identical empty range (generalize SeedInitialTicks, LockstepManager.cs:579-590); single start-state hash {roster+faction-count+initial-delay+rulesetHash+scenarioHash} compared fail-closed; land inbound PROTOCOL_VERSION reject (server ignores client Hello today, DedicatedServer.cs:135-137) and rulesetHash@lobby.

---

## 4. Decisions to confirm (the interactive calls)

1. **SD-8 — Shipped player ceiling:** 4-now/8-fast-follow (recommended default) vs 8-in-1.0? FR-40 *targets* 8; the soak is the cost.
2. **SD-9 — Parties lobby UI timing:** Nakama parties API now (cheap) with the *UI* deferrable (recommended) vs full parties UI in 1.0?
3. **SD-6 — Player model:** confirm Faction==player (extend enum to Player8) for 1.0 before the ~dozen `(int)Faction` index sites are touched; decoupled playerSlot only if teams/observers are wanted in 1.0.
4. **SD-11 — AOT posture:** analyzer-CI-now + extraction/build deferred (recommended, revised) vs extraction now?
5. **SD-9 routing:** static dedicated-server endpoint vs allocated instance for matchmade (non-LAN) N-player — confirm there is a server-allocation story or accept static-endpoint for 1.0.

---

## 5. Migration sequence (strangler, golden-checksum-gated, always-shippable)

Reordered per the scope lens so the value-dense, *low-FR-39-risk* fixes land and ship **before** the risky wire rewrite. Every step is golden-checksum-gated at N=2.

1. **(M1/M5 pre)** SimChecksum widen to all active factions + all per-faction arrays + guard test (SD-7); add the **slot-tagged Checksum packet + server-side collector** (SD-5) — *pure additions, no FR-39 packet regression, immediately improve diagnosability.* SD-5 does **not** depend on the tick wire format and must not wait behind it.
2. **(M5)** Faction model expansion to Player8 + the `(int)Faction`/2-player-loop audit + ScenarioDirector float→Fixed (SD-6). Re-run N=2 golden + new N=3/N=4 harness.
3. **(M5)** Merged tick packet + one-slot client gate + ready-count server + spectator demux rewrite (SD-1/SD-2/SD-3). **The #1 FR-39 regression gate** — the 2-player path becomes N=2 of the new format; golden re-verify before declaring done. Co-design the tagged envelope with D2 *first* (SD-12).
4. **(M5)** Server RTT collection + server-dictated ACK-gated delay + receipt re-clamp (SD-4); start-state agreement + inbound PROTOCOL_VERSION/rulesetHash gates (SD-13).
5. **(M5)** Deterministic freeze-and-continue drop policy (SD-10) — cover with a mid-match-drop desync test (drop, assert checksums in sync 300+ ticks after).
6. **(M5)** Nakama N + parties API + server-side slot assignment + routing decision (SD-9); parties lobby UI (deferrable slice).
7. **(M5)** Replay v2 header + tagged body (SD-12).
8. **(late M5 / post-1.0)** RelayCore/ITransport extraction + server.csproj scaffold + full PublishAot Linux build + TrimmerRootAssembly rooting (SD-11). AOT analyzer stays a CI gate throughout.

---

## 6. Prerequisites surfaced

See the structured `topPrerequisites` block. The load-bearing ones:
- **FR-39 green** before the rewrite (golden baseline).
- **SimChecksum widened** (SD-7) before *any* N>2 verification — otherwise N-player desync tests are blind to crystal/supply divergence.
- **Adversarial N-peer harness** (forged sub-bundle, over-count, merged-from-client, forged DelayProposal/checksum-slot, mid-match drop) — the original harness only tested honest-peer desync.
- **D2 envelope co-design** before freezing SD-1/SD-12.
- **Inbound D3 gates landed** (PROTOCOL_VERSION reject, rulesetHash@lobby) — *not* yet implemented in the server-validating direction.
- **Authoritative expected-player-count AND initial-delay sources** pinned (drive SD-3 gate, N-faction seeding, spectator/player slot split, tick-0 pre-seed). Note ServerTransport hard-splits MAX_SLOTS=4 into 2 players + 2 spectators (ServerTransport.cs:22-24) — an N-player lobby must reallocate that split dynamically; spectator capacity competes with player capacity for the slot pool.
- **Merged-packet ceilings:** MERGED_MAX_BYTES, per-sub-bundle count<=MAX_ORDERS (drop, not clamp), total-orders-per-tick fuel cap reconciled with D2 fuel, correctly-sized merge buffer (`_relayBuf` is one-faction-sized today, DedicatedServer.cs:51-52). Required because the merged packet + per-faction DSL payloads multiply per-tick size by N AND DSL volume — a large packet on a slow link can blow the `[2,12]` delay window.

---

## 7. Hand-offs

- **→ D2:** the merged packet (SD-1) and replay v2 body (SD-12) must adopt a **tagged record envelope** carrying per-faction `DslEventCommand`; co-design the layout and the **intra-tick order (orders-then-events, ascending faction)** *before* the wire format is frozen, or D5 freezes a format D2 must re-break. Reconcile MAX_ORDERS=32 / 1-byte count against per-faction DSL event volume + per-tick fuel.
- **→ D3:** reconcile PerPlayer 0..7 against the extended Faction enum (SD-6); confirm `checksum_algo_version` bumped for the all-faction hash (SD-7); confirm the inbound PROTOCOL_VERSION reject and rulesetHash@lobby are landed server-side (they are not today).
- **→ D4 (AI):** drop-to-deterministic-AI for dropped slots (SD-10 fast-follow) needs an AI all peers run bit-identically in the sim tick (D1 effect-graph + D4 determinism). D5 ships freeze-and-continue; AI-takeover is D4-coupled.
- **→ implementation/M5 sprint:** the section-5 ordering; each step always-shippable, golden-checksum-gated at N=2.
- **→ engine/CI:** server.csproj scaffold + AOT analyzer gate; the full PublishAot build (with TrimmerRootAssembly rooting for GodotSharp + target) hands to a late-M5/post-1.0 task.

---

## 8. Residual risks / watch-items

- **FR-39 regression (highest):** two new behaviors replace literal 2-player bytes — the merged packet (SD-1) *and* the server-side checksum collector (SD-5). The 2-player path becomes N=2 of new code; a weak/ skipped N=2 golden re-verify silently regresses the #1 ship risk. Both changes are golden-gated in section 5.
- **8-player slowest-peer stalls:** lockstep comfort caps at 4-8; at N>~6 on bad links the slowest peer dominates UX. Server-dictated delay (SD-4) + freeze-and-continue (SD-10) mitigate but cannot eliminate — the SD-8 4-now default is the explicit hedge.
- **Faction-model churn:** a missed `(int)Faction` index site or a missed 2-player loop is a latent N>2 desync/crash; the widened SimChecksum (SD-7) is the safety net that surfaces it in testing — but note SD-7 does **not** catch trigger-arg divergence (event args are strings derived from state), which is why SD-6 converts ScenarioDirector to Fixed.
- **Tamper surface on the merged packet:** if the server fails to re-stamp faction from the slot, or accepts a merged-shape packet from a client, a peer impersonates another faction (or the server). SD-1's distinct-type + re-stamp + drop rules are the fix; the adversarial harness must fire these.
- **Delay-clamp bypass:** the received-delay path is unclamped today (`Math.Max(myDesired, theirDelay)`, LockstepManager.cs:495) — a forged DelayProposal pushes delay outside `[2,12]`. SD-4 re-clamps on receipt and accepts proposals only from the server channel.
- **AOT extraction scope creep (deferred):** pulling RelayCore out of `DedicatedServer:Node` behind ITransport can leak Godot types (Error, ENetConnection); the sockets/managed-ENet AOT impl is unverified until the build runs — deferring it with the build (SD-11) is what keeps this from being a late 1.0 surprise.
- **Chat identity spoof (minor):** chat is broadcast with a client-controlled faction byte the server explicitly trusts (DedicatedServer.cs:159-165). Fold the SD-1 re-stamp principle into chat (overwrite faction = `SLOT_FACTION[fromSlot]`) — cheap, removes an obvious N-player impersonation.
- **Parties/lobby-UI breadth:** SD-9's UI is the most open-ended solo-dev slice and the most likely to slip under M5 pressure — which (with SD-8) is why 4-now/parties-later is the kept fallback.
- **Disconnect-freeze feel:** idle units sit motionless — fine for friends/family EA, weak competitively; revisit when D4 AI-takeover lands.

---

## 9. Adversarial review note (4 lenses)

The recommendation **changed materially** under review; the original "Option A, 8-now, extract-now, just-add-a-slot-id" framing did not survive intact.

- **Determinism & Lockstep (sound-with-changes):** surfaced two *criticals* folded into the design — (a) the merged packet had **no specified application order**, now fixed as ascending-faction-then-orders-then-events (SD-2); (b) **SimChecksum ignores Crystal/Supply entirely**, now widened (SD-7, verified at SimChecksum.cs:51-54). Also forced ACK-gated server-dictated delay (SD-4), tick-0 start-state agreement (SD-13), deterministic drop-cutover (SD-10), a no-majority halt rule (SD-5), and the DSL-vs-order intra-tick order (SD-2). One verifier detail corrected: ConstructionTimer *is* already hashed (line 48).
- **Static-validation / anti-tamper (sound-with-changes):** the *linchpin critical* — the merged packet's faction byte must be **server-stamped from the slot, never copied from the client** — became the core of SD-1, plus a **direction/shape discriminator** (distinct packet types) so the server rejects merged-from-client. Added: merged-packet size/count/fuel ceilings, transport-authoritative checksum slot (SD-5), receipt-side delay re-clamp (SD-4), spectator consumes only server-built output, chat faction overwrite, and an adversarial harness prerequisite.
- **Brownfield-fit / D1-D2-D3 coherence (sound-with-changes):** two *criticals* corrected the proposal's own claims — (a) "**server is the single fan-in for checksums**" is false against the as-built **P2P-local** compare (DedicatedServer.cs:148-156 / LockstepManager.cs:366-373), so SD-5 is reframed as a topology inversion + second golden-gated wire change; (b) the **merged packet breaks the in-scope spectator path** (demux by cmdFaction, LockstepManager.cs:408-424), now explicit in SD-2's change surface. Also corrected "server already relays Ping/Pong" (false — no such cases, DedicatedServer.cs:133-166 → SD-4 net-new), the D2 tagged-envelope co-design prerequisite (SD-12), and the unimplemented inbound D3 Hello/rulesetHash gates (SD-13).
- **Scope & solo-dev cost (sound-with-changes):** drove the **headline flip** — "build A's architecture, **ship C's scope**" (SD-8) — because recommending 8-unconditionally while admitting 4-now is regret-free is gold-plating deferrable verification breadth. Also **deferred the AOT extraction itself** (not just the build) off the critical path (SD-11, revised), noted the existing dual-stream gate as prior art (weakening one of A's pros to a wash), elevated spectator rework into the cost estimate, broadened the SimChecksum audit to D1 SoA stores, reordered the migration so low-risk fixes ship first, and tightened "frozen/idle" to a deterministic passive-sim-continues definition (SD-10).