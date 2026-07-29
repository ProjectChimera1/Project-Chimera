#nullable enable
using System.Collections.Generic;
using ProjectChimera.UI; // FactionPalette (Godot-free canonical faction/team color table, Story 9.7)

namespace ProjectChimera.Core
{
    /// <summary>
    /// Story 9.15 — the Godot-free, Tier-1-testable score-screen data builder. Replaces the P1/P2-hardcoded body of
    /// <c>MainScene.ShowGameOver</c> (which only ever rendered two columns) with a pure, per-active-slot projection:
    /// one <see cref="GameOverRow"/> per active faction (<c>Player1..Player{activeCount}</c>, up to 8), each carrying
    /// its verdict (WON/LOST from the folded <see cref="WinStateStore"/>), its match stats (from
    /// <see cref="MatchStats"/>), and the canonical faction color-key (from the Godot-free <see cref="FactionPalette"/>).
    ///
    /// <para>No Godot types and no <c>float</c>: the color is carried as the palette's RGBA bytes + glyph + name, NEVER
    /// a <c>Godot.Color</c> — the presentation layer (<c>ShowGameOver</c>) converts each row's bytes to a
    /// <c>Color</c> via <c>FactionPaletteGodot.ToColor</c>. This keeps the 4-slot correctness provable at the data
    /// layer, independent of the Godot render.</para>
    /// </summary>
    public static class GameOverSummary
    {
        /// <summary>One score-screen row: an active faction's identity, verdict, stats, and canonical color-key.</summary>
        public readonly struct GameOverRow
        {
            /// <summary>0-based player slot (0 → Player1).</summary>
            public readonly int Slot;
            /// <summary>The 1-based faction for this slot (<see cref="FactionRegistry.ToFaction"/>).</summary>
            public readonly Faction Faction;
            /// <summary>The raw folded verdict (<see cref="WinStateStore.VERDICT_NONE"/>/<c>_WON</c>/<c>_LOST</c>).</summary>
            public readonly int Verdict;
            /// <summary>True iff this faction latched <see cref="WinStateStore.VERDICT_WON"/>.</summary>
            public readonly bool Won;
            /// <summary>Units this faction killed.</summary>
            public readonly int Kills;
            /// <summary>Units this faction lost.</summary>
            public readonly int Losses;
            /// <summary>Units this faction trained/spawned.</summary>
            public readonly int UnitsBuilt;
            /// <summary>Ore this faction mined.</summary>
            public readonly int OreMined;
            /// <summary>Story 11.2 — crystal this faction mined.</summary>
            public readonly int CrystalMined;
            /// <summary>Story 11.2 — enemy buildings this faction razed.</summary>
            public readonly int BuildingsRazed;
            /// <summary>Canonical faction color-key (RGBA bytes — never a Godot.Color).</summary>
            public readonly byte ColorR, ColorG, ColorB, ColorA;
            /// <summary>Colorblind glyph paired with the color (color is never the only signal).</summary>
            public readonly string ColorGlyph;
            /// <summary>Short faction display name (e.g. "P3").</summary>
            public readonly string Name;

            public GameOverRow(int slot, Faction faction, int verdict, int kills, int losses,
                               int unitsBuilt, int oreMined, int crystalMined, int buildingsRazed,
                               FactionPalette.Entry color)
            {
                Slot           = slot;
                Faction        = faction;
                Verdict        = verdict;
                Won            = verdict == WinStateStore.VERDICT_WON;
                Kills          = kills;
                Losses         = losses;
                UnitsBuilt     = unitsBuilt;
                OreMined       = oreMined;
                CrystalMined   = crystalMined;
                BuildingsRazed = buildingsRazed;
                ColorR     = color.R;
                ColorG     = color.G;
                ColorB     = color.B;
                ColorA     = color.A;
                ColorGlyph = color.Glyph;
                Name       = color.Name;
            }

            /// <summary>Verdict label for the score screen ("WON" / "LOST" / "—").</summary>
            public string VerdictLabel => Verdict switch
            {
                WinStateStore.VERDICT_WON  => "WON",
                WinStateStore.VERDICT_LOST => "LOST",
                _                          => "—",
            };
        }

        /// <summary>
        /// Build one <see cref="GameOverRow"/> per ACTIVE faction — every playable faction (<c>Player1..Player{PLAYER_COUNT}</c>)
        /// whose folded <see cref="WinStateStore.Verdict"/> is non-<see cref="WinStateStore.VERDICT_NONE"/>. This makes the
        /// row set correct-by-construction on a NON-CONTIGUOUS active set (e.g. active slots {P1,P3}: P2 is inactive/NONE and
        /// is skipped, P3 is included — never the bare-count mapping's "emit P2, drop P3" defect). Each row's WON/LOST comes
        /// straight from the folded verdict, so a team victory renders every ally as WON. Godot-free.
        /// </summary>
        public static GameOverRow[] Build(MatchStats stats, WinStateStore winState)
        {
            var rows = new List<GameOverRow>(FactionRegistry.PLAYER_COUNT);
            for (int slot = 0; slot < FactionRegistry.PLAYER_COUNT; slot++)
            {
                Faction f = FactionRegistry.ToFaction(slot);
                int verdict = winState.Verdict[(int)f];
                if (verdict == WinStateStore.VERDICT_NONE) continue; // inactive / undecided slot — no row
                rows.Add(new GameOverRow(
                    slot, f, verdict,
                    stats.Kills(f), stats.Losses(f), stats.UnitsBuilt(f), stats.OreMined(f),
                    stats.CrystalMined(f), stats.BuildingsRazed(f),
                    FactionPalette.ForFaction(f)));
            }
            return rows.ToArray();
        }
    }
}
