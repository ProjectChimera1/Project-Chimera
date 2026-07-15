#nullable enable
using System;

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// Story 6.7 — the single Godot-free source of truth for the supported set of authored map sizes.
    ///
    /// A "map size" here is the authored PLAYABLE HALF-EXTENT (<see cref="ScenarioData.MapBounds"/>), NOT a variable
    /// grid dimension. The fog (<c>FogOfWarSystem</c>), flow-field (<c>FlowField.WORLD_HALF_INT</c> = 128), pathability
    /// (<c>PathabilityGrid</c>), and spatial-hash (<c>SpatialHash</c>, coverage ±160) grids are FIXED at ±128/±160 and
    /// are never re-parameterized by this story (that is the epic's escalated determinism-critical slice). Every value
    /// here is therefore chosen ≤ <c>FlowField.WORLD_HALF_INT</c> (128) so an authored position at the map edge always
    /// falls safely inside the fixed grids' coverage — giving observably-different, checksum-stable maps (different
    /// camera / NavMesh / placement bounds) with zero determinism risk. The <c>GridDimensionConsistencyTests</c> guard
    /// asserts this ≤-128 invariant so a future size that exceeds coverage fails the build rather than silently
    /// placing units out of bounds.
    ///
    /// Consumed by the New-Map picker, <see cref="ScenarioData.CreateBlank"/>, and the guard test. Pure C# —
    /// no <c>using Godot;</c>.
    /// </summary>
    public enum MapSize
    {
        /// <summary>Small skirmish map — 80-unit half-extent (160×160 playable).</summary>
        Small,
        /// <summary>Medium map — 120-unit half-extent (240×240 playable). Matches the historical default
        /// <see cref="ScenarioData.MapBounds"/> of 120, so an existing map's implicit size is "Medium".</summary>
        Medium,
        /// <summary>Large map — 128-unit half-extent (256×256 playable), the maximum that fits the fixed ±128 grids.</summary>
        Large,
    }

    /// <summary>
    /// Static helper over <see cref="MapSize"/>: the ordered supported set, size↔bounds mapping, and display labels.
    /// The one place a size's half-extent is defined, so the picker / factory / validator / guard test never drift.
    /// </summary>
    public static class MapSizes
    {
        /// <summary>The maximum half-extent any supported size may use — the fixed flow/fog grid coverage
        /// (<c>FlowField.WORLD_HALF_INT</c> = 128). Duplicated as a literal here (rather than referencing the
        /// Navigation constant) to keep this file dependency-light; the guard test asserts they agree.</summary>
        public const float MaxHalfExtent = 128f;

        /// <summary>Every supported size, in ascending playable-extent order. The single enumeration source.</summary>
        public static readonly MapSize[] All = { MapSize.Small, MapSize.Medium, MapSize.Large };

        /// <summary>The authored <see cref="ScenarioData.MapBounds"/> half-extent for a supported size. Every value
        /// is ≤ <see cref="MaxHalfExtent"/> so it sits inside the fixed grids' coverage.</summary>
        public static float ToBounds(MapSize size) => size switch
        {
            MapSize.Small  => 80f,
            MapSize.Medium => 120f,
            MapSize.Large  => 128f,
            _              => throw new ArgumentOutOfRangeException(nameof(size), size, "Unsupported map size."),
        };

        /// <summary>The nearest supported size whose bounds match <paramref name="bounds"/>. An exact-match lookup
        /// used to label an already-authored map; returns <see cref="MapSize.Medium"/> (the historical default) for a
        /// bounds value that is not one of the supported extents (a legacy/hand-authored map keeps loading — sizing is
        /// authoring metadata, never a load gate).</summary>
        public static MapSize FromBounds(float bounds)
        {
            foreach (MapSize s in All)
                if (ToBounds(s) == bounds) return s;
            return MapSize.Medium;
        }

        /// <summary>True when <paramref name="bounds"/> is exactly one of the supported extents.</summary>
        public static bool IsSupportedBounds(float bounds)
        {
            foreach (MapSize s in All)
                if (ToBounds(s) == bounds) return true;
            return false;
        }

        /// <summary>A human-readable label for the picker, e.g. "Medium (240×240)".</summary>
        public static string Label(MapSize size)
        {
            int span = (int)(ToBounds(size) * 2f);
            return $"{size} ({span}×{span})";
        }
    }
}
