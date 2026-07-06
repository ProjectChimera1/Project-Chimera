#nullable enable
using Godot;
using ProjectChimera.UI.Theme;

namespace ProjectChimera.UI.Components
{
    /// <summary>
    /// spinner (UX-DR29/52) — the "transmute" loader. A procedural <see cref="Control"/> (D-1) drawing
    /// three layers from the live accent tokens, transcribed from <c>theme.js</c> (<c>#chimera-spin-ring</c>
    /// / <c>-tri</c> / <c>-core</c>, viewBox 96):
    ///   • <b>ring</b> — a faint accent-dim circle + an accent dash arc + 4 edge ticks, rotating CW (2.6s);
    ///   • <b>triangles</b> — the fire + ghosted water pair with vertex anchors, rotating CCW (5.2s);
    ///   • <b>core</b> — the accent nucleus with an accent-glow, pulsing scale + opacity (1.3s).
    ///
    /// The three layers advance independent phase angles in <see cref="_Process"/> and draw rotated in
    /// <see cref="_Draw"/>. ALL motion is gated on <see cref="ChimeraMotion.ReducedMotion"/> — when set,
    /// the phases stop advancing (a still sigil), no busy loop. On an accent switch the spinner re-reads its
    /// tokens and repaints (tracked <c>SubscribeAccentChanged</c> → <see cref="QueueRedraw"/>), so its glow
    /// follows the accent live. Pass an <c>overrideColor</c> to freeze it to a fixed hue (the banner-stall's
    /// warn spinner) — that instance ignores the accent entirely.
    ///
    /// Presentation layer.
    /// </summary>
    public partial class ChimeraSpinner : Control
    {
        private float _ringAngle;   // CW
        private float _triAngle;    // CCW
        private float _corePhase;   // pulse
        private Color? _override;   // when set, the whole sigil is this one hue (banner-stall warn)

        /// <summary>
        /// Build a spinner at <paramref name="size"/> px (use <see cref="ComponentMetrics.SpinnerSm"/> 22 /
        /// <see cref="ComponentMetrics.SpinnerDefault"/> 48 / <see cref="ComponentMetrics.SpinnerLg"/> 96).
        /// <paramref name="overrideColor"/> freezes it to a single hue (no accent-follow) — used for the
        /// banner-stall warn spinner where the CSS sets all three accent shades to <c>--warn</c>.
        /// </summary>
        public static ChimeraSpinner Create(int size = 48, Color? overrideColor = null)
        {
            var sp = new ChimeraSpinner
            {
                _override = overrideColor,
                CustomMinimumSize = new Vector2(size, size),
            };
            sp.MouseFilter = MouseFilterEnum.Ignore;
            // "Transmuting…" accessible/role-equivalent name (Godot has no ARIA; this is the closest handle).
            sp.Name = "Spinner";
            // Accent-follow only when not colour-overridden.
            if (overrideColor == null)
            {
                ChimeraComponents.SubscribeAccentChanged(sp, _ =>
                {
                    if (GodotObject.IsInstanceValid(sp)) sp.QueueRedraw();
                });
            }
            return sp;
        }

        /// <inheritdoc/>
        public override void _Process(double delta)
        {
            // Reduced-motion (UX-DR44): hold still, and never spin the frame loop needlessly.
            if (ChimeraMotion.ReducedMotion) return;
            _ringAngle += (float)(delta / (ComponentMetrics.SpinnerRingMs / 1000.0) * Mathf.Tau); // CW
            _triAngle  -= (float)(delta / (ComponentMetrics.SpinnerTriMs / 1000.0) * Mathf.Tau);  // CCW
            _corePhase += (float)(delta / (ComponentMetrics.SpinnerCoreMs / 1000.0) * Mathf.Tau); // pulse
            QueueRedraw();
        }

        /// <inheritdoc/>
        public override void _Draw()
        {
            if (_override == null && !ChimeraComponents.IsInitialized) return;

            float s = Size.X / ComponentMetrics.SealViewBox; // spinner shares the seal's 96 viewBox
            Vector2 P(float x, float y) => new Vector2(x * s, y * s);
            var center = P(48, 48);

            // Resolve the palette: either the frozen override (all shades one hue) or the live accent tokens.
            Color accent, dim, bright, glow;
            if (_override is Color oc)
            {
                accent = dim = bright = oc;
                glow = new Color(oc.R, oc.G, oc.B, 0.28f);
            }
            else
            {
                accent = ChimeraComponents.Col(ThemeTokens.Accent);
                dim = ChimeraComponents.Col(ThemeTokens.AccentDim);
                bright = ChimeraComponents.Col(ThemeTokens.AccentBright);
                glow = ChimeraComponents.Col(ThemeTokens.AccentGlow);
            }

            // ── Ring layer (rotates CW by _ringAngle) ──
            DrawArc(center, 44f * s, 0f, Mathf.Tau, 72, new Color(dim, 0.4f), 2f * s, true); // faint full circle
            // Accent dash arc: dasharray 60/216 → ~78° of sweep out of the full circle, spun by _ringAngle.
            const float dashSweep = 60f / (60f + 216f) * Mathf.Tau;
            DrawArc(center, 44f * s, _ringAngle, _ringAngle + dashSweep, 24, accent, 2.5f * s, true);
            // 4 edge ticks (outer r48 → inner r41), rotating with the ring.
            for (int i = 0; i < 4; i++)
            {
                float a = _ringAngle + i * Mathf.Pi / 2f;
                var dir = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                DrawLine(center + dir * 48f * s, center + dir * 41f * s, new Color(accent, 0.85f), 2f * s, true);
            }

            // ── Triangle layer (rotates CCW by _triAngle) ──
            Vector2 R(Vector2 p) => Rotate(p, center, _triAngle);
            Vector2[] fire = { R(P(48, 19)), R(P(73.4f, 63)), R(P(22.6f, 63)) };
            Vector2[] water = { R(P(48, 77)), R(P(22.6f, 33)), R(P(73.4f, 33)) };
            DrawTri(fire, accent, 2.4f * s);
            DrawTri(water, new Color(dim, 0.75f), 1.7f * s);
            foreach (var v in fire) DrawCircle(v, 3.2f * s, bright);

            // ── Core layer (pulse: scale .92↔1.06 + opacity .45↔1 over the cycle) ──
            float t = 0.5f - 0.5f * Mathf.Cos(_corePhase);          // smooth 0→1→0
            float scale = Mathf.Lerp(0.92f, 1.06f, t);
            float alpha = Mathf.Lerp(0.45f, 1f, t);
            DrawCircle(center, 6.4f * s * 2.2f, new Color(glow, glow.A * alpha)); // accent-glow, live
            DrawCircle(center, 6.4f * s * scale, new Color(accent, accent.A * alpha));
        }

        // Rotate a point around a center by an angle (radians).
        private static Vector2 Rotate(Vector2 p, Vector2 c, float a)
        {
            var d = p - c;
            float cos = Mathf.Cos(a), sin = Mathf.Sin(a);
            return c + new Vector2(d.X * cos - d.Y * sin, d.X * sin + d.Y * cos);
        }

        private void DrawTri(Vector2[] tri, Color color, float width)
        {
            DrawPolyline(new[] { tri[0], tri[1], tri[2], tri[0] }, color, width, true);
        }
    }
}
