#nullable enable
using System;
using ProjectChimera.Combat;
using ProjectChimera.Core;
using ProjectChimera.Effects;
using ProjectChimera.Navigation;
using Xunit;

namespace ProjectChimera.Sim.Tests.Effects
{
    /// <summary>
    /// Story 15.4 (DW-249 + DW-250) — the SearchArea TARGET-SELECTION contract when the hit buffer is the
    /// binding constraint. Two defects lived here, both invisible below 64 entities-in-radius and both fixed by
    /// pushing the target filter INTO the spatial query (<c>SpatialHash.QueryRadiusLowestIds</c>):
    ///
    /// <list type="number">
    ///   <item>DW-250 — the 64-slot buffer filled with UNFILTERED candidates and was compacted by
    ///   <c>TargetMatcher</c> afterwards, so a crowd of non-matching entities could starve the fan-out to ZERO
    ///   even though matching entities were standing in the radius.</item>
    ///   <item>DW-249 — over-cap selection kept the first buffer-full in spatial cell-scan order, i.e. it was
    ///   decided by grid geometry rather than by the documented ascending-id contract.</item>
    /// </list>
    ///
    /// Godot-free, Fixed-only, no RNG. Both fan-out tests are arranged so the OLD behaviour produces a
    /// concretely different answer (0 targets / the wrong 64 ids), so they are genuine regression teeth rather
    /// than restatements of the new code.
    /// </summary>
    public class SearchAreaSelectionTests
    {
        private static FixedVec3 At(int x) => new FixedVec3(Fixed.FromInt(x), Fixed.Zero, Fixed.Zero);

        // ── DW-250: the filter must run BEFORE the buffer is truncated ────────────────────────────────

        [Fact]
        public void EnemyFanOut_IsNotStarved_ByACrowdOfLowerIdAllies()
        {
            // A nuke cast in the middle of the caster's own army. The allies are created FIRST, so they hold the
            // lowest ids and are the ones a cell-ordered unfiltered scan collects; the enemies sit behind them.
            var w = new EntityWorld();
            int caster = w.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3)); // id 0
            const int allies = 70;                                                    // > MaxHitsPerSearch
            for (int i = 0; i < allies; i++)
                w.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3)); // ids 1..70
            const int enemies = 5;
            for (int i = 0; i < enemies; i++)
                w.Create(FixedVec3.Zero, Faction.Player2, Fixed.FromInt(100), Fixed.FromInt(3)); // ids 71..75

            var sh = new SpatialHash();
            sh.Rebuild(w);

            var search = new SearchAreaEffect(Fixed.FromInt(10), TargetFilter.Enemy,
                                              new DamageEffect(Fixed.One, DamageType.Normal));
            var buffer = new int[EffectCaps.MaxHitsPerSearch];
            var ctx = new EffectContext(w, caster, caster, Faction.Player1, DamageTable.Default, sh);

            int n = search.FindTargets(in ctx, buffer);

            // BEFORE the fix this returned 0: the buffer filled with ids 0..63 (caster + allies, all of which the
            // Enemy filter rejects) and the compaction pass then had nothing left to keep.
            Assert.Equal(enemies, n);
            for (int i = 0; i < n; i++)
                Assert.Equal(Faction.Player2, w.FactionOf[buffer[i]]);
            Assert.Equal(allies + 1, buffer[0]); // the first enemy id, not an ally
        }

        [Fact]
        public void OverCap_MatchingEnemies_StillFillTheWholeFanOut_WhenAlliesCrowdTheRadius()
        {
            // Same crowd, but now there are MORE enemies than the cap: the fan-out must be a full 64 matching
            // targets (the allies must not consume slots at all), and it must be the lowest 64 enemy ids.
            var w = new EntityWorld();
            int caster = w.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3)); // id 0
            const int allies = 40;
            for (int i = 0; i < allies; i++)
                w.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3)); // ids 1..40
            const int enemies = 80;
            for (int i = 0; i < enemies; i++)
                w.Create(FixedVec3.Zero, Faction.Player2, Fixed.FromInt(100), Fixed.FromInt(3)); // ids 41..120

            var sh = new SpatialHash();
            sh.Rebuild(w);

            var search = new SearchAreaEffect(Fixed.FromInt(10), TargetFilter.Enemy,
                                              new DamageEffect(Fixed.One, DamageType.Normal));
            var buffer = new int[EffectCaps.MaxHitsPerSearch];
            var ctx = new EffectContext(w, caster, caster, Faction.Player1, DamageTable.Default, sh);

            int n = search.FindTargets(in ctx, buffer);

            // BEFORE the fix: ids 0..63 filled the buffer (41 non-matching) and compaction left 23 targets.
            Assert.Equal(EffectCaps.MaxSearchTargets, n);
            for (int i = 0; i < n; i++)
            {
                Assert.Equal(Faction.Player2, w.FactionOf[buffer[i]]);
                Assert.Equal(allies + 1 + i, buffer[i]); // the lowest 64 ENEMY ids, ascending
            }
        }

        // ── DW-249: over-cap selection is the GLOBAL lowest ids, not the first cell-scan buffer-full ──

        [Fact]
        public void OverCapSelection_KeepsTheGloballyLowestIds_NotTheFirstCellScanned()
        {
            // Cell-scan order runs ascending in cz then cx, so x=-50 (cx=11) is visited BEFORE x=+50 (cx=21).
            // The 8 LOWEST-id enemies are parked in the later-scanned cell on purpose: a scan that stops when the
            // buffer fills can never see them.
            var w = new EntityWorld();
            int caster = w.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3)); // id 0
            const int lateCell = 8;
            for (int i = 0; i < lateCell; i++)
                w.Create(At(50), Faction.Player2, Fixed.FromInt(100), Fixed.FromInt(3));  // ids 1..8   (cx=21)
            for (int i = 0; i < EffectCaps.MaxHitsPerSearch; i++)
                w.Create(At(-50), Faction.Player2, Fixed.FromInt(100), Fixed.FromInt(3)); // ids 9..72  (cx=11)

            var sh = new SpatialHash();
            sh.Rebuild(w);

            var search = new SearchAreaEffect(Fixed.FromInt(100), TargetFilter.Enemy,
                                              new DamageEffect(Fixed.One, DamageType.Normal));
            var buffer = new int[EffectCaps.MaxHitsPerSearch];
            var ctx = new EffectContext(w, caster, caster, Faction.Player1, DamageTable.Default, sh);

            int n = search.FindTargets(in ctx, buffer);

            // BEFORE the fix this was ids 9..72 (the earlier cell's buffer-full). The contract is the lowest 64
            // ids over the WHOLE radius — ids 1..64 — so buffer[0] flips from 9 to 1 and ids 65..72 drop out.
            Assert.Equal(EffectCaps.MaxSearchTargets, n);
            for (int i = 0; i < n; i++)
                Assert.Equal(i + 1, buffer[i]);
        }

        // ── SpatialHash-level: the displacement rule itself, on a deliberately tiny buffer ────────────

        /// <summary>Accept-everything predicate — isolates the SELECTION rule from any filtering.</summary>
        private readonly struct AcceptAll : IEntityQueryFilter
        {
            public bool Accepts(EntityWorld world, int entityId) => true;
        }

        /// <summary>Accepts only EVEN entity ids — a predicate whose rejects would otherwise eat buffer slots.</summary>
        private readonly struct AcceptEvenIds : IEntityQueryFilter
        {
            public bool Accepts(EntityWorld world, int entityId) => (entityId & 1) == 0;
        }

        [Fact]
        public void QueryRadiusLowestIds_EvictsTheLargestHeldId_WhenAFullBufferMeetsALowerCandidate()
        {
            // ids 0,1 sit in the LATER-scanned cell, ids 2,3,4 in the earlier one, and the buffer holds 3. A
            // stop-when-full scan yields [2,3,4]; the lowest-ids rule must evict 4 then 3 and return [0,1,2].
            var w = new EntityWorld();
            w.Create(At(50), Faction.Player2, Fixed.FromInt(100), Fixed.Zero);  // id 0 (cx=21, scanned later)
            w.Create(At(50), Faction.Player2, Fixed.FromInt(100), Fixed.Zero);  // id 1
            w.Create(At(-50), Faction.Player2, Fixed.FromInt(100), Fixed.Zero); // id 2 (cx=11, scanned first)
            w.Create(At(-50), Faction.Player2, Fixed.FromInt(100), Fixed.Zero); // id 3
            w.Create(At(-50), Faction.Player2, Fixed.FromInt(100), Fixed.Zero); // id 4

            var sh = new SpatialHash();
            sh.Rebuild(w);

            var buffer = new int[3];
            int n = sh.QueryRadiusLowestIds(w, FixedVec3.Zero, Fixed.FromInt(100), -1, buffer, new AcceptAll());

            Assert.Equal(3, n);
            Assert.Equal(0, buffer[0]);
            Assert.Equal(1, buffer[1]);
            Assert.Equal(2, buffer[2]);
        }

        [Fact]
        public void QueryRadiusLowestIds_RejectedCandidates_NeverConsumeABufferSlot()
        {
            // 10 co-located entities, buffer of 3, predicate keeps only even ids. If rejects could occupy slots
            // the answer would be [0,2] (ids 0,1,2 scanned, 1 dropped afterwards); the contract is [0,2,4].
            var w = new EntityWorld();
            for (int i = 0; i < 10; i++)
                w.Create(FixedVec3.Zero, Faction.Player2, Fixed.FromInt(100), Fixed.Zero); // ids 0..9

            var sh = new SpatialHash();
            sh.Rebuild(w);

            var buffer = new int[3];
            int n = sh.QueryRadiusLowestIds(w, FixedVec3.Zero, Fixed.FromInt(10), -1, buffer, new AcceptEvenIds());

            Assert.Equal(3, n);
            Assert.Equal(0, buffer[0]);
            Assert.Equal(2, buffer[1]);
            Assert.Equal(4, buffer[2]);
        }

        [Fact]
        public void QueryRadiusLowestIds_HonoursExcludeId_AndTheRadius()
        {
            // Boundary behaviour carried over from QueryRadius: excludeId is dropped, and an entity outside the
            // radius is never collected however low its id.
            var w = new EntityWorld();
            int near = w.Create(At(1), Faction.Player2, Fixed.FromInt(100), Fixed.Zero);   // id 0, in radius
            w.Create(At(2), Faction.Player2, Fixed.FromInt(100), Fixed.Zero);              // id 1, in radius
            w.Create(At(15), Faction.Player2, Fixed.FromInt(100), Fixed.Zero);             // id 2, scanned cell but OUT of radius

            var sh = new SpatialHash();
            sh.Rebuild(w);

            var buffer = new int[EffectCaps.MaxHitsPerSearch];
            int n = sh.QueryRadiusLowestIds(w, FixedVec3.Zero, Fixed.FromInt(10), near, buffer, new AcceptAll());

            Assert.Equal(1, n);
            Assert.Equal(1, buffer[0]);
        }

        [Fact]
        public void QueryRadiusLowestIds_ZeroLengthBuffer_ReturnsZero_AndNeverIndexes()
        {
            var w = new EntityWorld();
            w.Create(FixedVec3.Zero, Faction.Player2, Fixed.FromInt(100), Fixed.Zero);

            var sh = new SpatialHash();
            sh.Rebuild(w);

            int n = sh.QueryRadiusLowestIds(w, FixedVec3.Zero, Fixed.FromInt(10), -1,
                                            Array.Empty<int>(), new AcceptAll());

            Assert.Equal(0, n);
        }
    }
}
