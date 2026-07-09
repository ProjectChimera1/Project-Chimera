#nullable enable
using System;
using System.Linq;
using ProjectChimera.Core; // Fixed, HeroStore, HeroId

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// The canonical INIT-STATE hash (Story 3.2, AR-12 / D4-C) — a NEW, distinct FNV-64 over the full match
    /// start-state = the scenario content model PLUS the persistent <see cref="HeroStore"/> contents, computed ONCE
    /// at match start. It is 3.2's primary determinism deliverable: the MP-safety guarantee that two clients starting
    /// from mismatched hero loadouts (level/XP/roster) are REJECTED at the handshake instead of desyncing in-sim from
    /// tick 1 — the StartCrystal-desync lesson, applied to heroes.
    ///
    /// It is deliberately its OWN hash with its own <see cref="AlgoVersion"/>, NOT an extension of
    /// <see cref="CanonicalModelHash"/> (whose own AlgoVersion moves independently — Story 4.4 bumped it 3→4 — and
    /// which structurally cannot see heroes — they come from PlayerProfile, not ScenarioData) and NOT the per-tick
    /// <see cref="SimChecksum"/>. Per D-2 it is DRY: it folds
    /// the <see cref="CanonicalModelHash.Compute"/> value as the content SEED (never re-deriving that slot/node/
    /// building/unit walk), then the hero rows, so the result is a strict superset of content = "full init-time
    /// state including HeroStore" (AC3).
    ///
    /// Determinism discipline:
    ///   • Computed over the canonical MODEL + HeroStore via <c>Fixed.Raw</c> canonical integers — NEVER a post-spawn
    ///     <see cref="EntityWorld"/> float snapshot (which would pass through <c>Fixed.FromFloat</c> and let the
    ///     M2-local and M5-server producers diverge on float round-trips).
    ///   • Hero rows are folded ASCENDING BY <see cref="HeroId"/> (<see cref="HeroStore.FoldOrder"/>), so the SAME
    ///     set of heroes hashes identically regardless of mint order or slot layout (producer-independence).
    ///   • <see cref="AlgoVersion"/> is mixed FIRST, so a version bump moves the value even with no field change
    ///     (mirrors <see cref="CanonicalModelHash"/>); a <c>0 → 1</c> sentinel keeps a valid state off the fail-open
    ///     "no hash" value.
    ///
    /// D-3: Story 3.2 COMPUTES + PINS this value (via the golden + the independent-FNV pin). Wiring it into the
    /// network Ready-packet + the server-attested multi-hash handshake is DEFERRED to Epic 9 / M5 — do NOT add it to
    /// the wire or bump PROTOCOL_VERSION here (mirrors how <see cref="CanonicalModelHash"/> landed in Story 1.7).
    ///
    /// Godot-free (src/Core/Definitions) so Tier-1 computes it headless; sim code (in the analyzer gate) — int/ulong/
    /// <c>Fixed.Raw</c> only.
    /// </summary>
    public static class StartStateHash
    {
        /// <summary>Algorithm version of THIS hash (independent of <see cref="CanonicalModelHash.AlgoVersion"/> and
        /// <see cref="SimChecksum.AlgoVersion"/>). 1 = content seed + HeroStore {HeroId, Level, Xp} (Story 3.2);
        /// 2 = additionally folds the per-hero inventory (INVENTORY_SLOTS refs) + the placed map-items (Story 3.15) so a
        /// mismatched initial item loadout is rejectable at the handshake. Bump only when the folded set/order changes.</summary>
        public const int AlgoVersion = 2;

        private const ulong Offset = 14695981039346656037UL; // FNV-64 offset basis (same primitive as CanonicalModelHash)
        private const ulong Prime  = 1099511628211UL;        // FNV-64 prime

        /// <summary>
        /// Compute the 64-bit start-state hash over <paramref name="model"/> (the applied scenario content) plus
        /// <paramref name="heroes"/> (the persistent hero init state). Never returns 0 (sentinel). An EMPTY HeroStore
        /// (the Story 3.2 runtime, before the 3.9 load path) folds no hero rows → the hash is the content seed alone
        /// (still distinct from the raw <see cref="CanonicalModelHash"/> value: AlgoVersion is mixed first and the
        /// seed is folded as two mixes).
        /// </summary>
        public static ulong Compute(ScenarioData model, HeroStore heroes)
        {
            ulong h = Offset;

            h = MixInt(h, AlgoVersion);                             // namespaces the hash; a bump moves the value alone
            h = MixULong(h, CanonicalModelHash.Compute(model));     // content SEED (D-2 — DRY; CanonicalModelHash untouched, stays v3)

            // Story 3.15 (v2): the per-scenario USABLE inventory-slot cap (NULL ⇒ the full HeroStore.INVENTORY_SLOTS
            // stride). It is sim-affecting (drives the full-inventory pickup denial via ItemSystem.UsableSlots), so two
            // clients starting from mismatched caps would diverge — it MUST be handshake-rejectable. Folded here as its
            // resolved default (mirrors ScenarioApplier's ConfigureUsableSlots argument) so the same effective cap hashes
            // identically whether authored explicitly or omitted.
            h = MixInt(h, model.InventorySlotCount ?? HeroStore.INVENTORY_SLOTS);

            // HeroStore rows, ASCENDING BY HeroId (producer-independent). Fold only the CANONICAL persisted state —
            // the stable identity + the progression init state (Level/Xp). EntityId (the runtime entity link) is
            // deliberately NOT folded: it is which entity currently embodies the hero this match, would differ between
            // producers, and is not persisted init state.
            int[] order = heroes.FoldOrder();
            foreach (int slot in order)
            {
                h = MixULong(h, heroes.Id[slot].Value);   // stable identity: two 32-bit mixes (low/high), like SimRng state (D-4)
                h = MixInt(h, heroes.Level[slot]);        // hero level (init state; mutated mid-match from Story 3.13)
                h = MixInt(h, heroes.Xp[slot].Raw);       // accumulated XP as its canonical Fixed.Raw integer
                // Story 3.15 (v2): the per-hero inventory refs. In 3.15 hero inventory starts EMPTY each match, so these
                // fold their -1 sentinel — but the fold makes a scenario that DOES seed a hero loadout (future) rejectable.
                int invBase = slot * HeroStore.INVENTORY_SLOTS;
                for (int s = 0; s < HeroStore.INVENTORY_SLOTS; s++)
                    h = MixInt(h, heroes.Inventory[invBase + s]);
            }

            // Story 3.15 (v2): placed map-items — sorted by a TOTAL order (item_id ordinal, then quantized X/Z Raw) so
            // neither JSON array order nor a tie on a partial key can move the hash (the CanonicalModelHash placement-walk
            // convention). Fold item_id (UTF-8, length-prefixed) + the quantized X/Z the sim will place at.
            foreach (ScenarioItem it in (model.Items ?? Array.Empty<ScenarioItem>())
                         .OrderBy(x => x.ItemId, StringComparer.Ordinal)
                         .ThenBy(x => Fixed.FromFloat(x.X).Raw).ThenBy(x => Fixed.FromFloat(x.Z).Raw))
            {
                h = MixStr(h, it.ItemId);
                h = MixInt(h, Fixed.FromFloat(it.X).Raw);
                h = MixInt(h, Fixed.FromFloat(it.Z).Raw);
            }

            return h == 0UL ? 1UL : h; // sentinel: a valid init-state must never hash to the fail-open "no hash" value
        }

        /// <summary>FNV-64 fold of a 32-bit int as 4 little-endian bytes (mirrors <see cref="CanonicalModelHash"/>).</summary>
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
        /// SimRng-state fold convention, applied to the content seed and to each <see cref="HeroId.Value"/>.</summary>
        private static ulong MixULong(ulong h, ulong value)
        {
            h = MixInt(h, (int)(value & 0xFFFFFFFFUL)); // low 32 bits
            h = MixInt(h, (int)(value >> 32));          // high 32 bits
            return h;
        }

        /// <summary>FNV-64 fold of a string (Story 3.15): a length prefix (so "ab"+"c" != "a"+"bc", and null != "")
        /// followed by the UTF-8 bytes — mirrors <see cref="CanonicalModelHash"/>.MixStr.</summary>
        private static ulong MixStr(ulong h, string? s)
        {
            h = MixInt(h, s?.Length ?? -1);
            if (s == null) return h;
            foreach (byte by in System.Text.Encoding.UTF8.GetBytes(s))
            {
                h ^= by;
                h *= Prime;
            }
            return h;
        }
    }
}
