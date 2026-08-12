#nullable enable
using System.Text.Json;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Dsl;       // Story 7.3 — DslValueType/VarScope, TriggerGraph (trigger_graph gate tests)
using ProjectChimera.Effects;   // Story 7.3 — DirectHpDeltaEffect / SequenceEffect (embedded-effect bounds tests)
using Xunit;

namespace ProjectChimera.Sim.Tests.Validation
{
    /// <summary>
    /// Story 1.11 (AC3 — Decision #1) — the LLM-Trigger validated-only smoke test. Proves the AR-39 gate now
    /// inspects <c>Triggers[]</c> (it previously did not — an accepted LLM/editor trigger reached
    /// <c>ScenarioDirector</c> entirely unvalidated). A well-formed trigger passes and mints a
    /// <see cref="Validated{T}"/> wrapping the same model; each malformed case (unknown event/condition/action
    /// type, invalid faction slot, unknown building_type, invalid operator, out-of-range spawn coordinate,
    /// dangling timer reference) is rejected with a single LOCATED <c>scenario.triggers[...]</c> error and never
    /// reaches the tick.
    ///
    /// AC3c — no LLM is invoked anywhere: crafted <see cref="TriggerDefinition"/>s are fed through the pure-C#
    /// gate. The no-bypass guarantee (a <see cref="Validated{T}"/> is sole-minted by
    /// <see cref="ScenarioValidator"/>) is covered structurally by <c>ValidatedMintingTests</c>' source scan —
    /// this change adds NO new <c>new Validated&lt;</c> (it reuses the validator's single mint), so that scan
    /// stays green. The residual editor-accept routing seam (<c>TriggerEditorPanel.OnAcceptPressed</c> appends
    /// without the applier) is the documented Decision #1 follow-up.
    /// </summary>
    public class TriggerValidationTests
    {
        private static ScenarioValidator NewValidator() => new();

        /// <summary>
        /// A minimal VALID model (mirrors <c>NegativeValidationTests.ValidModel</c>) carrying one well-formed
        /// trigger that exercises an event (with operator), a building_type condition, a spawn action, and a
        /// create_timer / timer_expires pair so the dangling-timer check has a SATISFIED reference.
        /// </summary>
        private static ScenarioData ValidModelWithTrigger() => new ScenarioData
        {
            MapBounds = 120f,
            WinCondition = WinCondition.DestroyAllBuildings,
            PlayerSlots = new[]
            {
                new ScenarioPlayerSlot { Slot = 0, FactionJson = "res://a.json", StartOre = 200f, BaseX = -45f, BaseZ = 0f },
                new ScenarioPlayerSlot { Slot = 1, FactionJson = "res://b.json", StartOre = 200f, BaseX =  45f, BaseZ = 0f },
            },
            ResourceNodes = new[] { new ScenarioResourceNode { X = 10f, Z = 10f, Supply = 400f, Rate = 5f, MaxGatherers = 4 } },
            Buildings = new[] { new ScenarioBuilding { Type = "CommandCenter", Slot = 0, X = -45f, Z = 0f, PreBuilt = true } },
            Units = new[] { new ScenarioUnit { UnitId = "worker", Slot = 1, X = 42f, Z = 3f } },
            Triggers = new[]
            {
                new TriggerDefinition
                {
                    Name = "wave",
                    Events = new[]
                    {
                        new TriggerEvent { Type = "resource_threshold", Faction = 1, Amount = Fixed.FromInt(500), Operator = ">=" },
                        new TriggerEvent { Type = "timer_expires", TimerName = "spawn_clock" },
                    },
                    Conditions = new[]
                    {
                        new TriggerCondition { Type = "building_exists", Faction = 1, BuildingType = "Barracks", Operator = ">=" },
                    },
                    Actions = new[]
                    {
                        new TriggerAction { Type = "create_timer", TimerName = "spawn_clock", TimerSeconds = Fixed.FromInt(30) },
                        new TriggerAction { Type = "spawn_unit", UnitId = "soldier", Faction = 1, X = Fixed.FromInt(40), Z = Fixed.FromInt(5), Count = 3 },
                    },
                },
            },
        };

        [Fact]
        public void ValidTrigger_Passes_AndMintsValidatedWrappingSameModel()
        {
            var model = ValidModelWithTrigger();
            ValidationResult r = NewValidator().Validate(model);
            Assert.True(r.Ok, r.Error);
            Assert.Null(r.Error);
            Assert.Same(model, r.Value.Value); // AC3b: the validator is the minter; it wraps the very instance validated
        }

        [Fact]
        public void EventFactionSlotAboveEngineCeiling_IsRejected_LocatingTheEventFaction()
        {
            var m = ValidModelWithTrigger();
            m.Triggers[0].Events[0].Faction = 8; // Story 9.2: [0,7] valid; slot 8 → (Faction)9 → Ore[9], an OOB crash
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("triggers[0].events[0].faction", r.Error!);
        }

        [Fact]
        public void NegativeFactionSlotInCondition_IsRejected()
        {
            var m = ValidModelWithTrigger();
            m.Triggers[0].Conditions[0].Faction = -1;
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("triggers[0].conditions[0].faction", r.Error!);
        }

        [Fact]
        public void UnknownBuildingTypeInCondition_IsRejected_NotSilentlyInert()
        {
            var m = ValidModelWithTrigger();
            m.Triggers[0].Conditions[0].BuildingType = "Frost"; // building_exists would silently never match → dead trigger
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("triggers[0].conditions[0].building_type", r.Error!);
        }

        [Fact]
        public void InvalidOperator_IsRejected()
        {
            var m = ValidModelWithTrigger();
            m.Triggers[0].Events[0].Operator = "=>"; // not in {>,<,>=,<=,==,!=} → Compare returns false forever
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("triggers[0].events[0].operator", r.Error!);
        }

        [Fact]
        public void OutOfRangeSpawnCoordinate_IsRejectedAtTheJsonBoundary_ByFixedJsonConverter()
        {
            // Story 7.1: TriggerAction.X/Z are now Fixed, quantized at the JSON boundary. An out-of-16.16-range
            // spawn coordinate can no longer even be constructed in code (a Fixed cannot hold ±40000); the finite/
            // range gate MOVED to FixedJsonConverter, which rejects it at deserialize time with a located
            // "16.16 range" error — exactly where the spec relocates the check. The validator's own coordinate gate
            // now covers only map_bounds (proven by SpawnCoordinateOutsideMapBounds_IsRejected below).
            var options = new JsonSerializerOptions { Converters = { new FixedJsonConverter() } };
            const string json = "{\"type\":\"spawn_unit\",\"unit_id\":\"soldier\",\"x\":40000,\"z\":5}";
            var ex = Assert.Throws<JsonException>(
                () => JsonSerializer.Deserialize<TriggerAction>(json, options));
            Assert.Contains("16.16 range", ex.Message);
        }

        [Fact]
        public void SpawnCoordinateOutsideMapBounds_IsRejected()
        {
            var m = ValidModelWithTrigger();
            m.Triggers[0].Actions[1].Z = Fixed.FromInt(200); // inside the Fixed range but outside map_bounds 120
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("triggers[0].actions[1].z", r.Error!);
            Assert.Contains("map_bounds", r.Error!);
        }

        [Fact]
        public void DanglingTimerReference_IsRejected()
        {
            var m = ValidModelWithTrigger();
            m.Triggers[0].Events[1].TimerName = "ghost_clock"; // no create_timer creates "ghost_clock" → dangling
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("triggers[0].events[1].timer_name", r.Error!);
        }

        [Fact]
        public void UnknownEventType_IsRejected()
        {
            var m = ValidModelWithTrigger();
            m.Triggers[0].Events[0].Type = "on_eclipse"; // not a known event type — would silently never fire
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("triggers[0].events[0].type", r.Error!);
        }

        [Fact]
        public void UnknownActionType_IsRejected()
        {
            var m = ValidModelWithTrigger();
            m.Triggers[0].Actions[1].Type = "nuke_everything"; // break the spawn action (not the create_timer, to keep the timer ref satisfied)
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("triggers[0].actions[1].type", r.Error!);
        }

        [Fact]
        public void NullTriggersArray_IsRejected()
        {
            var m = ValidModelWithTrigger();
            m.Triggers = null!;
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("triggers", r.Error!);
        }

        [Fact]
        public void Validate_NeverThrows_OnTriggerWithNullSubArrays()
        {
            // Purity: null Events/Conditions/Actions inside a trigger must NOT throw (the validator treats them as
            // empty via `?? Array.Empty`), so a partially-deserialized trigger yields a located result, not an NRE.
            var m = ValidModelWithTrigger();
            m.Triggers[0].Events = null!;
            m.Triggers[0].Conditions = null!;
            m.Triggers[0].Actions = null!;
            var ex = Record.Exception(() => NewValidator().Validate(m));
            Assert.Null(ex);
        }

        // ── Story 7.3 P3 — the trigger_graph channel is validated at the pre-tick gate (parse + effect-bounds) ──

        [Fact]
        public void MalformedTriggerGraph_IsRejected_WithLocatedError()
        {
            // An unknown node kind fails the closed-registry FromJson parse; the validator catches the JsonException
            // and returns a LOCATED scenario.trigger_graph error (instead of crashing mid-apply in LoadScenario).
            var m = ValidModelWithTrigger();
            m.TriggerGraphJson = "{ \"nodes\": [ { \"id\": 0, \"kind\": \"bogus_kind\" } ], \"exec_edges\": [], \"data_edges\": [] }";
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("trigger_graph", r.Error!);
        }

        [Fact]
        public void OverCapEmbeddedEffect_InTriggerGraph_IsRejected()
        {
            // A run_effect whose embedded effect is a Sequence with 9 children exceeds MaxSequenceChildren=8. It parses
            // (the converter doesn't bound child count) but EffectBounds.Validate rejects it — the SAME load-time
            // bounds gate every other effect source gets, applied here at the trigger_graph channel.
            var children = new EffectNode[9];
            for (int i = 0; i < children.Length; i++) children[i] = new DirectHpDeltaEffect(Fixed.FromInt(-1));
            TriggerGraph g = TriggerGraph.BuildRunEffectTrigger("t", "match_start", new SequenceEffect(children));

            var m = ValidModelWithTrigger();
            m.TriggerGraphJson = g.ToCanonicalJson();
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("trigger_graph", r.Error!);
        }

        [Fact]
        public void WellFormedTriggerGraph_IsAccepted()
        {
            // A valid run_effect graph (single direct_hp_delta) passes both the parse and the effect-bounds gate.
            TriggerGraph g = TriggerGraph.BuildRunEffectTrigger("t", "match_start", new DirectHpDeltaEffect(Fixed.FromInt(-10)));
            var m = ValidModelWithTrigger();
            m.TriggerGraphJson = g.ToCanonicalJson();
            ValidationResult r = NewValidator().Validate(m);
            Assert.True(r.Ok, r.Error);
        }

        // ── Story 7.3 P4 — a variable_comparison cannot read a TriggerLocal-scoped variable ──

        [Fact]
        public void ConditionReadingTriggerLocalVariable_IsRejected()
        {
            // A condition evaluates BEFORE the trigger-local scope is entered, so a TriggerLocal read returns 0 —
            // reject it fail-closed (TriggerLocal is action-write-scratch only).
            var m = ValidModelWithTrigger();
            m.Variables = new[] { new ScenarioVariable { Name = "loc", Type = DslValueType.Int, Scope = VarScope.TriggerLocal } };
            m.Triggers[0].Conditions = new[]
            {
                new TriggerCondition { Type = "variable_comparison", Variable = "loc", Operator = "==", Value = 1 },
            };
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("conditions[0].variable", r.Error!);
            Assert.Contains("TriggerLocal", r.Error!);
        }

        // ── Story 7.3 P5 — Int-only + faction-range gating for variable read/write leaves ──

        [Fact]
        public void ConditionOnNonIntVariable_IsRejected()
        {
            var m = ValidModelWithTrigger();
            m.Variables = new[] { new ScenarioVariable { Name = "pt", Type = DslValueType.Point, Scope = VarScope.Global } };
            m.Triggers[0].Conditions = new[]
            {
                new TriggerCondition { Type = "variable_comparison", Variable = "pt", Operator = "==", Value = 1 },
            };
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("conditions[0].variable", r.Error!);
        }

        [Fact]
        public void SetVariableOnNonIntVariable_IsRejected()
        {
            var m = ValidModelWithTrigger();
            m.Variables = new[] { new ScenarioVariable { Name = "fx", Type = DslValueType.Fixed, Scope = VarScope.Global } };
            // Keep the create_timer action so the timer_expires event stays satisfied; add the offending set_variable.
            m.Triggers[0].Actions = new[]
            {
                new TriggerAction { Type = "create_timer", TimerName = "spawn_clock", TimerSeconds = Fixed.FromInt(30) },
                new TriggerAction { Type = "set_variable", Variable = "fx", Value = 5 },
            };
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("actions[1].variable", r.Error!);
        }

        [Fact]
        public void VariableLeafFactionOutOfRange_IsRejected_LocatingTheFaction()
        {
            // The rejection comes from the canonical CheckFactionSlot bound (engine ceiling [0,3], applied to EVERY
            // action faction) — the engine ceiling is a strict subset of the DSL player-slot range, so no separate
            // variable-leaf range check exists (review follow-up removed the unreachable one this test once named).
            var m = ValidModelWithTrigger();
            m.Variables = new[] { new ScenarioVariable { Name = "n", Type = DslValueType.Int, Scope = VarScope.PerPlayer } };
            m.Triggers[0].Actions = new[]
            {
                new TriggerAction { Type = "create_timer", TimerName = "spawn_clock", TimerSeconds = Fixed.FromInt(30) },
                new TriggerAction { Type = "set_variable", Variable = "n", Faction = 9, Value = 5 },
            };
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("actions[1].faction", r.Error!);
        }

        // ── Story 7.3 P6 — variable/timer declaration rules (blank/duplicate) + the declared-timer-seeding path ──

        [Fact]
        public void BlankVariableName_IsRejected()
        {
            var m = ValidModelWithTrigger();
            m.Variables = new[] { new ScenarioVariable { Name = "   ", Type = DslValueType.Int, Scope = VarScope.Global } };
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("variables[0].name", r.Error!);
        }

        [Fact]
        public void DuplicateVariableName_IsRejected()
        {
            var m = ValidModelWithTrigger();
            m.Variables = new[]
            {
                new ScenarioVariable { Name = "dup", Type = DslValueType.Int, Scope = VarScope.Global },
                new ScenarioVariable { Name = "dup", Type = DslValueType.Int, Scope = VarScope.PerPlayer },
            };
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("variables[1].name", r.Error!);
            Assert.Contains("duplicate", r.Error!);
        }

        [Fact]
        public void BlankTimerName_IsRejected()
        {
            var m = ValidModelWithTrigger();
            m.Timers = new[] { new ScenarioTimer { Name = "", Seconds = Fixed.FromInt(5) } };
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("timers[0].name", r.Error!);
        }

        [Fact]
        public void TimerExpires_NamingADeclaredScenarioTimer_IsAccepted()
        {
            // The declared-timer-seeding path: a timer_expires that names a declared ScenarioTimer (never created by
            // any create_timer action) is ACCEPTED (declaredTimers is seeded with the ScenarioTimer names), not
            // flagged dangling. A positive test proving the seeding actually works.
            var m = ValidModelWithTrigger();
            m.Timers = new[] { new ScenarioTimer { Name = "declared_clock", Seconds = Fixed.FromInt(5) } };
            m.Triggers[0].Events = new[]
            {
                new TriggerEvent { Type = "resource_threshold", Faction = 1, Amount = Fixed.FromInt(500), Operator = ">=" },
                new TriggerEvent { Type = "timer_expires", TimerName = "declared_clock" }, // only in m.Timers, never create_timer'd
            };
            ValidationResult r = NewValidator().Validate(m);
            Assert.True(r.Ok, r.Error);
        }

        // ── Review follow-up — the trigger_graph gate also runs the 7.2 cycle guard + parse-level id sanity ──

        [Fact]
        public void CyclicTriggerGraph_IsRejectedAtTheGate_NotMidApply()
        {
            // A cyclic exec chain parses fine (FromJson does no structural checks) but previously blew up ONLY
            // inside LoadScenario — the exact partial-apply crash the gate exists to close. The chain-rejoin
            // edge (b → a) necessarily gives node a TWO incoming exec edges, so since DW-358 the structural
            // gate's forked exec-IN reject fires FIRST (every trigger-reachable exec cycle forks an exec-in);
            // BuildExecutionOrder's cycle guard stays the defense-in-depth for direct callers (pinned by
            // ForEachExecutionTests.BodyChainRejoiningAncestor_IsALocatedCycleReject). Either way the reject is
            // a LOCATED validation error at the gate — never a mid-apply crash.
            var g = new TriggerGraph();
            g.Nodes.Add(new TriggerNode { Id = 0, Name = "cyc" });
            g.Nodes.Add(new EventNode { Id = 1, Kind = "match_start" });
            g.Nodes.Add(new ActionNode { Id = 2, Kind = "display_message", Text = "a" });
            g.Nodes.Add(new ActionNode { Id = 3, Kind = "display_message", Text = "b" });
            g.ExecEdges.Add(new ExecEdge(1, 0, 0, 0)); // event → trigger
            g.ExecEdges.Add(new ExecEdge(0, 0, 2, 0)); // trigger → a
            g.ExecEdges.Add(new ExecEdge(2, 0, 3, 0)); // a → b
            g.ExecEdges.Add(new ExecEdge(3, 0, 2, 0)); // b → a (cycle; forks a's exec-in)

            var m = ValidModelWithTrigger();
            m.TriggerGraphJson = g.ToCanonicalJson();
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("trigger_graph", r.Error!);
            Assert.Contains("multiple exec edges enter port", r.Error!);
        }

        [Fact]
        public void NegativeNodeId_InTriggerGraph_IsRejected()
        {
            // Parse-level id sanity: canonical ids are 0-based, and Merge's id-offset union assumes non-negative
            // ids (a negative authored id could alias onto an existing node after offsetting and silently
            // drop/rewire a trigger). FromJson rejects it fail-closed.
            var m = ValidModelWithTrigger();
            m.TriggerGraphJson = """
            { "nodes": [ { "id": -1, "kind": "trigger" } ], "exec_edges": [], "data_edges": [] }
            """;
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("non-negative", r.Error!);
        }

        // ── Review follow-up — the graph channel gets the SAME semantic rulebook as the flat channel ──

        private static string SingleNodeGraph(NodeBase node, bool wireAsEvent = false, bool wireAsCondition = false, bool wireAsAction = false)
        {
            var g = new TriggerGraph();
            g.Nodes.Add(new TriggerNode { Id = 0, Name = "t" });
            g.Nodes.Add(new EventNode { Id = 1, Kind = "match_start" });
            g.ExecEdges.Add(new ExecEdge(1, 0, 0, 0));
            g.Nodes.Add(node);
            if (wireAsEvent)     g.ExecEdges.Add(new ExecEdge(node.Id, 0, 0, 0));
            if (wireAsCondition) g.DataEdges.Add(new DataEdge(node.Id, 0, 0, 1, DataWireType.Boolean));
            if (wireAsAction)    g.ExecEdges.Add(new ExecEdge(0, 0, node.Id, 0));
            return g.ToCanonicalJson();
        }

        [Fact]
        public void GraphChannel_ConditionReadingTriggerLocalVariable_IsRejected()
        {
            var m = ValidModelWithTrigger();
            m.Variables = new[] { new ScenarioVariable { Name = "loc", Type = DslValueType.Int, Scope = VarScope.TriggerLocal } };
            m.TriggerGraphJson = SingleNodeGraph(
                new ConditionNode { Id = 2, Kind = "variable_comparison", Variable = "loc", Operator = "==", Value = 1 },
                wireAsCondition: true);
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("trigger_graph condition node 2", r.Error!);
            Assert.Contains("TriggerLocal", r.Error!);
        }

        [Fact]
        public void GraphChannel_SetVariableOnNonIntVariable_IsRejected()
        {
            var m = ValidModelWithTrigger();
            m.Variables = new[] { new ScenarioVariable { Name = "fx", Type = DslValueType.Fixed, Scope = VarScope.Global } };
            m.TriggerGraphJson = SingleNodeGraph(
                new ActionNode { Id = 2, Kind = "set_variable", Variable = "fx", Value = 5 },
                wireAsAction: true);
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("trigger_graph action node 2", r.Error!);
            Assert.Contains("Int-typed", r.Error!);
        }

        [Fact]
        public void GraphChannel_FactionAboveEngineCeiling_IsRejected()
        {
            // The same engine-ceiling OOB crash class the flat channel rejects: (Faction)(slot+1) indexes size-5
            // per-faction arrays at runtime, identically in both channels.
            var m = ValidModelWithTrigger();
            m.TriggerGraphJson = SingleNodeGraph(
                new EventNode { Id = 2, Kind = "unit_dies", Faction = 9 },
                wireAsEvent: true);
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("trigger_graph event node 2.faction", r.Error!);
        }

        [Fact]
        public void GraphChannel_DanglingTimerExpires_IsRejected()
        {
            var m = ValidModelWithTrigger();
            m.TriggerGraphJson = SingleNodeGraph(
                new EventNode { Id = 2, Kind = "timer_expires", TimerName = "ghost" },
                wireAsEvent: true);
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("trigger_graph event node 2", r.Error!);
            Assert.Contains("ghost", r.Error!);
        }

        [Fact]
        public void FlatTimerExpires_NamingAGraphCreateTimer_IsAccepted()
        {
            // Cross-channel timer namespace: the director merges both channels into ONE execution graph, so a flat
            // timer_expires may legitimately reference a timer a GRAPH create_timer action arms. The dangling-timer
            // union must span both channels (it previously false-flagged this as dangling).
            var m = ValidModelWithTrigger();
            m.Triggers[0].Events = new[]
            {
                new TriggerEvent { Type = "resource_threshold", Faction = 1, Amount = Fixed.FromInt(500), Operator = ">=" },
                new TriggerEvent { Type = "timer_expires", TimerName = "graph_clock" }, // armed only by the graph channel
            };
            m.TriggerGraphJson = SingleNodeGraph(
                new ActionNode { Id = 2, Kind = "create_timer", TimerName = "graph_clock", TimerSeconds = Fixed.FromInt(10) },
                wireAsAction: true);
            ValidationResult r = NewValidator().Validate(m);
            Assert.True(r.Ok, r.Error);
        }

        // ── Review follow-up — declaration well-formedness beyond names ──

        [Fact]
        public void DeclaredTimer_NonPositiveSeconds_IsRejected()
        {
            // The load path clamps via Math.Max(1, SecondsToTicks(s)) — a zero/negative declaration would silently
            // become a 1-tick timer firing on the first tick. The declaration path must not be more permissive than
            // the create_timer action (which requires > 0 at runtime).
            var m = ValidModelWithTrigger();
            m.Timers = new[] { new ScenarioTimer { Name = "z", Seconds = Fixed.Zero } };
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("timers[0].seconds", r.Error!);
        }

        [Fact]
        public void IntVariable_FractionalInitial_IsRejected()
        {
            // An Int initial of 2.5 would silently truncate to 2 at load (ScopeInitialRaw → ToInt) — fail-closed
            // instead of silently rewriting the declaration. (A negative WHOLE initial stays legal.)
            var m = ValidModelWithTrigger();
            m.Variables = new[] { new ScenarioVariable { Name = "i", Type = DslValueType.Int, Scope = VarScope.Global, Initial = Fixed.FromFloat(2.5f) } };
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("variables[0].initial", r.Error!);

            var ok = ValidModelWithTrigger();
            ok.Variables = new[] { new ScenarioVariable { Name = "i", Type = DslValueType.Int, Scope = VarScope.Global, Initial = Fixed.FromInt(-3) } };
            Assert.True(NewValidator().Validate(ok).Ok);
        }

        [Fact]
        public void BoolVariable_NonBinaryInitial_IsRejected()
        {
            var m = ValidModelWithTrigger();
            m.Variables = new[] { new ScenarioVariable { Name = "b", Type = DslValueType.Bool, Scope = VarScope.Global, Initial = Fixed.FromInt(7) } };
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("variables[0].initial", r.Error!);

            var ok = ValidModelWithTrigger();
            ok.Variables = new[] { new ScenarioVariable { Name = "b", Type = DslValueType.Bool, Scope = VarScope.Global, Initial = Fixed.One } };
            Assert.True(NewValidator().Validate(ok).Ok);
        }

        // ── Story 7.4 — the expression layer at the validator gate ──

        /// <summary>Declared-variable map matching the m.Variables set the expression tests declare.</summary>
        private static readonly System.Collections.Generic.Dictionary<string, (DslValueType Type, VarScope Scope)> ExprVars =
            new(System.StringComparer.Ordinal)
            {
                ["gold"] = (DslValueType.Int,   VarScope.Global),
                ["done"] = (DslValueType.Bool,  VarScope.Global),
                ["rate"] = (DslValueType.Fixed, VarScope.Global),
                ["tl"]   = (DslValueType.Int,   VarScope.TriggerLocal),
            };

        private static ScenarioVariable[] ExprVarDecls() => new[]
        {
            new ScenarioVariable { Name = "gold", Type = DslValueType.Int,   Scope = VarScope.Global },
            new ScenarioVariable { Name = "done", Type = DslValueType.Bool,  Scope = VarScope.Global },
            new ScenarioVariable { Name = "rate", Type = DslValueType.Fixed, Scope = VarScope.Global },
            new ScenarioVariable { Name = "tl",   Type = DslValueType.Int,   Scope = VarScope.TriggerLocal },
        };

        /// <summary>A single-trigger graph whose condition-in port is fed by the given expression NODES (hand-built
        /// raw IR): trigger 0, match_start event 1, then the supplied nodes/edges, with <paramref name="rootId"/>
        /// wired into the condition-in port over the given wire.</summary>
        private static string ConditionExprGraph(NodeBase[] exprNodes, DataEdge[] operandEdges, int rootId,
            DataWireType rootWire = DataWireType.Boolean)
        {
            var g = new TriggerGraph();
            g.Nodes.Add(new TriggerNode { Id = 0, Name = "t" });
            g.Nodes.Add(new EventNode { Id = 1, Kind = "match_start" });
            g.ExecEdges.Add(new ExecEdge(1, 0, 0, 0));
            foreach (NodeBase n in exprNodes) g.Nodes.Add(n);
            foreach (DataEdge e in operandEdges) g.DataEdges.Add(e);
            g.DataEdges.Add(new DataEdge(rootId, TriggerGraph.ExprDataOutPort, 0, TriggerGraph.TriggerConditionInPort, rootWire));
            return g.ToCanonicalJson();
        }

        [Fact]
        public void WellTypedExpressionCondition_IsAccepted()
        {
            var m = ValidModelWithTrigger();
            m.Variables = ExprVarDecls();
            m.TriggerGraphJson = TriggerGraph.BuildExpressionTrigger(
                "t", "match_start", "(gold >= 10 && !done) || count(1) < 5", "gold", 0, "(gold + 5) * 2", ExprVars)
                .ToCanonicalJson();
            ValidationResult r = NewValidator().Validate(m);
            Assert.True(r.Ok, r.Error);
        }

        [Fact]
        public void ExpressionTypeMismatch_IsRejected_WithLocatedError()
        {
            // Bool && Int — a strict-typing reject surfaced as a located ValidationResult.Fail at the gate.
            var m = ValidModelWithTrigger();
            m.Variables = ExprVarDecls();
            m.TriggerGraphJson = ConditionExprGraph(
                new NodeBase[]
                {
                    new ExprVarNode { Id = 2, Name = "done" },
                    new ExprLiteralNode { Id = 3, ValueType = DslValueType.Int, Raw = 1 },
                    new ExprBinaryNode { Id = 4, Op = "and" },
                },
                new[]
                {
                    new DataEdge(2, 0, 4, TriggerGraph.ExprOperandPort0, DataWireType.Boolean),
                    new DataEdge(3, 0, 4, TriggerGraph.ExprOperandPort1, DataWireType.Int),
                },
                rootId: 4);
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("expr node 4", r.Error!);
        }

        [Fact]
        public void ExpressionLiteralZeroDivisor_IsRejectedAtTheGate()
        {
            var m = ValidModelWithTrigger();
            m.Variables = ExprVarDecls();
            m.TriggerGraphJson = ConditionExprGraph(
                new NodeBase[]
                {
                    new ExprVarNode { Id = 2, Name = "gold" },
                    new ExprLiteralNode { Id = 3, ValueType = DslValueType.Int, Raw = 0 },
                    new ExprBinaryNode { Id = 4, Op = "div" },
                    new ExprLiteralNode { Id = 5, ValueType = DslValueType.Int, Raw = 0 },
                    new ExprBinaryNode { Id = 6, Op = "gt" },
                },
                new[]
                {
                    new DataEdge(2, 0, 4, TriggerGraph.ExprOperandPort0, DataWireType.Int),
                    new DataEdge(3, 0, 4, TriggerGraph.ExprOperandPort1, DataWireType.Int),
                    new DataEdge(4, 0, 6, TriggerGraph.ExprOperandPort0, DataWireType.Int),
                    new DataEdge(5, 0, 6, TriggerGraph.ExprOperandPort1, DataWireType.Int),
                },
                rootId: 6);
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("literal-zero divisor", r.Error!);
        }

        [Fact]
        public void ExpressionUndeclaredVariable_IsRejected()
        {
            var m = ValidModelWithTrigger();
            m.TriggerGraphJson = ConditionExprGraph(
                new NodeBase[]
                {
                    new ExprVarNode { Id = 2, Name = "ghost" },
                },
                System.Array.Empty<DataEdge>(),
                rootId: 2);
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("undeclared", r.Error!);
        }

        [Fact]
        public void ExpressionTriggerLocalReadInCondition_IsRejected()
        {
            var m = ValidModelWithTrigger();
            m.Variables = ExprVarDecls();
            m.TriggerGraphJson = ConditionExprGraph(
                new NodeBase[]
                {
                    new ExprVarNode { Id = 2, Name = "tl" },
                    new ExprLiteralNode { Id = 3, ValueType = DslValueType.Int, Raw = 1 },
                    new ExprBinaryNode { Id = 4, Op = "eq" },
                },
                new[]
                {
                    new DataEdge(2, 0, 4, TriggerGraph.ExprOperandPort0, DataWireType.Int),
                    new DataEdge(3, 0, 4, TriggerGraph.ExprOperandPort1, DataWireType.Int),
                },
                rootId: 4);
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("TriggerLocal", r.Error!);
        }

        [Fact]
        public void ExpressionOverDepthCap_IsRejected_NamingTheCap()
        {
            var nodes = new System.Collections.Generic.List<NodeBase>
            {
                new ExprLiteralNode { Id = 2, ValueType = DslValueType.Bool, Raw = 1 },
            };
            var edges = new System.Collections.Generic.List<DataEdge>();
            int cur = 2;
            for (int i = 0; i < ExprBounds.MaxExprDepth; i++)
            {
                int id = 3 + i;
                nodes.Add(new ExprUnaryNode { Id = id, Op = "not" });
                edges.Add(new DataEdge(cur, 0, id, TriggerGraph.ExprOperandPort0, DataWireType.Boolean));
                cur = id;
            }
            var m = ValidModelWithTrigger();
            m.TriggerGraphJson = ConditionExprGraph(nodes.ToArray(), edges.ToArray(), rootId: cur);
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("MaxExprDepth", r.Error!);
        }

        [Fact]
        public void ExprVarFactionAboveEngineCeiling_IsRejected_FailClosed()
        {
            // Story 9.2 raised the engine ceiling (CheckFactionSlot) to Player8 so it now COINCIDES with the DSL's
            // per-player structural bound (DslVarTable.PlayerSlots = 8, i.e. slots [0,8)). A per-player slotted read
            // at slot 8 (== PLAYER_COUNT) is therefore out of BOTH ranges and fail-closes at the canonical-range
            // converter gate during graph parse (which fires ahead of the redundant-now CheckFactionSlot gate).
            // Either way it must be rejected and located — an in-range read [0,7] would silently pass.
            var m = ValidModelWithTrigger();
            m.Variables = new[]
            {
                new ScenarioVariable { Name = "score", Type = DslValueType.Int, Scope = VarScope.PerPlayer },
            };
            m.TriggerGraphJson = ConditionExprGraph(
                new NodeBase[]
                {
                    new ExprVarNode { Id = 2, Name = "score", Faction = 8 },
                    new ExprLiteralNode { Id = 3, ValueType = DslValueType.Int, Raw = 1 },
                    new ExprBinaryNode { Id = 4, Op = "eq" },
                },
                new[]
                {
                    new DataEdge(2, 0, 4, TriggerGraph.ExprOperandPort0, DataWireType.Int),
                    new DataEdge(3, 0, 4, TriggerGraph.ExprOperandPort1, DataWireType.Int),
                },
                rootId: 4);
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("faction", r.Error!);
            Assert.Contains("8", r.Error!);
        }

        [Fact]
        public void StraySrcPortOnExpressionConsumerEdge_IsRejected()
        {
            // Expression nodes emit only on ExprDataOutPort (= 0): a condition-in consumer edge leaving src_port 7
            // previously compiled, validated, and round-tripped untouched — a silently-tolerated non-canonical
            // encoding, rejected located now (review, 7.4 pass 2).
            var g = new TriggerGraph();
            g.Nodes.Add(new TriggerNode { Id = 0, Name = "t" });
            g.Nodes.Add(new EventNode { Id = 1, Kind = "match_start" });
            g.ExecEdges.Add(new ExecEdge(1, 0, 0, 0));
            g.Nodes.Add(new ExprLiteralNode { Id = 2, ValueType = DslValueType.Bool, Raw = 1 });
            g.DataEdges.Add(new DataEdge(2, 7, 0, TriggerGraph.TriggerConditionInPort, DataWireType.Boolean));

            var m = ValidModelWithTrigger();
            m.TriggerGraphJson = g.ToCanonicalJson();
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            // Story 7.7: the structural gate (port legality) now fires first — same error class, located.
            Assert.Contains("is not a data-out port", r.Error!);
        }

        [Fact]
        public void NonBoolExpressionOnConditionIn_IsRejected()
        {
            // An Int-rooted expression wired into the trigger's condition-in port must reject (conditions are Bool).
            var m = ValidModelWithTrigger();
            m.Variables = ExprVarDecls();
            m.TriggerGraphJson = ConditionExprGraph(
                new NodeBase[] { new ExprVarNode { Id = 2, Name = "gold" } },
                System.Array.Empty<DataEdge>(),
                rootId: 2,
                rootWire: DataWireType.Int);
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("Bool", r.Error!);
        }

        [Fact]
        public void SetVariable_FixedTargetWithFixedValueExpression_Passes_WhileLiteralPathStillRejects()
        {
            // The 7.4 widening: a Fixed-typed target is legal WHEN fed by a Fixed value expression…
            var ok = ValidModelWithTrigger();
            ok.Variables = ExprVarDecls();
            ok.TriggerGraphJson = TriggerGraph.BuildExpressionTrigger(
                "t", "match_start", null, "rate", 0, "1.5 + 0.25", ExprVars).ToCanonicalJson();
            ValidationResult rOk = NewValidator().Validate(ok);
            Assert.True(rOk.Ok, rOk.Error);

            // …while the LITERAL path keeps the 7.3 Int-only rule (GraphChannel_SetVariableOnNonIntVariable covers
            // the located message; this pins the two paths side by side).
            var bad = ValidModelWithTrigger();
            bad.Variables = ExprVarDecls();
            bad.TriggerGraphJson = SingleNodeGraph(
                new ActionNode { Id = 2, Kind = "set_variable", Variable = "rate", Value = 5 },
                wireAsAction: true);
            ValidationResult rBad = NewValidator().Validate(bad);
            Assert.False(rBad.Ok);
            Assert.Contains("Int-typed", rBad.Error!);
        }

        [Fact]
        public void SetVariable_BoolTargetWithBoolValueExpression_Passes()
        {
            var m = ValidModelWithTrigger();
            m.Variables = ExprVarDecls();
            m.TriggerGraphJson = TriggerGraph.BuildExpressionTrigger(
                "t", "match_start", null, "done", 0, "gold > 5", ExprVars).ToCanonicalJson();
            ValidationResult r = NewValidator().Validate(m);
            Assert.True(r.Ok, r.Error);
        }

        [Fact]
        public void ValueExpressionTypeMismatchWithTarget_IsRejected()
        {
            // An Int expression wired into a Fixed-typed target: hand-built (the text helper refuses to build it).
            var g = new TriggerGraph();
            g.Nodes.Add(new TriggerNode { Id = 0, Name = "t" });
            g.Nodes.Add(new EventNode { Id = 1, Kind = "match_start" });
            g.ExecEdges.Add(new ExecEdge(1, 0, 0, 0));
            g.Nodes.Add(new ActionNode { Id = 2, Kind = "set_variable", Variable = "rate" });
            g.ExecEdges.Add(new ExecEdge(0, 0, 2, 0));
            g.Nodes.Add(new ExprLiteralNode { Id = 3, ValueType = DslValueType.Int, Raw = 7 });
            g.DataEdges.Add(new DataEdge(3, TriggerGraph.ExprDataOutPort, 2, TriggerGraph.ActionValueInPort, DataWireType.Int));

            var m = ValidModelWithTrigger();
            m.Variables = ExprVarDecls();
            m.TriggerGraphJson = g.ToCanonicalJson();
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("rate", r.Error!);
        }

        [Fact]
        public void ValueExpressionOnNonSetVariableAction_IsRejected()
        {
            // "No expression wiring into … any ActionNode field other than set_variable's value-in port."
            var g = new TriggerGraph();
            g.Nodes.Add(new TriggerNode { Id = 0, Name = "t" });
            g.Nodes.Add(new EventNode { Id = 1, Kind = "match_start" });
            g.ExecEdges.Add(new ExecEdge(1, 0, 0, 0));
            g.Nodes.Add(new ActionNode { Id = 2, Kind = "display_message", Text = "hi" });
            g.ExecEdges.Add(new ExecEdge(0, 0, 2, 0));
            g.Nodes.Add(new ExprLiteralNode { Id = 3, ValueType = DslValueType.Int, Raw = 7 });
            g.DataEdges.Add(new DataEdge(3, TriggerGraph.ExprDataOutPort, 2, TriggerGraph.ActionValueInPort, DataWireType.Int));

            var m = ValidModelWithTrigger();
            m.TriggerGraphJson = g.ToCanonicalJson();
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("set_variable", r.Error!);
        }

        [Fact]
        public void ValueExpressionIntoRunEffectNode_IsRejectedAtTheGate()
        {
            // A value-in edge whose dst is a run_effect node IS consumed by the runtime (BuildExecutionOrder
            // surfaces it and LoadScenario's compile throws mid-apply) — the gate must reject it first, not
            // pattern-match it into the "unconsumed" ignore path.
            var g = new TriggerGraph();
            g.Nodes.Add(new TriggerNode { Id = 0, Name = "t" });
            g.Nodes.Add(new EventNode { Id = 1, Kind = "match_start" });
            g.ExecEdges.Add(new ExecEdge(1, 0, 0, 0));
            g.Nodes.Add(new EffectActionNode { Id = 2, Effect = new DirectHpDeltaEffect(Fixed.FromInt(-10)) });
            g.ExecEdges.Add(new ExecEdge(0, 0, 2, 0));
            g.Nodes.Add(new ExprLiteralNode { Id = 3, ValueType = DslValueType.Int, Raw = 7 });
            g.DataEdges.Add(new DataEdge(3, TriggerGraph.ExprDataOutPort, 2, TriggerGraph.ActionValueInPort, DataWireType.Int));

            var m = ValidModelWithTrigger();
            m.TriggerGraphJson = g.ToCanonicalJson();
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            // Story 7.7: the structural gate rejects it first (run_effect has no data-in ports) — same class.
            Assert.Contains("is not a data-in port", r.Error!);
            Assert.Contains("run_effect", r.Error!);
        }

        [Fact]
        public void NonExpressionSourceOnValueInPort_IsRejected()
        {
            // A value-in edge whose SRC is not an expression node is silently ignored at runtime (the literal
            // Value would win against the authored wiring) — reject it located instead of passing the gate.
            var m = ValidModelWithTrigger();
            m.Variables = ExprVarDecls();
            var g = new TriggerGraph();
            g.Nodes.Add(new TriggerNode { Id = 0, Name = "t" });
            g.Nodes.Add(new EventNode { Id = 1, Kind = "match_start" });
            g.ExecEdges.Add(new ExecEdge(1, 0, 0, 0));
            g.Nodes.Add(new ActionNode { Id = 2, Kind = "set_variable", Variable = "gold" });
            g.ExecEdges.Add(new ExecEdge(0, 0, 2, 0));
            g.Nodes.Add(new ConditionNode { Id = 3, Kind = "always" });
            g.DataEdges.Add(new DataEdge(3, TriggerGraph.ConditionDataOutPort, 2, TriggerGraph.ActionValueInPort, DataWireType.Int));
            m.TriggerGraphJson = g.ToCanonicalJson();
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("action node 2", r.Error!);
            Assert.Contains("not an expression node", r.Error!);
        }

        [Fact]
        public void ForkedValueInEdges_AreRejected_AtTheGate()
        {
            // TWO expression edges into ONE set_variable value-in port — the seenValueInPorts fork reject
            // ("forked; exactly one allowed"), previously uncovered.
            var m = ValidModelWithTrigger();
            m.Variables = ExprVarDecls();
            var g = new TriggerGraph();
            g.Nodes.Add(new TriggerNode { Id = 0, Name = "t" });
            g.Nodes.Add(new EventNode { Id = 1, Kind = "match_start" });
            g.ExecEdges.Add(new ExecEdge(1, 0, 0, 0));
            g.Nodes.Add(new ActionNode { Id = 2, Kind = "set_variable", Variable = "gold" });
            g.ExecEdges.Add(new ExecEdge(0, 0, 2, 0));
            g.Nodes.Add(new ExprLiteralNode { Id = 3, ValueType = DslValueType.Int, Raw = 1 });
            g.Nodes.Add(new ExprLiteralNode { Id = 4, ValueType = DslValueType.Int, Raw = 2 });
            g.DataEdges.Add(new DataEdge(3, TriggerGraph.ExprDataOutPort, 2, TriggerGraph.ActionValueInPort, DataWireType.Int));
            g.DataEdges.Add(new DataEdge(4, TriggerGraph.ExprDataOutPort, 2, TriggerGraph.ActionValueInPort, DataWireType.Int));
            m.TriggerGraphJson = g.ToCanonicalJson();
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("forked", r.Error!);
        }

        // ── Story 7.5 — custom-event gate rejects (every Bad-authoring matrix row lands as a located Fail) ──

        private static ScenarioCustomEvent CustomEv(string name, params (string Name, DslValueType Type)[] ps) =>
            new()
            {
                Name = name,
                Params = ps.Length == 0
                    ? null
                    : System.Array.ConvertAll(ps, p => new ScenarioEventParam { Name = p.Name, Type = p.Type }),
            };

        private static readonly System.Collections.Generic.Dictionary<string, (DslValueType Type, VarScope Scope)>
            NoDecls = new(System.StringComparer.Ordinal);

        [Fact]
        public void ValidCustomEventScenario_PassesTheGate()
        {
            var m = ValidModelWithTrigger();
            m.Variables = new[]
            {
                new ScenarioVariable { Name = "gold",  Type = DslValueType.Int, Scope = VarScope.Global, Initial = Fixed.FromInt(4) },
                new ScenarioVariable { Name = "score", Type = DslValueType.Int, Scope = VarScope.Global },
            };
            m.CustomEvents = new[] { CustomEv("wave_start", ("count", DslValueType.Int)) };
            var decls = new System.Collections.Generic.Dictionary<string, (DslValueType Type, VarScope Scope)>(System.StringComparer.Ordinal)
            {
                ["gold"] = (DslValueType.Int, VarScope.Global), ["score"] = (DslValueType.Int, VarScope.Global),
            };
            TriggerGraph g = TriggerGraph.BuildCustomEventTrigger(
                "A", "match_start", null, null, "wave_start", new[] { "gold + 1" }, -1, false,
                null, 0, null, decls, m.CustomEvents);
            g.Merge(TriggerGraph.BuildCustomEventTrigger(
                "H", "custom_event", "wave_start", "event.count > 2", null, null, -1, false,
                "score", 0, "event.count * 10", decls, m.CustomEvents));
            m.TriggerGraphJson = g.ToCanonicalJson();

            ValidationResult r = NewValidator().Validate(m);
            Assert.True(r.Ok, r.Error);
        }

        [Fact]
        public void DuplicateCustomEventNames_AreRejected_AtTheDeclarationsBlock()
        {
            var m = ValidModelWithTrigger();
            m.CustomEvents = new[] { CustomEv("dup"), CustomEv("dup") };
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("custom_events", r.Error!);
            Assert.Contains("duplicate", r.Error!);
        }

        [Fact]
        public void CustomEventShadowingUnitDies_IsRejected()
        {
            var m = ValidModelWithTrigger();
            m.CustomEvents = new[] { CustomEv("unit_dies") };
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("shadows", r.Error!);
        }

        [Fact]
        public void RaiseOfUndeclaredEvent_IsRejected_AtTheGraphGate()
        {
            var m = ValidModelWithTrigger();
            var g = new TriggerGraph();
            g.Nodes.Add(new TriggerNode { Id = 0, Name = "t" });
            g.Nodes.Add(new EventNode { Id = 1, Kind = "match_start" });
            g.ExecEdges.Add(new ExecEdge(1, 0, 0, 0));
            g.Nodes.Add(new RaiseEventNode { Id = 2, Name = "ghost" });
            g.ExecEdges.Add(new ExecEdge(0, 0, 2, 0));
            m.TriggerGraphJson = g.ToCanonicalJson();
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("ghost", r.Error!);
            Assert.Contains("not a declared custom event", r.Error!);
        }

        [Fact]
        public void EventGhostParamRead_IsRejected_AtTheGate()
        {
            var m = ValidModelWithTrigger();
            m.CustomEvents = new[] { CustomEv("wave_start", ("count", DslValueType.Int)) };
            var g = new TriggerGraph();
            g.Nodes.Add(new TriggerNode { Id = 0, Name = "h" });
            g.Nodes.Add(new EventNode { Id = 1, Kind = "custom_event", EventName = "wave_start" });
            g.ExecEdges.Add(new ExecEdge(1, 0, 0, 0));
            g.Nodes.Add(new ExprEventParamNode { Id = 2, Name = "ghost" });
            g.DataEdges.Add(new DataEdge(2, TriggerGraph.ExprDataOutPort, 0, TriggerGraph.TriggerConditionInPort, DataWireType.Boolean));
            m.TriggerGraphJson = g.ToCanonicalJson();
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("ghost", r.Error!);
        }

        [Fact]
        public void RaiseArgArityMismatch_IsRejected_AtTheGate()
        {
            var m = ValidModelWithTrigger();
            m.CustomEvents = new[] { CustomEv("typed", ("count", DslValueType.Int)) };
            var g = new TriggerGraph();
            g.Nodes.Add(new TriggerNode { Id = 0, Name = "t" });
            g.Nodes.Add(new EventNode { Id = 1, Kind = "match_start" });
            g.ExecEdges.Add(new ExecEdge(1, 0, 0, 0));
            g.Nodes.Add(new RaiseEventNode { Id = 2, Name = "typed" }); // declared param 0 has NO arg edge
            g.ExecEdges.Add(new ExecEdge(0, 0, 2, 0));
            m.TriggerGraphJson = g.ToCanonicalJson();
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("no argument edge", r.Error!);
        }

        [Fact]
        public void RaiseArgTypeMismatch_IsRejected_AtTheGate()
        {
            var m = ValidModelWithTrigger();
            m.CustomEvents = new[] { CustomEv("typed", ("count", DslValueType.Int)) };
            var g = new TriggerGraph();
            g.Nodes.Add(new TriggerNode { Id = 0, Name = "t" });
            g.Nodes.Add(new EventNode { Id = 1, Kind = "match_start" });
            g.ExecEdges.Add(new ExecEdge(1, 0, 0, 0));
            g.Nodes.Add(new RaiseEventNode { Id = 2, Name = "typed" });
            g.ExecEdges.Add(new ExecEdge(0, 0, 2, 0));
            g.Nodes.Add(new ExprLiteralNode { Id = 3, ValueType = DslValueType.Bool, Raw = 1 }); // Bool → Int param
            g.DataEdges.Add(new DataEdge(3, TriggerGraph.ExprDataOutPort, 2, TriggerGraph.RaiseArgInPort0, DataWireType.Boolean));
            m.TriggerGraphJson = g.ToCanonicalJson();
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("wire", r.Error!);
        }

        [Fact]
        public void RaiserNotInAllowedRaisers_IsRejected_AtTheGate()
        {
            var m = ValidModelWithTrigger();
            m.CustomEvents = new[]
            {
                new ScenarioCustomEvent { Name = "gated", AllowedRaisers = new[] { 0 } },
            };
            var g = new TriggerGraph();
            g.Nodes.Add(new TriggerNode { Id = 0, Name = "t" });
            g.Nodes.Add(new EventNode { Id = 1, Kind = "match_start" });
            g.ExecEdges.Add(new ExecEdge(1, 0, 0, 0));
            g.Nodes.Add(new RaiseEventNode { Id = 2, Name = "gated", Raiser = 1 }); // 1 ∉ {0}
            g.ExecEdges.Add(new ExecEdge(0, 0, 2, 0));
            m.TriggerGraphJson = g.ToCanonicalJson();
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("allowed_raisers", r.Error!);
        }

        [Fact]
        public void ParamReadOnAMultiEventTrigger_IsRejected_AtTheGate()
        {
            // A trigger subscribed to TWO built-in events whose condition reads event params: no single
            // subscription declares them, so the compile rejects located (the single-subscription rule).
            var m = ValidModelWithTrigger();
            var g = new TriggerGraph();
            g.Nodes.Add(new TriggerNode { Id = 0, Name = "multi" });
            g.Nodes.Add(new EventNode { Id = 1, Kind = "unit_dies" });
            g.ExecEdges.Add(new ExecEdge(1, 0, 0, 0));
            g.Nodes.Add(new EventNode { Id = 2, Kind = "match_start" });
            g.ExecEdges.Add(new ExecEdge(2, 0, 0, 0));
            g.Nodes.Add(new ExprEventParamNode { Id = 3, Name = "victim" });
            g.DataEdges.Add(new DataEdge(3, TriggerGraph.ExprDataOutPort, 0, TriggerGraph.TriggerConditionInPort, DataWireType.Boolean));
            m.TriggerGraphJson = g.ToCanonicalJson();
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("exactly one event", r.Error!);
        }

        [Fact]
        public void SameTickDispatchCycle_IsRejected_AtTheGate_NamingTheCycle()
        {
            var m = ValidModelWithTrigger();
            m.CustomEvents = new[] { CustomEv("e1"), CustomEv("e2") };
            TriggerGraph g = TriggerGraph.BuildCustomEventTrigger(
                "h1", "custom_event", "e1", null, "e2", null, -1, false, null, 0, null, NoDecls, m.CustomEvents);
            g.Merge(TriggerGraph.BuildCustomEventTrigger(
                "h2", "custom_event", "e2", null, "e1", null, -1, false, null, 0, null, NoDecls, m.CustomEvents));
            m.TriggerGraphJson = g.ToCanonicalJson();
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("e1→e2→e1", r.Error!);
        }

        [Fact]
        public void FanOutOverCap_IsRejected_AtTheGate_NamingTheConstant()
        {
            var m = ValidModelWithTrigger();
            m.CustomEvents = new[] { CustomEv("hub") };
            TriggerGraph g = TriggerGraph.BuildCustomEventTrigger(
                "h0", "custom_event", "hub", null, null, null, -1, false, null, 0, null, NoDecls, m.CustomEvents);
            for (int i = 1; i <= EventBounds.MaxEventFanOut; i++) // cap + 1 subscribers total
                g.Merge(TriggerGraph.BuildCustomEventTrigger(
                    $"h{i}", "custom_event", "hub", null, null, null, -1, false, null, 0, null, NoDecls, m.CustomEvents));
            m.TriggerGraphJson = g.ToCanonicalJson();
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("MaxEventFanOut", r.Error!);
        }

        // AC3c — AR-13 (a random effect is valid only if it draws from SimRng) stays RESERVED: no random
        // trigger-effect TYPE exists pre-Epic-2, so there is nothing to validate yet. This is the documented
        // pending case; the mature rule is enforced by Epic 2's effect-validator (Story 2.3) the first moment an
        // effect schema exists. Do NOT fabricate a random effect type here, and never invoke a real LLM in a test.
        [Fact(Skip = "AR-13 reserved until the Epic 2 effect schema (Story 2.3) — no random trigger-effect type exists pre-Epic-2.")]
        public void RandomEffect_MustDrawFromSimRng_ReservedUntilStory2_3() { }
    }

    /// <summary>
    /// Story 7.6 — the loop/array/fuel LOAD gate (validator side) plus the <c>ScenarioDirector.LoadScenario</c>
    /// backstop parity net: every bad-declaration / bad-loop shape in the I/O matrix rejects LOCATED at the gate
    /// (naming the DslBounds constant where a cap is involved), and — via the SHARED DslLoopGate — identically at
    /// the backstop for direct LoadScenario callers.
    /// </summary>
    public class LoopGateValidationTests
    {
        private static ScenarioValidator NewValidator() => new();

        private static ScenarioData BaseModel() => new ScenarioData
        {
            MapBounds = 120f,
            WinCondition = WinCondition.DestroyAllBuildings,
            PlayerSlots = new[]
            {
                new ScenarioPlayerSlot { Slot = 0, FactionJson = "res://a.json", StartOre = 100f, BaseX = -45f, BaseZ = 0f },
                new ScenarioPlayerSlot { Slot = 1, FactionJson = "res://b.json", StartOre = 100f, BaseX =  45f, BaseZ = 0f },
            },
            ResourceNodes = System.Array.Empty<ScenarioResourceNode>(),
            Buildings = System.Array.Empty<ScenarioBuilding>(),
            Units = System.Array.Empty<ScenarioUnit>(),
            Triggers = System.Array.Empty<TriggerDefinition>(),
            Regions = new[] { new ScenarioRegion { Id = "zone", MinX = -10f, MinZ = -10f, MaxX = 10f, MaxZ = 10f } },
        };

        private static ScenarioVariable IntArray(string name, int capacity = 8) => new()
        {
            Name = name, Type = DslValueType.Array, Scope = VarScope.Global,
            ElementType = DslValueType.Int, Capacity = capacity,
        };

        private static ScenarioVariable LocalInt(string name) => new()
        {
            Name = name, Type = DslValueType.Int, Scope = VarScope.TriggerLocal,
        };

        /// <summary>A single-trigger graph: match_start → <paramref name="chainHead"/> (nodes/edges appended by the caller).</summary>
        private static TriggerGraph SingleTrigger(out int nextId)
        {
            var g = new TriggerGraph();
            g.Nodes.Add(new TriggerNode { Id = 0, Name = "t" });
            g.Nodes.Add(new EventNode { Id = 1, Kind = "match_start" });
            g.ExecEdges.Add(new ExecEdge(1, TriggerGraph.EventExecOutPort, 0, TriggerGraph.TriggerEventInPort));
            nextId = 2;
            return g;
        }

        private static ValidationResult Validate(ScenarioData m) => NewValidator().Validate(m);

        private static void AssertRejectedAtBothGates(ScenarioData m, string messageFragment)
        {
            ValidationResult r = Validate(m);
            Assert.False(r.Ok, "the validator gate should reject");
            Assert.Contains(messageFragment, r.Error!);

            // Backstop parity: a direct LoadScenario caller fails closed with the SAME located rule (the shared
            // DslLoopGate — one implementation, no drift).
            var director = new ScenarioDirector(new BuildingStore(), new ResourceStore(Fixed.Zero), new DslVarTable(), new DslLoopState());
            var ex = Assert.Throws<System.Text.Json.JsonException>(() => director.LoadScenario(m));
            Assert.Contains(messageFragment, ex.Message);
        }

        // ── Bad declarations (I/O matrix row) ───────────────────────────────────

        [Fact]
        public void ArrayWithoutCapacity_IsRejectedAtBothGates()
        {
            ScenarioData m = BaseModel();
            m.Variables = new[] { new ScenarioVariable { Name = "a", Type = DslValueType.Array, Scope = VarScope.Global, ElementType = DslValueType.Int } };
            AssertRejectedAtBothGates(m, "requires 'capacity'");
        }

        [Fact]
        public void ArrayWithoutElementType_IsRejectedAtBothGates()
        {
            ScenarioData m = BaseModel();
            m.Variables = new[] { new ScenarioVariable { Name = "a", Type = DslValueType.Array, Scope = VarScope.Global, Capacity = 4 } };
            AssertRejectedAtBothGates(m, "requires 'element_type'");
        }

        [Theory]
        [InlineData(0)]
        [InlineData(65)]
        public void ArrayCapacityOutOfRange_IsRejectedAtBothGates(int capacity)
        {
            ScenarioData m = BaseModel();
            m.Variables = new[] { IntArray("a", capacity) };
            AssertRejectedAtBothGates(m, "MaxArrayCapacity");
        }

        [Fact]
        public void PerPlayerArray_IsRejectedAtBothGates()
        {
            ScenarioData m = BaseModel();
            var v = IntArray("a");
            v.Scope = VarScope.PerPlayer;
            m.Variables = new[] { v };
            AssertRejectedAtBothGates(m, "Global-scope only");
        }

        [Fact]
        public void StrayElementTypeOnScalar_IsRejected()
        {
            ScenarioData m = BaseModel();
            m.Variables = new[] { new ScenarioVariable { Name = "n", Type = DslValueType.Int, Scope = VarScope.Global, ElementType = DslValueType.Int } };
            ValidationResult r = Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("only valid on an Array-typed declaration", r.Error!);
        }

        // ── Loud caps (I/O matrix rows: missing up_to, cap-product overflow) ────

        [Fact]
        public void EntityForEachWithoutUpTo_IsRejected_DirectingToBatchedOrUpTo()
        {
            ScenarioData m = BaseModel();
            TriggerGraph g = SingleTrigger(out int id);
            g.Nodes.Add(new ForEachNode { Id = id, Source = "faction_units", Faction = 0 }); // UpTo unset
            g.ExecEdges.Add(new ExecEdge(0, TriggerGraph.TriggerExecOutPort, id, TriggerGraph.ActionExecInPort));
            m.TriggerGraphJson = g.ToCanonicalJson();

            ValidationResult r = Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("up_to", r.Error!);
            Assert.Contains("for_each_batched", r.Error!); // the error DIRECTS the author to the two fixes
            AssertRejectedAtBothGates(m, "for_each_batched");
        }

        [Fact]
        public void UpToBeyondMaxForEachItems_IsRejectedNamingTheConstant()
        {
            ScenarioData m = BaseModel();
            TriggerGraph g = SingleTrigger(out int id);
            g.Nodes.Add(new ForEachNode { Id = id, Source = "faction_units", Faction = 0, UpTo = 65 });
            g.ExecEdges.Add(new ExecEdge(0, TriggerGraph.TriggerExecOutPort, id, TriggerGraph.ActionExecInPort));
            m.TriggerGraphJson = g.ToCanonicalJson();
            AssertRejectedAtBothGates(m, "MaxForEachItems");
        }

        [Fact]
        public void NestedCapProductOverflow_IsRejectedNamingMaxDslOpsPerTrigger()
        {
            // 64 × 64 × 2 body actions = 1 + 64×(1 + 64×2) = 8257 > MaxDslOpsPerTrigger (4096).
            ScenarioData m = BaseModel();
            TriggerGraph g = SingleTrigger(out int id);
            int outer = id++, inner = id++, a1 = id++, a2 = id++;
            g.Nodes.Add(new ForEachNode { Id = outer, Source = "faction_units", Faction = 0, UpTo = 64 });
            g.Nodes.Add(new ForEachNode { Id = inner, Source = "faction_units", Faction = 0, UpTo = 64 });
            g.Nodes.Add(new ActionNode { Id = a1, Kind = "set_variable", Variable = "x", Value = 1 });
            g.Nodes.Add(new ActionNode { Id = a2, Kind = "set_variable", Variable = "x", Value = 2 });
            g.ExecEdges.Add(new ExecEdge(0, TriggerGraph.TriggerExecOutPort, outer, TriggerGraph.ActionExecInPort));
            g.ExecEdges.Add(new ExecEdge(outer, TriggerGraph.ForEachBodyOutPort, inner, TriggerGraph.ActionExecInPort));
            g.ExecEdges.Add(new ExecEdge(inner, TriggerGraph.ForEachBodyOutPort, a1, TriggerGraph.ActionExecInPort));
            g.ExecEdges.Add(new ExecEdge(a1, TriggerGraph.ActionExecOutPort, a2, TriggerGraph.ActionExecInPort));
            m.TriggerGraphJson = g.ToCanonicalJson();
            AssertRejectedAtBothGates(m, "MaxDslOpsPerTrigger");
        }

        [Fact]
        public void NestingBeyondMaxLoopNesting_IsRejectedNamingTheConstant()
        {
            // 5 nested loops (up_to 1 keeps the cost tiny — only DEPTH trips).
            ScenarioData m = BaseModel();
            TriggerGraph g = SingleTrigger(out int id);
            int prev = 0, prevPort = TriggerGraph.TriggerExecOutPort;
            for (int d = 0; d < 5; d++)
            {
                int loop = id++;
                g.Nodes.Add(new ForEachNode { Id = loop, Source = "faction_units", Faction = 0, UpTo = 1 });
                g.ExecEdges.Add(new ExecEdge(prev, prevPort, loop, TriggerGraph.ActionExecInPort));
                prev = loop;
                prevPort = TriggerGraph.ForEachBodyOutPort;
            }
            m.TriggerGraphJson = g.ToCanonicalJson();
            AssertRejectedAtBothGates(m, "MaxLoopNesting");
        }

        // ── Loop-var / source rules ─────────────────────────────────────────────

        [Fact]
        public void ArrayLoopWithoutLoopVar_IsRejected()
        {
            ScenarioData m = BaseModel();
            m.Variables = new[] { IntArray("arr") };
            TriggerGraph g = SingleTrigger(out int id);
            g.Nodes.Add(new ForEachNode { Id = id, Source = "array", ArrayName = "arr" });
            g.ExecEdges.Add(new ExecEdge(0, TriggerGraph.TriggerExecOutPort, id, TriggerGraph.ActionExecInPort));
            m.TriggerGraphJson = g.ToCanonicalJson();
            AssertRejectedAtBothGates(m, "loop_var");
        }

        [Fact]
        public void LoopVarNotTriggerLocal_IsRejected()
        {
            ScenarioData m = BaseModel();
            m.Variables = new[]
            {
                IntArray("arr"),
                new ScenarioVariable { Name = "v", Type = DslValueType.Int, Scope = VarScope.Global },
            };
            TriggerGraph g = SingleTrigger(out int id);
            g.Nodes.Add(new ForEachNode { Id = id, Source = "array", ArrayName = "arr", LoopVar = "v" });
            g.ExecEdges.Add(new ExecEdge(0, TriggerGraph.TriggerExecOutPort, id, TriggerGraph.ActionExecInPort));
            m.TriggerGraphJson = g.ToCanonicalJson();
            AssertRejectedAtBothGates(m, "must be TriggerLocal-scoped");
        }

        [Fact]
        public void LoopVarTypeMismatch_IsRejected()
        {
            ScenarioData m = BaseModel();
            var arr = IntArray("arr");
            arr.ElementType = DslValueType.Fixed; // Fixed elements
            m.Variables = new[] { arr, LocalInt("v") }; // but an Int loop var
            TriggerGraph g = SingleTrigger(out int id);
            g.Nodes.Add(new ForEachNode { Id = id, Source = "array", ArrayName = "arr", LoopVar = "v" });
            g.ExecEdges.Add(new ExecEdge(0, TriggerGraph.TriggerExecOutPort, id, TriggerGraph.ActionExecInPort));
            m.TriggerGraphJson = g.ToCanonicalJson();
            AssertRejectedAtBothGates(m, "Int-typed; this loop writes Fixed");
        }

        [Fact]
        public void ArrayLoopOverUndeclaredArray_IsRejected()
        {
            ScenarioData m = BaseModel();
            m.Variables = new[] { LocalInt("v") };
            TriggerGraph g = SingleTrigger(out int id);
            g.Nodes.Add(new ForEachNode { Id = id, Source = "array", ArrayName = "ghost", LoopVar = "v" });
            g.ExecEdges.Add(new ExecEdge(0, TriggerGraph.TriggerExecOutPort, id, TriggerGraph.ActionExecInPort));
            m.TriggerGraphJson = g.ToCanonicalJson();
            AssertRejectedAtBothGates(m, "declared Array variable");
        }

        [Fact]
        public void RegionUnitsOverUnknownRegion_IsRejected()
        {
            ScenarioData m = BaseModel();
            TriggerGraph g = SingleTrigger(out int id);
            g.Nodes.Add(new ForEachNode { Id = id, Source = "region_units", RegionId = "nowhere", UpTo = 8 });
            g.ExecEdges.Add(new ExecEdge(0, TriggerGraph.TriggerExecOutPort, id, TriggerGraph.ActionExecInPort));
            m.TriggerGraphJson = g.ToCanonicalJson();
            AssertRejectedAtBothGates(m, "references no declared region");
        }

        // ── Batched restrictions ────────────────────────────────────────────────

        [Fact]
        public void BatchedNestedInsideForEach_IsRejected()
        {
            ScenarioData m = BaseModel();
            TriggerGraph g = SingleTrigger(out int id);
            int outer = id++, nested = id++;
            g.Nodes.Add(new ForEachNode { Id = outer, Source = "faction_units", Faction = 0, UpTo = 4 });
            g.Nodes.Add(new ForEachBatchedNode { Id = nested, Source = "faction_units", Faction = 0, BatchSize = 4 });
            g.ExecEdges.Add(new ExecEdge(0, TriggerGraph.TriggerExecOutPort, outer, TriggerGraph.ActionExecInPort));
            g.ExecEdges.Add(new ExecEdge(outer, TriggerGraph.ForEachBodyOutPort, nested, TriggerGraph.ActionExecInPort));
            m.TriggerGraphJson = g.ToCanonicalJson();
            AssertRejectedAtBothGates(m, "TOP-LEVEL");
        }

        [Fact]
        public void TwoBatchedInOneTrigger_IsRejected()
        {
            ScenarioData m = BaseModel();
            TriggerGraph g = SingleTrigger(out int id);
            int b1 = id++, b2 = id++;
            g.Nodes.Add(new ForEachBatchedNode { Id = b1, Source = "faction_units", Faction = 0, BatchSize = 4 });
            g.Nodes.Add(new ForEachBatchedNode { Id = b2, Source = "faction_units", Faction = 0, BatchSize = 4 });
            g.ExecEdges.Add(new ExecEdge(0, TriggerGraph.TriggerExecOutPort, b1, TriggerGraph.ActionExecInPort));
            g.ExecEdges.Add(new ExecEdge(b1, TriggerGraph.ActionExecOutPort, b2, TriggerGraph.ActionExecInPort));
            m.TriggerGraphJson = g.ToCanonicalJson();
            AssertRejectedAtBothGates(m, "at most ONE");
        }

        [Fact]
        public void MoreThanMaxBatchedLoops_IsRejected()
        {
            ScenarioData m = BaseModel();
            var g = new TriggerGraph();
            int id = 0;
            for (int t = 0; t < DslBounds.MaxBatchedLoops + 1; t++)
            {
                int trig = id++, ev = id++, batched = id++;
                g.Nodes.Add(new TriggerNode { Id = trig, Name = $"t{t}" });
                g.Nodes.Add(new EventNode { Id = ev, Kind = "match_start" });
                g.Nodes.Add(new ForEachBatchedNode { Id = batched, Source = "faction_units", Faction = 0, BatchSize = 4 });
                g.ExecEdges.Add(new ExecEdge(ev, TriggerGraph.EventExecOutPort, trig, TriggerGraph.TriggerEventInPort));
                g.ExecEdges.Add(new ExecEdge(trig, TriggerGraph.TriggerExecOutPort, batched, TriggerGraph.ActionExecInPort));
            }
            m.TriggerGraphJson = g.ToCanonicalJson();
            AssertRejectedAtBothGates(m, "MaxBatchedLoops");
        }

        [Fact]
        public void BatchSizeOutOfRange_IsRejected()
        {
            ScenarioData m = BaseModel();
            TriggerGraph g = SingleTrigger(out int id);
            g.Nodes.Add(new ForEachBatchedNode { Id = id, Source = "faction_units", Faction = 0, BatchSize = 0 });
            g.ExecEdges.Add(new ExecEdge(0, TriggerGraph.TriggerExecOutPort, id, TriggerGraph.ActionExecInPort));
            m.TriggerGraphJson = g.ToCanonicalJson();
            AssertRejectedAtBothGates(m, "MaxForEachItems");
        }

        // ── Array-action shapes ─────────────────────────────────────────────────

        [Fact]
        public void ArrayPushWithoutValueEdge_IsRejected()
        {
            ScenarioData m = BaseModel();
            m.Variables = new[] { IntArray("arr") };
            TriggerGraph g = SingleTrigger(out int id);
            g.Nodes.Add(new ActionNode { Id = id, Kind = "array_push", Variable = "arr" });
            g.ExecEdges.Add(new ExecEdge(0, TriggerGraph.TriggerExecOutPort, id, TriggerGraph.ActionExecInPort));
            m.TriggerGraphJson = g.ToCanonicalJson();
            AssertRejectedAtBothGates(m, "requires a value-in expression edge");
        }

        [Fact]
        public void ArraySetWithoutIndexEdge_IsRejected()
        {
            ScenarioData m = BaseModel();
            m.Variables = new[] { IntArray("arr") };
            TriggerGraph g = SingleTrigger(out int id);
            int act = id;
            g.Nodes.Add(new ActionNode { Id = act, Kind = "array_set", Variable = "arr" });
            g.Nodes.Add(new ExprLiteralNode { Id = act + 1, ValueType = DslValueType.Int, Raw = 1 });
            g.ExecEdges.Add(new ExecEdge(0, TriggerGraph.TriggerExecOutPort, act, TriggerGraph.ActionExecInPort));
            g.DataEdges.Add(new DataEdge(act + 1, TriggerGraph.ExprDataOutPort, act, TriggerGraph.ActionValueInPort, DataWireType.Int));
            m.TriggerGraphJson = g.ToCanonicalJson();
            AssertRejectedAtBothGates(m, "index-in expression edge");
        }

        [Fact]
        public void ArrayPushElementTypeMismatch_IsRejected()
        {
            ScenarioData m = BaseModel();
            var arr = IntArray("arr");
            arr.ElementType = DslValueType.Fixed;
            m.Variables = new[] { arr };
            TriggerGraph g = SingleTrigger(out int id);
            int act = id;
            g.Nodes.Add(new ActionNode { Id = act, Kind = "array_push", Variable = "arr" });
            g.Nodes.Add(new ExprLiteralNode { Id = act + 1, ValueType = DslValueType.Int, Raw = 1 }); // Int into Fixed[]
            g.ExecEdges.Add(new ExecEdge(0, TriggerGraph.TriggerExecOutPort, act, TriggerGraph.ActionExecInPort));
            g.DataEdges.Add(new DataEdge(act + 1, TriggerGraph.ExprDataOutPort, act, TriggerGraph.ActionValueInPort, DataWireType.Int));
            m.TriggerGraphJson = g.ToCanonicalJson();
            AssertRejectedAtBothGates(m, "does not match array");
        }

        [Fact]
        public void ArrayActionOnUndeclaredArray_IsRejected()
        {
            ScenarioData m = BaseModel();
            TriggerGraph g = SingleTrigger(out int id);
            g.Nodes.Add(new ActionNode { Id = id, Kind = "array_clear", Variable = "ghost" });
            g.ExecEdges.Add(new ExecEdge(0, TriggerGraph.TriggerExecOutPort, id, TriggerGraph.ActionExecInPort));
            m.TriggerGraphJson = g.ToCanonicalJson();
            AssertRejectedAtBothGates(m, "declared Array variable");
        }

        [Fact]
        public void FlatActionTypes_StayClosedToArrayKinds()
        {
            // The flat channel's _actionTypes is untouched: a flat trigger cannot carry an array action.
            ScenarioData m = BaseModel();
            m.Triggers = new[]
            {
                new TriggerDefinition
                {
                    Name = "flat",
                    Events = new[] { new TriggerEvent { Type = "match_start" } },
                    Actions = new[] { new TriggerAction { Type = "array_push", Variable = "arr" } },
                },
            };
            ValidationResult r = Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("not a known trigger action type", r.Error!);
        }

        // ── Forked-edge rejects (review P6): cond-in / index-in forks mirror the value-in fork rule ──

        [Fact]
        public void ForkedBranchConditionEdges_AreRejectedAtBothGates()
        {
            // TWO Bool expression edges into ONE branch condition-in port — previously silently resolved
            // lowest-id-wins while the value-in port rejected the same fork shape.
            ScenarioData m = BaseModel();
            TriggerGraph g = SingleTrigger(out int id);
            int branch = id++, e1 = id++, e2 = id++, act = id++;
            g.Nodes.Add(new BranchNode { Id = branch });
            g.Nodes.Add(new ExprLiteralNode { Id = e1, ValueType = DslValueType.Bool, Raw = 1 });
            g.Nodes.Add(new ExprLiteralNode { Id = e2, ValueType = DslValueType.Bool, Raw = 0 });
            g.Nodes.Add(new ActionNode { Id = act, Kind = "display_message", Text = "x" });
            g.ExecEdges.Add(new ExecEdge(0, TriggerGraph.TriggerExecOutPort, branch, TriggerGraph.ActionExecInPort));
            g.ExecEdges.Add(new ExecEdge(branch, TriggerGraph.BranchThenOutPort, act, TriggerGraph.ActionExecInPort));
            g.DataEdges.Add(new DataEdge(e1, TriggerGraph.ExprDataOutPort, branch, TriggerGraph.BranchCondInPort, DataWireType.Boolean));
            g.DataEdges.Add(new DataEdge(e2, TriggerGraph.ExprDataOutPort, branch, TriggerGraph.BranchCondInPort, DataWireType.Boolean));
            m.TriggerGraphJson = g.ToCanonicalJson();
            AssertRejectedAtBothGates(m, "forked");
        }

        [Fact]
        public void ForkedIndexInEdges_AreRejectedAtBothGates()
        {
            // TWO Int expression edges into ONE array_set index-in port (data port 2) — same fork class.
            ScenarioData m = BaseModel();
            m.Variables = new[] { IntArray("arr") };
            TriggerGraph g = SingleTrigger(out int id);
            int act = id++, val = id++, i1 = id++, i2 = id++;
            g.Nodes.Add(new ActionNode { Id = act, Kind = "array_set", Variable = "arr" });
            g.Nodes.Add(new ExprLiteralNode { Id = val, ValueType = DslValueType.Int, Raw = 7 });
            g.Nodes.Add(new ExprLiteralNode { Id = i1, ValueType = DslValueType.Int, Raw = 0 });
            g.Nodes.Add(new ExprLiteralNode { Id = i2, ValueType = DslValueType.Int, Raw = 1 });
            g.ExecEdges.Add(new ExecEdge(0, TriggerGraph.TriggerExecOutPort, act, TriggerGraph.ActionExecInPort));
            g.DataEdges.Add(new DataEdge(val, TriggerGraph.ExprDataOutPort, act, TriggerGraph.ActionValueInPort, DataWireType.Int));
            g.DataEdges.Add(new DataEdge(i1, TriggerGraph.ExprDataOutPort, act, TriggerGraph.ActionIndexInPort, DataWireType.Int));
            g.DataEdges.Add(new DataEdge(i2, TriggerGraph.ExprDataOutPort, act, TriggerGraph.ActionIndexInPort, DataWireType.Int));
            m.TriggerGraphJson = g.ToCanonicalJson();
            AssertRejectedAtBothGates(m, "forked");
        }

        // ── Duplicate declarations, loop constructs present (review P7; arming is unconditional since 7.7) ──

        [Fact]
        public void DuplicateArrayDeclaration_WithLoopConstructs_IsRejectedAtBothGates()
        {
            // DW-817 — the rationale, corrected. At 7.6 the backstop armed the duplicate-name reject only when
            // expressions existed (anyExpr), and this case (a duplicate-declared array + a loop, NO expressions)
            // was the hole: loop_var/array typing gated against the FIRST declaration while runtime Resolve may
            // bind another. The 7.7 gate/backstop reconciliation removed that arming condition entirely —
            // LoadScenario now passes requireUnique: true UNCONDITIONALLY, so EVERY load rejects a duplicate name,
            // loop constructs or not (see BuildDeclMap's own doc, and BuildDeclMapUniquenessTests, which covers the
            // loop-FREE half and fails if that call site ever becomes conditional again). This row is therefore the
            // loop-carrying instance of a rule that no longer keys off loops at all — kept because it is the exact
            // shape the 7.6 hole had.
            ScenarioData m = BaseModel();
            m.Variables = new[] { IntArray("arr", 8), IntArray("arr", 4), LocalInt("v") };
            TriggerGraph g = SingleTrigger(out int id);
            g.Nodes.Add(new ForEachNode { Id = id, Source = "array", ArrayName = "arr", LoopVar = "v" });
            g.ExecEdges.Add(new ExecEdge(0, TriggerGraph.TriggerExecOutPort, id, TriggerGraph.ActionExecInPort));
            m.TriggerGraphJson = g.ToCanonicalJson();

            ValidationResult r = Validate(m);
            Assert.False(r.Ok, "the validator gate should reject the duplicate declaration");
            Assert.Contains("duplicate", r.Error!);

            // Backstop (the messages differ textually — the validator's uniqueness pass predates the shared
            // gate — but both reject fail-closed, located at the duplicated name).
            var director = new ScenarioDirector(new BuildingStore(), new ResourceStore(Fixed.Zero), new DslVarTable(), new DslLoopState());
            var ex = Assert.Throws<System.Text.Json.JsonException>(() => director.LoadScenario(m));
            Assert.Contains("'arr'", ex.Message);
            Assert.Contains("declared more than once", ex.Message);
        }

        // ── Array-source up_to messaging (review P9): 0 = full array is LEGAL, the message must say so ──

        [Fact]
        public void ArrayLoopNegativeUpTo_IsRejected_WithoutExcludingTheLegalZero()
        {
            ScenarioData m = BaseModel();
            m.Variables = new[] { IntArray("arr"), LocalInt("v") };
            TriggerGraph g = SingleTrigger(out int id);
            g.Nodes.Add(new ForEachNode { Id = id, Source = "array", ArrayName = "arr", LoopVar = "v", UpTo = -1 });
            g.ExecEdges.Add(new ExecEdge(0, TriggerGraph.TriggerExecOutPort, id, TriggerGraph.ActionExecInPort));
            m.TriggerGraphJson = g.ToCanonicalJson();
            AssertRejectedAtBothGates(m, "0 (the full array) or 1..DslBounds.MaxForEachItems");
        }

        // ── Spawn cap (I/O matrix row) — both gates, both sides of the range (review P5) ────────

        [Theory]
        [InlineData(70)]   // beyond EffectCaps.MaxSpawnCount
        [InlineData(0)]    // low side: a spawn that would silently do nothing
        [InlineData(-3)]
        public void FlatSpawnCountOutOfRange_IsRejectedAtBothGates(int count)
        {
            ScenarioData m = BaseModel();
            m.Triggers = new[]
            {
                new TriggerDefinition
                {
                    Name = "spawn",
                    Events = new[] { new TriggerEvent { Type = "match_start" } },
                    Actions = new[] { new TriggerAction { Type = "spawn_unit", UnitId = "soldier", Count = count } },
                },
            };
            AssertRejectedAtBothGates(m, "MaxSpawnCount");
        }

        [Theory]
        [InlineData(70)]
        [InlineData(0)]
        [InlineData(-3)]
        public void GraphSpawnCountOutOfRange_IsRejectedAtBothGates(int count)
        {
            ScenarioData m = BaseModel();
            TriggerGraph g = SingleTrigger(out int id);
            g.Nodes.Add(new ActionNode { Id = id, Kind = "spawn_unit", UnitId = "soldier", Count = count });
            g.ExecEdges.Add(new ExecEdge(0, TriggerGraph.TriggerExecOutPort, id, TriggerGraph.ActionExecInPort));
            m.TriggerGraphJson = g.ToCanonicalJson();
            AssertRejectedAtBothGates(m, "MaxSpawnCount");
        }

        // ── A VALID loop scenario passes the gate whole ─────────────────────────

        [Fact]
        public void WellFormedLoopScenario_PassesTheGate()
        {
            ScenarioData m = BaseModel();
            m.Variables = new[]
            {
                IntArray("arr"),
                LocalInt("v"),
                new ScenarioVariable { Name = "sum", Type = DslValueType.Int, Scope = VarScope.Global },
            };
            var decls = new System.Collections.Generic.Dictionary<string, (DslValueType Type, VarScope Scope)>(System.StringComparer.Ordinal)
            {
                ["arr"] = (DslValueType.Array, VarScope.Global),
                ["v"]   = (DslValueType.Int, VarScope.TriggerLocal),
                ["sum"] = (DslValueType.Int, VarScope.Global),
            };
            var arrayDecls = new System.Collections.Generic.Dictionary<string, (DslValueType Elem, int Capacity)>(System.StringComparer.Ordinal)
            {
                ["arr"] = (DslValueType.Int, 8),
            };

            TriggerGraph g = SingleTrigger(out int id);
            int push = id++, loop = id++, body = id++;
            g.Nodes.Add(new ActionNode { Id = push, Kind = "array_push", Variable = "arr" });
            g.Nodes.Add(new ForEachNode { Id = loop, Source = "array", ArrayName = "arr", LoopVar = "v", UpTo = 8 });
            g.Nodes.Add(new ActionNode { Id = body, Kind = "set_variable", Variable = "sum" });
            g.ExecEdges.Add(new ExecEdge(0, TriggerGraph.TriggerExecOutPort, push, TriggerGraph.ActionExecInPort));
            g.ExecEdges.Add(new ExecEdge(push, TriggerGraph.ActionExecOutPort, loop, TriggerGraph.ActionExecInPort));
            g.ExecEdges.Add(new ExecEdge(loop, TriggerGraph.ForEachBodyOutPort, body, TriggerGraph.ActionExecInPort));
            (int pushRoot, _) = ExprParser.Parse("7", g, decls, arrayDecls);
            g.DataEdges.Add(new DataEdge(pushRoot, TriggerGraph.ExprDataOutPort, push, TriggerGraph.ActionValueInPort, DataWireType.Int));
            (int sumRoot, _) = ExprParser.Parse("sum + v", g, decls, arrayDecls);
            g.DataEdges.Add(new DataEdge(sumRoot, TriggerGraph.ExprDataOutPort, body, TriggerGraph.ActionValueInPort, DataWireType.Int));
            m.TriggerGraphJson = g.ToCanonicalJson();

            ValidationResult r = Validate(m);
            Assert.True(r.Ok, r.Error);
        }
    }
}
