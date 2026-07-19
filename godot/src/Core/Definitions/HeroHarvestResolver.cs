#nullable enable
using System.Collections.Generic;
using ProjectChimera.Core;      // Fixed, HeroStore, HeroId, ItemStore

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// The Godot-free plain-data core of the end-of-match hero HARVEST + picker has-vs-fallback resolution
    /// (DW-27, DW-32). Extracted from <c>MainScene.ResetToAuthoredStart</c> (the live-row capture) and
    /// <c>HeroPickerOverlay.ResolveHeroProgress</c> (the Save/Overwrite fallback rule) so the two decisions are
    /// Tier-1 testable without Godot: a wrong-way change that drops the harvested Level/Xp and re-persists the
    /// level-1/0 placeholder (DW-27), or a fallen hero's manifest-persisted attributes finalizing per FR-7a
    /// (DW-32), now go RED in <c>ProjectChimera.Sim.Tests</c> instead of shipping behind a green suite.
    ///
    /// <para>Pure move-and-delegate — reproduces EXACTLY two existing decisions with no behavior change:
    /// (1) <see cref="Capture"/> = the first live (<see cref="HeroStore.Alive"/>) row whose id matches
    /// <see cref="HeroProfileLoader.MintId"/> of the deployed profile → its Level/Xp + <see cref="HeroProfileLoader.CaptureInventory"/>;
    /// (2) <see cref="ResolveProgress"/>/<see cref="ResolveInventory"/> = "harvest Has for THIS heroDefId ? harvested : fallback/null".</para>
    ///
    /// <para>Pure C#: no <c>using Godot</c>, no wall-clock/RNG/float, ascending list iteration only (no
    /// <c>Dictionary</c> enumeration) — in-scope for the release analyzer gate.</para>
    /// </summary>
    public static class HeroHarvestResolver
    {
        /// <summary>
        /// The end-of-match harvest of a deployed hero's live progress. A <c>readonly struct</c> of value fields plus one
        /// reference: <see cref="Inventory"/> aliases the <see cref="List{T}"/> that <see cref="HeroProfileLoader.CaptureInventory"/>
        /// returns (it is freshly built per capture and not retained elsewhere, so the alias is safe). <see cref="Has"/> is
        /// true only when a live <see cref="HeroStore.Alive"/> row matched the deployed profile's minted id at capture time;
        /// otherwise this is <see cref="None"/> (<c>default</c>, so an un-harvested picker session falls back exactly as before).
        /// </summary>
        public readonly struct HeroHarvest
        {
            /// <summary>True when a live hero row matched the deployed profile at capture — the Level/Xp/Inventory below are real.</summary>
            public readonly bool Has;
            /// <summary>The deployed profile's <see cref="PlayerProfile.HeroDefId"/> — the unit id the harvested values belong to.</summary>
            public readonly string? HeroDefId;
            /// <summary>The captured live <see cref="HeroStore.Level"/>.</summary>
            public readonly int Level;
            /// <summary>The captured live <see cref="HeroStore.Xp"/>.</summary>
            public readonly Fixed Xp;
            /// <summary>The captured live carried loadout (item-def id + charges + slot), or null when no store was available.
            /// Typed as the concrete <see cref="List{T}"/> that <see cref="HeroProfileLoader.CaptureInventory"/> returns and
            /// that <see cref="PlayerProfile.Inventory"/> holds, so the re-mint / Save paths assign it with no runtime cast.</summary>
            public readonly List<ProfileInventoryItem>? Inventory;

            /// <summary>Construct a populated harvest (see <see cref="Capture"/>); external code reads via the resolve helpers.</summary>
            public HeroHarvest(bool has, string? heroDefId, int level, Fixed xp, List<ProfileInventoryItem>? inventory)
            {
                Has       = has;
                HeroDefId = heroDefId;
                Level     = level;
                Xp        = xp;
                Inventory = inventory;
            }

            /// <summary>The "nothing harvested" harvest (<c>Has == false</c>). Equal to <c>default(HeroHarvest)</c>, so the
            /// flat <c>SceneContext.Harvest</c> field defaults to it and an un-harvested picker session falls back as before.</summary>
            public static readonly HeroHarvest None = default;
        }

        /// <summary>
        /// Capture the deployed <paramref name="profile"/>'s live progress from <paramref name="heroes"/> — mirrors the old
        /// inline <c>ResetToAuthoredStart</c> step 1 on the production path: a null profile / null store, or no matching live
        /// row, yields <see cref="HeroHarvest.None"/>; otherwise the first <see cref="HeroStore.Alive"/> row whose
        /// <see cref="HeroStore.Id"/> equals <see cref="HeroProfileLoader.MintId"/> of the profile is harvested with its
        /// Level/Xp and (when the item stores are supplied) its <see cref="HeroProfileLoader.CaptureInventory"/> loadout.
        /// The <paramref name="items"/>/<paramref name="reg"/> null-guard is the one addition over the original (which
        /// always passed non-null stores): it lets Godot-free callers/tests omit them and get a null loadout — the
        /// production picker path always supplies both, so behavior there is unchanged.
        ///
        /// <para>Keys on the persisted <see cref="HeroStore.Alive"/> row (NOT <c>Alive3_14</c>), so a FALLEN hero whose
        /// row stays alive for persistence (revival disabled or awaiting) is still harvestable (FR-7a / DW-32). Iterates
        /// slots in ascending order — no nondeterministic enumeration.</para>
        /// </summary>
        public static HeroHarvest Capture(HeroStore heroes, ItemStore? items, ItemRegistry? reg, PlayerProfile? profile)
        {
            if (profile == null || heroes == null) return HeroHarvest.None;

            HeroId target = HeroProfileLoader.MintId(profile);
            for (int slot = 0; slot < heroes.Count; slot++)
            {
                if (!heroes.Alive[slot] || heroes.Id[slot] != target) continue;
                // Story 3.16: harvest the live carried inventory when the item stores are available (the picker path always
                // supplies them; persistence-only callers may not) — else null, exactly as the pre-extraction guard did.
                List<ProfileInventoryItem>? inv = (items != null && reg != null)
                    ? HeroProfileLoader.CaptureInventory(heroes, items, reg, slot)
                    : null;
                return new HeroHarvest(true, profile.HeroDefId, heroes.Level[slot], heroes.Xp[slot], inv);
            }
            return HeroHarvest.None;
        }

        /// <summary>
        /// The has-vs-fallback Level/Xp rule from <c>HeroPickerOverlay.ResolveHeroProgress</c>: return the harvested
        /// (Level, Xp) when the harvest <see cref="HeroHarvest.Has"/> and its <see cref="HeroHarvest.HeroDefId"/> matches
        /// <paramref name="heroDefId"/>, else the supplied fallback (Save: authored 1 / 0; Overwrite: the target's own).
        /// </summary>
        public static (int Level, Fixed Xp) ResolveProgress(in HeroHarvest harvest, string heroDefId,
                                                            int fallbackLevel, Fixed fallbackXp)
        {
            if (harvest.Has && harvest.HeroDefId == heroDefId) return (harvest.Level, harvest.Xp);
            return (fallbackLevel, fallbackXp);
        }

        /// <summary>
        /// The has-vs-fallback inventory rule: return the harvested loadout when the harvest <see cref="HeroHarvest.Has"/>
        /// and its <see cref="HeroHarvest.HeroDefId"/> matches <paramref name="heroDefId"/>, else null. The caller decides
        /// the null fallback (Save persists an empty loadout; Overwrite keeps the target's previously-saved inventory).
        /// </summary>
        public static List<ProfileInventoryItem>? ResolveInventory(in HeroHarvest harvest, string heroDefId)
        {
            if (harvest.Has && harvest.HeroDefId == heroDefId) return harvest.Inventory;
            return null;
        }
    }
}
