#nullable enable
using System.Linq;
using System.Text.Json;
using ProjectChimera.Combat;            // DamageType
using ProjectChimera.Core;              // Fixed
using ProjectChimera.Core.Definitions;  // TriggerDefinition, TriggerEvent, ...
using ProjectChimera.Dsl;               // TriggerGraph, NodeBaseJsonConverter
using ProjectChimera.Effects;           // EffectNode, SequenceEffect, DamageEffect
using Xunit;

namespace ProjectChimera.Sim.Tests.Dsl
{
    /// <summary>
    /// Story 7.2 — the closed-registry <see cref="NodeBaseJsonConverter"/>: every node round-trips by persistent id
    /// with all exec/data edges preserved; an unknown <c>kind</c> / stray / duplicate property is a LOCATED
    /// <see cref="JsonException"/> naming the offender; and a <c>run_effect</c> node's embedded D1 effect subgraph
    /// round-trips byte-faithfully (delegated to <c>EffectNodeJsonConverter</c>, no second executor).
    /// </summary>
    public class TriggerGraphConverterTests
    {
        [Fact]
        public void Graph_JsonRoundTrip_ReproducesEveryNodeByIdAndAllEdges()
        {
            // Two full triggers migrated from flat, plus a standalone run_effect node (embed capability).
            var flat = new[]
            {
                new TriggerDefinition
                {
                    Name = "A", Priority = 2,
                    Events = new[] { new TriggerEvent { Type = "match_start" } },
                    Conditions = new[] { new TriggerCondition { Type = "always" } },
                    Actions = new[]
                    {
                        new TriggerAction { Type = "spawn_unit", UnitId = "grunt", X = Fixed.FromFloat(1.5f), Z = Fixed.FromFloat(2.5f), Count = 3 },
                        new TriggerAction { Type = "display_message", Text = "hi" },
                    },
                },
                new TriggerDefinition
                {
                    Name = "B",
                    Events = new[] { new TriggerEvent { Type = "unit_dies", Faction = 1 } },
                    Actions = new[] { new TriggerAction { Type = "victory", Faction = 0 } },
                },
            };
            TriggerGraph graph = TriggerGraph.FromFlat(flat);
            graph.Nodes.Add(new EffectActionNode
            {
                Id = 100,
                Effect = new SequenceEffect(new DamageEffect(Fixed.FromInt(10), DamageType.Normal)),
            });

            string json = graph.ToCanonicalJson();
            TriggerGraph back = TriggerGraph.FromJson(json);

            // Every node reproduced by id + concrete type.
            Assert.Equal(graph.Nodes.Count, back.Nodes.Count);
            foreach (NodeBase original in graph.Nodes)
            {
                NodeBase? roundtripped = back.Nodes.SingleOrDefault(n => n.Id == original.Id);
                Assert.NotNull(roundtripped);
                Assert.Equal(original.GetType(), roundtripped!.GetType());
            }

            // Every exec + data edge preserved (order-independent set equality via value Equals).
            Assert.Equal(graph.ExecEdges.OrderBy(e => e).ToList(), back.ExecEdges.OrderBy(e => e).ToList());
            Assert.Equal(graph.DataEdges.OrderBy(e => e).ToList(), back.DataEdges.OrderBy(e => e).ToList());

            // And the whole thing lowers back to the same flat triggers (id-preservation is behavioral).
            TriggerDefinition[] lowered = back.ToFlat();
            Assert.Equal(flat.Length, lowered.Length);
            Assert.Equal("A", lowered[0].Name);
            Assert.Equal("B", lowered[1].Name);
            Assert.Equal(2, lowered[0].Actions.Length);
        }

        [Fact]
        public void UnknownKind_IsLocatedReject_NamingTheKind()
        {
            const string json = """{ "nodes": [ { "id": 0, "kind": "run_script" } ], "exec_edges": [], "data_edges": [] }""";
            JsonException ex = Assert.Throws<JsonException>(() => TriggerGraph.FromJson(json));
            Assert.Contains("unknown node kind 'run_script'", ex.Message);
        }

        [Fact]
        public void StrayProperty_IsLocatedReject_NamingTheProperty()
        {
            // "script" is not in the trigger allow-list → fail-closed (no scripting escape hatch).
            const string json = """{ "nodes": [ { "id": 0, "kind": "trigger", "name": "T", "script": "evil()" } ], "exec_edges": [], "data_edges": [] }""";
            JsonException ex = Assert.Throws<JsonException>(() => TriggerGraph.FromJson(json));
            Assert.Contains("script", ex.Message);
            Assert.Contains("unknown property", ex.Message);
        }

        [Fact]
        public void DuplicateProperty_IsLocatedReject_NamingTheProperty()
        {
            // A repeated "priority" must not smuggle a second value past validation.
            const string json = """{ "nodes": [ { "id": 0, "kind": "trigger", "priority": 1, "priority": 2 } ], "exec_edges": [], "data_edges": [] }""";
            JsonException ex = Assert.Throws<JsonException>(() => TriggerGraph.FromJson(json));
            Assert.Contains("priority", ex.Message);
            Assert.Contains("duplicate property", ex.Message);
        }

        [Fact]
        public void ExprVarFaction_OutsideCanonicalRange_IsLocatedReject_NotALossyRewrite()
        {
            // Write omits EVERY negative faction as a bare read, so "faction": -5 would deserialize fine,
            // serialize as OMITTED, and read back as -1 — a silent lossy rewrite. Read rejects it fail-closed.
            const string neg = """{ "nodes": [ { "id": 0, "kind": "expr_var", "name": "gold", "faction": -5 } ], "exec_edges": [], "data_edges": [] }""";
            JsonException ex = Assert.Throws<JsonException>(() => TriggerGraph.FromJson(neg));
            Assert.Contains("faction", ex.Message);
            Assert.Contains("-5", ex.Message);

            // Above the slotted ceiling (0..7) is equally outside the canonical encoding.
            const string over = """{ "nodes": [ { "id": 0, "kind": "expr_var", "name": "gold", "faction": 8 } ], "exec_edges": [], "data_edges": [] }""";
            Assert.Throws<JsonException>(() => TriggerGraph.FromJson(over));
        }

        [Fact]
        public void RunEffectNode_EmbeddedEffectSubgraph_RoundTripsUnchanged()
        {
            var graph = new TriggerGraph();
            graph.Nodes.Add(new EffectActionNode
            {
                Id = 0,
                Effect = new SequenceEffect(
                    new DamageEffect(Fixed.FromInt(10), DamageType.Normal),
                    new DamageEffect(Fixed.FromFloat(2.5f), DamageType.Magic)),
            });

            string json = graph.ToCanonicalJson();
            TriggerGraph back = TriggerGraph.FromJson(json);

            var node = Assert.IsType<EffectActionNode>(back.Nodes.Single());
            var seq = Assert.IsType<SequenceEffect>(node.Effect);
            Assert.Equal(2, seq.Children.Length);
            var d0 = Assert.IsType<DamageEffect>(seq.Children[0]);
            var d1 = Assert.IsType<DamageEffect>(seq.Children[1]);
            Assert.Equal(Fixed.FromInt(10).Raw, d0.Amount.Raw);
            Assert.Equal(DamageType.Normal, d0.Type);
            Assert.Equal(Fixed.FromFloat(2.5f).Raw, d1.Amount.Raw);
            Assert.Equal(DamageType.Magic, d1.Type);
        }

        [Fact]
        public void RunEffect_KindIsInTheClosedRegistry_AndParsesAsEffectActionNode()
        {
            const string json = """
            { "nodes": [ { "id": 7, "kind": "run_effect", "effect": { "kind": "damage", "amount": 5, "damage_type": "Pierce" } } ],
              "exec_edges": [], "data_edges": [] }
            """;
            TriggerGraph g = TriggerGraph.FromJson(json);
            var node = Assert.IsType<EffectActionNode>(g.Nodes.Single());
            Assert.Equal(7, node.Id);
            var dmg = Assert.IsType<DamageEffect>(node.Effect);
            Assert.Equal(DamageType.Pierce, dmg.Type);
        }

        // ── Story 7.4 (pass-2 review) — the exact integer-math Fixed codec for expr_literal values ──

        private static TriggerGraph SingleFixedLiteral(int raw)
        {
            var g = new TriggerGraph();
            g.Nodes.Add(new ExprLiteralNode { Id = 0, ValueType = DslValueType.Fixed, Raw = raw });
            return g;
        }

        [Theory]
        [InlineData(int.MaxValue)]  // 32767.9999847412109375 — Fixed.ToFloat rounds this UP to 32768f (reload reject)
        [InlineData(int.MinValue)]  // -32768 exactly
        [InlineData(19660801)]      // 300.0000152587890625 — beyond float's 24-bit mantissa (silent quantization)
        [InlineData(-98304)]        // -1.5
        [InlineData(98304)]         // 1.5
        [InlineData(1)]             // 0.0000152587890625 — the smallest positive raw
        [InlineData(0)]
        [InlineData(65536)]         // 1.0 (fraction-free path)
        public void ExprFixedLiteral_RoundTripsRawExact_AndByteIdentical(int raw)
        {
            // Pre-patch, expr_literal Fixed values rode the float-based FixedJsonConverter: raw int.MaxValue
            // serialized as 32768 and REJECTED on reload (a persist-then-cannot-load data loss the editor reported
            // as success), and any raw beyond float's mantissa silently quantized. The exact integer-math codec
            // must round-trip EVERY raw bit-exactly, byte-identically.
            string json = SingleFixedLiteral(raw).ToCanonicalJson();
            TriggerGraph back = TriggerGraph.FromJson(json);
            var lit = Assert.IsType<ExprLiteralNode>(back.Nodes.Single());
            Assert.Equal(raw, lit.Raw);                      // raw-exact through the JSON boundary
            Assert.Equal(json, back.ToCanonicalJson());      // byte-identical canonical round-trip
        }

        [Fact]
        public void ExprFixedLiteral_CeilingRaw_EmitsTheExactDecimal_NeverFloatRounded32768()
        {
            string json = SingleFixedLiteral(int.MaxValue).ToCanonicalJson();
            Assert.Contains("32767.9999847412109375", json);
            Assert.DoesNotContain("\"value\":32768", json);
        }

        [Fact]
        public void ExprFixedLiteral_TextAndRawIr_YieldTheSameRaw_ForAFineDecimal()
        {
            // One IR: the same decimal authored as CEL text and as raw-IR JSON must produce the same raw.
            // Pre-patch, text rounded half-up (0.00001 → raw 1) while raw-IR truncated through float (raw 0).
            var g = new TriggerGraph();
            (int rootId, _) = ExprParser.Parse("0.00001", g,
                new System.Collections.Generic.Dictionary<string, (DslValueType, VarScope)>(System.StringComparer.Ordinal));
            int textRaw = ((ExprLiteralNode)g.Nodes.Single(n => n.Id == rootId)).Raw;

            const string json = """
            { "nodes": [ { "id": 0, "kind": "expr_literal", "type": "Fixed", "value": 0.00001 } ],
              "exec_edges": [], "data_edges": [] }
            """;
            int rawIrRaw = ((ExprLiteralNode)TriggerGraph.FromJson(json).Nodes.Single()).Raw;

            Assert.Equal(1, textRaw);       // round-half-up, pure integer math
            Assert.Equal(textRaw, rawIrRaw); // both surfaces agree — one IR
        }

        [Fact]
        public void ExprLiteral_MissingValue_IsLocatedReject()
        {
            // A missing 'value' used to silently default to 0 and evaluate — fail-closed now.
            const string json = """{ "nodes": [ { "id": 0, "kind": "expr_literal", "type": "Int" } ], "exec_edges": [], "data_edges": [] }""";
            JsonException ex = Assert.Throws<JsonException>(() => TriggerGraph.FromJson(json));
            Assert.Contains("value", ex.Message);
        }

        [Fact]
        public void ExprLiteral_MissingType_IsLocatedReject()
        {
            const string json = """{ "nodes": [ { "id": 0, "kind": "expr_literal", "value": 3 } ], "exec_edges": [], "data_edges": [] }""";
            JsonException ex = Assert.Throws<JsonException>(() => TriggerGraph.FromJson(json));
            Assert.Contains("type", ex.Message);
        }

        [Fact]
        public void ExprFixedLiteral_ExponentNotation_IsLocatedReject()
        {
            // The exact codec accepts plain decimals only (Write never emits exponents; parsing them through
            // float would reintroduce the quantization this codec exists to remove).
            const string json = """{ "nodes": [ { "id": 0, "kind": "expr_literal", "type": "Fixed", "value": 1.5e2 } ], "exec_edges": [], "data_edges": [] }""";
            JsonException ex = Assert.Throws<JsonException>(() => TriggerGraph.FromJson(json));
            Assert.Contains("plain decimal", ex.Message);
        }

        [Fact]
        public void ExprFixedLiteral_OutOfRange_IsLocatedReject()
        {
            const string json = """{ "nodes": [ { "id": 0, "kind": "expr_literal", "type": "Fixed", "value": 40000.5 } ], "exec_edges": [], "data_edges": [] }""";
            JsonException ex = Assert.Throws<JsonException>(() => TriggerGraph.FromJson(json));
            Assert.Contains("16.16 range", ex.Message);
        }
    }
}
