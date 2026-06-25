#nullable enable
namespace ProjectChimera.Effects
{
    /// <summary>
    /// Composition node: a time-axis effect with an initial pulse, a periodic pulse every
    /// <see cref="PeriodTicks"/> for <see cref="PeriodCount"/> periods, and a final pulse on expiry. It is the
    /// third of the exactly-three composition nodes (AC1).
    ///
    /// The TYPE is defined in Story 2.1 so the closed vocabulary is complete, but its periodic EXECUTION resolves
    /// against the ModifierStore and lands in Story 2.2b. In 2.1 the executor recognizes the type and fail-closes
    /// (throws) rather than mutating a nonexistent store; <c>EffectBounds.Validate</c> still walks its sub-effects
    /// for the depth/structure check. <see cref="InitialEffect"/>, <see cref="PeriodEffect"/>, and
    /// <see cref="ExpireEffect"/> are optional (null = no pulse at that phase).
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

        /// <summary>Number of periodic pulses (bounded by <c>EffectCaps.MaxPersistentPeriods</c> in 2.2b).</summary>
        public readonly int PeriodCount;

        /// <summary>Construct a persistent (time-axis) effect descriptor.</summary>
        public PersistentEffect(EffectNode? initialEffect, EffectNode? periodEffect, EffectNode? expireEffect,
                                int periodTicks, int periodCount)
        {
            InitialEffect = initialEffect;
            PeriodEffect = periodEffect;
            ExpireEffect = expireEffect;
            PeriodTicks = periodTicks;
            PeriodCount = periodCount;
        }
    }
}
