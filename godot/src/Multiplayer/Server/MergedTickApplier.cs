#nullable enable
using System;
using ProjectChimera.Core;    // EntityWorld, Faction, UnitOrder
using ProjectChimera.Combat;  // CombatEventQueue, ItemSystem
using ProjectChimera.Economy; // BuildingSystem, ResearchSystem

namespace ProjectChimera.Multiplayer.Server
{
    /// <summary>
    /// Story 9.3 (SD-3, the consume half) — the SINGLE deterministic apply order shared VERBATIM by the client
    /// player path, the spectator path, and the FR-39 golden. Decodes a <see cref="MergedTickPacket"/> and
    /// applies its sub-bundles in WIRE ORDER (ascending faction id, which the builder guarantees), each order via
    /// the existing <see cref="OrderApplier.Apply"/> — so orders and DSL-event orders inside a bundle keep the
    /// exact per-faction sent order the pre-rewrite direct-apply path used. Godot-free (under
    /// <c>src/Multiplayer/Server/**</c>), so the golden exercises this real code, not a duplicate.
    ///
    /// The presentation/exec-tick delegates mirror <see cref="OrderApplier.Apply"/>'s optional tail: the client
    /// forwards its live hooks (path requests / Buildings / Items / Research / DslEventSink); the golden and
    /// spectator pass null (a null hook makes the corresponding command a deterministic no-op, exactly as in the
    /// existing live/replay parity).
    /// </summary>
    public static class MergedTickApplier
    {
        /// <summary>
        /// Allocating convenience wrapper over the pooled core below: decodes into three FRESH scratch arrays per
        /// call (thread-safe by construction — no hidden shared scratch). Fine for tests and cold one-shot paths;
        /// the per-tick production path (<c>LockstepManager.ApplyMerged</c>, once per tick per client/spectator at
        /// 30 tps) passes its own preallocated scratch to the pooled overload instead so the determinism-critical
        /// apply path never allocates (DW-391).
        /// </summary>
        public static void Apply(byte[] merged, int len, EntityWorld world,
            Action<int, float, float>? onRequestPath = null,
            Action<int, float, float>? onRequestAttackMove = null,
            Action<int>? onCancelPath = null,
            BuildingSystem? buildings = null,
            CombatEventQueue? events = null,
            ItemSystem? items = null,
            ResearchSystem? research = null,
            Func<int, int, int, int, bool>? dslSink = null,
            Action<Faction, UnitOrder[], int, int>? onSubBundle = null,
            WinStateStore? winState = null)
        {
            Apply(merged, len, world,
                new Faction[MergedTickPacket.MERGED_MAX_SUBBUNDLES],
                new int[MergedTickPacket.MERGED_MAX_SUBBUNDLES],
                new UnitOrder[MergedTickPacket.MERGED_MAX_SUBBUNDLES * TickCommandPacket.MAX_ORDERS],
                onRequestPath, onRequestAttackMove, onCancelPath,
                buildings, events, items, research, dslSink, onSubBundle, winState);
        }

        /// <summary>
        /// Decode <paramref name="merged"/> and apply every sub-bundle's orders per faction ascending — the pooled
        /// core (DW-391): the three scratch arrays are genuinely CALLER-OWNED (preallocate once, pass them every
        /// tick) so the per-tick apply path performs ZERO heap allocation, matching <see cref="MergedTickBuilder"/>'s
        /// preallocation discipline. Dirty reuse is safe and deterministic: <see cref="MergedTickPacket.TryRead"/>
        /// overwrites every index this method subsequently reads (factions/orderCounts for all b &lt; subBundleCount,
        /// orders for i &lt; count per bundle), and nothing beyond those ranges is ever read — prior-tick residue
        /// cannot leak. A malformed or empty (e.g. a pre-seeded bootstrap-gap) packet decodes to nothing and is a
        /// deterministic no-op. <paramref name="onSubBundle"/> is an optional per-sub-bundle hook
        /// (faction, ordersFlat, baseIdx, count) invoked BEFORE that bundle is applied — the client uses it to feed
        /// the replay recorder from the single authoritative command stream (it receives the caller's own
        /// <paramref name="scratchOrdersFlat"/>); the golden/spectator pass null.
        /// </summary>
        /// <param name="scratchFactions">Caller-owned; at least <see cref="MergedTickPacket.MERGED_MAX_SUBBUNDLES"/> entries.</param>
        /// <param name="scratchOrderCounts">Caller-owned; at least <see cref="MergedTickPacket.MERGED_MAX_SUBBUNDLES"/> entries.</param>
        /// <param name="scratchOrdersFlat">Caller-owned; at least <see cref="MergedTickPacket.MERGED_MAX_SUBBUNDLES"/> * <see cref="TickCommandPacket.MAX_ORDERS"/> entries.</param>
        public static void Apply(byte[] merged, int len, EntityWorld world,
            Faction[] scratchFactions, int[] scratchOrderCounts, UnitOrder[] scratchOrdersFlat,
            Action<int, float, float>? onRequestPath = null,
            Action<int, float, float>? onRequestAttackMove = null,
            Action<int>? onCancelPath = null,
            BuildingSystem? buildings = null,
            CombatEventQueue? events = null,
            ItemSystem? items = null,
            ResearchSystem? research = null,
            Func<int, int, int, int, bool>? dslSink = null,
            Action<Faction, UnitOrder[], int, int>? onSubBundle = null,
            WinStateStore? winState = null)
        {
            // Undersized scratch would IndexOutOfRange mid-decode (or silently work until the first full packet);
            // fail loudly at the call site instead — the MergedTickBuilder ctor-validation precedent.
            if (scratchFactions == null || scratchFactions.Length < MergedTickPacket.MERGED_MAX_SUBBUNDLES)
                throw new ArgumentException(
                    $"scratchFactions must have at least {MergedTickPacket.MERGED_MAX_SUBBUNDLES} entries.", nameof(scratchFactions));
            if (scratchOrderCounts == null || scratchOrderCounts.Length < MergedTickPacket.MERGED_MAX_SUBBUNDLES)
                throw new ArgumentException(
                    $"scratchOrderCounts must have at least {MergedTickPacket.MERGED_MAX_SUBBUNDLES} entries.", nameof(scratchOrderCounts));
            if (scratchOrdersFlat == null || scratchOrdersFlat.Length < MergedTickPacket.MERGED_MAX_SUBBUNDLES * TickCommandPacket.MAX_ORDERS)
                throw new ArgumentException(
                    $"scratchOrdersFlat must have at least {MergedTickPacket.MERGED_MAX_SUBBUNDLES * TickCommandPacket.MAX_ORDERS} entries.", nameof(scratchOrdersFlat));

            if (!MergedTickPacket.TryRead(merged, len, out _, scratchFactions, scratchOrderCounts, scratchOrdersFlat, out int subBundleCount))
                return; // malformed / empty / ceiling breach → deterministic no-op

            for (int b = 0; b < subBundleCount; b++)
            {
                Faction faction = scratchFactions[b];
                int count       = scratchOrderCounts[b];
                int baseIdx     = b * TickCommandPacket.MAX_ORDERS;

                onSubBundle?.Invoke(faction, scratchOrdersFlat, baseIdx, count);

                for (int i = 0; i < count; i++)
                    OrderApplier.Apply(world, in scratchOrdersFlat[baseIdx + i], faction,
                        onRequestPath, onRequestAttackMove, onCancelPath,
                        buildings, events, items, research, dslSink, winState);
            }
        }
    }
}
