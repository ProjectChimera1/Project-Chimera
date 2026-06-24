#nullable enable
using System;
using Godot;
using ProjectChimera.Core;

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
    /// Circular command buffers:
    ///   _localBuf[tick % BUFFER_SIZE]  — local commands keyed by execution tick
    ///   _remoteBuf[tick % BUFFER_SIZE] — remote commands, filled on receipt
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

        private const int   MIN_DELAY   = 2;   // minimum delay (≈ 66ms) — safe floor for LAN
        private const int   MAX_DELAY   = 12;  // maximum delay (≈ 400ms) — gives up to ~200ms OWL

        /// <summary>Circular buffer slots (must be power of 2 and > MAX_DELAY + 1).</summary>
        private const int BUFFER_SIZE = 16;
        private const int BUFFER_MASK = BUFFER_SIZE - 1;

        private const float TICK_MS    = 1000f / 30f; // milliseconds per sim tick at 30 Hz
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

        private float _smoothedRttMs = INPUT_DELAY * TICK_MS * 2f; // initial estimate
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

        // ── Circular command buffers ──────────────────────────────────────────

        private readonly UnitOrder[] _localBuf   = new UnitOrder[BUFFER_SIZE * TickCommandPacket.MAX_ORDERS];
        private readonly int[]       _localCount  = new int[BUFFER_SIZE];
        private readonly bool[]      _localSent   = new bool[BUFFER_SIZE];

        private readonly UnitOrder[] _remoteBuf      = new UnitOrder[BUFFER_SIZE * TickCommandPacket.MAX_ORDERS];
        private readonly int[]       _remoteCount     = new int[BUFFER_SIZE];
        private readonly bool[]      _remoteArrived   = new bool[BUFFER_SIZE];
        private readonly uint[]      _remoteTickFor   = new uint[BUFFER_SIZE];

        // Spectator-only: P1 commands routed into _localBuf
        private readonly bool[]      _localArrived    = new bool[BUFFER_SIZE];
        private readonly uint[]      _localTickFor    = new uint[BUFFER_SIZE];

        private readonly UnitOrder[] _tempBuf = new UnitOrder[TickCommandPacket.MAX_ORDERS];

        // ── Send buffers ──────────────────────────────────────────────────────

        private readonly byte[] _sendBuf = new byte[
            TickCommandPacket.HEADER_BYTES + TickCommandPacket.MAX_ORDERS * UnitOrder.SIZE];
        private readonly byte[] _checksumBuf = new byte[9];

        // ── Checksum tracking ─────────────────────────────────────────────────

        private uint _pendingLocalChecksum;
        private bool _localChecksumReady;

        // ── Init ──────────────────────────────────────────────────────────────

        public LockstepManager(ENetTransport transport, EntityWorld world)
        {
            _transport = transport;
            _world     = world;

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
            ResetAdaptiveState();

            for (int i = 0; i < _currentDelay; i++)
            {
                int mod = i & BUFFER_MASK;
                _localCount[mod]    = 0;
                _localArrived[mod]  = true;
                _localTickFor[mod]  = (uint)i;
                _remoteCount[mod]   = 0;
                _remoteArrived[mod] = true;
                _remoteTickFor[mod] = (uint)i;
            }

            GD.Print("[Lockstep] Spectating. Both faction streams routed from network.");
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

            // ── Spectator path ────────────────────────────────────────────────
            if (IsSpectator)
            {
                int execModS = (int)(currentTick & BUFFER_MASK);

                _transport.Poll();

                bool p1Ready = _localArrived[execModS]  && _localTickFor[execModS]  == currentTick;
                bool p2Ready = _remoteArrived[execModS] && _remoteTickFor[execModS] == currentTick;
                if (!p1Ready || !p2Ready)
                {
                    IsStalling = true;
                    return false;
                }

                int p1Base = execModS * TickCommandPacket.MAX_ORDERS;
                int p2Base = execModS * TickCommandPacket.MAX_ORDERS;
                ApplyOrders(_localBuf,  p1Base, _localCount[execModS],  Faction.Player1);
                ApplyOrders(_remoteBuf, p2Base, _remoteCount[execModS], Faction.Player2);

                _localArrived[execModS]  = false;  _localCount[execModS]  = 0;
                _remoteArrived[execModS] = false;  _remoteCount[execModS] = 0;
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

            // ── Send local commands for issueTick ─────────────────────────────
            if (!_localSent[issueMod])
            {
                int n = _pendingCount;
                _localCount[issueMod] = n;
                int base_ = issueMod * TickCommandPacket.MAX_ORDERS;
                for (int i = 0; i < n; i++)
                    _localBuf[base_ + i] = _pendingOrders[i];
                _pendingCount = 0;

                int bytes = TickCommandPacket.Write(
                    _sendBuf, issueTick, LocalFaction,
                    _localBuf, base_, n);
                _transport.SendCommands(_sendBuf, bytes);
                _localSent[issueMod] = true;
            }

            // ── Poll transport ────────────────────────────────────────────────
            _transport.Poll();

            // ── Check if execution tick is ready ──────────────────────────────
            bool remoteReady = _remoteArrived[execMod] && _remoteTickFor[execMod] == currentTick;
            if (!remoteReady)
            {
                IsStalling = true;
                return false;
            }

            // ── Apply both peers' commands ────────────────────────────────────
            int localBase  = execMod * TickCommandPacket.MAX_ORDERS;
            int remoteBase = execMod * TickCommandPacket.MAX_ORDERS;

            var remoteFaction = LocalFaction == Faction.Player1 ? Faction.Player2 : Faction.Player1;
            ApplyOrders(_localBuf,  localBase,  _localCount[execMod],  LocalFaction);
            ApplyOrders(_remoteBuf, remoteBase, _remoteCount[execMod], remoteFaction);

            if (Recorder != null)
            {
                Recorder.RecordTick(currentTick, LocalFaction,
                    _localBuf, localBase, _localCount[execMod]);
                Recorder.RecordTick(currentTick, remoteFaction,
                    _remoteBuf, remoteBase, _remoteCount[execMod]);
            }

            _localSent[execMod]    = false;  _localCount[execMod]  = 0;
            _remoteArrived[execMod] = false; _remoteCount[execMod] = 0;

            IsStalling = false;
            return true;
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

        // ── Incoming packet dispatch ──────────────────────────────────────────

        private void HandlePacket(byte[] data, int len, int channel)
        {
            if (len < 1) return;
            var type = (PacketType)data[0];

            switch (type)
            {
                case PacketType.TickCommands:
                    HandleTickCommands(data, len);
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

        private void HandleTickCommands(byte[] data, int len)
        {
            if (!TickCommandPacket.TryRead(data, len, out uint tick, out Faction cmdFaction,
                                           _tempBuf, out int count)) return;

            int mod   = (int)(tick & BUFFER_MASK);
            int base_ = mod * TickCommandPacket.MAX_ORDERS;

            if (IsSpectator)
            {
                if (cmdFaction == Faction.Player1)
                {
                    _localCount[mod] = count;
                    for (int i = 0; i < count; i++) _localBuf[base_ + i] = _tempBuf[i];
                    _localArrived[mod] = true;
                    _localTickFor[mod] = tick;
                }
                else
                {
                    _remoteCount[mod] = count;
                    for (int i = 0; i < count; i++) _remoteBuf[base_ + i] = _tempBuf[i];
                    _remoteArrived[mod] = true;
                    _remoteTickFor[mod] = tick;
                }
            }
            else
            {
                _remoteCount[mod] = count;
                for (int i = 0; i < count; i++) _remoteBuf[base_ + i] = _tempBuf[i];
                _remoteArrived[mod] = true;
                _remoteTickFor[mod] = tick;
            }
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
        /// Compute the ideal input delay from the current smoothed RTT.
        /// = ceil(OWL / TICK_MS) + 1, clamped to [MIN_DELAY, MAX_DELAY].
        /// The +1 provides a one-tick safety margin above the bare minimum.
        /// </summary>
        private int ComputeTargetDelay()
        {
            float owlMs = _smoothedRttMs / 2f;
            int ticks   = (int)Math.Ceiling(owlMs / TICK_MS);
            return Math.Clamp(ticks + 1, MIN_DELAY, MAX_DELAY);
        }

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
            int  agreedDelay  = Math.Max(myDesired, theirDelay);

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
                    _localCount[mod]    = 0;
                    _localSent[mod]     = true;   // "sent" — both peers treat as empty
                    _remoteCount[mod]   = 0;
                    _remoteArrived[mod] = true;   // "received" — both peers treat as empty
                    _remoteTickFor[mod] = gap;
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
            _smoothedRttMs           = INPUT_DELAY * TICK_MS * 2f;
            _pingSeq                 = 0;
            _lastPingSentTick        = 0;
            _pendingDelayChange      = false;
            _lastSentProposalDelay   = -1;
        }

        private void SeedInitialTicks()
        {
            for (int i = 0; i < _currentDelay; i++)
            {
                int mod = i & BUFFER_MASK;
                _localCount[mod]    = 0;
                _localSent[mod]     = true;
                _remoteCount[mod]   = 0;
                _remoteArrived[mod] = true;
                _remoteTickFor[mod] = (uint)i;
            }
        }

        // ── Apply orders to EntityWorld ───────────────────────────────────────

        private void ApplyOrders(UnitOrder[] buf, int baseIdx, int count, Faction expectedFaction)
        {
            for (int i = 0; i < count; i++)
            {
                var o  = buf[baseIdx + i];
                int id = o.UnitId;
                if (!_world.IsAlive(id)) continue;
                if (_world.FactionOf[id] != expectedFaction) continue; // anti-cheat

                _world.CommandState[id] = o.Command;

                switch (o.Command)
                {
                    case UnitCommand.Move:
                    {
                        var target = new FixedVec3(Fixed.FromRaw(o.TargetX), Fixed.Zero,
                                                   Fixed.FromRaw(o.TargetZ));
                        _world.CommandGoal[id]  = target;
                        _world.MoveTarget[id]   = target;
                        _world.Flags[id]        = (_world.Flags[id] | EntityFlags.Moving)
                                                  & ~EntityFlags.Attacking;
                        _world.AttackTarget[id] = -1;
                        OnRequestPath?.Invoke(id, Fixed.FromRaw(o.TargetX).ToFloat(),
                                                  Fixed.FromRaw(o.TargetZ).ToFloat());
                        break;
                    }
                    case UnitCommand.AttackMove:
                    {
                        var target = new FixedVec3(Fixed.FromRaw(o.TargetX), Fixed.Zero,
                                                   Fixed.FromRaw(o.TargetZ));
                        _world.CommandGoal[id]  = target;
                        _world.MoveTarget[id]   = target;
                        _world.Flags[id]        = (_world.Flags[id] | EntityFlags.Moving)
                                                  & ~EntityFlags.Attacking;
                        _world.AttackTarget[id] = -1;
                        OnRequestAttackMove?.Invoke(id, Fixed.FromRaw(o.TargetX).ToFloat(),
                                                        Fixed.FromRaw(o.TargetZ).ToFloat());
                        break;
                    }
                    case UnitCommand.Stop:
                    case UnitCommand.HoldPosition:
                    {
                        _world.Flags[id]        = _world.Flags[id]
                                                  & ~(EntityFlags.Moving | EntityFlags.Attacking);
                        _world.AttackTarget[id] = -1;
                        OnCancelPath?.Invoke(id);
                        break;
                    }
                }
            }
        }
    }
}
