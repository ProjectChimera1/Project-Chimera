#nullable enable
using System;

namespace ProjectChimera.Multiplayer.Server
{
    /// <summary>How a match trusts the identities behind its transport slots (Story 15-14 / DW-200).</summary>
    public enum TrustMode : byte
    {
        /// <summary>LAN / offline: slots are trusted as-is — NO login, NO attestation, ever (Alec's ruling:
        /// LAN play must never be gated behind an account). Every identity check passes; the gate is inert.</summary>
        LanTrust = 0,
        /// <summary>Online (Nakama-matched): every PLAYER slot must attest a verified identity before it may
        /// Ready, and a rejoin must re-present the SAME identity the slot held pre-drop. Fail-closed.</summary>
        OnlineAttest = 1,
    }

    /// <summary>
    /// Story 15-14 (DW-200, Alec's 2026-08-12 build-now ruling) — the Godot-free host-side IDENTITY GATE:
    /// per-slot attested identity + the fail-closed predicates the server consults at Ready and at rejoin.
    ///
    /// <para><b>The shape (and why it works on LAN).</b> The 15-14 HALT (2026-08-08) proved the FULL rail —
    /// live server→Nakama token validation — cannot be built-and-verified without hosted infra. This gate is
    /// the enforcement rail built NOW with the VERIFIER as an injected seam: in <see cref="TrustMode.LanTrust"/>
    /// (the default, and the only mode a LAN match ever runs) every check passes and nothing is gated; in
    /// <see cref="TrustMode.OnlineAttest"/> the injected <c>verifier</c> decides whether a presented
    /// (userId, token) credential is genuine — today a shared-secret/JWT or live-Nakama implementation plugs in
    /// there WITHOUT touching this gate, the packet, or the server wiring (the RejoinTokens D-5 precedent:
    /// same seam, stronger mint). A null verifier in OnlineAttest mode REJECTS everything — fail-closed, never
    /// fail-open.</para>
    ///
    /// <para><b>What it protects.</b> (1) READY: an unattested player slot cannot start an online match —
    /// the DW-200 "binary-patched client claims any hero" hole closes at the door. (2) REJOIN: an attested
    /// slot's mid-match reconnect must present the SAME userId that held the slot pre-drop, ON TOP of the
    /// Story 15-1 RejoinToken — so even a stolen token cannot hand the slot to a different account. Slot
    /// identity is TRANSPORT-AUTHORITATIVE throughout (the packet names no slot).</para>
    /// </summary>
    public sealed class IdentityGate
    {
        private readonly TrustMode _mode;
        private readonly Func<string, string, bool>? _verifier; // (userId, token) → credential genuine?
        private readonly string?[] _userId;                     // attested identity per slot (null = none)

        public IdentityGate(TrustMode mode, int maxSlots, Func<string, string, bool>? verifier = null)
        {
            _mode = mode;
            _verifier = verifier;
            _userId = new string?[maxSlots];
        }

        /// <summary>The mode this match runs under (frozen at construction — a match never changes trust).</summary>
        public TrustMode Mode => _mode;

        /// <summary>The attested identity behind <paramref name="slot"/> (null when none / LanTrust).</summary>
        public string? UserIdOf(int slot) => (uint)slot < (uint)_userId.Length ? _userId[slot] : null;

        /// <summary>Recycle discipline: a slot's identity dies with its CONNECTION (called on connect, like the
        /// rate-limiter resets) — a recycled slot never inherits the prior occupant's attestation. The PRE-DROP
        /// identity a rejoin must match is captured separately via <see cref="CaptureForRejoin"/>.</summary>
        public void Reset(int slot)
        {
            if ((uint)slot < (uint)_userId.Length) _userId[slot] = null;
        }

        /// <summary>
        /// Record an attestation for <paramref name="slot"/> (transport-authoritative). In LanTrust the packet
        /// is ignored entirely (LAN asks for no identity and STORES none — nothing to leak or misuse). In
        /// OnlineAttest the credential goes through the injected verifier; only a verified identity is stored.
        /// Returns true when the slot is now attested.
        /// </summary>
        public bool RecordAttestation(int slot, string userId, string token)
        {
            if ((uint)slot >= (uint)_userId.Length) return false;
            if (_mode == TrustMode.LanTrust) return false;
            if (string.IsNullOrEmpty(userId)) return false;
            if (_verifier == null || !_verifier(userId, token)) return false; // no verifier ⇒ fail-closed
            _userId[slot] = userId;
            return true;
        }

        /// <summary>The READY gate: may <paramref name="slot"/> ready into the match? LanTrust: always.
        /// OnlineAttest: only an attested slot (<paramref name="reason"/> says why not, for the server log).</summary>
        public bool MayReady(int slot, out string? reason)
        {
            reason = null;
            if (_mode == TrustMode.LanTrust) return true;
            if (UserIdOf(slot) != null) return true;
            reason = "slot has not attested a verified identity (OnlineAttest mode is fail-closed)";
            return false;
        }

        // ── The rejoin bind (the D-5 "stronger mint" seam, delivered) ─────────

        private readonly string?[] _rejoinBind = new string?[64]; // sized generously; indexed by player slot

        /// <summary>Freeze the CURRENT attested identities as the rejoin-bind set — called at StartGame
        /// (alongside the RejoinTokens mint), so a mid-match reconnect is checked against who held the slot
        /// when the match began, not against whatever the recycled connection later claims.</summary>
        public void CaptureForRejoin(int maxPlayers)
        {
            for (int s = 0; s < maxPlayers && s < _rejoinBind.Length; s++)
                _rejoinBind[s] = (uint)s < (uint)_userId.Length ? _userId[s] : null;
        }

        /// <summary>
        /// The REJOIN identity check, layered on top of the Story 15-1 token: in LanTrust it always passes (the
        /// token alone is the LAN-grade identity — D-5). In OnlineAttest the reconnected connection must have
        /// RE-ATTESTED (post-reconnect — its old attestation died with the old connection) the SAME userId that
        /// held the slot at StartGame; a slot that was never bound (e.g. attestation-less spectator promoted by
        /// a future feature) fails closed.
        /// </summary>
        public bool RejoinIdentityOk(int slot)
        {
            if (_mode == TrustMode.LanTrust) return true;
            string? bound = (uint)slot < (uint)_rejoinBind.Length ? _rejoinBind[slot] : null;
            string? now   = UserIdOf(slot);
            return bound != null && now != null && string.Equals(bound, now, StringComparison.Ordinal);
        }
    }
}
