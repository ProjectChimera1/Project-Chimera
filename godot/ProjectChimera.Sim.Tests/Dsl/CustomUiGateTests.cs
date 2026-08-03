#nullable enable
using System.Collections.Generic;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Dsl;
using Xunit;

namespace ProjectChimera.Sim.Tests.Dsl
{
    /// <summary>
    /// Story 7.8 — the shared <see cref="CustomUiGate"/> rulebook (the ONE implementation both
    /// <c>ScenarioValidator</c> and <c>ScenarioDirector.LoadScenario</c> invoke): caps rejected at load naming the
    /// constant, duplicate ids, bind resolve + type-match against the declared-variable registry. Every reject is a
    /// LOCATED error under <c>scenario.custom_ui.widgets[i]…</c>.
    /// </summary>
    public class CustomUiGateTests
    {
        private static Dictionary<string, (DslValueType Type, VarScope Scope)> Vars(
            params (string Name, DslValueType Type, VarScope Scope)[] vs)
        {
            var d = new Dictionary<string, (DslValueType, VarScope)>();
            foreach (var v in vs) d[v.Name] = (v.Type, v.Scope);
            return d;
        }

        private static Dictionary<string, (DslValueType Elem, int Capacity)> Arrays(
            params (string Name, DslValueType Elem, int Capacity)[] vs)
        {
            var d = new Dictionary<string, (DslValueType, int)>();
            foreach (var v in vs) d[v.Name] = (v.Elem, v.Capacity);
            return d;
        }

        private static readonly Dictionary<string, (DslValueType, int)> NoArrays = new();

        private static CustomUiTree Tree(params WidgetBase[] widgets) => new() { Widgets = widgets };

        [Fact]
        public void NullTree_Passes()
        {
            Assert.Null(CustomUiGate.Check(null, Vars(), NoArrays));
        }

        [Fact]
        public void ValidScalarAndArrayBinds_Pass()
        {
            var tree = Tree(
                new CounterWidget { Id = 1, Bind = "score", W = 160, H = 40 },
                new TimerWidget { Id = 2, Bind = "clock", VisibleBind = "show", W = 120, H = 40 },
                new LeaderboardWidget { Id = 3, Bind = "board", Rows = 8, W = 240, H = 200 });
            var vars = Vars(("score", DslValueType.Int, VarScope.Global),
                            ("clock", DslValueType.Fixed, VarScope.Global),
                            ("show", DslValueType.Bool, VarScope.Global),
                            ("board", DslValueType.Array, VarScope.Global));
            var arrays = Arrays(("board", DslValueType.Int, 16));
            Assert.Null(CustomUiGate.Check(tree, vars, arrays));
        }

        [Fact]
        public void UnresolvedBind_Rejects_Located()
        {
            var tree = Tree(new CounterWidget { Id = 1, Bind = "ghost", W = 160, H = 40 });
            string? err = CustomUiGate.Check(tree, Vars(), NoArrays);
            Assert.NotNull(err);
            Assert.Contains("scenario.custom_ui.widgets[0].bind", err);
            Assert.Contains("ghost", err!);
        }

        [Fact]
        public void TypeMismatchedScalarBind_Rejects()
        {
            // A Counter binds a Bool — a value bind must be Int/Fixed.
            var tree = Tree(new CounterWidget { Id = 1, Bind = "flag", W = 160, H = 40 });
            var vars = Vars(("flag", DslValueType.Bool, VarScope.Global));
            string? err = CustomUiGate.Check(tree, vars, NoArrays);
            Assert.NotNull(err);
            Assert.Contains("Bool", err!);
        }

        [Fact]
        public void RepeaterBoundToScalar_Rejects()
        {
            var tree = Tree(new LeaderboardWidget { Id = 1, Bind = "score", W = 240, H = 200 });
            var vars = Vars(("score", DslValueType.Int, VarScope.Global));
            string? err = CustomUiGate.Check(tree, vars, NoArrays);
            Assert.NotNull(err);
            Assert.Contains("Array", err!);
        }

        [Fact]
        public void TriggerLocalBind_Rejects()
        {
            var tree = Tree(new CounterWidget { Id = 1, Bind = "scratch", W = 160, H = 40 });
            var vars = Vars(("scratch", DslValueType.Int, VarScope.TriggerLocal));
            string? err = CustomUiGate.Check(tree, vars, NoArrays);
            Assert.NotNull(err);
            Assert.Contains("TriggerLocal", err!);
        }

        [Fact]
        public void DuplicateId_Rejects()
        {
            var tree = Tree(new CounterWidget { Id = 5, W = 160, H = 40 }, new TimerWidget { Id = 5, W = 120, H = 40 });
            string? err = CustomUiGate.Check(tree, Vars(), NoArrays);
            Assert.NotNull(err);
            Assert.Contains("duplicate", err!.ToLowerInvariant());
        }

        [Fact]
        public void OverWidgetCount_Rejects_NamingConst()
        {
            var widgets = new WidgetBase[DslBounds.MaxWidgetCount + 1];
            for (int i = 0; i < widgets.Length; i++) widgets[i] = new PanelWidget { Id = i, W = 10, H = 10 };
            string? err = CustomUiGate.Check(Tree(widgets), Vars(), NoArrays);
            Assert.NotNull(err);
            Assert.Contains("MaxWidgetCount", err!);
        }

        [Fact]
        public void OverDepth_Rejects_NamingConst()
        {
            // Build a chain deeper than MaxWidgetDepth.
            WidgetBase leaf = new PanelWidget { Id = 999, W = 10, H = 10 };
            WidgetBase current = leaf;
            for (int i = 0; i < DslBounds.MaxWidgetDepth + 1; i++)
                current = new PanelWidget { Id = i, W = 10, H = 10, Children = new[] { current } };
            string? err = CustomUiGate.Check(Tree(current), Vars(), NoArrays);
            Assert.NotNull(err);
            Assert.Contains("MaxWidgetDepth", err!);
        }

        [Fact]
        public void OverListRows_Rejects_NamingConst()
        {
            var tree = Tree(new LeaderboardWidget { Id = 1, Bind = "board", Rows = DslBounds.MaxListRows + 1, W = 240, H = 200 });
            var vars = Vars(("board", DslValueType.Array, VarScope.Global));
            var arrays = Arrays(("board", DslValueType.Int, 16));
            string? err = CustomUiGate.Check(tree, vars, arrays);
            Assert.NotNull(err);
            Assert.Contains("MaxListRows", err!);
        }

        [Fact]
        public void NestedChildBind_ResolvesAndLocates()
        {
            var tree = Tree(new PanelWidget
            {
                Id = 1, W = 300, H = 120,
                Children = new WidgetBase[] { new CounterWidget { Id = 2, Bind = "ghost", W = 160, H = 40 } },
            });
            string? err = CustomUiGate.Check(tree, Vars(), NoArrays);
            Assert.NotNull(err);
            Assert.Contains("scenario.custom_ui.widgets[0].children[0].bind", err!);
        }
    }
}
