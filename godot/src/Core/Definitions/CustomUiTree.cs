#nullable enable
using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// Story 7.8 — the 9-point anchor an anchored widget positions against inside the fixed 16:9 canvas. A closed
    /// enum serialized by NAME (the registered <c>JsonStringEnumConverter</c>), so an unknown anchor fails closed at
    /// parse. The <c>WidgetBaseJsonConverter</c> additionally rejects a numeric/out-of-set anchor with a located
    /// error, and <c>CustomUiGate</c> re-validates it (belt-and-suspenders).
    /// </summary>
    public enum AnchorPoint
    {
        TopLeft, TopCenter, TopRight,
        CenterLeft, Center, CenterRight,
        BottomLeft, BottomCenter, BottomRight,
    }

    /// <summary>
    /// Story 7.8 — the closed-vocabulary declarative custom-UI widget tree persisted in <c>ScenarioData</c>. Pure
    /// sim-layer/definitions C#: <b>Godot-free and Fixed/int-only</b> — positions/sizes are integer canvas units,
    /// no fractional numeric type is stored, and no presentation type appears here. The tree is
    /// (de)serialized through the closed-registry <see cref="WidgetBaseJsonConverter"/> (kind discriminator, no
    /// reflection, no <c>[JsonPolymorphic]</c>), folded into <c>CanonicalModelHash</c> via a typed recursive walk
    /// (so divergent UIs refuse to start), and validated + cap-checked at load by <c>CustomUiGate</c>. The renderer
    /// (<c>CustomUiBridge</c>, presentation-side) reads a version-stamped copy of already-checksummed variable state
    /// and re-formats a widget only when its bound variable's version changes; all formatting is presentation-side.
    ///
    /// The tree is read-only display (Story 7.8) — there is NO Button/write-rail kind here (that is Story 7.9).
    ///
    /// <para>Fail-closed on unknown keys (<see cref="JsonUnmappedMemberHandlingAttribute"/> Disallow): an
    /// unrecognized property on the <c>custom_ui</c> object (e.g. a stray/typo'd key) throws a
    /// <c>JsonException</c> at parse instead of being silently dropped — matching the widget-level converter's
    /// <c>RejectUnknownProperties</c> posture. This attribute is LOCAL to this type; the global serializer options
    /// are unchanged (other ScenarioData types keep their lenient handling).</para>
    /// </summary>
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed class CustomUiTree
    {
        /// <summary>The authored reference canvas width in integer canvas units. Fixed 16:9 (default 1920×1080);
        /// the renderer maps this onto the actual viewport's 16:9 safe area. Folded into the canonical hash.</summary>
        [JsonPropertyName("canvas_w")]
        public int CanvasWidth { get; set; } = 1920;

        /// <summary>The authored reference canvas height in integer canvas units (default 1080). Folded.</summary>
        [JsonPropertyName("canvas_h")]
        public int CanvasHeight { get; set; } = 1080;

        /// <summary>The top-level widgets. Each may nest <see cref="WidgetBase.Children"/> (Panel is the natural
        /// container). Serialized element-by-element through <see cref="WidgetBaseJsonConverter"/>.</summary>
        [JsonPropertyName("widgets")]
        public WidgetBase[] Widgets { get; set; } = Array.Empty<WidgetBase>();
    }

    /// <summary>
    /// Story 7.8 — the closed widget kinds. A closed union: any other <c>kind</c> string fails closed at parse in
    /// <see cref="WidgetBaseJsonConverter"/> with a located error naming it. No <c>Button</c> (write rail, 7.9).
    /// </summary>
    public enum WidgetKind
    {
        Panel, Label, Counter, ProgressBar, Timer, Leaderboard, FloatingText, ItemList,
    }

    /// <summary>
    /// Story 7.8 — the base of every widget. Carries the anchor + integer offset/size + optional visibility bind +
    /// nested children + the verbatim <c>_editor</c> annotation bag (excluded from the canonical hash BY
    /// CONSTRUCTION — the typed fold reads typed fields only, never this bag). Godot-free, Fixed/int-only.
    /// </summary>
    public abstract class WidgetBase
    {
        /// <summary>The widget id, unique within a tree (duplicate ids reject at <c>CustomUiGate</c>).</summary>
        public int Id { get; set; }

        /// <summary>The 9-point anchor this widget positions against inside the 16:9 canvas.</summary>
        public AnchorPoint Anchor { get; set; } = AnchorPoint.TopLeft;

        /// <summary>Integer x offset (canvas units) from the anchor. May be negative (e.g. a right-anchored inset).</summary>
        public int X { get; set; }

        /// <summary>Integer y offset (canvas units) from the anchor.</summary>
        public int Y { get; set; }

        /// <summary>Integer width (canvas units).</summary>
        public int W { get; set; }

        /// <summary>Integer height (canvas units).</summary>
        public int H { get; set; }

        /// <summary>OPTIONAL trigger-driven visibility bind: a declared DSL variable name whose truthy value shows
        /// the widget. Null = always visible. Must resolve + type-match (Int/Fixed/Bool) at load (<c>CustomUiGate</c>).</summary>
        public string? VisibleBind { get; set; }

        /// <summary>Nested children (Panel is the container; leaves default to none). Recursion depth is capped by
        /// <c>DslBounds.MaxWidgetDepth</c> at load.</summary>
        public WidgetBase[] Children { get; set; } = Array.Empty<WidgetBase>();

        /// <summary>The OPTIONAL per-widget verbatim <c>_editor</c> annotation bag (authoring positions/affordances).
        /// Round-tripped, NEVER interpreted, size-capped (<c>DslBounds.MaxEditorBagBytes</c>), and EXCLUDED from the
        /// canonical hash by construction (the NodeBase <c>_editor</c> precedent).</summary>
        public JsonElement? Editor { get; set; }

        /// <summary>The closed-registry discriminator this widget serializes under.</summary>
        public abstract WidgetKind Kind { get; }

        /// <summary>The OPTIONAL data bind — the declared variable this widget displays. Null = no data bind (Panel).
        /// Scalar widgets require an Int/Fixed bind; repeaters require an <c>Array&lt;scalar&gt;</c> bind. The single
        /// generic accessor <c>CustomUiGate</c> checks (never a per-kind copy).</summary>
        public virtual string? ValueBind => null;

        /// <summary>True when <see cref="ValueBind"/> must resolve to an <c>Array&lt;scalar&gt;</c> variable
        /// (Leaderboard/ItemList repeaters); false when it must be a scalar (Label/Counter/ProgressBar/Timer).</summary>
        public virtual bool ExpectsArrayBind => false;

        /// <summary>The authored row cap for a data-bound repeater (Leaderboard/ItemList); 0 for non-repeaters.
        /// Cap-checked against <c>DslBounds.MaxListRows</c> at load.</summary>
        public virtual int MaxRows => 0;
    }

    /// <summary>Story 7.8 — a background container. Holds children; binds no data.</summary>
    public sealed class PanelWidget : WidgetBase
    {
        public override WidgetKind Kind => WidgetKind.Panel;
    }

    /// <summary>Story 7.8 — a text label. Optional static <see cref="Text"/> caption + optional scalar
    /// <see cref="Bind"/> (its formatted value is appended presentation-side).</summary>
    public sealed class LabelWidget : WidgetBase
    {
        public override WidgetKind Kind => WidgetKind.Label;
        public string? Text { get; set; }
        public string? Bind { get; set; }
        public override string? ValueBind => Bind;
    }

    /// <summary>Story 7.8 — a live integer/Fixed counter bound to a scalar variable (formatted int→string
    /// presentation-side).</summary>
    public sealed class CounterWidget : WidgetBase
    {
        public override WidgetKind Kind => WidgetKind.Counter;
        public string? Bind { get; set; }
        public override string? ValueBind => Bind;
    }

    /// <summary>Story 7.8 — a progress bar: a scalar <see cref="Bind"/> value over an integer <see cref="Max"/>
    /// denominator (fraction computed presentation-side).</summary>
    public sealed class ProgressBarWidget : WidgetBase
    {
        public override WidgetKind Kind => WidgetKind.ProgressBar;
        public string? Bind { get; set; }
        public int Max { get; set; } = 100;
        public override string? ValueBind => Bind;
    }

    /// <summary>Story 7.8 — a countdown/count-up timer bound to a Fixed(seconds)/Int(ticks) variable, formatted
    /// mm:ss presentation-side (no string ever enters the tick).</summary>
    public sealed class TimerWidget : WidgetBase
    {
        public override WidgetKind Kind => WidgetKind.Timer;
        public string? Bind { get; set; }
        public override string? ValueBind => Bind;
    }

    /// <summary>Story 7.8 — a data-bound leaderboard repeater over an <c>Array&lt;scalar&gt;</c> variable, one row
    /// per element up to <see cref="MaxRows"/> (cap <c>DslBounds.MaxListRows</c>).</summary>
    public sealed class LeaderboardWidget : WidgetBase
    {
        public override WidgetKind Kind => WidgetKind.Leaderboard;
        public string? Bind { get; set; }
        private int _maxRows = 8;
        public int Rows { get => _maxRows; set => _maxRows = value; }
        public override string? ValueBind => Bind;
        public override bool ExpectsArrayBind => true;
        public override int MaxRows => _maxRows;
    }

    /// <summary>Story 7.8 — a transient floating-text readout. Optional static <see cref="Text"/> + optional scalar
    /// <see cref="Bind"/>.</summary>
    public sealed class FloatingTextWidget : WidgetBase
    {
        public override WidgetKind Kind => WidgetKind.FloatingText;
        public string? Text { get; set; }
        public string? Bind { get; set; }
        public override string? ValueBind => Bind;
    }

    /// <summary>Story 7.8 — a data-bound item-list repeater over an <c>Array&lt;scalar&gt;</c> variable, one row per
    /// element up to <see cref="MaxRows"/> (cap <c>DslBounds.MaxListRows</c>).</summary>
    public sealed class ItemListWidget : WidgetBase
    {
        public override WidgetKind Kind => WidgetKind.ItemList;
        public string? Bind { get; set; }
        private int _maxRows = 8;
        public int Rows { get => _maxRows; set => _maxRows = value; }
        public override string? ValueBind => Bind;
        public override bool ExpectsArrayBind => true;
        public override int MaxRows => _maxRows;
    }
}
