#nullable enable
using ProjectChimera.Core;              // Fixed, HeroStore
using ProjectChimera.Core.Definitions;
using ProjectChimera.Dsl;               // DslValueType / VarScope, TriggerGraph
using ProjectChimera.Effects;           // DirectHpDeltaEffect (a representative trigger_graph payload)
using Xunit;

namespace ProjectChimera.Sim.Tests.Validation
{
    /// <summary>
    /// Story 7.7 — INVERTED from the Story 7.3 exclusion tests this file used to hold (renamed
    /// …DeclarationExclusionTests → …DeclarationFoldTests to match): the
    /// <see cref="ScenarioData.Variables"/> / <see cref="ScenarioData.Timers"/> /
    /// <see cref="ScenarioData.TriggerGraphJson"/> declarations now FOLD into <see cref="CanonicalModelHash"/>
    /// (v8) — the "authoritative handshake fold is 7.7" promise discharged — so a peer with divergent
    /// declarations/graph is rejected at the LOBBY instead of desyncing at tick 1. Cosmetic invariants keep their
    /// teeth: null ≡ empty, and a graph re-serialization / `_editor` edit must not move the hash (the typed fold
    /// reads fields, never JSON bytes).
    /// </summary>
    public class CanonicalModelHashDeclarationFoldTests
    {
        private static ScenarioData BaseModel() => new ScenarioData
        {
            Id = "m", DisplayName = "M", TerrainRef = "", MapBounds = 120f,
            WinCondition = WinCondition.DestroyAllBuildings,
            PlayerSlots = new[] { new ScenarioPlayerSlot { Slot = 0, FactionJson = "res://a.json" } },
        };

        private static void AddDeclarations(ScenarioData m)
        {
            m.Variables = new[]
            {
                new ScenarioVariable { Name = "score", Type = DslValueType.Int,   Scope = VarScope.PerPlayer, Initial = Fixed.FromInt(3) },
                new ScenarioVariable { Name = "rate",  Type = DslValueType.Fixed, Scope = VarScope.Global,    Initial = Fixed.FromFloat(2.5f) },
            };
            m.Timers = new[] { new ScenarioTimer { Name = "clock", Seconds = Fixed.FromInt(30) } };
            m.TriggerGraphJson = TriggerGraph
                .BuildRunEffectTrigger("t", "match_start", new DirectHpDeltaEffect(Fixed.FromInt(-1)))
                .ToCanonicalJson();
        }

        [Fact]
        public void AlgoVersions_Unchanged() // 10 canonical (7.5 merge fold) / 2 start-state (value moves via the seed)
        {
            Assert.Equal(12, CanonicalModelHash.AlgoVersion);
            Assert.Equal(2, StartStateHash.AlgoVersion);
        }

        [Fact]
        public void AddingDeclarations_ChangesCanonicalHash()
        {
            var without = BaseModel();
            var with = BaseModel();
            AddDeclarations(with);
            Assert.NotEqual(CanonicalModelHash.Compute(without), CanonicalModelHash.Compute(with));
        }

        [Fact]
        public void ChangingADeclaredInitial_ChangesCanonicalHash()
        {
            // v8: divergent declared INITIALS are now caught at the LOBBY handshake, not at SimChecksum tick 1.
            var a = BaseModel();
            a.Variables = new[] { new ScenarioVariable { Name = "v", Type = DslValueType.Int, Scope = VarScope.Global, Initial = Fixed.FromInt(1) } };
            var b = BaseModel();
            b.Variables = new[] { new ScenarioVariable { Name = "v", Type = DslValueType.Int, Scope = VarScope.Global, Initial = Fixed.FromInt(2) } };
            Assert.NotEqual(CanonicalModelHash.Compute(a), CanonicalModelHash.Compute(b));
        }

        [Fact]
        public void ChangingATimerDuration_ChangesCanonicalHash()
        {
            var a = BaseModel();
            a.Timers = new[] { new ScenarioTimer { Name = "clock", Seconds = Fixed.FromInt(30) } };
            var b = BaseModel();
            b.Timers = new[] { new ScenarioTimer { Name = "clock", Seconds = Fixed.FromInt(60) } };
            Assert.NotEqual(CanonicalModelHash.Compute(a), CanonicalModelHash.Compute(b));
        }

        [Fact]
        public void ChangingAGraphNodeField_ChangesCanonicalHash()
        {
            // A sim-semantic graph edit (the embedded run_effect delta) must move the hash (typed graph fold).
            var a = BaseModel();
            a.TriggerGraphJson = TriggerGraph
                .BuildRunEffectTrigger("t", "match_start", new DirectHpDeltaEffect(Fixed.FromInt(-1)))
                .ToCanonicalJson();
            var b = BaseModel();
            b.TriggerGraphJson = TriggerGraph
                .BuildRunEffectTrigger("t", "match_start", new DirectHpDeltaEffect(Fixed.FromInt(-2)))
                .ToCanonicalJson();
            Assert.NotEqual(CanonicalModelHash.Compute(a), CanonicalModelHash.Compute(b));
        }

        [Fact]
        public void GraphJsonCosmeticReencoding_DoesNotChangeCanonicalHash()
        {
            // The fold is TYPED (parsed nodes/edges), never JSON bytes — so whitespace/formatting differences in
            // the stored TriggerGraphJson string are invisible to the hash.
            TriggerGraph g = TriggerGraph.BuildRunEffectTrigger("t", "match_start", new DirectHpDeltaEffect(Fixed.FromInt(-1)));
            string canonical = g.ToCanonicalJson();
            string reencoded = canonical.Replace("\r\n", "\n").Replace("\n", "\n "); // cosmetic whitespace shuffle
            var a = BaseModel(); a.TriggerGraphJson = canonical;
            var b = BaseModel(); b.TriggerGraphJson = reencoded;
            Assert.Equal(CanonicalModelHash.Compute(a), CanonicalModelHash.Compute(b));
        }

        [Fact]
        public void EditorAnnotationEdit_DoesNotChangeCanonicalHash()
        {
            // The per-node `_editor` bag is excluded BY CONSTRUCTION (the typed fold never reads it): the same
            // graph with and without editor annotations must hash identically.
            TriggerGraph plain = TriggerGraph.BuildRunEffectTrigger("t", "match_start", new DirectHpDeltaEffect(Fixed.FromInt(-1)));
            string withEditor = InjectEditorBag(plain.ToCanonicalJson());
            var a = BaseModel(); a.TriggerGraphJson = plain.ToCanonicalJson();
            var b = BaseModel(); b.TriggerGraphJson = withEditor;
            Assert.Equal(CanonicalModelHash.Compute(a), CanonicalModelHash.Compute(b));
        }

        /// <summary>Round-trip the canonical JSON adding an `_editor` bag to every node (via the converter, so the
        /// result is exactly what the editor would persist).</summary>
        private static string InjectEditorBag(string canonicalJson)
        {
            TriggerGraph g = TriggerGraph.FromJson(canonicalJson);
            using var doc = System.Text.Json.JsonDocument.Parse("{\"x\": 120, \"y\": -40, \"note\": \"moved\"}");
            foreach (NodeBase n in g.Nodes)
                n.Editor = doc.RootElement.Clone();
            return g.ToCanonicalJson();
        }

        [Fact]
        public void LayoutMove_ViaNodeEditorAnnotation_DoesNotChangeCanonicalHash_AlgoVersionsPinned()
        {
            // Story 7.10 — a T3 canvas layout move persists x/y into each node's `_editor` bag via the position
            // seam; the typed hash fold never reads `_editor`, so the MP handshake hash is byte-identical and NO
            // AlgoVersion moves (CanonicalModelHash 11 / SimChecksum 18 / StartStateHash 2 — no golden re-baseline).
            TriggerGraph plain = TriggerGraph.BuildRunEffectTrigger("t", "match_start", new DirectHpDeltaEffect(Fixed.FromInt(-1)));
            var a = BaseModel(); a.TriggerGraphJson = plain.ToCanonicalJson();

            TriggerGraph moved = TriggerGraph.FromJson(plain.ToCanonicalJson());
            int i = 0;
            foreach (NodeBase n in moved.Nodes) { NodeEditorAnnotation.SetPosition(n, 120 + 30 * i, -40 * i); i++; }
            var b = BaseModel(); b.TriggerGraphJson = moved.ToCanonicalJson();

            Assert.Equal(CanonicalModelHash.Compute(a), CanonicalModelHash.Compute(b));

            // Strengthen the guarantee to the strongest handshake hash that folds canonical model content:
            // StartStateHash folds CanonicalModelHash as its content seed, so a cosmetic layout move must leave it
            // byte-identical too (an empty HeroStore folds no hero rows — the seed alone).
            var heroes = new HeroStore();
            Assert.Equal(StartStateHash.Compute(a, heroes), StartStateHash.Compute(b, heroes));

            Assert.Equal(12, CanonicalModelHash.AlgoVersion);
            Assert.Equal(20, SimChecksum.AlgoVersion);
            Assert.Equal(2, StartStateHash.AlgoVersion);
        }

        [Fact]
        public void Stamps_AreExcluded_DoNotChangeCanonicalHash()
        {
            // schema_version/checksum_algo_version are excluded so a legacy re-save (which stamps them) never
            // moves the handshake hash of an otherwise-identical model.
            var without = BaseModel();
            var with = BaseModel();
            with.SchemaVersion = ScenarioSerializer.CurrentSchemaVersion;
            with.ChecksumAlgoVersion = CanonicalModelHash.AlgoVersion;
            Assert.Equal(CanonicalModelHash.Compute(without), CanonicalModelHash.Compute(with));
        }

        [Fact]
        public void UnparseableGraphJson_FoldsSentinel_NeverThrows()
        {
            var broken = BaseModel();
            broken.TriggerGraphJson = "{ this is not json";
            ulong h = 0;
            var ex = Record.Exception(() => h = CanonicalModelHash.Compute(broken));
            Assert.Null(ex);          // Compute stays pure/never-throw
            Assert.NotEqual(0UL, h);  // and still never the fail-open 0
            // The sentinel is distinct from both "absent" and any real parsed graph.
            var absent = BaseModel();
            Assert.NotEqual(CanonicalModelHash.Compute(absent), h);
        }

        [Fact]
        public void NullAndEmptyDeclarations_HashIdenticallyToOneAnother()
        {
            var nulls = BaseModel(); // Variables/Timers/TriggerGraphJson all null
            var empties = BaseModel();
            empties.Variables = System.Array.Empty<ScenarioVariable>();
            empties.Timers = System.Array.Empty<ScenarioTimer>();
            empties.TriggerGraphJson = "";
            Assert.Equal(CanonicalModelHash.Compute(nulls), CanonicalModelHash.Compute(empties));
        }

        [Fact]
        public void AbsentGraph_And_AuthoredEmptyGraph_HashIdentically()
        {
            // Review follow-up: a hand-authored EMPTY graph ({"nodes":[]…}) is semantically identical to an absent
            // one — it must fold the same absent marker, or two behaviorally-equal scenarios false-positive-
            // mismatch at the lobby handshake.
            var absent = BaseModel(); // TriggerGraphJson null
            var empty = BaseModel();
            empty.TriggerGraphJson = new TriggerGraph().ToCanonicalJson(); // zero nodes, zero edges
            Assert.Equal(CanonicalModelHash.Compute(absent), CanonicalModelHash.Compute(empty));
        }

        [Fact]
        public void AddingDeclarations_ChangesStartStateHash()
        {
            // The canonical seed moved, so StartStateHash moves with it (its own AlgoVersion stays 2).
            var without = BaseModel();
            var with = BaseModel();
            AddDeclarations(with);
            var heroes = new HeroStore(); // empty → no hero rows folded
            Assert.NotEqual(StartStateHash.Compute(without, heroes), StartStateHash.Compute(with, heroes));
        }
    }
}
