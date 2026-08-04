#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ProjectChimera.AI;                 // AiDifficulty
using ProjectChimera.Core.Definitions;   // ScenarioData, ScenarioCustomEvent, ScenarioVariable
using ProjectChimera.Core.Skirmish;      // SkirmishSetup, SetupSlot, SlotKind, FactionEntry, SkirmishSetupToScenario
using ProjectChimera.Dsl;                // TriggerGraph, node types, VarScope
using Xunit;

namespace ProjectChimera.Sim.Tests.Skirmish
{
    /// <summary>
    /// DW-609 — the two per-slot channels the DW-458 prune-and-reconcile did not reach:
    /// <c>ScenarioData.TriggerGraphJson</c> (graph-IR node <c>Faction</c> fields) and
    /// <c>ScenarioCustomEvent.AllowedRaisers</c>. Both rode <c>ShallowClone</c> verbatim.
    ///
    /// <para>The severity is NOT staleness. <c>ScenarioValidator</c> accepts non-contiguous slot ordinals and the
    /// editor produces them first-class (<c>RemoveStartSlot</c> removes by value with no renumber), so on a
    /// {0,2,5} map <c>Build</c>'s positional pairing renumbers survivors {0→0, 2→1, 5→2} — an unreconciled ref to
    /// authored slot 2 then lands on the LIVE player who is now ordinal 2 (authored 5). A graph trigger fires for
    /// the wrong player, and a raise authorization silently transfers: the intended raiser is dropped at
    /// <c>ScenarioDirector</c>'s <c>Array.IndexOf</c> auth check with no error, while an unintended player
    /// inherits it. These tests pin the invariant every modern RTS holds — a reference designates the player the
    /// author meant, or nobody, but never a DIFFERENT player.</para>
    /// </summary>
    public class SkirmishGraphReconcileTests
    {
        // ── Builders ────────────────────────────────────────────────────────────────

        private static IReadOnlyList<FactionEntry> Factions() => new List<FactionEntry>
        {
            new()
            {
                Id = "alpha", DisplayName = "alpha", ResPath = "res://factions/alpha_faction.json",
                Units = new List<FactionUnitEntry> { new() { Id = "worker", Category = "Worker" } },
            },
        };

        private static SetupSlot Human(int slot) => new() { Slot = slot, Kind = SlotKind.Human, FactionId = "alpha" };
        private static SetupSlot Ai(int slot)    => new() { Slot = slot, Kind = SlotKind.Ai,    FactionId = "alpha", Ai = AiDifficulty.Normal };

        /// <summary>A launch with THREE active players — pairs against the first three base start positions.</summary>
        private static SkirmishSetup Setup3p() => new() { MapId = "m1", Slots = new List<SetupSlot> { Human(0), Ai(1), Ai(2) } };

        /// <summary>A launch with TWO active players — on a 3-start map this DROPS the third base slot.</summary>
        private static SkirmishSetup Setup1v1() => new() { MapId = "m1", Slots = new List<SetupSlot> { Human(0), Ai(1) } };

        private static ScenarioData BaseMap(params int[] slotOrdinals)
        {
            var m = new ScenarioData { Id = "m1", DisplayName = "m1", MapBounds = 120f };
            m.PlayerSlots = slotOrdinals.Select(o => new ScenarioPlayerSlot
            {
                Slot = o, FactionJson = "res://factions/alpha_faction.json",
                StartOre = 200f, BaseX = -45f + o * 15f, BaseZ = 0f,
            }).ToArray();
            return m;
        }

        private static string GraphWith(params NodeBase[] nodes)
        {
            var g = new TriggerGraph();
            g.Nodes.AddRange(nodes);
            return g.ToCanonicalJson();
        }

        private static IReadOnlyList<NodeBase> NodesOf(string? json) =>
            TriggerGraph.FromJson(json!).Nodes.OrderBy(n => n.Id).ToList();

        // ── The wrong-player defect: a SURVIVING slot that got renumbered ────────────

        [Fact]
        public void GraphNode_RefToRenumberedSurvivor_FollowsThatPlayer_NotTheOrdinal()
        {
            // Authored {0,2,5}; a 3-player launch renumbers {0→0, 2→1, 5→2}. A graph event keyed to authored
            // slot 2 must follow that player to ordinal 1 — pre-fix it stayed 2, i.e. the player authored as 5.
            ScenarioData baseMap = BaseMap(0, 2, 5);
            baseMap.TriggerGraphJson = GraphWith(new EventNode { Id = 0, Kind = "unit_dies", Faction = 2 });

            ScenarioData built = SkirmishSetupToScenario.Build(Setup3p(), baseMap, Factions());

            var ev = Assert.IsType<EventNode>(NodesOf(built.TriggerGraphJson)[0]);
            Assert.Equal(1, ev.Faction);
        }

        [Fact]
        public void AllowedRaisers_RefToRenumberedSurvivor_FollowsThatPlayer_NotTheOrdinal()
        {
            // The authorization-transfer half: authored raiser {2} must become {1}. Pre-fix it stayed {2},
            // handing raise rights to the player authored as slot 5 and silently revoking them from slot 2's.
            ScenarioData baseMap = BaseMap(0, 2, 5);
            baseMap.CustomEvents = new[]
            {
                new ScenarioCustomEvent { Name = "wave_cleared", AllowedRaisers = new[] { 2 } },
            };

            ScenarioData built = SkirmishSetupToScenario.Build(Setup3p(), baseMap, Factions());

            Assert.Equal(new[] { 1 }, built.CustomEvents![0].AllowedRaisers);
        }

        // ── Dropped slots: inert, never re-pointed at a live player ──────────────────

        [Fact]
        public void GraphNode_RefToDroppedSlot_IsCanonicalizedToTheVacantSeat()
        {
            // NORMALIZATION, not a defect fix — be precise about which is which. A dropped ordinal is always
            // GREATER than all k paired ordinals (the pairing takes the k lowest), so d ≥ k always: a dropped
            // reference was already outside the live span and already inert before this change. What the redirect
            // buys is a SINGLE representation of "absent" (every dropped ref collapses to exactly k) instead of an
            // arbitrary scatter of authored ordinals, so the vacant-seat invariant is checkable rather than
            // incidental. Asserting the exact value — the ≥-span form of this assertion is vacuous.
            ScenarioData baseMap = BaseMap(0, 2, 5);
            baseMap.TriggerGraphJson = GraphWith(new ActionNode { Id = 0, Kind = "spawn_unit", UnitId = "worker", Faction = 5 });

            ScenarioData built = SkirmishSetupToScenario.Build(Setup1v1(), baseMap, Factions());

            var act = Assert.IsType<ActionNode>(NodesOf(built.TriggerGraphJson)[0]);
            Assert.Equal(2, built.PlayerSlots.Length);
            Assert.Equal(2, act.Faction);                  // exactly the vacant seat, not the authored 5
            Assert.True(act.Faction < 8, "must stay inside the engine faction-slot ceiling");
        }

        [Fact]
        public void AllowedRaisers_RefToDroppedSlot_IsRemoved_LeavingSystemRaiseOnly()
        {
            ScenarioData baseMap = BaseMap(0, 1, 2);
            baseMap.CustomEvents = new[]
            {
                new ScenarioCustomEvent { Name = "wave_cleared", AllowedRaisers = new[] { 0, 2 } },
            };

            ScenarioData built = SkirmishSetupToScenario.Build(Setup1v1(), baseMap, Factions());

            Assert.Equal(new[] { 0 }, built.CustomEvents![0].AllowedRaisers);
        }

        [Fact]
        public void AllowedRaisers_SystemRaiser_IsNeverTreatedAsASlot()
        {
            // −1 is "system raise", always legal — it must survive untouched rather than being mapped or dropped.
            ScenarioData baseMap = BaseMap(0, 1, 2);
            baseMap.CustomEvents = new[]
            {
                new ScenarioCustomEvent { Name = "e", AllowedRaisers = new[] { -1, 2 } },
            };

            ScenarioData built = SkirmishSetupToScenario.Build(Setup1v1(), baseMap, Factions());

            Assert.Equal(new[] { -1 }, built.CustomEvents![0].AllowedRaisers);
        }

        // ── Kind rulebook: identical to the flat channel ─────────────────────────────

        [Fact]
        public void GraphNode_SystemKeyedKind_IsNotTreatedAsASlotRef()
        {
            // timer_expires is system-keyed — its Faction is not a player reference, so a renumber must not
            // rewrite it (the flat channel's FactionKeyedEventKinds rule, applied to the graph channel).
            ScenarioData baseMap = BaseMap(0, 2, 5);
            baseMap.TriggerGraphJson = GraphWith(new EventNode { Id = 0, Kind = "timer_expires", TimerName = "t", Faction = 2 });

            ScenarioData built = SkirmishSetupToScenario.Build(Setup3p(), baseMap, Factions());

            var ev = Assert.IsType<EventNode>(NodesOf(built.TriggerGraphJson)[0]);
            Assert.Equal(2, ev.Faction);
        }

        [Fact]
        public void GraphNode_PerPlayerVariableRef_IsRemapped_BareVariableIsNot()
        {
            // variable_comparison is slot-keyed ONLY for a PerPlayer-declared variable — the flat channel's rule.
            ScenarioData baseMap = BaseMap(0, 2, 5);
            baseMap.Variables = new[]
            {
                new ScenarioVariable { Name = "kills", Scope = VarScope.PerPlayer, Type = DslValueType.Int },
                new ScenarioVariable { Name = "wave",  Scope = VarScope.Global,    Type = DslValueType.Int },
            };
            baseMap.TriggerGraphJson = GraphWith(
                new ConditionNode { Id = 0, Kind = "variable_comparison", Variable = "kills", Faction = 2 },
                new ConditionNode { Id = 1, Kind = "variable_comparison", Variable = "wave",  Faction = 2 });

            ScenarioData built = SkirmishSetupToScenario.Build(Setup3p(), baseMap, Factions());
            IReadOnlyList<NodeBase> nodes = NodesOf(built.TriggerGraphJson);

            Assert.Equal(1, Assert.IsType<ConditionNode>(nodes[0]).Faction); // PerPlayer → follows the player
            Assert.Equal(2, Assert.IsType<ConditionNode>(nodes[1]).Faction); // Global   → not a slot ref
        }

        [Fact]
        public void GraphNode_NegativeFaction_IsBareOrAnyFaction_AndIsNeverRemapped()
        {
            // −1 means "any faction / bare read" on the iteration + expression nodes; it is not a slot.
            ScenarioData baseMap = BaseMap(0, 2, 5);
            baseMap.TriggerGraphJson = GraphWith(
                new ForEachNode    { Id = 0, Source = "faction_units", Faction = -1 },
                new OrderUnitsNode { Id = 1, Command = "move", Faction = -1 });

            ScenarioData built = SkirmishSetupToScenario.Build(Setup3p(), baseMap, Factions());
            IReadOnlyList<NodeBase> nodes = NodesOf(built.TriggerGraphJson);

            Assert.Equal(-1, Assert.IsType<ForEachNode>(nodes[0]).Faction);
            Assert.Equal(-1, Assert.IsType<OrderUnitsNode>(nodes[1]).Faction);
        }

        [Fact]
        public void GraphNode_IterationFilterSlot_IsRemappedRegardlessOfKind()
        {
            // ForEach/OrderUnits document Faction ≥ 0 as a concrete slot filter — it must follow the renumber
            // or a "for each of player 2's units" loop silently iterates a different player's army.
            ScenarioData baseMap = BaseMap(0, 2, 5);
            baseMap.TriggerGraphJson = GraphWith(
                new ForEachBatchedNode { Id = 0, Source = "faction_units", Faction = 2 },
                new ExprVarNode        { Id = 1, Name = "kills",   Faction = 2 });

            ScenarioData built = SkirmishSetupToScenario.Build(Setup3p(), baseMap, Factions());
            IReadOnlyList<NodeBase> nodes = NodesOf(built.TriggerGraphJson);

            Assert.Equal(1, Assert.IsType<ForEachBatchedNode>(nodes[0]).Faction);
            Assert.Equal(1, Assert.IsType<ExprVarNode>(nodes[1]).Faction);
        }

        // ── The identity path stays byte-identical ──────────────────────────────────

        [Fact]
        public void IdentityLaunch_LeavesBothChannelsReferenceIdentical()
        {
            // A 2-start map launched 1v1 keeps every ordinal — the reconcile is skipped wholesale, so both
            // channels must come through as the SAME references (no re-serialization, no scenario-byte movement).
            ScenarioData baseMap = BaseMap(0, 1);
            baseMap.TriggerGraphJson = GraphWith(new EventNode { Id = 0, Kind = "unit_dies", Faction = 1 });
            baseMap.CustomEvents = new[] { new ScenarioCustomEvent { Name = "e", AllowedRaisers = new[] { 0, 1 } } };

            ScenarioData built = SkirmishSetupToScenario.Build(Setup1v1(), baseMap, Factions());

            Assert.Same(baseMap.TriggerGraphJson, built.TriggerGraphJson);
            Assert.Same(baseMap.CustomEvents, built.CustomEvents);
        }

        [Fact]
        public void NonIdentityLaunch_WithNothingToRemap_StillLeavesTheGraphStringVerbatim()
        {
            // A graph that carries no slot reference must not be re-serialized just because OTHER channels were
            // reconciled — ToCanonicalJson would reorder an authored-but-uncanonical graph and move bytes.
            ScenarioData baseMap = BaseMap(0, 1, 2);
            baseMap.TriggerGraphJson = GraphWith(new EventNode { Id = 0, Kind = "match_start", Faction = 0 });

            ScenarioData built = SkirmishSetupToScenario.Build(Setup1v1(), baseMap, Factions());

            Assert.Same(baseMap.TriggerGraphJson, built.TriggerGraphJson);
        }

        [Fact]
        public void MalformedGraphJson_IsPassedThroughForTheLoadGateToReject()
        {
            // Fail-soft: the launch transform has no way to report a located error; the load gate does.
            ScenarioData baseMap = BaseMap(0, 1, 2);
            baseMap.TriggerGraphJson = "{ not valid json";

            ScenarioData built = SkirmishSetupToScenario.Build(Setup1v1(), baseMap, Factions());

            Assert.Same(baseMap.TriggerGraphJson, built.TriggerGraphJson);
        }

        [Fact]
        public void Build_NeverMutatesTheBaseMapsGraphOrEvents()
        {
            ScenarioData baseMap = BaseMap(0, 2, 5);
            string authoredGraph = GraphWith(new EventNode { Id = 0, Kind = "unit_dies", Faction = 2 });
            baseMap.TriggerGraphJson = authoredGraph;
            baseMap.CustomEvents = new[] { new ScenarioCustomEvent { Name = "e", AllowedRaisers = new[] { 2 } } };

            SkirmishSetupToScenario.Build(Setup3p(), baseMap, Factions());

            Assert.Equal(authoredGraph, baseMap.TriggerGraphJson);
            Assert.Equal(new[] { 2 }, baseMap.CustomEvents[0].AllowedRaisers);
        }

        // ── The class guard ─────────────────────────────────────────────────────────

        [Fact]
        public void EveryNodeTypeWithAFactionField_IsCoveredByTheReconcile()
        {
            // THE POINT OF THIS FILE. DW-458 was correct for the channels it knew about and silently wrong for
            // the ones it did not — and nothing failed, which is why the defect survived a full review sweep.
            // A new graph node kind carrying a slot reference must not be able to reintroduce that silence: this
            // fails the moment such a type exists without being listed in SlotCarryingGraphNodeTypes (and the
            // reconcile's switch is written from that same list).
            var declared = new HashSet<System.Type>(SkirmishSetupToScenario.SlotCarryingGraphNodeTypes);

            List<System.Type> actual = typeof(NodeBase).Assembly.GetTypes()
                .Where(t => !t.IsAbstract && typeof(NodeBase).IsAssignableFrom(t))
                .Where(t => t.GetProperty("Faction", BindingFlags.Public | BindingFlags.Instance)?.PropertyType == typeof(int))
                .ToList();

            List<System.Type> uncovered = actual.Where(t => !declared.Contains(t)).ToList();
            Assert.True(uncovered.Count == 0,
                "graph node type(s) carry a Faction slot reference but are not handled by the DW-609 reconcile — " +
                "a skirmish launch that renumbers slots will silently point them at the wrong player: " +
                string.Join(", ", uncovered.Select(t => t.Name)));

            // And the declared list must not rot in the other direction (a type renamed/removed).
            List<System.Type> stale = declared.Where(t => !actual.Contains(t)).ToList();
            Assert.True(stale.Count == 0, "declared but no longer a Faction-carrying node: " + string.Join(", ", stale.Select(t => t.Name)));
        }
    }
}
