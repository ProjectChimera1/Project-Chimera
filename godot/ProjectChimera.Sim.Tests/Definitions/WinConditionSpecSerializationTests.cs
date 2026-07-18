#nullable enable
using System;
using System.IO;
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// Story 7.11 (P5) — the <see cref="ScenarioSerializer"/> chokepoint discipline for the win-condition preset spec:
    /// a <see cref="WinPresetKind.None"/> spec emits NO <c>win_condition_spec</c> key and round-trips to a null spec
    /// (byte-identical to pre-7.11); an active preset round-trips its own params; and a preset carrying a STALE
    /// cross-preset param (the editor reuses one spec across preset switches) serializes WITHOUT that foreign key, so
    /// two semantically-identical scenarios serialize byte-identically — restoring the "same serialize ⇄ same hash"
    /// discipline that <c>CanonicalModelHash</c> (which folds only the active preset's params) relies on.
    /// </summary>
    public class WinConditionSpecSerializationTests
    {
        private static ScenarioData Base() => ScenarioData.CreateBlank("wincon-serialize");

        private static ScenarioData RoundTrip(ScenarioData s)
        {
            string path = Path.Combine(Path.GetTempPath(), $"chimera-7-11-{Guid.NewGuid():N}.json");
            try
            {
                ScenarioSerializer.SaveToFile(s, path);
                return ScenarioSerializer.LoadFromFile(path)!;
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void NonePreset_OmitsKey_And_DeserializesToNull()
        {
            var s = Base();
            s.WinConditionSpec = new WinConditionSpec { Preset = WinPresetKind.None };
            string json = ScenarioSerializer.Serialize(s);
            Assert.DoesNotContain("win_condition_spec", json);
            Assert.NotNull(s.WinConditionSpec); // Serialize never mutates the caller's model

            ScenarioData back = RoundTrip(s);
            Assert.Null(back.WinConditionSpec);
        }

        [Fact]
        public void KotH_RoundTrips_ItsParams()
        {
            var s = Base();
            s.Regions = new[] { new ScenarioRegion { Id = "zone", Name = "Zone", MinX = -5, MinZ = -5, MaxX = 5, MaxZ = 5 } };
            s.WinConditionSpec = new WinConditionSpec { Preset = WinPresetKind.KingOfTheHill, RegionId = "zone", HoldTicks = 300 };

            ScenarioData back = RoundTrip(s);
            Assert.NotNull(back.WinConditionSpec);
            Assert.Equal(WinPresetKind.KingOfTheHill, back.WinConditionSpec!.Preset);
            Assert.Equal("zone", back.WinConditionSpec.RegionId);
            Assert.Equal(300, back.WinConditionSpec.HoldTicks);
        }

        [Fact]
        public void Survival_RoundTrips_ItsParams()
        {
            var s = Base();
            s.WinConditionSpec = new WinConditionSpec { Preset = WinPresetKind.TimedSurvival, FactionSlot = 0, SurviveTicks = 900 };

            ScenarioData back = RoundTrip(s);
            Assert.NotNull(back.WinConditionSpec);
            Assert.Equal(WinPresetKind.TimedSurvival, back.WinConditionSpec!.Preset);
            Assert.Equal(0, back.WinConditionSpec.FactionSlot);
            Assert.Equal(900, back.WinConditionSpec.SurviveTicks);
        }

        [Fact]
        public void KotH_WithStrayLeaderIndex_OmitsForeignKey_And_IsByteIdenticalToCleanSpec()
        {
            var clean = Base();
            clean.WinConditionSpec = new WinConditionSpec { Preset = WinPresetKind.KingOfTheHill, RegionId = "zone", HoldTicks = 300 };

            var stray = Base();
            // The editor reused one spec: it carries a leftover Assassination param the active KotH preset does not own.
            stray.WinConditionSpec = new WinConditionSpec
            {
                Preset = WinPresetKind.KingOfTheHill, RegionId = "zone", HoldTicks = 300,
                LeaderUnitIndex = 4, StructureIndex = 7, FactionSlot = 2, SurviveTicks = 999,
            };

            string cleanJson = ScenarioSerializer.Serialize(clean);
            string strayJson = ScenarioSerializer.Serialize(stray);

            Assert.DoesNotContain("leader_unit_index", strayJson);
            Assert.DoesNotContain("structure_index",   strayJson);
            Assert.DoesNotContain("faction_slot",       strayJson);
            Assert.DoesNotContain("survive_ticks",      strayJson);
            Assert.Equal(cleanJson, strayJson); // byte-identical: the stale params never reach the wire

            // And the live in-editor spec is untouched — the normalization happened on the serialization copy only.
            Assert.Equal(4, stray.WinConditionSpec!.LeaderUnitIndex);
        }
    }
}
