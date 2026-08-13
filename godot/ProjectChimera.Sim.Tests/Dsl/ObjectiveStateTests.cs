#nullable enable
using System;
using System.Text.Json;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Dsl;
using Xunit;

namespace ProjectChimera.Sim.Tests.Dsl
{
    /// <summary>
    /// Story 7.14 — headless coverage for objective state riding the existing v16 DslVarTable fold: the three action
    /// leaves (show/complete/fail_objective) flip an authored objective's reserved Global-Int var, transitions replay
    /// byte-identically across two seeded runs with NO SimChecksum bump, an unknown objective id rejects at load, and
    /// a scenario carrying none of the new kinds declares no reserved var (the SimChecksum-neutral differential guard).
    /// </summary>
    public class ObjectiveStateTests
    {
        private static (ScenarioDirector Director, DslVarTable Vars) Build(ScenarioData scenario)
        {
            var vars = new DslVarTable();
            var director = new ScenarioDirector(new BuildingStore(), new ResourceStore(Fixed.Zero), vars);
            director.LoadScenario(scenario);
            return (director, vars);
        }

        /// <summary>match_start → one objective action leaf (kind) targeting objectiveId.</summary>
        private static TriggerGraph ObjectiveActionGraph(string kind, string objectiveId)
        {
            var g = new TriggerGraph();
            g.Nodes.Add(new TriggerNode { Id = 0, Name = "t" });
            g.Nodes.Add(new EventNode { Id = 1, Kind = "match_start" });
            NodeBase leaf = kind switch
            {
                NodeKinds.ShowObjective     => new ShowObjectiveNode { Id = 2, ObjectiveId = objectiveId },
                NodeKinds.CompleteObjective => new CompleteObjectiveNode { Id = 2, ObjectiveId = objectiveId },
                _                           => new FailObjectiveNode { Id = 2, ObjectiveId = objectiveId },
            };
            g.Nodes.Add(leaf);
            g.ExecEdges.Add(new ExecEdge(1, TriggerGraph.EventExecOutPort, 0, TriggerGraph.TriggerEventInPort));
            g.ExecEdges.Add(new ExecEdge(0, TriggerGraph.TriggerExecOutPort, 2, TriggerGraph.ActionExecInPort));
            return g;
        }

        private static ScenarioData ScenarioWith(string kind, string targetId, ObjectiveState initial)
            => new()
            {
                Objectives = new[] { new ScenarioObjective { Id = "obj1", Title = "Do the thing", InitialState = initial } },
                TriggerGraphJson = ObjectiveActionGraph(kind, targetId).ToCanonicalJson(),
            };

        [Fact]
        public void ShowObjective_FlipsHiddenToActive()
        {
            (ScenarioDirector d, DslVarTable vt) = Build(ScenarioWith(NodeKinds.ShowObjective, "obj1", ObjectiveState.Hidden));
            Assert.Equal((int)ObjectiveState.Hidden, vt.GetInt("objective:obj1", 0)); // seeded Hidden
            d.Tick(new EntityWorld(), Fixed.One);
            Assert.Equal((int)ObjectiveState.Active, vt.GetInt("objective:obj1", 0));
        }

        [Fact]
        public void CompleteObjective_SetsComplete()
        {
            (ScenarioDirector d, DslVarTable vt) = Build(ScenarioWith(NodeKinds.CompleteObjective, "obj1", ObjectiveState.Active));
            d.Tick(new EntityWorld(), Fixed.One);
            Assert.Equal((int)ObjectiveState.Complete, vt.GetInt("objective:obj1", 0));
        }

        [Fact]
        public void FailObjective_SetsFailed()
        {
            (ScenarioDirector d, DslVarTable vt) = Build(ScenarioWith(NodeKinds.FailObjective, "obj1", ObjectiveState.Active));
            d.Tick(new EntityWorld(), Fixed.One);
            Assert.Equal((int)ObjectiveState.Failed, vt.GetInt("objective:obj1", 0));
        }

        [Fact]
        public void ObjectiveTransition_ReplaysByteIdentical_AcrossTwoSeededRuns_NoSimChecksumBump()
        {
            // No bump — objective state rides the existing v16 DslVarTable fold.
            Assert.Equal(26, SimChecksum.AlgoVersion);

            uint FoldAfterTick()
            {
                (ScenarioDirector d, DslVarTable vt) = Build(ScenarioWith(NodeKinds.CompleteObjective, "obj1", ObjectiveState.Active));
                d.Tick(new EntityWorld(), Fixed.One);
                uint h = 2166136261u;
                vt.FoldInto(ref h, (acc, v) => (acc ^ (uint)v) * 16777619u);
                return h;
            }
            Assert.Equal(FoldAfterTick(), FoldAfterTick()); // two seeded runs fold byte-identically
        }

        [Fact]
        public void UnknownObjectiveId_RejectsAtLoad()
        {
            var scenario = ScenarioWith(NodeKinds.CompleteObjective, "does_not_exist", ObjectiveState.Active);
            var ex = Assert.Throws<JsonException>(() => Build(scenario));
            Assert.Contains("objective_id", ex.Message);
        }

        [Fact]
        public void NoAuthoredObjectives_DeclaresNoReservedVar_SimChecksumNeutralDifferentialGuard()
        {
            // The SimChecksum-neutral differential guard: a scenario carrying no authored objectives (every pre-7.14
            // scenario, incl. every golden) declares NO reserved objective var, so its DslVarTable folds byte-identical
            // to an EMPTY table — the fold gains no globals, so its per-tick SimChecksum is unchanged (no bump, no churn).
            var vars = new DslVarTable();
            var director = new ScenarioDirector(new BuildingStore(), new ResourceStore(Fixed.Zero), vars);
            director.LoadScenario(new ScenarioData()); // default WinCondition, no authored objectives, no triggers

            uint loaded = 2166136261u;
            vars.FoldInto(ref loaded, (acc, v) => (acc ^ (uint)v) * 16777619u);
            uint empty = 2166136261u;
            DslVarTable.FoldEmpty(ref empty, (acc, v) => (acc ^ (uint)v) * 16777619u);
            Assert.Equal(empty, loaded);
        }

        [Fact]
        public void ObjectiveActionTargetingPresentationOnlyDefault_RejectsAtLoad()
        {
            // Finding A (review): a preset-only scenario (no authored objectives) whose trigger targets the synthesized
            // default id ("victory") must REJECT at load — the default is presentation-only (no reserved var), so the
            // action would otherwise be a silent runtime no-op the designer gets no diagnostic for. The validator's and
            // the LoadScenario backstop's valid-target set is the MUTABLE (reserved-var-backed) objectives only.
            var scenario = new ScenarioData
            {
                WinCondition = WinCondition.DestroyAllBuildings, // no authored objectives → default id "victory"
                TriggerGraphJson = ObjectiveActionGraph(NodeKinds.CompleteObjective, ObjectiveResolver.DefaultObjectiveId)
                    .ToCanonicalJson(),
            };
            var ex = Assert.Throws<JsonException>(() => Build(scenario));
            Assert.Contains("objective_id", ex.Message);
        }

        [Fact]
        public void ObjectiveStateOrdinals_ArePinned_DeterminismTripwire()
        {
            // The enum ordinals are cast to int, seeded into a folded reserved var, and folded into SimChecksum. Reordering
            // or inserting a member (a change that round-trips JSON identically via the name converter) would silently
            // move every folded ordinal and break cross-version determinism. Pin them so such an edit fails loudly here.
            Assert.Equal(0, (int)ObjectiveState.Hidden);
            Assert.Equal(1, (int)ObjectiveState.Active);
            Assert.Equal(2, (int)ObjectiveState.Complete);
            Assert.Equal(3, (int)ObjectiveState.Failed);
        }

        [Fact]
        public void DefaultObjective_IsPresentationOnly_NoReservedVarDeclared()
        {
            // A preset-only scenario (no authored objectives) resolves a default objective for DISPLAY but declares no
            // folded var — so complete_objective would be a deterministic no-op AND the checksum stays neutral.
            var vars = new DslVarTable();
            var director = new ScenarioDirector(new BuildingStore(), new ResourceStore(Fixed.Zero), vars);
            director.LoadScenario(new ScenarioData { WinCondition = WinCondition.EliminateAllUnits });
            // The default's reserved var is never declared → reads the undeclared-name default 0, creating no slot.
            uint before = 2166136261u;
            vars.FoldInto(ref before, (acc, v) => (acc ^ (uint)v) * 16777619u);
            _ = vars.GetInt("objective:victory", 0); // a read must not create a slot
            uint after = 2166136261u;
            vars.FoldInto(ref after, (acc, v) => (acc ^ (uint)v) * 16777619u);
            Assert.Equal(before, after);
        }
    }
}
