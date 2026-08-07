#nullable enable
using ProjectChimera.Combat;            // CombatEventType, CombatEventQueue
using ProjectChimera.Core;              // FixedVec3, UnitTag
using ProjectChimera.Core.Definitions;  // CombatFeedbackProfile (presentation-only payload)

namespace ProjectChimera.Effects
{
    /// <summary>
    /// Story 15.13 (DW-248) — the CHECKSUM-NEUTRAL screen-shake presentation leaf. Its ONLY effect is to push a
    /// <see cref="CombatEventType.ShakeScreen"/> <c>CombatEvent</c> onto the presentation-only
    /// <see cref="CombatEventQueue"/> (never a <c>SimChecksum</c> input), carrying the authored
    /// <see cref="CombatFeedbackProfile"/>. <c>CombatFeedbackBridge</c> drains it and calls the camera's
    /// <c>SetShake(duration, strength)</c> from <c>Feedback.Shake</c> (a null shake ⇒ no shake).
    ///
    /// <para>It mutates ZERO folded sim state; the presentation payload (which carries <c>float</c>) is consciously
    /// EXCLUDED from the canonical fold, matching <see cref="CombatFeedbackProfile"/>'s documented hash exclusion.</para>
    /// </summary>
    public sealed class ShakeScreenEffect : LeafEffect
    {
        /// <summary>The authored presentation payload whose <c>Shake</c> the bridge applies (null ⇒ no shake).
        /// Presentation-only, excluded from <c>SimChecksum</c>/the canonical fold.</summary>
        public readonly CombatFeedbackProfile? Feedback;

        /// <summary>Construct the screen-shake leaf. <paramref name="requireTag"/> (default None) gates the apply on the
        /// primary target's tag.</summary>
        public ShakeScreenEffect(CombatFeedbackProfile? feedback, UnitTag requireTag = UnitTag.None) : base(requireTag)
            => Feedback = feedback;

        /// <inheritdoc />
        internal override void Apply(in EffectContext ctx)
        {
            // Resolve the event position (mirrors AbilityCastSystem's feedbackPos). Pure reads — no sim mutation.
            EntityWorld world = ctx.World;
            FixedVec3 pos = ctx.HasTargetPoint ? ctx.TargetPoint
                : world.IsAlive(ctx.PrimaryTargetId) ? world.Position[ctx.PrimaryTargetId]
                : world.IsAlive(ctx.CasterId) ? world.Position[ctx.CasterId]
                : FixedVec3.Zero;
            ctx.Events?.Push(CombatEventType.ShakeScreen, pos, Feedback); // null sink (bare test) ⇒ safe no-op
        }
    }
}
