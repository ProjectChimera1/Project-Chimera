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

        /// <summary>A custom Self heal with explicit costs/cooldown for the affordability + cooldown-boundary tests.</summary>
        public static AbilityDefinition SelfHeal(int costEnergy, int costOre, int costCrystal, int cooldownSec, int heal) =>
            new AbilityDefinition
            {
                Id = "test_heal", DisplayName = "Test Heal", Targeting = "Self",
                CostEnergy = Fixed.FromInt(costEnergy), CostOre = costOre, CostCrystal = costCrystal,
                Cooldown = Fixed.FromInt(cooldownSec), EffectGraph = new HealEffect(Fixed.FromInt(heal)),
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
