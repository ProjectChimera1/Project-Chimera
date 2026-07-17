#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ProjectChimera.Core;
using ProjectChimera.Dsl;
using Xunit;

namespace ProjectChimera.Sim.Tests.Dsl
{
    /// <summary>
    /// Story 7.5 — the IR surface of the custom-event layer: closed-registry converter round-trips + located
    /// rejects for the three new kinds (<c>raise_event</c>, <c>custom_event</c>, <c>expr_event_param</c>),
    /// canonical byte-identity, the <c>ToFlat</c> fail-closed guard (graph-channel-only — never lossy lowering),
    /// and the <c>event.&lt;param&gt;</c> parse/compile/eval matrix (undeclared param, no-single-subscription
    /// reject, ref-as-Int reads, total no-frame semantics).
    /// </summary>
    public class CustomEventIrTests
    {
        private static readonly Dictionary<string, (DslValueType Type, VarScope Scope)> NoVars =
            new(StringComparer.Ordinal);

        private static readonly Dictionary<string, (int Slot, DslValueType Type)> WaveParams =
            new(StringComparer.Ordinal)
            {
                ["count"] = (0, DslValueType.Int),
                ["rate"]  = (1, DslValueType.Fixed),
                ["armed"] = (2, DslValueType.Bool),
            };

        // ── Converter round-trips ────────────────────────────────────────────────

        [Fact]
        public void ThreeNewKinds_RoundTripByteIdentically_ThroughCanonicalJson()
        {
            var g = new TriggerGraph();
            g.Nodes.Add(new TriggerNode { Id = 0, Name = "t" });
            g.Nodes.Add(new EventNode { Id = 1, Kind = "custom_event", EventName = "wave_start" });
            g.ExecEdges.Add(new ExecEdge(1, TriggerGraph.EventExecOutPort, 0, TriggerGraph.TriggerEventInPort));
            g.Nodes.Add(new RaiseEventNode { Id = 2, Name = "next_wave", Raiser = 1, NextTick = true });
            g.ExecEdges.Add(new ExecEdge(0, TriggerGraph.TriggerExecOutPort, 2, TriggerGraph.ActionExecInPort));
            g.Nodes.Add(new ExprEventParamNode { Id = 3, Name = "count" });
            g.DataEdges.Add(new DataEdge(3, TriggerGraph.ExprDataOutPort, 2, TriggerGraph.RaiseArgInPort0, DataWireType.Int));

            string json = g.ToCanonicalJson();
            TriggerGraph back = TriggerGraph.FromJson(json);
            Assert.Equal(json, back.ToCanonicalJson()); // byte-identical canonical round-trip

            var ev = Assert.IsType<EventNode>(back.Nodes.Single(n => n.Id == 1));
            Assert.Equal("custom_event", ev.Kind);
            Assert.Equal("wave_start", ev.EventName);
            var raise = Assert.IsType<RaiseEventNode>(back.Nodes.Single(n => n.Id == 2));
            Assert.Equal("next_wave", raise.Name);
            Assert.Equal(1, raise.Raiser);
            Assert.True(raise.NextTick);
            var param = Assert.IsType<ExprEventParamNode>(back.Nodes.Single(n => n.Id == 3));
            Assert.Equal("count", param.Name);
        }

        [Fact]
        public void RaiseEventDefaults_OmitRaiserAndNextTick_AndReadBack()
        {
            var g = new TriggerGraph();
            g.Nodes.Add(new RaiseEventNode { Id = 0, Name = "ev" }); // Raiser -1, NextTick false — the defaults
            string json = g.ToCanonicalJson();
            Assert.DoesNotContain("raiser", json);
            Assert.DoesNotContain("next_tick", json);
            var back = Assert.IsType<RaiseEventNode>(TriggerGraph.FromJson(json).Nodes.Single());
            Assert.Equal(-1, back.Raiser);
            Assert.False(back.NextTick);
        }

        // ── Converter located rejects ────────────────────────────────────────────

        [Theory]
        [InlineData("{\"nodes\":[{\"id\":0,\"kind\":\"custom_event\"}],\"exec_edges\":[],\"data_edges\":[]}", "event_name")]
        [InlineData("{\"nodes\":[{\"id\":0,\"kind\":\"custom_event\",\"event_name\":\"e\",\"faction\":0}],\"exec_edges\":[],\"data_edges\":[]}", "unknown property")]
        [InlineData("{\"nodes\":[{\"id\":0,\"kind\":\"raise_event\"}],\"exec_edges\":[],\"data_edges\":[]}", "name")]
        [InlineData("{\"nodes\":[{\"id\":0,\"kind\":\"raise_event\",\"name\":\"e\",\"raiser\":8}],\"exec_edges\":[],\"data_edges\":[]}", "raiser")]
        [InlineData("{\"nodes\":[{\"id\":0,\"kind\":\"raise_event\",\"name\":\"e\",\"raiser\":-2}],\"exec_edges\":[],\"data_edges\":[]}", "raiser")]
        [InlineData("{\"nodes\":[{\"id\":0,\"kind\":\"expr_event_param\"}],\"exec_edges\":[],\"data_edges\":[]}", "name")]
        [InlineData("{\"nodes\":[{\"id\":0,\"kind\":\"expr_event_param\",\"name\":\"p\",\"faction\":0}],\"exec_edges\":[],\"data_edges\":[]}", "unknown property")]
        public void MalformedNewKindJson_IsRejectedLocated(string json, string expect)
        {
            var ex = Assert.Throws<JsonException>(() => TriggerGraph.FromJson(json));
            Assert.Contains(expect, ex.Message);
        }

        // ── ToFlat fail-closed (graph-channel-only, never lossy lowering) ────────

        [Fact]
        public void ToFlat_FailsClosed_OnEachOfTheThreeNewKinds()
        {
            var withRaise = new TriggerGraph();
            withRaise.Nodes.Add(new RaiseEventNode { Id = 0, Name = "e" });
            Assert.Contains("raise_event", Assert.Throws<JsonException>(() => withRaise.ToFlat()).Message);

            var withSub = new TriggerGraph();
            withSub.Nodes.Add(new EventNode { Id = 0, Kind = "custom_event", EventName = "e" });
            Assert.Contains("custom_event", Assert.Throws<JsonException>(() => withSub.ToFlat()).Message);

            var withParam = new TriggerGraph();
            withParam.Nodes.Add(new ExprEventParamNode { Id = 0, Name = "p" });
            Assert.Contains("expr_event_param", Assert.Throws<JsonException>(() => withParam.ToFlat()).Message);
        }

        // ── event.<param> parse matrix ───────────────────────────────────────────

        [Fact]
        public void EventParamRead_ParsesToExprEventParamNode_WithTheDeclaredType()
        {
            var g = new TriggerGraph();
            (int root, DataWireType wire) = ExprParser.Parse("event.count * 10", g, NoVars, eventParams: WaveParams);
            Assert.Equal(DataWireType.Int, wire);
            Assert.Contains(g.Nodes, n => n is ExprEventParamNode { Name: "count" });

            (_, DataWireType fixedWire) = ExprParser.Parse("event.rate", new TriggerGraph(), NoVars, eventParams: WaveParams);
            Assert.Equal(DataWireType.Fixed, fixedWire);
            (_, DataWireType boolWire) = ExprParser.Parse("event.armed && true", new TriggerGraph(), NoVars, eventParams: WaveParams);
            Assert.Equal(DataWireType.Boolean, boolWire);
            Assert.True(root >= 0);
        }

        [Fact]
        public void EventParamRead_WithoutAnEventMap_IsALocatedReject()
        {
            var ex = Assert.Throws<JsonException>(() => ExprParser.Parse("event.count", new TriggerGraph(), NoVars));
            Assert.Contains("event.count", ex.Message);
            Assert.Contains("exactly one event", ex.Message);
        }

        [Fact]
        public void UndeclaredEventParam_IsALocatedReject()
        {
            var ex = Assert.Throws<JsonException>(() => ExprParser.Parse("event.ghost", new TriggerGraph(), NoVars, eventParams: WaveParams));
            Assert.Contains("ghost", ex.Message);
        }

        [Fact]
        public void UnitDiesPayload_ReadsRefTypedParamsAsInt()
        {
            // victim/killer are EntityRef and killer_faction FactionRef in the payload contract — all SURFACE Int
            // (opaque raw handles, the one sanctioned ref→Int surface), so `event.killer_faction == 1` type-checks.
            var g = new TriggerGraph();
            (_, DataWireType wire) = ExprParser.Parse("event.killer_faction == 1", g, NoVars, eventParams: EventDispatchPlan.UnitDiesParams);
            Assert.Equal(DataWireType.Boolean, wire);
        }

        [Fact]
        public void DeclaredVariableNamedEvent_StillReadsAsAVariable()
        {
            // Only the `event.` prefix routes to the param read — a declared variable named "event" keeps meaning.
            var decls = new Dictionary<string, (DslValueType Type, VarScope Scope)>(StringComparer.Ordinal)
            {
                ["event"] = (DslValueType.Int, VarScope.Global),
            };
            var g = new TriggerGraph();
            (_, DataWireType wire) = ExprParser.Parse("event + 1", g, decls);
            Assert.Equal(DataWireType.Int, wire);
        }

        // ── event.<param> compile/eval matrix ────────────────────────────────────

        [Fact]
        public void CompiledEventParamRead_EvaluatesTheFrame_AndIsTotalWithoutOne()
        {
            var g = new TriggerGraph();
            (int root, _) = ExprParser.Parse("event.count * 10", g, NoVars, eventParams: WaveParams);
            Assert.True(ExprCompiler.TryCompile(g, root, NoVars, inCondition: false, WaveParams,
                out ExprProgram? program, out string? error), error);
            Assert.True(program!.ReadsEventParams);

            var vars = new DslVarTable();
            int[] frame = { 5, 0, 0, 0 };
            Assert.Equal(50, program.Eval(vars, null, frame, 1));
            // TOTAL no-frame semantics: no frame → the read evaluates to 0 (never a throw in the tick).
            Assert.Equal(0, program.Eval(vars, null));
            Assert.Equal(0, program.Eval(vars, null, frame, 0)); // slot ≥ live count → 0
        }

        [Fact]
        public void CompileWithoutAMap_RejectsTheEventParamNode_Located()
        {
            var g = new TriggerGraph();
            (int root, _) = ExprParser.Parse("event.count", g, NoVars, eventParams: WaveParams);
            Assert.False(ExprCompiler.TryCompile(g, root, NoVars, inCondition: false, out _, out string? error));
            Assert.Contains("event.count", error!);
            Assert.Contains("exactly one event", error!);
        }

        [Fact]
        public void CompileAgainstAMapMissingTheParam_RejectsLocated()
        {
            var g = new TriggerGraph();
            (int root, _) = ExprParser.Parse("event.count", g, NoVars, eventParams: WaveParams);
            var otherMap = new Dictionary<string, (int Slot, DslValueType Type)>(StringComparer.Ordinal)
            {
                ["other"] = (0, DslValueType.Int),
            };
            Assert.False(ExprCompiler.TryCompile(g, root, NoVars, inCondition: false, otherMap, out _, out string? error));
            Assert.Contains("declares no parameter 'count'", error!);
        }

        [Fact]
        public void ProgramsWithoutEventParams_ReportReadsEventParamsFalse()
        {
            var g = new TriggerGraph();
            (int root, _) = ExprParser.Parse("1 + 2", g, NoVars);
            Assert.True(ExprCompiler.TryCompile(g, root, NoVars, inCondition: false, out ExprProgram? program, out _));
            Assert.False(program!.ReadsEventParams);
        }
    }
}
