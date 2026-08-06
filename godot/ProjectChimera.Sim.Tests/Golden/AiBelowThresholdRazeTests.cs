#nullable enable
using ProjectChimera.AI;
using ProjectChimera.Combat;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Sim;
using ProjectChimera.Economy;
using Xunit;

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// Story 2.13 code-review follow-up (Alec) — the below-threshold RAZE stall-breaker.
    ///
    /// AC1.5's raze fallback only commits a FULL wave (>= the attack threshold). So a winning-but-STARVED AI —
    /// a remnant of units below the threshold, NO production building and no ore to rebuild one — would stand
    /// inert next to the last enemy base forever and a DestroyAllBuildings match would never conclude (the exact
    /// stall AC1 promises to remove, just narrowed to the below-threshold case). This proves such an AI still
    /// razes.
    ///
    /// STATE PREDICATE (not a hash compare), like <see cref="AiRazeTerminationTests"/>, so it runs on every OS.
    /// RED before the stall-breaker (3 units &lt; the Normal threshold of 5 → ScoreRazeBuildings returns 0 → the
    /// AI no-ops and the base survives); GREEN after (the below-threshold branch fires → the remnant razes).
    ///
    /// <para><b>DW-838 (decision 2026-08-06, Alec).</b> The branch used to ALSO require the AI to own a live
    /// CommandCenter, sold in-code as a determinism fence (the base-less cross-platform goldens' Player2 never
    /// satisfies it). As a gameplay rule that inverted the stall-breaker's own purpose: an AI whose CC has already
    /// been destroyed is strictly LESS able to grow back than one that still has it, and was the single case most
    /// in need of committing. The term is gone, and
    /// <see cref="BaselessStarvedWinningAi_BelowThreshold_StillRazesAndConcludes"/> pins the case it excluded.</para>
    /// </summary>
    public class AiBelowThresholdRazeTests
    {
        /// <summary>Comfortable bounded budget: the P2 remnant is placed within striking distance and the target's
        /// HP is set low, so a genuine raze concludes in a few hundred ticks; a stall runs out the full budget.</summary>
        private const int MaxTicks = 6000;

        /// <summary>DW-125 — the Normal attack threshold read SYMBOLICALLY from
        /// <see cref="AiOpponentSystem.DifficultyProfile"/> instead of hand-copying the literal <c>5</c>, so the
        /// below-threshold precondition below still fails loudly if a future difficulty-curve retune drops the real
        /// bar under this fixture's 3-unit remnant (which would silently move it onto the full-wave path).</summary>
        private static readonly int NormalAttackThreshold =
            AiOpponentSystem.DifficultyProfile(AiDifficulty.Normal).AttackThreshold;

        [Fact]
        public void StarvedWinningAi_BelowThreshold_StillRazesAndConcludes()
        {
            GoldenHarness h = BuildStuckBelowThresholdAi();
            Assert.True(CountFactionBuildings(h, Faction.Player1) > 0,
                "precondition: Player1 starts with a base to raze");
            Assert.True(CountFactionUnits(h, Faction.Player2) < NormalAttackThreshold,
                $"precondition: the AI must be BELOW the Normal attack threshold ({NormalAttackThreshold}) so this exercises the stall-breaker, not the full-wave path");

            int ticksToConclude = -1;
            for (int t = 0; t < MaxTicks; t++)
            {
                h.Host.StepOnce();
                if (CountFactionBuildings(h, Faction.Player1) == 0) { ticksToConclude = t; break; }
            }

            Assert.True(ticksToConclude >= 0,
                $"A below-threshold, production-less, ore-less Player2 AI did NOT conclude within {MaxTicks} ticks " +
                $"(still {CountFactionBuildings(h, Faction.Player1)} Player1 building(s) alive). The below-threshold " +
                $"raze stall-breaker in ScoreRazeBuildings is not firing.");
        }

        /// <summary>
        /// DW-838 — the case the deleted live-CommandCenter gate excluded: the SAME starved, below-threshold,
        /// production-less, ore-less remnant, except its CommandCenter is already gone. It is strictly WORSE off
        /// than the fixture above (it cannot even rebuild a base), so if any AI must commit its remnant it is this
        /// one — yet the old gate scored it 0 and left it standing inert beside the last enemy structure forever.
        ///
        /// <para>RED before the fix: with <c>HasLiveCommandCenter</c> still in the branch this AI can never satisfy
        /// it, <c>ScoreRazeBuildings</c> returns 0 on every tick, and the Player1 base survives the whole budget.</para>
        /// </summary>
        [Fact]
        public void BaselessStarvedWinningAi_BelowThreshold_StillRazesAndConcludes()
        {
            GoldenHarness h = BuildStuckBelowThresholdAi(withCommandCenter: false);
            Assert.Equal(0, CountFactionBuildings(h, Faction.Player2)); // precondition: the AI owns NO base at all
            Assert.True(CountFactionBuildings(h, Faction.Player1) > 0,
                "precondition: Player1 starts with a base to raze");
            Assert.True(CountFactionUnits(h, Faction.Player2) < NormalAttackThreshold,
                $"precondition: the AI must be BELOW the Normal attack threshold ({NormalAttackThreshold})");

            int ticksToConclude = -1;
            for (int t = 0; t < MaxTicks; t++)
            {
                h.Host.StepOnce();
                if (CountFactionBuildings(h, Faction.Player1) == 0) { ticksToConclude = t; break; }
            }

            Assert.True(ticksToConclude >= 0,
                $"A BASE-LESS below-threshold, production-less, ore-less Player2 AI did NOT conclude within " +
                $"{MaxTicks} ticks (still {CountFactionBuildings(h, Faction.Player1)} Player1 building(s) alive). " +
                $"The below-threshold raze stall-breaker is still gated on the AI owning a live CommandCenter " +
                $"(DW-838), which excludes the very remnant it exists to unstick.");
        }

        /// <summary>
        /// A winning-but-stuck AI: Player2 owns a completed CommandCenter (a real base) and 3 idle combat units
        /// (&lt; the Normal threshold of 5), with ZERO ore and NO production building — so it can never train back
        /// to a full wave. Player1 is a single passive, low-HP CommandCenter with no defenders — the raze target
        /// (HP set low so the test measures the AI's DECISION, not combat tuning). No Fixed.FromFloat anywhere.
        /// </summary>
        /// <param name="withCommandCenter">DW-838 — false builds the BASE-LESS variant (no Player2 CommandCenter),
        /// the remnant the deleted live-CommandCenter gate used to exclude. Everything else is identical, so the
        /// two facts differ in exactly the one bit the removed term read.</param>
        private static GoldenHarness BuildStuckBelowThresholdAi(bool withCommandCenter = true)
        {
            var host = SimulationHost.Create(
                NullLogSink.Instance,
                new FactionRegistry(2),
                new FactionDefinition(),
                new FactionDefinition(),
                damageTable: null,
                aiLevel: AiDifficulty.Normal);
            host.ChecksumInterval = 1;

            EntityWorld   world     = host.World;
            BuildingStore buildings = host.Buildings;
            ResourceStore resources = host.Resources;

            // ── Player2 (AI): a completed CommandCenter — its BASE — + deposit base. Ore stays 0, so the AI can
            //    afford NO production building → genuinely stuck. DW-838: withCommandCenter=false omits the base
            //    entirely, which is the strictly-worse-off remnant the old live-CommandCenter gate excluded. ──
            int p2cc = -1;
            if (withCommandCenter)
            {
                p2cc = buildings.Create(new FixedVec3(Fixed.Zero, Fixed.Zero, Fixed.Zero),
                                        Faction.Player2, BuildingType.CommandCenter);
                buildings.ConstructionTimer[p2cc] = Fixed.Zero; // complete
            }
            resources.FactionBase[(int)Faction.Player2] = new FixedVec3(Fixed.Zero, Fixed.Zero, Fixed.Zero);

            // ── Player2 remnant: 3 idle combat units (< threshold 5). Siege damage so they raze a Fortified
            //    building cleanly; placed within striking distance of the P1 base to keep the run short. ──
            for (int i = 0; i < 3; i++)
            {
                int u = world.Create(new FixedVec3(Fixed.FromInt(5), Fixed.Zero, Fixed.FromInt(i - 1)),
                                     Faction.Player2, Fixed.FromInt(80), Fixed.FromInt(3));
                world.EffectiveAttackDamage[u] = Fixed.FromInt(10);
                world.AttackRange[u]  = Fixed.FromInt(2);
                world.AttackSpeed[u]  = Fixed.FromInt(1);
                world.DamageTypeOf[u] = DamageType.Siege;   // anti-building — isolates the test to the DECISION
                world.ArmorTypeOf[u]  = ArmorType.Medium;
            }

            // ── Player1: one passive CommandCenter (the raze target), no defenders. HP forced low so any positive
            //    per-hit damage razes it quickly — the test asserts the AI ISSUES the raze, not combat balance. ──
            int p1cc = buildings.Create(new FixedVec3(Fixed.FromInt(10), Fixed.Zero, Fixed.Zero),
                                        Faction.Player1, BuildingType.CommandCenter);
            buildings.ConstructionTimer[p1cc] = Fixed.Zero;      // complete
            buildings.Health[p1cc]            = Fixed.FromInt(50); // low HP → fast, non-flaky raze
            resources.FactionBase[(int)Faction.Player1] = new FixedVec3(Fixed.FromInt(10), Fixed.Zero, Fixed.Zero);

            host.ScenarioDirector.LoadScenario(new ScenarioData());
            return new GoldenHarness(host, p2cc);
        }

        private static int CountFactionBuildings(GoldenHarness h, Faction f)
        {
            int n = 0;
            for (int i = 0; i < h.Buildings.Count; i++)
                if (h.Buildings.Alive[i] && h.Buildings.FactionOf[i] == f) n++;
            return n;
        }

        private static int CountFactionUnits(GoldenHarness h, Faction f)
        {
            int n = 0;
            for (int i = 0; i < h.World.HighWaterMark; i++)
                if (h.World.IsAlive(i) && h.World.FactionOf[i] == f) n++;
            return n;
        }
    }
}
