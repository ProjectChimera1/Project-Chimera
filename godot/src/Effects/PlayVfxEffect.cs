#nullable enable
using ProjectChimera.Combat;            // CombatEventType, CombatEventQueue
using ProjectChimera.Core;              // FixedVec3, UnitTag
using ProjectChimera.Core.Definitions;  // CombatFeedbackProfile (presentation-only payload)

namespace ProjectChimera.Effects
{
    /// <summary>
    /// Story 15.13 (DW-248) — the CHECKSUM-NEUTRAL visual-burst presentation leaf. Its ONLY effect is to push a
    /// <see cref="CombatEventType.PlayVfx"/> <c>CombatEvent</c> onto the presentation-only
    /// <see cref="CombatEventQueue"/> (which is NEVER a <c>SimChecksum</c> input), carrying the authored
    /// <see cref="CombatFeedbackProfile"/>. The existing <c>CombatFeedbackBridge</c> drainer renders it (a pooled
    /// hit-flash from <c>Feedback.HitFlash</c>, or the default look when absent).
    ///
    /// <para>It mutates ZERO folded sim state, so a graph is byte-identical for <c>SimChecksum</c> whether or not this
    /// leaf is present. The presentation payload (<see cref="CombatFeedbackProfile"/>, which carries <c>float</c>) is
    /// consciously EXCLUDED from the canonical fold — it cannot be folded deterministically and is presentation-only,
    /// matching <see cref="CombatFeedbackProfile"/>'s documented hash exclusion.</para>
    /// </summary>
    public sealed class PlayVfxEffect : LeafEffect
    {
        /// <summary>The authored presentation look to render (null ⇒ the bridge's default flash). Presentation-only,
        /// excluded from <c>SimChecksum</c>/the canonical fold.</summary>
        public readonly CombatFeedbackProfile? Feedback;

        /// <summary>Construct the visual-burst leaf. <paramref name="requireTag"/> (default None) gates the apply on the
        /// primary target's tag.</summary>
        public PlayVfxEffect(CombatFeedbackProfile? feedback, UnitTag requireTag = UnitTag.None) : base(requireTag)
            => Feedback = feedback;

        /// <inheritdoc />
        internal override void Apply(in EffectContext ctx)
        {
            // Resolve the event position (mirrors AbilityCastSystem's feedbackPos): the ground point for a GroundPoint
            // cast, else the primary target, else the caster, else origin. Pure reads — no sim mutation.
            EntityWorld world = ctx.World;
            FixedVec3 pos = ctx.HasTargetPoint ? ctx.TargetPoint
                : world.IsAlive(ctx.PrimaryTargetId) ? world.Position[ctx.PrimaryTargetId]
                : world.IsAlive(ctx.CasterId) ? world.Position[ctx.CasterId]
                : FixedVec3.Zero;
            ctx.Events?.Push(CombatEventType.PlayVfx, pos, Feedback); // null sink (bare test) ⇒ safe no-op
        }
    }
}
