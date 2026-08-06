#nullable enable
using ProjectChimera.Core;

namespace ProjectChimera.Combat
{
    /// <summary>
    /// DW-766 — the END-OF-TICK <see cref="DeathFeed"/> drain, registered LAST in the canonical tick order (index [16],
    /// after <c>ScenarioDirector</c> at [15]).
    ///
    /// <para><b>Why it exists.</b> <see cref="DeathFeed"/>'s type doc and two <see cref="SimChecksum"/> comments all cite
    /// the feed being "provably drained within the tick" as the reason it is excluded from the checksum fold. That was
    /// false: <see cref="HeroXpSystem"/> drains and clears at index [9], but the feed has producers AFTER it —
    /// <c>ItemSystem</c> at [10] reaches <see cref="Effects.ModifierStore"/>.<c>Apply</c>, whose DW-325 ceiling-collapse
    /// kill pushes a record (DW-490 threaded the shared feed into the store), and <c>ScenarioDirector</c> at [15] holds
    /// the feed in the <c>EffectContext</c> its <c>run_effect</c> graphs execute against. Either one leaves
    /// <c>Count == 1</c> at the checksum boundary, with the residue's hero XP — folded <c>HeroStore.Xp</c>/<c>Level</c> —
    /// landing a tick late. This system makes the stated invariant TRUE rather than weakening the claim: it runs past the
    /// LAST producer and credits the residue in the SAME tick.</para>
    ///
    /// <para><b>Position is the contract.</b> Anything that can kill must be registered BEFORE this system.
    /// <c>SimulationLoop</c> asserts <c>DeathFeed.Count == 0</c> at the tick boundary (immediately after the whole system
    /// array, before the checksum), so a future producer registered PAST this drain fails loudly instead of silently
    /// re-opening the hole. <c>SystemOrderTest</c> pins the index.</para>
    ///
    /// <para><b>Cost.</b> A tick whose deaths were all recorded before index [9] — every recorded golden — hits
    /// <see cref="HeroXpSystem.DrainResidue"/>'s <c>Count == 0</c> early return and is a strict no-op, so this system
    /// adds no behaviour to the runs the goldens pin. Pure C#, <see cref="Fixed"/>-only, no Godot.</para>
    /// </summary>
    public sealed class DeathFeedDrainSystem : ISimSystem
    {
        private readonly HeroXpSystem _xp;

        /// <summary>Wire the drain to the XP runtime that owns the credit rule (never a second copy of it).</summary>
        public DeathFeedDrainSystem(HeroXpSystem xp) => _xp = xp;

        /// <summary>Credit and clear whatever the post-[9] producers pushed this tick. <paramref name="dt"/> is unused —
        /// the residue pass banks XP only; level/growth stay at <see cref="HeroXpSystem"/>'s own index (see
        /// <see cref="HeroXpSystem.DrainResidue"/> for why).</summary>
        public void Tick(EntityWorld world, Fixed dt) => _xp.DrainResidue(world);
    }
}
