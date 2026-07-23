#nullable enable
using System;
using Godot;
using ProjectChimera.Core;
using ProjectChimera.Dsl; // Story 7.9 — EventBounds.MaxDslEventsPerTick
using ProjectChimera.Multiplayer.Server; // Story 9.3 — MergedTickApplier (the sole client command source)

namespace ProjectChimera.Multiplayer
{
    /// <summary>
    /// Deterministic lockstep coordinator with adaptive input-delay buffering.
    ///
    /// Input delay model (GDD §6):
    ///   A command issued at tick T executes at tick T + _currentDelay.
    ///   This pre-buffers N ticks of latency tolerance: as long as the remote peer's
    ///   commands arrive within _currentDelay ticks, the simulation never stalls.
    ///
    /// Adaptive delay:
    ///   RTT is measured via Ping/Pong packets every ~60 ticks (~2 s).
    ///   _currentDelay tracks a smoothed estimate of one-way latency rounded up to
    ///   the nearest tick, plus one tick of margin.  When the target differs from the
    ///   current delay, both peers negotiate a change via DelayProposal packets — both
    ///   sides agree on the new delay and the tick at which it takes effect, ensuring
    ///   the change is deterministic and does not cause desync.
    ///
    /// Server-authoritative merged tick (Story 9.3):
    ///   The client still SENDS its own single-faction TickCommands for issueTick, but it APPLIES only the
    ///   server's merged echo — one TickCommandsMerged per tick carrying every faction's sub-bundle (the local
    ///   one re-stamped and included). It gates on a single merged-arrival ring (_mergedArrived / _mergedTickFor /
    ///   _mergedBytes) and applies via MergedTickApplier — the SOLE command source, so every peer + spectator
    ///   applies byte-identical bytes in the same ascending-faction order. The client no longer self-applies, and
    ///   the two-slot p1Ready/p2Ready demux is gone.
    ///
    /// Match start bootstrap:
    ///   Ticks 0.._currentDelay-1 are pre-filled with empty command sets on both sides.
    ///
    /// Offline mode (IsOnline = false):
    ///   Commands pass through immediately.  No network traffic, zero overhead.
    /// </summary>
    public class LockstepManager
    {
        // ── Delay tuning ──────────────────────────────────────────────────────

        /// <summary>Starting input delay (ticks). Adaptive logic adjusts from here.</summary>
        public const int INPUT_DELAY = 4;

        // Story 1.11 (AC2a): the [MIN_DELAY, MAX_DELAY] clamp and the RTT→delay / cross-peer agreement math were
        // extracted to the Godot-free DelayMath helper so they are Tier-1 unit-testable. This class now delegates
        // to DelayMath.ComputeTargetDelay / DelayMath.AgreeDelay (behavior-neutral). TICK_MS also lives there.

        /// <summary>Circular buffer slots (must be a power of two and &gt; <c>DelayMath.MAX_DELAY</c> + 1).</summary>
        private const int BUFFER_SIZE = 16;
        private const int BUFFER_MASK = BUFFER_SIZE - 1;

        private const float RTT_ALPHA  = 0.125f;       // EWMA smoothing weight for RTT samples
        private const uint  PING_INTERVAL_TICKS = 60;  // send a ping every ~2 seconds

        // ── Events ────────────────────────────────────────────────────────────

        /// <summary>Fires when a desync is detected: (tick, localHash, remoteHash).</summary>
        public event Action<uint, uint, uint>? OnDesync;

        /// <summary>
        /// Fires ONCE when the authoritative server orders a TERMINAL halt for this client (Story 1.9a):
        /// either a DesyncAlert (this peer is the named minority of a strict majority) or a global Halt
        /// (no majority). Args: (tick, canonicalHash, hasCanonical). For a DesyncAlert hasCanonical is true and
        /// canonicalHash is the strict-majority hash; for a global no-majority Halt hasCanonical is false (and
        /// canonicalHash is 0). Consumers MUST branch on hasCanonical, NOT on "canonicalHash != 0" — 0 is a
        /// legitimate 32-bit checksum, so an attributed desync whose hash is 0 would otherwise be mislabeled as a
        /// global halt. The sim stops advancing; the presentation shows the terminal HALT overlay (UX-DR64e),
        /// DISTINCT from the recoverable stall banner.
        /// </summary>
        public event Action<uint, uint, bool>? OnHalt;

        /// <summary>Terminal once the server has ordered a halt (Story 1.9a). Gates <see cref="Flush"/>.</summary>
        private bool _halted;

        /// <summary>Fires when a chat message arrives. Args: (senderFaction, message).</summary>
        public event Action<Faction, string>? OnChatReceived;

        // ── Replay recording ──────────────────────────────────────────────────

        /// <summary>
        /// Optional replay recorder. When non-null, both players' commands are written
        /// to file after each executed tick. Assign before GoOnline; null-out after match ends.
        /// </summary>
        public ReplayRecorder? Recorder;

        // ── Path-request bridges (wired by MainScene) ─────────────────────────

        /// <summary>Called when a Move order should request a flow-field path. Args: (unitId, destX, destZ).</summary>
        public Action<int, float, float>? OnRequestPath;
        /// <summary>Called when an AttackMove order should request a path.</summary>
        public Action<int, float, float>? OnRequestAttackMove;
        /// <summary>Called when Stop or Hold should cancel any pending path.</summary>
        public Action<int>? OnCancelPath;

        /// <summary>Story 2.8 (D-1): the production system the shared OrderApplier uses to EXECUTE a Train command at
        /// exec-tick (the deterministic spend + queue on the canonical BuildingStore/ResourceStore). Wired by MainScene
        /// per match; null in headless/tests where Train no-ops. Must be the SAME instance the replay/offline paths use,
        /// or human-vs-human training diverges. Story 2.12: ALSO the SetRally exec-tick handler (SetRallyCommand).</summary>
        public ProjectChimera.Economy.BuildingSystem? Buildings;

        /// <summary>Story 3.15: the item/inventory runtime the shared OrderApplier uses to EXECUTE a UseItem / DropItem
        /// command (the deterministic charge-decrement / heal / ground-return on the canonical ItemStore/HeroStore).
        /// Wired by MainScene per match; null in headless/tests where UseItem/DropItem no-op. Must be the SAME instance
        /// the replay/offline paths use, or item use/drop diverges between live and replay.</summary>
        public ProjectChimera.Combat.ItemSystem? Items;

        /// <summary>Story 4.9: the research runtime the shared OrderApplier uses to EXECUTE a StartResearch / CancelResearch
        /// command at exec-tick (the deterministic spend/refund + progress on the canonical ResearchStore/ResourceStore).
        /// Wired by MainScene per match; null in headless/tests where the two commands no-op. Must be the SAME instance
        /// the replay/offline paths use, or research diverges between live and replay.</summary>
        public ProjectChimera.Economy.ResearchSystem? Research;

        /// <summary>Story 2.12 (AC4): the presentation event bus the shared OrderApplier pushes an OrderDenied event to
        /// when a Shift-queued order is rejected on a full ring. Wired by MainScene per match; null → the reject is still
        /// deterministic (it reads the folded OrderQueueCount), only the visual feedback is skipped. Presentation-only.</summary>
        public ProjectChimera.Combat.CombatEventQueue? CombatEvents;

        /// <summary>Story 7.9: the sim-side authorized-enqueue handle the shared OrderApplier calls to apply a
        /// button-originated DslEvent order at exec-tick (<c>ScenarioDirector.TryEnqueueExternalDslEvent</c>: eventIndex,
        /// raiserSlot, arg0, arg1 → bool). Wired by MainScene per match; null in headless/tests where DslEvent no-ops.
        /// Must be the SAME instance the replay/offline paths use, or button raises diverge between live and replay.</summary>
        public Func<int, int, int, int, bool>? DslEventSink;

        // ── Public state ──────────────────────────────────────────────────────

        public bool IsOnline   { get; private set; }
        /// <summary>True while waiting for the remote peer's commands for the current exec tick.</summary>
        public bool IsStalling { get; private set; }
        /// <summary>True when observing a match without participating.</summary>
        public bool IsSpectator { get; private set; }
        /// <summary>The local player's faction (set when the match starts).</summary>
        public Faction LocalFaction { get; private set; } = Faction.Player1;

        /// <summary>Active input-delay ticks (adapted from RTT measurements).</summary>
        public int CurrentDelay => _currentDelay;

        // ── Deps ──────────────────────────────────────────────────────────────

        private readonly ENetTransport _transport;
        private readonly EntityWorld   _world;

        // ── Mutable delay ─────────────────────────────────────────────────────

        private int _currentDelay = INPUT_DELAY;

        // ── RTT measurement ───────────────────────────────────────────────────

        private float _smoothedRttMs = INPUT_DELAY * DelayMath.TICK_MS * 2f; // initial estimate
        private byte  _pingSeq;
        private uint  _lastPingSentTick;
        private uint  _lastPingSentMs;   // wall-clock ms at the time we sent the last ping

        // ── Delay-change negotiation ──────────────────────────────────────────

        private bool  _pendingDelayChange;
        private int   _pendingNewDelay;
        private uint  _pendingApplyTick;

        // Deduplicate outgoing proposals to prevent echo loops.
        private int   _lastSentProposalDelay  = -1;
        private uint  _lastSentProposalApplyAt;

        // ── Input accumulator ─────────────────────────────────────────────────

        private readonly UnitOrder[] _pendingOrders = new UnitOrder[TickCommandPacket.MAX_ORDERS];
        private int                  _pendingCount;
        // Story 7.9 — how many of _pendingOrders this tick are DslEvent raises, so EnqueueDslEvent can enforce the
        // per-player MaxDslEventsPerTick cap (drop-newest). Reset alongside _pendingCount when the batch is sent.
        private int                  _pendingDslEventCount;

        // ── Send-tracking (Story 9.3) ─────────────────────────────────────────
        // The client still SENDS its own single-faction commands for issueTick; _localSent guards one send per
        // issueMod. There is no longer a local/remote apply buffer — the sole command source is the server's
        // merged echo (below).
        private readonly bool[] _localSent = new bool[BUFFER_SIZE];

        // ── Merged-arrival ring (Story 9.3) ───────────────────────────────────
        // One authoritative TickCommandsMerged per tick, keyed by tick % BUFFER_SIZE. Preallocated byte buffers
        // (copied into on receipt) so the receive path never allocates. A seeded bootstrap-gap tick stores len 0
        // (an empty merged → the applier is a deterministic no-op).
        private readonly bool[]   _mergedArrived = new bool[BUFFER_SIZE];
        private readonly uint[]   _mergedTickFor = new uint[BUFFER_SIZE];
        private readonly byte[][] _mergedBytes;
        private readonly int[]    _mergedLen     = new int[BUFFER_SIZE];

        // ── Send buffers ──────────────────────────────────────────────────────

        private readonly byte[] _sendBuf = new byte[
            TickCommandPacket.HEADER_BYTES + TickCommandPacket.MAX_ORDERS * UnitOrder.SIZE];
        private readonly byte[] _checksumBuf = new byte[9];

        // ── Checksum tracking ─────────────────────────────────────────────────

        private uint _pendingLocalChecksum;
        private bool _localChecksumReady;

        // ── Replay recording (Story 9.3) ──────────────────────────────────────
        // The recorder is now fed from the single authoritative merged stream (all factions, ascending), via
        // MergedTickApplier's per-sub-bundle hook. _recordTick carries the exec tick to the cached hook so no
        // closure is allocated per tick.
        private uint _recordTick;
        private readonly Action<Faction, UnitOrder[], int, int> _recordHook;

        // ── Init ──────────────────────────────────────────────────────────────

        public LockstepManager(ENetTransport transport, EntityWorld world)
        {
            _transport = transport;
            _world     = world;

            _mergedBytes = new byte[BUFFER_SIZE][];
            for (int i = 0; i < BUFFER_SIZE; i++)
                _mergedBytes[i] = new byte[MergedTickPacket.MERGED_MAX_BYTES];

            _recordHook = (faction, buf, baseIdx, count) =>
                Recorder?.RecordTick(_recordTick, faction, buf, baseIdx, count);

            _transport.OnPacketReceived += HandlePacket;
        }

        // ── Match lifecycle ───────────────────────────────────────────────────

        /// <summary>
        /// Switch to online mode. Pre-seeds the first _currentDelay ticks with empty
        /// command sets so the sim can run through them without stalling.
        /// </summary>
        public void GoOnline(Faction localFaction)
        {
            LocalFaction  = localFaction;
            IsOnline      = true;
            IsStalling    = false;
            _pendingCount = 0;
            _pendingDslEventCount = 0;
            ResetAdaptiveState();
            SeedInitialTicks();

            GD.Print($"[Lockstep] Online as {localFaction}. " +
                     $"Initial delay: {_currentDelay} ticks ({_currentDelay * 33}ms at 30 Hz).");
        }

        public void GoOffline()
        {
            IsOnline    = false;
            IsSpectator = false;
            IsStalling  = false;
        }

        /// <summary>Spectator mode: both P1+P2 command streams arrive from the network.</summary>
        public void GoSpectate()
        {
            LocalFaction  = Faction.Neutral;
            IsSpectator   = true;
            IsOnline      = true;
            IsStalling    = false;
            _pendingCount = 0;
            _pendingDslEventCount = 0;
            ResetAdaptiveState();

            // Story 9.3: a spectator consumes the SAME server-built merged stream as the players — one merged
            // packet per tick, not a P1/P2 demux. Seed the bootstrap-gap ticks (0.._currentDelay-1) as empty.
            SeedInitialTicks();

            GD.Print("[Lockstep] Spectating. Consuming the server-authoritative merged tick stream.");
        }

        // ── Command accumulation ──────────────────────────────────────────────

        /// <summary>
        /// Queue a local command for this tick.
        /// Returns true (apply now) in offline mode; false (deferred) in online mode.
        /// </summary>
        public bool EnqueueOrder(int unitId, UnitCommand command, Fixed targetX, Fixed targetZ)
        {
            if (!IsOnline)   return true;
            if (IsSpectator) return false;

            if (_pendingCount < TickCommandPacket.MAX_ORDERS)
                _pendingOrders[_pendingCount++] = new UnitOrder(unitId, command, targetX, targetZ);

            return false;
        }

        /// <summary>
        /// Story 7.9 — queue a custom-UI Button's custom-event raise on the lockstep bus. Mirrors
        /// <see cref="EnqueueOrder"/>: OFFLINE (F5 playtest) it applies immediately through the SAME
        /// <see cref="OrderApplier.Apply"/> the online/replay paths use (structural parity) so the event enters the
        /// director's queue that tick; ONLINE it buffers a <see cref="UnitCommand.DslEvent"/> order for the exec-tick
        /// (applied identically on every peer, recorded to replay). The <see cref="LocalFaction"/> becomes the raiser
        /// at apply time (OrderApplier derives the 0-based slot). Enforces <see cref="EventBounds.MaxDslEventsPerTick"/>
        /// DETERMINISTICALLY drop-newest (never a throw), and shares the existing 32-order packet budget. Spectators
        /// cannot raise (they own no faction). Returns true when applied now (offline), false when buffered/dropped.
        /// </summary>
        public bool EnqueueDslEvent(int eventIndex, int arg0, int arg1)
        {
            // Offline (F5): apply-now through the shared applier — the raiser is ALWAYS Player1 (the offline
            // editor's local faction), structurally, never whatever LocalFaction a previous online/spectate session
            // left behind (GoOnline/GoSpectate mutate it and nothing resets it on the way back offline; a stale
            // Neutral would make every offline press a silent authorization drop).
            if (!IsOnline)
            {
                var order = new UnitOrder(eventIndex, UnitCommand.DslEvent, Fixed.FromRaw(arg0), Fixed.FromRaw(arg1));
                OrderApplier.Apply(_world, in order, Faction.Player1,
                    OnRequestPath, OnRequestAttackMove, OnCancelPath, Buildings, CombatEvents, Items, Research, DslEventSink);
                return true;
            }
            if (IsSpectator) return false;

            // Online: buffer for the exec-tick, honouring BOTH the per-tick DslEvent cap and the shared packet budget
            // (drop-newest — mirrors the _pendingCount < MAX_ORDERS idiom; never throws).
            if (DslEventRateLimit.CanAccept(_pendingDslEventCount, _pendingCount, TickCommandPacket.MAX_ORDERS))
            {
                _pendingOrders[_pendingCount++] = new UnitOrder(eventIndex, UnitCommand.DslEvent,
                    Fixed.FromRaw(arg0), Fixed.FromRaw(arg1));
                _pendingDslEventCount++;
            }
            return false;
        }

        // ── Per-tick flush ────────────────────────────────────────────────────

        /// <summary>
        /// Call once per frame while online.
        ///
        /// Offline: returns true immediately.
        /// Online:
        ///   1. Apply any agreed delay change that has matured.
        ///   2. Optionally send a RTT ping.
        ///   3. Drain pending orders → local buffer → send for issueTick.
        ///   4. Poll transport.
        ///   5. If remote commands for execTick are ready: apply both peers' orders, return true.
        ///      Otherwise stall (return false).
        /// </summary>
        public bool Flush(uint currentTick)
        {
            if (!IsOnline) return true;

            // Server-authoritative terminal HALT (Story 1.9a): once the server orders a halt, the sim stops
            // advancing — permanently. We deliberately do NOT set IsStalling, so the recoverable stall banner
            // stays hidden; the distinct terminal HALT overlay is shown via the OnHalt event instead (UX-DR64e).
            if (_halted) return false;

            // ── Spectator path (Story 9.3) ────────────────────────────────────
            // A spectator sends nothing; it just gates on the single merged-arrival flag and applies the merged
            // packet (all factions, ascending) exactly like a player — one deterministic apply order for both.
            if (IsSpectator)
            {
                int execModS = (int)(currentTick & BUFFER_MASK);

                _transport.Poll();

                if (!(_mergedArrived[execModS] && _mergedTickFor[execModS] == currentTick))
                {
                    IsStalling = true;
                    return false;
                }

                ApplyMerged(execModS, currentTick);
                _mergedArrived[execModS] = false;
                IsStalling = false;
                return true;
            }

            // ── Apply matured delay change ────────────────────────────────────
            if (_pendingDelayChange && currentTick >= _pendingApplyTick)
                CommitDelayChange(currentTick, _pendingNewDelay);

            // ── Periodic RTT ping ─────────────────────────────────────────────
            if (currentTick - _lastPingSentTick >= PING_INTERVAL_TICKS)
                SendPing(currentTick);

            uint issueTick = currentTick + (uint)_currentDelay;
            int  issueMod  = (int)(issueTick & BUFFER_MASK);
            int  execMod   = (int)(currentTick & BUFFER_MASK);

            // ── Send local commands for issueTick (single-faction, client → server) ──
            // The client still sends its own bundle; the server fans it in and echoes it back inside the merged
            // packet. It is NOT self-applied here — the merged echo is the sole command source (Story 9.3).
            if (!_localSent[issueMod])
            {
                int n = _pendingCount;
                _pendingCount = 0;
                _pendingDslEventCount = 0; // Story 7.9 — the DslEvent-per-tick budget resets with the batch

                int bytes = TickCommandPacket.Write(
                    _sendBuf, issueTick, LocalFaction, _pendingOrders, 0, n);
                _transport.SendCommands(_sendBuf, bytes);
                _localSent[issueMod] = true;
            }

            // ── Poll transport ────────────────────────────────────────────────
            _transport.Poll();

            // ── Gate on the merged packet for this exec tick ──────────────────
            if (!(_mergedArrived[execMod] && _mergedTickFor[execMod] == currentTick))
            {
                IsStalling = true;
                return false;
            }

            // ── Apply the authoritative merged tick (its SOLE command source) ──
            ApplyMerged(execMod, currentTick);

            _localSent[execMod]     = false;
            _mergedArrived[execMod] = false;

            IsStalling = false;
            return true;
        }

        /// <summary>
        /// Story 9.3 — apply the stored merged packet for ring slot <paramref name="mod"/> via the single
        /// <see cref="MergedTickApplier"/> core (the same core the spectator path and the FR-39 golden use). The
        /// per-sub-bundle recorder hook feeds the replay recorder from the one authoritative command stream. A
        /// seeded bootstrap/gap tick (len 0) decodes to nothing → a deterministic no-op.
        /// </summary>
        private void ApplyMerged(int mod, uint currentTick)
        {
            _recordTick = currentTick;
            MergedTickApplier.Apply(_mergedBytes[mod], _mergedLen[mod], _world,
                OnRequestPath, OnRequestAttackMove, OnCancelPath,
                Buildings, CombatEvents, Items, Research, DslEventSink,
                Recorder != null ? _recordHook : null);
        }

        // ── Checksum exchange ─────────────────────────────────────────────────

        public void SendChecksum(uint tick, uint localHash)
        {
            // Spectators run the sim (so the SimulationHost checksum sink fires) but must NEVER vote in the
            // server's quorum — a spectator's checksum reaching the collector can complete a tick's bucket on the
            // wrong reporter set, masking a real player desync or forcing a false HALT (D6). The server-side drop
            // (DedicatedServer: slot >= MAX_PLAYERS) is the primary guard; this is the matching client-side one.
            if (!IsOnline || IsSpectator) return;
            _pendingLocalChecksum = localHash;
            _localChecksumReady   = true;
            int len = TickCommandPacket.WriteChecksum(_checksumBuf, tick, localHash);
            _transport.SendReliable(_checksumBuf[..len]);
        }

        /// <summary>
        /// Enter the terminal halt state (Story 1.9a): stop advancing the sim and fire <see cref="OnHalt"/> ONCE.
        /// Idempotent — repeated server alerts after the first are ignored. Clears <see cref="IsStalling"/> so the
        /// recoverable stall banner (which the terminal HALT overlay must be visually distinct from) is not left
        /// showing underneath the overlay.
        /// </summary>
        private void RaiseHalt(uint tick, uint canonicalHash, bool hasCanonical)
        {
            if (_halted) return;
            _halted = true;
            IsStalling = false;
            OnHalt?.Invoke(tick, canonicalHash, hasCanonical);
        }

        // ── Chat ──────────────────────────────────────────────────────────────

        public void SendChat(string message)
        {
            if (!IsOnline || IsSpectator || string.IsNullOrEmpty(message)) return;
            _transport.SendReliable(TickCommandPacket.MakeChat(LocalFaction, message));
        }

        /// <summary>
        /// Story 7.13 (Arm D) — raise a bounded player_chat CODE onto the REPLICATED, tick-stamped rail so every
        /// client (and replay) evaluates it on the identical tick. Rides the EXISTING 11-byte
        /// <see cref="UnitCommand.DslEvent"/> order via <see cref="EnqueueDslEvent"/> (eventIndex =
        /// <see cref="EventBounds.PlayerChatRailCode"/>, arg0 = the chat code, arg1 unused) — NO new wire, NO replay
        /// VERSION change. Only the bounded integer code + the sender's own faction slot enter the tick; the free-text
        /// chat STRING stays on the reliable <see cref="SendChat"/> side-channel for DISPLAY only (never in the tick).
        /// Offline it applies immediately (raiser = Player1); online it buffers for the exec-tick (raiser = LocalFaction),
        /// honouring the same per-tick DslEvent cap and packet budget as any button raise.
        /// </summary>
        public bool SendPlayerChat(int chatCode) => EnqueueDslEvent(EventBounds.PlayerChatRailCode, chatCode, 0);

        // ── Incoming packet dispatch ──────────────────────────────────────────

        private void HandlePacket(byte[] data, int len, int channel)
        {
            if (len < 1) return;
            var type = (PacketType)data[0];

            switch (type)
            {
                case PacketType.TickCommandsMerged:
                    // Story 9.3: the server-authoritative merged tick — the client's SOLE command source (its own
                    // bundle round-trips through the server and comes back re-stamped inside this packet).
                    HandleMergedTick(data, len);
                    break;

                case PacketType.Checksum:
                    // Story 1.9a (D8/D9): DORMANT in server-authoritative online play. The dedicated server now
                    // CONSUMES Checksum packets into its quorum collector and GENERATES DesyncAlert/Halt — clients
                    // no longer receive a peer's raw Checksum, so this P2P compare never fires (no double-fire with
                    // the server path). Kept inert + defensive (a direct-P2P topology without the server would
                    // still use it); the authoritative halt path is the two cases below.
                    if (TickCommandPacket.TryReadChecksum(data, len, out uint cTick, out uint remoteHash))
                    {
                        if (_localChecksumReady && remoteHash != _pendingLocalChecksum)
                        {
                            GD.PrintErr($"[Lockstep] DESYNC at tick {cTick}: " +
                                        $"local=0x{_pendingLocalChecksum:X8} remote=0x{remoteHash:X8}");
                            OnDesync?.Invoke(cTick, _pendingLocalChecksum, remoteHash);
                        }
                        _localChecksumReady = false;
                    }
                    break;

                case PacketType.DesyncAlert:
                    // Server → this client: you are the named minority of a strict majority. Terminal halt.
                    if (TickCommandPacket.TryReadDesyncAlert(data, len, out uint dTick, out uint canon))
                    {
                        GD.PrintErr($"[Lockstep] SERVER DESYNC ALERT @tick {dTick} (canonical 0x{canon:X8}) — this peer diverged.");
                        RaiseHalt(dTick, canon, hasCanonical: true);
                    }
                    break;

                case PacketType.Halt:
                    // Server → everyone: no strict-majority canonical hash. Terminal halt for all peers.
                    if (TickCommandPacket.TryReadHalt(data, len, out uint hTick, out HaltReason hReason))
                    {
                        GD.PrintErr($"[Lockstep] SERVER HALT @tick {hTick} ({hReason}) — match cannot continue.");
                        RaiseHalt(hTick, 0u, hasCanonical: false);
                    }
                    break;

                case PacketType.Chat:
                    if (TickCommandPacket.TryReadChat(data, len, out Faction chatFaction, out string chatMsg))
                        OnChatReceived?.Invoke(chatFaction, chatMsg);
                    break;

                case PacketType.Ping:
                    // Reply immediately with a Pong echoing the sender's timestamp.
                    if (len >= 6)
                        _transport.SendReliable(TickCommandPacket.MakePong(data[1],
                            (uint)(data[2] | (data[3] << 8) | (data[4] << 16) | (data[5] << 24))));
                    break;

                case PacketType.Pong:
                    HandlePong(data, len);
                    break;

                case PacketType.DelayProposal:
                    HandleDelayProposal(data, len);
                    break;
            }
        }

        /// <summary>
        /// Story 9.3 — receive the server-authoritative merged tick. Keyed by its own tick into the merged-arrival
        /// ring; the raw bytes are copied into the preallocated ring buffer (no per-packet allocation) and applied
        /// later in <see cref="Flush"/> when the sim reaches that tick. Identical handling for players and
        /// spectators — the merged packet is the sole command source for both.
        /// </summary>
        private void HandleMergedTick(byte[] data, int len)
        {
            if (!MergedTickPacket.TryPeekTick(data, len, out uint tick)) return;
            if (len > MergedTickPacket.MERGED_MAX_BYTES) return; // over-ceiling → drop (defensive; the codec also rejects)

            int mod = (int)(tick & BUFFER_MASK);
            Array.Copy(data, _mergedBytes[mod], len);
            _mergedLen[mod]     = len;
            _mergedArrived[mod] = true;
            _mergedTickFor[mod] = tick;
        }

        // ── RTT measurement ───────────────────────────────────────────────────

        private void SendPing(uint currentTick)
        {
            _lastPingSentTick = currentTick;
            _lastPingSentMs   = (uint)Time.GetTicksMsec();
            _transport.SendReliable(TickCommandPacket.MakePing(_pingSeq, _lastPingSentMs));
            _pingSeq++;
        }

        private void HandlePong(byte[] data, int len)
        {
            if (!TickCommandPacket.TryReadPong(data, len, out byte seq, out uint senderMs)) return;
            if (seq != (byte)(_pingSeq - 1)) return; // stale pong from a previous seq — ignore

            float rttSample = (float)Time.GetTicksMsec() - senderMs;
            if (rttSample <= 0f || rttSample > 10_000f) return; // sanity-check

            // Exponential weighted moving average.
            _smoothedRttMs = _smoothedRttMs * (1f - RTT_ALPHA) + rttSample * RTT_ALPHA;

            GD.Print($"[Lockstep] RTT sample: {rttSample:F0}ms  smoothed: {_smoothedRttMs:F0}ms");
            MaybeProposeDelayChange();
        }

        // ── Adaptive delay negotiation ────────────────────────────────────────

        /// <summary>
        /// Compute the ideal input delay from the current smoothed RTT. Delegates to the Godot-free
        /// <see cref="DelayMath.ComputeTargetDelay"/> (Story 1.11, AC2a) — ceil(OWL / TICK_MS) + 1 clamped to
        /// [MIN_DELAY, MAX_DELAY] — so the policy is Tier-1 unit-testable.
        /// </summary>
        private int ComputeTargetDelay() => DelayMath.ComputeTargetDelay(_smoothedRttMs);

        private void MaybeProposeDelayChange()
        {
            if (!IsOnline || IsSpectator) return;
            if (_pendingDelayChange) return; // already negotiating

            int target = ComputeTargetDelay();
            if (target == _currentDelay) return; // no change needed

            uint applyAt = ComputeSafeApplyAt(target, 0);
            SendDelayProposal(target, applyAt);

            _pendingDelayChange  = true;
            _pendingNewDelay     = target;
            _pendingApplyTick    = applyAt;
        }

        private void HandleDelayProposal(byte[] data, int len)
        {
            if (!TickCommandPacket.TryReadDelayProposal(data, len,
                    out byte theirDelay, out uint theirApplyAt)) return;

            int  myDesired    = ComputeTargetDelay();
            int  agreedDelay  = DelayMath.AgreeDelay(myDesired, theirDelay);

            // Accept their applyAt if it is still safely in the future;
            // otherwise extend it.  Both peers converge to the same value because
            // the initiator sets a tick that is far ahead, and the responder keeps
            // it unless it has already passed.
            uint agreedApplyAt = theirApplyAt > ComputeSafeApplyAt(agreedDelay, 4)
                ? theirApplyAt
                : ComputeSafeApplyAt(agreedDelay, 0);

            // Update local pending state.
            _pendingDelayChange = true;
            _pendingNewDelay    = agreedDelay;
            // Take the later of the two apply ticks to give both buffers time to catch up.
            _pendingApplyTick   = _pendingApplyTick > agreedApplyAt ? _pendingApplyTick : agreedApplyAt;

            // Respond only if the agreed values differ from what we last sent
            // (prevents infinite echo between the two peers).
            if (agreedDelay != _lastSentProposalDelay || agreedApplyAt != _lastSentProposalApplyAt)
                SendDelayProposal(agreedDelay, _pendingApplyTick);
        }

        private void SendDelayProposal(int delay, uint applyAt)
        {
            _transport.SendReliable(TickCommandPacket.MakeDelayProposal((byte)delay, applyAt));
            _lastSentProposalDelay   = delay;
            _lastSentProposalApplyAt = applyAt;
        }

        /// <summary>
        /// A tick far enough ahead that both peers can pre-seed any gap before it arrives.
        /// extraMargin adds additional slack (e.g. when checking if their proposal is safe).
        /// </summary>
        private uint ComputeSafeApplyAt(int newDelay, uint extraMargin)
        {
            // The sim must advance at least max(currentDelay, newDelay) + 4 ticks before
            // the change takes effect so the buffers are fully drained.
            uint margin = (uint)(Math.Max(_currentDelay, newDelay) * 2 + 8) + extraMargin;
            // _lastPingSentTick is a reasonable proxy for currentTick when called from HandlePong.
            return _lastPingSentTick + margin;
        }

        /// <summary>
        /// Apply a delay change at the agreed tick.
        /// If the new delay is LARGER, pre-seed the gap ticks as empty so neither peer
        /// expects real commands for those slots — both sides do this identically.
        /// If SMALLER, the already-buffered (empty) gap ticks will execute harmlessly.
        /// </summary>
        private void CommitDelayChange(uint currentTick, int newDelay)
        {
            _pendingDelayChange = false;
            if (newDelay == _currentDelay) return;

            if (newDelay > _currentDelay)
            {
                for (uint gap = currentTick + (uint)_currentDelay + 1;
                     gap <= currentTick + (uint)newDelay; gap++)
                {
                    int mod = (int)(gap & BUFFER_MASK);
                    // Both peers pre-seed the gap as empty and do NOT send for it; the server therefore never
                    // emits a merged packet for these ticks, so the client self-seeds an empty merged (len 0 →
                    // the applier no-ops) exactly as it does for the bootstrap-gap ticks (Story 9.3).
                    _localSent[mod]     = true;   // "sent" — both peers treat as empty
                    _mergedArrived[mod] = true;   // "received" — empty merged
                    _mergedTickFor[mod] = gap;
                    _mergedLen[mod]     = 0;
                }
            }

            GD.Print($"[Lockstep] Delay: {_currentDelay} → {newDelay} ticks " +
                     $"(±{newDelay * 33}ms budget at 30 Hz).");
            _currentDelay = newDelay;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private void ResetAdaptiveState()
        {
            _currentDelay            = INPUT_DELAY;
            _smoothedRttMs           = INPUT_DELAY * DelayMath.TICK_MS * 2f;
            _pingSeq                 = 0;
            _lastPingSentTick        = 0;
            _pendingDelayChange      = false;
            _lastSentProposalDelay   = -1;
        }

        private void SeedInitialTicks()
        {
            // Story 9.3: the first REAL merged packet the server can emit is for tick == _currentDelay (the first
            // issueTick both clients send). Ticks 0.._currentDelay-1 therefore have no merged packet and are
            // pre-seeded empty (len 0 → the applier no-ops) so the sim can advance through the bootstrap gap.
            for (int i = 0; i < _currentDelay; i++)
            {
                int mod = i & BUFFER_MASK;
                _localSent[mod]     = true;
                _mergedArrived[mod] = true;
                _mergedTickFor[mod] = (uint)i;
                _mergedLen[mod]     = 0;
            }
        }
    }
}
