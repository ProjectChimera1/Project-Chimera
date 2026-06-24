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
        // Slot 0 = Player1, Slot 1 = Player2 (first come first served).

        private static readonly Faction[] SLOT_FACTION = { Faction.Player1, Faction.Player2 };

        // ── State ─────────────────────────────────────────────────────────────────

        private ServerTransport _transport = null!;
        private State           _state     = State.Waiting;
        private readonly bool[] _ready     = new bool[ServerTransport.MAX_SLOTS];

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

        // Reusable buffers to avoid per-frame allocation
        private readonly byte[] _relayBuf  = new byte[
            TickCommandPacket.HEADER_BYTES + TickCommandPacket.MAX_ORDERS * UnitOrder.SIZE + 16];
        private readonly UnitOrder[] _validateBuf = new UnitOrder[TickCommandPacket.MAX_ORDERS];

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
                // Story 1.9b: a player left mid-match (ends the 1v1) — emit the determinism verdict-so-far.
                _serverHost?.LogSummary();

                // Notify the remaining player peer.
                int other = 1 - slot;
                if (_transport.IsSlotConnected(other))
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
                        RelayTickCommands(slot, data, len);
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
                    // Broadcast chat to all connected peers (players + spectators).
                    // No validation needed — the faction byte is decorative; the server
                    // could overwrite it with the slot's known faction, but for friends/
                    // family EA we trust the sender.
                    _transport.BroadcastReliable(data[..len]);
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

            bool bothConnected = CountConnectedPlayers() >= ServerTransport.MAX_PLAYERS;
            if (bothConnected && _ready[0] && _ready[1])
            {
                _state = State.InGame;

                // Story 1.9a (D5): stand up the server-authority core for this match. expectedPeerCount = the
                // connected PLAYER count (spectators excluded — D6). The transport seams are wrapped in lambdas
                // because SendReliableTo/BroadcastReliable take an optional length arg, so a method-group
                // conversion to Action<int,byte[]> / Action<byte[]> won't bind.
                int playerCount = CountConnectedPlayers();
                _serverHost = new ServerHost(playerCount, Log ?? new NullLogSink(),
                    (s, pkt) => _transport.SendReliableTo(s, pkt),
                    pkt => _transport.BroadcastReliable(pkt));

                // Broadcast StartGame (tick 0) to both peers simultaneously.
                var startPkt = TickCommandPacket.MakeStartGame(startTick: 0);
                _transport.BroadcastReliable(startPkt);
                GD.Print($"[Server] Both peers ready — broadcasting StartGame. Match begins (quorum N={playerCount}).");
            }
            else
            {
                _state = State.BothReady;
            }
        }

        // ── Command relay + validation ────────────────────────────────────────────

        /// <summary>
        /// Validate and relay a TickCommands packet from <paramref name="fromSlot"/> to the other peer.
        /// Anti-cheat: drops any order targeting a unit that does not belong to the sender's faction.
        /// </summary>
        private void RelayTickCommands(int fromSlot, byte[] data, int len)
        {
            if (!TickCommandPacket.TryRead(data, len, out uint tick, out Faction claimedFaction,
                                           _validateBuf, out int count))
            {
                GD.PrintErr($"[Server] Malformed TickCommands from slot {fromSlot} — dropping.");
                return;
            }

            // The slot determines the real faction regardless of what the packet claims.
            Faction expectedFaction = SLOT_FACTION[fromSlot];
            if (claimedFaction != expectedFaction)
            {
                GD.PrintErr($"[Server] Faction spoof from slot {fromSlot}: " +
                            $"claimed {claimedFaction}, expected {expectedFaction} — dropping.");
                return;
            }

            // Relay to the other player, plus any connected spectators.
            int other = 1 - fromSlot;
            _transport.SendCommandsTo(other, data, len);
            _transport.BroadcastCommandsToSpectators(data, len);
        }

        // ── Helpers ───────────────────────────────────────────────────────────────

        private int CountConnectedPlayers()
        {
            int count = 0;
            for (int s = 0; s < ServerTransport.MAX_PLAYERS; s++)
                if (_transport.IsSlotConnected(s)) count++;
            return count;
        }

        // ── Cleanup ───────────────────────────────────────────────────────────────

        public override void _ExitTree()
        {
            // Story 1.9b: emit the final determinism verdict on server shutdown (if a match ran).
            _serverHost?.LogSummary();
            _transport?.Dispose();
        }
    }
}
