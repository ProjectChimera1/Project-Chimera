---
title: 'Per-client command-rate throttle / anti-spam on the dedicated server'
type: 'feature'
created: '2026-07-24'
status: 'done'
review_loop_iteration: 0
followup_review_recommended: false # patched this pass: 0 high, 0 medium, 2 low → score 2 (<5), no high
baseline_revision: '04065a83f1e0f6391d62ecac7827c06fdeac1897'
final_revision: 'd94ed83bc546ef191e8749ce5c2f0f20da72c9eb'
context:
  - '{project-root}/_bmad-output/implementation-artifacts/epic-9-context.md'
warnings: ['oversized']
---

<intent-contract>

## Intent

**Problem:** The dedicated server fans every inbound `TickCommands` packet straight into the merged-tick builder with no per-client rate ceiling (`DedicatedServer.HandlePacket` → `FanInTickCommands`, DedicatedServer.cs:419-422). A misbehaving or malicious client on a trusted-friends EA match can flood the server with far more command packets per second than legitimate 30 tps play ever produces, burning server CPU/bandwidth.

**Approach:** Add a pure server-side, per-slot command-rate throttle that silently drops a client's `TickCommands` packets once they exceed a cap set comfortably above worst-case legitimate play, mirroring Story 9.3's silent drop-not-notify contract. It is anti-spam, not anti-cheat: a validation layer at the receive edge that never enters the simulation, the merged builder, or any checksum.

## Boundaries & Constraints

**Always:**
- The throttle lives in a new **Godot-free** class under `src/Multiplayer/Server/**` (compiled into the Tier-1 assembly + banned-API analyzer), mirroring `MergedTickBuilder`/`DropController`; the Godot `DedicatedServer` node stays a thin adapter.
- Because it sits in the analyzed sim assembly, use **integer arithmetic only** — no `float`/`double`, `System.Random`, `DateTime`, or `Dictionary` enumeration. The wall-clock reference is an injected `ulong` millisecond value (`Time.GetTicksMsec()` from the adapter), exactly as `applyAtTick` is injected into `DropController`.
- Slot identity is transport-authoritative: keyed only off the `slot` argument `HandlePacket` already resolved via `ServerTransport.FindSlot`, never off a packet byte.
- A dropped packet is discarded **silently** — no server→client packet, no `DesyncAlert`, no `FanInTickCommands` call. Any server-console diagnostic must itself be throttled (never one line per dropped packet).
- The cap sits **above worst-case legitimate play**: ≥ 2× the 30-tick/sec sustained rate, with burst headroom above the `[2,12]` delay pipeline (`DelayMath.MIN_DELAY`/`MAX_DELAY`) and the 32-order packet (`TickCommandPacket.MAX_ORDERS`). Magnitudes are named constants.
- Per-slot state is **reset on connect** (`HandleConnect`, DedicatedServer.cs:236) so a recycled slot never inherits the prior occupant's count (SoA-recycle-trap discipline). One slot at its cap must never affect another slot's admissions.

**Block If:**
- (none — the cap floor and silent-drop contract are fully determined by the epic constraint and the Story 9.3 precedent.)

**Never:**
- Never touch the simulation, `MergedTickBuilder`, `SimChecksum`, `ScenarioApplier`, replay, or any determinism path — the throttle decision must never fold into any hash or alter tick output.
- Never rate-limit against a client-influenceable or stall-prone clock (e.g. the packet's own tick field, or `_latestSeenTick` which only advances on a *valid* submit). Use injected wall-clock ms.
- Never notify the throttled client, retaliate, or escalate to a disconnect/ban — this is EA anti-spam, not anti-cheat enforcement.
- No third resource clock, no new NuGet dependency, no per-packet allocation on the hot path.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Legit sustained | 1 packet/slot every ~33ms (30 tps) over several simulated seconds | Every packet admitted; drop count 0 | No error expected |
| Legit burst | A burst of `MAX_DELAY`(12)+ packets from one slot at the same ms (delay-pipeline catch-up) | All admitted — below the cap | No error expected |
| Spam flood | Hundreds of packets from one slot at the same ms | First `MAX_COMMANDS_PER_WINDOW` admitted, remainder dropped silently; admitted == cap | Silent drop, no client packet |
| Window recovery | Slot capped, then `WINDOW_MS` of injected time elapses | Admissions resume from a fresh window | No error expected |
| Per-slot isolation | Slot A flooded/capped; slot B sends legit rate | Slot B fully admitted regardless of A | No error expected |
| Slot reuse | Slot capped, client disconnects, new client connects into same slot (`HandleConnect`) | Reused slot starts fresh — no inherited count | No error expected |

</intent-contract>

## Code Map

- `godot/src/Multiplayer/Server/CommandRateLimiter.cs` -- **new**, Godot-free per-slot fixed/sliding-window limiter; integer-only; `bool TryAdmit(int slot, ulong nowMs)` returns `false` to drop; `Reset(int slot)`; diagnostic `long DroppedCount(int slot)`.
- `godot/src/Multiplayer/DedicatedServer.cs` -- construct `_rateLimiter` alongside `_builder`/`_delayController`/`_dropController` (lines 550-560), sized to `ServerTransport.MAX_SLOTS`; gate the `TickCommands` dispatch case (lines 419-422) through `TryAdmit(slot, Time.GetTicksMsec())` before `FanInTickCommands`; call `_rateLimiter.Reset(slot)` in `HandleConnect` (line 236).
- `godot/src/Multiplayer/Server/MergedTickBuilder.cs` -- **read-only reference** for the silent `return false` drop contract (`.cs:106-169`) this mirrors.
- `godot/src/Multiplayer/Server/DropController.cs` -- **read-only reference** for the Godot-free per-slot-array + injected-clock pattern.
- `godot/src/Multiplayer/DelayMath.cs` / `NetworkCommand.cs` / `Core/SimulationLoop.cs` -- **read-only**: cap-floor constants (`MIN_DELAY`/`MAX_DELAY` `[2,12]`, `TickCommandPacket.MAX_ORDERS`=32, 30 tps).
- `godot/ProjectChimera.Sim.Tests/Server/CommandRateLimiterTests.cs` -- **new**, Tier-1 xUnit, mirroring `MergedTickBuilderTests.cs`/`DropControllerTests.cs`.

## Tasks & Acceptance

**Execution:**
- `godot/src/Multiplayer/Server/CommandRateLimiter.cs` -- New Godot-free class. Per-slot parallel arrays (`long[] _count`, `ulong[] _windowStartMs`, `long[] _dropped`) sized to a ctor `slots` count. `TryAdmit(slot, nowMs)`: out-of-range slot ⇒ `false`; if `nowMs - _windowStartMs[slot] >= WINDOW_MS` reset that slot's window (`_windowStartMs=nowMs`, `_count=0`); if `_count < MAX_COMMANDS_PER_WINDOW` increment and return `true`, else increment `_dropped[slot]` and return `false`. `Reset(slot)` zeroes that slot's window/count (not the lifetime drop tally). Constants: `WINDOW_MS` (e.g. `1000`) and `MAX_COMMANDS_PER_WINDOW` (e.g. `60` — 2× the 30 tps sustained rate, far above the 12-tick delay pipeline); document the floor. Integer-only; no float/Random/DateTime/Dictionary.
- `godot/src/Multiplayer/DedicatedServer.cs` -- Add `_rateLimiter` field; construct it at the InGame transition (near line 550) sized to `ServerTransport.MAX_SLOTS`. In `HandlePacket`'s `TickCommands` case, when InGame, admit through `_rateLimiter.TryAdmit(slot, (ulong)Time.GetTicksMsec())` and only then call `FanInTickCommands`; a non-admit is a silent no-op (optionally a throttled `GD.Print` diagnostic, never per-packet). In `HandleConnect`, call `_rateLimiter?.Reset(slot)` so a reused slot starts clean.
- `godot/ProjectChimera.Sim.Tests/Server/CommandRateLimiterTests.cs` -- Cover every I/O-matrix row with injected synthetic `nowMs`: sustained-legit-all-admitted, burst-below-cap-admitted, flood-drops-past-cap (admitted == cap, `DroppedCount` == overflow), window-recovery, per-slot isolation, reset-clears-window, out-of-range-slot-rejected.

**Acceptance Criteria:**
- Given the dedicated server is InGame, when a client submits at the legitimate 30 tps rate (and short delay-pipeline bursts), then every `TickCommands` packet is admitted to `FanInTickCommands` and none is dropped.
- Given a client floods `TickCommands` far above the cap within one window, when the packets arrive, then admissions stop at the cap and every excess packet is dropped with no server→client packet, no `DesyncAlert`, and no call into `FanInTickCommands`/`MergedTickBuilder` — the sim and every checksum are byte-identical to a no-flood run.
- Given a slot has been throttled and a new client later connects into that reused slot, when `HandleConnect` runs, then the slot's throttle window is reset and legitimate submissions are admitted immediately.
- Given one slot is being throttled, when another slot submits at the legitimate rate, then the second slot is unaffected.
- Given the new limiter class, when the solution is built, then it compiles clean under the sim-assembly banned-API analyzer (no float/Random/DateTime/Dictionary-enumeration findings).

## Design Notes

**Why wall-clock ms, not tick-counted (a deliberate divergence from `DropController`).** Other server authorities are tick-counted because their *output enters the sim* (`DropController` injects empties at `applyAtTick`). The throttle's output is only drop/accept and **never enters the sim**, so it is free of the determinism constraint — and spam is intrinsically a real-time-rate phenomenon. `_latestSeenTick` is unusable as a clock: it advances only after a *valid* `Submit` (DedicatedServer.cs:597), so a flood of same-tick/dropped packets would not move it (zero resolution to tell flood from legit), and the tick field is client-provided. Injected wall-clock ms keeps the class Godot-free, unit-testable with synthetic timestamps, and decoupled from sim state.

**Cap sizing.** Legit sustained rate is 1 packet/slot/tick at 30 tps = 30/sec (the builder already idempotently drops a duplicate `(slot,tick)`, `MergedTickBuilder.cs:159`). `MAX_COMMANDS_PER_WINDOW`=60 over `WINDOW_MS`=1000 gives 2× sustained headroom and ~1s of burst — above the `[2,12]` pipeline and reconnect catch-up, while stopping a real flood (hundreds-to-thousands/sec). A fixed window's ≤2× boundary burst is fine for anti-spam. The silent `return false` shape mirrors 9.3's spoof/over-count drops verbatim.

## Verification

**Commands:**
- `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` -- expected: all green, including the new `CommandRateLimiterTests` (every I/O-matrix row) and the pre-existing server/checksum/replay round-trips (proves determinism preserved — the throttle touches no sim path).
- `dotnet build godot/godot.csproj` -- expected: clean; the new Godot-free limiter raises no banned-API analyzer finding and the `DedicatedServer` adapter edits compile.

## Spec Change Log

## Review Triage Log

### 2026-07-24 — Review pass
- intent_gap: 0
- bad_spec: 0
- patch: 2: (high 0, medium 0, low 2)
- defer: 2
- reject: 7
- addressed_findings:
  - `[low]` `[patch]` The throttled-drop diagnostic in `DedicatedServer.cs`'s `TickCommands` case logged on `DroppedCount(slot) % RATE_LIMIT_LOG_EVERY == 0`, but `DroppedCount` is post-incremented (≥1 on the first real drop), so the FIRST console line appeared only at the 128th dropped packet and a slot dropping <128 in a match left no trace; an out-of-range slot (count 0) would also satisfy `0 % 128 == 0` and log a nonsensical "0 packets dropped" line. Fixed: capture `long dropped` once and gate on `dropped == 1 || (dropped > 0 && dropped % RATE_LIMIT_LOG_EVERY == 0)` — logs the first drop then every 128th, never at 0.
  - `[low]` `[patch]` `CommandRateLimiter`'s `MAX_COMMANDS_PER_WINDOW` floor-derivation doc cited "reconnect catch-up" as a burst source, but client reconnect/backlog-replay is not implemented in this codebase, so the justification rested partly on a non-existent path. Trimmed to rest only on the real bounded `[2,12]` delay pipeline (a lockstep client is at most `MAX_DELAY` ticks ahead); the cap value (60) is unchanged.

Deferred (2): the merged-tick fan-in (`MergedTickBuilder.TryBuild`) has no timeout/force-advance, so any never-arriving slot stalls all clients — a pre-existing Story 9.3/9.6 backbone property, not reachable by 9.13 under the `[2,12]`-bounded pipeline, worth a missing-slot watchdog for the 9.15 4-player e2e work; and non-command client packet types (`Chat`/`LobbyChat`/`Ping`/`DelayAck`/`DropAck`/`Checksum`) remain unthrottled (pre-existing anti-DoS gap, `Chat`/`LobbyChat` amplify to all peers). Both logged to `deferred-work.md`.

Rejected (7): order/byte-rate metering instead of packet-rate (the epic explicitly makes "a full 32-order packet every tick" the legitimate worst case → packet-rate IS the intended surface); hardcoded cap/window vs the data-driven rule (tick rate is a global engine const, not creator-variable, and the anti-spam cap is infra not gameplay balance); `_windowStartMs == 0` sentinel (intentional — it forces the next packet to re-anchor a fresh window; the only failure needs an in-match reset within the first 1 s of engine boot, precluded by the lobby handshake); unguarded non-monotonic-clock underflow (the sole caller passes monotonic `Time.GetTicksMsec()`, and the fail direction is fail-OPEN — the anti-spam-safe direction, never dropping legit traffic); recycled-slot log attributes the prior occupant's lifetime tally (cosmetic wording; the tally is per-slot-per-match by design and asserted by a test); the Godot adapter wiring is not unit-tested (the `DedicatedServer` node is un-unit-testable by established project design — every sibling controller `_builder`/`_delayController`/`_dropController` is wired identically in the same untested adapter; all decision logic lives in the fully-tested Godot-free class); and the `_rateLimiter == null` fail-open branch is defensively-dead (consistent with the codebase's `?.`/null-guard convention for the sibling controllers).

## Auto Run Result

Status: done

**Summary:** Implemented Story 9.13 — a per-client command-rate throttle / anti-spam layer on the dedicated server. A new Godot-free, integer-only `CommandRateLimiter` (per-slot fixed 1000 ms window, cap 60 = 2× the 30 tps sustained rate) gates each inbound `TickCommands` packet at the `DedicatedServer.HandlePacket` receive edge: over-cap packets are dropped silently (no server→client packet, no `DesyncAlert`, no fan-in call) exactly as Story 9.3's spoof/over-count drops, keyed off the transport-authoritative slot and clocked by injected wall-clock ms so the decision never enters the sim, the merged builder, or any checksum. Per-slot state resets on `HandleConnect` (slot-recycle discipline). Four review lenses (adversarial, edge-case, verification-gap, intent-alignment) ran in parallel over the full diff; findings triaged 0 intent_gap / 0 bad_spec / 2 patch / 2 defer / 7 reject.

**Files changed:**
- `godot/src/Multiplayer/Server/CommandRateLimiter.cs` (new) — Godot-free per-slot fixed-window limiter; integer-only (passes the banned-API analyzer); `TryAdmit(slot, nowMs)` / `Reset(slot)` / `DroppedCount(slot)`.
- `godot/src/Multiplayer/DedicatedServer.cs` — construct `_rateLimiter` (sized to `ServerTransport.MAX_SLOTS`) at the InGame transition; gate the `TickCommands` dispatch case through `TryAdmit` before `FanInTickCommands` with a self-throttled first-drop-then-every-128 diagnostic; reset per slot in `HandleConnect`.
- `godot/ProjectChimera.Sim.Tests/Server/CommandRateLimiterTests.cs` (new) — 10 Tier-1 xUnit tests covering every I/O-matrix row (sustained, burst, flood, window recovery + boundary, per-slot isolation, reset-clears-window-not-tally, out-of-range, cap-floor, ctor guard).

**Review findings breakdown:** 2 patches applied (first-drop logging visibility; cap-comment accuracy), 2 deferred (fan-in has no missing-slot watchdog; non-command packet types unthrottled), 7 rejected.

**Follow-up review recommendation:** false. This pass's patched findings: 0 high, 0 medium, 2 low → score `3×0 + 1×2 = 2` (< 5), no high.

**Verification:** `dotnet build godot/godot.csproj` → 0 errors (13 pre-existing warnings). `dotnet test …ProjectChimera.Sim.Tests.csproj` → 3330 passed, 1 skipped (pre-existing reserved test), 0 failed — the new limiter tests plus every server/checksum/replay round-trip green, confirming the throttle touches no determinism path. Matrix Test Audit: all 6 I/O rows covered by tests that ran and passed.

**Residual risks:** (1) The Godot adapter wiring (the ~5-line gate + `Reset` call) is verified by inspection only — consistent with the project's established server-adapter pattern (no headless GdUnit4 server harness; every sibling controller is wired the same way), with all decision logic in the fully-tested Godot-free class. (2) The two deferred items above are pre-existing MP-backbone gaps surfaced incidentally, not reachable by this change under legitimate play.
