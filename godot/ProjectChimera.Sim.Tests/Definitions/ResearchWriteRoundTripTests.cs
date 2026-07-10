#nullable enable
using System.Collections.Generic;
using System.IO;
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// Story 4.11 — <see cref="FactionWriter.SyncFactionResearch"/>'s persistence teeth, mirroring
    /// <see cref="FactionWriteRoundTripTests"/>'s shape for <c>SyncFactionBuildings</c>/<c>SyncFactionUnits</c>: a
    /// pure, Godot-free string transform proven directly against an in-code faction JSON string. Confirms
    /// <c>root["research"]</c> is the ONLY thing that ever changes (buildings/units/faction-level keys survive
    /// byte-identical), new/duplicated/deleted entries round-trip, and the result still parses on
    /// <see cref="FactionDefinition.LoadFromFile"/> (the self-check every CreationSuite panel's Save path relies on).
    /// </summary>
    public class ResearchWriteRoundTripTests
    {
        private const string Faction = """
        {
          "id": "alpha",
          "display_name": "Rebel Alchemists",
          "buildings": [
            { "id": "barracks", "display_name": "Barracks", "category": "Structure", "hp": 500,
              "construction_time": 10, "supply_bonus": 0, "produces_category": "Melee" }
          ],
          "research": [
            { "id": "armor_up", "display_name": "Armor Up", "cancel_refund_fraction": 0.5,
              "prerequisites": ["barracks"],
              "levels": [
                { "cost": { "ore": 50 }, "time_ticks": 300, "modifier_delta": { "armor_delta": 1.0 } }
              ]
            }
          ]
        }
        """;

        private static ResearchDefinition Armor() => new()
        {
            Id = "armor_up",
            DisplayName = "Armor Up",
            CancelRefundFraction = 0.5f,
            Prerequisites = new[] { "barracks" },
            Levels = new List<ResearchLevel>
            {
                new ResearchLevel
                {
                    Cost = new Dictionary<string, int> { ["ore"] = 50 },
                    TimeTicks = 300,
                    ModifierDelta = new ResearchModifierDelta { ArmorDelta = 1.0f },
                },
            },
        };

        [Fact]
        public void Unchanged_List_RoundTrips_BuildingsAndFactionLevelKeysUntouched()
        {
            string patched = FactionWriter.SyncFactionResearch(Faction, new List<ResearchDefinition> { Armor() });

            Assert.Contains("\"barracks\"", patched);
            Assert.Contains("\"Rebel Alchemists\"", patched);

            FactionDefinition reloaded = ParseInline(patched);
            Assert.Single(reloaded.Research);
            ResearchDefinition r = reloaded.Research[0];
            Assert.Equal("armor_up", r.Id);
            Assert.Equal("Armor Up", r.DisplayName);
            Assert.Equal(0.5f, r.CancelRefundFraction);
            Assert.Equal(new[] { "barracks" }, r.Prerequisites);
            Assert.Single(r.Levels);
            Assert.Equal(50, r.Levels[0].Cost!["ore"]);
            Assert.Equal(300, r.Levels[0].TimeTicks);
            Assert.Equal(1.0f, r.Levels[0].ModifierDelta!.ArmorDelta);
        }

        [Fact]
        public void EditedLevel_RewritesTheWholeLevelsArray()
        {
            ResearchDefinition edited = Armor();
            edited.Levels[0].Cost!["ore"] = 999;
            edited.Levels.Add(new ResearchLevel { TimeTicks = 600, Cost = new Dictionary<string, int> { ["crystal"] = 20 } });

            string patched = FactionWriter.SyncFactionResearch(Faction, new List<ResearchDefinition> { edited });
            FactionDefinition reloaded = ParseInline(patched);

            ResearchDefinition r = reloaded.Research[0];
            Assert.Equal(2, r.Levels.Count);
            Assert.Equal(999, r.Levels[0].Cost!["ore"]);
            Assert.Equal(20, r.Levels[1].Cost!["crystal"]);
            Assert.Equal(600, r.Levels[1].TimeTicks);
        }

        [Fact]
        public void NewResearchEntry_Appends()
        {
            var list = new List<ResearchDefinition>
            {
                Armor(),
                new ResearchDefinition
                {
                    Id = "speed_up",
                    DisplayName = "Speed Up",
                    Levels = new List<ResearchLevel> { new ResearchLevel { TimeTicks = 150 } },
                },
            };

            string patched = FactionWriter.SyncFactionResearch(Faction, list);
            FactionDefinition reloaded = ParseInline(patched);

            Assert.Equal(2, reloaded.Research.Count);
            Assert.NotNull(reloaded.GetResearch("speed_up"));
        }

        [Fact]
        public void RemovedResearchEntry_Drops()
        {
            string patched = FactionWriter.SyncFactionResearch(Faction, new List<ResearchDefinition>());
            FactionDefinition reloaded = ParseInline(patched);
            Assert.Empty(reloaded.Research);
        }

        [Fact]
        public void ZeroModifierDelta_OmitsTheKey_NotWrittenAsAllZeroObject()
        {
            ResearchDefinition r = Armor();
            r.Levels[0].ModifierDelta = new ResearchModifierDelta(); // every field 0 — no stat effect

            string patched = FactionWriter.SyncFactionResearch(Faction, new List<ResearchDefinition> { r });

            Assert.DoesNotContain("modifier_delta", patched);
        }

        [Fact]
        public void NullResearchListElement_SkippedNoThrow()
        {
            // Review-pass fix: a malformed hand-edited "research": [null, {...}] deserializes to a null list
            // element at runtime despite the non-nullable-typed IReadOnlyList<ResearchDefinition> parameter — this
            // must never NRE the write path, mirroring ResearchValidator.Validate's identical null-skip.
            var list = new List<ResearchDefinition> { null!, Armor() };

            string patched = FactionWriter.SyncFactionResearch(Faction, list);
            FactionDefinition reloaded = ParseInline(patched);

            Assert.Single(reloaded.Research);
            Assert.Equal("armor_up", reloaded.Research[0].Id);
        }

        [Fact]
        public void NullLevelsListElement_SkippedNoThrow()
        {
            ResearchDefinition edited = Armor();
            edited.Levels.Add(null!); // malformed hand-edited "levels": [{...}, null]

            string patched = FactionWriter.SyncFactionResearch(Faction, new List<ResearchDefinition> { edited });
            FactionDefinition reloaded = ParseInline(patched);

            Assert.Single(reloaded.Research[0].Levels);
            Assert.Equal(300, reloaded.Research[0].Levels[0].TimeTicks);
        }

        /// <summary>Parse a patched faction JSON string via a temp file through the SAME lenient loader every
        /// CreationSuite panel's Save self-check uses (<see cref="FactionDefinition.LoadFromFile"/> takes an
        /// absolute path, not a string, and the Tier-1 harness can't resolve <c>res://</c>).</summary>
        private static FactionDefinition ParseInline(string json)
        {
            string path = Path.Combine(Path.GetTempPath(), $"chimera_research_writer_{System.Guid.NewGuid():N}.json");
            File.WriteAllText(path, json);
            try { return FactionDefinition.LoadFromFile(path); }
            finally { File.Delete(path); }
        }
    }
}
