#nullable enable
using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using ProjectChimera.Dsl; // DslValueType (Story 7.9 Button arg types — a plain enum; sim-layer clean, engine-free)

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
    /// Story 7.8/7.9 — the closed widget kinds. A closed union: any other <c>kind</c> string fails closed at parse in
    /// <see cref="WidgetBaseJsonConverter"/> with a located error naming it. Story 7.9 appended <c>Button</c> (the
    /// interactive write-rail kind) LAST — do not reorder (enum names fold by NAME so order is cosmetic to the hash,
    /// but append-only keeps the source diff minimal).
    /// </summary>
    public enum WidgetKind
    {
        Panel, Label, Counter, ProgressBar, Timer, Leaderboard, FloatingText, ItemList,
        Button, // Story 7.9 — the write rail
    }

    /// <summary>
    /// Story 7.9 — the CLOSED, presentation-only local-action vocabulary a <c>Button</c> may perform INSTEAD of (or
    /// alongside) raising a networked custom event. Each action is handled entirely inside <c>CustomUiBridge</c>
    /// (presentation): it touches only Godot <c>Control</c> visibility and a presentation-only local variable store,
    /// NEVER a sim system, the DSL, or the lockstep bus — so it is provably disjoint from the sim/DSL namespaces and
    /// outside <c>SimChecksum</c> (a test asserts the checksum is byte-identical whether local actions fire or not).
    /// Godot-free/int-only here; an unknown local action rejects fail-closed at parse.
    /// </summary>
    public enum LocalUiAction
    {
        None,                 // no local action (the button raises a custom event instead)
        ToggleWidgetVisible,  // flip the target widget's Control visibility (LocalTargetWidgetId)
        OpenSubPanel,         // show the target widget (LocalTargetWidgetId)
        CloseSelf,            // hide this button's own widget
        SetLocalUiVar,        // set a presentation-only local UI var (LocalVarName = LocalVarValue)
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

    /// <summary>
    /// Story 7.9 — the interactive WRITE-RAIL widget. A press either raises a REGISTERED custom event through the
    /// lockstep command bus (<see cref="EventName"/> resolved to a registry index presentation-side, then
    /// <c>LockstepManager.EnqueueDslEvent</c>) or performs a presentation-only <see cref="LocalUiAction"/> — never
    /// mutating sim state directly. Godot-free/int-only: it stores the authored caption, the event name (resolved to
    /// an int index once at bridge init — the string NEVER enters the tick), the quantized int arg raws (Int/Bool
    /// value or <c>Fixed.Raw</c>) with a parallel authored-type array (authoring/gate-only — used to type-match args
    /// against the event's declared params and to round-trip; NOT folded into the canonical hash and NOT on the
    /// wire), and the local-action + its target. Validated at load by <c>CustomUiGate</c> (event declared,
    /// param-count ≤ <c>EventBounds.MaxButtonEventParams</c>, arg types match, at least one of event/local-action,
    /// valid local target).
    /// </summary>
    public sealed class ButtonWidget : WidgetBase
    {
        public override WidgetKind Kind => WidgetKind.Button;

        /// <summary>The button caption (static; presentation-only). Folded into the canonical hash.</summary>
        public string? Text { get; set; }

        /// <summary>OPTIONAL — the authored name of the registered custom event this button raises. Resolved to the
        /// registry INDEX once, presentation-side, at bridge init (the string never enters the tick). Null ⇒ this
        /// button performs only a <see cref="LocalAction"/>.</summary>
        public string? EventName { get; set; }

        /// <summary>The quantized argument raws forwarded to the raised event's param slots (Int/Bool value or
        /// <c>Fixed.Raw</c>). Length must equal the event's declared param count (≤
        /// <c>EventBounds.MaxButtonEventParams</c>). Empty for a local-action-only or param-less button.</summary>
        public int[] ArgRaws { get; set; } = Array.Empty<int>();

        /// <summary>The authored TYPE of each arg (parallel to <see cref="ArgRaws"/>) — authoring/gate metadata used
        /// to type-match against the event's declared params and to round-trip the JSON form. NOT folded into the
        /// canonical hash (only the raws fold) and NEVER on the wire.</summary>
        public DslValueType[] ArgTypes { get; set; } = Array.Empty<DslValueType>();

        /// <summary>OPTIONAL — the presentation-only local action this button performs (default <see cref="LocalUiAction.None"/>).</summary>
        public LocalUiAction LocalAction { get; set; } = LocalUiAction.None;

        /// <summary>The target widget id for <see cref="LocalUiAction.ToggleWidgetVisible"/>/<see cref="LocalUiAction.OpenSubPanel"/>
        /// (unused/−1 otherwise). Validated to name an existing widget at load.</summary>
        public int LocalTargetWidgetId { get; set; } = -1;

        /// <summary>The presentation-only local UI var name written by <see cref="LocalUiAction.SetLocalUiVar"/>
        /// (a store distinct from the sim <c>DslVarTable</c>). Null/unused for other actions.</summary>
        public string? LocalVarName { get; set; }

        /// <summary>The value written to <see cref="LocalVarName"/> by <see cref="LocalUiAction.SetLocalUiVar"/>.</summary>
        public int LocalVarValue { get; set; }

        /// <summary>Story 7.9 — the single Godot-free routing predicate for a press: true iff this button raises a
        /// networked custom event (has an <see cref="EventName"/>). A local-action-only button (<c>EventName</c> null)
        /// NEVER touches the lockstep bus — its press is presentation-only, so it can never enter <c>SimChecksum</c>.
        /// <c>CustomUiBridge</c> routes the <c>Pressed</c> handler through this so the seam is the one source of truth.</summary>
        public bool RaisesSimEvent => !string.IsNullOrEmpty(EventName);
    }
}
