#nullable enable
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Sim;
using ProjectChimera.Dsl;
using Xunit;

namespace ProjectChimera.Sim.Tests.Dsl
{
    /// <summary>
    /// DW-348 — ORPHAN loop/array nodes (not exec-reachable from any trigger chain) get the SAME per-node
    /// semantic checks the exec walk applies: an orphan <c>for_each</c> referencing an undeclared array, a bad
    /// <c>up_to</c>, a loop-var rule violation, or an array action naming an undeclared array all reject at
    /// BOTH load gates instead of validating silently as inert canvas nodes. The T3 WIP posture is preserved:
    /// an individually-VALID disconnected node still passes (rejection targets malformed content, not
    /// disconnection).
    ///
    /// DW-359 — pins the loop-var vs declared-variable collision guard: a <c>loop_var</c> naming a declared
    /// Global (or PerPlayer/Array) variable rejects via <c>DslLoopGate.CheckLoopVar</c>'s TriggerLocal-scope
    /// requirement — declaration names are unique at both gates, so the loop binding can never silently shadow
    /// a declared non-local variable. The orphan variant of that collision was the one silent path (it skipped
    /// the loop gate entirely) and is closed by the DW-348 orphan pass.
    /// </summary>
    public class DslLoopGateOrphanTests
    {
        private static ScenarioData ModelWithGraph(string graphJson) => new ScenarioData
        {
            Id = "m", DisplayName = "M", TerrainRef = "", MapBounds = 120f,
            WinCondition = WinCondition.DestroyAllBuildings,
            PlayerSlots = new[] { new ScenarioPlayerSlot { Slot = 0 } },
            TriggerGraphJson = graphJson,
        };

        private static ValidationResult Validate(ScenarioData model) => new ScenarioValidator().Validate(model);

        /// <summary>A minimal WIRED single-trigger graph (event 1 → trigger 0 → action 2) the orphan under test
        /// is appended to, so the scenario always has a normal reachable chain alongside the orphan.</summary>
        private static TriggerGraph WiredGraph()
        {
            var g = new TriggerGraph();
            g.Nodes.Add(new TriggerNode { Id = 0, Name = "t" });
            g.Nodes.Add(new EventNode { Id = 1, Kind = "match_start" });
            g.Nodes.Add(new ActionNode { Id = 2, Kind = "display_message", Text = "hi" });
            g.ExecEdges.Add(new ExecEdge(1, TriggerGraph.EventExecOutPort, 0, TriggerGraph.TriggerEventInPort));
            g.ExecEdges.Add(new ExecEdge(0, TriggerGraph.TriggerExecOutPort, 2, TriggerGraph.ActionExecInPort));
            return g;
        }

        [Fact]
        public void OrphanForEach_ReferencingUndeclaredArray_Rejects() // the DW-348 headline case
        {
            var g = WiredGraph();
            g.Nodes.Add(new ForEachNode { Id = 10, Source = "array", ArrayName = "nope", UpTo = 0, LoopVar = "lv" });

            ValidationResult r = Validate(ModelWithGraph(g.ToCanonicalJson()));
            Assert.False(r.Ok);
            Assert.Contains("for_each node 10", r.Error);
            Assert.Contains("'array_name' to name a declared Array variable", r.Error);
        }

        [Fact]
        public void OrphanEntityForEach_MissingUpToCap_Rejects()
        {
            var g = WiredGraph();
            g.Nodes.Add(new ForEachNode { Id = 10, Source = "faction_units", Faction = -1, UpTo = 0 });

            ValidationResult r = Validate(ModelWithGraph(g.ToCanonicalJson()));
            Assert.False(r.Ok);
            Assert.Contains("for_each node 10", r.Error);
            Assert.Contains("requires an explicit 'up_to' cap", r.Error);
        }

        [Fact]
        public void OrphanForEach_UndeclaredLoopVar_Rejects()
        {
            var g = WiredGraph();
            g.Nodes.Add(new ForEachNode { Id = 10, Source = "faction_units", Faction = -1, UpTo = 3, LoopVar = "ghost" });

            ValidationResult r = Validate(ModelWithGraph(g.ToCanonicalJson()));
            Assert.False(r.Ok);
            Assert.Contains("loop_var 'ghost' is not a declared variable", r.Error);
        }

        [Fact]
        public void OrphanForEachBatched_BadBatchSize_Rejects()
        {
            var g = WiredGraph();
            g.Nodes.Add(new ForEachBatchedNode { Id = 10, Source = "faction_units", Faction = -1, BatchSize = 0 });

            ValidationResult r = Validate(ModelWithGraph(g.ToCanonicalJson()));
            Assert.False(r.Ok);
            Assert.Contains("for_each_batched node 10", r.Error);
            Assert.Contains("batch_size 0 is out of range", r.Error);
        }

        [Fact]
        public void OrphanArrayAction_ReferencingUndeclaredArray_Rejects()
        {
            var g = WiredGraph();
            g.Nodes.Add(new ActionNode { Id = 10, Kind = "array_push", Variable = "nope" });

            ValidationResult r = Validate(ModelWithGraph(g.ToCanonicalJson()));
            Assert.False(r.Ok);
            Assert.Contains("action node 10 (array_push)", r.Error);
            Assert.Contains("must name a declared Array variable", r.Error);
        }

        [Fact]
        public void OrphanChainBody_ArrayActionAlsoChecked() // orphan CHAINS are covered node-by-node
        {
            var g = WiredGraph();
            // Orphan for_each 10 (individually valid) whose body chain carries a bad array action 11.
            g.Nodes.Add(new ForEachNode { Id = 10, Source = "faction_units", Faction = -1, UpTo = 3 });
            g.Nodes.Add(new ActionNode { Id = 11, Kind = "array_clear", Variable = "nope" });
            g.ExecEdges.Add(new ExecEdge(10, TriggerGraph.ForEachBodyOutPort, 11, TriggerGraph.ActionExecInPort));

            ValidationResult r = Validate(ModelWithGraph(g.ToCanonicalJson()));
            Assert.False(r.Ok);
            Assert.Contains("action node 11 (array_clear)", r.Error);
            Assert.Contains("must name a declared Array variable", r.Error);
        }

        [Fact]
        public void OrphanForEach_IndividuallyValid_StillPasses() // the T3 WIP posture is preserved
        {
            var g = WiredGraph();
            g.Nodes.Add(new ForEachNode { Id = 10, Source = "faction_units", Faction = -1, UpTo = 3 });
            g.Nodes.Add(new ForEachBatchedNode { Id = 11, Source = "faction_units", Faction = -1, BatchSize = 2 });

            ValidationResult r = Validate(ModelWithGraph(g.ToCanonicalJson()));
            Assert.True(r.Ok, r.Error);
        }

        [Fact]
        public void OrphanForEach_UndeclaredArray_RejectsAtBothGates() // gate/backstop parity for the orphan pass
        {
            var g = WiredGraph();
            g.Nodes.Add(new ForEachNode { Id = 10, Source = "array", ArrayName = "nope", UpTo = 0, LoopVar = "lv" });
            ScenarioData model = ModelWithGraph(g.ToCanonicalJson());

            ValidationResult r = Validate(model);
            Assert.False(r.Ok);
            Assert.Contains("'array_name' to name a declared Array variable", r.Error);

            var host = SimulationHost.Create(NullLogSink.Instance, new FactionRegistry(2),
                new FactionDefinition(), new FactionDefinition());
            var ex = Assert.Throws<System.Text.Json.JsonException>(() => host.ScenarioDirector.LoadScenario(model));
            Assert.Contains("'array_name' to name a declared Array variable", ex.Message);
        }

        // ── DW-359 — loop-var vs declared-variable collision. The loop_var must itself be a DECLARED
        //    TriggerLocal variable and declaration names are unique at both gates, so a loop_var naming a
        //    declared Global/PerPlayer/Array variable ALWAYS rejects (scope), reachable or orphan — the loop
        //    binding can never silently shadow a declared non-local variable inside the body. ──

        [Fact]
        public void ReachableForEach_LoopVarCollidingWithADeclaredGlobal_Rejects()
        {
            var g = WiredGraph();
            g.Nodes.Add(new ForEachNode { Id = 10, Source = "faction_units", Faction = -1, UpTo = 3, LoopVar = "gold" });
            g.ExecEdges.Add(new ExecEdge(2, TriggerGraph.ActionExecOutPort, 10, TriggerGraph.ActionExecInPort));

            ScenarioData model = ModelWithGraph(g.ToCanonicalJson());
            model.Variables = new[]
            {
                new ScenarioVariable { Name = "gold", Type = DslValueType.Int, Scope = VarScope.Global },
            };

            ValidationResult r = Validate(model);
            Assert.False(r.Ok);
            Assert.Contains("loop_var 'gold' must be TriggerLocal-scoped", r.Error);
            Assert.Contains("Global", r.Error);
        }

        [Fact]
        public void OrphanForEach_LoopVarCollidingWithADeclaredGlobal_Rejects() // the one previously-silent path
        {
            // Before the DW-348 orphan pass this loaded CLEAN: the orphan loop skipped CheckLoopVar entirely,
            // so the Global-name collision (DW-359's scenario) was never examined.
            var g = WiredGraph();
            g.Nodes.Add(new ForEachNode { Id = 10, Source = "faction_units", Faction = -1, UpTo = 3, LoopVar = "gold" });

            ScenarioData model = ModelWithGraph(g.ToCanonicalJson());
            model.Variables = new[]
            {
                new ScenarioVariable { Name = "gold", Type = DslValueType.Int, Scope = VarScope.Global },
            };

            ValidationResult r = Validate(model);
            Assert.False(r.Ok);
            Assert.Contains("loop_var 'gold' must be TriggerLocal-scoped", r.Error);
        }

        [Fact]
        public void ReachableForEach_LoopVarCollidingWithADeclaredPerPlayer_Rejects()
        {
            var g = WiredGraph();
            g.Nodes.Add(new ForEachNode { Id = 10, Source = "faction_units", Faction = -1, UpTo = 3, LoopVar = "score" });
            g.ExecEdges.Add(new ExecEdge(2, TriggerGraph.ActionExecOutPort, 10, TriggerGraph.ActionExecInPort));

            ScenarioData model = ModelWithGraph(g.ToCanonicalJson());
            model.Variables = new[]
            {
                new ScenarioVariable { Name = "score", Type = DslValueType.Int, Scope = VarScope.PerPlayer },
            };

            ValidationResult r = Validate(model);
            Assert.False(r.Ok);
            Assert.Contains("loop_var 'score' must be TriggerLocal-scoped", r.Error);
        }

        [Fact]
        public void DuplicateDeclaration_CannotSmuggleALoopVarGlobalCollision() // the uniqueness half of DW-359
        {
            // TriggerLocal "gold" declared FIRST, Global "gold" second: without the duplicate-name reject the
            // declMap would carry the TriggerLocal entry, the loop var would gate clean, and the Global would be
            // silently shadowed for every trigger-context read. Both gates reject the duplicate itself.
            var g = WiredGraph();
            g.Nodes.Add(new ForEachNode { Id = 10, Source = "faction_units", Faction = -1, UpTo = 3, LoopVar = "gold" });
            g.ExecEdges.Add(new ExecEdge(2, TriggerGraph.ActionExecOutPort, 10, TriggerGraph.ActionExecInPort));

            ScenarioData model = ModelWithGraph(g.ToCanonicalJson());
            model.Variables = new[]
            {
                new ScenarioVariable { Name = "gold", Type = DslValueType.Int, Scope = VarScope.TriggerLocal },
                new ScenarioVariable { Name = "gold", Type = DslValueType.Int, Scope = VarScope.Global },
            };

            ValidationResult r = Validate(model);
            Assert.False(r.Ok);
            Assert.Contains("duplicate", r.Error);

            var host = SimulationHost.Create(NullLogSink.Instance, new FactionRegistry(2),
                new FactionDefinition(), new FactionDefinition());
            var ex = Assert.Throws<System.Text.Json.JsonException>(() => host.ScenarioDirector.LoadScenario(model));
            Assert.Contains("declared more than once", ex.Message);
        }
    }
}
