#nullable enable
using Godot;
using ProjectChimera.UI.Theme; // ThemeTokens, ThemeBuilder
using GodotTheme = Godot.Theme; // the ProjectChimera.UI.Theme namespace shadows the bare Theme type

namespace ProjectChimera.UI
{
    /// <summary>
    /// Story 11.1 — the staged loading screen. A topmost <see cref="CanvasLayer"/> shown while the boot phase runner
    /// applies the launched skirmish scenario; it renders the map name plus a "Loading… &lt;phase&gt; (i/N)" line driven
    /// by the REAL per-phase progress seam on <c>ScenePhaseRunner</c> (see <see cref="OnPhaseStarting"/>). Presentation
    /// only — it never touches sim state. Freed by <c>MainScene</c> the moment Play begins.
    ///
    /// <para>Deliberately dependency-light (plain Godot controls + the shared Theme's colors/fonts, no ChimeraComponents
    /// factory) so it is safe to stand up at the fragile start of boot, before the UI kit is guaranteed initialized.</para>
    /// </summary>
    public partial class LoadingScreenOverlay : CanvasLayer
    {
        private GodotTheme _theme = null!;
        private Label _titleLabel = null!;
        private Label _phaseLabel = null!;

        /// <summary>Build the overlay and set the map name shown while loading.</summary>
        /// <param name="mapName">The launching map's display name (shown large).</param>
        public void Initialize(string mapName)
        {
            Layer = 40; // above the main menu (20) and every in-scene overlay

            _theme = ResourceLoader.Load<GodotTheme>(ThemeBuilder.ThemePath, cacheMode: ResourceLoader.CacheMode.Ignore)
                     ?? ThemeBuilder.Build();

            var root = new Control();
            root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            root.MouseFilter = Control.MouseFilterEnum.Stop; // eat clicks during the load
            AddChild(root);

            var backdrop = new ColorRect { Color = _theme.GetColor(ThemeTokens.SurfaceVoid, ThemeTokens.Type) };
            backdrop.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            backdrop.MouseFilter = Control.MouseFilterEnum.Ignore;
            root.AddChild(backdrop);

            var center = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
            center.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            root.AddChild(center);

            var col = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
            col.AddThemeConstantOverride("separation", 16);
            center.AddChild(col);

            _titleLabel = new Label { Text = string.IsNullOrEmpty(mapName) ? "Loading" : mapName };
            _titleLabel.HorizontalAlignment = HorizontalAlignment.Center;
            _titleLabel.AddThemeFontOverride("font", _theme.GetFont(ThemeTokens.FontDisplay, ThemeTokens.Type));
            _titleLabel.AddThemeFontSizeOverride("font_size", _theme.GetFontSize(ThemeTokens.T3xl, ThemeTokens.Type));
            _titleLabel.AddThemeColorOverride("font_color", _theme.GetColor(ThemeTokens.TextHi, ThemeTokens.Type));
            col.AddChild(_titleLabel);

            _phaseLabel = new Label { Text = "Loading…" };
            _phaseLabel.HorizontalAlignment = HorizontalAlignment.Center;
            _phaseLabel.AddThemeFontOverride("font", _theme.GetFont(ThemeTokens.FontMono, ThemeTokens.Type));
            _phaseLabel.AddThemeFontSizeOverride("font_size", _theme.GetFontSize(ThemeTokens.Tmd, ThemeTokens.Type));
            _phaseLabel.AddThemeColorOverride("font_color", _theme.GetColor(ThemeTokens.TextMid, ThemeTokens.Type));
            col.AddChild(_phaseLabel);
        }

        /// <summary>The <c>ScenePhaseRunner</c> progress seam callback — invoked once per phase, in canonical order,
        /// immediately before each phase runs. Updates the "Loading… &lt;phase&gt; (i/N)" line.</summary>
        public void OnPhaseStarting(int index, int total, string phaseName)
        {
            if (_phaseLabel != null)
                _phaseLabel.Text = $"Loading… {phaseName} ({index}/{total})";
        }
    }
}
