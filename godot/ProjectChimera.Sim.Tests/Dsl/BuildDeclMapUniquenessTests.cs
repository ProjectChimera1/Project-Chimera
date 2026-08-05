#nullable enable
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Dsl;
using Xunit;

namespace ProjectChimera.Sim.Tests.Dsl
{
    /// <summary>
    /// DW-587 — pins the ARMING POSTURE of <c>ScenarioDirector.BuildDeclMap</c>'s <c>requireUnique</c> flag, the
    /// behaviour whose doc comment had gone stale.
    ///
    /// The 7.6 prose said duplicate declarations reject only when armed by the 7.4 <c>anyExpr</c> rule OR by the
    /// presence of loop constructs. The 7.7 gate/backstop reconciliation removed the <c>HasLoopConstructs</c>
    /// guard, so <c>LoadScenario</c>'s own call now passes <c>requireUnique: true</c> UNCONDITIONALLY and the
    /// <c>CompileExpressionPrograms</c> backstop's <c>anyExpr</c> call can only ever re-check what that
    /// unconditional call already rejected. No call site keys off loop constructs any more.
    ///
    /// Existing coverage only pinned the two ARMED cases — <c>DuplicateVariableDeclarations_WithExpressions_
    /// FailClosed_AtLoadScenario</c> (expressions present) and <c>DuplicateArrayDeclaration_WithLoopConstructs_
    /// IsRejectedAtBothGates</c> (a loop present) — i.e. exactly the two conditions the stale prose named. The
    /// gap these tests close is the case the stale prose implied was ADMITTED: a loop-free, expression-free,
    /// custom-event-free scenario carrying a duplicate declaration. That load rejects too, so the real behaviour
    /// is strictly stricter than the old comment claimed.
    ///
    /// This matters beyond the prose: DW-359's shadowing argument (a <c>loop_var</c> can never silently shadow a
    /// declared Global/PerPlayer, because declaration names are unique at BOTH gates) is only sound while this
    /// call site stays unconditional. Re-arming it conditionally must fail here, not pass silently.
    /// </summary>
    public class BuildDeclMapUniquenessTests
    {
        /// <summary>The bare (non-host) director the ledger names as the direct-LoadScenario caller — the path the
        /// backstop exists for. Modifier-free trigger content, so the DW-340 ModifierStore gate never arms.</summary>
        private static ScenarioDirector NewBareDirector() => new ScenarioDirector(
            new BuildingStore(), new ResourceStore(Fixed.Zero), new DslVarTable(), new DslLoopState());

        /// <summary>One name declared twice across scopes — the collision DslVarTable.Resolve would bind
        /// PerPlayer-first while a first-wins declMap types against the Global entry.</summary>
        private static ScenarioVariable[] DuplicateScalars() => new[]
        {
            new ScenarioVariable { Name = "gold", Type = DslValueType.Int, Scope = VarScope.Global },
            new ScenarioVariable { Name = "gold", Type = DslValueType.Int, Scope = VarScope.PerPlayer },
        };

        /// <summary>The same shape with the collision removed — the control, so a reject above can never be
        /// credited to the loop-free/expression-free posture itself.</summary>
        private static ScenarioVariable[] UniqueScalars() => new[]
        {
            new ScenarioVariable { Name = "gold",  Type = DslValueType.Int, Scope = VarScope.Global },
            new ScenarioVariable { Name = "score", Type = DslValueType.Int, Scope = VarScope.PerPlayer },
        };

        /// <summary>One array name declared twice at different capacities — the 7.6 review's motivating case,
        /// here WITHOUT the for_each that used to be required to arm the reject.</summary>
        private static ScenarioVariable[] DuplicateArrays() => new[]
        {
            new ScenarioVariable
            {
                Name = "arr", Type = DslValueType.Array, ElementType = DslValueType.Int,
                Capacity = 8, Scope = VarScope.Global,
            },
            new ScenarioVariable
            {
                Name = "arr", Type = DslValueType.Array, ElementType = DslValueType.Int,
                Capacity = 4, Scope = VarScope.Global,
            },
        };

        /// <summary>A LEGACY flat trigger (Story 7.3 parity shape): lowers to event/trigger/action nodes only —
        /// no expression node, no custom event, no for_each/for_each_batched/branch. So <c>anyExpr</c> is false
        /// and there is nothing a loop-construct scan could latch onto; only the unconditional call can reject.</summary>
        private static TriggerDefinition[] LegacyFlatTriggers() => new[]
        {
            new TriggerDefinition
            {
                Name    = "writer",
                Events  = new[] { new TriggerEvent { Type = "match_start", Faction = 0 } },
                Actions = new[] { new TriggerAction { Type = "set_variable", Variable = "gold", Value = 5 } },
            },
        };

        [Fact]
        public void DuplicateScalarDeclaration_WithNoTriggersAtAll_RejectsAtLoadScenario()
        {
            // The purest arming probe: an EMPTY graph cannot carry an expression or a loop construct, so under the
            // pre-7.7 conditional arming this load succeeded and silently kept a first-wins declMap.
            var scenario = new ScenarioData { Variables = DuplicateScalars() };

            var ex = Assert.Throws<System.Text.Json.JsonException>(
                () => NewBareDirector().LoadScenario(scenario));
            Assert.Contains("declared more than once", ex.Message);
            Assert.Contains("'gold'", ex.Message); // located at the offending name, never a bare "invalid scenario"
        }

        [Fact]
        public void DuplicateScalarDeclaration_InALoopFreeExpressionFreeLegacyScenario_RejectsAtLoadScenario()
        {
            // Real legacy content this time (flat triggers that lower to a non-empty graph), still expression-free
            // and loop-free: the exact case the stale comment implied was admitted.
            var scenario = new ScenarioData
            {
                Variables = DuplicateScalars(),
                Triggers  = LegacyFlatTriggers(),
            };

            var ex = Assert.Throws<System.Text.Json.JsonException>(
                () => NewBareDirector().LoadScenario(scenario));
            Assert.Contains("declared more than once", ex.Message);
            Assert.Contains("'gold'", ex.Message);
        }

        [Fact]
        public void DuplicateArrayDeclaration_WithoutAnyLoopConstruct_RejectsAtLoadScenario()
        {
            // The array half. DslLoopGate.CheckDeclarations validates array SHAPE but never uniqueness, and it runs
            // AFTER BuildDeclMap — so if the arming ever became loop-conditional again, two same-named arrays at
            // different capacities would load clean and the compiler would type against capacity 8 while a later
            // Resolve could bind the 4-slot declaration.
            var scenario = new ScenarioData { Variables = DuplicateArrays() };

            var ex = Assert.Throws<System.Text.Json.JsonException>(
                () => NewBareDirector().LoadScenario(scenario));
            Assert.Contains("declared more than once", ex.Message);
            Assert.Contains("'arr'", ex.Message);
        }

        [Fact]
        public void UniqueDeclarations_InTheSameLoopFreeLegacyScenario_LoadCleanly()
        {
            // Control: identical posture, collision removed. Proves the three rejects above are caused by the
            // DUPLICATE name and not by the bare director, the empty/legacy graph, or the array declarations.
            var scalars = new ScenarioData { Variables = UniqueScalars(), Triggers = LegacyFlatTriggers() };
            NewBareDirector().LoadScenario(scalars);

            var arrays = new ScenarioData
            {
                Variables = new[]
                {
                    new ScenarioVariable
                    {
                        Name = "arr", Type = DslValueType.Array, ElementType = DslValueType.Int,
                        Capacity = 8, Scope = VarScope.Global,
                    },
                    new ScenarioVariable
                    {
                        Name = "other", Type = DslValueType.Array, ElementType = DslValueType.Int,
                        Capacity = 4, Scope = VarScope.Global,
                    },
                },
            };
            NewBareDirector().LoadScenario(arrays);
        }
    }
}
