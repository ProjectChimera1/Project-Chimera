#nullable enable
using ProjectChimera.Core; // SimulationLoop.DT_SECONDS — the authoritative 30 Hz timestep

namespace ProjectChimera.Multiplayer
{
    /// <summary>
    /// DW-912 — the ONLINE fixed-timestep pacer: how much sim time a frame is allowed to consume while the
    /// lockstep client is gated on the server's merged command stream. Godot-free (the DelayMath /
    /// MergedArrivalRing precedent) so the pacing policy is Tier-1 unit-testable without an engine.
    ///
    /// <para><b>Why this exists.</b> The offline path runs <c>SimulationLoop.Update(delta)</c>, whose accumulator
    /// consumes real time in fixed <see cref="SimulationLoop.DT_SECONDS"/> increments — 30 ticks/sec regardless of
    /// frame rate. The online path did NOT: it ran one <c>Flush</c> + one <c>StepOnce</c> per RENDERED FRAME, so the
    /// simulation advanced at the frame rate. On a 252 FPS machine that is ~252 ticks/sec — the match runs at ~8×
    /// speed, and, because the client sends exactly one <c>TickCommands</c> packet per tick, it also transmits
    /// ~252 command packets/sec.</para>
    ///
    /// <para><b>What that cost us.</b> The dedicated server's anti-spam gate admits
    /// <see cref="Server.CommandRateLimiter.MAX_COMMANDS_PER_WINDOW"/> = 60 command packets per slot per second and
    /// SILENTLY drops the rest — a cap derived, correctly, from "1 packet/slot/tick at 30 tps = 30/sec" with 2×
    /// headroom. The frame-paced client blew straight through it. Packet 61 was dropped; with
    /// <c>LockstepManager.INPUT_DELAY</c> = 4 that packet carried the orders for tick 60 + 4 = <b>tick 64</b>;
    /// <c>MergedTickBuilder.TryBuild</c> waits for ALL players with no deadline, so tick 64 was never emitted, and
    /// the merged packet is the sole command source for every client — so BOTH machines hung, permanently, at
    /// tick 64. Observed on the 2026-08-08 two-machine LAN run. The client never resends (the ring's sticky
    /// <c>HasSent</c>), so nothing recovers it.</para>
    ///
    /// <para><b>The two rules this encodes.</b>
    /// <list type="number">
    ///   <item>Sim time advances at a fixed 30 Hz off wall-clock, never off the frame rate — so the send rate is a
    ///     property of the tick rate, not of anyone's GPU.</item>
    ///   <item>A network stall BANKS NOTHING (<see cref="Stall"/>). In lockstep, sim time is defined by the command
    ///     stream, not by the wall clock: catching up on the seconds lost to a stall would both fast-forward the
    ///     match and fire exactly the burst that trips the throttle again. The backlog is instead capped at
    ///     <see cref="MAX_CATCHUP_TICKS"/> so an ordinary frame hitch is still absorbed.</item>
    /// </list>
    /// Together they bound the client to ≤ <c>30 + MAX_CATCHUP_TICKS</c> command packets in any one-second window
    /// — 34 against the server's cap of 60, restoring the ~1.8× headroom the limiter was designed around.</para>
    ///
    /// <para><b>Not simulation state.</b> The accumulator is wall-clock pacing: it is never folded into
    /// <c>SimChecksum</c>, cannot move a golden, and needs no cross-machine agreement (two peers pacing differently
    /// still execute the identical tick sequence — the merged stream, not the clock, decides what tick N contains).
    /// Its <c>float</c> math trips the 1.10b advisory CHM0001 for the same reason DelayMath's RTT math does, and is
    /// correct for the same reason.</para>
    /// </summary>
    public sealed class LockstepPacer
    {
        /// <summary>
        /// Ceiling on the banked backlog, in ticks. Absorbs an ordinary frame hitch (a shader compile, an asset
        /// upload) without losing sim time, while bounding the worst-case one-second send burst to
        /// <c>30 + MAX_CATCHUP_TICKS</c>. Must stay far enough under
        /// <see cref="Server.CommandRateLimiter.MAX_COMMANDS_PER_WINDOW"/> − 30 that a catch-up can never itself
        /// trip the throttle — the failure this whole class exists to prevent.
        /// </summary>
        public const int MAX_CATCHUP_TICKS = 4;

        private float _accumulator;

        /// <summary>Banked sim time not yet spent, in seconds. Test/diagnostic seam.</summary>
        public float AccumulatorSeconds => _accumulator;

        /// <summary>True when at least one whole fixed tick of real time has accrued and may be attempted.</summary>
        public bool HasTickBudget => _accumulator >= SimulationLoop.DT_SECONDS;

        /// <summary>
        /// Add a frame's real elapsed time, then clamp the bank to <see cref="MAX_CATCHUP_TICKS"/>. The clamp
        /// subsumes the offline loop's separate spiral-of-death guard: however long the frame was (a breakpoint, a
        /// tab-out, a level load), at most MAX_CATCHUP_TICKS ticks are ever owed. A non-positive delta is ignored.
        /// </summary>
        public void Accumulate(float realDeltaSeconds)
        {
            if (realDeltaSeconds > 0f) _accumulator += realDeltaSeconds;

            float ceiling = MAX_CATCHUP_TICKS * SimulationLoop.DT_SECONDS;
            if (_accumulator > ceiling) _accumulator = ceiling;
        }

        /// <summary>
        /// Spend one fixed tick. The caller must have checked <see cref="HasTickBudget"/> AND had its per-tick gate
        /// (the merged-arrival <c>Flush</c>) succeed — a tick is spent only when it is actually simulated. Floored
        /// at zero so a mis-ordered call can never bank negative time.
        /// </summary>
        public void ConsumeTick()
        {
            _accumulator -= SimulationLoop.DT_SECONDS;
            if (_accumulator < 0f) _accumulator = 0f;
        }

        /// <summary>
        /// The per-tick gate refused: the merged packet for this tick has not arrived. Hold exactly one tick of
        /// budget — enough that the next frame re-enters the loop and polls the transport immediately (so stall
        /// RECOVERY is frame-rate responsive, not 30 Hz), but not one tick more, so no backlog accrues while the
        /// network is behind and there is no catch-up burst when it recovers.
        /// </summary>
        public void Stall()
        {
            if (_accumulator > SimulationLoop.DT_SECONDS) _accumulator = SimulationLoop.DT_SECONDS;
        }

        /// <summary>Drop all banked time — match start / return to Edit, so a new match never inherits a stale bank.</summary>
        public void Reset() => _accumulator = 0f;
    }
}
