#nullable enable
namespace ProjectChimera.Core
{
    /// <summary>
    /// DW-54: one entity's RESOLVED ability wiring, captured by value at <see cref="EntityWorld.SnapshotUnit"/> time —
    /// the castable registry indices plus the three passive slots (aura / on-hit / while-alive).
    ///
    /// <para><b>Why this exists.</b> Story 3.17's <see cref="EntityWorld.RestoreUnit"/> re-derives a deleted unit's
    /// authored fields by routing its pinned <see cref="Definitions.UnitDefinition"/> back through the single
    /// <see cref="EntityWorld.ApplyUnitDefinition"/> mapper — deliberately, so a future def-derived field is restored
    /// for free. That is correct for authored VALUES (hp, armor, tags…), which live on the def and are what the
    /// creator authored. It is NOT correct for the ability slots: those are not authored data but
    /// <c>AbilityRegistry</c> INDICES back-filled once at scenario link by
    /// <see cref="Definitions.UnitDefinition.ResolveAbilities"/>, and the def object a snapshot pins is a LIVE,
    /// SHARED, MUTABLE roster entry — the Unit Card editor writes <c>def.Abilities</c> in place and the balance-apply
    /// path swaps a whole <see cref="Definitions.UnitDefinition"/> into <c>faction.Units[i]</c>. An undo landing after
    /// any of that would re-derive from a def whose resolution has since moved, gone stale, or been reset to empty
    /// (a re-resolve against a different/absent registry clears every index) — the deleted unit would come back with
    /// different abilities, or silently with none at all.</para>
    ///
    /// <para>So the RESOLUTION is pinned at capture time and replayed verbatim; every other def-derived field keeps
    /// re-deriving from the def exactly as Story 3.17 intended. Same posture the SP save/load overlay already takes
    /// (<c>SaveGameState</c> persists the resolved indices rather than trusting a def to still resolve the same way).</para>
    ///
    /// <para>Immutable + Godot-free, so an undo entry can hold it for a whole editor session and a Tier-1 xUnit test
    /// can build one. Reference type (not a struct) so the common no-abilities case costs a single shared
    /// <see cref="None"/> reference and no per-snapshot allocation.</para>
    /// </summary>
    public sealed class PinnedAbilityWiring
    {
        /// <summary>The pin for a unit that carries no castable ability and no passive — the overwhelmingly common
        /// case. Shared, so capturing such a unit allocates nothing. Applying it writes count 0 and −1 into all three
        /// passive slots, which is exactly what an unabilitied def would produce.</summary>
        public static readonly PinnedAbilityWiring None = new PinnedAbilityWiring(System.Array.Empty<int>(), -1, -1, -1);

        /// <summary>The castable slots' registry indices, in slot order. Private + copied at construction so a pin
        /// held across an undo history can never be mutated by whoever supplied the array.</summary>
        private readonly int[] _active;

        /// <summary>Capture a resolution. <paramref name="activeIndices"/> is COPIED (never aliased); a null array is
        /// treated as empty. The three passive indices use −1 for "none", matching the SoA convention.</summary>
        public PinnedAbilityWiring(int[]? activeIndices, int auraIndex, int onHitIndex, int selfPassiveIndex)
        {
            if (activeIndices is null || activeIndices.Length == 0)
            {
                _active = System.Array.Empty<int>();
            }
            else
            {
                _active = new int[activeIndices.Length];
                System.Array.Copy(activeIndices, _active, activeIndices.Length);
            }
            AuraIndex        = auraIndex;
            OnHitIndex       = onHitIndex;
            SelfPassiveIndex = selfPassiveIndex;
        }

        /// <summary>Number of populated castable slots (already capped to <see cref="EntityWorld.MAX_ABILITIES_PER_UNIT"/>
        /// by the capture, which reads the SoA the cap was applied to).</summary>
        public int Count => _active.Length;

        /// <summary>The registry index in castable slot <paramref name="slot"/> (0-based, &lt; <see cref="Count"/>).</summary>
        public int ActiveAt(int slot) => _active[slot];

        /// <summary>Registry index of the while-alive AURA passive (−1 = none).</summary>
        public int AuraIndex { get; }

        /// <summary>Registry index of the ON-HIT rider passive (−1 = none).</summary>
        public int OnHitIndex { get; }

        /// <summary>Registry index of the WHILE-ALIVE self-passive (−1 = none).</summary>
        public int SelfPassiveIndex { get; }
    }
}
