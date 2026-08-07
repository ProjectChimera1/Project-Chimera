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
            // Story 15.11: the ability slot now rides the UnitOrder's dedicated byte (5th ctor arg), not TargetX.
            OrderApplier.Apply(h.World, new UnitOrder(c, UnitCommand.CastAbility, Fixed.Zero, Fixed.FromRaw(-1), slot: 2),
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
        public void GroundPointCast_NowResolves_SpendsAndStartsCooldown()
        {
            // Story 15.11 (DW-280): the 2.4a AC6 fence is REMOVED — GroundPoint casting is supported. A GroundPoint cast
            // resolves at the wire-supplied ground point (stored mode-agnostically into PendingCastPointX/Z by
            // OrderApplier, read by AbilityCastSystem's GroundPoint branch), so it now spends its cost and starts its
            // cooldown rather than refusing as a no-op. (ground_nuke here is a bare Damage leaf and a ground cast leaves
            // the primary target INVALID (-1, review P3) — NOT the caster — so the leaf no-ops on the IsAlive(-1) guard;
            // a real ground nuke uses a SearchArea centred on the point. See GroundCast_SearchArea_… for the AoE path.)
            var h = new CastHarness(AbilityTestAbilities.GroundPointDamage());
            int caster = h.Caster("ground_nuke", energy: 50, pos: V(0, 0, 0));

            h.IssueAndTick(caster, -1);

            Assert.Equal(Fixed.FromInt(50 - 40).Raw, h.World.Energy[caster].Raw); // ground_nuke's 40 energy WAS spent
            Assert.True(h.Cooldown(caster) > 0);                                  // the cooldown started (cast resolved)
            Assert.Equal(Fixed.FromInt(100).Raw, h.World.Health[caster].Raw);     // caster NOT self-harmed by the bare leaf (P3: primary target is -1, not the caster)
        }

        [Fact]
        public void GroundCast_SearchArea_DamagesAtThePoint_NotTheCaster()
        {
            // Story 15.11 (review P4a/P3): a real GroundPoint AoE (SearchArea centred on the clicked point) damages the
            // clustered dummies AT the point and leaves the caster's HP UNCHANGED (the primary target is invalid for a
            // ground cast, so nothing resolves on the caster). Uses a SearchArea ability, not a bare Damage leaf.
            var h = new CastHarness(AbilityTestAbilities.GroundPointNuke());
            int caster = h.Caster("ground_nuke_aoe", energy: 50, pos: V(0, 0, 0));
            int d1 = h.Target(200, V(10, 0, 0));  // clustered at the ground point, inside the 4u radius
            int d2 = h.Target(200, V(11, 0, 1));  // caster is ~10u away → outside the radius
            Fixed casterHpBefore = h.World.Health[caster];

            // GroundPoint cast: slot 0 in the wire byte, the ground point (10,0,0) in TargetX/TargetZ.
            OrderApplier.Apply(h.World,
                new UnitOrder(caster, UnitCommand.CastAbility, Fixed.FromInt(10), Fixed.FromInt(0), slot: 0),
                Faction.Player1);
            h.Cast.Tick(h.World, SimulationLoop.FixedDt);

            Assert.Equal(casterHpBefore.Raw, h.World.Health[caster].Raw);     // caster NOT self-harmed (P3)
            Assert.True(h.World.Health[d1] < Fixed.FromInt(200), "dummy at the ground point should have taken damage");
            Assert.True(h.World.Health[d2] < Fixed.FromInt(200), "second clustered dummy should have taken damage");
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
