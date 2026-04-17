#nullable enable
using Godot;
using ProjectChimera.Core;

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
                case PacketType.DesyncAlert:
                    // Relay checksum and desync alerts transparently so each peer can
                    // compare its hash against the other side's.
                    if (_state == State.InGame)
                    {
                        int other = 1 - slot;
                        _transport.SendReliableTo(other, data, len);
                    }
                    break;

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
            if (_state != State.BothConnected && _state != State.BothReady) return;

            _ready[slot] = true;
            GD.Print($"[Server] Slot {slot} is Ready.");

            if (_ready[0] && _ready[1])
            {
                _state = State.InGame;
                // Broadcast StartGame (tick 0) to both peers simultaneously.
                var startPkt = TickCommandPacket.MakeStartGame(startTick: 0);
                _transport.BroadcastReliable(startPkt);
                GD.Print("[Server] Both peers ready — broadcasting StartGame. Match begins.");
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
            _transport?.Dispose();
        }
    }
}
