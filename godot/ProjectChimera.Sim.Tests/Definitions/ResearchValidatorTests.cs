#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// Story 4.8a — <see cref="ResearchValidator"/>'s import-time field/referential/cycle/cap lint, and its wiring
    /// into <see cref="FactionDefinition.LoadFromFile"/>. One test per I/O Matrix row in the spec:
    /// spec-4-8a-researchdefinition-content-model-validation.md.
    /// </summary>
    public class ResearchValidatorTests
    {
        private const string RequiredBuildingFields =
            "\"construction_time\": 10, \"supply_bonus\": 0, \"produces_category\": \"Worker\"";

        private static string Building(string id, string availableResearchJson = "[]") =>
            $$"""{ "id": "{{id}}", "display_name": "{{id}}", "category": "Structure", "hp": 100, {{RequiredBuildingFields}}, "available_research": {{availableResearchJson}} }""";

        private static string OneLevel(int timeTicks = 10, string costJson = "{}") =>
            $$"""{ "time_ticks": {{timeTicks}}, "cost": {{costJson}} }""";

        private static string Research(string id, string levelsJson = "", string prereqsJson = "[]", float cancelRefund = 0f) =>
            $$"""{ "id": "{{id}}", "display_name": "{{id}}", "cancel_refund_fraction": {{cancelRefund}}, "prerequisites": {{prereqsJson}}, "levels": [{{levelsJson}}] }""";

        private static string FactionJson(string buildingsJson = "", string researchJson = "") => $$"""
        {
          "id": "test_faction",
          "display_name": "Test Faction",
          "buildings": [{{buildingsJson}}],
          "research": [{{researchJson}}]
        }
        """;

        private static string WriteTempFaction(string json)
        {
            string path = Path.Combine(Path.GetTempPath(), $"chimera_research_validator_{Guid.NewGuid():N}.json");
            File.WriteAllText(path, json);
            return path;
        }

        // ── Happy path ────────────────────────────────────────────────────────────

        [Fact]
        public void HappyPath_TwoLevelResearch_LoadsCleanly_ResolvesViaGetResearchAndIndexOfResearch()
        {
            string research = Research("armor_up",
                levelsJson: OneLevel(10, "{\"ore\": 50}") + "," + OneLevel(20, "{\"ore\": 100, \"crystal\": 20}"));
            string json = FactionJson(researchJson: research);
            string path = WriteTempFaction(json);
            try
            {
                FactionDefinition def = FactionDefinition.LoadFromFile(path);
                Assert.NotNull(def.GetResearch("armor_up"));
                Assert.Equal(0, def.IndexOfResearch("armor_up"));
                Assert.Equal(2, def.GetResearch("armor_up")!.Levels.Count);
            }
            finally { File.Delete(path); }
        }

        // ── Duplicate research id ────────────────────────────────────────────────

        [Fact]
        public void DuplicateResearchId_Throws_NamingTheId()
        {
            string research = Research("armor_up", OneLevel()) + "," + Research("armor_up", OneLevel());
            string json = FactionJson(researchJson: research);
            string path = WriteTempFaction(json);
            try
            {
                var ex = Assert.Throws<InvalidOperationException>(() => FactionDefinition.LoadFromFile(path));
                Assert.Contains("duplicate research id", ex.Message);
                Assert.Contains("armor_up", ex.Message);
            }
            finally { File.Delete(path); }
        }

        // ── Empty Levels ──────────────────────────────────────────────────────────

        [Fact]
        public void EmptyLevels_Throws_LocatedError()
        {
            string research = Research("armor_up", levelsJson: "");
            string json = FactionJson(researchJson: research);
            string path = WriteTempFaction(json);
            try
            {
                var ex = Assert.Throws<InvalidOperationException>(() => FactionDefinition.LoadFromFile(path));
                Assert.Contains("armor_up", ex.Message);
                Assert.Contains("levels", ex.Message);
                Assert.Contains("at least one level", ex.Message);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void NullLevelsList_TreatedAsEmpty_NoThrowNoCrash_ButStillLocatedError()
        {
            var def = new FactionDefinition();
            def.Research.Add(new ResearchDefinition { Id = "armor_up", Levels = null! });

            var ex = Record.Exception(() => ResearchValidator.Validate(def));
            Assert.Null(ex);
            var errors = ResearchValidator.Validate(def);
            Assert.Contains(errors, e => e.Contains("armor_up") && e.Contains("at least one level"));
        }

        // ── Non-positive level time ──────────────────────────────────────────────

        [Fact]
        public void NonPositiveLevelTime_Throws_LocatedError()
        {
            string research = Research("armor_up", levelsJson: OneLevel(0));
            string json = FactionJson(researchJson: research);
            string path = WriteTempFaction(json);
            try
            {
                var ex = Assert.Throws<InvalidOperationException>(() => FactionDefinition.LoadFromFile(path));
                Assert.Contains("armor_up", ex.Message);
                Assert.Contains("time_ticks", ex.Message);
            }
            finally { File.Delete(path); }
        }

        // ── Unregistered level-cost resource id ──────────────────────────────────

        [Fact]
        public void UnregisteredLevelCostResourceId_Throws_LocatedError()
        {
            string research = Research("armor_up", levelsJson: OneLevel(10, "{\"wood\": 5}"));
            string json = FactionJson(researchJson: research);
            string path = WriteTempFaction(json);
            try
            {
                var ex = Assert.Throws<InvalidOperationException>(() => FactionDefinition.LoadFromFile(path));
                Assert.Contains("armor_up", ex.Message);
                Assert.Contains("wood", ex.Message);
                Assert.Contains("unknown resource id", ex.Message);
            }
            finally { File.Delete(path); }
        }

        // ── Out-of-range cancel refund fraction ──────────────────────────────────

        [Fact]
        public void OutOfRangeCancelRefundFraction_Throws_LocatedError()
        {
            string research = Research("armor_up", levelsJson: OneLevel(), cancelRefund: 1.5f);
            string json = FactionJson(researchJson: research);
            string path = WriteTempFaction(json);
            try
            {
                var ex = Assert.Throws<InvalidOperationException>(() => FactionDefinition.LoadFromFile(path));
                Assert.Contains("armor_up", ex.Message);
                Assert.Contains("cancel_refund_fraction", ex.Message);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void NegativeCancelRefundFraction_Throws_LocatedError()
        {
            string research = Research("armor_up", levelsJson: OneLevel(), cancelRefund: -0.1f);
            string json = FactionJson(researchJson: research);
            string path = WriteTempFaction(json);
            try
            {
                var ex = Assert.Throws<InvalidOperationException>(() => FactionDefinition.LoadFromFile(path));
                Assert.Contains("cancel_refund_fraction", ex.Message);
            }
            finally { File.Delete(path); }
        }

        // ── Unknown prerequisite id ───────────────────────────────────────────────

        [Fact]
        public void UnknownPrerequisiteId_Throws_LocatedError()
        {
            string research = Research("armor_up", levelsJson: OneLevel(), prereqsJson: "[\"ghost\"]");
            string json = FactionJson(researchJson: research);
            string path = WriteTempFaction(json);
            try
            {
                var ex = Assert.Throws<InvalidOperationException>(() => FactionDefinition.LoadFromFile(path));
                Assert.Contains("armor_up", ex.Message);
                Assert.Contains("ghost", ex.Message);
                Assert.Contains("unknown", ex.Message);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void PrerequisiteReferencingABuildingId_NoError()
        {
            string research = Research("armor_up", levelsJson: OneLevel(), prereqsJson: "[\"barracks\"]");
            string json = FactionJson(buildingsJson: Building("barracks"), researchJson: research);
            string path = WriteTempFaction(json);
            try
            {
                FactionDefinition def = FactionDefinition.LoadFromFile(path); // throws on regression
                Assert.NotNull(def.GetResearch("armor_up"));
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void NullPrerequisitesArray_TreatedAsEmpty_NoThrowNoCrash()
        {
            var def = new FactionDefinition();
            def.Research.Add(new ResearchDefinition
            {
                Id = "armor_up",
                Prerequisites = null!,
                Levels = new List<ResearchLevel> { new ResearchLevel { TimeTicks = 10 } },
            });

            var ex = Record.Exception(() => ResearchValidator.Validate(def));
            Assert.Null(ex);
            Assert.Empty(ResearchValidator.Validate(def));
        }

        // ── Unknown AvailableResearch id ──────────────────────────────────────────

        [Fact]
        public void UnknownAvailableResearchId_Throws_LocatedError()
        {
            string json = FactionJson(buildingsJson: Building("barracks", "[\"nonexistent\"]"));
            string path = WriteTempFaction(json);
            try
            {
                var ex = Assert.Throws<InvalidOperationException>(() => FactionDefinition.LoadFromFile(path));
                Assert.Contains("barracks", ex.Message);
                Assert.Contains("nonexistent", ex.Message);
                Assert.Contains("available_research", ex.Message);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void KnownAvailableResearchId_NoError()
        {
            string research = Research("armor_up", levelsJson: OneLevel());
            string json = FactionJson(buildingsJson: Building("barracks", "[\"armor_up\"]"), researchJson: research);
            string path = WriteTempFaction(json);
            try
            {
                FactionDefinition def = FactionDefinition.LoadFromFile(path); // throws on regression
                Assert.Equal(new[] { "armor_up" }, def.GetBuilding("barracks")!.AvailableResearch);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void NullAvailableResearchArray_TreatedAsEmpty_NoThrowNoCrash()
        {
            var def = new FactionDefinition();
            def.Buildings.Add(new BuildingDefinition
            {
                Id = "barracks", ConstructionTime = 10f, SupplyBonus = 0, ProducesCategory = "Worker",
                AvailableResearch = null!,
            });

            var ex = Record.Exception(() => ResearchValidator.Validate(def));
            Assert.Null(ex);
            Assert.Empty(ResearchValidator.Validate(def));
        }

        // ── Research→research cycle (first-fail) ─────────────────────────────────

        [Fact]
        public void TwoNodeResearchCycle_Throws_NamingTheChain()
        {
            string research =
                Research("r1", levelsJson: OneLevel(), prereqsJson: "[\"r2\"]") + "," +
                Research("r2", levelsJson: OneLevel(), prereqsJson: "[\"r1\"]");
            string json = FactionJson(researchJson: research);
            string path = WriteTempFaction(json);
            try
            {
                var ex = Assert.Throws<InvalidOperationException>(() => FactionDefinition.LoadFromFile(path));
                Assert.Contains("research cycle", ex.Message);
                Assert.Contains("r1 -> r2 -> r1", ex.Message);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void SelfResearchCycle_Throws_NamingTheId()
        {
            string research = Research("r1", levelsJson: OneLevel(), prereqsJson: "[\"r1\"]");
            string json = FactionJson(researchJson: research);
            string path = WriteTempFaction(json);
            try
            {
                var ex = Assert.Throws<InvalidOperationException>(() => FactionDefinition.LoadFromFile(path));
                Assert.Contains("research cycle", ex.Message);
                Assert.Contains("r1 -> r1", ex.Message);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void CycleWithAcyclicLeadIn_ReportsOnlyTheTrimmedSubChain()
        {
            // Second-review-pass fix: prior cycle tests all closed back to the DFS entry node (startIdx == 0), so the
            // path.IndexOf(prereq) chain-trimming branch did no trimming and was unverified. Here `lead` is an
            // acyclic prefix into a b<->c cycle: the reported chain must start at the repeated id (b), NOT include
            // the lead-in — proving the trim.
            string research =
                Research("lead", levelsJson: OneLevel(), prereqsJson: "[\"b\"]") + "," +
                Research("b", levelsJson: OneLevel(), prereqsJson: "[\"c\"]") + "," +
                Research("c", levelsJson: OneLevel(), prereqsJson: "[\"b\"]");
            string json = FactionJson(researchJson: research);
            string path = WriteTempFaction(json);
            try
            {
                var ex = Assert.Throws<InvalidOperationException>(() => FactionDefinition.LoadFromFile(path));
                Assert.Contains("research cycle: b -> c -> b", ex.Message);
                Assert.DoesNotContain("lead ->", ex.Message);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void ResearchPrerequisitingABuilding_NeverParticipatesInCycle()
        {
            // A research→building edge is always a graph leaf — proves the DFS restricts its walk to
            // Research→Research edges only (mirrors TechTreeValidator restricting to Buildings→Buildings).
            string research = Research("r1", levelsJson: OneLevel(), prereqsJson: "[\"barracks\"]");
            string json = FactionJson(buildingsJson: Building("barracks"), researchJson: research);
            string path = WriteTempFaction(json);
            try
            {
                FactionDefinition def = FactionDefinition.LoadFromFile(path); // throws on regression
                Assert.NotNull(def.GetResearch("r1"));
            }
            finally { File.Delete(path); }
        }

        // ── Over-cap research count ───────────────────────────────────────────────

        [Fact]
        public void OverCapResearchCount_Throws_LocatedError()
        {
            var def = new FactionDefinition();
            for (int i = 0; i < ResearchValidator.MaxResearchPerFaction + 1; i++)
            {
                def.Research.Add(new ResearchDefinition
                {
                    Id = $"r{i}",
                    Levels = new List<ResearchLevel> { new ResearchLevel { TimeTicks = 10 } },
                });
            }

            var errors = ResearchValidator.Validate(def);
            Assert.Contains(errors, e => e.Contains("exceeding") && e.Contains(ResearchValidator.MaxResearchPerFaction.ToString()));
        }

        [Fact]
        public void AtCapResearchCount_NoOverCapError()
        {
            var def = new FactionDefinition();
            for (int i = 0; i < ResearchValidator.MaxResearchPerFaction; i++)
            {
                def.Research.Add(new ResearchDefinition
                {
                    Id = $"r{i}",
                    Levels = new List<ResearchLevel> { new ResearchLevel { TimeTicks = 10 } },
                });
            }

            var errors = ResearchValidator.Validate(def);
            Assert.DoesNotContain(errors, e => e.Contains("exceeding"));
        }

        // ── Multiple simultaneous defects: list-all in one thrown message ────────

        [Fact]
        public void MultipleSimultaneousDefects_AllErrorsSurfaceInOneThrownMessage()
        {
            string research =
                Research("dup", levelsJson: OneLevel()) + "," +
                Research("dup", levelsJson: OneLevel()) + "," +
                Research("bad_level", levelsJson: "") + "," +
                Research("bad_prereq", levelsJson: OneLevel(), prereqsJson: "[\"ghost\"]");
            string json = FactionJson(researchJson: research);
            string path = WriteTempFaction(json);
            try
            {
                var ex = Assert.Throws<InvalidOperationException>(() => FactionDefinition.LoadFromFile(path));
                Assert.Contains("duplicate research id", ex.Message);
                Assert.Contains("dup", ex.Message);
                Assert.Contains("bad_level", ex.Message);
                Assert.Contains("at least one level", ex.Message);
                Assert.Contains("bad_prereq", ex.Message);
                Assert.Contains("ghost", ex.Message);
            }
            finally { File.Delete(path); }
        }

        // ── Review-pass fix: blank/missing research id ───────────────────────────

        [Fact]
        public void BlankResearchId_Throws_LocatedError()
        {
            string research = Research("", levelsJson: OneLevel());
            string json = FactionJson(researchJson: research);
            string path = WriteTempFaction(json);
            try
            {
                var ex = Assert.Throws<InvalidOperationException>(() => FactionDefinition.LoadFromFile(path));
                Assert.Contains("must be a non-empty id", ex.Message);
            }
            finally { File.Delete(path); }
        }

        // ── Review-pass fix: ResearchLevel.Cost value range ──────────────────────

        [Fact]
        public void NegativeLevelCostValue_Throws_LocatedError()
        {
            string research = Research("armor_up", levelsJson: OneLevel(10, "{\"ore\": -5}"));
            string json = FactionJson(researchJson: research);
            string path = WriteTempFaction(json);
            try
            {
                var ex = Assert.Throws<InvalidOperationException>(() => FactionDefinition.LoadFromFile(path));
                Assert.Contains("armor_up", ex.Message);
                Assert.Contains("-5", ex.Message);
                Assert.Contains("must be >= 0", ex.Message);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void OverRangeLevelCostValue_Throws_LocatedError()
        {
            string research = Research("armor_up", levelsJson: OneLevel(10, "{\"ore\": 40000}"));
            string json = FactionJson(researchJson: research);
            string path = WriteTempFaction(json);
            try
            {
                var ex = Assert.Throws<InvalidOperationException>(() => FactionDefinition.LoadFromFile(path));
                Assert.Contains("armor_up", ex.Message);
                Assert.Contains("40000", ex.Message);
                Assert.Contains("exceeds the maximum resource cost", ex.Message);
            }
            finally { File.Delete(path); }
        }

        // ── Review-pass fix: NaN/Infinity guard on ResearchModifierDelta ─────────

        [Fact]
        public void NonFiniteModifierDeltaFields_ProduceLocatedErrors()
        {
            // Second-review-pass fix: previously only max_health_delta + armor_delta were asserted, so the
            // attack_damage_delta/move_speed_delta CheckFiniteModifier calls were unverified — a dropped/duplicated
            // call would have passed. All four fields are now set non-finite and all four errors asserted.
            var def = new FactionDefinition();
            def.Research.Add(new ResearchDefinition
            {
                Id = "armor_up",
                Levels = new List<ResearchLevel>
                {
                    new ResearchLevel
                    {
                        TimeTicks = 10,
                        ModifierDelta = new ResearchModifierDelta
                        {
                            MaxHealthDelta = float.NaN,
                            AttackDamageDelta = float.NegativeInfinity,
                            MoveSpeedDelta = float.NaN,
                            ArmorDelta = float.PositiveInfinity,
                        },
                    },
                },
            });

            var errors = ResearchValidator.Validate(def);
            Assert.Contains(errors, e => e.Contains("max_health_delta") && e.Contains("finite"));
            Assert.Contains(errors, e => e.Contains("attack_damage_delta") && e.Contains("finite"));
            Assert.Contains(errors, e => e.Contains("move_speed_delta") && e.Contains("finite"));
            Assert.Contains(errors, e => e.Contains("armor_delta") && e.Contains("finite"));
        }

        [Fact]
        public void FiniteButOutOfFixedRangeModifierDelta_ProducesLocatedError()
        {
            // Second-review-pass fix: level cost values were range-checked against the 16.16 Fixed ceiling, but the
            // four modifier-delta floats — which quantize into the SAME Fixed fields at 4.8b order time — were only
            // finite-checked. 100000 is valid JSON, finite, but overflows Fixed; it must now be a located error,
            // closing the asymmetry with the cost range check.
            var def = new FactionDefinition();
            def.Research.Add(new ResearchDefinition
            {
                Id = "armor_up",
                Levels = new List<ResearchLevel>
                {
                    new ResearchLevel
                    {
                        TimeTicks = 10,
                        ModifierDelta = new ResearchModifierDelta { MaxHealthDelta = 100000f },
                    },
                },
            });

            var errors = ResearchValidator.Validate(def);
            Assert.Contains(errors, e => e.Contains("max_health_delta") && e.Contains("exceeds the representable"));
        }

        [Fact]
        public void InRangeNegativeModifierDelta_NoError()
        {
            // A legitimately negative (debuff-style) delta well within the Fixed range must NOT trip the symmetric
            // range check — proves the bound is a magnitude ceiling, not a reject-negative rule like cost.
            var def = new FactionDefinition();
            def.Research.Add(new ResearchDefinition
            {
                Id = "armor_up",
                Levels = new List<ResearchLevel>
                {
                    new ResearchLevel
                    {
                        TimeTicks = 10,
                        ModifierDelta = new ResearchModifierDelta { ArmorDelta = -50f },
                    },
                },
            });

            Assert.Empty(ResearchValidator.Validate(def));
        }

        // ── Review-pass fix: NaN guard on CancelRefundFraction ───────────────────

        [Fact]
        public void NaNCancelRefundFraction_ProducesLocatedError()
        {
            var def = new FactionDefinition();
            def.Research.Add(new ResearchDefinition
            {
                Id = "armor_up",
                CancelRefundFraction = float.NaN,
                Levels = new List<ResearchLevel> { new ResearchLevel { TimeTicks = 10 } },
            });

            var errors = ResearchValidator.Validate(def);
            Assert.Contains(errors, e => e.Contains("cancel_refund_fraction") && e.Contains("NaN"));
        }

        // ── Review-pass fix: character-set sanitization for research ids ─────────

        [Fact]
        public void ResearchIdWithInvalidCharacters_Throws_LocatedError()
        {
            string research = Research("Armor Up!", levelsJson: OneLevel());
            string json = FactionJson(researchJson: research);
            string path = WriteTempFaction(json);
            try
            {
                var ex = Assert.Throws<InvalidOperationException>(() => FactionDefinition.LoadFromFile(path));
                Assert.Contains("Armor Up!", ex.Message);
                Assert.Contains("[a-z0-9_]", ex.Message);
            }
            finally { File.Delete(path); }
        }

        // ── Review-pass fix: null-safety throughout ResearchValidator/FactionDefinition ──

        [Fact]
        public void NullResearchList_TreatedAsEmpty_NoThrowNoCrash()
        {
            var def = new FactionDefinition { Research = null! };

            var ex = Record.Exception(() => ResearchValidator.Validate(def));
            Assert.Null(ex);
            Assert.Empty(ResearchValidator.Validate(def));
        }

        [Fact]
        public void NullElementInResearchList_TreatedAsSkipped_NoThrowNoCrash()
        {
            var def = new FactionDefinition();
            def.Research.Add(null!);

            var ex = Record.Exception(() => ResearchValidator.Validate(def));
            Assert.Null(ex);
            Assert.Empty(ResearchValidator.Validate(def));
        }

        [Fact]
        public void NullElementInLevelsList_TreatedAsSkipped_NoThrowNoCrash()
        {
            var def = new FactionDefinition();
            def.Research.Add(new ResearchDefinition
            {
                Id = "armor_up",
                Levels = new List<ResearchLevel> { null!, new ResearchLevel { TimeTicks = 10 } },
            });

            var ex = Record.Exception(() => ResearchValidator.Validate(def));
            Assert.Null(ex);
            Assert.Empty(ResearchValidator.Validate(def));
        }

        [Fact]
        public void NullElementInBuildingsList_TreatedAsSkipped_NoThrowNoCrash()
        {
            var def = new FactionDefinition();
            def.Buildings.Add(null!);

            var ex = Record.Exception(() => ResearchValidator.Validate(def));
            Assert.Null(ex);
            Assert.Empty(ResearchValidator.Validate(def));
        }

        [Fact]
        public void GetResearchAndIndexOfResearch_SkipNullElement_NoThrowNoCrash()
        {
            var def = new FactionDefinition();
            def.Research.Add(null!);
            def.Research.Add(new ResearchDefinition { Id = "armor_up" });

            Assert.NotNull(def.GetResearch("armor_up"));
            Assert.Equal(1, def.IndexOfResearch("armor_up"));
            Assert.Null(def.GetResearch("ghost"));
            Assert.Equal(-1, def.IndexOfResearch("ghost"));
        }

        [Fact]
        public void GetResearchAndIndexOfResearch_NullResearchList_NoThrowNoCrash()
        {
            // Second-review-pass fix: a null Research list (malformed JSON "research": null) is tolerated by the
            // validator, so such a file LOADS without error — the accessors must then survive it too. Previously
            // both getters did a bare foreach/Count over the null list and NRE'd, contradicting their own doc claim.
            var def = new FactionDefinition { Research = null! };

            Assert.Null(def.GetResearch("armor_up"));
            Assert.Equal(-1, def.IndexOfResearch("armor_up"));
        }

        // ── Existing shipped faction content unaffected ──────────────────────────

        [Fact]
        public void AlphaFaction_LoadsCleanly_NoNewErrorFromResearchValidator()
        {
            string path = ResolveDataPath("alpha_faction.json");
            FactionDefinition def = FactionDefinition.LoadFromFile(path); // throws on any regression
            Assert.NotNull(def);
            Assert.Empty(def.Research); // no research authored yet — validator is a no-op
        }

        [Fact]
        public void BetaFaction_LoadsCleanly_NoNewErrorFromResearchValidator()
        {
            string path = ResolveDataPath("beta_faction.json");
            FactionDefinition def = FactionDefinition.LoadFromFile(path); // throws on any regression
            Assert.NotNull(def);
            Assert.Empty(def.Research);
        }

        /// <summary>Resolve a shipped faction JSON by walking up from the test-assembly directory to
        /// <c>resources/data/factions/</c> (mirrors <c>TechTreeValidatorTests.ResolveDataPath</c>).</summary>
        private static string ResolveDataPath(string fileName)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, "resources", "data", "factions");
                if (Directory.Exists(candidate)) return Path.Combine(candidate, fileName);
                dir = dir.Parent;
            }
            throw new DirectoryNotFoundException(
                $"Could not locate resources/data/factions above {AppContext.BaseDirectory}");
        }
    }
}
