#nullable enable
using System.Collections.Generic;
using Godot;
using ProjectChimera.UI.Theme;

namespace ProjectChimera.UI.Components
{
    /// <summary>
    /// tabs (UX-DR24) as a thin stateful component (Story 3.1b D-2). Three variants:
    ///   • <b>underline</b> — active tab gets an accent underline bar + accent-bright label;
    ///   • <b>boxed</b> — active tab sits in a surface-2 chamfered box;
    ///   • <b>segment</b> — a pill group (the Simple/Advanced disclosure toggle 3.4+ needs): the active
    ///     segment is an accent fill with accent-ink text.
    /// Tracks the active index and emits <see cref="TabChanged"/>. Accent surfaces are shared + registered
    /// (auto-retint); the active label re-reads its accent token on a switch via a tracked subscription,
    /// so the whole component follows the accent in one op (AC4).
    ///
    /// Presentation layer; reads styling through the <see cref="ChimeraComponents"/> factory.
    /// </summary>
    public partial class ChimeraTabs : PanelContainer
    {
        /// <summary>Emitted with the newly-active tab index.</summary>
        [Signal]
        public delegate void TabChangedEventHandler(int index);

        private readonly List<Button> _tabs = new();
        private ChimeraComponents.TabsVariant _variant;
        private int _active;

        private StyleBoxFlat _activeBox = null!;   // underline bar / boxed box / segment fill (per variant)
        private StyleBoxFlat _inactiveBox = null!; // transparent, matching margins (no layout shift)

        /// <summary>The active tab index.</summary>
        public int Active => _active;

        /// <summary>Build a tabs component of the given variant over the labels; first tab active.</summary>
        public static ChimeraTabs Create(ChimeraComponents.TabsVariant variant, params string[] labels)
        {
            var t = new ChimeraTabs();
            t.Build(variant, labels);
            return t;
        }

        private void Build(ChimeraComponents.TabsVariant variant, string[] labels)
        {
            _variant = variant;
            int cutSm = ChimeraComponents.Const(ThemeTokens.CutSm);
            int padH = ChimeraComponents.Const(ThemeTokens.S3);
            int padV = ChimeraComponents.Const(ThemeTokens.S2);

            // Outer: segment draws a surface-2 chamfered group box; underline/boxed are frameless.
            var content = new HBoxContainer();
            if (variant == ChimeraComponents.TabsVariant.Segment)
            {
                var outer = ChimeraStyleBox.Chamfer(cutSm, ChimeraComponents.Col(ThemeTokens.Surface2), ChimeraComponents.Col(ThemeTokens.Line));
                outer.WithContentMargins(4, 4);
                AddThemeStyleboxOverride("panel", outer);
                content.AddThemeConstantOverride("separation", 4);
            }
            else
            {
                AddThemeStyleboxOverride("panel", new StyleBoxEmpty());
                content.AddThemeConstantOverride("separation", padV);
            }
            AddChild(content);

            // Active/inactive state boxes per variant (built once; the accent ones shared + registered).
            switch (variant)
            {
                case ChimeraComponents.TabsVariant.Underline:
                    _activeBox = ChimeraComponents.SharedAccentBox("tabs/underline/active", () =>
                    {
                        var b = new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0), BorderColor = ChimeraComponents.Col(ThemeTokens.Accent), BorderWidthBottom = 2 };
                        b.WithContentMargins(padH, padV);
                        return b;
                    }, ChimeraComponents.Border(ThemeTokens.Accent));
                    break;
                case ChimeraComponents.TabsVariant.Boxed:
                    _activeBox = ChimeraStyleBox.Chamfer(cutSm, ChimeraComponents.Col(ThemeTokens.Surface2), ChimeraComponents.Col(ThemeTokens.Line));
                    _activeBox.WithContentMargins(padH, padV);
                    break;
                default: // Segment: accent fill (inner cut 4), accent-ink text
                    _activeBox = ChimeraComponents.SharedAccentBox("tabs/segment/active", () =>
                    {
                        var b = ChimeraStyleBox.Chamfer(ComponentMetrics.CutThumb, ChimeraComponents.Col(ThemeTokens.Accent), ChimeraComponents.Col(ThemeTokens.Accent), 0);
                        b.WithContentMargins(padH, padV);
                        return b;
                    }, ChimeraComponents.Fill(ThemeTokens.Accent), ChimeraComponents.Border(ThemeTokens.Accent));
                    break;
            }

            _inactiveBox = new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0) };
            _inactiveBox.WithContentMargins(padH, padV);

            for (int i = 0; i < labels.Length; i++)
            {
                int idx = i;
                var tab = new Button { Text = variant == ChimeraComponents.TabsVariant.Segment ? labels[i] : ChimeraComponents.Up(labels[i]) };
                tab.AddThemeFontOverride("font", ChimeraComponents.DisplayTracked(1));
                tab.AddThemeFontSizeOverride("font_size", ChimeraComponents.SizeOf(ThemeTokens.Tsm));
                tab.FocusMode = FocusModeEnum.None; // tabs manage their own selection, no focus ring
                tab.Pressed += () => SetActive(idx);
                _tabs.Add(tab);
                content.AddChild(tab);
            }

            // Re-style the active tab's accent-bound text on an accent switch (registered boxes self-retint).
            ChimeraComponents.SubscribeAccentChanged(_ =>
            {
                if (GodotObject.IsInstanceValid(this)) Restyle();
            });

            SetActive(0);
        }

        /// <summary>Switch the active tab, restyle, and emit <see cref="TabChanged"/>.</summary>
        public void SetActive(int index)
        {
            if (index < 0 || index >= _tabs.Count) return;
            _active = index;
            Restyle();
            EmitSignal(SignalName.TabChanged, index);
        }

        // Apply active/inactive styling to every tab (re-reads accent tokens fresh so a switch retints).
        private void Restyle()
        {
            for (int i = 0; i < _tabs.Count; i++)
            {
                bool active = i == _active;
                var tab = _tabs[i];
                var box = active ? _activeBox : _inactiveBox;
                foreach (var st in new[] { "normal", "hover", "pressed" })
                    tab.AddThemeStyleboxOverride(st, box);

                StringName activeText = _variant == ChimeraComponents.TabsVariant.Segment ? ThemeTokens.AccentInk : ThemeTokens.AccentBright;
                if (_variant == ChimeraComponents.TabsVariant.Boxed) activeText = ThemeTokens.TextHi; // boxed active is not accent-tinted

                var textColor = active ? ChimeraComponents.Col(activeText) : ChimeraComponents.Col(ThemeTokens.TextLo);
                tab.AddThemeColorOverride("font_color", textColor);
                tab.AddThemeColorOverride("font_hover_color", active ? textColor : ChimeraComponents.Col(ThemeTokens.TextMid));
                tab.AddThemeColorOverride("font_pressed_color", textColor);
            }
        }
    }
}
