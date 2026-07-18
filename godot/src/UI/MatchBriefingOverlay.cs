#nullable enable
using System;
using System.Collections.Generic;
using Godot;
using ProjectChimera.Core.Definitions;   // ScenarioData, ResolvedObjective
using ProjectChimera.UI.Components;        // ChimeraComponents
using ProjectChimera.UI.Theme;             // ThemeTokens, ThemeBuilder, AccentController
using GodotTheme = Godot.Theme;

namespace ProjectChimera.UI
{
    /// <summary>
    /// Story 7.14 — the skippable pre-match briefing surface. A code-built <see cref="CanvasLayer"/> overlay (mirrors
    /// <c>HeroPickerOverlay</c>) shown at Play-start that presents the map name/description, the resolved objective
    /// list, and the local faction blurb, then a Continue button that dismisses it and lets Play proceed.
    ///
    /// <para><b>Presentation-only, never gates the tick.</b> A lockstep peer cannot pause its deterministic sim on a
    /// local dismissal — this overlay writes NO sim state, folds into NO checksum, and never blocks the sim loop. It is
    /// parameterized (<see cref="ShowBriefing"/> takes the raw display fields) for Epic 13 campaign-mission reuse.</para>
    /// </summary>
    public partial class MatchBriefingOverlay : CanvasLayer
    {
        private const float PANEL_W = 620f;
        private const float PANEL_H = 520f;

        private GodotTheme         _theme  = null!;
        private AccentController?  _accent;

        private ColorRect       _scrim       = null!;
        private PanelContainer  _panel       = null!;
        private VBoxContainer   _bodyHost    = null!;
        private Label           _titleLabel  = null!;

        public override void _Ready()
        {
            EnsureKitInitialized();
            BuildChrome();
            Visible = false;
        }

        private void EnsureKitInitialized()
        {
            _theme = ResourceLoader.Load<GodotTheme>(ThemeBuilder.ThemePath, cacheMode: ResourceLoader.CacheMode.Ignore)
                     ?? ThemeBuilder.Build();
            if (!ChimeraComponents.IsInitialized)
            {
                _accent = new AccentController { Name = "AccentController" };
                AddChild(_accent);
                _accent.Initialize(_theme);
                ChimeraComponents.Initialize(_theme, _accent);
            }
        }

        private void BuildChrome()
        {
            Layer = 22; // above the quest-log, below toasts

            _scrim = new ColorRect { Color = new Color(0.04f, 0.05f, 0.08f, 0.78f), MouseFilter = Control.MouseFilterEnum.Stop };
            _scrim.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            AddChild(_scrim);

            var center = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
            center.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            AddChild(center);

            _panel = ChimeraComponents.Panel(ChimeraComponents.PanelVariant.Default);
            _panel.CustomMinimumSize = new Vector2(PANEL_W, PANEL_H);
            _panel.Theme = _theme;
            center.AddChild(_panel);

            var root = new VBoxContainer();
            root.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S4));
            _panel.AddChild(root);

            _titleLabel = Heading("Briefing", ThemeTokens.Txl);
            root.AddChild(_titleLabel);

            var scroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
            scroll.CustomMinimumSize = new Vector2(0, 320);
            root.AddChild(scroll);

            _bodyHost = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _bodyHost.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S3));
            scroll.AddChild(_bodyHost);

            var continueBtn = ChimeraComponents.Button("Continue", ChimeraComponents.ButtonVariant.Primary);
            continueBtn.Pressed += Dismiss;
            var btnRow = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
            btnRow.AddChild(continueBtn);
            root.AddChild(btnRow);
        }

        /// <summary>Show the briefing for a resolved match (the in-engine entry point). Convenience over
        /// <see cref="ShowBriefing"/>: pulls the display name/description off <paramref name="scenario"/>.</summary>
        public void ShowForScenario(ScenarioData? scenario, IReadOnlyList<ResolvedObjective> objectives, string? factionBlurb)
        {
            string title = string.IsNullOrWhiteSpace(scenario?.DisplayName) ? "Match Briefing" : scenario!.DisplayName;
            ShowBriefing(title, scenario?.Description, objectives, factionBlurb);
        }

        /// <summary>Story 7.14 — the parameterized briefing entry point (Epic 13 mission-briefing reuse). A missing
        /// description / blurb simply omits that section (no crash). Never gates the tick.</summary>
        public void ShowBriefing(string mapName, string? description, IReadOnlyList<ResolvedObjective> objectives,
            string? factionBlurb)
        {
            _titleLabel.Text = string.IsNullOrWhiteSpace(mapName) ? "Match Briefing" : mapName;

            foreach (Node c in _bodyHost.GetChildren()) c.QueueFree();

            if (!string.IsNullOrWhiteSpace(description))
                _bodyHost.AddChild(Body(description!, ThemeTokens.TextMid));

            // A Hidden objective is "not yet revealed to the player" (see ObjectiveState.Hidden) and the in-match quest
            // log deliberately hides its row until a show_objective reveals it. The briefing must honor the same reveal-
            // later contract — listing a Hidden objective's title here would spoil it at match start. So show only the
            // non-Hidden objectives; if that leaves none (every objective starts Hidden), fall back to the generic line.
            var visibleObjectives = new List<ResolvedObjective>();
            if (objectives != null)
                foreach (ResolvedObjective o in objectives)
                    if (o.InitialState != ObjectiveState.Hidden) visibleObjectives.Add(o);

            _bodyHost.AddChild(SectionLabel("Objectives"));
            if (visibleObjectives.Count > 0)
            {
                foreach (ResolvedObjective o in visibleObjectives)
                    _bodyHost.AddChild(Body("•  " + o.Title, ThemeTokens.TextHi));
            }
            else
            {
                _bodyHost.AddChild(Body("•  " + WinObjectiveText.GenericVictory, ThemeTokens.TextHi));
            }

            if (!string.IsNullOrWhiteSpace(factionBlurb))
            {
                _bodyHost.AddChild(SectionLabel("Faction"));
                _bodyHost.AddChild(Body(factionBlurb!, ThemeTokens.TextMid));
            }

            Visible = true;
        }

        /// <summary>Skip/dismiss — hides the overlay. The sim was never gated, so nothing to resume.</summary>
        public void Dismiss() => Visible = false;

        // ── Small label builders (mirror HeroPickerOverlay) ──

        private Label Heading(string text, StringName sizeToken)
        {
            var l = new Label { Text = text };
            l.AddThemeFontOverride("font", _theme.GetFont(ThemeTokens.FontDisplay, ThemeTokens.Type));
            l.AddThemeFontSizeOverride("font_size", _theme.GetFontSize(sizeToken, ThemeTokens.Type));
            l.AddThemeColorOverride("font_color", _theme.GetColor(ThemeTokens.TextHi, ThemeTokens.Type));
            return l;
        }

        private Label SectionLabel(string text)
        {
            var l = new Label { Text = text };
            l.AddThemeFontOverride("font", _theme.GetFont(ThemeTokens.FontDisplay, ThemeTokens.Type));
            l.AddThemeFontSizeOverride("font_size", _theme.GetFontSize(ThemeTokens.Tlg, ThemeTokens.Type));
            l.AddThemeColorOverride("font_color", _theme.GetColor(ThemeTokens.Accent, ThemeTokens.Type));
            return l;
        }

        private Label Body(string text, StringName colorToken)
        {
            var l = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart };
            l.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            l.AddThemeColorOverride("font_color", _theme.GetColor(colorToken, ThemeTokens.Type));
            return l;
        }
    }
}
