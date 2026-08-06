using ProjectChimera.AI;
using ProjectChimera.Combat;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Sim;

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// Story 1.11 (AC1) — the AI-ACTIVE golden scenario for the Utility-AI smoke test.
    ///
    /// The existing goldens deliberately STARVE the AI (GoldenScenario.cs:151-156 /
    /// MultiFactionScenario.cs:123-127: Player2 holds 3 combat units &lt; the Normal attack threshold of 5 and
    /// 0 ore, so <see cref="AiOpponentSystem"/> runs but no-ops deterministically). This scenario INVERTS that
    /// recipe so the AI at sim index 7 actually acts: Player2 gets 300 ore (&gt; COST_BARRACKS = 100) and a full
    /// idle wave of 5 combat units.
    ///
    /// <para><b>What the AI actually does here (DW-839 — corrected 2026-08-06, verified by driving the fixture, not
    /// re-derived from the weights).</b> This doc used to claim "on tick 1 it scores BuildBarracks (0.85) highest and
    /// builds a Barracks, then — once it is no longer building — launches an attack with the wave". Both halves were
    /// wrong. Player1 fields a CommandCenter and NO units, so <c>EnemyThreatRemains</c> is false while
    /// <c>EnemyBuildingExists</c> is true and <see cref="AiOpponentSystem"/> scores RAZE highest on tick 1: 0.90,
    /// ahead of BuildBarracks (0.85) and LaunchAttack (0.65 × 5/10 = 0.325). It runs <c>DoRazeBuildings</c> — all 5
    /// units are ordered <c>AttackBuilding</c> onto P1's CommandCenter. On tick 2 that wave is no longer AVAILABLE
    /// (a commanded unit is not conscriptable), so the raze score falls to 0 and BuildBarracks wins: the Barracks
    /// non-vacuity signal is real, just one tick later and for a different reason. <c>DoLaunchAttack</c> NEVER runs
    /// in this scenario, so the golden has never pinned the wave path. Pinned by
    /// <c>AiActiveScenarioRazePathTests</c> so this description cannot silently go stale again.</para>
    ///
    /// The AI's decisions reach <see cref="SimChecksum"/> transitively (building spawn → Alive / Health /
    /// ConstructionTimer; ore spend → Ore; raze command → unit Position one tick later), so the recorded
    /// per-tick checksum sequence pins the AI's behavior.
    ///
    /// CAVEAT (AC1c — the float boundary; do NOT fix here): <see cref="AiOpponentSystem"/> scores actions with
    /// raw <c>float</c> and picks the winner via a <c>float &gt;</c> compare (AiOpponentSystem.cs:266-271).
    /// Same-machine / same-JIT this is fully deterministic, so the two-run and golden-match assertions hold.
    /// CROSS-PLATFORM it is the known float→Fixed debt (D2): a near-tie <c>&gt;</c> can resolve differently on
    /// another CPU/JIT. So this golden is recorded ONCE on the ship-primary machine (Windows), its golden-match
    /// assertion is Windows-gated, and it is deliberately EXCLUDED from the 1.10c Win↔Linux cross-platform gate
    /// until the AI is migrated to <c>Fixed</c>. Do NOT add it to that gate.
    /// </summary>
    public static class AiActiveScenario
    {
        /// <summary>300 ticks = 10s at 30 tps; with ChecksumInterval = 1 that yields 300 samples (ticks 1..300).</summary>
        public const int DefaultTicks = 300;

        /// <summary>
        /// Pinned difficulty (AC1): the score weights AND the attack threshold branch on it, so it MUST be
        /// fixed for the golden to reproduce. Normal → attack threshold 5, aggression 0.65, tech 0.70.
        /// </summary>
        public const AiDifficulty PinnedDifficulty = AiDifficulty.Normal;

        /// <summary>Idle Player2 combat units pre-placed — exactly the Normal attack threshold (5), so the AI
        /// launches a wave the first tick it is not busy building.</summary>
        private const int P2CombatUnits = 5;

        /// <summary>
        /// Construct a fresh, fully-wired sim with the AI ACTIVE (Player2). Allocates new stores/systems on every
        /// call — no static or shared mutable state — so two calls in one process are independent (AC1a) and a
        /// fresh process reproduces the committed golden exactly (AC1b).
        /// </summary>
        public static GoldenHarness Build()
        {
            // Sim spine via SimulationHost (Story 1.8a) with the AI difficulty PINNED. FactionRegistry(2) =
            // P1+P2 active, so the checksum's faction loop mixes Ore[1] then Ore[2]. null DamageTable →
            // DamageTable.Default (same as the other goldens).
            var host = SimulationHost.Create(
                NullLogSink.Instance,
                new FactionRegistry(2),
                new FactionDefinition(),
                new FactionDefinition(),
                damageTable: null,
                aiLevel: PinnedDifficulty);
            host.ChecksumInterval = 1;   // checksum EVERY tick

            int firstP2Unit = Populate(host);
            return new GoldenHarness(host, firstP2Unit);
        }

        /// <summary>
        /// Story 3.10 test seam — (re)establish this scenario's authored start on an existing host. Used to verify
        /// <see cref="SimulationHost.ClearForReset"/> restores the AI's per-match decision state: a clear + this
        /// repopulate must reproduce a fresh boot byte-for-byte. Also the single setup path <see cref="Build"/> uses.
        /// </summary>
        public static int Populate(SimulationHost host)
        {
            int firstP2Unit = PopulateScenario(host.World, host.Buildings, host.Resources);

            // Mirror MainScene's director lifecycle (empty trigger state; ScenarioDirector.Tick early-returns
            // with no triggers — a faithful no-op).
            host.ScenarioDirector.LoadScenario(new ScenarioData());
            return firstP2Unit;
        }

        /// <summary>
        /// Populate an AI-active scenario. Returns the entity id of the first Player2 combat unit (a harmless
        /// perturbation handle for <see cref="GoldenHarness"/>; this scenario does not perturb).
        /// </summary>
        private static int PopulateScenario(EntityWorld world, BuildingStore buildings, ResourceStore resources)
        {
            // ── Player2 idle wave (ids 0..4): 5 combat units at Idle / Inactive (EntityWorld.Create defaults),
            //    NOT flagged Moving, so AiSnapshot counts them as AvailableCombatUnits → ScoreLaunchAttack fires
            //    once the AI is free. Clustered in the P2 half, clear of P1 so the attack march stays combat-free.
            //    All authored in Fixed (no Fixed.FromFloat). ──
            int firstP2Unit = -1;
            for (int i = 0; i < P2CombatUnits; i++)
            {
                int u = world.Create(new FixedVec3(Fixed.FromInt(40), Fixed.Zero, Fixed.FromInt(i * 2 - 4)),
                                     Faction.Player2, Fixed.FromInt(80), Fixed.FromInt(3));
                world.EffectiveAttackDamage[u] = Fixed.FromInt(6);
                world.AttackRange[u]  = Fixed.FromInt(2);
                world.AttackSpeed[u]  = Fixed.FromInt(1);
                world.DamageTypeOf[u] = DamageType.Normal;
                world.ArmorTypeOf[u]  = ArmorType.Medium;
                if (firstP2Unit < 0) firstP2Unit = u;
            }

            // ── Player2 base: a completed CommandCenter + deposit base. NO pre-placed Barracks — the AI BUILDS
            //    one (the non-vacuity signal). ──
            int p2cc = buildings.Create(new FixedVec3(Fixed.FromInt(45), Fixed.Zero, Fixed.Zero),
                                        Faction.Player2, BuildingType.CommandCenter);
            buildings.ConstructionTimer[p2cc] = Fixed.Zero; // mark complete
            resources.FactionBase[(int)Faction.Player2] = new FixedVec3(Fixed.FromInt(45), Fixed.Zero, Fixed.Zero);

            // P2 ore: 300 > COST_BARRACKS (100) so ScoreBuildBarracks (0.85) is affordable and wins on TICK 2 —
            // tick 1 goes to the raze at 0.90 (DW-839; see the type doc). Either way the AI builds, which is the
            // non-vacuity signal this ore exists for.
            resources.AddOre(Faction.Player2, Fixed.FromInt(300));

            // ── Player1: a passive, far-off base so the checksum's faction loop covers two ACTIVE factions.
            //    DW-839 — what this arrangement actually exercises: a P1 CommandCenter with NO P1 units is the exact
            //    input that makes ScoreRazeBuildings win (no defenders, a structure to raze), so the P2 wave is
            //    ordered AttackBuilding onto THIS building and chases it at (60,60). It is NOT a LaunchAttack wave
            //    and it never marches on the AI's hardcoded P1_BASE (-45,0,0). Still deterministic and combat-free:
            //    5 units at speed 3 cannot cross the ~28 units to (60,60) inside the 300-tick (10s) horizon, so
            //    nothing ever enters weapons range and no damage is dealt. P1 takes no actions (no AI plays P1). ──
            int p1cc = buildings.Create(new FixedVec3(Fixed.FromInt(60), Fixed.Zero, Fixed.FromInt(60)),
                                        Faction.Player1, BuildingType.CommandCenter);
            buildings.ConstructionTimer[p1cc] = Fixed.Zero; // mark complete
            resources.FactionBase[(int)Faction.Player1] = new FixedVec3(Fixed.FromInt(60), Fixed.Zero, Fixed.FromInt(60));
            resources.AddOre(Faction.Player1, Fixed.FromInt(200));

            return firstP2Unit;
        }
    }
}
