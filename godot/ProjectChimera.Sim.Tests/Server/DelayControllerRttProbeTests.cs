#nullable enable
using ProjectChimera.Multiplayer;         // DelayMath (MIN/MAX_DELAY)
using ProjectChimera.Multiplayer.Server;   // DelayController
using Xunit;

namespace ProjectChimera.Sim.Tests.Server
{
    /// <summary>
    /// DW-395 / DW-396 / DW-401 — the server RTT probe made client-symmetric and Tier-1 testable: the ping seq is
    /// controller-owned (<see cref="DelayController.NextPingSeq"/>), a Pong folds ONLY when it echoes the
    /// OUTSTANDING probe seq and at most once per slot per probe (<see cref="DelayController.RecordPong"/>), the
    /// RTT delta is computed in wrap-safe uint arithmetic (uint-truncated clock on both ends), and a rebuilt
    /// controller (a second match in one server process) starts from a fresh probe state.
    /// </summary>
    public class DelayControllerRttProbeTests
    {
        private const int Expected     = 2;
        private const int InitialDelay = 4;  // LockstepManager.INPUT_DELAY

        // ── The happy path: a genuine echo of the outstanding probe folds and can dictate ──

        [Fact]
        public void FreshEcho_OfTheOutstandingProbe_FoldsAndDictates()
        {
            var c = new DelayController(Expected, InitialDelay);
            byte seq = c.NextPingSeq();

            // Ping stamped at 5_000 ms; echo processed at 6_000 ms → RTT 1000 ms → MAX_DELAY dictate.
            c.RecordPong(0, seq, nowMs: 6_000u, senderMs: 5_000u);

            Assert.True(c.TryComputeDirective(100u, 0u, out int delay, out _));
            Assert.Equal(DelayMath.MAX_DELAY, delay);
        }

        // ── DW-395: a stale echo of a SUPERSEDED probe is ignored (client-symmetric seq guard) ──

        [Fact]
        public void StaleEcho_OfASupersededProbe_IsNotFolded()
        {
            var c = new DelayController(Expected, InitialDelay);
            byte seq0 = c.NextPingSeq();
            c.NextPingSeq(); // a second probe went out — seq0 is now stale

            // The old server folded ANY pong: this stale echo's elapsed time spans a whole ping interval and
            // would read as a huge "RTT" (9 s here), over-dictating delay. The seq guard must drop it.
            c.RecordPong(0, seq0, nowMs: 10_000u, senderMs: 1_000u);

            Assert.False(c.TryComputeDirective(100u, 0u, out _, out _)); // nothing folded → nothing to dictate
        }

        [Fact]
        public void ForgedEcho_BeforeAnyProbeWasIssued_IsNotFolded()
        {
            var c = new DelayController(Expected, InitialDelay);

            // No ping has been issued: (byte)(_pingSeq - 1) would wrap to 255, so a forged seq-255 pong would
            // pass a naive stale check. The no-probe-outstanding guard must drop it.
            c.RecordPong(0, seq: 255, nowMs: 6_000u, senderMs: 5_000u);

            Assert.False(c.TryComputeDirective(100u, 0u, out _, out _));
        }

        // ── DW-395: one echo per slot per probe — a duplicated/forged second Pong cannot re-fold ──

        [Fact]
        public void DuplicateEcho_OfTheSameProbe_FoldsOnlyOnce()
        {
            var c = new DelayController(Expected, InitialDelay);
            byte seq = c.NextPingSeq();

            // Genuine echo: RTT 100 ms → smoothed 100 → target 3 (one tick under the initial 4).
            c.RecordPong(0, seq, nowMs: 1_000u, senderMs: 900u);
            // Forged second echo of the SAME probe with an ancient senderMs → would fold RTT 1000 ms, dragging
            // the EWMA to 212.5 ms → target 5. Must be ignored (one sample per slot per probe).
            c.RecordPong(0, seq, nowMs: 1_000u, senderMs: 0u);

            // DW-931: a 1-step change is damped — the first evaluation arms the persistence streak, and the
            // directive issues once it has held (no prior commit, so no dwell). The dictated VALUE still
            // discriminates fold-once (3) from fold-twice (5): both would emit here, at different delays.
            Assert.False(c.TryComputeDirective(100u, 0u, out _, out _));
            Assert.True(c.TryComputeDirective(100u + (uint)DelayController.SHRINK_STREAK_TICKS, 0u, out int delay, out _));
            Assert.Equal(3, delay); // the fold-once value; a folded duplicate would spike the jitter track
                                    // (DW-933) and urgent-grow to MAX_DELAY at the first evaluation instead
        }

        [Fact]
        public void NextProbe_ReArmsTheSlot_SoTheNextEchoFolds()
        {
            var c = new DelayController(Expected, InitialDelay);
            byte seq0 = c.NextPingSeq();
            c.RecordPong(0, seq0, nowMs: 1_000u, senderMs: 900u);  // RTT 100 folds

            byte seq1 = c.NextPingSeq();                            // new probe re-arms the per-slot guard
            c.RecordPong(0, seq1, nowMs: 2_000u, senderMs: 1_000u); // RTT 1000 folds → EWMA 212.5

            // DW-933: the 100→1000 swing also spikes the jitter track (deviation 900 → jitter 225), so the
            // EFFECTIVE RTT (212.5 + 4·225 = 1112.5 ms) targets MAX_DELAY — an urgent grow that emits on the
            // FIRST evaluation, bypassing the DW-931 streak. (If the re-arm failed the EWMA would still read
            // 100 ms with ZERO jitter → a shrink target, which cannot emit this early — the Assert.True below
            // would fail either way, so the fold-discrimination is preserved.)
            Assert.True(c.TryComputeDirective(100u, 0u, out int delay, out _));
            Assert.Equal(DelayMath.MAX_DELAY, delay);
        }

        // ── DW-396: the RTT delta is wrap-safe uint arithmetic (uint-truncated clock on both ends) ──

        [Fact]
        public void ClockWrapAcross32Bits_StillYieldsTheTrueRtt()
        {
            var c = new DelayController(Expected, InitialDelay);
            byte seq = c.NextPingSeq();

            // Ping stamped 16 ms before the uint clock wrapped (0xFFFF_FFF0); the echo lands 5 ms after the wrap.
            // True RTT = 21 ms. A full-width (ulong/float) subtraction reads ~-4.29e9 or ~+4.29e9 depending on
            // sign handling — either way outside the sanity window, so every sample past ~49.7 days of server
            // uptime would be rejected and delay adaptation silently frozen. Modular uint subtraction cancels
            // the wrap: 5 - 0xFFFF_FFF0 ≡ 21 (mod 2^32).
            c.RecordPong(0, seq, nowMs: 5u, senderMs: 0xFFFF_FFF0u);

            // DW-931: the resulting SHRINK is damped — arm, then emit after the shrink streak (no dwell: first
            // change). A wrap-unsafe fold would have rejected the sample → NOTHING folds → both calls false.
            Assert.False(c.TryComputeDirective(100u, 0u, out _, out _));
            Assert.True(c.TryComputeDirective(100u + (uint)DelayController.SHRINK_STREAK_TICKS, 0u, out int delay, out _));
            Assert.Equal(DelayMath.MIN_DELAY, delay); // 21 ms RTT → the floor (a genuine, sane sample)
        }

        [Fact]
        public void ImplausiblyOldEcho_IsStillRejectedBySanity()
        {
            var c = new DelayController(Expected, InitialDelay);
            byte seq = c.NextPingSeq();

            // A (seq-valid) echo whose modular delta exceeds the 10 s sanity window is rejected, exactly like the
            // client's HandlePong. Also covers a forged FUTURE senderMs (underflow → huge modular delta).
            c.RecordPong(0, seq, nowMs: 60_000u, senderMs: 5_000u);   // 55 s "RTT"
            c.RecordPong(1, seq, nowMs: 5_000u, senderMs: 6_000u);    // future stamp → wraps to ~4.29e9

            Assert.False(c.TryComputeDirective(100u, 0u, out _, out _));
        }

        // ── DW-401: a rebuilt controller (second match, same process) starts from fresh probe state ──

        [Fact]
        public void RebuiltController_RestartsTheProbeSeq_AndRejectsThePriorMatchsEcho()
        {
            // Match 1 advances the probe seq. The seq lives IN the controller (not the server node), so
            // reconstructing the controller at the next match's start — what DedicatedServer.HandleReady does —
            // is what resets it. This pins that contract.
            var match1 = new DelayController(Expected, InitialDelay);
            match1.NextPingSeq(); // 0
            byte lastMatch1Seq = match1.NextPingSeq(); // 1

            var match2 = new DelayController(Expected, InitialDelay);
            byte firstSeq = match2.NextPingSeq();
            Assert.Equal(0, firstSeq); // fresh probe state, NOT a carry-over of match 1's counter

            // A late echo from match 1 (its seq, an ancient timestamp) reaching the new match is rejected.
            match2.RecordPong(0, lastMatch1Seq, nowMs: 500_000u, senderMs: 499_900u);
            Assert.False(match2.TryComputeDirective(100u, 0u, out _, out _));

            // The new match's own echo folds normally.
            match2.RecordPong(0, firstSeq, nowMs: 500_000u, senderMs: 499_000u); // RTT 1000 → MAX
            Assert.True(match2.TryComputeDirective(100u, 0u, out int delay, out _));
            Assert.Equal(DelayMath.MAX_DELAY, delay);
        }

        // ── Slot hygiene: spectator/out-of-range echoes never fold ──

        [Fact]
        public void EchoFromANonPlayerSlot_IsIgnored()
        {
            var c = new DelayController(Expected, InitialDelay);
            byte seq = c.NextPingSeq();

            c.RecordPong(5, seq, nowMs: 6_000u, senderMs: 5_000u);  // spectator slot (not in the player map)
            c.RecordPong(-1, seq, nowMs: 6_000u, senderMs: 5_000u); // defensive out-of-range

            Assert.False(c.TryComputeDirective(100u, 0u, out _, out _));
        }
    }
}
