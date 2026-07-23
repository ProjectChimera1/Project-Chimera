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

        // ── Story 9.4: server-dictated delay + start-state agreement ───────────────

        /// <summary>The Godot-free server delay authority — per-slot RTT EWMA + dictated-delay directive/ACK state
        /// machine. Constructed at InGame (HandleReady) alongside <see cref="_builder"/>. Null until the match starts.</summary>
        private Server.DelayController? _delayController;

        /// <summary>Story 9.6: the Godot-free ACK-gated freeze authority — on an in-match disconnect it holds the
        /// pending <c>DropDirective</c>, collects survivor <c>DropAck</c>s, and (on all-ACK) marks the slot frozen so
        /// <see cref="Server.FrozenSlotInjector"/> injects empty commands for it each tick. Constructed at InGame
        /// (HandleReady) alongside <see cref="_builder"/>/<see cref="_delayController"/>. Null until the match starts.</summary>
        private Server.DropController? _dropController;

        /// <summary>Story 9.6: scratch buffer for an injected empty single-faction packet (0 orders → HEADER_BYTES).
        /// Reused across pumps — the frozen-slot drain never allocates per tick.</summary>
        private readonly byte[] _injectBuf = new byte[TickCommandPacket.HEADER_BYTES];

        /// <summary>Story 9.6: cached broadcast sink for the frozen-slot drain (avoids a per-pump lambda alloc).
        /// Assigned when the match starts (HandleReady).</summary>
        private System.Action<byte[], int>? _injectBroadcast;

        /// <summary>Per-slot Ready-packet agreement data collected before StartGame (protocol version + 64-bit
        /// match-agreement hash), compared fail-closed by
        /// <see cref="Server.ServerLobbyPolicy.CheckStartStateAgreement"/> at the readiness gate.</summary>
        private readonly ulong[]  _readyHash    = new ulong[ServerTransport.MAX_SLOTS];
        private readonly ushort[] _readyVersion = new ushort[ServerTransport.MAX_SLOTS];

        /// <summary>Seconds elapsed since the last per-slot RTT-probe (Ping) broadcast.</summary>
        private double _sincePing;
        private byte   _pingSeq;

        /// <summary>How often the server probes each client's RTT (seconds).</summary>
        private const double PING_INTERVAL_SEC = 1.0;

        /// <summary>The highest sim tick the server has seen fanned in — the frontier a dictated directive's
        /// <c>applyAtTick</c> is measured forward from.</summary>
        private uint _latestSeenTick;

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

        public override void _Process(double delta)
        {
            _transport?.Poll();

            // Story 9.6: after draining transport (which may have advanced the survivor frontier via FanIn), inject
            // empty commands for any frozen slot across the whole unemitted gap so the merged stream keeps flowing
            // even when no fresh survivor packet arrived this frame.
            PumpFrozenInjection();

            // Story 9.4: once in-match, probe each player's RTT periodically and dictate ONE delay for the whole
            // match when the target shifts. The server is the SINGLE delay authority — clients apply changes only
            // from a DelayDirective (never their own DelayProposal), so all commit the same value at the same tick.
            if (_state != State.InGame || _delayController == null) return;

            _sincePing += delta;
            if (_sincePing >= PING_INTERVAL_SEC)
            {
                _sincePing = 0;
                var ping = TickCommandPacket.MakePing(_pingSeq++, (uint)Time.GetTicksMsec());
                for (int s = 0; s < ServerTransport.MAX_PLAYERS; s++)
                    if (_transport.IsSlotConnected(s)) _transport.SendReliableTo(s, ping);
            }

            // PATCH 1a: the confirmed high-water = the tick through which the merged fan-in has emitted (all players
            // submitted past it). It gates directive pipelining so a new directive is not issued until the prior one
            // has matured (been applied) on every client.
            uint confirmed = _builder != null && _builder.EmittedThrough >= 0 ? (uint)_builder.EmittedThrough : 0u;
            if (_delayController.TryComputeDirective(_latestSeenTick, confirmed, out int delay, out uint applyAtTick))
            {
                var directive = TickCommandPacket.MakeDelayDirective((byte)delay, applyAtTick);
                _transport.BroadcastCommands(directive, directive.Length);
                GD.Print($"[Server] Dictating input delay → {delay} ticks, applyAtTick {applyAtTick} (awaiting all-{ExpectedPlayers} ACK).");
            }
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

            // Story 9.6: an in-match disconnect is NO LONGER treated as match-over. The transport has already
            // cleared this slot, so CountConnectedPlayers() is the surviving player count. If any survivor remains,
            // dictate a deterministic freeze-and-continue for the dropped slot and STAY InGame; only a survivor-less
            // drop ends the match.
            if (_state == State.InGame && _dropController != null && _builder != null)
            {
                int survivors = CountConnectedPlayers();
                if (survivors <= 0)
                {
                    // No one left to play — the match is truly over. Emit the determinism verdict-so-far (once) and
                    // fall through to the lobby-state recompute below.
                    EmitSummaryOnce();
                }
                else
                {
                    // applyAtTick = the next unemitted merged tick (the informational idle-from marker). The freeze is
                    // tick-counted, never wall-clock. Survivors = every OTHER connected player slot. This marker stays
                    // valid across the directive→ACK window BECAUSE the merge is stalled on the now-silent dropped slot
                    // until commit: EmittedThrough cannot advance past this tick until injection begins (post-commit),
                    // so EmittedThrough+1 still names the first idle tick when the survivors read it.
                    uint applyAtTick = (uint)(_builder.EmittedThrough + 1);
                    int[] survivorSlots = SurvivingPlayerSlots(slot);
                    if (_dropController.NotifyDrop(slot, applyAtTick, survivorSlots))
                    {
                        var directive = TickCommandPacket.MakeDropDirective((byte)SLOT_FACTION[slot], applyAtTick);
                        _transport.BroadcastReliable(directive);
                        GD.Print($"[Server] Slot {slot} ({SLOT_FACTION[slot]}) dropped mid-match — freezing at tick " +
                                 $"{applyAtTick}, awaiting {survivorSlots.Length} survivor ACK(s). Match continues.");
                    }
                    return; // keep InGame — no MATCH SUMMARY, no state flip
                }
            }
            else if (_state == State.InGame)
            {
                // Unreachable today: at InGame both _builder and _dropController are always constructed together in
                // HandleReady. If a future setup-ordering regression ever leaves them null while InGame, the freeze
                // path would silently no-op (a drop would fall through to a match-ending state flip below). Make that
                // a VISIBLE failure rather than a silent one.
                GD.PrintErr($"[Server] In-match disconnect (slot {slot}) but freeze machinery is null " +
                            $"(_builder={_builder != null}, _dropController={_dropController != null}) — " +
                            "freeze-and-continue skipped; falling through to match-end. This is a setup-ordering bug.");
                EmitSummaryOnce();
            }

            int playerCount = CountConnectedPlayers();
            _state = playerCount >= ServerTransport.MAX_PLAYERS
                ? State.BothConnected
                : State.OneConnected;
        }

        /// <summary>Story 9.6: the connected PLAYER slots other than <paramref name="droppedSlot"/> — the set that
        /// must ACK a drop directive before the freeze commits. Spectators (slots ≥ MAX_PLAYERS) are excluded.</summary>
        private int[] SurvivingPlayerSlots(int droppedSlot)
        {
            var survivors = new System.Collections.Generic.List<int>();
            for (int s = 0; s < ServerTransport.MAX_PLAYERS; s++)
                if (s != droppedSlot && _transport.IsSlotConnected(s)) survivors.Add(s);
            return survivors.ToArray();
        }

        /// <summary>Story 9.6: map a DropAck's (transport-untrusted) faction byte back to a player slot via the
        /// authoritative <see cref="SLOT_FACTION"/> table — never trusting it as a slot index. −1 if no slot matches.</summary>
        private static int FactionToSlot(Faction faction)
        {
            for (int s = 0; s < ServerTransport.MAX_PLAYERS; s++)
                if (SLOT_FACTION[s] == faction) return s;
            return -1;
        }

        /// <summary>Story 9.6: inject empty commands for every frozen slot across the whole unemitted gap up to the
        /// current frontier, building + broadcasting each newly-completable merged tick. Called after transport Poll
        /// (<see cref="_Process"/>) and after a survivor's submit (<see cref="FanInTickCommands"/>).</summary>
        private void PumpFrozenInjection()
        {
            if (_builder == null || _dropController == null || _injectBroadcast == null) return;
            if (_dropController.FrozenSlots.Count == 0) return;
            Server.FrozenSlotInjector.Drain(_builder, _dropController.FrozenSlots, SLOT_FACTION,
                _latestSeenTick, _injectBuf, _injectBroadcast);
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
                    HandleReady(slot, data, len);
                    break;

                case PacketType.Ping:
                    // Story 9.4: symmetry/defensive — a client Ping is echoed back as a Pong (the server is
                    // normally the pinger, but never leave an inbound probe unanswered). Wire: type(1)+seq(1)+ms(4).
                    if (len >= 6)
                        _transport.SendReliableTo(slot, TickCommandPacket.MakePong(
                            data[1], (uint)(data[2] | (data[3] << 8) | (data[4] << 16) | (data[5] << 24))));
                    break;

                case PacketType.Pong:
                    // Story 9.4: the client's echo of a server RTT probe. rtt = now - the senderMs we stamped into
                    // the Ping. Slot is transport-authoritative (this callback's slot), never a packet byte.
                    if (_state == State.InGame && _delayController != null && slot < ServerTransport.MAX_PLAYERS &&
                        TickCommandPacket.TryReadPong(data, len, out _, out uint pongMs))
                        _delayController.RecordRtt(slot, (float)Time.GetTicksMsec() - pongMs);
                    break;

                case PacketType.DelayAck:
                    // Story 9.4: a player acknowledges the pending server-dictated delay. When every player has
                    // ACKed the (delay, applyAtTick) pair, log the commit and advance the controller so the next
                    // directive may issue.
                    if (_state == State.InGame && _delayController != null && slot < ServerTransport.MAX_PLAYERS &&
                        TickCommandPacket.TryReadDelayAck(data, len, out byte ackDelay, out uint ackApplyAt))
                    {
                        _delayController.RecordAck(slot, ackDelay, ackApplyAt);
                        if (_delayController.AllAcked(ackDelay, ackApplyAt))
                        {
                            _delayController.Commit(ackDelay, ackApplyAt);
                            GD.Print($"[Server] Delay change committed → {ackDelay} ticks at tick {ackApplyAt} (all {ExpectedPlayers} players ACKed).");
                        }
                    }
                    break;

                case PacketType.DropAck:
                    // Story 9.6: a survivor acknowledges the pending freeze directive. Slot is transport-authoritative
                    // (this callback's `slot`); the ACK's faction byte is mapped back to the DROPPED slot via
                    // SLOT_FACTION (never trusted as a slot index). When every survivor has ACKed the same
                    // (droppedSlot, applyAtTick), commit the freeze, drop the leaver from the checksum quorum, and
                    // begin injecting empty commands so the merged stream keeps flowing.
                    if (_state == State.InGame && _dropController != null && slot < ServerTransport.MAX_PLAYERS &&
                        TickCommandPacket.TryReadDropAck(data, len, out byte dropAckFaction, out uint dropApplyAt))
                    {
                        int droppedSlot = FactionToSlot((Faction)dropAckFaction);
                        if (droppedSlot >= 0)
                        {
                            _dropController.RecordAck(slot, droppedSlot, dropApplyAt);
                            if (_dropController.AllAcked() && _dropController.Commit())
                            {
                                _serverHost?.DropReporter(droppedSlot);
                                GD.Print($"[Server] Freeze committed for slot {droppedSlot} ({(Faction)dropAckFaction}) at " +
                                         $"tick {dropApplyAt} (all survivors ACKed). Injecting empty commands; quorum reduced.");
                                PumpFrozenInjection(); // fill the gap immediately so survivors unstall
                            }
                        }
                    }
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

        private void HandleReady(int slot, byte[] data, int len)
        {
            if (slot >= ServerTransport.MAX_PLAYERS) return; // spectators don't send Ready
            if (_state == State.InGame) return;              // match already started — ignore late/duplicate Ready

            // Story 1.9a: RECORD the ready even if the other player hasn't connected yet (it was previously
            // DROPPED unless the server was already BothConnected). A client that readies the instant it connects
            // — e.g. the auto-join loopback smoke, or simply a faster peer — must not deadlock waiting on a Ready
            // the server threw away. Start only once BOTH players are connected AND both have readied; the
            // connect/ready order no longer matters.
            _ready[slot] = true;

            // Story 9.4: collect this slot's Ready agreement payload (protocol version + 64-bit match-agreement
            // hash). A short/undersized/malformed Ready parses false → version 0 / hash 0 → the fail-closed
            // agreement gate below rejects the start (never fail-open).
            TickCommandPacket.TryReadReady(data, len, out ushort readyVersion, out ulong readyHash);
            _readyVersion[slot] = readyVersion;
            _readyHash[slot]    = readyHash;
            GD.Print($"[Server] Slot {slot} is Ready (protocol v{readyVersion}, match hash 0x{readyHash:X16}).");

            // Story 9.3: N-shaped count machine — start once `connected == expected && ready == expected` (no
            // `_ready[0] && _ready[1]` two-slot literal). `expected` is the match's player width (MAX_PLAYERS today).
            int connected  = CountConnectedPlayers();
            int readyCount = CountReadyPlayers();
            if (Server.ServerLobbyPolicy.ShouldStart(connected, readyCount, ExpectedPlayers))
            {
                // Story 9.4: the ADDITIONAL start-state-agreement gate — every slot must share one non-zero
                // match-agreement hash AND run PROTOCOL_VERSION. On disagreement, broadcast a terminal HALT and do
                // NOT StartGame (fail-closed). Checked BEFORE flipping state / standing up the match machinery.
                HaltReason? disagreement = Server.ServerLobbyPolicy.CheckStartStateAgreement(
                    _readyHash, _readyVersion, ExpectedPlayers);
                if (disagreement != null)
                {
                    GD.PrintErr($"[Server] Start-state agreement FAILED ({disagreement}) — broadcasting HALT, not starting.");
                    _transport.BroadcastReliable(TickCommandPacket.MakeHalt(0u, disagreement.Value));
                    _state = State.BothReady;
                    return;
                }

                _state = State.InGame;

                // Story 9.3: stand up the authoritative merged fan-in for this match — expected = the connected
                // player count (== ExpectedPlayers at the gate). Spectators are NOT counted (they never submit).
                _builder = new Server.MergedTickBuilder(connected, SLOT_FACTION);

                // Story 9.4: stand up the server delay authority alongside the fan-in. The initial delay baseline is
                // LockstepManager.INPUT_DELAY (the delay the clients start at), so no directive issues until a
                // measured RTT genuinely shifts the target.
                _delayController = new Server.DelayController(connected, LockstepManager.INPUT_DELAY);

                // Story 9.6: stand up the ACK-gated freeze authority alongside the fan-in + delay authority, and
                // cache the frozen-slot drain's broadcast sink (BroadcastCommands reaches every peer + spectator).
                _dropController  = new Server.DropController(connected);
                _injectBroadcast = (buf, n) => _transport.BroadcastCommands(buf, n);

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
            if (_builder.Submit(fromSlot, data, len, out uint tick))
            {
                // Story 9.4: track the frontier so a dictated delay directive's applyAtTick lands safely ahead of it.
                if (tick > _latestSeenTick) _latestSeenTick = tick;
                if (_builder.TryBuild(tick, out byte[] merged, out int mergedLen))
                    _transport.BroadcastCommands(merged, mergedLen);
            }

            // Story 9.6: a survivor's submit just advanced the frontier — inject empties for any frozen slot so the
            // now-fannable ticks (survivor arrived, frozen slot silent) complete and the survivor unstalls.
            PumpFrozenInjection();
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
