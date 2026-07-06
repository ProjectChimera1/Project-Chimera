#nullable enable
using System;
using System.Collections.Generic;
using Godot;
using ProjectChimera.UI.Theme;

namespace ProjectChimera.UI.Components
{
    /// <summary>
    /// The Story 3.1b simple-component kit: a static factory that builds the 13 reusable, Theme-styled
    /// controls (panel, btn, icon-btn, kbd, chip, readout, tag, progress, slider, input, tabs, list-row,
    /// num-input) every later editor (3.3–3.7) and the shell (3.11) compose from. This is the "simple"
    /// half of the kit — the composite/feedback components (menu/tooltip/dialog/toast/…) are Story 3.1c.
    ///
    /// DELIVERY MECHANISM (Story 3.1b decision D-1): per-instance styling read from the loaded
    /// <c>main.tres</c> via <see cref="ThemeTokens"/> constants — the exact pattern
    /// <see cref="ThemePreview"/> proves. <c>main.tres</c> / <see cref="ThemeTokens"/> /
    /// <see cref="ThemeBuilder"/> are NOT modified for styling; the only 3.1a-code edits are the two
    /// assigned deferred fixes (Chamfer clamp, AccentController lifecycle+signal). Every color is
    /// <c>GetThemeColor(token,"Chimera")</c>, every global cut/spacing a <c>GetThemeConstant</c>, every
    /// font a <c>GetThemeFont</c>/<c>Size</c> — nothing is invented, so "styled only from the Theme"
    /// (AC1/AC2) holds. Component-intrinsic dims that are not tokens live in <see cref="ComponentMetrics"/>.
    ///
    /// CONTEXT: the factory binds once to the loaded theme + the app's single <see cref="AccentController"/>
    /// via <see cref="Initialize"/>. There is one canonical <c>main.tres</c> and one live accent for the
    /// whole UI, so a static context (not per-call plumbing) is the right shape; 3.11 initializes it at
    /// startup, the 3.1b proof scene per-scene.
    ///
    /// Presentation layer. <c>Godot.Theme</c> is written fully-qualified throughout: the
    /// <c>using ProjectChimera.UI.Theme;</c> brings the namespace into scope, which shadows the bare type
    /// <c>Godot.Theme</c>.
    /// </summary>
    public static partial class ChimeraComponents
    {
        // ── Variant / size vocabularies (a caller passes an enum, not a CSS class string) ──

        /// <summary>btn (UX-DR14) style variants.</summary>
        public enum ButtonVariant { Primary, Secondary, Ghost, Danger }

        /// <summary>btn (UX-DR14) size variants.</summary>
        public enum ButtonSize { Sm, Default, Lg, Block }

        /// <summary>panel (UX-DR13) variants.</summary>
        public enum PanelVariant { Default, Surface2, Flat, Accent }

        /// <summary>tag (UX-DR19) variants: tinted-bg + colored-text pairs.</summary>
        public enum TagVariant { Neutral, Lock, Ok, Accent, Danger }

        /// <summary>progress (UX-DR20) variants.</summary>
        public enum ProgressVariant { Default, Ok, Xp }

        /// <summary>tabs (UX-DR24) variants.</summary>
        public enum TabsVariant { Underline, Boxed, Segment }

        // ── Context (bound once via Initialize) ──

        private static Godot.Theme? _theme;
        private static AccentController? _accent;

        // Shared accent-stylebox cache (D-4). Keyed by a stable per-(variant, state) string so N primary
        // buttons register ONE box with the controller — the registry stays bounded as the kit scales.
        private static readonly Dictionary<string, StyleBoxFlat> _accentBoxCache = new();

        // Cached tracked-display FontVariations (UX-DR7 "display + tracking"), keyed by glyph spacing px.
        private static readonly Dictionary<int, FontVariation> _trackedDisplay = new();

        // Accent-bound text/icon subscriptions (D-3), each paired with its target node. Freed targets are
        // pruned on every accent switch AND opportunistically on every new bind (TrackHandler) — the
        // text/icon analog of the D-4 stylebox registry, so the list stays bounded to live controls even
        // under high-churn per-show binders (tooltip term / default-toast bar) between switches (3.1b + 3.1c).
        private static readonly List<(AccentController.AccentChangedEventHandler Handler, GodotObject Target)> _accentColorHandlers = new();

        /// <summary>True once <see cref="Initialize"/> has bound a theme + a LIVE accent controller. Uses
        /// <see cref="GodotObject.IsInstanceValid"/> (not a plain null check) so a controller freed by a scene
        /// reload reads as un-initialized and the next in-scene consumer re-initializes the factory instead of
        /// binding to the freed node. (Story 3.3 review — closes the 3.1c-deferred reload-safety root.)</summary>
        public static bool IsInitialized => _theme != null && GodotObject.IsInstanceValid(_accent);

        /// <summary>
        /// Bind the factory to the loaded canonical theme and the app's single accent controller. Call
        /// once (idempotent — re-initializing rebinds and drops the shared caches so boxes rebuild from
        /// the new theme). The 3.1b proof scene and 3.11 shell both call this after loading <c>main.tres</c>.
        /// </summary>
        public static void Initialize(Godot.Theme theme, AccentController accent)
        {
            // Cleanly tear down any prior binding FIRST — Reset() unsubscribes the tracked handlers + the
            // prune hook from the OLD controller and clears its registry, so re-initializing (even on the
            // SAME live controller) never orphans a subscription (3.1b review). Reset() also drops the
            // shared caches, so boxes rebuild from the new theme.
            Reset();
            _theme = theme;
            _accent = accent;
            // One persistent, non-tracked subscription that prunes freed targets on every accent switch.
            accent.AccentChanged += OnAccentChangedPrune;
        }

        /// <summary>
        /// Tear down the factory's shared state (D-4): unsubscribe every accent-bound text/icon handler,
        /// drop the shared accent-stylebox cache, and clear the controller's registry. Call on a full UI
        /// teardown / scene swap so nothing leaks across the boundary.
        /// </summary>
        public static void Reset()
        {
            // IsInstanceValid (not a plain != null): after a scene reload the static _accent can reference a
            // FREED controller whose C# wrapper is still non-null — unsubscribing / Clear() on it throws
            // ObjectDisposedException. Skip the controller teardown when it's freed, but still drop the lists /
            // caches below unconditionally so a re-Initialize rebinds cleanly. (Story 3.3 review.)
            if (_accent != null && GodotObject.IsInstanceValid(_accent))
            {
                _accent.AccentChanged -= OnAccentChangedPrune;
                foreach (var (handler, _) in _accentColorHandlers)
                    _accent.AccentChanged -= handler;
                _accent.Clear();
            }
            _accentColorHandlers.Clear();
            _accentBoxCache.Clear();
            _trackedDisplay.Clear();
        }

        // Subscribe an accent handler AND track its target so a later switch can prune it once freed.
        private static void TrackHandler(GodotObject target, AccentController.AccentChangedEventHandler handler)
        {
            // Opportunistically drop already-freed entries before adding, so high-churn per-show binders
            // (tooltip term / default-toast bar+icon, rebound on every reveal) can't grow the registry
            // unbounded between accent switches — the "bounded to live controls" invariant then holds at all
            // times, not only right after a SwitchAccent. Binds are off the hot path (on show, not per-frame),
            // so the O(live) scan is cheap. (3.1c review — the per-show-binder turn of the accent seam.)
            PruneAccentHandlers();
            Accent.AccentChanged += handler;
            _accentColorHandlers.Add((handler, target));
        }

        // Persistent prune hook (subscribed in Initialize, dropped in Reset): on every accent switch, drop
        // handlers whose target Control/CanvasItem was freed — unsubscribing them from the controller and
        // removing them from the list. Bounds the registry to live accent-bound controls (3.1b review).
        // Disconnecting mid-emit is safe: Godot iterates a snapshot of the connection list per emission.
        private static void OnAccentChangedPrune(string _) => PruneAccentHandlers();

        private static void PruneAccentHandlers()
        {
            if (_accent == null) return;
            for (int i = _accentColorHandlers.Count - 1; i >= 0; i--)
            {
                if (!GodotObject.IsInstanceValid(_accentColorHandlers[i].Target))
                {
                    _accent.AccentChanged -= _accentColorHandlers[i].Handler;
                    _accentColorHandlers.RemoveAt(i);
                }
            }
        }

        // ── Theme accessors (fail fast with a clear message if the factory was not initialized) ──

        private static Godot.Theme Theme => _theme
            ?? throw new InvalidOperationException(
                "ChimeraComponents.Initialize(theme, accent) must be called before building components.");

        private static AccentController Accent => _accent
            ?? throw new InvalidOperationException(
                "ChimeraComponents.Initialize(theme, accent) must be called before building components.");

        /// <summary>Read a Color token from the canonical theme (type "Chimera").</summary>
        internal static Color Col(StringName token) => Theme.GetColor(token, ThemeTokens.Type);

        /// <summary>Read an int constant token (spacing / global cut / speed) from the theme.</summary>
        internal static int Const(StringName token) => Theme.GetConstant(token, ThemeTokens.Type);

        /// <summary>Read a font role from the theme.</summary>
        internal static Font FontOf(StringName token) => Theme.GetFont(token, ThemeTokens.Type);

        /// <summary>Read a font-size token from the theme.</summary>
        internal static int SizeOf(StringName token) => Theme.GetFontSize(token, ThemeTokens.Type);

        /// <summary>
        /// The display font (Chakra Petch) with OpenType-free glyph tracking applied via a cached
        /// <see cref="FontVariation"/> (Godot 4 has no per-Control letter-spacing; FontVariation glyph
        /// spacing is the engine-correct way). Pass the CSS tracking already converted to px (≈ em×size,
        /// rounded); <c>0</c> returns the untracked display font.
        /// </summary>
        internal static Font DisplayTracked(int spacingPx)
        {
            if (spacingPx <= 0) return FontOf(ThemeTokens.FontDisplay);
            if (_trackedDisplay.TryGetValue(spacingPx, out var fv)) return fv;
            fv = new FontVariation { BaseFont = FontOf(ThemeTokens.FontDisplay) };
            fv.SetSpacing(TextServer.SpacingType.Glyph, spacingPx);
            _trackedDisplay[spacingPx] = fv;
            return fv;
        }

        /// <summary>
        /// JetBrains Mono with tabular figures (UX-DR34) AND 700 weight — the readout / num-input value
        /// face. Built on the RAW mono (<see cref="ThemeTokens.FontMono"/>) so both the <c>tnum</c> feature
        /// and the <c>wght</c> variation axis apply (JetBrains Mono is a variable font). Built at
        /// construction time only (never per-frame), so a fresh instance per call is negligible.
        /// </summary>
        internal static Font MonoTnumBold()
        {
            var ts = TextServerManager.GetPrimaryInterface();
            var fv = new FontVariation
            {
                BaseFont = FontOf(ThemeTokens.FontMono),
                OpentypeFeatures = new Godot.Collections.Dictionary { { ts.NameToTag("tnum"), 1 } },
            };
            fv.SetVariationOpentype(new Godot.Collections.Dictionary { { ts.NameToTag("wght"), 700 } });
            return fv;
        }

        // ── Shared accent stylebox (D-4) ──

        /// <summary>One accent binding on a shared box: which stylebox property mirrors which accent token.</summary>
        internal readonly record struct AccentBind(AccentController.AccentProperty Property, StringName Token);

        /// <summary>Fill-tracks-<c>accent</c> bind shorthand.</summary>
        internal static AccentBind Fill(StringName token) => new(AccentController.AccentProperty.Fill, token);

        /// <summary>Border-tracks-token bind shorthand.</summary>
        internal static AccentBind Border(StringName token) => new(AccentController.AccentProperty.Border, token);

        /// <summary>
        /// Get (or build once and cache) a shared accent-tinted stylebox for <paramref name="key"/>. The
        /// first call builds it and registers each accent binding with the controller; every later call
        /// with the same key returns the SAME instance, so the controller's registry stays bounded no
        /// matter how many components share the look (D-4). <paramref name="key"/> must be globally unique
        /// per (component, variant, state).
        /// </summary>
        internal static StyleBoxFlat SharedAccentBox(string key, Func<StyleBoxFlat> build, params AccentBind[] binds)
        {
            if (_accentBoxCache.TryGetValue(key, out var cached)) return cached;
            var box = build();
            foreach (var b in binds)
                Accent.RegisterAccentBox(box, b.Property, b.Token);
            _accentBoxCache[key] = box;
            return box;
        }

        // ── Accent-bound text/icon colors (D-3) — the seam stylebox retinting can't reach ──

        /// <summary>
        /// Bind a control's theme Color override (default <c>font_color</c>) to a specific accent token so
        /// it re-reads the new value on every <see cref="AccentController.SwitchAccent"/>. Applies now and
        /// on <c>AccentChanged</c>. Use for accent-colored text (btn-primary ink, tab active label, tag
        /// --accent text). Guards against use-after-free if the control is freed before <see cref="Reset"/>.
        /// </summary>
        internal static void BindAccentColor(Control ctrl, StringName accentToken, string colorItem = "font_color")
        {
            void Apply()
            {
                if (GodotObject.IsInstanceValid(ctrl))
                    ctrl.AddThemeColorOverride(colorItem, Col(accentToken));
            }
            AccentController.AccentChangedEventHandler handler = _ => Apply();
            Apply();
            TrackHandler(ctrl, handler);
        }

        /// <summary>
        /// Bind a CanvasItem's <c>Modulate</c> to an accent token (for a glyph/icon that must retint on an
        /// accent switch). Applies now and on <c>AccentChanged</c>; use-after-free guarded.
        /// </summary>
        internal static void BindAccentModulate(CanvasItem item, StringName accentToken)
        {
            void Apply()
            {
                if (GodotObject.IsInstanceValid(item))
                    item.Modulate = Col(accentToken);
            }
            AccentController.AccentChangedEventHandler handler = _ => Apply();
            Apply();
            TrackHandler(item, handler);
        }

        // ── Small shared helpers ──

        /// <summary>CSS <c>text-transform: uppercase</c> — Godot has no per-Control transform, so uppercase
        /// the string. Invariant culture (UI chrome, not localized content).</summary>
        internal static string Up(string text) => text.ToUpperInvariant();
    }
}
