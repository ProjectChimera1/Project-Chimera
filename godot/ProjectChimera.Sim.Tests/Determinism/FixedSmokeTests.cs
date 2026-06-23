using ProjectChimera.Core;
using Xunit;

namespace ProjectChimera.Sim.Tests.Determinism;

public class FixedSmokeTests
{
    [Fact]
    public void IntRoundTrip()
    {
        Assert.Equal(7, Fixed.FromInt(7).ToInt());
        Assert.Equal(-3, Fixed.FromInt(-3).ToInt());
    }

    [Fact]
    public void MultiplyAndDivide()
    {
        Assert.Equal(12, (Fixed.FromInt(3) * Fixed.FromInt(4)).ToInt());
        Assert.Equal(Fixed.Half, Fixed.One / Fixed.FromInt(2)); // 1 / 2 == Half
    }

    [Fact]
    public void RawRoundTrip()
    {
        // FromInt(1) must yield the ONE bit pattern (1 << 16) — verifies the integer→raw shift.
        Assert.Equal(Fixed.ONE, Fixed.FromInt(1).Raw);

        // An arbitrary fractional raw (1.5 in 16.16 == 0x18000) must survive FromRaw → Raw unchanged.
        const int raw = 0x18000;
        Assert.Equal(raw, Fixed.FromRaw(raw).Raw);
    }

    [Fact]
    public void SqrtIsExactForPerfectSquares()
    {
        Assert.Equal(4, Fixed.Sqrt(Fixed.FromInt(16)).ToInt());
    }

    [Fact]
    public void Vec3Add()
    {
        var v = FixedVec3.One + FixedVec3.One;
        Assert.Equal(new FixedVec3(Fixed.FromInt(2), Fixed.FromInt(2), Fixed.FromInt(2)), v);
    }
}
