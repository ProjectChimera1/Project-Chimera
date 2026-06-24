---
baseline_commit: 3599834
---

# Story 1.9a: ServerBootstrap headless peer + server checksum collector with quorum + HALT (loopback)

Status: review

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->
<!-- 1.9a: Tasks 1–9 complete (Tier-1 183 green, goldens byte-identical, godot.csproj builds). Task 10 code
     complete + in-engine overlay/boot verified; the live 3-process loopback (server + 2 clients, F9-induced
     desync) is the one remaining MANUAL gate before 'done'. -->


## Story

As a solo developer building the server authority the sim needs,
I want a `ServerBootstrap` peer composition root (the headless branch builds `SimulationHost` + `ScenarioValidator` + `ScenarioApplier` with no presentation) and a Godot-free server-side checksum collector with strict-majority desync attribution + terminal HALT, with the two M1 forks (ascending-faction-slot same-tick tie-break, server >2-player quorum) pinned, all loopback-testable on one machine,
so that the server holds real validated sim state and can detect, attribute, and HALT a desync cleanly — before I attempt a two-machine LAN run (Story 1.9b).

> **This is migration Step 6 of the determinism strangler (`game-architecture.md` Step 6 / §D5, AR-38 + AR-40 + UX-DR64e).** It **creates the shared sim path the server lacks today** (the server holds **zero** sim state at `3599834` — `DedicatedServer` is a pure ENet relay). It is the first half of the former Story 1.9; the **#1-ship-risk two-physical-machine LAN proof is Story 1.9b** and is explicitly out of scope here (loopback / single machine only). **Three facts from the codebase reshape the idealized architecture text and are settled below:** (1) the server is the **stateful ARBITER** in 1.9a — `ServerBootstrap` proves it *can* hold validated sim state and the collector quorums over **peer-reported** checksums; the server casting its *own* re-simulated match vote requires `TickCommandsMerged` and is **Epic 9** (D3). (2) The architecture's `OnChecksum`/`_collector.Record`/`TryMajority` shape is a **recommendation, not committed code** — the concrete `ServerChecksumCollector` is net-new and designed here (D4). (3) The exact user-facing HALT string is **specified nowhere** — it is a UX call; this story ships a recommended default and surfaces it as Open Question #1.

## Acceptance Criteria

1. **(ServerBootstrap is a Godot-free peer composition root reusing the 1.8 spine)** **Given** the headless branch (`MainScene.cs:185-197`, today `new DedicatedServer(); server.Start(port); return;`) **When** `ServerBootstrap` runs **Then** it builds a `SimulationHost` (via `SimulationHost.Create`) + runs `ScenarioValidator.Validate` + applies through `ScenarioApplier.Apply(Validated<ScenarioData>)` with **no presentation / no Godot Node tree**, reusing the **exact** sim spine from Stories 1.8a/1.8b — `ServerBootstrap` lives in `src/Core/Sim/` with **no `using Godot`**, compiles into the Godot-free Tier-1 project, and **obtains its `Validated<ScenarioData>` through the validator** (it cannot `new Validated<…>` — the `Proof` ctor is internal + `ValidatedSoleMinterTest`-scanned). A Tier-1 determinism test runs the committed golden scenario **300+ ticks through a `ServerBootstrap`-built host** and asserts the checksum sequence is **byte-identical to the committed golden** (i.e. the server's sim path == the client's, satisfying arch Step-6 "server start-state checksum == client offline start-state").

2. **(Cross-faction same-tick event tie-break is pinned to ascending faction slot — AR-40 fork #1)** **Given** two state-mutating events that resolve on the **same tick** from **different faction slots** **When** the sim ticks **Then** the canonical resolution order is **ascending faction slot** (today subsumed by the ascending-entity-ID combat iteration — the only cross-faction same-tick *hashed* mutation at `3599834` is the `DamageResolver.Apply` death sequence `world.Destroy`; the `CombatEventQueue` is **presentation-only, excluded from the checksum** and is **not** touched), the rule is **pinned by a named comment/constant** (citing AR-40 + the forward DSL-event owner Epic 7), **and** a Tier-1 golden test with a **symmetric two-faction same-tick mutual-engagement** scenario proves the checksum sequence is **deterministic and order-stable** across repeated/separate-process runs.

3. **(Server quorum: strict-majority canonical, or terminal HALT on no majority — AR-40 fork #2)** **Given** a Godot-free `ServerChecksumCollector` that buffers slot-tagged 32-bit checksums per **executed** sim tick (the post-apply tick, identical across peers) within a bounded window and drops stale/non-matching ticks **When** all expected peers have reported for a tick **Then** it declares the **strict-majority** (`> N/2`) hash as canonical and names the **minority** slot(s); on **no strict majority** it reports "global desync, no canonical." The collector is **N-shaped** (any N≥2; N=2 ⇒ a 1-vs-1 mismatch is *no majority*) and is proven by Tier-1 unit tests for **all-agree**, **one-minority (attribution at N=3)**, **no-majority**, **stale-tick drop**, and **duplicate-(slot,tick) idempotency**. **Slot is transport-authoritative — never read from the packet payload.** The wire/checksum stays **32-bit `uint`** (no widening).

4. **(Induced divergence → DesyncAlert names the diverged peer → terminal HALT with a clear message — AC3 of the epic / UX-DR64e)** **Given** an induced divergence on one loopback peer **When** the server's collector detects the mismatch **Then** `ServerHost` broadcasts/sends a **`DesyncAlert`** (to a minority peer when a majority exists) or a **`Halt`** packet (on no majority), naming the diverged peer/tick; the receiving client **terminates the match** and surfaces a **clear, terminal user-facing HALT message** addressed to the "Commander" (UX-DR65 voice), **visually and behaviorally distinct from the recoverable stall banner** (UX-DR28 `banner-stall`, a warn pill). Verified **end-to-end on a single machine** via a loopback in-engine smoke (two clients + one server, a debug-induced one-peer perturbation) — **no second machine** (that is Story 1.9b).

5. **(No sim behavior change — goldens byte-identical, Godot-free boundary, suite green)** **Given** the full Tier-1 suite **When** it runs **Then** **all three** `*.golden.txt` verify green **unchanged** (`git status` clean) — every change in this story is **additive server/networking + a pinning test**; nothing mutates the 30 Hz tick. `ServerBootstrap`, `ServerHost`, and `ServerChecksumCollector` contain **no `using Godot`/`GD.*`** and pass `GodotFreeBoundaryTest`. If a golden moves, something leaked into the tick — **fix it; do not re-record.**

_Covers: AR-38 (`ServerBootstrap` peer composition root + Godot-free `ServerHost` extracted from `DedicatedServer`; NativeAOT project-split deferred post-1.0), AR-40 (the two M1 checksum forks pinned: ascending-faction-slot same-tick tie-break + server >2-player quorum = true strict majority), UX-DR64e (desync → terminal HALT, distinct from silent drift / from the stall banner). Depends on: 1.8c (DONE — `SimulationHost`/`ScenarioApplier`/`ScenePhaseRunner` shipped; the headless early-return at `MainScene.cs:190` was explicitly left as "1.9a's seam"). Transitively builds on 1.7 (`ScenarioValidator`/`Validated<T>`), 1.8a (`SimulationHost`/`ILogSink`), 1.8b (`ScenarioApplier`). Independent of 1.9b (the two-machine LAN run) and Epic 9 (the N-scale relay rewrite)._

> Split from former 1.9. AR-38 ServerBootstrap peer root; AR-40 pins the two M1 checksum forks. UX-DR64e desync→terminal HALT. Brownfield: replaces the opaque checksum relay at `DedicatedServer.cs:148-157` and adds a server-authoritative path alongside `LockstepManager`. **Single-machine / loopback — no second machine required.** Net-new types: `ServerBootstrap`, `ServerHost`, `ServerChecksumCollector`, the `DesyncAlert`/`Halt` packet builders, `HaltReason`. Additive only — the 30 Hz tick is untouched.

---

## Developer Context

**You (the dev agent) have ONLY this file. Read this whole section before editing anything.** The work is **three small net-new Godot-free types** (`ServerBootstrap`, `ServerHost`, `ServerChecksumCollector`), **new packet builders** (`MakeDesyncAlert`/`MakeHalt` + a `Halt=0x13` type + `HaltReason`), a **re-point of the headless branch** in `MainScene`, a **rewrite of the 9-line opaque relay** in `DedicatedServer`, a **client-side HALT handler** in `LockstepManager` + a **terminal HALT overlay** wired in `MatchLifecycleController`, **one pinned tie-break invariant + golden**, and **new Tier-1 tests** in a `Server/` folder. The sim tick is **NOT** touched — goldens stay byte-identical. The traps are:

1. **Re-implementing apply/validate/spawn instead of reusing the 1.8 spine.** The entire point of AR-38 is that the server reuses the **exact** sim path. `ServerBootstrap` calls `SimulationHost.Create(...)`, `new ScenarioValidator().Validate(model)`, and `new ScenarioApplier(host, log, slotDefs).Apply(r.Value)` — **verbatim** the same three types the client uses (1.8b explicitly made `ScenarioApplier` `public` "for verbatim 1.9a reuse"). Do **not** write a second applier, a second validator, or a second spawn path. If you find yourself reading `ScenarioData` fields to mutate stores, you are reinventing `ScenarioApplier` — stop. (D2)
2. **Trying to mint `Validated<T>` from `ServerBootstrap`.** `Validated<T>`'s only mint path is `ScenarioValidator.Validate` (the `Proof` ctor is `internal` and a `ValidatedSoleMinterTest` source-scan forbids `new Validated<` anywhere but `ScenarioValidator.cs`). `ServerBootstrap` **must** call `Validate` and pass the resulting `r.Value` to `Apply`. This is the AR-38 guarantee, not an inconvenience. (D2)
3. **Making the server a voting *player* in 1.9a (Epic-9 scope creep).** The server is the **arbiter** here: it holds validated start-state (proven by the AC1 golden-determinism test) and quorums over **peer-reported** checksums. Having the server **tick a live match from a merged command stream** to cast its own vote requires `TickCommandsMerged` (re-stamp faction from authoritative slot + single merged broadcast) — that is **AR-17 SD-1/SD-2 = Epic 9 (Story 9-3a)**. Attribution is N-shaped and is **unit-tested with ≥3 synthetic streams**; the 2-client loopback proves **HALT-on-no-majority** end-to-end. Do NOT build the merged-command feed, the Ready-COUNT state machine (SD-3), or server-dictated adaptive delay (SD-4) here. (D3 — and see Open Question #2)
4. **Touching `CombatEventQueue` for the tie-break.** `CombatEventQueue` (`src/Combat/CombatEventQueue.cs`) is a **presentation feedback** ring buffer (`MeleeHit`/`RangedHit`/`SplashHit`/`UnitKilled`), drained by `CombatFeedbackBridge`, **excluded from `SimChecksum`**. The determinism-relevant same-tick ordering is the **hashed** death sequence inside `DamageResolver.Apply` (`world.Destroy`). The tie-break is **pinned + tested**, not re-engineered — combat already iterates ascending entity-ID. (D11)
5. **Moving a golden.** This story is **additive** (server/networking + a pinning test). It must change **zero** sim ticks — same class as 1.7/1.8a. If any `*.golden.txt` moves, you leaked something into the tick — **find it and fix it; never set `CHIMERA_GOLDEN_RECORD`.** (AC5)
6. **Widening the checksum wire.** The architecture pinned **32-bit wire / 64-bit canonical** (`game-architecture.md:2301`). The collector buffers `uint`; the `Checksum` packet stays 9 bytes (`type+tick+hash`); `MakeDesyncAlert`/`MakeHalt` use the same `uint` width. Do **not** widen to `ulong`. (D12)
7. **Reading the slot from the packet.** A peer's slot is **transport-authoritative** (`ServerTransport`, the ENet peer→slot map), never client-supplied. `ServerHost.OnChecksum(slot, …)` gets `slot` from the **transport callback**, not from the `Checksum` payload (the payload has only `tick`+`hash`). A client must not be able to spoof another slot's checksum. (D5)
8. **Leaving the client's P2P compare double-firing.** Once the server **consumes** `Checksum` packets (instead of opaquely relaying them to the other peer), clients **no longer receive a peer's raw checksum**, so the P2P compare at `LockstepManager.cs:365-376` goes dormant. Don't leave a half-relay that fires *both* the old P2P path *and* the new server `DesyncAlert` — route the authoritative halt through the server only. (D8/D9)
9. **`ServerHost`/`ServerChecksumCollector` calling Godot or the concrete transport.** They are Godot-free + Tier-1-tested. `ServerHost` takes **transport seams** (`Action<int,byte[]> sendReliableTo`, `Action<byte[]> broadcastReliable`) — the same pattern as `SimulationHost.SetChecksumSink(Action<uint,uint>)` — so a test can capture emitted packets without ENet. `DedicatedServer` (the Godot Node) injects the real `_transport.SendReliableTo`/`BroadcastReliable`. (D5/D13)

### The shape of the work (3 net-new Godot-free types + 2 packet builders + 1 headless re-point + 1 relay rewrite + 1 client handler + 1 HALT overlay + 1 pinned tie-break + a new `Server/` test folder; goldens UNCHANGED)

1. **Net-new `ServerChecksumCollector`** (`src/Multiplayer/Server/ServerChecksumCollector.cs`, Godot-free) — the pure quorum engine: `Record(tick, slot, hash) → Bucket`, `Bucket.AllPeersReported`, `Bucket.TryMajority(out canonical, out minority)`; bounded tick window; stale-tick drop; duplicate-(slot,tick) idempotency. N-shaped strict majority. (D4)
2. **Net-new `ServerHost`** (`src/Multiplayer/Server/ServerHost.cs`, Godot-free) — the server-authority core extracted from `DedicatedServer`: owns the collector + the expected-peer set; `OnChecksum(slot, tick, hash)` turns collector verdicts into actions over injected transport seams (`DesyncAlert` to minority, `Halt` broadcast on no majority), sets a terminal `Halted` flag. (D5)
3. **Net-new `ServerBootstrap`** (`src/Core/Sim/ServerBootstrap.cs`, Godot-free) — the peer composition root: `Build(model, slotFactionDefs, damageTable, log, activeCount) → SimulationHost?` via `SimulationHost.Create` → `ScenarioValidator.Validate` (fail-closed on the server: invalid ⇒ log + null) → `ScenarioApplier.Apply(r.Value)`. (D2)
4. **`DesyncAlert`/`Halt` packets** (`src/Multiplayer/NetworkCommand.cs`) — add `Halt = 0x13` to `PacketType`; `enum HaltReason : byte { NoMajority = 0 }`; `MakeDesyncAlert(uint tick, uint canonicalHash)` + `TryReadDesyncAlert`; `MakeHalt(uint tick, HaltReason reason)` + `TryReadHalt`. Mirror the `WriteChecksum`/`TryReadChecksum` 9-byte LE pattern. (D7)
5. **Re-point the headless branch** (`MainScene.cs:185-197`) — the thin Godot edge resolves the port + loads/resolves the scenario model, faction defs, and damage table (reusing existing loaders + `ProjectSettings.GlobalizePath` on the Godot side), then calls `ServerBootstrap.Build(...)` to construct the sim spine, and constructs `DedicatedServer` **injected with a `ServerHost`**. (D2/D5)
6. **Rewrite the opaque relay** (`DedicatedServer.cs:148-157`) — `case PacketType.Checksum:` now **parses** (`TryReadChecksum`) and feeds `_serverHost.OnChecksum(slot, tick, hash)` instead of forwarding raw bytes to `1 - slot`. Clients no longer send `DesyncAlert` (the server *generates* it). Preserve `TickCommands` relay, `Chat`, `Ready` exactly. (D8)
7. **Client HALT handler** (`LockstepManager.cs`) — add handlers for inbound `DesyncAlert` + `Halt`: stop advancing the sim and raise a terminal halt event (extend `OnDesync` or add `OnHalt`). The dormant P2P compare at `:365-376` no longer receives peer checksums. (D9)
8. **Terminal HALT overlay** (`MatchLifecycleController.cs` — today `OnDesync` only logs) — turn the halt event into a **terminal** user-facing overlay (reuse the `GameOverOverlay` pattern), with the UX-DR65-voiced message, distinct from the stall banner. (D10)
9. **Pin the AR-40 tie-break** — a named comment/constant on the canonical "ascending faction slot" rule (at the combat resolution site) + a Tier-1 golden for a symmetric two-faction same-tick mutual-engagement. (D11)
10. **Tests** — new `ProjectChimera.Sim.Tests/Server/`: `ServerBootstrapDeterminismTests.cs` (AC1), `ServerChecksumCollectorTests.cs` (AC3), `ServerHostTests.cs` (AC3/AC4 verdict→packet), a tie-break golden in `Golden/` (AC2), plus packet round-trip tests. The loopback end-to-end (AC4) is an **in-engine smoke** (the production wiring crosses the Godot Node boundary). (D13)

### Key design decisions (settled here — do NOT re-derive)

**D1 — Three-way split: `ServerBootstrap` (compose) / `ServerHost` (authority core) / `DedicatedServer` (transport shell).** Per `game-architecture.md:1515-1525`. `ServerBootstrap` (`src/Core/Sim/`) builds the sim spine, no presentation. `ServerHost` + `ServerChecksumCollector` (`src/Multiplayer/Server/`, Godot-free) hold the checksum collector + majority-vote + `DesyncAlert`/HALT. `DedicatedServer` **stays the Godot `Node` transport shell** (`src/Multiplayer/DedicatedServer.cs`, `partial : Node`) and **delegates** parsed checksum packets to `ServerHost`. _(Forward, NOT this story: `ProtocolVersion`/`DelayController` also live under `src/Multiplayer/Server/` — Epic 9.)_

**D2 — `ServerBootstrap` reuses the 1.8 spine verbatim; it never re-implements apply/validate, and it goes *through* the validator.** Build path:
```csharp
public static SimulationHost? Build(
    ScenarioData model, FactionDefinition?[] slotFactionDefs, DamageTable? damageTable,
    ILogSink log, int activeFactionCount)
{
    var host = SimulationHost.Create(
        log, new FactionRegistry(activeFactionCount),
        slotFactionDefs[(int)Faction.Player1], slotFactionDefs[(int)Faction.Player2],
        damageTable);                                   // same Create the client calls
    var r = new ScenarioValidator().Validate(model);    // ONLY mint path for Validated<T>
    if (!r.Ok)                                          // server is authoritative ⇒ fail-closed
    { log.Warn($"[ServerBootstrap] scenario REJECTED: {r.Error}"); return null; }
    new ScenarioApplier(host, log, slotFactionDefs).Apply(r.Value);
    return host;
}
```
`SimulationHost.Create` signature (verified — `src/Core/Sim/SimulationHost.cs`): `static SimulationHost Create(ILogSink log, FactionRegistry checksumFactions, FactionDefinition? factionDef1 = null, FactionDefinition? factionDef2 = null, DamageTable? damageTable = null, AiDifficulty aiLevel = AiDifficulty.Normal)`. `ScenarioApplier` ctor (verified): `ScenarioApplier(SimulationHost host, ILogSink log, FactionDefinition?[] slotFactionDefs)`; `Apply(Validated<ScenarioData> v)` reads the model via **`v.Value`** (NOT `.Model`). The **Godot edge** (the re-pointed `MainScene` headless branch) does all `res://`/`ProjectSettings.GlobalizePath` resolution + loading and passes **already-resolved** inputs down — `ServerBootstrap` stays Godot-free (mirrors how the client's presentation pre-pass fills `slotFactionDefs` in place before `Apply`). **On the server the validator is fail-closed** (a server with no valid start-state cannot arbitrate) — this is the natural home for the fail-closed posture even while client *master* stays shadow (1.7 D7); it does not change any golden (the golden model is valid).

**D3 — The server is the stateful ARBITER in 1.9a; the server's own re-simulated *vote* is Epic 9.** `ServerBootstrap` discharges AR-38 by giving the server a **validated, determinism-proven** sim spine (AC1: golden through `ServerBootstrap` == committed golden == client). The `ServerChecksumCollector` quorums over **peer-reported** `Checksum` packets (the clients already send them via `LockstepManager.SendChecksum`, `:335-342`). **Attribution** (naming a minority peer) needs ≥3 votes and is **unit-tested with 3 synthetic streams**; the **2-client loopback** proves the **no-majority → HALT** path end-to-end. The server **ticking a live match from a merged command stream** to add its own vote requires `TickCommandsMerged` (`game-architecture.md` SD-1/SD-2) and is **Epic 9 (Story 9-3a)**. _(Open Question #2 surfaces this for your confirmation — it is the load-bearing scope call. If Alec wants the server voting now, that pulls SD-1/SD-2 forward and changes the estimate.)_

**D4 — `ServerChecksumCollector`: pure, slot-indexed, bounded-window strict-majority engine.** The architecture gives the *shape* (`Record(tick,slot,hash)→bucket`; `bucket.AllPeersReported`; `bucket.TryMajority(out canonical, out minority)`; "one slot-tagged hash/slot/**60-tick window**", `game-architecture.md:1122-1126, 2319-2330`) but **no committed code** — design it as:
```csharp
public sealed class ServerChecksumCollector
{
    private const int MaxSlots = 4;            // ServerTransport ceiling in 1.0 (N≤4; 8 = constant bump)
    public readonly struct Verdict
    {                                          // returned when a bucket completes
        public bool Complete { get; }          // all expected peers reported for this tick
        public bool HasMajority { get; }
        public uint Canonical { get; }
        public IReadOnlyList<int> Minority { get; }   // ascending slot order (stable attribution)
    }
    public ServerChecksumCollector(int expectedPeerCount) { /* ... */ }
    /// Record one peer's checksum for an EXECUTED tick. Stale (outside window) or duplicate
    /// (slot,tick) inputs are ignored. Returns the verdict (Complete=false until all reported).
    public Verdict Record(uint tick, int slot, uint hash) { /* ... */ }
}
```
- **Strict majority** = a hash held by `> expectedPeerCount / 2` reporting peers. N=2 ⇒ both must agree (1-vs-1 is **not** a majority). N=3 ⇒ 2 agree ⇒ the odd one out is the named minority. **No majority** ⇒ `HasMajority=false` ⇒ caller HALTs.
- **Window:** keep a small ring of per-tick buckets (e.g. keyed `tick % WINDOW`, `WINDOW ≥` a few checksum intervals). A checksum for a tick older than the window's floor is **dropped** ("stale checksums for non-matching ticks are dropped, not compared"). Evict a bucket once its verdict is consumed.
- **The collector is server-side networking, NOT in the 30 Hz sim tick** — it is exempt from the in-tick determinism rules (it may use a `Dictionary` internally). BUT its **output must be order-stable**: iterate slots **ascending** when building `Minority`, so attribution is reproducible. (Prefer a slot-indexed fixed array over a `Dictionary` anyway — `MaxSlots` is 4.)
- **`expectedPeerCount`** comes from the connected **player** slots (transport-authoritative), **excluding** spectators (D6). N-shaped: 8-player is a `MaxSlots` constant bump + the `Faction` enum extension (Story 9.2), not a rewrite (`game-architecture.md:1089-1092`).

**D5 — `ServerHost`: collector verdicts → packets over injected transport seams.** Godot-free + Tier-1-testable:
```csharp
public sealed class ServerHost
{
    private readonly ServerChecksumCollector _collector;
    private readonly Action<int, byte[]> _sendReliableTo;     // (slot, packet)
    private readonly Action<byte[]> _broadcastReliable;       // (packet)
    public bool Halted { get; private set; }

    public ServerHost(int expectedPeerCount, Action<int,byte[]> sendReliableTo, Action<byte[]> broadcastReliable)
    { _collector = new ServerChecksumCollector(expectedPeerCount); _sendReliableTo = sendReliableTo; _broadcastReliable = broadcastReliable; }

    /// slot is TRANSPORT-AUTHORITATIVE (from the ENet peer→slot map), never the packet payload.
    public void OnChecksum(int slot, uint tick, uint hash)
    {
        if (Halted) return;
        var v = _collector.Record(tick, slot, hash);
        if (!v.Complete) return;
        if (v.HasMajority)
            foreach (int s in v.Minority)                                   // ascending ⇒ stable
                _sendReliableTo(s, TickCommandPacket.MakeDesyncAlert(tick, v.Canonical));
        else
        { _broadcastReliable(TickCommandPacket.MakeHalt(tick, HaltReason.NoMajority)); Halted = true; }
    }
}
```
In production, `DedicatedServer` injects `(_transport.SendReliableTo, _transport.BroadcastReliable)`; tests inject closures that capture emitted packets. **HALT is terminal in 1.9a** (recovery/rejoin policy is deferred — `game-architecture.md:2332`).

**D6 — Spectators are excluded from the quorum in 1.9a; read-only attest is forward.** A spectator is a Neutral-slot peer (`slot >= ServerTransport.MAX_PLAYERS`, `DedicatedServer.cs:173`). It is **not** counted in `expectedPeerCount` and its checksums (if any) are ignored by the collector. _(Open Question #4 — confirm vs the architecture's optional "spectators attest read-only" model.)_

**D7 — New `DesyncAlert`/`Halt` builders + a `Halt = 0x13` type; mirror the 9-byte `Checksum` pattern.** At `3599834`, `PacketType.DesyncAlert = 0x12` **exists as a type** but **no builder generates it** (it was only ever relayed). Add to `NetworkCommand.cs`:
```csharp
// PacketType enum: add after DesyncAlert (0x12)
Halt = 0x13,            // server: no canonical hash — terminal HALT for everyone

public enum HaltReason : byte { NoMajority = 0 }   // extensible (ProtocolMismatch etc. = Epic 9)

// DesyncAlert wire: type(1) + tick(4 LE) + canonicalHash(4 LE) = 9 bytes  (mirror WriteChecksum)
public static byte[] MakeDesyncAlert(uint tick, uint canonicalHash) { /* ... */ }
public static bool TryReadDesyncAlert(byte[] buf, int len, out uint tick, out uint canonicalHash) { /* ... */ }

// Halt wire: type(1) + tick(4 LE) + reason(1) = 6 bytes
public static byte[] MakeHalt(uint tick, HaltReason reason) { /* ... */ }
public static bool TryReadHalt(byte[] buf, int len, out uint tick, out HaltReason reason) { /* ... */ }
```
Use the existing `WriteUint`/`ReadUint` LE helpers (the `WriteChecksum`/`TryReadChecksum` pair, `NetworkCommand.cs:~159-178`, is the template).

**D8 — `DedicatedServer` stops opaquely relaying `Checksum`; it parses + feeds `ServerHost`.** Replace the block at `DedicatedServer.cs:148-157`:
```csharp
// BEFORE (3599834): opaque 2-player relay
case PacketType.Checksum:
case PacketType.DesyncAlert:
    if (_state == State.InGame) { int other = 1 - slot; _transport.SendReliableTo(other, data, len); }
    break;

// AFTER: server consumes the checksum into the collector (no peer relay)
case PacketType.Checksum:
    if (_state == State.InGame && TickCommandPacket.TryReadChecksum(data, len, out uint ckTick, out uint ckHash))
        _serverHost.OnChecksum(slot, ckTick, ckHash);   // slot is transport-authoritative
    break;
// DesyncAlert is now SERVER-GENERATED (clients never send it) — no inbound case needed.
```
The hardcoded `1 - slot` (a 2-player assumption) is **removed**. `_serverHost` is constructed when the match starts (in `HandleReady`, when `_state → InGame`, `:179-186`) with `expectedPeerCount` = the connected player count and the two transport callbacks. **Preserve** `TickCommands` relay (`:143-146,199`), `Chat` (`:159-165`), `Ready` (`:171-191`), and the `SLOT_FACTION` assignment (`:42`) exactly — the `TickCommands` → `TickCommandsMerged` rework is **Epic 9**.

**D9 — Client: handle inbound `DesyncAlert`/`Halt` → terminal halt; the P2P compare goes dormant.** In `LockstepManager`: keep `SendChecksum(uint,uint)` (`:335-342`, client→server) unchanged. Add inbound handling (next to the `case PacketType.Checksum:` at `:365-376`):
```csharp
case PacketType.DesyncAlert:   // this client is the named minority
    if (TickCommandPacket.TryReadDesyncAlert(data, len, out uint dTick, out uint canon))
    { GD.PrintErr($"[Lockstep] SERVER desync alert @tick {dTick} (canonical 0x{canon:X8})"); RaiseHalt(dTick); }
    break;
case PacketType.Halt:          // global no-majority
    if (TickCommandPacket.TryReadHalt(data, len, out uint hTick, out HaltReason reason))
    { GD.PrintErr($"[Lockstep] SERVER HALT @tick {hTick} ({reason})"); RaiseHalt(hTick); }
    break;
```
`RaiseHalt` stops advancing the sim (a terminal `_halted` flag that gates `Flush`, `:240-331`) and fires the halt event. **The existing P2P compare at `:365-376` no longer receives peer `Checksum` packets** (the server consumes them) — it becomes dead for the authoritative path; either gate it behind `if (!IsOnlineAuthoritative)` or leave it inert with a comment. **Do not double-fire.** Reuse `OnDesync` (`:55`) or add a sibling `OnHalt` event — your call; keep one terminal path.

**D10 — Terminal HALT overlay: distinct from the stall banner.** `MatchLifecycleController` today wires `OnDesync` to a **log only** (`:34`). Upgrade it to drive a **terminal** overlay (reuse the `GameOverOverlay` pattern — there is a `GameOverOverlayPhase`). The **stall banner** (`banner-stall`, UX-DR28 / `DESIGN.md:96`) is a transient **warn** pill for a lagging-but-recoverable peer; the **desync HALT** is **terminal + `danger`-styled**, ends the match, and offers only "Return to Menu". **Recommended message** (UX-DR65 voice — "Commander", terse/mechanical, mono status string; **confirm exact copy via Open Question #1**):
> **MATCH HALTED**
> Simulation desync detected at tick {tick}. The match cannot continue.
> `· desync · #{canonical:X8}`   `[ Return to Menu ]`

**D11 — AR-40 fork #1: pin ascending-faction-slot for cross-faction same-tick events; today = the combat death sequence (ascending entity-ID).** The **only** same-tick cross-faction *hashed* mutation at `3599834` is combat: `CombatSystem` iterates entities **ascending-ID**, and the hashed effect is `world.Destroy` in `DamageResolver.Apply` (`src/Combat/DamageResolver.cs:51-72`). The `CombatEventQueue` is **presentation-only** (`UnitKilled` etc. drained by `CombatFeedbackBridge`, excluded from `SimChecksum`) — **do not touch it** (Trap #4). To "pin the fork" (AR-40, the same discharge-by-reservation pattern 1.7 used for AR-13): (a) add a **named comment/constant** at the combat resolution site stating the canonical rule — *"Cross-faction same-tick events resolve in **ascending faction-slot** order (AR-40 M1 fork #1); today subsumed by the ascending-entity-ID combat iteration. Forward owner for cross-faction **DSL events**: Epic 7 SD-2."*; (b) add a Tier-1 **golden** test (`Golden/SameTickTieBreakGoldenTests.cs` + a small symmetric scenario) where a `Player1` and a `Player2` unit mutually engage and resolve on the **same tick**, asserting the checksum sequence is **byte-identical** across two same-process runs and across separate processes (determinism + order-stability). **Do NOT** build the Epic-7 DSL-event ordering or a merged-packet demux here — the recommended-default rule is pinned; the enforcement *site* for DSL events is forward (Open Question #5). _Expectation: the test passes with current combat (no behavior change). If it reveals a real same-tick non-determinism, that is a genuine bug — fix it and re-baseline that one new golden intentionally with justification; the existing three goldens must still not move._

**D12 — Wire stays 32-bit `uint`; do not widen.** `game-architecture.md:2301` ("the wire Ready hashes + the live per-60-tick `SimChecksum` stay 32-bit"). `SimChecksum.Compute(...)` already returns `uint` (`AlgoVersion = 3`). The collector buffers `uint`; all new packets use `uint`. 64-bit is the load-time *canonical model* hash only (1.7) — not the wire.

**D13 — Tier-1 testability: Godot-free + add the `src/Multiplayer/Server/` include to the test csproj.** `ServerBootstrap` (`src/Core/Sim/`) **auto-globs** into Tier-1 (`..\src\Core\**`). `ServerHost` + `ServerChecksumCollector` live in `src/Multiplayer/Server/` — a **guaranteed-Godot-free subfolder** — and must be added to `ProjectChimera.Sim.Tests.csproj` via a named include mirroring the existing three Multiplayer includes (`ReplayRecorder`/`ReplayPlayer`/`NetworkCommand`):
```xml
<Compile Include="..\src\Multiplayer\Server\**\*.cs" LinkBase="Sim\Multiplayer\Server" />
```
(`NetworkCommand.cs` is **already** included, so the new builders are available.) New tests live in a net-new `ProjectChimera.Sim.Tests/Server/` folder (precedent: `Bootstrap/PhaseOrderTest`, `Sim/SystemOrderTest` each got their own folder). `LockstepManager`/`DedicatedServer`/`MatchLifecycleController` use `GD.*` and are **not** in Tier-1 — their behavior (the loopback end-to-end) is covered by the in-engine smoke, not xUnit.

### Pre-flight facts you MUST NOT re-derive (verified against the codebase at `3599834`)

- **The server holds ZERO sim state today.** `DedicatedServer` (`src/Multiplayer/DedicatedServer.cs`, `public partial class DedicatedServer : Node`) has only `_transport = new ServerTransport()` — **no** `EntityWorld`/`SimulationLoop`/`SimulationHost`/stores. It is a pure ENet relay. `ServerBootstrap` **creates** the shared sim path (it does not preserve one). [Source: DedicatedServer.cs; game-architecture.md:1517]
- **The opaque checksum relay is `DedicatedServer.cs:148-157`** — `case PacketType.Checksum: case PacketType.DesyncAlert:` → `if (_state == State.InGame) { int other = 1 - slot; _transport.SendReliableTo(other, data, len); }`. The `1 - slot` is a **2-player hardcode**. This is the block D8 replaces. [Source: DedicatedServer.cs:148-157]
- **The headless seam is `MainScene.cs:185-197`** (`_Ready`): `if (DisplayServer.GetName() == "headless" || OS.HasFeature("dedicated_server")) { int port = ParsePortArg(DedicatedServer.DEFAULT_PORT); var server = new DedicatedServer(); AddChild(server); server.Start(port); return; }`. `ParsePortArg` is `MainScene.cs:996`. Stories 1.8a/1.8c **explicitly left this branch untouched** as "1.9a's seam." This is where D5 re-points. [Source: MainScene.cs:185-197,996; 1-8c Dev Notes]
- **Server slot↔faction + state machine.** `static readonly Faction[] SLOT_FACTION = { Faction.Player1, Faction.Player2 }` (`:42`); `enum State { Waiting, OneConnected, BothConnected, BothReady, InGame }` (`:32`); `HandleReady` (`:171-191`) flips to `InGame` when `_ready[0] && _ready[1]` and broadcasts `TickCommandPacket.MakeStartGame(0)`. Spectators are `slot >= ServerTransport.MAX_PLAYERS` (`:173`). **Preserve all of this.** [Source: DedicatedServer.cs:32,42,171-191]
- **Checksum packet wire = 9 bytes, 32-bit, tick-tagged, NO peer id in payload.** `TickCommandPacket.WriteChecksum(byte[] buf, uint tick, uint checksum) → int` and `TryReadChecksum(byte[] buf, int len, out uint tick, out uint checksum) → bool` (`NetworkCommand.cs:~159-178`): `type(1) + tick(4 LE) + checksum(4 LE)`. Peer identity is the **transport slot**, not the payload. [Source: NetworkCommand.cs]
- **`PacketType` (`NetworkCommand.cs:10-40`):** `Hello=0x01, Ready=0x02, StartGame=0x03, TickCommands=0x10, Checksum=0x11, DesyncAlert=0x12, Chat=0x20, Ping=0x40, Pong=0x41, DelayProposal=0x42`. **`Halt` does not exist** (add `0x13`). **`DesyncAlert=0x12` exists but has NO builder** — `Make*` exist only for `Hello/Ready/StartGame/Chat/Ping/Pong/DelayProposal` (`:190-328`). `MakeDesyncAlert`/`MakeHalt` are **net-new**. _(`Ping`/`Pong`/`DelayProposal` wire already exists; the server-dictated adaptive-delay LOGIC is Epic 9 — don't build it.)_ [Source: NetworkCommand.cs:10-40,190-328]
- **Client lockstep checksum path.** `LockstepManager` (`src/Multiplayer/LockstepManager.cs`, `public class`, **not** a Node, **uses `GD.PrintErr`** ⇒ not Tier-1): `SendChecksum(uint tick, uint localHash)` (`:335-342`) sends client→server; the **P2P** receive-compare is `:365-376` (compares `remoteHash != _pendingLocalChecksum`, fires `OnDesync` event `:55` = `Action<uint,uint,uint>`); `Flush(uint)` (`:240-331`) gates sim advance / sets `IsStalling`. **No server quorum, no HALT, no `DesyncAlert` generation exists.** [Source: LockstepManager.cs:55,142-147,240-331,335-342,365-376]
- **`OnDesync` is logged, never halts.** `MatchLifecycleController` (`src/Core/Bootstrap/Phases/MatchLifecycleController.cs:34`) wires `OnDesync` to a `GD.PrintErr` only. D10 upgrades this to a terminal overlay. [Source: MatchLifecycleController.cs:34]
- **The 1.8 sim spine (reuse verbatim).** `SimulationHost` (`src/Core/Sim/SimulationHost.cs`, `public sealed`, namespace `ProjectChimera.Core.Sim`): factory `static Create(ILogSink, FactionRegistry, FactionDefinition? = null, FactionDefinition? = null, DamageTable? = null, AiDifficulty = Normal)`; get-only props `World/Nodes/Resources/Buildings/Projectiles/CombatEvents/MatchStats/BuildSys/ScenarioDirector/Fog`; `CurrentTick/LastChecksum/ChecksumInterval`; `StepOnce()`, `int Update(float)`, `SetChecksumSink(Action<uint,uint>)`. 9-system order `[Building, Gathering, Movement, (RESERVED Modifier — Epic 2), Combat, Projectile, Supply, FogOfWar, AiOpponent, ScenarioDirector-last]`, pinned by `Sim/SystemOrderTest`. [Source: SimulationHost.cs]
- **`ScenarioApplier`** (`src/Core/Sim/ScenarioApplier.cs`, `public sealed`): ctor `(SimulationHost host, ILogSink log, FactionDefinition?[] slotFactionDefs)`; `Apply(Validated<ScenarioData> v)` (reads `v.Value`; order slots→nodes→buildings→units→`ScenarioDirector.LoadScenario`); `int SpawnUnit(UnitDefinition, Faction, float, float)`; `void ApplyFallback()`; `static BuildingType ParseBuildingType(string)`. **It is `public` for verbatim 1.9a reuse.** 1.8b review added a **null-model guard** + a **slot-bounds guard** to `Apply` specifically because it is "slated for verbatim 1.9a `ServerBootstrap` reuse" — so `ServerBootstrap` inherits a safe `Apply`. [Source: ScenarioApplier.cs; 1-8b Dev Notes]
- **`ScenarioValidator` + `Validated<T>`** (`src/Core/Definitions/`): `ValidationResult Validate(ScenarioData m)` (pure; `Pass(validated)` / `Fail(located)` / `Fail(located, value)`); `Validated<T>` is `readonly struct { T Value }`; the ctor requires a `ScenarioValidator.Proof` whose ctor is **`internal`** (the private-ctor trick is a CS0122 compile error — see 1.7 Debug Log) + a `ValidatedSoleMinterTest` source-scan. **`ServerBootstrap` cannot mint `Validated<T>` — it must call `Validate`.** [Source: ScenarioValidator.cs; Validated.cs; 1-7 Dev Agent Record]
- **`SimChecksum.Compute(EntityWorld, BuildingStore, ResourceStore, FactionRegistry) → uint`** (`src/Core/SimChecksum.cs`), `AlgoVersion = 3` (Ore/Crystal/Supply/FactionBase per active faction + `SimRng.State`). 32-bit. The collector compares these values. [Source: SimChecksum.cs]
- **`DamageResolver.Apply(in DamageContext, Fixed, DamageType) → bool`** (`src/Combat/DamageResolver.cs:51-72`) is the single death sequence: `world.Health[t] -= dmg`; on lethal → `Events?.Push(UnitKilled)` (presentation), `Stats?.RecordKill` (MatchStats, **NOT hashed**), `world.Destroy(t)` (**hashed**). The tie-break (D11) is about `Destroy` ordering, not the `Events` push. [Source: DamageResolver.cs:51-72]
- **`CombatEventQueue`** (`src/Combat/CombatEventQueue.cs`) is a 256-slot presentation ring buffer drained by `CombatFeedbackBridge`; **excluded from `SimChecksum`**. Do not touch it for determinism. [Source: CombatEventQueue.cs]
- **Tier-1 project** (`godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj`): `Microsoft.NET.Sdk` (NOT Godot), `net8.0`, xUnit 2.9.2. Globs `..\src\Core\**`, `..\src\Combat\**`, `..\src\Economy\**`, `..\src\Navigation\**`, `..\src\AI\**` + **three named** Multiplayer files (`ReplayRecorder.cs`, `ReplayPlayer.cs`, **`NetworkCommand.cs`**). `<Compile Remove>`s `MainScene.cs` + `Bootstrap/Phases/**`. Folders: `Bootstrap/`, `Builder/`, `Combat/`, `Determinism/`, `Golden/`, `Sim/`, `Validation/` (+ root `GodotFreeBoundaryTest.cs`). **`Server/` is NEW.** Tier-1 was **152 passing** after 1.8c. [Source: ProjectChimera.Sim.Tests.csproj]
- **All 1.9a target types are net-new (confirmed absent):** `ServerBootstrap`, `ServerHost`, `ServerChecksumCollector`, `HaltReason`, `MakeDesyncAlert`, `MakeHalt`, `PacketType.Halt`. [Source: grep @ 3599834]

### Scope fence — do NOT, in this story

- **Do NOT** build `TickCommandsMerged` re-stamping / the merged-packet application order (AR-17 SD-1/SD-2), the **Ready-COUNT** state machine (SD-3), or **server-dictated adaptive input delay** (SD-4 — even though `Ping`/`Pong`/`DelayProposal` wire exists). **Epic 9** (Stories 9-3a/9-3b/9-4).
- **Do NOT** make the server a voting *player* by ticking a live match from relayed commands — the server is the **arbiter** here (D3). Its own re-sim vote is Epic 9.
- **Do NOT** attempt a two-machine / LAN run. **Story 1.9b** owns the physical-machine FR-39 proof. 1.9a is loopback only.
- **Do NOT** create a separate AOT `.csproj`, add `PublishAot`, or extract `RelayCore`/`ITransport` — the NativeAOT project-split is **deferred post-1.0** (`game-architecture.md` SD-11). Only Godot-free discipline + the existing AOT-analyzer gate (1.10b) now.
- **Do NOT** harden the Ready handshake (multi-hash `{scenarioHash,rulesetHash,startStateHash}`, `PROTOCOL_VERSION` compare, `hash==0` hard-reject) — leave `LobbyUi.cs:315`'s fail-open compare and `DedicatedServer.HandleReady`'s hash-ignore exactly as-is. **Epic 9** (Story 9-4).
- **Do NOT** widen the checksum wire past 32-bit `uint` (D12).
- **Do NOT** add per-match seed plumbing — `ServerBootstrap` reuses the default-seeded `EntityWorld` (`DEFAULT_RNG_SEED`), which is exactly why server start-state == client start-state. A real per-match seed handshake is Epic 9.
- **Do NOT** count spectators in the quorum (D6); read-only attestation is forward.
- **Do NOT** touch `CombatEventQueue` or any presentation feedback for the tie-break (D11/Trap #4).
- **Do NOT** move/re-record the three existing goldens (AC5). Additive-only ⇒ byte-identical.
- **Do NOT** call `GD.*`/`using Godot` in `ServerBootstrap`/`ServerHost`/`ServerChecksumCollector` — they are Godot-free (`GodotFreeBoundaryTest`). Logging is via `ILogSink` (Info/Warn) or the presentation edge.

---

## Tasks / Subtasks

- [x] **Task 1 — Net-new `ServerChecksumCollector` (pure quorum engine) (AC: 3)**
  - [x] Create `godot/src/Multiplayer/Server/ServerChecksumCollector.cs` (`#nullable enable`, namespace `ProjectChimera.Multiplayer.Server`, **no `using Godot`**): the `Verdict` struct + `ServerChecksumCollector(int expectedPeerCount)` + `Verdict Record(uint tick, int slot, uint hash)` per D4 — bounded tick window, stale-tick drop, duplicate-(slot,tick) idempotency, strict majority (`> N/2`), **ascending-slot** minority list.
  - [x] Add `<Compile Include="..\src\Multiplayer\Server\**\*.cs" LinkBase="Sim\Multiplayer\Server" />` to `ProjectChimera.Sim.Tests.csproj` (D13).
  - [x] `dotnet build godot/godot.csproj` → green.

- [x] **Task 2 — Net-new `DesyncAlert`/`Halt` packet builders + `Halt` type (AC: 4)**
  - [x] In `godot/src/Multiplayer/NetworkCommand.cs`: add `PacketType.Halt = 0x13`; `public enum HaltReason : byte { NoMajority = 0 }`; `MakeDesyncAlert(uint tick, uint canonicalHash)` + `TryReadDesyncAlert`; `MakeHalt(uint tick, HaltReason reason)` + `TryReadHalt` — using the existing `WriteUint`/`ReadUint` LE helpers, mirroring `WriteChecksum`/`TryReadChecksum` (D7).
  - [x] `dotnet build godot/godot.csproj` → green.

- [x] **Task 3 — Net-new `ServerHost` (verdicts → packets over transport seams) (AC: 3, 4)**
  - [x] Create `godot/src/Multiplayer/Server/ServerHost.cs` (`#nullable enable`, namespace `ProjectChimera.Multiplayer.Server`, **no `using Godot`**): per D5 — owns the collector, `OnChecksum(int slot, uint tick, uint hash)` ⇒ `DesyncAlert` to each minority slot on majority, `Halt` broadcast + terminal `Halted=true` on no majority. Transport via injected `Action<int,byte[]>` / `Action<byte[]>`.
  - [x] `dotnet build godot/godot.csproj` → green.

- [x] **Task 4 — Net-new `ServerBootstrap` (Godot-free composition root) (AC: 1)**
  - [x] Create `godot/src/Core/Sim/ServerBootstrap.cs` (`#nullable enable`, namespace `ProjectChimera.Core.Sim`, **no `using Godot`**): `static SimulationHost? Build(ScenarioData model, FactionDefinition?[] slotFactionDefs, DamageTable? damageTable, ILogSink log, int activeFactionCount)` per D2 — `SimulationHost.Create` → `new ScenarioValidator().Validate(model)` (fail-closed: invalid ⇒ `log.Warn` + `null`) → `new ScenarioApplier(host, log, slotFactionDefs).Apply(r.Value)` → return host.
  - [x] `dotnet build godot/godot.csproj` → green.

- [x] **Task 5 — Re-point the headless branch + rewrite the relay in `DedicatedServer` (AC: 1, 3, 4)**
  - [x] `MainScene.cs` headless branch: the Godot edge resolves port + loads/resolves the scenario `ScenarioData` (`ScenarioSerializer.LoadFromFile` + per-slot faction resolution mirroring `ScenarioLoadPhase`), the `FactionDefinition?[]` slot defs, and the `DamageTable` (reuse existing loaders + `GlobalizePath`) in a new `BuildHeadlessServerSimHost()`, calls `ServerBootstrap.Build(...)`, and constructs `DedicatedServer { SimHost = … }` injected with the resulting `SimulationHost` (held for Epic-9; not ticked in 1.9a). Kept `ParsePortArg`/`Start(port)`/`return`. Null host (missing/invalid scenario) ⇒ relay + quorum only.
  - [x] `DedicatedServer`: added `ServerHost _serverHost` constructed in `HandleReady` when `_state → InGame` (`expectedPeerCount` = `CountConnectedPlayers()`; seams wrapped in lambdas because `SendReliableTo`/`BroadcastReliable` take an optional length arg). Replaced the `:148-157` block per D8 (`Checksum` → `TryReadChecksum` → `_serverHost.OnChecksum(slot, …)`; dropped the `DesyncAlert` relay case). **Preserved** `TickCommands`/`Chat`/`Ready`/`SLOT_FACTION`.
  - [x] `dotnet build godot/godot.csproj` → green (0 errors).

- [x] **Task 6 — Client HALT handling + terminal overlay (AC: 4)**
  - [x] `LockstepManager`: added inbound `case PacketType.DesyncAlert:` + `case PacketType.Halt:` per D9 → `RaiseHalt(tick, canonicalHash)` (terminal `_halted` flag gating `Flush`; fires new `OnHalt` event; clears `IsStalling` so the stall banner is not left showing). Annotated the now-dormant P2P compare (`:365-376`) as inert in server-authoritative play (no double-fire — server consumes Checksums). `SendChecksum` unchanged.
  - [x] `MatchLifecycleController`: subscribes `OnHalt` → new `MainScene.ShowHalt(tick, canonical)` — a **terminal**, danger-styled overlay (reuses the `GameOverOverlay` root), UX-DR65 "Commander" voice, mono status string, "Return to Menu", distinct from the stall banner (D10). Exact copy = the recommended default (Open Question #1). **Verified rendering in-engine** (screenshot).
  - [x] `dotnet build godot/godot.csproj` → green (0 errors).

- [x] **Task 7 — Pin the AR-40 same-tick tie-break + golden (AC: 2)**
  - [x] Add the named comment/constant on the canonical "ascending faction slot" rule at the combat resolution site (D11), citing AR-40 + Epic 7 (forward DSL-event owner). **No sim behavior change.** — added at `CombatSystem.Tick`'s ascending-ID loop.
  - [x] New `godot/ProjectChimera.Sim.Tests/Golden/SameTickTieBreakGoldenTests.cs` (+ a small symmetric two-faction same-tick mutual-engagement scenario `SameTickTieBreakScenario.cs` + recorded `same-tick-tie-break.golden.txt`): assert the checksum sequence is byte-identical across two same-process runs **and** across separate-process invocations (determinism + order-stability). Passed with current combat — no existing golden moved.
  - [x] `dotnet test --filter FullyQualifiedName~SameTickTieBreak` → green.

- [x] **Task 8 — Tier-1 `Server/` tests (AC: 1, 3, 4)**
  - [x] New `godot/ProjectChimera.Sim.Tests/Server/ServerChecksumCollectorTests.cs` (AC3): all-agree → majority canonical, no minority; **one-minority at N=3** → canonical = the pair, minority names the odd slot; **no-majority** (N=2 mismatch; N=3 all-different; N=4 2-2 split) → `HasMajority=false`; **stale tick** dropped; **duplicate (slot,tick)** idempotent; verdict incomplete until all expected peers report; resolved tick does not re-complete; N=4 minority attribution regardless of report order; ctor rejects out-of-range N.
  - [x] New `Server/ServerHostTests.cs` (AC3/AC4): inject capturing `sendReliableTo`/`broadcastReliable`; assert a majority emits one `DesyncAlert` (parseable via `TryReadDesyncAlert`, carrying the canonical hash) to **each** minority slot; assert clean majority emits nothing; assert no-majority emits a broadcast `Halt` (`TryReadHalt` → `NoMajority`) and sets `Halted`; assert `Halted` is terminal (later `OnChecksum` is a no-op); null seams throw.
  - [x] New `Server/ServerBootstrapDeterminismTests.cs` (AC1): build a host via `ServerBootstrap.Build(<applier-golden model>, …)`, run **300 ticks**, assert the checksum sequence is **byte-identical to the committed `golden-applier-scenario.golden.txt`** (reuse the `GoldenChecksumReplay` harness) — i.e. server sim path == client. Also assert two in-process server-built runs agree, and `ServerBootstrap.Build(<invalid model>, …)` returns `null` (fail-closed) and logs `REJECTED`.
  - [x] Add packet round-trip asserts (`Server/ServerPacketTests.cs`): `MakeDesyncAlert`/`TryReadDesyncAlert` and `MakeHalt`/`TryReadHalt` round-trip (incl. uint.MaxValue); truncated + wrong-type buffers return `false`.
  - [x] `dotnet test --filter FullyQualifiedName~Server` → green (29 tests).

- [x] **Task 9 — Prove AC5 (goldens byte-identical, Godot-free, suite green) (AC: 5)**
  - [x] `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` → ALL green (**183 passing**, 0 failed = 152 prior + 29 Server + 2 tie-break), with **all three** existing `*.golden.txt` UNCHANGED (only the NEW `same-tick-tie-break.golden.txt` is added). No existing golden moved.
  - [x] Confirmed `ServerBootstrap`/`ServerHost`/`ServerChecksumCollector` have zero `using Godot`/`GD.`; `GodotFreeBoundaryTest` passes (in the 183). No signature change to `SimChecksum.Compute`/`ISimSystem`/any `Tick`/`Apply` — `CombatSystem.Tick` gained only a comment; `DamageResolver`/`SimChecksum`/`ScenarioApplier` untouched.

- [~] **Task 10 — Loopback in-engine smoke (AC: 4)** _(tooling complete + server/overlay verified at runtime; the 2-client F9 observation is push-button for Alec)_
  - [x] **Debug divergence hook:** `#if DEBUG` `F9` in `MainScene._UnhandledInput` perturbs THIS peer's sim (+1 raw health on the first alive entity, mirroring the golden AC3 nudge) while `IsOnline`.
  - [x] **One-click launcher + auto-join (so Alec only clicks + presses F9):** `godot/tools/loopback-desync-smoke.cmd` starts a `--headless` server + two client windows with `-- --autojoin 127.0.0.1:7777`; a `#if DEBUG` `LobbyUi.AutoJoinDedicated` (+ `MainScene` `--autojoin` arg) connects + auto-readies via the REAL JoinGame/Ready path — no lobby clicks.
  - [x] **Headless server VERIFIED at runtime** (ran the actual Godot binary headless): boots clean, builds + validates the sim spine (`[ServerBootstrap] Validated server sim spine built + applied (AR-38)`), and binds the port (`Listening on port … (max 4 peers)`), with **0 errors** in stderr.
  - [x] **Fixed a latent headless NRE:** the headless branch returns before building `_ctx`, so `MainScene._Process/_Input/_UnhandledInput` were NRE-ing every frame — added a `_headless` guard; stderr is now clean.
  - [x] **Terminal HALT overlay VERIFIED in-engine** (godot-mcp): `MainScene.ShowHalt(123,0xDEADBEEF)` renders "MATCH HALTED" (danger red) + UX-DR64e message + mono `· desync · #DEADBEEF` + "Return to Menu", no runtime error (screenshot).
  - [ ] **Final manual observation (Alec, ~30s):** run `godot/tools/loopback-desync-smoke.cmd`, wait for both clients to auto-join, click a client + press `F9` → confirm BOTH clients show MATCH HALTED and stop advancing. This is the one piece that needs 3 live processes (out of this single-instance session's reach). The full server-side path + packets + determinism are exhaustively Tier-1-tested; the server boot is runtime-verified above — only the live 2-client ENet round-trip remains to eyeball.

---

## Dev Notes

### `ServerChecksumCollector` (Task 1) — skeleton (window/idempotency abbreviated; implement all of D4)
```csharp
#nullable enable
using System.Collections.Generic;

namespace ProjectChimera.Multiplayer.Server
{
    /// <summary>
    /// Server-side strict-majority desync collector (AR-40 fork #2). Buffers slot-tagged 32-bit
    /// checksums per EXECUTED sim tick within a bounded window; declares the strict-majority hash
    /// canonical and names the minority slot(s), or "no canonical" on no majority. N-shaped (N≤4 in 1.0).
    /// Server-side networking — NOT in the 30 Hz tick — but its output is order-stable (ascending slot).
    /// </summary>
    public sealed class ServerChecksumCollector
    {
        public const int MaxSlots = 4;                 // ServerTransport ceiling (8 = constant bump, Story 9.2)
        private const int Window = 8;                  // ring of recent checksum ticks (>= a few intervals)

        public readonly struct Verdict
        {
            public bool Complete { get; }              // all expected peers reported for this tick
            public bool HasMajority { get; }
            public uint Canonical { get; }
            public IReadOnlyList<int> Minority { get; } // ascending slot order
            public Verdict(bool complete, bool hasMajority, uint canonical, IReadOnlyList<int> minority)
            { Complete = complete; HasMajority = hasMajority; Canonical = canonical; Minority = minority; }
            public static readonly Verdict Pending = new(false, false, 0, System.Array.Empty<int>());
        }

        private readonly int _expected;
        // per-tick bucket: hash per slot + reported flags; keyed tick % Window (drop stale).
        // ... ring buffer of { uint tickOf; uint[] hash = new uint[MaxSlots]; bool[] got = new bool[MaxSlots]; int n; } ...

        public ServerChecksumCollector(int expectedPeerCount) { _expected = expectedPeerCount; /* alloc ring */ }

        public Verdict Record(uint tick, int slot, uint hash)
        {
            // 1. locate/init the bucket for `tick`; if `tick` is older than the window floor → return Pending (drop).
            // 2. if this (slot,tick) already reported → return Pending (idempotent).
            // 3. store hash[slot]=hash, got[slot]=true, n++.
            // 4. if n < _expected → return Pending.
            // 5. tally hashes among reported slots; find a hash held by > _expected/2 slots.
            //    - found → Verdict(complete:true, hasMajority:true, canonical, minority = reported slots (ascending) whose hash != canonical)
            //    - none  → Verdict(complete:true, hasMajority:false, 0, Array.Empty<int>())
            //    then evict the bucket.
            return Verdict.Pending;
        }
    }
}
```
> N=2 ⇒ `> 1` requires both equal; a 1-vs-1 split has no majority. N=3 ⇒ `> 1.5` ⇒ 2 equal ⇒ the third is the minority. Build the tally with a slot-indexed scan (no `Dictionary` enumeration in the attribution path) so the minority order is deterministic.

### `ServerBootstrap` (Task 4) — full (this is the whole type)
```csharp
#nullable enable
using ProjectChimera.Core.Definitions;          // ScenarioData, ScenarioValidator, ValidationResult, Validated<>
using ProjectChimera.Economy;                    // BuildingType / FactionDefinition (confirm namespaces at the types)

namespace ProjectChimera.Core.Sim
{
    /// <summary>
    /// Headless peer composition root (AR-38). Builds the EXACT 1.8 sim spine — SimulationHost +
    /// ScenarioValidator + ScenarioApplier — with NO presentation, reused verbatim by the dedicated server.
    /// Godot-free: the caller (the thin Godot edge) resolves res:// paths and loads the model/defs first.
    /// </summary>
    public static class ServerBootstrap
    {
        /// <summary>Build a validated, applied sim host, or null if the scenario fails validation
        /// (the server is authoritative — it fail-closes rather than tick unvalidated state).</summary>
        public static SimulationHost? Build(
            ScenarioData model, FactionDefinition?[] slotFactionDefs, DamageTable? damageTable,
            ILogSink log, int activeFactionCount)
        {
            var host = SimulationHost.Create(
                log, new FactionRegistry(activeFactionCount),
                slotFactionDefs[(int)Faction.Player1], slotFactionDefs[(int)Faction.Player2],
                damageTable);
            var r = new ScenarioValidator().Validate(model);   // ONLY way to obtain Validated<ScenarioData>
            if (!r.Ok) { log.Warn($"[ServerBootstrap] scenario REJECTED: {r.Error}"); return null; }
            new ScenarioApplier(host, log, slotFactionDefs).Apply(r.Value);
            return host;
        }
    }
}
```
> Confirm the exact namespaces of `FactionDefinition`/`DamageTable`/`ScenarioData` at their declarations before the `using`s (the agents place `FactionDefinition`/`BuildingType` near `Economy`; `ScenarioData`/validator in `Core.Definitions`). `FactionRegistry(int activeCount)` — the same ctor `MainScene` uses (`new FactionRegistry(2)` for 2-player).

### `ServerHost` (Task 3) — see D5 (complete skeleton there). Inject `(slot,packet)`/`(packet)` callbacks; `Halted` is terminal.

### `DesyncAlert`/`Halt` packets (Task 2) — pattern (mirror `WriteChecksum`)
```csharp
// DesyncAlert: type(1) + tick(4 LE) + canonicalHash(4 LE) = 9 bytes
public static byte[] MakeDesyncAlert(uint tick, uint canonicalHash)
{
    var b = new byte[9]; int p = 0;
    b[p++] = (byte)PacketType.DesyncAlert; WriteUint(b, ref p, tick); WriteUint(b, ref p, canonicalHash); return b;
}
public static bool TryReadDesyncAlert(byte[] buf, int len, out uint tick, out uint canonicalHash)
{
    tick = 0; canonicalHash = 0;
    if (len < 9 || (PacketType)buf[0] != PacketType.DesyncAlert) return false;
    int p = 1; tick = ReadUint(buf, ref p); canonicalHash = ReadUint(buf, ref p); return true;
}
// Halt: type(1) + tick(4 LE) + reason(1) = 6 bytes
public static byte[] MakeHalt(uint tick, HaltReason reason)
{
    var b = new byte[6]; int p = 0; b[p++] = (byte)PacketType.Halt; WriteUint(b, ref p, tick); b[p] = (byte)reason; return b;
}
public static bool TryReadHalt(byte[] buf, int len, out uint tick, out HaltReason reason)
{
    tick = 0; reason = default;
    if (len < 6 || (PacketType)buf[0] != PacketType.Halt) return false;
    int p = 1; tick = ReadUint(buf, ref p); reason = (HaltReason)buf[p]; return true;
}
```

### `DedicatedServer` wiring (Task 5) — the relay rewrite + `ServerHost` construction
- Field: `private ServerHost? _serverHost;`
- In `HandleReady`, at the `_state = State.InGame` branch (`:181-186`), after `MakeStartGame`: `int n = /* connected player slots, e.g. count of _transport.IsSlotConnected(s) for s in 0..MAX_PLAYERS */; _serverHost = new ServerHost(n, _transport.SendReliableTo, _transport.BroadcastReliable);` (confirm the exact `ServerTransport` send/broadcast signatures — wrap if they take `(int,byte[],int)` vs `(int,byte[])`).
- Replace `:148-157` with the D8 `case PacketType.Checksum:` parse-and-feed; remove the `DesyncAlert` relay case.

### Constraints & gotchas
- **`dotnet build` / `dotnet test` are authoritative** for C# correctness; the Godot MCP `run` does not rebuild the test assembly. Build + test before declaring done. [Source: 1.1–1.8 Dev Notes]
- **Additive-only ⇒ goldens byte-identical** (AC5). The sim tick is untouched; the only sim-adjacent change is a *comment/constant* (D11) + a *new* golden. If any of the three existing `*.golden.txt` moves, you leaked into the tick — fix it, do not re-record. [Source: 1.7/1.8a precedent]
- **Reuse, don't reinvent.** `SimulationHost.Create`, `ScenarioValidator.Validate`, `ScenarioApplier.Apply`, `SimChecksum.Compute`, `TickCommandPacket.WriteChecksum`/`Write*`/`ReadUint` — all exist. `ServerBootstrap` is ~15 lines because it *composes*. [Source: 1.8a/1.8b/1.7]
- **Slot is transport-authoritative.** `OnChecksum`'s `slot` is the ENet peer slot from `HandlePacket`, never the packet payload (which carries only tick+hash). A spoofed-slot checksum must be impossible. [Source: game-architecture.md:1123; D5]
- **HALT is terminal in 1.9a.** Recovery/rejoin is explicitly deferred (`game-architecture.md:2332`). The overlay offers only "Return to Menu." [Source: arch]
- **Don't double-fire desync.** Once the server consumes `Checksum`, the client P2P compare (`:365-376`) is dead for the authoritative path — guard it or leave it inert; the only halt path is the server `DesyncAlert`/`Halt`. [Source: D8/D9]
- **Godot-free boundary.** `ServerBootstrap`/`ServerHost`/`ServerChecksumCollector` are Godot-free (`GodotFreeBoundaryTest`). `ServerHost` takes transport seams (callbacks), never the concrete `ServerTransport`. `ILogSink` (Info/Warn) for any logging — never `GD.*`. [Source: project-context.md; D5/D13]
- **N≤4 ship, N-shaped build.** `MaxSlots = 4`; 8 is a later constant bump + `Faction`-enum extension (Story 9.2), not a rewrite. The collector quorum logic is identical at any N. [Source: game-architecture.md:1082-1092]
- **Pre-existing CS8632** nullable warnings are not this story's bug — leave them. [Source: 1.7 Dev Notes]

### Project Structure Notes
- **NEW (sim, Godot-free):** `godot/src/Core/Sim/ServerBootstrap.cs`.
- **NEW (server authority core, Godot-free):** `godot/src/Multiplayer/Server/ServerHost.cs`, `godot/src/Multiplayer/Server/ServerChecksumCollector.cs`.
- **NEW tests:** `godot/ProjectChimera.Sim.Tests/Server/` — `ServerChecksumCollectorTests.cs`, `ServerHostTests.cs`, `ServerBootstrapDeterminismTests.cs` (+ optional `ServerPacketTests.cs`); `godot/ProjectChimera.Sim.Tests/Golden/SameTickTieBreakGoldenTests.cs` (+ its scenario, e.g. `SameTickTieBreakScenario.cs` + `same-tick-tie-break.golden.txt`).
- **EDIT:** `godot/src/Multiplayer/NetworkCommand.cs` (Halt type + HaltReason + 4 builders/readers); `godot/src/Multiplayer/DedicatedServer.cs` (ServerHost field + ctor in HandleReady; relay rewrite at :148-157); `godot/src/Core/MainScene.cs` (headless branch re-point :185-197); `godot/src/Multiplayer/LockstepManager.cs` (DesyncAlert/Halt handlers + dormant-P2P guard); `godot/src/Core/Bootstrap/Phases/MatchLifecycleController.cs` (terminal HALT overlay); the combat resolution site (D11 comment/constant); `godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` (add the `src/Multiplayer/Server/**` include).
- **UNCHANGED (must stay so):** all three `*.golden.txt`; `SimChecksum.cs`; `SimulationHost.cs`/`ScenarioApplier.cs`/`ScenarioValidator.cs`/`Validated.cs` (consumed, not modified); `CombatEventQueue.cs`; `DamageResolver.cs` (logic — only a comment may be added at the call site, not the resolver); `ISimSystem`; the 9-system tick order.
- **NOT here:** `TickCommandsMerged`, Ready-COUNT, adaptive-delay logic, multi-hash handshake, AOT `.csproj`, two-machine harness — Epic 9 / 1.9b / post-1.0.

### Project Context Rules
_Extracted from `_bmad-output/project-context.md` + `game-architecture.md` — these govern every edit here:_
- **Simulation/Presentation boundary is sacred.** `ServerBootstrap`/`ServerHost`/`ServerChecksumCollector` are sim/sim-adjacent Godot-free C#. The Godot `Node` `DedicatedServer` + `MainScene` headless edge are presentation; they inject seams and resolve paths. No `Vector3`/`float` gameplay state, no `using Godot` in the new types. [Source: project-context.md "The One Architectural Rule"]
- **Determinism: ascending order, `Fixed`, no wall-clock.** The tie-break pins **ascending faction slot** (D11). The collector compares the existing 32-bit `SimChecksum` (Ore/Crystal/Supply/FactionBase + `SimRng.State`, all `Fixed.Raw`/int). No `float`, no `System.Random`, no `DateTime` enters any new code. [Source: project-context.md "Determinism"]
- **Reuse existing systems; no parallel ones.** The server reuses `SimulationHost`/`ScenarioApplier`/`ScenarioValidator`/`SimChecksum`/`TickCommandPacket` — it does not fork them. [Source: project-context.md "Data layout / reuse"]
- **Peer agreement is server-enforced over the whole model.** This story moves desync detection from the client P2P stopgap to a **server-side majority-vote collector** — exactly the architecture's "a trusted server computes/attests agreement; it must stop being a pure relay." [Source: project-context.md "Forward Architecture Rules"; game-architecture.md SD-5]
- **Headless detection** via `DisplayServer.GetName() == "headless"` (already at `MainScene.cs:190`); the dedicated server renders no UI (UX-DR49). [Source: project-context.md "Godot C# gotchas"]
- **Engine/runtime:** Godot 4.6.3, .NET 8 (`net8.0`); `ProjectChimera.*` namespaces; `godot.csproj`/`godot.sln`; Tier-1 `ProjectChimera.Sim.Tests` (xUnit, Godot-free). [Source: project-context.md "Technology Stack"]

### References
- [Source: epics.md#Story-1.9a (lines 688-704)] — story statement + the 3 epic ACs (ServerBootstrap builds the spine no-presentation; cross-faction same-tick tie-break + >2-player quorum, strict-majority-or-HALT; induced divergence → DesyncAlert names diverged peer + terminal HALT, UX-DR64e); Covers AR-38/AR-40/UX-DR64; Depends on 1.8c; loopback/single-machine.
- [Source: epics.md (lines 200, 229, 233, 333-334)] — AR-17 (D5 N-aware relay → stateful authority; the Epic-9 superset), AR-38 (ServerBootstrap peer root + Godot-free ServerHost; NativeAOT split deferred), AR-40 (the two M1 forks), UX-DR64e (desync→terminal HALT) + UX-DR65 (voice: "Commander", mono status).
- [Source: epics.md (lines 706-720)] — Story 1.9b owns the two-physical-machine FR-39 LAN proof (out of scope here).
- [Source: game-architecture.md:1515-1525] — Decision 5 (C6): ServerBootstrap = peer composition root; ServerHost = Godot-free authority core extracted from DedicatedServer; DedicatedServer = transport shell; the server-shared source set; `EnableDynamicLoading` must not be inherited.
- [Source: game-architecture.md:1713-1715] — Migration Step 6 stand-up procedure + the determinism test ("server start-state checksum == client offline start-state on the same scenario").
- [Source: game-architecture.md:2319-2332] — N7 RULE + reference `OnChecksum`/`_collector.Record`/`TryMajority` (the collector shape, recommendation not committed code); strict majority; minority→DesyncAlert; no majority→HALT; checksum tick = executed (post-ApplyOrders) tick; stale ticks dropped; HALT terminal until a defined recovery policy (deferred).
- [Source: game-architecture.md:1122-1126 (SD-5)] — server-side collector + majority-vote attribution; "today relayed opaquely DedicatedServer.cs:148-156"; slot-tagged hash/slot/60-tick window; slot transport-authoritative (ServerTransport.cs:170); no-majority → "global desync, no canonical" + HALT (fail-closed).
- [Source: game-architecture.md:1082-1092 (SD-8), 2301 (wire width), 1148-1153 (SD-11), 2527-2529 (M1 forks + deferred recovery policy)] — N≤4 ship / N-shaped build; 32-bit wire / 64-bit canonical; NativeAOT project-split deferred post-1.0; the two M1 forks + the deferred abort/HALT recovery-policy UX call.
- [Source: DedicatedServer.cs:32,42,128-167,171-191,199] — State machine, SLOT_FACTION, HandlePacket dispatch, the :148-157 opaque relay, HandleReady, RelayTickCommands (anti-cheat). [Source: MainScene.cs:185-197,996] — headless seam + ParsePortArg. [Source: NetworkCommand.cs:10-40,~159-178,190-328] — PacketType enum, WriteChecksum/TryReadChecksum, the Make* builders (no DesyncAlert/Halt builder). [Source: LockstepManager.cs:55,335-342,365-376] — OnDesync event, SendChecksum, the dormant P2P compare. [Source: MatchLifecycleController.cs:34] — OnDesync logs-only.
- [Source: SimulationHost.cs; ScenarioApplier.cs; ScenarioValidator.cs; Validated.cs; SimChecksum.cs; DamageResolver.cs:51-72; CombatEventQueue.cs] — the reused spine APIs + the hashed death sequence + the presentation-only event queue. [Source: ProjectChimera.Sim.Tests.csproj] — Tier-1 globs + named Multiplayer includes; the new `src/Multiplayer/Server/**` include + `Server/` test folder.

---

## Dev Agent Record

### Agent Model Used

Claude Opus 4.8 (`claude-opus-4-8`) — gds-dev-story workflow.

### Debug Log References

- Full Tier-1 suite: **183 passing, 0 failed** (`dotnet test ProjectChimera.Sim.Tests`). Server filter alone: 29 passing.
- `dotnet build godot/godot.csproj` → **0 errors** (7 pre-existing CS8632 warnings, out of scope per the story).
- One test bug found+fixed during red→green: an N=4 "two-element minority" assertion was impossible (a strict majority of 4 = 3 leaves ≤1 minority) — the collector correctly returned no-majority for a 2-2 split; test corrected.
- Tie-break golden recorded via `CHIMERA_GOLDEN_RECORD=1` then embedded; verified byte-identical in normal mode. The three existing goldens never re-recorded.
- In-engine (godot-mcp, Godot 4.6.3): game boots clean post-wiring; `MainScene.ShowHalt(123,0xDEADBEEF)` renders the terminal HALT overlay with no runtime error (screenshot).

### Completion Notes List

**Implemented (all ACs except the live 3-process loopback observation — Task 10, see below):**
- **AC1** — `ServerBootstrap.Build` (Godot-free, `src/Core/Sim/`) composes the verbatim 1.8 spine (`SimulationHost.Create` → `ScenarioValidator.Validate` → `ScenarioApplier.Apply`); fail-closed (invalid ⇒ `null` + log). `ServerBootstrapDeterminismTests` runs the SAME model the client-path applier golden uses through a ServerBootstrap-built host for 300 ticks and asserts byte-identity to `golden-applier-scenario.golden.txt` ⇒ **server sim path == client sim path**.
- **AC2** — AR-40 fork #1 pinned with a named comment at the `CombatSystem.Tick` ascending-ID resolution site (the only cross-faction same-tick *hashed* mutation is `world.Destroy`); new `SameTickTieBreakGoldenTests` + symmetric duel scenario + recorded golden prove determinism + order-stability across in-process and separate-process runs. `CombatEventQueue` untouched (Trap #4).
- **AC3** — `ServerChecksumCollector` (Godot-free): N-shaped strict-majority (`> N/2`), bounded ring window, stale-tick drop, duplicate-(slot,tick) idempotency, ascending-slot minority. Unit-tested: all-agree, one-minority@N=3, no-majority (N=2 mismatch / N=3 all-diff / N=4 2-2), stale drop, idempotency, incomplete-until-all, no-re-complete, ctor bounds.
- **AC4** — `MakeDesyncAlert`/`TryReadDesyncAlert` (9B) + `MakeHalt`/`TryReadHalt` (6B) + `Halt=0x13` + `HaltReason` (mirror the 9B Checksum LE pattern, 32-bit). `ServerHost` turns verdicts into wire actions over injected seams (DesyncAlert→minority / Halt broadcast + terminal `Halted`); slot is transport-authoritative. Client `LockstepManager` handles inbound `DesyncAlert`/`Halt` → terminal `_halted` (gates `Flush`) + `OnHalt`; `MatchLifecycleController` → `MainScene.ShowHalt` terminal danger overlay (distinct from the stall banner, "Return to Menu").
- **AC5** — additive only; **all three existing goldens byte-identical** (only the NEW tie-break golden added). New sim-adjacent types are Godot-free (`GodotFreeBoundaryTest` green). No signature change to `SimChecksum`/`ISimSystem`/`Tick`/`Apply` — `CombatSystem.Tick` gained only a comment.

**Design adherence (settled decisions honored):** server is the **arbiter**, not a voting player (D3 — no `TickCommandsMerged`/Ready-COUNT/adaptive-delay; the held `SimHost` is built but NOT ticked, for Epic 9); slot transport-authoritative (D5/D8); spectators excluded from quorum (D6); 32-bit wire, no widening (D12); fail-closed validator on the server (D2).

**Open Questions — shipped recommended defaults (Alec can override):** #1 exact HALT copy = D10 default (mono `· desync · #{canonical:X8}` for a DesyncAlert, `· @tick {n}` for a global Halt); #2 server = arbiter (not voting); #3 N=2 mismatch = no-majority → terminal HALT; #4 spectators excluded; #5 DSL-event tie-break enforcement site deferred to Epic 7; #6 `src/Multiplayer/Server/` home + Tier-1 include.

**Task 10 — made push-button + runtime-verified as far as one session allows:** added a one-click launcher (`godot/tools/loopback-desync-smoke.cmd`) + a `#if DEBUG` auto-join/auto-ready (`LobbyUi.AutoJoinDedicated` + `MainScene --autojoin`) so Alec only **runs the .cmd, then clicks a client + presses F9**. Runtime-verified this session: (a) the **headless server boots clean** via the real Godot binary — builds + validates the sim spine + binds the port, 0 stderr errors; (b) fixed a **latent headless NRE** (`_Process`/`_Input`/`_UnhandledInput` dereferenced a null `_ctx` after the headless early-return) via a `_headless` guard; (c) the **terminal HALT overlay renders** correctly in-engine. The one piece needing 3 live processes — the 2-client F9 → server-`Halt` → both-halt ENet round-trip — is the final eyeball for Alec; everything it builds on (collector quorum, `ServerHost`, packets, `ServerBootstrap` determinism) is Tier-1-tested and the server boot is runtime-verified.

### Change Log

- 2026-06-24 — Story 1.9a implemented (Tasks 1–9 complete; Task 10 tooling complete + server-boot/overlay runtime-verified, the live 2-client F9 round-trip is a push-button manual eyeball). 3 net-new Godot-free types (`ServerBootstrap`/`ServerHost`/`ServerChecksumCollector`), 4 net-new packet builders + `Halt`/`HaltReason`, headless-branch re-point + relay rewrite, client HALT handler + terminal overlay, AR-40 tie-break pin + golden. Tier-1 183 green; existing goldens byte-identical; `godot.csproj` builds. baseline_commit `3599834` preserved.
- 2026-06-24 — Task 10 loopback tooling: one-click launcher (`godot/tools/loopback-desync-smoke.cmd`) + `#if DEBUG` auto-join/auto-ready (`LobbyUi`/`MainScene`). Fixed a latent headless NRE (`_headless` guard on `MainScene._Process`/`_Input`/`_UnhandledInput`). Headless server boot runtime-verified (sim spine built + port bound, 0 stderr errors).
- 2026-06-24 — Loopback bug fix (found while standing up the smoke): `DedicatedServer.HandleReady` dropped a Ready received before both peers connected, so a fast/auto-join client deadlocked on "Ready! Waiting for other player". Now records the early Ready and starts on both-connected-AND-ready (order-independent). Server boot re-verified; `godot.csproj` builds clean.

### File List

**NEW — production (Godot-free):**
- `godot/src/Core/Sim/ServerBootstrap.cs`
- `godot/src/Multiplayer/Server/ServerChecksumCollector.cs`
- `godot/src/Multiplayer/Server/ServerHost.cs`

**NEW — loopback smoke tooling (Task 10):**
- `godot/tools/loopback-desync-smoke.cmd` — one-click launcher: headless server + two auto-joining client windows; then F9 on a client induces the desync.

**NEW — tests + golden:**
- `godot/ProjectChimera.Sim.Tests/Server/ServerChecksumCollectorTests.cs`
- `godot/ProjectChimera.Sim.Tests/Server/ServerHostTests.cs`
- `godot/ProjectChimera.Sim.Tests/Server/ServerBootstrapDeterminismTests.cs`
- `godot/ProjectChimera.Sim.Tests/Server/ServerPacketTests.cs`
- `godot/ProjectChimera.Sim.Tests/Golden/SameTickTieBreakScenario.cs`
- `godot/ProjectChimera.Sim.Tests/Golden/SameTickTieBreakGoldenTests.cs`
- `godot/ProjectChimera.Sim.Tests/Golden/same-tick-tie-break.golden.txt`

**MODIFIED — production:**
- `godot/src/Multiplayer/NetworkCommand.cs` — `PacketType.Halt=0x13`, `HaltReason`, `MakeDesyncAlert`/`TryReadDesyncAlert`/`MakeHalt`/`TryReadHalt`.
- `godot/src/Multiplayer/DedicatedServer.cs` — `SimHost` prop + `ServerHost _serverHost`; construct in `HandleReady`; relay rewrite at the `Checksum` case (drop `DesyncAlert` relay); usings; Start log. **`HandleReady` now RECORDS an early Ready (one received before both peers connect) instead of dropping it, and starts only when both are connected AND ready** — fixes an auto-join / fast-peer deadlock found during the loopback smoke (a client that readies the instant it connects used to have its Ready thrown away → "Ready! Waiting for other player" forever).
- `godot/src/Core/MainScene.cs` — headless-branch re-point + `BuildHeadlessServerSimHost()`; `ShowHalt(uint,uint)` terminal overlay; `#if DEBUG` F9 divergence hook + `--autojoin` arg/`ParseAutoJoinArg`; `_headless` guard on `_Process`/`_Input`/`_UnhandledInput` (fixes a latent per-frame headless NRE).
- `godot/src/Multiplayer/LockstepManager.cs` — `OnHalt` event + `_halted` field + `RaiseHalt`; inbound `DesyncAlert`/`Halt` cases; dormant-P2P comment; `Flush` halt gate.
- `godot/src/Multiplayer/LobbyUi.cs` — `#if DEBUG` `AutoJoinDedicated`/`TryAutoReady` (reuses the real JoinGame/Ready path for the one-click loopback smoke).
- `godot/src/Core/Bootstrap/Phases/MatchLifecycleController.cs` — subscribe `OnHalt` → `ShowHalt`.
- `godot/src/Combat/CombatSystem.cs` — AR-40 fork #1 tie-break named comment at the resolution site (comment only — no behavior change).

**MODIFIED — tests/build:**
- `godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` — `src/Multiplayer/Server/**` compile include + `same-tick-tie-break.golden.txt` embedded resource.
- `godot/ProjectChimera.Sim.Tests/Golden/GoldenApplierScenario.cs` — `BuildModel()`/`BuildFaction()` → `public` (reused by the AC1 determinism test).

**MODIFIED — artifacts:**
- `_bmad-output/implementation-artifacts/sprint-status.yaml` — 1.9a → in-progress → review.

---

## Open Questions for Alec (surfaced after analysis — recommended defaults are in the story; answer only if you want to change them)

1. **Exact terminal HALT message string (UX call).** The architecture/UX specify the *behavior* (terminal, "Commander" voice, mono status, distinct from the stall banner) but **no literal copy**. Story ships the D10 recommended default — _"MATCH HALTED · Simulation desync detected at tick {tick}. The match cannot continue. · desync · #{canonical:X8} · [Return to Menu]"_. Override if you want different wording.
2. **Server = arbiter (recommended) vs voting player in 1.9a.** Default (D3): the server holds validated start-state (AC1 proven) and quorums over **peer-reported** checksums; the server casting its **own re-simulated match vote** (which needs `TickCommandsMerged`) is **Epic 9**. Attribution is unit-tested at N=3; the 2-client loopback proves HALT-on-no-majority. Confirm — pulling the server's live vote into 1.9a drags SD-1/SD-2 (the merged-command feed) forward and materially grows the story.
3. **N=2 semantics.** Default: a 2-peer mismatch is **no majority → terminal HALT for both** (consistent with strict majority; the "2-peer strict-equality fast path" is an optional optimization, not built). Confirm.
4. **Spectators in the quorum.** Default (D6): **excluded** (Neutral slots are not counted in `expectedPeerCount`). The architecture's alternative is "spectators compute + attest read-only." Excluding is simpler for 1.9a; read-only attest can be a fast-follow.
5. **Cross-faction tie-break enforcement site for forward DSL events.** Default (D11): pin the **rule** (ascending faction slot) now + test the current combat path; the exact code site for ordering cross-faction **DSL events** (inside `ScenarioDirector` event collection vs at the merged-packet demux) is **Epic 7 (SD-2)** — not decided here.
6. **`ServerHost`/`ServerChecksumCollector` home + Tier-1 include.** Default (D13): `src/Multiplayer/Server/` (Godot-free subfolder) + a `<Compile Include="..\src\Multiplayer\Server\**\*.cs" />` in the Tier-1 csproj (mirrors the existing named Multiplayer includes). Alternative: put the collector in the already-globbed `src/Core/Sim/`. The architecture's target tree puts them under `src/Multiplayer/Server/`.
