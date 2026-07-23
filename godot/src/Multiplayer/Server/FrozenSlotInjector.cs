#nullable enable
using System;
using System.Collections.Generic;
using ProjectChimera.Core; // Faction, UnitOrder

namespace ProjectChimera.Multiplayer.Server
{
    /// <summary>
    /// Story 9.6 (the shared, Tier-1 merge-continuation core) — the empty-injection drain that keeps the merged
    /// fan-in flowing while a faction's slot is frozen. Static + Godot-free so BOTH the <c>DedicatedServer</c> node
    /// AND the FR-39 golden test (<c>MidMatchDropScenario</c>) exercise the identical production code (parity).
    ///
    /// <para><b>Drain the whole gap, not a future margin.</b> In lockstep the survivor's frontier PLATEAUS while it
    /// stalls (it only submits <c>execTick + delay</c> and stops advancing exec while blocked), so a
    /// <see cref="DelayController"/>-style "future applyAtTick + margin" would DEADLOCK — the frontier never reaches
    /// it. Injection therefore fills EVERY unemitted tick from <c>EmittedThrough + 1</c> up to the current
    /// <paramref name="frontier"/> each pump. Each successful <see cref="MergedTickBuilder.TryBuild"/> advances the
    /// emitted high-water, so the next tick in the ascending scan is always exactly one past it (comfortably inside
    /// the builder's accept window).</para>
    ///
    /// <para>Empty injection reuses <see cref="MergedTickBuilder.Submit"/> UNCHANGED — its idempotent-duplicate
    /// guard means a frozen slot's already-in-flight real command still WINS over a later injected empty (the slot's
    /// final pre-drop actions execute, then it goes idle). A tick whose fan-in is still incomplete (e.g. the
    /// survivor has not submitted it yet) simply does not build — no broadcast, no harm — and completes on a later
    /// pump once the survivor catches up.</para>
    /// </summary>
    public static class FrozenSlotInjector
    {
        // orderCount 0 → Write never indexes the array; a shared empty array avoids per-pump allocation.
        private static readonly UnitOrder[] EmptyOrders = Array.Empty<UnitOrder>();

        /// <summary>
        /// For each unemitted tick <c>t</c> in <c>(builder.EmittedThrough, frontier]</c> ascending: submit an EMPTY
        /// single-faction <see cref="TickCommandPacket"/> for every frozen slot (faction re-stamped from
        /// <paramref name="slotFaction"/>), then attempt <see cref="MergedTickBuilder.TryBuild"/> and, on success,
        /// broadcast the merged packet via <paramref name="broadcast"/>.
        /// </summary>
        /// <param name="builder">The authoritative per-tick fan-in (shared with the live command relay).</param>
        /// <param name="frozenSlots">Committed frozen slots (ascending). No-op when empty.</param>
        /// <param name="slotFaction">Slot → authoritative faction (for the re-stamped empty packet).</param>
        /// <param name="frontier">The current submission frontier (highest tick seen). The drain never keys past it.</param>
        /// <param name="scratch">Caller-owned buffer ≥ <see cref="TickCommandPacket.HEADER_BYTES"/> for the empty packet.</param>
        /// <param name="broadcast">Sink for a built merged packet: (buffer, length).</param>
        public static void Drain(MergedTickBuilder builder, IReadOnlyList<int> frozenSlots, Faction[] slotFaction,
                                 uint frontier, byte[] scratch, Action<byte[], int> broadcast)
        {
            if (builder == null || frozenSlots == null || frozenSlots.Count == 0) return;

            for (long t = builder.EmittedThrough + 1; t <= (long)frontier; t++)
            {
                uint tick = (uint)t;
                for (int k = 0; k < frozenSlots.Count; k++)
                {
                    int slot = frozenSlots[k];
                    int len = TickCommandPacket.Write(scratch, tick, slotFaction[slot], EmptyOrders, 0);
                    // Idempotent: if the frozen slot already fanned in a real command for this tick, Submit no-ops
                    // (returns false) and the real command wins — we do not care about the return value.
                    builder.Submit(slot, scratch, len, out _);
                }

                if (builder.TryBuild(tick, out byte[] merged, out int mergedLen))
                    broadcast(merged, mergedLen);
            }
        }
    }
}
