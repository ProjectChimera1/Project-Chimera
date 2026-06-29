#nullable enable
using ProjectChimera.Combat;            // DamageType
using ProjectChimera.Core;              // Fixed
using ProjectChimera.Core.Definitions;  // AbilityDefinition
using ProjectChimera.Effects;           // *Effect, Modifier, StackRule, StatusFlags, TargetFilter

namespace ProjectChimera.Sim.Tests.Effects
{
    /// <summary>
    /// Story 2.6 — in-code passive ability definitions for the passive-runtime tests + the passive golden,
    /// mirroring the three shipped sample JSONs (<c>aura_guard</c> / <c>onhit_searing</c> / <c>furnace_trickle</c>)
    /// but built deterministically in code so tests stay hermetic (no filesystem). The values match the JSONs, so
    /// these double as a sanity check on the sample data (the <see cref="AbilityTestAbilities"/> pattern).
    ///
    /// These do NOT need to pass <c>AbilityValidator</c> — that gate runs at load/save, proven by
    /// <c>PassiveAbilityValidationTests</c> against the JSONs. Here the graphs are run directly by the executor.
    /// </summary>
    internal static class PassiveTestAbilities
    {
        /// <summary>The modifier id the aura grants (matches aura_guard.json's modifier.id).</summary>
        public const int AuraModifierId = 2001;

        /// <summary>aura_guard: activation Aura, None targeting, SearchArea(radius 5, Ally) → ApplyModifier(+5 armor,
        /// 2-tick Refresh). The per-tick grant: a short Refresh modifier carrying +armor (Modifier.ArmorDelta).</summary>
        public static AbilityDefinition AuraGuard() => new AbilityDefinition
        {
            Id = "aura_guard", DisplayName = "Guardian Aura", Targeting = "None", Activation = "aura",
            EffectGraph = new SearchAreaEffect(
                Fixed.FromInt(5), TargetFilter.Ally,
                // Modifier ctor: id, durationTicks, stacking, maxStacks, maxHealthDelta, attackDamageDelta,
                // moveSpeedDelta, status, periodEffect, periodTicks, armorDelta (Story 2.6 trailing param).
                new ApplyModifierEffect(new Modifier(
                    AuraModifierId, 2, StackRule.Refresh, 1, Fixed.Zero, Fixed.Zero, Fixed.Zero,
                    StatusFlags.None, null, 0, Fixed.FromInt(5)))),
        };

        /// <summary>onhit_searing: activation OnHit, None targeting, Damage 15 Magic — the rider that fires when this
        /// unit's melee attack lands.</summary>
        public static AbilityDefinition OnHitSearing() => new AbilityDefinition
        {
            Id = "onhit_searing", DisplayName = "Searing Strikes", Targeting = "None", Activation = "on_hit",
            EffectGraph = new DamageEffect(Fixed.FromInt(15), DamageType.Magic),
        };

        /// <summary>furnace_trickle: activation WhileAlive, Self targeting, Persistent(period Heal 2 every 5 ticks,
        /// 256 periods) — the continuous self-regen installed at spawn.</summary>
        public static AbilityDefinition FurnaceTrickle() => new AbilityDefinition
        {
            Id = "furnace_trickle", DisplayName = "Furnace Trickle", Targeting = "Self", Activation = "while_alive",
            EffectGraph = new PersistentEffect(null, new HealEffect(Fixed.FromInt(2)), null, 5, 256),
        };
    }
}
