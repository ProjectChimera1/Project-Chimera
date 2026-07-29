#nullable enable
using System.Collections.Generic;
using ProjectChimera.Combat; // DenialReason
using ProjectChimera.Core;    // ResourceStore, Faction, Fixed

namespace ProjectChimera.Economy
{
    /// <summary>
    /// Story 11.4 review (P5) — the SINGLE home for resolving which resource made a cost unaffordable, so a Train /
    /// Build / Research / Shop afford-reject reports the resource that is actually short instead of every duplicated
    /// copy fabricating "Not enough Ore". Resolves the FIRST unaffordable entry of the real cost map; an entry that is
    /// neither ore nor crystal (a sparse/custom key, or the fail-closed unregistered case) falls back to the generic
    /// <see cref="DenialReason.InsufficientResources"/> rather than mislabeling it as ore.
    ///
    /// Godot-free (Core + Combat only) so it is Tier-1 testable, and shared by <c>BuildingSystem</c> and
    /// <c>ResearchSystem</c> so the two can never drift.
    /// </summary>
    public static class DenialReasons
    {
        /// <summary>Resolve the denial reason for a cost the faction cannot afford. Call this only on the reject path
        /// (after <see cref="ResourceStore.CanAfford"/> has returned false); it re-probes the same guard state to name
        /// the specific short resource (single-truth — the same guard that rejected computes it).</summary>
        public static DenialReason ForUnaffordableCost(ResourceStore resources, Faction faction,
                                                       IReadOnlyDictionary<string, int> cost)
        {
            foreach (var (key, amount) in cost)
            {
                if (amount <= 0) continue;
                switch (key)
                {
                    case "ore":
                        if (!resources.CanAffordOre(faction, Fixed.FromInt(amount))) return DenialReason.NeedOre;
                        break;
                    case "crystal":
                        if (!resources.CanAffordCrystal(faction, Fixed.FromInt(amount))) return DenialReason.NeedCrystal;
                        break;
                    default:
                        // Unregistered / custom resource id — ResourceStore.CanAfford fails closed on it, so it is the
                        // reason the cost was unaffordable. Report a generic shortage, never a fabricated ore one.
                        return DenialReason.InsufficientResources;
                }
            }
            // Every named entry was individually affordable (e.g. a rounding/edge case) — a generic shortage is the
            // honest fallback rather than pinning a resource that actually passed.
            return DenialReason.InsufficientResources;
        }
    }
}
