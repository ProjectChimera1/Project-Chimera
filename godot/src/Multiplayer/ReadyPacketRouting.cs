#nullable enable
namespace ProjectChimera.Multiplayer
{
    /// <summary>
    /// DW-360 — the outcome of routing one inbound Ready packet: either the peer may be marked ready (with the
    /// P2P slot to mark), or the start is BLOCKED with the status text to surface. Constructed only via
    /// <see cref="Blocked"/>/<see cref="Ready"/>, so "peer marked ready despite a block" is unrepresentable —
    /// a blocked decision never carries a ready flag or a slot.
    /// </summary>
    public readonly struct ReadyRouteDecision
    {
        /// <summary>True iff the gate ALLOWED — the caller may set its peer-ready flag and mark the slot.</summary>
        public bool PeerReady { get; }

        /// <summary>The P2P slot to mark occupied+ready (host view → 1, joiner view → 0); −1 when blocked.</summary>
        public int PeerSlot { get; }

        /// <summary>The human-readable status text to surface on a block; null when the peer readied.</summary>
        public string? BlockReason { get; }

        private ReadyRouteDecision(bool peerReady, int peerSlot, string? blockReason)
        {
            PeerReady   = peerReady;
            PeerSlot    = peerSlot;
            BlockReason = blockReason;
        }

        /// <summary>A blocked start: the peer is NOT ready, no slot is marked, and <paramref name="reason"/> is surfaced.</summary>
        public static ReadyRouteDecision Blocked(string reason) => new ReadyRouteDecision(false, -1, reason);

        /// <summary>An allowed ready: mark <paramref name="peerSlot"/> occupied+ready.</summary>
        public static ReadyRouteDecision Ready(int peerSlot) => new ReadyRouteDecision(true, peerSlot, null);
    }

    /// <summary>
    /// DW-360 — the Godot-free Ready-packet ROUTING extracted from <c>LobbyUi</c>'s <c>PacketType.Ready</c>
    /// handler (the <see cref="HandshakeGate"/>/<c>FactionLaunchGate</c> extraction precedent): parse the widened
    /// Ready payload, gate the peer's protocol version fail-closed, and marshal the parsed flag + hash argument
    /// slots into <see cref="HandshakeGate.CheckStart"/> in the correct order. Only the pure
    /// <c>CheckStart</c> DECISION was unit-tested before; this owns the wiring AROUND it, closing the class of
    /// regression where the handler passes <c>peerHashParsed: true</c> unconditionally, swaps the local/peer hash
    /// argument slots, or marks the peer ready before consulting the gate — each of which would re-open the
    /// fail-open start with every prior test still green.
    ///
    /// Pure function: no logging, no side effects; <c>LobbyUi</c> applies the returned decision (status text,
    /// peer-ready flag, slot marking) and owns all Godot presentation.
    /// </summary>
    public static class ReadyPacketRouting
    {
        /// <summary>
        /// Route one inbound Ready packet:
        ///   • an unparseable/short/wrong-type payload → parsed=false → <see cref="HandshakeGate.CheckStart"/>
        ///     treats the peer hash as 0 and BLOCKS (fail-closed — an unreadable payload is never proof of
        ///     start-state agreement);
        ///   • a PARSED payload whose protocol version ≠ <see cref="TickCommandPacket.PROTOCOL_VERSION"/> →
        ///     BLOCK with the version-mismatch text (checked before the hash gate, so a version skew is reported
        ///     as such);
        ///   • otherwise the local/peer 64-bit match-agreement hashes go through <c>CheckStart</c> — local first,
        ///     peer second (the argument order the block text attributes: "Your match" = local) — any block reason
        ///     is returned verbatim;
        ///   • allowed → the peer readies at slot 1 (host view) or slot 0 (joiner view).
        /// </summary>
        /// <param name="data">The raw inbound packet bytes.</param>
        /// <param name="len">The valid byte count in <paramref name="data"/>.</param>
        /// <param name="localAgreementHash">This client's 64-bit <c>MatchAgreementHash</c> (0 = not computed → blocks).</param>
        /// <param name="localContentBreakdown">The LOCAL per-domain content fingerprint appended to any block
        /// reason as a comparison aid (Story 9.16); null/empty appends nothing.</param>
        /// <param name="isHostRole">True when this client hosts the P2P match (the ready peer is then slot 1).</param>
        public static ReadyRouteDecision Route(byte[] data, int len, ulong localAgreementHash,
            string? localContentBreakdown, bool isHostRole)
        {
            // Story 7.7 / 9.4 — the parsed flag MUST reflect TryReadReady's verdict: false routes into the
            // fail-closed hash-0 block below, so an unreadable payload can never mark the peer ready.
            bool parsed = TickCommandPacket.TryReadReady(data, len, out ushort peerVersion, out ulong peerHash);

            // A PARSED payload with a wrong protocol version blocks with the version text (an unparsed payload
            // reports version 0, which is meaningless — it falls through to the hash gate's not-computed block).
            if (parsed && peerVersion != TickCommandPacket.PROTOCOL_VERSION)
                return ReadyRouteDecision.Blocked(
                    "CANNOT START — peer protocol version mismatch.\n" +
                    $"Peer protocol: v{peerVersion}\n" +
                    $"Your protocol: v{TickCommandPacket.PROTOCOL_VERSION}");

            // Argument slots: LOCAL first, PEER second — CheckStart's block text attributes "Your match" to the
            // first argument. peerHashParsed carries the parse verdict (never a constant).
            string? block = HandshakeGate.CheckStart(localAgreementHash, peerHash, peerHashParsed: parsed,
                localBreakdown: localContentBreakdown);
            if (block != null)
                return ReadyRouteDecision.Blocked(block);

            // P2P: the ready peer occupies slot 1 (from the host's view) or slot 0 (from the joiner's view).
            return ReadyRouteDecision.Ready(isHostRole ? 1 : 0);
        }
    }
}
