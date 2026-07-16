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
            m.Triggers[0].Events[0].Faction = 5; // ScenarioDirector would do (Faction)6 → Ore[6], an OOB crash
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
            // inside LoadScenario — the exact partial-apply crash the gate exists to close. The gate now runs
            // BuildExecutionOrder (7.2's fail-closed cycle guard) so the cycle is a located validation error.
            var g = new TriggerGraph();
            g.Nodes.Add(new TriggerNode { Id = 0, Name = "cyc" });
            g.Nodes.Add(new EventNode { Id = 1, Kind = "match_start" });
            g.Nodes.Add(new ActionNode { Id = 2, Kind = "display_message", Text = "a" });
            g.Nodes.Add(new ActionNode { Id = 3, Kind = "display_message", Text = "b" });
            g.ExecEdges.Add(new ExecEdge(1, 0, 0, 0)); // event → trigger
            g.ExecEdges.Add(new ExecEdge(0, 0, 2, 0)); // trigger → a
            g.ExecEdges.Add(new ExecEdge(2, 0, 3, 0)); // a → b
            g.ExecEdges.Add(new ExecEdge(3, 0, 2, 0)); // b → a (cycle)

            var m = ValidModelWithTrigger();
            m.TriggerGraphJson = g.ToCanonicalJson();
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("trigger_graph", r.Error!);
            Assert.Contains("cycle", r.Error!);
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

        // AC3c — AR-13 (a random effect is valid only if it draws from SimRng) stays RESERVED: no random
        // trigger-effect TYPE exists pre-Epic-2, so there is nothing to validate yet. This is the documented
        // pending case; the mature rule is enforced by Epic 2's effect-validator (Story 2.3) the first moment an
        // effect schema exists. Do NOT fabricate a random effect type here, and never invoke a real LLM in a test.
        [Fact(Skip = "AR-13 reserved until the Epic 2 effect schema (Story 2.3) — no random trigger-effect type exists pre-Epic-2.")]
        public void RandomEffect_MustDrawFromSimRng_ReservedUntilStory2_3() { }
    }
}
