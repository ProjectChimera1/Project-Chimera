#nullable enable
using ProjectChimera.Core;              // EntityWorld, Faction, Fixed, FixedVec3, WinStateStore, WinConditionSystem, RegionStore
using ProjectChimera.Core.Definitions;  // ScenarioData, WinConditionSpec, WinPresetKind, FactionDefinition
using ProjectChimera.Core.Sim;          // SimulationHost, NullLogSink, SimulationLoop
using ProjectChimera.Multiplayer;       // OrderApplier, UnitOrder, UnitCommand
using Xunit;

namespace ProjectChimera.Sim.Tests.WinConditions
{
    /// <summary>
    /// DW-590 (decision 2026-08-05) — the allied-KotH concede that used to ORPHAN the rep-keyed hold accumulator.
    /// <c>UpdateKothCounters</c> stored a team's contiguous sole-hold count on its lowest-slot member FULL STOP, while
    /// <c>KothWinningTeam</c> only reads verdict-NONE factions. So when the rep latched LOST out-of-band (a Story 11.2
    /// CONCEDE — or the DSL <c>defeat</c> leaf) while a live ally kept sole-holding the zone, the count kept accruing on
    /// the dead rep, the ally was zeroed every tick as a non-rep, and the team's hold could NEVER reach the win: the
    /// match hung with no path to resolution (2 live teams ⇒ last-team-standing no-ops, and KotH has no other win).
    /// The rep is now the lowest-slot UNRESOLVED member and the accrued count is CARRIED to it on the re-rep.
    ///
    /// <para>Red without the fix (verified against the pre-fix file: 4 failed / 2 passed) —
    /// <see cref="RepConcedes_LiveAllySoleHolds_AccumulatorReKeysToAlly_AndTeamWins"/>,
    /// <see cref="ReKey_PicksLowestUnresolvedMember_ThroughTwoSuccessiveConcedes"/>,
    /// <see cref="ReKey_CarriesTheLiveCount_ContestBeforeTheConcedeRestartsTheHoldAtOne"/> and
    /// <see cref="RepConcedeReKeyResolution_FoldsDeterministically_AcrossTwoIdenticalRuns"/> all hang the match on the
    /// pre-fix code (no verdict ever latches). <see cref="ConcededSoleHolder_NoUnresolvedAlly_KeepsHistoricRepSlot"/>
    /// and <see cref="AllUnresolved_TeamHold_AccruesOnTheLowestSlotRep_Unchanged"/> are the fold-neutrality pins: they
    /// pass on BOTH sides, documenting that the folded <c>KothHoldTicks</c> values are byte-identical for FFA and for
    /// every all-unresolved team — which is why no golden moves (no golden scenario uses the KotH preset at all).</para>
    ///
    /// <para>Godot-free (NullLogSink); drives <c>WinCon.Tick</c> directly and concedes through the REAL
    /// <c>OrderApplier</c> wire path.</para>
    /// </summary>
    public class KothRepReKeyTests
    {
        private static readonly Fixed Dt = SimulationLoop.FixedDt;

        private const int WON  = WinStateStore.VERDICT_WON;
        private const int LOST = WinStateStore.VERDICT_LOST;
        private const int NONE = WinStateStore.VERDICT_NONE;

        private static SimulationHost Host(int players) => SimulationHost.Create(
            NullLogSink.Instance, new FactionRegistry(players), new FactionDefinition(), new FactionDefinition());

        private static FixedVec3 At(int x, int z) => new FixedVec3(Fixed.FromInt(x), Fixed.Zero, Fixed.FromInt(z));

        private static int Unit(SimulationHost h, int x, int z, Faction f) =>
            h.World.Create(At(x, z), f, Fixed.FromInt(10), Fixed.FromInt(3));

        private static RegionStore OneRegion(string id, int min, int max) =>
            new RegionStore(new[] { id },
                            new[] { new FixedRect(Fixed.FromInt(min), Fixed.FromInt(min), Fixed.FromInt(max), Fixed.FromInt(max)) });

        /// <summary>KotH on a -5..5 "zone". Configure zeroes the folded store, so it must run BEFORE any verdict poke.</summary>
        private static void ConfigureKoth(SimulationHost h, int holdTicks) =>
            h.WinCon.Configure(new ScenarioData
            {
                WinConditionSpec = new WinConditionSpec { Preset = WinPresetKind.KingOfTheHill, RegionId = "zone", HoldTicks = holdTicks },
            }, OneRegion("zone", -5, 5), null, null);

        /// <summary>Surrender <paramref name="f"/> through the real Story 11.2 wire path (the exact latch a live
        /// in-match concede performs — not a hand-poked verdict).</summary>
        private static void Concede(SimulationHost h, Faction f) =>
            OrderApplier.Apply(h.World, new UnitOrder(0, UnitCommand.Concede, Fixed.Zero, Fixed.Zero), f, winState: h.WinState);

        private static void Tick(SimulationHost h, int times = 1)
        {
            for (int t = 0; t < times; t++) h.WinCon.Tick(h.World, Dt);
        }

        // ── The headline DW-590 case: the rep concedes, the ally keeps holding, the team still wins ───────────────

        [Fact]
        public void RepConcedes_LiveAllySoleHolds_AccumulatorReKeysToAlly_AndTeamWins()
        {
            var h = Host(3);
            h.Alliances.TeamId[(int)Faction.Player2] = (int)Faction.Player1; // team {P1,P2} (rep = P1) vs {P3}
            Unit(h, 0, 0, Faction.Player1);   // rep, in the zone
            Unit(h, 1, 1, Faction.Player2);   // ally, in the zone
            Unit(h, 20, 20, Faction.Player3); // opponent outside (DW-188: it must EXIST or the wipeout fallback resolves early)
            ConfigureKoth(h, holdTicks: 6);

            h.WinState.MatchTicks = WinConditionSystem.GRACE_TICKS - 1;
            Tick(h, 3);
            Assert.Equal(3, h.WinState.KothHoldTicks[(int)Faction.Player1]); // the team's count sits on the rep
            Assert.Equal(0, h.WinState.KothHoldTicks[(int)Faction.Player2]);

            // The REP surrenders while its ally holds the hill alone. The counter must MOVE to the ally, keeping the
            // hold contiguous — pre-fix it kept accruing on the LOST rep that KothWinningTeam can never read.
            Concede(h, Faction.Player1);
            Assert.Equal(LOST, h.WinState.Verdict[(int)Faction.Player1]);

            Tick(h);
            Assert.Equal(4, h.WinState.KothHoldTicks[(int)Faction.Player2]); // carried 3 → 4, NOT restarted at 1
            Assert.Equal(0, h.WinState.KothHoldTicks[(int)Faction.Player1]); // vacated
            Assert.False(h.WinCon.IsFullyResolved());                        // the match continues (2 live teams)

            // 5, 6 → hold_ticks reached on the ally's counter → the whole team wins.
            Tick(h, 2);
            Assert.True(h.WinCon.IsFullyResolved());
            Assert.Equal(WON,  h.WinState.Verdict[(int)Faction.Player2]);
            Assert.Equal(LOST, h.WinState.Verdict[(int)Faction.Player1]); // the surrender is monotone — never resurrected
            Assert.Equal(LOST, h.WinState.Verdict[(int)Faction.Player3]);
            Assert.Equal((int)Faction.Player2, h.WinState.WinnerFaction());
        }

        // ── The re-rep picks the LOWEST unresolved member, and survives a second concede ──────────────────────────

        [Fact]
        public void ReKey_PicksLowestUnresolvedMember_ThroughTwoSuccessiveConcedes()
        {
            var h = Host(4);
            h.Alliances.TeamId[(int)Faction.Player2] = (int)Faction.Player1; // team {P1,P2,P3} vs {P4}
            h.Alliances.TeamId[(int)Faction.Player3] = (int)Faction.Player1;
            Unit(h, 0, 0, Faction.Player1);
            Unit(h, 1, 1, Faction.Player2);
            Unit(h, 2, 2, Faction.Player3);
            Unit(h, 20, 20, Faction.Player4); // opponent outside
            ConfigureKoth(h, holdTicks: 8);

            h.WinState.MatchTicks = WinConditionSystem.GRACE_TICKS - 1;
            Tick(h, 2);
            Assert.Equal(2, h.WinState.KothHoldTicks[(int)Faction.Player1]);

            // P1 out → the accumulator re-keys to P2 (the lowest UNRESOLVED member), not to some other slot.
            Concede(h, Faction.Player1);
            Tick(h);
            Assert.Equal(0, h.WinState.KothHoldTicks[(int)Faction.Player1]);
            Assert.Equal(3, h.WinState.KothHoldTicks[(int)Faction.Player2]);
            Assert.Equal(0, h.WinState.KothHoldTicks[(int)Faction.Player3]);

            // P2 out too → it re-keys AGAIN, to P3, still carrying the same contiguous hold.
            Concede(h, Faction.Player2);
            Tick(h);
            Assert.Equal(0, h.WinState.KothHoldTicks[(int)Faction.Player2]);
            Assert.Equal(4, h.WinState.KothHoldTicks[(int)Faction.Player3]);
            Assert.False(h.WinCon.IsFullyResolved());

            // 5, 6, 7, 8 → the last unresolved member completes the hold and the team wins.
            Tick(h, 4);
            Assert.True(h.WinCon.IsFullyResolved());
            Assert.Equal(WON,  h.WinState.Verdict[(int)Faction.Player3]);
            Assert.Equal(LOST, h.WinState.Verdict[(int)Faction.Player1]);
            Assert.Equal(LOST, h.WinState.Verdict[(int)Faction.Player2]);
            Assert.Equal(LOST, h.WinState.Verdict[(int)Faction.Player4]);
            Assert.Equal((int)Faction.Player3, h.WinState.WinnerFaction());
        }

        // ── The carry is the LIVE count, never a resurrection of a hold the opponent already broke ────────────────

        [Fact]
        public void ReKey_CarriesTheLiveCount_ContestBeforeTheConcedeRestartsTheHoldAtOne()
        {
            var h = Host(3);
            h.Alliances.TeamId[(int)Faction.Player2] = (int)Faction.Player1; // team {P1,P2} vs {P3}
            Unit(h, 0, 0, Faction.Player1);
            Unit(h, 1, 1, Faction.Player2);
            Unit(h, 20, 20, Faction.Player3); // opponent outside the zone
            ConfigureKoth(h, holdTicks: 5);

            h.WinState.MatchTicks = WinConditionSystem.GRACE_TICKS - 1;
            Tick(h, 3);
            Assert.Equal(3, h.WinState.KothHoldTicks[(int)Faction.Player1]);

            // The opponent steps IN → contested → every counter resets. The hold the rep built is genuinely gone.
            int intruder = Unit(h, 3, 3, Faction.Player3);
            Tick(h);
            Assert.Equal(0, h.WinState.KothHoldTicks[(int)Faction.Player1]);

            // NOW the rep concedes and the intruder dies. The re-rep must carry the LIVE count (0) — restarting the
            // ally's hold at 1 — not resurrect the pre-contest 3 off a stale slot.
            Concede(h, Faction.Player1);
            h.World.Destroy(intruder);
            Tick(h);
            Assert.Equal(1, h.WinState.KothHoldTicks[(int)Faction.Player2]);
            Assert.Equal(0, h.WinState.KothHoldTicks[(int)Faction.Player1]);

            // 2, 3, 4, 5 → the ally completes a FULL fresh hold and the team wins.
            Tick(h, 3);
            Assert.False(h.WinCon.IsFullyResolved()); // still 4 < 5 — the hold really did restart
            Tick(h);
            Assert.True(h.WinCon.IsFullyResolved());
            Assert.Equal(WON,  h.WinState.Verdict[(int)Faction.Player2]);
            Assert.Equal(LOST, h.WinState.Verdict[(int)Faction.Player3]);
        }

        // ── Fold neutrality: the pre-DW-590 slot/value is preserved wherever the old rep was already correct ──────

        [Fact]
        public void ConcededSoleHolder_NoUnresolvedAlly_KeepsHistoricRepSlot()
        {
            var h = Host(3); // FFA teams-of-1 — the shipped default, and the shape every golden runs
            Unit(h, 0, 0, Faction.Player1);     // the (soon-to-concede) sole holder
            Unit(h, 20, 20, Faction.Player2);   // outside
            Unit(h, -20, -20, Faction.Player3); // outside — two live opponents keep the match unresolved
            ConfigureKoth(h, holdTicks: 1000);  // unreachable: no hold-win can pre-empt this

            h.WinState.MatchTicks = WinConditionSystem.GRACE_TICKS - 1;
            Tick(h, 2);
            Assert.Equal(2, h.WinState.KothHoldTicks[(int)Faction.Player1]);

            // A conceded teams-of-1 holder has NO unresolved team member: the rep falls back to the historic
            // lowest-slot member, so the folded counter keeps advancing on the SAME slot with the SAME values as
            // before DW-590 (an inert accumulator — KothWinningTeam's verdict-NONE scan can never read it).
            Concede(h, Faction.Player1);
            Tick(h, 3);
            Assert.Equal(5, h.WinState.KothHoldTicks[(int)Faction.Player1]);
            Assert.Equal(0, h.WinState.WinnerFaction());
            Assert.Equal(NONE, h.WinState.Verdict[(int)Faction.Player2]);
            Assert.Equal(NONE, h.WinState.Verdict[(int)Faction.Player3]);
        }

        [Fact]
        public void AllUnresolved_TeamHold_AccruesOnTheLowestSlotRep_Unchanged()
        {
            var h = Host(3);
            h.Alliances.TeamId[(int)Faction.Player2] = (int)Faction.Player1; // team {P1,P2} vs {P3}
            Unit(h, 0, 0, Faction.Player1);
            Unit(h, 1, 1, Faction.Player2);
            Unit(h, 20, 20, Faction.Player3);
            ConfigureKoth(h, holdTicks: 1000);

            h.WinState.MatchTicks = WinConditionSystem.GRACE_TICKS - 1;
            Tick(h, 7);

            // Nobody is resolved → the rep is the lowest-slot member exactly as before, and every non-rep member is
            // zeroed. This is the fold shape the (KotH-free) goldens would see; DW-590 cannot perturb it.
            Assert.Equal(7, h.WinState.KothHoldTicks[(int)Faction.Player1]);
            Assert.Equal(0, h.WinState.KothHoldTicks[(int)Faction.Player2]);
            Assert.Equal(0, h.WinState.KothHoldTicks[(int)Faction.Player3]);
        }

        // ── Determinism: the re-rep resolution folds byte-identically across two identical runs ────────────────────

        [Fact]
        public void RepConcedeReKeyResolution_FoldsDeterministically_AcrossTwoIdenticalRuns()
        {
            uint Run()
            {
                var h = Host(3);
                h.Alliances.TeamId[(int)Faction.Player2] = (int)Faction.Player1;
                Unit(h, 0, 0, Faction.Player1);
                Unit(h, 1, 1, Faction.Player2);
                Unit(h, 20, 20, Faction.Player3);
                ConfigureKoth(h, holdTicks: 6);

                h.WinState.MatchTicks = WinConditionSystem.GRACE_TICKS - 1;
                Tick(h, 3);
                Concede(h, Faction.Player1);
                Tick(h, 3);
                Assert.True(h.WinCon.IsFullyResolved()); // the sequence really reaches a terminal state
                return SimChecksum.Compute(h.World, h.Buildings, h.Resources, new FactionRegistry(3),
                                           winState: h.WinState, alliances: h.Alliances);
            }

            Assert.Equal(Run(), Run());
        }
    }
}
