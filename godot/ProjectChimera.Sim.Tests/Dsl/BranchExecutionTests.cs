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
    /// Story 7.6 — the <c>branch</c> exec container (the I/O-matrix row): a Bool expression on the condition-in
    /// data port picks the then/else chain, the port-0 continuation ALWAYS runs after the taken branch, and —
    /// because branch conditions compile <c>inCondition:false</c> — a TriggerLocal loop variable is legal in a
    /// branch condition (unlike the trigger condition-in).
    /// </summary>
    public class BranchExecutionTests
    {
        private static readonly Dictionary<string, (DslValueType Type, VarScope Scope)> Decls =
            new(StringComparer.Ordinal)
            {
                ["flag"]  = (DslValueType.Bool, VarScope.Global),
                ["a"]     = (DslValueType.Int, VarScope.Global),
                ["b"]     = (DslValueType.Int, VarScope.Global),
                ["c"]     = (DslValueType.Int, VarScope.Global),
                ["arr"]   = (DslValueType.Array, VarScope.Global),
                ["big"]   = (DslValueType.Int, VarScope.Global),
                ["small"] = (DslValueType.Int, VarScope.Global),
                ["v"]     = (DslValueType.Int, VarScope.TriggerLocal),
            };

        private static readonly Dictionary<string, (DslValueType Elem, int Capacity)> ArrayDecls =
            new(StringComparer.Ordinal) { ["arr"] = (DslValueType.Int, 8) };

        private static ScenarioVariable[] Variables(bool flag) => new[]
        {
            new ScenarioVariable { Name = "flag",  Type = DslValueType.Bool,  Scope = VarScope.Global, Initial = flag ? Fixed.One : Fixed.Zero },
            new ScenarioVariable { Name = "a",     Type = DslValueType.Int,   Scope = VarScope.Global },
            new ScenarioVariable { Name = "b",     Type = DslValueType.Int,   Scope = VarScope.Global },
            new ScenarioVariable { Name = "c",     Type = DslValueType.Int,   Scope = VarScope.Global },
            new ScenarioVariable { Name = "arr",   Type = DslValueType.Array, Scope = VarScope.Global, ElementType = DslValueType.Int, Capacity = 8 },
            new ScenarioVariable { Name = "big",   Type = DslValueType.Int,   Scope = VarScope.Global },
            new ScenarioVariable { Name = "small", Type = DslValueType.Int,   Scope = VarScope.Global },
            new ScenarioVariable { Name = "v",     Type = DslValueType.Int,   Scope = VarScope.TriggerLocal },
        };

        private static (ScenarioDirector Director, DslVarTable Vars) Build(ScenarioData scenario)
        {
            var vars = new DslVarTable();
            var director = new ScenarioDirector(new BuildingStore(), new ResourceStore(Fixed.Zero), vars, new DslLoopState());
            director.LoadScenario(scenario);
            return (director, vars);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Branch_TakesTheTruthPath_AndTheContinuationAlwaysRuns(bool flag)
        {
            // trigger → branch(flag) [then: a=1 | else: b=1] → continuation: c=1.
            var g = new TriggerGraph();
            g.Nodes.Add(new TriggerNode { Id = 0, Name = "branch" });
            g.Nodes.Add(new EventNode { Id = 1, Kind = "match_start" });
            g.Nodes.Add(new BranchNode { Id = 2 });
            g.Nodes.Add(new ActionNode { Id = 3, Kind = "set_variable", Variable = "a", Value = 1 });
            g.Nodes.Add(new ActionNode { Id = 4, Kind = "set_variable", Variable = "b", Value = 1 });
            g.Nodes.Add(new ActionNode { Id = 5, Kind = "set_variable", Variable = "c", Value = 1 });
            g.ExecEdges.Add(new ExecEdge(1, TriggerGraph.EventExecOutPort, 0, TriggerGraph.TriggerEventInPort));
            g.ExecEdges.Add(new ExecEdge(0, TriggerGraph.TriggerExecOutPort, 2, TriggerGraph.ActionExecInPort));
            g.ExecEdges.Add(new ExecEdge(2, TriggerGraph.BranchThenOutPort, 3, TriggerGraph.ActionExecInPort));
            g.ExecEdges.Add(new ExecEdge(2, TriggerGraph.BranchElseOutPort, 4, TriggerGraph.ActionExecInPort));
            g.ExecEdges.Add(new ExecEdge(2, TriggerGraph.ActionExecOutPort, 5, TriggerGraph.ActionExecInPort)); // port 0 continuation
            (int condRoot, _) = ExprParser.Parse("flag", g, Decls, ArrayDecls);
            g.DataEdges.Add(new DataEdge(condRoot, TriggerGraph.ExprDataOutPort, 2, TriggerGraph.BranchCondInPort, DataWireType.Boolean));

            var scenario = new ScenarioData { Variables = Variables(flag), TriggerGraphJson = g.ToCanonicalJson() };
            (ScenarioDirector director, DslVarTable vars) = Build(scenario);
            director.Tick(new EntityWorld(), Fixed.One);

            Assert.Equal(flag ? 1 : 0, vars.GetInt("a", 0));
            Assert.Equal(flag ? 0 : 1, vars.GetInt("b", 0));
            Assert.Equal(1, vars.GetInt("c", 0)); // the continuation runs after EITHER branch
        }

        [Fact]
        public void BranchCondition_MayReadTheLoopVar()
        {
            // Push [1,2,3]; for_each arr (v); body: branch (v >= 2) [then: big += 1 | else: small += 1].
            var g = new TriggerGraph();
            g.Nodes.Add(new TriggerNode { Id = 0, Name = "classify" });
            g.Nodes.Add(new EventNode { Id = 1, Kind = "match_start" });
            g.Nodes.Add(new ActionNode { Id = 2, Kind = "array_push", Variable = "arr" });
            g.Nodes.Add(new ActionNode { Id = 3, Kind = "array_push", Variable = "arr" });
            g.Nodes.Add(new ActionNode { Id = 4, Kind = "array_push", Variable = "arr" });
            g.Nodes.Add(new ForEachNode { Id = 5, Source = "array", ArrayName = "arr", LoopVar = "v" });
            g.Nodes.Add(new BranchNode { Id = 6 });
            g.Nodes.Add(new ActionNode { Id = 7, Kind = "set_variable", Variable = "big" });
            g.Nodes.Add(new ActionNode { Id = 8, Kind = "set_variable", Variable = "small" });
            g.ExecEdges.Add(new ExecEdge(1, TriggerGraph.EventExecOutPort, 0, TriggerGraph.TriggerEventInPort));
            g.ExecEdges.Add(new ExecEdge(0, TriggerGraph.TriggerExecOutPort, 2, TriggerGraph.ActionExecInPort));
            g.ExecEdges.Add(new ExecEdge(2, TriggerGraph.ActionExecOutPort, 3, TriggerGraph.ActionExecInPort));
            g.ExecEdges.Add(new ExecEdge(3, TriggerGraph.ActionExecOutPort, 4, TriggerGraph.ActionExecInPort));
            g.ExecEdges.Add(new ExecEdge(4, TriggerGraph.ActionExecOutPort, 5, TriggerGraph.ActionExecInPort));
            g.ExecEdges.Add(new ExecEdge(5, TriggerGraph.ForEachBodyOutPort, 6, TriggerGraph.ActionExecInPort));
            g.ExecEdges.Add(new ExecEdge(6, TriggerGraph.BranchThenOutPort, 7, TriggerGraph.ActionExecInPort));
            g.ExecEdges.Add(new ExecEdge(6, TriggerGraph.BranchElseOutPort, 8, TriggerGraph.ActionExecInPort));

            void WireValue(string text, int actionId)
            {
                (int root, _) = ExprParser.Parse(text, g, Decls, ArrayDecls);
                g.DataEdges.Add(new DataEdge(root, TriggerGraph.ExprDataOutPort, actionId, TriggerGraph.ActionValueInPort, DataWireType.Int));
            }
            WireValue("1", 2);
            WireValue("2", 3);
            WireValue("3", 4);
            WireValue("big + 1", 7);
            WireValue("small + 1", 8);
            (int condRoot, _) = ExprParser.Parse("v >= 2", g, Decls, ArrayDecls); // a TriggerLocal read — LEGAL here
            g.DataEdges.Add(new DataEdge(condRoot, TriggerGraph.ExprDataOutPort, 6, TriggerGraph.BranchCondInPort, DataWireType.Boolean));

            var scenario = new ScenarioData { Variables = Variables(false), TriggerGraphJson = g.ToCanonicalJson() };
            (ScenarioDirector director, DslVarTable vars) = Build(scenario);
            director.Tick(new EntityWorld(), Fixed.One);

            Assert.Equal(2, vars.GetInt("big", 0));   // 2 and 3
            Assert.Equal(1, vars.GetInt("small", 0)); // 1
        }

        // ── Exec-walk recursion seatbelt (review P9) ────────────────────────────

        /// <summary>Build a trigger whose action chain nests <paramref name="depth"/> BranchNodes, each hanging
        /// off the previous branch's THEN port. Branch nesting does NOT count toward MaxLoopNesting, so before
        /// the P9 seatbelt a hostile graph JSON thousands of containers deep drove BuildExecutionOrder's
        /// WalkChain recursion into an uncatchable StackOverflowException BEFORE any DslLoopGate check ran
        /// (BuildExecutionOrder builds the very view the gate walks).</summary>
        private static TriggerGraph NestedBranchGraph(int depth)
        {
            var g = new TriggerGraph();
            g.Nodes.Add(new TriggerNode { Id = 0, Name = "deep" });
            g.Nodes.Add(new EventNode { Id = 1, Kind = "match_start" });
            g.ExecEdges.Add(new ExecEdge(1, TriggerGraph.EventExecOutPort, 0, TriggerGraph.TriggerEventInPort));
            int prevId = 0, prevPort = TriggerGraph.TriggerExecOutPort;
            for (int i = 0; i < depth; i++)
            {
                int id = 2 + i;
                g.Nodes.Add(new BranchNode { Id = id });
                g.ExecEdges.Add(new ExecEdge(prevId, prevPort, id, TriggerGraph.ActionExecInPort));
                prevId   = id;
                prevPort = TriggerGraph.BranchThenOutPort; // nest the next branch INSIDE this one's then chain
            }
            return g;
        }

        [Fact]
        public void HostileContainerNesting_BeyondMaxExecWalkDepth_IsALocatedRejectNotAStackOverflow()
        {
            TriggerGraph g = NestedBranchGraph(DslBounds.MaxExecWalkDepth + 8);
            var ex = Assert.Throws<System.Text.Json.JsonException>(() => g.BuildExecutionOrder());
            // Located AND names the violated constant (the module's load-gate error contract).
            Assert.Contains($"DslBounds.MaxExecWalkDepth={DslBounds.MaxExecWalkDepth}", ex.Message);
        }

        [Fact]
        public void LegallyNestedContainers_PassTheExecWalkDepthSeatbelt()
        {
            // Well within the seatbelt (and within anything the nesting/cost gates would admit) — the walk
            // resolves the whole nested view without a reject.
            TriggerGraph g = NestedBranchGraph(8);
            List<TriggerGraph.TriggerExec> execs = g.BuildExecutionOrder();
            TriggerGraph.TriggerExec ex = Assert.Single(execs);
            Assert.IsType<BranchNode>(Assert.Single(ex.Items).Node);
        }

        [Fact]
        public void BranchWithoutCondition_IsALocatedLoadReject()
        {
            var g = new TriggerGraph();
            g.Nodes.Add(new TriggerNode { Id = 0, Name = "bare" });
            g.Nodes.Add(new EventNode { Id = 1, Kind = "match_start" });
            g.Nodes.Add(new BranchNode { Id = 2 });
            g.ExecEdges.Add(new ExecEdge(1, TriggerGraph.EventExecOutPort, 0, TriggerGraph.TriggerEventInPort));
            g.ExecEdges.Add(new ExecEdge(0, TriggerGraph.TriggerExecOutPort, 2, TriggerGraph.ActionExecInPort));

            var scenario = new ScenarioData { Variables = Variables(false), TriggerGraphJson = g.ToCanonicalJson() };
            var director = new ScenarioDirector(new BuildingStore(), new ResourceStore(Fixed.Zero), new DslVarTable(), new DslLoopState());
            var ex = Assert.Throws<System.Text.Json.JsonException>(() => director.LoadScenario(scenario));
            Assert.Contains("condition-in", ex.Message);
        }
    }
}
