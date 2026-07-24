#nullable enable
using Godot;
using System;
using System.Threading.Tasks;
using ProjectChimera.Core;            // Faction, FactionRegistry
using ProjectChimera.Core.Definitions; // ScenarioData, FactionDefinition, PlayerProfile, OnlineProfileSource wiring (Story 9.12)
using ProjectChimera.UI;              // FactionPalette, FactionPaletteGodot, HeroPickerOverlay (Story 9.12)
using ProjectChimera.UI.Components;   // ChimeraComponents (kit)
using ProjectChimera.UI.Theme;        // ThemeTokens, ThemeBuilder, AccentController
using GodotTheme = Godot.Theme;       // the ProjectChimera.UI.Theme namespace shadows the bare Theme type

namespace ProjectChimera.Multiplayer
{
    /// <summary>
    /// Pre-game lobby overlay (Story 9.7 rebuild, UX-DR69): Direct IP (LAN/dev) and Online (Nakama matchmaking)
    /// modes on the Chimera design kit. Adds an N-slot grid (2-4) with per-slot colorblind dots + glyphs
    /// (<see cref="FactionPalette"/>), ready pills, a ping display, a scenario header with a version-match hash
    /// check, a pre-match lobby chat pane (over the <see cref="PacketType.LobbyChat"/> packet), and a host
    /// <b>Start</b> button gated on <see cref="LobbyReadyModel.AllReady"/>.
    ///
    /// The lobby handshake wire (Hello / Ready / StartGame, <see cref="HandshakeGate"/>, PROTOCOL_VERSION checks) is
    /// UNCHANGED — this is a presentation rebuild over the same protocol. Slot occupancy shown is best-effort from
    /// what the client can observe (no new roster wire): the local assigned slot + peer connect/ready events.
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

        // ── Story 9.12: server-validated online hero rail (the LIVE production caller) ──────

        /// <summary>The ONLINE hero-profile source over the owned <see cref="_nakama"/> (server-RPC-only writes, never a
        /// raw client storage write). Constructed in <see cref="Initialize"/> so the online picker is backed by the
        /// server storage object, NOT the offline <c>LocalProfileSource</c>.</summary>
        private OnlineProfileSource? _onlineProfiles;

        /// <summary>The reused hero picker, put in ONLINE mode (attestation-gated). Built lazily on first online show.</summary>
        private HeroPickerOverlay? _onlinePicker;

        /// <summary>True once the player has picked a hero whose profile the server ATTESTED — the fail-closed gate on
        /// Ready for an online match. Reset on close/cancel/disconnect.</summary>
        private bool _onlineHeroAttested;

        /// <summary>Story 9.12: live providers for the scenario + per-slot faction defs the online picker needs for
        /// compatibility (set by <c>MatchLifecycleController</c>; read at picker-build time, after setup completes).</summary>
        public Func<ScenarioData?>? ScenarioProvider { get; set; }
        public Func<FactionDefinition?[]>? SlotFactionDefsProvider { get; set; }

        // ── Nakama config (Inspector-exported on MainScene, passed via Initialize) ─

        public string NakamaHost    { get; set; } = "localhost";
        public int    NakamaPort    { get; set; } = 7350;
        public string NakamaKey     { get; set; } = "defaultkey";
        public string GameServerIp  { get; set; } = "localhost";
        public int    GameServerPort { get; set; } = 7777;

        /// <summary>Story 9.7: the number of player slots this match expects (from the loaded scenario's
        /// PlayerSlots). Drives the N-slot grid + the all-ready Start gate. Defaults to 2.</summary>
        public int PlayerCount { get; set; } = 2;

        // ── Scenario hash (set by MainScene after scenario load) ─────────────────

        /// <summary>
        /// FNV-1a hash of the current scenario file, computed by MainScene after loading.
        /// Retained for status-text display only (Story 9.4 moved the wire to <see cref="MatchAgreementHash"/>).
        /// 0 = not set / no file on disk.
        /// </summary>
        public uint ScenarioHash { get; set; }

        /// <summary>
        /// Story 9.4 — the 64-bit start-state-agreement value (ruleset + initial-delay + roster + faction-count +
        /// start-state), computed by MainScene and SENT on the widened Ready packet. The host compares the peer's
        /// value with its own via the fail-closed <see cref="HandshakeGate"/>; any 0 or mismatch blocks the start.
        /// </summary>
        public ulong MatchAgreementHash { get; set; }

        /// <summary>
        /// Story 9.4 — true when this match's delay is SERVER-dictated (a dedicated server assigned our faction, or
        /// online/Nakama mode), so the client must run as a delay follower (<c>LockstepManager.ServerDictatedDelay</c>).
        /// False for a P2P host match. Set in <see cref="FireMatchStart"/> before <see cref="Close"/> resets state.
        /// </summary>
        public bool ServerDictated { get; private set; }

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

        private Label   _titleLabel  = null!;
        private Label   _statusLabel = null!;
        private Label   _scenarioHeader = null!;
        private Label   _pingLabel   = null!;
        private Button  _readyBtn    = null!;
        private Button  _startBtn    = null!;
        private Button  _cancelBtn   = null!;
        private VBoxContainer _slotGrid = null!;
        private RichTextLabel _chatLog  = null!;
        private LineEdit      _chatInput = null!;

        // ── Kit context ────────────────────────────────────────────────────────────

        private GodotTheme       _theme  = null!;
        private AccentController? _accent;

        // ── State ─────────────────────────────────────────────────────────────────

        private bool         _readyConfirmed;
        private bool         _peerReadyConfirmed;
        private Core.Faction _assignedFaction = Core.Faction.Neutral;
        private bool         _onlineModeActive;
        private bool         _isHostRole;

        /// <summary>Story 9.7: the N-slot readiness model — the presentation + all-ready-Start-gate source.</summary>
        private LobbyReadyModel _readyModel = new(FactionRegistry.PLAYER_COUNT);

        // Story 9.7 (P2): scratch for decoding the server's authoritative lobby-roster snapshot.
        private readonly bool[] _rosterOccupied = new bool[TickCommandPacket.MAX_ROSTER_SLOTS];
        private readonly bool[] _rosterReady    = new bool[TickCommandPacket.MAX_ROSTER_SLOTS];

        // ── Lobby ping (Story 9.7) ─────────────────────────────────────────────────

        private byte   _pingSeq;
        private uint   _lastPingMs;
        private double _sincePing;
        private int    _lastRttMs = -1;
        private const double LOBBY_PING_INTERVAL_SEC = 1.0;

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
                GameServerPort = GameServerPort,
                TargetPlayerCount = PlayerCount
            };
            _nakama.OnMatchFound  += HandleNakamaMatchFound;
            _nakama.OnStatusText  += SetStatus;
            _nakama.OnDisconnected += () => SetStatus("Matchmaking server disconnected.");

            // Story 9.12: construct the ONLINE hero-profile rail over the owned _nakama. This is the production caller
            // that makes the server-validated rail LIVE — the online picker (surfaced before Ready) is backed by this
            // server storage object, never the offline LocalProfileSource. Writes route only through the validating RPC.
            _onlineProfiles = new OnlineProfileSource(_nakama);
        }

        public override void _Ready()
        {
            Layer = 20;
            EnsureKitInitialized();
            BuildUi();
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

        // ── Visibility ────────────────────────────────────────────────────────────

        public new void Show()
        {
            Visible = true;
            _readyModel = new LobbyReadyModel(FactionRegistry.PLAYER_COUNT);
            _readyBtn.Visible = false;
            _startBtn.Visible = false;

            // Story 9.7 (P4): P2P Direct Host uses ENet maxPeers=1 — it can only ever seat 2. For an N>2 scenario,
            // disable the Host path (use a dedicated server / matchmaking) so a 3–4-slot sim can't start a 2-human
            // match. Join (to a dedicated server) and Online matchmaking stay available.
            bool p2pHostAllowed = PlayerCount <= 2;
            _hostBtn.Disabled = !p2pHostAllowed;
            _hostBtn.TooltipText = p2pHostAllowed
                ? ""
                : $"This {PlayerCount}-player scenario needs a dedicated server — Direct Host (P2P) seats only 2.";

            RebuildSlotGrid();
            UpdateScenarioHeader();
            SetStatus(p2pHostAllowed
                ? "Enter IP to join, click Host, or use Online matchmaking."
                : $"{PlayerCount}-player scenario: Join a dedicated server or use Online matchmaking (P2P Host seats only 2).");
        }

        public void Close()
        {
            Visible = false;
            _readyConfirmed     = false;
            _peerReadyConfirmed = false;
            _assignedFaction    = Core.Faction.Neutral;
            _onlineHeroAttested = false; // Story 9.12: re-require attestation next online match
            _readyModel.Reset();
        }

        // ── Frame ────────────────────────────────────────────────────────────────

        public override void _Process(double delta)
        {
            if (!Visible) return;
            _transport.Poll();
            _nakama.DrainEvents();   // marshal Nakama background events to main thread

            // Story 9.7: lobby-side RTT probe so the grid can show a real ping (the server + a P2P peer both echo
            // a Ping with a Pong). Only while connected.
            if (_transport.IsConnected)
            {
                _sincePing += delta;
                if (_sincePing >= LOBBY_PING_INTERVAL_SEC)
                {
                    _sincePing  = 0;
                    _lastPingMs = (uint)Time.GetTicksMsec();
                    _transport.SendReliable(TickCommandPacket.MakePing(_pingSeq, _lastPingMs));
                    _pingSeq++;
                }
            }

#if DEBUG
            // Loopback smoke (auto-join): ready as soon as the connection is established, INDEPENDENT of the
            // server's Hello packet arriving.
            if (_autoReady && !_readyConfirmed && _transport.IsConnected) OnReadyPressed();
#endif
        }

#if DEBUG
        // ── Story 1.9a (Task 10 loopback smoke, DEBUG-only) ────────────────────────

        private bool _autoReady;

        /// <summary>Connect to a dedicated server (ip:port) and auto-ready with NO user interaction.</summary>
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
                _isHostRole = true;
                SetStatus($"Hosting on port {port}. Waiting for player…");
                _hostBtn.Disabled = true;
                _joinBtn.Disabled = true;
                // Host is the local player in slot 0 (P2P) until/unless a server assigns otherwise.
                _readyModel.SetOccupied(0, true);
                RebuildSlotGrid();
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
            _nakama.TargetPlayerCount = PlayerCount;

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
            _cancelFindBtn.Visible = false;
            SetStatus($"Match found! Joining server {info.ServerIp}:{info.ServerPort}…");

            var err = _transport.JoinGame(info.ServerIp, info.ServerPort);
            if (err != Error.Ok)
            {
                SetStatus($"Failed to connect to game server: {err}");
                _findBtn.Disabled = false;
                _onlineModeActive = false;
            }
        }

        // ── Shared lobby flow ──────────────────────────────────────────────────────

        private void OnReadyPressed()
        {
            // Story 9.12: FAIL-CLOSED online hero gate. In an online (Nakama) match the player cannot Ready until a hero
            // profile has ATTESTED with the server. Surface the online picker (backed by OnlineProfileSource) instead of
            // readying; the launch callback (OnOnlineHeroChosen) flips _onlineHeroAttested and re-enters this path.
            if (_onlineModeActive && !_onlineHeroAttested)
            {
                ShowOnlineHeroPicker();
                return;
            }

            _readyConfirmed    = true;
            _readyBtn.Disabled = true;
            _readyModel.SetOccupied(LocalSlot(), true);
            _readyModel.SetReady(LocalSlot(), true);
            // Story 9.4: the widened Ready carries our PROTOCOL_VERSION + the 64-bit match-agreement hash.
            _transport.SendReliable(TickCommandPacket.MakeReady(TickCommandPacket.PROTOCOL_VERSION, MatchAgreementHash));
#if DEBUG
            GD.Print($"[Lobby] Ready packet SENT (protocol v{TickCommandPacket.PROTOCOL_VERSION}, match hash=0x{MatchAgreementHash:X16}).");
#endif
            string hashStr = ScenarioHash != 0 ? $"  [map 0x{ScenarioHash:X8}]" : "";
            SetStatus($"Ready! Waiting for other player…{hashStr}");
            RebuildSlotGrid();
            TryStartGame();
        }

        private void OnStartPressed()
        {
            // Host-only explicit Start (P2P). Enabled only when all-ready; delegates to the same TryStartGame path.
            TryStartGame();
        }

        // ── Story 9.12: online hero picker (the live server-validated rail) ─────────────────

        /// <summary>Surface the ONLINE hero picker (backed by <see cref="_onlineProfiles"/>), attestation-gated. Built
        /// lazily so the scenario + per-slot faction defs (populated during setup) are read at show time.</summary>
        private void ShowOnlineHeroPicker()
        {
            EnsureOnlinePicker();
            _onlinePicker!.SetLocalFactionFilter(ResolveLocalFactionDef()); // refresh in case the server just assigned us
            _onlinePicker.ShowForOnline();
            SetStatus("Pick and attest your online hero with the server, then you can Ready.");
        }

        private void EnsureOnlinePicker()
        {
            if (_onlinePicker != null) return;
            _onlinePicker = new HeroPickerOverlay();
            AddChild(_onlinePicker);
            ScenarioData? scenario = ScenarioProvider?.Invoke();
            FactionDefinition?[] defs = SlotFactionDefsProvider?.Invoke() ?? Array.Empty<FactionDefinition?>();
            // Back the picker with the SERVER source (never the offline LocalProfileSource) and enable the attest gate.
            _onlinePicker.Initialize(scenario, _onlineProfiles!, defs, OnOnlineHeroChosen);
            _onlinePicker.EnableOnlineAttestation(_nakama);
        }

        /// <summary>The launch callback from the online picker: fires ONLY after a successful server attestation (the
        /// picker's fail-closed <see cref="HeroPickerOverlay"/> gate). A null profile ("play without a hero") does NOT
        /// satisfy the online gate — the player stays blocked. On a real attested profile, flip the gate and re-enter
        /// the Ready path.</summary>
        private void OnOnlineHeroChosen(PlayerProfile? profile)
        {
            if (profile == null) return; // fail-closed: an online match requires an attested hero
            // P3: a late attestation must not ready a cancelled/disconnected lobby. If online mode was torn down while
            // the attest was in flight (Cancel / peer disconnect resets _onlineModeActive), drop the result silently —
            // the transport is gone and OnReadyPressed would otherwise skip the online gate and "ready" a dead match.
            if (!_onlineModeActive || !_transport.IsConnected) return;
            _onlineHeroAttested = true;
            SetStatus($"Hero '{(string.IsNullOrEmpty(profile.DisplayName) ? profile.ProfileId : profile.DisplayName)}' " +
                      "attested by the server. Readying…");
            OnReadyPressed(); // now passes the online gate → the real Ready path
        }

        /// <summary>The local player's server-assigned <see cref="FactionDefinition"/> for the online compatibility
        /// filter, or null when unassigned (Neutral) / out of range — then the picker stays slot-agnostic.</summary>
        private FactionDefinition? ResolveLocalFactionDef()
        {
            if (_assignedFaction == Core.Faction.Neutral) return null;
            FactionDefinition?[] defs = SlotFactionDefsProvider?.Invoke() ?? Array.Empty<FactionDefinition?>();
            int idx = (int)_assignedFaction; // SlotFactionDefs is indexed by (int)Faction (Player1 == 1)
            return idx >= 0 && idx < defs.Length ? defs[idx] : null;
        }

        private void OnCancelPressed()
        {
            _transport.Disconnect();
            _hostBtn.Disabled  = false;
            _joinBtn.Disabled  = false;
            _findBtn.Disabled  = false;
            _readyBtn.Visible  = false;
            _startBtn.Visible  = false;
            _readyConfirmed     = false;
            _peerReadyConfirmed = false;
            _onlineModeActive   = false;
            _isHostRole         = false;
            _onlineHeroAttested = false; // Story 9.12
            _readyModel.Reset();
            RebuildSlotGrid();
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

            // P2P HOST knows it is a 2-player match the instant a peer connects (ENet maxPeers=1): mark both slots.
            // Story 9.7 (P2b): a JOINER (dedicated OR P2P) does NOT infer occupancy from the raw ENet connect — it
            // waits for the authoritative Hello (P2P host confirm → slots 0+1) or the server LobbyRoster (dedicated),
            // so no phantom slot lingers on the dedicated path.
            if (_transport.IsHost && !_onlineModeActive)
            {
                _readyModel.SetOccupied(0, true);
                _readyModel.SetOccupied(1, true);
                _transport.SendReliable(TickCommandPacket.MakeHello());
            }
            RebuildSlotGrid();
        }

        private void HandlePeerDisconnected()
        {
            SetStatus("Peer disconnected.");
            _readyBtn.Visible   = false;
            _startBtn.Visible   = false;
            _hostBtn.Disabled   = false;
            _joinBtn.Disabled   = false;
            _findBtn.Disabled   = false;
            _readyConfirmed     = false;
            _peerReadyConfirmed = false;
            _onlineModeActive   = false;
            _onlineHeroAttested = false; // Story 9.12
            _readyModel.Reset();
            RebuildSlotGrid();
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
                    // Story 9.4: validate the peer/server PROTOCOL_VERSION fail-closed (the D3.8 gap).
                    if (!TickCommandPacket.TryReadHello(data, len, out var f, out ushort helloVer)
                        || helloVer != TickCommandPacket.PROTOCOL_VERSION)
                    {
                        SetStatus("CANNOT START — protocol version mismatch.\n" +
                                  $"Server protocol: v{helloVer}\n" +
                                  $"Your protocol:  v{TickCommandPacket.PROTOCOL_VERSION}\n" +
                                  "Both players must run the same game build.");
                        _readyBtn.Visible = false;
                        break;
                    }
                    if (f != Core.Faction.Neutral)
                    {
                        // Dedicated server assigned our faction/slot. Mark OUR slot; the server LobbyRoster fills the rest.
                        _assignedFaction = f;
                        _readyModel.SetOccupied(LocalSlot(), true);
                        SetStatus($"Server assigned faction: {f}. Click Ready when set up.");
                    }
                    else
                    {
                        // P2P host confirmed (Neutral Hello): a 2-player match — mark host (slot 0) + self (slot 1).
                        _readyModel.SetOccupied(0, true);
                        _readyModel.SetOccupied(1, true);
                        SetStatus("Host confirmed. Click Ready when set up.");
                    }
                    _readyBtn.Visible  = true;
                    _readyBtn.Disabled = false;
                    RebuildSlotGrid();
#if DEBUG
                    TryAutoReady();   // Story 1.9a loopback smoke: auto-ready once the server assigns a faction
#endif
                    break;

                case PacketType.Ready:
                {
                    // Story 7.7 / 9.4 — the pure HandshakeGate decision over the 64-bit match-agreement hash.
                    bool parsed = TickCommandPacket.TryReadReady(data, len, out ushort peerVer, out ulong peerHash);
                    if (parsed && peerVer != TickCommandPacket.PROTOCOL_VERSION)
                    {
                        SetStatus("CANNOT START — peer protocol version mismatch.\n" +
                                  $"Peer protocol: v{peerVer}\n" +
                                  $"Your protocol: v{TickCommandPacket.PROTOCOL_VERSION}");
                        _peerReadyConfirmed = false;
                        return;
                    }
                    string? block = HandshakeGate.CheckStart(MatchAgreementHash, peerHash, peerHashParsed: parsed);
                    if (block != null)
                    {
                        SetStatus(block);
                        _peerReadyConfirmed = false; // don't allow TryStartGame
                        return;
                    }
                    _peerReadyConfirmed = true;
                    // P2P: the ready peer occupies slot 1 (from the host's view) or slot 0 (from the joiner's view).
                    int peerSlot = _isHostRole ? 1 : 0;
                    _readyModel.SetOccupied(peerSlot, true);
                    _readyModel.SetReady(peerSlot, true);
                    SetStatus("Other player is ready!");
                    RebuildSlotGrid();
                    TryStartGame();
                    break;
                }

                case PacketType.StartGame:
                    FireMatchStart(isHost: false);
                    break;

                case PacketType.Ping:
                    // Answer an inbound lobby ping so the sender can measure RTT.
                    if (len >= 6)
                        _transport.SendReliable(TickCommandPacket.MakePong(data[1],
                            (uint)(data[2] | (data[3] << 8) | (data[4] << 16) | (data[5] << 24))));
                    break;

                case PacketType.Pong:
                    if (TickCommandPacket.TryReadPong(data, len, out byte seq, out uint senderMs)
                        && seq == (byte)(_pingSeq - 1))
                    {
                        int rtt = (int)((uint)Time.GetTicksMsec() - senderMs);
                        if (rtt >= 0 && rtt < 10_000) { _lastRttMs = rtt; UpdatePing(); }
                    }
                    break;

                case PacketType.LobbyChat:
                    if (TickCommandPacket.TryReadLobbyChat(data, len, out Core.Faction chatFaction, out string chatMsg))
                        AppendChat(chatFaction, chatMsg);
                    break;

                case PacketType.LobbyRoster:
                    // Story 9.7 (P2): the authoritative pre-match roster/ready snapshot from a dedicated server —
                    // the ONLY way this client observes remote slots on the dedicated path. Drives the N-slot grid.
                    if (TickCommandPacket.TryReadLobbyRoster(data, len, out int rn, _rosterOccupied, _rosterReady))
                    {
                        for (int s = 0; s < rn; s++)
                        {
                            _readyModel.SetOccupied(s, _rosterOccupied[s]);
                            _readyModel.SetReady(s, _rosterReady[s]);
                        }
                        RebuildSlotGrid();
                        TryStartGame(); // re-evaluate the (host-only P2P) start gate / button-enable
                    }
                    break;
            }
        }

        private void TryStartGame()
        {
            // Story 9.7 (P4): ONE predicate — LobbyReadyModel.AllReady(PlayerCount) — gates BOTH the Start-button
            // enable AND the start execution (no more two-peer _readyConfirmed/_peerReadyConfirmed disagreeing with
            // the button). For N=2 P2P this equals both peers ready; for N>2 dedicated it reflects the server roster.
            bool allReady = _readyModel.AllReady(PlayerCount);
            if (_startBtn != null) _startBtn.Disabled = !allReady;

            if (!allReady) return;

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

            ServerDictated = _assignedFaction != Core.Faction.Neutral || _onlineModeActive;

            GD.Print($"[Lobby] Match starting — faction: {localFaction} (serverAssigned={_assignedFaction != Core.Faction.Neutral}, online={_onlineModeActive}, serverDictated={ServerDictated})");
            Close();
            OnMatchStart?.Invoke(isHost, localFaction);
        }

        // ── Lobby chat ─────────────────────────────────────────────────────────────

        private void OnChatSubmitted(string text)
        {
            string msg = text.Trim();
            _chatInput.Text = "";
            if (string.IsNullOrEmpty(msg) || !_transport.IsConnected) return;
            Core.Faction f = LobbyLocalFaction();
            _transport.SendReliable(TickCommandPacket.MakeLobbyChat(f, msg));
            AppendChat(f, msg); // optimistic echo (the dedicated server rebroadcasts to sender too)
        }

        private void AppendChat(Core.Faction faction, string message)
        {
            var entry = FactionPalette.ForFaction(faction);
            string safe = message.Replace("[", "（").Replace("]", "）");
            _chatLog.PushColor(entry.ToColor());
            _chatLog.AddText($"{entry.Glyph} {entry.Name}: ");
            _chatLog.Pop();
            _chatLog.AddText(safe + "\n");
        }

        // ── Slot grid + header ──────────────────────────────────────────────────────

        private int LocalSlot()
            => _assignedFaction != Core.Faction.Neutral ? (int)_assignedFaction - 1 : (_isHostRole ? 0 : 1);

        private Core.Faction LobbyLocalFaction()
            => _assignedFaction != Core.Faction.Neutral ? _assignedFaction
             : FactionRegistry.ToFaction(_isHostRole ? 0 : 1);

        /// <summary>Rebuild the N-slot grid: each row = a colorblind dot + glyph + faction name + a ready pill.</summary>
        private void RebuildSlotGrid()
        {
            foreach (Node c in _slotGrid.GetChildren()) c.QueueFree();

            int n = PlayerCount < 2 ? 2 : (PlayerCount > FactionRegistry.PLAYER_COUNT ? FactionRegistry.PLAYER_COUNT : PlayerCount);

            // Story 9.14: compute each slot's canonical TEAM id from the applied scenario's per-slot teams (the SAME
            // AllianceSeeder mapping the sim seeds into AllianceStore), so the lobby DISPLAYS teams — the lobby never
            // authors them. Start from the FFA default (TeamId[f]==f); ComputeTeamIds overwrites only teamed members.
            // Null scenario (not yet loaded) ⇒ FFA, so every slot shows its own-faction glyph (byte-safe presentation).
            ScenarioData? teamScenario = ScenarioProvider?.Invoke();
            int[] teamIdByFaction = new int[FactionRegistry.FACTION_ARRAY_SIZE];
            for (int f = 0; f < teamIdByFaction.Length; f++) teamIdByFaction[f] = f;
            Core.AllianceSeeder.ComputeTeamIds(teamScenario, teamIdByFaction);

            for (int slot = 0; slot < n; slot++)
            {
                var entry = FactionPalette.ForSlot(slot);
                var row = new HBoxContainer();
                row.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S2));

                // Colorblind dot (color) — never the sole signal; the glyph + name accompany it.
                var dot = new ColorRect
                {
                    Color = entry.ToColor(),
                    CustomMinimumSize = new Vector2(14, 14),
                    SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                };
                row.AddChild(dot);

                var glyphLabel = new Label { Text = entry.Glyph };
                glyphLabel.AddThemeColorOverride("font_color", entry.ToColor());
                row.AddChild(glyphLabel);

                var nameLabel = new Label { Text = entry.Name, CustomMinimumSize = new Vector2(60, 0) };
                row.AddChild(nameLabel);

                // Story 9.14: per-slot TEAM glyph, keyed by the canonical team id (own-faction glyph when Team==0/FFA).
                // Colorblind rule: the palette Glyph (a distinct shape) carries the team, never color alone — a "Team"
                // label + the team-representative Name accompany it. All allies share ONE canonical id → ONE glyph.
                int factionIdx = slot + 1;
                int canonicalTeamId = factionIdx < teamIdByFaction.Length ? teamIdByFaction[factionIdx] : 0;
                var teamEntry = FactionPalette.ForFaction((Core.Faction)canonicalTeamId);
                var teamLabel = new Label
                {
                    Text = $"Team {teamEntry.Glyph}",
                    CustomMinimumSize = new Vector2(64, 0),
                    TooltipText = $"Team {teamEntry.Name}",
                };
                teamLabel.AddThemeColorOverride("font_color", teamEntry.ToColor());
                row.AddChild(teamLabel);

                bool occupied = _readyModel.IsOccupied(slot);
                bool ready    = _readyModel.IsReady(slot);
                string tagText = !occupied ? "OPEN" : ready ? "READY" : "NOT READY";
                var tagVariant = !occupied ? ChimeraComponents.TagVariant.Neutral
                               : ready ? ChimeraComponents.TagVariant.Ok
                               : ChimeraComponents.TagVariant.Danger;
                row.AddChild(ChimeraComponents.Tag(tagText, tagVariant));

                _slotGrid.AddChild(row);
            }

            UpdatePing();
            if (_startBtn != null)
            {
                bool canStart = _transport != null && _transport.IsHost && !_onlineModeActive;
                _startBtn.Visible  = canStart;
                _startBtn.Disabled = !(_readyModel.AllReady(PlayerCount) || (_readyConfirmed && _peerReadyConfirmed));
            }
        }

        private void UpdateScenarioHeader()
        {
            // Story 9.7 (UX-DR69): a scenario header with the version-match hash check surfaced. A computed
            // (non-zero) MatchAgreementHash means a validated start-state exists on this client; 0 means the lobby
            // will BLOCK the start (HandshakeGate fail-closed).
            string hashState = MatchAgreementHash != 0
                ? $"hash 0x{MatchAgreementHash:X16} ✓"
                : "hash NOT COMPUTED — start will be blocked";
            _scenarioHeader.Text = $"Scenario version-match: {hashState}   ·   {PlayerCount} players";
        }

        private void UpdatePing()
        {
            _pingLabel.Text = _lastRttMs < 0 ? "Ping: —" : $"Ping: {_lastRttMs} ms";
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

        // ── UI construction (Chimera kit) ───────────────────────────────────────────

        private void BuildUi()
        {
            var root = new Control();
            root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            root.Theme = _theme;
            root.MouseFilter = Control.MouseFilterEnum.Stop;
            AddChild(root);

            var bg = new ColorRect { Color = new Color(0, 0, 0, 0.75f) };
            bg.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            root.AddChild(bg);

            var center = new CenterContainer();
            center.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            root.AddChild(center);

            var panel = ChimeraComponents.Panel();
            panel.CustomMinimumSize = new Vector2(560, 560);
            center.AddChild(panel);

            var vbox = new VBoxContainer { CustomMinimumSize = new Vector2(520, 0) };
            vbox.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S3));
            panel.AddChild(vbox);

            _titleLabel = MakeHeading("MULTIPLAYER LOBBY", ThemeTokens.T2xl);
            vbox.AddChild(_titleLabel);

            _scenarioHeader = MakeBody("Scenario version-match: —", ThemeTokens.TextMid, ThemeTokens.Tsm);
            vbox.AddChild(_scenarioHeader);

            // ── Slot grid ──────────────────────────────────────────────────────────
            var slotHeader = MakeBody("Slots", ThemeTokens.TextLo, ThemeTokens.Txs);
            vbox.AddChild(slotHeader);
            _slotGrid = new VBoxContainer();
            _slotGrid.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S1));
            vbox.AddChild(_slotGrid);

            _pingLabel = MakeBody("Ping: —", ThemeTokens.TextLo, ThemeTokens.Txs);
            vbox.AddChild(_pingLabel);

            vbox.AddChild(new HSeparator());

            // ── Mode tab toggle ─────────────────────────────────────────────────────
            var tabRow = new HBoxContainer();
            tabRow.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S1));
            var directTabBtn = ChimeraComponents.Button("Direct (LAN / IP)", ChimeraComponents.ButtonVariant.Secondary);
            var onlineTabBtn = ChimeraComponents.Button("Online (Matchmaking)", ChimeraComponents.ButtonVariant.Secondary);
            directTabBtn.Pressed += ShowDirectTab;
            onlineTabBtn.Pressed += ShowOnlineTab;
            tabRow.AddChild(directTabBtn);
            tabRow.AddChild(onlineTabBtn);
            vbox.AddChild(tabRow);

            // ── Direct tab ───────────────────────────────────────────────────────────
            _directTab = new VBoxContainer();
            ((VBoxContainer)_directTab).AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S2));

            var portRow = new HBoxContainer();
            portRow.AddChild(ChimeraComponents.FieldLabel("Port"));
            _portSpin = new SpinBox { MinValue = 1024, MaxValue = 65535, Value = 7777,
                                      CustomMinimumSize = new Vector2(110, 0) };
            portRow.AddChild(_portSpin);
            ((VBoxContainer)_directTab).AddChild(portRow);

            var ipRow = new HBoxContainer();
            ipRow.AddChild(ChimeraComponents.FieldLabel("IP (join)"));
            _ipField = ChimeraComponents.Input("192.168.1.x");
            _ipField.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            ipRow.AddChild(_ipField);
            ((VBoxContainer)_directTab).AddChild(ipRow);

            var actionRow = new HBoxContainer();
            actionRow.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S2));
            _hostBtn = ChimeraComponents.Button("Host Game", ChimeraComponents.ButtonVariant.Primary);
            _joinBtn = ChimeraComponents.Button("Join Game", ChimeraComponents.ButtonVariant.Secondary);
            _hostBtn.Pressed += OnHostPressed;
            _joinBtn.Pressed += OnJoinPressed;
            actionRow.AddChild(_hostBtn);
            actionRow.AddChild(_joinBtn);
            ((VBoxContainer)_directTab).AddChild(actionRow);

            vbox.AddChild(_directTab);

            // ── Online tab ───────────────────────────────────────────────────────────
            _onlineTab = new VBoxContainer { Visible = false };
            ((VBoxContainer)_onlineTab).AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S2));

            var emailRow = new HBoxContainer();
            emailRow.AddChild(ChimeraComponents.FieldLabel("Email"));
            _emailField = ChimeraComponents.Input("you@example.com");
            _emailField.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            emailRow.AddChild(_emailField);
            ((VBoxContainer)_onlineTab).AddChild(emailRow);

            var passRow = new HBoxContainer();
            passRow.AddChild(ChimeraComponents.FieldLabel("Password"));
            _passwordField = ChimeraComponents.Input("••••••••");
            _passwordField.Secret = true;
            _passwordField.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            passRow.AddChild(_passwordField);
            ((VBoxContainer)_onlineTab).AddChild(passRow);

            var findRow = new HBoxContainer();
            findRow.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S2));
            _findBtn       = ChimeraComponents.Button("Find Match", ChimeraComponents.ButtonVariant.Primary);
            _cancelFindBtn = ChimeraComponents.Button("Cancel Search", ChimeraComponents.ButtonVariant.Secondary);
            _cancelFindBtn.Visible = false;
            _findBtn.Pressed       += OnFindMatchPressed;
            _cancelFindBtn.Pressed += OnCancelFindPressed;
            findRow.AddChild(_findBtn);
            findRow.AddChild(_cancelFindBtn);
            ((VBoxContainer)_onlineTab).AddChild(findRow);

            ((VBoxContainer)_onlineTab).AddChild(MakeBody(
                $"Matchmaking server: {NakamaHost}:{NakamaPort}\nGame server: {GameServerIp}:{GameServerPort}",
                ThemeTokens.TextLo, ThemeTokens.Txs));

            vbox.AddChild(_onlineTab);

            vbox.AddChild(new HSeparator());

            // ── Lobby chat ───────────────────────────────────────────────────────────
            _chatLog = new RichTextLabel
            {
                BbcodeEnabled     = true,
                ScrollFollowing   = true,
                CustomMinimumSize = new Vector2(0, 100),
            };
            vbox.AddChild(_chatLog);

            var chatRow = new HBoxContainer();
            chatRow.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S1));
            _chatInput = ChimeraComponents.Input("Type to chat in the lobby…");
            _chatInput.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            _chatInput.MaxLength = 200;
            _chatInput.TextSubmitted += OnChatSubmitted;
            chatRow.AddChild(_chatInput);
            vbox.AddChild(chatRow);

            vbox.AddChild(new HSeparator());

            _statusLabel = MakeBody("Choose Direct or Online to begin.", ThemeTokens.TextMid, ThemeTokens.Tsm);
            _statusLabel.AutowrapMode = TextServer.AutowrapMode.Word;
            vbox.AddChild(_statusLabel);

            // ── Shared bottom (Ready / Start / Cancel) ───────────────────────────────
            var bottomRow = new HBoxContainer();
            bottomRow.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S2));
            _readyBtn = ChimeraComponents.Button("✓  Ready", ChimeraComponents.ButtonVariant.Primary);
            _startBtn = ChimeraComponents.Button("Start", ChimeraComponents.ButtonVariant.Primary);
            _cancelBtn = ChimeraComponents.Button("Cancel", ChimeraComponents.ButtonVariant.Danger);
            _readyBtn.Visible = false;
            _startBtn.Visible = false;
            _startBtn.Disabled = true;
            _readyBtn.Pressed  += OnReadyPressed;
            _startBtn.Pressed  += OnStartPressed;
            _cancelBtn.Pressed += OnCancelPressed;
            bottomRow.AddChild(_readyBtn);
            bottomRow.AddChild(_startBtn);
            bottomRow.AddChild(_cancelBtn);
            vbox.AddChild(bottomRow);
        }

        // ── UI helpers ────────────────────────────────────────────────────────────

        private Label MakeHeading(string text, StringName sizeToken)
        {
            var l = new Label { Text = text };
            l.AddThemeFontOverride("font", _theme.GetFont(ThemeTokens.FontDisplay, ThemeTokens.Type));
            l.AddThemeFontSizeOverride("font_size", _theme.GetFontSize(sizeToken, ThemeTokens.Type));
            l.AddThemeColorOverride("font_color", _theme.GetColor(ThemeTokens.TextHi, ThemeTokens.Type));
            return l;
        }

        private Label MakeBody(string text, StringName colorToken, StringName sizeToken)
        {
            var l = new Label { Text = text };
            l.AddThemeFontOverride("font", _theme.GetFont(ThemeTokens.FontUi, ThemeTokens.Type));
            l.AddThemeFontSizeOverride("font_size", _theme.GetFontSize(sizeToken, ThemeTokens.Type));
            l.AddThemeColorOverride("font_color", _theme.GetColor(colorToken, ThemeTokens.Type));
            return l;
        }

        private void SetStatus(string msg) => _statusLabel.Text = msg;
    }
}
