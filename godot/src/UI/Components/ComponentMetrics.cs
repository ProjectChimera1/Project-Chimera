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
        /// <summary>tag / readout-icon / num-input / switch-knob cut (3px).</summary>
        public const int CutMicro = 3;
        /// <summary>slider thumb / tooltip cut (4px).</summary>
        public const int CutThumb = 4;

        // ══════════════════════════════════════════════════════════════════════════════════════════════
        // Story 3.1c — composite / feedback component intrinsic dims (transcribed from chimera.css +
        // editor.css + theme.js; see the story's Per-Component Spec Table). Global cuts (cut_sm=5 /
        // cut_lg=14) are read from the Theme, NOT duplicated here — only per-component values live below.
        // ══════════════════════════════════════════════════════════════════════════════════════════════

        // ── switch (UX-DR31/54) — editor.css .switch ──
        /// <summary>switch track width (42px).</summary>
        public const int SwitchWidth = 42;
        /// <summary>switch track height (24px).</summary>
        public const int SwitchHeight = 24;
        /// <summary>switch knob is an 18×18 FACETED SQUARE (never a round dot).</summary>
        public const int SwitchKnob = 18;
        /// <summary>switch knob left offset when OFF (3px) — also its top offset (centered: (24−18)/2 = 3).</summary>
        public const int SwitchKnobInset = 3;
        /// <summary>switch knob left offset when ON (21px) — slides 3→21.</summary>
        public const int SwitchKnobOnLeft = 21;
        /// <summary>switch knob chamfer (3px). The track uses the global cut_sm (5) token.</summary>
        public const int CutSwitchKnob = CutMicro;

        // ── tooltip (UX-DR26/53) — chimera.css .tip__pop + editor.css .f-tip ──
        /// <summary>tooltip cut (4px, both roles).</summary>
        public const int CutTooltip = CutThumb;
        /// <summary>.tip__pop max-width (240px).</summary>
        public const int TooltipMaxWidthPop = 240;
        /// <summary>.f-tip fixed width (200px).</summary>
        public const int TooltipWidthField = 200;
        /// <summary>.tip__pop padding (7px vertical, 10px horizontal).</summary>
        public const int TooltipPopPadH = 10;
        /// <summary>.tip__pop padding (7px vertical).</summary>
        public const int TooltipPopPadV = 7;
        /// <summary>.f-tip padding (9px horizontal).</summary>
        public const int TooltipFieldPadH = 9;
        /// <summary>.f-tip padding (7px vertical).</summary>
        public const int TooltipFieldPadV = 7;
        /// <summary>Gap between the tooltip and the top of its target (.tip__pop bottom calc(100%+8)).</summary>
        public const int TooltipGap = 8;
        /// <summary>Tooltip rise-in offset on reveal (translateY 4→0 over speed).</summary>
        public const int TooltipRiseY = 4;
        /// <summary>Short-hover delay before a hover tooltip reveals, ms (keyboard focus reveals instantly).</summary>
        public const int TooltipHoverDelayMs = 400;
        /// <summary>.f-tip fade duration (120ms — the .tip__pop pop fade reads the speed token instead).</summary>
        public const int TooltipFieldFadeMs = 120;

        // ── menu (UX-DR23) — chimera.css .menu / .menu-item ──
        /// <summary>menu popover min-width (180px).</summary>
        public const int MenuMinWidth = 180;
        /// <summary>menu panel padding (5px).</summary>
        public const int MenuPanelPad = 5;
        /// <summary>menu-item horizontal padding (10px).</summary>
        public const int MenuItemPadH = 10;
        /// <summary>menu-item vertical padding (8px).</summary>
        public const int MenuItemPadV = 8;

        // ── dialog (UX-DR27) — chimera.css .scrim / .dialog ──
        /// <summary>dialog max-width (min(560px, 90%)). The cut is the global cut_lg (14) token.</summary>
        public const int DialogMaxWidth = 560;
        /// <summary>dialog width as a fraction of the viewport (the 90% side of min(560, 90%)).</summary>
        public const float DialogWidthPct = 0.9f;
        /// <summary>dialog head vertical padding (20px; horizontal is s5=24). Body 24=s5, foot 16=s4 read tokens.</summary>
        public const int DialogHeadPadV = 20;

        // ── toast (UX-DR28) — chimera.css .toast / .banner-stall ──
        /// <summary>toast fixed width (300px, per the .alerts stacking column).</summary>
        public const int ToastWidth = 300;
        /// <summary>toast left accent-bar width (3px full-height ::after).</summary>
        public const int ToastAccentBar = 3;
        /// <summary>toast leading icon size (22px).</summary>
        public const int ToastIcon = 22;
        /// <summary>toast horizontal padding (16px). Vertical 12px; gap s3.</summary>
        public const int ToastPadH = 16;
        /// <summary>toast vertical padding (12px).</summary>
        public const int ToastPadV = 12;
        /// <summary>toast slide-in duration (~250ms — NOT the 130ms speed token; HUD.html slideIn keyframe).</summary>
        public const int ToastSlideMs = 250;
        /// <summary>Toast stack offset from the top-left screen corner (a host-position intrinsic; equals s5=24
        /// but is a layout anchor, not the spacing token — see .alerts top/left in HUD.html).</summary>
        public const int ToastHostMargin = 24;
        /// <summary>banner-stall horizontal padding (22px). Vertical 12px.</summary>
        public const int BannerStallPadH = 22;
        /// <summary>banner-stall vertical padding (12px).</summary>
        public const int BannerStallPadV = 12;
        /// <summary>banner-stall cut (6px).</summary>
        public const int CutBannerStall = 6;
        /// <summary>banner-stall warn-tinted background alpha (oklch(.80 .15 80 / 0.14) → warn @ ~14%).</summary>
        public const float BannerStallBgAlpha = 0.14f;

        // ── spinner (UX-DR29) — chimera.css .tmute + theme.js #chimera-spin-* (viewBox 96) ──
        /// <summary>spinner small size (22px).</summary>
        public const int SpinnerSm = 22;
        /// <summary>spinner default size (48px).</summary>
        public const int SpinnerDefault = 48;
        /// <summary>spinner large size (96px).</summary>
        public const int SpinnerLg = 96;
        /// <summary>spinner ring clockwise loop (2600ms).</summary>
        public const int SpinnerRingMs = 2600;
        /// <summary>spinner triangle counter-clockwise loop (5200ms).</summary>
        public const int SpinnerTriMs = 5200;
        /// <summary>spinner core pulse loop (1300ms).</summary>
        public const int SpinnerCoreMs = 1300;

        // ── mark (UX-DR30) — theme.js #chimera-seal (viewBox 96) / #chimera-triad (viewBox 48) ──
        /// <summary>The reference viewBox the seal + spinner geometry is authored in (96×96).</summary>
        public const int SealViewBox = 96;
        /// <summary>The reference viewBox the small heavy-stroke .triad variant is authored in (48×48).</summary>
        public const int TriadViewBox = 48;
        /// <summary>At or below this pixel size the mark renders the .triad variant (≤24px).</summary>
        public const int MarkTriadThreshold = 24;
    }
}
