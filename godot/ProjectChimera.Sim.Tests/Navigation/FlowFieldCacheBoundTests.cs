#nullable enable
using ProjectChimera.Core;
using ProjectChimera.Navigation;
using Xunit;

namespace ProjectChimera.Sim.Tests.Navigation
{
    /// <summary>
    /// DW-485 — FlowFieldSystem._cache must be BOUNDED. Before the fix, every distinct destination
    /// cell retained a ~192 KB field until the next obstacle/terrain change (~20 MB after ~100
    /// distinct move orders). The fix caps the cache at <see cref="FlowFieldSystem.MAX_CACHED_FIELDS"/>
    /// entries with least-recently-used eviction, while keeping eviction invisible to the sim:
    /// an evicted field recomputes byte-identical (the BFS is a pure function of goal cell +
    /// obstacles), and instances already held by callers are never pooled or mutated.
    /// </summary>
    public class FlowFieldCacheBoundTests
    {
        private const int MAX = FlowFieldSystem.MAX_CACHED_FIELDS;
        private const int GS  = FlowField.GRID_SIZE;

        /// <summary>
        /// World-space goal for the i-th distinct goal cell (col = i, row = 0).
        /// x = −127 + 2i → ix = x + 128 = 1 + 2i → col = (1 + 2i) / 2 = i; z = −127 → row 0.
        /// Valid for i ≤ 127.
        /// </summary>
        private static FixedVec3 Goal(int i)
            => new FixedVec3(Fixed.FromInt(-127 + 2 * i), Fixed.Zero, Fixed.FromInt(-127));

        // ── Bounding ──────────────────────────────────────────────────────────

        [Fact]
        public void DistinctGoalsBeyondTheCap_NeverExceedMaxCachedFields()
        {
            var sys = new FlowFieldSystem();

            for (int i = 0; i < MAX + 8; i++)
            {
                sys.GetOrCompute(Goal(i));
                Assert.True(sys.CachedFieldCount <= MAX,
                    $"cache held {sys.CachedFieldCount} fields after {i + 1} distinct goals (cap {MAX})");
            }

            Assert.Equal(MAX, sys.CachedFieldCount);
        }

        [Fact]
        public void RepeatedGoal_UnderTheCap_ReturnsTheSameCachedInstance()
        {
            var sys = new FlowFieldSystem();

            FlowField first  = sys.GetOrCompute(Goal(0));
            FlowField second = sys.GetOrCompute(Goal(0));

            Assert.Same(first, second);
            Assert.Equal(1, sys.CachedFieldCount);
        }

        // ── LRU policy ────────────────────────────────────────────────────────

        [Fact]
        public void Eviction_IsLeastRecentlyUsed_ATouchedEntrySurvivesTheUntouchedOneIsEvicted()
        {
            var sys = new FlowFieldSystem();

            // Fill exactly to the cap: goals 0..MAX-1 (insert order = stamp order).
            FlowField f0 = sys.GetOrCompute(Goal(0));
            FlowField f1 = sys.GetOrCompute(Goal(1));
            for (int i = 2; i < MAX; i++)
                sys.GetOrCompute(Goal(i));
            Assert.Equal(MAX, sys.CachedFieldCount);

            // Touch goal 0 → it becomes most-recently-used; goal 1 is now the LRU entry.
            Assert.Same(f0, sys.GetOrCompute(Goal(0)));

            // One past the cap → evicts goal 1 (LRU), NOT the recently touched goal 0.
            sys.GetOrCompute(Goal(MAX));
            Assert.Equal(MAX, sys.CachedFieldCount);

            // Goal 0 survived: still the same cached instance.
            Assert.Same(f0, sys.GetOrCompute(Goal(0)));

            // Goal 1 was evicted: a new request recomputes a fresh instance.
            Assert.NotSame(f1, sys.GetOrCompute(Goal(1)));
        }

        // ── Eviction is invisible to the sim ──────────────────────────────────

        [Fact]
        public void EvictedField_RecomputesByteIdentical_AndTheHeldInstanceStaysValid()
        {
            // Non-trivial obstacles so the field carries real routing decisions: a N-S wall at
            // column 64 with a gap (same shape as FlowFieldBlockingTests.GappedWall).
            var mask = new bool[FlowField.CELL_COUNT];
            for (int row = 0; row < GS; row++)
            {
                if (row >= 60 && row <= 67) continue;
                mask[row * GS + 64] = true;
            }

            var sys = new FlowFieldSystem();
            sys.SetStaticBlocked(mask);
            sys.RebuildObstacles(new BuildingStore());

            FlowField original = sys.GetOrCompute(Goal(0));

            // Push MAX more distinct goals through: goal 0 is the LRU and gets evicted.
            for (int i = 1; i <= MAX; i++)
                sys.GetOrCompute(Goal(i));

            FlowField recomputed = sys.GetOrCompute(Goal(0));
            Assert.NotSame(original, recomputed); // proves goal 0 really was evicted

            // Byte-identical recompute — eviction can never change what a unit is steered by.
            // Reading `original` here also proves an evicted-but-held instance stays intact.
            Assert.Equal(original.GoalWorld.X.Raw, recomputed.GoalWorld.X.Raw);
            Assert.Equal(original.GoalWorld.Z.Raw, recomputed.GoalWorld.Z.Raw);
            for (int c = 0; c < FlowField.CELL_COUNT; c++)
            {
                Assert.Equal(original.Directions[c].X.Raw, recomputed.Directions[c].X.Raw);
                Assert.Equal(original.Directions[c].Z.Raw, recomputed.Directions[c].Z.Raw);
            }
        }

        // ── Clearing paths still empty the bounded cache ──────────────────────

        [Fact]
        public void InvalidateCache_EmptiesTheCache_AndForcesRecompute()
        {
            var sys = new FlowFieldSystem();

            FlowField before = sys.GetOrCompute(Goal(0));
            Assert.Equal(1, sys.CachedFieldCount);

            sys.InvalidateCache();
            Assert.Equal(0, sys.CachedFieldCount);

            Assert.NotSame(before, sys.GetOrCompute(Goal(0)));
            Assert.Equal(1, sys.CachedFieldCount);
        }

        [Fact]
        public void SetBuildingObstacle_ClearsTheBoundedCache()
        {
            var sys = new FlowFieldSystem();
            for (int i = 0; i < 4; i++)
                sys.GetOrCompute(Goal(i));
            Assert.Equal(4, sys.CachedFieldCount);

            sys.SetBuildingObstacle(new FixedVec3(Fixed.Zero, Fixed.Zero, Fixed.Zero), true);
            Assert.Equal(0, sys.CachedFieldCount);
        }
    }
}
