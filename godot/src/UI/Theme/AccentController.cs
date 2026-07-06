#nullable enable
using System.Collections.Generic;
using Godot;

namespace ProjectChimera.UI.Theme
{
    /// <summary>
    /// The UX-DR4 accent-switch mechanism as working code (Story 3.1a decision D-3).
    ///
    /// There is ONE live <c>main.tres</c>. <see cref="SwitchAccent"/> rewrites the 6 accent
    /// <see cref="Color"/> entries (<c>accent</c>, <c>accent-bright</c>, <c>accent-dim</c>,
    /// <c>accent-ink</c>, <c>accent-glow</c>, <c>accent-wash</c>) on that theme in a loop. Each
    /// <c>SetColor</c> emits <c>changed</c> → NOTIFICATION_THEME_CHANGED cascades a repaint down every
    /// Control using the theme; the connection is CONNECT_DEFERRED, so the 6-call loop coalesces into a
    /// single end-of-frame repaint.
    ///
    /// ⚠ The seam that silently breaks (see DESIGN-DECISIONS.md D-3): an accent-tinted
    /// <see cref="StyleBoxFlat"/> gets its fill/border from <c>BgColor</c>/<c>BorderColor</c> —
    /// sub-resource properties, NOT theme Color tokens — so they do NOT follow the accent Color entry.
    /// Register such styleboxes here and this controller rewrites them in the same switch (mutating a
    /// StyleBox also emits <c>changed</c> and rides the same repaint). Each registration binds a stylebox
    /// property to a SPECIFIC accent token via <see cref="RegisterAccentBox"/>, so a hover surface can
    /// track <c>accent_bright</c> and a pressed surface <c>accent_dim</c> — not just the base
    /// <c>accent</c>. <see cref="RegisterAccentFill"/> / <see cref="RegisterAccentBorder"/> are
    /// base-<c>accent</c> conveniences over that general path.
    ///
    /// Presentation layer node. <c>Godot.Theme</c> is fully qualified (the enclosing namespace shadows
    /// the bare type name).
    /// </summary>
    public partial class AccentController : Node
    {
        /// <summary>Which <see cref="StyleBoxFlat"/> color property an accent binding drives.</summary>
        public enum AccentProperty { Fill, Border }

        /// <summary>One tracked stylebox: which property mirrors which accent token.</summary>
        private readonly record struct AccentBinding(StyleBoxFlat Box, AccentProperty Property, StringName Token);

        /// <summary>
        /// Fired at the end of every successful <see cref="SwitchAccent"/>, carrying the new accent name
        /// (Story 3.1b D-3). Registered STYLEBOXES are already retinted inside the switch; this signal is
        /// for the OTHER accent seam — accent-bound TEXT/ICON colors (a Label <c>font_color</c>, a
        /// TextureRect <c>modulate</c>) are baked values that do NOT follow the theme's accent Color
        /// tokens. Subscribers re-read their token here so the whole kit retints in one op (AC4).
        /// </summary>
        [Signal]
        public delegate void AccentChangedEventHandler(string accentName);

        private Godot.Theme? _theme;

        // Accent-tinted styleboxes whose colors must be rewritten in lock-step with the accent tokens.
        // Each binding names the exact accent token it mirrors (base accent OR any of the 5 variants).
        private readonly List<AccentBinding> _accentBoxes = new();

        /// <summary>The currently applied accent name (default = teal).</summary>
        public string CurrentAccent { get; private set; } = ThemeTokens.DefaultAccent;

        /// <summary>Bind the controller to the live theme it mutates. Call once after loading the theme.</summary>
        public void Initialize(Godot.Theme theme) => _theme = theme;

        /// <summary>Register a stylebox whose <c>BgColor</c> tracks the base <c>accent</c> token.</summary>
        public void RegisterAccentFill(StyleBoxFlat box) => RegisterAccentBox(box, AccentProperty.Fill, ThemeTokens.Accent);

        /// <summary>Register a stylebox whose <c>BorderColor</c> tracks the base <c>accent</c> token.</summary>
        public void RegisterAccentBorder(StyleBoxFlat box) => RegisterAccentBox(box, AccentProperty.Border, ThemeTokens.Accent);

        /// <summary>
        /// Register a stylebox property to track a SPECIFIC accent token (one of
        /// <see cref="ThemeTokens.AccentTokens"/>: <c>accent</c> / <c>accent_bright</c> / <c>accent_dim</c> /
        /// <c>accent_ink</c> / <c>accent_glow</c> / <c>accent_wash</c>). On every <see cref="SwitchAccent"/>
        /// the box's chosen property is set to that token's value in the NEW palette, so hover / pressed /
        /// glow surfaces retint to the right shade — not just the base accent. Idempotent per (box, property).
        /// </summary>
        public void RegisterAccentBox(StyleBoxFlat box, AccentProperty property, StringName accentToken)
        {
            foreach (var b in _accentBoxes)
                if (b.Box == box && b.Property == property)
                    return; // already tracking this property on this box
            _accentBoxes.Add(new AccentBinding(box, property, accentToken));
        }

        /// <summary>
        /// Stop tracking a stylebox: drop ALL bindings for <paramref name="box"/> (both Fill and Border
        /// if it was registered for both). Call from component/panel teardown so the registry does not
        /// grow unbounded as UI churns (Story 3.1b D-4). Returns true if at least one binding was removed.
        /// </summary>
        public bool Unregister(StyleBoxFlat box) => _accentBoxes.RemoveAll(b => b.Box == box) > 0;

        /// <summary>Drop every accent-stylebox binding (full UI teardown / scene swap). D-4.</summary>
        public void Clear() => _accentBoxes.Clear();

        /// <summary>
        /// Switch the whole UI to the named accent (teal / amber / violet) in one operation: rewrite the
        /// 6 accent Color tokens on the live theme AND retint every registered accent stylebox to the new
        /// value of the token it was bound to. Returns false (and no-ops) if the theme is unbound or the
        /// accent name is unknown.
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

            // 2) Retint the accent-tinted styleboxes — the seam Color tokens don't cover. Each box mirrors
            //    the specific accent token it was registered against (base accent or any of the 5 variants).
            foreach (var binding in _accentBoxes)
            {
                var color = Color.FromHtml(ThemeTokens.AccentHexFor(palette, binding.Token));
                if (binding.Property == AccentProperty.Fill)
                    binding.Box.BgColor = color;
                else
                    binding.Box.BorderColor = color;
            }

            CurrentAccent = palette.Name;

            // 3) Fire the seam signal for accent-bound text/icon colors that stylebox retinting can't
            //    reach (D-3). Emitted AFTER CurrentAccent updates so a handler that reads it sees the new
            //    value. Stateless subscribers just re-read GetThemeColor(accent…).
            EmitSignal(SignalName.AccentChanged, palette.Name);
            return true;
        }
    }
}
