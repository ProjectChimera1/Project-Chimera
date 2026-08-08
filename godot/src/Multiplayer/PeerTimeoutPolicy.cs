#nullable enable

namespace ProjectChimera.Multiplayer
{
    /// <summary>
    /// DW-911 — the ENet peer-timeout budget, in one place, for both the client
    /// (<see cref="ENetTransport"/>) and the dedicated server (<see cref="ServerTransport"/>).
    ///
    /// <para><b>Why this exists.</b> ENet's disconnect timer is derived from the measured round-trip time: a
    /// reliable packet's retry window doubles until it passes the limit, and the peer is dropped once reliable
    /// traffic has gone unacknowledged for at least <c>timeoutMinimum</c>. That makes the tolerated stall
    /// PROPORTIONAL TO LATENCY — a peer on a 300 ms link survives a stall of many seconds, while a peer on a
    /// <b>1-2 ms LAN</b> is dropped almost as soon as its main thread blocks. The healthier the link, the less
    /// slack there is. That inversion is the trap, and it is a well-known Godot issue (godotengine/godot#40618,
    /// "ENET server disconnects client due to timeout caused by loading a level", and #20056 which exposed these
    /// settings precisely so games could raise them); it is reported as WORST on debug builds.</para>
    ///
    /// <para><b>What it cost us.</b> The FR-39 two-machine LAN gate (2026-08-08) could not be scored: the laptop
    /// peer connected, agreed the match hash, entered Play and was dropped at tick 4, then tick 9, on a WIRED link
    /// measuring 0% loss at 1-5 ms, with no exception on either side. Every run ended
    /// <c>MATCH SUMMARY: 1 windows compared (0 cross-peer attested, 1 single-reporter) — INCONCLUSIVE</c>, because
    /// the server correctly refuses to attest a one-peer match. The gate was measuring the transport's stall
    /// tolerance, not determinism.</para>
    ///
    /// <para><b>What this does and does not fix.</b> Raising the budget buys a stalling peer time to recover; it
    /// does NOT make the stall acceptable, and it must never be used to hide one — that is what the
    /// <c>[FrameStall]</c> and <c>[NavBake]</c> diagnostics are for. A peer that is genuinely gone is still dropped,
    /// just on a human timescale rather than a sub-second one. Nothing here touches the simulation: ENet timing is
    /// transport-layer, is not folded into <c>SimChecksum</c>, and cannot move a golden — a dropped peer is handled
    /// deterministically by the server's freeze/empty-command path either way.</para>
    /// </summary>
    public static class PeerTimeoutPolicy
    {
        /// <summary>Retry-window doubling limit before the minimum-timeout clause can fire. ENet's default (32) is
        /// already the permissive end of the scale; the problem is the MINIMUM below, not this.</summary>
        public const int TIMEOUT_LIMIT = 32;

        /// <summary>
        /// Milliseconds of unacknowledged reliable traffic tolerated before a peer may be dropped (ENet default:
        /// 5000). Raised to 20 s so a debug-build client can survive a level load, a synchronous navmesh bake, a
        /// shader compile or a first-frame asset upload without being disconnected mid-match. 20 s is chosen to be
        /// comfortably longer than the worst stall the diagnostics have recorded while still being shorter than a
        /// human's patience for a genuinely dead peer.
        /// </summary>
        public const int TIMEOUT_MIN_MS = 20_000;

        /// <summary>Absolute ceiling: a peer is dropped after this long regardless of the retry state (ENet default:
        /// 30000). Raised in proportion with <see cref="TIMEOUT_MIN_MS"/> so the ceiling never fires FIRST and
        /// silently re-imposes the default behaviour this policy exists to widen.</summary>
        public const int TIMEOUT_MAX_MS = 60_000;
    }
}
