#nullable enable
using System.Collections.Generic;

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// Import-time gate for the authored sparse <see cref="UnitDefinition.Cost"/> map (Story 4.3). Runs over the
    /// SAME <see cref="FactionDefinition.LoadFromFile"/> aggregate <c>errors</c> channel that
    /// <see cref="BuildingDefinitionValidator"/>/<see cref="TechTreeValidator"/> already throw with — additive, not
    /// a replacement. Mirrors <see cref="TechTreeValidator"/>'s doc/shape: pure C#, no logging, no throw — the
    /// caller (<see cref="FactionDefinition.LoadFromFile"/>) decides to throw. List-all: every offending entry is
    /// reported, not just the first.
    ///
    /// Two checks per authored <c>Cost</c> entry, across every <see cref="FactionDefinition.Units"/> AND
    /// <see cref="FactionDefinition.Buildings"/> entry (a <see cref="BuildingDefinition"/> IS a
    /// <see cref="UnitDefinition"/>, so walking <c>Buildings</c> as its own list — not re-walking via <c>Units</c> —
    /// keeps the "building"/"unit" kind label in the located error accurate):
    /// 1. A key outside <c>{"ore","crystal"}</c> — the only resource ids <see cref="ResourceStore"/> has runtime
    ///    balance storage for today (see the spec's Design Notes on why this does NOT cross-reference a scenario's
    ///    declared <see cref="ScenarioData.Resources"/> registry).
    /// 2. A value <c>&lt; 0</c> or <c>&gt;= 32768</c> — mirrors <see cref="UnitDefinitionValidator"/>'s <c>CheckCost</c>
    ///    range (a negative cost ADDS that resource each spend; 32768 is the 16.16 <see cref="Fixed"/> ceiling).
    ///
    /// A null <c>Cost</c> (the common case — no authored <c>cost</c> key) is skipped entirely; unauthored units/
    /// buildings keep loading exactly as before.
    /// </summary>
    public static class ResourceCostValidator
    {
        /// <summary>The 16.16 representable ceiling (mirrors <see cref="UnitDefinitionValidator"/>'s <c>Range</c> /
        /// <see cref="ScenarioValidator"/>'s <c>Range</c>).</summary>
        private const int Range = 32768;

        /// <summary>The only resource ids with runtime <see cref="ResourceStore"/> backing today.</summary>
        private static readonly HashSet<string> _knownResourceIds = new() { "ore", "crystal" };

        /// <summary>
        /// Validate every authored <see cref="UnitDefinition.Cost"/> map in <paramref name="def"/>'s <c>Units</c>
        /// and <c>Buildings</c> lists. Returns every located unknown-resource-id and out-of-range-amount error
        /// (list-all — every offending entry is reported). Empty when every authored cost map is well-formed (or
        /// none is authored at all).
        /// </summary>
        public static IReadOnlyList<string> Validate(FactionDefinition def)
        {
            var errors = new List<string>();
            if (def == null) return errors;

            foreach (UnitDefinition u in def.Units)
                ValidateEntry(errors, "unit", u.Id ?? "", u.Cost);

            foreach (BuildingDefinition b in def.Buildings)
                ValidateEntry(errors, "building", b.Id ?? "", b.Cost);

            return errors;
        }

        private static void ValidateEntry(List<string> errors, string kind, string id, Dictionary<string, int>? cost)
        {
            if (cost == null) return; // unauthored — nothing to validate (legacy cost_ore/cost_crystal path)

            foreach (var (key, value) in cost)
            {
                if (!_knownResourceIds.Contains(key))
                {
                    errors.Add($"{kind} '{id}'.cost: references unknown resource id '{key}' " +
                               "(no runtime resource registered for it yet).");
                    continue; // an unknown key's value is meaningless to range-check
                }

                if (value < 0)
                    errors.Add($"{kind} '{id}'.cost['{key}']={value} must be >= 0 " +
                               "(a negative cost ADDS that resource each time it is spent).");
                else if (value >= Range)
                    errors.Add($"{kind} '{id}'.cost['{key}']={value} exceeds the maximum resource cost ({Range}).");
            }
        }
    }
}
