#nullable enable
using ProjectChimera.Combat;            // DamageType
using ProjectChimera.Core;              // EntityWorld, Fixed, FixedVec3, Faction, ResourceStore, SimulationLoop
using ProjectChimera.Core.Definitions;  // AbilityDefinition, AbilityRegistry
using ProjectChimera.Effects;           // ModifierSystem, ModifierStore, AbilityCastSystem, Modifier, *Effect, StatusFlags
using ProjectChimera.Multiplayer;       // OrderApplier, UnitOrder

namespace ProjectChimera.Sim.Tests.Effects
{
    /// <summary>
    /// In-code ability definitions for the Story 2.4a tests + the ability-cast golden, mirroring the shipped sample
    /// JSONs (battle_fury / minor_heal / fireball) but built deterministically in code so tests stay hermetic (no
    /// filesystem). Values match the JSONs so the tests double as a sanity check on the sample data.
    /// </summary>
    internal static class AbilityTestAbilities
    {
        /// <summary>battle_fury: Self, 35 energy, 12s cd, ApplyModifier(+12 atk, +1 move, 150-tick Refresh).</summary>
        public static AbilityDefinition BattleFury() => new AbilityDefinition
        {
            Id = "battle_fury", DisplayName = "Battle Fury", Targeting = "Self",
            CostEnergy = Fixed.FromInt(35), Cooldown = Fixed.FromInt(12),
            // Modifier ctor order (matches ModifierScenario): id, durationTicks, stacking, maxStacks,
            // maxHealthDelta, attackDamageDelta, moveSpeedDelta, status, periodEffect, periodTicks.
            EffectGraph = new ApplyModifierEffect(new Modifier(
                1001, 150, StackRule.Refresh, 1, Fixed.Zero, Fixed.FromInt(12), Fixed.FromInt(1),
                StatusFlags.None, null, 0)),
        };

        /// <summary>minor_heal: Self, 20 energy, 3s cd, Heal 40.</summary>
        public static AbilityDefinition MinorHeal() => new AbilityDefinition
        {
            Id = "minor_heal", DisplayName = "Minor Heal", Targeting = "Self",
            CostEnergy = Fixed.FromInt(20), Cooldown = Fixed.FromInt(3),
            EffectGraph = new HealEffect(Fixed.FromInt(40)),
        };

        /// <summary>fireball-like: TargetUnit, 50 energy, 6s cd, Damage 80 Magic (single-target form — the simple
        /// leaf, no SearchArea — so a TargetUnit pipeline test can assert a tight damage delta on one target).</summary>
        public static AbilityDefinition FireballSingle() => new AbilityDefinition
        {
            Id = "fireball", DisplayName = "Fireball", Targeting = "TargetUnit",
            CostEnergy = Fixed.FromInt(50), Cooldown = Fixed.FromInt(6),
            EffectGraph = new DamageEffect(Fixed.FromInt(80), DamageType.Magic),
        };

        /// <summary>A GroundPoint Damage ability — the AC6 out-of-scope fence. Used to prove a GroundPoint cast
        /// refuses as a no-op (no ground (x,z) plumbing in 2.4a) rather than falling through to a self-cast.</summary>
        public static AbilityDefinition GroundPointDamage() => new AbilityDefinition
        {
            Id = "ground_nuke", DisplayName = "Ground Nuke", Targeting = "GroundPoint",
            CostEnergy = Fixed.FromInt(40), Cooldown = Fixed.FromInt(8),
            EffectGraph = new DamageEffect(Fixed.FromInt(80), DamageType.Magic),
        };

        /// <summary>
        /// matter_infusion (DW-221) — the SHIPPED worker signature ability, mirrored field-for-field from
        /// <c>resources/data/abilities/matter_infusion.json</c>: Self, 15 energy / 15 ore / 10 crystal, 20s cooldown,
        /// <c>apply_modifier</c> of modifier id 1002 — 90 ticks, Refresh, max_stacks 1, <c>move_speed_delta: 1</c>,
        /// status None, no period. Distinct from <see cref="SelfHeal"/>, which only borrowed matter_infusion's COSTS
        /// while running a HealEffect — so the real apply_modifier / move-speed path had no worker-cast coverage.
        /// Values match the JSON, so these tests double as a sanity check on the shipped data.
        /// (Modifier ctor order matches BattleFury.)
        /// </summary>
        public static AbilityDefinition MatterInfusion() => new AbilityDefinition
        {
            Id = "matter_infusion", DisplayName = "Matter Infusion", Targeting = "Self",
            CostEnergy = Fixed.FromInt(15), CostOre = 15, CostCrystal = 10,
            Cooldown = Fixed.FromInt(20),
            EffectGraph = new ApplyModifierEffect(new Modifier(
                1002, 90, StackRule.Refresh, 1, Fixed.Zero, Fixed.Zero, Fixed.FromInt(1),
                StatusFlags.None, null, 0)),
        };

        /// <summary>A custom Self heal with explicit costs/cooldown for the affordability + cooldown-boundary tests.</summary>
        public static AbilityDefinition SelfHeal(int costEnergy, int costOre, int costCrystal, int cooldownSec, int heal) =>
            new AbilityDefinition
            {
                Id = "test_heal", DisplayName = "Test Heal", Targeting = "Self",
                CostEnergy = Fixed.FromInt(costEnergy), CostOre = costOre, CostCrystal = costCrystal,
                Cooldown = Fixed.FromInt(cooldownSec), EffectGraph = new HealEffect(Fixed.FromInt(heal)),
            };

        /// <summary>equal_exchange (Story 2.10; Story 2.13 self-cost migration): the Equal Exchange VITALITY-price
        /// shape — Self, no matter cost, a beneficial +15 atk self-buff (120-tick Refresh) plus a FLAT −25 HP self-cost.
        /// Story 2.13 (D-4) moves that self-cost from a graph <c>direct_hp_delta(-25)</c> leaf to the first-class
        /// <c>cost_health: 25</c> field (one source of truth; <c>allow_self_lethal: false</c> so a would-be-lethal cast
        /// is refused). The tick-1 Health 100→75 drop is UNCHANGED (byte-identical golden). Mirrors the shipped
        /// spike_transmutation.json. (Modifier ctor order matches BattleFury.)</summary>
        public static AbilityDefinition EqualExchange() => new AbilityDefinition
        {
            Id = "equal_exchange", DisplayName = "Equal Exchange", Targeting = "Self",
            CostEnergy = Fixed.Zero, Cooldown = Fixed.FromInt(10),
            CostHealth = 25, AllowSelfLethal = false, // Story 2.13: self-cost migrated from the direct_hp_delta leaf
            EffectGraph = new ApplyModifierEffect(new Modifier(
                1100, 120, StackRule.Refresh, 1, Fixed.Zero, Fixed.FromInt(15), Fixed.Zero,
                StatusFlags.None, null, 0)),
        };
    }

    /// <summary>
    /// A minimally-wired ability-cast environment: an <see cref="EntityWorld"/>, a <see cref="ResourceStore"/>, a
    /// <see cref="ModifierSystem"/>+<see cref="ModifierStore"/> (attached, for ApplyModifier casts), an
    /// <see cref="AbilityRegistry"/>, and the <see cref="AbilityCastSystem"/> under test. Mirrors the production
    /// wiring (<see cref="ProjectChimera.Core.Sim.SimulationHost"/>) but Godot-free and host-free so the cast spine
    /// is unit-testable in isolation. Casts are issued through the SHARED <see cref="OrderApplier"/> (the real
    /// intent path), then one <see cref="AbilityCastSystem.Tick"/> consumes them.
    /// </summary>
    internal sealed class CastHarness
    {
        public readonly EntityWorld World;
        public readonly ResourceStore Resources;
        public readonly ModifierSystem ModSys;
        public readonly ModifierStore Modifiers;
        public readonly AbilityRegistry Registry;
        public readonly AbilityCastSystem Cast;

        public CastHarness(params AbilityDefinition[] abilities)
        {
            World     = new EntityWorld();
            Resources = new ResourceStore(Fixed.Zero);
            ModSys    = new ModifierSystem();
            Modifiers = new ModifierStore(World, ModSys);
            ModSys.AttachStore(Modifiers);
            Registry  = new AbilityRegistry(abilities);
            Cast      = new AbilityCastSystem(Registry, Resources, Modifiers);
        }

        /// <summary>Create a P1 caster with ability <paramref name="abilityId"/> in slot 0 and a full energy pool.</summary>
        public int Caster(string abilityId, int energy, FixedVec3 pos = default)
        {
            int id = World.Create(pos, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            World.AbilityId[id * EntityWorld.MAX_ABILITIES_PER_UNIT + 0] = Registry.IndexOf(abilityId);
            World.AbilityCount[id] = 1;
            World.MaxEnergy[id] = Fixed.FromInt(energy);
            World.Energy[id]    = Fixed.FromInt(energy);
            return id;
        }

        /// <summary>Create a plain Neutral target unit (an enemy of Player1) with the given health.</summary>
        public int Target(int health, FixedVec3 pos = default)
            => World.Create(pos, Faction.Neutral, Fixed.FromInt(health), Fixed.FromInt(3));

        /// <summary>Issue a slot-0 cast intent through the shared OrderApplier, then tick the cast system once.</summary>
        public void IssueAndTick(int casterId, int targetId)
        {
            OrderApplier.Apply(World, new UnitOrder(casterId, UnitCommand.CastAbility,
                Fixed.FromRaw(0), Fixed.FromRaw(targetId)), Faction.Player1);
            Cast.Tick(World, SimulationLoop.FixedDt);
        }

        /// <summary>Advance the cast system <paramref name="n"/> ticks with no new intent (cooldown countdown).</summary>
        public void TickCast(int n)
        {
            for (int t = 0; t < n; t++) Cast.Tick(World, SimulationLoop.FixedDt);
        }

        public int Cooldown(int casterId, int slot = 0)
            => World.AbilityCooldownTicks[casterId * EntityWorld.MAX_ABILITIES_PER_UNIT + slot];
    }
}
