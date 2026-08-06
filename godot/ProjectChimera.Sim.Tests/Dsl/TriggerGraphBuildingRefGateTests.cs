#nullable enable
using System;
using System.Collections.Generic;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Dsl;
using Xunit;

namespace ProjectChimera.Sim.Tests.Dsl
{
    /// <summary>
    /// DW-628 — the trigger_graph channel's building refs were never gated at ALL: a raw-IR graph could name a
    /// nonexistent building, load clean, and stay permanently inert. That is exactly the silently-inert class the
    /// flat gate's <c>UnknownBuildingTypeInCondition_IsRejected_NotSilentlyInert</c> check exists to prevent —
    /// <c>building_completed</c> would index-encode an id no placed building ever carries and
    /// <c>building_exists</c> would match nothing, forever, with no load error anywhere.
    ///
    /// <para>The fix is <see cref="GraphStructureGate.CheckBuildingRefs"/>, run by <see cref="ScenarioValidator"/>
    /// over the parsed graph channel with the SAME per-slot faction defs the flat arms get, resolving against the
    /// SAME two vocabularies (a legacy <see cref="BuildingType"/> enum NAME, or an authored building-def id in the
    /// faction the node itself names) through the SAME <c>IsKnownBuildingType</c>/<c>OwnerFactionDef</c> pair —
    /// one vocabulary by construction, not a second copy (the DW-627 lesson).</para>
    ///
    /// <para>These tests are Godot-free and split in two halves: the PURE gate (called directly, so the rule is
    /// testable without a whole ScenarioData) and the WIRING (a real scenario through
    /// <c>ScenarioValidator.Validate</c>, proving the channel is actually gated at the authoritative pre-tick
    /// gate and that the flat and graph channels now accept/reject the same refs).</para>
    /// </summary>
    public class TriggerGraphBuildingRefGateTests
    {
        private const string CustomId = "watchtower";

        // ── Fixtures ────────────────────────────────────────────────────────────

        /// <summary>A faction authoring command_center/barracks PLUS a custom "watchtower" with no
        /// <see cref="BuildingType"/> enum member (it places as <see cref="BuildingType.Custom"/>).</summary>
        private static FactionDefinition FactionWithWatchtower()
        {
            var f = new FactionDefinition { Id = "alpha", DisplayName = "Alpha" };
            f.Units.Add(new UnitDefinition { Id = "worker", Category = "Worker", Hp = 50f });
            f.Buildings.Add(new BuildingDefinition
            {
                Id = "command_center", DisplayName = "Command Center", Category = "Structure",
                Hp = 500f, ConstructionTime = 15f, SupplyBonus = 10, ProducesCategory = "Worker",
            });
            f.Buildings.Add(new BuildingDefinition
            {
                Id = "barracks", DisplayName = "Barracks", Category = "Structure",
                Hp = 300f, ConstructionTime = 10f, SupplyBonus = 0, ProducesCategory = "Melee",
            });
            f.Buildings.Add(new BuildingDefinition
            {
                Id = CustomId, DisplayName = "Watch Tower", Category = "Structure",
                Hp = 150f, ConstructionTime = 8f, SupplyBonus = 3, ProducesCategory = "Melee",
            });
            return f;
        }

        /// <summary>A second faction authoring NO watchtower — the fail-closed arm of the faction qualifier.</summary>
        private static FactionDefinition FactionWithoutWatchtower()
        {
            var f = new FactionDefinition { Id = "beta", DisplayName = "Beta" };
            f.Units.Add(new UnitDefinition { Id = "worker", Category = "Worker", Hp = 50f });
            f.Buildings.Add(new BuildingDefinition
            {
                Id = "command_center", DisplayName = "Command Center", Category = "Structure",
                Hp = 500f, ConstructionTime = 15f, SupplyBonus = 10, ProducesCategory = "Worker",
            });
            return f;
        }

        /// <summary>Per-slot defs indexed by <c>(int)Faction</c> (slot + 1), matching the applier's cast.</summary>
        private static FactionDefinition?[] SlotDefs() =>
            SlotDefs(FactionWithWatchtower(), FactionWithoutWatchtower());

        private static FactionDefinition?[] SlotDefs(FactionDefinition slot0, FactionDefinition slot1)
        {
            var defs = new FactionDefinition?[FactionRegistry.FACTION_ARRAY_SIZE];
            defs[(int)Faction.Player1] = slot0;
            defs[(int)Faction.Player2] = slot1;
            return defs;
        }

        /// <summary>
        /// The graph the raw-IR hatch actually authors: trigger 0, driven by event 1, gated by condition 2, running
        /// action 3. Either leaf may carry a building ref (null = the field is simply absent).
        /// </summary>
        private static TriggerGraph GraphWith(
            string eventKind, int eventFaction, string? eventBuilding,
            string condKind,  int condFaction,  string? condBuilding)
        {
            var g = new TriggerGraph();
            g.Nodes.Add(new TriggerNode { Id = 0, Name = "t" });
            g.Nodes.Add(new EventNode { Id = 1, Kind = eventKind, Faction = eventFaction, BuildingType = eventBuilding });
            g.Nodes.Add(new ConditionNode { Id = 2, Kind = condKind, Faction = condFaction, BuildingType = condBuilding });
            g.Nodes.Add(new ActionNode { Id = 3, Kind = "display_message", Text = "hi" });
            g.ExecEdges.Add(new ExecEdge(1, TriggerGraph.EventExecOutPort, 0, TriggerGraph.TriggerEventInPort));
            g.ExecEdges.Add(new ExecEdge(0, TriggerGraph.TriggerExecOutPort, 3, TriggerGraph.ActionExecInPort));
            g.DataEdges.Add(new DataEdge(2, TriggerGraph.ConditionDataOutPort, 0,
                TriggerGraph.TriggerConditionInPort, DataWireType.Boolean));
            return g;
        }

        /// <summary>A minimal valid scenario carrying the graph channel ONLY (no flat triggers), so every reject
        /// below is unambiguously the graph channel's.</summary>
        private static ScenarioData ModelWithGraph(TriggerGraph g) => new ScenarioData
        {
            MapBounds = 120f,
            WinCondition = WinCondition.DestroyAllBuildings,
            PlayerSlots = new[]
            {
                new ScenarioPlayerSlot { Slot = 0, FactionJson = "res://a.json", StartOre = 200f, BaseX = -45f, BaseZ = 0f },
                new ScenarioPlayerSlot { Slot = 1, FactionJson = "res://b.json", StartOre = 200f, BaseX =  45f, BaseZ = 0f },
            },
            ResourceNodes = new[] { new ScenarioResourceNode { X = 10f, Z = 10f, Supply = 400f, Rate = 5f, MaxGatherers = 4 } },
            Buildings = Array.Empty<ScenarioBuilding>(),
            Units = Array.Empty<ScenarioUnit>(),
            Triggers = Array.Empty<TriggerDefinition>(),
            TriggerGraphJson = g.ToCanonicalJson(),
        };

        // ── The pure gate ───────────────────────────────────────────────────────

        [Fact]
        public void PureGate_UnknownRefOnACondition_Rejects_NamingTheNodeAndTheSlot()
        {
            string? err = GraphStructureGate.CheckBuildingRefs(
                GraphWith("match_start", 0, null, "building_exists", 0, "Frost"), SlotDefs());

            Assert.NotNull(err);
            Assert.Contains("condition node 2.building_type='Frost'", err!, StringComparison.Ordinal);
            Assert.Contains("faction slot 0", err!, StringComparison.Ordinal);
        }

        [Fact]
        public void PureGate_UnknownRefOnAnEvent_Rejects()
        {
            string? err = GraphStructureGate.CheckBuildingRefs(
                GraphWith("building_completed", 0, "no_such_building", "always", 0, null), SlotDefs());

            Assert.NotNull(err);
            Assert.Contains("event node 1.building_type='no_such_building'", err!, StringComparison.Ordinal);
        }

        [Fact]
        public void PureGate_AbsentAndEmptyRefsAreNoOps()
        {
            // No building filter at all is the overwhelmingly common shape (every match_start/always node) — it
            // must stay a pass, exactly like the flat arms' IsNullOrEmpty short-circuit.
            Assert.Null(GraphStructureGate.CheckBuildingRefs(
                GraphWith("match_start", 0, null, "always", 0, null), SlotDefs()));
            Assert.Null(GraphStructureGate.CheckBuildingRefs(
                GraphWith("match_start", 0, "", "always", 0, ""), SlotDefs()));
        }

        [Fact]
        public void PureGate_LegacyEnumName_Passes_ButTheCustomSentinelDoesNot()
        {
            Assert.Null(GraphStructureGate.CheckBuildingRefs(
                GraphWith("building_completed", 0, "Barracks", "always", 0, null), SlotDefs()));

            // The bare "Custom" sentinel resolves no def (a stat-less, unrendered ghost) — rejected here exactly as
            // at the scenario-buildings gate and the flat trigger arms, so widening never smuggled it in.
            Assert.NotNull(GraphStructureGate.CheckBuildingRefs(
                GraphWith("building_completed", 0, "Custom", "always", 0, null), SlotDefs()));
        }

        [Fact]
        public void PureGate_AuthoredCustomId_Resolves_ThroughTheQualifyingSlotsFaction()
        {
            // Slot 0's faction authors the watchtower → accepted (this is the DW-170 vocabulary, now live in the
            // graph channel too). Slot 1's faction does NOT → the SAME id rejects, which is what makes the
            // acceptance faction-QUALIFIED rather than "some faction somewhere declares it".
            Assert.Null(GraphStructureGate.CheckBuildingRefs(
                GraphWith("match_start", 0, null, "building_exists", 0, CustomId), SlotDefs()));

            string? err = GraphStructureGate.CheckBuildingRefs(
                GraphWith("match_start", 0, null, "building_exists", 1, CustomId), SlotDefs());
            Assert.NotNull(err);
            Assert.Contains(CustomId, err!, StringComparison.Ordinal);
            Assert.Contains("faction slot 1", err!, StringComparison.Ordinal);
        }

        [Fact]
        public void PureGate_WithNoFactionDefsThreaded_StaysEnumNameOnly()
        {
            // Byte-identical to the flat channel's behavior with nothing threaded (TriggerCustomBuildingRefTests'
            // CustomBuildingRef_WithNoFactionDefsThreaded_StaysEnumNameOnly): the enum name passes, the authored id
            // has nothing to resolve against and fails closed.
            Assert.Null(GraphStructureGate.CheckBuildingRefs(
                GraphWith("building_completed", 0, "Barracks", "always", 0, null), null));
            Assert.NotNull(GraphStructureGate.CheckBuildingRefs(
                GraphWith("building_completed", 0, CustomId, "always", 0, null), null));
        }

        [Fact]
        public void PureGate_FirstFailIsTheLOWEST_NodeId_RegardlessOfListOrder()
        {
            // Determinism: the module convention is ascending-id first-fail, so the reported node cannot depend on
            // the order the nodes happen to sit in the list (a canvas re-save reorders them freely).
            var g = new TriggerGraph();
            g.Nodes.Add(new ConditionNode { Id = 9, Kind = "building_exists", Faction = 0, BuildingType = "Frost" });
            g.Nodes.Add(new TriggerNode { Id = 0, Name = "t" });
            g.Nodes.Add(new EventNode { Id = 4, Kind = "building_completed", Faction = 0, BuildingType = "Sleet" });

            string? err = GraphStructureGate.CheckBuildingRefs(g, SlotDefs());
            Assert.NotNull(err);
            Assert.Contains("event node 4", err!, StringComparison.Ordinal);   // id 4 < id 9
            Assert.DoesNotContain("node 9", err!, StringComparison.Ordinal);
        }

        [Fact]
        public void PureGate_NullGraph_IsNotItsBusiness()
        {
            // A null graph is Check()'s located reject; this pass must not NRE on it (containment posture).
            Assert.Null(GraphStructureGate.CheckBuildingRefs(null!, SlotDefs()));
        }

        // ── The wiring: the authoritative pre-tick gate ──────────────────────────

        [Fact]
        public void UnknownBuildingTypeInGraphCondition_IsRejectedAtTheGate_NotSilentlyInert()
        {
            // THE DW-628 HEADLINE. Pre-fix this scenario validated CLEAN: the graph channel had no building check
            // anywhere, so the building_exists condition reached the director and matched nothing, forever.
            ScenarioData m = ModelWithGraph(GraphWith("match_start", 0, null, "building_exists", 0, "Frost"));

            ValidationResult r = new ScenarioValidator().Validate(m, SlotDefs());

            Assert.False(r.Ok);
            Assert.Contains("scenario.trigger_graph condition node 2.building_type='Frost'", r.Error!, StringComparison.Ordinal);
        }

        [Fact]
        public void UnknownBuildingTypeInGraphEvent_IsRejectedAtTheGate()
        {
            ScenarioData m = ModelWithGraph(GraphWith("building_completed", 0, "no_such_building", "always", 0, null));

            ValidationResult r = new ScenarioValidator().Validate(m, SlotDefs());

            Assert.False(r.Ok);
            Assert.Contains("scenario.trigger_graph event node 1.building_type='no_such_building'", r.Error!, StringComparison.Ordinal);
        }

        [Fact]
        public void GraphChannelAcceptsWhatTheFlatChannelAccepts_AndRejectsWhatItRejects()
        {
            // Channel parity is the whole point of DW-628: the SAME ref, authored in either channel, must get the
            // SAME verdict from the SAME vocabularies. Sweep three refs across both channels and compare.
            FactionDefinition?[] defs = SlotDefs();
            var cases = new (string Ref, bool Expected)[]
            {
                ("Barracks", true),   // legacy enum name
                (CustomId,   true),   // authored id in slot 0's faction (DW-170)
                ("Frost",    false),  // resolves in neither vocabulary
            };

            foreach ((string bref, bool expected) in cases)
            {
                ScenarioData graphModel = ModelWithGraph(
                    GraphWith("match_start", 0, null, "building_exists", 0, bref));
                bool graphOk = new ScenarioValidator().Validate(graphModel, defs).Ok;

                ScenarioData flatModel = ModelWithGraph(
                    GraphWith("match_start", 0, null, "always", 0, null));
                flatModel.TriggerGraphJson = null;
                flatModel.Triggers = new[]
                {
                    new TriggerDefinition
                    {
                        Name = "t",
                        Events     = new[] { new TriggerEvent { Type = "match_start", Faction = 0 } },
                        Conditions = new[] { new TriggerCondition { Type = "building_exists", Faction = 0, BuildingType = bref, Operator = ">=" } },
                        Actions    = new[] { new TriggerAction { Type = "display_message", Text = "hi" } },
                    },
                };
                bool flatOk = new ScenarioValidator().Validate(flatModel, defs).Ok;

                Assert.Equal(expected, flatOk);
                Assert.Equal(flatOk, graphOk);
            }
        }

        [Fact]
        public void AuthoredCustomIdInTheGraphChannel_StillPassesTheGate()
        {
            // The gate must not close the DW-170 door it was built beside: an authored id qualified by a faction
            // that declares it stays valid content in the raw-IR channel.
            ScenarioData m = ModelWithGraph(GraphWith("building_completed", 0, CustomId, "building_exists", 0, CustomId));

            ValidationResult r = new ScenarioValidator().Validate(m, SlotDefs());

            Assert.True(r.Ok, r.Error);
        }

        [Fact]
        public void OutOfRangeFactionSlot_IsStillReportedAsTheFactionError()
        {
            // Ordering guard: an out-of-range slot nulls the owner def, which would downgrade the message to
            // "unknown building". The building pass runs AFTER the per-node faction pass so the author is told
            // what is actually wrong.
            ScenarioData m = ModelWithGraph(GraphWith("match_start", 0, null, "building_exists", 99, CustomId));

            ValidationResult r = new ScenarioValidator().Validate(m, SlotDefs());

            Assert.False(r.Ok);
            Assert.Contains("faction", r.Error!, StringComparison.Ordinal);
            Assert.DoesNotContain("building_type", r.Error!, StringComparison.Ordinal);
        }

        [Fact]
        public void GraphChannelBuildingRefs_AreGatedEvenOnAnUNWIREDNode()
        {
            // The T3 canvas keeps disconnected work-in-progress nodes (mere unreachability is not a structural
            // reject), and the director's building-id table walks EVERY node — so an orphan node's bad ref is just
            // as inert and must be gated too. Mirrors the whole-graph posture of the other semantic passes.
            var g = GraphWith("match_start", 0, null, "always", 0, null);
            g.Nodes.Add(new ConditionNode { Id = 7, Kind = "building_exists", Faction = 0, BuildingType = "Frost" });

            ValidationResult r = new ScenarioValidator().Validate(ModelWithGraph(g), SlotDefs());

            Assert.False(r.Ok);
            Assert.Contains("condition node 7", r.Error!, StringComparison.Ordinal);
        }

        [Fact]
        public void ExistingGraphContentWithoutBuildingRefs_StillValidates()
        {
            // Legacy parity: the overwhelming majority of authored graphs carry no building ref at all, and this
            // pass must be a pure no-op for them (no new reject surface).
            Assert.Null(GraphStructureGate.CheckBuildingRefs(TriggerGraph.FromFlat(new[]
            {
                new TriggerDefinition
                {
                    Name = "koth",
                    Events     = new[] { new TriggerEvent { Type = "match_start" } },
                    Conditions = new[] { new TriggerCondition { Type = "always" } },
                    Actions    = new[] { new TriggerAction { Type = "display_message", Text = "a" } },
                },
            }), SlotDefs()));

            ValidationResult r = new ScenarioValidator().Validate(
                ModelWithGraph(GraphWith("match_start", 0, null, "always", 0, null)), SlotDefs());
            Assert.True(r.Ok, r.Error);
        }
    }
}
