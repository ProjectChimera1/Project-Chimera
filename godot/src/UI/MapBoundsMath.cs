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
    }
}
