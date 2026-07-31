#nullable enable
using Godot;
using System;
using ProjectChimera.Core;           // GameOverSummary (Godot-free score-screen data)
using ProjectChimera.UI.Components;  // ChimeraComponents
using ProjectChimera.UI.Theme;       // ThemeTokens, ThemeBuilder, AccentController
using GodotTheme = Godot.Theme;      // the ProjectChimera.UI.Theme namespace shadows the bare Theme type

namespace ProjectChimera.UI
{
    /// <summary>
    /// Story 11.2 (FR-66) — the kit-styled victory/defeat score screen that replaces the raw-node body of
    /// <c>MainScene.ShowGameOver</c>. Consumes the Godot-free <see cref="GameOverSummary.Build"/> projection: a
    /// VICTORY/DEFEAT banner keyed off the LOCAL faction's verdict, one row per active faction (Result / Built / Killed
    /// / Lost / Razed / Ore / Crystal), the match duration from the sim tick counter, and actions Play Again / Quit to
    /// Menu / Save Replay (the last shown only when a recording was retained). Pure presentation — it emits events the
    /// scene wires.
    /// </summary>
    public partial class ScoreScreenOverlay : CanvasLayer
    {
        /// <summary>Re-open the skirmish setup screen for another match.</summary>
        public event Action? OnPlayAgain;
        /// <summary>Return to Edit + re-show the main menu.</summary>
        public event Action? OnQuitToMenu;
        /// <summary>Rename/annotate the just-recorded replay at the given path.</summary>
        public event Action<string>? OnSaveReplay;

        private GodotTheme        _theme  = null!;
        private VBoxContainer     _body   = null!;

        /// <summary>Build the overlay shell (hidden). Call once at bootstrap.</summary>
        public void Initialize()
        {
            Layer   = 25; // the terminal end-of-match surface — above the in-match menu (14), settings (15), and the
                          // pre-match briefing (22), so nothing can ever cover the resolved victory/defeat screen

            Visible = false;

            _theme = ChimeraComponents.EnsureInitialized(this);

            var anchorRoot = new Control();
            anchorRoot.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            anchorRoot.MouseFilter = Control.MouseFilterEnum.Stop;
            anchorRoot.Theme = _theme;
            AddChild(anchorRoot);

            Color voidC = _theme.GetColor(ThemeTokens.SurfaceVoid, ThemeTokens.Type);
            var scrim = new ColorRect { Color = new Color(voidC.R, voidC.G, voidC.B, 0.88f) };
            scrim.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            scrim.MouseFilter = Control.MouseFilterEnum.Ignore;
            anchorRoot.AddChild(scrim);

            var center = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
            center.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            anchorRoot.AddChild(center);

            var card = ChimeraComponents.Panel(ChimeraComponents.PanelVariant.Default);
            card.CustomMinimumSize = new Vector2(680, 0);
            center.AddChild(card);

            _body = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
            _body.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S4));
            card.AddChild(_body);
        }

        /// <summary>
        /// Populate + show the score screen. <paramref name="localWon"/> keys the VICTORY/DEFEAT banner (the LOCAL
        /// seat's verdict, never the team representative). <paramref name="winnerLine"/> is the sub-heading phrasing.
        /// <paramref name="matchTicks"/> is the sim tick count → duration at 30 tps. <paramref name="savedReplayPath"/>
        /// null/empty hides the Save-Replay action.
        /// </summary>
        public void Show(GameOverSummary.GameOverRow[] rows, bool localWon, string winnerLine,
                         int matchTicks, string? savedReplayPath)
        {
            foreach (Node child in _body.GetChildren()) { _body.RemoveChild(child); child.QueueFree(); }

            // ── Banner ──
            var banner = new Label { Text = localWon ? "VICTORY" : "DEFEAT", HorizontalAlignment = HorizontalAlignment.Center };
            banner.AddThemeFontOverride("font", ChimeraComponents.FontOf(ThemeTokens.FontDisplay));
            banner.AddThemeFontSizeOverride("font_size", ChimeraComponents.SizeOf(ThemeTokens.T4xl));
            banner.AddThemeColorOverride("font_color",
                ChimeraComponents.Col(localWon ? ThemeTokens.AccentBright : ThemeTokens.Danger));
            _body.AddChild(banner);

            var sub = new Label { Text = winnerLine, HorizontalAlignment = HorizontalAlignment.Center };
            sub.AddThemeFontOverride("font", ChimeraComponents.FontOf(ThemeTokens.FontUi));
            sub.AddThemeFontSizeOverride("font_size", ChimeraComponents.SizeOf(ThemeTokens.Tlg));
            sub.AddThemeColorOverride("font_color", ChimeraComponents.Col(ThemeTokens.TextMid));
            _body.AddChild(sub);

            int totalSec = (matchTicks < 0 ? 0 : matchTicks) / 30; // deterministic sim duration (30 tps)
            var dur = new Label { Text = $"Duration  {totalSec / 60}:{totalSec % 60:D2}", HorizontalAlignment = HorizontalAlignment.Center };
            dur.AddThemeFontOverride("font", ChimeraComponents.FontOf(ThemeTokens.FontUi));
            dur.AddThemeFontSizeOverride("font_size", ChimeraComponents.SizeOf(ThemeTokens.Tmd));
            dur.AddThemeColorOverride("font_color", ChimeraComponents.Col(ThemeTokens.TextLo));
            _body.AddChild(dur);

            _body.AddChild(new HSeparator());

            // ── Per-faction table ──
            AddRow("Player", "Result", "Built", "Killed", "Lost", "Razed", "Ore", "Crystal",
                   ChimeraComponents.Col(ThemeTokens.TextLo), ThemeTokens.Tsm, header: true);
            foreach (GameOverSummary.GameOverRow r in rows)
            {
                Color c = Color.Color8(r.ColorR, r.ColorG, r.ColorB, r.ColorA);
                AddRow($"{r.ColorGlyph} {r.Name}", r.VerdictLabel,
                       $"{r.UnitsBuilt}", $"{r.Kills}", $"{r.Losses}", $"{r.BuildingsRazed}",
                       $"{r.OreMined:N0}", $"{r.CrystalMined:N0}", c, ThemeTokens.Tmd, header: false);
            }

            _body.AddChild(new HSeparator());

            // ── Actions ──
            var actions = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
            actions.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S3));
            _body.AddChild(actions);

            var playAgain = ChimeraComponents.Button("Play Again", ChimeraComponents.ButtonVariant.Primary);
            playAgain.Pressed += () => OnPlayAgain?.Invoke();
            actions.AddChild(playAgain);

            var quit = ChimeraComponents.Button("Quit to Menu", ChimeraComponents.ButtonVariant.Secondary);
            quit.Pressed += () => OnQuitToMenu?.Invoke();
            actions.AddChild(quit);

            if (!string.IsNullOrEmpty(savedReplayPath))
            {
                string captured = savedReplayPath!;
                var save = ChimeraComponents.Button("Save Replay", ChimeraComponents.ButtonVariant.Ghost);
                save.Pressed += () => OnSaveReplay?.Invoke(captured);
                actions.AddChild(save);
            }

            Visible = true;
            playAgain.GrabFocus();
        }

        public void Hide() => Visible = false;

        private void AddRow(string c0, string c1, string c2, string c3, string c4, string c5, string c6, string c7,
                            Color color, StringName sizeToken, bool header)
        {
            var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
            row.AddThemeConstantOverride("separation", 0);
            void Cell(string text, float width, HorizontalAlignment align)
            {
                var lbl = new Label { Text = text, HorizontalAlignment = align, CustomMinimumSize = new Vector2(width, 0) };
                lbl.AddThemeFontOverride("font", ChimeraComponents.FontOf(header ? ThemeTokens.FontDisplay : ThemeTokens.FontUi));
                lbl.AddThemeFontSizeOverride("font_size", ChimeraComponents.SizeOf(sizeToken));
                lbl.AddThemeColorOverride("font_color", color);
                row.AddChild(lbl);
            }
            Cell(c0, 120, HorizontalAlignment.Left);
            Cell(c1, 80,  HorizontalAlignment.Center);
            Cell(c2, 70,  HorizontalAlignment.Center);
            Cell(c3, 70,  HorizontalAlignment.Center);
            Cell(c4, 70,  HorizontalAlignment.Center);
            Cell(c5, 70,  HorizontalAlignment.Center);
            Cell(c6, 90,  HorizontalAlignment.Center);
            Cell(c7, 90,  HorizontalAlignment.Center);
            _body.AddChild(row);
        }
    }
}
