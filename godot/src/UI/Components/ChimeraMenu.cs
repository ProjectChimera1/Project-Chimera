#nullable enable
using Godot;
using ProjectChimera.UI.Theme;

namespace ProjectChimera.UI.Components
{
    /// <summary>
    /// menu (UX-DR23) — a dropdown popover (NOT a right-click context menu; the mock has zero
    /// <c>contextmenu</c>). A <see cref="PopupPanel"/> (D-2) holding a VBox of themed menu-item
    /// <see cref="Button"/>s. PopupPanel gives native above-everything layering, positioning near the
    /// trigger, and close-on-outside-click for free, while letting us fully own the item styling: hover =
    /// surface_4 + text_hi, <c>is-active</c> = accent_bright text (bound so it retints on an accent switch).
    ///
    /// The panel is chamfered (cut_sm, surface_2, shadow_pop + line_strong inset). Items carry an id and an
    /// optional leading check glyph; selecting one emits <see cref="IdPressed"/> and closes. Open it anchored
    /// under a trigger with <see cref="OpenBelow"/>.
    ///
    /// Presentation layer.
    /// </summary>
    public partial class ChimeraMenu : PopupPanel
    {
        /// <summary>Emitted with the chosen item's id; the menu closes on selection.</summary>
        [Signal]
        public delegate void IdPressedEventHandler(int id);

        private VBoxContainer _items = null!;

        /// <summary>Build an empty menu popover; add items with <see cref="AddItem"/>.</summary>
        public static ChimeraMenu Create()
        {
            var m = new ChimeraMenu();
            m.Build();
            return m;
        }

        private void Build()
        {
            // Chamfered popover panel: surface_2, cut_sm, shadow_pop + inset line_strong hairline, pad 5.
            var box = ChimeraStyleBox.Chamfer(ChimeraComponents.Const(ThemeTokens.CutSm),
                ChimeraComponents.Col(ThemeTokens.Surface2), ChimeraComponents.Col(ThemeTokens.LineStrong), 1);
            box.WithContentMargins(ComponentMetrics.MenuPanelPad, ComponentMetrics.MenuPanelPad)
               .WithShadow(ThemeTokens.GetShadow(ThemeTokens.ShadowPop));
            AddThemeStyleboxOverride("panel", box);

            _items = new VBoxContainer { CustomMinimumSize = new Vector2(ComponentMetrics.MenuMinWidth, 0) };
            _items.AddThemeConstantOverride("separation", 0);
            AddChild(_items);
        }

        /// <summary>
        /// Add a menu item. <paramref name="active"/> renders it in accent_bright (the <c>is-active</c>
        /// current-choice state, bound to retint on a switch); <paramref name="check"/> is an optional
        /// leading glyph (e.g. a checkmark). Selecting the item emits <see cref="IdPressed"/> and closes.
        /// </summary>
        public void AddItem(string label, int id, bool active = false, string? check = null)
        {
            var btn = new Button
            {
                Text = string.IsNullOrEmpty(check) ? label : $"{check}  {label}",
                Alignment = HorizontalAlignment.Left,
                FocusMode = Control.FocusModeEnum.All, // Tab-focusable for keyboard operation
            };
            btn.AddThemeFontOverride("font", ChimeraComponents.FontOf(ThemeTokens.FontUi));
            btn.AddThemeFontSizeOverride("font_size", ChimeraComponents.SizeOf(ThemeTokens.Tsm));

            // Flat item styleboxes (no chamfer on items — the CSS .menu-item has none): transparent normal,
            // surface_4 hover/pressed. Item pad 8×10.
            var clear = new Color(0, 0, 0, 0);
            StyleBoxFlat Item(Color fill)
            {
                var b = ChimeraStyleBox.Chamfer(0, fill, fill, 0);
                b.WithContentMargins(ComponentMetrics.MenuItemPadH, ComponentMetrics.MenuItemPadV);
                return b;
            }
            btn.AddThemeStyleboxOverride("normal", Item(clear));
            btn.AddThemeStyleboxOverride("hover", Item(ChimeraComponents.Col(ThemeTokens.Surface4)));
            btn.AddThemeStyleboxOverride("pressed", Item(ChimeraComponents.Col(ThemeTokens.Surface4)));
            btn.AddThemeStyleboxOverride("focus", Item(ChimeraComponents.Col(ThemeTokens.Surface4)));

            if (active)
            {
                // is-active: accent_bright text across every state, bound so an accent switch retints it.
                ChimeraComponents.BindAccentColorMulti(btn, ThemeTokens.AccentBright,
                    new[] { "font_color", "font_hover_color", "font_pressed_color", "font_focus_color" });
            }
            else
            {
                btn.AddThemeColorOverride("font_color", ChimeraComponents.Col(ThemeTokens.TextMid));
                btn.AddThemeColorOverride("font_hover_color", ChimeraComponents.Col(ThemeTokens.TextHi));
                btn.AddThemeColorOverride("font_pressed_color", ChimeraComponents.Col(ThemeTokens.TextHi));
                btn.AddThemeColorOverride("font_focus_color", ChimeraComponents.Col(ThemeTokens.TextHi));
            }

            btn.Pressed += () =>
            {
                EmitSignal(SignalName.IdPressed, id);
                Hide();
            };
            _items.AddChild(btn);
        }

        /// <summary>
        /// Open the menu anchored directly below <paramref name="trigger"/> (left edges aligned, at least the
        /// trigger's width, min-width 180). The popover closes itself on an outside click.
        /// </summary>
        public void OpenBelow(Control trigger)
        {
            Rect2 gr = trigger.GetGlobalRect();
            int width = (int)Mathf.Max(ComponentMetrics.MenuMinWidth, gr.Size.X);

            // Measure the content height so a bottom-edge trigger flips the menu ABOVE (or clamps on-screen)
            // instead of spilling off the bottom — the sibling tooltip clamps X+Y; the menu must too (3.1c
            // review). Height 0 in Popup() keeps the popover auto-sized to its contents.
            float h = GetContentsMinimumSize().Y;
            Vector2 vp = trigger.GetViewportRect().Size;
            float below = gr.Position.Y + gr.Size.Y + 4f;
            float above = gr.Position.Y - h - 4f;
            float x = Mathf.Clamp(gr.Position.X, 0f, Mathf.Max(0f, vp.X - width));
            float y = below + h <= vp.Y ? below : (above >= 0f ? above : Mathf.Max(0f, vp.Y - h));

            Popup(new Rect2I(new Vector2I((int)x, (int)y), new Vector2I(width, 0)));
        }
    }
}
