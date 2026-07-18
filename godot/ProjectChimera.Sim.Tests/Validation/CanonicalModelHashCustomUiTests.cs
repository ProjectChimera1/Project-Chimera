#nullable enable
using System.Text.Json;
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Validation
{
    /// <summary>
    /// Story 7.8 — the v9 <see cref="CanonicalModelHash"/> custom-UI fold sensitivity suite. A sim-semantic widget
    /// edit (bind / kind / layout field) MOVES the handshake hash so divergent widget trees are rejected at the
    /// lobby; a cosmetic edit (<c>_editor</c> content, absent-vs-empty) does NOT; and the fold is a TYPED walk, not
    /// JSON bytes. Precedent: <c>CanonicalModelHashDeclarationFoldTests</c>.
    /// </summary>
    public class CanonicalModelHashCustomUiTests
    {
        private static ScenarioData Base() => new() { MapBounds = 120f };

        private static CustomUiTree OneCounter(string bind = "score", int x = -220) => new()
        {
            Widgets = new WidgetBase[] { new CounterWidget { Id = 1, Anchor = AnchorPoint.TopRight, X = x, W = 200, H = 48, Bind = bind } },
        };

        /// <summary>A scenario carrying a single-widget custom UI tree.</summary>
        private static ScenarioData WithWidget(WidgetBase w)
        {
            var m = Base();
            m.CustomUi = new CustomUiTree { Widgets = new[] { w } };
            return m;
        }

        /// <summary>A scenario carrying an explicit custom UI tree (for canvas-dimension mutations).</summary>
        private static ScenarioData WithTree(CustomUiTree t)
        {
            var m = Base();
            m.CustomUi = t;
            return m;
        }

        private static void AssertMoves(ScenarioData a, ScenarioData b) =>
            Assert.NotEqual(CanonicalModelHash.Compute(a), CanonicalModelHash.Compute(b));

        [Fact]
        public void AlgoVersion_IsTwelve() => Assert.Equal(14, CanonicalModelHash.AlgoVersion);

        [Fact]
        public void AbsentVsEmpty_HashEqual()
        {
            var absent = Base();                                  // CustomUi == null
            var empty = Base(); empty.CustomUi = new CustomUiTree(); // present but no widgets
            Assert.Equal(CanonicalModelHash.Compute(absent), CanonicalModelHash.Compute(empty));
        }

        [Fact]
        public void PresentTree_MovesHash()
        {
            var withUi = Base(); withUi.CustomUi = OneCounter();
            Assert.NotEqual(CanonicalModelHash.Compute(Base()), CanonicalModelHash.Compute(withUi));
        }

        [Fact]
        public void ChangedBind_MovesHash()
        {
            var a = Base(); a.CustomUi = OneCounter(bind: "score");
            var b = Base(); b.CustomUi = OneCounter(bind: "kills");
            Assert.NotEqual(CanonicalModelHash.Compute(a), CanonicalModelHash.Compute(b));
        }

        [Fact]
        public void ChangedKind_MovesHash()
        {
            var a = Base(); a.CustomUi = new CustomUiTree { Widgets = new WidgetBase[] { new CounterWidget { Id = 1, Bind = "score" } } };
            var b = Base(); b.CustomUi = new CustomUiTree { Widgets = new WidgetBase[] { new LabelWidget { Id = 1, Bind = "score" } } };
            Assert.NotEqual(CanonicalModelHash.Compute(a), CanonicalModelHash.Compute(b));
        }

        [Fact]
        public void ChangedLayoutField_MovesHash()
        {
            var a = Base(); a.CustomUi = OneCounter(x: -220);
            var b = Base(); b.CustomUi = OneCounter(x: -100);
            Assert.NotEqual(CanonicalModelHash.Compute(a), CanonicalModelHash.Compute(b));
        }

        [Fact]
        public void ChangedAnchor_MovesHash()
        {
            var a = Base(); a.CustomUi = new CustomUiTree { Widgets = new WidgetBase[] { new CounterWidget { Id = 1, Anchor = AnchorPoint.TopRight, Bind = "score" } } };
            var b = Base(); b.CustomUi = new CustomUiTree { Widgets = new WidgetBase[] { new CounterWidget { Id = 1, Anchor = AnchorPoint.TopLeft, Bind = "score" } } };
            Assert.NotEqual(CanonicalModelHash.Compute(a), CanonicalModelHash.Compute(b));
        }

        // ── Per-field fold sensitivity (Story 7.8, v9): one fact per remaining folded field (mirrors
        //    ChangedBind_MovesHash) — each mutates exactly one field on an otherwise-identical present tree. ──

        [Fact]
        public void ChangedId_MovesHash() => AssertMoves(
            WithWidget(new CounterWidget { Id = 1, Bind = "score" }),
            WithWidget(new CounterWidget { Id = 2, Bind = "score" }));

        [Fact]
        public void ChangedY_MovesHash() => AssertMoves(
            WithWidget(new CounterWidget { Id = 1, Bind = "score", Y = 0 }),
            WithWidget(new CounterWidget { Id = 1, Bind = "score", Y = 40 }));

        [Fact]
        public void ChangedW_MovesHash() => AssertMoves(
            WithWidget(new CounterWidget { Id = 1, Bind = "score", W = 200 }),
            WithWidget(new CounterWidget { Id = 1, Bind = "score", W = 240 }));

        [Fact]
        public void ChangedH_MovesHash() => AssertMoves(
            WithWidget(new CounterWidget { Id = 1, Bind = "score", H = 48 }),
            WithWidget(new CounterWidget { Id = 1, Bind = "score", H = 64 }));

        [Fact]
        public void ChangedVisibleBind_MovesHash() => AssertMoves(
            WithWidget(new CounterWidget { Id = 1, Bind = "score", VisibleBind = null }),
            WithWidget(new CounterWidget { Id = 1, Bind = "score", VisibleBind = "show" }));

        [Fact]
        public void ChangedCanvasWidth_MovesHash() => AssertMoves(
            WithTree(new CustomUiTree { CanvasWidth = 1920, Widgets = new WidgetBase[] { new CounterWidget { Id = 1, Bind = "score" } } }),
            WithTree(new CustomUiTree { CanvasWidth = 1280, Widgets = new WidgetBase[] { new CounterWidget { Id = 1, Bind = "score" } } }));

        [Fact]
        public void ChangedCanvasHeight_MovesHash() => AssertMoves(
            WithTree(new CustomUiTree { CanvasHeight = 1080, Widgets = new WidgetBase[] { new CounterWidget { Id = 1, Bind = "score" } } }),
            WithTree(new CustomUiTree { CanvasHeight = 720, Widgets = new WidgetBase[] { new CounterWidget { Id = 1, Bind = "score" } } }));

        [Fact]
        public void ChangedProgressBarMax_MovesHash() => AssertMoves(
            WithWidget(new ProgressBarWidget { Id = 1, Bind = "hp", Max = 100 }),
            WithWidget(new ProgressBarWidget { Id = 1, Bind = "hp", Max = 200 }));

        [Fact]
        public void ChangedLeaderboardRows_MovesHash() => AssertMoves(
            WithWidget(new LeaderboardWidget { Id = 1, Bind = "board", Rows = 8 }),
            WithWidget(new LeaderboardWidget { Id = 1, Bind = "board", Rows = 5 }));

        [Fact]
        public void ChangedItemListRows_MovesHash() => AssertMoves(
            WithWidget(new ItemListWidget { Id = 1, Bind = "items", Rows = 8 }),
            WithWidget(new ItemListWidget { Id = 1, Bind = "items", Rows = 5 }));

        [Fact]
        public void ChangedLabelText_MovesHash() => AssertMoves(
            WithWidget(new LabelWidget { Id = 1, Text = "Score" }),
            WithWidget(new LabelWidget { Id = 1, Text = "Kills" }));

        [Fact]
        public void ChangedFloatingTextText_MovesHash() => AssertMoves(
            WithWidget(new FloatingTextWidget { Id = 1, Text = "Go!" }),
            WithWidget(new FloatingTextWidget { Id = 1, Text = "Stop!" }));

        [Fact]
        public void EditorBagContent_DoesNotMoveHash()
        {
            var a = Base(); a.CustomUi = OneCounter();
            a.CustomUi!.Widgets[0].Editor = JsonSerializer.Deserialize<JsonElement>("{\"note\":\"here\"}");
            var b = Base(); b.CustomUi = OneCounter();
            b.CustomUi!.Widgets[0].Editor = JsonSerializer.Deserialize<JsonElement>("{\"note\":\"somewhere-else\",\"z\":99}");
            Assert.Equal(CanonicalModelHash.Compute(a), CanonicalModelHash.Compute(b));
        }

        [Fact]
        public void ChildBindChange_MovesHash()
        {
            CustomUiTree Panel(string childBind) => new()
            {
                Widgets = new WidgetBase[]
                {
                    new PanelWidget { Id = 1, Children = new WidgetBase[] { new CounterWidget { Id = 2, Bind = childBind } } },
                },
            };
            var a = Base(); a.CustomUi = Panel("score");
            var b = Base(); b.CustomUi = Panel("kills");
            Assert.NotEqual(CanonicalModelHash.Compute(a), CanonicalModelHash.Compute(b));
        }

        [Fact]
        public void SameTree_HashStable_ReSaveNeutral()
        {
            var a = Base(); a.CustomUi = OneCounter();
            // Round-trip through the serializer (a re-save) and confirm the hash is unchanged.
            var opt = new System.Text.Json.JsonSerializerOptions
            {
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(), new FixedJsonConverter(), new WidgetBaseJsonConverter() },
            };
            var back = JsonSerializer.Deserialize<ScenarioData>(ScenarioSerializer.Serialize(a), opt);
            Assert.Equal(CanonicalModelHash.Compute(a), CanonicalModelHash.Compute(back!));
        }
    }
}
