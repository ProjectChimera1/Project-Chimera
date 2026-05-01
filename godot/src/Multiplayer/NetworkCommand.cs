using System;
using System.Runtime.InteropServices;
using ProjectChimera.Core;

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
        /// <summary>Desync detected — alert the other peer.</summary>
        DesyncAlert  = 0x12,
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
