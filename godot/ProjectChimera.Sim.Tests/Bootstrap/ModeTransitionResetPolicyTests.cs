#nullable enable
using ProjectChimera.Core.Bootstrap;
using Xunit;

namespace ProjectChimera.Sim.Tests.Bootstrap
{
    /// <summary>
    /// Truth-table coverage for <see cref="ModeTransitionResetPolicy.Decide"/> (DW-22) — the pure extraction of
    /// <c>WinConditionPhase</c>'s reset-routing DECISION. This pins the value the highest-blast-radius reset gates on:
    /// a regression that returned <see cref="ModeResetAction.AuthoredStart"/> during a live online match or an active
    /// replay is what would desync lockstep / clobber the restored replay seed downstream. Scope note: these tests
    /// assert <c>Decide</c>'s return value only — the Godot-coupled handler's dispatch on that value (calling
    /// <c>ResetToAuthoredStart</c> vs <c>ResetMatchOnReturnToEdit</c>) is covered by the in-engine gate, not here.
    /// Mirrors <see cref="PhaseOrderTest"/>'s Godot-free Xunit conventions.
    /// </summary>
    public class ModeTransitionResetPolicyTests
    {
        [Theory]
        // isOnline, hasReplay, targetIsPlay, expected — all 8 rows of the spec I/O matrix.
        [InlineData(false, false, true,  ModeResetAction.AuthoredStart)] // offline editor loop → Play
        [InlineData(false, false, false, ModeResetAction.AuthoredStart)] // offline editor loop → Edit
        [InlineData(true,  false, true,  ModeResetAction.None)]          // online match → Play (never re-apply)
        [InlineData(true,  false, false, ModeResetAction.Lifecycle)]     // online match → Edit (lifecycle-only)
        [InlineData(false, true,  true,  ModeResetAction.None)]          // replay playback → Play (never clobber seed)
        [InlineData(false, true,  false, ModeResetAction.Lifecycle)]     // replay playback → Edit
        [InlineData(true,  true,  true,  ModeResetAction.None)]          // online + replay → Play
        [InlineData(true,  true,  false, ModeResetAction.Lifecycle)]     // online + replay → Edit
        public void Decide_MatchesTruthTable(bool isOnline, bool hasReplay, bool targetIsPlay, ModeResetAction expected)
        {
            Assert.Equal(expected, ModeTransitionResetPolicy.Decide(isOnline, hasReplay, targetIsPlay));
        }

        /// <summary>
        /// Safety invariant surfaced by the matrix: <c>AuthoredStart</c> is returned iff
        /// <c>!isOnline &amp;&amp; !hasReplay</c>, for EVERY target mode. Exhaustively enumerated so no
        /// (isOnline, hasReplay, targetIsPlay) combination can ever return the world-clobbering
        /// <c>AuthoredStart</c> — the value the handler gates the destructive reset on — while online or replaying.
        /// </summary>
        [Fact]
        public void AuthoredStart_ReturnedIffOfflineAndNoReplay_ForEveryTargetMode()
        {
            foreach (bool isOnline in new[] { false, true })
            foreach (bool hasReplay in new[] { false, true })
            foreach (bool targetIsPlay in new[] { false, true })
            {
                bool expectAuthoredStart = !isOnline && !hasReplay;
                ModeResetAction action = ModeTransitionResetPolicy.Decide(isOnline, hasReplay, targetIsPlay);
                Assert.Equal(expectAuthoredStart, action == ModeResetAction.AuthoredStart);
            }
        }
    }
}
