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

        /// <summary>
        /// The faction <c>res://</c> path each authored start position was written AGAINST, indexed by POSITION in the
        /// map's ascending-<c>Slot</c> order (the same positional pairing <see cref="SkirmishSetupToScenario"/> uses).
        /// A map's pre-placed units name that faction's unit ids, so this is the source roster the role remap reads.
        /// </summary>
        public IReadOnlyList<string> SlotFactionResPaths { get; init; } = System.Array.Empty<string>();

        /// <summary>The map's pre-placed starting units, normalized to start-POSITION (not raw slot ordinal) so the
        /// validator and the transform agree on which player inherits which starting army.</summary>
        public IReadOnlyList<MapPrePlacedUnit> PrePlacedUnits { get; init; } = System.Array.Empty<MapPrePlacedUnit>();
    }

    /// <summary>Story 11.1 — one pre-placed starting unit authored by a map, keyed by start POSITION.</summary>
    public sealed class MapPrePlacedUnit
    {
        /// <summary>Index into the map's ascending-<c>Slot</c> start positions (NOT the raw <c>Slot</c> ordinal).</summary>
        public int Position { get; init; }
        /// <summary>The unit id as authored — an id in that position's authored faction roster.</summary>
        public string UnitId { get; init; } = "";
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

        /// <summary>
        /// This faction's roster in authored order — the coordinate space of the ROLE SKELETON. A map's pre-placed
        /// units name one faction's ids, so launching a different faction must translate them; the translation key is
        /// (<see cref="FactionUnitEntry.Category"/>, ordinal-within-category), resolved against this list. Ordered, not
        /// a dictionary, because the ordinal IS the role coordinate.
        /// </summary>
        public IReadOnlyList<FactionUnitEntry> Units { get; init; } = System.Array.Empty<FactionUnitEntry>();
    }

    /// <summary>Story 11.1 — one roster entry: the unit's id plus its category, the two fields the role remap needs.
    /// A flattened projection of <c>UnitDefinition</c> so the transform stays pure data (no def graph, no Godot).</summary>
    public sealed class FactionUnitEntry
    {
        public string Id { get; init; } = "";
        /// <summary>The <c>UnitCategory</c> token ("Worker"/"Melee"/"Ranged"/"Siege"/"Air"), matched case-insensitively
        /// to mirror <c>FactionDefinition.GetUnitsByCategory</c>.</summary>
        public string Category { get; init; } = "";
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

                // Positional normalization: the transform pairs the i-th ACTIVE setup slot with the i-th base slot in
                // ascending-Slot order, so both the authored faction path and the pre-placed units are recorded by that
                // same POSITION. A map declaring sparse ordinals (0, 2, 5) therefore stays consistent between the
                // validator's pre-flight check and the transform's actual remap.
                ScenarioPlayerSlot[] orderedSlots = (map.PlayerSlots ?? System.Array.Empty<ScenarioPlayerSlot>())
                    .OrderBy(s => s.Slot).ToArray();
                var positionOfSlot = new Dictionary<int, int>();
                for (int i = 0; i < orderedSlots.Length; i++) positionOfSlot[orderedSlots[i].Slot] = i;

                var prePlaced = new List<MapPrePlacedUnit>();
                foreach (ScenarioUnit u in map.Units ?? System.Array.Empty<ScenarioUnit>())
                {
                    if (u == null) continue;
                    if (!positionOfSlot.TryGetValue(u.Slot, out int pos)) continue; // orphaned → dropped by the transform too
                    prePlaced.Add(new MapPrePlacedUnit { Position = pos, UnitId = u.UnitId ?? "" });
                }

                entries.Add(new MapEntry
                {
                    Id                  = id,
                    DisplayName         = string.IsNullOrEmpty(map.DisplayName) ? map.Id : map.DisplayName,
                    ResPath             = CombineRes(resScenariosDir, fileName),
                    MapBounds           = map.MapBounds,
                    SuggestedPlayers    = map.SuggestedPlayers,
                    StartPositionCount  = map.PlayerSlots?.Length ?? 0,
                    Author              = map.Author ?? "",
                    SlotFactionResPaths = orderedSlots.Select(s => s.FactionJson ?? "").ToList(),
                    PrePlacedUnits      = prePlaced,
                });
            }

            return entries
                .OrderBy(e => e.Id, System.StringComparer.Ordinal)
                .ThenBy(e => e.ResPath, System.StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>
        /// Scan <paramref name="absFactionsDir"/> for <see cref="FactionFiles.DiscoveryGlob"/> files, gate each through
        /// <see cref="FactionValidator.ValidateComplete"/> (drop-on-fail — the roster-completeness discovery contract),
        /// dedupe by id (first file in ordinal filename order wins), and return one <see cref="FactionEntry"/> each with
        /// its <c>res://</c> path — ordinal-sorted by id (mirrors <see cref="FactionDefinition.LoadSelectableFromDirectory"/>,
        /// which does not expose the source path this story needs). Never throws.
        ///
        /// <para>DW-696: the discovery glob is DERIVED from the constant the faction wizard writes files under, never a
        /// hand-copied literal, so a suffix change can no longer make wizard-saved factions silently undiscoverable here.</para>
        ///
        /// <para>DW-780: this is the THIRD faction-file reader, and it runs the SAME DW-537 raw duplicate-cost-key pass
        /// the other two do (<see cref="FactionDefinition.LoadFromFile"/> throws on it,
        /// <see cref="FactionDefinition.LoadSelectableFromDirectory"/> excludes-with-reason). Without it a faction whose
        /// <c>cost</c> block silently last-wins would list as a selectable skirmish faction while both other entry
        /// points reject it — the same selectable-but-not-launchable split DW-327/DW-537 closed elsewhere. Drop-on-fail,
        /// matching this method's never-throws posture.</para>
        /// </summary>
        public static IReadOnlyList<FactionEntry> ScanFactions(string absFactionsDir, string resFactionsDir)
        {
            if (string.IsNullOrEmpty(absFactionsDir) || !Directory.Exists(absFactionsDir))
                return System.Array.Empty<FactionEntry>();

            string[] files;
            try { files = Directory.GetFiles(absFactionsDir, FactionFiles.DiscoveryGlob); } // DW-696: derived, never hand-copied
            catch { return System.Array.Empty<FactionEntry>(); }

            var entries = new List<FactionEntry>();
            var seenIds = new HashSet<string>(System.StringComparer.Ordinal);
            foreach (string file in files.OrderBy(f => f, System.StringComparer.Ordinal))
            {
                // DW-780: the raw text is hoisted into a local (still inside the try, so an I/O fault keeps the
                // drop-on-fail path) because the duplicate-key pass below re-walks it — the exact shape
                // LoadSelectableFromDirectory uses.
                string text;
                FactionDefinition? def;
                try
                {
                    text = File.ReadAllText(file);
                    def = JsonSerializer.Deserialize<FactionDefinition>(text, FactionDefinition.JsonOptions);
                }
                catch { continue; } // malformed → skip, scan continues

                if (def is null) continue;
                if (CostDuplicateKeyGuard.Scan(text).Count > 0) continue;  // DW-780: last-wins cost block → not selectable
                if (!FactionValidator.ValidateComplete(def).Ok) continue; // incomplete roster → not selectable
                if (!seenIds.Add(def.Id)) continue;                       // duplicate id → first-file-wins

                // Flatten the roster to (id, category) in AUTHORED ORDER — the role-skeleton coordinate space the
                // cross-faction unit remap resolves against. A null element is skipped (DW-103 convention).
                var roster = new List<FactionUnitEntry>();
                foreach (UnitDefinition u in def.Units ?? new List<UnitDefinition>())
                {
                    if (u == null) continue;
                    roster.Add(new FactionUnitEntry { Id = u.Id ?? "", Category = u.Category ?? "" });
                }

                entries.Add(new FactionEntry
                {
                    Id          = def.Id,
                    DisplayName = string.IsNullOrEmpty(def.DisplayName) ? def.Id : def.DisplayName,
                    ResPath     = CombineRes(resFactionsDir, Path.GetFileName(file)),
                    Units       = roster,
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
