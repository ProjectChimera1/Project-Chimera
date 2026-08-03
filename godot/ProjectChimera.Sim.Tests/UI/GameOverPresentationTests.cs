#nullable enable
using ProjectChimera.Core; // GameOverPresentation, GameOverSummary, MatchStats, WinStateStore, Faction
using Xunit;

namespace ProjectChimera.Sim.Tests.UI
{
    /// <summary>
    /// DW-190 / DW-448 — the headless regression net for the two win-presentation decisions MainScene consumes:
    ///
    /// <para><b>DW-190</b> (<see cref="GameOverPresentation.DecideOutcome"/>): the Story 7.12 per-player-elimination
    /// rule. The pinned regression class: in a &gt;2-faction match, a LOCAL loss while other factions are still
    /// undecided must yield <c>EliminateLocal</c> (spectator flip), NEVER the terminal <c>GameOver</c> — even though
    /// the old any-latched gates (<see cref="WinStateStore.IsResolved"/> / <see cref="WinStateStore.SoleLoserFaction"/>)
    /// are already true at that moment. Each fixture asserts those old predicates' values explicitly so a gate
    /// regression is visible as a failing assertion, not a silent semantic slide.</para>
    ///
    /// <para><b>DW-448</b> (<see cref="GameOverPresentation.BuildHeadline"/>): the Story 9.15 local-win headline.
    /// The pinned regression class: VICTORY iff the LOCAL seat's OWN latched verdict is WON — never keyed off the
    /// team-representative <c>winnerPlayer</c> (lowest WON slot), which showed DEFEAT to a winning 2v2 ally on a
    /// higher slot. Plus the exact "Team Victory — …" / "Player N Wins!" / "No Victor — Match Over" phrasing.</para>
    /// </summary>
    public class GameOverPresentationTests
    {
        private const int WON  = WinStateStore.VERDICT_WON;
        private const int LOST = WinStateStore.VERDICT_LOST;

        private static GameOverPresentation.OutcomeDecision Decide(
            WinStateStore win, bool fullyResolved, Faction local, bool alreadyEliminated = false)
            => GameOverPresentation.DecideOutcome(win, fullyResolved, local, alreadyEliminated);

        // ── DW-190: DecideOutcome ──────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void LocalLoss_MatchNotFullyResolved_EliminatesLocal_NeverTerminalGameOver()
        {
            // 3-faction match: the LOCAL P1 just latched LOST; P2/P3 are still undecided (VERDICT_NONE).
            var win = new WinStateStore();
            win.Verdict[(int)Faction.Player1] = LOST;

            // The DW-190 trap, asserted explicitly: the OLD pre-7.12 gates are ALREADY true here — an any-latched
            // IsResolved() gate or the SoleLoserFaction() defeat form would fire the terminal overlay the instant
            // the local player lost, the exact defect Story 7.12 exists to prevent.
            Assert.True(win.IsResolved());
            Assert.Equal(1, win.SoleLoserFaction());

            GameOverPresentation.OutcomeDecision d = Decide(win, fullyResolved: false, Faction.Player1);

            Assert.Equal(GameOverPresentation.OutcomeKind.EliminateLocal, d.Kind);
            Assert.NotEqual(GameOverPresentation.OutcomeKind.GameOver, d.Kind);
        }

        [Fact]
        public void LocalLoss_AlreadyEliminated_DegradesToContinue()
        {
            // The spectator flip is once-per-match (MainScene's _localEliminated guard is a helper input):
            // on every later frame the same LOST verdict must decide Continue, not EliminateLocal again.
            var win = new WinStateStore();
            win.Verdict[(int)Faction.Player1] = LOST;

            GameOverPresentation.OutcomeDecision d =
                Decide(win, fullyResolved: false, Faction.Player1, alreadyEliminated: true);

            Assert.Equal(GameOverPresentation.OutcomeKind.Continue, d.Kind);
        }

        [Fact]
        public void RemoteLoss_LocalUndecided_Continues()
        {
            // Someone ELSE was eliminated mid-match: no local flip, no game over — the match just continues.
            var win = new WinStateStore();
            win.Verdict[(int)Faction.Player2] = LOST;

            GameOverPresentation.OutcomeDecision d = Decide(win, fullyResolved: false, Faction.Player1);

            Assert.Equal(GameOverPresentation.OutcomeKind.Continue, d.Kind);
        }

        [Fact]
        public void WinnerPresent_GameOverWithTeamRep_RegardlessOfLocalEliminationState()
        {
            // Fully resolved 3-faction outcome: P2 WON, P1 (local) and P3 LOST. The terminal overlay fires with
            // the team representative — for a fresh local loss AND for an already-spectating local player
            // (the winner check precedes the elimination branch; the overlay replaces the banner).
            var win = new WinStateStore();
            win.Verdict[(int)Faction.Player1] = LOST;
            win.Verdict[(int)Faction.Player2] = WON;
            win.Verdict[(int)Faction.Player3] = LOST;

            foreach (bool alreadyEliminated in new[] { false, true })
            {
                GameOverPresentation.OutcomeDecision d =
                    Decide(win, fullyResolved: true, Faction.Player1, alreadyEliminated);
                Assert.Equal(GameOverPresentation.OutcomeKind.GameOver, d.Kind);
                Assert.Equal(2, d.WinnerRep); // 1-based team representative (WinnerFaction: lowest WON slot)
            }
        }

        [Fact]
        public void WinnerOnHigherSlot_WinnerRepIsThatSlot()
        {
            // The representative is the lowest WON slot — here P3 is the only winner.
            var win = new WinStateStore();
            win.Verdict[(int)Faction.Player1] = LOST;
            win.Verdict[(int)Faction.Player2] = LOST;
            win.Verdict[(int)Faction.Player3] = WON;

            GameOverPresentation.OutcomeDecision d = Decide(win, fullyResolved: true, Faction.Player1);

            Assert.Equal(GameOverPresentation.OutcomeKind.GameOver, d.Kind);
            Assert.Equal(3, d.WinnerRep);
        }

        [Fact]
        public void FullyResolved_NoWinner_GameOverNoVictorForm()
        {
            // The LOST-only outcome (e.g. a single-active-faction preset loss latches only VERDICT_LOST): every
            // active faction is latched, nobody WON → GameOver with 0 (the no-victor / match-over defeat form).
            var win = new WinStateStore();
            win.Verdict[(int)Faction.Player1] = LOST;

            GameOverPresentation.OutcomeDecision d = Decide(win, fullyResolved: true, Faction.Player1);

            Assert.Equal(GameOverPresentation.OutcomeKind.GameOver, d.Kind);
            Assert.Equal(0, d.WinnerRep);
        }

        [Fact]
        public void NothingLatched_Continues()
        {
            GameOverPresentation.OutcomeDecision d =
                Decide(new WinStateStore(), fullyResolved: false, Faction.Player1);

            Assert.Equal(GameOverPresentation.OutcomeKind.Continue, d.Kind);
        }

        [Fact]
        public void NeutralLocalSeat_NeverTakesTheEliminationFlip()
        {
            // A Neutral local seat (spectator) must never trigger the elimination flip — even against a
            // defensively poisoned Verdict[0] (never assigned by the sim; the guard survives the extraction).
            var win = new WinStateStore();
            win.Verdict[(int)Faction.Neutral] = LOST;

            GameOverPresentation.OutcomeDecision d = Decide(win, fullyResolved: false, Faction.Neutral);

            Assert.Equal(GameOverPresentation.OutcomeKind.Continue, d.Kind);
        }

        // ── DW-448: BuildHeadline ──────────────────────────────────────────────────────────────────────────────

        /// <summary>The ledger-prescribed 2v2 fixture: teams {1,1,2,2}, P1+P2 WON, P3+P4 LOST.</summary>
        private static (GameOverSummary.GameOverRow[] rows, WinStateStore win) TwoVsTwoWon()
        {
            var win = new WinStateStore();
            win.Verdict[(int)Faction.Player1] = WON;
            win.Verdict[(int)Faction.Player2] = WON;
            win.Verdict[(int)Faction.Player3] = LOST;
            win.Verdict[(int)Faction.Player4] = LOST;
            return (GameOverSummary.Build(new MatchStats(), win), win);
        }

        [Fact]
        public void TwoVsTwo_WinningAllyOnHigherSlot_SeesVictory_WithTeamLine()
        {
            // THE 2v2 bug this DW exists to pin: winnerPlayer is the team representative (P1 = lowest WON slot),
            // the local seat is the ALLY P2. Keying VICTORY off winnerPlayer (the old revert) shows this winning
            // ally DEFEAT while their own stat row reads WON. LocalWin must come from P2's OWN latched verdict.
            (GameOverSummary.GameOverRow[] rows, WinStateStore win) = TwoVsTwoWon();

            GameOverPresentation.Headline h =
                GameOverPresentation.BuildHeadline(rows, win, Faction.Player2, winnerPlayer: 1);

            Assert.True(h.LocalWin);
            Assert.Equal("Team Victory — P1, P2 Win!", h.WinnerLine); // >1 WON → allied phrasing, canonical names
        }

        [Fact]
        public void TwoVsTwo_LosingSeats_SeeDefeat_SameTeamLine()
        {
            (GameOverSummary.GameOverRow[] rows, WinStateStore win) = TwoVsTwoWon();

            foreach (Faction loser in new[] { Faction.Player3, Faction.Player4 })
            {
                GameOverPresentation.Headline h =
                    GameOverPresentation.BuildHeadline(rows, win, loser, winnerPlayer: 1);
                Assert.False(h.LocalWin);
                Assert.Equal("Team Victory — P1, P2 Win!", h.WinnerLine);
            }
        }

        [Fact]
        public void SingleWinner_PlayerLineExact_LocalKeysOwnVerdict()
        {
            var win = new WinStateStore();
            win.Verdict[(int)Faction.Player1] = WON;
            win.Verdict[(int)Faction.Player2] = LOST;
            GameOverSummary.GameOverRow[] rows = GameOverSummary.Build(new MatchStats(), win);

            GameOverPresentation.Headline winner =
                GameOverPresentation.BuildHeadline(rows, win, Faction.Player1, winnerPlayer: 1);
            Assert.True(winner.LocalWin);
            Assert.Equal("Player 1 Wins!", winner.WinnerLine); // exactly one WON row → never the team phrasing

            GameOverPresentation.Headline loser =
                GameOverPresentation.BuildHeadline(rows, win, Faction.Player2, winnerPlayer: 1);
            Assert.False(loser.LocalWin);
            Assert.Equal("Player 1 Wins!", loser.WinnerLine);
        }

        [Fact]
        public void FfaWinnerOnNonFirstSlot_EachSeatKeysItsOwnVerdict()
        {
            // 4-FFA won by P2 (winnerPlayer=2): kills any residual "local == Player1 / winnerPlayer == 1"-shaped
            // keying — the P2 seat sees VICTORY, the P1 seat sees DEFEAT, and the line names the real winner.
            var win = new WinStateStore();
            win.Verdict[(int)Faction.Player1] = LOST;
            win.Verdict[(int)Faction.Player2] = WON;
            win.Verdict[(int)Faction.Player3] = LOST;
            win.Verdict[(int)Faction.Player4] = LOST;
            GameOverSummary.GameOverRow[] rows = GameOverSummary.Build(new MatchStats(), win);

            Assert.True(GameOverPresentation.BuildHeadline(rows, win, Faction.Player2, 2).LocalWin);
            Assert.False(GameOverPresentation.BuildHeadline(rows, win, Faction.Player1, 2).LocalWin);
            Assert.Equal("Player 2 Wins!", GameOverPresentation.BuildHeadline(rows, win, Faction.Player1, 2).WinnerLine);
        }

        [Fact]
        public void NoVictor_MatchOverLine_NeverVictory()
        {
            // The LOST-only no-victor outcome: winnerPlayer 0 → the match-over line, and no seat shows VICTORY.
            var win = new WinStateStore();
            win.Verdict[(int)Faction.Player1] = LOST;
            GameOverSummary.GameOverRow[] rows = GameOverSummary.Build(new MatchStats(), win);

            GameOverPresentation.Headline h =
                GameOverPresentation.BuildHeadline(rows, win, Faction.Player1, winnerPlayer: 0);

            Assert.False(h.LocalWin);
            Assert.Equal("No Victor — Match Over", h.WinnerLine);
        }
    }
}
