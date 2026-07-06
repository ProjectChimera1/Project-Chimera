#nullable enable
using System.Globalization;
using System.Threading;
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// Story 3.3 (AC1 / UX-DR34) — <see cref="UnitCardText.FormatStat"/>: the mono readout's numeric formatter.
    /// Up to 2 decimals, trailing zeros trimmed, invariant culture (a comma-decimal host locale must NOT render
    /// "0,95" — that would visually diverge from the authored data). Godot-free (pure presentation logic).
    /// </summary>
    public class UnitCardFormatTests
    {
        [Theory]
        [InlineData(55f, "55")]       // whole number → no decimals
        [InlineData(0.95f, "0.95")]   // attack_speed-style two-decimal
        [InlineData(1.5f, "1.5")]     // one trailing decimal kept
        [InlineData(8f, "8")]         // vision-style whole
        [InlineData(0f, "0")]         // crystal-at-0 (D-4)
        [InlineData(4.5f, "4.5")]     // speed
        [InlineData(3.8f, "3.8")]     // siege atk-interval
        [InlineData(270f, "270")]     // heavy hp
        public void FormatStat_TrimsTrailingZeros_UpToTwoDecimals(float value, string expected)
        {
            Assert.Equal(expected, UnitCardText.FormatStat(value));
        }

        [Fact]
        public void FormatStat_UsesInvariantDecimalSeparator_UnderACommaDecimalLocale()
        {
            CultureInfo prev = Thread.CurrentThread.CurrentCulture;
            try
            {
                // de-DE uses a comma decimal separator; the invariant formatter must still emit a '.'.
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                Assert.Equal("0.95", UnitCardText.FormatStat(0.95f));
                Assert.Equal("1.5", UnitCardText.FormatStat(1.5f));
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = prev;
            }
        }
    }
}
