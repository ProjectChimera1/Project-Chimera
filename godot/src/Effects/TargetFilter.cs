#nullable enable
using System;

namespace ProjectChimera.Effects
{
    /// <summary>
    /// An OR-able set of target-selection predicates used by <see cref="SearchAreaEffect"/> to decide which
    /// entities its child fans out to. The allegiance bits (Self/Ally/Enemy/Neutral) are an OR-group: a
    /// candidate is selected if it matches ANY set allegiance bit. <see cref="Alive"/> is an AND-constraint.
    ///
    /// The allegiance bits + Alive are joined as described above. The
    /// <see cref="Air"/>/<see cref="Ground"/>/<see cref="Structure"/> domain bits are an AND-constraint evaluated
    /// since Story 2.9a (via the shared <c>DomainClassifier</c>): if any is set, the candidate's domain must be among
    /// them; if none is set, every domain is eligible (so pre-2.9a filters are unchanged).
    /// </summary>
    [Flags]
    public enum TargetFilter : byte
    {
        /// <summary>No predicate. With no allegiance bit set, every in-radius entity is allegiance-eligible.</summary>
        None = 0,

        /// <summary>The caster itself.</summary>
        Self = 1 << 0,
        /// <summary>Same faction as the caster, excluding the caster.</summary>
        Ally = 1 << 1,
        /// <summary>A different, non-Neutral faction than the caster.</summary>
        Enemy = 1 << 2,
        /// <summary>The Neutral faction.</summary>
        Neutral = 1 << 3,

        /// <summary>AND-constraint: the candidate must be alive (redundant with the spatial-hash snapshot, but explicit).</summary>
        Alive = 1 << 4,

        // ── Domain AND-constraint (evaluated since Story 2.9a) ──
        /// <summary>Air units (candidate <c>CategoryOf == Air</c>).</summary>
        Air = 1 << 5,
        /// <summary>Ground units (candidate <c>CategoryOf</c> is Worker/Melee/Ranged/Siege).</summary>
        Ground = 1 << 6,
        /// <summary>Structures/buildings (candidate <c>CategoryOf == Structure</c>).</summary>
        Structure = 1 << 7,
    }
}
