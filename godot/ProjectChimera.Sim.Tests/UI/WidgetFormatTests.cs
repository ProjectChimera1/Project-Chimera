#nullable enable
using System;
using System.Globalization;
using System.Threading;
using ProjectChimera.Core; // Fixed, SimulationLoop
using ProjectChimera.Dsl;  // DslValueType
using ProjectChimera.UI;   // WidgetFormat
using Xunit;

namespace ProjectChimera.Sim.Tests.UI
{
    /// <summary>
    /// DW-363 — regression net for <see cref="WidgetFormat"/>, the Story-7.8 custom-UI read-rail presentation
    /// formatters. The helper shipped with ZERO coverage even though every branch it carries is non-obvious:
    /// <list type="bullet">
    ///   <item><c>MmSs</c> reads a Fixed bind as SECONDS but any other bind as TICKS, divides by
    ///   <c>ticksPerSecond</c> only when that argument is positive, and clamps a negative/expired value to
    ///   <c>0:00</c>.</item>
    ///   <item><c>Fraction</c> guards a non-positive denominator (without it, <c>0/0</c> returns NaN straight
    ///   through both clamp comparisons and <c>n/0</c> returns +Infinity → a FULL bar), keeps a Fixed bind's
    ///   sub-integer precision, and clamps the result to [0,1].</item>
    ///   <item><c>Number</c> renders a Fixed bind as a trimmed decimal off the 16.16 raw, everything else as the
    ///   bare integer, always culture-invariantly.</item>
    /// </list>
    /// The helper is presentation-only — nothing here is folded into SimChecksum / CanonicalModelHash /
    /// StartStateHash, so a regression is a visible display bug, never a desync. These tests pin the shipped
    /// behavior; they do not change it.
    /// </summary>
    public class WidgetFormatTests
    {
        // Raw 16.16 helpers so each expectation is written in human units, not magic ints.
        private static int Sec(int seconds) => Fixed.FromInt(seconds).Raw;

        // ── Int: the bare culture-invariant integer ────────────────────────────────

        [Theory]
        [InlineData(0, "0")]
        [InlineData(7, "7")]
        [InlineData(-42, "-42")]
        [InlineData(int.MaxValue, "2147483647")]
        [InlineData(int.MinValue, "-2147483648")]
        public void Int_IsTheBareInvariantInteger(int value, string expected)
        {
            Assert.Equal(expected, WidgetFormat.Int(value));
        }

        // ── Number: Fixed → trimmed decimal, everything else → the integer ─────────

        [Theory]
        [InlineData(DslValueType.Int, 1234, "1234")]
        [InlineData(DslValueType.Int, -7, "-7")]
        [InlineData(DslValueType.Bool, 1, "1")]
        [InlineData(DslValueType.Bool, 0, "0")]
        [InlineData(DslValueType.EntityRef, 42, "42")]
        [InlineData(DslValueType.FactionRef, 3, "3")]
        [InlineData(DslValueType.TimerRef, 9, "9")]
        public void Number_NonFixedBind_RendersTheRawInteger(DslValueType type, int raw0, string expected)
        {
            Assert.Equal(expected, WidgetFormat.Number(type, raw0));
        }

        [Fact]
        public void Number_FixedBind_ReadsThe1616Raw_NotTheRawInteger()
        {
            // The single most important discriminator: the same raw renders completely differently per type.
            // A Fixed raw of 65536 is the number 1, not the number 65536.
            Assert.Equal("65536", WidgetFormat.Number(DslValueType.Int, Fixed.ONE));
            Assert.Equal("1", WidgetFormat.Number(DslValueType.Fixed, Fixed.ONE));
        }

        [Theory]
        [InlineData(0, "0")]
        [InlineData(1, "1")]
        [InlineData(7, "7")]
        [InlineData(-3, "-3")]
        [InlineData(1000, "1000")]
        public void Number_FixedWholeNumber_TrimsTheDecimalPart(int whole, string expected)
        {
            Assert.Equal(expected, WidgetFormat.Number(DslValueType.Fixed, Fixed.FromInt(whole).Raw));
        }

        [Theory]
        [InlineData(Fixed.ONE / 2, "0.5")]
        [InlineData(Fixed.ONE / 4, "0.25")]
        [InlineData(-(Fixed.ONE / 2), "-0.5")]
        [InlineData(Fixed.ONE + (Fixed.ONE / 2), "1.5")]
        [InlineData(Fixed.ONE * 12 + (Fixed.ONE / 2), "12.5")]
        public void Number_FixedFraction_RendersUpToTwoDecimals(int raw0, string expected)
        {
            Assert.Equal(expected, WidgetFormat.Number(DslValueType.Fixed, raw0));
        }

        [Fact]
        public void Number_FixedSubPrecision_RoundsToZero_RatherThanShowingTheRaw()
        {
            // raw 1 is 1/65536 ≈ 0.0000153 — the "0.##" format collapses it to zero. If the Fixed branch ever
            // fell through to the Int branch this would read "1".
            Assert.Equal("0", WidgetFormat.Number(DslValueType.Fixed, 1));
        }

        [Fact]
        public void Number_FixedThirds_RoundToTwoDecimals()
        {
            // 21845/65536 ≈ 0.33333 → "0.33"; 43691/65536 ≈ 0.66667 → "0.67".
            Assert.Equal("0.33", WidgetFormat.Number(DslValueType.Fixed, Fixed.ONE / 3));
            Assert.Equal("0.67", WidgetFormat.Number(DslValueType.Fixed, (Fixed.ONE * 2) / 3));
        }

        [Fact]
        public void Number_IsCultureInvariant_UnderACommaDecimalCulture()
        {
            // de-DE renders 0.5 as "0,5" and groups thousands with "." — both would corrupt the widget text.
            // (Under globalization-invariant mode the runtime hands back an invariant-behaving culture; the
            // swap then proves nothing but the invariant expectations below still hold, so the test stays valid
            // rather than failing on a runtime-configuration difference.)
            var comma = new CultureInfo("de-DE");

            CultureInfo previous = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = comma;

                Assert.Equal("0.5", WidgetFormat.Number(DslValueType.Fixed, Fixed.ONE / 2));
                Assert.Equal("12.5", WidgetFormat.Number(DslValueType.Fixed, Fixed.ONE * 12 + (Fixed.ONE / 2)));
                Assert.Equal("1234567", WidgetFormat.Number(DslValueType.Int, 1234567));
                Assert.Equal("1234567", WidgetFormat.Int(1234567));
                Assert.Equal("1:30", WidgetFormat.MmSs(DslValueType.Fixed, Sec(90)));
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = previous;
            }
        }

        // ── MmSs: Fixed-as-seconds vs Int-as-ticks ────────────────────────────────

        [Theory]
        [InlineData(0, "0:00")]
        [InlineData(5, "0:05")]
        [InlineData(59, "0:59")]
        [InlineData(60, "1:00")]
        [InlineData(65, "1:05")]
        [InlineData(90, "1:30")]
        [InlineData(600, "10:00")]
        [InlineData(3600, "60:00")] // minutes are NOT wrapped into hours
        public void MmSs_FixedBind_IsWholeSeconds(int seconds, string expected)
        {
            Assert.Equal(expected, WidgetFormat.MmSs(DslValueType.Fixed, Sec(seconds)));
        }

        [Fact]
        public void MmSs_FixedBind_FloorsAPartialSecond()
        {
            // 59 + 65535/65536 seconds must read 0:59, never round up to 1:00.
            Assert.Equal("0:59", WidgetFormat.MmSs(DslValueType.Fixed, Sec(59) + (Fixed.ONE - 1)));
            Assert.Equal("0:00", WidgetFormat.MmSs(DslValueType.Fixed, Fixed.ONE - 1));
        }

        [Fact]
        public void MmSs_FixedBind_IgnoresTicksPerSecond()
        {
            // A Fixed bind is already seconds — the tick-rate argument must not touch it.
            const string expected = "1:30";
            Assert.Equal(expected, WidgetFormat.MmSs(DslValueType.Fixed, Sec(90)));
            Assert.Equal(expected, WidgetFormat.MmSs(DslValueType.Fixed, Sec(90), 1));
            Assert.Equal(expected, WidgetFormat.MmSs(DslValueType.Fixed, Sec(90), 1000));
            Assert.Equal(expected, WidgetFormat.MmSs(DslValueType.Fixed, Sec(90), 0));
            Assert.Equal(expected, WidgetFormat.MmSs(DslValueType.Fixed, Sec(90), -30));
        }

        [Theory]
        [InlineData(0, "0:00")]
        [InlineData(29, "0:00")]  // integer division truncates a partial second down
        [InlineData(30, "0:01")]
        [InlineData(59, "0:01")]
        [InlineData(900, "0:30")]
        [InlineData(1800, "1:00")]
        [InlineData(2700, "1:30")]
        public void MmSs_IntBind_IsTicksAtTheDefaultRate(int ticks, string expected)
        {
            Assert.Equal(expected, WidgetFormat.MmSs(DslValueType.Int, ticks));
        }

        [Fact]
        public void MmSs_SameRaw_ReadsDifferentlyPerBindType()
        {
            // 2700 as TICKS is 1:30; 2700 as a Fixed raw is 0.041 seconds → 0:00. Collapsing the two branches
            // would silently mis-time every custom-UI timer widget.
            Assert.Equal("1:30", WidgetFormat.MmSs(DslValueType.Int, 2700));
            Assert.Equal("0:00", WidgetFormat.MmSs(DslValueType.Fixed, 2700));
        }

        [Fact]
        public void MmSs_DefaultTickRate_TracksTheAuthoritativeSimRate()
        {
            // WidgetFormat carries its own DefaultTicksPerSecond literal. This pins it to SimulationLoop's
            // authoritative rate so the two cannot drift apart unnoticed.
            Assert.Equal("0:01", WidgetFormat.MmSs(DslValueType.Int, SimulationLoop.TICKS_PER_SECOND));
            foreach (int ticks in new[] { 0, 1, 29, 30, 900, 2700, 123456 })
            {
                Assert.Equal(
                    WidgetFormat.MmSs(DslValueType.Int, ticks, SimulationLoop.TICKS_PER_SECOND),
                    WidgetFormat.MmSs(DslValueType.Int, ticks));
            }
        }

        [Theory]
        [InlineData(120, 60, "0:02")]
        [InlineData(60, 60, "0:01")]
        [InlineData(600, 10, "1:00")]
        [InlineData(1, 1, "0:01")]
        public void MmSs_IntBind_HonoursAnExplicitTickRate(int ticks, int ticksPerSecond, string expected)
        {
            Assert.Equal(expected, WidgetFormat.MmSs(DslValueType.Int, ticks, ticksPerSecond));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(-30)]
        [InlineData(int.MinValue)]
        public void MmSs_NonPositiveTickRate_TreatsTheRawAsSeconds_WithoutDividing(int ticksPerSecond)
        {
            // The `ticksPerSecond > 0` guard is the only thing standing between a zero rate and a
            // DivideByZeroException; a negative rate must not invert the clock either.
            Assert.Equal("1:30", WidgetFormat.MmSs(DslValueType.Int, 90, ticksPerSecond));
            Assert.Equal("0:00", WidgetFormat.MmSs(DslValueType.Int, 0, ticksPerSecond));
        }

        [Theory]
        [InlineData(DslValueType.Int, -1)]
        [InlineData(DslValueType.Int, -900)]
        [InlineData(DslValueType.Int, int.MinValue)]
        [InlineData(DslValueType.Fixed, -1)]              // a sub-precision negative still floors to -1 second
        [InlineData(DslValueType.Fixed, -Fixed.ONE)]
        [InlineData(DslValueType.Fixed, int.MinValue)]
        [InlineData(DslValueType.TimerRef, -5)]
        public void MmSs_NegativeOrExpired_ClampsToZero(DslValueType type, int raw0)
        {
            Assert.Equal("0:00", WidgetFormat.MmSs(type, raw0));
        }

        [Fact]
        public void MmSs_NonFixedBinds_AllTakeTheTicksPath()
        {
            // The switch is Fixed-vs-everything-else, not Fixed-vs-Int.
            foreach (DslValueType type in new[]
                     {
                         DslValueType.Int, DslValueType.Bool, DslValueType.EntityRef,
                         DslValueType.FactionRef, DslValueType.TimerRef, DslValueType.Point,
                         DslValueType.Array,
                     })
            {
                Assert.Equal("0:30", WidgetFormat.MmSs(type, 900));
            }
        }

        [Fact]
        public void MmSs_ExtremeRaws_DoNotThrowAndStayWellFormed()
        {
            string big = WidgetFormat.MmSs(DslValueType.Int, int.MaxValue);
            Assert.Contains(":", big);
            Assert.Equal(2, big.Substring(big.IndexOf(':') + 1).Length); // seconds are always 2 digits

            Assert.Equal("0:00", WidgetFormat.MmSs(DslValueType.Int, int.MinValue));
            Assert.Equal("0:00", WidgetFormat.MmSs(DslValueType.Fixed, int.MinValue));
        }

        [Theory]
        [InlineData(0, "0:00")]
        [InlineData(9, "0:09")]
        [InlineData(10, "0:10")]
        [InlineData(59, "0:59")]
        public void MmSs_SecondsAreAlwaysTwoDigits_MinutesAreNotPadded(int seconds, string expected)
        {
            Assert.Equal(expected, WidgetFormat.MmSs(DslValueType.Fixed, Sec(seconds)));
        }

        // ── Fraction: the divide-by-zero guard and the [0,1] clamp ────────────────

        [Theory]
        [InlineData(0, 100, 0.0)]
        [InlineData(25, 100, 0.25)]
        [InlineData(50, 100, 0.5)]
        [InlineData(100, 100, 1.0)]
        [InlineData(3, 4, 0.75)]
        [InlineData(1, 8, 0.125)]
        public void Fraction_IntBind_DividesDirectly(int raw0, int max, double expected)
        {
            Assert.Equal(expected, WidgetFormat.Fraction(DslValueType.Int, raw0, max), 10);
        }

        [Theory]
        [InlineData(101, 100)]
        [InlineData(150, 100)]
        [InlineData(int.MaxValue, 1)]
        public void Fraction_AboveTheDenominator_ClampsToOne(int raw0, int max)
        {
            Assert.Equal(1.0, WidgetFormat.Fraction(DslValueType.Int, raw0, max), 10);
        }

        [Theory]
        [InlineData(-1, 100)]
        [InlineData(-50, 100)]
        [InlineData(int.MinValue, 1)]
        public void Fraction_BelowZero_ClampsToZero(int raw0, int max)
        {
            Assert.Equal(0.0, WidgetFormat.Fraction(DslValueType.Int, raw0, max), 10);
        }

        [Theory]
        [InlineData(DslValueType.Int, 50, 0)]
        [InlineData(DslValueType.Int, 0, 0)]
        [InlineData(DslValueType.Int, -5, 0)]
        [InlineData(DslValueType.Int, 50, -100)]
        [InlineData(DslValueType.Fixed, Fixed.ONE / 2, 0)]
        [InlineData(DslValueType.Fixed, Fixed.ONE, -4)]
        public void Fraction_NonPositiveDenominator_IsAnEmptyBar_NeverNaNOrFull(DslValueType type, int raw0, int max)
        {
            double f = WidgetFormat.Fraction(type, raw0, max);

            // Without the `max <= 0` guard: 50/0 is +Infinity (clamped to a FULL bar) and 0/0 is NaN, which
            // slips through both `f < 0` and `f > 1` comparisons and reaches the ProgressBar unclamped.
            Assert.False(double.IsNaN(f), "a non-positive denominator must not produce NaN");
            Assert.False(double.IsInfinity(f), "a non-positive denominator must not produce Infinity");
            Assert.Equal(0.0, f, 10);
        }

        [Fact]
        public void Fraction_FixedBind_KeepsSubIntegerPrecision()
        {
            // The documented reason the Fixed branch exists: a fractional Fixed fills the bar proportionally
            // instead of stepping by whole units. Read as an Int raw, 32768/1 would clamp to a FULL bar.
            Assert.Equal(0.5, WidgetFormat.Fraction(DslValueType.Fixed, Fixed.ONE / 2, 1), 10);
            Assert.Equal(0.25, WidgetFormat.Fraction(DslValueType.Fixed, Fixed.ONE / 4, 1), 10);
            Assert.Equal(1.0, WidgetFormat.Fraction(DslValueType.Int, Fixed.ONE / 2, 1), 10);
        }

        [Theory]
        [InlineData(3, 4, 0.75)]
        [InlineData(1, 4, 0.25)]
        [InlineData(0, 4, 0.0)]
        [InlineData(4, 4, 1.0)]
        [InlineData(5, 4, 1.0)]   // upper clamp on the Fixed branch
        [InlineData(-1, 4, 0.0)]  // lower clamp on the Fixed branch
        public void Fraction_FixedBind_ClampsLikeTheIntBranch(int whole, int max, double expected)
        {
            Assert.Equal(expected, WidgetFormat.Fraction(DslValueType.Fixed, Fixed.FromInt(whole).Raw, max), 10);
        }

        [Fact]
        public void Fraction_FixedBind_ReadsThe1616Raw_NotTheRawInteger()
        {
            // A Fixed raw of 2*65536 over a max of 4 is 0.5, not a clamped full bar.
            Assert.Equal(0.5, WidgetFormat.Fraction(DslValueType.Fixed, Fixed.FromInt(2).Raw, 4), 10);
            Assert.Equal(1.0, WidgetFormat.Fraction(DslValueType.Int, Fixed.FromInt(2).Raw, 4), 10);
        }

        [Fact]
        public void Fraction_IsAlwaysAFiniteValueInTheUnitInterval()
        {
            int[] raws = { int.MinValue, -Fixed.ONE, -1, 0, 1, Fixed.ONE / 2, Fixed.ONE, 12345, int.MaxValue };
            int[] maxes = { int.MinValue, -100, -1, 0, 1, 2, 100, int.MaxValue };
            var types = new[] { DslValueType.Int, DslValueType.Fixed, DslValueType.Bool, DslValueType.EntityRef };

            foreach (DslValueType type in types)
            foreach (int raw0 in raws)
            foreach (int max in maxes)
            {
                double f = WidgetFormat.Fraction(type, raw0, max);
                Assert.False(double.IsNaN(f), $"NaN for {type} raw0={raw0} max={max}");
                Assert.False(double.IsInfinity(f), $"Infinity for {type} raw0={raw0} max={max}");
                Assert.InRange(f, 0.0, 1.0);
            }
        }

        [Fact]
        public void MmSs_NeverThrows_AcrossTheRawAndTickRateGrid()
        {
            int[] raws = { int.MinValue, -Fixed.ONE, -1, 0, 1, Fixed.ONE, Sec(90), int.MaxValue };
            int[] rates = { int.MinValue, -30, -1, 0, 1, 30, 1000, int.MaxValue };
            var types = new[] { DslValueType.Int, DslValueType.Fixed, DslValueType.TimerRef };

            foreach (DslValueType type in types)
            foreach (int raw0 in raws)
            foreach (int rate in rates)
            {
                string s = WidgetFormat.MmSs(type, raw0, rate);
                int colon = s.IndexOf(':');
                Assert.True(colon > 0, $"malformed '{s}' for {type} raw0={raw0} rate={rate}");
                Assert.Equal(2, s.Length - colon - 1);
                Assert.True(int.Parse(s.Substring(colon + 1), CultureInfo.InvariantCulture) < 60);
                Assert.DoesNotContain("-", s); // the negative clamp must hold everywhere
            }
        }

        [Fact]
        public void Number_NeverThrows_AcrossTheRawGrid()
        {
            int[] raws = { int.MinValue, -Fixed.ONE, -1, 0, 1, Fixed.ONE, int.MaxValue };
            var types = new[] { DslValueType.Int, DslValueType.Fixed, DslValueType.Bool, DslValueType.Point };

            foreach (DslValueType type in types)
            foreach (int raw0 in raws)
            {
                string s = WidgetFormat.Number(type, raw0);
                Assert.False(string.IsNullOrEmpty(s));
                Assert.DoesNotContain(",", s);           // never a grouping separator / comma decimal point
                Assert.DoesNotContain("E", s, StringComparison.OrdinalIgnoreCase); // never scientific notation
            }
        }
    }
}
