#nullable enable
using System.Text.Json;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Dsl;
using Xunit;

namespace ProjectChimera.Sim.Tests.Validation
{
    /// <summary>
    /// Story 7.8 — proves the shared <see cref="CustomUiGate"/> is actually WIRED into BOTH load gates: the
    /// authoritative pre-tick <see cref="ScenarioValidator"/> (fails a <see cref="ValidationResult"/>) and the
    /// fail-closed <c>ScenarioDirector.LoadScenario</c> backstop (throws a located <c>JsonException</c>). The gate
    /// rules themselves live in <c>CustomUiGateTests</c>; these tests prove ADOPTION (the located error surfaces at
    /// each site). Rejecting fixture: a Counter bound to an undeclared variable (unresolved bind).
    /// </summary>
    public class ScenarioValidatorCustomUiTests
    {
        /// <summary>A minimal otherwise-VALID model: one declared slot inside MapBounds, no custom UI.</summary>
        private static ScenarioData ValidModel() => new ScenarioData
        {
            MapBounds = 120f,
            WinCondition = WinCondition.DestroyAllBuildings,
            PlayerSlots = new[]
            {
                new ScenarioPlayerSlot { Slot = 0, FactionJson = "res://a.json", StartOre = 200f, BaseX = -45f, BaseZ = 0f },
            },
        };

        /// <summary>A gate-rejecting custom-UI tree: a Counter bound to an undeclared variable.</summary>
        private static CustomUiTree RejectingTree() => new()
        {
            Widgets = new WidgetBase[] { new CounterWidget { Id = 1, Bind = "ghost" } },
        };

        [Fact]
        public void Validator_Passes_WhenNoCustomUi()
        {
            ValidationResult r = new ScenarioValidator().Validate(ValidModel());
            Assert.True(r.Ok, r.Error);
        }

        [Fact]
        public void Validator_Rejects_GateRejectingCustomUi_Located()
        {
            var m = ValidModel();
            m.CustomUi = RejectingTree();
            ValidationResult r = new ScenarioValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("scenario.custom_ui.widgets[0].bind", r.Error!);
            Assert.Contains("ghost", r.Error!);
        }

        [Fact]
        public void Director_LoadScenario_Throws_OnGateRejectingCustomUi_Located()
        {
            var director = new ScenarioDirector(new BuildingStore(), new ResourceStore(Fixed.Zero), new DslVarTable());
            var scenario = new ScenarioData { CustomUi = RejectingTree() };
            var ex = Assert.Throws<JsonException>(() => director.LoadScenario(scenario));
            Assert.Contains("scenario.custom_ui.widgets[0].bind", ex.Message);
            Assert.Contains("ghost", ex.Message);
        }
    }
}
