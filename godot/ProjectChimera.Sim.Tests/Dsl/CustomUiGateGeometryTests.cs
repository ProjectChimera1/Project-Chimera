#nullable enable
using System.Collections.Generic;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Dsl;
using Xunit;

namespace ProjectChimera.Sim.Tests.Dsl
{
    /// <summary>
    /// DW-364 / DW-365 — the <see cref="CustomUiGate"/> geometry + lower-bound hardening. The renderer used to
    /// silently rewrite degenerate authored values (canvas &lt;= 0 → 1920×1080, a 0×0 widget → invisible,
    /// <c>rows:0</c> → <c>DslBounds.MaxListRows</c>, <c>max&lt;=0</c> → an empty bar) while
    /// <c>CanonicalModelHash</c> folded the RAW authored values into the MP handshake hash — hash and render
    /// disagreed on what the value meant. The gate now rejects the divergence AT LOAD with located errors:
    ///   • canvas dims must be ≥ 1 (tree-level, checked before the widget walk);
    ///   • every widget's W/H must be ≥ 1 (X/Y offsets may still be negative — an anchored inset);
    ///   • a repeater's authored <c>rows</c> must be ≥ 1 (the upper cap <c>MaxListRows</c> already existed);
    ///   • a ProgressBar's <c>max</c> must be ≥ 1.
    /// Every test here FAILS without the DW-364/DW-365 gate checks.
    /// </summary>
    public class CustomUiGateGeometryTests
    {
        private static readonly Dictionary<string, (DslValueType, VarScope)> NoVars = new();
        private static readonly Dictionary<string, (DslValueType, int)> NoArrays = new();

        private static CustomUiTree Tree(params WidgetBase[] widgets) => new() { Widgets = widgets };

        // ── DW-364 — canvas dims ────────────────────────────────────────────────

        [Fact]
        public void CanvasZeroWidth_Rejects_Located()
        {
            var tree = new CustomUiTree { CanvasWidth = 0, Widgets = new WidgetBase[] { new PanelWidget { Id = 1, W = 10, H = 10 } } };
            string? err = CustomUiGate.Check(tree, NoVars, NoArrays);
            Assert.NotNull(err);
            Assert.Contains("scenario.custom_ui.canvas_w=0", err!);
        }

        [Fact]
        public void CanvasNegativeHeight_Rejects_Located()
        {
            var tree = new CustomUiTree { CanvasHeight = -1080, Widgets = new WidgetBase[] { new PanelWidget { Id = 1, W = 10, H = 10 } } };
            string? err = CustomUiGate.Check(tree, NoVars, NoArrays);
            Assert.NotNull(err);
            Assert.Contains("scenario.custom_ui.canvas_h=-1080", err!);
        }

        [Fact]
        public void CanvasChecked_EvenOnWidgetlessTree()
        {
            // The validator invokes the gate whenever custom_ui is present; a degenerate canvas is an author
            // error even before any widget exists (fail-closed, never a renderer-side default).
            var tree = new CustomUiTree { CanvasWidth = 0 };
            string? err = CustomUiGate.Check(tree, NoVars, NoArrays);
            Assert.NotNull(err);
            Assert.Contains("canvas_w=0", err!);
        }

        [Fact]
        public void DefaultCanvas_WithPositiveGeometry_Passes()
        {
            // The authored default (1920×1080) plus positive widget sizes — the entire happy path is untouched.
            var tree = Tree(new PanelWidget { Id = 1, W = 300, H = 120 });
            Assert.Null(CustomUiGate.Check(tree, NoVars, NoArrays));
        }

        // ── DW-364 — widget W/H ─────────────────────────────────────────────────

        [Fact]
        public void ZeroWidth_Rejects_Located()
        {
            // W defaults to 0 — an authored widget that never set a width used to pass the gate and render invisibly.
            var tree = Tree(new LabelWidget { Id = 1, Text = "hi", H = 40 });
            string? err = CustomUiGate.Check(tree, NoVars, NoArrays);
            Assert.NotNull(err);
            Assert.Contains("scenario.custom_ui.widgets[0].w=0", err!);
        }

        [Fact]
        public void NegativeHeight_Rejects_Located()
        {
            var tree = Tree(new LabelWidget { Id = 1, Text = "hi", W = 200, H = -5 });
            string? err = CustomUiGate.Check(tree, NoVars, NoArrays);
            Assert.NotNull(err);
            Assert.Contains("scenario.custom_ui.widgets[0].h=-5", err!);
        }

        [Fact]
        public void NegativeOffsets_StillPass()
        {
            // X/Y are anchor-relative offsets and MAY be negative (e.g. a right-anchored inset) — only W/H are
            // gated. Guards against over-tightening.
            var tree = Tree(new LabelWidget { Id = 1, Text = "hi", X = -40, Y = -12, W = 200, H = 40 });
            Assert.Null(CustomUiGate.Check(tree, NoVars, NoArrays));
        }

        [Fact]
        public void NestedChildGeometry_Rejects_LocatedUnderChildren()
        {
            var tree = Tree(new PanelWidget
            {
                Id = 1, W = 300, H = 120,
                Children = new WidgetBase[] { new CounterWidget { Id = 2, W = 160 } }, // H = 0
            });
            string? err = CustomUiGate.Check(tree, NoVars, NoArrays);
            Assert.NotNull(err);
            Assert.Contains("scenario.custom_ui.widgets[0].children[0].h=0", err!);
        }

        // ── DW-365 — repeater rows lower bound ──────────────────────────────────

        [Fact]
        public void RepeaterZeroRows_Rejects_Located()
        {
            // rows:0 used to pass the gate, fold RAW into the canonical hash, then be silently overridden by the
            // renderer's `MaxRows > 0 ? MaxRows : MaxListRows` fallback — hash and render disagreed on its meaning.
            var tree = Tree(new LeaderboardWidget { Id = 1, Rows = 0, W = 240, H = 200 });
            string? err = CustomUiGate.Check(tree, NoVars, NoArrays);
            Assert.NotNull(err);
            Assert.Contains("scenario.custom_ui.widgets[0].rows=0", err!);
            Assert.Contains("MaxListRows", err!);
        }

        [Fact]
        public void RepeaterNegativeRows_Rejects_Located()
        {
            var tree = Tree(new ItemListWidget { Id = 1, Rows = -3, W = 240, H = 200 });
            string? err = CustomUiGate.Check(tree, NoVars, NoArrays);
            Assert.NotNull(err);
            Assert.Contains("scenario.custom_ui.widgets[0].rows=-3", err!);
        }

        [Fact]
        public void RepeaterRows_AtBothBounds_Pass()
        {
            Assert.Null(CustomUiGate.Check(
                Tree(new LeaderboardWidget { Id = 1, Rows = 1, W = 240, H = 200 }), NoVars, NoArrays));
            Assert.Null(CustomUiGate.Check(
                Tree(new ItemListWidget { Id = 1, Rows = DslBounds.MaxListRows, W = 240, H = 200 }), NoVars, NoArrays));
        }

        // ── DW-365 — ProgressBar max lower bound ────────────────────────────────

        [Fact]
        public void ProgressBarZeroMax_Rejects_Located()
        {
            // max:0 used to pass the gate and fold RAW while WidgetFormat.Fraction rendered it as an empty bar.
            var tree = Tree(new ProgressBarWidget { Id = 1, Max = 0, W = 220, H = 24 });
            string? err = CustomUiGate.Check(tree, NoVars, NoArrays);
            Assert.NotNull(err);
            Assert.Contains("scenario.custom_ui.widgets[0].max=0", err!);
        }

        [Fact]
        public void ProgressBarNegativeMax_Rejects_Located()
        {
            var tree = Tree(new ProgressBarWidget { Id = 1, Max = -100, W = 220, H = 24 });
            string? err = CustomUiGate.Check(tree, NoVars, NoArrays);
            Assert.NotNull(err);
            Assert.Contains("scenario.custom_ui.widgets[0].max=-100", err!);
        }

        [Fact]
        public void ProgressBarDefaultMax_Passes()
        {
            // The authored default (Max = 100) — an author who never touched max is untouched by DW-365.
            var tree = Tree(new ProgressBarWidget { Id = 1, W = 220, H = 24 });
            Assert.Null(CustomUiGate.Check(tree, NoVars, NoArrays));
        }
    }
}
