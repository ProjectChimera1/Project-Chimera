namespace ProjectChimera.Core
{
    /// <summary>
    /// The set of target domains a unit is <b>capable of attacking</b> (Story 2.9a, FR-11/FR-12). This is an
    /// <b>attacker-capability</b> axis, distinct from the unit's OWN archetype (<see cref="UnitCategory"/>): an
    /// <c>["Air"]</c> ground unit is an anti-air-only defender, while an Air unit with the default <see cref="All"/>
    /// still hits ground. Authored via <c>UnitDefinition.attack_domains</c>; default (unauthored) is <see cref="All"/>
    /// so every existing unit attacks every domain exactly as before.
    ///
    /// NOT folded into <c>SimChecksum</c> — it is an authored-immutable, combat-read field, exactly like
    /// <see cref="UnitCategory"/> / <c>DamageTypeOf</c> / <c>ArmorTypeOf</c>. A divergent authored choice changes which
    /// target is hit, which moves the affected entity's already-folded Position/Health (caught transitively).
    /// </summary>
    [System.Flags]
    public enum AttackDomain : byte
    {
        None      = 0,
        Ground    = 1 << 0,
        Air       = 1 << 1,
        Structure = 1 << 2,
        All       = Ground | Air | Structure,
    }

    /// <summary>
    /// The three mutually-exclusive target domains a candidate can belong to. This is the neutral, single-source
    /// classification of "which domain is this entity" — both the combat auto-acquire filter (<see cref="AttackDomain"/>,
    /// AC1) and the ability-side <c>SearchArea</c> filter (<c>Effects.TargetFilter</c> Air/Ground/Structure bits, AC6)
    /// map <see cref="DomainClassifier.Of"/> onto their own bit set, so the two filters can never disagree on what a
    /// "flyer" is.
    /// </summary>
    public enum Domain : byte
    {
        Ground    = 0,
        Air       = 1,
        Structure = 2,
    }

    /// <summary>
    /// Single source of truth for "which <see cref="Domain"/> a target belongs to" and the combat-side capability
    /// check. Pure integer logic (no <c>Fixed</c>, no float, no allocation) — safe inside the deterministic tick.
    /// </summary>
    public static class DomainClassifier
    {
        /// <summary>
        /// Classify a target's archetype into its domain: Air units fly, Structures are buildings, everything else
        /// (Worker/Melee/Ranged/Siege) is ground. This is the ONLY place the "what is a flyer" decision lives.
        /// </summary>
        public static Domain Of(UnitCategory category) => category switch
        {
            UnitCategory.Air       => Domain.Air,
            UnitCategory.Structure => Domain.Structure,
            _                      => Domain.Ground,
        };

        /// <summary>The <see cref="AttackDomain"/> bit corresponding to a target's <see cref="Domain"/>.</summary>
        public static AttackDomain BitOf(Domain domain) => domain switch
        {
            Domain.Air       => AttackDomain.Air,
            Domain.Structure => AttackDomain.Structure,
            _                => AttackDomain.Ground,
        };

        /// <summary>
        /// True if an attacker whose capability mask is <paramref name="attackerDomains"/> may attack a target whose
        /// archetype is <paramref name="targetCategory"/> (AC1). A single integer bit-AND — deterministic and order-neutral.
        /// </summary>
        public static bool CanAttack(AttackDomain attackerDomains, UnitCategory targetCategory)
            => (attackerDomains & BitOf(Of(targetCategory))) != AttackDomain.None;
    }
}
