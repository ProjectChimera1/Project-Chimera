#nullable enable
using ProjectChimera.Core;           // Fixed
using ProjectChimera.Core.Bootstrap; // ScenarioDelegateMath
using Xunit;

namespace ProjectChimera.Sim.Tests.Bootstrap
{
    /// <summary>
    /// DW-333 — the Godot-free arithmetic behind <c>ScenarioDelegateBinder</c>'s trigger delegates
    /// (<see cref="ScenarioDelegateMath"/>): the multi-unit <c>spawn_unit</c> fan-out coordinate
    /// (<c>x + i·2.5</c>, Fixed-only — it feeds <c>ScenarioApplier.SpawnUnitAt</c> and therefore sim truth /
    /// SimChecksum) and the <c>display_message</c> Fixed→float presentation conversion. Before this extraction the
    /// binder needed a Godot <c>SceneContext</c>, so a wrong <c>SpawnLateralOffset.Raw</c> (163840 ≠ 2.5) or broken
    /// accumulation would have shipped with no failing test (<c>ScenarioDirectorSpawnActionTests</c> captures
    /// <c>OnSpawnUnit</c> and bypasses the binder arithmetic entirely).
    /// </summary>
    public class ScenarioDelegateMathTests
    {
        [Fact]
        public void SpawnLateralOffset_IsExactly2Point5_InSixteenSixteenFixed()
        {
            // The determinism-relevant constant: 2.5 world units = 163840 raw in 16.16.
            Assert.Equal(163840, ScenarioDelegateMath.SpawnLateralOffset.Raw);

            // Cross-check without repeating the magic raw value: 2 × 2.5 == 5, exactly, in Fixed.
            Assert.Equal(Fixed.FromInt(5).Raw,
                (ScenarioDelegateMath.SpawnLateralOffset * Fixed.FromInt(2)).Raw);
        }

        [Fact]
        public void FanOutX_IndexZero_SpawnsAtTheAuthoredAnchor()
        {
            Assert.Equal(Fixed.FromInt(37).Raw,  ScenarioDelegateMath.FanOutX(Fixed.FromInt(37), 0).Raw);
            Assert.Equal(Fixed.FromInt(-12).Raw, ScenarioDelegateMath.FanOutX(Fixed.FromInt(-12), 0).Raw);
            Assert.Equal(0, ScenarioDelegateMath.FanOutX(Fixed.Zero, 0).Raw);
        }

        [Fact]
        public void FanOutX_AccumulatesExactly2Point5PerUnit()
        {
            Fixed x = Fixed.FromInt(10);

            Assert.Equal(Fixed.FromInt(10).Raw, ScenarioDelegateMath.FanOutX(x, 0).Raw);
            Assert.Equal(819200,                ScenarioDelegateMath.FanOutX(x, 1).Raw); // 12.5 = 819200 raw
            Assert.Equal(Fixed.FromInt(15).Raw, ScenarioDelegateMath.FanOutX(x, 2).Raw);
            Assert.Equal(1146880,               ScenarioDelegateMath.FanOutX(x, 3).Raw); // 17.5 = 1146880 raw
            Assert.Equal(Fixed.FromInt(20).Raw, ScenarioDelegateMath.FanOutX(x, 4).Raw);
        }

        [Fact]
        public void FanOutX_SuccessiveUnits_AreExactlyOneOffsetApart()
        {
            // The fan-out is a pure arithmetic progression: every adjacent pair differs by EXACTLY the lateral
            // offset (no drift, no rounding residue) — the property a broken accumulation would violate.
            Fixed x = Fixed.FromRaw(123456); // a non-integer anchor exercises fractional raw accumulation
            for (int i = 0; i < 10; i++)
            {
                Fixed step = ScenarioDelegateMath.FanOutX(x, i + 1) - ScenarioDelegateMath.FanOutX(x, i);
                Assert.Equal(ScenarioDelegateMath.SpawnLateralOffset.Raw, step.Raw);
            }
        }

        [Fact]
        public void FanOutX_NegativeAnchor_StaysExactInFixed()
        {
            // -4 + 2·2.5 = 1, exactly — Fixed arithmetic across the sign boundary loses nothing.
            Assert.Equal(Fixed.FromInt(1).Raw, ScenarioDelegateMath.FanOutX(Fixed.FromInt(-4), 2).Raw);
        }

        [Fact]
        public void ToastDurationSeconds_ConvertsExactlyAtThePresentationBoundary()
        {
            // The display_message duration crosses Fixed→float ONLY here (never in the tick). 16.16 values with
            // power-of-two fractions are exactly representable in float, so these are exact equalities.
            Assert.Equal(4.0f, ScenarioDelegateMath.ToastDurationSeconds(Fixed.FromInt(4)));
            Assert.Equal(2.5f, ScenarioDelegateMath.ToastDurationSeconds(ScenarioDelegateMath.SpawnLateralOffset));
            Assert.Equal(0.0f, ScenarioDelegateMath.ToastDurationSeconds(Fixed.Zero));
            Assert.Equal(-1.5f, ScenarioDelegateMath.ToastDurationSeconds(Fixed.FromRaw(-98304))); // -1.5 = -98304 raw
        }
    }
}
