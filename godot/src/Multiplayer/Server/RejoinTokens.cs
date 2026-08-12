#nullable enable
using System.Security.Cryptography;

namespace ProjectChimera.Multiplayer.Server
{
    /// <summary>
    /// Story 15-1 (D-5) — the per-match REJOIN IDENTITY tokens: at StartGame the server mints one random
    /// 64-bit token per player slot and sends it to that slot's client; a mid-match reconnect presents its
    /// token, and matching proves "the same person who held this slot" with ZERO external dependencies —
    /// LAN-safe by construction. Story 15-14 (DW-200) upgrades the MINT (binding it to a Nakama account for
    /// public matches) without changing this verify seam.
    ///
    /// <para><b>Threat model honesty.</b> This is session identity, not authentication: it stops slot
    /// hijacking by OTHER lobby members / port-scanners (the token never travels except at StartGame to its
    /// owner and back at rejoin), not a man-in-the-middle on an untrusted network — that is 15-14's tier.
    /// Tokens are per-match, in-memory, dead with the process (reconnect across a server restart is out of
    /// scope, spec 15-1).</para>
    ///
    /// <para>Server-side bookkeeping only — NEVER sim/checksum input, so the OS CSPRNG is fine here (the
    /// determinism rules bind the sim, not the relay).</para>
    /// </summary>
    public sealed class RejoinTokens
    {
        private readonly ulong[] _tokens;
        private readonly bool[] _issued;

        public RejoinTokens(int maxSlots)
        {
            _tokens = new ulong[maxSlots];
            _issued = new bool[maxSlots];
        }

        /// <summary>Mint (or re-mint) the token for <paramref name="slot"/>. Called per player slot at
        /// StartGame. Re-minting invalidates any prior token for the slot (a fresh match = fresh identity).</summary>
        public ulong Mint(int slot)
        {
            System.Span<byte> b = stackalloc byte[8];
            RandomNumberGenerator.Fill(b);
            ulong t = System.BitConverter.ToUInt64(b);
            if (t == 0) t = 1; // 0 is the "never issued" sentinel on the wire — never mint it
            _tokens[slot] = t;
            _issued[slot] = true;
            return t;
        }

        /// <summary>True iff <paramref name="presented"/> is EXACTLY the live token minted for
        /// <paramref name="slot"/>. False for an un-minted slot, an out-of-range slot, or any mismatch —
        /// fail-closed, constant-shape (no early-out on partial state that could leak issuance).</summary>
        public bool Verify(int slot, ulong presented)
        {
            if ((uint)slot >= (uint)_tokens.Length) return false;
            return _issued[slot] && presented != 0 && _tokens[slot] == presented;
        }

        /// <summary>Invalidate every token (match over / server reset).</summary>
        public void Clear()
        {
            System.Array.Clear(_tokens);
            System.Array.Clear(_issued);
        }
    }
}
