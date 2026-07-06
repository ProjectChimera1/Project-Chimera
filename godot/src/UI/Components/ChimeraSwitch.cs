#nullable enable
using Godot;
using ProjectChimera.UI.Theme;

namespace ProjectChimera.UI.Components
{
    /// <summary>
    /// switch (UX-DR31/54) — a clean faceted on/off toggle for simple↔advanced disclosure and inline field
    /// reveal. Built on a toggle <see cref="Button"/> (D-5), NOT a <see cref="CheckButton"/> (whose knob is a
    /// round <c>Texture2D</c> icon that can't be a faceted token-driven square). Inheriting Button gives
    /// focus + Space-to-toggle + the <c>Toggled(bool)</c> signal for free (a11y / UX-DR45).
    ///
    /// Anatomy (editor.css <c>.switch</c>): a 42×24 faceted track (surface_4 off → accent on) with an
    /// 18×18 faceted-SQUARE knob (text_mid off → accent_ink on) that slides left 3→21 over the <c>speed</c>
    /// token (130ms — "toggles snap"). The Button's own chrome is empty; a <c>_track</c> + <c>_knob</c>
    /// child Panel draw the visible switch, and an accent focus ring draws over on focus.
    ///
    /// ACCENT SEAM: the track/knob are STATEFUL two-color surfaces (they must both TWEEN their color AND
    /// retint on an accent switch), so — unlike the kit's always-accent registered boxes — they use a
    /// per-instance stylebox whose <c>bg_color</c> is tweened, plus a tracked <c>SubscribeAccentChanged</c>
    /// that re-reads accent ONLY while on. This mirrors how <see cref="ChimeraTabs"/> restyles its active
    /// label on a switch rather than registering a color that would go stale in the off state.
    ///
    /// Presentation layer.
    /// </summary>
    public partial class ChimeraSwitch : Button
    {
        private Panel _track = null!;
        private Panel _knob = null!;
        private StyleBoxFlat _trackBox = null!;
        private StyleBoxFlat _knobBox = null!;
        private Control? _revealTarget;
        private Tween? _tween; // the in-flight toggle animation, if any (killed on re-toggle / accent switch)

        /// <summary>Whether the switch is on (mirrors the toggle Button's pressed state).</summary>
        public bool On => ButtonPressed;

        /// <summary>Build a switch, optionally starting on. Wire the inherited <c>Toggled(bool)</c> signal.</summary>
        public static ChimeraSwitch Create(bool on = false)
        {
            var sw = new ChimeraSwitch();
            sw.Build(on);
            return sw;
        }

        private void Build(bool on)
        {
            ToggleMode = true;
            CustomMinimumSize = new Vector2(ComponentMetrics.SwitchWidth, ComponentMetrics.SwitchHeight);

            // The Button chrome itself draws nothing — the track/knob children are the switch. Focus draws
            // the shared accent ring over the top (same registered ring the buttons use → retints in step).
            var empty = new StyleBoxEmpty();
            foreach (var st in new[] { "normal", "hover", "pressed", "hover_pressed", "disabled" })
                AddThemeStyleboxOverride(st, empty);
            AddThemeStyleboxOverride("focus", ChimeraComponents.SharedAccentBox("btn/focus",
                () => ChimeraStyleBox.Chamfer(ChimeraComponents.Const(ThemeTokens.CutSm),
                        new Color(0, 0, 0, 0), ChimeraComponents.Col(ThemeTokens.Accent), 2),
                ChimeraComponents.Border(ThemeTokens.Accent)));

            // Track: full-rect faceted (cut_sm) panel, per-instance box so its bg_color can tween.
            _trackBox = ChimeraStyleBox.Chamfer(ChimeraComponents.Const(ThemeTokens.CutSm),
                ChimeraComponents.Col(ThemeTokens.Surface4), ChimeraComponents.Col(ThemeTokens.Surface4), 0);
            _track = new Panel { MouseFilter = MouseFilterEnum.Ignore };
            _track.SetAnchorsPreset(LayoutPreset.FullRect);
            _track.AddThemeStyleboxOverride("panel", _trackBox);
            AddChild(_track);

            // Knob: an 18×18 FACETED SQUARE (cut 3 — never round), positioned by absolute offsets.
            _knobBox = ChimeraStyleBox.Chamfer(ComponentMetrics.CutSwitchKnob,
                ChimeraComponents.Col(ThemeTokens.TextMid), ChimeraComponents.Col(ThemeTokens.TextMid), 0);
            _knob = new Panel { MouseFilter = MouseFilterEnum.Ignore };
            _knob.AnchorLeft = 0; _knob.AnchorTop = 0; _knob.AnchorRight = 0; _knob.AnchorBottom = 0;
            _knob.OffsetTop = ComponentMetrics.SwitchKnobInset;
            _knob.OffsetBottom = ComponentMetrics.SwitchKnobInset + ComponentMetrics.SwitchKnob;
            _knob.AddThemeStyleboxOverride("panel", _knobBox);
            AddChild(_knob);

            // Re-read accent (only while on) on a switch — the stateful-surface analog of the kit's
            // registration. Kill any in-flight toggle tween first: an accent switch landing mid-toggle would
            // otherwise be overwritten when the (stale-colored) tween completes, leaving a stale track (AC6).
            ChimeraComponents.SubscribeAccentChanged(this, _ =>
            {
                if (!GodotObject.IsInstanceValid(this) || !On) return;
                if (_tween != null && _tween.IsValid()) _tween.Kill();
                _trackBox.BgColor = ChimeraComponents.Col(ThemeTokens.Accent);
                _knobBox.BgColor = ChimeraComponents.Col(ThemeTokens.AccentInk);
                _knob.OffsetLeft = ComponentMetrics.SwitchKnobOnLeft;
                _knob.OffsetRight = ComponentMetrics.SwitchKnobOnLeft + ComponentMetrics.SwitchKnob;
            });

            Toggled += OnToggled;

            // Initial state (no animation, no signal — just paint the correct look).
            SetPressedNoSignal(on);
            ApplyState(on, animate: false);
        }

        private void OnToggled(bool on) => ApplyState(on, animate: true);

        /// <summary>
        /// Set the on/off state programmatically (no <c>Toggled</c> signal). <paramref name="animate"/>
        /// slides the knob + fades the color over the speed token; false snaps instantly.
        /// </summary>
        public void SetOn(bool on, bool animate = true)
        {
            SetPressedNoSignal(on);
            ApplyState(on, animate);
        }

        /// <summary>
        /// Bind a control to reveal/hide inline when the switch toggles (the Promote-to-Hero pattern,
        /// UX-DR54). Sets the target's initial visibility to the current state.
        /// </summary>
        public void BindReveal(Control target)
        {
            _revealTarget = target;
            if (GodotObject.IsInstanceValid(target)) target.Visible = On;
        }

        // Apply the visual + reveal for a state, tweening (unless animate is false or reduced-motion is on).
        private void ApplyState(bool on, bool animate)
        {
            Color trackTo = on ? ChimeraComponents.Col(ThemeTokens.Accent) : ChimeraComponents.Col(ThemeTokens.Surface4);
            Color knobTo = on ? ChimeraComponents.Col(ThemeTokens.AccentInk) : ChimeraComponents.Col(ThemeTokens.TextMid);
            float knobLeft = on ? ComponentMetrics.SwitchKnobOnLeft : ComponentMetrics.SwitchKnobInset;
            float knobRight = knobLeft + ComponentMetrics.SwitchKnob;

            // Kill a prior in-flight toggle so rapid toggles (or an accent switch) never leave a stale tween
            // racing the latest state.
            if (_tween != null && _tween.IsValid()) _tween.Kill();

            double dur = animate ? ChimeraMotion.SpeedSeconds() : 0.0;
            if (dur <= 0.0)
            {
                _trackBox.BgColor = trackTo;
                _knobBox.BgColor = knobTo;
                _knob.OffsetLeft = knobLeft;
                _knob.OffsetRight = knobRight;
            }
            else
            {
                _tween = CreateTween().SetParallel(true);
                _tween.TweenProperty(_trackBox, "bg_color", trackTo, dur);
                _tween.TweenProperty(_knobBox, "bg_color", knobTo, dur);
                _tween.TweenProperty(_knob, "offset_left", knobLeft, dur);
                _tween.TweenProperty(_knob, "offset_right", knobRight, dur);
            }

            if (_revealTarget != null && GodotObject.IsInstanceValid(_revealTarget))
                _revealTarget.Visible = on;
        }
    }
}
