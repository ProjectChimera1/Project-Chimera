#nullable enable
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Sim;
using Xunit;

namespace ProjectChimera.Sim.Tests.WinConditions
{
    /// <summary>
    /// DW-188 (decision 2026-07-19) — the guarded King-of-the-Hill elimination fallback: a KotH match used to
    /// conclude ONLY by the hold-win, so a mutual annihilation (every asset on every team destroyed — no team can
    /// ever hold the zone again) hung unresolved forever. Now a faction whose team's unresolved members are ALL
    /// totally wiped (no units AND no buildings) latches LOST and <c>ApplyLastTeamStanding</c> resolves the match.
    /// The guard is TEAM-scoped and grace-gated so the hold-race win path is untouched: a wiped faction with a live
    /// ally stays unresolved (protecting the rep-keyed hold accumulator — pinned by
    /// <c>NFactionVictoryTests.KotH_TeamRepLeavesZone_AllyStillHolds_TeamCounterSurvivesAndWins</c>), an asset-KIND
    /// loss never applies, and matches between live opponents still conclude only by the hold-win. Every test here
    /// FAILS on the pre-fix code (the match simply never resolved). Godot-free (NullLogSink).
    /// </summary>
    public class KothEliminationFallbackTests
    {
        private static readonly Fixed Dt = SimulationLoop.FixedDt;

        private static SimulationHost BuildHost(int players = 2) => SimulationHost.Create(
            NullLogSink.Instance, new FactionRegistry(players), new FactionDefinition(), new FactionDefinition());

        private static FixedVec3 At(int x, int z) => new FixedVec3(Fixed.FromInt(x), Fixed.Zero, Fixed.FromInt(z));

        private static RegionStore OneRegion(string id, int min, int max) =>
            new RegionStore(new[] { id },
                            new[] { new FixedRect(Fixed.FromInt(min), Fixed.FromInt(min), Fixed.FromInt(max), Fixed.FromInt(max)) });

        private static int Unit(SimulationHost h, int x, int z, Faction f) =>
            h.World.Create(At(x, z), f, Fixed.FromInt(10), Fixed.FromInt(3));

        /// <summary>KotH on a -5..5 "zone" with a hold requirement far beyond any test's tick span, so no hold-win
        /// can ever pre-empt the elimination path under test.</summary>
        private static void ConfigureKoth(SimulationHost h) =>
            h.WinCon.Configure(new ScenarioData
            {
                WinConditionSpec = new WinConditionSpec { Preset = WinPresetKind.KingOfTheHill, RegionId = "zone", HoldTicks = 300 },
            }, OneRegion("zone", -5, 5), null, null);

        private const int WON  = WinStateStore.VERDICT_WON;
        private const int LOST = WinStateStore.VERDICT_LOST;
        private const int NONE = WinStateStore.VERDICT_NONE;

        // ── The headline DW-188 case: simultaneous mutual annihilation resolves via the double-elim tie-break ────

        [Fact]
        public void MutualAnnihilation_1v1_SimultaneousWipe_ResolvesByTieBreak_HighestSlotWins()
        {
            var h = BuildHost();
            int u1 = Unit(h, 0, 0, Faction.Player1);   // in the zone
            int u2 = Unit(h, 20, 20, Faction.Player2); // outside the zone
            ConfigureKoth(h);

            // Past grace with both alive → no verdict (KotH between live opponents only concludes by the hold-win).
            h.WinState.MatchTicks = WinConditionSystem.GRACE_TICKS - 1;
            h.WinCon.Tick(h.World, Dt);
            Assert.False(h.WinState.IsResolved());

            // Every asset on BOTH sides dies before the next tick — the mutual annihilation that used to hang the
            // match forever. The wipeout latch + last-team-standing double-elim tie-break must resolve it: the
            // highest slot eliminated this tick (P2) wins — the same 7.11 P1+P2 parity the built-ins reproduce.
            h.World.Destroy(u1);
            h.World.Destroy(u2);
            h.WinCon.Tick(h.World, Dt);

            Assert.True(h.WinCon.IsFullyResolved());
            Assert.Equal(LOST, h.WinState.Verdict[(int)Faction.Player1]);
            Assert.Equal(WON,  h.WinState.Verdict[(int)Faction.Player2]);
            Assert.Equal((int)Faction.Player2, h.WinState.WinnerFaction());
        }

        // ── One-sided total wipeout: TOTAL means units AND buildings; the survivor wins without holding ─────────

        [Fact]
        public void OneSidedWipeout_UnitsGoneButBuildingRemains_NoLatch_ThenTotalWipeResolvesSurvivor()
        {
            var h = BuildHost();
            int u1 = Unit(h, 0, 0, Faction.Player1);
            int b1 = h.Buildings.Create(At(-14, 0), Faction.Player1, BuildingType.CommandCenter);
            Unit(h, 20, 20, Faction.Player2); // survivor, never enters the zone
            ConfigureKoth(h);

            h.WinState.MatchTicks = WinConditionSystem.GRACE_TICKS - 1;
            h.WinCon.Tick(h.World, Dt);
            Assert.False(h.WinState.IsResolved());

            // P1 loses every UNIT but keeps a building — it could still train a holder, so the fallback must NOT
            // fire (a hold-incapable-today faction is not a wiped faction).
            h.World.Destroy(u1);
            for (int t = 0; t < 5; t++) h.WinCon.Tick(h.World, Dt);
            Assert.False(h.WinState.IsResolved());
            Assert.Equal(NONE, h.WinState.Verdict[(int)Faction.Player1]);

            // The building falls too → TOTAL wipeout → P1 LOST, and the sole live team (P2) wins immediately —
            // without ever having set foot in the zone (annihilation short-circuits the hold-race).
            h.Buildings.Destroy(b1);
            h.WinCon.Tick(h.World, Dt);

            Assert.True(h.WinCon.IsFullyResolved());
            Assert.Equal(LOST, h.WinState.Verdict[(int)Faction.Player1]);
            Assert.Equal(WON,  h.WinState.Verdict[(int)Faction.Player2]);
            Assert.Equal((int)Faction.Player2, h.WinState.WinnerFaction());
            Assert.Equal(0, h.WinState.KothHoldTicks[(int)Faction.Player2]); // the win owed nothing to the hill
        }

        // ── Grace gate: absence inside the grace window is "not spawned yet", never an instant loss ─────────────

        [Fact]
        public void Wipeout_InsideGrace_DoesNotLatch_UntilGraceEnds()
        {
            var h = BuildHost();
            Unit(h, 0, 0, Faction.Player1); // P1 holds the (uncontested) zone; P2 never has any asset
            ConfigureKoth(h);

            // Inside the grace window P2's absence must NOT latch (a match_start trigger could still spawn it) —
            // this is the same gate that keeps the applier-path KotH test resolving by hold inside grace.
            for (int t = 0; t < 10; t++) h.WinCon.Tick(h.World, Dt);
            Assert.False(h.WinState.IsResolved());

            // The tick that crosses the grace boundary latches P2's wipeout and resolves P1 the winner.
            h.WinState.MatchTicks = WinConditionSystem.GRACE_TICKS - 1;
            h.WinCon.Tick(h.World, Dt);
            Assert.True(h.WinCon.IsFullyResolved());
            Assert.Equal(WON,  h.WinState.Verdict[(int)Faction.Player1]);
            Assert.Equal(LOST, h.WinState.Verdict[(int)Faction.Player2]);
        }

        // ── Team scoping: a wiped faction with a live ally stays unresolved; a dead team latches together ───────

        [Fact]
        public void TeamWipeout_WipedMemberWithLiveAlly_StaysUnresolved_WholeTeamLatchesTogether()
        {
            var h = BuildHost(3);
            h.Alliances.TeamId[(int)Faction.Player2] = (int)Faction.Player1; // {P1,P2} vs {P3}
            int u1 = Unit(h, 0, 0, Faction.Player1);  // rep, in the zone
            int u2 = Unit(h, 1, 1, Faction.Player2);  // ally, in the zone
            Unit(h, 20, 20, Faction.Player3);         // opponent, outside
            ConfigureKoth(h);

            h.WinState.MatchTicks = WinConditionSystem.GRACE_TICKS - 1;
            h.WinCon.Tick(h.World, Dt);
            Assert.False(h.WinState.IsResolved());

            // P1 (the team REP) is totally wiped, but its ally still fights: the TEAM guard must keep P1
            // unresolved — a per-faction latch here would orphan the rep-keyed hold counter (see
            // NFactionVictoryTests.KotH_TeamRepLeavesZone… for the hold-still-wins half of that guarantee).
            h.World.Destroy(u1);
            for (int t = 0; t < 5; t++) h.WinCon.Tick(h.World, Dt);
            Assert.Equal(NONE, h.WinState.Verdict[(int)Faction.Player1]);
            Assert.False(h.WinState.IsResolved());

            // The ally falls too → the whole dead team latches LOST on the SAME tick → P3 is the last team
            // standing and wins.
            h.World.Destroy(u2);
            h.WinCon.Tick(h.World, Dt);

            Assert.True(h.WinCon.IsFullyResolved());
            Assert.Equal(LOST, h.WinState.Verdict[(int)Faction.Player1]);
            Assert.Equal(LOST, h.WinState.Verdict[(int)Faction.Player2]);
            Assert.Equal(WON,  h.WinState.Verdict[(int)Faction.Player3]);
            Assert.Equal((int)Faction.Player3, h.WinState.WinnerFaction());
        }

        // ── Determinism: the new resolution path folds byte-identically across two identical runs ───────────────

        [Fact]
        public void MutualAnnihilationResolution_FoldsDeterministically_AcrossTwoIdenticalRuns()
        {
            uint Run()
            {
                var h = BuildHost();
                int u1 = Unit(h, 0, 0, Faction.Player1);
                int u2 = Unit(h, 20, 20, Faction.Player2);
                ConfigureKoth(h);
                h.WinState.MatchTicks = WinConditionSystem.GRACE_TICKS - 1;
                h.WinCon.Tick(h.World, Dt);
                h.World.Destroy(u1);
                h.World.Destroy(u2);
                h.WinCon.Tick(h.World, Dt);
                Assert.True(h.WinCon.IsFullyResolved());
                return SimChecksum.Compute(h.World, h.Buildings, h.Resources, new FactionRegistry(2), winState: h.WinState);
            }

            Assert.Equal(Run(), Run());
        }
    }
}
