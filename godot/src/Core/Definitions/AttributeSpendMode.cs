#nullable enable

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// Story 15-24e — the creator-authored choice of WHO allocates a hero's attribute growth, declared once per
    /// faction on <see cref="AttributeModelDefinition.SpendMode"/> (JSON <c>spend_mode</c>).
    ///
    /// <para>Deliberately TWO members, not three. The parent spec's "auto / spent / player-choice" reads as a
    /// single toggle plus an AUTHORING-time question: where the auto vector's numbers come from (hand-authored, or
    /// drafted by the Attribute Editor's LLM hook — leg f). That is not a third runtime behaviour, so it is not a
    /// third enum member. Widening later costs one member plus a <c>ContentHash</c> bump, and nothing authors the
    /// field yet.</para>
    ///
    /// <para><b>AI-controlled slots always behave as <see cref="Auto"/></b> (Alec, 2026-08-26). That decision is
    /// read from the match-agreed <c>AiControlPlan</c> — handshake-folded and match-constant — never from any
    /// client-local notion of who is playing; see <c>HeroStore.IsPlayerSpent</c>, the single oracle both the
    /// withholding site and the read seam consult.</para>
    /// </summary>
    public enum AttributeSpendMode
    {
        /// <summary>The pre-15-24e behaviour and the JSON default: a level applies the hero's authored per-level
        /// attribute vector immediately. Every shipped faction is here (none authors <c>spend_mode</c>), which is
        /// what makes the whole story golden-neutral.</summary>
        Auto = 0,

        /// <summary>A level grants ONE unspent attribute point instead of applying the per-level attribute vector;
        /// the player allocates it through <c>UnitCommand.SpendAttributePoint</c>. The FLAT hero stat-growth lanes
        /// (<c>health_per_level</c> / <c>damage_per_level</c> / <c>armor_per_level</c>) are hero STAT growth, not
        /// attributes, and keep applying automatically in this mode too.</summary>
        PlayerSpent = 1,
    }
}
