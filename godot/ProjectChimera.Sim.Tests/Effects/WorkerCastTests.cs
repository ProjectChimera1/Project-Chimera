#nullable enable
using ProjectChimera.Core;
using Xunit;

namespace ProjectChimera.Sim.Tests.Effects
{
    /// <summary>
    /// Story 2.9b (AC1) — a WORKER (GatherState != Inactive) casts through the EXACT same cast pipeline a combat unit
    /// uses: casting is category-agnostic. These prove (a) affordability + atomicity across energy/ore/crystal holds
    /// for a worker caster (the check-all-then-debit-all contract does not depend on GatherState), and (b) the
    /// worker's gather/build loop state is bit-for-bit unchanged by issuing AND resolving a cast (AC1.4 — the cast
    /// never durably touches CommandState/GatherState). No new sim code backs this: these tests prove the EXISTING
    /// contract extends to workers; they do not add a parallel path. Mirrors <see cref="AbilityAffordabilityTests"/>'s
    /// construction, with the caster made a worker via a live GatherState.
    /// </summary>
    public class WorkerCastTests
    {
        private const int P1 = (int)Faction.Player1;

        /// <summary>A WORKER caster (matter_infusion-shaped costs: SelfHeal 15E/15O/10C, 20s cd, heal 20). The ONLY
        /// thing that makes it a "worker" is a live GatherState — the cast pipeline never branches on it.</summary>
        private static CastHarness Worker(int energy, int ore, int crystal, out int worker,
            GatherState gather = GatherState.Gathering)
        {
            var h = new CastHarness(AbilityTestAbilities.SelfHeal(costEnergy: 15, costOre: 15, costCrystal: 10, cooldownSec: 20, heal: 20));
            worker = h.Caster("test_heal", energy: energy);
            h.World.GatherState[worker] = gather;             // <-- makes this a gatherer, not a combat unit
            h.Resources.AddOre(Faction.Player1, Fixed.FromInt(ore));
            h.Resources.AddCrystal(Faction.Player1, Fixed.FromInt(crystal));
            h.World.Health[worker] = Fixed.FromInt(50);        // damaged so a successful heal is observable
            return h;
        }

        // ── AC1.2 / AC1.3 — a worker casts and debits all three; refusal is atomic (no partial spend) ──

        [Fact]
        public void Worker_AffordingAll_CastsAndDebitsEnergyOreCrystal()
        {
            var h = Worker(energy: 50, ore: 100, crystal: 100, out int w);
            h.IssueAndTick(w, -1);

            Assert.Equal(Fixed.FromInt(35).Raw, h.World.Energy[w].Raw);        // 50 - 15
            Assert.Equal(Fixed.FromInt(85).Raw, h.Resources.Ore[P1].Raw);       // 100 - 15
            Assert.Equal(Fixed.FromInt(90).Raw, h.Resources.Crystal[P1].Raw);   // 100 - 10
            Assert.Equal(600, h.Cooldown(w));                                   // 20s * 30 tps — cooldown started
            Assert.Equal(Fixed.FromInt(70).Raw, h.World.Health[w].Raw);         // 50 + 20 heal — the effect ran
        }

        [Fact]
        public void Worker_InsufficientCrystal_RefusesAtomically_NoPartialSpend()
        {
            // The headline atomicity case: ore + energy are sufficient, crystal (the LAST-checked resource) is short →
            // the cast must refuse having debited NOTHING (not ore, not energy).
            var h = Worker(energy: 50, ore: 100, crystal: 4, out int w); // 4 < 10
            h.IssueAndTick(w, -1);
            AssertNothingSpent(h, w, energy: 50, ore: 100, crystal: 4);
        }

        [Fact]
        public void Worker_InsufficientOre_RefusesAtomically()
        {
            var h = Worker(energy: 50, ore: 5, crystal: 100, out int w); // 5 < 15
            h.IssueAndTick(w, -1);
            AssertNothingSpent(h, w, energy: 50, ore: 5, crystal: 100);
        }

        [Fact]
        public void Worker_InsufficientEnergy_RefusesAtomically()
        {
            var h = Worker(energy: 10, ore: 100, crystal: 100, out int w); // 10 < 15
            h.IssueAndTick(w, -1);
            AssertNothingSpent(h, w, energy: 10, ore: 100, crystal: 100);
        }

        private static void AssertNothingSpent(CastHarness h, int w, int energy, int ore, int crystal)
        {
            Assert.Equal(Fixed.FromInt(energy).Raw,  h.World.Energy[w].Raw);       // energy unchanged
            Assert.Equal(Fixed.FromInt(ore).Raw,     h.Resources.Ore[P1].Raw);     // ore unchanged
            Assert.Equal(Fixed.FromInt(crystal).Raw, h.Resources.Crystal[P1].Raw); // crystal unchanged
            Assert.Equal(0, h.Cooldown(w));                                        // no cooldown started
            Assert.Equal(Fixed.FromInt(50).Raw, h.World.Health[w].Raw);            // effect did NOT run (still 50)
        }

        // ── AC1.4 — the worker's gather/build loop is bit-for-bit unchanged by issuing + resolving a cast ──

        [Fact]
        public void Worker_MidGather_CastLeavesGatherStateBitForBit_Unchanged()
        {
            var h = Worker(energy: 50, ore: 100, crystal: 100, out int w, gather: GatherState.Gathering);
            h.World.GatherTarget[w] = 7;
            h.World.CarryAmount[w]  = Fixed.FromInt(9);

            GatherState gsBefore = h.World.GatherState[w];
            int         gtBefore = h.World.GatherTarget[w];
            long        caBefore = h.World.CarryAmount[w].Raw;
            UnitCommand csBefore = h.World.CommandState[w];

            h.IssueAndTick(w, -1); // issue AND resolve the cast in the same tick

            Assert.Equal(Fixed.FromInt(90).Raw, h.Resources.Crystal[P1].Raw); // sanity: the cast actually fired
            Assert.Equal(gsBefore, h.World.GatherState[w]);
            Assert.Equal(gtBefore, h.World.GatherTarget[w]);
            Assert.Equal(caBefore, h.World.CarryAmount[w].Raw);
            Assert.Equal(csBefore, h.World.CommandState[w]);
        }

        [Fact]
        public void Worker_MidBuild_CastLeavesBuildStateBitForBit_Unchanged()
        {
            var h = Worker(energy: 50, ore: 100, crystal: 100, out int w, gather: GatherState.Idle);
            h.World.CommandState[w] = UnitCommand.Build;
            h.World.BuildTarget[w]  = 3;

            h.IssueAndTick(w, -1);

            // CommandState is restored to Build (a cast is never durably CastAbility — NetworkCommand captures/restores
            // the prior state), and BuildTarget is untouched — the gather/build loop resumes as if no cast occurred.
            Assert.Equal(UnitCommand.Build, h.World.CommandState[w]);
            Assert.Equal(3, h.World.BuildTarget[w]);
            Assert.Equal(Fixed.FromInt(90).Raw, h.Resources.Crystal[P1].Raw); // the cast still fired
        }
    }
}
