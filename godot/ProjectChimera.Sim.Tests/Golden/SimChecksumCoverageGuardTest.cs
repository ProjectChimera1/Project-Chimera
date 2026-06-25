#nullable enable
using System;
using System.Linq;
using System.Reflection;
using ProjectChimera.Core;
using Xunit;

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// Story 1.3b (AC2 + the AC1 known-state pin) — guards that <see cref="SimChecksum"/> actually covers
    /// every per-faction <see cref="ResourceStore"/> array, and pins the v2 algorithm to a fixed expected hash.
    ///
    /// Two complementary guards:
    ///   1. <see cref="EveryPerFactionResourceArray_IsFoldedIntoTheChecksum"/> — reflection + differential
    ///      mutation. If a future story adds a public per-faction array to ResourceStore but forgets to fold it
    ///      into the checksum, mutating that array leaves the hash unchanged and this test FAILS, naming the
    ///      uncovered field. This proves *actual* coverage instead of a hand-maintained list that silently drifts.
    ///   2. <see cref="KnownWorldState_ProducesPinnedV5Hash"/> — a snapshot/tripwire: a hand-built fixed world
    ///      hashes to a committed constant. Any unintended change to the algorithm (reordering mixes, adding or
    ///      dropping a field) moves the constant and turns this red, forcing a conscious re-pin + AlgoVersion bump.
    ///
    /// MatchStats is deliberately EXCLUDED from both the hash and this guard (Story 1.3b design decision D2):
    /// its per-faction arrays are PRIVATE, write-only, derived from already-hashed entity deaths, and never
    /// branch the tick — observational scoreboard data (analogous to the hash-excluded CombatFeedbackProfile).
    /// The reflection scan below only sees PUBLIC fields, so MatchStats is invisible to it regardless; this note
    /// exists so a future dev does not "helpfully" fold it in.
    /// </summary>
    public class SimChecksumCoverageGuardTest
    {
        /// <summary>
        /// AC2 — every public per-faction array on <see cref="ResourceStore"/> must move the checksum when
        /// mutated. Reflects the per-faction array fields (length == the faction-array size), differential-mutates
        /// each on an ACTIVE slot, and asserts <see cref="SimChecksum.Compute"/> changes. A field whose mutation
        /// does NOT change the hash → FAIL naming it. Also asserts the five known arrays are all present, so the
        /// guard fails loudly if one is renamed/removed or its length drifts out of the reflected set.
        /// </summary>
        [Fact]
        public void EveryPerFactionResourceArray_IsFoldedIntoTheChecksum()
        {
            var registry  = new FactionRegistry(2);    // P1, P2 active — the checksum loop reads these slots
            var world     = new EntityWorld();          // empty — isolates ResourceStore's contribution to the hash
            var buildings = new BuildingStore();        // empty
            const int slot = (int)Faction.Player1;      // an active slot the loop reads (compile-time constant: 1)

            // Reflect the per-faction array fields: public instance arrays whose length equals the faction-array
            // size (== a constructed instance's Ore.Length == the private FACTION_COUNT). Length-matching excludes
            // any future non-faction-sized public array from being treated as per-faction.
            var reference = new ResourceStore(Fixed.Zero);
            int factionLen = reference.Ore.Length;
            FieldInfo[] perFaction = typeof(ResourceStore)
                .GetFields(BindingFlags.Public | BindingFlags.Instance)
                .Where(f => f.FieldType.IsArray)
                .Where(f => ((Array)f.GetValue(reference)!).Length == factionLen)
                .ToArray();

            Assert.NotEmpty(perFaction);

            // All five known per-faction arrays must be in the reflected set (documents intent; fails loudly if
            // one is renamed/removed or its length stops matching the faction-array size).
            string[] names = perFaction.Select(f => f.Name).ToArray();
            foreach (string expected in new[] { "Ore", "Crystal", "SupplyUsed", "SupplyCap", "FactionBase" })
                Assert.True(names.Contains(expected),
                    $"Expected per-faction array ResourceStore.{expected} was not found by the coverage scan " +
                    $"(found: {string.Join(", ", names)}). It may have been renamed, removed, or its length " +
                    $"no longer matches the faction-array size.");

            // Differential mutation: each array, on a fresh store, must move the checksum when its active slot
            // changes. A no-move means the array escaped the hash.
            foreach (FieldInfo field in perFaction)
            {
                var resources = new ResourceStore(Fixed.Zero);
                uint before = SimChecksum.Compute(world, buildings, resources, registry);
                MutateActiveSlot(field, resources, slot);
                uint after  = SimChecksum.Compute(world, buildings, resources, registry);

                Assert.True(before != after,
                    $"Per-faction array ResourceStore.{field.Name} is NOT folded into SimChecksum: " +
                    $"mutating [{(Faction)slot}] left the checksum unchanged. Add it to the active-faction " +
                    $"block in SimChecksum.Compute and bump SimChecksum.AlgoVersion (or document a deliberate " +
                    $"exclusion the way MatchStats is documented).");
            }
        }

        /// <summary>
        /// AC1 — pins the v5 algorithm. A hand-built, fully-deterministic world (all <see cref="Fixed"/>; no
        /// FromFloat, no wall-clock; the shared <see cref="SimRng"/> seeded to a fixed known value) must hash to
        /// a committed constant. This is a tripwire: an intentional algorithm change must update BOTH this constant
        /// AND <see cref="SimChecksum.AlgoVersion"/> in the same commit (mirrors the Story 9.1 "known world state →
        /// fixed expected hash" guard). The value was recorded once from a green run; it is byte-identical across
        /// Windows/Linux because every hashed field is Fixed and the RNG seed is an explicit constant.
        /// (Story 1.13: bumped v4→v5 when CollisionRadius + SeparationPriorityOf were folded in — the known-state
        /// world leaves them at Create() defaults, so the hash still moves because new mixes are added.)
        /// </summary>
        [Fact]
        public void KnownWorldState_ProducesPinnedV5Hash()
        {
            // Algorithm version must be exactly 5 (Story 1.13's separation-config fold). If this fails, the const below is stale.
            Assert.Equal(5, SimChecksum.AlgoVersion);

            uint actual = ComputeKnownStateHash();

            // ── Pinned v5 hash for the fixed world built by ComputeKnownStateHash() ───────────────────────────
            // An intentional SimChecksum algorithm change must update this value AND bump SimChecksum.AlgoVersion.
            const uint ExpectedV5Hash = 0x5E7BE3D8; // recorded from a green v5 run; re-pin only on an intentional algo change
            Assert.True(actual == ExpectedV5Hash,
                $"Known-state v5 checksum changed: expected 0x{ExpectedV5Hash:X8}, actual 0x{actual:X8}. " +
                $"If this is an INTENTIONAL algorithm change, re-pin ExpectedV5Hash to 0x{actual:X8} and bump " +
                $"SimChecksum.AlgoVersion. If not, you broke the deterministic checksum — investigate.");
        }

        /// <summary>
        /// AC6c (Story 1.12) / AC6b (Story 1.13) — the EntityWorld analogue of the ResourceStore coverage guard
        /// above: prove the folded per-entity fields ACTUALLY move the checksum. Mutating CommandTarget, a
        /// PatrolWaypoints slot / PatrolCount / PatrolIndex / PatrolDir (v4), or CollisionRadius / SeparationPriorityOf
        /// (v5) on a live entity MUST move <see cref="SimChecksum.Compute"/>. A no-move means a field escaped the
        /// hash — a silent desync surface. (PatrolWaypoints is count-driven, so the route must have PatrolCount &gt; 0
        /// for its slots to be read.) CategoryOf is intentionally absent — it is presentation-read and NOT folded.
        /// </summary>
        [Fact]
        public void EntityCommandFields_AreFoldedIntoTheChecksum()
        {
            var registry  = new FactionRegistry(2);
            var buildings = new BuildingStore();
            var resources = new ResourceStore(Fixed.Zero);

            // CommandTarget folded.
            AssertFieldFoldedIntoChecksum(buildings, resources, registry, w =>
            {
                int e = w.Create(new FixedVec3(Fixed.FromInt(1), Fixed.Zero, Fixed.FromInt(2)),
                                 Faction.Player1, Fixed.FromInt(10), Fixed.FromInt(3));
                return () => w.CommandTarget[e] = 5;
            });

            // PatrolCount folded.
            AssertFieldFoldedIntoChecksum(buildings, resources, registry, w =>
            {
                int e = w.Create(new FixedVec3(Fixed.FromInt(1), Fixed.Zero, Fixed.FromInt(2)),
                                 Faction.Player1, Fixed.FromInt(10), Fixed.FromInt(3));
                return () => w.PatrolCount[e] = 3;
            });

            // PatrolIndex folded.
            AssertFieldFoldedIntoChecksum(buildings, resources, registry, w =>
            {
                int e = w.Create(new FixedVec3(Fixed.FromInt(1), Fixed.Zero, Fixed.FromInt(2)),
                                 Faction.Player1, Fixed.FromInt(10), Fixed.FromInt(3));
                return () => w.PatrolIndex[e] = 2;
            });

            // PatrolDir folded.
            AssertFieldFoldedIntoChecksum(buildings, resources, registry, w =>
            {
                int e = w.Create(new FixedVec3(Fixed.FromInt(1), Fixed.Zero, Fixed.FromInt(2)),
                                 Faction.Player1, Fixed.FromInt(10), Fixed.FromInt(3));
                return () => w.PatrolDir[e] = -1;
            });

            // PatrolWaypoints folded (count-driven — set PatrolCount > 0 first so the slot is read).
            AssertFieldFoldedIntoChecksum(buildings, resources, registry, w =>
            {
                int e = w.Create(new FixedVec3(Fixed.FromInt(1), Fixed.Zero, Fixed.FromInt(2)),
                                 Faction.Player1, Fixed.FromInt(10), Fixed.FromInt(3));
                w.PatrolCount[e] = 2; // make the first 2 waypoint slots part of the hashed set
                return () => w.PatrolWaypoints[e * EntityWorld.MAX_PATROL_WAYPOINTS + 1] =
                    new FixedVec3(Fixed.FromInt(9), Fixed.Zero, Fixed.FromInt(9));
            });

            // CollisionRadius folded (v5, Story 1.13) — mutate to a value != the Create() default (1.0).
            AssertFieldFoldedIntoChecksum(buildings, resources, registry, w =>
            {
                int e = w.Create(new FixedVec3(Fixed.FromInt(1), Fixed.Zero, Fixed.FromInt(2)),
                                 Faction.Player1, Fixed.FromInt(10), Fixed.FromInt(3));
                return () => w.CollisionRadius[e] = Fixed.Half;
            });

            // SeparationPriorityOf folded (v5, Story 1.13) — mutate to a value != the Create() default (Normal).
            // (CategoryOf is deliberately NOT proven here: it is presentation-read and NOT folded.)
            AssertFieldFoldedIntoChecksum(buildings, resources, registry, w =>
            {
                int e = w.Create(new FixedVec3(Fixed.FromInt(1), Fixed.Zero, Fixed.FromInt(2)),
                                 Faction.Player1, Fixed.FromInt(10), Fixed.FromInt(3));
                return () => w.SeparationPriorityOf[e] = SeparationPriority.Push;
            });
        }

        /// <summary>
        /// Build a fresh world via <paramref name="setup"/> (which returns a mutation thunk), checksum before,
        /// run the mutation, checksum after, and assert the hash moved. Buildings/resources/registry are shared
        /// constants so only the EntityWorld field under test varies.
        /// </summary>
        private static void AssertFieldFoldedIntoChecksum(BuildingStore buildings, ResourceStore resources,
            FactionRegistry registry, System.Func<EntityWorld, System.Action> setup)
        {
            var world = new EntityWorld();
            System.Action mutate = setup(world);
            uint before = SimChecksum.Compute(world, buildings, resources, registry);
            mutate();
            uint after = SimChecksum.Compute(world, buildings, resources, registry);
            Assert.True(before != after,
                "A folded EntityWorld per-entity field is NOT folded into SimChecksum: mutating it left the " +
                "checksum unchanged. Add it to the entity loop in SimChecksum.Compute (and bump AlgoVersion).");
        }

        /// <summary>
        /// Build a small fixed world by hand and compute its v5 checksum. Fully self-contained: every hashed
        /// field is set explicitly with <see cref="Fixed"/> so the pinned hash does not silently depend on store
        /// constructor defaults a future story might change. The shared <see cref="SimRng"/> is reseeded to a
        /// fixed known value so the RNG fold is pinned independently of EntityWorld.DEFAULT_RNG_SEED. The new v5
        /// separation fields are left at their Create() defaults (CollisionRadius=1.0, SeparationPriorityOf=Normal).
        /// </summary>
        private static uint ComputeKnownStateHash()
        {
            // Two entities (hashed: Position X/Y/Z + Health). Speed (4th arg) is not hashed; fixed for clarity.
            var world = new EntityWorld();

            // v3 (Story 1.5): SimRng.State is folded into the checksum. Reseed to a fixed known value so the pin
            // is explicit and independent of the EntityWorld default seed.
            const ulong KnownRngSeed = 0x0123456789ABCDEFUL;
            world.Rng.Seed(KnownRngSeed);
            world.Create(new FixedVec3(Fixed.FromInt(3), Fixed.Zero, Fixed.FromInt(-5)),
                         Faction.Player1, Fixed.FromInt(42), Fixed.FromInt(3));
            world.Create(new FixedVec3(Fixed.FromInt(-7), Fixed.FromInt(1), Fixed.FromInt(9)),
                         Faction.Player2, Fixed.FromInt(88), Fixed.FromInt(3));

            // One building (hashed: Alive + Health + ConstructionTimer). Set Health/Timer explicitly so the pin
            // is independent of BuildingStore.Create's default health.
            var buildings = new BuildingStore();
            int b0 = buildings.Create(new FixedVec3(Fixed.FromInt(-14), Fixed.Zero, Fixed.Zero),
                                      Faction.Player1, BuildingType.CommandCenter);
            buildings.Health[b0]            = Fixed.FromInt(500);
            buildings.ConstructionTimer[b0] = Fixed.FromInt(5);

            // ResourceStore: distinct values across all five per-faction arrays for P1/P2 (the active slots).
            var resources = new ResourceStore(Fixed.Zero);
            resources.Ore[(int)Faction.Player1]        = Fixed.FromInt(150);
            resources.Ore[(int)Faction.Player2]        = Fixed.FromInt(75);
            resources.Crystal[(int)Faction.Player1]    = Fixed.FromInt(10);
            resources.Crystal[(int)Faction.Player2]    = Fixed.FromInt(3);
            resources.SupplyUsed[(int)Faction.Player1] = 4;
            resources.SupplyUsed[(int)Faction.Player2] = 7;
            resources.SupplyCap[(int)Faction.Player1]  = 20;
            resources.SupplyCap[(int)Faction.Player2]  = 30;
            resources.FactionBase[(int)Faction.Player1] = new FixedVec3(Fixed.FromInt(-14), Fixed.Zero, Fixed.FromInt(2));
            resources.FactionBase[(int)Faction.Player2] = new FixedVec3(Fixed.FromInt(14), Fixed.Zero, Fixed.FromInt(-2));

            return SimChecksum.Compute(world, buildings, resources, new FactionRegistry(2));
        }

        /// <summary>
        /// Set an active slot of <paramref name="field"/> to a distinct, type-appropriate value so its
        /// contribution to the checksum is observable. An unhandled element type throws a clear "extend the
        /// guard" error, forcing a conscious decision when a new per-faction array type appears.
        /// </summary>
        private static void MutateActiveSlot(FieldInfo field, ResourceStore r, int slot)
        {
            var arr  = (Array)field.GetValue(r)!;
            Type elem = field.FieldType.GetElementType()!;
            if      (elem == typeof(Fixed))     arr.SetValue(Fixed.FromInt(999), slot);
            else if (elem == typeof(int))       arr.SetValue(123456, slot);
            else if (elem == typeof(FixedVec3)) arr.SetValue(new FixedVec3(Fixed.FromInt(7), Fixed.FromInt(8), Fixed.FromInt(9)), slot);
            else throw new NotSupportedException(
                $"Coverage guard cannot mutate ResourceStore.{field.Name} (element {elem.Name}). " +
                $"Extend MutateActiveSlot for this type so its coverage can be proven.");
        }
    }
}
