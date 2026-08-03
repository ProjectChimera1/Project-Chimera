#nullable enable
using System.Collections.Generic;
using System.Linq;
using ProjectChimera.Core;                  // Faction
using ProjectChimera.Core.Bootstrap;         // TeamColorPalette
using ProjectChimera.Core.Definitions;       // ScenarioData, ScenarioPlayerSlot
using ProjectChimera.Core.Skirmish;          // SkirmishSetup, SetupSlot, SlotKind, SkirmishSetupToScenario, FactionEntry
using Xunit;

namespace ProjectChimera.Sim.Tests.Skirmish
{
    /// <summary>
    /// DW-460 + DW-462 — the team-color slot-mapping regression net. The slot→palette mapping shipped INVERTED twice
    /// during Story 11.1 (PATCH 7 + follow-up-2, each caught only by human review — Godot-coupled, previously
    /// untestable headlessly), and the setup-screen swatch drifted from the in-match color for any config whose
    /// active slots are not row-0-contiguous. Both mappings now live behind Godot-free seams — the palette + faction
    /// −1 shift in <see cref="TeamColorPalette"/> (DW-462) and the row→launch-index rank in
    /// <see cref="SkirmishSetupToScenario.LaunchIndexBySlot"/> (DW-460) — pinned here by headless RGBA assertions.
    /// </summary>
    public class TeamColorSlotMappingTests
    {
        private static void AssertRgb(float r, float g, float b, TeamColorPalette.Rgb actual)
        {
            Assert.Equal(r, actual.R);
            Assert.Equal(g, actual.G);
            Assert.Equal(b, actual.B);
        }

        // ── DW-462: the palette pins (blue/red/green/gold by LAUNCH index) ────────────

        [Fact]
        public void Palette_PinsTheFourLaunchIndexColors_Verbatim()
        {
            // Index 0/1 are the original P1 blue / P2 red VERBATIM (visual/golden continuity); 2/3 = green/gold.
            // Exact float equality on purpose: the seam must carry the pre-extraction literals bit-identically.
            Assert.Equal(4, TeamColorPalette.SlotCount);
            AssertRgb(0.2f, 0.5f, 1.0f, TeamColorPalette.SlotColorAt(0));  // blue
            AssertRgb(1.0f, 0.3f, 0.2f, TeamColorPalette.SlotColorAt(1));  // red
            AssertRgb(0.3f, 0.85f, 0.4f, TeamColorPalette.SlotColorAt(2)); // green
            AssertRgb(0.95f, 0.8f, 0.2f, TeamColorPalette.SlotColorAt(3)); // gold
        }

        [Fact]
        public void SlotColorAt_ClampsOutOfRangeIndices_NeverThrows()
        {
            Assert.Equal(TeamColorPalette.SlotColorAt(0), TeamColorPalette.SlotColorAt(-7));
            Assert.Equal(TeamColorPalette.SlotColorAt(TeamColorPalette.SlotCount - 1),
                         TeamColorPalette.SlotColorAt(99));
        }

        // ── DW-462: the −1 faction shift (the twice-shipped inversion) ────────────────

        [Fact]
        public void ColorForFaction_ShiftsTheOneBasedOrdinal_Player1IsBlueNotRed()
        {
            // THE recurring 11.1 bug: without the −1 shift Player1 rendered SlotColorAt(1) = red and Player2 green.
            AssertRgb(0.2f, 0.5f, 1.0f, TeamColorPalette.ColorForFaction(Faction.Player1));  // blue
            AssertRgb(1.0f, 0.3f, 0.2f, TeamColorPalette.ColorForFaction(Faction.Player2));  // red
            AssertRgb(0.3f, 0.85f, 0.4f, TeamColorPalette.ColorForFaction(Faction.Player3)); // green
            AssertRgb(0.95f, 0.8f, 0.2f, TeamColorPalette.ColorForFaction(Faction.Player4)); // gold
        }

        [Fact]
        public void InactiveSlotColor_IsOutsideTheTeamPalette()
        {
            // The setup screen's Open/Closed swatch grey must never collide with a live team color, or an inactive
            // row would masquerade as a player.
            for (int i = 0; i < TeamColorPalette.SlotCount; i++)
                Assert.NotEqual(TeamColorPalette.SlotColorAt(i), TeamColorPalette.InactiveSlotColor);
        }

        // ── DW-460: swatch key = rank among active slots in LAUNCH order, not the row index ──

        private static SetupSlot Row(int slot, SlotKind kind, int team = 0) => new()
        {
            Slot = slot, Kind = kind, Team = team,
            FactionId = kind == SlotKind.Human || kind == SlotKind.Ai ? "alpha" : null,
        };

        [Fact]
        public void LaunchIndexBySlot_KeysByActiveRank_NotRowIndex()
        {
            // THE DW-460 drift case from the ledger: rows 0/1 Open, Human in row 2, AI in row 3. The human launches
            // as Player1 (blue). Keyed by ROW index (the pre-fix swatch), the human's swatch showed SlotColorAt(2) =
            // green while it played blue.
            var rows = new List<SetupSlot>
            {
                Row(0, SlotKind.Open), Row(1, SlotKind.Open), Row(2, SlotKind.Human), Row(3, SlotKind.Ai),
            };
            IReadOnlyDictionary<int, int> idx = SkirmishSetupToScenario.LaunchIndexBySlot(rows);

            Assert.Equal(0, idx[2]); // human row → launch index 0 → the blue it actually plays
            Assert.Equal(1, idx[3]); // ai row → launch index 1 → red
            // And the color the fixed mapping yields for the human row is NOT the old row-keyed green.
            Assert.NotEqual(TeamColorPalette.SlotColorAt(2), TeamColorPalette.SlotColorAt(idx[2]));
        }

        [Fact]
        public void LaunchIndexBySlot_HumanRanksFirst_EvenFromAHigherRow()
        {
            // Mirrors Build's Human-first renumbering (offline the local human is hardwired to Player1): a rank
            // computed by row order among actives alone would paint the AI blue and the human red — drift again.
            var rows = new List<SetupSlot> { Row(0, SlotKind.Ai), Row(1, SlotKind.Human) };
            IReadOnlyDictionary<int, int> idx = SkirmishSetupToScenario.LaunchIndexBySlot(rows);

            Assert.Equal(0, idx[1]); // human (row 1) → Player1 blue
            Assert.Equal(1, idx[0]); // ai (row 0) → Player2 red
        }

        [Fact]
        public void LaunchIndexBySlot_OmitsInactiveRows()
        {
            var rows = new List<SetupSlot>
            {
                Row(0, SlotKind.Closed), Row(1, SlotKind.Human), Row(2, SlotKind.Open), Row(3, SlotKind.Ai),
            };
            IReadOnlyDictionary<int, int> idx = SkirmishSetupToScenario.LaunchIndexBySlot(rows);

            Assert.Equal(2, idx.Count);       // only the two active rows carry a launch index / team color
            Assert.False(idx.ContainsKey(0)); // Closed → inactive grey swatch
            Assert.False(idx.ContainsKey(2)); // Open → inactive grey swatch
        }

        [Fact]
        public void LaunchIndexBySlot_NullAndEmpty_ReturnEmpty()
        {
            Assert.Empty(SkirmishSetupToScenario.LaunchIndexBySlot(null));
            Assert.Empty(SkirmishSetupToScenario.LaunchIndexBySlot(new List<SetupSlot>()));
        }

        // ── DW-460: the anti-drift lock — the swatch mapping IS Build's renumbering ──────

        [Fact]
        public void LaunchIndexBySlot_MatchesBuildsContiguousRenumbering()
        {
            // The swatch mapping and Build's renumbering must be the SAME function or the swatch lies again. Each
            // active row carries a distinct Team marker; the built PlayerSlot at that row's launch index must carry
            // it. Non-contiguous + human-after-AI on purpose (both historical drift shapes at once).
            var setup = new SkirmishSetup
            {
                MapId = "m1",
                Slots = new List<SetupSlot>
                {
                    Row(0, SlotKind.Open), Row(1, SlotKind.Ai, team: 2),
                    Row(2, SlotKind.Human, team: 1), Row(3, SlotKind.Closed),
                },
            };
            ScenarioData built = SkirmishSetupToScenario.Build(setup, BaseMap(4), Factions("alpha"));
            IReadOnlyDictionary<int, int> idx = SkirmishSetupToScenario.LaunchIndexBySlot(setup.Slots);

            Assert.Equal(2, built.PlayerSlots.Length);
            Assert.Equal(1, built.PlayerSlots[idx[2]].Team); // the human row's marker landed on its launch index
            Assert.Equal(2, built.PlayerSlots[idx[1]].Team); // the AI row's marker landed on its launch index
            Assert.Equal(0, idx[2]);                         // and the human's launch index is Player1 = blue
        }

        // ── Minimal builders (the SkirmishSetupTests shapes) ─────────────────────────

        private static IReadOnlyList<FactionEntry> Factions(params string[] ids) =>
            ids.Select(i => new FactionEntry
            {
                Id = i, DisplayName = i, ResPath = $"res://factions/{i}_faction.json",
            }).ToList();

        private static ScenarioData BaseMap(int slots)
        {
            var m = new ScenarioData { Id = "m1", DisplayName = "m1", MapBounds = 120f };
            var ps = new ScenarioPlayerSlot[slots];
            for (int i = 0; i < slots; i++)
                ps[i] = new ScenarioPlayerSlot { Slot = i, FactionJson = "res://factions/alpha_faction.json" };
            m.PlayerSlots = ps;
            return m;
        }
    }
}
