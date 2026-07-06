#nullable enable

namespace ProjectChimera.UI.Components
{
    /// <summary>
    /// Component-intrinsic dimensions the Story 3.1b kit needs that are NOT global Theme tokens
    /// (AC2). Per the 3.1a token table these were pre-declared as "per-component extras used later in
    /// 3.1b/c" — sizes and micro-chamfers that belong to one component's anatomy, not the shared scale.
    /// Every value here is transcribed from the shipped Claude Design mock (<c>chimera.css</c>); naming
    /// them as documented constants is what keeps the factory free of magic numbers.
    ///
    /// The three GLOBAL chamfer cuts (8 / 5 / 14) live in the Theme as <c>cut</c> / <c>cut_sm</c> /
    /// <c>cut_lg</c> and must be read from there — they are NOT duplicated here. Only the per-component
    /// micro-cuts (2 / 3 / 4) that the CSS hardcodes per element are constants.
    ///
    /// Presentation layer.
    /// </summary>
    public static class ComponentMetrics
    {
        // ── icon-btn (UX-DR15) ──
        /// <summary>icon-btn is a fixed 36×36 square (chimera.css .icon-btn width/height).</summary>
        public const int IconBtnSize = 36;
        /// <summary>The centered glyph size inside an icon-btn (18px).</summary>
        public const int IconBtnGlyph = 18;

        // ── readout (UX-DR18) ──
        /// <summary>The faceted icon plate on a readout is 22×22.</summary>
        public const int ReadoutIconSize = 22;

        // ── progress (UX-DR20) ──
        /// <summary>Progress track height (8px bar).</summary>
        public const int ProgressTrackHeight = 8;

        // ── slider (UX-DR21) ──
        /// <summary>Slider track height (6px).</summary>
        public const int SliderTrackHeight = 6;
        /// <summary>Slider thumb width (14px).</summary>
        public const int SliderThumbWidth = 14;
        /// <summary>Slider thumb height (18px).</summary>
        public const int SliderThumbHeight = 18;

        // ── kbd (UX-DR16) — the SOLE rounded element ──
        /// <summary>kbd corner radius (3px, ROUND — the one non-chamfer corner in the whole kit).</summary>
        public const int KbdRadius = 3;
        /// <summary>kbd bottom-border width (2px, the "keycap" lip).</summary>
        public const int KbdBottomBorder = 2;
        /// <summary>kbd minimum width (18px) so single keys stay square-ish.</summary>
        public const int KbdMinWidth = 18;

        // ── num-input (UX-DR32) ──
        /// <summary>num-input fixed width (64px, right-aligned mono).</summary>
        public const int NumInputWidth = 64;

        // ── Per-component micro-chamfers (NOT global cut tokens; hardcoded px in the CSS) ──
        /// <summary>progress track cut (2px).</summary>
        public const int CutProgress = 2;
        /// <summary>tag / readout-icon / num-input cut (3px).</summary>
        public const int CutMicro = 3;
        /// <summary>slider thumb cut (4px).</summary>
        public const int CutThumb = 4;
    }
}
