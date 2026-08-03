#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// Story 4.2 (AC1/AC2) — <see cref="TechTreeValidator"/>'s import-time referential + cycle lint, and its
    /// wiring into <see cref="FactionDefinition.LoadFromFile"/>. One test per I/O Matrix row in the spec:
    /// unknown building prereq, unknown unit prereq, 2-node cycle, self cycle, custom-building prereq target,
    /// null-prerequisites-no-throw, and the existing alpha/beta faction JSON loading clean.
    /// </summary>
    public class TechTreeValidatorTests
    {
        private const string RequiredFields =
            "\"construction_time\": 10, \"supply_bonus\": 0, \"produces_category\": \"Worker\"";

        private static string Building(string id, string prereqsJson = "[]") =>
            $$"""{ "id": "{{id}}", "display_name": "{{id}}", "category": "Structure", "hp": 100, {{RequiredFields}}, "prerequisites": {{prereqsJson}} }""";

        private static string BuildingNoPrereqField(string id) =>
            $$"""{ "id": "{{id}}", "display_name": "{{id}}", "category": "Structure", "hp": 100, {{RequiredFields}} }""";

        private static string BuildingNullPrereq(string id) =>
            $$"""{ "id": "{{id}}", "display_name": "{{id}}", "category": "Structure", "hp": 100, {{RequiredFields}}, "prerequisites": null }""";

        private static string Unit(string id, string category, string prereqsJson) =>
            $$"""{ "id": "{{id}}", "display_name": "{{id}}", "category": "{{category}}", "hp": 50, "prerequisites": {{prereqsJson}} }""";

        private static string FactionJson(string buildingsJson, string unitsJson = "") => $$"""
        {
          "id": "test_faction",
          "display_name": "Test Faction",
          "units": [{{unitsJson}}],
          "buildings": [{{buildingsJson}}]
        }
        """;

        private static string WriteTempFaction(string json)
        {
            string path = Path.Combine(Path.GetTempPath(), $"chimera_techtree_validator_{Guid.NewGuid():N}.json");
            File.WriteAllText(path, json);
            return path;
        }

        // ── Unknown building prereq ──────────────────────────────────────────────

        [Fact]
        public void UnknownBuildingPrereq_Throws_NamingReferencingIdAndUnknownId()
        {
            string json = FactionJson(Building("archery_range", "[\"barrackz\"]"));
            string path = WriteTempFaction(json);
            try
            {
                var ex = Assert.Throws<InvalidOperationException>(() => FactionDefinition.LoadFromFile(path));
                Assert.Contains("archery_range", ex.Message);
                Assert.Contains("barrackz", ex.Message);
                Assert.Contains("unknown building id", ex.Message);
            }
            finally { File.Delete(path); }
        }

        // ── Unknown unit prereq ──────────────────────────────────────────────────

        [Fact]
        public void UnknownUnitPrereq_Throws_NamingReferencingIdAndUnknownId()
        {
            string json = FactionJson(
                BuildingNoPrereqField("archery_range"),
                Unit("archer", "Ranged", "[\"barrackz\"]"));
            string path = WriteTempFaction(json);
            try
            {
                var ex = Assert.Throws<InvalidOperationException>(() => FactionDefinition.LoadFromFile(path));
                Assert.Contains("archer", ex.Message);
                Assert.Contains("barrackz", ex.Message);
                Assert.Contains("unknown building id", ex.Message);
            }
            finally { File.Delete(path); }
        }

        // ── 2-node cycle ──────────────────────────────────────────────────────────

        [Fact]
        public void TwoNodeCycle_Throws_NamingBothIdsInCycleOrder()
        {
            string json = FactionJson(Building("a", "[\"b\"]") + "," + Building("b", "[\"a\"]"));
            string path = WriteTempFaction(json);
            try
            {
                var ex = Assert.Throws<InvalidOperationException>(() => FactionDefinition.LoadFromFile(path));
                Assert.Contains("tech tree cycle", ex.Message);
                Assert.Contains("a", ex.Message);
                Assert.Contains("b", ex.Message);
            }
            finally { File.Delete(path); }
        }

        // ── Self cycle ────────────────────────────────────────────────────────────

        [Fact]
        public void SelfCycle_Throws_NamingTheId()
        {
            string json = FactionJson(Building("a", "[\"a\"]"));
            string path = WriteTempFaction(json);
            try
            {
                var ex = Assert.Throws<InvalidOperationException>(() => FactionDefinition.LoadFromFile(path));
                Assert.Contains("tech tree cycle", ex.Message);
                Assert.Contains("a -> a", ex.Message);
            }
            finally { File.Delete(path); }
        }

        // ── Duplicate building id (review-pass addition) ─────────────────────────

        [Fact]
        public void DuplicateBuildingId_Throws_NamingTheId()
        {
            // Review-pass fix (Story 4.2): without this check, the second building sharing an id would silently
            // collapse into the first for both the referential-lint id set and the cycle-detection graph, so a
            // cycle reachable only through the duplicate's own, distinct prerequisites could go undetected.
            string json = FactionJson(Building("archery_range") + "," + Building("archery_range"));
            string path = WriteTempFaction(json);
            try
            {
                var ex = Assert.Throws<InvalidOperationException>(() => FactionDefinition.LoadFromFile(path));
                Assert.Contains("duplicate building id", ex.Message);
                Assert.Contains("archery_range", ex.Message);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void DuplicateBuildingId_UnsoundCycleWouldOtherwiseHideBehindTheDuplicate_StillCaughtAsDuplicateError()
        {
            // The scenario the missing check would have hidden: "a" requires "b" (fine, first occurrence), and a
            // SECOND "b" requires "a" (a genuine cycle reachable only via the duplicate's own edges). Asserting on
            // the duplicate-id error alone is sufficient — the load fails either way, so the cycle can never
            // silently pass regardless of which "b" the DFS happens to walk.
            var def = new FactionDefinition();
            def.Buildings.Add(new BuildingDefinition { Id = "a", Prerequisites = new[] { "b" } });
            def.Buildings.Add(new BuildingDefinition { Id = "b" });
            def.Buildings.Add(new BuildingDefinition { Id = "b", Prerequisites = new[] { "a" } });

            var errors = TechTreeValidator.Validate(def);
            Assert.Contains(errors, e => e.Contains("duplicate building id") && e.Contains("b"));
        }

        // ── 3+-node (indirect) cycle ──────────────────────────────────────────────

        [Fact]
        public void ThreeNodeIndirectCycle_Throws_NamingTheFullChain()
        {
            // The 2-node/self-cycle tests above don't exercise the DFS's path-slicing chain-extraction logic
            // (Visit's `path.IndexOf` + slice) beyond a trivial 1-2 element chain — this proves the general case.
            string json = FactionJson(
                Building("a", "[\"b\"]") + "," + Building("b", "[\"c\"]") + "," + Building("c", "[\"a\"]"));
            string path = WriteTempFaction(json);
            try
            {
                var ex = Assert.Throws<InvalidOperationException>(() => FactionDefinition.LoadFromFile(path));
                Assert.Contains("tech tree cycle", ex.Message);
                Assert.Contains("a -> b -> c -> a", ex.Message);
            }
            finally { File.Delete(path); }
        }

        // ── List-all: multiple simultaneous violations surface together ──────────

        [Fact]
        public void TwoSimultaneousDanglingReferences_BothSurfaceInOneThrownMessage()
        {
            // Proves the "list-all, never first-fail" referential-lint claim (the class doc / FactionDefinition's
            // doc comment) rather than just asserting it from the implementation's `List<string>`/`AddRange` shape.
            string json = FactionJson(
                Building("archery_range", "[\"barrackz\"]") + "," + Building("siege_workshop", "[\"nope\"]"));
            string path = WriteTempFaction(json);
            try
            {
                var ex = Assert.Throws<InvalidOperationException>(() => FactionDefinition.LoadFromFile(path));
                Assert.Contains("archery_range", ex.Message);
                Assert.Contains("barrackz", ex.Message);
                Assert.Contains("siege_workshop", ex.Message);
                Assert.Contains("nope", ex.Message);
            }
            finally { File.Delete(path); }
        }

        // ── Custom building as prereq target ─────────────────────────────────────

        [Fact]
        public void CustomBuildingPrereqTarget_NoValidationError()
        {
            // The validator only checks that the id exists among Buildings[].Id — it does not care whether the
            // referencing type is enum-backed or BuildingType.Custom (Story 4.1's Custom sentinel has no
            // TechTreeChecker mapping, but is a perfectly valid AUTHORED id here).
            var def = new FactionDefinition();
            def.Buildings.Add(new BuildingDefinition { Id = "watchtower" });
            def.Buildings.Add(new BuildingDefinition { Id = "keep", Prerequisites = new[] { "watchtower" } });

            var errors = TechTreeValidator.Validate(def);
            Assert.Empty(errors);
        }

        // ── Null prerequisites array ─────────────────────────────────────────────

        [Fact]
        public void NullPrerequisitesArray_TreatedAsEmpty_NoThrowNoCrash()
        {
            string json = FactionJson(BuildingNullPrereq("command_center"));
            string path = WriteTempFaction(json);
            try
            {
                FactionDefinition def = FactionDefinition.LoadFromFile(path);
                Assert.Single(def.Buildings);
                Assert.Null(def.Buildings[0].Prerequisites);
            }
            finally { File.Delete(path); }
        }

        // ── DW-100: null Units/Buildings LIST and null ELEMENT (malformed-but-parseable JSON) ────────────────

        [Fact]
        public void Validate_NullUnitsAndBuildingsLists_NoThrow()
        {
            // "units": null / "buildings": null leaves the lists null after deserialize — Validate must treat each
            // as empty, never NRE.
            var def = new FactionDefinition { Units = null!, Buildings = null! };
            var ex = Record.Exception(() => TechTreeValidator.Validate(def));
            Assert.Null(ex);
            Assert.Empty(TechTreeValidator.Validate(def));
        }

        [Fact]
        public void Validate_NullUnitElement_SkipsNull_StillLintsSurvivor()
        {
            // "units": [null, {archer with a dangling prereq}] — the null is skipped, the real archer is still linted.
            var def = new FactionDefinition();
            def.Buildings.Add(new BuildingDefinition { Id = "barracks" });
            def.Units = new List<UnitDefinition>
            {
                null!,
                new UnitDefinition { Id = "archer", Prerequisites = new[] { "ghost" } },
            };

            var ex = Record.Exception(() => TechTreeValidator.Validate(def));
            Assert.Null(ex);
            Assert.Contains(TechTreeValidator.Validate(def), e => e.Contains("archer") && e.Contains("ghost"));
        }

        [Fact]
        public void Validate_NullBuildingElement_SkipsNull_StillLintsSurvivor()
        {
            // "buildings": [null, {barracks with a dangling prereq}] — null skipped, barracks still linted.
            var def = new FactionDefinition();
            def.Buildings = new List<BuildingDefinition>
            {
                null!,
                new BuildingDefinition { Id = "barracks", Prerequisites = new[] { "ghost" } },
            };

            var ex = Record.Exception(() => TechTreeValidator.Validate(def));
            Assert.Null(ex);
            Assert.Contains(TechTreeValidator.Validate(def), e => e.Contains("barracks") && e.Contains("ghost"));
        }

        [Fact]
        public void Validate_NullPrerequisitesOnBuildingAndUnit_NoThrowNoErrors()
        {
            var def = new FactionDefinition();
            def.Buildings.Add(new BuildingDefinition { Id = "command_center", Prerequisites = null! });
            var unit = new UnitDefinition { Id = "worker", Prerequisites = null! };
            def.Units.Add(unit);

            var ex = Record.Exception(() => TechTreeValidator.Validate(def));
            Assert.Null(ex);
            Assert.Empty(TechTreeValidator.Validate(def));
        }

        // ── DW-59: deep-chain stack safety (explicit-stack iterative cycle DFS) ──

        [Fact]
        public void DeepPrerequisiteChain_100kDeep_AcyclicValidatesClean_NoStackOverflow()
        {
            // DW-59 regression: Visit was a plain recursive DFS — one C# call-stack frame per depth level —
            // so a creator-authored (or malicious-scale) chain this deep overflowed the call stack during
            // faction load, and StackOverflowException kills the test host process uncatchably (this test
            // crashes the whole run against the recursive version; 100k frames ≈ 15 MB of stack, far past
            // any default). The explicit-stack rewrite bounds depth by heap memory instead.
            var def = new FactionDefinition();
            const int depth = 100_000;
            for (int i = 0; i < depth; i++)
            {
                def.Buildings.Add(new BuildingDefinition
                {
                    Id = $"b{i}",
                    Prerequisites = i + 1 < depth ? new[] { $"b{i + 1}" } : Array.Empty<string>(),
                });
            }

            var errors = TechTreeValidator.Validate(def);

            Assert.Empty(errors);
        }

        [Fact]
        public void DeepPrerequisiteChain_100kDeep_CycleClosingAtTheDeepEnd_StillDetectedWithExactChainTail()
        {
            // The cycle sits at the BOTTOM of the chain, so the DFS must survive the full 100k-frame descent
            // before it can even reach it — and the first-fail message must still name the exact closing pair
            // (path-slice extraction unchanged by the DW-59 iterative rewrite). The deepest building points
            // back at its parent: b99999 requires b99998, which is Gray on the path → "b99998 -> b99999 -> b99998".
            var def = new FactionDefinition();
            const int depth = 100_000;
            for (int i = 0; i < depth; i++)
            {
                def.Buildings.Add(new BuildingDefinition
                {
                    Id = $"b{i}",
                    Prerequisites = i + 1 < depth ? new[] { $"b{i + 1}" } : new[] { $"b{depth - 2}" },
                });
            }

            var errors = TechTreeValidator.Validate(def);

            string cycle = Assert.Single(errors);
            Assert.Equal($"tech tree cycle: b{depth - 2} -> b{depth - 1} -> b{depth - 2}.", cycle);
        }

        [Fact]
        public void DeepChain_MidChainSiblingBranches_TraversalOrderAndBlackSkipUnchanged()
        {
            // Order-preservation guard for the DW-59 rewrite: a node with TWO prerequisites must examine
            // them in array order, fully retreating (blackening) the first subtree before descending the
            // second, and a Black re-encounter must be skipped silently — the first cycle found in that
            // DFS order is the ONE reported (first-fail), byte-identical message included.
            var def = new FactionDefinition();
            // root → left (clean, shared leaf) then root → right → right2 → right (the cycle the DFS
            // reaches SECOND); leaf is re-encountered Black via right2.
            def.Buildings.Add(new BuildingDefinition { Id = "root", Prerequisites = new[] { "left", "right" } });
            def.Buildings.Add(new BuildingDefinition { Id = "left", Prerequisites = new[] { "leaf" } });
            def.Buildings.Add(new BuildingDefinition { Id = "leaf" });
            def.Buildings.Add(new BuildingDefinition { Id = "right", Prerequisites = new[] { "right2" } });
            def.Buildings.Add(new BuildingDefinition { Id = "right2", Prerequisites = new[] { "leaf", "right" } });

            var errors = TechTreeValidator.Validate(def);

            string cycle = Assert.Single(errors);
            Assert.Equal("tech tree cycle: right -> right2 -> right.", cycle);
        }

        // ── Valid acyclic chain (existing shipped content) ───────────────────────

        [Fact]
        public void AlphaFaction_LoadsCleanly_NoNewError()
        {
            string path = ResolveDataPath("alpha_faction.json");
            FactionDefinition def = FactionDefinition.LoadFromFile(path); // throws on any regression
            Assert.NotNull(def);
        }

        [Fact]
        public void BetaFaction_LoadsCleanly_NoNewError()
        {
            string path = ResolveDataPath("beta_faction.json");
            FactionDefinition def = FactionDefinition.LoadFromFile(path); // throws on any regression
            Assert.NotNull(def);
        }

        // ── Story 4.6: ValidateProposedEdge (the Visual Tech Tree Editor's single-edge validation surface) ────────

        [Fact]
        public void ValidateProposedEdge_SelfEdge_RejectedWithWordingIdenticalToValidate()
        {
            // Proposing "a requires a" resolves through the SAME target-lookup + temporary-mutation + DetectCycle
            // path as every other proposed edge (no separate hand-formatted string) — cross-check its wording against
            // Validate() on an authored building that already lists itself as its own prerequisite, the same
            // cross-check pattern used for the 2-/3-node cases below, so the two code paths can never drift apart.
            var proposed = new FactionDefinition();
            proposed.Buildings.Add(new BuildingDefinition { Id = "a" });
            string? proposedErr = TechTreeValidator.ValidateProposedEdge(proposed, "a", "a");

            var authored = new FactionDefinition();
            authored.Buildings.Add(new BuildingDefinition { Id = "a", Prerequisites = new[] { "a" } });
            var authoredErrors = TechTreeValidator.Validate(authored);

            Assert.NotNull(proposedErr);
            Assert.Contains(proposedErr!, authoredErrors);
            // No mutation — the proposed edge is validated, never persisted, by this method.
            Assert.Empty(proposed.Buildings[0].Prerequisites ?? Array.Empty<string>());
        }

        [Fact]
        public void ValidateProposedEdge_ProposedTwoNodeCycle_RejectedWithWordingIdenticalToValidate()
        {
            // "a" already requires "b" (authored). Proposing the edge "b requires a" would close a 2-node cycle —
            // prove ValidateProposedEdge's rejection wording is BYTE-IDENTICAL to what Validate produces for the
            // equivalent already-authored graph (both share DetectCycle, so this is a code-sharing guarantee, not
            // just a convention).
            var proposed = new FactionDefinition();
            proposed.Buildings.Add(new BuildingDefinition { Id = "a", Prerequisites = new[] { "b" } });
            proposed.Buildings.Add(new BuildingDefinition { Id = "b" });
            string? proposedErr = TechTreeValidator.ValidateProposedEdge(proposed, "a", "b");

            var authored = new FactionDefinition();
            authored.Buildings.Add(new BuildingDefinition { Id = "a", Prerequisites = new[] { "b" } });
            authored.Buildings.Add(new BuildingDefinition { Id = "b", Prerequisites = new[] { "a" } });
            var authoredErrors = TechTreeValidator.Validate(authored);

            Assert.NotNull(proposedErr);
            Assert.Contains(proposedErr!, authoredErrors);

            // No mutation of the proposed def's target building's Prerequisites (restored via the finally).
            Assert.Equal(Array.Empty<string>(), proposed.Buildings[1].Prerequisites ?? Array.Empty<string>());
        }

        [Fact]
        public void ValidateProposedEdge_ProposedThreeNodeIndirectCycle_RejectedWithWordingIdenticalToValidate()
        {
            // "a"→"b"→"c" authored. Proposing "c requires a" would close the 3-node indirect cycle.
            var proposed = new FactionDefinition();
            proposed.Buildings.Add(new BuildingDefinition { Id = "a", Prerequisites = new[] { "b" } });
            proposed.Buildings.Add(new BuildingDefinition { Id = "b", Prerequisites = new[] { "c" } });
            proposed.Buildings.Add(new BuildingDefinition { Id = "c" });
            string? proposedErr = TechTreeValidator.ValidateProposedEdge(proposed, "a", "c");

            var authored = new FactionDefinition();
            authored.Buildings.Add(new BuildingDefinition { Id = "a", Prerequisites = new[] { "b" } });
            authored.Buildings.Add(new BuildingDefinition { Id = "b", Prerequisites = new[] { "c" } });
            authored.Buildings.Add(new BuildingDefinition { Id = "c", Prerequisites = new[] { "a" } });
            var authoredErrors = TechTreeValidator.Validate(authored);

            Assert.NotNull(proposedErr);
            Assert.Contains(proposedErr!, authoredErrors);
        }

        [Fact]
        public void ValidateProposedEdge_ValidNonCyclicEdge_ReturnsNull()
        {
            var def = new FactionDefinition();
            def.Buildings.Add(new BuildingDefinition { Id = "a" });
            def.Buildings.Add(new BuildingDefinition { Id = "b" });

            string? err = TechTreeValidator.ValidateProposedEdge(def, "a", "b");

            Assert.Null(err);
            // Never mutates — the target's Prerequisites is restored regardless of the outcome.
            Assert.Empty(def.Buildings[1].Prerequisites ?? Array.Empty<string>());
        }

        [Fact]
        public void ValidateProposedEdge_UnknownTarget_ReturnsNullDefensively()
        {
            var def = new FactionDefinition();
            def.Buildings.Add(new BuildingDefinition { Id = "a" });

            string? err = TechTreeValidator.ValidateProposedEdge(def, "a", "nonexistent");

            Assert.Null(err);
        }

        [Fact]
        public void ValidateProposedEdge_NullBuildingElementOrNullList_NoThrow()
        {
            // DW-100: ValidateProposedEdge's two scan loops (target lookup + source-is-building) must tolerate a null
            // Buildings element and a null Buildings list — a null-safety class its Validate sibling covers, but which
            // was previously untested on this method's own loops.
            var withNullElement = new FactionDefinition();
            withNullElement.Buildings = new List<BuildingDefinition>
            {
                null!,
                new BuildingDefinition { Id = "a" },
                new BuildingDefinition { Id = "b" },
            };
            var ex1 = Record.Exception(() => TechTreeValidator.ValidateProposedEdge(withNullElement, "a", "b"));
            Assert.Null(ex1);

            var nullList = new FactionDefinition { Buildings = null! };
            var ex2 = Record.Exception(() => TechTreeValidator.ValidateProposedEdge(nullList, "a", "b"));
            Assert.Null(ex2);
        }

        [Fact]
        public void ValidateProposedEdge_NonBuildingSourceId_RejectedInline()
        {
            // Story 4.11 review-pass fix: research nodes share this graph's port type, so a research id can now
            // reach this method as sourceId, where it was previously always a building id by construction.
            // DetectCycle only walks Buildings→Buildings edges and would otherwise silently ignore it and return
            // null ("valid") — this must be rejected inline instead, with wording identical to the import-time
            // referential lint.
            var def = new FactionDefinition();
            def.Buildings.Add(new BuildingDefinition { Id = "a" });
            def.Research.Add(new ResearchDefinition { Id = "armor_up", Levels = { new ResearchLevel { TimeTicks = 10 } } });

            string? err = TechTreeValidator.ValidateProposedEdge(def, "armor_up", "a");

            Assert.NotNull(err);
            Assert.Contains("unknown building id", err!);
            Assert.Contains("armor_up", err!);
            // No mutation on rejection.
            Assert.Empty(def.Buildings[0].Prerequisites ?? Array.Empty<string>());
        }

        /// <summary>Resolve a shipped faction JSON by walking up from the test-assembly directory to
        /// <c>resources/data/factions/</c> (mirrors <c>CanonicalScenarioTests.DataFile</c> /
        /// <c>DataDrivenBuildingScenario.AlphaFactionPath</c>, Story 1.10a's established pattern).</summary>
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
