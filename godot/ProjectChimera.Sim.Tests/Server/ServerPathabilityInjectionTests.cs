#nullable enable
using System.Collections.Generic;
using System.Linq;
using ProjectChimera.Core;              // Faction, Fixed, FixedVec3, EntityFlags, ElevationGrid, FactionRegistry
using ProjectChimera.Core.Definitions;  // ScenarioData & sub-types, FactionDefinition, ScenarioValidator
using ProjectChimera.Core.Sim;          // ServerBootstrap, ScenarioApplier, SimulationHost, NullLogSink
using ProjectChimera.Navigation;        // PathabilityGrid
using ProjectChimera.Sim.Tests.Golden;  // GoldenApplierScenario.BuildFaction / BuildModel
using Xunit;

namespace ProjectChimera.Sim.Tests.Server
{
    /// <summary>
    /// DW-508 — the headless/server leg must resolve and inject the SAME static blocked-cell grid the client's
    /// <c>ScenarioLoadPhase</c> injects.
    ///
    /// <para><b>The defect.</b> <see cref="ServerBootstrap.Build"/> validated + applied the scenario but never called
    /// <c>SetPathabilityGrid</c> (nor <c>SetElevationGrid</c>), so on the headless leg <c>world.Pathability</c> stayed
    /// <c>null</c>: <c>MovementSystem</c>'s blocked-cell rejection was a server-side NO-OP while every client enforced
    /// it. On any map with painted, blocking-prop, water or slope-derived blocked cells the arbitrating server's unit
    /// positions — and therefore its <c>SimChecksum</c> — diverged from its peers' from the first tick a unit touched
    /// a wall, i.e. the arbiter itself was the desync source.</para>
    ///
    /// <para><b>Determinism.</b> Every assertion here rides a purpose-built model that actually blocks something. The
    /// flat/no-op arm below pins the other half of the contract: a model with nothing blocked resolves to a
    /// <c>null</c> grid and both setters are exact no-ops, which is why no committed golden moves (the golden
    /// scenarios carry no painted layer, no props, no water and no slope config).</para>
    /// </summary>
    public class ServerPathabilityInjectionTests
    {
        private static readonly ScenarioValidator Validator = new();

        // ── Fixtures ─────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Flow-cell column 64 spans world X ∈ [0, 2) — the painted N-S wall the mover must not cross.</summary>
        private const int WallColumn = 64;

        /// <summary>A full N-S painted wall at <see cref="WallColumn"/>, encoded exactly as the authored layer is.</summary>
        private static string WallBase64()
        {
            var mask = new bool[PathabilityGrid.CELL_COUNT];
            const int GS = PathabilityGrid.GRID_SIZE;
            for (int row = 0; row < GS; row++) mask[row * GS + WallColumn] = true;
            return PathabilityGrid.ToBase64(mask)!;
        }

        /// <summary>A 256×256 / ±128 elevation grid with a hard cliff at world X=0 (0 west, 10 east) — the shared
        /// slope fixture (identical to ResolvedGridSpawnGuardTests.CliffAtX0 / SlopeAutoBlockTests).</summary>
        private static ElevationGrid CliffAtX0()
        {
            const int N = 256;
            var heights = new Fixed[N * N];
            for (int row = 0; row < N; row++)
                for (int col = 0; col < N; col++)
                    heights[row * N + col] = col >= 128 ? Fixed.FromInt(10) : Fixed.Zero;
            return new ElevationGrid(heights, N, N, Fixed.FromInt(-128), Fixed.FromInt(-128), Fixed.One);
        }

        /// <summary>Two slots, two far-apart pre-built command centres and ONE Player1 worker west of a painted wall.
        /// Zero starting ore so nothing is produced during the tick window (a low-noise, symmetric board).</summary>
        private static ScenarioData WallMap() => new ScenarioData
        {
            Id = "server_wall_map", DisplayName = "Server Wall Map", TerrainRef = "", MapBounds = 120f,
            WinCondition = WinCondition.DestroyAllBuildings,
            PathabilityBlocked = WallBase64(),
            PlayerSlots = new[]
            {
                new ScenarioPlayerSlot { Slot = 0, StartOre = 0f, BaseX = -45f, BaseZ = 0f },
                new ScenarioPlayerSlot { Slot = 1, StartOre = 0f, BaseX =  45f, BaseZ = 0f },
            },
            Buildings = new[]
            {
                new ScenarioBuilding { Type = "CommandCenter", Slot = 0, X = -45f, Z = 0f, PreBuilt = true },
                new ScenarioBuilding { Type = "CommandCenter", Slot = 1, X =  45f, Z = 0f, PreBuilt = true },
            },
            Units = new[] { new ScenarioUnit { UnitId = "worker", Slot = 0, X = -10f, Z = 0f } },
        };

        /// <summary>A blocking prop + a water rect, both well clear of every placement — the two non-painted arms of
        /// the union the server must also carry.</summary>
        private static ScenarioData PropWaterMap()
        {
            ScenarioData m = WallMap();
            m.Id = "server_prop_water_map";
            m.PathabilityBlocked = null;                       // isolate the footprint arm
            m.Props = new[] { new ScenarioProp { PropId = "rock", X = 10f, Z = 0f, BlocksPathing = true } };
            m.Water = new[] { new ScenarioWater { X = 20f, Z = -4f, W = 4f, H = 8f } };
            return m;
        }

        /// <summary>A slope-auto-block map with nothing painted: the blocked cells exist ONLY once an elevation grid
        /// is supplied, so it also proves the elevation grid reaches the world.</summary>
        private static ScenarioData SlopeMap() => new ScenarioData
        {
            Id = "server_slope_map", DisplayName = "Server Slope Map", TerrainRef = "", MapBounds = 120f,
            SlopeAutoBlock = true, SlopeBlockThreshold = 1f,
            PlayerSlots = new[] { new ScenarioPlayerSlot { Slot = 0, StartOre = 0f, BaseX = -45f, BaseZ = 0f } },
            Units = new[] { new ScenarioUnit { UnitId = "worker", Slot = 0, X = 40f, Z = 0f } }, // east of the cliff
        };

        /// <summary>Build through the REAL <see cref="ServerBootstrap"/> (fresh faction defs per call — Build mutates
        /// them via ResolveAbilities / the tag validator).</summary>
        private static SimulationHost BuildServer(ScenarioData model, ElevationGrid? elev = null, int factions = 2)
        {
            var slotDefs = new FactionDefinition?[5];
            slotDefs[(int)Faction.Player1] = GoldenApplierScenario.BuildFaction();
            slotDefs[(int)Faction.Player2] = GoldenApplierScenario.BuildFaction();

            SimulationHost? host = ServerBootstrap.Build(
                model, slotDefs, damageTable: null, NullLogSink.Instance,
                activeFactionCount: factions, elevationGrid: elev);
            Assert.NotNull(host);   // every fixture here is a VALID model — fail-closed must not trip
            return host!;
        }

        /// <summary>Build through the CLIENT recipe — the exact ScenarioLoadPhase order (elevation → pathability →
        /// Apply) over the identical host construction ServerBootstrap performs.</summary>
        private static SimulationHost BuildClientPath(ScenarioData model, ElevationGrid? elev = null, int factions = 2)
        {
            var slotDefs = new FactionDefinition?[5];
            slotDefs[(int)Faction.Player1] = GoldenApplierScenario.BuildFaction();
            slotDefs[(int)Faction.Player2] = GoldenApplierScenario.BuildFaction();

            var host = SimulationHost.Create(
                NullLogSink.Instance, new FactionRegistry(factions),
                slotDefs[(int)Faction.Player1], slotDefs[(int)Faction.Player2]);

            var applier = new ScenarioApplier(host, NullLogSink.Instance, slotDefs);
            applier.SetElevationGrid(elev);
            applier.SetPathabilityGrid(ScenarioApplier.BuildPathabilityGrid(model, elev));

            ValidationResult r = Validator.Validate(model, slotDefs);
            Assert.True(r.Ok, r.Error);
            applier.Apply(r.Value);
            return host;
        }

        /// <summary>Command entity 0 due east and step the host, recording the per-tick checksum.</summary>
        private static List<uint> RunEastbound(SimulationHost host, int ticks)
        {
            host.ChecksumInterval = 1;
            host.World.MoveTarget[0] = new FixedVec3(Fixed.FromInt(30), Fixed.Zero, Fixed.Zero);
            host.World.Flags[0]     |= EntityFlags.Moving;

            var seq = new List<uint>(ticks);
            for (int t = 0; t < ticks; t++) { host.StepOnce(); seq.Add(host.LastChecksum); }
            return seq;
        }

        // ── The defect: the server world carried no blocked cells at all ─────────────────────────────────────────

        [Fact]
        public void Build_PaintedLayer_ReachesTheServerWorld()
        {
            SimulationHost host = BuildServer(WallMap());

            PathabilityGrid? grid = host.World.Pathability;
            Assert.NotNull(grid);                                   // pre-fix: null — the whole defect
            Assert.True(grid!.AnyBlocked);
            Assert.True(grid.IsBlocked(Fixed.One, Fixed.Zero));     // world X=1 → the painted wall column
            Assert.False(grid.IsBlocked(Fixed.FromInt(-10), Fixed.Zero));
        }

        [Fact]
        public void Build_BlockingPropAndWaterFootprints_ReachTheServerWorld()
        {
            SimulationHost host = BuildServer(PropWaterMap());

            PathabilityGrid? grid = host.World.Pathability;
            Assert.NotNull(grid);                                                   // pre-fix: null
            Assert.True(grid!.IsBlocked(Fixed.FromInt(10), Fixed.Zero));            // the blocks_pathing prop's cell
            Assert.True(grid.IsBlocked(Fixed.FromInt(21), Fixed.Zero));             // inside the water rect
            Assert.False(grid.IsBlocked(Fixed.FromInt(-10), Fixed.Zero));           // the worker's own cell stays clear
        }

        [Fact]
        public void Build_WithElevationGrid_DerivesSlopeCells_AndSamplesSpawnElevation()
        {
            SimulationHost host = BuildServer(SlopeMap(), CliffAtX0());

            // Slope arm: the two flow columns straddling the cliff auto-block (DW-149 widened it to both sides).
            PathabilityGrid? grid = host.World.Pathability;
            Assert.NotNull(grid);                                                   // pre-fix: null
            Assert.True(grid!.IsBlocked(Fixed.FromInt(-1), Fixed.Zero));

            // Elevation arm: the unit east of the cliff spawns at height 10, not flat — proving SetElevationGrid landed.
            Assert.Equal(Fixed.FromInt(10).Raw, host.World.Elevation[0].Raw);       // pre-fix: 0 (no grid injected)
        }

        [Fact]
        public void Build_ResolvesTheIdenticalBlockedSet_AsTheClientPath()
        {
            PathabilityGrid? server = BuildServer(WallMap()).World.Pathability;
            PathabilityGrid? client = BuildClientPath(WallMap()).World.Pathability;

            Assert.NotNull(server);                                                 // pre-fix: null vs a real client grid
            Assert.NotNull(client);
            Assert.Equal(client!.Blocked, server!.Blocked);                         // cell-for-cell, one shared recipe
        }

        // ── The consequence: an arbitrating server that walked its units through walls ───────────────────────────

        [Fact]
        public void ServerTick_ConfinesAMoverBehindTheWall_InsteadOfWalkingThrough()
        {
            SimulationHost host = BuildServer(WallMap());
            var wall = new PathabilityGrid(PathabilityGrid.FromBase64(WallMap().PathabilityBlocked));

            host.World.MoveTarget[0] = new FixedVec3(Fixed.FromInt(30), Fixed.Zero, Fixed.Zero);
            host.World.Flags[0]     |= EntityFlags.Moving;

            for (int t = 0; t < 300; t++)
            {
                host.StepOnce();
                FixedVec3 p = host.World.Position[0];
                Assert.False(wall.IsBlocked(p.X, p.Z),
                    $"the SERVER let a unit occupy a blocked cell at tick {t + 1} (X={p.X.ToFloat()}).");
                Assert.True(p.X < Fixed.Zero,
                    $"the SERVER let a unit cross the wall at tick {t + 1} (X={p.X.ToFloat()}) — pre-DW-508 behaviour.");
            }

            Assert.True(host.World.Position[0].X > Fixed.FromInt(-10), "the mover never advanced — the fixture is inert.");
        }

        [Fact]
        public void ServerChecksums_MatchTheClientPath_OnAMapWithBlockedCells()
        {
            List<uint> server = RunEastbound(BuildServer(WallMap()),     200);
            List<uint> client = RunEastbound(BuildClientPath(WallMap()), 200);

            // Pre-fix the server's mover walked THROUGH the wall while the client's stopped at it, so the arbiter's
            // checksum diverged from every peer's within the first seconds of the match.
            int firstDiff = -1;
            for (int i = 0; i < server.Count; i++) if (server[i] != client[i]) { firstDiff = i; break; }
            Assert.True(firstDiff < 0,
                $"server↔client checksum divergence at tick {firstDiff + 1} " +
                $"(server=0x{(firstDiff < 0 ? 0u : server[firstDiff]):X8}, client=0x{(firstDiff < 0 ? 0u : client[firstDiff]):X8}).");
        }

        // ── The no-op half: nothing blocked ⇒ null grids ⇒ no committed golden can move ──────────────────────────

        [Fact]
        public void Build_FlatModel_LeavesBothGridsUnset_ExactNoOp()
        {
            SimulationHost host = BuildServer(GoldenApplierScenario.BuildModel());

            // The golden scenarios carry no painted layer, no props, no water and no slope config, so Resolve returns
            // null and BOTH setters are exact no-ops — the reason DW-508 moves no golden / CanonicalModelHash /
            // StartStateHash. If this ever turns red, a golden re-baseline decision is owed BEFORE the fix ships.
            Assert.Null(host.World.Pathability);
            for (int id = 0; id < host.World.HighWaterMark; id++)
                Assert.Equal(0L, host.World.Elevation[id].Raw);
        }

        [Fact]
        public void Build_FlatModel_ChecksumsAreIdenticalToTheClientPath()
        {
            List<uint> server = RunEastbound(BuildServer(GoldenApplierScenario.BuildModel()),     120);
            List<uint> client = RunEastbound(BuildClientPath(GoldenApplierScenario.BuildModel()), 120);
            Assert.True(server.SequenceEqual(client), "the flat/legacy path must stay byte-identical between the legs.");
        }
    }
}
