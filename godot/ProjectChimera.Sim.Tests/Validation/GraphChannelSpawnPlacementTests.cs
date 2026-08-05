#nullable enable
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Dsl;         // TriggerGraph / ActionNode / EventNode / TriggerNode / ExecEdge
using ProjectChimera.Navigation;  // PathabilityGrid
using Xunit;

namespace ProjectChimera.Sim.Tests.Validation
{
    /// <summary>
    /// DW-509 — CHANNEL PARITY for the <c>spawn_unit</c> PLACEMENT gates.
    ///
    /// <para><b>The defect.</b> <see cref="ScenarioValidator"/> runs two semantic passes over the same execution
    /// walk: the FLAT <c>Triggers[]</c> pass and the <c>trigger_graph</c> node pass. The flat pass gated every
    /// <c>spawn_unit</c> action's coordinates twice — <c>CheckCoordFixed</c> (±<c>map_bounds</c>) and
    /// <c>CheckNotBlocked</c> (the authored blocked union: painted ∪ blocking prop / water footprint) — while the
    /// graph pass's <c>ActionNode</c> case ran only the faction, count and (DW-240) unit_id gates. So a spawn the
    /// gate rejects when written as a flat trigger was ACCEPTED when written as the byte-equivalent graph node:
    /// the same runtime write (<c>ScenarioDelegateBinder.OnSpawnUnit</c> → <c>ScenarioApplier.SpawnUnitAt</c>) could
    /// place a unit outside the map or onto a cell no unit can legally stand on, and authoring it in the graph IR
    /// was a one-line bypass of both rules.</para>
    ///
    /// <para><b>RED-teeth proof.</b> Delete the <c>DW-509</c> block in the graph <c>case ActionNode ga:</c> arm and
    /// every <c>…IsRejected…</c> row below turns RED while its flat-channel twin stays GREEN — which is exactly the
    /// asymmetry being closed. The accept rows stay GREEN either way (they pin that in-bounds, clear spawns are
    /// untouched).</para>
    /// </summary>
    public class GraphChannelSpawnPlacementTests
    {
        private static readonly ScenarioValidator Validator = new();

        // Flow cell (64, 64) — its centre world position is (1, 1). Same fixture the Story 6.5 painted-cell tests use.
        private const float BlockedX = 1f, BlockedZ = 1f;

        private static string BlockedCell64_64()
        {
            var mask = new bool[PathabilityGrid.CELL_COUNT];
            mask[64 * PathabilityGrid.GRID_SIZE + 64] = true;
            return PathabilityGrid.ToBase64(mask)!;
        }

        /// <summary>A minimal valid one-slot model (map_bounds 120, no triggers, no paint).</summary>
        private static ScenarioData Base() => new ScenarioData
        {
            MapBounds = 120f,
            WinCondition = WinCondition.DestroyAllBuildings,
            PlayerSlots = new[] { new ScenarioPlayerSlot { Slot = 0, StartOre = 200f, BaseX = -45f, BaseZ = 0f } },
        };

        /// <summary>The GRAPH channel spelling: one trigger whose single action leaf is a <c>spawn_unit</c> at (x, z).</summary>
        private static ScenarioData GraphSpawnAt(Fixed x, Fixed z)
        {
            var g = new TriggerGraph();
            g.Nodes.Add(new TriggerNode { Id = 0, Name = "t" });
            g.Nodes.Add(new EventNode { Id = 1, Kind = "match_start" });
            g.ExecEdges.Add(new ExecEdge(1, TriggerGraph.EventExecOutPort, 0, TriggerGraph.TriggerEventInPort));
            g.Nodes.Add(new ActionNode { Id = 2, Kind = "spawn_unit", UnitId = "worker", Faction = 0, X = x, Z = z, Count = 1 });
            g.ExecEdges.Add(new ExecEdge(0, TriggerGraph.TriggerExecOutPort, 2, TriggerGraph.ActionExecInPort));

            ScenarioData m = Base();
            m.TriggerGraphJson = g.ToCanonicalJson();
            return m;
        }

        /// <summary>The FLAT channel spelling of the SAME spawn — the parity reference each graph row is compared to.</summary>
        private static ScenarioData FlatSpawnAt(Fixed x, Fixed z)
        {
            ScenarioData m = Base();
            m.Triggers = new[]
            {
                new TriggerDefinition
                {
                    Name = "t",
                    Events  = new[] { new TriggerEvent { Type = "match_start" } },
                    Actions = new[] { new TriggerAction { Type = "spawn_unit", UnitId = "worker", Faction = 0, X = x, Z = z, Count = 1 } },
                },
            };
            return m;
        }

        // ── ±map_bounds ─────────────────────────────────────────────────────────────────────────────────────────

        [Theory]
        [InlineData(200, 0)]    // X beyond +map_bounds
        [InlineData(-200, 0)]   // X beyond -map_bounds
        [InlineData(0, 200)]    // Z beyond +map_bounds
        [InlineData(0, -200)]   // Z beyond -map_bounds
        public void GraphSpawnUnitOutsideMapBounds_IsRejected_LocatingTheNode(int x, int z)
        {
            ValidationResult r = Validator.Validate(GraphSpawnAt(Fixed.FromInt(x), Fixed.FromInt(z)));
            Assert.False(r.Ok, "a graph-authored spawn outside map_bounds must fail the gate");
            Assert.Contains("trigger_graph action node 2", r.Error!);
            Assert.Contains("map_bounds", r.Error!);
        }

        [Theory]
        [InlineData(200, 0)]
        [InlineData(0, -200)]
        public void OutOfBoundsSpawn_IsRejectedInBOTHChannels_TheParityClaim(int x, int z)
        {
            // The DW-509 claim in one assertion: the two channels are the same gate. Both spellings of the same
            // spawn must be rejected, and both messages must name the same rule.
            ValidationResult flat  = Validator.Validate(FlatSpawnAt(Fixed.FromInt(x), Fixed.FromInt(z)));
            ValidationResult graph = Validator.Validate(GraphSpawnAt(Fixed.FromInt(x), Fixed.FromInt(z)));
            Assert.False(flat.Ok);
            Assert.False(graph.Ok);
            Assert.Contains("map_bounds", flat.Error!);
            Assert.Contains("map_bounds", graph.Error!);
        }

        [Fact]
        public void GraphSpawnUnitExactlyOnTheBoundsEdge_IsAccepted()
        {
            // ±bounds is INCLUSIVE for CheckCoordFixed (the flat channel's threshold) — the graph channel must not be
            // stricter, or a legal edge spawn would start rejecting.
            Assert.True(Validator.Validate(GraphSpawnAt(Fixed.FromInt(120), Fixed.FromInt(-120))).Ok);
        }

        [Fact]
        public void GraphSpawnUnitInsideBounds_IsAccepted()
        {
            Assert.True(Validator.Validate(GraphSpawnAt(Fixed.FromInt(20), Fixed.FromInt(5))).Ok);
        }

        // ── blocked cells ───────────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void GraphSpawnUnitOnPaintedBlockedCell_IsRejected_LocatingTheNode()
        {
            ScenarioData m = GraphSpawnAt(Fixed.FromFloat(BlockedX), Fixed.FromFloat(BlockedZ));
            m.PathabilityBlocked = BlockedCell64_64();
            ValidationResult r = Validator.Validate(m);
            Assert.False(r.Ok, "a graph-authored spawn onto a painted blocked cell must fail the gate");
            Assert.Contains("trigger_graph action node 2", r.Error!);
            Assert.Contains("blocked", r.Error!);
        }

        [Fact]
        public void BlockedCellSpawn_IsRejectedInBOTHChannels_TheParityClaim()
        {
            ScenarioData flatModel = FlatSpawnAt(Fixed.FromFloat(BlockedX), Fixed.FromFloat(BlockedZ));
            flatModel.PathabilityBlocked = BlockedCell64_64();
            ScenarioData graphModel = GraphSpawnAt(Fixed.FromFloat(BlockedX), Fixed.FromFloat(BlockedZ));
            graphModel.PathabilityBlocked = BlockedCell64_64();

            ValidationResult flat  = Validator.Validate(flatModel);
            ValidationResult graph = Validator.Validate(graphModel);
            Assert.False(flat.Ok);
            Assert.False(graph.Ok);
            Assert.Contains("blocked", flat.Error!);
            Assert.Contains("blocked", graph.Error!);
        }

        [Fact]
        public void GraphSpawnUnitOnBlockingPropFootprint_IsRejected()
        {
            // The blocked union the flat channel checks is painted ∪ prop/water footprint — the graph channel reads
            // the SAME decoded grid, so a spawn onto a blocking prop's cell is rejected with no paint layer at all.
            ScenarioData m = GraphSpawnAt(Fixed.FromInt(30), Fixed.FromInt(30));
            m.Props = new[] { new ScenarioProp { PropId = "rock", X = 30f, Z = 30f, BlocksPathing = true } };
            ValidationResult r = Validator.Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("blocked", r.Error!);
        }

        [Fact]
        public void GraphSpawnUnitOnAClearCell_WithAPaintedLayerPresent_IsAccepted()
        {
            // A painted wall exists, but the spawn is elsewhere — no false positive (the pass path is untouched).
            ScenarioData m = GraphSpawnAt(Fixed.FromInt(20), Fixed.FromInt(5));
            m.PathabilityBlocked = BlockedCell64_64();
            Assert.True(Validator.Validate(m).Ok);
        }

        // ── ordering / non-interference ─────────────────────────────────────────────────────────────────────────

        [Fact]
        public void GraphSpawnCountGate_StillFiresBeforeThePlacementGates()
        {
            // The new placement checks were inserted AFTER the count gate (mirroring the flat arm's order), so an
            // out-of-range count on an out-of-bounds spawn still reports the count rule first — a stable first-fail.
            ScenarioData m = GraphSpawnAt(Fixed.FromInt(200), Fixed.Zero);
            var g = TriggerGraph.FromJson(m.TriggerGraphJson!);
            foreach (NodeBase n in g.Nodes) if (n is ActionNode a) a.Count = 0;
            m.TriggerGraphJson = g.ToCanonicalJson();

            ValidationResult r = Validator.Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("MaxSpawnCount", r.Error!);
        }

        [Fact]
        public void NonSpawnGraphActionOutsideBounds_IsUnaffected()
        {
            // X/Z are shared ActionNode fields — a non-spawn action (display_message) carries whatever the author left
            // there and has no placement semantics, so the new gates must not touch it.
            var g = new TriggerGraph();
            g.Nodes.Add(new TriggerNode { Id = 0, Name = "t" });
            g.Nodes.Add(new EventNode { Id = 1, Kind = "match_start" });
            g.ExecEdges.Add(new ExecEdge(1, TriggerGraph.EventExecOutPort, 0, TriggerGraph.TriggerEventInPort));
            g.Nodes.Add(new ActionNode
            {
                Id = 2, Kind = "display_message", Text = "hi", Faction = 0,
                X = Fixed.FromInt(9000), Z = Fixed.FromInt(-9000),
            });
            g.ExecEdges.Add(new ExecEdge(0, TriggerGraph.TriggerExecOutPort, 2, TriggerGraph.ActionExecInPort));

            ScenarioData m = Base();
            m.TriggerGraphJson = g.ToCanonicalJson();
            Assert.True(Validator.Validate(m).Ok, "only spawn_unit carries placement semantics");
        }
    }
}
