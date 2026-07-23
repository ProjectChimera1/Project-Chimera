#nullable enable
using System.IO;
using System.Linq;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// Story 6.7 — start positions generalize from a hardcoded 2 to 2–4 slots, mapped to factions ONLY through
    /// <see cref="FactionRegistry.ToFaction"/>. Story 9.2 raised the engine ceiling to Player8: slots in [0,8) are
    /// now valid; slot ≥ 8 (== PLAYER_COUNT) and duplicate slots stay HARD fail-closed; the below-suggested-players
    /// case is a SOFT non-fatal advisory that leaves <see cref="ScenarioValidator.Validate"/> passing.
    /// </summary>
    public class StartPositionSlotTests
    {
        private static ScenarioData MapWithSlots(int count, int suggested = 0)
        {
            var slots = new ScenarioPlayerSlot[count];
            for (int i = 0; i < count; i++)
                slots[i] = new ScenarioPlayerSlot
                {
                    Slot = i, FactionJson = "res://a.json", StartOre = 200f, StartCrystal = 0f,
                    BaseX = -50f + i * 20f, BaseZ = 0f,
                };
            return new ScenarioData
            {
                Id = "m", DisplayName = "Map", MapBounds = 120f, SuggestedPlayers = suggested,
                WinCondition = WinCondition.DestroyAllBuildings,
                PlayerSlots   = slots,
                ResourceNodes = System.Array.Empty<ScenarioResourceNode>(),
                Buildings     = System.Array.Empty<ScenarioBuilding>(),
                Units         = System.Array.Empty<ScenarioUnit>(),
                Triggers      = System.Array.Empty<TriggerDefinition>(),
            };
        }

        [Theory]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        public void TwoToFourSlots_Validate_AndMapToDistinctFactions(int count)
        {
            var m = MapWithSlots(count);
            Assert.True(new ScenarioValidator().Validate(m).Ok);

            var factions = m.PlayerSlots.Select(s => FactionRegistry.ToFaction(s.Slot)).ToArray();
            Assert.Equal(count, factions.Distinct().Count());
            Assert.Equal(Faction.Player1, factions[0]);
            Assert.Equal((Faction)(count), factions[count - 1]); // Player{count}
        }

        [Fact]
        public void PlayerSlots_RoundTripThroughSaveLoad()
        {
            string p = Path.Combine(Path.GetTempPath(), "chimera_slots_" + System.Guid.NewGuid().ToString("N") + ".json");
            try
            {
                var m = MapWithSlots(4, suggested: 4);
                ScenarioSerializer.SaveToFile(m, p);
                var loaded = ScenarioSerializer.LoadFromFile(p);
                Assert.NotNull(loaded);
                Assert.Equal(4, loaded!.PlayerSlots.Length);
                for (int i = 0; i < 4; i++)
                    Assert.Equal(i, loaded.PlayerSlots[i].Slot);
            }
            finally { if (File.Exists(p)) File.Delete(p); }
        }

        [Fact]
        public void SlotAtEngineCeiling_FailsClosed()
        {
            // Story 9.2: the engine ceiling is now Player8. Slot 8 == PLAYER_COUNT overflows the [0,8) valid range
            // and the Faction enum (which tops at Player8 = slot 7).
            var m = MapWithSlots(2);
            m.PlayerSlots[1].Slot = 8;
            var r = new ScenarioValidator().Validate(m);
            Assert.False(r.Ok);
        }

        [Theory]
        [InlineData(4)] // maps to Player5 — newly valid after Story 9.2
        [InlineData(7)] // maps to Player8 — the top valid slot
        public void SlotInExpandedRange_IsAccepted(int slot)
        {
            // Story 9.2: slots 4-7 now map to real, backed Faction members (Player5..Player8) and pass validation.
            var m = MapWithSlots(2);
            m.PlayerSlots[1].Slot = slot;
            Assert.True(new ScenarioValidator().Validate(m).Ok);
        }

        [Fact]
        public void DuplicateSlot_FailsClosed()
        {
            var m = MapWithSlots(2);
            m.PlayerSlots[1].Slot = 0; // duplicate of slot 0
            Assert.False(new ScenarioValidator().Validate(m).Ok);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(9)] // Story 9.2: [2,8] valid; 9 == PLAYER_COUNT+1 is the first rejected value
        public void SuggestedPlayersOutOfRange_FailsClosed(int suggested)
        {
            var m = MapWithSlots(2, suggested);
            var r = new ScenarioValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("suggested_players", r.Error);
        }

        [Fact]
        public void SuggestedPlayersAtCeiling_Passes()
            // Story 9.2: suggested_players = 8 (PLAYER_COUNT) is now a valid authoring intent for an 8-player map.
            => Assert.True(new ScenarioValidator().Validate(MapWithSlots(2, suggested: 8)).Ok);

        [Fact]
        public void SuggestedPlayersUnset_Passes()
            => Assert.True(new ScenarioValidator().Validate(MapWithSlots(2, suggested: 0)).Ok);

        // ── Below-suggested advisory (SOFT, non-fatal) ──────────────────────────

        [Fact]
        public void BelowSuggested_IsAdvisory_NotFatal()
        {
            var m = MapWithSlots(2, suggested: 4); // 2 placed, 4 suggested
            // Validate still PASSES (not a fail-closed error).
            Assert.True(new ScenarioValidator().Validate(m).Ok);
            // But an advisory is collected.
            var advisories = new ScenarioValidator().CollectAdvisories(m);
            Assert.NotEmpty(advisories);
            Assert.Contains("start position", advisories[0]);
        }

        [Fact]
        public void MetSuggested_NoAdvisory()
        {
            var m = MapWithSlots(4, suggested: 4);
            Assert.Empty(new ScenarioValidator().CollectAdvisories(m));
        }

        [Fact]
        public void SuggestedUnset_NoAdvisory()
        {
            var m = MapWithSlots(2, suggested: 0);
            Assert.Empty(new ScenarioValidator().CollectAdvisories(m));
        }

        // ── Out-of-bounds start position advisory (SOFT, non-fatal — patch 4) ────

        [Fact]
        public void StartPositionOutsideBounds_IsAdvisory_NotFatal()
        {
            var m = MapWithSlots(2);
            m.PlayerSlots[1].BaseX = m.MapBounds + 50f; // shove a start position outside the bounds

            var advisories = new ScenarioValidator().CollectAdvisories(m);
            Assert.Contains(advisories, a => a.Contains("outside the current map bounds"));
        }

        [Fact]
        public void StartPositionsInsideBounds_NoOutOfBoundsAdvisory()
        {
            var m = MapWithSlots(2); // all bases well within ±120
            Assert.DoesNotContain(new ScenarioValidator().CollectAdvisories(m),
                                  a => a.Contains("outside the current map bounds"));
        }

        [Fact]
        public void StartPositionExactlyAtBounds_IsNotOutOfBounds()
        {
            // Review pass 2 — the advisory predicate matches the hard validator's strict `> bounds`: an on-edge base at
            // exactly ±MapBounds is IN-bounds for both, so it must NOT trip the advisory (else the early warning would
            // contradict a Validate that still passes at the boundary).
            var m = MapWithSlots(2);
            m.PlayerSlots[1].BaseX = m.MapBounds;  // exactly on the edge
            m.PlayerSlots[1].BaseZ = -m.MapBounds; // exactly on the opposite edge
            Assert.DoesNotContain(new ScenarioValidator().CollectAdvisories(m),
                                  a => a.Contains("outside the current map bounds"));
        }

        // ── Content out-of-bounds advisory (SOFT, non-fatal — review pass 2) ─────
        // A map-size shrink can strand ANY authored content past the new bounds, not just start positions. The advisory
        // now covers buildings/units/resource nodes/props/water so the shrink surfaces a visible cause instead of a
        // silent, unloadable export.

        [Fact]
        public void ContentOutsideBounds_IsAdvisory_NotFatal()
        {
            var m = MapWithSlots(2);
            m.ResourceNodes = new[]
            {
                new ScenarioResourceNode { X = m.MapBounds + 30f, Z = 0f }, // stranded by a shrink
            };
            var advisories = new ScenarioValidator().CollectAdvisories(m);
            Assert.Contains(advisories, a => a.Contains("placed object(s) are outside the current map bounds"));
        }

        [Fact]
        public void ContentInsideBounds_NoContentAdvisory()
        {
            var m = MapWithSlots(2);
            m.Buildings = new[] { new ScenarioBuilding { X = 10f, Z = -10f, Slot = 0 } };
            m.Units     = new[] { new ScenarioUnit { X = -20f, Z = 20f, Slot = 0 } };
            Assert.DoesNotContain(new ScenarioValidator().CollectAdvisories(m),
                                  a => a.Contains("placed object(s) are outside the current map bounds"));
        }

        [Fact]
        public void ContentAdvisory_CountsEveryStrandedObject()
        {
            var m = MapWithSlots(2);
            m.Buildings     = new[] { new ScenarioBuilding { X = m.MapBounds + 5f, Z = 0f, Slot = 0 } };
            m.Units         = new[] { new ScenarioUnit { X = 0f, Z = m.MapBounds + 5f, Slot = 0 } };
            m.ResourceNodes = new[] { new ScenarioResourceNode { X = -(m.MapBounds + 5f), Z = 0f } };

            var advisory = System.Linq.Enumerable.Single(
                new ScenarioValidator().CollectAdvisories(m),
                a => a.Contains("placed object(s) are outside the current map bounds"));
            Assert.Contains("3 placed object(s)", advisory);
        }
    }
}
