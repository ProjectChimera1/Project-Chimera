#nullable enable
using System;
using ProjectChimera.Core;              // BuildingType, TechTreeChecker
using ProjectChimera.Core.Definitions;  // BuildingDefinition

namespace ProjectChimera.UI
{
    /// <summary>
    /// DW-169 — pure, Godot-free nav-footprint resolution policy for placed buildings. Extracted from the
    /// Godot-coupled <c>NavObstacleManager</c> (which owned a private copy of the built-in size table and returned a
    /// fixed 5×3×5 box for EVERY non-enum id) so the policy is unit-testable without running Godot, and so a large
    /// authored building no longer renders at mesh size while nav-blocking a fixed small box.
    ///
    /// Resolution order (first hit wins):
    ///   1. The def's authored <c>nav_footprint</c> (<see cref="BuildingDefinition.TryGetNavFootprint"/>) — the
    ///      explicit override, honored for built-ins and customs alike.
    ///   2. A built-in enum id (<see cref="TechTreeChecker.BuildingTypeFromId"/>) → its legacy
    ///      <see cref="BUILT_IN_FOOTPRINT"/> table entry — byte-identical to the pre-DW-169 behavior for every
    ///      un-authored built-in, so existing maps bake the exact navmesh they always did. The mesh source is
    ///      deliberately NOT consulted for built-ins (changing legacy nav behavior needs its own in-engine pass).
    ///   3. A Custom/authored id → the mesh-AABB-derived size supplied by <paramref name="meshSize"/> (already
    ///      MeshScale-scaled by the caller). The Func is invoked lazily — only on this branch — so built-ins never
    ///      pay a GLB load.
    ///   4. The guarded <see cref="CUSTOM_FOOTPRINT"/> default (no def / no mesh / degenerate AABB) — the same
    ///      5×3×5 the old code used unconditionally, so a mesh-less custom building behaves exactly as before.
    ///
    /// Lives under <c>src/UI</c> (NOT globbed by SimSources.props; single-file-included by
    /// ProjectChimera.Sim.Tests — the StartSlotMath/MapBoundsMath precedent). Presentation-side floats are fine
    /// here: the navmesh carve size is never folded into SimChecksum / CanonicalModelHash / StartStateHash.
    /// </summary>
    public static class BuildingNavFootprint
    {
        /// <summary>A plain 3-component size (world units) — deliberately not Godot's Vector3 so the policy and its
        /// tests stay Godot-free. The consuming presentation layer converts.</summary>
        public readonly struct Size3
        {
            public readonly float X;
            public readonly float Y;
            public readonly float Z;
            public Size3(float x, float y, float z) { X = x; Y = y; Z = z; }
        }

        /// <summary>
        /// The canonical built-in footprint table, indexed by <see cref="BuildingType"/> value — MOVED here from
        /// <c>NavObstacleManager.TYPE_SIZE</c> (values byte-identical) so the nav policy owns a single copy. Must
        /// match <c>BuildingBridge.TYPE_FALLBACK</c> exactly (the visual fallback boxes) — the long-standing
        /// invariant that the nav obstacle and the placeholder visual agree.
        /// </summary>
        public static readonly Size3[] BUILT_IN_FOOTPRINT =
        {
            new Size3(6f, 4f, 6f), // CommandCenter
            new Size3(5f, 3f, 5f), // Barracks
            new Size3(4f, 3f, 5f), // ArcheryRange
            new Size3(5f, 3f, 7f), // SiegeWorkshop
            new Size3(5f, 3f, 7f), // Aviary (Story 2.8)
        };

        /// <summary>Story 6.8 (kept as DW-169's last resort) — the guarded footprint for a Custom/authored building
        /// with no authored <c>nav_footprint</c> and no usable mesh AABB. Matches <c>BuildingBridge.CUSTOM_FALLBACK</c>
        /// (the missing-GLB placeholder box) so the visual and the nav obstacle agree in the fallback case too.</summary>
        public static readonly Size3 CUSTOM_FOOTPRINT = new Size3(5f, 3f, 5f);

        /// <summary>
        /// Resolve the nav-obstacle footprint for a placed building. <paramref name="definitionId"/> is the
        /// building's <c>BuildingStore.DefinitionId</c>; <paramref name="def"/> its resolved
        /// <see cref="BuildingDefinition"/> (null when no faction def carries the id); <paramref name="meshSize"/>
        /// lazily supplies the mesh-AABB-derived size (already MeshScale-scaled), invoked ONLY when a custom id has
        /// no authored footprint — return null for "no usable mesh" (missing GLB / placeholder box). See the class
        /// doc for the full resolution order.
        /// </summary>
        public static Size3 Resolve(string? definitionId, BuildingDefinition? def, Func<Size3?>? meshSize = null)
        {
            // 1. Authored override — built-ins and customs alike. TryGetNavFootprint is the shared validity rule
            //    (exactly 3 finite positive values), so a malformed authored value — already a located import-time
            //    error — falls through instead of producing a degenerate obstacle.
            if (def != null && def.TryGetNavFootprint(out float ax, out float ay, out float az))
                return new Size3(ax, ay, az);

            // 2. Built-in id → the legacy table, byte-identical to pre-DW-169 for un-authored built-ins.
            BuildingType? t = TechTreeChecker.BuildingTypeFromId(definitionId);
            if (t is BuildingType bt && (int)bt >= 0 && (int)bt < BUILT_IN_FOOTPRINT.Length)
                return BUILT_IN_FOOTPRINT[(int)bt];

            // 3. Custom id → mesh-AABB-derived size (lazy; a degenerate/non-finite size is rejected so a broken
            //    GLB can never bake a zero/NaN obstacle into the navmesh).
            Size3? m = meshSize?.Invoke();
            if (m is Size3 ms && IsUsable(in ms))
                return ms;

            // 4. Guarded default — exactly the old fixed CUSTOM_FOOTPRINT behavior.
            return CUSTOM_FOOTPRINT;
        }

        /// <summary>True when every component is finite and strictly positive — the gate a mesh-derived size must
        /// pass before it becomes a nav obstacle (a placeholder/degenerate AABB falls to <see cref="CUSTOM_FOOTPRINT"/>).</summary>
        public static bool IsUsable(in Size3 s) =>
            float.IsFinite(s.X) && s.X > 0f &&
            float.IsFinite(s.Y) && s.Y > 0f &&
            float.IsFinite(s.Z) && s.Z > 0f;
    }
}
