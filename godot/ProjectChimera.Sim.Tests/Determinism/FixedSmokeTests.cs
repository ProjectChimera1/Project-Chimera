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
        Assert.Equal(Fixed.ONE, Fixed.FromRaw(Fixed.One.Raw).Raw);
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
