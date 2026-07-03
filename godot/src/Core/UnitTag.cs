namespace ProjectChimera.Core
{
    /// <summary>
    /// An authorable classification of what a unit fundamentally IS (Story 2.11, DG-4 / FR-56 / AR-8) — a third,
    /// orthogonal combat axis distinct from <c>damage_type</c>, <c>armor_type</c>, and the <see cref="UnitCategory"/>
    /// archetype. The effect engine filters and counters by it (<c>SearchArea.require_tag</c> and the single-target
    /// <c>LeafEffect.RequireTag</c> gate), so a creator can express "bonus damage vs Mechanical" or "heal only Organic"
    /// WITHOUT abusing the damage/armor matrix (which scales how hard a hit lands — a different lever). Authored via
    /// <c>UnitDefinition.tags</c> (a snake_case string array); a unit MAY carry several tags (OR-folded).
    ///
    /// Divergence from <see cref="AttackDomain"/>: there is deliberately <b>NO <c>All</c> member</b>. An unauthored
    /// unit is <see cref="None"/> (matched by no tag predicate), NOT "every tag" — the back-compat default is
    /// "untagged," so no shipping unit silently gains a classification.
    ///
    /// NOT folded into <c>SimChecksum</c> — it is an authored-immutable, effect-read field, exactly like
    /// <see cref="AttackDomain"/> / <see cref="UnitCategory"/> / <c>DamageTypeOf</c> / <c>ArmorTypeOf</c> (none of
    /// which <c>SimChecksum</c> references). A divergent authored tag changes which target an effect selects, which
    /// moves the affected entity's already-folded Health/ModifierStore/Position (caught transitively — the
    /// checksum-fold-timing rule: fold only when an array first becomes mutable mid-match; tags stay read-only here).
    /// </summary>
    [System.Flags]
    public enum UnitTag : byte
    {
        None      = 0,
        Organic   = 1 << 0,
        Mechanical = 1 << 1,
        Magical   = 1 << 2,
    }
}
