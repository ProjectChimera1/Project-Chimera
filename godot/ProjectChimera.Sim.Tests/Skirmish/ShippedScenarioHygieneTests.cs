#nullable enable
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using ProjectChimera.Core.Skirmish; // SkirmishCatalog, MapEntry
using Xunit;

namespace ProjectChimera.Sim.Tests.Skirmish
{
    /// <summary>
    /// DW-461 — content hygiene for the SHIPPED skirmish map catalog. <c>SkirmishCatalog.ScanMaps</c> surfaces every
    /// parseable <c>*.json</c> with ≥1 start position in <c>godot/resources/data/scenarios/</c> as a selectable,
    /// launchable map on the skirmish setup screen — there is no other curation gate. Dev/test scratch saves landing
    /// in that directory therefore SHIP as playable maps (that is exactly how <c>123.json</c> "Alpha Skirmish" and
    /// <c>my-new-map.json</c> "My New Map" surfaced). This test IS the curation allowlist: the shipped set is pinned
    /// exactly, so adding a map (or a stray scratch save) turns Tier-1 red until the allowlist is deliberately
    /// updated. Relocated scratch content lives in <c>dev-scratch/scenarios/</c> at the repo root.
    /// </summary>
    public class ShippedScenarioHygieneTests
    {
        /// <summary>THE curated shipped-map id set. Editing this list is the deliberate act of shipping (or
        /// retiring) a skirmish map — keep it ordinally sorted to match the catalog's deterministic order.</summary>
        private static readonly string[] ShippedMapIds =
        {
            "alpha_map_01",
            "map_02_iron_crossing",
            "map_03_the_narrows",
            "map_04_scorched_plains",
            "map_05_crossroads",
            "map_06_contested_peaks",
            "map_10_mirror_lake",
            "map_11_blitz",
            "map_12_the_frontier",
            "quad_map_01",
        };

        private static string ScenariosDir([CallerFilePath] string thisFile = "")
            => Path.GetFullPath(Path.Combine(
                Path.GetDirectoryName(thisFile)!, "..", "..", "resources", "data", "scenarios"));

        [Fact]
        public void ShippedScenariosDir_ContainsExactlyTheCuratedMapSet()
        {
            IReadOnlyList<MapEntry> maps = SkirmishCatalog.ScanMaps(ScenariosDir(), "res://resources/data/scenarios");

            Assert.Equal(ShippedMapIds, maps.Select(m => m.Id).ToArray());
        }

        [Fact]
        public void ShippedScenariosDir_ContainsOnlyMapJsonFiles_NoScratchExportsOrFragments()
        {
            // The dir is the shipped catalog surface: every file in it must BE a curated map file. A stray export
            // (e.g. the relocated 123.chimera.zip), a fragment, or a scratch save with 0 start positions would either
            // ship dead bytes or list as a map. File set == the allowlist's file basis.
            string[] files = Directory.GetFiles(ScenariosDir())
                .Select(Path.GetFileName)
                .Where(f => f != null)
                .Select(f => f!)
                .OrderBy(f => f, System.StringComparer.Ordinal)
                .ToArray();

            Assert.All(files, f => Assert.EndsWith(".json", f, System.StringComparison.Ordinal));
            Assert.Equal(ShippedMapIds.Length, files.Length); // every file IS a listed map (ids pinned above)
        }
    }
}
