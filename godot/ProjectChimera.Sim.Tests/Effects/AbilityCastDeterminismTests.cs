#nullable enable
using ProjectChimera.Core;
using Xunit;

namespace ProjectChimera.Sim.Tests.Effects
{
    /// <summary>
    /// Story 2.4a (AC3) — the cast resolves DETERMINISTICALLY. Two identical casts on two fresh identical worlds
    /// must hash identically (the same-machine determinism floor); a different ability in the slot must DIVERGE the
    /// hash (the non-vacuous negative control — proves the fold actually reflects what was cast). This is the
    /// desync-detection surface for the cast spine: a peer that cast something else must be detectable.
    /// </summary>
    public class AbilityCastDeterminismTests
    {
        private static uint Hash(CastHarness h) =>
            SimChecksum.Compute(h.World, new BuildingStore(), h.Resources, new FactionRegistry(2), h.Modifiers);

        [Fact]
        public void IdenticalSelfCasts_ProduceEqualChecksums()
        {
            var a = new CastHarness(AbilityTestAbilities.BattleFury());
            int ca = a.Caster("battle_fury", energy: 50);
            a.IssueAndTick(ca, targetId: -1);

            var b = new CastHarness(AbilityTestAbilities.BattleFury());
            int cb = b.Caster("battle_fury", energy: 50);
            b.IssueAndTick(cb, targetId: -1);

            Assert.Equal(Hash(a), Hash(b));
        }

        [Fact]
        public void DifferentAbilityInSlot_DivergesTheChecksum()
        {
            var a = new CastHarness(AbilityTestAbilities.BattleFury(), AbilityTestAbilities.MinorHeal());
            int ca = a.Caster("battle_fury", energy: 50);   // 35 energy, 12s cd, +12 atk modifier
            a.IssueAndTick(ca, -1);

            var b = new CastHarness(AbilityTestAbilities.BattleFury(), AbilityTestAbilities.MinorHeal());
            int cb = b.Caster("minor_heal", energy: 50);    // 20 energy, 3s cd, heal (no modifier)
            b.IssueAndTick(cb, -1);

            // Different energy debit (15 vs 30), cooldown (360 vs 90), and modifier-instance count (1 vs 0) — all
            // folded — so the post-cast world states MUST hash differently.
            Assert.NotEqual(Hash(a), Hash(b));
        }
    }
}
