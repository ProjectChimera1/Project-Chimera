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

        /// <summary>A gate-rejecting custom-UI tree: a Counter bound to an undeclared variable. Geometry is valid
        /// (DW-364 rejects non-positive W/H BEFORE binds are checked) so the reject stays a BIND reject.</summary>
        private static CustomUiTree RejectingTree() => new()
        {
            Widgets = new WidgetBase[] { new CounterWidget { Id = 1, Bind = "ghost", W = 160, H = 40 } },
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

        // ── Story 7.9 — Button registry threading at BOTH sites (review pass 2) ────────────────────────────────
        // CustomUiGate.Check's customEvents parameter is OPTIONAL (null fail-closes every event button), so a
        // reverted/forgotten registry pass at either site compiles clean and silently bricks all event-button
        // content. These prove the registry actually arrives: a valid event button is ACCEPTED at each site, and
        // an undeclared event still rejects located at the validator site.

        /// <summary>One declared 0-param event + a Button raising it — valid iff the registry reaches the gate.</summary>
        private static void AddEventButton(ScenarioData m)
        {
            m.CustomEvents = new[] { new ScenarioCustomEvent { Name = "buy", AllowedRaisers = new[] { 0 } } };
            m.CustomUi = new CustomUiTree
            {
                Widgets = new WidgetBase[] { new ButtonWidget { Id = 1, W = 160, H = 48, Text = "Buy", EventName = "buy" } },
            };
        }

        [Fact]
        public void Validator_Accepts_ValidEventButton_RegistryThreaded()
        {
            var m = ValidModel();
            AddEventButton(m);
            ValidationResult r = new ScenarioValidator().Validate(m);
            Assert.True(r.Ok, r.Error); // fails with "'buy' is not a declared custom event" if the registry pass regresses
        }

        [Fact]
        public void Director_LoadScenario_Accepts_ValidEventButton_RegistryThreaded()
        {
            var director = new ScenarioDirector(new BuildingStore(), new ResourceStore(Fixed.Zero), new DslVarTable());
            var scenario = new ScenarioData();
            AddEventButton(scenario);
            director.LoadScenario(scenario); // throws located if the backstop's registry pass regresses
        }

        [Fact]
        public void Validator_Rejects_UndeclaredEventButton_Located()
        {
            var m = ValidModel();
            AddEventButton(m);
            ((ButtonWidget)m.CustomUi!.Widgets![0]).EventName = "ghost_event";
            ValidationResult r = new ScenarioValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("scenario.custom_ui.widgets[0].event", r.Error!);
            Assert.Contains("ghost_event", r.Error!);
        }
    }
}
