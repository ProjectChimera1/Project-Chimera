#nullable enable
using ProjectChimera.Core;

namespace ProjectChimera.Combat
{
    /// <summary>One recorded unit death (Story 3.13): the world position it died at, the victim's faction (so a hero on
    /// the victim's own faction is excluded), and the XP bounty it awards. A plain value struct — snapshotted at the
    /// single death choke point (<see cref="DamageResolver.KillEntity"/>, BEFORE <see cref="EntityWorld.Destroy"/>
    /// recycles the slot) because the corpse is unobservable after that (D-1).</summary>
    public readonly struct DeathRecord
    {
        public readonly FixedVec3 Position;
        public readonly Faction   Faction;
        public readonly Fixed     Bounty;

        public DeathRecord(FixedVec3 position, Faction faction, Fixed bounty)
        {
            Position = position;
            Faction  = faction;
            Bounty   = bounty;
        }
    }

    /// <summary>
    /// A host-owned, per-tick TRANSIENT buffer of unit deaths (Story 3.13, D-1). <c>DamageResolver.KillEntity</c>
    /// pushes a <see cref="DeathRecord"/> at the single death choke point; <see cref="HeroXpSystem"/> (index 9, after
    /// combat + projectiles) drains it each tick, credits every hostile hero in range, and <see cref="Clear"/>s it.
    ///
    /// <para>Drained + cleared every tick, so it is EMPTY at the checksum boundary → NOT folded into
    /// <see cref="SimChecksum"/> (exactly like <see cref="CombatEventQueue"/>).  Pure C# — no Godot dependency.</para>
    ///
    /// <para><b>DW-766 — that emptiness is ENFORCED, not assumed.</b> The claim above was false as written, because
    /// index [9] is not the last producer: DW-490 threaded this same feed into <see cref="Effects.ModifierStore"/>,
    /// whose DW-325 ceiling-collapse kill is reachable from <c>ItemSystem</c> at [10] (a net-negative
    /// <c>max_health_delta</c> item collapsing its claimant's ceiling during a pickup), and <c>ScenarioDirector</c> at
    /// [15] holds the feed in the <c>EffectContext</c> its <c>run_effect</c> graphs execute against. Either one left
    /// <see cref="Count"/> at 1 when the checksum was taken, with the residue's XP — which feeds the FOLDED
    /// <c>HeroStore.Xp</c>/<c>Level</c> — landing a tick late: mutable state outside the desync detector. Two things
    /// now make the invariant true rather than merely stated:
    /// <list type="number">
    /// <item><c>DeathFeedDrainSystem</c> at index [16], registered past the LAST producer, credits the residue in the
    ///   SAME tick (through <see cref="HeroXpSystem.DrainResidue"/>) and clears the feed.</item>
    /// <item><c>SimulationLoop</c> asserts <see cref="Count"/> == 0 at the tick boundary — immediately after the whole
    ///   system array, before the checksum — so a future producer registered past the drain fails LOUDLY instead of
    ///   silently re-opening the hole.</item>
    /// </list>
    /// The fold set is unchanged: this feed is still NOT hashed, and must not become hashed — the fix is that the
    /// premise for excluding it now holds.</para>
    ///
    /// <para><b>DW-616 — lossless, not capped.</b> This used to be a flat 256-slot ring that silently dropped every
    /// death past the cap, mirroring the pre-DW-469 <see cref="CombatEventQueue"/>. That mirror was wrong: unlike the
    /// event queue — whose contents are presentation-only, which is why DW-469 could settle for a two-lane
    /// priority-reserve split — a dropped <see cref="DeathRecord"/> awards no hero XP, and
    /// <c>HeroStore.Xp</c>/<c>Level</c>/the growth-modifier stacks it drives ARE folded into
    /// <see cref="SimChecksum"/>. A &gt;256-death tick was therefore a determinism-visible feedback loss, and no
    /// priority lane can help because every death carries exactly the same kind of folded consequence: there is no
    /// low-value class to sacrifice. The buffer is now grown on demand (amortized doubling from
    /// <see cref="INITIAL_CAPACITY"/>) so a death is NEVER dropped.</para>
    ///
    /// <para><b>Determinism.</b> Growth is a pure function of the push sequence and touches nothing observable: only
    /// <see cref="Count"/> and <see cref="Get"/> over <c>[0, Count)</c> are ever read, records keep their exact push
    /// order across a copy, and the capacity itself is never folded, serialized, or replicated. Two peers that grew
    /// differently (say one restored a save mid-match) still drain an identical record sequence and credit identical
    /// XP. Retained capacity is bounded in practice by the peak deaths in one tick, itself bounded by the
    /// <c>KillEntity</c> calls a tick can make (&lt;= <c>EntityWorld.MAX_ENTITIES</c> plus in-tick respawn churn).</para>
    ///
    /// <para><b>Golden analysis (DW-616 closure).</b> The first <see cref="INITIAL_CAPACITY"/> pushes of any tick run
    /// the identical append — same index, same order, same <see cref="Count"/> — so every tick with at most that many
    /// deaths produces a bit-identical <see cref="HeroXpSystem"/> credit pass and therefore a bit-identical checksum.
    /// The change is observable ONLY on a tick that would previously have overflowed, which no recorded golden or
    /// replay scenario contains (their armies are orders of magnitude under 256 kills in one tick, and the whole
    /// entity cap is 4096). Cost of the deliberate re-baseline this entry budgeted for: ZERO goldens move — pinned by
    /// <c>DeathFeedCapacityTests.Push_AtInitialCapacity_DoesNotGrow_SoSubCapTicksAreBitIdenticalToThePreFixRing</c>
    /// and by the unchanged golden suite.</para>
    /// </summary>
    public sealed class DeathFeed
    {
        /// <summary>
        /// DW-616 — the starting (and, on every tick a real match produces, final) buffer size. Deliberately the old
        /// flat cap: a tick at or below this allocates exactly what it always did, so the common path carries no new
        /// cost and no behavioural change.
        /// </summary>
        public const int INITIAL_CAPACITY = 256;

        private DeathRecord[] _buf = new DeathRecord[INITIAL_CAPACITY];
        private int _count;

        /// <summary>Number of deaths recorded this tick.</summary>
        public int Count => _count;

        /// <summary>
        /// DW-616 — the current backing-array size. Diagnostic/test observability ONLY: no simulation system reads it,
        /// it is never folded into <see cref="SimChecksum"/>, and it is not part of any save or wire format, so two
        /// peers holding different capacities are not divergent.
        /// </summary>
        public int Capacity => _buf.Length;

        /// <summary>Returns the death record at index <paramref name="i"/>. No bounds checking (the drain loop reads 0..Count).</summary>
        public DeathRecord Get(int i) => _buf[i];

        /// <summary>Record a death. DW-616: never drops — the buffer grows on demand (see the type remarks), because a
        /// dropped record silently withholds folded hero XP.</summary>
        public void Push(FixedVec3 position, Faction faction, Fixed bounty)
        {
            if (_count == _buf.Length) Grow();
            _buf[_count++] = new DeathRecord(position, faction, bounty);
        }

        /// <summary>DW-616 — double the backing array, preserving push order exactly (an ordered copy; the drain reads
        /// <c>[0, Count)</c>, so index-for-index preservation is the whole contract). Called only on the tick a death
        /// count first exceeds the current capacity; the grown buffer is then retained, so the growth is amortized and
        /// never recurs at that size.</summary>
        private void Grow()
        {
            var grown = new DeathRecord[_buf.Length == 0 ? INITIAL_CAPACITY : _buf.Length * 2];
            System.Array.Copy(_buf, grown, _count);
            _buf = grown;
        }

        /// <summary>Resets the buffer so the next tick starts fresh (called by <see cref="HeroXpSystem"/> at end-of-tick).
        /// Retains the grown capacity deliberately — re-shrinking would re-allocate on every busy tick, and the capacity
        /// is unobservable to the simulation.</summary>
        public void Clear() => _count = 0;
    }
}
