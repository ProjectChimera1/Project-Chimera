#nullable enable
using ProjectChimera.Core;

namespace ProjectChimera.Effects
{
    /// <summary>How a re-applied <see cref="Modifier"/> with the same <see cref="Modifier.Id"/> stacks.</summary>
    public enum StackRule : byte
    {
        /// <summary>Re-applying resets the existing instance's duration; no second stack.</summary>
        Refresh = 0,
        /// <summary>Re-applying adds an independent stack, up to <see cref="Modifier.MaxStacks"/>.</summary>
        Stack = 1,
        /// <summary>Re-applying is ignored while an instance is active.</summary>
        Ignore = 2,
    }

    /// <summary>
    /// Boolean status effects a <see cref="Modifier"/> may impose. OR-able. Reserved set — the systems that
    /// honour each flag land with the ModifierStore (Story 2.2b) and the abilities that use them.
    /// </summary>
    [System.Flags]
    public enum StatusFlags : byte
    {
        None = 0,
        Stunned = 1 << 0,
        Rooted = 1 << 1,
        Silenced = 1 << 2,
        Disarmed = 1 << 3,
        Invulnerable = 1 << 4,
    }

    /// <summary>
    /// A first-class, timed stat/status descriptor applied to an entity (AR-9). It is NOT an
    /// <see cref="EffectNode"/> — it is the typed payload an <see cref="ApplyModifierEffect"/> leaf installs into
    /// the ModifierStore. Both the type AND its fields are DEFINED in Story 2.1 (so the closed vocabulary is
    /// complete and AC1's "first-class Modifier" holds); its STORE RESOLUTION — registering, stacking, ticking
    /// the period, recomputing effective stats, and expiry — lands in Story 2.2b alongside the ModifierStore.
    ///
    /// All magnitudes are <see cref="Fixed"/> (deterministic). The optional periodic effect re-uses the same
    /// closed Effect-Graph (a <see cref="PersistentEffect"/>-style time axis), so modifiers introduce no new
    /// execution surface.
    /// </summary>
    public sealed class Modifier
    {
        /// <summary>Identity used for stacking — two modifiers stack against each other iff their ids match.</summary>
        public readonly int Id;

        /// <summary>Lifetime in sim ticks (0 = instantaneous/one-shot; &lt;0 = permanent until dispelled).</summary>
        public readonly int DurationTicks;

        /// <summary>Behaviour when a same-id modifier is re-applied.</summary>
        public readonly StackRule Stacking;

        /// <summary>Maximum simultaneous stacks when <see cref="Stacking"/> is <see cref="StackRule.Stack"/>.</summary>
        public readonly int MaxStacks;

        // ── Fixed stat deltas (additive; the effective-stat recompute lives in 2.2b) ──
        /// <summary>Flat max-health delta while active.</summary>
        public readonly Fixed MaxHealthDelta;
        /// <summary>Flat attack-damage delta while active.</summary>
        public readonly Fixed AttackDamageDelta;
        /// <summary>Flat move-speed delta while active.</summary>
        public readonly Fixed MoveSpeedDelta;
        /// <summary>Flat armor delta while active (Story 2.6). Folds into <c>EffectiveArmor</c>; reduces incoming
        /// post-matrix damage (<c>DamageResolver</c> subtracts <c>EffectiveArmor</c>, floored at 0).</summary>
        public readonly Fixed ArmorDelta;

        /// <summary>Status flags imposed while active.</summary>
        public readonly StatusFlags Status;

        /// <summary>Optional effect run every <see cref="PeriodTicks"/> ticks (DoT/HoT). Null = no periodic tick.</summary>
        public readonly EffectNode? PeriodEffect;

        /// <summary>Period length in ticks for <see cref="PeriodEffect"/> (0 when there is none).</summary>
        public readonly int PeriodTicks;

        /// <summary>Construct a modifier descriptor. Pure data; no execution happens here.
        /// <paramref name="armorDelta"/> is an optional trailing parameter (Story 2.6) so every pre-2.6
        /// <c>new Modifier(...)</c> call site stays source-compatible (defaults to <see cref="Fixed.Zero"/>).</summary>
        public Modifier(int id, int durationTicks, StackRule stacking, int maxStacks,
                        Fixed maxHealthDelta, Fixed attackDamageDelta, Fixed moveSpeedDelta,
                        StatusFlags status, EffectNode? periodEffect, int periodTicks,
                        Fixed armorDelta = default)
        {
            Id = id;
            DurationTicks = durationTicks;
            Stacking = stacking;
            MaxStacks = maxStacks;
            MaxHealthDelta = maxHealthDelta;
            AttackDamageDelta = attackDamageDelta;
            MoveSpeedDelta = moveSpeedDelta;
            Status = status;
            PeriodEffect = periodEffect;
            PeriodTicks = periodTicks;
            ArmorDelta = armorDelta;
        }
    }
}
