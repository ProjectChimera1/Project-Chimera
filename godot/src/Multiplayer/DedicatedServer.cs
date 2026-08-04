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

        /// <summary>Story 9.7: N-aware lobby states (de-binary-fied from the old
        /// {Waiting,OneConnected,BothConnected,BothReady} 1v1 shape). <c>Waiting</c> = no players connected;
        /// <c>Lobby</c> = ≥1 player connected/readying but the match has not started; <c>InGame</c> = the match is
        /// live. Only <c>InGame</c> gates behavior; the earlier lobby distinctions were cosmetic and do not scale.</summary>
        private enum State { Waiting, Lobby, InGame }

        // ── Config ────────────────────────────────────────────────────────────────

        /// <summary>Default port — can be overridden by command-line arg "--port N".</summary>
        public const int DEFAULT_PORT = 7777;

        /// <summary>
        /// Story 9.7: the number of players THIS match expects before it may start (and the merged fan-in width /
        /// checksum quorum size). Derived from the loaded scenario's <c>PlayerSlots.Count</c> and injected by
        /// MainScene's headless edge (the SAME count fed to the client's <c>FactionRegistry(N)</c> and the server's
        /// <c>activeFactionCount</c>, so client/server checksum spans cannot diverge). Defaults to
        /// 2 when unset (the pre-9.7 1v1 default; the loopback self-test and MainScene set their own). Clamped into
        /// [1, <see cref="ServerTransport.MAX_PLAYERS"/>] on read.
        /// </summary>
        public int ExpectedPlayerCount { get; init; } = PlayerCountPolicy.MpFloor;

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

        /// <summary>The player count this match expects before it may start (and the merged fan-in width) — the
        /// scenario-derived <see cref="ExpectedPlayerCount"/>, clamped into [1, <see cref="ServerTransport.MAX_PLAYERS"/>].
        /// Story 9.7 raised the ceiling to 4 and made the count per-match (from the scenario) rather than the fixed
        /// 1v1 <c>MAX_PLAYERS</c>.</summary>
        private int ExpectedPlayers =>
            ExpectedPlayerCount < 1 ? 1
            : ExpectedPlayerCount > ServerTransport.MAX_PLAYERS ? ServerTransport.MAX_PLAYERS
            : ExpectedPlayerCount;

        // ── State ─────────────────────────────────────────────────────────────────

        private ServerTransport _transport = null!;
        private State           _state     = State.Waiting;
        private readonly bool[] _ready     = new bool[ServerTransport.MAX_SLOTS];

        /// <summary>Story 9.7 (P2): reusable scratch for the lobby roster/ready snapshot broadcast (no per-broadcast alloc).</summary>
        private readonly bool[] _rosterOccupied = new bool[ServerTransport.MAX_PLAYERS];
        private readonly bool[] _rosterReady    = new bool[ServerTransport.MAX_PLAYERS];

        /// <summary>Story 9.3: the authoritative per-tick merged fan-in. Constructed at InGame (HandleReady) with
        /// the connected player count. Null until the match starts.</summary>
        private Server.MergedTickBuilder? _builder;

        /// <summary>Story 9.7: the frozen server-authoritative slot→faction roster, snapshotted at StartGame from the
        /// connected player slots (arrival order). The single source downstream faction-stamps from. Null until start.</summary>
        private Server.AssignedRoster? _roster;

        // ── Story 9.4: server-dictated delay + start-state agreement ───────────────

        /// <summary>The Godot-free server delay authority — per-slot RTT EWMA + dictated-delay directive/ACK state
        /// machine. Constructed at InGame (HandleReady) alongside <see cref="_builder"/>. Null until the match starts.</summary>
        private Server.DelayController? _delayController;

        /// <summary>Story 9.6: the Godot-free ACK-gated freeze authority — on an in-match disconnect it holds the
        /// pending <c>DropDirective</c>, collects survivor <c>DropAck</c>s, and (on all-ACK) marks the slot frozen so
        /// <see cref="Server.FrozenSlotInjector"/> injects empty commands for it each tick. Constructed at InGame
        /// (HandleReady) alongside <see cref="_builder"/>/<see cref="_delayController"/>. Null until the match starts.</summary>
        private Server.DropController? _dropController;

        /// <summary>Story 9.13: the Godot-free per-slot command-rate throttle (anti-spam, NOT anti-cheat). Gates each
        /// inbound <c>TickCommands</c> packet through <c>TryAdmit(slot, wall-clock ms)</c> so a misbehaving/malicious
        /// client cannot flood the merged fan-in. Its decision (drop/accept) NEVER enters the sim, the builder, or any
        /// checksum. Sized to <see cref="ServerTransport.MAX_SLOTS"/> and constructed at InGame (HandleReady)
        /// alongside <see cref="_builder"/>/<see cref="_delayController"/>/<see cref="_dropController"/>; reset per
        /// slot on connect so a recycled slot never inherits the prior occupant's count.</summary>
        private Server.CommandRateLimiter? _rateLimiter;

        /// <summary>Story 9.13: rate-limit at which a throttled-drop diagnostic is printed — one line per this many
        /// dropped packets per slot, never one line per drop (keeps a flood from spamming the server console).
        /// Shared by the command-throttle (9.13) and receive-edge-throttle (DW-434) diagnostics.</summary>
        private const long RATE_LIMIT_LOG_EVERY = 128;

        /// <summary>DW-434: the SHARED per-slot receive-edge throttle across ALL client-sendable packet types (the
        /// Story-9.13 <see cref="_rateLimiter"/> gates only the TickCommands arm; Chat/LobbyChat/MapPing each
        /// <c>BroadcastReliable</c> to every peer — an amplifying flood the command throttle never saw). Gates the
        /// TOP of <see cref="HandlePacket"/>, so pings/acks/checksums and unknown/malformed types are bounded too.
        /// Alive from construction (unlike the match-scoped <see cref="_rateLimiter"/>) because LobbyChat/Ready
        /// amplification is lobby-phase; per-slot state resets on connect (recycle discipline). Anti-spam only —
        /// its accept/drop never enters the sim, the builder, or any checksum.</summary>
        private readonly Server.CommandRateLimiter _receiveLimiter = new Server.CommandRateLimiter(
            ServerTransport.MAX_SLOTS, Server.CommandRateLimiter.MAX_RECEIVE_PER_WINDOW,
            Server.CommandRateLimiter.WINDOW_MS);

        /// <summary>DW-392: per-peer protocol-violation counter — bounds attacker-triggerable log writes. Every
        /// misbehavior arm (a client-sent <c>TickCommandsMerged</c> spoof, an undecodable Chat/LobbyChat/MapPing,
        /// a merged fan-in <c>Submit</c> drop: faction spoof / over-count / malformed / replayed tick) records here
        /// and logs only when <see cref="Server.ProtocolViolationTracker.Record"/> says so (the 1st, then every
        /// <see cref="Server.ProtocolViolationTracker.LOG_EVERY"/>-th) — a flood can no longer write one log line
        /// per packet. Reset per slot on connect so a recycled slot never inherits the prior occupant's tally.</summary>
        private readonly Server.ProtocolViolationTracker _violations =
            new Server.ProtocolViolationTracker(ServerTransport.MAX_SLOTS);

        /// <summary>Story 9.6: scratch buffer for an injected empty single-faction packet (0 orders → HEADER_BYTES).
        /// Reused across pumps — the frozen-slot drain never allocates per tick.</summary>
        private readonly byte[] _injectBuf = new byte[TickCommandPacket.HEADER_BYTES];

        /// <summary>Story 9.6: cached broadcast sink over <c>_transport.BroadcastCommands</c>, shared by the
        /// frozen-slot drain AND the fan-in relay (<see cref="FanInTickCommands"/>) — avoids a per-pump/per-packet
        /// lambda alloc. Assigned when the match starts (HandleReady), together with <see cref="_builder"/>.</summary>
        private System.Action<byte[], int>? _injectBroadcast;

        /// <summary>Per-slot Ready-packet agreement data collected before StartGame (protocol version + 64-bit
        /// match-agreement hash), compared fail-closed by
        /// <see cref="Server.ServerLobbyPolicy.CheckStartStateAgreement"/> at the readiness gate.</summary>
        private readonly ulong[]  _readyHash    = new ulong[ServerTransport.MAX_SLOTS];
        private readonly ushort[] _readyVersion = new ushort[ServerTransport.MAX_SLOTS];

        /// <summary>Seconds elapsed since the last per-slot RTT-probe (Ping) broadcast. Reset alongside the
        /// <see cref="_delayController"/> rebuild at match start (DW-401). The probe SEQ itself lives in the
        /// controller (<see cref="Server.DelayController.NextPingSeq"/>) so a rebuilt controller starts fresh.</summary>
        private double _sincePing;

        /// <summary>How often the server probes each client's RTT (seconds).</summary>
        private const double PING_INTERVAL_SEC = 1.0;

        /// <summary>The highest sim tick the server has seen fanned in — the frontier a dictated directive's
        /// <c>applyAtTick</c> is measured forward from. Reset alongside the <see cref="_delayController"/> rebuild
        /// at match start (DW-401): a second match in one process must not measure its first directive's
        /// applyAtTick forward from the PRIOR match's (large) frontier, which would schedule it at an unreachable
        /// tick and silently break adaptive delay.</summary>
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
                // DW-395/DW-401: the probe seq is controller-owned — one seq per broadcast, re-arming the
                // per-slot one-echo-per-probe guard; a rebuilt controller (second match) restarts from seq 0.
                var ping = TickCommandPacket.MakePing(_delayController.NextPingSeq(), (uint)Time.GetTicksMsec());
                for (int s = 0; s < ExpectedPlayers; s++)
                    if (_transport.IsSlotConnected(s)) _transport.SendReliableTo(s, ping);
            }

            // DW-400: bound the un-ACKed directive window. Reliable transport means every still-connected client
            // received + scheduled the directive — a wedged ACK must not disable adaptive delay for the match.
            if (_delayController.CheckAckTimeout(_latestSeenTick, out int toDelay, out uint toApplyAt))
                GD.Print($"[Server] Delay ACK timeout — force-committing {toDelay} ticks at tick {toApplyAt} " +
                         "(reliable transport implies delivery; a genuine drop is excused on the freeze path).");

            // PATCH 1a: the confirmed high-water = the tick through which the merged fan-in has emitted (all players
            // submitted past it). It gates directive pipelining so a new directive is not issued until the prior one
            // has matured (been applied) on every client.
            uint confirmed = _builder != null && _builder.EmittedThrough >= 0 ? (uint)_builder.EmittedThrough : 0u;
            if (_delayController.TryComputeDirective(_latestSeenTick, confirmed, out int delay, out uint applyAtTick))
            {
                var directive = TickCommandPacket.MakeDelayDirective((byte)delay, applyAtTick);
                _transport.BroadcastCommands(directive, directive.Length);
                GD.Print($"[Server] Dictating input delay → {delay} ticks, applyAtTick {applyAtTick} " +
                         $"(awaiting all-{_delayController.ActiveCount} ACK).");
            }
        }

        // ── Connection events ─────────────────────────────────────────────────────

        private void HandleConnect(int slot)
        {
            // Story 9.13: a slot can be recycled (a prior occupant disconnected, a new client connects into the same
            // slot). Reset the command-rate throttle for this slot so the new occupant never inherits the prior one's
            // admission count (SoA-recycle-trap discipline). Null-safe: no-op until a match's limiter exists.
            _rateLimiter?.Reset(slot);

            // DW-392/DW-434: the same recycle discipline for the connection-lifetime receive-edge throttle window
            // and the per-peer protocol-violation tally — a fresh occupant starts clean on both.
            _receiveLimiter.Reset(slot);
            _violations.Reset(slot);

            // Story 9.7 (SD-9): the player/spectator split is the DYNAMIC SlotAllocation.Classify over the per-match
            // ExpectedPlayers boundary (not the fixed MAX_PLAYERS 1v1 partition). A slot at/above the player count is
            // a spectator for THIS match.
            if (Server.SlotAllocation.Classify(slot, ExpectedPlayers, ServerTransport.MAX_SLOTS)
                != Server.SlotRole.Player)
            {
                // Spectator slot — send Neutral faction assignment, no state-machine effect.
                GD.Print($"[Server] Spectator connected → slot {slot}.");
                _transport.SendReliableTo(slot, TickCommandPacket.MakeHello(Faction.Neutral));
                BroadcastLobbyRoster(); // so the new spectator sees the current lobby state
                return;
            }

            Faction f = SLOT_FACTION[slot];
            GD.Print($"[Server] Slot {slot} connected → assigned {f}.");
            _transport.SendReliableTo(slot, TickCommandPacket.MakeHello(f));

            _state = State.Lobby;
            GD.Print($"[Server] State → {_state} ({CountConnectedPlayers()}/{ExpectedPlayers} players connected).");
            BroadcastLobbyRoster(); // Story 9.7 (P2): occupancy changed
        }

        private void HandleDisconnect(int slot)
        {
            GD.Print($"[Server] Slot {slot} disconnected.");
            _ready[slot] = false;

            if (slot >= ExpectedPlayers) return; // spectator — no state change

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
            _state = playerCount > 0 ? State.Lobby : State.Waiting;
            BroadcastLobbyRoster(); // Story 9.7 (P2): occupancy/ready changed (lobby-phase only; no-op InGame)
        }

        /// <summary>Story 9.6: the connected PLAYER slots other than <paramref name="droppedSlot"/> — the set that
        /// must ACK a drop directive before the freeze commits. Spectators (slots ≥ ExpectedPlayers) are excluded.</summary>
        private int[] SurvivingPlayerSlots(int droppedSlot)
        {
            var survivors = new System.Collections.Generic.List<int>();
            for (int s = 0; s < ExpectedPlayers; s++)
                if (s != droppedSlot && _transport.IsSlotConnected(s)) survivors.Add(s);
            return survivors.ToArray();
        }

        /// <summary>Story 9.6: map a DropAck's (transport-untrusted) faction byte back to a player slot via the
        /// authoritative <see cref="SLOT_FACTION"/> table — never trusting it as a slot index. −1 if no slot matches.</summary>
        private int FactionToSlot(Faction faction)
        {
            for (int s = 0; s < ExpectedPlayers; s++)
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
            // DW-434: the SHARED per-slot receive-edge throttle — applied to EVERY inbound client packet BEFORE
            // type dispatch, so all client-sendable types are bounded (the Chat/LobbyChat/MapPing broadcast
            // amplifiers, Ping/Pong/acks, checksums, and unknown/malformed bytes), not just the TickCommands arm
            // (whose Story-9.13 _rateLimiter still applies its tighter cap inside that arm). A non-admit is a
            // SILENT drop (mirrors the 9.13 contract — no server→client packet, no state change) with a BOUNDED
            // diagnostic. The clock is injected wall-clock ms, never a client-influenceable value.
            if (!_receiveLimiter.TryAdmit(slot, (ulong)Time.GetTicksMsec()))
            {
                long rxDropped = _receiveLimiter.DroppedCount(slot);
                if (rxDropped == 1 || (rxDropped > 0 && rxDropped % RATE_LIMIT_LOG_EVERY == 0))
                    GD.Print($"[Server] Receive-edge throttle active on slot {slot} ({rxDropped} packets dropped).");
                return;
            }

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
                    // Story 9.4: the client's echo of a server RTT probe. Slot is transport-authoritative (this
                    // callback's slot), never a packet byte. The controller applies the client-symmetric guards
                    // (DW-395: seq must match the OUTSTANDING probe, one echo per slot per probe) and computes the
                    // RTT in WRAP-SAFE uint arithmetic against the same uint-truncated clock the ping stamped
                    // (DW-396: a full-width subtraction would reject every sample past ~49.7 days of uptime,
                    // silently freezing delay adaptation). Player-slot membership is the controller's slot map.
                    if (_state == State.InGame && _delayController != null &&
                        TickCommandPacket.TryReadPong(data, len, out byte pongSeq, out uint pongMs))
                        _delayController.RecordPong(slot, pongSeq, (uint)Time.GetTicksMsec(), pongMs);
                    break;

                case PacketType.DelayAck:
                    // Story 9.4: a player acknowledges the pending server-dictated delay. When every ACTIVE player
                    // has ACKed the (delay, applyAtTick) pair, log the commit and advance the controller so the
                    // next directive may issue. Player-slot membership is the controller's slot map (DW-397) —
                    // a spectator/unknown slot's ACK is ignored inside RecordAck.
                    if (_state == State.InGame && _delayController != null &&
                        TickCommandPacket.TryReadDelayAck(data, len, out byte ackDelay, out uint ackApplyAt))
                    {
                        _delayController.RecordAck(slot, ackDelay, ackApplyAt);
                        if (_delayController.AllAcked(ackDelay, ackApplyAt))
                        {
                            _delayController.Commit(ackDelay, ackApplyAt);
                            GD.Print($"[Server] Delay change committed → {ackDelay} ticks at tick {ackApplyAt} " +
                                     $"(all {_delayController.ActiveCount} players ACKed).");
                        }
                    }
                    break;

                case PacketType.DropAck:
                    // Story 9.6: a survivor acknowledges the pending freeze directive. Slot is transport-authoritative
                    // (this callback's `slot`); the ACK's faction byte is mapped back to the DROPPED slot via
                    // SLOT_FACTION (never trusted as a slot index). When every survivor has ACKed the same
                    // (droppedSlot, applyAtTick), commit the freeze, drop the leaver from the checksum quorum, and
                    // begin injecting empty commands so the merged stream keeps flowing.
                    if (_state == State.InGame && _dropController != null && slot < ExpectedPlayers &&
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

                                // DW-400: excuse the frozen slot from the delay authority — it leaves the delay-ACK
                                // quorum and its stale RTT stops driving the dictated delay. If a delay directive was
                                // pending on ONLY its ACK, finalize now (otherwise _pending would wedge forever and
                                // adaptive delay would be silently disabled for the rest of the match).
                                _delayController?.DeactivateSlot(droppedSlot);
                                if (_delayController != null &&
                                    _delayController.TryFinalizePending(out int exDelay, out uint exApplyAt))
                                    GD.Print($"[Server] Delay change committed → {exDelay} ticks at tick {exApplyAt} " +
                                             "(surviving players had all ACKed; dropped slot excused).");

                                PumpFrozenInjection(); // fill the gap immediately so survivors unstall
                            }
                        }
                    }
                    break;

                case PacketType.TickCommands:
                    if (_state == State.InGame)
                    {
                        // Story 9.13: gate the fan-in through the per-slot command-rate throttle. Slot is
                        // transport-authoritative; the clock is injected wall-clock ms (Time.GetTicksMsec), never a
                        // client-influenceable or valid-submit-gated tick. A non-admit is a SILENT no-op — no
                        // server→client packet, no DesyncAlert, no FanInTickCommands call (mirrors Story 9.3's
                        // silent-drop contract). The console diagnostic is itself throttled (never one line per drop).
                        if (_rateLimiter == null || _rateLimiter.TryAdmit(slot, (ulong)Time.GetTicksMsec()))
                            FanInTickCommands(slot, data, len);
                        else
                        {
                            // Story 9.13: silent drop (no client packet, no fan-in). The diagnostic is throttled —
                            // one line on the FIRST drop, then one per RATE_LIMIT_LOG_EVERY — so a real flood can't
                            // spam the console, yet a low-volume drop still leaves a trace. Gated on dropped > 0 so an
                            // (unreachable-while-sized-to-MAX_SLOTS) out-of-range slot never logs a "0 dropped" line.
                            long dropped = _rateLimiter.DroppedCount(slot);
                            if (dropped == 1 || (dropped > 0 && dropped % RATE_LIMIT_LOG_EVERY == 0))
                                GD.Print($"[Server] Command-rate throttle active on slot {slot} " +
                                         $"({dropped} packets dropped this match).");
                        }
                    }
                    break;

                case PacketType.TickCommandsMerged:
                    // Story 9.3: the merged tick is server-authored (server → client ONLY). A client that sends
                    // one is spoofing the authoritative stream — hard-reject, no state change. (Defense in depth:
                    // Server.ServerPacketRelay.FanInTickCommands ALSO type-rejects merged-shaped bytes before the
                    // builder, and MergedTickBuilder.Submit rejects them again — both Tier-1-tested (DW-394); this
                    // arm is the outermost dispatch guard + the server-console diagnostic.) DW-392: the reject log
                    // is BOUNDED via the per-peer violation counter — it was one PrintErr per packet, a soft
                    // log-write DoS a client could drive at wire rate.
                    if (_violations.Record(slot))
                        GD.PrintErr($"[Server] Rejected merged-shaped packet from slot {slot} — TickCommandsMerged is " +
                                    $"server→client only ({_violations.Count(slot)} protocol violations this connection).");
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
                    if (_state == State.InGame && _serverHost != null && slot < ExpectedPlayers &&
                        TickCommandPacket.TryReadChecksum(data, len, out uint ckTick, out uint ckHash))
                        _serverHost.OnChecksum(slot, ckTick, ckHash);
                    break;
                // PacketType.DesyncAlert is now SERVER-GENERATED (clients never send it) — the old relay case is gone.

                case PacketType.Chat:
                {
                    // Story 9.3 (chat-spoof fix): the client's faction byte is spoofable, so RE-STAMP it from the
                    // sender's transport-authoritative slot before rebroadcasting (a spectator sender → Neutral).
                    // The decode → StampChatFaction → MakeChat re-encode pipeline is the Godot-free, Tier-1-tested
                    // Server.ServerPacketRelay.RestampChat (DW-394), so no client-supplied faction byte survives.
                    byte[]? restamped = Server.ServerPacketRelay.RestampChat(
                        slot, data, len, SLOT_FACTION, ExpectedPlayers);
                    if (restamped != null)
                    {
                        _transport.BroadcastReliable(restamped);
                    }
                    else if (_violations.Record(slot))
                    {
                        // Story 9.3: the old relay rebroadcast raw bytes unconditionally; the re-stamp path can only
                        // rebroadcast a chat it can decode. Log the drop so a malformed chat is observable, not
                        // silent. DW-392: bounded via the per-peer violation counter (was one PrintErr per packet).
                        GD.PrintErr($"[DedicatedServer] Dropped undecodable Chat from slot {slot} ({len} bytes; " +
                                    $"{_violations.Count(slot)} protocol violations this connection).");
                    }
                    break;
                }

                case PacketType.MapPing:
                    // Story 11.4 (FR-74): relay a minimap map-ping to every connected peer (presentation-only,
                    // reliable side-channel, never folded). RE-STAMP the sender's authoritative faction from its
                    // transport slot (never the spoofable client byte), mirroring the Chat relay.
                    if (TickCommandPacket.TryReadMapPing(data, len, out _, out int pingX, out int pingZ))
                    {
                        Faction stampedPing = Server.ServerLobbyPolicy.StampChatFaction(
                            slot, SLOT_FACTION, ExpectedPlayers);
                        _transport.BroadcastReliable(TickCommandPacket.MakeMapPing(stampedPing, pingX, pingZ));
                    }
                    else if (_violations.Record(slot))
                    {
                        // DW-392: an undecodable MapPing was previously a fully SILENT drop — count it as a protocol
                        // violation and log BOUNDED so malformed-packet spam is observable without a per-packet write.
                        GD.PrintErr($"[DedicatedServer] Dropped undecodable MapPing from slot {slot} ({len} bytes; " +
                                    $"{_violations.Count(slot)} protocol violations this connection).");
                    }
                    break;

                case PacketType.LobbyChat:
                    // Story 9.7 (P1): relay PRE-MATCH lobby chat to every connected peer so chat works on the
                    // dedicated/online path (not just P2P). Mirrors the in-match Chat relay: RE-STAMP the sender's
                    // AUTHORITATIVE faction from its transport slot (never the spoofable client byte), re-encode via
                    // MakeLobbyChat, and BroadcastReliable.
                    if (TickCommandPacket.TryReadLobbyChat(data, len, out _, out string lobbyMsg))
                    {
                        Faction stampedLobby = Server.ServerLobbyPolicy.StampChatFaction(
                            slot, SLOT_FACTION, ExpectedPlayers);
                        _transport.BroadcastReliable(TickCommandPacket.MakeLobbyChat(stampedLobby, lobbyMsg));
                    }
                    else if (_violations.Record(slot))
                    {
                        // DW-392: bounded via the per-peer violation counter (was one PrintErr per packet).
                        GD.PrintErr($"[DedicatedServer] Dropped undecodable LobbyChat from slot {slot} ({len} bytes; " +
                                    $"{_violations.Count(slot)} protocol violations this connection).");
                    }
                    break;
            }
        }

        // ── Lobby handshake ───────────────────────────────────────────────────────

        private void HandleReady(int slot, byte[] data, int len)
        {
            if (slot >= ExpectedPlayers) return; // spectators don't send Ready
            if (_state == State.InGame) return;  // match already started — ignore late/duplicate Ready

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

            BroadcastLobbyRoster(); // Story 9.7 (P2): ready state changed — refresh every client's grid

            // Story 9.3: N-shaped count machine — start once `connected == expected && ready == expected` (no
            // `_ready[0] && _ready[1]` two-slot literal). `expected` is the match's player width (MAX_PLAYERS today).
            int connected  = CountConnectedPlayers();
            int readyCount = CountReadyPlayers();
            if (Server.ServerLobbyPolicy.ShouldStart(connected, readyCount, ExpectedPlayers))
            {
                // The connected player slots (arrival order) — collected ONCE and fed to the agreement gate, the
                // roster freeze, and the delay authority so all three read the SAME slot map (DW-397).
                var arrivalSlots = new System.Collections.Generic.List<int>(connected);
                for (int s = 0; s < ExpectedPlayers; s++)
                    if (_transport.IsSlotConnected(s)) arrivalSlots.Add(s);

                // Story 9.4: the ADDITIONAL start-state-agreement gate — every READY PLAYER slot must share one
                // non-zero match-agreement hash AND run PROTOCOL_VERSION. Evaluated over the actual occupied slot
                // set (DW-397), not a dense [0, expected) assumption that would read an unoccupied slot's default-0
                // hash and false-HALT under a non-contiguous layout. On disagreement, broadcast a terminal HALT and
                // do NOT StartGame (fail-closed). Checked BEFORE flipping state / standing up the match machinery.
                HaltReason? disagreement = Server.ServerLobbyPolicy.CheckStartStateAgreement(
                    _readyHash, _readyVersion, arrivalSlots);
                if (disagreement != null)
                {
                    GD.PrintErr($"[Server] Start-state agreement FAILED ({disagreement}) — broadcasting HALT, not starting.");
                    _transport.BroadcastReliable(TickCommandPacket.MakeHalt(0u, disagreement.Value));
                    // DW-398: wipe the collected hashes/versions/ready flags so a later re-Ready (the Story 9.6
                    // reconnect/re-ready path) can never re-run the gate against stale payloads from this aborted
                    // attempt — every player must re-attest from scratch.
                    Server.ServerLobbyPolicy.ResetAgreement(_readyHash, _readyVersion, _ready);
                    _state = State.Lobby;
                    BroadcastLobbyRoster(); // ready flags changed — keep any surviving client's grid truthful
                    return;
                }

                // Story 9.7 (AC1): FREEZE the server-authoritative roster from the connected player slots (arrival
                // order) at StartGame — the single source every downstream faction-stamp reads from (slot →
                // FactionRegistry.ToFaction), never a client packet byte. A duplicate/absent/out-of-range slot
                // rejects the build → HALT (fail-closed) rather than starting a match whose slots we can't attest.
                if (!Server.AssignedRoster.TryFreeze(arrivalSlots, connected, out var roster, out string? rosterErr)
                    || roster == null)
                {
                    GD.PrintErr($"[Server] AssignedRoster freeze FAILED ({rosterErr}) — broadcasting HALT, not starting.");
                    _transport.BroadcastReliable(TickCommandPacket.MakeHalt(0u, HaltReason.StartStateDisagreement));
                    // DW-398: same stale-agreement wipe as the disagreement branch above.
                    Server.ServerLobbyPolicy.ResetAgreement(_readyHash, _readyVersion, _ready);
                    _state = State.Lobby;
                    BroadcastLobbyRoster();
                    return;
                }
                _roster = roster;

                _state = State.InGame;

                // Story 9.3/9.7: stand up the authoritative merged fan-in for this match — expected = the connected
                // player count (== ExpectedPlayers at the gate), faction-stamped from the frozen AssignedRoster (its
                // slot→faction array is identical to SLOT_FACTION for the occupied prefix, but sourced from the
                // attested roster). Spectators are NOT counted (they never submit).
                _builder = new Server.MergedTickBuilder(connected, _roster.SlotFactions());

                // Story 9.4: stand up the server delay authority alongside the fan-in. The initial delay baseline is
                // LockstepManager.INPUT_DELAY (the delay the clients start at), so no directive issues until a
                // measured RTT genuinely shifts the target. Constructed from the SAME slot map the gate + roster
                // used (DW-397) so RTT/ACK indexing tracks the actual occupied slots, sized to the transport's slot
                // bound so a high slot id can never run off the end.
                _delayController = new Server.DelayController(
                    arrivalSlots, ServerTransport.MAX_SLOTS, LockstepManager.INPUT_DELAY);

                // DW-401: the delay-frontier fields are SIBLINGS of the controller and must reset with it — a
                // second match in one server process would otherwise compute its first directive's applyAtTick
                // forward from the PRIOR match's frontier (scheduling it at an unreachable tick) and inherit a
                // mid-interval ping accumulator. The probe seq resets implicitly: it lives in the new controller.
                _latestSeenTick = 0;
                _sincePing      = 0;

                // Story 9.6: stand up the ACK-gated freeze authority alongside the fan-in + delay authority, and
                // cache the frozen-slot drain's broadcast sink (BroadcastCommands reaches every peer + spectator).
                _dropController  = new Server.DropController(connected);
                _injectBroadcast = (buf, n) => _transport.BroadcastCommands(buf, n);

                // Story 9.13: stand up the per-slot command-rate throttle. Sized to MAX_SLOTS (not `connected`) so the
                // slot argument HandlePacket already resolved via ServerTransport.FindSlot indexes it directly, and a
                // spectator/higher slot never runs off the end. Anti-spam only — its accept/drop never enters the sim.
                _rateLimiter = new Server.CommandRateLimiter(ServerTransport.MAX_SLOTS);

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
                _state = State.Lobby;
            }
        }

        // ── Command fan-in (Story 9.3) ─────────────────────────────────────────────

        /// <summary>
        /// Story 9.3: fan a player's single-faction TickCommands into the authoritative
        /// <see cref="Server.MergedTickBuilder"/>. When the last expected player's submission completes the tick,
        /// broadcast the ONE merged packet to ALL peers (players + spectators) on CH_COMMANDS. All the
        /// determinism-critical work (faction re-stamp / spoof-drop / over-count-drop / merged-from-client
        /// hard-reject / ascending sort / byte-ceiling drop) lives in the Godot-free builder, and the COMPOSITION
        /// (submit → frontier advance → build → broadcast) is the Godot-free, Tier-1-tested
        /// <see cref="Server.ServerPacketRelay.FanInTickCommands"/> (DW-394) — this node is a thin adapter
        /// (transport in → relay → transport out). The broadcast sink is the cached <see cref="_injectBroadcast"/>
        /// (constructed alongside <see cref="_builder"/> at StartGame — no per-packet lambda alloc).
        /// </summary>
        private void FanInTickCommands(int fromSlot, byte[] data, int len)
        {
            if (_builder == null || _injectBroadcast == null) return; // not yet InGame
            var fanIn = Server.ServerPacketRelay.FanInTickCommands(_builder, fromSlot, data, len,
                ref _latestSeenTick, _injectBroadcast);
            if ((fanIn == Server.CommandFanInResult.Dropped ||
                 fanIn == Server.CommandFanInResult.RejectedMergedFromClient) &&
                _violations.Record(fromSlot))
            {
                // DW-392: a fan-in drop (faction spoof / over-count / malformed / replayed-or-aliased tick /
                // duplicate resubmit / spectator submit — MergedTickBuilder.Submit's false arms, surfaced here as
                // the relay's Dropped/RejectedMergedFromClient results) is misbehavior a correct lockstep client
                // never produces, and it was previously swallowed with NO log — a server-invisible grief vector
                // (the DW-393 stalled-merge freeze would be undiagnosable). Count it per-peer and log BOUNDED
                // (the 1st, then every LOG_EVERY-th) so it is observable without opening a log-write DoS. Only
                // CLIENT packets route here — FrozenSlotInjector's expected idempotent Submit-false empties go
                // straight to the builder, never through this adapter.
                GD.PrintErr($"[Server] Dropped invalid TickCommands from slot {fromSlot} " +
                            $"({_violations.Count(fromSlot)} protocol violations this connection).");
            }

            // Story 9.6: a survivor's submit just advanced the frontier — inject empties for any frozen slot so the
            // now-fannable ticks (survivor arrived, frozen slot silent) complete and the survivor unstalls.
            PumpFrozenInjection();
        }

        // ── Helpers ───────────────────────────────────────────────────────────────

        // Both counting helpers + the start gate delegate to the Godot-free Server.ServerLobbyPolicy so the policy
        // is Tier-1 unit-testable (spectators — slots >= MAX_PLAYERS — are excluded by the maxPlayers bound).
        private int CountConnectedPlayers()
            => Server.ServerLobbyPolicy.CountConnectedPlayers(_transport.IsSlotConnected, ExpectedPlayers);

        private int CountReadyPlayers()
            => Server.ServerLobbyPolicy.CountReadyPlayers(_transport.IsSlotConnected, s => _ready[s], ExpectedPlayers);

        /// <summary>Story 9.7 (P2): broadcast the authoritative pre-match lobby roster/ready snapshot to every peer so
        /// each client's N-slot grid reflects remote-slot occupancy + readiness (unobservable otherwise on the
        /// dedicated path). Suppressed once InGame (the roster is a lobby-phase signal only).</summary>
        private void BroadcastLobbyRoster()
        {
            if (_state == State.InGame) return;
            int n = ExpectedPlayers;
            for (int s = 0; s < n; s++)
            {
                _rosterOccupied[s] = _transport.IsSlotConnected(s);
                _rosterReady[s]    = _rosterOccupied[s] && _ready[s];
            }
            _transport.BroadcastReliable(TickCommandPacket.MakeLobbyRoster(n, _rosterOccupied, _rosterReady));
        }

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
