#nullable enable
using System;
using Godot;
using ProjectChimera.Core;

namespace ProjectChimera.Multiplayer
{
    /// <summary>
    /// Deterministic lockstep coordinator with input-delay buffering for 1v1 play.
    ///
    /// Input delay model (GDD §6):
    ///   A command issued at tick T is scheduled to EXECUTE at tick T + INPUT_DELAY.
    ///   This pre-buffers N ticks of latency tolerance: as long as the remote peer's
    ///   commands arrive within INPUT_DELAY ticks (≈133ms at 30 Hz for N=4), the
    ///   simulation never stalls.
    ///
    /// Circular command buffers:
    ///   _localBuf[tick % BUFFER_SIZE]  — local commands keyed by execution tick
    ///   _remoteBuf[tick % BUFFER_SIZE] — remote commands, filled on receipt
    ///   Both are flat UnitOrder arrays: slot s starts at s * MAX_ORDERS.
    ///
    /// Match start bootstrap:
    ///   Ticks 0..INPUT_DELAY-1 are pre-filled with empty command sets on both sides.
    ///   The sim runs through these empty ticks immediately, reaching the first real
    ///   execution tick (INPUT_DELAY) right as the first sent packet is expected.
    ///
    /// Stalling:
    ///   Only occurs when latency exceeds the INPUT_DELAY budget. Visible to the
    ///   player as "Waiting for peer" in the HUD (MainScene.UpdateHud checks IsStalling).
    ///
    /// Offline mode (IsOnline = false):
    ///   Commands pass through immediately. No network traffic, zero overhead.
    /// </summary>
    public class LockstepManager
    {
        // ── Tuning ────────────────────────────────────────────────────────────

        /// <summary>
        /// Number of sim ticks a command is delayed before it executes.
        /// 4 ticks = 133ms at 30 Hz — covers LAN RTT comfortably.
        /// Increase to 6–8 for high-latency internet play.
        /// </summary>
        public const int INPUT_DELAY = 4;

        /// <summary>Circular buffer slots (must be > INPUT_DELAY + 1, power of 2).</summary>
        private const int BUFFER_SIZE = 16;
        private const int BUFFER_MASK = BUFFER_SIZE - 1;

        // ── Events ────────────────────────────────────────────────────────────────

        /// <summary>Fires when a desync is detected: (tick, localHash, remoteHash).</summary>
        public event Action<uint, uint, uint>? OnDesync;

        /// <summary>
        /// Fires when a chat message arrives from the remote peer.
        /// Args: (senderFaction, message).
        /// Fire on the Godot main thread — ENetTransport.Poll() is called from _Process.
        /// </summary>
        public event Action<Faction, string>? OnChatReceived;

        // ── Replay recording ──────────────────────────────────────────────────────

        /// <summary>
        /// Optional replay recorder. When non-null, both players' commands are written
        /// to file after each executed tick. Assign before calling <see cref="GoOnline"/>;
        /// dispose and null-out when the match ends.
        /// </summary>
        public ReplayRecorder? Recorder;

        // ── Path-request bridges (set by MainScene; called from ApplyOrders) ──────
        // These let LockstepManager trigger FlowFieldBridge (Godot layer) without
        // taking a direct dependency on Godot types. Args: (unitId, worldDestX, worldDestZ).

        /// <summary>Called when a Move order should request a flow-field path. Args: (unitId, destX, destZ).</summary>
        public Action<int, float, float>? OnRequestPath;
        /// <summary>Called when an AttackMove order should request a path.</summary>
        public Action<int, float, float>? OnRequestAttackMove;
        /// <summary>Called when a Stop or Hold order should cancel any pending path.</summary>
        public Action<int>? OnCancelPath;

        // ── Public state ──────────────────────────────────────────────────────────

        public bool IsOnline   { get; private set; }
        /// <summary>True while waiting for the remote peer's commands for the current exec tick.</summary>
        public bool IsStalling { get; private set; }
        /// <summary>
        /// True when this peer is observing a match without participating.
        /// Both P1 and P2 command streams arrive from the network; no commands are sent.
        /// </summary>
        public bool IsSpectator { get; private set; }

        // ── Deps ──────────────────────────────────────────────────────────────────

        private readonly ENetTransport _transport;
        private readonly EntityWorld   _world;

        // ── Accumulator for this frame's player input ─────────────────────────────

        private readonly UnitOrder[] _pendingOrders = new UnitOrder[TickCommandPacket.MAX_ORDERS];
        private int                  _pendingCount;

        // ── Circular command buffers (flat: slot s → indices [s*MAX_ORDERS … (s+1)*MAX_ORDERS-1]) ─

        private readonly UnitOrder[] _localBuf  = new UnitOrder[BUFFER_SIZE * TickCommandPacket.MAX_ORDERS];
        private readonly int[]       _localCount = new int[BUFFER_SIZE];
        private readonly bool[]      _localSent  = new bool[BUFFER_SIZE];

        private readonly UnitOrder[] _remoteBuf     = new UnitOrder[BUFFER_SIZE * TickCommandPacket.MAX_ORDERS];
        private readonly int[]       _remoteCount    = new int[BUFFER_SIZE];
        private readonly bool[]      _remoteArrived  = new bool[BUFFER_SIZE];
        private readonly uint[]      _remoteTickFor  = new uint[BUFFER_SIZE];

        // Spectator-only: track arrival of P1 packets routed into _localBuf
        private readonly bool[]      _localArrived   = new bool[BUFFER_SIZE];
        private readonly uint[]      _localTickFor   = new uint[BUFFER_SIZE];

        // Temp buffer for deserializing incoming packets before copying into the circular buffer.
        private readonly UnitOrder[] _tempBuf = new UnitOrder[TickCommandPacket.MAX_ORDERS];

        // ── Send buffer ───────────────────────────────────────────────────────────

        private readonly byte[] _sendBuf = new byte[
            TickCommandPacket.HEADER_BYTES + TickCommandPacket.MAX_ORDERS * UnitOrder.SIZE];

        // ── Checksum tracking ─────────────────────────────────────────────────────

        private uint _pendingLocalChecksum;
        private bool _localChecksumReady;
        private readonly byte[] _checksumBuf = new byte[9];

        // ── Faction assignment ────────────────────────────────────────────────────

        /// <summary>The local player's faction (set when the match starts).</summary>
        public Faction LocalFaction { get; private set; } = Faction.Player1;

        // ── Init ──────────────────────────────────────────────────────────────────

        public LockstepManager(ENetTransport transport, EntityWorld world)
        {
            _transport = transport;
            _world     = world;

            _transport.OnPacketReceived += HandlePacket;
        }

        /// <summary>
        /// Switch to online mode. Pre-seeds the first INPUT_DELAY ticks with empty
        /// command sets so the sim can run through them without stalling.
        /// </summary>
        public void GoOnline(Faction localFaction)
        {
            LocalFaction  = localFaction;
            IsOnline      = true;
            IsStalling    = false;
            _pendingCount = 0;

            // Pre-fill ticks 0..INPUT_DELAY-1 with empty commands on both sides.
            // Both peers do this identically — no commands precede tick INPUT_DELAY.
            for (int i = 0; i < INPUT_DELAY; i++)
            {
                int mod = i & BUFFER_MASK;
                _localCount[mod]   = 0;
                _localSent[mod]    = true;         // "sent" (empty packet)
                _remoteCount[mod]  = 0;
                _remoteArrived[mod] = true;        // pre-filled empty
                _remoteTickFor[mod] = (uint)i;
            }

            GD.Print($"[Lockstep] Online. Playing as {localFaction}. " +
                     $"Input delay: {INPUT_DELAY} ticks ({INPUT_DELAY * 33}ms at 30 Hz).");
        }

        public void GoOffline()
        {
            IsOnline    = false;
            IsSpectator = false;
            IsStalling  = false;
        }

        /// <summary>
        /// Enter spectator mode: both P1 and P2 command streams arrive from the network.
        /// No commands are ever sent. The fog overlay should have RevealAll = true.
        /// </summary>
        public void GoSpectate()
        {
            LocalFaction  = Faction.Neutral;
            IsSpectator   = true;
            IsOnline      = true;
            IsStalling    = false;
            _pendingCount = 0;

            // Pre-seed both command streams for ticks 0..INPUT_DELAY-1 (same as GoOnline).
            for (int i = 0; i < INPUT_DELAY; i++)
            {
                int mod = i & BUFFER_MASK;
                _localCount[mod]    = 0;
                _localArrived[mod]  = true;
                _localTickFor[mod]  = (uint)i;
                _remoteCount[mod]   = 0;
                _remoteArrived[mod] = true;
                _remoteTickFor[mod] = (uint)i;
            }

            GD.Print("[Lockstep] Spectating. Both faction streams will be routed from network.");
        }

        // ── Command accumulation (called by SelectionSystem) ──────────────────────

        /// <summary>
        /// Queue a local command to be sent this tick.
        ///
        /// Offline mode: returns true immediately — caller applies now.
        /// Online mode:  stores command; Flush() will send and apply at the correct tick.
        ///               Returns false — caller must NOT apply immediately.
        /// </summary>
        public bool EnqueueOrder(int unitId, UnitCommand command, Fixed targetX, Fixed targetZ)
        {
            if (!IsOnline)   return true;  // offline pass-through
            if (IsSpectator) return false; // spectators never issue commands

            if (_pendingCount < TickCommandPacket.MAX_ORDERS)
                _pendingOrders[_pendingCount++] = new UnitOrder(unitId, command, targetX, targetZ);

            return false;
        }

        // ── Per-tick flush ────────────────────────────────────────────────────────

        /// <summary>
        /// Call once per frame while online (from MainScene._Process).
        ///
        /// Offline: returns true immediately.
        /// Online:
        ///   1. If not yet sent for issueTick (= currentTick + INPUT_DELAY): drain pending
        ///      orders, store in local buffer, and send to the remote peer.
        ///   2. Poll transport for incoming packets.
        ///   3. If remote commands for execTick (= currentTick) have arrived: apply both
        ///      peers' orders and return true (sim advances one tick).
        ///   4. Otherwise return false (sim stalls — rare on LAN with INPUT_DELAY=4).
        /// </summary>
        public bool Flush(uint currentTick)
        {
            if (!IsOnline) return true;

            // ── Spectator fast-path ────────────────────────────────────────────
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

                _localArrived[execModS]  = false;
                _localCount[execModS]    = 0;
                _remoteArrived[execModS] = false;
                _remoteCount[execModS]   = 0;
                IsStalling = false;
                return true;
            }

            uint issueTick = currentTick + INPUT_DELAY;
            int  issueMod  = (int)(issueTick & BUFFER_MASK);
            int  execMod   = (int)(currentTick & BUFFER_MASK);

            // ── Step 1: Send local commands for issueTick ─────────────────────
            if (!_localSent[issueMod])
            {
                // Copy pending input into the circular buffer slot for issueTick.
                int n = _pendingCount;
                _localCount[issueMod] = n;
                int base_ = issueMod * TickCommandPacket.MAX_ORDERS;
                for (int i = 0; i < n; i++)
                    _localBuf[base_ + i] = _pendingOrders[i];

                _pendingCount = 0;

                // Send to remote peer, labelled with issueTick.
                int bytes = TickCommandPacket.Write(
                    _sendBuf, issueTick, LocalFaction,
                    _localBuf, base_, n);
                _transport.SendCommands(_sendBuf, bytes);
                _localSent[issueMod] = true;
            }

            // ── Step 2: Poll transport ────────────────────────────────────────
            _transport.Poll();

            // ── Step 3: Check if execution tick is ready ──────────────────────
            bool remoteReady = _remoteArrived[execMod] && _remoteTickFor[execMod] == currentTick;
            if (!remoteReady)
            {
                IsStalling = true;
                return false;
            }

            // ── Step 4: Apply both peers' commands, advance sim ───────────────
            int localBase  = execMod * TickCommandPacket.MAX_ORDERS;
            int remoteBase = execMod * TickCommandPacket.MAX_ORDERS;

            var remoteFaction_ = LocalFaction == Faction.Player1 ? Faction.Player2 : Faction.Player1;
            ApplyOrders(_localBuf,  localBase,  _localCount[execMod],  LocalFaction);
            ApplyOrders(_remoteBuf, remoteBase, _remoteCount[execMod], remoteFaction_);

            // ── Record to replay file (if recorder attached) ──────────────────
            if (Recorder != null)
            {
                Recorder.RecordTick(currentTick, LocalFaction,
                    _localBuf, localBase, _localCount[execMod]);
                Recorder.RecordTick(currentTick, remoteFaction_,
                    _remoteBuf, remoteBase, _remoteCount[execMod]);
            }

            // Clear slots for reuse (BUFFER_SIZE ticks later).
            _localSent[execMod]    = false;
            _localCount[execMod]   = 0;
            _remoteArrived[execMod] = false;
            _remoteCount[execMod]  = 0;

            IsStalling = false;
            return true;
        }

        // ── Checksum exchange ─────────────────────────────────────────────────────

        /// <summary>
        /// Send a checksum to the remote peer and check against any pending remote checksum.
        /// Call from SimulationLoop.OnChecksum handler.
        /// </summary>
        public void SendChecksum(uint tick, uint localHash)
        {
            if (!IsOnline) return;

            _pendingLocalChecksum = localHash;
            _localChecksumReady   = true;

            int len = TickCommandPacket.WriteChecksum(_checksumBuf, tick, localHash);
            _transport.SendReliable(_checksumBuf[..len]);
        }

        // ── Incoming packet dispatch ──────────────────────────────────────────────

        private void HandlePacket(byte[] data, int len, int channel)
        {
            if (len < 1) return;
            var type = (PacketType)data[0];

            switch (type)
            {
                case PacketType.TickCommands:
                    if (TickCommandPacket.TryRead(data, len, out uint tick, out Faction cmdFaction,
                                                  _tempBuf, out int count))
                    {
                        int mod   = (int)(tick & BUFFER_MASK);
                        int base_ = mod * TickCommandPacket.MAX_ORDERS;

                        if (IsSpectator)
                        {
                            // Route P1 commands → _localBuf, P2 commands → _remoteBuf.
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
                            // Normal mode: all incoming TickCommands are from the remote peer.
                            _remoteCount[mod] = count;
                            for (int i = 0; i < count; i++) _remoteBuf[base_ + i] = _tempBuf[i];
                            _remoteArrived[mod] = true;
                            _remoteTickFor[mod] = tick;
                        }
                    }
                    break;

                case PacketType.Checksum:
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

                case PacketType.Chat:
                    if (TickCommandPacket.TryReadChat(data, len, out Faction chatFaction, out string chatMsg))
                        OnChatReceived?.Invoke(chatFaction, chatMsg);
                    break;
            }
        }

        /// <summary>
        /// Send a chat message to the remote peer (or server for relay).
        /// Only works when online; silently ignored offline and in spectator mode.
        /// </summary>
        public void SendChat(string message)
        {
            if (!IsOnline || IsSpectator || string.IsNullOrEmpty(message)) return;
            _transport.SendReliable(TickCommandPacket.MakeChat(LocalFaction, message));
        }

        // ── Apply orders to EntityWorld ────────────────────────────────────────────

        /// <param name="expectedFaction">
        /// The faction that issued these orders. Orders targeting units that do NOT
        /// belong to this faction are silently dropped — basic anti-cheat validation.
        /// </param>
        private void ApplyOrders(UnitOrder[] buf, int baseIdx, int count, Faction expectedFaction)
        {
            for (int i = 0; i < count; i++)
            {
                var o  = buf[baseIdx + i];
                int id = o.UnitId;
                if (!_world.IsAlive(id)) continue;

                // Anti-cheat: silently drop orders targeting units the sender doesn't own.
                if (_world.FactionOf[id] != expectedFaction) continue;

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
