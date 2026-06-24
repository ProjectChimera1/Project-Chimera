---
baseline_commit: 38e0b61
---

# Story 1.9b: FR-39 two-machine LAN determinism — green the #1 ship-risk gate

Status: done  <!-- AC1–3 + AC5 dev-complete + code-reviewed (gds-code-review 2026-06-24, 3-layer adversarial; all 6 patches applied + suite green). AC4 (the physical two-machine LAN run) is PARKED pending a 2nd machine per Resolved Decision #1 — Task 7 stays unchecked; run lan-determinism-runbook.md + record it in the Change Log when the hardware exists. -->

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->
<!-- 1.9b is the SECOND half of the former Story 1.9 and is a DIFFERENT KIND of story than every Epic-1
     story before it: its acceptance is a HUMAN-PERFORMED two-physical-machine LAN run (the dev agent — an
     LLM — cannot run two machines). The dev agent's job is to make that gate EXECUTABLE, SELF-EVIDENCING,
     and AUTOMATED-PROXY-PROVEN on one machine over real ENet sockets, then hand Alec a push-button runbook.
     1.9a (DONE) already built + loopback-proved the whole server checksum/quorum/HALT engine AND fixed the
     root-cause ENet packet-drop bug, so real MP delivery works. 1.9b adds the missing PASS evidence trail,
     a LAN launcher + runbook, an extended headless self-test, and then Alec runs the physical gate. -->

## Story

As a solo developer who must prove multiplayer never silently desyncs,
I want the FR-39 two-machine LAN determinism gate made **executable, instrumented with a clear PASS/FAIL verdict, and proven end-to-end over real network sockets** (with the physical two-machine run reduced to a push-button runbook + drill), so that I can confirm — and re-confirm on demand — that two players complete a full match in lockstep for **300+ ticks with ZERO desync**, and that a real divergence trips the DesyncAlert/HALT path with a clear message, **closing the #1 ship risk the entire 1.0 rests on**.

> **This is the second half of the determinism strangler's networking milestone (former Story 1.9, split so the manual two-machine run isn't buried under the engine work).** Story **1.9a is DONE**: it built the Godot-free server authority (`ServerBootstrap` + `ServerHost` + `ServerChecksumCollector`), the `DesyncAlert`/`Halt` packets, the client HALT handler + terminal overlay, and — critically — **fixed a pre-existing ENet bug that was silently dropping every data packet** (`ENetTransport`/`ServerTransport` read `service()`'s `result[2]` instead of `peer.GetPacket()`), so real multiplayer delivery now works at all. The engine is believed correct and is **loopback-proven** (an automated in-process self-test + Alec's live 3-window run).
>
> **What 1.9b is really about** is the part 1.9a explicitly deferred: the proof on **two real physical machines** (FR-39 — the #1 ship risk, *never run*), plus the instrumentation and tooling that make that proof **readable** (a server-side "N windows compared, 0 desync — PASS" verdict that does not exist today) and **repeatable** (a LAN launcher + runbook + an extended headless self-test). **Three facts shape this story and are settled below:** (1) An LLM dev agent **cannot run two machines** — so AC1–AC3 + AC5 are dev-agent-completable + machine-verifiable, and **AC4 is a human gate** Alec performs (recorded in the Change Log, exactly as 1.9a's live run was). (2) The networking is **already LAN-ready** (`JoinGame(ip,port)` takes any IP; the lobby has an IP field; the server binds to `"*"`) — so this is **not** a networking-feature story; the real code gap is **server-side positive observability** (`ServerHost.OnChecksum` only ever speaks on *desync*; a clean window logs/counts nothing). (3) "P2.4" is **not** a formal spec — it is shorthand for this LAN determinism gate; a concrete deterministic 2-player scenario **already exists as data** (`map_02_iron_crossing.json`), so **no scenario authoring is required**.

## Acceptance Criteria

1. **(Server emits a determinism PASS/FAIL verdict — the missing positive evidence)** **Given** a match running against the 1.9a dedicated server, **When** the connected player peers report their per-window checksums and all agree, **Then** the server logs a per-window confirmation (`[Determinism] tick {t}: all {N} peers matched 0x{hash:X8} (window #{k})`) and maintains running counters; on match end / server stop it emits a terminal summary (`[Determinism] MATCH SUMMARY: {windows} windows compared, {desyncs} desync — PASS|FAIL`). `ServerHost` stays **Godot-free** (logs via an injected `ILogSink`, never `GD.*`) and exposes `WindowsCompared` / `DesyncCount` / `Passing` for assertions. Tier-1 unit tests prove: a clean N-peer run increments `WindowsCompared` only and ends `PASS`; a divergence increments `DesyncCount` and flips the summary to `FAIL`; the summary string is exact. **No change to what is hashed or how often** (the 60-tick `SimChecksum` cadence and `SimChecksum.Compute` are untouched).

2. **(Automated end-to-end proxy over REAL loopback ENet sockets — no second machine)** **Given** the extended headless self-test, **When** run via `godot --headless -- --loopback-test`, **Then** it stands up the **real** `DedicatedServer` (real `ServerHost` + collector) + **two real `ENetTransport` clients** over loopback ENet in one process, runs a **clean phase of ≥5 comparison windows** (≥300 ticks-equivalent) of matching checksums and asserts the server reports **PASS** with the expected window count and **zero halt**, **then** injects a one-peer divergence and asserts **both** clients receive the server HALT — and exits `0` **only if BOTH** the clean-PASS phase and the HALT drill pass (a regression guard for the full network→verdict→HALT path). This is the strongest FR-39 proof obtainable without a second machine and **must stay green**.

3. **(Two-machine launcher + runbook + visible in-sync evidence on each machine)** **Given** a parameterized LAN launcher and a written runbook, **When** Alec follows them, **Then**: a launcher accepts a **target server IP** (no longer hardcoded `127.0.0.1`) and starts the correct process per the pinned topology (D3); a runbook (`godot/tools/lan-determinism-runbook.md`) documents network/firewall/port setup, the existing lobby **content-hash match** pre-check, the canonical **P2.4 scenario** (D4), the play-≥300-ticks step, **where to read the verdict** (the server console summary from AC1 + the client checksum readout), the **F9 desync drill**, the **adaptive-delay watch-item** (D9), and troubleshooting; and the client-side live checksum readout (`MainScene.cs:596-597`) + per-window `[Checksum]` client log (`MainScene.cs:308`) are **verified visible** during a real online match so **both screens show the matching hash every window**. The existing loopback smoke (`loopback-desync-smoke.ps1`) is **not broken**.

4. **(The physical two-machine gate — HUMAN-performed; the actual FR-39 proof)** **Given** two Windows machines on the same LAN running the runbook against the canonical P2.4 scenario, **When** they play a full match for **≥300 ticks** (≥5 windows; a multi-minute match recommended for a meaningful sample), **Then** the server reports **PASS** with **zero desync**, both clients' checksum readouts **match every window**, and the adaptive input delay adapts (4→2) **without** causing a desync — **AND** a deliberate **F9 divergence** on one machine fires the `DesyncAlert`/`Halt` path with the clear terminal "MATCH HALTED" message on **both** machines. **This AC is confirmed by Alec's live run (recorded in the Change Log), not by the dev agent.** The dev-agent deliverables (AC1–3, 5) make it push-button and **must be complete and green before this gate is attempted**. **PARKED as of 2026-06-24:** Alec has one machine right now (Resolved Decision #1), so AC4 waits for a second LAN machine — the story reaches `review` on AC1–3 + AC5; AC4 is performed and logged when the hardware is available. This is the designed split, not an incomplete story.

5. **(No sim behavior change — goldens byte-identical, Godot-free boundary preserved, suite green)** **Given** the full Tier-1 suite, **When** it runs, **Then** **all four** existing `*.golden.txt` verify green **unchanged** (`git status` clean for goldens) — every change here is **additive networking/observability + tooling + docs**; nothing mutates the 30 Hz tick, `SimChecksum`, the checksum interval, or the wire width. `ServerHost`/`ServerChecksumCollector` contain **no `using Godot`/`GD.*`** and pass `GodotFreeBoundaryTest`. If a golden moves, something leaked into the tick — **fix it; do not re-record.**

_Covers: FR-39 (two-machine LAN, 300+ ticks, zero desync — the hard gate), UX-DR84 (LAN lockstep journey), UX-DR64e (desync→terminal HALT verified over the wire, not just unit-tested). Depends on: 1.9a (DONE — server quorum/HALT engine + the ENet packet-drop fix that makes real MP deliver). Independent of Epic 9 (N-scale relay, Nakama matchmaking, server-vote, disconnect policy) and Story 1.10c (cross-platform Windows↔Linux gate)._

> Split from former 1.9 — **THE #1 ship risk**, isolated as a dedicated physical-machine gate. UX-DR84 LAN lockstep journey: "300+ ticks with checksums in lockstep, zero desync." Brownfield: the networking is already LAN-capable; this story adds the missing **server-side PASS verdict** (`ServerHost`), a **LAN launcher + runbook**, an **extended self-test**, and pins the canonical scenario — then the human gate. **Additive only — the 30 Hz tick, `SimChecksum`, and the wire are untouched.**

---

## Developer Context

**You (the dev agent) have ONLY this file. Read this whole section before editing anything.** This story is **lighter on production code than 1.9a** and heavier on **observability, tooling, docs, and a human verification gate**. The production code is essentially **one focused change**: give `ServerHost` a positive PASS/FAIL verdict (counters + an injected `ILogSink` + a summary), because today it is **silent on success and only speaks on failure**. Everything else is: extend the existing headless self-test to also assert the clean-PASS path, add a LAN launcher + runbook, pin the canonical scenario, and verify the per-machine on-screen evidence. **You cannot satisfy AC4 — it is Alec's physical two-machine run. Do NOT fake it, and do NOT mark the story done on AC4's behalf.** The traps are:

1. **Pretending to run two machines (or claiming AC4 is met).** You are an LLM; you have one machine. AC4 is a **human gate**. Your contract is AC1–AC3 + AC5 (all machine-verifiable here) plus a **push-button runbook** so Alec's physical run is trivial. The closest you get to "two machines" is the **loopback self-test over real ENet sockets** (AC2) — that proves the network/verdict/HALT path but **NOT** machine-specific divergence (different Windows locale/region, any stray float/hardware path). Be honest about that boundary in your Completion Notes. (D1)
2. **Putting `using Godot`/`GD.*` into `ServerHost` or `ServerChecksumCollector` for the new logging.** They are **Godot-free + Tier-1-tested** (`GodotFreeBoundaryTest`). Add observability via an **injected `ILogSink`** — the **exact** Godot-free logging seam `SimulationHost.Create(ILogSink, …)` and `ServerBootstrap.Build(…, ILogSink, …)` already use. `DedicatedServer` (the Godot Node) injects a `GodotLogSink` (its `Info` → `GD.Print`, so the verdict lands in the server's Output console); tests inject a **capturing** `ILogSink`. Do **not** add a Godot dependency to the authority core. (D2)
3. **Hardening the Ready handshake to "prove content sync."** Multi-hash `{scenarioHash, rulesetHash, startStateHash}`, `PROTOCOL_VERSION` compare, and `hash==0` hard-reject are **Epic 9 (Story 9-4)** and were explicitly fenced out of 1.9a. 1.9b **only USES** the **existing** lobby scenario-hash display (`LobbyUi.cs:280-282, 366-367` — "Your map: 0x… / Peer map: 0x…") as a **manual pre-check in the runbook**. Leave the fail-open compare exactly as-is. (D8)
4. **Re-recording a golden.** This story is **additive** (networking/observability/tooling/docs). It must change **zero** sim ticks — same class as 1.7/1.8a/1.9a. If any `*.golden.txt` moves, you leaked something into the tick — **find it and fix it; never set `CHIMERA_GOLDEN_RECORD`.** (AC5)
5. **Pulling Epic 9 forward.** No Nakama matchmaking/lobby (9-6), no server **re-simulated vote** / `TickCommandsMerged` (9-3a), no disconnect freeze/continue policy (9-5), no N>2 / 8-player widening (9-2/9-3a), no adaptive-delay **server dictation** (9-4). 1.9b is **strictly 2-player LAN on the 1.9a engine** as it stands. (Scope fence)
6. **Confusing 1.9b with the cross-platform gate.** Windows↔Linux golden-diff determinism (AR-37) is **Story 1.10c** (via WSL). 1.9b is **same-platform Windows↔Windows** over a LAN. Do not bring Linux/WSL/cross-platform into 1.9b. (Scope fence)
7. **Building a listen-server / "host-and-play" mode.** It does not exist and is **out of scope**. The 1.9a quorum collector lives in `DedicatedServer`, and the AC requires the **server-collected** checksums — so the topology is **dedicated server + 2 clients** (the server co-located with client 1 on machine A; client 2 on machine B). Do not add a host-plays-too path. (D3)
8. **Widening the wire / changing the checksum cadence / touching `SimChecksum`.** The observability **reads the existing `Verdict`**; it does not change what is hashed (`SimChecksum.Compute`, `AlgoVersion=3`), the 32-bit wire, or the 60-tick interval (`SimulationLoop.ChecksumInterval = 60`). (Trap-class of AC5)
9. **Over-engineering the self-test into "two real sims in one process."** Real **sim** determinism is **already Tier-1-proven** (1.9a's `ServerBootstrapDeterminismTests`: 300 ticks byte-identical to the golden). The self-test's job (AC2) is to prove the **network → collector → verdict → HALT** path carries matching checksums to **PASS** and a divergence to **HALT** — **synthetic** checksums (as the existing test uses) are sufficient and correct for that. Do **not** rebuild full `LockstepManager`-driven sim loops in-process. (D6)

### The shape of the work (1 focused server-observability change + 1 self-test extension + 1 LAN launcher + 1 runbook + scenario pin + AC5 proof; then Alec's physical gate. Goldens UNCHANGED.)

1. **Server determinism verdict** (`ServerHost.cs` + `DedicatedServer.cs`, + Tier-1 tests) — `ServerHost` gains an injected `ILogSink`, `WindowsCompared`/`DesyncCount`/`Passing`, per-clean-window Info log, and a `LogSummary()`; `DedicatedServer` constructs it with a `GodotLogSink` and calls `LogSummary()` on match end/disconnect/exit. (AC1) — **the only real production code.**
2. **Extend the headless self-test** (`LoopbackDesyncSelfTest.cs`) — add a clean-PASS phase that asserts the server reaches `PASS` with ≥5 windows and zero halt **before** the existing divergence→HALT drill; exit 0 only if both pass. (AC2)
3. **LAN launcher** (`godot/tools/lan-desync-smoke.ps1`, net-new) — a `-ServerIp`/`-Port`-parameterized sibling of the loopback script: on the server machine starts `--server`; on a client machine starts `--autojoin <ip>:<port>`. Keep the safe stale-process cleanup; do not break `loopback-desync-smoke.ps1`. (AC3)
4. **Two-machine runbook** (`godot/tools/lan-determinism-runbook.md`, net-new) — prerequisites, pinned topology, canonical scenario, step-by-step, where to read PASS, the F9 drill, the adaptive-delay watch-item, troubleshooting, explicit pass/fail criteria. (AC3/AC4)
5. **Pin + verify the canonical P2.4 scenario** (reuse existing data; an optional Tier-1 determinism check of that scenario through `ServerBootstrap` if it is not already golden-covered). (AC1/AC4)
6. **Verify per-machine on-screen evidence** — confirm the client checksum readout (`MainScene.cs:596-597`) + `[Checksum]` per-window log (`MainScene.cs:308`) are visible in an online match; harden only if hidden. (AC3)
7. **Prove AC5** — full Tier-1 green, four existing goldens byte-identical, `ServerHost`/collector Godot-free, no `SimChecksum`/tick/wire change. (AC5)
8. **Manual two-machine gate** — Alec's physical run + F9 drill per the runbook. (AC4 — human)

### Key design decisions (settled here — do NOT re-derive)

**D1 — 1.9b = "make the gate executable + self-evidencing + automated-proxy-proven," with the physical run as a HUMAN gate.** An LLM dev agent cannot run two physical machines, so the story is engineered to deliver **everything build-able and machine-verifiable** (AC1 server verdict, AC2 loopback self-test over real sockets, AC3 launcher+runbook+on-screen evidence, AC5 no-regression) and to make Alec's physical run (AC4) **push-button**. This mirrors **1.9a Task 10 exactly**: the automated headless self-test + Alec's live multi-window run together discharged the end-to-end AC; the dev agent owned the automation, Alec owned the live eyeball. The story reaches **`review`** when AC1–3 + AC5 are done and green; **AC4 is confirmed by Alec** and logged in the Change Log when performed. _Do not block the engineering deliverables on the hardware (Resolved Decision #1 — Alec has one machine as of 2026-06-24, so AC4 is parked and the build proceeds) — they de-risk FR-39 as far as is possible without a second machine, regardless of when the physical run happens._

**D2 — Server observability lives in `ServerHost` via an injected `ILogSink` + public counters; `DedicatedServer` injects a `GodotLogSink`.** `ServerHost` today (`ServerHost.cs:44-63`) acts **only** on desync and logs/counts **nothing** on a clean window. Change:
```csharp
// ServerHost ctor gains an ILogSink (Godot-free seam already used by SimulationHost/ServerBootstrap):
public ServerHost(int expectedPeerCount, ILogSink log,
                  Action<int,byte[]> sendReliableTo, Action<byte[]> broadcastReliable) { … }

public int  WindowsCompared { get; private set; }   // clean, unanimous windows tallied
public int  DesyncCount     { get; private set; }    // minority alerts + no-majority halts
public bool Passing => DesyncCount == 0;

public void OnChecksum(int slot, uint tick, uint hash)
{
    if (Halted) return;
    var v = _collector.Record(tick, slot, hash);
    if (!v.Complete) return;

    if (v.HasMajority && v.Minority.Count == 0)       // ALL peers agree → the PASS evidence
    {
        WindowsCompared++;
        _log.Info($"[Determinism] tick {tick}: all {ExpectedPeerCount} peers matched 0x{v.Canonical:X8} (window #{WindowsCompared}).");
    }
    else if (v.HasMajority)                            // majority + named minority
    {
        DesyncCount++;
        foreach (int s in v.Minority)
            _sendReliableTo(s, TickCommandPacket.MakeDesyncAlert(tick, v.Canonical));
        _log.Warn($"[Determinism] tick {tick}: DESYNC — minority slot(s) {string.Join(',', v.Minority)} (canonical 0x{v.Canonical:X8}).");
    }
    else                                              // no strict majority → terminal HALT
    {
        DesyncCount++;
        _broadcastReliable(TickCommandPacket.MakeHalt(tick, HaltReason.NoMajority));
        Halted = true;
        _log.Warn($"[Determinism] tick {tick}: GLOBAL DESYNC — no canonical hash. Broadcasting terminal HALT.");
    }
}

/// Emit the terminal verdict. Call on match end / disconnect / server shutdown.
public void LogSummary() =>
    _log.Info($"[Determinism] MATCH SUMMARY: {WindowsCompared} windows compared, {DesyncCount} desync — {(Passing ? "PASS" : "FAIL")}.");
```
`DedicatedServer` constructs `new ServerHost(playerCount, new GodotLogSink(), wrap(_transport.SendReliableTo), _transport.BroadcastReliable)` (it currently passes only the two transport lambdas — add the `GodotLogSink`). Call `_serverHost?.LogSummary()` from the server's match-end / `HandleDisconnect` / `_ExitTree` path so a human reading the server console sees the verdict. **The behavior (alerts/HALT) is unchanged — this only ADDS counters + log lines.** Tests inject a capturing `ILogSink` and assert counters + the exact summary string. _(Alternative considered + rejected: keep `ServerHost` pure and log from `DedicatedServer`. Rejected because the clean-window signal isn't currently surfaced out of the void `OnChecksum`, and `ILogSink` is the established Godot-free seam — injecting it keeps the verdict unit-testable.)_

**D3 — Topology is pinned: dedicated server + 2 clients, server co-located with client 1 on machine A; client 2 on machine B.** There is **no listen-server** mode, and the AC requires the **server-collected** checksums (the 1.9a quorum lives in `DedicatedServer`), so the run is **3 processes across 2 machines**: **Machine A** runs the dedicated server (`--server`) **and** client 1 (a normal client window); **Machine B** runs client 2. Both clients connect to **machine A's LAN IP** (client 1 may use `127.0.0.1`; client 2 uses A's `192.168.x.x`). **The determinism proof is real** because client 1 (machine A) and client 2 (machine B) run **independent sim instances on different physical machines** and the server compares their checksums — the server happening to share machine A is irrelevant (in 1.9a the server holds validated state but is the **arbiter**, not a ticking player). _(A 3-machine variant — server alone on A, clients on B and C — is more "pure" but needs 3 boxes; the 2-machine setup is the gate. Open Question #2.)_

**D4 — Canonical "P2.4 scenario" = an existing 2-player symmetric skirmish; recommend `map_02_iron_crossing.json`. No authoring.** "P2.4" is **not** a formal test-case ID anywhere in the PRD/test docs — it is shorthand for this LAN gate (UX-DR84 / FR-39). Deterministic 2-player scenarios **already exist** under `godot/resources/data/scenarios/` (`map_02_iron_crossing.json` — symmetric Alpha-vs-Beta, CommandCenters + 2 workers/side + 8 resource nodes, `DestroyAllBuildings` win; `alpha_map_01.json` — the established primary test scenario; plus `map_03…map_12`). **Pin `map_02_iron_crossing.json` as the canonical P2.4 scenario** (symmetric ⇒ no slot is advantaged, and it has economy + combat to exercise the sim). Reuse as-is. _(Open Question #3 — confirm the scenario; `alpha_map_01.json` is the obvious alternative.)_

**D5 — PASS threshold: ≥300 ticks = ≥5 comparison windows is the FLOOR; a full multi-minute match is the real test.** The checksum interval is **60 ticks** (`SimulationLoop.ChecksumInterval = 60`), so the FR-39 literal "300+ ticks" is only **5 windows** (~10 s). That is the minimum bar; the runbook should call for a **multi-minute match** (a 10-minute match ≈ 18,000 ticks ≈ **300 windows**) for a meaningful sample, and **must run long enough for the adaptive input delay to adapt** (D9). The server summary reports the **actual** window count, so the evidence scales with how long Alec plays. The self-test (AC2) asserts the floor (≥5 windows) headlessly.

**D6 — The self-test stays SYNTHETIC-checksum (network-path proof), not a real-sim harness.** Real **sim** determinism is already Tier-1-proven by 1.9a's `ServerBootstrapDeterminismTests` (300 ticks == committed golden). `LoopbackDesyncSelfTest` proves the **transport → collector → verdict → HALT** path with fixed `GOOD`/`BAD` hashes. AC2 only **adds a clean-PASS assertion** to that existing shape (server reaches `PASS`, ≥5 windows, zero halt) before the existing divergence drill. Do not rebuild `LockstepManager`-driven sim loops in-process — that is unnecessary scope and the two existing proofs already cover sim + network separately. _(The one thing only AC4's physical run covers — machine-specific divergence — is called out honestly in D1.)_

**D7 — Run-from-source (`godot --path`) on both machines for 1.9b; export presets are Epic 10.** There is **no `export_presets.cfg`** in the repo (the game runs from the editor / `godot --path godot/` only). For 1.9b, **both machines run from source** with the Godot 4.6.3 mono editor + the repo cloned (a solo dev's two dev machines) — this needs **zero** export pipeline and avoids export-config risk. A shipped Windows `.exe` (and the Linux build) belong to **Epic 10** (Story 10-7 Linux export, 10-9a Steam pipeline). _(Optional accelerator, NOT required: a throwaway Windows "Desktop" export preset would let machine B run a `.exe` without the editor; mention it in the runbook as optional, but do not build the export pipeline here. Open Question #4.)_

**D8 — Use the EXISTING on-screen evidence + the existing lobby content-hash check; do not build or harden either.** Per-machine in-sync evidence already exists: each client logs `[Checksum] tick={t} hash=0x{…:X8}` every window (`MainScene.cs:308`) and renders a live checksum readout (`MainScene.cs:596-597`, `0x{_host.LastChecksum:X8}`). The lobby already shows a **content-hash match** at Ready time (`LobbyUi.cs:280-282, 366-367` — "Your map: 0x… / Peer map: 0x…"), which is UX-DR84's "all content synced" pre-check. 1.9b **verifies these are visible/usable** in a real online match and references them in the runbook — it does **not** rebuild them and does **not** harden the handshake (Trap #3).

**D9 — The adaptive input delay (RTT 4→2) MUST be exercised and must NOT cause desync.** Lockstep starts at 4 ticks of input delay and adapts down toward 2 on a low-RTT LAN (`Snapshot.md:56-60` flags this: "Both machines should log `[Lockstep] Delay: 4 → 2 ticks`… the delay reduction must NOT cause desync"). The runbook must instruct Alec to **play long enough for the delay to adapt** and to confirm determinism **holds across the change**. This is a real determinism risk surface unique to the live run — it is not exercised by the synthetic self-test.

### Pre-flight facts you MUST NOT re-derive (verified against the codebase, 2026-06-24)

**Networking is already LAN-ready (this is NOT a networking-feature story):**
- **Client join takes any IP.** `ENetTransport.JoinGame(string ip, int port)` (`src/Multiplayer/ENetTransport.cs:71-90`) → `_peer = _host.ConnectToHost(ip, port, CHANNEL_COUNT)` (`:78`). **Not** hardcoded to localhost. [Source: ENetTransport.cs:71-90]
- **The lobby has an IP entry field.** `LobbyUi._ipField` (`LobbyUi.cs:54`); `OnJoinPressed` reads `_ipField.Text.Trim()` (`:190`) and calls `_transport.JoinGame(ip, port)` (`:194`); placeholder `"192.168.1.x"` (`:477`). A user on machine B can type machine A's LAN IP today. [Source: LobbyUi.cs:50-56,188-205,473-481]
- **Server binds to all interfaces.** `ServerTransport.Listen(int port)` → `CreateHostBound("*", port, maxPeers: MAX_SLOTS, …)` (`ServerTransport.cs:56`); `DedicatedServer.DEFAULT_PORT = 7777` (`DedicatedServer.cs:39`); `--port N` override via `MainScene.ParsePortArg` (`MainScene.cs:~196-205`). [Source: ServerTransport.cs:56; DedicatedServer.cs:39; MainScene.cs]
- **No listen-server mode; topology must be dedicated-server + 2 clients.** `MainScene` headless/`--server` branch builds the server spine and `return`s before any client setup (`MainScene.cs:204-235`); there is no host-and-play. The 1.9a quorum collector lives in `DedicatedServer`, so the server-collected-checksum AC requires the dedicated server. [Source: MainScene.cs:204-235]
- **No export presets.** No `export_presets.cfg` under `godot/`; the game runs from the editor / `godot --path`. [Source: repo scan]

**The observability gap (the real production work):**
- **`ServerHost.OnChecksum` is silent on success.** `ServerHost.cs:44-63` acts **only** on the minority (`DesyncAlert`) and no-majority (`Halt`) branches; a clean unanimous window (`v.HasMajority && v.Minority.Count == 0`) does **nothing** — no log, no counter. `ServerHost` has **no `ILogSink`** (pure logic + two transport seams); it exposes `Halted` and `ExpectedPeerCount` (`:27,:30`). **This is what AC1 fills.** [Source: ServerHost.cs:20-64]
- **The `Verdict` has everything needed to log a clean window.** `ServerChecksumCollector.Verdict` (`ServerChecksumCollector.cs:37-58`): `Complete`, `HasMajority`, `Canonical`, `Minority` (ascending slots). A clean window ⇒ `Complete && HasMajority && Minority.Count == 0`, `Canonical` = the agreed hash. [Source: ServerChecksumCollector.cs:37-58]
- **`ILogSink` is the Godot-free logging seam.** `SimulationHost.Create(ILogSink log, …)` and `ServerBootstrap.Build(…, ILogSink log, …)` already take it; `MainScene` supplies a `GodotLogSink` (`MainScene.cs:34`, `_logSink = new GodotLogSink()`) whose `Info` routes to `GD.Print`. Inject this into `ServerHost`. [Source: SimulationHost.cs; ServerBootstrap.cs; MainScene.cs:34]
- **Server logs go to its Output/console via `GD.Print`.** e.g. `DedicatedServer.cs:88-89` "[Server] Both peers ready — broadcasting StartGame… (quorum N=…)." A human running `--server` reads this window. [Source: DedicatedServer.cs]

**Checksum cadence + send path:**
- **Interval = 60 ticks.** `SimulationLoop.ChecksumInterval = 60` (`SimulationLoop.cs:33`); fires `OnChecksum?.Invoke(CurrentTick, LastChecksum)` at `CurrentTick % 60 == 0` (`:97-102,:137-138`). 300 ticks ⇒ 5 windows. [Source: SimulationLoop.cs:33,97-102,137-138]
- **Client sends checksum to the server.** `LockstepManager.SendChecksum(uint tick, uint localHash)` (`:355-366`) guards `!IsOnline || IsSpectator` then `SendReliable`. (Spectators were excluded from the quorum by the 1.9a code review.) [Source: LockstepManager.cs:355-366]

**Existing assets to reuse / verify (NOT rebuild):**
- **F9 desync inducer works over a real network.** `MainScene.cs:441-457`, `#if DEBUG`, gated on `_ctx.Lockstep.IsOnline`; nudges the first alive entity's `Health` (+1 raw) so the local checksum diverges. No single-machine assumption — pressing F9 on machine B diverges B's sim; the server sees no N=2 majority and broadcasts HALT to both. **This is AC4's drill.** [Source: MainScene.cs:441-457]
- **The headless self-test (extend it).** `LoopbackDesyncSelfTest.cs:1-126`, `#if DEBUG`, `godot --headless -- --loopback-test`: real `DedicatedServer` + 2 real `ENetTransport` loopback clients in one process; phases `Connecting → Agreeing → AwaitingHalt → Done`; sends `GOOD=0xA11AA11A` then a one-peer `BAD=0xDEADBEEF`; asserts both clients HALT; `Quit(pass?0:1)`. **AC2 adds a clean-PASS assertion to the Agreeing phase.** [Source: LoopbackDesyncSelfTest.cs:1-126]
- **The loopback launcher (clone + parameterize, don't break).** `godot/tools/loopback-desync-smoke.ps1` hardcodes `--autojoin "127.0.0.1:$Port"` (`:36-40`); `$Godot = C:\Godot\Godot_v4.6.3-stable_mono_win64\…exe`, `$Proj = D:\Projects\Project_Chimera\godot`, `$Port = 7777`; kills stale instances matched by `--autojoin|--server|--headless` (never the editor); `--server` windowed server; clients `--autojoin`. The **LAN** launcher is a parameterized sibling. [Source: loopback-desync-smoke.ps1]
- **Client on-screen + log evidence + lobby content-hash check.** `MainScene.cs:308` (`[Checksum] tick=… hash=0x…`), `MainScene.cs:596-597` (live readout `0x{_host.LastChecksum:X8}`), `LobbyUi.cs:280-282,366-367` (map-hash match at Ready). Verify visible; do not rebuild. [Source: MainScene.cs:308,596-597; LobbyUi.cs:280-282,366-367]

**Scenarios (data already exists):**
- `godot/resources/data/scenarios/map_02_iron_crossing.json` (symmetric 2-player; recommended canonical P2.4), `alpha_map_01.json` (primary test scenario), `map_03…map_12`. **No authoring required.** [Source: resources/data/scenarios/]

**Tier-1 project:** `godot/ProjectChimera.Sim.Tests` (xUnit, Godot-free, `net8.0`); **183 passing after 1.9a**; `Server/` folder exists (1.9a) with `ServerHostTests.cs`, `ServerChecksumCollectorTests.cs`, `ServerBootstrapDeterminismTests.cs`, `ServerPacketTests.cs`. Add the AC1 observability asserts here. [Source: 1-9a File List; ProjectChimera.Sim.Tests.csproj]

### Scope fence — do NOT, in this story

- **Do NOT** attempt to satisfy AC4 yourself or claim the two-machine run happened — it is **Alec's human gate** (D1). Deliver AC1–3 + AC5, green, and the runbook; then surface the gate for Alec.
- **Do NOT** add `using Godot`/`GD.*` to `ServerHost`/`ServerChecksumCollector` — log via the injected `ILogSink` (D2/Trap #2).
- **Do NOT** harden the Ready handshake (multi-hash, `PROTOCOL_VERSION`, `hash==0` reject) — **Epic 9 / Story 9-4** (Trap #3/D8).
- **Do NOT** pull Epic 9 forward: no Nakama matchmaking (9-6), no server re-sim vote / `TickCommandsMerged` (9-3a), no disconnect freeze/continue policy (9-5), no N>2 / 8-player (9-2/9-3a), no server-dictated adaptive delay (9-4) (Trap #5).
- **Do NOT** do the cross-platform Windows↔Linux gate — **Story 1.10c** (AR-37) (Trap #6).
- **Do NOT** build a listen-server / host-and-play mode (D3/Trap #7).
- **Do NOT** build an export pipeline / `export_presets.cfg` / shipped builds — **Epic 10** (10-7/10-9a); 1.9b runs from source (D7).
- **Do NOT** change `SimChecksum`, the 60-tick interval, or the 32-bit wire (Trap #8/AC5).
- **Do NOT** turn the self-test into a two-real-sim harness — synthetic checksums are correct for the network-path proof (D6/Trap #9).
- **Do NOT** move/re-record the four existing goldens — additive-only ⇒ byte-identical (AC5/Trap #4).
- **Do NOT** break `loopback-desync-smoke.ps1` or the 1.9a `LoopbackDesyncSelfTest` HALT drill — extend, don't replace.

---

## Tasks / Subtasks

- [x] **Task 1 — Server determinism PASS/FAIL verdict + observability (AC: 1, 5)**
  - [x] `ServerHost.cs`: add an `ILogSink log` ctor param (place it after `expectedPeerCount`, before the transport seams; null-check like the seams); add `WindowsCompared`/`DesyncCount` (private set) + `bool Passing => DesyncCount == 0`. In `OnChecksum`, split the `HasMajority` branch into clean-window (`Minority.Count == 0` ⇒ `WindowsCompared++` + `_log.Info` per D2) vs minority (`DesyncCount++` + the existing `DesyncAlert` loop + a `_log.Warn`); in the no-majority branch `DesyncCount++` + the existing `Halt` + a `_log.Warn`. Add `LogSummary()` per D2. **Behavior (alerts/HALT) unchanged.**
  - [x] `DedicatedServer.cs`: pass a `GodotLogSink` into the `ServerHost` it builds in `HandleReady` (alongside the existing transport lambdas). Reuse the existing `GodotLogSink` — construct `new GodotLogSink()` in `DedicatedServer`, or (mirroring how 1.9a already injects `SimHost = serverSimHost`) have `MainScene` pass its `_logSink` (`MainScene.cs:34`) into `DedicatedServer` as a stored field for the later `HandleReady` construction. Add a `public ServerHost? Host => _serverHost;` accessor (for Task 2). Call `_serverHost?.LogSummary()` on the server's match-end / `HandleDisconnect` / `_ExitTree` path so the verdict prints to the server console.
  - [x] Tier-1 `Server/ServerHostObservabilityTests.cs` (new, or extend `ServerHostTests.cs`): inject a **capturing** `ILogSink`; a clean N=2/N=3 run increments `WindowsCompared` only, `DesyncCount==0`, `Passing==true`, and `LogSummary()` emits the exact `… N windows compared, 0 desync — PASS.` string; a minority/no-majority window increments `DesyncCount` and flips the summary to `FAIL`; per-clean-window `Info` text matches the D2 format.
  - [x] Fix the **`ServerHost` ctor call sites** for the new `ILogSink` param: the production site is `DedicatedServer.HandleReady`; the test sites are in the existing `Server/ServerHostTests.cs` (`new ServerHost(...)` — pass a capturing or `NullLogSink`). The `LoopbackDesyncSelfTest` does **not** construct `ServerHost` (it goes through the real `DedicatedServer`), so it needs no ctor change — only the `DedicatedServer.Host` accessor from Task 2. `dotnet build godot/godot.csproj` → green.

- [x] **Task 2 — Extend the headless self-test with a clean-PASS assertion (AC: 2)**
  - [x] `LoopbackDesyncSelfTest.cs`: in the `Agreeing` phase, run a clean run of **≥5 comparison windows** of matching `GOOD` checksums and assert the server reports **PASS** (expose the server's `WindowsCompared`/`Passing` via a `DedicatedServer` accessor, or assert via a captured summary) with **zero halt** BEFORE injecting the divergence; keep the existing divergence→both-HALT drill; `Quit(0)` **only if both** the clean-PASS and the HALT drill pass. Update the `RESULT:` line to report the clean window count.
  - [x] Run `godot --headless -- --loopback-test` → **exit 0 = PASS** (clean PASS + HALT drill). Capture the console transcript for the Debug Log.

- [x] **Task 3 — LAN launcher + per-machine on-screen evidence verification (AC: 3)**
  - [x] New `godot/tools/lan-desync-smoke.ps1`: params `-ServerIp <ip>`, `-Port 7777`, `-Role server|client`; `server` ⇒ start `--server --port`; `client` ⇒ start `--autojoin <ServerIp>:<Port>`. Reuse the safe stale-process cleanup (match `--autojoin|--server|--headless`, never the editor). Print clear "run this on machine A / machine B" guidance. **Do not modify** `loopback-desync-smoke.ps1`.
  - [x] Verify (in-engine, online match) the client checksum readout (`MainScene.cs:596-597`) is visible and the per-window `[Checksum]` log (`MainScene.cs:308`) prints; harden only if hidden (e.g., ensure the readout shows during an online match, not just offline). Confirm the lobby map-hash match line shows (`LobbyUi.cs:366-367`). — VERIFIED by inspection: the checksum hash is on the always-visible HUD line 1 (`MainScene.cs:613-614`, `Hash 0x........  ONLINE`); no hardening needed.

- [x] **Task 4 — Two-machine LAN determinism runbook (AC: 3, 4)**
  - [x] New `godot/tools/lan-determinism-runbook.md`: **Prerequisites** (Godot 4.6.3 mono + repo on both machines; same LAN; Windows Firewall allow inbound UDP 7777 on the server machine). **Topology** (D3 — server + client 1 on machine A, client 2 on machine B; find machine A's LAN IP via `ipconfig`). **Canonical scenario** (D4). **Steps** (start `--server` on A; launch client 1 on A → join `127.0.0.1`/A's IP; launch client 2 on B → join A's IP; both confirm matching map hash in the lobby; Ready; play ≥300 ticks — a multi-minute match). **Read the verdict** (server console `MATCH SUMMARY: … — PASS`; both client readouts match every window). **F9 drill** (press F9 on one client → both show terminal "MATCH HALTED"). **Watch-item** (adaptive delay 4→2 must not desync — D9). **Troubleshooting** (firewall, can't connect, map-hash mismatch, stale process). **Explicit PASS/FAIL criteria.**

- [x] **Task 5 — Pin + verify the canonical P2.4 scenario (AC: 1, 4)**
  - [x] Pin `map_02_iron_crossing.json` (D4) as the canonical P2.4 scenario in the runbook. Verify it loads and plays ≥300 ticks deterministically. If it is **not** already covered by an existing golden/determinism test, add a Tier-1 `Server/` (or `Golden/`) determinism check that runs THIS scenario through a `ServerBootstrap`-built host for ≥300 ticks (reuse the `GoldenChecksumReplay` / `ServerBootstrapDeterminismTests` harness) and asserts a stable checksum sequence across two in-process runs. If it **is** covered, cite the test and skip.

- [x] **Task 6 — Prove AC5 (goldens byte-identical, Godot-free, suite green) (AC: 5)**
  - [x] `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` → ALL green (183 prior + the new observability/scenario tests), with **all four** existing `*.golden.txt` UNCHANGED. Confirm `ServerHost`/`ServerChecksumCollector` still have zero `using Godot`/`GD.`; `GodotFreeBoundaryTest` passes. No signature change to `SimChecksum.Compute`/`ISimSystem`/any `Tick`/`Apply`; the 60-tick interval and 32-bit wire are unchanged.

- [ ] **Task 7 — Physical two-machine LAN gate (AC: 4) — ALEC (human gate, NOT the dev agent; PARKED pending a second machine)**
  - [ ] **PARKED as of 2026-06-24 (Resolved Decision #1 — Alec has one machine).** After Tasks 1–6 are done and green, surface the runbook to Alec; the story moves to `review` **without** this box checked. When a second LAN machine is available, Alec runs the two-machine LAN match per `lan-determinism-runbook.md`: full match ≥300 ticks → server `MATCH SUMMARY: … 0 desync — PASS`, both client readouts match every window, adaptive delay adapts without desync; then the F9 drill → both machines show terminal HALT. **Record the outcome in the Change Log** when performed. _(The dev agent does NOT check this box; Alec's confirmation does — exactly as 1.9a's live run was logged.)_

---

## Dev Notes

### Task 1 — `ServerHost` observability (full intent; implement per D2)
The change is **purely additive** to `ServerHost.cs:44-63`. The current code has three outcomes after `v.Complete`: majority-with-minority (alert), majority-clean (currently a no-op), no-majority (halt). Add the **clean-window counter + Info log** to the currently-empty success path, add `DesyncCount++` to the two failure paths, and add the two public counters + `Passing` + `LogSummary()`. The ctor gains `ILogSink log` (null-checked). **Do not** change the collector, the packet builders, the transport seams, or the spectator handling. `GodotLogSink` already exists and routes `Info`→`GD.Print` (`MainScene.cs:34` constructs one); `DedicatedServer` constructs its own `new GodotLogSink()` for the `ServerHost`.

> Per-window `Info` every 2 s is the **intended evidence trail**, not noise — a multi-minute match produces a readable column of `… window #k` lines ending in the `MATCH SUMMARY` verdict. Keep it `Info` (not `Warn`).

### Task 2 — self-test clean-PASS phase (extend, do not replace)
The existing `Agreeing` phase (`LoopbackDesyncSelfTest.cs:84-100`) already sends `GOOD` every 50 ms; it just doesn't **assert** a server PASS before diverging. Add: drive ≥5 windows of `GOOD` from both peers, then assert the server reached `Passing == true` with `WindowsCompared >= 5` and `Halted == false` (expose these via a `DedicatedServer` getter, e.g. `public ServerHost? Host => _serverHost;`), THEN run the existing divergence into `AwaitingHalt`. `Finish(false, …)` on any failed assertion. Keep `PORT=49777`, the phase machine, and the `Quit` semantics.

### Task 3/4 — LAN launcher + runbook shape
The LAN launcher is a thin parameterized clone of `loopback-desync-smoke.ps1` — same Godot path / cleanup, but `--autojoin "$ServerIp:$Port"` and a `-Role` switch so the **same script** runs on both machines (server role on A, client role on A and B). The runbook is the human-facing companion (D3/D4/D5/D8/D9). Both live in `godot/tools/` next to the loopback assets.

### Why the physical run can't be fully replaced by loopback (state this honestly in Completion Notes)
Two processes on **one** machine share locale/region/culture and the same CPU, so loopback **cannot** catch machine-specific nondeterminism (a stray `CultureInfo`-sensitive parse, a region-dependent path, any float/hardware leak). The 1.3b "locale fix" and the 1.10c cross-platform gate exist precisely because these are real. The loopback self-test (AC2) proves the **network/verdict/HALT path**; only AC4's **two real machines** proves the sim agrees across **different** Windows installs. That residual is exactly the #1 ship risk FR-39 names — which is why AC4 is a mandatory human gate, not optional polish.

### Project Structure Notes
- **NEW:** `godot/tools/lan-desync-smoke.ps1`, `godot/tools/lan-determinism-runbook.md`; Tier-1 `Server/ServerHostObservabilityTests.cs` (+ optionally a `map_02` determinism test).
- **MODIFIED:** `godot/src/Multiplayer/Server/ServerHost.cs` (ctor + counters + logs + `LogSummary`), `godot/src/Multiplayer/DedicatedServer.cs` (inject `GodotLogSink`, call `LogSummary`, expose `Host` for the self-test), `godot/src/Multiplayer/LoopbackDesyncSelfTest.cs` (clean-PASS phase), and the existing `Server/ServerHostTests.cs` (ctor signature). Possibly `godot/src/Core/MainScene.cs` only if the client checksum readout needs hardening to show online (verify first).
- **UNCHANGED (must stay so):** all four `*.golden.txt`; `SimChecksum.cs`; the 60-tick interval; `ServerChecksumCollector.cs` (consumed, not modified); the packet builders + wire width; the 30 Hz tick / `ISimSystem` / 9-system order; the Ready handshake; `ENetTransport`/`ServerTransport`/`LobbyUi` join paths (already LAN-ready).
- **NOT here:** two-machine harness *automation* (impossible for an LLM — AC4 is human), handshake hardening, Nakama, server-vote, disconnect policy, N>2, cross-platform, export presets — Epic 9 / 1.10c / Epic 10.

### Project Context Rules
_Extracted from `_bmad-output/project-context.md` + `game-architecture.md` — these govern every edit here:_
- **Simulation/Presentation boundary is sacred.** `ServerHost`/`ServerChecksumCollector` are Godot-free; observability is added via the Godot-free `ILogSink`. `DedicatedServer`/`MainScene` are the presentation/Godot edge that inject `GodotLogSink` and read the console. No `using Godot` in the authority core. [Source: project-context.md "The One Architectural Rule"]
- **Peer agreement is server-enforced over the whole model.** This story completes the server-authority story by making the server's majority-vote verdict **legible** (a PASS/FAIL summary), discharging "a trusted server computes/attests agreement." [Source: project-context.md "Forward Architecture Rules"; game-architecture.md SD-5]
- **Determinism: `Fixed`, ascending order, no wall-clock.** No new sim math — observability counts the existing 32-bit `SimChecksum` verdicts. No `float`/`System.Random`/`DateTime` enters any new code; the new counters are plain `int`. [Source: project-context.md "Determinism"]
- **Reuse, don't fork.** Reuse `ServerHost`/`ServerChecksumCollector`/`ILogSink`/`GodotLogSink`/`LoopbackDesyncSelfTest`/`ENetTransport`/`LobbyUi`/the existing scenarios — add to them, don't parallel them. [Source: project-context.md "Data layout / reuse"]
- **Headless detection** via `DisplayServer.GetName() == "headless"`; the dedicated server renders no UI (UX-DR49) and prints its verdict to stdout. [Source: project-context.md "Godot C# gotchas"]
- **Engine/runtime:** Godot 4.6.3, .NET 8 (`net8.0`); `ProjectChimera.*`; `godot.csproj`/`godot.sln`; Tier-1 `ProjectChimera.Sim.Tests` (xUnit, Godot-free). [Source: project-context.md "Technology Stack"]

### References
- [Source: epics.md (lines 706-720)] — Story 1.9b statement + the 2 epic ACs (server-collected slot-tagged checksums stay in lockstep every window with ZERO desync, P2.4 LAN test passes, UX-DR84; and a real desync fires the 1.9a DesyncAlert/HALT path with the clear message, verified end-to-end). Covers FR-39, UX-DR84. Depends on 1.9a. "Requires two physical machines."
- [Source: prd.md:282] — **FR-39**: two players complete a full MP match on **separate machines (LAN)** with checksums in sync for **300+ ticks and zero desync**; the **P2.4 LAN determinism test must pass** (never run; #1 risk). [Source: epics.md:155] — HARD GATE: zero desyncs in ≥95% of MP matches; FR-39 passes (non-negotiable, §7). [Source: epics.md:415] — FR-39 coverage row.
- [Source: epics.md:357; EXPERIENCE.md:135] — **UX-DR84** LAN lockstep journey (two friends; host picks scenario; lobby shows version-hash match + "all content synced"; both ready → 300+ ticks in lockstep, zero desync).
- [Source: 1-9a story (DONE)] — the predecessor: `ServerBootstrap`/`ServerHost`/`ServerChecksumCollector`, `DesyncAlert`/`Halt` packets, client HALT handler + terminal overlay, the AR-40 tie-break pin, the loopback self-test + smoke launcher, and the **root-cause ENet packet-drop fix** (`peer.GetPacket()`), on which real MP delivery — and therefore 1.9b — depends.
- [Source: ServerHost.cs:20-64] — the silent-on-success `OnChecksum` (the AC1 gap) + `Halted`/`ExpectedPeerCount`. [Source: ServerChecksumCollector.cs:37-58] — the `Verdict` fields. [Source: SimulationLoop.cs:33,97-102] — the 60-tick interval + `OnChecksum` fire. [Source: LockstepManager.cs:355-366] — `SendChecksum`. [Source: DedicatedServer.cs:39,88-89] — `DEFAULT_PORT`, server console logging.
- [Source: ENetTransport.cs:71-90; LobbyUi.cs:50-56,188-205,473-481,280-282,366-367; ServerTransport.cs:56; MainScene.cs:204-235,308,441-457,596-597] — the LAN-ready join path, server bind, headless/`--server` branch, F9 inducer, client checksum readout + per-window log, lobby content-hash check.
- [Source: LoopbackDesyncSelfTest.cs:1-126; loopback-desync-smoke.ps1] — the self-test (extend for AC2) + the loopback launcher (clone+parameterize for AC3). [Source: resources/data/scenarios/map_02_iron_crossing.json, alpha_map_01.json] — existing 2-player scenarios (no authoring). [Source: Snapshot.md:56-60] — the pre-existing LAN smoke checklist (RTT, adaptive delay 4→2, play 300+ ticks, same hash both machines).
- [Source: game-architecture.md (AR-37 / Story 1.10c)] — cross-platform Windows↔Linux determinism gate is a **separate** story; 1.9b is same-platform.

---

## Dev Agent Record

### Agent Model Used

Claude Opus 4.8 (`claude-opus-4-8`) — gds-dev-story workflow.

### Debug Log References

- Full Tier-1 suite: **191 passing, 0 failed** (`dotnet test ProjectChimera.Sim.Tests`) = 183 prior + 5 `ServerHostObservabilityTests` + 3 `CanonicalScenarioTests`. Filters: `~Server` → 37; `~CanonicalScenario` → 3.
- `dotnet build godot/godot.csproj` → **0 errors** (7 pre-existing CS8632 warnings, out of scope).
- **Headless self-test** `godot --headless -- --loopback-test` → **exit 0 = PASS**. Transcript (key lines): `[Determinism] tick 1..5: all 2 peers matched 0xA11AA11A (window #1..5)` → `clean phase OK — server reports 5 windows compared, 0 desync (PASS)` → `divergence injected at tick 6` → `[Determinism] tick 6: GLOBAL DESYNC — no canonical hash. Broadcasting terminal HALT.` → `RESULT: PASS — clean PASS (5 windows, 0 desync) + both clients HALTed after divergence` → `MATCH SUMMARY: 5 windows compared, 1 desync — FAIL.` (the FAIL summary is correct — the self-test deliberately induces a desync; the TEST passes because it proved both the clean-PASS and the HALT paths).
- Goldens: `git status --short -- '*.golden.txt'` → **empty** (four existing goldens byte-identical).
- `lan-desync-smoke.ps1` → PowerShell parser: PARSE OK, no syntax errors.

### Completion Notes List

- **AC1 (server determinism verdict)** — `ServerHost` gained an injected `ILogSink` + `WindowsCompared`/`DesyncCount`/`Passing` + a per-clean-window `Info` line + `LogSummary()`; `OnChecksum` now splits the completed-tick verdict into clean (all agree → tally + log), minority (alert + count), and no-majority (HALT + count). `DedicatedServer` injects a `GodotLogSink` (new `Log` init-prop, default `NullLogSink`), exposes `Host`, and calls `LogSummary()` on player disconnect + `_ExitTree`. The 1.9a silent-on-success gap is closed — the server now prints the FR-39 PASS/FAIL verdict to its console. Unit-tested (`ServerHostObservabilityTests`): clean N=2/N=3 counting + PASS summary; no-majority → FAIL + `Halted`; minority@N=3 → FAIL + no halt; exact per-window + summary text. **Alert/HALT behavior unchanged.**
- **AC2 (automated end-to-end proxy)** — `LoopbackDesyncSelfTest` extended: the `Agreeing` phase now waits for the server to tally **≥5 clean windows** and asserts `Host.Passing && !Host.Halted` BEFORE inducing the divergence; exit 0 only if **both** the clean-PASS and the existing both-HALT drill pass. Verified live headless (exit 0).
- **AC3 (LAN launcher + on-screen evidence)** — `lan-desync-smoke.ps1` (`-Role server|client`, `-ServerIp`, `-Port`, `-CleanFirst`) parameterizes the server IP; confirmed `--autojoin <ip:port>` honors a remote IP (`MainScene.cs:375-381` → `AutoJoinDedicated(ip,port)`), so a client can reach a LAN server. Client checksum readout verified visible online by inspection (`MainScene.cs:613-614`, the always-on HUD line `Hash 0x........  ONLINE`) — no hardening needed. `loopback-desync-smoke.ps1` untouched.
- **AC4 (physical two-machine gate) — PARKED, HUMAN.** Alec has one machine as of 2026-06-24 (Resolved Decision #1), so the physical run is deferred to a second LAN machine. `lan-determinism-runbook.md` makes it push-button (topology, scenario, steps, verdict-reading, F9 drill, adaptive-delay watch-item, troubleshooting, explicit PASS/FAIL). **Task 7 is intentionally left unchecked** — the dev agent does not (cannot) satisfy AC4; Alec checks it and logs the run when performed. The story reaching `review` on AC1–3 + AC5 is the designed split, not incomplete work.
- **AC5 (no regression)** — additive only: **191 Tier-1 green** (incl. `GodotFreeBoundaryTest` ⇒ `ServerHost`/`ServerChecksumCollector` still zero `using Godot`/`GD.`); **four existing goldens byte-identical** (`git` clean); no change to `SimChecksum`, the 60-tick `ChecksumInterval`, the 32-bit wire, the 30 Hz tick, or any sim signature.
- **Scenario discovery (informs the runbook + Open Question follow-up).** The match scenario is the **`MainScene.ScenarioPath` export** (default `alpha_map_01.json`), **not** a lobby picker. `CanonicalScenarioTests` proves both `map_02_iron_crossing` (pinned canonical P2.4) and `alpha_map_01` load + validate, and map_02 runs **deterministically** through `ServerBootstrap` (two 300-tick runs identical). The runbook documents setting `ScenarioPath` to map_02 on **both** machines, the zero-config `alpha_map_01` fallback, and the lobby hash-gate that **blocks** a scenario mismatch (the determinism safety net).

### Change Log

- 2026-06-24 — **Code review (`gds-code-review`, 3-layer adversarial: Blind Hunter + Edge Case Hunter + Acceptance Auditor) — verdict PASS.** 12 findings → 2 decisions (both resolved by Alec → patches), 6 patches applied + verified, 1 deferred, 5 dismissed. **Headline fix (P1, HIGH): the production dedicated server was SILENT** — `MainScene.cs:227` constructed `DedicatedServer` without wiring `Log`, so every AC1 `[Determinism]` window line + `MATCH SUMMARY` was dropped on the real `--server`/`--headless` path; the loopback self-test passed only because it injects its own sink. Fixed: `Log = _logSink`. Also — P2: `_summaryLogged`/`EmitSummaryOnce` guard against a duplicate MATCH SUMMARY (disconnect + `_ExitTree`); **P5 (Alec's decision): `WindowsCompared` now counts ALL completed windows (clean + desync)** so the summary reports the true total — verified live (self-test: `MATCH SUMMARY: 6 windows compared, 1 desync`, was 5); **P6 (Alec's decision): `LogSummary` emits `INCONCLUSIVE` at 0 windows** so a no-data match no longer reads as PASS; P3: runbook lobby-block claim qualified (the scenario-hash compare is fail-open on `hash==0`); P4: "three"→"four" goldens. **Verification:** game build 0 errors, **Tier-1 192 green** (was 191; +1 INCONCLUSIVE test), all 4 `*.golden.txt` byte-identical, headless `--loopback-test` exit 0. AC4 physical two-machine gate remains PARKED. Status `review` → `done`.
- 2026-06-24 — Story 1.9b implemented (Tasks 1–6 complete; Task 7 = the physical two-machine LAN gate is **PARKED pending a second machine**, Alec's to run + log). AC1 server determinism PASS/FAIL verdict (`ServerHost` `ILogSink` + counters + per-window log + `MATCH SUMMARY`); AC2 extended `LoopbackDesyncSelfTest` clean-PASS assertion (headless `--loopback-test` exit 0 = PASS); AC3 `lan-desync-smoke.ps1` LAN launcher + `lan-determinism-runbook.md` + verified client HUD readout; AC5 additive — Tier-1 **191 green**, four existing goldens byte-identical, `ServerHost`/collector Godot-free. Canonical P2.4 scenario pinned (`map_02_iron_crossing`) + verified valid/deterministic; surfaced that the scenario is the `ScenarioPath` export (default `alpha_map_01`). `baseline_commit` `38e0b61`. Status → review.

### File List

**NEW — tests:**
- `godot/ProjectChimera.Sim.Tests/Server/ServerHostObservabilityTests.cs`
- `godot/ProjectChimera.Sim.Tests/Server/CanonicalScenarioTests.cs`

**NEW — tooling + docs:**
- `godot/tools/lan-desync-smoke.ps1`
- `godot/tools/lan-determinism-runbook.md`

**MODIFIED — production:**
- `godot/src/Multiplayer/Server/ServerHost.cs` — injected `ILogSink`; `WindowsCompared`/`DesyncCount`/`Passing`; per-clean-window `Info`; `LogSummary()`; `OnChecksum` split clean/minority/no-majority (behavior unchanged).
- `godot/src/Multiplayer/DedicatedServer.cs` — `Log` init-prop (default `NullLogSink`); `Host` read-only accessor; construct `ServerHost` with the sink; `LogSummary()` on disconnect + `_ExitTree`.
- `godot/src/Multiplayer/LoopbackDesyncSelfTest.cs` — clean-PASS phase (asserts ≥5 windows + `Host.Passing` before diverging); injects a `GodotLogSink` so the determinism verdict prints; reads `DedicatedServer.Host`.

**MODIFIED — tests:**
- `godot/ProjectChimera.Sim.Tests/Server/ServerHostTests.cs` — new `ServerHost` ctor (`ILogSink` arg) in `Make` + a null-log throw assertion.

**MODIFIED — artifacts:**
- `_bmad-output/implementation-artifacts/sprint-status.yaml` — 1.9b `backlog` → `ready-for-dev` → `in-progress` → `review`.

---

### Review Findings

_Code review (`gds-code-review`, 3-layer adversarial: Blind Hunter + Edge Case Hunter + Acceptance Auditor, all Opus 4.8) — 2026-06-24. Diff `38e0b61..7a20356`. 12 raw findings → **2 decisions, 4 patches, 1 deferred, 5 dismissed**. Headline: the loopback self-test is green but it MASKED a production gap (P1) — the real server never prints the verdict._

**Decisions (RESOLVED by Alec 2026-06-24 → both converted to patches P5/P6):**

- [x] [Review][Decision→Patch P5] **`WindowsCompared` / MATCH SUMMARY counts CLEAN windows only** — on a FAIL run the summary under-reports total windows (e.g. `1 windows compared, 1 desync` for 2 actually-completed windows). [`ServerHost.cs:77,104`] (blind+auditor). **RESOLVED: option (b) — add a total-windows counter.** `WindowsCompared` now counts ALL completed windows (clean + desync) so the summary reports the true total; the literal AC1 string format is unchanged (only the semantics of the count), but the 2 affected tests + the XML doc are updated. (Also corrects the field's own misnomer.) → see Patch P5.
- [x] [Review][Decision→Patch P6] **Vacuous PASS at zero windows** — a server that ran but never completed a comparison window logs `0 windows compared, 0 desync — PASS`. [`ServerHost.cs` `Passing`/`LogSummary`] (blind). **RESOLVED: option (a) — emit `INCONCLUSIVE` when `WindowsCompared==0`** (accepts a third verdict word beyond AC1's "PASS|FAIL"); clean-PASS / FAIL strings unchanged; add a Tier-1 test for the no-data case. → see Patch P6.

**Patches:**

- [x] [Review][Patch] **[HIGH — production server is silent]** `MainScene` builds the dedicated server WITHOUT wiring `Log`, so the real `--server` / `--headless` path defaults to `NullLogSink` and DROPS every AC1 verdict line (per-window `[Determinism]` + `MATCH SUMMARY`). The loopback self-test passes only because IT injects its own `GodotLogSink`; the runbook tells Alec to read a console that prints nothing. [`MainScene.cs:227`] (edge) — fix: `new DedicatedServer { SimHost = serverSimHost, Log = _logSink }`.
- [x] [Review][Patch] **[MEDIUM]** Duplicate `MATCH SUMMARY` — `LogSummary()` fires on the disconnect path AND in `_ExitTree`, printing the terminal verdict twice on the normal end-of-match path (masked today by NullLogSink; becomes visible once the HIGH patch lands). [`DedicatedServer.cs:147,294`] (blind+edge) — fix: a `_summaryLogged` guard (or null `_serverHost` after the disconnect summary).
- [x] [Review][Patch] **[LOW — doc]** Runbook over-claims mismatch protection — "you cannot accidentally start a mismatched game" holds only when BOTH peers publish a valid non-zero scenario hash; the lobby compare is fail-open when either hash is 0 (a fail-closed-rejected scenario publishes hash 0). [`lan-determinism-runbook.md` §3, §8] (auditor) — fix: qualify the claim (blocks when both sides have a valid non-zero hash).
- [x] [Review][Patch] **[NIT — doc]** Prose says "all three existing `*.golden.txt`" but FOUR goldens are embedded (`same-tick-tie-break` was added in 1.9a). All four are byte-identical (substantive AC5 MET); only the count is stale. [AC5 / Scope Fence / Completion Notes] (auditor) — fix: "three" → "four".
- [x] [Review][Patch P5] **[from Decision 1]** Make `WindowsCompared` count ALL completed windows (clean + desync) — increment it unconditionally after the `v.Complete` gate; `DesyncCount` still counts only diverged windows; clean = `WindowsCompared − DesyncCount`. Summary string format unchanged; update XML docs + the `NoMajority_N2` and `Minority_N3` test assertions to the new totals. [`ServerHost.cs`; `ServerHostObservabilityTests.cs`] (resolved decision).
- [x] [Review][Patch P6] **[from Decision 2]** `LogSummary()` emits `INCONCLUSIVE` (not PASS) when `WindowsCompared == 0`; PASS/FAIL paths unchanged. Add a Tier-1 test asserting the no-data summary ends `INCONCLUSIVE.`. [`ServerHost.cs`; `ServerHostObservabilityTests.cs`] (resolved decision).

**Deferred** (→ `deferred-work.md`):

- [x] [Review][Defer] LAN launcher's `--server`/`--autojoin`/F9 are `#if DEBUG`; against a future exported (non-DEBUG) build the `-Role server` path silently boots a normal client, not a server. Currently consistent (1.9b runs from source = DEBUG), but a latent trap once exports exist. [`lan-desync-smoke.ps1`] (edge) — deferred, surfaced by this change but only actionable at Epic 10 / Story 10-7.

**Dismissed (5):** the N≥3 clean-window condition, the `Halted` early-return, no-double-count, `Minority` non-null, and `_tick` advancing to 5 real windows — all VERIFIED correct by the Edge Case Hunter against the live code; "1 windows" grammar (AC1-pinned exact format); minority `Warn`-string drift from the D2 sample (cosmetic, non-asserted, AC1 pins only the clean+summary strings); loopback clean-phase "timeout-vs-desync" diagnostic (the synthetic-GOOD clean phase cannot desync, so the trigger can't occur); observability tests not asserting alert/HALT dispatch (already covered by `ServerHostTests`); test nullable `!` suppressions (no runtime defect).

---

## Resolved Decisions (Alec, 2026-06-24 — these are SETTLED; do not re-ask)

1. **Two-machine availability → ONE machine currently.** Alec has **one** Windows machine as of 2026-06-24. **AC4 (the physical two-machine gate) is therefore PARKED pending a second LAN machine** — it is **not** a blocker and **not** a failure. The dev agent delivers **AC1–3 + AC5** (server verdict, loopback self-test over real sockets, LAN launcher + runbook, no-regression) — all machine-verifiable on one box — and the story reaches **`review`** on those. The runbook is written and ready so AC4 is push-button the moment a second machine exists; Alec runs it then and records the result in the Change Log. **Do NOT block the build on the hardware** (D1).
2. **Topology = server + client 1 on machine A, client 2 on machine B** (D3 default confirmed). Two machines; the server co-locates with client 1 on A (it arbitrates, it doesn't tick a match), so the proof is still two independent sims on two physical machines.
3. **Canonical P2.4 scenario = `map_02_iron_crossing.json`** (D4 default confirmed). Symmetric 2-player; reuse existing data, no authoring.
4. **Run-from-source (`godot --path`) on both machines** (D7 default confirmed). No export preset; the export pipeline stays Epic 10 (10-7/10-9a).
