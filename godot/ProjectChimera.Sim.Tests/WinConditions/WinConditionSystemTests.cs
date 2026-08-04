#nullable enable
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Sim;
using Xunit;

namespace ProjectChimera.Sim.Tests.WinConditions
{
    /// <summary>
    /// Story 7.11 — headless coverage for the sim-layer <see cref="WinConditionSystem"/>: the two built-ins pick the
    /// SAME winner/loser the old <c>MainScene.CheckWinCondition</c> switch produced, each of the four T1 presets
    /// resolves the correct faction, the KotH contested/sole-hold rule holds, the grace period defers the verdict,
    /// and the win-state folds deterministically. Godot-free (NullLogSink); drives <c>WinCon.Tick</c> directly for
    /// isolated logic and <c>SimChecksum.Compute</c> for the determinism proof.
    /// </summary>
    public class WinConditionSystemTests
    {
        private static readonly Fixed Dt = SimulationLoop.FixedDt;

        private static SimulationHost BuildHost() => SimulationHost.Create(
            NullLogSink.Instance, new FactionRegistry(2), new FactionDefinition(), new FactionDefinition());

        private static FixedVec3 At(int x, int z) => new FixedVec3(Fixed.FromInt(x), Fixed.Zero, Fixed.FromInt(z));

        /// <summary>Tick the win system just past the grace boundary (advance MatchTicks to grace-1, then one tick).</summary>
        private static void TickPastGrace(SimulationHost h)
        {
            h.WinState.MatchTicks = WinConditionSystem.GRACE_TICKS - 1;
            h.WinCon.Tick(h.World, Dt);
        }

        // ── Built-in parity: same winner/loser as the old MainScene switch ──────────────────────────────────────

        [Fact]
        public void DestroyAllBuildings_P1HasBuilding_P2None_P1Wins_MatchesOldSwitch()
        {
            var h = BuildHost();
            h.Buildings.Create(At(-14, 0), Faction.Player1, BuildingType.CommandCenter); // P1 alive, P2 none
            h.WinCon.Configure(new ScenarioData { WinCondition = WinCondition.DestroyAllBuildings },
                               RegionStore.Empty, null, null);

            TickPastGrace(h);

            // Old switch: `if (!p1Alive) ShowGameOver(2); else if (!p2Alive) ShowGameOver(1);` → P1 wins.
            Assert.Equal((int)Faction.Player1, h.WinState.WinnerFaction());
            Assert.Equal(WinStateStore.VERDICT_WON,  h.WinState.Verdict[(int)Faction.Player1]);
            Assert.Equal(WinStateStore.VERDICT_LOST, h.WinState.Verdict[(int)Faction.Player2]);
        }

        [Fact]
        public void DestroyAllBuildings_P2HasBuilding_P1None_P2Wins_MatchesOldSwitch()
        {
            var h = BuildHost();
            h.Buildings.Create(At(14, 0), Faction.Player2, BuildingType.CommandCenter); // P2 alive, P1 none
            h.WinCon.Configure(new ScenarioData { WinCondition = WinCondition.DestroyAllBuildings },
                               RegionStore.Empty, null, null);

            TickPastGrace(h);

            Assert.Equal((int)Faction.Player2, h.WinState.WinnerFaction());
        }

        [Fact]
        public void EliminateAllUnits_P1HasUnit_P2None_P1Wins_MatchesOldSwitch()
        {
            var h = BuildHost();
            h.World.Create(At(0, 0), Faction.Player1, Fixed.FromInt(10), Fixed.FromInt(3)); // P1 alive, P2 none
            h.WinCon.Configure(new ScenarioData { WinCondition = WinCondition.EliminateAllUnits },
                               RegionStore.Empty, null, null);

            TickPastGrace(h);

            Assert.Equal((int)Faction.Player1, h.WinState.WinnerFaction());
        }

        [Fact]
        public void BothSidesAlive_NoVerdict()
        {
            var h = BuildHost();
            h.Buildings.Create(At(-14, 0), Faction.Player1, BuildingType.CommandCenter);
            h.Buildings.Create(At(14, 0),  Faction.Player2, BuildingType.CommandCenter);
            h.WinCon.Configure(new ScenarioData { WinCondition = WinCondition.DestroyAllBuildings },
                               RegionStore.Empty, null, null);

            TickPastGrace(h);

            Assert.Equal(0, h.WinState.WinnerFaction());
            Assert.False(h.WinState.IsResolved());
        }

        [Fact]
        public void Grace_NoVerdict_BeforeGraceElapses_EvenInEndState()
        {
            var h = BuildHost();
            h.Buildings.Create(At(-14, 0), Faction.Player1, BuildingType.CommandCenter); // P1 alive, P2 none → would win
            h.WinCon.Configure(new ScenarioData { WinCondition = WinCondition.DestroyAllBuildings },
                               RegionStore.Empty, null, null);

            // One tick, well inside the grace window.
            h.WinCon.Tick(h.World, Dt);
            Assert.Equal(0, h.WinState.WinnerFaction());

            // Advance to just before grace end — still no verdict.
            h.WinState.MatchTicks = WinConditionSystem.GRACE_TICKS - 2;
            h.WinCon.Tick(h.World, Dt); // → MatchTicks == GRACE-1 (< GRACE) → no latch
            Assert.Equal(0, h.WinState.WinnerFaction());

            // The very next tick crosses grace → the verdict latches.
            h.WinCon.Tick(h.World, Dt);
            Assert.Equal((int)Faction.Player1, h.WinState.WinnerFaction());
        }

        // ── King of the Hill ────────────────────────────────────────────────────────────────────────────────────

        private static RegionStore OneRegion(string id, int min, int max) =>
            new RegionStore(new[] { id },
                            new[] { new FixedRect(Fixed.FromInt(min), Fixed.FromInt(min), Fixed.FromInt(max), Fixed.FromInt(max)) });

        [Fact]
        public void KingOfTheHill_SoleHold_ForNContiguousTicks_Wins()
        {
            var h = BuildHost();
            h.World.Create(At(0, 0), Faction.Player1, Fixed.FromInt(10), Fixed.FromInt(3)); // P1 unit inside zone
            // DW-188: the opponent must EXIST (outside the zone) — an asset-less faction now latches LOST via the
            // KotH elimination fallback, which would resolve the match before the hold completes.
            h.World.Create(At(20, 20), Faction.Player2, Fixed.FromInt(10), Fixed.FromInt(3));
            var regions = OneRegion("zone", -5, 5);
            h.WinCon.Configure(new ScenarioData
            {
                WinConditionSpec = new WinConditionSpec { Preset = WinPresetKind.KingOfTheHill, RegionId = "zone", HoldTicks = 5 },
            }, regions, null, null);

            h.WinState.MatchTicks = WinConditionSystem.GRACE_TICKS - 1;
            for (int t = 0; t < 4; t++)
            {
                h.WinCon.Tick(h.World, Dt);
                Assert.Equal(0, h.WinState.WinnerFaction()); // counter 1..4 < 5
            }
            h.WinCon.Tick(h.World, Dt); // counter reaches 5 → win
            Assert.Equal((int)Faction.Player1, h.WinState.WinnerFaction());
        }

        [Fact]
        public void KingOfTheHill_Contested_CounterDoesNotAdvance_AndResets()
        {
            var h = BuildHost();
            h.World.Create(At(0, 0), Faction.Player1, Fixed.FromInt(10), Fixed.FromInt(3));
            h.World.Create(At(1, 1), Faction.Player2, Fixed.FromInt(10), Fixed.FromInt(3)); // both inside → contested
            var regions = OneRegion("zone", -5, 5);
            h.WinCon.Configure(new ScenarioData
            {
                WinConditionSpec = new WinConditionSpec { Preset = WinPresetKind.KingOfTheHill, RegionId = "zone", HoldTicks = 3 },
            }, regions, null, null);

            h.WinState.MatchTicks = WinConditionSystem.GRACE_TICKS - 1;
            for (int t = 0; t < 20; t++) h.WinCon.Tick(h.World, Dt);

            Assert.Equal(0, h.WinState.WinnerFaction());
            Assert.Equal(0, h.WinState.KothHoldTicks[(int)Faction.Player1]);
            Assert.Equal(0, h.WinState.KothHoldTicks[(int)Faction.Player2]);
        }

        [Fact]
        public void KingOfTheHill_ResetsToZero_WhenSoleHolderLosesExclusivity()
        {
            var h = BuildHost();
            int p1 = h.World.Create(At(0, 0), Faction.Player1, Fixed.FromInt(10), Fixed.FromInt(3));
            // DW-188: keep P2 alive (outside the zone) so the wipeout fallback cannot resolve the match early.
            h.World.Create(At(20, 20), Faction.Player2, Fixed.FromInt(10), Fixed.FromInt(3));
            var regions = OneRegion("zone", -5, 5);
            h.WinCon.Configure(new ScenarioData
            {
                WinConditionSpec = new WinConditionSpec { Preset = WinPresetKind.KingOfTheHill, RegionId = "zone", HoldTicks = 100 },
            }, regions, null, null);

            h.WinState.MatchTicks = WinConditionSystem.GRACE_TICKS - 1;
            for (int t = 0; t < 3; t++) h.WinCon.Tick(h.World, Dt);
            Assert.Equal(3, h.WinState.KothHoldTicks[(int)Faction.Player1]);

            // A P2 unit enters → contested → the P1 counter resets to 0.
            h.World.Create(At(1, 1), Faction.Player2, Fixed.FromInt(10), Fixed.FromInt(3));
            h.WinCon.Tick(h.World, Dt);
            Assert.Equal(0, h.WinState.KothHoldTicks[(int)Faction.Player1]);
        }

        // ── Timed Survival ──────────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void TimedSurvival_DesignatedFactionAliveAtDeadline_Wins()
        {
            var h = BuildHost();
            h.World.Create(At(0, 0), Faction.Player1, Fixed.FromInt(10), Fixed.FromInt(3)); // survivor stays alive
            h.WinCon.Configure(new ScenarioData
            {
                WinConditionSpec = new WinConditionSpec { Preset = WinPresetKind.TimedSurvival, FactionSlot = 0, SurviveTicks = 3 },
            }, RegionStore.Empty, null, null);

            h.WinState.MatchTicks = WinConditionSystem.GRACE_TICKS - 1;
            for (int t = 0; t < 3; t++) h.WinCon.Tick(h.World, Dt); // countdown 3→0
            Assert.Equal((int)Faction.Player1, h.WinState.WinnerFaction());
        }

        [Fact]
        public void TimedSurvival_DesignatedFactionEliminatedBeforeDeadline_Loses()
        {
            var h = BuildHost();
            int u = h.World.Create(At(0, 0), Faction.Player1, Fixed.FromInt(10), Fixed.FromInt(3));
            h.World.Create(At(5, 5), Faction.Player2, Fixed.FromInt(10), Fixed.FromInt(3)); // opponent survives → wins
            h.WinCon.Configure(new ScenarioData
            {
                WinConditionSpec = new WinConditionSpec { Preset = WinPresetKind.TimedSurvival, FactionSlot = 0, SurviveTicks = 1000 },
            }, RegionStore.Empty, null, null);

            h.World.Destroy(u); // designated faction eliminated
            TickPastGrace(h);

            Assert.Equal((int)Faction.Player2, h.WinState.WinnerFaction());
            Assert.Equal(WinStateStore.VERDICT_LOST, h.WinState.Verdict[(int)Faction.Player1]);
        }

        // ── Assassination ───────────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void Assassination_LeaderDies_OwnerLoses()
        {
            var h = BuildHost();
            int leader = h.World.Create(At(0, 0), Faction.Player1, Fixed.FromInt(10), Fixed.FromInt(3));
            h.World.Create(At(5, 5), Faction.Player2, Fixed.FromInt(10), Fixed.FromInt(3)); // the winner
            var scenario = new ScenarioData
            {
                Units = new[] { new ScenarioUnit { UnitId = "leader", Slot = 0, X = 0, Z = 0 } },
                WinConditionSpec = new WinConditionSpec { Preset = WinPresetKind.Assassination, LeaderUnitIndex = 0 },
            };
            h.WinCon.Configure(scenario, RegionStore.Empty, new[] { leader }, null);

            // Leader alive → no verdict.
            TickPastGrace(h);
            Assert.Equal(0, h.WinState.WinnerFaction());

            // Leader dies → owner (P1) loses, P2 wins.
            h.World.Destroy(leader);
            h.WinCon.Tick(h.World, Dt);
            Assert.Equal((int)Faction.Player2, h.WinState.WinnerFaction());
        }

        // ── DW-184: the leader holds a generation-stamped ref — a same-tick same-faction recycle still loses ─────

        [Fact]
        public void Assassination_SameTickSlotRecycle_SameFaction_LossStillLatches()
        {
            var h = BuildHost();
            int leader = h.World.Create(At(0, 0), Faction.Player1, Fixed.FromInt(10), Fixed.FromInt(3));
            h.World.Create(At(5, 5), Faction.Player2, Fixed.FromInt(10), Fixed.FromInt(3)); // the winner
            var scenario = new ScenarioData
            {
                Units = new[] { new ScenarioUnit { UnitId = "leader", Slot = 0, X = 0, Z = 0 } },
                WinConditionSpec = new WinConditionSpec { Preset = WinPresetKind.Assassination, LeaderUnitIndex = 0 },
            };
            h.WinCon.Configure(scenario, RegionStore.Empty, new[] { leader }, null);

            TickPastGrace(h);
            Assert.Equal(0, h.WinState.WinnerFaction()); // leader alive → no verdict

            // The ABA edge (DW-184, the Landmark P6 twin): the leader dies and its slot recycles into a NEW
            // same-faction unit BEFORE the next win tick (EntityWorld.Destroy frees the slot to the LIFO free-list
            // immediately; a same-tick Create pops it — entity ids had no generation counter). The old raw-id
            // IsAlive+faction check saw an alive, same-faction unit and masked the assassination, leaving the
            // leader effectively immortal; the generation-stamped packed ref must still latch the loss.
            h.World.Destroy(leader);
            int recycled = h.World.Create(At(0, 0), Faction.Player1, Fixed.FromInt(10), Fixed.FromInt(3));
            Assert.Equal(leader, recycled); // precondition: the world really recycled the same slot

            h.WinCon.Tick(h.World, Dt);
            Assert.Equal((int)Faction.Player2, h.WinState.WinnerFaction()); // the DESIGNATED leader is gone → P1 loses
            Assert.Equal(WinStateStore.VERDICT_LOST, h.WinState.Verdict[(int)Faction.Player1]);
        }

        // ── Landmark Destruction ────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void LandmarkDestruction_StructureDestroyed_OwnerLoses()
        {
            var h = BuildHost();
            int slot = h.Buildings.Create(At(-14, 0), Faction.Player1, BuildingType.CommandCenter);
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
            Assert.Equal((int)Faction.Player2, h.WinState.WinnerFaction()); // owner P1 loses
        }

        // ── Double-elimination tie-break (T6): both sides gone → Player2 wins (old `if(!p1) ShowGameOver(2)` bias) ──

        [Fact]
        public void DestroyAllBuildings_BothSidesZeroBuildings_Player2Wins_MatchesOldSwitchBias()
        {
            var h = BuildHost();
            // No buildings for EITHER faction after grace → simultaneous double-elimination. The old switch's
            // `if (!p1Alive) ShowGameOver(2); else if (!p2Alive) ShowGameOver(1);` resolves the !p1 branch first.
            h.WinCon.Configure(new ScenarioData { WinCondition = WinCondition.DestroyAllBuildings },
                               RegionStore.Empty, null, null);

            TickPastGrace(h);

            Assert.Equal((int)Faction.Player2, h.WinState.WinnerFaction());
            Assert.Equal(WinStateStore.VERDICT_WON,  h.WinState.Verdict[(int)Faction.Player2]);
            Assert.Equal(WinStateStore.VERDICT_LOST, h.WinState.Verdict[(int)Faction.Player1]);
        }

        // ── Verdict-latch finality (T4): once resolved the winner never flips, even under changed state ────────────

        [Fact]
        public void Verdict_IsFinal_WinnerDoesNotFlip_WhenStateChangesAfterResolve()
        {
            var h = BuildHost();
            // P1 sole-holds the zone and wins. DW-188: P2 must be alive (outside the zone) so the resolution is the
            // HOLD-win this test pins, not the wipeout fallback.
            int p1 = h.World.Create(At(0, 0), Faction.Player1, Fixed.FromInt(10), Fixed.FromInt(3));
            h.World.Create(At(20, 20), Faction.Player2, Fixed.FromInt(10), Fixed.FromInt(3));
            var regions = OneRegion("zone", -5, 5);
            h.WinCon.Configure(new ScenarioData
            {
                WinConditionSpec = new WinConditionSpec { Preset = WinPresetKind.KingOfTheHill, RegionId = "zone", HoldTicks = 5 },
            }, regions, null, null);

            h.WinState.MatchTicks = WinConditionSystem.GRACE_TICKS - 1;
            for (int t = 0; t < 5; t++) h.WinCon.Tick(h.World, Dt);
            Assert.Equal((int)Faction.Player1, h.WinState.WinnerFaction());

            // Now flip the on-field situation hard: remove P1, let P2 sole-hold the zone for a long time.
            h.World.Destroy(p1);
            h.World.Create(At(0, 0), Faction.Player2, Fixed.FromInt(10), Fixed.FromInt(3));
            for (int t = 0; t < 50; t++) h.WinCon.Tick(h.World, Dt);

            // The latch is final — P1 stays the winner, P2 never latches WON, no LOST flips to something else.
            Assert.Equal((int)Faction.Player1, h.WinState.WinnerFaction());
            Assert.Equal(WinStateStore.VERDICT_WON,  h.WinState.Verdict[(int)Faction.Player1]);
            Assert.Equal(WinStateStore.VERDICT_LOST, h.WinState.Verdict[(int)Faction.Player2]);
        }

        // ── Determinism: EACH of the six conditions folds byte-identically across two identical tick sequences (T5) ──

        // Each builder configures a fresh host so that ticking past grace RESOLVES the named condition. Returned as
        // MemberData so every condition is an independent, named determinism case.
        public static readonly object[][] SixConditions =
        {
            new object[] { "DestroyAllBuildings" },
            new object[] { "EliminateAllUnits" },
            new object[] { "KingOfTheHill" },
            new object[] { "TimedSurvival" },
            new object[] { "Assassination" },
            new object[] { "LandmarkDestruction" },
        };

        private static uint RunCondition(string which)
        {
            var h = BuildHost();
            switch (which)
            {
                case "DestroyAllBuildings":
                    h.Buildings.Create(At(-14, 0), Faction.Player1, BuildingType.CommandCenter); // P1 wins
                    h.WinCon.Configure(new ScenarioData { WinCondition = WinCondition.DestroyAllBuildings },
                                       RegionStore.Empty, null, null);
                    TickPastGrace(h);
                    break;

                case "EliminateAllUnits":
                    h.World.Create(At(0, 0), Faction.Player1, Fixed.FromInt(10), Fixed.FromInt(3)); // P1 wins
                    h.WinCon.Configure(new ScenarioData { WinCondition = WinCondition.EliminateAllUnits },
                                       RegionStore.Empty, null, null);
                    TickPastGrace(h);
                    break;

                case "KingOfTheHill":
                {
                    h.World.Create(At(0, 0), Faction.Player1, Fixed.FromInt(10), Fixed.FromInt(3));
                    // DW-188: live P2 outside the zone keeps this the HOLD-win determinism case (no wipeout latch).
                    h.World.Create(At(20, 20), Faction.Player2, Fixed.FromInt(10), Fixed.FromInt(3));
                    var regions = OneRegion("zone", -5, 5);
                    h.WinCon.Configure(new ScenarioData
                    {
                        WinConditionSpec = new WinConditionSpec { Preset = WinPresetKind.KingOfTheHill, RegionId = "zone", HoldTicks = 5 },
                    }, regions, null, null);
                    h.WinState.MatchTicks = WinConditionSystem.GRACE_TICKS - 1;
                    for (int t = 0; t < 6; t++) h.WinCon.Tick(h.World, Dt);
                    break;
                }

                case "TimedSurvival":
                    h.World.Create(At(0, 0), Faction.Player1, Fixed.FromInt(10), Fixed.FromInt(3)); // survivor
                    h.WinCon.Configure(new ScenarioData
                    {
                        WinConditionSpec = new WinConditionSpec { Preset = WinPresetKind.TimedSurvival, FactionSlot = 0, SurviveTicks = 3 },
                    }, RegionStore.Empty, null, null);
                    h.WinState.MatchTicks = WinConditionSystem.GRACE_TICKS - 1;
                    for (int t = 0; t < 4; t++) h.WinCon.Tick(h.World, Dt);
                    break;

                case "Assassination":
                {
                    int leader = h.World.Create(At(0, 0), Faction.Player1, Fixed.FromInt(10), Fixed.FromInt(3));
                    h.World.Create(At(5, 5), Faction.Player2, Fixed.FromInt(10), Fixed.FromInt(3));
                    var scenario = new ScenarioData
                    {
                        Units = new[] { new ScenarioUnit { UnitId = "leader", Slot = 0, X = 0, Z = 0 } },
                        WinConditionSpec = new WinConditionSpec { Preset = WinPresetKind.Assassination, LeaderUnitIndex = 0 },
                    };
                    h.WinCon.Configure(scenario, RegionStore.Empty, new[] { leader }, null);
                    h.World.Destroy(leader);
                    TickPastGrace(h);
                    break;
                }

                case "LandmarkDestruction":
                {
                    int slot = h.Buildings.Create(At(-14, 0), Faction.Player1, BuildingType.CommandCenter);
                    h.World.Create(At(5, 5), Faction.Player2, Fixed.FromInt(10), Fixed.FromInt(3));
                    var scenario = new ScenarioData
                    {
                        Buildings = new[] { new ScenarioBuilding { Type = "CommandCenter", Slot = 0, X = -14, Z = 0 } },
                        WinConditionSpec = new WinConditionSpec { Preset = WinPresetKind.LandmarkDestruction, StructureIndex = 0 },
                    };
                    h.WinCon.Configure(scenario, RegionStore.Empty, null, new[] { slot });
                    h.Buildings.Destroy(slot);
                    TickPastGrace(h);
                    break;
                }
            }

            Assert.True(h.WinState.IsResolved(), $"condition '{which}' should have resolved");
            return SimChecksum.Compute(h.World, h.Buildings, h.Resources, new FactionRegistry(2), winState: h.WinState);
        }

        [Theory]
        [MemberData(nameof(SixConditions))]
        public void EachCondition_FoldsDeterministically_AcrossTwoIdenticalRuns(string which)
        {
            Assert.Equal(RunCondition(which), RunCondition(which));
        }

        // ── Review P1: a LOST-only outcome (single-active-faction preset loss) still ENDS the match ─────────────

        [Fact]
        public void TimedSurvival_SingleActiveFaction_Elimination_LatchesLostOnly_AndResolvesTheMatch()
        {
            // One active faction: a survival loss resolves Resolve(Neutral, P1) — no real winner exists to latch
            // WON, only P1's LOST latches. IsResolved()/SoleLoserFaction() must still surface the match end.
            var h = SimulationHost.Create(NullLogSink.Instance, new FactionRegistry(1),
                                          new FactionDefinition(), new FactionDefinition());
            h.WinCon.Configure(new ScenarioData
            {
                WinConditionSpec = new WinConditionSpec { Preset = WinPresetKind.TimedSurvival, FactionSlot = 0, SurviveTicks = 1000 },
            }, RegionStore.Empty, null, null);

            // No P1 units/buildings ever exist → eliminated; the absence-loss latches once past grace (P2).
            TickPastGrace(h);

            Assert.True(h.WinState.IsResolved());                                        // LOST-only still resolves
            Assert.Equal(0, h.WinState.WinnerFaction());                                 // nobody WON
            Assert.Equal((int)Faction.Player1, h.WinState.SoleLoserFaction());           // P1 is the sole loser
            Assert.Equal(WinStateStore.VERDICT_LOST, h.WinState.Verdict[(int)Faction.Player1]);
            Assert.Equal(WinStateStore.VERDICT_NONE, h.WinState.Verdict[(int)Faction.Player2]);

            // The Tick early-return latch holds: further ticks freeze MatchTicks and never flip any verdict.
            int ticksAtResolve = h.WinState.MatchTicks;
            for (int t = 0; t < 25; t++) h.WinCon.Tick(h.World, Dt);
            Assert.Equal(ticksAtResolve, h.WinState.MatchTicks);
            Assert.Equal(WinStateStore.VERDICT_LOST, h.WinState.Verdict[(int)Faction.Player1]);
            Assert.Equal(0, h.WinState.WinnerFaction());
            Assert.Equal((int)Faction.Player1, h.WinState.SoleLoserFaction());
        }

        // ── Review P2: absence-interpreted-as-loss is grace-gated (match_start spawns land AFTER this system) ───

        [Fact]
        public void TimedSurvival_FactionAbsentAtTick1_SpawnedInsideGrace_NoLossLatches()
        {
            var h = BuildHost();
            h.World.Create(At(5, 5), Faction.Player2, Fixed.FromInt(10), Fixed.FromInt(3)); // opponent present
            h.WinCon.Configure(new ScenarioData
            {
                WinConditionSpec = new WinConditionSpec { Preset = WinPresetKind.TimedSurvival, FactionSlot = 0, SurviveTicks = 1000 },
            }, RegionStore.Empty, null, null);

            // The designated faction does not exist yet (a match_start trigger would spawn it AFTER this system's
            // tick 1) — inside the grace window that absence must NOT read as an instant loss.
            for (int t = 0; t < 10; t++) h.WinCon.Tick(h.World, Dt);
            Assert.False(h.WinState.IsResolved());

            // Hand-place the survivor before tick 90 (stands in for the director's match_start spawn)…
            h.World.Create(At(0, 0), Faction.Player1, Fixed.FromInt(10), Fixed.FromInt(3));

            // …then run well past the grace boundary: the faction is alive, so no loss ever latches (and the
            // 1000-tick deadline is still far away, so no win either).
            for (int t = 0; t < 200; t++) h.WinCon.Tick(h.World, Dt);
            Assert.False(h.WinState.IsResolved());
            Assert.Equal(0, h.WinState.WinnerFaction());
        }

        // ── Review P5: an unresolved KotH region falls back to the built-in path (never a silent stalemate) ─────

        [Fact]
        public void KingOfTheHill_UnresolvedRegion_FallsBackToBuiltin_MatchStaysWinnable()
        {
            var h = BuildHost();
            h.Buildings.Create(At(-14, 0), Faction.Player1, BuildingType.CommandCenter); // P1 alive, P2 none
            // The region id resolves against RegionStore.Empty → unresolved. Configure must fall back to the
            // built-in enum (DestroyAllBuildings) instead of leaving a KotH that can never advance a counter.
            h.WinCon.Configure(new ScenarioData
            {
                WinCondition     = WinCondition.DestroyAllBuildings,
                WinConditionSpec = new WinConditionSpec { Preset = WinPresetKind.KingOfTheHill, RegionId = "missing", HoldTicks = 300 },
            }, RegionStore.Empty, null, null);

            TickPastGrace(h);
            Assert.Equal((int)Faction.Player1, h.WinState.WinnerFaction()); // built-in elimination resolved P1's win
        }

        // ── Review P6: the landmark holds a generation-stamped ref — a same-tick same-faction recycle still loses ──

        [Fact]
        public void LandmarkDestruction_SameTickSlotRecycle_SameFaction_LossStillLatches()
        {
            var h = BuildHost();
            int slot = h.Buildings.Create(At(-14, 0), Faction.Player1, BuildingType.CommandCenter);
            var scenario = new ScenarioData
            {
                Buildings = new[] { new ScenarioBuilding { Type = "CommandCenter", Slot = 0, X = -14, Z = 0 } },
                WinConditionSpec = new WinConditionSpec { Preset = WinPresetKind.LandmarkDestruction, StructureIndex = 0 },
            };
            h.WinCon.Configure(scenario, RegionStore.Empty, null, new[] { slot });

            TickPastGrace(h);
            Assert.Equal(0, h.WinState.WinnerFaction()); // landmark intact

            // The ABA edge: destroy the landmark and recycle its slot for the SAME faction BEFORE the next tick
            // (a completing construction can do exactly this mid-match). A raw slot+faction check would see an
            // alive, same-faction building and miss the loss; the packed generation ref must not.
            h.Buildings.Destroy(slot);
            int recycled = h.Buildings.Create(At(-14, 0), Faction.Player1, BuildingType.CommandCenter);
            Assert.Equal(slot, recycled); // precondition: the store really recycled the same slot

            h.WinCon.Tick(h.World, Dt);
            Assert.Equal((int)Faction.Player2, h.WinState.WinnerFaction()); // the DESIGNATED landmark is gone → P1 loses
            Assert.Equal(WinStateStore.VERDICT_LOST, h.WinState.Verdict[(int)Faction.Player1]);
        }

        // ── Review P10: ClearForReset also resets the apply-time config (not just the folded store) ─────────────

        [Fact]
        public void ClearForReset_WithoutReconfigure_LeavesNoStalePreset_NoInstantVerdict()
        {
            var h = BuildHost();
            h.World.Create(At(0, 0), Faction.Player1, Fixed.FromInt(10), Fixed.FromInt(3));
            h.WinCon.Configure(new ScenarioData
            {
                WinConditionSpec = new WinConditionSpec { Preset = WinPresetKind.TimedSurvival, FactionSlot = 0, SurviveTicks = 1000 },
            }, RegionStore.Empty, null, null);

            // Clear WITHOUT a re-Configure. Without ResetConfig, _preset would stay TimedSurvival while the
            // cleared store reads SurvivalRemaining == 0 → an instant false win the moment P1 is alive again.
            h.ClearForReset();
            h.World.Create(At(0, 0), Faction.Player1, Fixed.FromInt(10), Fixed.FromInt(3));

            for (int t = 0; t < 10; t++) h.WinCon.Tick(h.World, Dt);
            Assert.False(h.WinState.IsResolved());
            Assert.Equal(0, h.WinState.WinnerFaction());
        }

        // ── Review P12(a): Neutral units neither hold nor contest the KotH zone (ActiveFactions excludes Neutral) ──

        [Fact]
        public void KingOfTheHill_NeutralUnitInZone_NeitherHoldsNorContests_SoleHolderStillWins()
        {
            var h = BuildHost();
            h.World.Create(At(0, 0), Faction.Player1, Fixed.FromInt(10), Fixed.FromInt(3)); // P1 in zone
            h.World.Create(At(1, 1), Faction.Neutral, Fixed.FromInt(10), Fixed.FromInt(3)); // Neutral bystander in zone
            // DW-188: live P2 outside the zone — otherwise the wipeout fallback resolves at tick 1 and the test
            // could no longer discriminate whether the Neutral unit contested the hold.
            h.World.Create(At(20, 20), Faction.Player2, Fixed.FromInt(10), Fixed.FromInt(3));
            var regions = OneRegion("zone", -5, 5);
            h.WinCon.Configure(new ScenarioData
            {
                WinConditionSpec = new WinConditionSpec { Preset = WinPresetKind.KingOfTheHill, RegionId = "zone", HoldTicks = 5 },
            }, regions, null, null);

            h.WinState.MatchTicks = WinConditionSystem.GRACE_TICKS - 1;
            for (int t = 0; t < 5; t++) h.WinCon.Tick(h.World, Dt);
            Assert.Equal((int)Faction.Player1, h.WinState.WinnerFaction()); // the Neutral unit never contested
        }

        // ── Review P12(b): survival elimination-vs-deadline same-tick tie → elimination checked first → loss ────

        [Fact]
        public void TimedSurvival_EliminatedOnTheExactDeadlineTick_EliminationChecksFirst_Loses()
        {
            var h = BuildHost();
            h.World.Create(At(5, 5), Faction.Player2, Fixed.FromInt(10), Fixed.FromInt(3)); // opponent survives
            h.WinCon.Configure(new ScenarioData
            {
                WinConditionSpec = new WinConditionSpec { Preset = WinPresetKind.TimedSurvival, FactionSlot = 0, SurviveTicks = 1 },
            }, RegionStore.Empty, null, null);

            // On the SAME (post-grace) tick, SurvivalRemaining reaches 0 (deadline) AND P1 has no live entity
            // (eliminated). The elimination branch is evaluated FIRST → the designated faction LOSES.
            TickPastGrace(h);
            Assert.Equal(0, h.WinState.SurvivalRemaining[(int)Faction.Player1]);
            Assert.Equal(WinStateStore.VERDICT_LOST, h.WinState.Verdict[(int)Faction.Player1]);
            Assert.Equal((int)Faction.Player2, h.WinState.WinnerFaction());
        }
    }
}
