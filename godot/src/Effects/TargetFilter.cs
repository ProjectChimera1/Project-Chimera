#nullable enable
using System;

namespace ProjectChimera.Effects
{
    /// <summary>
    /// An OR-able set of target-selection predicates used by <see cref="SearchAreaEffect"/> to decide which
    /// entities its child fans out to. The allegiance bits (Self/Ally/Enemy/Neutral) are an OR-group: a
    /// candidate is selected if it matches ANY set allegiance bit. <see cref="Alive"/> is an AND-constraint.
    ///
    /// 2.1 evaluates only Self / Ally / Enemy / Neutral / Alive (faction comparison + IsAlive). The
    /// <see cref="Air"/>/<see cref="Ground"/>/<see cref="Structure"/> bits are RESERVED — their evaluation
    /// (which needs per-entity movement/structure classification) lands in Story 2.9a. Do not author them yet.
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

        // ── RESERVED for Story 2.9a (do not evaluate in 2.1) ──
        /// <summary>RESERVED (2.9a): air units.</summary>
        Air = 1 << 5,
        /// <summary>RESERVED (2.9a): ground units.</summary>
        Ground = 1 << 6,
        /// <summary>RESERVED (2.9a): structures/buildings.</summary>
        Structure = 1 << 7,
    }
}
