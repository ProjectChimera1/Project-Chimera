#nullable enable
using ProjectChimera.Core; // Fixed, Faction, ResourceCollectionModel, ResourceKind

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// DW-151 (A10/V1) — the SINGLE Godot-free conversion site from an editor resource-node's live-store field set to
    /// its persisted <see cref="ScenarioResourceNode"/> DTO. Extracted so the mapping (Faction→0-based OwnerSlot with
    /// Neutral→-1, collection-model/resource-kind enum→string, empty structure id→null RequiresStructure, Fixed→float
    /// radius) lives in one place callable from <c>MainScene.SyncResourceNode.Add</c> AND unit-testable directly
    /// (mirrors the <c>MapBoundsMath</c>/<c>StartSlotMath</c> extraction pattern).
    /// </summary>
    public static class ResourceNodeDtoMap
    {
        /// <summary>Resolve a node's owner <see cref="Faction"/> to a 0-based scenario owner-slot: Neutral→-1
        /// (unset — inert for GATHER/Streaming), else <c>(int)faction - 1</c> (Player1→0, Player2→1, …).</summary>
        public static int OwnerSlotOf(Faction ownerFaction)
            => ownerFaction == Faction.Neutral ? -1 : (int)ownerFaction - 1;

        /// <summary>Build the persisted <see cref="ScenarioResourceNode"/> from the live-store field set.</summary>
        public static ScenarioResourceNode ToDto(float x, float z, float supply, float rate, int maxGatherers,
            ResourceCollectionModel collectionModel, ResourceKind resourceType,
            string requiresStructureId, Fixed requiresStructureRadius, Faction ownerFaction, int incomePeriodTicks)
            => new ScenarioResourceNode
            {
                X = x, Z = z, Supply = supply, Rate = rate, MaxGatherers = maxGatherers,
                CollectionModel = collectionModel.ToString(),
                ResourceType    = resourceType.ToString(),
                RequiresStructure = string.IsNullOrEmpty(requiresStructureId) ? null : requiresStructureId,
                RequiresStructureRadius = requiresStructureRadius.ToFloat(),
                OwnerSlot = OwnerSlotOf(ownerFaction),
                IncomePeriodTicks = incomePeriodTicks,
            };
    }
}
