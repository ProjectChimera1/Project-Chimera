#nullable enable
namespace ProjectChimera.Effects
{
    /// <summary>
    /// Composition node: a time-axis effect with an initial pulse, a periodic pulse every
    /// <see cref="PeriodTicks"/> for <see cref="PeriodCount"/> periods, and a final pulse on expiry. It is the
    /// third of the exactly-three composition nodes (AC1).
    ///
    /// DW-785 — the MECHANISM, not the release (the DW-663 rule: a version-scoped claim rots the moment the version
    /// moves and gives a reader nothing to check). <c>EffectExecutor</c> hands this node to
    /// <c>ModifierStore.InstallPersistent</c>, which runs <see cref="InitialEffect"/> immediately on the store's OWN
    /// dedicated executor and schedules <see cref="PeriodEffect"/>/<see cref="ExpireEffect"/> from there. The one
    /// thing the executor still fail-closes on is a MISSING store: an <c>EffectContext</c> with a null
    /// <c>ModifierStore</c> throws rather than silently no-op'ing. <c>EffectBounds.Validate</c> walks the sub-effects
    /// for the depth/structure check, and the Story 2.3 <c>AbilityValidator</c> AC5 fence rejects install leaves,
    /// nested persistents and <c>SearchAreaEffect</c> inside any phase, so no loadable ability can re-enter that
    /// executor. <see cref="InitialEffect"/>, <see cref="PeriodEffect"/>, and <see cref="ExpireEffect"/> are optional
    /// (null = no pulse at that phase).
    /// </summary>
    public sealed class PersistentEffect : CompositionEffect
    {
        /// <summary>Effect applied once when the persistent effect is installed (null = none).</summary>
        public readonly EffectNode? InitialEffect;

        /// <summary>Effect applied every <see cref="PeriodTicks"/> ticks (null = none).</summary>
        public readonly EffectNode? PeriodEffect;

        /// <summary>Effect applied once when the persistent effect expires (null = none).</summary>
        public readonly EffectNode? ExpireEffect;

        /// <summary>Ticks between periodic pulses.</summary>
        public readonly int PeriodTicks;

        /// <summary>Number of periodic pulses. Authored freely here; <c>ModifierStore.InstallPersistent</c> is what
        /// bounds it, clamping the installed schedule into <c>[0, EffectCaps.MaxPersistentPeriods]</c> (DW-785 — the
        /// clamp is the mechanism, the release number was not).</summary>
        public readonly int PeriodCount;

        /// <summary>
        /// Story 2.13 (AC4.2) — when true, the periodic pulse is LIFELONG: on reaching the
        /// <c>EffectCaps.MaxPersistentPeriods</c> cap, <c>ModifierStore.Advance</c> refills the SAME slot in place
        /// instead of expiring, so a <c>while_alive</c> self-passive (e.g. the Sanguine Furnace HoT) keeps pulsing
        /// until the host dies/recycles. Authored / peer-identical — NOT folded (the PeriodTicks/PeriodCount posture).
        /// Default false ⇒ every existing persistent still expires at the cap exactly as before.
        /// </summary>
        public readonly bool Lifelong;

        /// <summary>Construct a persistent (time-axis) effect descriptor.</summary>
        public PersistentEffect(EffectNode? initialEffect, EffectNode? periodEffect, EffectNode? expireEffect,
                                int periodTicks, int periodCount, bool lifelong = false)
        {
            InitialEffect = initialEffect;
            PeriodEffect = periodEffect;
            ExpireEffect = expireEffect;
            PeriodTicks = periodTicks;
            PeriodCount = periodCount;
            Lifelong = lifelong;
        }
    }
}
