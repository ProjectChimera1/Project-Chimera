#nullable enable
using System.Collections.Generic;
using ProjectChimera.Core;

namespace ProjectChimera.UI
{
    /// <summary>
    /// Story 11.4 (FR-74) — the under-attack alert throttle policy. Quantizes a world position to a coarse region
    /// cell (<see cref="AlertRegionCellSize"/>) and suppresses a repeat alert for that same cell within a named time
    /// window (<see cref="AlertRegionWindowSec"/>), so a sustained raid on one region is ONE alert stream, not spam.
    ///
    /// Godot-free (System.* + Core.Fixed only) so it compiles into the Tier-1 test assembly and its policy is unit-
    /// testable directly (the 11.3 persistence-core precedent). Purely presentation — it holds no sim state and never
    /// enters any determinism hash.
    /// </summary>
    public sealed class UnderAttackThrottle
    {
        /// <summary>World units per coarse region cell — a hit anywhere in the same cell within the window coalesces.</summary>
        public const double AlertRegionCellSize = 24.0;

        /// <summary>Seconds a region cell stays suppressed after an alert fires for it.</summary>
        public const double AlertRegionWindowSec = 8.0;

        private readonly double _cellSize;
        private readonly double _windowSec;

        // Last alert wall-clock (seconds) per region cell key. Presentation-only; grows with distinct raided regions
        // within a match, pruned lazily on query (an entry older than the window is overwritten, never leaked forever).
        private readonly Dictionary<long, double> _lastAlertSec = new();

        public UnderAttackThrottle(double cellSize = AlertRegionCellSize, double windowSec = AlertRegionWindowSec)
        {
            _cellSize  = cellSize > 0.0 ? cellSize : AlertRegionCellSize;
            _windowSec = windowSec >= 0.0 ? windowSec : AlertRegionWindowSec;
        }

        /// <summary>Should an under-attack alert fire for a hit at <paramref name="pos"/> at <paramref name="nowSec"/>?
        /// True the first time a region cell is hit and again once the window has elapsed; false for a repeat inside the
        /// window. A true result RECORDS the alert (so the immediately-following hit in the same cell is suppressed).</summary>
        public bool ShouldAlert(FixedVec3 pos, double nowSec) => ShouldAlert(pos.X.ToFloat(), pos.Z.ToFloat(), nowSec);

        /// <summary>Float-position overload of <see cref="ShouldAlert(FixedVec3,double)"/> (the presentation bridge
        /// already has a Godot Vector3 in hand).</summary>
        public bool ShouldAlert(float worldX, float worldZ, double nowSec)
        {
            long key = CellKey(worldX, worldZ);
            if (_lastAlertSec.TryGetValue(key, out double last) && nowSec - last < _windowSec)
                return false; // same region within the window → suppressed (one alert stream)
            _lastAlertSec[key] = nowSec;
            return true;
        }

        /// <summary>Quantize a world XZ position to a signed cell key (cellX in the high 32 bits, cellZ in the low).</summary>
        private long CellKey(float worldX, float worldZ)
        {
            int cx = (int)System.Math.Floor(worldX / _cellSize);
            int cz = (int)System.Math.Floor(worldZ / _cellSize);
            return ((long)cx << 32) | (uint)cz;
        }

        /// <summary>Forget every recorded region (e.g. on a match reset), so the next hit alerts immediately.</summary>
        public void Clear() => _lastAlertSec.Clear();
    }
}
