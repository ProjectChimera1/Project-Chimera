#nullable enable
using ProjectChimera.Core; // Faction

namespace ProjectChimera.UI
{
    /// <summary>
    /// Story 9.7 (AC2) — the canonical, Godot-free, Tier-1-testable faction/team palette. An 8-player,
    /// Okabe-Ito-derived colorblind-safe color set, each entry pairing an RGBA color (as bytes / a packed uint) with
    /// a distinct <see cref="Entry.Glyph"/> and a short <see cref="Entry.Name"/>. Consolidates the previously
    /// divergent presentation color sources (FactionVisualsPhase's P1/P2 blue/red, MatchChatOverlay's 5-color BBCode
    /// list) into one table indexed by <c>(int)Faction</c> (0 = Neutral, 1..8 = Player1..Player8).
    ///
    /// <para><b>Colorblind rule:</b> per-slot color is never the ONLY signal — every entry carries a
    /// <see cref="Entry.Glyph"/> + <see cref="Entry.Name"/> so the lobby can render a dot AND a glyph/label
    /// together.</para>
    ///
    /// This type is Godot-free (RGBA as bytes); the presentation-side <c>ToColor()</c> extension
    /// (<c>FactionPaletteGodot.cs</c>) converts an <see cref="Entry"/> to a <c>Godot.Color</c>.
    /// </summary>
    public static class FactionPalette
    {
        /// <summary>One palette entry: an RGBA color + a colorblind glyph + a short display name.</summary>
        public readonly struct Entry
        {
            public readonly byte R;
            public readonly byte G;
            public readonly byte B;
            public readonly byte A;
            /// <summary>A distinct non-color signal (shape/mark) so color is never the sole differentiator.</summary>
            public readonly string Glyph;
            /// <summary>Short display name (e.g. "P1").</summary>
            public readonly string Name;

            public Entry(byte r, byte g, byte b, byte a, string glyph, string name)
            {
                R = r; G = g; B = b; A = a; Glyph = glyph; Name = name;
            }

            /// <summary>The color packed big-endian as 0xRRGGBBAA.</summary>
            public uint Rgba => (uint)((R << 24) | (G << 16) | (B << 8) | A);

            /// <summary>The opaque RGB channels as a <c>#rrggbb</c> hex string — the form BBCode
            /// (<c>[color=#rrggbb]</c>) and CSS take. Alpha is deliberately omitted: the text surfaces that need
            /// this (chat, rich-text labels) have no per-glyph alpha channel.</summary>
            public string HexRgb => $"#{R:x2}{G:x2}{B:x2}";
        }

        // Index by (int)Faction: 0 = Neutral, 1..8 = Player1..Player8. Player colors are the Okabe-Ito set
        // (colorblind-safe); glyphs are distinct shapes so the color is never the only signal.
        private static readonly Entry[] Entries =
        {
            new(0x80, 0x80, 0x80, 0xFF, "◇", "Neutral"), // 0 Neutral — gray, hollow diamond
            new(0x00, 0x72, 0xB2, 0xFF, "●", "P1"),      // 1 Player1  — Okabe-Ito Blue
            new(0xD5, 0x5E, 0x00, 0xFF, "■", "P2"),      // 2 Player2  — Okabe-Ito Vermillion
            new(0x00, 0x9E, 0x73, 0xFF, "▲", "P3"),      // 3 Player3  — Okabe-Ito Bluish Green
            new(0xE6, 0x9F, 0x00, 0xFF, "◆", "P4"),      // 4 Player4  — Okabe-Ito Orange
            new(0x56, 0xB4, 0xE9, 0xFF, "★", "P5"),      // 5 Player5  — Okabe-Ito Sky Blue
            new(0xCC, 0x79, 0xA7, 0xFF, "⬢", "P6"),      // 6 Player6  — Okabe-Ito Reddish Purple
            new(0xF0, 0xE4, 0x42, 0xFF, "✚", "P7"),      // 7 Player7  — Okabe-Ito Yellow
            new(0xBF, 0xBF, 0xBF, 0xFF, "✦", "P8"),      // 8 Player8  — light gray (Okabe-Ito black lifted for a dark UI)
        };

        /// <summary>Total entries (Neutral + 8 players) = <see cref="FactionRegistry.FACTION_ARRAY_SIZE"/>.</summary>
        public const int Count = 9;

        /// <summary>Palette entry for a faction. An out-of-range faction falls back to the Neutral entry.</summary>
        public static Entry ForFaction(Faction faction)
        {
            int idx = (int)faction;
            return (uint)idx < (uint)Entries.Length ? Entries[idx] : Entries[0];
        }

        /// <summary>Palette entry for a 0-based player slot (slot 0 → Player1). Out-of-range → Neutral.</summary>
        public static Entry ForSlot(int slot)
            => (uint)slot < (uint)FactionRegistry.PLAYER_COUNT
                ? Entries[slot + 1]
                : Entries[0];
    }
}
