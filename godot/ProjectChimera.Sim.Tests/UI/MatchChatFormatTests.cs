#nullable enable
using System.Collections.Generic;
using ProjectChimera.Core; // Faction, FactionRegistry
using ProjectChimera.UI;   // MatchChatFormat, FactionPalette
using Xunit;

namespace ProjectChimera.Sim.Tests.UI
{
    /// <summary>
    /// DW-385 — the in-match chat overlay's speaker name/color mapping. Before this closure the overlay carried its
    /// own 5-entry BBCode color list and a Player1–Player4-only name switch, so every Player5–Player8 speaker
    /// rendered as the placeholder "??" in the same fallback gray. These tests pin the 8-player mapping on the
    /// Godot-free formatter the overlay now delegates to.
    /// </summary>
    public class MatchChatFormatTests
    {
        // ── DW-385: the 5–8 slots the overlay used to drop ─────────────────────────

        [Fact]
        public void EveryPlayerFaction_HasADistinctNonPlaceholderName()
        {
            var names = new HashSet<string>();
            for (int slot = 0; slot < FactionRegistry.PLAYER_COUNT; slot++)
            {
                Faction f = FactionRegistry.ToFaction(slot);
                string name = MatchChatFormat.DisplayName(f);

                Assert.False(string.IsNullOrWhiteSpace(name), $"{f} has a blank chat name");
                Assert.NotEqual("??", name);
                Assert.NotEqual(MatchChatFormat.DisplayName(Faction.Neutral), name);
                Assert.True(names.Add(name), $"{f}'s chat name '{name}' collides with another player's");
            }
            Assert.Equal(FactionRegistry.PLAYER_COUNT, names.Count);
        }

        [Fact]
        public void EveryPlayerFaction_HasADistinctColor_NotTheNeutralFallback()
        {
            string neutral = MatchChatFormat.ColorHex(Faction.Neutral);
            var colors = new HashSet<string>();
            for (int slot = 0; slot < FactionRegistry.PLAYER_COUNT; slot++)
            {
                Faction f = FactionRegistry.ToFaction(slot);
                string hex = MatchChatFormat.ColorHex(f);

                Assert.NotEqual(neutral, hex);
                Assert.True(colors.Add(hex), $"{f}'s chat color '{hex}' collides with another player's");
            }
            Assert.Equal(FactionRegistry.PLAYER_COUNT, colors.Count);
        }

        [Theory]
        [InlineData(Faction.Player5)]
        [InlineData(Faction.Player6)]
        [InlineData(Faction.Player7)]
        [InlineData(Faction.Player8)]
        public void HighSlotSpeaker_IsAttributedAndColored(Faction faction)
        {
            string line = MatchChatFormat.ChatLine(faction, "gg");

            Assert.Contains($"{MatchChatFormat.DisplayName(faction)}:", line);
            Assert.Contains($"[color={MatchChatFormat.ColorHex(faction)}]", line);
            Assert.DoesNotContain("??", line);
            Assert.EndsWith(" gg", line);
        }

        // ── The formatter is the palette's consumer, not a second source of truth ──

        [Fact]
        public void NameAndColor_ComeFromTheCanonicalFactionPalette()
        {
            for (int idx = 0; idx < FactionPalette.Count; idx++)
            {
                var f = (Faction)idx;
                var entry = FactionPalette.ForFaction(f);
                Assert.Equal(entry.Name, MatchChatFormat.DisplayName(f));
                Assert.Equal(entry.HexRgb, MatchChatFormat.ColorHex(f));
            }
        }

        [Fact]
        public void HexRgb_IsTheOpaqueRgbChannelsAsLowercaseBbcodeHex()
        {
            var e = FactionPalette.ForFaction(Faction.Player5);
            Assert.Equal($"#{e.R:x2}{e.G:x2}{e.B:x2}", e.HexRgb);
            Assert.Equal(7, e.HexRgb.Length);
        }

        [Fact]
        public void OutOfRangeFaction_FallsBackToNeutral_WithoutThrowing()
        {
            Assert.Equal(MatchChatFormat.DisplayName(Faction.Neutral), MatchChatFormat.DisplayName((Faction)200));
            Assert.Equal(MatchChatFormat.ColorHex(Faction.Neutral),    MatchChatFormat.ColorHex((Faction)200));
        }

        // ── Sanitization / line shape ─────────────────────────────────────────────

        [Fact]
        public void Sanitize_NeutralizesBbcodeBrackets()
        {
            string safe = MatchChatFormat.Sanitize("[color=red]pwn[/color]");
            Assert.DoesNotContain("[", safe);
            Assert.DoesNotContain("]", safe);
            Assert.Contains("color=red", safe);
        }

        [Fact]
        public void ChatLine_SanitizesTheMessageButKeepsExactlyOneColorTag()
        {
            string line = MatchChatFormat.ChatLine(Faction.Player7, "[img]evil[/img]");

            // The only BBCode in the line is the speaker-name color the formatter itself opened/closed.
            Assert.Equal(1, CountOccurrences(line, "[color="));
            Assert.Equal(1, CountOccurrences(line, "[/color]"));
            Assert.DoesNotContain("[img]", line);
        }

        [Fact]
        public void ChatLine_ToleratesNullMessage()
        {
            string line = MatchChatFormat.ChatLine(Faction.Player1, null);
            Assert.Contains($"{MatchChatFormat.DisplayName(Faction.Player1)}:", line);
        }

        [Fact]
        public void SystemLine_IsGrayStarredAndSanitized()
        {
            string line = MatchChatFormat.SystemLine("Player 6 dropped [x]");
            Assert.StartsWith($"[color={MatchChatFormat.SystemColorHex}]* ", line);
            Assert.EndsWith("[/color]", line);
            Assert.DoesNotContain("[x]", line);
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            int count = 0;
            for (int i = haystack.IndexOf(needle, System.StringComparison.Ordinal); i >= 0;
                 i = haystack.IndexOf(needle, i + needle.Length, System.StringComparison.Ordinal))
                count++;
            return count;
        }
    }
}
