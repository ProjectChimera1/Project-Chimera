---
status: blocked
---

# BMad Dev Auto Result — Story 15.1 (MP reconnect v1, FR-79 / DW-2)

Status: blocked
Blocking condition: Story cannot be responsibly implemented or verified in an unattended single-session run. (1) The story's design premise is false in a load-bearing way — there is no server-side full-match command log to "re-download", so v1 requires *building* that buffer, materially expanding scope. (2) Both verification gates — the AC's "2-player LAN rejoin + post-catch-up checksum agreement" and the binding In-Engine Gate (the diff must touch coupled surfaces) — cannot be satisfied by the single-client godot-mcp bridge unattended, and no automated two-peer/loopback *reconnect* harness exists yet. This needs a human scoping/verification decision before an unattended build is worthwhile.

## Auto Run Result

Status: blocked
Blocking condition: see above.

**Change:** None. Halted in step-02 (planning) before writing a spec or touching code. No files other than this result artifact were created or modified; the working tree is otherwise clean on `master`.

**Files changed:**
- `_bmad-output/implementation-artifacts/bmad-dev-auto-result-15-1-...md` — this HALT artifact (the run's handback record).

**Verification:** N/A — no implementation was attempted. The two blocking findings below were confirmed directly against the codebase.

---

## Why this halted (evidence)

Three synchronous exploration passes over the MP layer produced a complete picture. Two findings make an unattended `done` unreachable and change the story's true cost. Both are code-confirmed, not inferred.

### Finding A — the FR-79 / DW-2 design premise is false: there is no full-match server command log

The scoped design (epics.md §15.1, DW-2, `gap-analysis` line 149) rests on: *"The Epic-9 stateful server buffers the whole command stream; a rejoining client re-downloads it, fast-forward-simulates to the live tick, then resumes."*

The server does **not** buffer the whole stream:
- `godot/src/Multiplayer/Server/MergedTickBuilder.cs:28` — `RING = 64`. The authoritative fan-in is a **64-tick ring**; resolved ticks are discarded (`_resolvedThrough`, `:56`; anything `<= _resolvedThrough` or `> _resolvedThrough + ACCEPT_WINDOW(32)` is rejected, `:127-130`). It cannot replay from tick 0.
- `godot/src/Multiplayer/Server/ServerChecksumCollector.cs` — an 8-tick window, and it **permanently excludes a dropped slot** (`DropExpectedReporter`, `:254-259`, `_excluded[]`). `ServerHost.cs:209` marks this as the exact spot that must "re-admit instead of drop" *"if a DesyncAlert ever becomes recoverable (rejoin/resync)."*
- The only tick-0-onward command log that exists is the **client-side** `ReplayRecorder` `.chmr` file — not something the server holds.
- Rejoin is explicitly deferred with no wire support: `ServerHost.cs:22` ("HALT is TERMINAL … recovery/rejoin policy is deferred"), `NetworkCommand.cs:744` ("recovery/rejoin is deferred post-1.0"). `PacketType` (`NetworkCommand.cs:14-115`) has **no** rejoin/resync/command-log message type.

**Consequence:** v1 is not "reuse the buffer + replay." It requires building a new persistent per-match server command-log subsystem (model it on `ReplayRecorder`'s merged-frame stream, fed from `MergedTickBuilder` emissions), new `PacketType`s (+`PROTOCOL_VERSION` bump 2→3), a real `_state==InGame` branch in `HandleConnect` (today unguarded — it flips a live match back to Lobby and silences the command relay; this is the DW-599 race, `DedicatedServer.cs:289-326`), checksum-quorum re-admission, and a batch of match-lifecycle field resets (`_latestSeenTick`, `_ready[]`/`_readyHash[]`, the merged-arrival ring — DW-598/DW-600). This is multi-subsystem work, not a wiring pass.

### Finding B — neither verification gate is runnable unattended

- **AC gate:** epics.md §15.1 — *"2-player LAN rejoin mid-match with post-catch-up checksum agreement; a failed content gate rejects the rejoin without disturbing the live match."* This is inherently a **two-peer** assertion. The project has **no automated two-peer/loopback *reconnect* harness** (a `LoopbackPeerSim` exists for lobby/desync self-tests, but nothing exercises download-log → fast-forward → checksum-agrees; the DW-447 headless N-sim desync test that would enable it was 15-15 scope). Building that harness is itself prerequisite work.
- **In-Engine Gate (binding):** the feature must wire the catch-up loop into `MainScene.cs` (the fast-forward precedent is `MainScene.cs:1387-1401`), drive `godot/src/UI/LoadingScreenOverlay` for catch-up progress, and touch `Core/Bootstrap/MatchLifecycleController`. All are coupled surfaces, so `tools/verify-in-engine-gate.ps1` will require an in-engine artifact. A two-peer reconnect cannot be observed through the **single-client** godot-mcp bridge in one unattended session. `done` is therefore mechanically unreachable here.

(Note: `LobbyUi.cs`, where the "Rejoining…" status string would live, is under `godot/src/Multiplayer/` — *not* itself a coupled surface — but the `MainScene`/`UI`/`Bootstrap` wiring above still trips the gate.)

## What the investigation *did* establish (carry into the next attempt)

The building blocks are sound and reusable — the work is real but tractable once scoped:
- **Catch-up engine exists:** `ReplayPlayer.Flush(tick)` + `SimulationHost.StepOnce()` in a tight budgeted loop (template: `MainScene.cs:1387-1401`) fast-forwards a command log through the *same* `OrderApplier` the live path uses. `ReplayPlayer` currently reads a `FileStream` ctor — needs an in-memory/streamed frame source (or a sibling sharing `ApplyOrders`).
- **Command-log unit = the wire unit:** `MergedTickPacket` (`NetworkCommand.cs:1296`) is byte-identical to a `.chmr` body frame. A server buffer of these keyed by tick is the downloadable log.
- **Content re-gate is ready and pure:** `MatchAgreementHash.Compute` (AlgoVersion 3, folds RulesetHash + ContentHash(9.16) + roster + StartStateHash) via `HandshakeGate.CheckStart` + `LobbyVersionGate` + `ReadyPacketRouting.Route`. A failed gate already rejects cleanly without touching the live match. The `ServerLobbyPolicy` / `DedicatedServer.cs:634-636` "Story 9.6 reconnect/re-ready path" stale-payload wipe is the intended seam.
- **Slot identity ("9.7"):** `AssignedRoster` frozen `slot→faction` snapshot (`Server/AssignedRoster.cs`); transport-authoritative slot map (`ServerTransport.FindFreeSlot`). A rejoiner must re-bind the frozen slot atomically off the injector (DW-599).
- **Fast-forward is frame-wait-free:** `SimulationLoop.StepOnce()` bypasses the wall-clock accumulator, so N ticks crunch in a tight loop. Input delay = 4 (clamped [2,12]); live exec tick trails the issue frontier by `_currentDelay`.

## Recommended resolution (for Alec — needs one decision)

1. **Correct the plan's false premise** on DW-2 / epics.md §15.1: state that the server holds only a 64-tick ring, so v1 must *build* the full-match command-log buffer. (File this as a `decision:` line on DW-2, and consider a new DW for "no automated reconnect verification harness exists.")
2. **Decompose 15.1** along the project's established Godot-free-core / presentation seam (cf. 1-8a/b/c, 2-5a/b):
   - **15.1a — reconnect core, Godot-free, Tier-1 verifiable:** server command-log buffer + new `PacketType`s + `HandleConnect` InGame branch + injector→live handoff (DW-599) + checksum re-admission + lifecycle resets + client in-memory catch-up + content re-gate. Verify with a **loopback reconnect harness** (a real deliverable, DW-447-style) asserting post-catch-up checksum agreement and clean-reject-on-bad-content. No coupled surfaces → In-Engine Gate not applicable.
   - **15.1b — in-engine wiring + "Rejoining…" UX:** `MainScene` catch-up loop, `LoadingScreenOverlay` progress, `LobbyUi` status. In-Engine-gated; run interactively or with a two-instance loopback in one editor.
3. **Or** run 15.1 whole under an **interactive** dev session (a human present for the two-machine / two-instance gate) rather than `bmad-dev-auto`.

Under the current single-entry unattended contract I cannot split into new story ids or drop an AC unilaterally, so I am halting for this decision rather than shipping a large MP diff that cannot pass its own gate.
