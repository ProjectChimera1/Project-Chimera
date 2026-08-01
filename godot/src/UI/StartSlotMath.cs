#nullable enable
using System;
using System.Collections.Generic;
using ProjectChimera.Core.Definitions;

namespace ProjectChimera.UI
{
    /// <summary>
    /// DW-163 — pure, Godot-free arithmetic over a scenario's declared start-position slot SET (by
    /// <see cref="ScenarioPlayerSlot.Slot"/> VALUE, not array index/count). Extracted from the Godot-coupled
    /// <c>EntityPlacer.RefreshSubRow</c> so the non-contiguous slot logic (a validator-legal <c>{0,3}</c> set) is
    /// unit-testable without running Godot. The scenario WRITE path (<c>ScenarioData.UpsertStartSlot</c>/
    /// <c>RemoveStartSlot</c>) is already keyed by slot value; these helpers only drive the count-derived DISPLAY,
    /// marker sizing, and "+"/"−" target selection that were wrong for a non-contiguous set.
    /// </summary>
    public static class StartSlotMath
    {
        /// <summary>The sorted, ascending set of declared slot VALUES. A null/empty slot array yields an empty set.</summary>
        public static int[] DeclaredSlots(ScenarioPlayerSlot[]? slots)
        {
            if (slots == null || slots.Length == 0) return Array.Empty<int>();
            var list = new List<int>(slots.Length);
            foreach (var s in slots) list.Add(s.Slot);
            list.Sort();
            return list.ToArray();
        }

        /// <summary>The declared slots restricted to the editor's supported range <c>[0, ceiling)</c>. The
        /// creation-suite start-slot picker mirrors only <c>ceiling</c> economy rows (<c>_slotStartOre</c>/
        /// <c>_slotStartCrystal</c> are length <c>ceiling</c>), so a validator-legal but out-of-range slot — e.g. a
        /// 5–8-player <c>{5,6}</c> set (Story 9.2, not yet supported by this 4-slot picker) — must be DROPPED, not
        /// surfaced as a P-toggle or used to index the length-<c>ceiling</c> arrays. This restores the pre-DW-163
        /// count-clamped code's graceful degradation (it never surfaced a slot &gt;= ceiling) without its count bug.</summary>
        public static int[] DeclaredBelowCeiling(ScenarioPlayerSlot[]? slots, int ceiling)
        {
            var all = DeclaredSlots(slots);
            var list = new List<int>(all.Length);
            foreach (var s in all) if (s >= 0 && s < ceiling) list.Add(s);
            return list.ToArray();
        }

        /// <summary>The lowest slot value in <c>[0, ceiling)</c> that is NOT in <paramref name="declared"/> — the
        /// target the "+" button arms (fills a gap first, e.g. <c>{0,3}</c> → 1). Returns -1 when every slot below
        /// <paramref name="ceiling"/> is already declared (so "+" disables at a full set).</summary>
        public static int LowestUndeclared(int[] declared, int ceiling)
        {
            for (int c = 0; c < ceiling; c++)
                if (Array.IndexOf(declared, c) < 0) return c;
            return -1;
        }

        /// <summary>The highest declared slot value — the target the "−" button removes (e.g. <c>{0,3}</c> → 3).
        /// Returns -1 for an empty set.</summary>
        public static int MaxDeclared(int[] declared)
        {
            int m = -1;
            foreach (var d in declared) if (d > m) m = d;
            return m;
        }
    }
}
