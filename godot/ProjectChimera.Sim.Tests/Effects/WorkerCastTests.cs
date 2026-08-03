#nullable enable
using System.Collections.Generic;
using System.Linq;
using ProjectChimera.Core;
using ProjectChimera.Effects; // StatusFlags (DW-221 — the real matter_infusion apply_modifier path)
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

        // ═══ DW-221 — the REAL matter_infusion path: apply_modifier / move_speed_delta on a MID-GATHER worker ═══
        //
        // Everything above casts a HealEffect that merely BORROWS matter_infusion's costs, so the shipped worker
        // signature ability's actual payload — an apply_modifier that buffs EffectiveMoveSpeed for 90 ticks — had no
        // worker-cast coverage: nothing proved a move-speed buff installs on a gatherer, expires back to base, leaves
        // the gather loop bit-for-bit alone across its whole lifetime, or hashes deterministically.
        //
        // Move speed is a GATHERER'S core loop variable (the node↔base round trip), which is exactly why the untested
        // combination mattered: the buff mutates EffectiveMoveSpeed (folded into SimChecksum) while GatherState /
        // GatherTarget / CarryAmount must not move at all (they are NOT folded today — see DW-78 — so only a direct
        // field assertion can catch a regression there; this test never claims the checksum covers them).

        private const int MatterInfusionModId   = 1002; // must match resources/data/abilities/matter_infusion.json
        private const int MatterInfusionDuration = 90;  // ticks
        private const int InfusionScheduleTicks  = 100; // > duration, so the expiry lands inside the window

        private static readonly Fixed WorkerBaseSpeed    = Fixed.FromInt(3); // CastHarness.Caster's Create speed
        private static readonly Fixed WorkerBuffedSpeed  = Fixed.FromInt(4); // + move_speed_delta 1

        /// <summary>A mid-gather worker carrying a real load, wired with the SHIPPED matter_infusion ability.</summary>
        private static CastHarness InfusionWorker(out int worker, GatherState gather = GatherState.Gathering)
        {
            var h = new CastHarness(AbilityTestAbilities.MatterInfusion());
            worker = h.Caster("matter_infusion", energy: 50);
            h.World.GatherState[worker]    = gather; // <-- makes this a gatherer, not a combat unit
            h.World.GatherTarget[worker]   = 4;      // mid-trip: assigned to a node...
            h.World.CarryAmount[worker]    = Fixed.FromInt(6); // ...with a partial load aboard
            h.World.CarryCapacity[worker]  = Fixed.FromInt(10);
            h.Resources.AddOre(Faction.Player1, Fixed.FromInt(100));
            h.Resources.AddCrystal(Faction.Player1, Fixed.FromInt(100));
            return h;
        }

        [Fact]
        public void Worker_MatterInfusion_InstallsMoveSpeedBuff_AndDebitsAllThreeCosts()
        {
            var h = InfusionWorker(out int w);
            Assert.Equal(WorkerBaseSpeed.Raw, h.World.EffectiveMoveSpeed[w].Raw); // pre-cast baseline

            h.IssueAndTick(w, -1);

            // The apply_modifier leaf ran: EffectiveMoveSpeed is buffed EAGERLY on the cast tick (ModifierStore
            // recomputes on apply), while BaseMoveSpeed — authored, unfolded, in-tick-immutable — never moves.
            Assert.Equal(WorkerBuffedSpeed.Raw, h.World.EffectiveMoveSpeed[w].Raw);
            Assert.Equal(WorkerBaseSpeed.Raw,   h.World.BaseMoveSpeed[w].Raw);

            // ...as one live instance with the authored identity/duration/stack count (the folded slot state).
            Assert.Equal(1, h.Modifiers.CountAt(w));
            Assert.Equal(MatterInfusionModId,    h.Modifiers.ModifierIdAt(w, 0));
            Assert.Equal(MatterInfusionDuration, h.Modifiers.RemainingTicksAt(w, 0));
            Assert.Equal(1, h.Modifiers.StackCountAt(w, 0));
            Assert.Equal(StatusFlags.None, h.World.StatusFlagsOf[w]); // matter_infusion imposes no status

            // All three shipped costs were debited (15 energy / 15 ore / 10 crystal) and the 20s cooldown started.
            Assert.Equal(Fixed.FromInt(35).Raw, h.World.Energy[w].Raw);
            Assert.Equal(Fixed.FromInt(85).Raw, h.Resources.Ore[P1].Raw);
            Assert.Equal(Fixed.FromInt(90).Raw, h.Resources.Crystal[P1].Raw);
            Assert.Equal(600, h.Cooldown(w));
        }

        [Fact]
        public void Worker_MatterInfusion_ExpiresExactlyAtDuration_BackToBaseSpeed()
        {
            var h = InfusionWorker(out int w);
            h.IssueAndTick(w, -1);

            // One tick short of the duration the buff is STILL live (an off-by-one in the countdown fails here)...
            for (int t = 0; t < MatterInfusionDuration - 1; t++) h.ModSys.Tick(h.World, SimulationLoop.FixedDt);
            Assert.Equal(1, h.Modifiers.CountAt(w));
            Assert.Equal(1, h.Modifiers.RemainingTicksAt(w, 0));
            Assert.Equal(WorkerBuffedSpeed.Raw, h.World.EffectiveMoveSpeed[w].Raw);

            // ...and on the duration tick it expires and the speed returns EXACTLY to base (no residual drift).
            h.ModSys.Tick(h.World, SimulationLoop.FixedDt);
            Assert.Equal(0, h.Modifiers.CountAt(w));
            Assert.Equal(WorkerBaseSpeed.Raw, h.World.EffectiveMoveSpeed[w].Raw);
        }

        [Fact]
        public void Worker_MidGather_MatterInfusion_LeavesGatherLoopBitForBit_AcrossTheWholeBuffLifetime()
        {
            var h = InfusionWorker(out int w);

            GatherState gsBefore = h.World.GatherState[w];
            int         gtBefore = h.World.GatherTarget[w];
            long        caBefore = h.World.CarryAmount[w].Raw;
            long        ccBefore = h.World.CarryCapacity[w].Raw;
            UnitCommand csBefore = h.World.CommandState[w];

            h.IssueAndTick(w, -1);
            AssertGatherLoopUnchanged(h, w, gsBefore, gtBefore, caBefore, ccBefore, csBefore);
            Assert.Equal(WorkerBuffedSpeed.Raw, h.World.EffectiveMoveSpeed[w].Raw); // sanity: the buff really is live

            // Hold it across the ENTIRE lifetime including the expiry tick — a move-speed buff landing on (or leaving)
            // a gatherer must never nudge the gather state machine, in either direction.
            for (int t = 0; t < MatterInfusionDuration; t++) h.ModSys.Tick(h.World, SimulationLoop.FixedDt);
            Assert.Equal(0, h.Modifiers.CountAt(w));                                // the buff expired...
            AssertGatherLoopUnchanged(h, w, gsBefore, gtBefore, caBefore, ccBefore, csBefore); // ...loop still pristine
        }

        private static void AssertGatherLoopUnchanged(CastHarness h, int w, GatherState gs, int gt,
                                                      long carry, long capacity, UnitCommand cs)
        {
            Assert.Equal(gs, h.World.GatherState[w]);
            Assert.Equal(gt, h.World.GatherTarget[w]);
            Assert.Equal(carry, h.World.CarryAmount[w].Raw);
            Assert.Equal(capacity, h.World.CarryCapacity[w].Raw);
            Assert.Equal(cs, h.World.CommandState[w]);
        }

        /// <summary>
        /// Drive a fixed worker + matter_infusion schedule for <see cref="InfusionScheduleTicks"/> ticks, capturing the
        /// per-tick <see cref="SimChecksum"/>. The cast lands on tick 1 so the install, the countdown, the cooldown
        /// drain and the expiry all fall inside the window. <paramref name="cast"/> == false is the identical schedule
        /// with the cast omitted (the negative control).
        /// </summary>
        private static List<uint> RunInfusionSchedule(bool cast)
        {
            var h = InfusionWorker(out int w);
            var buildings = new BuildingStore();
            var registry  = new FactionRegistry(2);

            var hashes = new List<uint>(InfusionScheduleTicks);
            for (int t = 0; t < InfusionScheduleTicks; t++)
            {
                if (t == 1 && cast) h.IssueAndTick(w, -1); // issue AND resolve on tick 1
                else                h.TickCast(1);         // same number of cast-system ticks either way
                h.ModSys.Tick(h.World, SimulationLoop.FixedDt);
                hashes.Add(SimChecksum.Compute(h.World, buildings, h.Resources, registry, h.Modifiers));
            }
            return hashes;
        }

        [Fact]
        public void Worker_MatterInfusion_ChecksumSequence_IsDeterministic_AndTheCastMovesIt()
        {
            List<uint> a = RunInfusionSchedule(cast: true);
            List<uint> b = RunInfusionSchedule(cast: true);

            Assert.Equal(InfusionScheduleTicks, a.Count);
            Assert.True(a.SequenceEqual(b),
                "Two identical worker matter_infusion schedules diverged — nondeterminism in the worker cast path.");
            Assert.True(a.Distinct().Count() > 1,
                "Checksum sequence is constant — the schedule is not exercising the buff/cooldown state (vacuous).");

            // Negative control: without the cast the same schedule hashes DIFFERENTLY, so the sequence above is
            // genuinely driven by the move-speed buff + energy/ore/crystal debit + cooldown, not by the fixture alone.
            Assert.False(a.SequenceEqual(RunInfusionSchedule(cast: false)),
                "The cast left no trace in SimChecksum — a worker move-speed buff must be hashed sim truth.");
        }
    }
}
