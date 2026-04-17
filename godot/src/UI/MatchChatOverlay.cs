#nullable enable
using Godot;
using System;
using System.Collections.Generic;
using ProjectChimera.Core;
using ProjectChimera.Multiplayer;

namespace ProjectChimera.UI
{
    /// <summary>
    /// In-game text chat overlay for multiplayer matches.
    ///
    /// Layout: bottom-left CanvasLayer panel.
    ///   - Scrolling message list (last MAX_MESSAGES, BBCode colored by faction)
    ///   - Text input line shown when the player presses Enter or T
    ///   - Panel auto-hides after HIDE_AFTER_SECONDS of no new messages
    ///
    /// Keybindings (Play mode only):
    ///   Enter — open input, or send current message and close
    ///   Escape — close input without sending
    ///
    /// Usage:
    ///   var chat = new MatchChatOverlay();
    ///   AddChild(chat);
    ///   chat.Initialize(_lockstep);
    ///   // On match end: chat.Close();
    /// </summary>
    public partial class MatchChatOverlay : CanvasLayer
    {
        // ── Config ────────────────────────────────────────────────────────────────

        private const int   MAX_MESSAGES        = 12;
        private const float HIDE_AFTER_SECONDS  = 8f;
        private const int   PANEL_WIDTH         = 360;
        private const int   PANEL_HEIGHT        = 200;
        private const int   MARGIN_LEFT         = 12;
        private const int   MARGIN_BOTTOM       = 12;

        // Faction display colors (BBCode hex)
        private static readonly string[] FACTION_COLORS =
        {
            "#aaaaaa",   // Neutral
            "#4fc3f7",   // Player1 — light blue
            "#ef5350",   // Player2 — red
            "#66bb6a",   // Player3 — green
            "#ffa726",   // Player4 — orange
        };

        // ── State ─────────────────────────────────────────────────────────────────

        private LockstepManager? _lockstep;
        private Faction          _localFaction = Faction.Neutral;
        private float            _hideTimer;
        private bool             _inputOpen;

        private readonly List<string> _messages = new(MAX_MESSAGES + 1);

        // ── UI refs ───────────────────────────────────────────────────────────────

        private PanelContainer  _panel     = null!;
        private RichTextLabel   _log       = null!;
        private LineEdit        _input     = null!;
        private Control         _inputRow  = null!;

        // ── Setup ──────────────────────────────────────────────────────────────────

        /// <summary>Call after adding to scene tree. Pass null to suppress chat (replay/offline mode).</summary>
        public void Initialize(LockstepManager? lockstep, Faction localFaction = Faction.Neutral)
        {
            _lockstep     = lockstep;
            _localFaction = localFaction;

            if (_lockstep != null)
                _lockstep.OnChatReceived += HandleChatReceived;
        }

        public override void _Ready()
        {
            Layer = 8;  // above HUD (0), below lobby (20)
            BuildUi();
            SetPanelVisible(false);
        }

        // ── Cleanup ───────────────────────────────────────────────────────────────

        public void Close()
        {
            if (_lockstep != null)
            {
                _lockstep.OnChatReceived -= HandleChatReceived;
                _lockstep = null;
            }
            _messages.Clear();
            SetPanelVisible(false);
        }

        // ── Input ─────────────────────────────────────────────────────────────────

        public override void _UnhandledInput(InputEvent @event)
        {
            if (!Visible) return;
            if (@event is not InputEventKey key || !key.Pressed || key.Echo) return;

            switch (key.Keycode)
            {
                case Key.Enter:
                case Key.KpEnter:
                    if (_inputOpen)
                        TrySendMessage();
                    else
                        OpenInput();
                    GetViewport().SetInputAsHandled();
                    break;

                case Key.Escape:
                    if (_inputOpen)
                        CloseInput();
                    GetViewport().SetInputAsHandled();
                    break;
            }
        }

        // ── Frame ─────────────────────────────────────────────────────────────────

        public override void _Process(double delta)
        {
            if (!Visible) return;
            if (!_panel.Visible) return;
            if (_inputOpen) return;   // don't auto-hide while typing

            _hideTimer -= (float)delta;
            if (_hideTimer <= 0f)
                SetPanelVisible(false);
        }

        // ── Receive ───────────────────────────────────────────────────────────────

        private void HandleChatReceived(Faction faction, string message)
        {
            // Sanitize: strip BBCode tags from user input to prevent injection.
            string safe = message.Replace("[", "（").Replace("]", "）");
            string name = FactionName(faction);
            string color = FactionColor(faction);

            string line = $"[color={color}]{name}:[/color] {safe}";
            AddMessage(line);
        }

        /// <summary>
        /// Add a system message (e.g. "Player 2 connected") in neutral gray.
        /// Call from MainScene for match events.
        /// </summary>
        public void AddSystemMessage(string text)
        {
            AddMessage($"[color=#888888]* {text}[/color]");
        }

        // ── Send ──────────────────────────────────────────────────────────────────

        private void TrySendMessage()
        {
            string msg = _input.Text.Trim();
            CloseInput();

            if (string.IsNullOrEmpty(msg)) return;
            if (_lockstep == null) return;

            // Optimistically echo own message (we won't receive our own packet back
            // in P2P mode — dedicated server broadcasts back to sender too, but
            // showing it immediately feels better).
            string safe  = msg.Replace("[", "（").Replace("]", "）");
            string name  = FactionName(_localFaction);
            string color = FactionColor(_localFaction);
            AddMessage($"[color={color}]{name}:[/color] {safe}");

            _lockstep.SendChat(msg);
        }

        // ── UI helpers ────────────────────────────────────────────────────────────

        private void AddMessage(string bbLine)
        {
            _messages.Add(bbLine);
            if (_messages.Count > MAX_MESSAGES)
                _messages.RemoveAt(0);

            RebuildLog();
            ShowPanel();
        }

        private void RebuildLog()
        {
            _log.Clear();
            foreach (var line in _messages)
                _log.AppendText(line + "\n");
        }

        private void ShowPanel()
        {
            SetPanelVisible(true);
            _hideTimer = HIDE_AFTER_SECONDS;
        }

        private void SetPanelVisible(bool visible)
        {
            _panel.Visible = visible;
            if (!visible) CloseInput();
        }

        private void OpenInput()
        {
            _inputOpen       = true;
            _inputRow.Visible = true;
            _input.Text      = "";
            _input.GrabFocus();
            ShowPanel();
        }

        private void CloseInput()
        {
            _inputOpen        = false;
            _inputRow.Visible = false;
            _input.ReleaseFocus();
        }

        // ── UI construction ───────────────────────────────────────────────────────

        private void BuildUi()
        {
            _panel = new PanelContainer();
            _panel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomLeft);
            _panel.OffsetLeft   =  MARGIN_LEFT;
            _panel.OffsetBottom = -MARGIN_BOTTOM;
            _panel.OffsetRight  =  MARGIN_LEFT + PANEL_WIDTH;
            _panel.OffsetTop    = -(MARGIN_BOTTOM + PANEL_HEIGHT);
            _panel.CustomMinimumSize = new Vector2(PANEL_WIDTH, PANEL_HEIGHT);
            // Semi-transparent background
            var style = new StyleBoxFlat
            {
                BgColor        = new Color(0.06f, 0.06f, 0.06f, 0.75f),
                CornerRadiusTopLeft    = 4,
                CornerRadiusTopRight   = 4,
                CornerRadiusBottomLeft = 4,
                CornerRadiusBottomRight = 4,
            };
            _panel.AddThemeStyleboxOverride("panel", style);
            AddChild(_panel);

            var vbox = new VBoxContainer();
            vbox.AddThemeConstantOverride("separation", 2);
            _panel.AddChild(vbox);

            // Message log
            _log = new RichTextLabel
            {
                BbcodeEnabled       = true,
                ScrollFollowing = true,
                SizeFlagsVertical   = Control.SizeFlags.ExpandFill,
                CustomMinimumSize   = new Vector2(0, PANEL_HEIGHT - 40),
            };
            _log.AddThemeFontSizeOverride("normal_font_size", 13);
            vbox.AddChild(_log);

            // Input row (hidden until Enter pressed)
            _inputRow = new HBoxContainer();
            _inputRow.Visible = false;
            _inputRow.AddThemeConstantOverride("separation", 4);

            var prompt = new Label { Text = ">" };
            prompt.AddThemeFontSizeOverride("font_size", 13);
            _inputRow.AddChild(prompt);

            _input = new LineEdit
            {
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                MaxLength           = 200,
                PlaceholderText     = "Type message, Enter to send…",
            };
            _input.AddThemeFontSizeOverride("font_size", 13);
            // Consume Enter so it doesn't bubble to _UnhandledInput
            _input.TextSubmitted += _ => TrySendMessage();
            _inputRow.AddChild(_input);

            vbox.AddChild(_inputRow);

            // Hint label below panel (fades with panel)
            var hint = new Label { Text = "Enter = chat" };
            hint.AddThemeFontSizeOverride("font_size", 10);
            hint.AddThemeColorOverride("font_color", new Color(0.6f, 0.6f, 0.6f));
            hint.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomLeft);
            hint.OffsetLeft   =  MARGIN_LEFT;
            hint.OffsetBottom = -(MARGIN_BOTTOM + PANEL_HEIGHT + 2);
            hint.OffsetRight  =  MARGIN_LEFT + 80;
            hint.OffsetTop    = -(MARGIN_BOTTOM + PANEL_HEIGHT + 16);
            AddChild(hint);
        }

        // ── Static helpers ────────────────────────────────────────────────────────

        private static string FactionName(Faction f) => f switch
        {
            Faction.Player1 => "P1",
            Faction.Player2 => "P2",
            Faction.Player3 => "P3",
            Faction.Player4 => "P4",
            _               => "??",
        };

        private static string FactionColor(Faction f)
        {
            int idx = (int)f;
            return idx >= 0 && idx < FACTION_COLORS.Length ? FACTION_COLORS[idx] : "#aaaaaa";
        }
    }
}
