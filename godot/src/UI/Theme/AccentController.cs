#nullable enable
using System.Collections.Generic;
using Godot;

namespace ProjectChimera.UI.Theme
{
    /// <summary>
    /// The UX-DR4 accent-switch mechanism as working code (Story 3.1a decision D-3).
    ///
    /// There is ONE live <c>main.theme</c>. <see cref="SwitchAccent"/> rewrites the 6 accent
    /// <see cref="Color"/> entries (<c>accent</c>, <c>accent-bright</c>, <c>accent-dim</c>,
    /// <c>accent-ink</c>, <c>accent-glow</c>, <c>accent-wash</c>) on that theme in a loop. Each
    /// <c>SetColor</c> emits <c>changed</c> → NOTIFICATION_THEME_CHANGED cascades a repaint down every
    /// Control using the theme; the connection is CONNECT_DEFERRED, so the 6-call loop coalesces into a
    /// single end-of-frame repaint.
    ///
    /// ⚠ The seam that silently breaks (see DESIGN-DECISIONS.md D-3): an accent-tinted
    /// <see cref="StyleBoxFlat"/> gets its fill/border from <c>BgColor</c>/<c>BorderColor</c> —
    /// sub-resource properties, NOT theme Color tokens — so they do NOT follow the accent Color entry.
    /// Register such styleboxes here (<see cref="RegisterAccentFill"/> / <see cref="RegisterAccentBorder"/>)
    /// and this controller rewrites them in the same switch (mutating a StyleBox also emits
    /// <c>changed</c> and rides the same repaint).
    ///
    /// Presentation layer node. <c>Godot.Theme</c> is fully qualified (the enclosing namespace shadows
    /// the bare type name).
    /// </summary>
    public partial class AccentController : Node
    {
        private Godot.Theme? _theme;

        // Accent-tinted styleboxes whose colors must be rewritten in lock-step with the accent tokens.
        private readonly List<StyleBoxFlat> _accentFillBoxes   = new();
        private readonly List<StyleBoxFlat> _accentBorderBoxes = new();

        /// <summary>The currently applied accent name (default = teal).</summary>
        public string CurrentAccent { get; private set; } = ThemeTokens.DefaultAccent;

        /// <summary>Bind the controller to the live theme it mutates. Call once after loading the theme.</summary>
        public void Initialize(Godot.Theme theme) => _theme = theme;

        /// <summary>Register a stylebox whose <c>BgColor</c> tracks the <c>accent</c> token.</summary>
        public void RegisterAccentFill(StyleBoxFlat box)
        {
            if (!_accentFillBoxes.Contains(box))
                _accentFillBoxes.Add(box);
        }

        /// <summary>Register a stylebox whose <c>BorderColor</c> tracks the <c>accent</c> token.</summary>
        public void RegisterAccentBorder(StyleBoxFlat box)
        {
            if (!_accentBorderBoxes.Contains(box))
                _accentBorderBoxes.Add(box);
        }

        /// <summary>
        /// Switch the whole UI to the named accent (teal / amber / violet) in one operation: rewrite the
        /// 6 accent Color tokens on the live theme AND retint every registered accent stylebox. Returns
        /// false (and no-ops) if the theme is unbound or the accent name is unknown.
        /// </summary>
        public bool SwitchAccent(string accentName)
        {
            if (_theme == null)
            {
                GD.PrintErr("[AccentController] SwitchAccent called before Initialize(theme).");
                return false;
            }
            if (!ThemeTokens.TryGetPalette(accentName, out var palette))
            {
                GD.PrintErr($"[AccentController] Unknown accent '{accentName}'.");
                return false;
            }

            // 1) Rewrite the 6 accent Color tokens (one coalesced repaint via CONNECT_DEFERRED).
            string[] hex = palette.HexInTokenOrder;
            for (int i = 0; i < ThemeTokens.AccentTokens.Length; i++)
                _theme.SetColor(ThemeTokens.AccentTokens[i], ThemeTokens.Type, Color.FromHtml(hex[i]));

            // 2) Retint the accent-tinted styleboxes — the seam Color tokens don't cover.
            var accentColor = Color.FromHtml(palette.Accent);
            foreach (var box in _accentFillBoxes)
                box.BgColor = accentColor;
            foreach (var box in _accentBorderBoxes)
                box.BorderColor = accentColor;

            CurrentAccent = palette.Name;
            return true;
        }
    }
}
