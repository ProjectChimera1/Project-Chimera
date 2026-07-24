#nullable enable
using ProjectChimera.Core; // FactionRegistry, HeroStore

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// Story 9.4 — the SINGLE 64-bit start-state-agreement value carried on the widened Ready packet
    /// (<c>TickCommandPacket.MakeReady</c>) and gated fail-closed before tick 0: server-attested (the server
    /// compares every slot's value and HALTs on disagreement) and, on the P2P path, via the widened
    /// <c>HandshakeGate</c>. A canonical FNV-64 folding, in order:
    ///   1. <see cref="AlgoVersion"/> (namespaces the hash; a bump moves the value alone),
    ///   2. <see cref="RulesetHash.Compute"/> (the Effect-Graph structural-cap fingerprint),
    ///   3. the initial input delay (LockstepManager.INPUT_DELAY, passed in so this type stays Godot-free),
    ///   4. the active player count N (faction-count) — derived from the applied model's player slots,
    ///   5. each roster faction ordinal for slots 0..N-1 (via <see cref="FactionRegistry.ToFaction"/>),
    ///   6. <see cref="StartStateHash.Compute"/> (content seed + hero/item start-state — a strict superset of the
    ///      old 32-bit scenario hash, so it ALSO catches hero/item-loadout mismatch).
    /// Never returns 0 (sentinel) — a 0 on the wire is a hard reject.
    ///
    /// It USES <see cref="StartStateHash"/>'s value and does NOT modify it (so <c>hero-start-state.golden.txt</c>
    /// and <see cref="StartStateHash.AlgoVersion"/> stay put). The N + roster are derived DETERMINISTICALLY from
    /// the applied model (<c>CanonicalModelHash</c> already folds the player slots), so both peers reproduce the
    /// same value at Ready time — never a server-supplied roster the client cannot recompute.
    ///
    /// Godot-free (src/Core/Definitions) so Tier-1 computes it headless; same FNV-64 primitive as
    /// <see cref="StartStateHash"/> — int/ulong only, analyzer-clean.
    /// </summary>
    public static class MatchAgreementHash
    {
        /// <summary>Algorithm version of THIS hash. Mixed FIRST so a bump moves the value alone. Bump only when the
        /// folded set/order changes (independent of <see cref="RulesetHash.AlgoVersion"/> and
        /// <see cref="StartStateHash.AlgoVersion"/>, which fold in as components).</summary>
        public const int AlgoVersion = 2; // Story 9.14: bumped 1→2 — the per-slot Team ordinal now folds into the roster loop

        private const ulong Offset = 14695981039346656037UL; // FNV-64 offset basis (same primitive as StartStateHash)
        private const ulong Prime  = 1099511628211UL;        // FNV-64 prime

        /// <summary>
        /// Compute the 64-bit match-agreement hash. <paramref name="initialDelay"/> is the match's starting input
        /// delay (LockstepManager.INPUT_DELAY — passed in so this type never references the Godot-coupled
        /// LockstepManager). N (the active player count + roster) is taken from <paramref name="model"/>'s
        /// <see cref="ScenarioData.PlayerSlots"/>. Never returns 0 (sentinel).
        /// </summary>
        public static ulong Compute(int initialDelay, ScenarioData model, HeroStore heroes)
        {
            ulong h = Offset;

            h = MixInt(h, AlgoVersion);                 // namespaces the hash
            h = MixULong(h, RulesetHash.Compute());     // structural-cap ruleset fingerprint
            h = MixInt(h, initialDelay);                // initial input-delay budget

            // Active player count + roster — derived deterministically from the applied model's player slots
            // (a client reproduces both at Ready time; never trusted from a server byte).
            int n = model.PlayerSlots?.Length ?? 0;
            h = MixInt(h, n);
            for (int slot = 0; slot < n; slot++)
                h = MixInt(h, (int)FactionRegistry.ToFaction(slot)); // roster faction ordinal for each active slot (unchanged since 9.4)

            // Story 9.14: fold the CANONICAL seeded team-id mask — faction-indexed over [1, FACTION_COUNT), EXACTLY the
            // mapping AllianceSeeder writes into AllianceStore (and SimChecksum folds). Folding the WHOLE faction-keyed
            // mask — not the positional per-slot .Team ordinal — makes the handshake validate precisely the mask the sim
            // will run, independent of PlayerSlots ORDER or GAPS: a reordered / sparse / non-contiguous roster (e.g. a
            // removed middle slot) folds the identical faction→team mapping the seeder produces, so a team on a gapped
            // high slot can never slip past unfolded (the earlier positional fold missed exactly that, failing OPEN).
            // FFA → canonical id == own faction, so the fold is inert apart from the one-time AlgoVersion 1→2 bump. Teams
            // live in the shared applied model (both peers recompute identically; never a wire byte) → a team mismatch
            // fails the start closed via the existing handshake gate.
            int[] teamIds = AllianceSeeder.ComputeTeamIds(model);
            for (int fi = 1; fi < teamIds.Length; fi++) // faction indices only; Neutral (index 0) is never teamed
                h = MixInt(h, teamIds[fi]);

            h = MixULong(h, StartStateHash.Compute(model, heroes)); // content + hero/item start-state (superset of scenarioHash)

            return h == 0UL ? 1UL : h; // sentinel: a valid agreement value must never hash to the fail-open "no hash" value
        }

        /// <summary>FNV-64 fold of a 32-bit int as 4 little-endian bytes (mirrors <see cref="StartStateHash"/>).</summary>
        private static ulong MixInt(ulong h, int value)
        {
            uint v = (uint)value;
            h ^= v & 0xFF;         h *= Prime;
            h ^= (v >> 8) & 0xFF;  h *= Prime;
            h ^= (v >> 16) & 0xFF; h *= Prime;
            h ^= (v >> 24) & 0xFF; h *= Prime;
            return h;
        }

        /// <summary>FNV-64 fold of a 64-bit value as low-32 THEN high-32 (two <see cref="MixInt"/> folds) — the
        /// convention <see cref="StartStateHash"/> uses for its content seed / HeroId folds.</summary>
        private static ulong MixULong(ulong h, ulong value)
        {
            h = MixInt(h, (int)(value & 0xFFFFFFFFUL)); // low 32 bits
            h = MixInt(h, (int)(value >> 32));          // high 32 bits
            return h;
        }
    }
}
