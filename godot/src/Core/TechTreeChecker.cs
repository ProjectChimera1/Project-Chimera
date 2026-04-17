#nullable enable
namespace ProjectChimera.Core
{
    /// <summary>
    /// Checks tech tree prerequisites against the current building state.
    ///
    /// Prerequisites are building-type ID strings (e.g. "barracks", "archery_range")
    /// that must exist as alive, fully-constructed buildings for a given faction
    /// before a unit can be trained or a building can be placed.
    ///
    /// Pure C# — no Godot dependency.
    /// </summary>
    public static class TechTreeChecker
    {
        /// <summary>
        /// Returns true if every required building type exists as an alive,
        /// fully-constructed building for the given faction.
        /// Null or empty prerequisite arrays always pass.
        /// </summary>
        public static bool AreMet(BuildingStore buildings, Faction faction, string[]? prereqs)
        {
            if (prereqs == null || prereqs.Length == 0) return true;
            foreach (string p in prereqs)
            {
                BuildingType? bt = ParseBuildingType(p);
                if (bt == null || !HasCompletedBuilding(buildings, faction, bt.Value))
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Returns the human-readable name of the first unmet prerequisite,
        /// or null if all prerequisites are satisfied.
        /// </summary>
        public static string? FirstMissing(BuildingStore buildings, Faction faction, string[]? prereqs)
        {
            if (prereqs == null || prereqs.Length == 0) return null;
            foreach (string p in prereqs)
            {
                BuildingType? bt = ParseBuildingType(p);
                if (bt == null || !HasCompletedBuilding(buildings, faction, bt.Value))
                    return DisplayName(bt) ?? p;
            }
            return null;
        }

        /// <summary>Canonical string ID for a BuildingType (matches JSON building IDs).</summary>
        public static string BuildingTypeId(BuildingType type) => type switch
        {
            BuildingType.CommandCenter => "command_center",
            BuildingType.Barracks      => "barracks",
            BuildingType.ArcheryRange  => "archery_range",
            BuildingType.SiegeWorkshop => "siege_workshop",
            _                          => "",
        };

        // ── Private helpers ────────────────────────────────────────────────────

        private static bool HasCompletedBuilding(BuildingStore buildings, Faction faction, BuildingType type)
        {
            for (int i = 0; i < buildings.Count; i++)
            {
                if (!buildings.Alive[i]) continue;
                if (buildings.FactionOf[i] != faction) continue;
                if (buildings.Type[i] != type) continue;
                if (buildings.IsUnderConstruction(i)) continue; // not functional yet
                return true;
            }
            return false;
        }

        private static BuildingType? ParseBuildingType(string id) => id switch
        {
            "command_center" => BuildingType.CommandCenter,
            "barracks"       => BuildingType.Barracks,
            "archery_range"  => BuildingType.ArcheryRange,
            "siege_workshop" => BuildingType.SiegeWorkshop,
            _                => null,
        };

        private static string? DisplayName(BuildingType? type) => type switch
        {
            BuildingType.CommandCenter => "Command Center",
            BuildingType.Barracks      => "Barracks",
            BuildingType.ArcheryRange  => "Archery Range",
            BuildingType.SiegeWorkshop => "Siege Workshop",
            _                          => null,
        };
    }
}
