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
    /// DW-170 (recorded owner decision 2026-07-30: "Add faction-qualified building ref to trigger DSL") — a
    /// creator-authored (non-enum) building can now be NAMED in a trigger and actually resolves at runtime.
    ///
    /// <para>Before this fix a scenario that placed a custom building and referenced it from a trigger failed
    /// validation WHOLESALE (both the <c>building_completed</c> event and the <c>building_exists</c> condition ran
    /// an enum-name-only gate), and even past the gate the director's <c>Enum.TryParse&lt;BuildingType&gt;</c>
    /// returned false for every authored id — a silently dead trigger.</para>
    ///
    /// <para>The qualifier is the FACTION SLOT the event/condition already carries: a <c>building_completed</c>
    /// occurrence only ever matches its own builder slot and a <c>building_exists</c> scan only ever looks at the
    /// faction it names, so that slot resolves the owner <see cref="FactionDefinition"/> exactly like a pre-placed
    /// <c>scenario.buildings[].slot</c> does (Story 6.8's gate). Both vocabularies stay live: a legacy
    /// <c>BuildingType</c> enum name keeps its byte-identical path; an authored id matches the placed building's
    /// <c>BuildingStore.DefinitionId</c>. All Godot-free.</para>
    /// </summary>
    public class TriggerCustomBuildingRefTests
    {
        private const string CustomId = "watchtower";

        // ── Fixtures ────────────────────────────────────────────────────────────

        /// <summary>A faction authoring the built-in command_center/barracks PLUS a custom "watchtower" that has
        /// no <see cref="BuildingType"/> enum member (it places as <see cref="BuildingType.Custom"/>).</summary>
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

        /// <summary>A second faction that authors NO watchtower — the fail-closed arm of the qualifier.</summary>
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
        private static FactionDefinition?[] SlotDefs(FactionDefinition slot0, FactionDefinition slot1)
        {
            var defs = new FactionDefinition?[FactionRegistry.FACTION_ARRAY_SIZE];
            defs[(int)Faction.Player1] = slot0;
            defs[(int)Faction.Player2] = slot1;
            return defs;
        }

        /// <summary>A minimal valid scenario carrying ONE trigger whose event + condition are supplied by the
        /// caller (so each test names its own building ref / faction qualifier).</summary>
        private static ScenarioData ModelWithTrigger(TriggerEvent ev, TriggerCondition cond) => new ScenarioData
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
            Triggers = new[]
            {
                new TriggerDefinition
                {
                    Name = "t",
                    Events     = new[] { ev },
                    Conditions = new[] { cond },
                    Actions    = new[] { new TriggerAction { Type = "display_message", Text = "hi" } },
                },
            },
        };

        private static TriggerEvent MatchStart() => new() { Type = "match_start", Faction = 0 };
        private static TriggerCondition Always() => new() { Type = "always", Faction = 0 };

        // ── The gate: faction-qualified acceptance ──────────────────────────────

        [Fact]
        public void CustomBuildingRef_InCondition_PassesTheGate_WhenTheQualifyingFactionAuthorsIt()
        {
            // The DW-170 headline: a building_exists naming an AUTHORED id, qualified by faction slot 0 whose
            // faction declares it. Pre-fix the trigger gate was enum-name-only → the whole scenario was rejected.
            ScenarioData m = ModelWithTrigger(
                MatchStart(),
                new TriggerCondition { Type = "building_exists", Faction = 0, BuildingType = CustomId, Operator = ">=" });

            ValidationResult r = new ScenarioValidator().Validate(
                m, SlotDefs(FactionWithWatchtower(), FactionWithoutWatchtower()));

            Assert.True(r.Ok, r.Error);
        }

        [Fact]
        public void CustomBuildingRef_InBuildingCompletedEvent_PassesTheGate_WhenTheQualifyingFactionAuthorsIt()
        {
            ScenarioData m = ModelWithTrigger(
                new TriggerEvent { Type = "building_completed", Faction = 0, BuildingType = CustomId },
                Always());

            ValidationResult r = new ScenarioValidator().Validate(
                m, SlotDefs(FactionWithWatchtower(), FactionWithoutWatchtower()));

            Assert.True(r.Ok, r.Error);
        }

        [Fact]
        public void CustomBuildingRef_FailsClosed_WhenTheQUALIFYINGSlotsFactionDoesNotAuthorIt()
        {
            // Slot 1's faction has no watchtower — so the SAME id qualified by slot 1 must be rejected. This is
            // what makes the acceptance above faction-QUALIFIED rather than "some faction somewhere declares it".
            ScenarioData m = ModelWithTrigger(
                MatchStart(),
                new TriggerCondition { Type = "building_exists", Faction = 1, BuildingType = CustomId, Operator = ">=" });

            ValidationResult r = new ScenarioValidator().Validate(
                m, SlotDefs(FactionWithWatchtower(), FactionWithoutWatchtower()));

            Assert.False(r.Ok);
            Assert.Contains("triggers[0].conditions[0].building_type", r.Error!);
            Assert.Contains(CustomId, r.Error!);
        }

        [Fact]
        public void CustomBuildingRef_WithNoFactionDefsThreaded_StaysEnumNameOnly()
        {
            // Legacy parity: with nothing threaded the gate is byte-identical to the pre-DW-170 enum-only check.
            ScenarioData m = ModelWithTrigger(
                MatchStart(),
                new TriggerCondition { Type = "building_exists", Faction = 0, BuildingType = CustomId, Operator = ">=" });

            Assert.False(new ScenarioValidator().Validate(m).Ok);
        }

        [Fact]
        public void TrulyUnknownBuildingRef_StillFailsClosed_EvenWithFactionDefsThreaded()
        {
            // Widening the vocabulary must not open the gate: an id no faction declares is still "not silently inert".
            ScenarioData m = ModelWithTrigger(
                new TriggerEvent { Type = "building_completed", Faction = 0, BuildingType = "no_such_building" },
                Always());

            ValidationResult r = new ScenarioValidator().Validate(
                m, SlotDefs(FactionWithWatchtower(), FactionWithoutWatchtower()));

            Assert.False(r.Ok);
            Assert.Contains("triggers[0].events[0].building_type", r.Error!);
        }

        [Fact]
        public void LegacyEnumNameRef_StillPasses_AndTheCustomSentinelStillFails()
        {
            var defs = SlotDefs(FactionWithWatchtower(), FactionWithoutWatchtower());

            ScenarioData legacy = ModelWithTrigger(
                new TriggerEvent { Type = "building_completed", Faction = 0, BuildingType = "Barracks" }, Always());
            Assert.True(new ScenarioValidator().Validate(legacy, defs).Ok);

            // The bare "Custom" sentinel resolves no def (a stat-less ghost) — rejected here exactly as at the
            // scenario-buildings gate, so widening the trigger vocabulary did not smuggle it in.
            ScenarioData sentinel = ModelWithTrigger(
                new TriggerEvent { Type = "building_completed", Faction = 0, BuildingType = "Custom" }, Always());
            Assert.False(new ScenarioValidator().Validate(sentinel, defs).Ok);
        }

        // ── The runtime: building_exists resolves an authored id ────────────────

        private static ScenarioVariable IntVar(string name) =>
            new() { Name = name, Type = DslValueType.Int, Scope = VarScope.Global, Initial = Fixed.Zero };

        /// <summary>One trigger: match_start + a building_exists condition → set fires = 1.</summary>
        private static ScenarioData ConditionScenario(int faction, string buildingRef) => new ScenarioData
        {
            Variables = new[] { IntVar("fires") },
            Triggers = new[]
            {
                new TriggerDefinition
                {
                    Name = "t",
                    Events     = new[] { new TriggerEvent { Type = "match_start", Faction = 0 } },
                    Conditions = new[] { new TriggerCondition { Type = "building_exists", Faction = faction, BuildingType = buildingRef, Operator = ">=" } },
                    Actions    = new[] { new TriggerAction { Type = "set_variable", Variable = "fires", Value = 1 } },
                },
            },
        };

        private static (ScenarioDirector Director, DslVarTable Vars) BuildDirector(
            ScenarioData scenario, BuildingStore buildings)
        {
            var vars = new DslVarTable();
            var director = new ScenarioDirector(buildings, new ResourceStore(Fixed.Zero), vars);
            director.LoadScenario(scenario);
            return (director, vars);
        }

        /// <summary>Place a finished custom building (the BuildingType.Custom + authored-DefinitionId shape a
        /// creator-authored building actually gets through <c>PlaceBuildingDirectById</c>).</summary>
        private static int PlaceCustom(BuildingStore b, Faction faction, string id, bool complete = true)
        {
            int i = b.Create(FixedVec3.Zero, faction, BuildingType.Custom, buildingId: id,
                             health: Fixed.FromInt(150), supplyBonus: 3, constructionDuration: Fixed.FromInt(8));
            if (complete) b.ConstructionTimer[i] = Fixed.Zero;
            return i;
        }

        [Fact]
        public void BuildingExists_ResolvesAnAuthoredCustomId_AgainstDefinitionId()
        {
            // Pre-fix: Enum.TryParse<BuildingType>("watchtower") failed → the condition returned FALSE forever,
            // so the trigger was authorable, validated (after the gate half) and permanently dead.
            var buildings = new BuildingStore();
            PlaceCustom(buildings, Faction.Player1, CustomId);

            var (director, vars) = BuildDirector(ConditionScenario(faction: 0, CustomId), buildings);
            director.Tick(new EntityWorld(), Fixed.One);

            Assert.Equal(1, vars.GetInt("fires", 0));
        }

        [Fact]
        public void BuildingExists_CustomId_IsFactionQualified_AndIgnoresAnotherSlotsBuilding()
        {
            // The same authored id owned by P2 must NOT satisfy a slot-0-qualified condition.
            var buildings = new BuildingStore();
            PlaceCustom(buildings, Faction.Player2, CustomId);

            var (director, vars) = BuildDirector(ConditionScenario(faction: 0, CustomId), buildings);
            director.Tick(new EntityWorld(), Fixed.One);

            Assert.Equal(0, vars.GetInt("fires", 0));
        }

        [Fact]
        public void BuildingExists_CustomId_DoesNotMatchWhileStillUnderConstruction()
        {
            // "alive, FULLY BUILT" is part of the condition's contract — the authored-id arm must honor it too.
            var buildings = new BuildingStore();
            PlaceCustom(buildings, Faction.Player1, CustomId, complete: false);

            var (director, vars) = BuildDirector(ConditionScenario(faction: 0, CustomId), buildings);
            director.Tick(new EntityWorld(), Fixed.One);

            Assert.Equal(0, vars.GetInt("fires", 0));
        }

        [Fact]
        public void BuildingExists_UnknownAuthoredId_StillDoesNotMatch()
        {
            var buildings = new BuildingStore();
            PlaceCustom(buildings, Faction.Player1, CustomId);

            var (director, vars) = BuildDirector(ConditionScenario(faction: 0, "some_other_tower"), buildings);
            director.Tick(new EntityWorld(), Fixed.One);

            Assert.Equal(0, vars.GetInt("fires", 0));
        }

        [Fact]
        public void BuildingExists_LegacyEnumName_KeepsMatchingByEnumType()
        {
            // Regression net for the byte-identical legacy arm (the enum-Type comparison, not DefinitionId).
            var buildings = new BuildingStore();
            int b = buildings.Create(FixedVec3.Zero, Faction.Player1, BuildingType.Barracks);
            buildings.ConstructionTimer[b] = Fixed.Zero;

            var (director, vars) = BuildDirector(ConditionScenario(faction: 0, "Barracks"), buildings);
            director.Tick(new EntityWorld(), Fixed.One);

            Assert.Equal(1, vars.GetInt("fires", 0));
        }

        [Fact]
        public void BuildingExists_SnakeCaseBuiltInId_AlsoResolves_ViaDefinitionId()
        {
            // An enum-backed building still carries its canonical authored id, so naming a faction's "barracks"
            // def (the form the 4.5 editor and the scenario-buildings gate speak) resolves too.
            var buildings = new BuildingStore();
            int b = buildings.Create(FixedVec3.Zero, Faction.Player1, BuildingType.Barracks);
            buildings.ConstructionTimer[b] = Fixed.Zero;
            Assert.Equal("barracks", buildings.DefinitionId[b]);

            var (director, vars) = BuildDirector(ConditionScenario(faction: 0, "barracks"), buildings);
            director.Tick(new EntityWorld(), Fixed.One);

            Assert.Equal(1, vars.GetInt("fires", 0));
        }

        // ── The runtime: building_completed fires on an authored id ─────────────

        /// <summary>One trigger: building_completed(faction, ref) → fires = 1.</summary>
        private static ScenarioData CompletionScenario(int faction, string buildingRef) => new ScenarioData
        {
            Variables = new[] { IntVar("fires") },
            Triggers = new[]
            {
                new TriggerDefinition
                {
                    Name = "t",
                    Events  = new[] { new TriggerEvent { Type = "building_completed", Faction = faction, BuildingType = buildingRef } },
                    Actions = new[] { new TriggerAction { Type = "set_variable", Variable = "fires", Value = 1 } },
                },
            },
        };

        [Fact]
        public void BuildingCompleted_FiresOnAnAuthoredCustomId()
        {
            // Pre-fix the occurrence's only payload was the enum NAME — "Custom" for every authored building — so
            // a trigger naming "watchtower" could never match it.
            var buildings = new BuildingStore();
            int b = PlaceCustom(buildings, Faction.Player1, CustomId, complete: false);

            var (director, vars) = BuildDirector(CompletionScenario(faction: 0, CustomId), buildings);
            var world = new EntityWorld();

            director.Tick(world, Fixed.One);           // still under construction → no completion
            Assert.Equal(0, vars.GetInt("fires", 0));

            buildings.ConstructionTimer[b] = Fixed.Zero;
            director.Tick(world, Fixed.One);           // under construction → done: the completion edge
            Assert.Equal(1, vars.GetInt("fires", 0));
        }

        [Fact]
        public void BuildingCompleted_CustomId_IsFactionQualified()
        {
            var buildings = new BuildingStore();
            int b = PlaceCustom(buildings, Faction.Player2, CustomId, complete: false);

            var (director, vars) = BuildDirector(CompletionScenario(faction: 0, CustomId), buildings);
            var world = new EntityWorld();

            director.Tick(world, Fixed.One);
            buildings.ConstructionTimer[b] = Fixed.Zero;
            director.Tick(world, Fixed.One);

            Assert.Equal(0, vars.GetInt("fires", 0)); // P2's completion never reaches a slot-0 subscriber
        }

        [Fact]
        public void BuildingCompleted_LegacyEnumName_StillFires_AndAnotherCustomIdDoesNot()
        {
            var buildings = new BuildingStore();
            int legacy = buildings.Create(FixedVec3.Zero, Faction.Player1, BuildingType.Barracks);
            var (director, vars) = BuildDirector(CompletionScenario(faction: 0, "Barracks"), buildings);
            var world = new EntityWorld();

            director.Tick(world, Fixed.One);
            buildings.ConstructionTimer[legacy] = Fixed.Zero;
            director.Tick(world, Fixed.One);
            Assert.Equal(1, vars.GetInt("fires", 0));

            // A DIFFERENT authored id must not be dragged in by the new lane.
            var other = new BuildingStore();
            int c = PlaceCustom(other, Faction.Player1, CustomId, complete: false);
            var (d2, v2) = BuildDirector(CompletionScenario(faction: 0, "sky_forge"), other);
            d2.Tick(world, Fixed.One);
            other.ConstructionTimer[c] = Fixed.Zero;
            d2.Tick(world, Fixed.One);
            Assert.Equal(0, v2.GetInt("fires", 0));
        }

        // ── The DW-349 re-queue rail carries the authored id across a tick ──────

        [Fact]
        public void BuildingCompleted_CustomId_SurvivesTheRequeueRoundTrip()
        {
            // The authored id is a STRING and the re-queue rail is an int-only DslEventQueue, so a redelivered
            // occurrence would decode back to the enum name alone ("Custom") and silently stop matching. Shape:
            // T subscribes to building_completed(P1, "watchtower") and, when it fires, starts a for_each_batched
            // drip (13 units, batch 5 → 3 ticks) that SUPPRESSES it. A second watchtower completing mid-drip is
            // persisted on the rail and must still match when it is redelivered after the drip completes.
            var decls = new Dictionary<string, (DslValueType Type, VarScope Scope)>(StringComparer.Ordinal)
            {
                ["fires"] = (DslValueType.Int, VarScope.Global),
            };

            var g = new TriggerGraph();
            g.Nodes.Add(new TriggerNode { Id = 0, Name = "T" });
            g.Nodes.Add(new EventNode { Id = 1, Kind = "building_completed", Faction = 0, BuildingType = CustomId });
            g.Nodes.Add(new ActionNode { Id = 2, Kind = "set_variable", Variable = "fires" });
            g.Nodes.Add(new ForEachBatchedNode { Id = 3, Source = "faction_units", Faction = 0, BatchSize = 5 });
            g.ExecEdges.Add(new ExecEdge(1, TriggerGraph.EventExecOutPort, 0, TriggerGraph.TriggerEventInPort));
            g.ExecEdges.Add(new ExecEdge(0, TriggerGraph.TriggerExecOutPort, 2, TriggerGraph.ActionExecInPort));
            g.ExecEdges.Add(new ExecEdge(2, TriggerGraph.ActionExecOutPort, 3, TriggerGraph.ActionExecInPort));
            (int root, _) = ExprParser.Parse("fires + 1", g, decls);
            g.DataEdges.Add(new DataEdge(root, TriggerGraph.ExprDataOutPort, 2, TriggerGraph.ActionValueInPort, DataWireType.Int));

            var buildings = new BuildingStore();
            int a = PlaceCustom(buildings, Faction.Player1, CustomId, complete: false);
            int b = PlaceCustom(buildings, Faction.Player1, CustomId, complete: false);

            var vars  = new DslVarTable();
            var queue = new DslEventQueue();
            var loop  = new DslLoopState();
            var director = new ScenarioDirector(buildings, new ResourceStore(Fixed.Zero), vars, loop, queue);
            director.LoadScenario(new ScenarioData
            {
                Variables = new[] { IntVar("fires") },
                TriggerGraphJson = g.ToCanonicalJson(),
            });

            var world = new EntityWorld();
            for (int i = 0; i < 13; i++)
                world.Create(new FixedVec3(Fixed.FromInt(i), Fixed.Zero, Fixed.Zero), Faction.Player1, Fixed.FromInt(10), Fixed.One);

            director.Tick(world, Fixed.One); // baseline (both towers still building)
            Assert.Equal(0, vars.GetInt("fires", 0));

            buildings.ConstructionTimer[a] = Fixed.Zero;
            director.Tick(world, Fixed.One); // T fires, the 13-id drip starts
            Assert.Equal(1, vars.GetInt("fires", 0));
            Assert.True(loop.RowActive(0));

            buildings.ConstructionTimer[b] = Fixed.Zero;
            director.Tick(world, Fixed.One); // drip 5/13; T suppressed → the completion PERSISTS
            Assert.Equal(1, vars.GetInt("fires", 0));
            Assert.Equal(1, queue.Count);

            director.Tick(world, Fixed.One); // drip 10/13; redelivered → still suppressed → re-persisted
            Assert.Equal(1, vars.GetInt("fires", 0));
            Assert.Equal(1, queue.Count);

            director.Tick(world, Fixed.One); // drip completes → the redelivered completion must STILL match
            Assert.Equal(2, vars.GetInt("fires", 0)); // without the id lane on the rail this stays 1 forever
        }
    }
}
