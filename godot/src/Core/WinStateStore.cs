#nullable enable
namespace ProjectChimera.Core
{
    /// <summary>
    /// Per-match win-condition runtime state (Story 7.11) — the mid-match-mutable substrate the sim-layer
    /// <see cref="WinConditionSystem"/> reads/writes each tick. Built on the <see cref="ResearchStore"/> pattern:
    /// per-faction Struct-of-Arrays sized <see cref="FACTION_COUNT"/> (indices 0-4, cast from <see cref="Faction"/>;
    /// index 0/Neutral unused), integer ticks only, <see cref="Clear"/> restores post-ctor state, owned as a
    /// <see cref="ProjectChimera.Core.Sim.SimulationHost"/> property and reset from its <c>ClearForReset</c>.
    ///
    /// <para>Folded into <c>SimChecksum</c> (v19) in declaration order before the SimRng block: the scalar
    /// <see cref="MatchTicks"/> grace/elapsed counter first, then per ACTIVE faction (ascending — mirroring the
    /// ResourceStore/ResearchStore <c>ActiveFactions</c> iteration, NEVER a raw 0-4 stride)
    /// <see cref="KothHoldTicks"/>, <see cref="SurvivalRemaining"/>, and <see cref="Verdict"/>. Every field is a
    /// peer-divergent live win-state value, all <c>int</c> → cross-platform safe.</para>
    /// </summary>
    public class WinStateStore
    {
        private const int FACTION_COUNT = FactionRegistry.FACTION_ARRAY_SIZE; // 9: Neutral(0) + Player1..Player8

        /// <summary>Verdict sentinel — no outcome resolved yet for this faction.</summary>
        public const int VERDICT_NONE = 0;
        /// <summary>Verdict sentinel — this faction has WON (latched, never overwritten).</summary>
        public const int VERDICT_WON = 1;
        /// <summary>Verdict sentinel — this faction has LOST (latched, never overwritten).</summary>
        public const int VERDICT_LOST = 2;

        /// <summary>Elapsed simulation ticks since match start — advances by 1 per
        /// <see cref="WinConditionSystem"/> tick until the match resolves (the system's Tick early-returns once a
        /// verdict latches, so this stops at the resolution tick; the authored tick IS the count, no
        /// <see cref="Fixed"/>/dt conversion). Gates the win-evaluation grace period; a scalar (not per-faction)
        /// so it is folded once, ahead of the per-faction fields.</summary>
        public int MatchTicks;

        /// <summary>Per-faction King-of-the-Hill contiguous sole-hold tick counter. Advances only on a tick the
        /// faction SOLELY holds the designated region; resets to 0 the tick it no longer solely holds. Meaningless
        /// for non-KotH win conditions (stays 0).</summary>
        public readonly int[] KothHoldTicks;

        /// <summary>Per-faction Timed-Survival remaining-ticks countdown (decremented by 1 per tick while &gt; 0).
        /// Set once at scenario-apply for the designated faction only; 0 for every other faction and every non-
        /// survival win condition.</summary>
        public readonly int[] SurvivalRemaining;

        /// <summary>Per-faction latched verdict (<see cref="VERDICT_NONE"/>/<see cref="VERDICT_WON"/>/
        /// <see cref="VERDICT_LOST"/>). Once set to a non-none value it is never overwritten (the match outcome
        /// is final).</summary>
        public readonly int[] Verdict;

        public WinStateStore()
        {
            KothHoldTicks     = new int[FACTION_COUNT];
            SurvivalRemaining = new int[FACTION_COUNT];
            Verdict           = new int[FACTION_COUNT];
        }

        /// <summary>True once ANY real faction (index ≥ 1) has a latched non-<see cref="VERDICT_NONE"/> verdict —
        /// the match is resolved. Review P1: this deliberately includes the LOST-only outcome — in a
        /// single-active-faction match a preset loss calls <c>Resolve(Neutral, loser)</c> and only
        /// <see cref="VERDICT_LOST"/> latches (a Neutral "winner" is never latched WON), yet the match is still
        /// over; a WON-only scan would leave it running forever. Index 0/Neutral is skipped — it is never
        /// assigned a verdict.</summary>
        public bool IsResolved()
        {
            for (int f = 1; f < FACTION_COUNT; f++) // skip Neutral/index 0 — never assigned a verdict
                if (Verdict[f] != VERDICT_NONE) return true;
            return false;
        }

        /// <summary>
        /// The 1-based <see cref="Faction"/> value (<c>Player1 == 1</c>) of the faction that has WON, or 0 when no
        /// faction has yet won. Presentation calls <c>ShowGameOver((int)winnerFaction)</c> directly — the 1-based
        /// enum aligns with the existing 1-based overlay arg with no adapter math. Scans from index 1: Neutral
        /// (index 0) is never a winner.
        /// </summary>
        public int WinnerFaction()
        {
            for (int f = 1; f < FACTION_COUNT; f++) // skip Neutral/index 0 — never a winner
                if (Verdict[f] == VERDICT_WON) return f;
            return 0;
        }

        /// <summary>
        /// Review P1 — the 1-based <see cref="Faction"/> value of the SOLE faction latched
        /// <see cref="VERDICT_LOST"/> when NO faction has <see cref="VERDICT_WON"/>: the LOST-only outcome a
        /// single-active-faction preset loss produces (<c>Resolve(Neutral, loser)</c> latches only the loser —
        /// Neutral is never latched WON). Returns 0 when any faction has WON (the winner path owns the overlay)
        /// or when there is not exactly one latched loser. Presentation polls this alongside
        /// <see cref="WinnerFaction"/> to drive the defeat form of the game-over overlay.
        /// </summary>
        public int SoleLoserFaction()
        {
            int loser = 0, losses = 0;
            for (int f = 1; f < FACTION_COUNT; f++) // skip Neutral/index 0 — never assigned a verdict
            {
                if (Verdict[f] == VERDICT_WON) return 0;
                if (Verdict[f] == VERDICT_LOST) { loser = f; losses++; }
            }
            return losses == 1 ? loser : 0;
        }

        /// <summary>
        /// Story 3.10-style Edit↔Play reset support: restore every field to its post-construction value. A cleared
        /// store is byte-for-byte equal to a freshly-constructed one.
        /// </summary>
        public void Clear()
        {
            MatchTicks = 0;
            for (int f = 0; f < FACTION_COUNT; f++)
            {
                KothHoldTicks[f]     = 0;
                SurvivalRemaining[f] = 0;
                Verdict[f]           = VERDICT_NONE;
            }
        }
    }
}
