#nullable enable
using System.Runtime.CompilerServices;

namespace ProjectChimera.Core
{
    /// <summary>
    /// Story 6.4 — a Godot-free, <see cref="Fixed"/>-only axis-aligned rectangle in the world XZ plane, the
    /// deterministic geometry core for named map regions. Authored as <c>float MinX/MinZ/MaxX/MaxZ</c> on
    /// <c>ScenarioRegion</c> and resolved float→<see cref="Fixed"/> exactly once at <c>ScenarioApplier</c> (the
    /// sanctioned single conversion boundary) into this value type, held by a <see cref="RegionStore"/>.
    ///
    /// <para>Containment is a pure <see cref="Fixed"/> point-in-rect test with <b>INCLUSIVE</b> edges — a point
    /// exactly on <see cref="MinX"/>/<see cref="MaxX"/>/<see cref="MinZ"/>/<see cref="MaxZ"/> is inside. No
    /// <c>float</c>/<c>double</c>/<c>Mathf</c>/<c>System.Random</c> ever appears here, so it is safe on any sim
    /// path and Tier-1 testable without Godot.</para>
    /// </summary>
    public readonly struct FixedRect
    {
        public readonly Fixed MinX;
        public readonly Fixed MinZ;
        public readonly Fixed MaxX;
        public readonly Fixed MaxZ;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public FixedRect(Fixed minX, Fixed minZ, Fixed maxX, Fixed maxZ)
        {
            MinX = minX;
            MinZ = minZ;
            MaxX = maxX;
            MaxZ = maxZ;
        }

        /// <summary>
        /// True when the point (<paramref name="x"/>, <paramref name="z"/>) lies inside the rectangle. Edges are
        /// INCLUSIVE: a coordinate exactly equal to a min or max bound counts as inside. A degenerate rect
        /// (min == max on an axis) still admits the boundary line/point — never throws, never OOB.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Contains(Fixed x, Fixed z) =>
            x >= MinX && x <= MaxX && z >= MinZ && z <= MaxZ;

        /// <summary>Point-in-rect over a <see cref="FixedVec3"/> position, testing its X/Z (Y is ignored — regions
        /// are a 2D map footprint). Inclusive edges, per <see cref="Contains(Fixed, Fixed)"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Contains(FixedVec3 p) => Contains(p.X, p.Z);
    }
}
