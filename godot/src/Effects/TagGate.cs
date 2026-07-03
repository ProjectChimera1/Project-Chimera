#nullable enable
using ProjectChimera.Core;

namespace ProjectChimera.Effects
{
    /// <summary>
    /// The single <see cref="UnitTag"/>-intersection predicate shared by BOTH tag-consumption sites (Story 2.11,
    /// D-4): the area path (<c>SearchArea.RequireTag</c>, evaluated in <see cref="TargetMatcher"/>) and the
    /// single-target path (<c>LeafEffect.RequireTag</c>, gated once in <see cref="EffectExecutor"/>). Single-sourcing
    /// the intersection semantics here means the two sites can NEVER disagree on what "carries the required tag" means
    /// (AC4.4). Pure integer bit-AND — no <c>Fixed</c>/float/RNG/wall-clock/allocation — safe inside the deterministic
    /// tick, and a no-op (returns true immediately) when nothing is required, so every pre-2.11 effect is byte-identical.
    /// </summary>
    internal static class TagGate
    {
        /// <summary>
        /// Intersection semantics (AC3.2/AC4.4): true when no tag is required (<see cref="UnitTag.None"/> → back-compat
        /// match-all), OR the target's <paramref name="have"/> shares AT LEAST ONE bit with <paramref name="requireTag"/>.
        /// The single source of the "has the tag" decision — both consumption sites route through it.
        /// </summary>
        internal static bool Intersects(UnitTag have, UnitTag requireTag)
            => requireTag == UnitTag.None || (have & requireTag) != UnitTag.None;

        /// <summary>
        /// Bounds/alive-safe single-target gate (the <see cref="EffectExecutor"/> leaf path). True when no tag is
        /// required; otherwise the target must be alive (a dead/out-of-bounds id fails the gate — crash-safe via the
        /// fully-bounds-checked <see cref="EntityWorld.IsAlive"/>) AND its <c>TagsOf</c> must intersect the required
        /// set. A dead/invalid target no-ops in every leaf's own <c>Apply</c> anyway, so failing the gate there is
        /// behaviour-preserving; this just moves the no-op one step earlier and keeps the <c>TagsOf[]</c> read safe.
        /// </summary>
        internal static bool Passes(EntityWorld world, int targetId, UnitTag requireTag)
        {
            if (requireTag == UnitTag.None) return true;
            if (!world.IsAlive(targetId)) return false;
            return Intersects(world.TagsOf[targetId], requireTag);
        }
    }
}
