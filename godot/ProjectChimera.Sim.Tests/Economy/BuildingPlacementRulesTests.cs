#nullable enable
using System.Text.Json;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Economy;
using ProjectChimera.Navigation;
using Xunit;

namespace ProjectChimera.Sim.Tests.Economy
{
    /// <summary>
    /// DW-941 (the scenario-authorable minimum building gap) + DW-942's SIM half (blocked-terrain placement
    /// refusal — the fog half is per-viewer presentation state and deliberately has no sim test). Pins: the
    /// default gap is the WC3 grid feel (adjacent-but-not-touching refused, one seam apart accepted); gap 0
    /// permits footprint chaining while still refusing true overlap; a blocked pathability cell under the
    /// footprint refuses; the resolver is total (null/NaN → default, clamp into [0, 32]) and single-sourced with
    /// <see cref="BuildingSystem.DEFAULT_BUILDING_GAP"/>; and the ScenarioData field is omit-when-null (existing
    /// scenarios serialize byte-identically) and round-trips when authored. Godot-free, <see cref="Fixed"/>-only.
    /// </summary>
    public class BuildingPlacementRulesTests
    {
        private static FixedVec3 V(float x, float z) =>
            new FixedVec3(Fixed.FromFloat(x), Fixed.Zero, Fixed.FromFloat(z));

        private static FactionDefinition BuilderFaction()
        {
            var f = new FactionDefinition { Id = "dw941", DisplayName = "DW941" };
            f.Buildings.Add(new BuildingDefinition
            {
                Id = "barracks", Category = "Structure", CostOre = 100,
                ConstructionTime = 10f, SupplyBonus = 0, ProducesCategory = "Melee", Hp = 100f,
                NavFootprint = new float[] { 5f, 3f, 5f },
            });
            return f;
        }

        private static (EntityWorld world, BuildingStore buildings, BuildingSystem sys) NewHarness()
        {
            var world     = new EntityWorld();
            var buildings = new BuildingStore();
            var resources = new ResourceStore(Fixed.Zero);
            var sys       = new BuildingSystem(buildings, resources, BuilderFaction());
            return (world, buildings, sys);
        }

        // ── DW-941: the gap ─────────────────────────────────────────────────────

        [Fact]
        public void DefaultGap_RefusesAdjacentButNotTouching_AcceptsOneSeamApart()
        {
            // 5×5 footprints → HALF-extents 2.5, half-extent sum 5. With the default 1.0u gap the refusal band is
            // dx < 6: centres 5.5u apart (0.5u of clear ground — inside the 1u seam) refuses; 6.5u apart (1.5u of
            // clear ground) accepts. This IS the WC3 grid feel: adjacent placements always leave a pathable seam.
            var (world, buildings, sys) = NewHarness();
            Assert.True(sys.PlaceBuildingDirect(BuildingType.Barracks, Faction.Player1, V(0, 0), preBuilt: true) >= 0);

            Assert.False(sys.CanPlaceAt(BuildingType.Barracks, Faction.Player1, V(5.5f, 0), world));
            Assert.True (sys.CanPlaceAt(BuildingType.Barracks, Faction.Player1, V(6.5f, 0), world));
        }

        [Fact]
        public void ZeroGap_PermitsChaining_StillRefusesTrueOverlap()
        {
            // The walling arm: gap 0 → the refusal band is bare footprint overlap (dx < 5). Centres 5.5u apart
            // (footprints clear by 0.5u) is now placeable — buildings can chain into walls; 4u apart (footprints
            // genuinely overlap) still refuses.
            var (world, buildings, sys) = NewHarness();
            sys.MinBuildingGap = Fixed.Zero;
            Assert.True(sys.PlaceBuildingDirect(BuildingType.Barracks, Faction.Player1, V(0, 0), preBuilt: true) >= 0);

            Assert.True (sys.CanPlaceAt(BuildingType.Barracks, Faction.Player1, V(5.5f, 0), world));
            Assert.False(sys.CanPlaceAt(BuildingType.Barracks, Faction.Player1, V(4, 0), world));
        }

        // ── DW-942 (sim half): blocked terrain refuses ──────────────────────────

        [Fact]
        public void BlockedCellUnderTheFootprint_RefusesPlacement()
        {
            var (world, buildings, sys) = NewHarness();

            // Block exactly the cell under the candidate centre (the same WorldToCell mapping CanPlaceAt scans).
            var mask = new bool[PathabilityGrid.CELL_COUNT];
            FlowField.WorldToCell(Fixed.FromInt(20), Fixed.FromInt(20), out int col, out int row);
            mask[row * PathabilityGrid.GRID_SIZE + col] = true;
            world.SetPathabilityGrid(new PathabilityGrid(mask));

            Assert.False(sys.CanPlaceAt(BuildingType.Barracks, Faction.Player1, V(20, 20), world));
            Assert.True (sys.CanPlaceAt(BuildingType.Barracks, Faction.Player1, V(60, 60), world)); // clear ground
        }

        // ── DW-941: the resolver is total and single-sourced ────────────────────

        [Theory]
        [InlineData(null,             1.0f)] // omitted → the WC3-grid default
        [InlineData(0f,               0f)]   // authored 0 → chaining allowed, preserved verbatim
        [InlineData(2.5f,             2.5f)] // in-band → verbatim
        [InlineData(50f,              32f)]  // above the band → clamped (shadow-mode reachable only; validator rejects)
        [InlineData(-3f,              0f)]   // below the band → clamped
        [InlineData(float.NaN,        1.0f)] // non-finite → default
        public void ResolveBuildingMinGap_IsTotal(float? authored, float expected)
        {
            Assert.Equal(expected, ScenarioData.ResolveBuildingMinGap(authored));
        }

        [Fact]
        public void DefaultGapConstant_IsSingleSourcedFromTheResolver()
        {
            Assert.Equal(Fixed.FromFloat(ScenarioData.ResolveBuildingMinGap(null)).Raw,
                         BuildingSystem.DEFAULT_BUILDING_GAP.Raw);
        }

        // ── DW-941: serialization — omit-when-null, round-trip when authored ────

        // ── DW-942: the placement fog rule (creator option) ─────────────────────

        [Theory]
        [InlineData(null,        "explored")] // omitted → the WC3 default
        [InlineData("explored",  "explored")]
        [InlineData("visible",   "visible")]
        [InlineData("anywhere",  "anywhere")]
        [InlineData(" Visible ", "visible")]  // normalized (trim + case)
        [InlineData("garbage",   "explored")] // unknown → default (shadow-mode only; the validator fails it closed)
        public void ResolvePlacementFogRule_IsTotal(string? authored, string expected)
        {
            Assert.Equal(expected, ScenarioData.ResolvePlacementFogRule(authored));
        }

        [Fact]
        public void PlacementFogRule_OmittedWhenNull_RoundTripsWhenAuthored()
        {
            var opts = new JsonSerializerOptions { WriteIndented = true };
            Assert.DoesNotContain("placement_fog_rule", JsonSerializer.Serialize(new ScenarioData(), opts));

            string authored = JsonSerializer.Serialize(new ScenarioData { PlacementFogRule = "anywhere" }, opts);
            Assert.Contains("\"placement_fog_rule\"", authored);
            Assert.Equal("anywhere", JsonSerializer.Deserialize<ScenarioData>(authored, opts)!.PlacementFogRule);
        }

        [Fact]
        public void PlacementFogRule_DoesNotMoveTheCanonicalHash()
        {
            // The Regions-class exclusion, pinned: the rule gates order ISSUE on the local client only (the sim
            // never reads fog), so two scenarios differing only in it MUST hash identically — folding it would
            // false-reject lobbies over a value that cannot desync.
            var a = new ScenarioData { MapBounds = 120f };
            var b = new ScenarioData { MapBounds = 120f, PlacementFogRule = "anywhere" };
            Assert.Equal(CanonicalModelHash.Compute(a), CanonicalModelHash.Compute(b));
        }

        [Fact]
        public void BuildingMinGap_OmittedWhenNull_RoundTripsWhenAuthored()
        {
            var opts = new JsonSerializerOptions { WriteIndented = true };

            string bare = JsonSerializer.Serialize(new ScenarioData(), opts);
            Assert.DoesNotContain("building_min_gap", bare); // existing scenarios serialize byte-identically

            string authored = JsonSerializer.Serialize(new ScenarioData { BuildingMinGap = 2.5f }, opts);
            Assert.Contains("\"building_min_gap\"", authored);
            var reloaded = JsonSerializer.Deserialize<ScenarioData>(authored, opts);
            Assert.Equal(2.5f, reloaded!.BuildingMinGap);
        }
    }
}
