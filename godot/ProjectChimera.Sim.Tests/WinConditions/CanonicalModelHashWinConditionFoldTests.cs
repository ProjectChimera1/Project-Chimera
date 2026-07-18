#nullable enable
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.WinConditions
{
    /// <summary>
    /// Story 7.11 (P5 / CanonicalModelHash v12) — the SENSITIVITY teeth for the win-condition PRESET fold appended
    /// after the built-in <see cref="ScenarioData.WinCondition"/> enum (<see cref="CanonicalModelHash.MixWinConditionSpec"/>).
    /// Every preset dimension that <c>MixWinConditionSpec</c> folds — the preset KIND and each active-preset param —
    /// MUST move the canonical hash; a <see cref="WinPresetKind.None"/> spec MUST hash identically to a null spec (the
    /// omit-when-default discipline the serializer enforces); and a preset spec must differ from the same scenario
    /// carrying only the built-in enum (proving BOTH the enum AND the preset fold, not one masking the other).
    /// </summary>
    public class CanonicalModelHashWinConditionFoldTests
    {
        private static ScenarioData BaseModel() => new ScenarioData
        {
            Id = "m", DisplayName = "M", TerrainRef = "", MapBounds = 120f,
            WinCondition = WinCondition.DestroyAllBuildings,
            PlayerSlots = new[] { new ScenarioPlayerSlot { Slot = 0, FactionJson = "res://a.json" } },
        };

        private static ScenarioData With(WinConditionSpec spec)
        {
            var m = BaseModel();
            m.WinConditionSpec = spec;
            return m;
        }

        [Fact]
        public void AlgoVersion_Pinned_At12() => Assert.Equal(12, CanonicalModelHash.AlgoVersion);

        [Fact]
        public void NoneSpec_And_NullSpec_HashIdentically()
        {
            var nul = BaseModel();                                         // WinConditionSpec == null
            var non = With(new WinConditionSpec { Preset = WinPresetKind.None });
            Assert.Equal(CanonicalModelHash.Compute(nul), CanonicalModelHash.Compute(non));
        }

        [Fact]
        public void PresetKind_MovesTheHash()
        {
            // Four presets + None must produce five distinct hashes (the kind NAME folds).
            ulong koth  = CanonicalModelHash.Compute(With(new WinConditionSpec { Preset = WinPresetKind.KingOfTheHill,       RegionId = "zone", HoldTicks = 300 }));
            ulong surv  = CanonicalModelHash.Compute(With(new WinConditionSpec { Preset = WinPresetKind.TimedSurvival,       FactionSlot = 0, SurviveTicks = 900 }));
            ulong assn  = CanonicalModelHash.Compute(With(new WinConditionSpec { Preset = WinPresetKind.Assassination,       LeaderUnitIndex = 0 }));
            ulong land  = CanonicalModelHash.Compute(With(new WinConditionSpec { Preset = WinPresetKind.LandmarkDestruction, StructureIndex = 0 }));
            ulong none  = CanonicalModelHash.Compute(BaseModel());

            var all = new[] { koth, surv, assn, land, none };
            for (int i = 0; i < all.Length; i++)
                for (int j = i + 1; j < all.Length; j++)
                    Assert.NotEqual(all[i], all[j]);
        }

        [Fact]
        public void KotH_EachParam_MovesTheHash()
        {
            ulong baseline = CanonicalModelHash.Compute(With(new WinConditionSpec { Preset = WinPresetKind.KingOfTheHill, RegionId = "zone", HoldTicks = 300 }));
            ulong region   = CanonicalModelHash.Compute(With(new WinConditionSpec { Preset = WinPresetKind.KingOfTheHill, RegionId = "other", HoldTicks = 300 }));
            ulong hold     = CanonicalModelHash.Compute(With(new WinConditionSpec { Preset = WinPresetKind.KingOfTheHill, RegionId = "zone", HoldTicks = 301 }));
            Assert.NotEqual(baseline, region);
            Assert.NotEqual(baseline, hold);
        }

        [Fact]
        public void Survival_EachParam_MovesTheHash()
        {
            ulong baseline = CanonicalModelHash.Compute(With(new WinConditionSpec { Preset = WinPresetKind.TimedSurvival, FactionSlot = 0, SurviveTicks = 900 }));
            ulong slot     = CanonicalModelHash.Compute(With(new WinConditionSpec { Preset = WinPresetKind.TimedSurvival, FactionSlot = 1, SurviveTicks = 900 }));
            ulong ticks    = CanonicalModelHash.Compute(With(new WinConditionSpec { Preset = WinPresetKind.TimedSurvival, FactionSlot = 0, SurviveTicks = 901 }));
            Assert.NotEqual(baseline, slot);
            Assert.NotEqual(baseline, ticks);
        }

        [Fact]
        public void Assassination_LeaderIndex_MovesTheHash()
        {
            ulong baseline = CanonicalModelHash.Compute(With(new WinConditionSpec { Preset = WinPresetKind.Assassination, LeaderUnitIndex = 0 }));
            ulong idx      = CanonicalModelHash.Compute(With(new WinConditionSpec { Preset = WinPresetKind.Assassination, LeaderUnitIndex = 1 }));
            Assert.NotEqual(baseline, idx);
        }

        [Fact]
        public void Landmark_StructureIndex_MovesTheHash()
        {
            ulong baseline = CanonicalModelHash.Compute(With(new WinConditionSpec { Preset = WinPresetKind.LandmarkDestruction, StructureIndex = 0 }));
            ulong idx      = CanonicalModelHash.Compute(With(new WinConditionSpec { Preset = WinPresetKind.LandmarkDestruction, StructureIndex = 1 }));
            Assert.NotEqual(baseline, idx);
        }

        [Fact]
        public void BuiltinEnum_And_Preset_BothFold_Independently()
        {
            // Same preset, different built-in enum → hashes differ (the enum still folds beneath the preset).
            var a = With(new WinConditionSpec { Preset = WinPresetKind.Assassination, LeaderUnitIndex = 0 });
            a.WinCondition = WinCondition.DestroyAllBuildings;
            var b = With(new WinConditionSpec { Preset = WinPresetKind.Assassination, LeaderUnitIndex = 0 });
            b.WinCondition = WinCondition.EliminateAllUnits;
            Assert.NotEqual(CanonicalModelHash.Compute(a), CanonicalModelHash.Compute(b));

            // Same built-in enum, preset present vs absent → hashes differ (the preset folds atop the enum).
            var withPreset = With(new WinConditionSpec { Preset = WinPresetKind.Assassination, LeaderUnitIndex = 0 });
            var enumOnly   = BaseModel(); // same WinCondition, no preset
            Assert.NotEqual(CanonicalModelHash.Compute(withPreset), CanonicalModelHash.Compute(enumOnly));
        }
    }
}
