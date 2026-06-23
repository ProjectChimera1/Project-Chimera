using ProjectChimera.Core;
using Xunit;

namespace ProjectChimera.Sim.Tests.Determinism;

/// <summary>
/// Boundary-value determinism tests for <see cref="Fixed"/> — the edge cases the Story 1.1 review
/// explicitly deferred here. These pin the classic cross-machine lockstep desync sources: rounding
/// DIRECTION of multiply vs divide for negatives, unchecked overflow at the 16.16 limits, and Sqrt
/// of degenerate inputs.
///
/// Every expected value is derived INDEPENDENTLY from the mathematical definition (floor/truncate of
/// the true rational result), never by re-running the operator under test — per the 1.1 review's
/// "a tautological assert proves nothing" lesson.
/// </summary>
public class FixedBoundaryTests
{
    // ── Multiply: arithmetic >>16 FLOORS toward negative infinity ─────────────────
    //
    // operator* computes (int)(((long)a.Raw * b.Raw) >> 16). An arithmetic right shift floors,
    // so a negative product rounds AWAY from zero — the #1 cross-machine desync source if a port
    // ever swaps it for a truncating shift.

    [Fact]
    public void Multiply_PositiveProduct_FloorsTowardZero()
    {
        // (3/65536) * 0.5 -> true raw = 3*32768/65536 = 1.5 -> floor = 1.
        Assert.Equal(1, (Fixed.FromRaw(3) * Fixed.Half).Raw);
    }

    [Fact]
    public void Multiply_NegativeProduct_FloorsAwayFromZero()
    {
        // (-3/65536) * 0.5 -> true raw = -3*32768/65536 = -1.5 -> floor = -2  (NOT -1).
        // This is the rounding-DIRECTION trap: the naive "truncate toward zero" answer (-1) is wrong.
        Assert.Equal(-2, (Fixed.FromRaw(-3) * Fixed.Half).Raw);
    }

    [Fact]
    public void Multiply_ExactNegativeHalf_IsExact()
    {
        // -3 * 0.5 = -1.5, which is EXACT in 16.16 (raw -98304) -> no rounding occurs either way.
        Assert.Equal(-98304, (Fixed.FromInt(-3) * Fixed.Half).Raw);
    }

    // ── Divide: C# integer division TRUNCATES toward zero ─────────────────────────
    //
    // operator/ computes (int)(((long)a.Raw << 16) / b.Raw). C# long division truncates toward zero,
    // so the SAME -1.5 that multiply floored to -2 here truncates to -1. Pinning both makes the
    // multiply/divide asymmetry explicit — mixing them up silently desyncs lockstep.

    [Fact]
    public void Divide_PositiveResult_TruncatesTowardZero()
    {
        // (3/65536) / 2 -> true raw = (3<<16)/131072 = 196608/131072 = 1.5 -> truncate = 1.
        Assert.Equal(1, (Fixed.FromRaw(3) / Fixed.FromInt(2)).Raw);
    }

    [Fact]
    public void Divide_NegativeResult_TruncatesTowardZero()
    {
        // (-3/65536) / 2 -> true raw = -196608/131072 = -1.5 -> truncate toward zero = -1  (NOT -2).
        // Contrast Multiply_NegativeProduct_FloorsAwayFromZero: identical -1.5, opposite rounding.
        Assert.Equal(-1, (Fixed.FromRaw(-3) / Fixed.FromInt(2)).Raw);
    }

    // ── Overflow at the 16.16 limits: UNCHECKED, wrapping (never throws) ───────────
    //
    // operator+/- are plain int arithmetic with no checked context, so they wrap on overflow rather
    // than throw or saturate. Determinism depends on this being identical everywhere; pin the behavior.

    [Fact]
    public void Overflow_MaxPlusOne_WrapsToNegative()
    {
        Fixed wrapped = Fixed.MaxValue + Fixed.One; // must NOT throw
        // Behavioral: wrap-around makes the result negative (saturation would not).
        Assert.True(wrapped.Raw < 0, "MaxValue + One must wrap to negative (unchecked overflow), not saturate.");
        // Exact pin: int.MaxValue + 65536 wrapped into the int range.
        Assert.Equal(unchecked(int.MaxValue + Fixed.ONE), wrapped.Raw);
    }

    [Fact]
    public void Overflow_MinMinusOne_WrapsToPositive()
    {
        Fixed wrapped = Fixed.MinValue - Fixed.One; // must NOT throw
        Assert.True(wrapped.Raw > 0, "MinValue - One must wrap to positive (unchecked underflow), not saturate.");
        Assert.Equal(unchecked(int.MinValue - Fixed.ONE), wrapped.Raw);
    }

    // ── Sqrt of degenerate / irrational inputs ────────────────────────────────────

    [Fact]
    public void Sqrt_Zero_IsZero()
    {
        Assert.Equal(0, Fixed.Sqrt(Fixed.Zero).Raw);
    }

    [Fact]
    public void Sqrt_Negative_IsZero()
    {
        // The implementation guards a.Raw <= 0 and returns Zero (rather than NaN/throw) — pin that.
        Assert.Equal(0, Fixed.Sqrt(Fixed.FromInt(-4)).Raw);
    }

    [Fact]
    public void Sqrt_NonPerfectSquare_IsDeterministicFloor()
    {
        Fixed root = Fixed.Sqrt(Fixed.FromInt(2));

        // Deterministic pin: the fixed-point Newton iteration settles on raw 92681 for sqrt(2).
        // Independently: true sqrt(2) * 65536 = 92681.9..., whose floor is 92681.
        Assert.Equal(92681, root.Raw);

        // Independent correctness check: squaring the root returns ~2. Squaring a floored 16.16 root
        // loses a few raw units (here exactly 3: 131069 vs 131072), so allow a small deterministic margin.
        Fixed squared = root * root;
        Assert.True(Fixed.Abs(squared - Fixed.FromInt(2)).Raw <= 8,
            $"Sqrt(2)^2 should be ~2; got raw {squared.Raw} (expected near {Fixed.FromInt(2).Raw}).");
    }
}
