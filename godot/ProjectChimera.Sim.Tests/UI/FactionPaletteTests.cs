#nullable enable
using System.Collections.Generic;
using ProjectChimera.Core; // Faction, FactionRegistry
using ProjectChimera.UI;   // FactionPalette
using Xunit;

namespace ProjectChimera.Sim.Tests.UI
{
    /// <summary>Story 9.7 (AC2) — the Godot-free colorblind-safe faction palette (color never the sole signal).</summary>
    public class FactionPaletteTests
    {
        [Fact]
        public void EveryPlayerEntry_HasNonEmptyGlyphAndName()
        {
            // The colorblind rule: color is never the ONLY signal — a glyph + name always accompany the dot.
            for (int slot = 0; slot < FactionRegistry.PLAYER_COUNT; slot++)
            {
                var e = FactionPalette.ForSlot(slot);
                Assert.False(string.IsNullOrEmpty(e.Glyph), $"slot {slot} has an empty glyph");
                Assert.False(string.IsNullOrEmpty(e.Name),  $"slot {slot} has an empty name");
            }
        }

        [Fact]
        public void PlayerColorsAndGlyphs_AreDistinct()
        {
            var colors = new HashSet<uint>();
            var glyphs = new HashSet<string>();
            for (int slot = 0; slot < FactionRegistry.PLAYER_COUNT; slot++)
            {
                var e = FactionPalette.ForSlot(slot);
                Assert.True(colors.Add(e.Rgba),  $"slot {slot} color collides");
                Assert.True(glyphs.Add(e.Glyph), $"slot {slot} glyph collides");
            }
        }

        [Fact]
        public void ForSlot_MatchesForFaction()
        {
            for (int slot = 0; slot < FactionRegistry.PLAYER_COUNT; slot++)
            {
                var bySlot = FactionPalette.ForSlot(slot);
                var byFac  = FactionPalette.ForFaction(FactionRegistry.ToFaction(slot));
                Assert.Equal(byFac.Rgba, bySlot.Rgba);
                Assert.Equal(byFac.Glyph, bySlot.Glyph);
            }
        }

        [Fact]
        public void OutOfRange_FallsBackToNeutral()
        {
            var neutral = FactionPalette.ForFaction(Faction.Neutral);
            Assert.Equal(neutral.Rgba, FactionPalette.ForSlot(-1).Rgba);
            Assert.Equal(neutral.Rgba, FactionPalette.ForSlot(99).Rgba);
            Assert.Equal(neutral.Rgba, FactionPalette.ForFaction((Faction)200).Rgba);
        }

        [Fact]
        public void Rgba_PacksChannelsBigEndian()
        {
            var e = FactionPalette.ForFaction(Faction.Player1);
            uint expected = (uint)((e.R << 24) | (e.G << 16) | (e.B << 8) | e.A);
            Assert.Equal(expected, e.Rgba);
        }
    }
}
