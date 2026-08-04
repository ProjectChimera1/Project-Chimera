#nullable enable
namespace ProjectChimera.Multiplayer
{
    /// <summary>
    /// DW-402 / DW-403 — the CLIENT-side <see cref="TickCommandPacket.PROTOCOL_VERSION"/> gate + mismatch-recovery
    /// state machine, extracted from <c>LobbyUi.HandlePacket</c>'s inbound-Hello and peer-Ready version clauses
    /// (Story 9.4, the client half of the D3.8 closure) so the decision is Tier-1 unit-testable Godot-free — the
    /// <c>DelayMath.ResolveDirectiveReceipt</c> / <see cref="HandshakeGate"/> extraction precedent (<c>LobbyUi</c>
    /// itself is Godot-coupled and not instantiable in Tier-1; the accepted boundary is that the CALL sites stay
    /// uncovered, the DECISION does not).
    ///
    /// DW-402 — the server-side twin (<c>ServerLobbyPolicy.CheckStartStateAgreement</c>) has been unit-tested since
    /// 9.4, but the client clauses had NO coverage: deleting either one reopened the D3.8 gap (a v1 client
    /// proceeding against a v2 peer) with a green suite. Both clauses now live HERE, where
    /// <c>LobbyVersionGateTests</c> pins them.
    ///
    /// DW-403 — a client-side version mismatch now LATCHES (<see cref="Blocked"/>) instead of the block living only
    /// in a hidden Ready button: while blocked, inbound peer Ready packets are refused fail-closed (a Ready — even
    /// one carrying a matching version — cannot out-run the version turmoil), and a SUBSEQUENTLY VALID Hello on the
    /// SAME connection RECOVERS the lobby: <see cref="HelloVerdict.Recovered"/> tells the caller to restore
    /// ready-ability and restart the ready handshake from a clean slate (stale pre-block ready/confirmation state
    /// must not survive into the recovered lobby). <see cref="Reset"/> on disconnect/cancel/close so a NEW
    /// connection never inherits a stale block.
    ///
    /// One bool of state + pure decisions; no logging, no side effects — <c>LobbyUi</c> surfaces the returned
    /// reasons as its status text.
    /// </summary>
    public sealed class LobbyVersionGate
    {
        /// <summary>The local build's protocol version every inbound version is compared against
        /// (<see cref="TickCommandPacket.PROTOCOL_VERSION"/> in production; injectable for tests).</summary>
        private readonly ushort _localVersion;

        /// <summary>True while the lobby is version-blocked: a mismatched (or unreadable) Hello, or a mismatched
        /// peer Ready, latched it. Cleared ONLY by a subsequently valid Hello (<see cref="EvaluateHello"/> →
        /// <see cref="HelloVerdict.Recovered"/>) or by <see cref="Reset"/> (disconnect/cancel/close).</summary>
        public bool Blocked { get; private set; }

        public LobbyVersionGate(ushort localVersion) => _localVersion = localVersion;

        /// <summary>The <see cref="EvaluateHello"/> decision. Exactly one of the block/allow shapes:
        /// <see cref="Allowed"/> false → surface <see cref="BlockReason"/> and hide the Ready button;
        /// <see cref="Allowed"/> true + <see cref="Recovered"/> true → the DW-403 recovery: restore ready-ability
        /// AND reset the stale ready handshake before proceeding; <see cref="Allowed"/> true + <see cref="Recovered"/>
        /// false → the normal first-Hello path, proceed unchanged.</summary>
        public readonly struct HelloVerdict
        {
            /// <summary>True when the Hello parsed and carried the local protocol version — proceed with the
            /// faction/slot handling and show the Ready button.</summary>
            public readonly bool Allowed;

            /// <summary>DW-403 — true when this VALID Hello arrived while <see cref="Blocked"/> was latched: the
            /// caller must restart the ready handshake from a clean slate (clear local/peer ready confirmation and
            /// the ready model's ready flags) so no pre-block state leaks into the recovered lobby.</summary>
            public readonly bool Recovered;

            /// <summary>The human-readable block reason to surface as lobby status when <see cref="Allowed"/> is
            /// false; null on allow.</summary>
            public readonly string? BlockReason;

            public HelloVerdict(bool allowed, bool recovered, string? blockReason)
            {
                Allowed     = allowed;
                Recovered   = recovered;
                BlockReason = blockReason;
            }
        }

        /// <summary>
        /// The inbound-Hello version gate (DW-402's first clause), fail-closed: an unreadable Hello
        /// (<paramref name="parsed"/> false — its version reads back 0) or a version differing from the local build
        /// BLOCKS and latches <see cref="Blocked"/>. A valid Hello allows — and if the gate was latched, reports
        /// <see cref="HelloVerdict.Recovered"/> (DW-403: a subsequently valid Hello on the same connection restores
        /// the lobby to a ready-able state) and clears the latch.
        /// </summary>
        public HelloVerdict EvaluateHello(bool parsed, ushort helloVersion)
        {
            if (!parsed || helloVersion != _localVersion)
            {
                Blocked = true;
                return new HelloVerdict(allowed: false, recovered: false, blockReason:
                    "CANNOT START — protocol version mismatch.\n" +
                    $"Server protocol: v{helloVersion}\n" +
                    $"Your protocol:  v{_localVersion}\n" +
                    "Both players must run the same game build.");
            }

            bool recovered = Blocked;   // DW-403: valid Hello while latched = the recovery signal
            Blocked = false;
            return new HelloVerdict(allowed: true, recovered: recovered, blockReason: null);
        }

        /// <summary>
        /// The peer-Ready version gate (DW-402's second clause). Returns null to PROCEED to the
        /// <see cref="HandshakeGate"/> hash check, or the block reason to surface:
        ///   • a parsed Ready with a differing version → block AND latch <see cref="Blocked"/> (same recovery rule:
        ///     only a subsequently valid Hello — or <see cref="Reset"/> on disconnect — clears it);
        ///   • ANY Ready while <see cref="Blocked"/> is latched → refused fail-closed, even with a matching version
        ///     (DW-403's latch: a Ready cannot out-run a version-blocked lobby);
        ///   • an UNPARSEABLE Ready (<paramref name="parsed"/> false, unblocked) → null: it proceeds to the hash
        ///     gate, which treats the unparsed hash as 0 and blocks fail-closed there
        ///     (<see cref="HandshakeGate.CheckStart"/> with <c>peerHashParsed: false</c> — the pre-extraction flow,
        ///     preserved);
        ///   • a parsed, version-matching Ready (unblocked) → null: proceed to the hash gate.
        /// </summary>
        public string? CheckPeerReady(bool parsed, ushort peerVersion)
        {
            if (parsed && peerVersion != _localVersion)
            {
                Blocked = true;
                return "CANNOT START — peer protocol version mismatch.\n" +
                       $"Peer protocol: v{peerVersion}\n" +
                       $"Your protocol: v{_localVersion}";
            }

            if (Blocked)
                return "CANNOT START — protocol version mismatch.\n" +
                       "Ready ignored — the lobby is version-blocked until a valid Hello arrives (or you reconnect).";

            return null; // proceed to the HandshakeGate hash check (which is fail-closed on an unparsed payload)
        }

        /// <summary>Clear the latched block. Called on disconnect / cancel / lobby close — a new connection (or a
        /// reopened lobby) must never inherit a stale version block. A reset is NOT a recovery: the next valid
        /// Hello after a reset reports <see cref="HelloVerdict.Recovered"/> false.</summary>
        public void Reset() => Blocked = false;
    }
}
