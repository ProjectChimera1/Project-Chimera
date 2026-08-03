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
    /// DW-374 — a WC3-style dash-command ("-42") ALSO raises the bounded player_chat chat-code on the replicated,
    /// tick-stamped lockstep rail (LockstepManager.SendPlayerChat) so an authored trigger subscribed to
    /// player_chat actually fires; parsing lives in the Godot-free MatchChatCommands. Free text stays
    /// display-only on the reliable SendChat side-channel — no string ever enters the tick.
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

        // Speaker names + BBCode colors come from MatchChatFormat / FactionPalette — the canonical 8-player table.
        // (DW-385: a local 5-entry color list + a Player1–Player4-only name switch rendered every Player5–Player8
        // speaker as an indistinguishable gray "??" in a 5–8 faction match.)

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
            // Name/color lookup + BBCode-injection sanitization live in the Godot-free MatchChatFormat.
            AddMessage(MatchChatFormat.ChatLine(faction, message));
        }

        /// <summary>
        /// Add a system message (e.g. "Player 2 connected") in neutral gray.
        /// Call from MainScene for match events.
        /// </summary>
        public void AddSystemMessage(string text)
        {
            AddMessage(MatchChatFormat.SystemLine(text));
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
            // showing it immediately feels better). Same formatter as the receive path,
            // so the local echo can never disagree with how a peer renders us.
            AddMessage(MatchChatFormat.ChatLine(_localFaction, msg));

            _lockstep.SendChat(msg);

            // DW-374 — the presentation-side chat-string→code map the Story 7.13 (Arm D) player_chat rail was
            // built for: a dash-command ("-42") parses to a bounded chat code and rides SendPlayerChat
            // (EnqueueDslEvent under the hood) so an authored player_chat trigger fires on the identical tick on
            // every peer and in replay. Offline it applies immediately as Player1; a spectator's raise is a
            // deterministic drop inside EnqueueDslEvent. The display string above is untouched — the typed
            // command still shows in chat (WC3 behavior), but only the CODE is sim-visible.
            if (MatchChatCommands.TryParseChatCode(msg, out int chatCode))
                _lockstep.SendPlayerChat(chatCode);
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
    }
}
