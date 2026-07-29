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
        /// Decode <paramref name="merged"/> and apply every sub-bundle's orders per faction ascending. A malformed
        /// or empty (e.g. a pre-seeded bootstrap-gap) packet decodes to nothing and is a deterministic no-op.
        /// <paramref name="onSubBundle"/> is an optional per-sub-bundle hook (faction, ordersFlat, baseIdx, count)
        /// invoked BEFORE that bundle is applied — the client uses it to feed the replay recorder from the single
        /// authoritative command stream; the golden/spectator pass null.
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
            // Caller-owned scratch (single apply per tick — a few KB, not a hot per-entity path).
            var factions    = new Faction[MergedTickPacket.MERGED_MAX_SUBBUNDLES];
            var orderCounts = new int[MergedTickPacket.MERGED_MAX_SUBBUNDLES];
            var ordersFlat  = new UnitOrder[MergedTickPacket.MERGED_MAX_SUBBUNDLES * TickCommandPacket.MAX_ORDERS];

            if (!MergedTickPacket.TryRead(merged, len, out _, factions, orderCounts, ordersFlat, out int subBundleCount))
                return; // malformed / empty / ceiling breach → deterministic no-op

            for (int b = 0; b < subBundleCount; b++)
            {
                Faction faction = factions[b];
                int count       = orderCounts[b];
                int baseIdx     = b * TickCommandPacket.MAX_ORDERS;

                onSubBundle?.Invoke(faction, ordersFlat, baseIdx, count);

                for (int i = 0; i < count; i++)
                    OrderApplier.Apply(world, in ordersFlat[baseIdx + i], faction,
                        onRequestPath, onRequestAttackMove, onCancelPath,
                        buildings, events, items, research, dslSink, winState);
            }
        }
    }
}
