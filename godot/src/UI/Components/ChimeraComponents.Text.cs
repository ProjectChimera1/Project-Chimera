#nullable enable
using Godot;
using ProjectChimera.UI.Theme;

namespace ProjectChimera.UI.Components
{
    /// <summary>
    /// DW-23: single-sourced kit bootstrap + typography label factories. Before this partial, the
    /// <c>EnsureKitInitialized()</c> bootstrap was copy-pasted into the runtime UI consumers and private
    /// <c>Heading</c>/<c>Body</c> label factories into most of them, with real per-consumer style drift.
    /// These static helpers now own font/size/color, so that typography is single-sourced across the
    /// runtime consumers and can no longer drift per consumer. Each call site still keeps its own
    /// contextual layout flags (<c>SizeFlagsVertical</c>, <c>AutowrapMode</c>, <c>SizeFlagsHorizontal</c>),
    /// which are deliberately NOT owned here — layout is per-context, not per-typography — so a call site's
    /// produced <see cref="Label"/> matches its pre-refactor styling only when it re-applies those flags
    /// (each conversion site was checked individually for that).
    ///
    /// Scope note: the demo/proof scenes (<c>ComponentGallery</c>, <c>ComponentPreview</c>,
    /// <c>ThemePreview</c>) keep their own accent/bootstrap wiring because they drive accent switching
    /// directly; <c>ThemePreview</c> is a standalone 3.1a proof harness that never calls
    /// <see cref="Initialize"/>, so it retains its own private label helper.
    /// </summary>
    public static partial class ChimeraComponents
    {
        /// <summary>
        /// Idempotent kit bootstrap shared by every consumer (DW-23). ALWAYS loads the canonical theme
        /// fresh (<c>CacheMode.Ignore</c>, falling back to an in-memory <see cref="ThemeBuilder.Build"/>
        /// if the committed file is missing) so a caller that needs <c>_theme</c> elsewhere (e.g.
        /// <c>panel.Theme = _theme</c>) always gets it. The one-time factory bootstrap — create the single
        /// <see cref="AccentController"/>, parent it to <paramref name="owner"/>, and call
        /// <see cref="Initialize"/> — runs ONLY when <see cref="IsInitialized"/> is false, so a later
        /// consumer is a clean no-op. Returns the loaded theme.
        /// </summary>
        /// <param name="owner">The node the single app-wide <see cref="AccentController"/> is parented to on
        /// first init. It SHOULD outlive the UI session (a stable overlay / scene root / phase scene, not a
        /// closable transient panel): the accent is app-wide and freeing its owner invalidates it for every
        /// consumer until the next <see cref="EnsureInitialized"/> re-bootstraps. Must be non-null.</param>
        public static Godot.Theme EnsureInitialized(Node owner)
        {
            System.ArgumentNullException.ThrowIfNull(owner);
            var theme = ResourceLoader.Load<Godot.Theme>(ThemeBuilder.ThemePath, cacheMode: ResourceLoader.CacheMode.Ignore)
                        ?? ThemeBuilder.Build();
            if (!IsInitialized)
            {
                var accent = new AccentController { Name = "AccentController" };
                owner.AddChild(accent);
                accent.Initialize(theme);
                Initialize(theme, accent);
            }
            return theme;
        }

        /// <summary>
        /// Display-font heading label: overrides <c>font</c>=<c>FontDisplay</c>,
        /// <c>font_size</c>=<c>SizeOf(sizeToken)</c>, <c>font_color</c>=<c>TextHi</c>. Layout is contextual
        /// and stays at the call site. Legacy 1-arg <c>Heading(text)</c> callers pass their hardcoded size
        /// (<c>ThemeTokens.Tlg</c>) explicitly.
        /// </summary>
        public static Label Heading(string text, StringName sizeToken)
        {
            var l = new Label { Text = text };
            l.AddThemeFontOverride("font", FontOf(ThemeTokens.FontDisplay));
            l.AddThemeFontSizeOverride("font_size", SizeOf(sizeToken));
            l.AddThemeColorOverride("font_color", Col(ThemeTokens.TextHi));
            return l;
        }

        /// <summary>
        /// Body label. ALWAYS overrides <c>font_color</c> with <paramref name="colorToken"/>. When
        /// <paramref name="sizeToken"/> is non-null it ALSO overrides <c>font</c>=<c>FontUi</c> and
        /// <c>font_size</c>=<c>SizeOf(sizeToken)</c> (the 3-arg font+size form); when null it applies
        /// neither, so the theme's default font is inherited (the 2-arg color-only form). Layout flags are
        /// contextual and stay at the call site.
        /// </summary>
        public static Label Body(string text, StringName colorToken, StringName? sizeToken = null)
        {
            var l = new Label { Text = text };
            if (sizeToken != null)
            {
                l.AddThemeFontOverride("font", FontOf(ThemeTokens.FontUi));
                l.AddThemeFontSizeOverride("font_size", SizeOf(sizeToken));
            }
            l.AddThemeColorOverride("font_color", Col(colorToken));
            return l;
        }
    }
}
