#nullable enable

namespace ProjectChimera.UI
{
    /// <summary>
    /// DW-159 — pure, Godot-free map-bounds predicate extracted from <c>EntityPlacer.WithinMapBounds</c> so the
    /// off-map paste/move guard is unit-testable without running Godot (mirrors the <see cref="StartSlotMath"/>
    /// extraction). <c>EntityPlacer.WithinMapBounds</c> delegates here so the behavior is byte-identical.
    /// </summary>
    public static class MapBoundsMath
    {
        /// <summary>True when world XZ (<paramref name="x"/>, <paramref name="z"/>) is inside the ±<paramref name="bounds"/>
        /// square. A null <paramref name="bounds"/> means no scenario is loaded (bounds unknown) ⇒ allow.</summary>
        public static bool Within(float x, float z, float? bounds)
        {
            if (bounds == null) return true;
            float b = bounds.Value;
            return x >= -b && x <= b && z >= -b && z <= b;
        }

        /// <summary>
        /// Story 15.2 (Route C, DW-160) — the PRESENTATION visual half-extent: the playable
        /// <paramref name="mapBounds"/> plus the non-playable <paramref name="borderExtent"/> (a negative border is
        /// clamped away, so it can only ever add). Camera pan and the fallback ground plane render across this extent
        /// while placement/AI/triggers stay bounded by <paramref name="mapBounds"/> alone. Pure/Godot-free so the
        /// INCLUSION formula is Tier-1 testable (its exclusion from every hash is pinned separately by
        /// <c>BorderExtentTests</c>); <c>ScenarioLoadPhase.VisualHalfExtentOf</c> delegates here.
        /// </summary>
        public static float VisualHalfExtent(float mapBounds, float borderExtent)
            => mapBounds + (borderExtent > 0f ? borderExtent : 0f);
    }
}
