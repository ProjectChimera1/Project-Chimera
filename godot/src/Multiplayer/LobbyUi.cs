#nullable enable
using Godot;
using System;
using System.Threading.Tasks;

namespace ProjectChimera.Multiplayer
{
    /// <summary>
    /// Pre-game lobby overlay: Direct IP (LAN/dev) and Online (Nakama matchmaking) modes.
    ///
    /// Direct mode:
    ///   Host → open ENet port, wait for peer → Ready → StartGame.
    ///   Join  → connect to IP:port → Ready → StartGame.
    ///
    /// Online mode:
    ///   Email + password → auth with Nakama → Find Match → Nakama groups 2 players →
    ///   auto-joins dedicated server at configured address → Ready/StartGame as normal.
    ///
    /// This is a CanvasLayer Node added as a child of MainScene.
    /// </summary>
    public partial class LobbyUi : CanvasLayer
    {
        // ── Events ────────────────────────────────────────────────────────────────

        /// <summary>Fires when both peers are ready and StartGame has been agreed.</summary>
        public event Action<bool, Core.Faction>? OnMatchStart;

        // ── Deps ──────────────────────────────────────────────────────────────────

        private ENetTransport _transport = null!;
        private NakamaService _nakama    = null!;

        // ── Nakama config (Inspector-exported on MainScene, passed via Initialize) ─

        public string NakamaHost    { get; set; } = "localhost";
        public int    NakamaPort    { get; set; } = 7350;
        public string NakamaKey     { get; set; } = "defaultkey";
        public string GameServerIp  { get; set; } = "localhost";
        public int    GameServerPort { get; set; } = 7777;

        // ── Scenario hash (set by MainScene after scenario load) ─────────────────

        /// <summary>
        /// FNV-1a hash of the current scenario file, computed by MainScene after loading.
        /// Sent with the Ready packet so the host can verify both peers have the same map.
        /// 0 = not set / no file on disk.
        /// </summary>
        public uint ScenarioHash { get; set; }

        // ── UI refs — Direct tab ───────────────────────────────────────────────────

        private Control  _directTab  = null!;
        private SpinBox  _portSpin   = null!;
        private LineEdit _ipField    = null!;
        private Button   _hostBtn    = null!;
        private Button   _joinBtn    = null!;

        // ── UI refs — Online tab ───────────────────────────────────────────────────

        private Control  _onlineTab     = null!;
        private LineEdit _emailField    = null!;
        private LineEdit _passwordField = null!;
        private Button   _findBtn       = null!;
        private Button   _cancelFindBtn = null!;

        // ── UI refs — shared ──────────────────────────────────────────────────────

        private Label  _titleLabel  = null!;
        private Label  _statusLabel = null!;
        private Button _readyBtn    = null!;
        private Button _cancelBtn   = null!;

        // ── State ─────────────────────────────────────────────────────────────────

        private bool         _readyConfirmed;
        private bool         _peerReadyConfirmed;
        private Core.Faction _assignedFaction = Core.Faction.Neutral;
        private bool         _onlineModeActive;

        // ── Setup ──────────────────────────────────────────────────────────────────

        /// <summary>Call once after adding to scene tree.</summary>
        public void Initialize(ENetTransport transport)
        {
            _transport = transport;
            _transport.OnPeerConnected    += HandlePeerConnected;
            _transport.OnPeerDisconnected += HandlePeerDisconnected;
            _transport.OnPacketReceived   += HandlePacket;

            _nakama = new NakamaService
            {
                NakamaHost   = NakamaHost,
                NakamaPort   = NakamaPort,
                NakamaKey    = NakamaKey,
                GameServerIp   = GameServerIp,
                GameServerPort = GameServerPort
            };
            _nakama.OnMatchFound  += HandleNakamaMatchFound;
            _nakama.OnStatusText  += SetStatus;
            _nakama.OnDisconnected += () => SetStatus("Matchmaking server disconnected.");
        }

        public override void _Ready()
        {
            Layer = 20;
            BuildUi();
            Visible = false;
        }

        // ── Visibility ────────────────────────────────────────────────────────────

        public new void Show()
        {
            Visible = true;
            _readyBtn.Visible = false;
            SetStatus("Enter IP to join, click Host, or use Online matchmaking.");
        }

        public void Close()
        {
            Visible = false;
            _readyConfirmed     = false;
            _peerReadyConfirmed = false;
            _assignedFaction    = Core.Faction.Neutral;
        }

        // ── Frame ────────────────────────────────────────────────────────────────

        public override void _Process(double delta)
        {
            if (!Visible) return;
            _transport.Poll();
            _nakama.DrainEvents();   // marshal Nakama background events to main thread
#if DEBUG
            // Loopback smoke (auto-join): ready as soon as the connection is established, INDEPENDENT of the
            // server's Hello packet arriving. The Hello (faction assignment) is reliable and still arrives to
            // set the faction before StartGame, but readiness must not hinge on its timing — a single delayed/
            // missed Hello otherwise leaves the lobby stuck on "Ready! Waiting" forever. Fires once (OnReadyPressed
            // sets _readyConfirmed). The connect event itself is reliable, so this always fires after connecting.
            if (_autoReady && !_readyConfirmed && _transport.IsConnected) OnReadyPressed();
#endif
        }

#if DEBUG
        // ── Story 1.9a (Task 10 loopback smoke, DEBUG-only) ────────────────────────
        // Auto-connect to a dedicated server and auto-ready with NO user interaction, reusing the real
        // JoinGame + Ready path, so a one-click launcher can stand up server + 2 clients into a live match
        // (then F9 induces a one-peer desync). Never compiled into a release build.

        private bool _autoReady;

        /// <summary>Connect to a dedicated server (ip:port) and auto-ready. Made Visible so _Process polls the
        /// transport during the handshake; the lobby closes itself on StartGame (FireMatchStart → Close).</summary>
        public void AutoJoinDedicated(string ip, int port)
        {
            Show();                 // so _Process(delta) polls the transport during the handshake
            _autoReady = true;
            var err = _transport.JoinGame(ip, port);
            SetStatus(err == Error.Ok ? $"[AUTO] Connecting to {ip}:{port}…" : $"[AUTO] connect failed: {err}");
        }

        /// <summary>Fire the real Ready path once, when armed (called when the server's Hello arrives).</summary>
        private void TryAutoReady()
        {
            GD.Print($"[Lobby] TryAutoReady: autoReady={_autoReady} readyConfirmed={_readyConfirmed}");
            if (_autoReady && !_readyConfirmed) OnReadyPressed();
        }
#endif

        // ── Direct tab — button handlers ──────────────────────────────────────────

        private void OnHostPressed()
        {
            int port = (int)_portSpin.Value;
            var err  = _transport.HostGame(port);
            if (err == Error.Ok)
            {
                SetStatus($"Hosting on port {port}. Waiting for player…");
                _hostBtn.Disabled = true;
                _joinBtn.Disabled = true;
            }
            else
            {
                SetStatus($"Failed to open port {port}: {err}");
            }
        }

        private void OnJoinPressed()
        {
            string ip   = _ipField.Text.Trim();
            int    port = (int)_portSpin.Value;
            if (string.IsNullOrEmpty(ip)) { SetStatus("Enter a host IP address."); return; }

            var err = _transport.JoinGame(ip, port);
            if (err == Error.Ok)
            {
                SetStatus($"Connecting to {ip}:{port}…");
                _hostBtn.Disabled = true;
                _joinBtn.Disabled = true;
            }
            else
            {
                SetStatus($"Connect failed: {err}");
            }
        }

        // ── Online tab — button handlers ──────────────────────────────────────────

        private void OnFindMatchPressed()
        {
            string email    = _emailField.Text.Trim();
            string password = _passwordField.Text;
            if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password))
            {
                SetStatus("Enter email and password to find a match.");
                return;
            }

            _findBtn.Disabled      = true;
            _cancelFindBtn.Visible = true;
            _onlineModeActive      = true;

            // Fire-and-forget; NakamaService enqueues status updates.
            _ = ConnectAndSearchAsync(email, password);
        }

        private async Task ConnectAndSearchAsync(string email, string password)
        {
            try
            {
                if (!_nakama.IsConnected)
                    await _nakama.ConnectAsync(email, password);
                await _nakama.FindMatchAsync();
            }
            catch (Exception ex)
            {
                SetStatus($"Matchmaking error: {ex.Message}");
                _findBtn.Disabled      = false;
                _cancelFindBtn.Visible = false;
                _onlineModeActive      = false;
            }
        }

        private void OnCancelFindPressed()
        {
            _ = _nakama.CancelSearchAsync();
            _findBtn.Disabled      = false;
            _cancelFindBtn.Visible = false;
            _onlineModeActive      = false;
            SetStatus("Search cancelled.");
        }

        // ── Online — Nakama match found ────────────────────────────────────────────

        private void HandleNakamaMatchFound(MatchFoundInfo info)
        {
            // Both players connect to the configured dedicated server.
            // DedicatedServer.cs assigns factions via Hello packets.
            _cancelFindBtn.Visible = false;
            SetStatus($"Match found! Joining server {info.ServerIp}:{info.ServerPort}…");

            var err = _transport.JoinGame(info.ServerIp, info.ServerPort);
            if (err != Error.Ok)
            {
                SetStatus($"Failed to connect to game server: {err}");
                _findBtn.Disabled = false;
                _onlineModeActive = false;
            }
            // HandlePeerConnected will fire when DedicatedServer responds
        }

        // ── Shared lobby flow ──────────────────────────────────────────────────────

        private void OnReadyPressed()
        {
            _readyConfirmed    = true;
            _readyBtn.Disabled = true;
            _transport.SendReliable(TickCommandPacket.MakeReady(ScenarioHash));
#if DEBUG
            GD.Print($"[Lobby] Ready packet SENT (scenarioHash=0x{ScenarioHash:X8}).");
#endif
            string hashStr = ScenarioHash != 0 ? $"  [map 0x{ScenarioHash:X8}]" : "";
            SetStatus($"Ready! Waiting for other player…{hashStr}");
            TryStartGame();
        }

        private void OnCancelPressed()
        {
            _transport.Disconnect();
            _hostBtn.Disabled  = false;
            _joinBtn.Disabled  = false;
            _findBtn.Disabled  = false;
            _readyBtn.Visible  = false;
            _readyConfirmed     = false;
            _peerReadyConfirmed = false;
            _onlineModeActive   = false;
            SetStatus("Disconnected.");
        }

        // ── Transport callbacks ────────────────────────────────────────────────────

        private void HandlePeerConnected()
        {
#if DEBUG
            GD.Print($"[Lobby] peer connected (isHost={_transport.IsHost}, online={_onlineModeActive}, autoReady={_autoReady})");
#endif
            SetStatus("Connected! Click Ready when set up.");
            _readyBtn.Visible  = true;
            _readyBtn.Disabled = false;

            // In direct P2P mode, host sends Hello to identify itself.
            // In dedicated-server mode (direct or online), the server sends Hello first.
            if (_transport.IsHost && !_onlineModeActive)
                _transport.SendReliable(TickCommandPacket.MakeHello());
        }

        private void HandlePeerDisconnected()
        {
            SetStatus("Peer disconnected.");
            _readyBtn.Visible   = false;
            _hostBtn.Disabled   = false;
            _joinBtn.Disabled   = false;
            _findBtn.Disabled   = false;
            _readyConfirmed     = false;
            _peerReadyConfirmed = false;
            _onlineModeActive   = false;
        }

        private void HandlePacket(byte[] data, int len, int channel)
        {
            if (len < 1) return;
            var type = (PacketType)data[0];

            switch (type)
            {
                case PacketType.Hello:
#if DEBUG
                    GD.Print("[Lobby] Hello packet received from server.");
#endif
                    if (TickCommandPacket.TryReadHello(data, len, out var f)
                        && f != Core.Faction.Neutral)
                    {
                        _assignedFaction = f;
                        SetStatus($"Server assigned faction: {f}. Click Ready when set up.");
                    }
                    else
                    {
                        SetStatus("Host confirmed. Click Ready when set up.");
                    }
                    _readyBtn.Visible  = true;
                    _readyBtn.Disabled = false;
#if DEBUG
                    TryAutoReady();   // Story 1.9a loopback smoke: auto-ready once the server assigns a faction
#endif
                    break;

                case PacketType.Ready:
                    _peerReadyConfirmed = true;
                    if (TickCommandPacket.TryReadReady(data, len, out uint peerHash))
                    {
                        if (ScenarioHash != 0 && peerHash != 0 && peerHash != ScenarioHash)
                        {
                            // Mismatched scenario files = guaranteed desync. Block the match.
                            SetStatus(
                                $"MAP MISMATCH — cannot start!\n" +
                                $"Your map: 0x{ScenarioHash:X8}\n" +
                                $"Peer map:  0x{peerHash:X8}\n" +
                                "Both players must load the same scenario file.");
                            _peerReadyConfirmed = false; // don't allow TryStartGame
                            return;
                        }
                        SetStatus("Other player is ready!");
                    }
                    else
                    {
                        SetStatus("Other player is ready!");
                    }
                    TryStartGame();
                    break;

                case PacketType.StartGame:
                    FireMatchStart(isHost: false);
                    break;
            }
        }

        private void TryStartGame()
        {
            if (!_readyConfirmed || !_peerReadyConfirmed) return;

            if (_transport.IsHost && !_onlineModeActive)
            {
                _transport.SendReliable(TickCommandPacket.MakeStartGame(startTick: 0));
                FireMatchStart(isHost: true);
            }
            // In dedicated-server / online mode, StartGame comes from the server.
        }

        private void FireMatchStart(bool isHost)
        {
            Core.Faction localFaction = _assignedFaction != Core.Faction.Neutral
                ? _assignedFaction
                : (isHost ? Core.Faction.Player1 : Core.Faction.Player2);

            GD.Print($"[Lobby] Match starting — faction: {localFaction} (serverAssigned={_assignedFaction != Core.Faction.Neutral}, online={_onlineModeActive})");
            Close();
            OnMatchStart?.Invoke(isHost, localFaction);
        }

        // ── Tab switching ─────────────────────────────────────────────────────────

        private void ShowDirectTab()
        {
            _directTab.Visible = true;
            _onlineTab.Visible = false;
        }

        private void ShowOnlineTab()
        {
            _directTab.Visible = false;
            _onlineTab.Visible = true;
        }

        // ── UI construction ───────────────────────────────────────────────────────

        private void BuildUi()
        {
            var bg = new ColorRect { Color = new Color(0, 0, 0, 0.75f) };
            bg.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            AddChild(bg);

            var root = new PanelContainer();
            root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.Center);
            root.CustomMinimumSize = new Vector2(430, 380);
            root.GrowHorizontal = Control.GrowDirection.Both;
            root.GrowVertical   = Control.GrowDirection.Both;
            AddChild(root);

            var vbox = new VBoxContainer { CustomMinimumSize = new Vector2(410, 0) };
            vbox.AddThemeConstantOverride("separation", 10);
            root.AddChild(vbox);

            _titleLabel = MakeLabel("Multiplayer Lobby", 22, bold: true);
            vbox.AddChild(_titleLabel);
            vbox.AddChild(MakeSeparator());

            // ── Mode tab toggle ────────────────────────────────────────────────────
            var tabRow = new HBoxContainer();
            tabRow.AddThemeConstantOverride("separation", 4);
            var directTabBtn = MakeButton("Direct (LAN / IP)");
            var onlineTabBtn = MakeButton("Online (Matchmaking)");
            directTabBtn.CustomMinimumSize = new Vector2(195, 32);
            onlineTabBtn.CustomMinimumSize = new Vector2(195, 32);
            directTabBtn.Pressed += ShowDirectTab;
            onlineTabBtn.Pressed += ShowOnlineTab;
            tabRow.AddChild(directTabBtn);
            tabRow.AddChild(onlineTabBtn);
            vbox.AddChild(tabRow);

            vbox.AddChild(MakeSeparator());

            // ── Direct tab ─────────────────────────────────────────────────────────
            _directTab = new VBoxContainer();
            _directTab.AddThemeConstantOverride("separation", 8);

            var portRow = new HBoxContainer();
            portRow.AddChild(MakeLabel("Port:", 14));
            _portSpin = new SpinBox { MinValue = 1024, MaxValue = 65535, Value = 7777,
                                      CustomMinimumSize = new Vector2(110, 0) };
            portRow.AddChild(_portSpin);
            _directTab.AddChild(portRow);

            var ipRow = new HBoxContainer();
            ipRow.AddChild(MakeLabel("IP (join):", 14));
            _ipField = new LineEdit
            {
                PlaceholderText     = "192.168.1.x",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                CustomMinimumSize   = new Vector2(200, 0)
            };
            ipRow.AddChild(_ipField);
            _directTab.AddChild(ipRow);

            var actionRow = new HBoxContainer();
            actionRow.AddThemeConstantOverride("separation", 8);
            _hostBtn = MakeButton("Host Game");
            _joinBtn = MakeButton("Join Game");
            _hostBtn.Pressed += OnHostPressed;
            _joinBtn.Pressed += OnJoinPressed;
            actionRow.AddChild(_hostBtn);
            actionRow.AddChild(_joinBtn);
            _directTab.AddChild(actionRow);

            vbox.AddChild(_directTab);

            // ── Online tab ─────────────────────────────────────────────────────────
            _onlineTab = new VBoxContainer();
            _onlineTab.AddThemeConstantOverride("separation", 8);
            _onlineTab.Visible = false;

            var emailRow = new HBoxContainer();
            emailRow.AddChild(MakeLabel("Email:", 14));
            _emailField = new LineEdit
            {
                PlaceholderText     = "you@example.com",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                CustomMinimumSize   = new Vector2(230, 0)
            };
            emailRow.AddChild(_emailField);
            _onlineTab.AddChild(emailRow);

            var passRow = new HBoxContainer();
            passRow.AddChild(MakeLabel("Password:", 14));
            _passwordField = new LineEdit
            {
                PlaceholderText     = "••••••••",
                Secret              = true,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                CustomMinimumSize   = new Vector2(230, 0)
            };
            passRow.AddChild(_passwordField);
            _onlineTab.AddChild(passRow);

            var findRow = new HBoxContainer();
            findRow.AddThemeConstantOverride("separation", 8);
            _findBtn       = MakeButton("Find Match");
            _cancelFindBtn = MakeButton("Cancel Search");
            _cancelFindBtn.Visible = false;
            _findBtn.Pressed       += OnFindMatchPressed;
            _cancelFindBtn.Pressed += OnCancelFindPressed;
            findRow.AddChild(_findBtn);
            findRow.AddChild(_cancelFindBtn);
            _onlineTab.AddChild(findRow);

            _onlineTab.AddChild(MakeLabel(
                $"Matchmaking server: {NakamaHost}:{NakamaPort}\nGame server: {GameServerIp}:{GameServerPort}",
                11));

            vbox.AddChild(_onlineTab);

            // ── Shared bottom ──────────────────────────────────────────────────────
            vbox.AddChild(MakeSeparator());

            _statusLabel = MakeLabel("Choose Direct or Online to begin.", 13);
            _statusLabel.AutowrapMode = TextServer.AutowrapMode.Word;
            vbox.AddChild(_statusLabel);

            var bottomRow = new HBoxContainer();
            bottomRow.AddThemeConstantOverride("separation", 8);
            _readyBtn  = MakeButton("✓  Ready");
            _cancelBtn = MakeButton("Cancel");
            _readyBtn.Visible = false;
            _readyBtn.Pressed  += OnReadyPressed;
            _cancelBtn.Pressed += OnCancelPressed;
            bottomRow.AddChild(_readyBtn);
            bottomRow.AddChild(_cancelBtn);
            vbox.AddChild(bottomRow);
        }

        // ── UI helpers ────────────────────────────────────────────────────────────

        private static Label MakeLabel(string text, int size, bool bold = false)
        {
            var lbl = new Label { Text = text };
            lbl.AddThemeFontSizeOverride("font_size", size);
            return lbl;
        }

        private static Button MakeButton(string text) =>
            new Button { Text = text, CustomMinimumSize = new Vector2(130, 36) };

        private static HSeparator MakeSeparator() => new HSeparator();

        private void SetStatus(string msg) => _statusLabel.Text = msg;
    }
}
