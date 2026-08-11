#nullable enable
using System;
using ProjectChimera.Multiplayer;         // DelayMath (MIN/MAX_DELAY)
using ProjectChimera.Multiplayer.Server;   // DelayController
using Xunit;

namespace ProjectChimera.Sim.Tests.Server
{
    /// <summary>
    /// DW-931 — delay-controller damping. The undamped comparator re-dictated the instant the EWMA'd worst-slot
    /// RTT crossed a tick boundary; the delay-2/3 boundary sits at smoothed RTT = 66.7 ms, exactly where the
    /// 2026-08-10 11:21 LAN match oscillated (frame bursts delayed Pong replies → the EWMA swung across the
    /// boundary → ~40 renegotiations in 4.5 min, each widening one costing a client-side gap-seed hiccup).
    /// Pins: a flapping RTT series produces NO change; a target must PERSIST (grow fast / shrink slowly); shrink
    /// needs a deadband margin; a genuine RTT jump (≥ URGENT_GROW_STEP) still grows immediately; a force-commit
    /// arms the dwell clock; and the streak/dwell clocks survive uint tick wrap. The pending/maturity gates —
    /// the actual desync guards — are exercised untouched by the existing DelayControllerTests.
    /// </summary>
    public class DelayControllerDampingTests
    {
        private const int Expected = 2;

        /// <summary>Feed enough identical samples that the EWMA (alpha 0.125) converges to ~<paramref name="rttMs"/>.</summary>
        private static void Converge(DelayController c, int slot, float rttMs, int samples = 40)
        {
            for (int i = 0; i < samples; i++) c.RecordRtt(slot, rttMs);
        }

        // ── 1) The field flap: an EWMA oscillating across the 66.7 ms boundary emits NOTHING ──

        [Fact]
        public void FlappingRttAcrossTheBoundary_NeverRenegotiates()
        {
            // Committed delay 3. The smoothed RTT alternates ~90 ms (target 3 == committed) and ~40 ms (target 2)
            // every 60 ticks — the 11:21-log shape. Each dip back to target==committed resets the shrink streak,
            // and no 40 ms phase lasts SHRINK_STREAK_TICKS, so across 6000 ticks (10 shrink-dwell windows) not a
            // single directive may issue. Undamped, this series renegotiated on every phase flip.
            var c = new DelayController(Expected, initialDelay: 3);
            for (uint tick = 0; tick < 6000u; tick++)
            {
                if (tick % 120u == 0u)  Converge(c, 0, 90f);
                if (tick % 120u == 60u) Converge(c, 0, 40f);
                Assert.False(c.TryComputeDirective(tick, confirmedTick: 0u, out _, out _));
            }
        }

        [Fact]
        public void DipBackToCommitted_ResetsTheShrinkStreak_ToZero()
        {
            // A shrink streak in progress is fully restarted by a single evaluation at target==committed: the
            // second 40 ms stretch must re-hold the ENTIRE SHRINK_STREAK_TICKS, not resume the first one's clock.
            var c = new DelayController(Expected, initialDelay: 3);
            Converge(c, 0, 40f);
            Assert.False(c.TryComputeDirective(100u, 0u, out _, out _));  // arms the shrink streak at tick 100

            Converge(c, 0, 90f);                                          // RTT back up → target == committed
            Assert.False(c.TryComputeDirective(250u, 0u, out _, out _));  // resets the streak

            Converge(c, 0, 40f);
            Assert.False(c.TryComputeDirective(260u, 0u, out _, out _));  // re-arms at tick 260
            // 100 + SHRINK_STREAK would have matured the ORIGINAL streak — but it was reset, so still withheld:
            Assert.False(c.TryComputeDirective(100u + (uint)DelayController.SHRINK_STREAK_TICKS + 10u, 0u, out _, out _));
            // The re-armed streak matures only at 260 + SHRINK_STREAK (first change → no dwell):
            Assert.True(c.TryComputeDirective(260u + (uint)DelayController.SHRINK_STREAK_TICKS, 0u, out int delay, out _));
            Assert.Equal(2, delay);
        }

        // ── 2) Asymmetry: grow proves itself in 2 s, shrink in 10 s + a 20 s dwell ──

        [Fact]
        public void SustainedPlusOneGrow_EmitsAfterGrowStreak_NotBefore()
        {
            var c = new DelayController(Expected, initialDelay: 2);
            c.RecordRtt(0, 80f); // OWL 40 → target 3: a +1 grow (below URGENT_GROW_STEP → streak-gated)
            Assert.False(c.TryComputeDirective(0u, 0u, out _, out _));    // arms at tick 0
            Assert.False(c.TryComputeDirective((uint)DelayController.GROW_STREAK_TICKS - 1u, 0u, out _, out _));
            Assert.True(c.TryComputeDirective((uint)DelayController.GROW_STREAK_TICKS, 0u, out int delay, out _));
            Assert.Equal(3, delay);
        }

        [Fact]
        public void SustainedShrink_EmitsAfterShrinkStreak_NotBefore()
        {
            var c = new DelayController(Expected, initialDelay: 3);
            c.RecordRtt(0, 5f); // target 2 — well clear of the deadband
            Assert.False(c.TryComputeDirective(0u, 0u, out _, out _));    // arms at tick 0
            Assert.False(c.TryComputeDirective((uint)DelayController.SHRINK_STREAK_TICKS - 1u, 0u, out _, out _));
            Assert.True(c.TryComputeDirective((uint)DelayController.SHRINK_STREAK_TICKS, 0u, out int delay, out _));
            Assert.Equal(2, delay);
        }

        [Fact]
        public void ShrinkAfterACommittedGrow_AlsoWaitsForTheShrinkDwell()
        {
            // Grow 2→3 commits (applyAtTick = the dwell base), then the RTT collapses: the shrink must hold the
            // streak AND wait SHRINK_DWELL_TICKS from where the grow landed — the anti-thrash asymmetry.
            var c = new DelayController(Expected, initialDelay: 2);
            c.RecordRtt(0, 80f);
            Assert.False(c.TryComputeDirective(0u, 0u, out _, out _));
            Assert.True(c.TryComputeDirective((uint)DelayController.GROW_STREAK_TICKS, 0u, out int d1, out uint a1));
            Assert.Equal(3, d1);
            c.RecordAck(0, d1, a1);
            c.RecordAck(1, d1, a1);
            Assert.True(c.Commit(d1, a1));

            Converge(c, 0, 5f); // link improves dramatically → target 2 (shrink)
            uint matured = a1 + (uint)DelayMath.MAX_DELAY + 1u; // clears the maturity gate (unchanged semantics)
            uint armTick = matured + 50u;
            Assert.False(c.TryComputeDirective(armTick, confirmedTick: matured, out _, out _)); // arms the streak

            uint emitTick = Math.Max(armTick + (uint)DelayController.SHRINK_STREAK_TICKS,
                                     a1 + (uint)DelayController.SHRINK_DWELL_TICKS);
            Assert.False(c.TryComputeDirective(emitTick - 1u, confirmedTick: matured, out _, out _));
            Assert.True(c.TryComputeDirective(emitTick, confirmedTick: matured, out int d2, out _));
            Assert.Equal(2, d2);
        }

        // ── 3) Shrink deadband: touching the boundary is not clearing it ──

        [Fact]
        public void SmoothedRttInsideTheDeadband_NeverShrinks()
        {
            // 60 ms maps to target 2, but 60 + SHRINK_MARGIN_MS = 72 ms maps back to 3 — the EWMA is grazing the
            // 66.7 ms boundary, exactly the flap zone. The shrink must never arm, however long it persists.
            var c = new DelayController(Expected, initialDelay: 3);
            c.RecordRtt(0, 60f);
            for (uint tick = 0; tick <= 10_000u; tick += 100u)
                Assert.False(c.TryComputeDirective(tick, 0u, out _, out _));
        }

        [Fact]
        public void SmoothedRttClearOfTheDeadband_ShrinksAfterTheStreak()
        {
            var c = new DelayController(Expected, initialDelay: 3);
            c.RecordRtt(0, 40f); // 40 + 12 = 52 ms still maps to target 2 → a genuine shrink candidate
            Assert.False(c.TryComputeDirective(0u, 0u, out _, out _));    // arms
            Assert.True(c.TryComputeDirective((uint)DelayController.SHRINK_STREAK_TICKS, 0u, out int delay, out _));
            Assert.Equal(2, delay);
        }

        // ── 4) Urgent grow: a real RTT jump bypasses streak AND dwell ──

        [Fact]
        public void UrgentGrow_EmitsImmediately_NoStreakRequired()
        {
            var c = new DelayController(Expected, initialDelay: 2);
            c.RecordRtt(0, 300f); // OWL 150 → target 6: a +4 jump ≥ URGENT_GROW_STEP
            Assert.True(c.TryComputeDirective(0u, 0u, out int delay, out _)); // first evaluation — no arming pass
            Assert.Equal(6, delay);
        }

        [Fact]
        public void UrgentGrow_BypassesTheDwell_AfterARecentCommit()
        {
            // A grow commits, and INSIDE its grow-dwell window the RTT explodes: the ≥2-tick grow must not wait.
            var c = new DelayController(Expected, initialDelay: 2);
            c.RecordRtt(0, 80f);
            Assert.False(c.TryComputeDirective(0u, 0u, out _, out _));
            Assert.True(c.TryComputeDirective((uint)DelayController.GROW_STREAK_TICKS, 0u, out int d1, out uint a1));
            c.RecordAck(0, d1, a1);
            c.RecordAck(1, d1, a1);
            Assert.True(c.Commit(d1, a1));

            Converge(c, 0, 1000f); // sustained spike → target MAX_DELAY, a ≥2 jump from 3
            uint matured = a1 + (uint)DelayMath.MAX_DELAY + 1u;
            uint tick    = matured + 1u; // well inside GROW_DWELL_TICKS of a1 — the bypass must ignore that
            Assert.True(tick < a1 + (uint)DelayController.GROW_DWELL_TICKS); // guard the premise
            Assert.True(c.TryComputeDirective(tick, confirmedTick: matured, out int d2, out _));
            Assert.Equal(DelayMath.MAX_DELAY, d2);
        }

        // ── 5) A force-commit (ACK timeout) arms the dwell clock like a normal commit ──

        [Fact]
        public void ForceCommit_ArmsTheDwellClock()
        {
            var c = new DelayController(Expected, initialDelay: 2);
            c.RecordRtt(0, 1000f); // urgent grow 2→12
            Assert.True(c.TryComputeDirective(100u, 0u, out int d1, out uint a1));
            // Slot 1 never ACKs → the DW-400 timeout force-commits the directive.
            c.RecordAck(0, d1, a1);
            Assert.True(c.CheckAckTimeout(100u + (uint)DelayController.ACK_TIMEOUT_TICKS, out _, out _));

            Converge(c, 0, 5f); // link recovers → shrink candidate
            uint matured = a1 + (uint)DelayMath.MAX_DELAY + 1u;
            uint armTick = 350u; // past the maturity gate (a1 = 132 → gate 144)
            Assert.True(armTick > matured); // guard the premise
            Assert.False(c.TryComputeDirective(armTick, confirmedTick: matured, out _, out _)); // arms the streak

            // Streak satisfied at armTick + SHRINK_STREAK (= 650), but the dwell — armed by the FORCE-commit —
            // runs to a1 + SHRINK_DWELL (= 732): the shrink must wait for the later bound.
            uint streakDone = armTick + (uint)DelayController.SHRINK_STREAK_TICKS;
            uint dwellDone  = a1 + (uint)DelayController.SHRINK_DWELL_TICKS;
            Assert.True(streakDone < dwellDone); // guard: dwell is the binding constraint in this layout
            Assert.False(c.TryComputeDirective(streakDone, confirmedTick: matured, out _, out _));
            Assert.False(c.TryComputeDirective(dwellDone - 1u, confirmedTick: matured, out _, out _));
            Assert.True(c.TryComputeDirective(dwellDone, confirmedTick: matured, out int d2, out _));
            Assert.Equal(2, d2);
        }

        // ── 6) Wrap safety: the streak clock survives the uint tick wrap (DW-396 idiom) ──

        [Fact]
        public void GrowStreak_HeldAcrossTheUintTickWrap_StillMatures()
        {
            // Arm 20 ticks before uint.MaxValue; the streak matures GROW_STREAK_TICKS later, after the wrap.
            // Signed or checked subtraction would read the held time as negative/huge and mis-time the streak.
            var c = new DelayController(Expected, initialDelay: 2);
            c.RecordRtt(0, 80f); // +1 grow → streak-gated
            uint armTick  = uint.MaxValue - 20u;
            uint emitTick = unchecked(armTick + (uint)DelayController.GROW_STREAK_TICKS); // wraps to 39
            Assert.False(c.TryComputeDirective(armTick, 0u, out _, out _));               // arms pre-wrap
            Assert.False(c.TryComputeDirective(unchecked(emitTick - 1u), 0u, out _, out _));
            Assert.True(c.TryComputeDirective(emitTick, 0u, out int delay, out _));       // matures post-wrap
            Assert.Equal(3, delay);
        }
    }
}
