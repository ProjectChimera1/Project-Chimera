#nullable enable
using System;
using ProjectChimera.Core; // Faction

namespace ProjectChimera.Multiplayer.Server
{
    /// <summary>DW-394 — what <see cref="ServerPacketRelay.FanInTickCommands"/> did with one inbound packet, so
    /// the composition (not just each primitive) is Tier-1 assertable.</summary>
    public enum CommandFanInResult
    {
        /// <summary>No builder yet (the match has not started) — nothing was touched.</summary>
        NoBuilder,
        /// <summary>A merged-shaped packet from a client — hard-rejected BEFORE the builder (the dispatch-level
        /// guard; <see cref="MergedTickBuilder.Submit"/> also rejects it as defense-in-depth).</summary>
        RejectedMergedFromClient,
        /// <summary>The builder dropped the submission (malformed / spoof / stale / duplicate / bad slot).</summary>
        Dropped,
        /// <summary>Accepted and buffered; the tick's fan-in is still incomplete — nothing broadcast yet.</summary>
        Buffered,
        /// <summary>Accepted, the tick completed, and the ONE merged packet was broadcast.</summary>
        MergedBroadcast,
    }

    /// <summary>
    /// DW-394 — the Godot-free <c>DedicatedServer</c> delegation seams (the builder/applier/lobby-policy
    /// extraction pattern of Story 9.3), so the FAN-IN COMPOSITION — slot/len into
    /// <see cref="MergedTickBuilder.Submit"/>, the frontier advance, <see cref="MergedTickBuilder.TryBuild"/>,
    /// then the broadcast — and the Chat re-stamp pipeline (decode →
    /// <see cref="ServerLobbyPolicy.StampChatFaction"/> → <c>MakeChat</c> re-encode) are exercised by Tier-1
    /// tests, not just their underlying unit primitives. An adapter-level transposition (wrong slot/len, omitted
    /// broadcast, dropped chat re-encode letting a spoofed faction byte through) now fails a test instead of
    /// shipping green. The Godot node stays a thin transport adapter around these.
    /// </summary>
    public static class ServerPacketRelay
    {
        /// <summary>
        /// Story 9.3/9.4 — fan a player's single-faction TickCommands into the authoritative
        /// <paramref name="builder"/>: hard-reject a client-sent merged-shaped packet before the builder, submit
        /// (slot, data, len) verbatim, advance the <paramref name="latestSeenTick"/> frontier MONOTONICALLY (never
        /// backwards — a buffered older tick must not regress the delay authority's marker), and, when the last
        /// expected player's submission completes the tick, broadcast the ONE merged packet via
        /// <paramref name="broadcastCommands"/>. All determinism-critical merge work stays inside the builder;
        /// this seam owns only the composition the Godot node used to inline.
        /// </summary>
        /// <param name="builder">The match's authoritative fan-in; null before the match starts (no-op).</param>
        /// <param name="fromSlot">The transport-authoritative sender slot (never a packet byte).</param>
        /// <param name="data">The raw inbound packet bytes.</param>
        /// <param name="len">The valid byte count in <paramref name="data"/>.</param>
        /// <param name="latestSeenTick">The submission frontier — the highest tick fanned in so far.</param>
        /// <param name="broadcastCommands">Sink for a completed merged packet: (buffer, length). The buffer is the
        /// builder's reusable scratch — consume or copy synchronously.</param>
        public static CommandFanInResult FanInTickCommands(MergedTickBuilder? builder, int fromSlot,
            byte[] data, int len, ref uint latestSeenTick, Action<byte[], int> broadcastCommands)
        {
            if (builder == null) return CommandFanInResult.NoBuilder; // not yet InGame

            // Story 9.3: the merged tick is server-authored (server → client ONLY). A client that sends one is
            // spoofing the authoritative stream — hard-reject at dispatch, before the builder ever sees it.
            if (len >= 1 && (PacketType)data[0] == PacketType.TickCommandsMerged)
                return CommandFanInResult.RejectedMergedFromClient;

            if (!builder.Submit(fromSlot, data, len, out uint tick))
                return CommandFanInResult.Dropped;

            // Story 9.4: track the frontier so a dictated delay directive's applyAtTick lands safely ahead of it.
            if (tick > latestSeenTick) latestSeenTick = tick;

            if (builder.TryBuild(tick, out byte[] merged, out int mergedLen))
            {
                broadcastCommands(merged, mergedLen);
                return CommandFanInResult.MergedBroadcast;
            }
            return CommandFanInResult.Buffered;
        }

        /// <summary>
        /// Story 9.3 (chat-spoof fix) — the Chat re-stamp pipeline: decode the inbound packet, replace the
        /// client's SPOOFABLE faction byte with the sender's transport-authoritative slot faction via
        /// <see cref="ServerLobbyPolicy.StampChatFaction"/> (a spectator/out-of-range sender →
        /// <see cref="Faction.Neutral"/>), and re-encode via <c>MakeChat</c> so no client-supplied faction byte
        /// survives to the rebroadcast. Returns the re-encoded packet, or null when the inbound bytes are
        /// undecodable (the caller logs and drops — a malformed chat is observable, never silently relayed raw).
        /// </summary>
        /// <param name="senderSlot">The transport-authoritative sender slot (never a packet byte).</param>
        /// <param name="data">The raw inbound packet bytes.</param>
        /// <param name="len">The valid byte count in <paramref name="data"/>.</param>
        /// <param name="slotFaction">Slot → authoritative faction table.</param>
        /// <param name="maxPlayers">Player-slot bound; slots at/above stamp <see cref="Faction.Neutral"/>.</param>
        public static byte[]? RestampChat(int senderSlot, byte[] data, int len,
            Faction[] slotFaction, int maxPlayers)
        {
            if (!TickCommandPacket.TryReadChat(data, len, out _, out string message))
                return null;
            Faction stamped = ServerLobbyPolicy.StampChatFaction(senderSlot, slotFaction, maxPlayers);
            return TickCommandPacket.MakeChat(stamped, message);
        }
    }
}
