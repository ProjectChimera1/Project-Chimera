#nullable enable
using System;
using System.Runtime.InteropServices;
using ProjectChimera.Core;
using ProjectChimera.Combat;  // CombatEventQueue / CombatEventType (Story 2.12: OrderDenied on a full-ring reject)
using ProjectChimera.Economy; // BuildingSystem (Story 2.8: Train command applies at exec-tick; 2.12: SetRally)

namespace ProjectChimera.Multiplayer
{
    // ── Packet type discriminator ─────────────────────────────────────────────

    /// <summary>First byte of every packet on the wire.</summary>
    public enum PacketType : byte
    {
        /// <summary>Lobby handshake — host sends to confirm connection.</summary>
        Hello        = 0x01,
        /// <summary>Client signals it has loaded and is ready to start.</summary>
        Ready        = 0x02,
        /// <summary>Host broadcasts: both peers ready, start the sim on this tick.</summary>
        StartGame    = 0x03,
        /// <summary>Per-tick command bundle (UnitCommand orders for this tick).</summary>
        TickCommands = 0x10,
        /// <summary>World-state checksum for desync detection.</summary>
        Checksum     = 0x11,
        /// <summary>
        /// Server → a minority peer: the authoritative collector found this peer diverged from the
        /// strict-majority canonical hash. Wire: type(1) + tick(4 LE) + canonicalHash(4 LE) = 9 bytes.
        /// Server-GENERATED in 1.9a (no longer client-sent / opaquely relayed). See <see cref="TickCommandPacket.MakeDesyncAlert"/>.
        /// </summary>
        DesyncAlert  = 0x12,
        /// <summary>
        /// Server → everyone: no strict-majority canonical hash exists (global desync) — terminal HALT.
        /// Wire: type(1) + tick(4 LE) + reason(1) = 6 bytes. See <see cref="TickCommandPacket.MakeHalt"/>.
        /// </summary>
        Halt         = 0x13,
        /// <summary>
        /// In-game chat message.
        /// Wire format: type(1) + faction(1) + msgLen(2 LE) + msgBytes(UTF-8, max 200).
        /// Relayed by DedicatedServer to all connected peers.
        /// </summary>
        Chat          = 0x20,
        /// <summary>RTT probe sent by either peer. Wire: type(1) + seq(1) + senderMs(4 LE).</summary>
        Ping          = 0x40,
        /// <summary>RTT probe reply. Same wire format as Ping — echoes seq + senderMs back.</summary>
        Pong          = 0x41,
        /// <summary>
        /// Proposal to change the input-delay budget.
        /// Wire: type(1) + proposedDelay(1) + applyAtTick(4 LE).
        /// Both peers must agree before the change takes effect; see LockstepManager.
        /// </summary>
        DelayProposal = 0x42,
    }

    // ── HALT reason ───────────────────────────────────────────────────────────

    /// <summary>
    /// Why the server issued a terminal <see cref="PacketType.Halt"/>. Byte-wide and extensible
    /// (ProtocolMismatch / StartStateDisagreement etc. arrive with the Epic-9 server-authority work).
    /// </summary>
    public enum HaltReason : byte
    {
        /// <summary>No strict-majority canonical hash for the executed tick — global desync.</summary>
        NoMajority = 0,
    }

    // ── Per-unit order (11 bytes) ─────────────────────────────────────────────

    /// <summary>
    /// One unit command issued on a given tick.
    /// Serialised as 11 bytes: unitId(2) + command(1) + targetX(4) + targetZ(4).
    /// Y is not sent — the sim keeps units on the terrain height, determined locally.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct UnitOrder
    {
        /// <summary>Entity ID (fits in ushort — max 4096 entities).</summary>
        public readonly ushort UnitId;
        /// <summary>The command being issued.</summary>
        public readonly UnitCommand Command;
        /// <summary>World target X (Fixed raw int, 0 for Stop/Hold/Idle).</summary>
        public readonly int TargetX;
        /// <summary>World target Z (Fixed raw int, 0 for Stop/Hold/Idle).</summary>
        public readonly int TargetZ;

        public UnitOrder(int unitId, UnitCommand command, Fixed targetX, Fixed targetZ)
        {
            UnitId  = (ushort)unitId;
            Command = command;
            TargetX = targetX.Raw;
            TargetZ = targetZ.Raw;
        }

        public const int SIZE = 11; // bytes on the wire
    }

    // ── OrderApplier (the SINGLE deterministic command→world step) ──────────────

    /// <summary>
    /// Applies one <see cref="UnitOrder"/> to the <see cref="EntityWorld"/> — the deterministic command→world
    /// step shared verbatim by BOTH apply paths (<c>LockstepManager.ApplyOrders</c> live and
    /// <c>ReplayPlayer.ApplyOrders</c> on playback). Story 1.12 extracted this from the two formerly-duplicated
    /// switches so they can NEVER diverge: a command handled in one path but not the other is a guaranteed
    /// replay-vs-live desync (AR-17 / the epic's #1 trap), and that whole class of bug is now structurally
    /// impossible — there is exactly one switch.
    ///
    /// Godot-free (System.* + Core only), so it compiles into the Tier-1 test assembly and the parity is unit-
    /// testable directly (CommandApplyParityTests). The path-request delegates are PRESENTATION hooks (flow-field
    /// smoothing wired by MainScene) — null in the headless/golden/replay paths; the deterministic sim truth is
    /// always the MoveTarget + Moving/Attacking flags and the SoA fields set here.
    /// </summary>
    public static class OrderApplier
    {
        /// <summary>
        /// Apply <paramref name="o"/> to <paramref name="world"/> for the unit it names. No-ops if that unit is
        /// dead or not owned by <paramref name="expectedFaction"/> (the anti-cheat guard both paths share).
        /// </summary>
        public static void Apply(EntityWorld world, in UnitOrder o, Faction expectedFaction,
            Action<int, float, float>? onRequestPath = null,
            Action<int, float, float>? onRequestAttackMove = null,
            Action<int>? onCancelPath = null,
            BuildingSystem? buildings = null,
            CombatEventQueue? events = null)
        {
            // Story 2.12 (Decision #2): mask the wire's queued flag (0x80) off the Command byte FIRST, so every
            // downstream compare + the command→state switch sees only the real 0-13 UnitCommand — never a flagged
            // 0x80|cmd value (which is not a valid enum member and must never reach CommandState or SimChecksum).
            // `queued` then decides append-vs-replace. A rally/train byte is never Shift-issued, so masking is a
            // harmless no-op for them (the flag is off), but we mask before the early-returns for uniform safety.
            var cmd    = (UnitCommand)((byte)o.Command & UnitOrderFlags.CommandMask);
            bool queued = ((byte)o.Command & UnitOrderFlags.Queued) != 0;

            // Story 2.8 (D-1): Train names a BUILDING (UnitId = buildingId), not an entity — handle it BEFORE the
            // entity-ownership guard below, which would misread the building id as an EntityWorld slot (rejecting or
            // corrupting whatever entity shares that index) and clobber its CommandState. The building-ownership
            // guard + the deterministic exec-tick ore/supply spend live in BuildingSystem.TrainUnitCommand.
            // `buildings` is null on headless/golden/replay-without-buildings paths → Train is a deterministic
            // no-op (goldens never train via the wire). TargetX carries the chosen unit index as a RAW int (read
            // directly; NEVER via .ToFloat() — the 1.12/2.4a packed-int lesson).
            if (cmd == UnitCommand.Train)
            {
                buildings?.TrainUnitCommand(o.UnitId, expectedFaction, o.TargetX);
                return;
            }

            // Story 2.12 (AC3): SetRally ALSO names a BUILDING (UnitId = buildingId) — handle it beside Train, BEFORE
            // the entity guard, for the same reason. The rally point rides TargetX/TargetZ as Fixed raw (the Move
            // encoding); BuildingSystem.SetRallyCommand does the bounds + Alive + faction anti-cheat check then writes
            // the store. It NEVER persists as a CommandState. `buildings` null → deterministic no-op (golden/replay).
            if (cmd == UnitCommand.SetRally)
            {
                buildings?.SetRallyCommand(o.UnitId, expectedFaction, Fixed.FromRaw(o.TargetX), Fixed.FromRaw(o.TargetZ));
                return;
            }

            int id = o.UnitId;
            if (!world.IsAlive(id)) return;
            if (world.FactionOf[id] != expectedFaction) return; // anti-cheat: only command your own units

            // Story 2.12 (AC1.2): a Shift-issued (queued) order APPENDS to the entity's order ring and does NOT touch
            // CommandState — OrderQueueSystem pops + dispatches it when the active order completes. A plain (non-flagged)
            // order CLEARS the ring (replace semantics) then applies immediately through the shared core, so a fresh
            // plain command always wipes any stale queued orders (WC3 behaviour).
            if (queued)
            {
                AppendOrder(world, id, cmd, o.TargetX, o.TargetZ, events);
                return;
            }
            world.OrderQueueCount[id] = 0; // plain (replace) — discard any pending queued orders before applying
            ApplyActiveOrder(world, id, cmd, o.TargetX, o.TargetZ, onRequestPath, onRequestAttackMove, onCancelPath);
        }

        /// <summary>
        /// Apply an ACTIVE (non-queued) order to a live, owned entity — the SINGLE command→<c>CommandState</c> mapping
        /// shared by the wire-entry <see cref="Apply"/> (plain/replace path) AND <c>OrderQueueSystem</c> (queue
        /// pop→dispatch). Extracted in Story 2.12 (Decision #4) so a popped queued order and a directly-issued one can
        /// NEVER diverge (the 1.12 one-switch rule — a second dispatch path is the epic's #1 desync trap). Callers
        /// guarantee <paramref name="id"/> is alive and owned; <paramref name="cmd"/> is the MASKED 0-13 command (a
        /// flagged 0x80 byte must never reach here). The pop path calls THIS directly (never the flag-wrapping
        /// <see cref="Apply"/>), so dispatching queued order 1 of N does not re-clear the ring. The path-request
        /// delegates are presentation hooks — null on the pop and all headless/golden/replay paths.
        /// </summary>
        public static void ApplyActiveOrder(EntityWorld world, int id, UnitCommand cmd, int targetX, int targetZ,
            Action<int, float, float>? onRequestPath = null,
            Action<int, float, float>? onRequestAttackMove = null,
            Action<int>? onCancelPath = null)
        {
            UnitCommand prior = world.CommandState[id]; // captured for CastAbility's restore (a cast must not clobber the order)
            world.CommandState[id] = cmd;

            switch (cmd)
            {
                case UnitCommand.Move:
                {
                    var target = new FixedVec3(Fixed.FromRaw(targetX), Fixed.Zero, Fixed.FromRaw(targetZ));
                    world.CommandGoal[id]  = target;
                    world.MoveTarget[id]   = target;
                    world.Flags[id]        = (world.Flags[id] | EntityFlags.Moving) & ~EntityFlags.Attacking;
                    world.AttackTarget[id] = -1;
                    ClearPatrolRoute(world, id);
                    onRequestPath?.Invoke(id, Fixed.FromRaw(targetX).ToFloat(), Fixed.FromRaw(targetZ).ToFloat());
                    break;
                }
                case UnitCommand.AttackMove:
                {
                    var target = new FixedVec3(Fixed.FromRaw(targetX), Fixed.Zero, Fixed.FromRaw(targetZ));
                    world.CommandGoal[id]  = target;
                    world.MoveTarget[id]   = target;
                    world.Flags[id]        = (world.Flags[id] | EntityFlags.Moving) & ~EntityFlags.Attacking;
                    world.AttackTarget[id] = -1;
                    ClearPatrolRoute(world, id);
                    onRequestAttackMove?.Invoke(id, Fixed.FromRaw(targetX).ToFloat(), Fixed.FromRaw(targetZ).ToFloat());
                    break;
                }
                case UnitCommand.Stop:
                case UnitCommand.HoldPosition:
                {
                    world.Flags[id]        = world.Flags[id] & ~(EntityFlags.Moving | EntityFlags.Attacking);
                    world.AttackTarget[id] = -1;
                    ClearPatrolRoute(world, id);
                    onCancelPath?.Invoke(id);
                    break;
                }
                case UnitCommand.AttackTarget:
                {
                    // The forced enemy id rides in TargetX as a RAW int (Fixed.FromRaw at issue time) — read it
                    // back directly, never via .ToFloat() (that path is float and would corrupt the id). Seed the
                    // transient AttackTarget too; CombatSystem.TickAttackTargetCombat drives MoveTarget each tick
                    // (the target moves), so we do NOT request a one-shot path here.
                    world.CommandTarget[id] = targetX;
                    world.AttackTarget[id]  = targetX;
                    world.Flags[id]        &= ~EntityFlags.Attacking;
                    ClearPatrolRoute(world, id);
                    break;
                }
                case UnitCommand.AttackBuilding:
                {
                    // Story 2.9a: the enemy BUILDING id rides in TargetX as a RAW int (Fixed.FromRaw at issue). Blind-store
                    // it in CommandTarget (disambiguated by CommandState==AttackBuilding, exactly like AttackTarget/Follow
                    // share CommandTarget) — CombatSystem.TickAttackBuildingCombat validates it (bounds/Alive/friendly/
                    // Structure-domain) in-tick. Set AttackTarget[id] = -1: a building id must NEVER enter the entity-space
                    // AttackTarget[] array (that would make world.IsAlive/FactionOf misread a building id as an entity).
                    world.CommandTarget[id] = targetX;
                    world.AttackTarget[id]  = -1;
                    world.Flags[id]        &= ~EntityFlags.Attacking;
                    ClearPatrolRoute(world, id);
                    break;
                }
                case UnitCommand.Follow:
                {
                    // Friendly id packed in TargetX as a raw int. CombatSystem.TickFollowCombat drives movement.
                    world.CommandTarget[id] = targetX;
                    world.Flags[id]        &= ~EntityFlags.Attacking;
                    ClearPatrolRoute(world, id);
                    break;
                }
                case UnitCommand.Patrol:
                {
                    // START a fresh route: leg 0 = current position (the return anchor), leg 1 = the clicked
                    // point. PatrolIndex=1 heads toward the clicked point first; dir=+1. (N=2 is the classic
                    // ping-pong.) TargetX/TargetZ are a Fixed.Raw ground point, exactly like Move.
                    int baseIdx = id * EntityWorld.MAX_PATROL_WAYPOINTS;
                    var clicked = new FixedVec3(Fixed.FromRaw(targetX), Fixed.Zero, Fixed.FromRaw(targetZ));
                    world.PatrolWaypoints[baseIdx + 0] = world.Position[id];
                    world.PatrolWaypoints[baseIdx + 1] = clicked;
                    world.PatrolCount[id]  = 2;
                    world.PatrolIndex[id]  = 1;
                    world.PatrolDir[id]    = 1;
                    world.CommandGoal[id]  = clicked;
                    world.MoveTarget[id]   = clicked;
                    world.Flags[id]        = (world.Flags[id] | EntityFlags.Moving) & ~EntityFlags.Attacking;
                    world.AttackTarget[id] = -1;
                    break;
                }
                case UnitCommand.PatrolAppend:
                {
                    // APPEND a waypoint to the far end of an existing route, WITHOUT disturbing the current leg
                    // (PatrolIndex/MoveTarget untouched). If not already patrolling, behave like a fresh Patrol.
                    // Appends past the cap are silently ignored (deterministic no-op). Then force CommandState
                    // back to Patrol (overriding the cmd set above) so CombatSystem never sees PatrolAppend.
                    int baseIdx = id * EntityWorld.MAX_PATROL_WAYPOINTS;
                    var clicked = new FixedVec3(Fixed.FromRaw(targetX), Fixed.Zero, Fixed.FromRaw(targetZ));
                    int n = world.PatrolCount[id];
                    if (n >= 2 && n < EntityWorld.MAX_PATROL_WAYPOINTS)
                    {
                        world.PatrolWaypoints[baseIdx + n] = clicked;
                        world.PatrolCount[id]              = (byte)(n + 1);
                    }
                    else if (n < 2)
                    {
                        // Not already patrolling → fresh route, identical to the Patrol case.
                        world.PatrolWaypoints[baseIdx + 0] = world.Position[id];
                        world.PatrolWaypoints[baseIdx + 1] = clicked;
                        world.PatrolCount[id]  = 2;
                        world.PatrolIndex[id]  = 1;
                        world.PatrolDir[id]    = 1;
                        world.CommandGoal[id]  = clicked;
                        world.MoveTarget[id]   = clicked;
                        world.Flags[id]        = (world.Flags[id] | EntityFlags.Moving) & ~EntityFlags.Attacking;
                        world.AttackTarget[id] = -1;
                    }
                    // else: route is full — silently ignore the extra waypoint.
                    world.CommandState[id] = UnitCommand.Patrol;
                    break;
                }
                case UnitCommand.CastAbility:
                {
                    // Story 2.4a (FR-11): a fire-and-forget cast INTENT. The ability slot rides in TargetX (raw int,
                    // packed at issue time via Fixed.FromRaw(slot)) and the target entity id in TargetZ (raw int,
                    // -1 = Self/None) — read back directly as targetX/targetZ, NEVER via .ToFloat() (that path is
                    // float and would corrupt the packed int — the 1.12 AttackTarget lesson). We write ONLY the
                    // pending-cast SoA; the effect graph runs in AbilityCastSystem INSIDE the tick. Running it here
                    // would advance the shared SimRng / query a stale SpatialHash off-tick (OrderApplier runs at input
                    // time, OUTSIDE the tick on the offline path) → desync vs the online/replay path. An instant cast
                    // must not interrupt the unit's order, so restore CommandState to its prior value (mirrors how
                    // PatrolAppend rewrites CommandState back to Patrol above).
                    world.CommandState[id] = prior;
                    int slot = targetX;
                    if (slot < 0 || slot >= world.AbilityCount[id]) break; // no such slot → deterministic no-op
                    world.PendingCastSlot[id]   = (byte)slot;
                    world.PendingCastTarget[id] = targetZ;
                    break;
                }
            }
        }

        /// <summary>
        /// Append a Shift-queued order to the entity's ring (Story 2.12, AC1.2 / AC4). Rejects DETERMINISTICALLY when
        /// the ring is already full (<see cref="EntityWorld.OrderQueueCount"/> == <see cref="EntityWorld.MAX_ORDER_QUEUE"/>):
        /// no append, no overflow, no crash — state unchanged. On the paths that wire a live <paramref name="events"/>
        /// sink it also pushes a presentation-only <see cref="CombatEventType.OrderDenied"/> at the entity's position
        /// (Story 11.9 consumes it). The reject DECISION reads the FOLDED OrderQueueCount, so every peer rejects
        /// identically and a null sink (replay) rejects the same way — only the visual feedback is skipped. Called ONLY
        /// from the single wire-entry <see cref="Apply"/> (never the pop path), so appending never touches CommandState.
        /// </summary>
        private static void AppendOrder(EntityWorld world, int id, UnitCommand cmd, int targetX, int targetZ,
            CombatEventQueue? events)
        {
            int count = world.OrderQueueCount[id];
            if (count >= EntityWorld.MAX_ORDER_QUEUE)
            {
                events?.Push(CombatEventType.OrderDenied, world.Position[id]);
                return; // ring full — deterministic reject (mirrors PatrolAppend's "route full → ignore", plus the event)
            }
            int slot = id * EntityWorld.MAX_ORDER_QUEUE + count;
            world.OrderQueueCmd[slot]     = (byte)cmd; // the MASKED 0-13 command (the 0x80 wire flag is already stripped)
            world.OrderQueueTargetX[slot] = targetX;
            world.OrderQueueTargetZ[slot] = targetZ;
            world.OrderQueueCount[id]     = (byte)(count + 1);
        }

        /// <summary>
        /// Reset the patrol-route ring to "no route" (Review, Story 1.12). Called whenever a unit is given a
        /// NON-patrol command, so a later <see cref="UnitCommand.PatrolAppend"/> (whose "already patrolling?"
        /// gate keys on <c>PatrolCount</c>) cannot resurrect an abandoned route, and the v4 checksum stops
        /// folding a dead route the unit no longer walks. Patrol/PatrolAppend own this state and do NOT call it.
        /// </summary>
        private static void ClearPatrolRoute(EntityWorld world, int id)
        {
            world.PatrolCount[id] = 0;
            world.PatrolIndex[id] = 0;
            world.PatrolDir[id]   = 1;
        }
    }

    // ── TickCommandPacket ──────────────────────────────────────────────────────

    /// <summary>
    /// All orders a player issues on a single tick.
    /// Wire format: PacketType(1) + tick(4) + faction(1) + orderCount(1) + orders(11 each).
    /// Max 32 orders per tick (352 bytes total worst-case) — well under 1 MTU.
    /// </summary>
    public static class TickCommandPacket
    {
        public const int MAX_ORDERS   = 32;
        public const int HEADER_BYTES = 7;  // type(1) + tick(4) + faction(1) + count(1)

        /// <summary>Serialise a tick command packet into <paramref name="buf"/>.</summary>
        /// <returns>Number of bytes written.</returns>
        public static int Write(byte[] buf, uint tick, Faction faction, UnitOrder[] orders, int orderCount)
            => Write(buf, tick, faction, orders, 0, orderCount);

        /// <summary>
        /// Serialise a tick command packet from a flat buffer slice starting at <paramref name="baseIdx"/>.
        /// </summary>
        /// <returns>Number of bytes written.</returns>
        public static int Write(byte[] buf, uint tick, Faction faction,
                                UnitOrder[] orders, int baseIdx, int orderCount)
        {
            if (orderCount > MAX_ORDERS) orderCount = MAX_ORDERS;
            int totalBytes = HEADER_BYTES + orderCount * UnitOrder.SIZE;
            if (buf.Length < totalBytes)
                throw new ArgumentException($"Buffer too small: need {totalBytes}, got {buf.Length}");

            int pos = 0;
            buf[pos++] = (byte)PacketType.TickCommands;

            // tick (4 bytes, little-endian)
            buf[pos++] = (byte)(tick);
            buf[pos++] = (byte)(tick >> 8);
            buf[pos++] = (byte)(tick >> 16);
            buf[pos++] = (byte)(tick >> 24);

            buf[pos++] = (byte)faction;
            buf[pos++] = (byte)orderCount;

            for (int i = 0; i < orderCount; i++)
            {
                var o = orders[baseIdx + i];
                buf[pos++] = (byte)(o.UnitId);
                buf[pos++] = (byte)(o.UnitId >> 8);
                buf[pos++] = (byte)o.Command;
                WriteInt(buf, ref pos, o.TargetX);
                WriteInt(buf, ref pos, o.TargetZ);
            }

            return totalBytes;
        }

        /// <summary>
        /// Parse a TickCommands packet. Returns false if the buffer is malformed.
        /// </summary>
        public static bool TryRead(byte[] buf, int len, out uint tick, out Faction faction,
                                   UnitOrder[] outOrders, out int orderCount)
        {
            tick = 0; faction = Faction.Neutral; orderCount = 0;
            if (len < HEADER_BYTES) return false;
            if ((PacketType)buf[0] != PacketType.TickCommands) return false;

            int pos = 1;
            tick = ReadUint(buf, ref pos);
            faction = (Faction)buf[pos++];
            orderCount = buf[pos++];

            if (orderCount > MAX_ORDERS) return false;
            if (len < HEADER_BYTES + orderCount * UnitOrder.SIZE) return false;

            for (int i = 0; i < orderCount; i++)
            {
                ushort unitId = (ushort)(buf[pos] | (buf[pos + 1] << 8)); pos += 2;
                var command = (UnitCommand)buf[pos++];
                int tx = ReadInt(buf, ref pos);
                int tz = ReadInt(buf, ref pos);
                outOrders[i] = new UnitOrder(unitId, command, Fixed.FromRaw(tx), Fixed.FromRaw(tz));
            }

            return true;
        }

        // ── ChecksumPacket helpers ─────────────────────────────────────────────

        /// <summary>Serialise a checksum packet (9 bytes).</summary>
        public static int WriteChecksum(byte[] buf, uint tick, uint checksum)
        {
            int pos = 0;
            buf[pos++] = (byte)PacketType.Checksum;
            WriteUint(buf, ref pos, tick);
            WriteUint(buf, ref pos, checksum);
            return pos; // 9
        }

        /// <summary>Parse a checksum packet.</summary>
        public static bool TryReadChecksum(byte[] buf, int len, out uint tick, out uint checksum)
        {
            tick = 0; checksum = 0;
            if (len < 9) return false;
            if ((PacketType)buf[0] != PacketType.Checksum) return false;
            int pos = 1;
            tick = ReadUint(buf, ref pos);
            checksum = ReadUint(buf, ref pos);
            return true;
        }

        // ── DesyncAlert / Halt helpers (server-generated, Story 1.9a) ──────────

        /// <summary>
        /// Server → a minority peer. Wire: type(1) + tick(4 LE) + canonicalHash(4 LE) = 9 bytes
        /// (mirrors <see cref="WriteChecksum"/>). 32-bit width — the wire is never widened to ulong (D12).
        /// </summary>
        public static byte[] MakeDesyncAlert(uint tick, uint canonicalHash)
        {
            var b = new byte[9];
            int p = 0;
            b[p++] = (byte)PacketType.DesyncAlert;
            WriteUint(b, ref p, tick);
            WriteUint(b, ref p, canonicalHash);
            return b;
        }

        /// <summary>Parse a DesyncAlert packet. Returns false if malformed/truncated.</summary>
        public static bool TryReadDesyncAlert(byte[] buf, int len, out uint tick, out uint canonicalHash)
        {
            tick = 0; canonicalHash = 0;
            if (len < 9) return false;
            if ((PacketType)buf[0] != PacketType.DesyncAlert) return false;
            int pos = 1;
            tick = ReadUint(buf, ref pos);
            canonicalHash = ReadUint(buf, ref pos);
            return true;
        }

        /// <summary>
        /// Server → everyone, on no strict-majority canonical hash. Wire: type(1) + tick(4 LE) + reason(1) = 6 bytes.
        /// Terminal HALT (recovery/rejoin is deferred post-1.0).
        /// </summary>
        public static byte[] MakeHalt(uint tick, HaltReason reason)
        {
            var b = new byte[6];
            int p = 0;
            b[p++] = (byte)PacketType.Halt;
            WriteUint(b, ref p, tick);
            b[p] = (byte)reason;
            return b;
        }

        /// <summary>Parse a Halt packet. Returns false if malformed/truncated.</summary>
        public static bool TryReadHalt(byte[] buf, int len, out uint tick, out HaltReason reason)
        {
            tick = 0; reason = default;
            if (len < 6) return false;
            if ((PacketType)buf[0] != PacketType.Halt) return false;
            int pos = 1;
            tick = ReadUint(buf, ref pos);
            reason = (HaltReason)buf[pos];
            return true;
        }

        // ── Lobby packet helpers ───────────────────────────────────────────────

        /// <summary>4-byte Hello packet: type(1) + protocolVersion(2) + faction(1).</summary>
        public const ushort PROTOCOL_VERSION = 1;

        /// <summary>
        /// Create a Hello packet. The server sends one to each connecting client,
        /// embedding the faction assigned to that client.
        /// P2P mode: both sides use Faction.Neutral (faction determined by host/client role).
        /// </summary>
        public static byte[] MakeHello(Core.Faction faction = Core.Faction.Neutral) => new byte[] {
            (byte)PacketType.Hello,
            (byte)PROTOCOL_VERSION,
            (byte)(PROTOCOL_VERSION >> 8),
            (byte)faction
        };

        /// <summary>Parse a Hello packet. Returns Faction.Neutral if the packet has no faction byte.</summary>
        public static bool TryReadHello(byte[] buf, int len, out Core.Faction faction)
        {
            faction = Core.Faction.Neutral;
            if (len < 3) return false;
            if ((PacketType)buf[0] != PacketType.Hello) return false;
            if (len >= 4) faction = (Core.Faction)buf[3];
            return true;
        }

        /// <summary>
        /// Ready packet: type(1) + scenarioHash(4).
        /// scenarioHash = ScenarioSerializer.ComputeFileHash(scenarioPath).
        /// The host compares the peer's hash with its own; a mismatch means different
        /// scenario files and would cause guaranteed desync — the match should not start.
        /// </summary>
        public static byte[] MakeReady(uint scenarioHash = 0)
        {
            var buf = new byte[5];
            buf[0] = (byte)PacketType.Ready;
            int pos = 1;
            WriteUint(buf, ref pos, scenarioHash);
            return buf;
        }

        /// <summary>Parse a Ready packet, extracting the scenario hash (0 if not present).</summary>
        public static bool TryReadReady(byte[] buf, int len, out uint scenarioHash)
        {
            scenarioHash = 0;
            if (len < 1) return false;
            if ((PacketType)buf[0] != PacketType.Ready) return false;
            if (len >= 5)
            {
                int pos = 1;
                scenarioHash = ReadUint(buf, ref pos);
            }
            return true;
        }

        /// <summary>StartGame packet: type(1) + startTick(4). Host sends this to both peers.</summary>
        public static byte[] MakeStartGame(uint startTick)
        {
            var buf = new byte[5];
            buf[0] = (byte)PacketType.StartGame;
            int pos = 1;
            WriteUint(buf, ref pos, startTick);
            return buf;
        }

        // ── Chat packet helpers ────────────────────────────────────────────────

        /// <summary>
        /// Maximum chat message length in bytes (UTF-8 encoded).
        /// ~200 ASCII characters; shorter for multi-byte Unicode.
        /// </summary>
        public const int MAX_CHAT_BYTES = 200;

        /// <summary>
        /// Serialise a chat message.
        /// Wire: type(1) + faction(1) + msgLen(2 LE) + msgBytes.
        /// </summary>
        public static byte[] MakeChat(Core.Faction faction, string message)
        {
            byte[] msgBytes = System.Text.Encoding.UTF8.GetBytes(message);
            if (msgBytes.Length > MAX_CHAT_BYTES)
                System.Array.Resize(ref msgBytes, MAX_CHAT_BYTES);

            int total = 4 + msgBytes.Length;
            var buf = new byte[total];
            buf[0] = (byte)PacketType.Chat;
            buf[1] = (byte)faction;
            buf[2] = (byte)msgBytes.Length;
            buf[3] = (byte)(msgBytes.Length >> 8);
            msgBytes.CopyTo(buf, 4);
            return buf;
        }

        /// <summary>Parse a Chat packet. Returns false if malformed.</summary>
        public static bool TryReadChat(byte[] buf, int len,
                                       out Core.Faction faction, out string message)
        {
            faction = Core.Faction.Neutral; message = "";
            if (len < 4) return false;
            if ((PacketType)buf[0] != PacketType.Chat) return false;
            faction = (Core.Faction)buf[1];
            int msgLen = buf[2] | (buf[3] << 8);
            if (msgLen < 0 || msgLen > MAX_CHAT_BYTES) return false;
            if (len < 4 + msgLen) return false;
            message = System.Text.Encoding.UTF8.GetString(buf, 4, msgLen);
            return true;
        }

        // ── RTT probe helpers ─────────────────────────────────────────────────

        /// <summary>Serialise a Ping packet (6 bytes). seq wraps at 255.</summary>
        public static byte[] MakePing(byte seq, uint senderMs)
        {
            var buf = new byte[6];
            buf[0] = (byte)PacketType.Ping;
            buf[1] = seq;
            int pos = 2;
            WriteUint(buf, ref pos, senderMs);
            return buf;
        }

        /// <summary>Serialise a Pong reply by echoing the Ping's seq and timestamp.</summary>
        public static byte[] MakePong(byte seq, uint senderMs)
        {
            var buf = new byte[6];
            buf[0] = (byte)PacketType.Pong;
            buf[1] = seq;
            int pos = 2;
            WriteUint(buf, ref pos, senderMs);
            return buf;
        }

        /// <summary>Parse a Pong packet. Returns false if malformed.</summary>
        public static bool TryReadPong(byte[] buf, int len, out byte seq, out uint senderMs)
        {
            seq = 0; senderMs = 0;
            if (len < 6) return false;
            if ((PacketType)buf[0] != PacketType.Pong) return false;
            seq = buf[1];
            int pos = 2;
            senderMs = ReadUint(buf, ref pos);
            return true;
        }

        // ── Delay-proposal helpers ────────────────────────────────────────────

        /// <summary>Serialise a DelayProposal packet (6 bytes).</summary>
        public static byte[] MakeDelayProposal(byte proposedDelay, uint applyAtTick)
        {
            var buf = new byte[6];
            buf[0] = (byte)PacketType.DelayProposal;
            buf[1] = proposedDelay;
            int pos = 2;
            WriteUint(buf, ref pos, applyAtTick);
            return buf;
        }

        /// <summary>Parse a DelayProposal packet. Returns false if malformed.</summary>
        public static bool TryReadDelayProposal(byte[] buf, int len,
                                                out byte proposedDelay, out uint applyAtTick)
        {
            proposedDelay = 0; applyAtTick = 0;
            if (len < 6) return false;
            if ((PacketType)buf[0] != PacketType.DelayProposal) return false;
            proposedDelay = buf[1];
            int pos = 2;
            applyAtTick = ReadUint(buf, ref pos);
            return true;
        }

        // ── Little-endian helpers ──────────────────────────────────────────────

        private static void WriteInt(byte[] buf, ref int pos, int v)
        {
            buf[pos++] = (byte)v;
            buf[pos++] = (byte)(v >> 8);
            buf[pos++] = (byte)(v >> 16);
            buf[pos++] = (byte)(v >> 24);
        }

        private static void WriteUint(byte[] buf, ref int pos, uint v)
        {
            buf[pos++] = (byte)v;
            buf[pos++] = (byte)(v >> 8);
            buf[pos++] = (byte)(v >> 16);
            buf[pos++] = (byte)(v >> 24);
        }

        private static int ReadInt(byte[] buf, ref int pos)
        {
            int v = buf[pos] | (buf[pos + 1] << 8) | (buf[pos + 2] << 16) | (buf[pos + 3] << 24);
            pos += 4;
            return v;
        }

        private static uint ReadUint(byte[] buf, ref int pos)
        {
            uint v = (uint)(buf[pos] | (buf[pos + 1] << 8) | (buf[pos + 2] << 16) | (buf[pos + 3] << 24));
            pos += 4;
            return v;
        }
    }
}
