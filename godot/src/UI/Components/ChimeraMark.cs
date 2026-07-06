#nullable enable
using Godot;
using ProjectChimera.UI.Theme;

namespace ProjectChimera.UI.Components
{
    /// <summary>
    /// mark (UX-DR30) — the Chimera Seal, the sole shipping alchemy motif (UX-DR38). A procedural
    /// <see cref="Control"/> that draws the sigil in <c>_Draw</c> from the live accent tokens (D-1): two
    /// concentric rings, a dominant fire up-triangle over a ghosted water down-triangle, a nucleus, and
    /// three vertex anchor nodes. Geometry is transcribed 1:1 from <c>theme.js</c> <c>#chimera-seal</c>
    /// (viewBox 96) and <c>#chimera-triad</c> (viewBox 48), scaled to the control size.
    ///
    /// WHY PROCEDURAL (D-1): drawing from <c>Col(accent…)</c> live means an accent switch just calls
    /// <see cref="QueueRedraw"/> (via a tracked <c>SubscribeAccentChanged</c>) and the whole sigil — glow
    /// included — retints for free. A baked <c>Texture2D</c> could only <c>modulate</c> by one color and
    /// could not reproduce the accent / accent-dim / accent-bright three-shade split, which is exactly why
    /// this is a <c>_Draw</c> control and why no <see cref="AccentController"/> extension is needed.
    ///
    /// Static (no motion). Presentation layer.
    /// </summary>
    public partial class ChimeraMark : Control
    {
        private bool _triad;

        /// <summary>
        /// Build a mark at <paramref name="size"/> px. When <paramref name="triad"/> (or size ≤ 24px) the
        /// heavy-stroke small variant renders — ring + fire triangle + nucleus only (drops the inner ring,
        /// water triangle, and vertex nodes), the favicon-scale form.
        /// </summary>
        public static ChimeraMark Create(int size = 96, bool triad = false)
        {
            var m = new ChimeraMark
            {
                _triad = triad || size <= ComponentMetrics.MarkTriadThreshold,
                CustomMinimumSize = new Vector2(size, size),
            };
            m.MouseFilter = MouseFilterEnum.Ignore;
            // Retint on an accent switch: re-read tokens and repaint (tracked + freed-guarded by the factory).
            ChimeraComponents.SubscribeAccentChanged(m, _ =>
            {
                if (GodotObject.IsInstanceValid(m)) m.QueueRedraw();
            });
            return m;
        }

        /// <inheritdoc/>
        public override void _Draw()
        {
            // Reads accent tokens — only valid once the factory is bound; a stray pre-Initialize paint no-ops.
            if (!ChimeraComponents.IsInitialized) return;
            if (_triad) DrawTriad();
            else DrawSeal();
        }

        // Full seal (viewBox 96): two rings, fire+water triangles, nucleus, three vertex nodes, + accent glow.
        private void DrawSeal()
        {
            float s = Size.X / ComponentMetrics.SealViewBox;
            Vector2 P(float x, float y) => new Vector2(x * s, y * s);
            var center = P(48, 48);

            Color accent = ChimeraComponents.Col(ThemeTokens.Accent);
            Color dim = ChimeraComponents.Col(ThemeTokens.AccentDim);
            Color bright = ChimeraComponents.Col(ThemeTokens.AccentBright);
            Color glow = ChimeraComponents.Col(ThemeTokens.AccentGlow);

            // Accent glow behind the nucleus (the accent_glow token, live — the thing a baked texture can't do).
            DrawCircle(center, 7.2f * s * 2.4f, glow);

            // Two concentric rings: working circle (accent r42 sw2.8) + inner guide (accent-dim r33 sw2).
            DrawArc(center, 42f * s, 0f, Mathf.Tau, 72, accent, 2.8f * s, true);
            DrawArc(center, 33f * s, 0f, Mathf.Tau, 64, dim, 2f * s, true);

            // Fire up-triangle (dominant, accent sw2.8) + ghosted water down-triangle (accent-dim sw2).
            Vector2[] fire = { P(48, 16), P(75.72f, 64), P(20.28f, 64) };
            Vector2[] water = { P(48, 80), P(20.28f, 32), P(75.72f, 32) };
            DrawClosedTri(fire, accent, 2.8f * s);
            DrawClosedTri(water, dim, 2f * s);

            // Nucleus + three vertex anchor nodes at the fire triangle's corners.
            DrawCircle(center, 7.2f * s, accent);
            foreach (var v in fire) DrawCircle(v, 3.8f * s, bright);
        }

        // Heavy-stroke small variant (viewBox 48): ring + fire triangle + nucleus (+ glow); no inner/water/nodes.
        private void DrawTriad()
        {
            float s = Size.X / ComponentMetrics.TriadViewBox;
            Vector2 P(float x, float y) => new Vector2(x * s, y * s);
            var center = P(24, 24);

            Color accent = ChimeraComponents.Col(ThemeTokens.Accent);
            Color glow = ChimeraComponents.Col(ThemeTokens.AccentGlow);

            DrawCircle(center, 4.4f * s * 2.4f, glow);
            DrawArc(center, 20f * s, 0f, Mathf.Tau, 56, accent, 2.6f * s, true);
            DrawClosedTri(new[] { P(24, 9.5f), P(36.5f, 31), P(11.5f, 31) }, accent, 2.6f * s);
            DrawCircle(center, 4.4f * s, accent);
        }

        // A closed triangle outline (miter-ish via a repeated first vertex on the polyline).
        private void DrawClosedTri(Vector2[] tri, Color color, float width)
        {
            DrawPolyline(new[] { tri[0], tri[1], tri[2], tri[0] }, color, width, true);
        }
    }
}
