#nullable enable
using ProjectChimera.Core;   // GameOverSummary, MatchStats, WinStateStore, Faction, FactionRegistry, Fixed
using ProjectChimera.UI;     // FactionPalette (canonical color-key)
using Xunit;

namespace ProjectChimera.Sim.Tests.UI
{
    /// <summary>
    /// Story 9.15 — the Godot-free 4-slot correctness proof for the score-screen data builder. Asserts
    /// <see cref="GameOverSummary.Build"/> yields exactly <c>activeCount</c> rows (up to 4) — each with the correct
    /// faction, verdict, kills/losses/built/ore, and the canonical <see cref="FactionPalette"/> color — for a resolved
    /// 2v2 and a resolved 4-FFA outcome. The former P1/P2 truncation (only two columns ever rendered) would fail this:
    /// slots 3–4 must be present and correct.
    /// </summary>
    public class GameOverSummaryTests
    {
        private const int WON  = WinStateStore.VERDICT_WON;
        private const int LOST = WinStateStore.VERDICT_LOST;

        private static void AssertColorMatchesPalette(in GameOverSummary.GameOverRow r)
        {
            FactionPalette.Entry e = FactionPalette.ForFaction(r.Faction);
            Assert.Equal(e.R, r.ColorR);
            Assert.Equal(e.G, r.ColorG);
            Assert.Equal(e.B, r.ColorB);
            Assert.Equal(e.A, r.ColorA);
            Assert.Equal(e.Name, r.Name);
        }

        [Fact]
        public void TwoVsTwo_FourRows_SlotsThreeAndFourPresent_VerdictsKillsColorsCorrect()
        {
            var win = new WinStateStore();
            win.Verdict[(int)Faction.Player1] = WON;
            win.Verdict[(int)Faction.Player2] = WON;
            win.Verdict[(int)Faction.Player3] = LOST;
            win.Verdict[(int)Faction.Player4] = LOST;

            var stats = new MatchStats();
            stats.RecordKill(Faction.Player3, Faction.Player1); // P1 kills=1, P3 losses=1
            stats.RecordUnitBuilt(Faction.Player4);
            stats.RecordUnitBuilt(Faction.Player4);             // P4 built=2
            stats.RecordOreMined(Faction.Player2, Fixed.FromInt(50)); // P2 ore=50

            GameOverSummary.GameOverRow[] rows = GameOverSummary.Build(stats, win);

            Assert.Equal(4, rows.Length); // no P1/P2 truncation

            // Slot → faction mapping is exact, including the previously-dropped slots 3 and 4.
            Assert.Equal(Faction.Player1, rows[0].Faction);
            Assert.Equal(Faction.Player2, rows[1].Faction);
            Assert.Equal(Faction.Player3, rows[2].Faction);
            Assert.Equal(Faction.Player4, rows[3].Faction);

            // Verdicts (team victory renders BOTH allies WON).
            Assert.True(rows[0].Won);
            Assert.True(rows[1].Won);
            Assert.False(rows[2].Won);
            Assert.False(rows[3].Won);
            Assert.Equal("WON",  rows[0].VerdictLabel);
            Assert.Equal("LOST", rows[3].VerdictLabel);

            // Stats copied per-faction (no index drift).
            Assert.Equal(1,  rows[0].Kills);
            Assert.Equal(1,  rows[2].Losses);
            Assert.Equal(2,  rows[3].UnitsBuilt);
            Assert.Equal(50, rows[1].OreMined);

            // Canonical faction color-key per row (Okabe-Ito palette; distinct per slot).
            foreach (GameOverSummary.GameOverRow r in rows) AssertColorMatchesPalette(r);
            Assert.NotEqual((rows[0].ColorR, rows[0].ColorG, rows[0].ColorB),
                            (rows[3].ColorR, rows[3].ColorG, rows[3].ColorB));
        }

        [Fact]
        public void FourFfa_LastFactionStanding_EachRowCorrect_NoTruncation()
        {
            var win = new WinStateStore();
            win.Verdict[(int)Faction.Player1] = LOST;
            win.Verdict[(int)Faction.Player2] = WON;  // AI faction is the last standing in the e2e
            win.Verdict[(int)Faction.Player3] = LOST;
            win.Verdict[(int)Faction.Player4] = LOST;

            var stats = new MatchStats();

            GameOverSummary.GameOverRow[] rows = GameOverSummary.Build(stats, win);

            Assert.Equal(4, rows.Length);
            Assert.True(rows[1].Won);                 // P2 WON
            Assert.False(rows[0].Won);
            Assert.False(rows[2].Won);
            Assert.False(rows[3].Won);
            for (int i = 0; i < rows.Length; i++)
            {
                Assert.Equal(FactionRegistry.ToFaction(i), rows[i].Faction);
                AssertColorMatchesPalette(rows[i]);
            }
        }

        [Fact]
        public void Build_OneRowPerLatchedVerdict_TwoSlot_And_EmptyWhenUnresolved()
        {
            var stats = new MatchStats();

            // Two-slot resolved match → exactly two rows (back-compat with the pre-9.15 P1/P2 case).
            var two = new WinStateStore();
            two.Verdict[(int)Faction.Player1] = WON;
            two.Verdict[(int)Faction.Player2] = LOST;
            GameOverSummary.GameOverRow[] twoRows = GameOverSummary.Build(stats, two);
            Assert.Equal(2, twoRows.Length);
            Assert.True(twoRows[0].Won);
            Assert.False(twoRows[1].Won);

            // No latched verdict at all → no rows (nothing to render).
            Assert.Empty(GameOverSummary.Build(stats, new WinStateStore()));
        }

        [Fact]
        public void Build_NonContiguousActiveSet_SkipsInactiveSlot_KeepsRealHigherSlot()
        {
            // Active factions {P1, P3}; P2 and P4 are inactive (VERDICT_NONE). The old bare-count mapping (rows for
            // slots 0..activeCount-1) would emit inactive P2 and DROP the real P3 — this proves correct-by-construction.
            var win = new WinStateStore();
            win.Verdict[(int)Faction.Player1] = WON;
            win.Verdict[(int)Faction.Player3] = LOST;
            var stats = new MatchStats();

            GameOverSummary.GameOverRow[] rows = GameOverSummary.Build(stats, win);

            Assert.Equal(2, rows.Length);
            Assert.Equal(Faction.Player1, rows[0].Faction);
            Assert.Equal(Faction.Player3, rows[1].Faction); // the real higher slot is present…
            Assert.DoesNotContain(rows, r => r.Faction == Faction.Player2); // …and inactive P2 is not rendered
            Assert.True(rows[0].Won);
            Assert.False(rows[1].Won);
        }
    }
}
