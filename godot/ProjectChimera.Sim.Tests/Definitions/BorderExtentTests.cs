#nullable enable
using System.IO;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// Story 15.2 (Route C, DW-160/DW-146/DW-162) — <see cref="ScenarioData.BorderExtent"/> is the PRESENTATION-ONLY
    /// visual border. This pins the I/O matrix that makes it zero-determinism-cost:
    ///   • a legacy JSON with no <c>border_extent</c> key deserialises to 0 (today's behaviour exactly);
    ///   • it is OMITTED byte-for-byte when default, PRESENT + round-trips when set;
    ///   • it is EXCLUDED from <see cref="CanonicalModelHash"/> AND <see cref="StartStateHash"/> — the load-bearing
    ///     determinism guard: a bordered map hashes IDENTICALLY to the same map without the border, with a
    ///     guard-the-guard check proving the hash is NOT vacuously constant;
    ///   • the validator fails closed on <c>map_bounds &gt; 128</c> with a message naming <c>border_extent</c>, while
    ///     <c>map_bounds == 128</c> (with or without a border) stays legal;
    ///   • no <c>AlgoVersion</c> moves.
    /// </summary>
    public class BorderExtentTests
    {
        private static ScenarioData BaseModel() => new()
        {
            Id = "m", DisplayName = "Map",
            MapBounds = 128f, WinCondition = WinCondition.DestroyAllBuildings,
            PlayerSlots = new[]
            {
                new ScenarioPlayerSlot { Slot = 0, FactionJson = "res://a.json", StartOre = 200f, BaseX = -40f, BaseZ = 0f },
                new ScenarioPlayerSlot { Slot = 1, FactionJson = "res://a.json", StartOre = 200f, BaseX =  40f, BaseZ = 0f },
            },
            ResourceNodes = System.Array.Empty<ScenarioResourceNode>(),
            Buildings     = System.Array.Empty<ScenarioBuilding>(),
            Units         = System.Array.Empty<ScenarioUnit>(),
            Triggers      = System.Array.Empty<TriggerDefinition>(),
        };

        private static ScenarioData WithBorder(float border)
        {
            var m = BaseModel();
            m.BorderExtent = border;
            return m;
        }

        // ── Deserialise / serialise ─────────────────────────────────────────────

        [Fact]
        public void LegacyJson_WithoutBorderExtent_DeserialisesToZero()
        {
            // A pre-15.2 scenario file carries no border_extent key; it must load as 0 (today's behaviour).
            const string legacy =
                "{\"id\":\"m\",\"display_name\":\"Map\",\"terrain_ref\":\"\",\"map_bounds\":120.0," +
                "\"win_condition\":\"DestroyAllBuildings\",\"player_slots\":[]," +
                "\"resource_nodes\":[],\"buildings\":[],\"units\":[]}";
            string p = Path.Combine(Path.GetTempPath(), "chimera_border_legacy_" + System.Guid.NewGuid().ToString("N") + ".json");
            try
            {
                File.WriteAllText(p, legacy);
                var loaded = ScenarioSerializer.LoadFromFile(p);
                Assert.NotNull(loaded);
                Assert.Equal(0f, loaded!.BorderExtent);
            }
            finally { if (File.Exists(p)) File.Delete(p); }
        }

        [Fact]
        public void DefaultBorderExtent_KeyIsOmitted()
        {
            // 0 is the type default ⇒ omit-when-default ⇒ byte-identical to a pre-15.2 map (no golden moves).
            string json = ScenarioSerializer.Serialize(BaseModel());
            Assert.DoesNotContain("\"border_extent\"", json);
        }

        [Fact]
        public void SetBorderExtent_KeyIsPresent_AndRoundTrips()
        {
            Assert.Contains("\"border_extent\"", ScenarioSerializer.Serialize(WithBorder(32f)));

            string p = Path.Combine(Path.GetTempPath(), "chimera_border_rt_" + System.Guid.NewGuid().ToString("N") + ".json");
            try
            {
                ScenarioSerializer.SaveToFile(WithBorder(32f), p);
                var loaded = ScenarioSerializer.LoadFromFile(p);
                Assert.NotNull(loaded);
                Assert.Equal(32f, loaded!.BorderExtent);
            }
            finally { if (File.Exists(p)) File.Delete(p); }
        }

        // ── Hash exclusion (presentation-only) — the load-bearing determinism guard ──

        [Fact]
        public void CanonicalModelHash_IdenticalWithAndWithoutBorderExtent()
            => Assert.Equal(CanonicalModelHash.Compute(BaseModel()),
                            CanonicalModelHash.Compute(WithBorder(32f)));

        [Fact]
        public void StartStateHash_IdenticalWithAndWithoutBorderExtent()
        {
            var heroes = new HeroStore();
            Assert.Equal(StartStateHash.Compute(BaseModel(), heroes),
                         StartStateHash.Compute(WithBorder(32f), heroes));
        }

        [Fact]
        public void CanonicalModelHash_IsNotVacuouslyConstant()
        {
            // Guard-the-guard: prove Compute actually discriminates content, so the border-equality above is
            // meaningful and not a hash that ignores everything. A FOLDED field (map_bounds) MUST move the hash.
            var a = BaseModel();
            var b = BaseModel();
            b.MapBounds = 120f; // a real, sim-affecting, folded change (a != b)
            Assert.NotEqual(CanonicalModelHash.Compute(a), CanonicalModelHash.Compute(b));
        }

        // ── Validator: fail-closed above 128, message names border_extent; 128 (± border) stays legal ──

        [Fact]
        public void Validator_RejectsMapBoundsAbove128_WithBorderExtentGuidance()
        {
            var m = ScenarioData.CreateBlank("m", size: MapSize.Large); // valid, map_bounds 128
            m.MapBounds = 160f;                                         // the retired outlier value
            var result = new ScenarioValidator().Validate(m);
            Assert.False(result.Ok);
            Assert.Contains("border_extent", result.Error);
        }

        [Fact]
        public void Validator_AcceptsMapBounds128_WithABorder()
        {
            var m = ScenarioData.CreateBlank("m", size: MapSize.Large); // map_bounds 128
            m.BorderExtent = 32f;                                       // a bordered map is legal
            var result = new ScenarioValidator().Validate(m);
            Assert.True(result.Ok, result.Error);
        }

        [Theory]
        [InlineData(-1f)]                       // negative — would shrink the visual extent below play
        [InlineData(float.NaN)]                 // non-finite
        [InlineData(float.PositiveInfinity)]    // non-finite
        public void Validator_RejectsNegativeOrNonFiniteBorder(float border)
        {
            // border_extent has NO upper cap (visual-only), but a negative / NaN / Inf value is fail-closed.
            var m = ScenarioData.CreateBlank("m", size: MapSize.Large); // valid map_bounds 128
            m.BorderExtent = border;
            var result = new ScenarioValidator().Validate(m);
            Assert.False(result.Ok);
            Assert.Contains("border_extent", result.Error);
        }

        [Fact]
        public void Validator_AllowsArbitrarilyLargeBorder()
        {
            // No upper cap: a map may LOOK far larger than it plays.
            var m = ScenarioData.CreateBlank("m", size: MapSize.Large);
            m.BorderExtent = 100000f;
            Assert.True(new ScenarioValidator().Validate(m).Ok);
        }

        // ── No AlgoVersion moved by this story ──────────────────────────────────

        [Fact]
        public void AlgoVersions_Unchanged()
        {
            Assert.Equal(16, CanonicalModelHash.AlgoVersion);
            Assert.Equal(24, SimChecksum.AlgoVersion);
            Assert.Equal(2, StartStateHash.AlgoVersion);
        }
    }
}
