#nullable enable
using ProjectChimera.Core;        // SimulationLoop.TICKS_PER_SECOND
using ProjectChimera.Multiplayer; // ReplayFormat
using Xunit;

namespace ProjectChimera.Sim.Tests.Multiplayer
{
    /// <summary>
    /// Story 9.11 (P7) — the Godot-free replay formatting/policy helpers, pinned at their boundaries. These back the
    /// browser row (duration/result) and the playback controls (clock/speed clamp), and reference the single
    /// authoritative <see cref="SimulationLoop.TICKS_PER_SECOND"/> — never a local "30" literal.
    /// </summary>
    public class ReplayFormatTests
    {
        [Fact]
        public void TicksPerSecond_IsTheAuthoritativeSimConstant()
            => Assert.Equal(SimulationLoop.TICKS_PER_SECOND, ReplayFormat.TicksPerSecond);

        [Theory]
        [InlineData(0u,    "0:00")]
        [InlineData(29u,   "0:00")] // < 1 s
        [InlineData(30u,   "0:01")] // exactly 1 s at 30 tps
        [InlineData(1800u, "1:00")] // 60 s
        [InlineData(1815u, "1:00")] // 60.5 s → floors to 60
        [InlineData(3630u, "2:01")]
        public void Duration_FormatsTickAsClock(uint tick, string expected)
            => Assert.Equal(expected, ReplayFormat.Duration(tick));

        [Theory]
        [InlineData(2, true,  "Player 2 won")]
        [InlineData(1, true,  "Player 1 won")]
        [InlineData(0, true,  "no victor")]
        [InlineData(5, false, "incomplete")]
        [InlineData(0, false, "incomplete")]
        public void ResultText_ReflectsTrailer(int winner, bool completed, string expected)
            => Assert.Equal(expected, ReplayFormat.ResultText(winner, completed));

        [Theory]
        [InlineData(-3, 1)]
        [InlineData(0,  1)]
        [InlineData(1,  1)]
        [InlineData(4,  4)]
        [InlineData(8,  8)]
        [InlineData(9,  8)]
        [InlineData(100, 8)]
        public void ClampSpeed_ClampsToSupportedRange(int input, int expected)
            => Assert.Equal(expected, ReplayFormat.ClampSpeed(input));
    }
}
