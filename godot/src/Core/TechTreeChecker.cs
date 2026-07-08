#nullable enable
namespace ProjectChimera.Core
{
    /// <summary>
    /// Checks tech tree prerequisites against the current building state.
    ///
    /// Prerequisites are building-definition ID strings (e.g. "barracks", "archery_range", or any
    /// data-authored id including a <see cref="BuildingType.Custom"/> building's id) that must exist as
    /// alive, fully-constructed buildings for a given faction before a unit can be trained or a building
    /// can be placed.
    ///
    /// Story 4.2: resolution is now purely string-match against <see cref="BuildingStore.DefinitionId"/> —
    /// no enum parse step — so ANY authored building id (not just the 5 legacy enum-backed ones) can be
    /// satisfied or named as a prerequisite. Display-name resolution moved out to the callers
    /// (<see cref="ProjectChimera.Economy.BuildingSystem"/>/<c>EntityPlacer</c>) that have a
    /// <see cref="ProjectChimera.Core.Definitions.FactionDefinition"/> in scope to resolve one from.
    ///
    /// Pure C# — no Godot dependency.
    /// </summary>
    public static class TechTreeChecker
    {
        /// <summary>
        /// Returns true if every required building id exists as an alive, fully-constructed building for
        /// the given faction. Null or empty prerequisite arrays always pass.
        /// </summary>
        public static bool AreMet(BuildingStore buildings, Faction faction, string[]? prereqs)
        {
            if (prereqs == null || prereqs.Length == 0) return true;
            foreach (string p in prereqs)
            {
                if (!HasCompletedBuilding(buildings, faction, p))
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Returns the raw id of the first unmet prerequisite, or null if all prerequisites are satisfied.
        /// No display-name resolution — this method has no <see cref="ProjectChimera.Core.Definitions.FactionDefinition"/>
        /// in scope to resolve one from; callers that need a display string resolve the returned id themselves.
        /// </summary>
        public static string? FirstMissing(BuildingStore buildings, Faction faction, string[]? prereqs)
        {
            if (prereqs == null || prereqs.Length == 0) return null;
            foreach (string p in prereqs)
            {
                if (!HasCompletedBuilding(buildings, faction, p))
                    return p;
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
            BuildingType.Aviary        => "aviary",        // Story 2.8 — else "" → GetBuilding("") misses the aviary def (no cost/prereq).
            _                          => "",
        };

        // ── Private helpers ────────────────────────────────────────────────────

        /// <summary>Matches the raw prereq string against <see cref="BuildingStore.DefinitionId"/> — any
        /// authored id, not just the legacy enum-backed 5 (Story 4.2 generalization; closes the
        /// <see cref="BuildingType.Custom"/> gap).</summary>
        private static bool HasCompletedBuilding(BuildingStore buildings, Faction faction, string definitionId)
        {
            for (int i = 0; i < buildings.Count; i++)
            {
                if (!buildings.Alive[i]) continue;
                if (buildings.FactionOf[i] != faction) continue;
                if (buildings.DefinitionId[i] != definitionId) continue;
                if (buildings.IsUnderConstruction(i)) continue; // not functional yet
                return true;
            }
            return false;
        }
    }
}
