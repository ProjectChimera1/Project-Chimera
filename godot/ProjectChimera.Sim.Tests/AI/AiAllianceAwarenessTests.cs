#nullable enable
using ProjectChimera.AI;
using ProjectChimera.Combat;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Sim;
using Xunit;

namespace ProjectChimera.Sim.Tests.AI
{
    /// <summary>
    /// DW-439 / DW-445 — <see cref="AiOpponentSystem"/>'s target / raze / threat classification must consult the
    /// <see cref="AllianceStore"/>, exactly like the combat, projectile, spatial-hash and ability sites Story 9.14
    /// made team-aware.
    ///
    /// <para><b>The defect.</b> The AI spelled hostility as <c>f != AI_FACTION &amp;&amp; f != Faction.Neutral</c> at
    /// four independent sites and never consulted the mask. On a team with a player (WC3's most-played
    /// "2 players + 2 computers" setup) that made its ALLY a target three different ways:</para>
    /// <list type="bullet">
    ///   <item>the raze picker returned the ally's base, the AI issued <c>AttackBuilding</c> at it,
    ///     <c>CombatSystem.TickAttackBuildingCombat</c>'s allied guard (CombatSystem.cs:435) rejected the order and
    ///     reverted the unit to Idle — every tick, forever, while the real enemy went untouched;</item>
    ///   <item>an ALLIED unit counted as a live "enemy defender", so the raze fallback never even scored;</item>
    ///   <item>the wave's hardcoded <c>P1_BASE</c> march destination walked the whole army into its ally's base.</item>
    /// </list>
    ///
    /// <para>Every test here drives the REAL sim (a full <see cref="SimulationHost"/>, no reflection, no scorer stub)
    /// and asserts on the orders/outcomes the AI actually produces. They are STATE PREDICATES, not hash compares
    /// (like <c>AiRazeTerminationTests</c>), so the AI's known float debt does not make them OS-dependent.</para>
    ///
    /// <para><b>DETERMINISM — no golden moves.</b> Every assertion below needs a mask that puts two factions on one
    /// team. No shipped scenario and no golden seeds one: they all run the FFA default (<c>TeamId[f]==f</c>), under
    /// which <see cref="AllianceStore.AreAllied"/> is false for every pair of distinct factions and the new
    /// <c>IsHostile</c> test returns exactly what the old inline test returned. <see cref="FfaAi_StillMarchesOnTheHardcodedP1Base"/>
    /// pins that unchanged FFA branch directly.</para>
    /// </summary>
    public class AiAllianceAwarenessTests
    {
        /// <summary>Generous bounded budget, matching <c>AiRazeTerminationTests</c>: a genuine raze concludes in a few
        /// hundred ticks at these distances; a stalled AI runs the budget out.</summary>
        private const int MaxTicks = 6000;

        /// <summary>DW-125 — read the wave threshold SYMBOLICALLY so a difficulty-curve retune moves these fixtures
        /// with it instead of silently dropping them below the bar they are built to clear.</summary>
        private static readonly int NormalAttackThreshold =
            AiOpponentSystem.DifficultyProfile(AiDifficulty.Normal).AttackThreshold;

        // ── DW-445: the raze/target picker ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The headline defect: an AI teamed with Player1, and with NOTHING hostile on the map, must never point an
        /// order at its ally. RED before the fix — <c>EnemyBuildingExists</c> counted the ally's base, so
        /// <c>ScoreRazeBuildings</c> won at 0.90 and <c>DoRazeBuildings</c> issued <c>AttackBuilding</c> onto it on
        /// tick 1 (combat then reverted every unit to Idle and the AI re-issued the identical order forever).
        /// </summary>
        [Fact]
        public void TeamedAi_NeverOrdersAnAttackOntoItsAllysBase()
        {
            SimulationHost host = NewHost();
            PlaceBase(host, Faction.Player2, x: 45, z: 0);                 // the AI's own base
            PlaceWave(host, Faction.Player2, NormalAttackThreshold, x: 40); // a full, idle wave
            PlaceBase(host, Faction.Player1, x: -45, z: 0);                 // the ALLY's base — the only base left
            PutOnOneTeam(host, Faction.Player1, Faction.Player2);

            // 120 ticks is far more than the one tick the pre-fix AI needed to issue the bad order.
            for (int t = 0; t < 120; t++)
            {
                host.StepOnce();
                AssertNoOrderAgainstAnAlly(host);
            }
        }

        /// <summary>
        /// The positive half: with an ally's base CLOSER than the enemy's, the AI must still march on the ENEMY. RED
        /// before the fix — the nearest-building picker returned the ally's base every tick, combat rejected the
        /// order, and the hostile base survived the whole budget untouched.
        /// </summary>
        [Fact]
        public void TeamedAi_RazesTheHostileBase_AndLeavesItsAllysBaseIntact()
        {
            SimulationHost host = NewHost();
            PlaceBase(host, Faction.Player2, x: 0, z: 0);                  // the AI's own base
            PlaceWave(host, Faction.Player2, NormalAttackThreshold, x: 5); // a full, idle wave
            int allyBase = PlaceBase(host, Faction.Player1, x: 10, z: 20); // NEARER than the enemy — the trap
            int enemyBase = PlaceBase(host, Faction.Player3, x: 30, z: 0); // the real target
            host.Buildings.Health[enemyBase] = Fixed.FromInt(50);          // low HP → the test measures the DECISION
            PutOnOneTeam(host, Faction.Player1, Faction.Player2);

            Fixed allyBaseHealthBefore = host.Buildings.Health[allyBase];

            Assert.True(StepUntil(host, h => !h.Buildings.Alive[enemyBase]),
                $"The teamed AI never razed the HOSTILE Player3 base within {MaxTicks} ticks. Its raze picker is " +
                "still returning the nearer ALLIED Player1 base, whose order combat rejects — the DW-445 stall.");

            Assert.True(host.Buildings.Alive[allyBase], "the ALLY's base must still stand");
            Assert.Equal(allyBaseHealthBefore, host.Buildings.Health[allyBase]);
        }

        // ── DW-439: the threat half ───────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// An ALLIED army is not a live enemy defender. RED before the fix — any breathing teammate unit latched
        /// <c>EnemyThreatRemains</c>, <c>ScoreRazeBuildings</c> returned 0 for as long as it lived, and the AI never
        /// concluded against the real enemy's base at all.
        /// </summary>
        [Fact]
        public void TeamedAi_DoesNotCountAnAlliedArmyAsALiveDefender()
        {
            SimulationHost host = NewHost();
            PlaceBase(host, Faction.Player2, x: 0, z: 0);
            PlaceWave(host, Faction.Player2, NormalAttackThreshold, x: 5);

            // The ally: a base plus a live, damage-bearing army, far off to one side. Before the fix these three
            // units alone were enough to freeze the AI's raze decision permanently.
            PlaceBase(host, Faction.Player1, x: -40, z: 0);
            PlaceWave(host, Faction.Player1, 3, x: -40);

            int enemyBase = PlaceBase(host, Faction.Player3, x: 30, z: 0);
            host.Buildings.Health[enemyBase] = Fixed.FromInt(50);
            PutOnOneTeam(host, Faction.Player1, Faction.Player2);

            Assert.True(StepUntil(host, h => !h.Buildings.Alive[enemyBase]),
                $"The teamed AI never razed the hostile Player3 base within {MaxTicks} ticks. Its snapshot is still " +
                "counting the ALLIED Player1 army as live enemy defenders, so the raze fallback never scores.");
        }

        // ── DW-445, recorded decision: allied-skip is the FLOOR, allied-aware priority is the execution ────────

        /// <summary>
        /// The "modern RTS" half of the recorded decision: allied-skip is the floor, and where it is cheap the AI
        /// should also avoid duplicating an ally's current focus. With a teammate already force-attacking the NEARER
        /// hostile structure, the AI's wave takes the other one — two buildings fall in parallel instead of five
        /// units stacking onto the one the ally is already chewing through.
        ///
        /// <para>It is a PREFERENCE, never a filter: <c>FindNearestEnemyBuilding</c> still returns the nearest
        /// structure when every candidate is ally-focused, so this can never reintroduce the "no target" stall.</para>
        /// </summary>
        [Fact]
        public void TeamedAi_PrefersAHostileBuildingItsAllyIsNotAlreadyRazing()
        {
            SimulationHost host = NewHost();
            PlaceBase(host, Faction.Player2, x: 0, z: 0);
            PlaceWave(host, Faction.Player2, NormalAttackThreshold, x: 0);

            int nearHostile = PlaceBase(host, Faction.Player3, x: 10, z: 0); // nearest — the ally is on this one
            int farHostile  = PlaceBase(host, Faction.Player3, x: 14, z: 0); // what the AI should take instead
            PutOnOneTeam(host, Faction.Player1, Faction.Player2);

            // One allied Player1 unit already force-attacking the NEAR structure.
            int allyUnit = PlaceWave(host, Faction.Player1, 1, x: 10, z: 3);
            host.World.CommandState[allyUnit]  = UnitCommand.AttackBuilding;
            host.World.CommandTarget[allyUnit] = host.Buildings.PackRef(nearHostile);
            host.World.AttackTarget[allyUnit]  = -1;

            host.StepOnce();

            int ordered = 0;
            for (int i = 0; i < host.World.HighWaterMark; i++)
            {
                if (!host.World.IsAlive(i) || host.World.FactionOf[i] != Faction.Player2) continue;
                if (host.World.CommandState[i] != UnitCommand.AttackBuilding) continue;
                Assert.True(host.Buildings.TryResolveRef(host.World.CommandTarget[i], out int b),
                    "the AI issued an AttackBuilding order whose packed target does not resolve");
                Assert.True(b == farHostile,
                    $"unit {i} was sent at building {b}, the structure its ALLY is already razing ({nearHostile}); " +
                    "the ally-focus preference is not de-duplicating the team's raze targets");
                ordered++;
            }

            Assert.True(ordered > 0,
                "precondition: the AI must actually have committed a raze wave this tick, otherwise the " +
                "ally-focus preference was never exercised");
        }

        // ── The FFA fence: the branch that must NOT move ──────────────────────────────────────────────────────

        /// <summary>
        /// The determinism fence for this bundle. Under FFA (every shipped scenario, every golden) Player1 is still
        /// hostile, so the wave must still march to the unchanged hardcoded <see cref="AiOpponentSystem.P1_BASE"/> —
        /// not to a newly-computed nearest-enemy destination, which would move every AI-bearing golden. Fails loudly
        /// if the alliance-aware destination logic ever leaks out of its teamed branch.
        /// </summary>
        [Fact]
        public void FfaAi_StillMarchesOnTheHardcodedP1Base()
        {
            SimulationHost host = NewHost();
            PlaceBase(host, Faction.Player2, x: 45, z: 0);
            PlaceWave(host, Faction.Player2, NormalAttackThreshold, x: 40);
            // No enemy buildings at all → ScoreRazeBuildings is 0 and LaunchAttack is the winning action.
            // The alliance mask is left at its FFA default — exactly what every golden runs on.

            host.StepOnce();

            int marching = 0;
            for (int i = 0; i < host.World.HighWaterMark; i++)
            {
                if (!host.World.IsAlive(i) || host.World.FactionOf[i] != Faction.Player2) continue;
                Assert.Equal(UnitCommand.AttackMove, host.World.CommandState[i]);
                Assert.Equal(AiOpponentSystem.P1_BASE, host.World.CommandGoal[i]);
                Assert.Equal(AiOpponentSystem.P1_BASE, host.World.MoveTarget[i]);
                marching++;
            }

            Assert.Equal(NormalAttackThreshold, marching);
        }

        // ── Fixture helpers ───────────────────────────────────────────────────────────────────────────────────

        /// <summary>A fully-wired 3-faction sim with the AI PINNED to Normal. Three active factions so a fixture can
        /// hold an ally (Player1), the AI (Player2) and a real enemy (Player3) at once. Ore starts at zero for every
        /// faction, so no build/expand action ever out-scores the attack/raze decision under test.</summary>
        private static SimulationHost NewHost()
        {
            SimulationHost host = SimulationHost.Create(
                NullLogSink.Instance,
                new FactionRegistry(3),
                new FactionDefinition(),
                new FactionDefinition(),
                damageTable: null,
                aiLevel: AiDifficulty.Normal);
            host.ScenarioDirector.LoadScenario(new ScenarioData());
            return host;
        }

        /// <summary>Put <paramref name="a"/> and <paramref name="b"/> on one team, using the SAME canonical encoding
        /// <c>AllianceSeeder</c> writes: the team id is the LOWEST faction slot among the members (an arbitrary team
        /// ordinal would fall outside the mask's domain and be silently dropped by the win scans).</summary>
        private static void PutOnOneTeam(SimulationHost host, Faction a, Faction b)
        {
            int canonical = (int)a < (int)b ? (int)a : (int)b;
            host.Alliances.TeamId[(int)a] = canonical;
            host.Alliances.TeamId[(int)b] = canonical;
            Assert.True(host.Alliances.AreAllied(a, b), "fixture precondition: the two factions must be allied");
        }

        /// <summary>A completed CommandCenter for <paramref name="f"/>, plus its deposit base. Returns the slot.</summary>
        private static int PlaceBase(SimulationHost host, Faction f, int x, int z)
        {
            var pos = new FixedVec3(Fixed.FromInt(x), Fixed.Zero, Fixed.FromInt(z));
            int cc = host.Buildings.Create(pos, f, BuildingType.CommandCenter);
            host.Buildings.ConstructionTimer[cc] = Fixed.Zero; // complete
            host.Resources.FactionBase[(int)f]   = pos;
            return cc;
        }

        /// <summary>Idle, damage-bearing combat units for <paramref name="f"/> in a short column at
        /// <paramref name="x"/>. Siege damage so a raze that IS ordered concludes quickly — these fixtures measure the
        /// AI's DECISION, not combat tuning. Returns the FIRST entity id created.</summary>
        private static int PlaceWave(SimulationHost host, Faction f, int count, int x, int z = 0)
        {
            int first = -1;
            for (int i = 0; i < count; i++)
            {
                int u = host.World.Create(
                    new FixedVec3(Fixed.FromInt(x), Fixed.Zero, Fixed.FromInt(z + i * 2 - 4)),
                    f, Fixed.FromInt(80), Fixed.FromInt(3));
                host.World.EffectiveAttackDamage[u] = Fixed.FromInt(10);
                host.World.AttackRange[u]  = Fixed.FromInt(2);
                host.World.AttackSpeed[u]  = Fixed.FromInt(1);
                host.World.DamageTypeOf[u] = DamageType.Siege;   // anti-building
                host.World.ArmorTypeOf[u]  = ArmorType.Medium;
                if (first < 0) first = u;
            }
            return first;
        }

        /// <summary>Step until <paramref name="done"/> holds or the budget runs out; true iff it held.</summary>
        private static bool StepUntil(SimulationHost host, System.Func<SimulationHost, bool> done)
        {
            for (int t = 0; t < MaxTicks; t++)
            {
                host.StepOnce();
                if (done(host)) return true;
            }
            return false;
        }

        /// <summary>Fail if ANY AI unit currently holds a force-attack order pointed at a faction the AI is allied
        /// with — the exact order combat rejects and the AI then re-issues forever.</summary>
        private static void AssertNoOrderAgainstAnAlly(SimulationHost host)
        {
            for (int i = 0; i < host.World.HighWaterMark; i++)
            {
                if (!host.World.IsAlive(i) || host.World.FactionOf[i] != Faction.Player2) continue;

                if (host.World.CommandState[i] == UnitCommand.AttackBuilding
                    && host.Buildings.TryResolveRef(host.World.CommandTarget[i], out int b))
                {
                    Assert.False(host.Alliances.AreAllied(Faction.Player2, host.Buildings.FactionOf[b]),
                        $"AI unit {i} was ordered to AttackBuilding an ALLIED faction's building (slot {b}, " +
                        $"{host.Buildings.FactionOf[b]}) — combat rejects that order and reverts the unit to Idle, " +
                        "which is the DW-439/DW-445 stall.");
                }
            }
        }
    }
}
