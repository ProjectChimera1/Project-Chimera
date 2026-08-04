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
    /// but built deterministically in code so tests stay hermetic (no filesystem). <c>aura_guard</c> / <c>onhit_searing</c>
    /// still mirror their JSONs exactly (the <see cref="AbilityTestAbilities"/> pattern).
    ///
    /// WARNING — <c>furnace_trickle</c> is DELIBERATELY DIVERGED (Story 2.10, D-3): <c>furnace_trickle.json</c> was
    /// retuned to Heal 3 / period 15, but this fixture stays FROZEN at Heal 2 / period 5 to keep the Story 2.6
    /// passive-scenario golden byte-identical (AC4). Do NOT "re-sync" it to the JSON — that silently moves the passive
    /// golden and forces a surprise re-baseline (the exact trap this note exists to prevent).
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
        /// 256 periods) — the continuous self-regen installed at spawn. NOTE: frozen at 2/5 ON PURPOSE — the shipped
        /// furnace_trickle.json was retuned to 3/15 in Story 2.10 (D-3); this fixture must NOT follow it or the Story 2.6
        /// passive golden moves (see the class-level WARNING).</summary>
        public static AbilityDefinition FurnaceTrickle() => new AbilityDefinition
        {
            Id = "furnace_trickle", DisplayName = "Furnace Trickle", Targeting = "Self", Activation = "while_alive",
            EffectGraph = new PersistentEffect(null, new HealEffect(Fixed.FromInt(2)), null, 5, 256),
        };

        /// <summary>The modifier id <see cref="IronSkin"/> installs (no collision with <see cref="AuraModifierId"/>).</summary>
        public const int IronSkinModifierId = 2002;

        /// <summary>The flat armor bonus <see cref="IronSkin"/> grants per installed stack.</summary>
        public const int IronSkinArmorPerStack = 4;

        /// <summary>
        /// iron_skin (DW-300): activation WhileAlive, Self targeting, a PERMANENT <c>ApplyModifier</c>
        /// (duration_ticks −1) carrying +4 armor — the OTHER validated while_alive root shape (the validator allows
        /// exactly "a permanent ApplyModifier OR a Persistent"; <see cref="FurnaceTrickle"/> covers the Persistent
        /// half). Deliberately <see cref="StackRule.Stack"/> (MaxStacks 4) so a DUPLICATED install is visible as a
        /// multiplied <c>EffectiveArmor</c> rather than a silent duration refresh. Used only by the DW-300
        /// re-apply-idempotence tests — it is NOT in any golden scenario.
        /// </summary>
        public static AbilityDefinition IronSkin() => new AbilityDefinition
        {
            Id = "iron_skin", DisplayName = "Iron Skin", Targeting = "Self", Activation = "while_alive",
            EffectGraph = new ApplyModifierEffect(new Modifier(
                IronSkinModifierId, -1, StackRule.Stack, 4, Fixed.Zero, Fixed.Zero, Fixed.Zero,
                StatusFlags.None, null, 0, Fixed.FromInt(IronSkinArmorPerStack))),
        };
    }
}
