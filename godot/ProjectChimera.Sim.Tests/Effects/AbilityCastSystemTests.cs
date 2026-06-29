#nullable enable
using ProjectChimera.Core;
using ProjectChimera.Multiplayer;
using Xunit;

namespace ProjectChimera.Sim.Tests.Effects
{
    /// <summary>
    /// Story 2.4a (AC2/AC6) — the full cast pipeline against a bare world: a Self ApplyModifier cast (battle_fury)
    /// installs the modifier + debits energy + starts the cooldown; a TargetUnit damage cast (fireball) damages the
    /// CHOSEN target; an unknown slot is a deterministic no-op; and a cast never clobbers the caster's order.
    /// </summary>
    public class AbilityCastSystemTests
    {
        private static FixedVec3 V(int x, int y, int z) => new FixedVec3(Fixed.FromInt(x), Fixed.FromInt(y), Fixed.FromInt(z));

        [Fact]
        public void SelfCast_BattleFury_InstallsModifier_DebitsEnergy_StartsCooldown()
        {
            var h = new CastHarness(AbilityTestAbilities.BattleFury());
            int c = h.Caster("battle_fury", energy: 50);
            Fixed baseAtk = h.World.EffectiveAttackDamage[c]; // 0 (Create default)

            h.IssueAndTick(c, -1);

            Assert.Equal(Fixed.FromInt(15).Raw, h.World.Energy[c].Raw);   // 50 - 35
            Assert.Equal(360, h.Cooldown(c));                            // 12s * 30
            Assert.Equal(1, h.Modifiers.CountAt(c));                     // one active modifier instance installed
            // The +12 attack modifier is recomputed eagerly on apply (ModifierStore.ApplyStatDeltas → RecomputeEntity).
            Assert.Equal((baseAtk + Fixed.FromInt(12)).Raw, h.World.EffectiveAttackDamage[c].Raw);
        }

        [Fact]
        public void TargetUnitCast_Fireball_DamagesTheChosenTarget()
        {
            var h = new CastHarness(AbilityTestAbilities.FireballSingle());
            int caster = h.Caster("fireball", energy: 50, pos: V(0, 0, 0));
            int target = h.World.Create(V(3, 0, 0), Faction.Player2, Fixed.FromInt(200), Fixed.FromInt(3));
            Fixed before = h.World.Health[target];

            h.IssueAndTick(caster, target);

            Assert.True(h.World.Health[target] < before, "Fireball must damage the chosen TargetUnit.");
            Assert.Equal(Fixed.Zero.Raw, h.World.Energy[caster].Raw); // 50 - 50
            Assert.Equal(180, h.Cooldown(caster));                    // 6s * 30
        }

        [Fact]
        public void CastForUnknownSlot_IsADeterministicNoOp()
        {
            var h = new CastHarness(AbilityTestAbilities.MinorHeal());
            int c = h.Caster("minor_heal", energy: 50);

            // Slot 2 does not exist (the unit has only slot 0). OrderApplier guards slot < AbilityCount → no intent set.
            OrderApplier.Apply(h.World, new UnitOrder(c, UnitCommand.CastAbility, Fixed.FromRaw(2), Fixed.FromRaw(-1)),
                Faction.Player1);
            Assert.Equal(EntityWorld.NO_PENDING_CAST, h.World.PendingCastSlot[c]); // OrderApplier rejected the bad slot

            h.Cast.Tick(h.World, SimulationLoop.FixedDt);
            Assert.Equal(Fixed.FromInt(50).Raw, h.World.Energy[c].Raw); // nothing was cast / debited
            Assert.Equal(0, h.Cooldown(c));
        }

        [Fact]
        public void TargetUnitCast_AtDeadTarget_RefusesAtomically_NoSelfHarm()
        {
            // Regression (code review 2026-06-28): a TargetUnit offensive cast whose target died before the intent is
            // consumed must be an atomic no-op — NOT redirected onto the caster. The previous dead-target fallback
            // shared the Self branch and nuked the caster AFTER the cost was already debited.
            var h = new CastHarness(AbilityTestAbilities.FireballSingle());
            int caster = h.Caster("fireball", energy: 50, pos: V(0, 0, 0));
            int target = h.World.Create(V(3, 0, 0), Faction.Player2, Fixed.FromInt(200), Fixed.FromInt(3));
            h.World.Destroy(target); // dies in the lockstep input-delay window, before this tick consumes the cast

            h.IssueAndTick(caster, target);

            Assert.Equal(Fixed.FromInt(100).Raw, h.World.Health[caster].Raw); // caster un-self-harmed
            Assert.Equal(Fixed.FromInt(50).Raw,  h.World.Energy[caster].Raw);  // energy NOT debited (atomic refuse)
            Assert.Equal(0, h.Cooldown(caster));                              // cooldown NOT started
            Assert.Equal(EntityWorld.NO_PENDING_CAST, h.World.PendingCastSlot[caster]); // intent still consumed (one-shot)
        }

        [Fact]
        public void TargetUnitCast_WithNoTarget_RefusesAtomically()
        {
            // A TargetUnit cast carrying target -1 (no unit chosen) is unfulfillable → atomic no-op, not a self-cast.
            var h = new CastHarness(AbilityTestAbilities.FireballSingle());
            int caster = h.Caster("fireball", energy: 50, pos: V(0, 0, 0));

            h.IssueAndTick(caster, -1);

            Assert.Equal(Fixed.FromInt(100).Raw, h.World.Health[caster].Raw);
            Assert.Equal(Fixed.FromInt(50).Raw,  h.World.Energy[caster].Raw);
            Assert.Equal(0, h.Cooldown(caster));
        }

        [Fact]
        public void GroundPointCast_IsRefused_NotSelfCast()
        {
            // AC6 fence: GroundPoint casting is out of scope for 2.4a. A GroundPoint ability must refuse as a no-op
            // (there is no ground (x,z) plumbing) — it must NOT fall through to a self-cast.
            var h = new CastHarness(AbilityTestAbilities.GroundPointDamage());
            int caster = h.Caster("ground_nuke", energy: 50, pos: V(0, 0, 0));

            h.IssueAndTick(caster, -1);

            Assert.Equal(Fixed.FromInt(100).Raw, h.World.Health[caster].Raw); // no self-damage
            Assert.Equal(Fixed.FromInt(50).Raw,  h.World.Energy[caster].Raw);  // no spend
            Assert.Equal(0, h.Cooldown(caster));                              // no cooldown
        }

        [Fact]
        public void Cast_PreservesTheCasterCommandState()
        {
            var h = new CastHarness(AbilityTestAbilities.BattleFury());
            int c = h.Caster("battle_fury", energy: 50);
            h.World.CommandState[c] = UnitCommand.Move; // the unit is mid-move

            // The cast intent must NOT clobber the Move order (OrderApplier restores CommandState to its prior value).
            OrderApplier.Apply(h.World, new UnitOrder(c, UnitCommand.CastAbility, Fixed.FromRaw(0), Fixed.FromRaw(-1)),
                Faction.Player1);

            Assert.Equal(UnitCommand.Move, h.World.CommandState[c]);
            Assert.Equal((byte)0, h.World.PendingCastSlot[c]); // but the cast intent WAS queued (slot 0)
        }
    }
}
