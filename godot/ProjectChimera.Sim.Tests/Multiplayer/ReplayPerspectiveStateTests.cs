#nullable enable
using ProjectChimera.Multiplayer;
using Xunit;

namespace ProjectChimera.Sim.Tests.Multiplayer
{
    /// <summary>
    /// DW-431 — the replay playback view-perspective state machine extracted from MainScene. Pins the
    /// begin-at-reveal-all default, the exact pre-extraction cycle/wrap behavior, and the DW-431 fix itself:
    /// <see cref="ReplayPerspectiveState.EndSession"/> resets a cycled single-player perspective back to
    /// reveal-all, so a replay that finishes naturally never leaves the frozen final frame under a stale
    /// player's fog-of-war (pre-fix, only F5 / return-to-Edit reset it).
    /// </summary>
    public class ReplayPerspectiveStateTests
    {
        [Fact]
        public void FreshState_IsRevealAll()
        {
            var s = new ReplayPerspectiveState();
            Assert.Equal(ReplayPerspectiveState.RevealAllPerspective, s.Perspective);
            Assert.True(s.IsRevealAll);
        }

        /// <summary>The pre-extraction MainScene.ReplayCyclePerspective wrap, byte-identical: reveal-all →
        /// roster[0] → roster[1] → reveal-all for a 2-player roster.</summary>
        [Fact]
        public void Cycle_WrapsThroughRosterBackToRevealAll()
        {
            var s = new ReplayPerspectiveState();
            Assert.Equal(0, s.Cycle(2));
            Assert.False(s.IsRevealAll);
            Assert.Equal(1, s.Cycle(2));
            Assert.Equal(ReplayPerspectiveState.RevealAllPerspective, s.Cycle(2)); // wrap
            Assert.True(s.IsRevealAll);
            Assert.Equal(0, s.Cycle(2)); // and around again
        }

        /// <summary>rosterLength 0 (no active replay — e.g. the cycle hotkey after teardown) always lands on
        /// reveal-all, exactly like the pre-extraction <c>rp?.Roster.Length ?? 0</c> path.</summary>
        [Fact]
        public void Cycle_WithNoRoster_AlwaysRevealAll()
        {
            var s = new ReplayPerspectiveState();
            Assert.Equal(ReplayPerspectiveState.RevealAllPerspective, s.Cycle(0));
            Assert.Equal(ReplayPerspectiveState.RevealAllPerspective, s.Cycle(0));
            Assert.True(s.IsRevealAll);
        }

        /// <summary>THE DW-431 regression: a session that ends (natural finish at the final tick, or teardown)
        /// while a single-player perspective is applied resets to reveal-all — the viewer is never left on a
        /// stale player's fog. Fails without the fix (pre-fix there was no end-of-session reset at all).</summary>
        [Fact]
        public void EndSession_AfterCyclingToAPlayer_ResetsToRevealAll()
        {
            var s = new ReplayPerspectiveState();
            s.BeginSession();
            s.Cycle(4);                 // roster[0] — a single player's fog is applied
            Assert.False(s.IsRevealAll);

            s.EndSession();             // the natural-finish teardown (MainScene replay-finished branch)
            Assert.True(s.IsRevealAll);
            Assert.Equal(ReplayPerspectiveState.RevealAllPerspective, s.Perspective);
        }

        /// <summary>BeginSession resets a perspective left over from a PRIOR session (the browser-load path calls
        /// it before each playback), so session N+1 never inherits session N's cycled viewer.</summary>
        [Fact]
        public void BeginSession_ResetsAPriorSessionsPerspective()
        {
            var s = new ReplayPerspectiveState();
            s.Cycle(2);
            Assert.False(s.IsRevealAll);
            s.BeginSession();
            Assert.True(s.IsRevealAll);
        }
    }
}
