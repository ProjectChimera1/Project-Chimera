#nullable enable
using Godot;

namespace ProjectChimera.UI.Theme
{
    /// <summary>
    /// Assembles the canonical <c>main.tres</c> from <see cref="ThemeTokens"/> + the bundled fonts
    /// (Story 3.1a, decision D-4). This is the reproducible, reviewable source for the committed
    /// artifact: run <see cref="Build"/> → <see cref="Save"/> to regenerate the text <c>.tres</c>.
    ///
    /// Every token is stored under the one custom theme type <see cref="ThemeTokens.Type"/> ("Chimera")
    /// so components read them by name (<c>GetThemeColor("surface-1", "Chimera")</c>). Stock control
    /// styling (Panel/Button/LineEdit via type variations) is deliberately NOT done here — that is
    /// Story 3.1b. This builder authors the token vault + the fonts + the default accent (teal) only.
    ///
    /// Presentation layer. <c>Godot.Theme</c> is fully qualified throughout because the enclosing
    /// namespace <c>ProjectChimera.UI.Theme</c> shadows the bare type name <c>Theme</c>.
    /// </summary>
    public static class ThemeBuilder
    {
        /// <summary>
        /// Committed artifact path (D-5). Text <c>.tres</c> (format=3, git-diffable) — Godot maps the
        /// <c>.theme</c> extension to its BINARY saver, so the diffable text theme the Dev Notes mandate
        /// is committed as <c>.tres</c>.
        /// </summary>
        public const string ThemePath = "res://assets/ui/main.tres";

        // Bundled OFL fonts (D-6). Chakra Petch is static; Space Grotesk / JetBrains Mono are variable.
        public const string DisplayFontPath = "res://assets/ui/fonts/chakra-petch/ChakraPetch-Regular.ttf";
        public const string UiFontPath      = "res://assets/ui/fonts/space-grotesk/SpaceGrotesk-VariableFont_wght.ttf";
        public const string MonoFontPath    = "res://assets/ui/fonts/jetbrains-mono/JetBrainsMono-VariableFont_wght.ttf";

        /// <summary>
        /// Build the full token vault as an in-memory <see cref="Godot.Theme"/>. Pure — no disk writes.
        /// Fonts load via <c>GD.Load</c>; if a font is not yet imported the color/size/constant vault
        /// is still authored (fonts are logged as missing so the caller can trigger a reimport).
        /// </summary>
        public static Godot.Theme Build()
        {
            var theme = new Godot.Theme();

            // ── Fonts (UX-DR7) + default body font ──
            var display = GD.Load<FontFile>(DisplayFontPath);
            var ui      = GD.Load<FontFile>(UiFontPath);
            var mono    = GD.Load<FontFile>(MonoFontPath);

            theme.DefaultFontSize = ThemeTokens.DefaultFontSize; // t-md = 15
            if (ui != null)
                theme.DefaultFont = ui; // body = Space Grotesk

            if (display != null) theme.SetFont(ThemeTokens.FontDisplay, ThemeTokens.Type, display);
            if (ui != null)      theme.SetFont(ThemeTokens.FontUi,      ThemeTokens.Type, ui);
            if (mono != null)    theme.SetFont(ThemeTokens.FontMono,    ThemeTokens.Type, mono);

            // ── UX-DR34: mono tabular-figure role — JetBrains Mono + OpenType tnum=1 ──
            // Same FontVariation/tnum pattern as CommandCardSystem.cs:365, but based on JetBrains Mono.
            if (mono != null)
            {
                var monoTnum = new FontVariation
                {
                    BaseFont = mono,
                    OpentypeFeatures = new Godot.Collections.Dictionary
                    {
                        { TextServerManager.GetPrimaryInterface().NameToTag("tnum"), 1 },
                    },
                };
                theme.SetFont(ThemeTokens.MonoTnum, ThemeTokens.Type, monoTnum);
            }

            if (display == null || ui == null || mono == null)
                GD.PrintErr($"[ThemeBuilder] Fonts not imported yet (display={display != null}, " +
                            $"ui={ui != null}, mono={mono != null}). Reimport assets/ui/fonts, then rebuild the theme.");

            // ── Colors (UX-DR1/2/3/5/6) — non-accent ──
            foreach (var (name, hex) in ThemeTokens.ColorTokens)
                theme.SetColor(name, ThemeTokens.Type, Color.FromHtml(hex));

            // ── Default accent palette = teal (UX-DR4). Switchable at runtime by AccentController. ──
            if (!ThemeTokens.TryGetPalette(ThemeTokens.DefaultAccent, out var accent))
                GD.PrintErr($"[ThemeBuilder] DefaultAccent '{ThemeTokens.DefaultAccent}' is not a known " +
                            $"palette; baked the '{accent.Name}' fallback instead.");
            string[] accentHex = accent.HexInTokenOrder;
            for (int i = 0; i < ThemeTokens.AccentTokens.Length; i++)
                theme.SetColor(ThemeTokens.AccentTokens[i], ThemeTokens.Type, Color.FromHtml(accentHex[i]));

            // ── Type scale (UX-DR8) ──
            foreach (var (name, px) in ThemeTokens.FontSizeTokens)
                theme.SetFontSize(name, ThemeTokens.Type, px);

            // ── Constants: spacing (UX-DR10) + chamfer cuts (UX-DR9) + motion speed (UX-DR50) ──
            foreach (var (name, val) in ThemeTokens.ConstantTokens)
                theme.SetConstant(name, ThemeTokens.Type, val);

            return theme;
        }

        /// <summary>Persist the theme to the committed text artifact (<see cref="ThemePath"/>).</summary>
        public static Error Save(Godot.Theme theme) => ResourceSaver.Save(theme, ThemePath);
    }
}
