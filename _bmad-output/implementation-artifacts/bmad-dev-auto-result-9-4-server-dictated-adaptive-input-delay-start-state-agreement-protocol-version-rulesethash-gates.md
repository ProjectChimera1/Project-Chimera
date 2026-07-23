---
status: blocked
---

# BMad Dev Auto Result — Story 9.4

Status: blocked
Blocking condition: missing hard prerequisite — Story 9.3 (server-authoritative merged-tick rewrite) is `backlog`/unbuilt, and 9.4's headline deliverable ("server-dictated" adaptive input delay with all-N-ACK commit) is built on top of the N-player authoritative server + tagged envelope that 9.3 delivers. Planning 9.4 now yields either throwaway 2-player-relay work or an architecture violation, so the spec cannot meet the "Sufficient" READY-FOR-DEVELOPMENT criterion.

## Why this is blocked (not a code failure)

The Epic-9 context defines a **linear multiplayer backbone** (D5 strangler order,
`epic-9-context.md:61`): **9.1 → 9.2 → 9.3 (merged-tick rewrite) → 9.4 (delay + start-state)**.
The dispatch skipped 9.3. Sprint status confirms: `9-3-...: backlog`, `9-4-...: backlog`
(`sprint-status.yaml:179-180`).

Story 9.3 is the story that converts the dedicated server from a **relay** into the
**single authority over merged tick truth**. Story 9.4's core deliverable — the design's
"Server-dictated input delay ... server-side RTT collection → authoritative broadcast →
**all-N-ACK** commit → receipt-side re-clamp to [2,12]" (`epic-9-context.md:44`) — is
authority machinery that logically rides 9.3's server-authoritative model and the tagged
envelope 9.3 is told to "freeze ... before any client consumes it" (`epic-9-context.md:46`).

### Proof 9.3 is genuinely unbuilt (verified in code, not just sprint-status)

- `TickCommandsMerged` does **not** exist — only a deferral comment: *"the live re-simulated
  server vote needs TickCommandsMerged and is Epic 9 (D3)"* (`DedicatedServer.cs:57`).
- The server is a **pure relay + checksum-quorum overlay**; it explicitly does **not tick the
  sim** (`DedicatedServer.cs:52-60`, `114`). `RelayTickCommands` forwards the per-client packet
  verbatim (`DedicatedServer.cs:267-289`) — no merge, no faction re-stamp into an output.
- Still hard-wired 2-player: `ServerTransport.MAX_PLAYERS = 2` (`ServerTransport.cs:24`),
  single-opponent `int other = 1 - slot` (`DedicatedServer.cs:162,286`), 2-player
  `LockstepManager` (`LockstepManager.cs:406`). 9.3 is what collapses `1 - slot` and makes
  "all-N-ACK" meaningful for N>2.
- 9.2's own spec "Never" section explicitly defers the merged-tick server to Story 9.3.

## What the investigation found for 9.4 (so 9.3-then-9.4 can proceed cleanly)

Distilled so the follow-up (build 9.3 first, then re-dispatch 9.4) has a head start.

**Already exists (NOT net-new — the epic's "net-new" claim is stale for the current tree):**
- Full P2P adaptive-delay pipeline: Ping/Pong/RTT EWMA, `DelayProposal` negotiate/commit, and
  the Tier-1-tested `DelayMath` policy core (`ComputeTargetDelay`, `AgreeDelay`). Clamp
  `[2,12]` = `DelayMath.MIN_DELAY/MAX_DELAY`. Initial seed `LockstepManager.INPUT_DELAY = 4`.
- `StartStateHash` — a real canonical **FNV-64** hash (`StartStateHash.cs`, `AlgoVersion=2`),
  computed at match start (`MainScene.cs:502-505`) but **computed-and-logged only**, never
  wired to the wire or compared between peers (deferred to Epic 9 by its own doc-comment).
- `PROTOCOL_VERSION = 1` constant, **written** into Hello but **never validated** inbound
  (`NetworkCommand.cs:630`; `TryReadHello` ignores the version bytes, `NetworkCommand.cs:645-652`).
- `HandshakeGate.CheckStart` fail-closed pre-tick-0 gate that today compares **only** the
  32-bit folded scenario-content hash (`HandshakeGate.cs`, `LobbyUi.cs:363-373`).
- `ServerChecksumCollector` (server-side strict-majority desync detection) + `SimChecksum`
  (`AlgoVersion=21`).

**Genuinely net-new for 9.4 (the real scope, once 9.3 lands):**
- (a) **Server role in delay** — server collects RTT and broadcasts an authoritative delay
  directive with all-N-ACK commit. **Depends on 9.3** (authoritative N-player server + tagged
  envelope). This is the blocked piece.
- (b) Receipt-side re-clamp of the untrusted delay byte — an explicit known gap owned by 9.4
  (`DelayMath.cs:51-57`). *(Independent of 9.3; small.)*
- (c) Inbound `PROTOCOL_VERSION` validation gate. *(Independent of 9.3.)*
- (d) `rulesetHash` — **does not exist in any form** (caps `MaxEffectDepth=8` etc. are NAMED in
  `EffectCaps.cs` but no hash is computed). Create it over `EffectCaps`. *(Independent of 9.3.)*
- (e) Start-state agreement — fold `{roster + faction-count + initial-delay + rulesetHash +
  scenarioHash}` (widen wire to 64-bit) and compare before tick 0. *(HandshakeGate/StartStateHash
  exist; largely independent of 9.3, but naturally rides the envelope 9.3 freezes.)*

## Recommendation

Implement **Story 9.3 (server-authoritative merged-tick rewrite)** before re-dispatching 9.4.
Once 9.3 lands the authoritative N-player server and the frozen tagged envelope, 9.4 becomes a
clean, non-throwaway slice (items b–e are mostly ready; item a slots onto 9.3's server authority).

Warnings carried from planning: `multiple-goals` (the story bundles three independently-shippable
goals: server-dictated delay, start-state agreement, and the PROTOCOL_VERSION/rulesetHash gates).
