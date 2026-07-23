#nullable enable
using Godot;
using ProjectChimera.Core;
using ProjectChimera.Core.Sim;            // SimulationHost (the validated spine built by ServerBootstrap)
using ProjectChimera.Multiplayer.Server;  // ServerHost (strict-majority quorum + DesyncAlert/HALT)

namespace ProjectChimera.Multiplayer
{
    /// <summary>
    /// Dedicated relay server for 1v1 matches.
    ///
    /// Topology: two clients connect to this server. The server:
    ///   1. Assigns factions (slot 0 = Player1, slot 1 = Player2).
    ///   2. Runs the lobby handshake (Hello → Ready × 2 → StartGame broadcast).
    ///   3. Relays TickCommands between peers after validating faction ownership.
    ///   4. Relays Checksum and DesyncAlert packets so P2P checksum comparison works through the server.
    ///
    /// The server does NOT run the simulation itself — the clients run identical deterministic
    /// sims and compare checksums P2P via relayed Checksum packets.
    ///
    /// To run headless:
    ///   Godot export → Linux headless → ./project.x86_64 --headless -- --port 7777
    ///   Or via <see cref="MainScene"/>._Ready() which detects DisplayServer.GetName()=="headless".
    ///
    /// Usage in code (already wired in MainScene):
    ///   var server = new DedicatedServer();
    ///   AddChild(server);
    ///   server.Start(port);
    /// </summary>
    public partial class DedicatedServer : Node
    {
        // ── Server state machine ──────────────────────────────────────────────────

        private enum State { Waiting, OneConnected, BothConnected, BothReady, InGame }

        // ── Config ────────────────────────────────────────────────────────────────

        /// <summary>Default port — can be overridden by command-line arg "--port N".</summary>
        public const int DEFAULT_PORT = 7777;

        // ── Faction → slot mapping ────────────────────────────────────────────────
        // Slot s → Player(s+1) (first come first served). N-shaped: derived from FactionRegistry.ToFaction so the
        // mapping grows automatically if ServerTransport.MAX_PLAYERS is ever raised (Story 9.7/9.15) — never a
        // hardcoded { Player1, Player2 } literal.

        private static readonly Faction[] SLOT_FACTION = BuildSlotFactions();

        private static Faction[] BuildSlotFactions()
        {
            var a = new Faction[ServerTransport.MAX_PLAYERS];
            for (int i = 0; i < a.Length; i++) a[i] = FactionRegistry.ToFaction(i);
            return a;
        }

        /// <summary>The player count this match expects before it may start (and the merged fan-in width). The
        /// relay ships N≤2; enabling &gt;2 live players is Story 9.7/9.15, but the count machine + builder are
        /// N-shaped so that is a constant bump, not a rearchitecture.</summary>
        private static int ExpectedPlayers => ServerTransport.MAX_PLAYERS;

        // ── State ─────────────────────────────────────────────────────────────────

        private ServerTransport _transport = null!;
        private State           _state     = State.Waiting;
        private readonly bool[] _ready     = new bool[ServerTransport.MAX_SLOTS];

        /// <summary>Story 9.3: the authoritative per-tick merged fan-in. Constructed at InGame (HandleReady) with
        /// the connected player count. Null until the match starts.</summary>
        private Server.MergedTickBuilder? _builder;

        // ── Story 1.9a: server authority ───────────────────────────────────────────

        /// <summary>
        /// The validated Godot-free sim spine built by <see cref="ServerBootstrap"/> and injected by MainScene's
        /// headless edge (AR-38). The server HOLDS it (proving it can hold validated start-state) but does NOT
        /// tick it in 1.9a — the live re-simulated server vote needs TickCommandsMerged and is Epic 9 (D3).
        /// Null when the scenario was missing/invalid ⇒ the server runs as a relay + quorum only.
        /// </summary>
        public SimulationHost? SimHost { get; init; }

        /// <summary>
        /// Optional logging seam injected by MainScene's headless edge (its GodotLogSink → the server console).
        /// The Story-1.9b determinism verdict (per-window PASS lines + the MATCH SUMMARY) is written here. Defaults
        /// to a NullLogSink when not injected (e.g. the in-process self-test reads the counters directly instead).
        /// </summary>
        public ILogSink? Log { get; init; }

        /// <summary>
        /// Server-authority core: the strict-majority checksum quorum + DesyncAlert/HALT generator. Constructed
        /// when the match starts (HandleReady → InGame) with the connected player count and the transport seams.
        /// </summary>
        private ServerHost? _serverHost;

        /// <summary>
        /// The server-authority core for the live match (null until StartGame), exposed read-only for the in-process
        /// loopback self-test to read the Story-1.9b determinism counters/verdict (WindowsCompared/DesyncCount/Passing).
        /// </summary>
        public ServerHost? Host => _serverHost;

        /// <summary>Guards <see cref="EmitSummaryOnce"/> so the terminal MATCH SUMMARY prints exactly once per match —
        /// the disconnect path and <see cref="_ExitTree"/> can otherwise both fire it (Story 1.9b review P2).</summary>
        private bool _summaryLogged;

        /// <summary>Emit the determinism MATCH SUMMARY at most once, regardless of how many shutdown paths run.</summary>
        private void EmitSummaryOnce()
        {
            if (_summaryLogged) return;
            _summaryLogged = true;
            _serverHost?.LogSummary();
        }

        // Story 9.3: the per-tick command relay is now the MergedTickBuilder (which owns its own decode +
        // assembly scratch), so the old per-faction relay/validate buffers are gone.

        // ── Init ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Start the server on <paramref name="port"/> (default 7777).
        /// Call after AddChild(this).
        /// </summary>
        public Error Start(int port = DEFAULT_PORT)
        {
            _transport = new ServerTransport();
            _transport.OnSlotConnected    += HandleConnect;
            _transport.OnSlotDisconnected += HandleDisconnect;
            _transport.OnPacketReceived   += HandlePacket;

            var err = _transport.Listen(port);
            if (err != Error.Ok)
                GD.PrintErr($"[Server] Failed to listen on port {port}: {err}");
            GD.Print($"[Server] Sim spine: {(SimHost != null ? "validated + held (AR-38)" : "none — relay + quorum only")}.");
            return err;
        }

        // ── Godot loop ────────────────────────────────────────────────────────────

        public override void _Process(double _delta)
        {
            _transport?.Poll();
        }

        // ── Connection events ─────────────────────────────────────────────────────

        private void HandleConnect(int slot)
        {
            if (slot >= ServerTransport.MAX_PLAYERS)
            {
                // Spectator slot — send Neutral faction assignment, no state-machine effect.
                GD.Print($"[Server] Spectator connected → slot {slot}.");
                _transport.SendReliableTo(slot, TickCommandPacket.MakeHello(Faction.Neutral));
                return;
            }

            Faction f = SLOT_FACTION[slot];
            GD.Print($"[Server] Slot {slot} connected → assigned {f}.");
            _transport.SendReliableTo(slot, TickCommandPacket.MakeHello(f));

            int playerCount = CountConnectedPlayers();
            _state = playerCount >= ServerTransport.MAX_PLAYERS
                ? State.BothConnected
                : State.OneConnected;

            GD.Print($"[Server] State → {_state}.");
        }

        private void HandleDisconnect(int slot)
        {
            GD.Print($"[Server] Slot {slot} disconnected.");
            _ready[slot] = false;

            if (slot >= ServerTransport.MAX_PLAYERS) return; // spectator — no state change

            if (_state == State.InGame)
            {
                // Story 1.9b: a player left mid-match — emit the determinism verdict-so-far (once).
                EmitSummaryOnce();

                // Story 9.3: notify EVERY other connected player peer (N-shaped — no `1 - slot` single-opponent
                // assumption). Slots are 0..MAX_PLAYERS-1; each surviving player is told the leaver's faction went Neutral.
                for (int other = 0; other < ServerTransport.MAX_PLAYERS; other++)
                    if (other != slot && _transport.IsSlotConnected(other))
                        _transport.SendReliableTo(other, TickCommandPacket.MakeHello(Faction.Neutral));
            }

            int playerCount = CountConnectedPlayers();
            _state = playerCount >= ServerTransport.MAX_PLAYERS
                ? State.BothConnected
                : State.OneConnected;
        }

        // ── Packet dispatch ───────────────────────────────────────────────────────

        private void HandlePacket(int slot, byte[] data, int len, int channel)
        {
            if (len < 1) return;
            var type = (PacketType)data[0];

            switch (type)
            {
                case PacketType.Hello:
                    // Client echoes Hello back — ignore.
                    break;

                case PacketType.Ready:
                    HandleReady(slot);
                    break;

                case PacketType.TickCommands:
                    if (_state == State.InGame)
                        FanInTickCommands(slot, data, len);
                    break;

                case PacketType.TickCommandsMerged:
                    // Story 9.3: the merged tick is server-authored (server → client ONLY). A client that sends
                    // one is spoofing the authoritative stream — hard-reject, no state change. (The builder also
                    // rejects it in Submit; this is the explicit dispatch-level guard.)
                    GD.PrintErr($"[Server] Rejected merged-shaped packet from slot {slot} — TickCommandsMerged is server→client only.");
                    break;

                case PacketType.Checksum:
                    // Story 1.9a (D8): the server CONSUMES checksums into the authoritative quorum collector
                    // instead of opaquely relaying them to the other peer. Slot is transport-authoritative —
                    // taken from THIS callback's `slot` (the ENet peer→slot map), never the packet payload (which
                    // carries only tick+hash) — so a client cannot spoof another slot's checksum. Spectators
                    // (slot >= MAX_PLAYERS) are EXCLUDED from the quorum (D6): they run the sim and send checksums
                    // too, but `expectedPeerCount` counts only players, so feeding a spectator's report would let
                    // a tick's bucket complete on the wrong reporter set — masking a real player desync, or
                    // tripping a false HALT. The collector's verdicts emit DesyncAlert (to a minority) or Halt
                    // (no majority) via ServerHost's seams.
                    if (_state == State.InGame && _serverHost != null && slot < ServerTransport.MAX_PLAYERS &&
                        TickCommandPacket.TryReadChecksum(data, len, out uint ckTick, out uint ckHash))
                        _serverHost.OnChecksum(slot, ckTick, ckHash);
                    break;
                // PacketType.DesyncAlert is now SERVER-GENERATED (clients never send it) — the old relay case is gone.

                case PacketType.Chat:
                    // Story 9.3 (chat-spoof fix): the client's faction byte is spoofable, so RE-STAMP it from the
                    // sender's transport-authoritative slot before rebroadcasting (a spectator sender → Neutral).
                    // The message text is re-encoded via MakeChat so no client-supplied faction byte survives.
                    if (TickCommandPacket.TryReadChat(data, len, out _, out string chatMsg))
                    {
                        Faction stamped = Server.ServerLobbyPolicy.StampChatFaction(
                            slot, SLOT_FACTION, ServerTransport.MAX_PLAYERS);
                        _transport.BroadcastReliable(TickCommandPacket.MakeChat(stamped, chatMsg));
                    }
                    else
                    {
                        // Story 9.3: the old relay rebroadcast raw bytes unconditionally; the re-stamp path can only
                        // rebroadcast a chat it can decode. Log the drop so a malformed chat is observable, not silent.
                        GD.PrintErr($"[DedicatedServer] Dropped undecodable Chat from slot {slot} ({len} bytes).");
                    }
                    break;
            }
        }

        // ── Lobby handshake ───────────────────────────────────────────────────────

        private void HandleReady(int slot)
        {
            if (slot >= ServerTransport.MAX_PLAYERS) return; // spectators don't send Ready
            if (_state == State.InGame) return;              // match already started — ignore late/duplicate Ready

            // Story 1.9a: RECORD the ready even if the other player hasn't connected yet (it was previously
            // DROPPED unless the server was already BothConnected). A client that readies the instant it connects
            // — e.g. the auto-join loopback smoke, or simply a faster peer — must not deadlock waiting on a Ready
            // the server threw away. Start only once BOTH players are connected AND both have readied; the
            // connect/ready order no longer matters.
            _ready[slot] = true;
            GD.Print($"[Server] Slot {slot} is Ready.");

            // Story 9.3: N-shaped count machine — start once `connected == expected && ready == expected` (no
            // `_ready[0] && _ready[1]` two-slot literal). `expected` is the match's player width (MAX_PLAYERS today).
            int connected  = CountConnectedPlayers();
            int readyCount = CountReadyPlayers();
            if (Server.ServerLobbyPolicy.ShouldStart(connected, readyCount, ExpectedPlayers))
            {
                _state = State.InGame;

                // Story 9.3: stand up the authoritative merged fan-in for this match — expected = the connected
                // player count (== ExpectedPlayers at the gate). Spectators are NOT counted (they never submit).
                _builder = new Server.MergedTickBuilder(connected, SLOT_FACTION);

                // Story 1.9a (D5): stand up the server-authority core for this match. expectedPeerCount = the
                // connected PLAYER count (spectators excluded — D6). The transport seams are wrapped in lambdas
                // because SendReliableTo/BroadcastReliable take an optional length arg, so a method-group
                // conversion to Action<int,byte[]> / Action<byte[]> won't bind.
                _serverHost = new ServerHost(connected, Log ?? new NullLogSink(),
                    (s, pkt) => _transport.SendReliableTo(s, pkt),
                    pkt => _transport.BroadcastReliable(pkt));

                // Broadcast StartGame (tick 0) to all peers simultaneously.
                var startPkt = TickCommandPacket.MakeStartGame(startTick: 0);
                _transport.BroadcastReliable(startPkt);
                GD.Print($"[Server] All {connected} players ready — broadcasting StartGame. Match begins (quorum N={connected}).");
            }
            else
            {
                _state = State.BothReady;
            }
        }

        // ── Command fan-in (Story 9.3) ─────────────────────────────────────────────

        /// <summary>
        /// Story 9.3: fan a player's single-faction TickCommands into the authoritative
        /// <see cref="Server.MergedTickBuilder"/>. When the last expected player's submission completes the tick,
        /// broadcast the ONE merged packet to ALL peers (players + spectators) on CH_COMMANDS. All the
        /// determinism-critical work (faction re-stamp / spoof-drop / over-count-drop / merged-from-client
        /// hard-reject / ascending sort / byte-ceiling drop) lives in the Godot-free builder — this node is a
        /// thin adapter (transport in → builder → transport out).
        /// </summary>
        private void FanInTickCommands(int fromSlot, byte[] data, int len)
        {
            if (_builder == null) return; // not yet InGame
            if (_builder.Submit(fromSlot, data, len, out uint tick) &&
                _builder.TryBuild(tick, out byte[] merged, out int mergedLen))
            {
                _transport.BroadcastCommands(merged, mergedLen);
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────────

        // Both counting helpers + the start gate delegate to the Godot-free Server.ServerLobbyPolicy so the policy
        // is Tier-1 unit-testable (spectators — slots >= MAX_PLAYERS — are excluded by the maxPlayers bound).
        private int CountConnectedPlayers()
            => Server.ServerLobbyPolicy.CountConnectedPlayers(_transport.IsSlotConnected, ServerTransport.MAX_PLAYERS);

        private int CountReadyPlayers()
            => Server.ServerLobbyPolicy.CountReadyPlayers(_transport.IsSlotConnected, s => _ready[s], ServerTransport.MAX_PLAYERS);

        // ── Cleanup ───────────────────────────────────────────────────────────────

        public override void _ExitTree()
        {
            // Story 1.9b: emit the final determinism verdict on server shutdown (if a match ran; once — the
            // disconnect path may already have emitted it). Review P2: guard against a duplicate MATCH SUMMARY.
            EmitSummaryOnce();
            _transport?.Dispose();
        }
    }
}
