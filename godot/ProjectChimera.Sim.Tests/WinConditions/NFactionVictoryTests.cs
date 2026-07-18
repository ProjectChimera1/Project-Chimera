#nullable enable
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Sim;
using Xunit;

namespace ProjectChimera.Sim.Tests.WinConditions
{
    /// <summary>
    /// Story 7.12 — headless coverage for N-faction, team-aware, last-team-standing win resolution: 3–4-faction
    /// built-in elimination, allied-team wipe (whole team wins), team-aware KotH sole-hold + contested-by-two-teams
    /// reset, Timed-Survival team win + designate-dies-early continuation, Assassination/Landmark target-faction-team
    /// loss with the remaining factions resolved by total wipeout, the AllianceStore mask API, and a 4-faction
    /// determinism replay. Godot-free (NullLogSink); drives <c>WinCon.Tick</c> directly for isolated logic and
    /// <c>SimChecksum.Compute</c> for the determinism proof. The 2-faction winner/loser parity (incl.
    /// double-elim → Player2) is pinned by <see cref="WinConditionSystemTests"/>.
    /// </summary>
    public class NFactionVictoryTests
    {
        private static readonly Fixed Dt = SimulationLoop.FixedDt;

        private static SimulationHost Host(int players) => SimulationHost.Create(
            NullLogSink.Instance, new FactionRegistry(players), new FactionDefinition(), new FactionDefinition());

        private static FixedVec3 At(int x, int z) => new FixedVec3(Fixed.FromInt(x), Fixed.Zero, Fixed.FromInt(z));

        private static void ConfigureBuiltin(SimulationHost h) =>
            h.WinCon.Configure(new ScenarioData { WinCondition = WinCondition.DestroyAllBuildings },
                               RegionStore.Empty, null, null);

        private static int CC(SimulationHost h, int x, Faction f) =>
            h.Buildings.Create(At(x, 0), f, BuildingType.CommandCenter);

        private static int Unit(SimulationHost h, int x, int z, Faction f) =>
            h.World.Create(At(x, z), f, Fixed.FromInt(10), Fixed.FromInt(3));

        private static void TickPastGrace(SimulationHost h)
        {
            h.WinState.MatchTicks = WinConditionSystem.GRACE_TICKS - 1;
            h.WinCon.Tick(h.World, Dt);
        }

        private const int WON  = WinStateStore.VERDICT_WON;
        private const int LOST = WinStateStore.VERDICT_LOST;
        private const int NONE = WinStateStore.VERDICT_NONE;

        // ── 3-FFA / 4-FFA built-in elimination → last faction standing ──────────────────────────────────────────

        [Fact]
        public void ThreeFfa_Builtin_Elimination_ContinuesThenLastFactionStandingWins()
        {
            var h = Host(3);
            int b1 = CC(h, -14, Faction.Player1);
            CC(h, 0,  Faction.Player2);
            CC(h, 14, Faction.Player3);
            ConfigureBuiltin(h);

            // Past grace, everyone alive → no verdict, match continues.
            TickPastGrace(h);
            Assert.Equal(0, h.WinState.WinnerFaction());
            Assert.False(h.WinCon.IsFullyResolved());

            // P1 loses all buildings → LOST at this tick; P2, P3 alive → MATCH CONTINUES (no winner yet).
            h.Buildings.Destroy(b1);
            h.WinCon.Tick(h.World, Dt);
            Assert.Equal(LOST, h.WinState.Verdict[(int)Faction.Player1]);
            Assert.Equal(0, h.WinState.WinnerFaction());
            Assert.False(h.WinCon.IsFullyResolved());

            // P2 loses all buildings → only P3 live → P3 WON, P2 LOST.
            int b2 = 1; // P2's CommandCenter is BuildingStore slot 1
            h.Buildings.Destroy(b2);
            h.WinCon.Tick(h.World, Dt);
            Assert.Equal((int)Faction.Player3, h.WinState.WinnerFaction());
            Assert.Equal(WON,  h.WinState.Verdict[(int)Faction.Player3]);
            Assert.Equal(LOST, h.WinState.Verdict[(int)Faction.Player2]);
            Assert.True(h.WinCon.IsFullyResolved());
        }

        [Fact]
        public void FourFfa_Builtin_Elimination_LastFactionStandingWins()
        {
            var h = Host(4);
            CC(h, -14, Faction.Player1);
            CC(h, -4,  Faction.Player2);
            CC(h, 6,   Faction.Player3);
            CC(h, 16,  Faction.Player4);
            ConfigureBuiltin(h);

            TickPastGrace(h); // all alive
            Assert.False(h.WinCon.IsFullyResolved());

            h.Buildings.Destroy(0); h.WinCon.Tick(h.World, Dt); // P1 out
            h.Buildings.Destroy(1); h.WinCon.Tick(h.World, Dt); // P2 out
            Assert.Equal(0, h.WinState.WinnerFaction());        // P3, P4 still live
            h.Buildings.Destroy(2); h.WinCon.Tick(h.World, Dt); // P3 out → P4 last

            Assert.Equal((int)Faction.Player4, h.WinState.WinnerFaction());
            Assert.Equal(WON, h.WinState.Verdict[(int)Faction.Player4]);
            foreach (var f in new[] { Faction.Player1, Faction.Player2, Faction.Player3 })
                Assert.Equal(LOST, h.WinState.Verdict[(int)f]);
        }

        // ── 2v2 allied team wipe → the whole surviving team WINS ────────────────────────────────────────────────

        [Fact]
        public void TwoVsTwo_AlliedTeamWipe_WholeSurvivingTeamWins()
        {
            var h = Host(4);
            // Teams: {P1,P2} (team id 1) vs {P3,P4} (team id 3).
            h.Alliances.TeamId[(int)Faction.Player2] = (int)Faction.Player1;
            h.Alliances.TeamId[(int)Faction.Player4] = (int)Faction.Player3;
            Assert.True(h.Alliances.AreAllied(Faction.Player1, Faction.Player2));
            Assert.False(h.Alliances.AreAllied(Faction.Player1, Faction.Player3));

            CC(h, -14, Faction.Player1);
            CC(h, -4,  Faction.Player2);
            int b3 = CC(h, 6,  Faction.Player3);
            int b4 = CC(h, 16, Faction.Player4);
            ConfigureBuiltin(h);

            TickPastGrace(h);
            Assert.False(h.WinCon.IsFullyResolved());

            // P3 out — team {P3,P4} still live via P4 → match continues.
            h.Buildings.Destroy(b3);
            h.WinCon.Tick(h.World, Dt);
            Assert.Equal(0, h.WinState.WinnerFaction());
            Assert.Equal(LOST, h.WinState.Verdict[(int)Faction.Player3]);

            // P4 out — team B fully wiped → team A {P1,P2} both WIN.
            h.Buildings.Destroy(b4);
            h.WinCon.Tick(h.World, Dt);
            Assert.Equal(WON,  h.WinState.Verdict[(int)Faction.Player1]);
            Assert.Equal(WON,  h.WinState.Verdict[(int)Faction.Player2]);
            Assert.Equal(LOST, h.WinState.Verdict[(int)Faction.Player3]);
            Assert.Equal(LOST, h.WinState.Verdict[(int)Faction.Player4]);
            Assert.Equal((int)Faction.Player1, h.WinState.WinnerFaction()); // lowest WON slot = team representative
        }

        // ── Team-aware King of the Hill ─────────────────────────────────────────────────────────────────────────

        private static RegionStore OneRegion(string id, int min, int max) =>
            new RegionStore(new[] { id },
                            new[] { new FixedRect(Fixed.FromInt(min), Fixed.FromInt(min), Fixed.FromInt(max), Fixed.FromInt(max)) });

        [Fact]
        public void KotH_AlliedCoHolders_DoNotContest_TeamHoldsAndWholeTeamWins()
        {
            var h = Host(3);
            h.Alliances.TeamId[(int)Faction.Player2] = (int)Faction.Player1; // {P1,P2} allied
            Unit(h, 0, 0, Faction.Player1);  // both allies in the zone — they must NOT contest each other
            Unit(h, 1, 1, Faction.Player2);
            // P3 outside the zone.
            var regions = OneRegion("zone", -5, 5);
            h.WinCon.Configure(new ScenarioData
            {
                WinConditionSpec = new WinConditionSpec { Preset = WinPresetKind.KingOfTheHill, RegionId = "zone", HoldTicks = 5 },
            }, regions, null, null);

            h.WinState.MatchTicks = WinConditionSystem.GRACE_TICKS - 1;
            for (int t = 0; t < 4; t++)
            {
                h.WinCon.Tick(h.World, Dt);
                Assert.Equal(0, h.WinState.WinnerFaction()); // team counter 1..4 < 5
            }
            h.WinCon.Tick(h.World, Dt); // team reaches hold_ticks → whole team wins

            Assert.Equal(WON,  h.WinState.Verdict[(int)Faction.Player1]);
            Assert.Equal(WON,  h.WinState.Verdict[(int)Faction.Player2]);
            Assert.Equal(LOST, h.WinState.Verdict[(int)Faction.Player3]);
        }

        [Fact]
        public void KotH_TwoTeamsContest_NoTeamSolelyHolds_AllCountersReset()
        {
            var h = Host(3);
            h.Alliances.TeamId[(int)Faction.Player2] = (int)Faction.Player1; // {P1,P2} vs {P3}
            Unit(h, 0, 0, Faction.Player1); // team A in zone
            Unit(h, 2, 2, Faction.Player3); // team B (P3) in zone → contested
            var regions = OneRegion("zone", -5, 5);
            h.WinCon.Configure(new ScenarioData
            {
                WinConditionSpec = new WinConditionSpec { Preset = WinPresetKind.KingOfTheHill, RegionId = "zone", HoldTicks = 3 },
            }, regions, null, null);

            h.WinState.MatchTicks = WinConditionSystem.GRACE_TICKS - 1;
            for (int t = 0; t < 20; t++) h.WinCon.Tick(h.World, Dt);

            Assert.Equal(0, h.WinState.WinnerFaction());
            Assert.Equal(0, h.WinState.KothHoldTicks[(int)Faction.Player1]);
            Assert.Equal(0, h.WinState.KothHoldTicks[(int)Faction.Player3]);
        }

        // ── Timed Survival (3 factions) ─────────────────────────────────────────────────────────────────────────

        [Fact]
        public void TimedSurvival_ThreeFactions_DesignatedAliveAtDeadline_WholeTeamWins()
        {
            var h = Host(3); // teams-of-1 FFA; designated = P2
            Unit(h, 0, 0, Faction.Player1);
            Unit(h, 5, 5, Faction.Player2); // the designated survivor
            Unit(h, -5, -5, Faction.Player3);
            h.WinCon.Configure(new ScenarioData
            {
                WinConditionSpec = new WinConditionSpec { Preset = WinPresetKind.TimedSurvival, FactionSlot = 1, SurviveTicks = 3 },
            }, RegionStore.Empty, null, null);

            h.WinState.MatchTicks = WinConditionSystem.GRACE_TICKS - 1;
            for (int t = 0; t < 3; t++) h.WinCon.Tick(h.World, Dt); // countdown 3→0

            Assert.Equal((int)Faction.Player2, h.WinState.WinnerFaction());
            Assert.Equal(WON,  h.WinState.Verdict[(int)Faction.Player2]);
            Assert.Equal(LOST, h.WinState.Verdict[(int)Faction.Player1]);
            Assert.Equal(LOST, h.WinState.Verdict[(int)Faction.Player3]);
        }

        [Fact]
        public void TimedSurvival_DesignatedDiesEarly_TeamLoses_RemainingPlayToLastStanding()
        {
            var h = Host(3); // designated = P2, FFA
            Unit(h, 0, 0, Faction.Player1);
            int p2 = Unit(h, 5, 5, Faction.Player2);
            int p3 = Unit(h, -5, -5, Faction.Player3);
            h.WinCon.Configure(new ScenarioData
            {
                WinConditionSpec = new WinConditionSpec { Preset = WinPresetKind.TimedSurvival, FactionSlot = 1, SurviveTicks = 1000 },
            }, RegionStore.Empty, null, null);

            // Designated P2 eliminated before the deadline → P2 LOST, match CONTINUES for P1 vs P3.
            h.World.Destroy(p2);
            TickPastGrace(h);
            Assert.Equal(LOST, h.WinState.Verdict[(int)Faction.Player2]);
            Assert.Equal(0, h.WinState.WinnerFaction());
            Assert.False(h.WinCon.IsFullyResolved());

            // P3 eliminated → P1 is the last team standing.
            h.World.Destroy(p3);
            h.WinCon.Tick(h.World, Dt);
            Assert.Equal((int)Faction.Player1, h.WinState.WinnerFaction());
            Assert.Equal(LOST, h.WinState.Verdict[(int)Faction.Player3]);
        }

        // ── Assassination / Landmark in a 3–4-faction match ─────────────────────────────────────────────────────

        [Fact]
        public void Assassination_FourFfa_LeaderDies_OwnerTeamLoses_OthersByWipeout_LastTeamWins()
        {
            var h = Host(4); // FFA; P1's leader is the designated target
            int leader = Unit(h, 0, 0, Faction.Player1);
            int p2 = Unit(h, 5, 5, Faction.Player2);
            int p3 = Unit(h, -5, -5, Faction.Player3);
            Unit(h, 8, 8, Faction.Player4);
            var scenario = new ScenarioData
            {
                Units = new[] { new ScenarioUnit { UnitId = "leader", Slot = 0, X = 0, Z = 0 } },
                WinConditionSpec = new WinConditionSpec { Preset = WinPresetKind.Assassination, LeaderUnitIndex = 0 },
            };
            h.WinCon.Configure(scenario, RegionStore.Empty, new[] { leader }, null);

            // Leader dies → P1 LOST at the death tick; P2,P3,P4 alive → match continues (no wipeout yet).
            h.World.Destroy(leader);
            TickPastGrace(h);
            Assert.Equal(LOST, h.WinState.Verdict[(int)Faction.Player1]);
            Assert.Equal(0, h.WinState.WinnerFaction());

            // Remaining factions eliminated by TOTAL WIPEOUT → last team standing wins.
            h.World.Destroy(p2); h.WinCon.Tick(h.World, Dt);
            h.World.Destroy(p3); h.WinCon.Tick(h.World, Dt);
            Assert.Equal((int)Faction.Player4, h.WinState.WinnerFaction());
            Assert.Equal(WON, h.WinState.Verdict[(int)Faction.Player4]);
        }

        [Fact]
        public void Landmark_ThreeFactions_StructureDies_OwnerTeamLoses_OtherByWipeout_LastTeamWins()
        {
            var h = Host(3); // FFA; P1's landmark is the designated target
            int slot = CC(h, -14, Faction.Player1);
            int p2 = Unit(h, 5, 5, Faction.Player2);
            Unit(h, -5, -5, Faction.Player3);
            var scenario = new ScenarioData
            {
                Buildings = new[] { new ScenarioBuilding { Type = "CommandCenter", Slot = 0, X = -14, Z = 0 } },
                WinConditionSpec = new WinConditionSpec { Preset = WinPresetKind.LandmarkDestruction, StructureIndex = 0 },
            };
            h.WinCon.Configure(scenario, RegionStore.Empty, null, new[] { slot });

            TickPastGrace(h);
            Assert.Equal(0, h.WinState.WinnerFaction()); // landmark intact

            h.Buildings.Destroy(slot);
            h.WinCon.Tick(h.World, Dt);
            Assert.Equal(LOST, h.WinState.Verdict[(int)Faction.Player1]); // owner team loses
            Assert.Equal(0, h.WinState.WinnerFaction());                  // P2, P3 still live

            h.World.Destroy(p2);
            h.WinCon.Tick(h.World, Dt);
            Assert.Equal((int)Faction.Player3, h.WinState.WinnerFaction()); // last team standing
        }

        // ── Simultaneous multi-team elimination tie-break (whole team) + monotone-latch invariant ────────────────

        [Fact]
        public void SimultaneousMutualWipe_HigherSlotTeamWins_WholeTeam()
        {
            var h = Host(4);
            // Teams A={P1,P4} (id 1), B={P2,P3} (id 2).
            h.Alliances.TeamId[(int)Faction.Player4] = (int)Faction.Player1;
            h.Alliances.TeamId[(int)Faction.Player3] = (int)Faction.Player2;
            int c1 = CC(h, -14, Faction.Player1);
            int c2 = CC(h, -4,  Faction.Player2);
            int c3 = CC(h, 6,   Faction.Player3);
            int c4 = CC(h, 16,  Faction.Player4);
            ConfigureBuiltin(h);
            TickPastGrace(h); // all alive

            // Destroy ALL four CCs before a single Tick → both teams eliminated SIMULTANEOUSLY this tick.
            h.Buildings.Destroy(c1); h.Buildings.Destroy(c2); h.Buildings.Destroy(c3); h.Buildings.Destroy(c4);
            h.WinCon.Tick(h.World, Dt);

            // Tie-break: highest slot eliminated this tick = P4 → team A {P1,P4} wins as a WHOLE (both promoted).
            Assert.Equal(WON,  h.WinState.Verdict[(int)Faction.Player1]);
            Assert.Equal(WON,  h.WinState.Verdict[(int)Faction.Player4]);
            Assert.Equal(LOST, h.WinState.Verdict[(int)Faction.Player2]);
            Assert.Equal(LOST, h.WinState.Verdict[(int)Faction.Player3]);
            Assert.Equal((int)Faction.Player1, h.WinState.WinnerFaction()); // lowest WON slot
        }

        [Fact]
        public void StaggeredDeaths_ThenSimultaneousWipe_PriorLostTeammateStaysLost()
        {
            var h = Host(4);
            // Teams A={P1,P4} (id 1), B={P2,P3} (id 2).
            h.Alliances.TeamId[(int)Faction.Player4] = (int)Faction.Player1;
            h.Alliances.TeamId[(int)Faction.Player3] = (int)Faction.Player2;
            int c1 = CC(h, -14, Faction.Player1);
            int c2 = CC(h, -4,  Faction.Player2);
            int c3 = CC(h, 6,   Faction.Player3);
            int c4 = CC(h, 16,  Faction.Player4);
            ConfigureBuiltin(h);
            TickPastGrace(h);

            // P1 dies on an EARLIER tick — team A stays live via P4.
            h.Buildings.Destroy(c1);
            h.WinCon.Tick(h.World, Dt);
            Assert.Equal(LOST, h.WinState.Verdict[(int)Faction.Player1]);
            Assert.False(h.WinCon.IsFullyResolved());

            // Later, P4 (team A's survivor) and BOTH of team B die on the SAME tick → simultaneous last-2-team wipe.
            h.Buildings.Destroy(c4); h.Buildings.Destroy(c2); h.Buildings.Destroy(c3);
            h.WinCon.Tick(h.World, Dt);

            // Tie-break awards team A, but P1 died an EARLIER tick and must STAY LOST (monotone latch): only P4,
            // eliminated THIS tick, is promoted to WON — WinnerFaction() must report P4, never the long-dead P1.
            Assert.Equal(WON,  h.WinState.Verdict[(int)Faction.Player4]);
            Assert.Equal(LOST, h.WinState.Verdict[(int)Faction.Player1]); // NOT resurrected
            Assert.Equal(LOST, h.WinState.Verdict[(int)Faction.Player2]);
            Assert.Equal(LOST, h.WinState.Verdict[(int)Faction.Player3]);
            Assert.Equal((int)Faction.Player4, h.WinState.WinnerFaction());
        }

        // ── KotH team-rep accumulator survives the rep leaving the zone while an ally holds ───────────────────────

        [Fact]
        public void KotH_TeamRepLeavesZone_AllyStillHolds_TeamCounterSurvivesAndWins()
        {
            var h = Host(3);
            h.Alliances.TeamId[(int)Faction.Player2] = (int)Faction.Player1; // team {P1,P2}, rep = P1 (lowest slot)
            int u1 = Unit(h, 0, 0, Faction.Player1); // rep unit in zone
            Unit(h, 1, 1, Faction.Player2);          // ally in zone
            // P3 opponent outside the zone.
            var regions = OneRegion("zone", -5, 5);
            h.WinCon.Configure(new ScenarioData
            {
                WinConditionSpec = new WinConditionSpec { Preset = WinPresetKind.KingOfTheHill, RegionId = "zone", HoldTicks = 6 },
            }, regions, null, null);

            h.WinState.MatchTicks = WinConditionSystem.GRACE_TICKS - 1;
            for (int t = 0; t < 3; t++) h.WinCon.Tick(h.World, Dt); // team holds; rep counter reaches 3
            Assert.Equal(3, h.WinState.KothHoldTicks[(int)Faction.Player1]);
            Assert.Equal(0, h.WinState.WinnerFaction());

            // The REP unit leaves the zone (dies) while the ALLY (P2) keeps holding. A per-unit accumulator would
            // reset here; the team-rep accumulator (keyed on the slot-stable rep) must SURVIVE and reach the win.
            h.World.Destroy(u1);
            for (int t = 0; t < 3; t++) h.WinCon.Tick(h.World, Dt); // 4,5,6 → team reaches hold_ticks

            Assert.Equal(WON,  h.WinState.Verdict[(int)Faction.Player1]);
            Assert.Equal(WON,  h.WinState.Verdict[(int)Faction.Player2]);
            Assert.Equal(LOST, h.WinState.Verdict[(int)Faction.Player3]);
        }

        // ── Timed Survival: survive_ticks < grace, designate never spawns → LOSES (never wins by timer) ────────────

        [Fact]
        public void TimedSurvival_SurviveTicksBelowGrace_DesignateNeverSpawns_LosesNotWinsByTimer()
        {
            var h = Host(2); // designated = P2, which NEVER spawns
            Unit(h, 0, 0, Faction.Player1); // P1 alive; no unit created for P2
            h.WinCon.Configure(new ScenarioData
            {
                WinConditionSpec = new WinConditionSpec { Preset = WinPresetKind.TimedSurvival, FactionSlot = 1, SurviveTicks = 3 },
            }, RegionStore.Empty, null, null);

            // survive_ticks (3) < GRACE (90): the countdown reaches 0 well inside the grace window while P2 has never
            // spawned. Loss-by-absence is grace-gated, so P2 must NOT win by timer — a never-alive faction cannot survive.
            for (int t = 0; t < 3; t++) h.WinCon.Tick(h.World, Dt);
            Assert.Equal(NONE, h.WinState.Verdict[(int)Faction.Player2]); // NOT won
            Assert.Equal(0, h.WinState.WinnerFaction());

            // Past grace, P2 is still absent → it deservedly LOSES, and P1 wins by last-team-standing.
            h.WinState.MatchTicks = WinConditionSystem.GRACE_TICKS - 1;
            h.WinCon.Tick(h.World, Dt);
            Assert.Equal(LOST, h.WinState.Verdict[(int)Faction.Player2]);
            Assert.Equal((int)Faction.Player1, h.WinState.WinnerFaction());
        }

        // ── AllianceStore mask API (FFA default / AreAllied / Clear) ────────────────────────────────────────────

        [Fact]
        public void AllianceStore_FfaDefault_AreAllied_And_Clear()
        {
            var a = new AllianceStore();
            // FFA default: team id == slot index; a faction is allied only with itself.
            Assert.Equal((int)Faction.Player1, a.TeamOf(Faction.Player1));
            Assert.Equal((int)Faction.Player3, a.TeamOf(Faction.Player3));
            Assert.True(a.AreAllied(Faction.Player2, Faction.Player2));  // self
            Assert.True(a.AreAllied(Faction.Neutral, Faction.Neutral));  // self, even Neutral
            Assert.False(a.AreAllied(Faction.Player1, Faction.Player2));

            // Share a team id → allied both ways.
            a.TeamId[(int)Faction.Player2] = (int)Faction.Player1;
            Assert.True(a.AreAllied(Faction.Player1, Faction.Player2));
            Assert.True(a.AreAllied(Faction.Player2, Faction.Player1));
            Assert.False(a.AreAllied(Faction.Player1, Faction.Player3));

            // Clear restores FFA.
            a.Clear();
            Assert.False(a.AreAllied(Faction.Player1, Faction.Player2));
            Assert.Equal((int)Faction.Player2, a.TeamOf(Faction.Player2));
        }

        // ── Determinism: two runs of the same seeded 4-faction scenario + tick sequence → byte-identical checksum ──

        [Fact]
        public void FourFaction_Replay_TwoRuns_ByteIdenticalChecksum()
        {
            uint Run()
            {
                var h = Host(4);
                // A non-trivial 2v2 alliance mask (folded at v20) + a FULL elimination sequence driven to resolution,
                // so the folded checksum reflects the evolving win-state (KothHoldTicks stay 0, but SurvivalRemaining
                // is untouched and every faction's Verdict transitions latch through the fold).
                h.Alliances.TeamId[(int)Faction.Player2] = (int)Faction.Player1; // team {P1,P2}
                h.Alliances.TeamId[(int)Faction.Player4] = (int)Faction.Player3; // team {P3,P4}
                int c1 = CC(h, -14, Faction.Player1);
                int c2 = CC(h, -4,  Faction.Player2);
                int c3 = CC(h, 6,   Faction.Player3);
                int c4 = CC(h, 16,  Faction.Player4);
                ConfigureBuiltin(h);

                h.WinState.MatchTicks = WinConditionSystem.GRACE_TICKS - 1;
                h.WinCon.Tick(h.World, Dt);   // all alive
                h.Buildings.Destroy(c3); h.WinCon.Tick(h.World, Dt); // P3 out (team {P3,P4} live via P4)
                h.Buildings.Destroy(c4); h.WinCon.Tick(h.World, Dt); // P4 out → team {P1,P2} wins as a whole
                for (int t = 0; t < 4; t++) h.WinCon.Tick(h.World, Dt); // keep ticking after resolution (must be inert)
                Assert.True(h.WinCon.IsFullyResolved()); // the sequence really reaches a terminal state
                return SimChecksum.Compute(h.World, h.Buildings, h.Resources, new FactionRegistry(4),
                                           winState: h.WinState, alliances: h.Alliances);
            }

            Assert.Equal(Run(), Run());
        }
    }
}
