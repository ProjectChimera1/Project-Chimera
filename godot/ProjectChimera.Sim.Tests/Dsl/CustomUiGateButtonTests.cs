#nullable enable
using System.Collections.Generic;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Dsl;
using Xunit;

namespace ProjectChimera.Sim.Tests.Dsl
{
    /// <summary>
    /// Story 7.9 — the write-rail <see cref="CustomUiGate"/> extension for <see cref="ButtonWidget"/>: a raise
    /// target must be a DECLARED custom event with ≤ <see cref="EventBounds.MaxButtonEventParams"/> params, exactly
    /// one authored arg per declared param, each arg's type matching the declared param type; a button must do
    /// something (event or local action); a local action must name a valid target. Every reject is LOCATED. Mirrors
    /// <c>CustomUiGateTests</c> (the read-rail suite).
    /// </summary>
    public class CustomUiGateButtonTests
    {
        private static readonly Dictionary<string, (DslValueType, VarScope)> NoVars = new();
        private static readonly Dictionary<string, (DslValueType, int)> NoArrays = new();

        private static CustomUiTree Tree(params WidgetBase[] widgets) => new() { Widgets = widgets };

        private static ScenarioCustomEvent Ev(string name, int[]? raisers = null, params (string, DslValueType)[] ps) =>
            new()
            {
                Name = name,
                Params = ps.Length == 0 ? null : System.Array.ConvertAll(ps, p => new ScenarioEventParam { Name = p.Item1, Type = p.Item2 }),
                AllowedRaisers = raisers,
            };

        private static ScenarioCustomEvent[] Events(params ScenarioCustomEvent[] es) => es;

        // ── Happy paths ─────────────────────────────────────────────────────────

        [Fact]
        public void ValidEventButton_Passes()
        {
            var tree = Tree(new ButtonWidget
            {
                Id = 1, EventName = "buy", ArgRaws = new[] { 3 }, ArgTypes = new[] { DslValueType.Int },
            });
            Assert.Null(CustomUiGate.Check(tree, NoVars, NoArrays, Events(Ev("buy", null, ("amount", DslValueType.Int)))));
        }

        [Fact]
        public void ValidParamlessEventButton_Passes()
        {
            var tree = Tree(new ButtonWidget { Id = 1, EventName = "vote" });
            Assert.Null(CustomUiGate.Check(tree, NoVars, NoArrays, Events(Ev("vote"))));
        }

        [Fact]
        public void ValidLocalActionButton_Passes()
        {
            var tree = Tree(new ButtonWidget { Id = 1, LocalAction = LocalUiAction.CloseSelf });
            Assert.Null(CustomUiGate.Check(tree, NoVars, NoArrays, Events()));
        }

        [Fact]
        public void ToggleTargetingExistingWidget_Passes()
        {
            var tree = Tree(
                new PanelWidget { Id = 2 },
                new ButtonWidget { Id = 1, LocalAction = LocalUiAction.ToggleWidgetVisible, LocalTargetWidgetId = 2 });
            Assert.Null(CustomUiGate.Check(tree, NoVars, NoArrays, Events()));
        }

        // ── I/O-matrix reject rows ──────────────────────────────────────────────

        [Fact]
        public void UndeclaredEvent_Rejects_Located()
        {
            var tree = Tree(new ButtonWidget { Id = 1, EventName = "ghost" });
            string? err = CustomUiGate.Check(tree, NoVars, NoArrays, Events(Ev("buy")));
            Assert.NotNull(err);
            Assert.Contains("scenario.custom_ui.widgets[0].event", err);
            Assert.Contains("ghost", err!);
        }

        [Fact]
        public void OverArgEvent_Rejects_NamingTheCap()
        {
            // The event declares 3 params (> MaxButtonEventParams = 2).
            var tree = Tree(new ButtonWidget
            {
                Id = 1, EventName = "big",
                ArgRaws = new[] { 1, 2, 3 },
                ArgTypes = new[] { DslValueType.Int, DslValueType.Int, DslValueType.Int },
            });
            string? err = CustomUiGate.Check(tree, NoVars, NoArrays, Events(
                Ev("big", null, ("a", DslValueType.Int), ("b", DslValueType.Int), ("c", DslValueType.Int))));
            Assert.NotNull(err);
            Assert.Contains("MaxButtonEventParams", err!);
        }

        [Fact]
        public void ArgTypeMismatch_Rejects_Located()
        {
            var tree = Tree(new ButtonWidget
            {
                Id = 1, EventName = "buy", ArgRaws = new[] { 1 }, ArgTypes = new[] { DslValueType.Bool },
            });
            string? err = CustomUiGate.Check(tree, NoVars, NoArrays, Events(Ev("buy", null, ("amount", DslValueType.Int))));
            Assert.NotNull(err);
            Assert.Contains("scenario.custom_ui.widgets[0].args[0]", err);
        }

        [Fact]
        public void WrongArgCount_Rejects()
        {
            var tree = Tree(new ButtonWidget { Id = 1, EventName = "buy" }); // 0 args, event declares 1
            string? err = CustomUiGate.Check(tree, NoVars, NoArrays, Events(Ev("buy", null, ("amount", DslValueType.Int))));
            Assert.NotNull(err);
            Assert.Contains("args", err!);
        }

        [Fact]
        public void NeitherEventNorLocalAction_Rejects()
        {
            var tree = Tree(new ButtonWidget { Id = 1 }); // no event, LocalAction defaults to None
            string? err = CustomUiGate.Check(tree, NoVars, NoArrays, Events());
            Assert.NotNull(err);
            Assert.Contains("no event or local action", err!);
        }

        [Fact]
        public void ToggleTargetingMissingWidget_Rejects_Located()
        {
            var tree = Tree(new ButtonWidget { Id = 1, LocalAction = LocalUiAction.ToggleWidgetVisible, LocalTargetWidgetId = 99 });
            string? err = CustomUiGate.Check(tree, NoVars, NoArrays, Events());
            Assert.NotNull(err);
            Assert.Contains("local_target", err!);
        }

        [Fact]
        public void SetLocalUiVar_WithoutName_Rejects()
        {
            var tree = Tree(new ButtonWidget { Id = 1, LocalAction = LocalUiAction.SetLocalUiVar });
            string? err = CustomUiGate.Check(tree, NoVars, NoArrays, Events());
            Assert.NotNull(err);
            Assert.Contains("local_var", err!);
        }

        [Fact]
        public void NullCustomEvents_TreatsEveryEventButtonAsUndeclared()
        {
            // With no registry passed, a raise target cannot resolve — the button rejects (fail-closed).
            var tree = Tree(new ButtonWidget { Id = 1, EventName = "buy" });
            string? err = CustomUiGate.Check(tree, NoVars, NoArrays, customEvents: null);
            Assert.NotNull(err);
            Assert.Contains("buy", err!);
        }
    }
}
