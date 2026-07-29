#nullable enable
using ProjectChimera.Core; // MatchStats, Faction, Fixed
using Xunit;

namespace ProjectChimera.Sim.Tests.Core
{
    /// <summary>
    /// Story 11.2 (FR-66) — the two NEW observational score counters on <see cref="MatchStats"/>: crystal mined
    /// (the Fixed→int twin of ore mined) and buildings razed (the killer-side twin of unit kills). Both are
    /// deliberately UNFOLDED (see the SimChecksum note) — this file proves the accessor/accumulate/Reset behavior;
    /// the determinism proof that they move no golden lives in <c>ConcedeCommandTests</c>.
    /// </summary>
    public class MatchStatsCountersTests
    {
        [Fact]
        public void CrystalMined_AccumulatesPerFaction_IntTruncated_ZeroWhenNone()
        {
            var s = new MatchStats();
            Assert.Equal(0, s.CrystalMined(Faction.Player1)); // nothing mined yet

            s.RecordCrystalMined(Faction.Player1, Fixed.FromInt(12));
            s.RecordCrystalMined(Faction.Player1, Fixed.FromInt(8));  // accumulates → 20
            s.RecordCrystalMined(Faction.Player2, Fixed.FromInt(5));  // isolated per faction

            Assert.Equal(20, s.CrystalMined(Faction.Player1));
            Assert.Equal(5,  s.CrystalMined(Faction.Player2));
            Assert.Equal(0,  s.CrystalMined(Faction.Player3));

            // Neutral (slot 0) is ignored — mirrors RecordOreMined's f > 0 guard.
            s.RecordCrystalMined(Faction.Neutral, Fixed.FromInt(99));
            Assert.Equal(0, s.CrystalMined(Faction.Neutral));
        }

        [Fact]
        public void BuildingsRazed_CreditsRazer_ZeroWhenNoneOrNeutral()
        {
            var s = new MatchStats();
            Assert.Equal(0, s.BuildingsRazed(Faction.Player1));

            s.RecordBuildingRazed(Faction.Player1);
            s.RecordBuildingRazed(Faction.Player1); // P1 razed 2
            s.RecordBuildingRazed(Faction.Player2); // P2 razed 1
            s.RecordBuildingRazed(Faction.Neutral); // razed by nobody → counts for nobody

            Assert.Equal(2, s.BuildingsRazed(Faction.Player1));
            Assert.Equal(1, s.BuildingsRazed(Faction.Player2));
            Assert.Equal(0, s.BuildingsRazed(Faction.Player3));
            Assert.Equal(0, s.BuildingsRazed(Faction.Neutral));
        }

        [Fact]
        public void Reset_ClearsBothNewCounters()
        {
            var s = new MatchStats();
            s.RecordCrystalMined(Faction.Player1, Fixed.FromInt(40));
            s.RecordBuildingRazed(Faction.Player2);

            s.Reset();

            Assert.Equal(0, s.CrystalMined(Faction.Player1));
            Assert.Equal(0, s.BuildingsRazed(Faction.Player2));
        }
    }
}
