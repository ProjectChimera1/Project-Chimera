#nullable enable
using System.Collections.Generic;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Sim;
using ProjectChimera.Navigation;
using ProjectChimera.Sim.Tests.Golden;   // GoldenApplierScenario.BuildFaction
using Xunit;

namespace ProjectChimera.Sim.Tests.Validation
{
    /// <summary>
    /// DW-148 (guard half) — the LOAD-TIME spawn-in-blocked-cell guard against the RESOLVED union pathability grid.
    ///
    /// <para><see cref="ScenarioValidator.Validate"/> (the Godot-free pre-tick gate) can only decode the AUTHORED
    /// blocked layers: slope-DERIVED cells depend on the terrain heightmap, which does not exist there. So a start base
    /// / resource node / building / pre-placed unit / <c>spawn_unit</c> trigger sitting on a slope-auto-blocked cell
    /// passed validation and shipped un-caught. <see cref="ScenarioValidator.CheckSpawnsNotBlocked"/> re-runs the same
    /// checks against the grid <see cref="ScenarioApplier.BuildPathabilityGrid"/> resolves at load, and
    /// <see cref="ScenarioApplier.Apply"/> (the one Godot-free chokepoint every lifecycle path funnels through)
    /// surfaces the located error.</para>
    ///
    /// <para>Fixture: the cliff-at-X=0 elevation grid (identical to SlopeAutoBlockTests / PathabilityReapplyRebuildTests)
    /// makes the two flow columns straddling the cliff — 63 and 64, world X ∈ [-2, 2) — slope-blocked, and nothing
    /// else (DW-149 widened the derivation from the low side only to both sides). World (-1, 0) is inside column 63;
    /// every other placement below sits well clear of both.</para>
    /// </summary>
    public class ResolvedGridSpawnGuardTests
    {
        /// <summary>World XZ inside the slope-derived blocked cell (flow cell col 63, row 64).</summary>
        private const float SlopeBlockedX = -1f, SlopeBlockedZ = 0f;

        private static readonly ScenarioValidator Validator = new();

        /// <summary>A 256×256 / ±128 elevation grid with a hard cliff at world X=0 (0 west, 10 east) — mirrors the real
        /// Godot elevation grid layout (identical to SlopeAutoBlockTests.CliffAtX0).</summary>
        private static ElevationGrid CliffAtX0()
        {
            const int N = 256;
            var heights = new Fixed[N * N];
            for (int row = 0; row < N; row++)
                for (int col = 0; col < N; col++)
                    heights[row * N + col] = col >= 128 ? Fixed.FromInt(10) : Fixed.Zero;
            return new ElevationGrid(heights, N, N, Fixed.FromInt(-128), Fixed.FromInt(-128), Fixed.One);
        }

        /// <summary>A minimal VALID slope-auto-block map: one slot based far from the cliff, nothing painted.</summary>
        private static ScenarioData SlopeMap() => new ScenarioData
        {
            Id = "slope_map", DisplayName = "Slope Map", TerrainRef = "", MapBounds = 120f,
            SlopeAutoBlock = true, SlopeBlockThreshold = 1f,
            PlayerSlots = new[]
            {
                new ScenarioPlayerSlot
                {
                    Slot = 0, FactionJson = "res://resources/data/factions/alpha_faction.json",
                    StartOre = 200f, BaseX = -45f, BaseZ = 0f,
                },
            },
        };

        private static PathabilityGrid Resolved(ScenarioData m)
        {
            PathabilityGrid? g = ScenarioApplier.BuildPathabilityGrid(m, CliffAtX0());
            Assert.NotNull(g);          // the cliff derives blocked cells — the fixture must actually block something
            Assert.True(g!.IsBlocked(Fixed.FromFloat(SlopeBlockedX), Fixed.FromFloat(SlopeBlockedZ)),
                "fixture broken: world (-1, 0) must be slope-derived blocked.");
            return g;
        }

        // ── The gap itself: Validate cannot see slope-derived cells; the guard can ────────────────────────────────

        [Fact]
        public void SlopeDerivedBlockedUnit_PassesValidate_ButTheGuardCatchesIt_Located()
        {
            var m = SlopeMap();
            m.Units = new[] { new ScenarioUnit { UnitId = "worker", Slot = 0, X = SlopeBlockedX, Z = SlopeBlockedZ } };

            // The pre-tick gate accepts it — the heightmap (and therefore the derived cell) is invisible there.
            Assert.True(Validator.Validate(m).Ok);

            string? err = ScenarioValidator.CheckSpawnsNotBlocked(m, Resolved(m));
            Assert.NotNull(err);
            Assert.Contains("units[0]", err!);
            Assert.Contains("blocked", err!);
            Assert.Contains("SLOPE-DERIVED", err!); // the message tells the author WHY a clean paint layer still blocks
        }

        [Fact]
        public void SlopeDerivedBlockedStartBase_IsCaught_Located()
        {
            var m = SlopeMap();
            m.PlayerSlots[0].BaseX = SlopeBlockedX;
            m.PlayerSlots[0].BaseZ = SlopeBlockedZ;
            Assert.True(Validator.Validate(m).Ok);

            string? err = ScenarioValidator.CheckSpawnsNotBlocked(m, Resolved(m));
            Assert.NotNull(err);
            Assert.Contains("player_slots[0]", err!);
        }

        [Fact]
        public void SlopeDerivedBlockedResourceNode_IsCaught_Located()
        {
            var m = SlopeMap();
            m.ResourceNodes = new[]
            {
                new ScenarioResourceNode { X = 10f, Z = 10f, Supply = 400f, Rate = 5f, MaxGatherers = 4 },
                new ScenarioResourceNode { X = SlopeBlockedX, Z = SlopeBlockedZ, Supply = 400f, Rate = 5f, MaxGatherers = 4 },
            };
            Assert.True(Validator.Validate(m).Ok);

            string? err = ScenarioValidator.CheckSpawnsNotBlocked(m, Resolved(m));
            Assert.NotNull(err);
            Assert.Contains("resource_nodes[1]", err!); // the SECOND node — a located index, not a hardcoded 0
        }

        [Fact]
        public void SlopeDerivedBlockedBuilding_IsCaught_Located()
        {
            var m = SlopeMap();
            m.Buildings = new[] { new ScenarioBuilding { Type = "CommandCenter", Slot = 0, X = SlopeBlockedX, Z = SlopeBlockedZ, PreBuilt = true } };
            Assert.True(Validator.Validate(m).Ok);

            string? err = ScenarioValidator.CheckSpawnsNotBlocked(m, Resolved(m));
            Assert.NotNull(err);
            Assert.Contains("buildings[0]", err!);
        }

        [Fact]
        public void SlopeDerivedBlockedSpawnUnitTrigger_IsCaught_Located()
        {
            var m = SlopeMap();
            m.Triggers = new[]
            {
                new TriggerDefinition
                {
                    Name = "t",
                    Events  = new[] { new TriggerEvent { Type = "match_start" } },
                    Actions = new[]
                    {
                        new TriggerAction
                        {
                            Type = "spawn_unit", UnitId = "worker", Faction = 0, Count = 1,
                            X = Fixed.FromFloat(SlopeBlockedX), Z = Fixed.FromFloat(SlopeBlockedZ),
                        },
                    },
                },
            };
            Assert.True(Validator.Validate(m).Ok);

            string? err = ScenarioValidator.CheckSpawnsNotBlocked(m, Resolved(m));
            Assert.NotNull(err);
            Assert.Contains("triggers[0].actions[0]", err!);
        }

        // ── No-op / no-false-positive paths (the flat & legacy common case must be untouched) ─────────────────────

        [Fact]
        public void ClearPlacements_UnderASlopeGrid_ReportNothing()
        {
            var m = SlopeMap();
            m.Units = new[] { new ScenarioUnit { UnitId = "worker", Slot = 0, X = -42f, Z = -3f } };
            m.Buildings = new[] { new ScenarioBuilding { Type = "CommandCenter", Slot = 0, X = -45f, Z = 0f, PreBuilt = true } };
            Assert.Null(ScenarioValidator.CheckSpawnsNotBlocked(m, Resolved(m)));
        }

        [Fact]
        public void NullOrAllClearGrid_IsAnExactNoOp()
        {
            var m = SlopeMap();
            m.Units = new[] { new ScenarioUnit { UnitId = "worker", Slot = 0, X = SlopeBlockedX, Z = SlopeBlockedZ } };

            Assert.Null(ScenarioValidator.CheckSpawnsNotBlocked(m, null));                                   // no grid at all
            Assert.Null(ScenarioValidator.CheckSpawnsNotBlocked(m, PathabilityGrid.Empty));                  // all-clear grid
            Assert.Null(ScenarioValidator.CheckSpawnsNotBlocked(m, ScenarioApplier.BuildPathabilityGrid(m, null))); // no heightmap ⇒ null grid
            Assert.Null(ScenarioValidator.CheckSpawnsNotBlocked(null, PathabilityGrid.Empty));               // no model
        }

        [Fact]
        public void NullCollectionElements_AreSkipped_NeverThrow()
        {
            // Validate() locates null elements; this guard must not NRE while scanning a malformed model.
            var m = SlopeMap();
            m.Units = new ScenarioUnit[] { null! };
            m.Buildings = new ScenarioBuilding[] { null! };
            m.ResourceNodes = new ScenarioResourceNode[] { null! };
            m.Triggers = new TriggerDefinition[] { null! };
            Assert.Null(ScenarioValidator.CheckSpawnsNotBlocked(m, Resolved(m)));
        }

        // ── The wiring: the applier chokepoint surfaces the located error at load ─────────────────────────────────

        [Fact]
        public void Applier_SurfacesTheLocatedGuardError_AtLoad()
        {
            var m = SlopeMap();
            m.Units = new[] { new ScenarioUnit { UnitId = "worker", Slot = 0, X = SlopeBlockedX, Z = SlopeBlockedZ } };

            var log = new CapturingLogSink();
            (SimulationHost host, ScenarioApplier applier) = Build(m, log);
            applier.SetPathabilityGrid(Resolved(m));   // mirrors ScenarioLoadPhase.BuildAndInjectPathabilityGrid

            ValidationResult r = Validator.Validate(m);
            Assert.True(r.Ok, r.Error);
            applier.Apply(r.Value);

            Assert.Contains(log.Warns, w => w.Contains("DW-148") && w.Contains("units[0]") && w.Contains("blocked"));
            // The authored content is still applied — a silent un-spawn would be worse than a reported bad placement.
            Assert.True(host.World.HighWaterMark > 0);
        }

        [Fact]
        public void Applier_ClearMap_LogsNoGuardWarning()
        {
            var m = SlopeMap();
            m.Units = new[] { new ScenarioUnit { UnitId = "worker", Slot = 0, X = -42f, Z = -3f } };

            var log = new CapturingLogSink();
            (SimulationHost _, ScenarioApplier applier) = Build(m, log);
            applier.SetPathabilityGrid(Resolved(m));

            ValidationResult r = Validator.Validate(m);
            Assert.True(r.Ok, r.Error);
            applier.Apply(r.Value);

            Assert.DoesNotContain(log.Warns, w => w.Contains("DW-148"));
        }

        /// <summary>Wire a host + applier over the shared golden faction (mirrors PathabilityReapplyRebuildTests).</summary>
        private static (SimulationHost host, ScenarioApplier applier) Build(ScenarioData model, ILogSink log)
        {
            FactionDefinition faction = GoldenApplierScenario.BuildFaction();
            var slotDefs = new FactionDefinition?[5];
            slotDefs[(int)Faction.Player1] = faction;
            slotDefs[(int)Faction.Player2] = faction;
            var host = SimulationHost.Create(log, new FactionRegistry(2), faction, faction);
            return (host, new ScenarioApplier(host, log, slotDefs));
        }

        /// <summary>Test ILogSink that captures messages so the load-time guard warning can be asserted.</summary>
        private sealed class CapturingLogSink : ILogSink
        {
            public readonly List<string> Infos = new();
            public readonly List<string> Warns = new();
            public void Info(string message) => Infos.Add(message);
            public void Warn(string message) => Warns.Add(message);
        }
    }
}
