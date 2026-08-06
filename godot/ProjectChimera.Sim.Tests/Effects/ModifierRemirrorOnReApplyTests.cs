#nullable enable
using ProjectChimera.Core;              // EntityWorld, Fixed, FixedVec3, Faction, FactionRegistry
using ProjectChimera.Core.Definitions;  // AbilityRegistry, FactionDefinition, UnitDefinition
using ProjectChimera.Core.Sim;          // SimulationHost, NullLogSink
using ProjectChimera.Effects;           // Modifier, StackRule, StatusFlags
using Xunit;

namespace ProjectChimera.Sim.Tests.Effects
{
    /// <summary>
    /// DW-659 — a live re-<c>ApplyUnitDefinition</c> must NOT wipe an installed modifier's contribution to
    /// <c>Effective*</c> for a unit that carries no self-passive.
    ///
    /// <para><b>The defect.</b> <see cref="EntityWorld.ApplyUnitDefinition"/> re-mirrors
    /// <c>BaseAttackDamage</c>/<c>BaseArmor</c> into <c>EffectiveAttackDamage</c>/<c>EffectiveArmor</c>, discarding
    /// every installed modifier's contribution, and <c>ModifierSystem.Tick</c> only recomputes entities something
    /// DIRTIED — so a unit carrying a research / item / aura modifier and NO self-passive silently lost the bonus on
    /// a live re-apply until an unrelated apply/remove happened to re-dirty it. DW-300 closed only the guarded
    /// self-passive path (<c>InstallSelfPassive</c>'s duplicate-skip calls <c>RecomputeEffectiveStats</c>); the
    /// general fix wires <c>ModifierStore.RecomputeEffectiveStats</c> as a THIRD
    /// <see cref="EntityWorld.OnUnitDefinitionApplied"/> subscriber in <see cref="SimulationHost"/>, so the re-mirror
    /// is always followed by <c>Effective = max(0, Base + Σ bonus)</c>.</para>
    ///
    /// <para>These tests drive the REAL host wiring (never a hand-rolled subscriber) so they fail if the third
    /// subscription is dropped, mis-ordered ahead of the two installers, or wired to the wrong seam.</para>
    /// </summary>
    public class ModifierRemirrorOnReApplyTests
    {
        /// <summary>An id no shipped modifier uses (research ids are 0x3439_00xx; the passive fixtures use 2001/2002).</summary>
        private const int ExternalModifierId = 6590;

        private const int DefAttackDamage = 10;   // UnitDefinition.AttackDamage → BaseAttackDamage
        private const int DefArmor        = 2;    // UnitDefinition.Armor        → BaseArmor
        private const int SpawnHp         = 100;  // Create arg                  → BaseMaxHealth
        private const int SpawnSpeed      = 3;    // Create arg                  → BaseMoveSpeed

        private const int BonusAttackDamage = 7;
        private const int BonusArmor        = 3;
        private const int BonusMaxHealth    = 25;
        private const int BonusMoveSpeed    = 1;

        private static FixedVec3 V(int x, int y, int z) =>
            new FixedVec3(Fixed.FromInt(x), Fixed.FromInt(y), Fixed.FromInt(z));

        /// <summary>A fully-wired host with an EMPTY ability registry — no auras, no self-passives, no research.</summary>
        private static SimulationHost NewHost()
        {
            var host = SimulationHost.Create(NullLogSink.Instance, new FactionRegistry(2),
                                             new FactionDefinition(), new FactionDefinition(),
                                             registry: AbilityRegistry.Empty);
            host.ChecksumInterval = 1;
            return host;
        }

        /// <summary>A def with NO abilities → <c>SelfPassiveAbilityIndex</c> stays −1, so the DW-300 guarded path
        /// (the only pre-existing repair of the re-mirror) can never fire for this unit.</summary>
        private static UnitDefinition PassiveFreeDef() => new UnitDefinition
        {
            Id = "plain_soldier", DisplayName = "Plain Soldier", Category = "Melee",
            Hp = SpawnHp, Speed = SpawnSpeed, AttackDamage = DefAttackDamage, Armor = DefArmor,
        };

        /// <summary>A PERMANENT, non-stacking external buff — the research / item / hero-growth shape (an
        /// <c>ApplyModifier</c> that is NOT installed by the unit's own while-alive passive).</summary>
        private static Modifier ExternalBuff() => new Modifier(
            ExternalModifierId, -1, StackRule.Ignore, 1,
            maxHealthDelta:    Fixed.FromInt(BonusMaxHealth),
            attackDamageDelta: Fixed.FromInt(BonusAttackDamage),
            moveSpeedDelta:    Fixed.FromInt(BonusMoveSpeed),
            status: StatusFlags.None, periodEffect: null, periodTicks: 0,
            armorDelta: Fixed.FromInt(BonusArmor));

        [Fact]
        public void LiveReApply_KeepsAnExternalModifiersEffectiveStats_OnAUnitWithNoSelfPassive()
        {
            SimulationHost host = NewHost();
            EntityWorld w = host.World;
            UnitDefinition def = PassiveFreeDef();

            int unit = w.Create(V(0, 0, 0), Faction.Player1, Fixed.FromInt(SpawnHp), Fixed.FromInt(SpawnSpeed));
            w.ApplyUnitDefinition(unit, def);
            Assert.Equal(-1, w.SelfPassiveAbilityIndex[unit]); // the DW-300 repair path is unreachable for this unit

            Assert.True(host.Modifiers.Apply(unit, ExternalBuff(), casterId: unit, casterFaction: Faction.Player1));

            // The store recomputes eagerly on apply, so the buffed stats are live BEFORE the re-apply.
            Fixed expectedDamage = Fixed.FromInt(DefAttackDamage + BonusAttackDamage);
            Fixed expectedArmor  = Fixed.FromInt(DefArmor + BonusArmor);
            Fixed expectedMaxHp  = Fixed.FromInt(SpawnHp + BonusMaxHealth);
            Fixed expectedSpeed  = Fixed.FromInt(SpawnSpeed + BonusMoveSpeed);
            Assert.Equal(expectedDamage.Raw, w.EffectiveAttackDamage[unit].Raw);
            Assert.Equal(expectedArmor.Raw,  w.EffectiveArmor[unit].Raw);

            // The defect: an in-place re-apply (upgrade / morph / tech re-map / editor restore) re-mirrors Base* over
            // Effective* and nothing dirties the entity, so pre-fix the bonus vanished for good.
            w.ApplyUnitDefinition(unit, def);

            Assert.Equal(expectedDamage.Raw, w.EffectiveAttackDamage[unit].Raw); // pre-fix: DefAttackDamage (bonus lost)
            Assert.Equal(expectedArmor.Raw,  w.EffectiveArmor[unit].Raw);        // pre-fix: DefArmor (bonus lost)
            // The mapper never writes these two, so they must be reproduced IDENTICALLY — never dropped and never
            // double-counted by the new recompute.
            Assert.Equal(expectedMaxHp.Raw,  w.EffectiveMaxHealth[unit].Raw);
            Assert.Equal(expectedSpeed.Raw,  w.EffectiveMoveSpeed[unit].Raw);

            // And it must STAY repaired: the entity is clean, so no later tick re-derives it for us.
            Assert.Equal(1, host.Modifiers.CountAt(unit)); // the re-apply installed nothing (no self-passive)
            for (int i = 0; i < 5; i++) host.StepOnce();
            Assert.Equal(expectedDamage.Raw, w.EffectiveAttackDamage[unit].Raw);
            Assert.Equal(expectedArmor.Raw,  w.EffectiveArmor[unit].Raw);
        }

        [Fact]
        public void GenuineSpawn_WithNoModifiers_IsUnperturbedByTheSeamRecompute()
        {
            // The seam recompute fires on EVERY def-based spawn, so its no-modifier result must be exactly the
            // Base*→Effective* mirror the mapper already wrote (Base + 0). This is the property the re-baseline
            // differential guard depends on; assert it directly rather than inferring it from golden movement.
            SimulationHost host = NewHost();
            EntityWorld w = host.World;

            int unit = w.Create(V(4, 0, 0), Faction.Player1, Fixed.FromInt(SpawnHp), Fixed.FromInt(SpawnSpeed));
            w.ApplyUnitDefinition(unit, PassiveFreeDef());

            Assert.Equal(0, host.Modifiers.CountAt(unit));
            Assert.Equal(w.BaseAttackDamage[unit].Raw, w.EffectiveAttackDamage[unit].Raw);
            Assert.Equal(w.BaseArmor[unit].Raw,        w.EffectiveArmor[unit].Raw);
            Assert.Equal(w.BaseMaxHealth[unit].Raw,    w.EffectiveMaxHealth[unit].Raw);
            Assert.Equal(w.BaseMoveSpeed[unit].Raw,    w.EffectiveMoveSpeed[unit].Raw);
            Assert.Equal(Fixed.FromInt(DefAttackDamage).Raw, w.EffectiveAttackDamage[unit].Raw);
            Assert.Equal(Fixed.FromInt(DefArmor).Raw,        w.EffectiveArmor[unit].Raw);
        }

        [Fact]
        public void SeamRecompute_RunsAfterTheInstallers_SoASelfPassiveSpawnStillLandsExactlyOneStack()
        {
            // Ordering teeth: the recompute is the THIRD subscriber. Wired FIRST it would run before
            // InstallSelfPassive / ApplyCompletedResearch and leave the re-mirror unrepaired for exactly the entities
            // whose installers no-op'd — i.e. the DW-659 population — while still looking correct on a fresh spawn.
            var reg = new AbilityRegistry(new[] { PassiveTestAbilities.IronSkin() });
            var host = SimulationHost.Create(NullLogSink.Instance, new FactionRegistry(2),
                                             new FactionDefinition(), new FactionDefinition(), registry: reg);
            EntityWorld w = host.World;

            var def = new UnitDefinition { Id = "ironclad", DisplayName = "Ironclad", Category = "Melee",
                                           Hp = SpawnHp, Speed = SpawnSpeed, AttackDamage = DefAttackDamage,
                                           Armor = DefArmor, Abilities = new[] { "iron_skin" } };
            def.ResolveAbilities(reg);

            int unit = w.Create(V(0, 0, 0), Faction.Player1, Fixed.FromInt(SpawnHp), Fixed.FromInt(SpawnSpeed));
            w.ApplyUnitDefinition(unit, def);

            // One install, and its armor is already live at the END of the seam (no tick needed) — which is only true
            // if the recompute ran AFTER the installer.
            Assert.Equal(1, host.Modifiers.CountAt(unit));
            Assert.Equal(Fixed.FromInt(DefArmor + PassiveTestAbilities.IronSkinArmorPerStack).Raw,
                         w.EffectiveArmor[unit].Raw);

            // A live re-apply is still exactly one stack (DW-300) AND still carries its armor (DW-659).
            for (int i = 0; i < 3; i++) w.ApplyUnitDefinition(unit, def);
            Assert.Equal(1, host.Modifiers.CountAt(unit));
            Assert.Equal(1, host.Modifiers.StackCountAt(unit, 0));
            Assert.Equal(Fixed.FromInt(DefArmor + PassiveTestAbilities.IronSkinArmorPerStack).Raw,
                         w.EffectiveArmor[unit].Raw);
        }
    }
}
