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
            Assert.Equal(14, CanonicalModelHash.AlgoVersion);
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

        /// <summary>Story 7.13 (review PATCH 2) — the ExprCallNode.Selector fold is OMIT-WHEN-DEFAULT, the discipline
        /// every other fold observes: an empty selector (count/distance) mixes NOTHING (so a pre-7.13 count node folds
        /// byte-identically apart from the version bump), while a present state-read selector stays discriminated so a
        /// divergent handshake rejects at the lobby.</summary>
        [Fact]
        public void ExprCallSelector_OmitWhenDefault_AndDiscriminatesWhenPresent()
        {
            // Empty vs null selector on a count() node → both omit → identical hash (the omit-when-default invariant;
            // a JSON round-trip also normalizes both to "", so this proves the fold adds no mix for an empty selector).
            var emptySel = BaseModel(); emptySel.TriggerGraphJson = GraphWithExprCall("count", "");
            var nullSel  = BaseModel(); nullSel.TriggerGraphJson  = GraphWithExprCall("count", null!);
            Assert.Equal(CanonicalModelHash.Compute(emptySel), CanonicalModelHash.Compute(nullSel));

            // A state read WITH a non-empty selector must hash DIFFERENTLY from the same fn+node with an empty
            // selector — selectors are discriminated when present (closes the Arm A handshake gap).
            var withSel    = BaseModel(); withSel.TriggerGraphJson    = GraphWithExprCall("region_unit_count", "region1");
            var withoutSel = BaseModel(); withoutSel.TriggerGraphJson = GraphWithExprCall("region_unit_count", "");
            Assert.NotEqual(CanonicalModelHash.Compute(withSel), CanonicalModelHash.Compute(withoutSel));
        }

        /// <summary>A minimal trigger graph carrying a single ExprCallNode (fn + selector) for the selector-fold test.</summary>
        private static string GraphWithExprCall(string fn, string selector)
        {
            var g = new TriggerGraph();
            g.Nodes.Add(new TriggerNode { Id = 0, Name = "t" });
            g.Nodes.Add(new EventNode { Id = 1, Kind = "match_start" });
            g.Nodes.Add(new ExprCallNode { Id = 2, Fn = fn, Selector = selector });
            g.ExecEdges.Add(new ExecEdge(1, TriggerGraph.EventExecOutPort, 0, TriggerGraph.TriggerEventInPort));
            return g.ToCanonicalJson();
        }

        /// <summary>Story 7.13 (follow-up review) — the eight new node kinds fold their SEMANTIC FIELDS into the typed
        /// walk (Arm A left them on the type-name `default` arm — folding presence but NOT field values, the handshake
        /// gap the v13 bump closed). Two models differing in exactly one folded field of one new kind must hash
        /// DIFFERENTLY, or a divergent scenario passes the lobby handshake and desyncs. No golden fixture carries these
        /// kinds, so this is their only fold-discrimination guard.</summary>
        [Fact]
        public void NewNodeKinds_FoldSemanticFields_DiscriminatingAtTheHandshake()
        {
            // order_units — command discriminates (faction/region/X/Z also fold; command is representative)
            Assert.NotEqual(HashWithNode(new OrderUnitsNode { Id = 2, Command = "move",        Faction = 0, RegionId = "r", X = Fixed.FromInt(3), Z = Fixed.FromInt(4) }),
                            HashWithNode(new OrderUnitsNode { Id = 2, Command = "attack_move", Faction = 0, RegionId = "r", X = Fixed.FromInt(3), Z = Fixed.FromInt(4) }));
            // order_units — the point coordinate (X.Raw folds)
            Assert.NotEqual(HashWithNode(new OrderUnitsNode { Id = 2, Command = "move", Faction = 0, RegionId = "r", X = Fixed.FromInt(3), Z = Fixed.FromInt(4) }),
                            HashWithNode(new OrderUnitsNode { Id = 2, Command = "move", Faction = 0, RegionId = "r", X = Fixed.FromInt(9), Z = Fixed.FromInt(4) }));
            // move_camera — camera name
            Assert.NotEqual(HashWithNode(new MoveCameraNode { Id = 2, CameraName = "camA" }),
                            HashWithNode(new MoveCameraNode { Id = 2, CameraName = "camB" }));
            // cinematic_mode — the on/off flag
            Assert.NotEqual(HashWithNode(new CinematicModeNode { Id = 2, Enabled = true }),
                            HashWithNode(new CinematicModeNode { Id = 2, Enabled = false }));
            // play_vfx — vfx id
            Assert.NotEqual(HashWithNode(new PlayVfxNode { Id = 2, VfxId = "boom", X = Fixed.Zero, Z = Fixed.Zero }),
                            HashWithNode(new PlayVfxNode { Id = 2, VfxId = "fizz", X = Fixed.Zero, Z = Fixed.Zero }));
            // random_choice — the weighted-branch structure
            Assert.NotEqual(HashWithNode(new RandomChoiceNode { Id = 2, Weights = new[] { 1, 2 } }),
                            HashWithNode(new RandomChoiceNode { Id = 2, Weights = new[] { 1, 3 } }));
            // enable_trigger / disable_trigger / run_trigger — target trigger id
            Assert.NotEqual(HashWithNode(new EnableTriggerNode  { Id = 2, TargetTriggerId = 5 }),
                            HashWithNode(new EnableTriggerNode  { Id = 2, TargetTriggerId = 6 }));
            Assert.NotEqual(HashWithNode(new DisableTriggerNode { Id = 2, TargetTriggerId = 5 }),
                            HashWithNode(new DisableTriggerNode { Id = 2, TargetTriggerId = 6 }));
            Assert.NotEqual(HashWithNode(new RunTriggerNode     { Id = 2, TargetTriggerId = 5 }),
                            HashWithNode(new RunTriggerNode     { Id = 2, TargetTriggerId = 6 }));
            // Story 7.14 — the three objective action-leaf kinds fold their objective_id (an explicit arm each, never
            // the type-name-only default): two actions differing only by target objective must hash differently.
            Assert.NotEqual(HashWithNode(new ShowObjectiveNode     { Id = 2, ObjectiveId = "a" }),
                            HashWithNode(new ShowObjectiveNode     { Id = 2, ObjectiveId = "b" }));
            Assert.NotEqual(HashWithNode(new CompleteObjectiveNode { Id = 2, ObjectiveId = "a" }),
                            HashWithNode(new CompleteObjectiveNode { Id = 2, ObjectiveId = "b" }));
            Assert.NotEqual(HashWithNode(new FailObjectiveNode     { Id = 2, ObjectiveId = "a" }),
                            HashWithNode(new FailObjectiveNode     { Id = 2, ObjectiveId = "b" }));
        }

        /// <summary>Story 7.14 — the authored `objectives` array is hash-EXCLUDED (authoring/presentation data on the
        /// variables/display_name basis): two scenarios differing ONLY in their objectives (or one with none) must hash
        /// IDENTICALLY, so an objective edit never moves the MP start-state handshake.</summary>
        [Fact]
        public void AuthoredObjectives_AreHashExcluded()
        {
            var without = BaseModel();
            var with = BaseModel();
            with.Objectives = new[]
            {
                new ScenarioObjective { Id = "kill_boss", Title = "Kill the boss", InitialState = ObjectiveState.Active },
                new ScenarioObjective { Id = "hold_hill", Title = "Hold the hill", InitialState = ObjectiveState.Hidden },
            };
            Assert.Equal(CanonicalModelHash.Compute(without), CanonicalModelHash.Compute(with));
        }

        /// <summary>A minimal trigger graph carrying a single new-kind node (id 2) for the field-fold discrimination
        /// test. The typed walk folds every node by id (CanonicalModelHash.cs:565), so exec wiring is irrelevant.</summary>
        private static ulong HashWithNode(NodeBase node)
        {
            var g = new TriggerGraph();
            g.Nodes.Add(new TriggerNode { Id = 0, Name = "t" });
            g.Nodes.Add(new EventNode { Id = 1, Kind = "match_start" });
            g.Nodes.Add(node);
            g.ExecEdges.Add(new ExecEdge(1, TriggerGraph.EventExecOutPort, 0, TriggerGraph.TriggerEventInPort));
            var m = BaseModel(); m.TriggerGraphJson = g.ToCanonicalJson();
            return CanonicalModelHash.Compute(m);
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

            Assert.Equal(14, CanonicalModelHash.AlgoVersion);
            Assert.Equal(24, SimChecksum.AlgoVersion);
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
