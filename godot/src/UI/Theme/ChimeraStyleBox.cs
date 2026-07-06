#nullable enable
using Godot;

namespace ProjectChimera.UI.Theme
{
    /// <summary>
    /// The canonical UX-DR9 chamfer recipe as working code (Story 3.1a decision D-1/D-2).
    ///
    /// The brand's faceted 45° corners come from a stock <see cref="StyleBoxFlat"/> with
    /// <c>CornerDetail = 1</c> (which turns the per-corner "radius" into a straight chamfer, NOT a curve)
    /// and <c>AntiAliasing = false</c> (crisp facet edges). The brand shape cuts <b>top-left +
    /// bottom-right</b> only, leaving TR + BL square — the distinctive low-poly diagonal from the
    /// shipped Claude Design UI (chimera.css:213). No custom StyleBox subclass, texture, or shader.
    ///
    /// This is the single place the recipe lives; 3.1b/3.1c and every editor build their chamfered
    /// surfaces from here. Presentation layer.
    /// </summary>
    public static class ChimeraStyleBox
    {
        /// <summary>
        /// Build a chamfered <see cref="StyleBoxFlat"/> with the brand's 2-corner (TL + BR) 45° cut.
        /// </summary>
        /// <param name="cut">Chamfer size in px (UX-DR9: cut 8 / cut-sm 5 / cut-lg 14).</param>
        /// <param name="bg">Fill color (a surface or accent token).</param>
        /// <param name="border">Hairline border color (an edge-light / line token).</param>
        /// <param name="borderWidth">Cel-shade hairline width in px (default 1; 0 = no border).</param>
        public static StyleBoxFlat Chamfer(int cut, Color bg, Color border, int borderWidth = 1)
        {
            // D-5 (Story 3.1b, folds 3.1a deferred #3): a negative author-supplied cut (e.g. an
            // arithmetic underflow, or a cut-lg=14 subtracted below zero) would assign a negative corner
            // radius and degenerate the facet silently. Clamp the low end here — the ONE place chamfers
            // are built. Godot already caps an oversized radius to half the box at draw time, so only the
            // floor needs a guard.
            cut = Mathf.Max(0, cut);

            var sb = new StyleBoxFlat
            {
                BgColor = bg,

                // D-2 shape: cut TL + BR, leave TR + BL square.
                CornerRadiusTopLeft     = cut,
                CornerRadiusBottomRight = cut,
                CornerRadiusTopRight    = 0,
                CornerRadiusBottomLeft  = 0,

                // D-1 mechanism: detail = 1 → straight 45° facet (NOT rounded); AA off for crisp edges.
                CornerDetail = 1,
                AntiAliasing = false,

                BorderColor      = border,
                BorderWidthTop   = borderWidth,
                BorderWidthBottom = borderWidth,
                BorderWidthLeft  = borderWidth,
                BorderWidthRight = borderWidth,
            };
            return sb;
        }

        /// <summary>
        /// Set the four <c>content_margin_*</c> properties (there is no content-margin virtual on
        /// StyleBox — content margins are plain properties). Convenience for the preview / components.
        /// </summary>
        public static StyleBoxFlat WithContentMargins(this StyleBoxFlat sb, int horizontal, int vertical)
        {
            sb.ContentMarginLeft   = horizontal;
            sb.ContentMarginRight  = horizontal;
            sb.ContentMarginTop    = vertical;
            sb.ContentMarginBottom = vertical;
            return sb;
        }

        /// <summary>
        /// Apply a UX-DR11 drop-shadow recipe to a StyleBoxFlat (size / offset / black-with-alpha).
        /// </summary>
        public static StyleBoxFlat WithShadow(this StyleBoxFlat sb, ThemeTokens.ShadowRecipe recipe)
        {
            sb.ShadowSize   = recipe.Size;
            sb.ShadowOffset = new Vector2(recipe.OffsetX, recipe.OffsetY);
            sb.ShadowColor  = new Color(0f, 0f, 0f, recipe.Alpha);
            return sb;
        }
    }
}
