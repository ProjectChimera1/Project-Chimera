#nullable enable

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// The scope a persistable attribute belongs to (Story 3.8). Keeps the persistence mechanism scope-general (the
    /// story's "hero / unit / player attributes") while today only <see cref="Hero"/> has any eligible attributes —
    /// <see cref="Unit"/> and <see cref="Player"/> are structurally present but empty until their backing stores land
    /// (items 3.15, meta later). The editor renders ONLY scopes with ≥1 eligible attribute, so an empty scope never
    /// advertises an unbuilt system (D-1).
    /// </summary>
    public enum AttributeScope : byte
    {
        /// <summary>Per-hero persistent state (the only populated scope today — <c>hero.level</c>, <c>hero.xp</c>).</summary>
        Hero = 0,
        /// <summary>Per-unit persistent state — zero eligible attributes until an ItemStore/backing store lands (3.15).</summary>
        Unit = 1,
        /// <summary>Per-player / faction-meta persistent state — no profile object exists yet (meta later).</summary>
        Player = 2,
    }

    /// <summary>The immutable descriptor for one eligible persistable attribute: its stable dotted key, scope, a
    /// human display label, and the UX-DR53 tooltip sentence shown on the editor checklist row.</summary>
    public readonly record struct PersistableAttribute(string Key, AttributeScope Scope, string Label, string Tip);

    /// <summary>
    /// The closed, evidence-backed catalog of attributes that MAY carry forward between matches (Story 3.8, mirroring
    /// <see cref="HeroLevelingPresets"/> / <see cref="UnitCompositionPresets"/>). Godot-free, deterministic,
    /// Tier-1-testable — pure arrays + linear search (no <c>Dictionary</c> enumeration, the sim-layer determinism rule).
    ///
    /// <para><b>The catalog IS the reachable vocabulary (D-1).</b> <see cref="Eligible"/> enumerates exactly the concrete
    /// per-instance persistable fields that exist TODAY: <c>hero.level</c> and <c>hero.xp</c> (both <see cref="HeroStore"/>
    /// init state loaded by Story 3.9). The Persistence Manifest editor builds its checklist FROM this array, so a creator
    /// can never select a mid-game attribute through the UI — "only init-time-eligible attributes may be selected" is
    /// satisfied by construction. <see cref="IneligibleKnown"/> names the mid-game keys that a hand-editor might try, so
    /// the validator can reject them with a specific fail-closed reason (rather than a generic "unknown attribute").</para>
    /// </summary>
    public static class PersistableAttributes
    {
        /// <summary>
        /// The ordered, closed list of init-time-eligible attributes (display order = checklist order). Today these are
        /// the ONLY concrete per-instance persistable fields (<see cref="HeroStore.Level"/> / <see cref="HeroStore.Xp"/>,
        /// both "loaded as deterministic init state by Story 3.9"). Any hand-edited key not in this set is rejected.
        /// </summary>
        public static readonly PersistableAttribute[] Eligible =
        {
            new PersistableAttribute("hero.level", AttributeScope.Hero, "Hero Level",
                "Carry each hero's level forward between matches. Loaded as deterministic starting state."),
            new PersistableAttribute("hero.xp", AttributeScope.Hero, "Accumulated XP",
                "Carry each hero's accumulated experience forward between matches. Loaded as deterministic starting state."),
            new PersistableAttribute("hero.inventory", AttributeScope.Hero, "Inventory",
                "Carry each hero's carried items (by type + charges) forward between matches. Re-minted as deterministic starting state."),
        };

        /// <summary>
        /// The ordered, closed list of KNOWN-INELIGIBLE keys — mid-game runtime state a creator might reach for by hand-
        /// editing the scenario JSON, paired with the reason it cannot persist. These have NO editor row (the checklist
        /// offers only <see cref="Eligible"/>); they exist so the validator can reject a hand-edited mid-game key with a
        /// specific "mid-game-only state cannot be persisted" message instead of the generic "unknown attribute".
        /// </summary>
        public static readonly (string Key, string Reason)[] IneligibleKnown =
        {
            ("hero.current_hp", "live health, not persisted starting state"),
            ("hero.energy",     "live energy/mana, not persisted starting state"),
            ("hero.position",   "in-match world position, not persisted starting state"),
            ("hero.cooldowns",  "in-match ability cooldowns, not persisted starting state"),
            ("player.ore",      "in-match economy balance, not persisted starting state"),
            ("player.crystal",  "in-match economy balance, not persisted starting state"),
            ("player.supply",   "in-match supply count, not persisted starting state"),
        };

        /// <summary>
        /// Resolve an eligible attribute by <paramref name="key"/> (exact, case-sensitive). Returns true + the descriptor
        /// when <paramref name="key"/> is in <see cref="Eligible"/>, else false. Linear scan over the tiny closed set.
        /// </summary>
        public static bool TryGetEligible(string? key, out PersistableAttribute attr)
        {
            for (int i = 0; i < Eligible.Length; i++)
            {
                if (Eligible[i].Key == key)
                {
                    attr = Eligible[i];
                    return true;
                }
            }
            attr = default;
            return false;
        }

        /// <summary>True iff <paramref name="key"/> names an init-time-eligible attribute in <see cref="Eligible"/>.</summary>
        public static bool IsEligible(string? key) => TryGetEligible(key, out _);

        /// <summary>
        /// The reason <paramref name="key"/> is a KNOWN mid-game-ineligible attribute, or null when it is not in
        /// <see cref="IneligibleKnown"/> (i.e. it is eligible or simply unknown — the caller distinguishes those).
        /// </summary>
        public static string? IneligibleReason(string? key)
        {
            for (int i = 0; i < IneligibleKnown.Length; i++)
                if (IneligibleKnown[i].Key == key) return IneligibleKnown[i].Reason;
            return null;
        }

        /// <summary>The eligible attributes in <paramref name="scope"/>, in catalog order (empty when the scope has none).
        /// The editor renders a section per scope with a non-empty result — no empty section is drawn (D-1).</summary>
        public static PersistableAttribute[] ByScope(AttributeScope scope)
        {
            int n = 0;
            for (int i = 0; i < Eligible.Length; i++)
                if (Eligible[i].Scope == scope) n++;

            var result = new PersistableAttribute[n];
            int j = 0;
            for (int i = 0; i < Eligible.Length; i++)
                if (Eligible[i].Scope == scope) result[j++] = Eligible[i];
            return result;
        }
    }
}
