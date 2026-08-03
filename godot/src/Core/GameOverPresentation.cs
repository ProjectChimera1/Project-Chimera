#nullable enable
using System.Collections.Generic;

namespace ProjectChimera.Core
{
    /// <summary>
    /// DW-190 / DW-448 — the Godot-free, Tier-1-testable presentation DECISIONS behind MainScene's win-handling
    /// branch and the <c>ShowGameOver</c> headline. MainScene holds NO win math (Story 7.11) and, after this
    /// extraction, no win-presentation LOGIC either: its <c>_Process</c> branch and <c>ShowGameOver</c> merely map
    /// these decisions onto Godot calls (overlay/banner/fog), so the exact regressions the deferred-work ledger
    /// names — the no-victor gate sliding back to <c>IsResolved()</c>/<c>SoleLoserFaction()</c> semantics
    /// (terminal overlay the instant a &gt;2-faction match's local player loses, DW-190), or VICTORY keyed off the
    /// team-representative <c>winnerPlayer</c> instead of the local seat's own verdict (the 2v2 "winning ally sees
    /// DEFEAT" bug, DW-448) — now fail headless unit tests (<c>GameOverPresentationTests</c>) instead of only an
    /// in-engine session.
    ///
    /// <para>Pure C#: no Godot types, no <c>float</c>. Read-only over <see cref="WinStateStore"/> — these helpers
    /// never mutate sim state, so SimChecksum / goldens are untouched.</para>
    /// </summary>
    public static class GameOverPresentation
    {
        // ── DW-190: the per-frame match-outcome decision (MainScene._Process win-handling branch) ──────────────

        /// <summary>What the presentation layer must do this frame for the win state.</summary>
        public enum OutcomeKind
        {
            /// <summary>Match still running and the local seat needs no flip — do nothing.</summary>
            Continue = 0,
            /// <summary>The LOCAL player just latched LOST while the match continues — flip to the RevealAll
            /// spectator view + non-terminal defeat banner (Story 7.12), exactly once.</summary>
            EliminateLocal = 1,
            /// <summary>The match is over — fire the terminal game-over overlay with <see cref="OutcomeDecision.WinnerRep"/>.</summary>
            GameOver = 2,
        }

        /// <summary>One frame's win-presentation decision: the action kind plus, for
        /// <see cref="OutcomeKind.GameOver"/>, the 1-based team-representative winner faction
        /// (0 = the no-victor / match-over defeat form).</summary>
        public readonly struct OutcomeDecision
        {
            /// <summary>The action the presentation layer must take.</summary>
            public readonly OutcomeKind Kind;
            /// <summary>For <see cref="OutcomeKind.GameOver"/>: the 1-based winner faction
            /// (<see cref="WinStateStore.WinnerFaction"/>'s team representative — the lowest WON slot), or 0 for
            /// the no-victor form. Always 0 for the other kinds.</summary>
            public readonly int WinnerRep;

            public OutcomeDecision(OutcomeKind kind, int winnerRep) { Kind = kind; WinnerRep = winnerRep; }
        }

        /// <summary>
        /// The Story 7.12 per-player-elimination decision, extracted verbatim from MainScene's <c>_Process</c>
        /// win branch (DW-190):
        /// <list type="number">
        /// <item>Any faction latched WON → <see cref="OutcomeKind.GameOver"/> with the team representative
        /// (the terminal overlay replaces the spectator banner even for an already-eliminated local player).</item>
        /// <item>Else, EVERY active faction latched (<paramref name="isFullyResolved"/> — the
        /// <c>WinConditionSystem.IsFullyResolved()</c> gate, NEVER the any-latched <c>IsResolved()</c>) →
        /// <see cref="OutcomeKind.GameOver"/> with 0 (the LOST-only no-victor form).</item>
        /// <item>Else, the LOCAL seat latched LOST (real faction, not yet flipped) →
        /// <see cref="OutcomeKind.EliminateLocal"/>: spectate until the match resolves — NOT game over. This is
        /// the story's headline rule: in a &gt;2-faction match a local loss must never end the match early.</item>
        /// <item>Else → <see cref="OutcomeKind.Continue"/>.</item>
        /// </list>
        /// </summary>
        /// <param name="winState">The folded sim win state (read-only here).</param>
        /// <param name="isFullyResolved">The sim's <c>WinConditionSystem.IsFullyResolved()</c> — true only when
        /// EVERY active faction has a latched verdict. Callers must NOT substitute
        /// <see cref="WinStateStore.IsResolved"/> (any-latched), which is the exact DW-190 regression.</param>
        /// <param name="localFaction">The local player's seat. <see cref="Faction.Neutral"/> (a spectator seat)
        /// never triggers the elimination flip.</param>
        /// <param name="localAlreadyEliminated">MainScene's <c>_localEliminated</c> once-per-match guard — when the
        /// spectator flip already ran, the decision degrades to <see cref="OutcomeKind.Continue"/>.</param>
        public static OutcomeDecision DecideOutcome(
            WinStateStore winState, bool isFullyResolved, Faction localFaction, bool localAlreadyEliminated)
        {
            int winnerRep = winState.WinnerFaction();
            if (winnerRep != 0)
                return new OutcomeDecision(OutcomeKind.GameOver, winnerRep);

            if (isFullyResolved)
                return new OutcomeDecision(OutcomeKind.GameOver, 0); // no victor — the defeat/match-over form

            if (!localAlreadyEliminated && localFaction != Faction.Neutral
                && winState.Verdict[(int)localFaction] == WinStateStore.VERDICT_LOST)
                return new OutcomeDecision(OutcomeKind.EliminateLocal, 0);

            return new OutcomeDecision(OutcomeKind.Continue, 0);
        }

        // ── DW-448: the ShowGameOver local-win + winner-line headline decision ─────────────────────────────────

        /// <summary>The score-screen headline: whether the LOCAL seat shows VICTORY, plus the winner sub-line.</summary>
        public readonly struct Headline
        {
            /// <summary>True iff the LOCAL faction's OWN latched verdict is WON — never keyed off the
            /// team-representative <c>winnerPlayer</c> (the 2v2 "winning ally sees DEFEAT" bug, DW-448).</summary>
            public readonly bool LocalWin;
            /// <summary>The winner sub-heading: "Team Victory — …" when &gt;1 faction latched WON,
            /// "Player N Wins!" for a single winner, "No Victor — Match Over" for the no-victor form.</summary>
            public readonly string WinnerLine;

            public Headline(bool localWin, string winnerLine) { LocalWin = localWin; WinnerLine = winnerLine; }
        }

        /// <summary>
        /// The Story 9.15 headline decision, extracted verbatim from <c>MainScene.ShowGameOver</c> (DW-448):
        /// VICTORY iff the LOCAL player's OWN faction latched <see cref="WinStateStore.VERDICT_WON"/> —
        /// <paramref name="winnerPlayer"/> is the team REPRESENTATIVE (lowest WON slot), not the local seat, so
        /// keying VICTORY off it would show DEFEAT to a winning ally on a higher slot while that same player's
        /// stat row reads WON. The sub-heading phrases a team win (&gt;1 faction WON, e.g. a 2v2) as
        /// "Team Victory — P1, P2 Win!", a single winner as "Player N Wins!", and
        /// <paramref name="winnerPlayer"/> ≤ 0 (the LOST-only outcome) as "No Victor — Match Over".
        /// </summary>
        /// <param name="rows">The score-screen rows (<see cref="GameOverSummary.Build"/>) — supplies the WON set
        /// and the canonical short names for the team-victory phrasing.</param>
        /// <param name="winState">The folded sim win state (read-only here).</param>
        /// <param name="localFaction">The local player's policy-resolved seat
        /// (<c>EffectiveLocalFaction</c> at the call site; Player1 offline).</param>
        /// <param name="winnerPlayer">The 1-based team-representative winner (<c>ShowGameOver</c>'s arg);
        /// ≤ 0 = no victor.</param>
        public static Headline BuildHeadline(
            GameOverSummary.GameOverRow[] rows, WinStateStore winState, Faction localFaction, int winnerPlayer)
        {
            bool localWin = winState.Verdict[(int)localFaction] == WinStateStore.VERDICT_WON;

            // The winning factions (a team victory latches WON for every ally). Drives the sub-heading phrasing.
            var wonNames = new List<string>();
            foreach (GameOverSummary.GameOverRow r in rows)
                if (r.Won) wonNames.Add(r.Name);

            string winnerLine = winnerPlayer <= 0 ? "No Victor — Match Over"
                              : wonNames.Count > 1
                                  ? $"Team Victory — {string.Join(", ", wonNames)} Win!"
                                  : $"Player {winnerPlayer} Wins!";

            return new Headline(localWin, winnerLine);
        }
    }
}
