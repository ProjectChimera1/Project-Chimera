#nullable enable
using System;
using ProjectChimera.Combat;   // DamageType
using ProjectChimera.Core;     // Fixed
using ProjectChimera.Effects;  // the closed 2.1 effect vocabulary + Modifier/StackRule/StatusFlags/TargetFilter

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// The closed set of simple-mode presets for the Ability Editor (Story 2.5a, FR-8/FR-10). Each preset is a
    /// pure <c>(tuned params) → <see cref="AbilityDefinition"/></c> builder over the closed 2.1 effect vocabulary —
    /// Godot-free, deterministic, Tier-1-testable. The Simple-mode editor enumerates <see cref="All"/> for its
    /// dropdown, seeds the numeric rows from <see cref="Defaults"/>, and rebuilds the in-memory model via
    /// <see cref="Build"/> on every edit (so the creator "never edits JSON" — AC1).
    ///
    /// DETERMINISM / analyzer: every numeric is <c>Fixed</c> or <c>int</c>, constructed via <c>Fixed.FromInt</c> /
    /// named constants — NEVER the <c>float</c> keyword (CHM0001) and NEVER <c>FromFloat</c>/<c>ToFloat</c> (CHM0005).
    /// Quantization at parse only.
    ///
    /// TARGETING: every default targets <c>Self</c> or <c>TargetUnit</c> ON PURPOSE — <c>GroundPoint</c>'s cast path
    /// was deferred in Story 2.4 (the command card supports Self/TargetUnit only), so a GroundPoint ability authors +
    /// validates fine but is not castable today. Keeping presets off GroundPoint keeps AC1's "castable in a match"
    /// true for every preset. (The raw-JSON escape hatch may still author GroundPoint — flagged in-UI.)
    /// </summary>
    public static class AbilityPresets
    {
        /// <summary>The closed preset registry (display order = authoring-suite dropdown order).</summary>
        public enum Kind : byte
        {
            /// <summary>Single-target magic damage on an enemy unit.</summary>
            TargetedDamage = 0,
            /// <summary>Flat self-heal.</summary>
            Heal = 1,
            /// <summary>Timed self attack-damage buff (apply_modifier).</summary>
            SelfBuff = 2,
            /// <summary>Cast on an enemy unit; magic damage to all enemies in a radius around it (search_area → damage).</summary>
            AoeNuke = 3,
        }

        /// <summary>Tunable numeric inputs for a preset. All fields are <c>Fixed</c>/<c>int</c> (no float keyword).
        /// A preset uses only the subset relevant to its <see cref="Kind"/> (e.g. <see cref="Radius"/> = AoE only,
        /// <see cref="DurationTicks"/> = Self Buff only); the editor shows the matching rows.</summary>
        public sealed class Params
        {
            public string Id { get; set; } = "new_ability";
            public string DisplayName { get; set; } = "New Ability";
            public Fixed CostEnergy { get; set; } = Fixed.Zero;
            public int CostOre { get; set; }
            public int CostCrystal { get; set; }
            public Fixed Cooldown { get; set; } = Fixed.Zero;
            /// <summary>Primary magnitude — damage / heal amount, or the Self Buff's attack-damage delta.</summary>
            public Fixed Amount { get; set; } = Fixed.Zero;
            /// <summary>AoE radius (AoE Nuke only).</summary>
            public Fixed Radius { get; set; } = Fixed.Zero;
            /// <summary>Buff duration in ticks (Self Buff only; &lt;0 = permanent, 0 = one-shot).</summary>
            public int DurationTicks { get; set; }
        }

        /// <summary>The closed preset list for the editor dropdown (stable order).</summary>
        public static readonly (Kind Kind, string Label)[] All =
        {
            (Kind.TargetedDamage, "Targeted Damage"),
            (Kind.Heal,           "Heal"),
            (Kind.SelfBuff,       "Self Buff"),
            (Kind.AoeNuke,        "AoE Nuke"),
        };

        /// <summary>Sensible starting values for a preset (also the known-good Tier-1 inputs). Magnitudes mirror the
        /// shipped sample shapes (fireball/minor_heal/battle_fury) without claiming sample parity.</summary>
        public static Params Defaults(Kind kind) => kind switch
        {
            Kind.TargetedDamage => new Params
            {
                Id = "new_targeted_damage", DisplayName = "Targeted Damage",
                CostEnergy = Fixed.FromInt(50), Cooldown = Fixed.FromInt(6),
                Amount = Fixed.FromInt(80),
            },
            Kind.Heal => new Params
            {
                Id = "new_heal", DisplayName = "Heal",
                CostEnergy = Fixed.FromInt(20), Cooldown = Fixed.FromInt(3),
                Amount = Fixed.FromInt(40),
            },
            Kind.SelfBuff => new Params
            {
                Id = "new_self_buff", DisplayName = "Self Buff",
                CostEnergy = Fixed.FromInt(35), Cooldown = Fixed.FromInt(12),
                Amount = Fixed.FromInt(12),    // attack_damage_delta
                DurationTicks = 150,
            },
            Kind.AoeNuke => new Params
            {
                Id = "new_aoe_nuke", DisplayName = "AoE Nuke",
                CostEnergy = Fixed.FromInt(60), Cooldown = Fixed.FromInt(8),
                Amount = Fixed.FromInt(30), Radius = Fixed.FromInt(4),
            },
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown ability preset kind."),
        };

        /// <summary>Default modifier identity for the Self Buff preset. A stable, deterministic constant (never a
        /// non-deterministic string hash). Creators needing a specific id edit it via the raw-JSON escape hatch.</summary>
        private const int SelfBuffModifierId = 1;

        /// <summary>Build the in-memory <see cref="AbilityDefinition"/> for a preset + its tuned params. Pure; produces
        /// a graph that passes <see cref="AbilityValidator"/> for any non-negative costs/cooldown and a real effect.</summary>
        public static AbilityDefinition Build(Kind kind, Params p) => kind switch
        {
            Kind.TargetedDamage => new AbilityDefinition
            {
                Id = p.Id, DisplayName = p.DisplayName, Targeting = "TargetUnit",
                CostEnergy = p.CostEnergy, CostOre = p.CostOre, CostCrystal = p.CostCrystal, Cooldown = p.Cooldown,
                EffectGraph = new DamageEffect(p.Amount, DamageType.Magic),
            },
            Kind.Heal => new AbilityDefinition
            {
                Id = p.Id, DisplayName = p.DisplayName, Targeting = "Self",
                CostEnergy = p.CostEnergy, CostOre = p.CostOre, CostCrystal = p.CostCrystal, Cooldown = p.Cooldown,
                EffectGraph = new HealEffect(p.Amount),
            },
            Kind.SelfBuff => new AbilityDefinition
            {
                Id = p.Id, DisplayName = p.DisplayName, Targeting = "Self",
                CostEnergy = p.CostEnergy, CostOre = p.CostOre, CostCrystal = p.CostCrystal, Cooldown = p.Cooldown,
                EffectGraph = new ApplyModifierEffect(new Modifier(
                    id: SelfBuffModifierId,
                    durationTicks: p.DurationTicks,
                    stacking: StackRule.Refresh,
                    maxStacks: 1,
                    maxHealthDelta: Fixed.Zero,
                    attackDamageDelta: p.Amount,
                    moveSpeedDelta: Fixed.Zero,
                    status: StatusFlags.None,
                    periodEffect: null,
                    periodTicks: 0)),
            },
            Kind.AoeNuke => new AbilityDefinition
            {
                Id = p.Id, DisplayName = p.DisplayName, Targeting = "TargetUnit",
                CostEnergy = p.CostEnergy, CostOre = p.CostOre, CostCrystal = p.CostCrystal, Cooldown = p.Cooldown,
                EffectGraph = new SearchAreaEffect(p.Radius, TargetFilter.Enemy,
                    new DamageEffect(p.Amount, DamageType.Magic)),
            },
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown ability preset kind."),
        };

        /// <summary>Convenience: build a preset with its default params (initial editor render + Tier-1 seed).</summary>
        public static AbilityDefinition BuildDefault(Kind kind) => Build(kind, Defaults(kind));
    }
}
