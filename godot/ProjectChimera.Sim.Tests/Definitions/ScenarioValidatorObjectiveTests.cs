#nullable enable
using System.Text.Json;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Dsl;
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// Story 7.14 (review) — the ScenarioValidator objective-DECLARATION gate and its ScenarioDirector.LoadScenario
    /// backstop parity. The four original objective test files exercise ObjectiveResolver/Serializer/LoadScenario but
    /// never ScenarioValidator.Validate, so the authoring/Save gate (consumed by MapWriteGate on F5/Save) was untested:
    /// a regression in these rejects could ship a malformed objective set into the runtime silently. These pin every
    /// reject through the SHARED ObjectiveResolver.CheckDeclarations rulebook — via the validator AND, for gate/backstop
    /// parity, via the LoadScenario backstop that a direct caller bypassing the validator must fail closed against.
    /// </summary>
    public class ScenarioValidatorObjectiveTests
    {
        private static ScenarioData Base() => ScenarioData.CreateBlank("Map", "Alec", "desc", 2, MapSize.Medium);

        private static ScenarioObjective Obj(string id, string title = "T", ObjectiveState st = ObjectiveState.Active)
            => new() { Id = id, Title = title, InitialState = st };

        // ── ScenarioValidator gate ───────────────────────────────────────────────

        [Fact]
        public void WellFormedObjectives_Pass()
        {
            var m = Base();
            m.Objectives = new[] { Obj("a"), Obj("b", "Second") };
            ValidationResult r = new ScenarioValidator().Validate(m);
            Assert.True(r.Ok, r.Error);
        }

        [Fact]
        public void DuplicateObjectiveId_Fails()
        {
            var m = Base();
            m.Objectives = new[] { Obj("a"), Obj("a") };
            ValidationResult r = new ScenarioValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("duplicate", r.Error);
        }

        [Fact]
        public void BlankObjectiveTitle_Fails()
        {
            var m = Base();
            m.Objectives = new[] { Obj("a", "   ") };
            ValidationResult r = new ScenarioValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("title", r.Error);
        }

        [Fact]
        public void EmptyObjectiveId_Fails()
        {
            var m = Base();
            m.Objectives = new[] { Obj("") };
            ValidationResult r = new ScenarioValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("id", r.Error);
        }

        [Fact]
        public void ReservedNamespaceObjectiveId_Fails()
        {
            var m = Base();
            m.Objectives = new[] { Obj(ObjectiveResolver.ReservedVarPrefix + "x") };
            ValidationResult r = new ScenarioValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("reserved", r.Error);
        }

        [Fact]
        public void NullObjectiveElement_Fails()
        {
            var m = Base();
            m.Objectives = new ScenarioObjective[] { null! };
            ValidationResult r = new ScenarioValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("null", r.Error);
        }

        [Fact]
        public void AuthoredVariableInReservedNamespace_Fails()
        {
            var m = Base();
            m.Variables = new[]
            {
                new ScenarioVariable { Name = ObjectiveResolver.ReservedVarPrefix + "sneaky", Type = DslValueType.Int },
            };
            ValidationResult r = new ScenarioValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("reserved", r.Error);
        }

        // ── LoadScenario backstop parity (direct caller bypassing the validator must fail closed identically) ──

        [Fact]
        public void Backstop_RejectsDuplicateObjectiveId()
        {
            var scenario = new ScenarioData { Objectives = new[] { Obj("a"), Obj("a") } };
            var director = new ScenarioDirector(new BuildingStore(), new ResourceStore(Fixed.Zero), new DslVarTable());
            var ex = Assert.Throws<JsonException>(() => director.LoadScenario(scenario));
            Assert.Contains("duplicate", ex.Message);
        }

        [Fact]
        public void Backstop_RejectsNullObjectiveElement_InsteadOfNre()
        {
            var scenario = new ScenarioData { Objectives = new ScenarioObjective[] { null! } };
            var director = new ScenarioDirector(new BuildingStore(), new ResourceStore(Fixed.Zero), new DslVarTable());
            var ex = Assert.Throws<JsonException>(() => director.LoadScenario(scenario));
            Assert.Contains("null", ex.Message);
        }

        [Fact]
        public void Backstop_RejectsAuthoredVariableInReservedNamespace()
        {
            var scenario = new ScenarioData
            {
                Variables = new[]
                {
                    new ScenarioVariable { Name = ObjectiveResolver.ReservedVarPrefix + "sneaky", Type = DslValueType.Int },
                },
            };
            var director = new ScenarioDirector(new BuildingStore(), new ResourceStore(Fixed.Zero), new DslVarTable());
            var ex = Assert.Throws<JsonException>(() => director.LoadScenario(scenario));
            Assert.Contains("reserved", ex.Message);
        }

        // ── ScenarioValidator gate: objective-ACTION target cross-reference ───────────────────────────────
        // (Verification-gap review) The declaration tests above never carry a trigger graph, and the objective-target
        // reject tests in ObjectiveStateTests go through LoadScenario (the backstop), so the validator GATE's own
        // objective-target cross-reference (ScenarioValidator.cs — mutableObjectiveIds + the explicit Show/Complete/
        // Fail reject loop + the objectiveExists lambda handed to DslLoopGate) had zero coverage: a regression there
        // (falsely rejecting a valid authored target at Save, or falsely accepting a dangling one) would ship green.
        // These drive Validate with an objective-action graph to pin both directions.

        /// <summary>match_start → complete_objective(targetId), the minimal objective-action trigger graph.</summary>
        private static string CompleteObjectiveGraphJson(string targetId)
        {
            var g = new TriggerGraph();
            g.Nodes.Add(new TriggerNode { Id = 0, Name = "t" });
            g.Nodes.Add(new EventNode { Id = 1, Kind = "match_start" });
            g.Nodes.Add(new CompleteObjectiveNode { Id = 2, ObjectiveId = targetId });
            g.ExecEdges.Add(new ExecEdge(1, TriggerGraph.EventExecOutPort, 0, TriggerGraph.TriggerEventInPort));
            g.ExecEdges.Add(new ExecEdge(0, TriggerGraph.TriggerExecOutPort, 2, TriggerGraph.ActionExecInPort));
            return g.ToCanonicalJson();
        }

        [Fact]
        public void Gate_ObjectiveActionTargetingAuthoredObjective_Passes()
        {
            var m = Base();
            m.Objectives = new[] { Obj("kill_boss") };
            m.TriggerGraphJson = CompleteObjectiveGraphJson("kill_boss");
            ValidationResult r = new ScenarioValidator().Validate(m);
            Assert.True(r.Ok, r.Error);
        }

        [Fact]
        public void Gate_ObjectiveActionTargetingUnknownObjective_Fails()
        {
            var m = Base();
            m.Objectives = new[] { Obj("kill_boss") };
            m.TriggerGraphJson = CompleteObjectiveGraphJson("does_not_exist");
            ValidationResult r = new ScenarioValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("no declared objective", r.Error);
        }

        [Fact]
        public void Gate_ObjectiveActionTargetingPresentationOnlyDefault_Fails()
        {
            // No authored objectives → the resolver synthesizes the presentation-only default id ("victory") with NO
            // reserved var, so it is NOT a mutable target: an action against it must be a located Save reject (not a
            // silent runtime no-op), exactly like the LoadScenario backstop.
            var m = Base();
            m.TriggerGraphJson = CompleteObjectiveGraphJson(ObjectiveResolver.DefaultObjectiveId);
            ValidationResult r = new ScenarioValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("no declared objective", r.Error);
        }
    }
}
