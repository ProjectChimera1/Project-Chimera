#nullable enable
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ProjectChimera.Core.Definitions; // ScenarioData, ScenarioSerializer, FactionDefinition, FactionValidator

namespace ProjectChimera.Core.Skirmish
{
    /// <summary>Story 11.1 — a selectable shipped map surfaced on the skirmish setup screen. Textual properties only
    /// (no live minimap thumbnail — deferred). <see cref="ResPath"/> is the <c>res://</c> path the transform commits
    /// as the base map source, so the same disk-scenario fail-closed apply path is reused.</summary>
    public sealed class MapEntry
    {
        public string Id { get; init; } = "";
        public string DisplayName { get; init; } = "";
        /// <summary>The map's <c>res://</c> scenario path (the base for the in-memory clone).</summary>
        public string ResPath { get; init; } = "";
        public float MapBounds { get; init; }
        public int SuggestedPlayers { get; init; }
        /// <summary>Number of authored start positions (= <c>PlayerSlots.Length</c>) — the cap on active slots.</summary>
        public int StartPositionCount { get; init; }
        public string Author { get; init; } = "";
    }

    /// <summary>Story 11.1 — a selectable faction with its <c>res://</c> path. The path is what a slot commits as
    /// <c>ScenarioPlayerSlot.FactionJson</c>, so the existing <c>ResolveSlotFactionDefs</c> resolves abilities + drops
    /// unknown-tag units at load (DW-121 closed by construction — never an in-memory def handed to a slot).</summary>
    public sealed class FactionEntry
    {
        public string Id { get; init; } = "";
        public string DisplayName { get; init; } = "";
        /// <summary>The faction JSON's <c>res://</c> path (the <c>FactionJson</c> source).</summary>
        public string ResPath { get; init; } = "";
    }

    /// <summary>
    /// Story 11.1 — enumerates the selectable shipped maps and factions for the skirmish setup screen. Godot-free
    /// (auto-globbed into the Tier-1 compile): it takes absolute OS directories (resolved by the presentation layer via
    /// <c>ProjectSettings.GlobalizePath</c>) plus the matching <c>res://</c> directory prefix so it can construct each
    /// entry's <c>res://</c> path without any Godot dependency — keeping it unit-testable against temp dirs. Never throws:
    /// a missing directory yields an empty list; an unparseable map is skipped (lenient); a faction failing
    /// <see cref="FactionValidator.ValidateComplete"/> is dropped (the discovery contract, mirroring
    /// <see cref="FactionDefinition.LoadSelectableFromDirectory"/>).
    /// </summary>
    public static class SkirmishCatalog
    {
        /// <summary>
        /// Scan <paramref name="absScenariosDir"/> for <c>*.json</c> map files (lenient — a file that fails to parse is
        /// skipped, never throwing), returning one <see cref="MapEntry"/> each with its <c>res://</c> path composed from
        /// <paramref name="resScenariosDir"/>. Deterministically ordered by <see cref="MapEntry.Id"/> (tie-broken by
        /// <see cref="MapEntry.ResPath"/>) so the list is stable regardless of on-disk enumeration order.
        /// </summary>
        public static IReadOnlyList<MapEntry> ScanMaps(string absScenariosDir, string resScenariosDir)
        {
            if (string.IsNullOrEmpty(absScenariosDir) || !Directory.Exists(absScenariosDir))
                return System.Array.Empty<MapEntry>();

            string[] files;
            try { files = Directory.GetFiles(absScenariosDir, "*.json"); }
            catch { return System.Array.Empty<MapEntry>(); }

            var entries = new List<MapEntry>();
            var seenIds = new HashSet<string>(System.StringComparer.Ordinal);
            foreach (string file in files.OrderBy(f => f, System.StringComparer.Ordinal))
            {
                ScenarioData? map;
                try { map = ScenarioSerializer.LoadFromFile(file); }
                catch { continue; } // lenient: a malformed map never aborts the scan

                if (map == null) continue;
                // Review patch: only real playable maps are selectable. A *.json in the scenarios dir with no start
                // positions (e.g. a fragment/saved non-map scenario) would otherwise list as a phantom entry that can
                // never launch (activeCount > 0 always exceeds a 0 start-position map). Skip it — mirrors how ScanFactions
                // drops files that fail the discovery contract.
                if ((map.PlayerSlots?.Length ?? 0) <= 0) continue;

                string fileName = Path.GetFileName(file);
                string id = string.IsNullOrEmpty(map.Id) ? Path.GetFileNameWithoutExtension(fileName) : map.Id;
                if (!seenIds.Add(id)) continue; // duplicate id → first file in ordinal filename order wins (mirrors ScanFactions)

                entries.Add(new MapEntry
                {
                    Id                 = id,
                    DisplayName        = string.IsNullOrEmpty(map.DisplayName) ? map.Id : map.DisplayName,
                    ResPath            = CombineRes(resScenariosDir, fileName),
                    MapBounds          = map.MapBounds,
                    SuggestedPlayers   = map.SuggestedPlayers,
                    StartPositionCount = map.PlayerSlots?.Length ?? 0,
                    Author             = map.Author ?? "",
                });
            }

            return entries
                .OrderBy(e => e.Id, System.StringComparer.Ordinal)
                .ThenBy(e => e.ResPath, System.StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>
        /// Scan <paramref name="absFactionsDir"/> for <c>*_faction.json</c> files, gate each through
        /// <see cref="FactionValidator.ValidateComplete"/> (drop-on-fail — the roster-completeness discovery contract),
        /// dedupe by id (first file in ordinal filename order wins), and return one <see cref="FactionEntry"/> each with
        /// its <c>res://</c> path — ordinal-sorted by id (mirrors <see cref="FactionDefinition.LoadSelectableFromDirectory"/>,
        /// which does not expose the source path this story needs). Never throws.
        /// </summary>
        public static IReadOnlyList<FactionEntry> ScanFactions(string absFactionsDir, string resFactionsDir)
        {
            if (string.IsNullOrEmpty(absFactionsDir) || !Directory.Exists(absFactionsDir))
                return System.Array.Empty<FactionEntry>();

            string[] files;
            try { files = Directory.GetFiles(absFactionsDir, "*_faction.json"); }
            catch { return System.Array.Empty<FactionEntry>(); }

            var entries = new List<FactionEntry>();
            var seenIds = new HashSet<string>(System.StringComparer.Ordinal);
            foreach (string file in files.OrderBy(f => f, System.StringComparer.Ordinal))
            {
                FactionDefinition? def;
                try { def = JsonSerializer.Deserialize<FactionDefinition>(File.ReadAllText(file), FactionDefinition.JsonOptions); }
                catch { continue; } // malformed → skip, scan continues

                if (def is null) continue;
                if (!FactionValidator.ValidateComplete(def).Ok) continue; // incomplete roster → not selectable
                if (!seenIds.Add(def.Id)) continue;                       // duplicate id → first-file-wins

                entries.Add(new FactionEntry
                {
                    Id          = def.Id,
                    DisplayName = string.IsNullOrEmpty(def.DisplayName) ? def.Id : def.DisplayName,
                    ResPath     = CombineRes(resFactionsDir, Path.GetFileName(file)),
                });
            }

            return entries.OrderBy(e => e.Id, System.StringComparer.Ordinal).ToList();
        }

        /// <summary>Compose a <c>res://</c> path from a directory prefix + a file name with exactly one separator. Kept
        /// Godot-free (a plain string join, not <c>ProjectSettings</c>) so the catalog stays Tier-1-testable.</summary>
        private static string CombineRes(string resDir, string fileName)
        {
            if (string.IsNullOrEmpty(resDir)) return fileName;
            return resDir.TrimEnd('/') + "/" + fileName;
        }
    }
}
