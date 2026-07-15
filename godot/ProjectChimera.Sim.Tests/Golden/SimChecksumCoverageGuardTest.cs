#nullable enable
using System;
using System.Linq;
using System.Reflection;
using ProjectChimera.Core;
using ProjectChimera.Effects; // ModifierStore / Modifier / StatusFlags (v6 fold coverage)
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
    ///   2. <see cref="KnownWorldState_ProducesPinnedV11Hash"/> — a snapshot/tripwire: a hand-built fixed world
    ///      hashes to a committed constant. Any unintended change to the algorithm (reordering mixes, adding or
    ///      dropping a field) moves the constant and turns this red, forcing a conscious re-pin + AlgoVersion bump.
    ///
    /// MatchStats is deliberately EXCLUDED from both the hash and this guard (Story 1.3b design decision D2):
    /// its per-faction arrays are PRIVATE, write-only, derived from already-hashed entity deaths, and never
    /// branch the tick — observational scoreboard data (analogous to the hash-excluded CombatFeedbackProfile).
    /// The reflection scan below only sees PUBLIC fields, so MatchStats is invisible to it regardless; this note
    /// exists so a future dev does not "helpfully" fold it in.
    ///
    /// Story 2.7 made the once-hypothetical "hash-excluded CombatFeedbackProfile" real: a presentation-read
    /// <c>EntityWorld.FeedbackProfile</c> (the first reference-typed per-entity SoA) plus a <c>CombatEvent.Feedback</c>
    /// reference on the (never-hashed) <c>CombatEventQueue</c> and a <c>ProjectileStore.Feedback</c> slot. ALL are
    /// deliberately NOT folded — presentation-read only, exactly like <see cref="EntityWorld.MeshType"/>/CategoryOf —
    /// so they add NO fold of their own: a presentation field never moves AlgoVersion or a golden (the version is 10
    /// and there are 18 goldens as of Story 3.12, both moved only by REAL folds like the Delivery + ProjectileSpeed fold).
    /// The reflection scan (ResourceStore-only) and the enumerated EntityWorld guard below both correctly ignore them;
    /// the dedicated exclusion teeth (a FeedbackProfile must not move Compute; draining the event queue must not
    /// perturb the sim) live in CombatFeedbackProfileTests.
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
        /// AC1 — pins the v7 algorithm. A hand-built, fully-deterministic world (all <see cref="Fixed"/>; no
        /// FromFloat, no wall-clock; the shared <see cref="SimRng"/> seeded to a fixed known value) must hash to
        /// a committed constant. This is a tripwire: an intentional algorithm change must update BOTH this constant
        /// AND <see cref="SimChecksum.AlgoVersion"/> in the same commit (mirrors the Story 9.1 "known world state →
        /// fixed expected hash" guard). The value was recorded once from a green run; it is byte-identical across
        /// Windows/Linux because every hashed field is Fixed and the RNG seed is an explicit constant.
        /// (Story 2.2b: bumped v5→v6 for Effective* / Energy / StatusFlagsOf + the ModifierStore instance state.
        /// Story 2.4a: bumped v6→v7 for the per-entity AbilityCooldownTicks fold — the known-state world has no
        /// abilities, so AbilityCount == 0 and the fold adds Mix(0) per entity, yet the hash still moves.
        /// Story 2.6: bumped v7→v8 for the per-entity EffectiveArmor fold — the known-state world has no armor
        /// (EffectiveArmor == 0 per entity, the Create default), so the fold adds one Mix(0) per entity, yet the
        /// hash still moves.)
        /// </summary>
        [Fact]
        public void KnownWorldState_ProducesPinnedV15Hash()
        {
            // Algorithm version must be exactly 15 (Story 6.3's per-entity Elevation fold). If this fails, the const below is stale.
            Assert.Equal(15, SimChecksum.AlgoVersion);

            uint actual = ComputeKnownStateHash();

            // ── Pinned v15 hash for the fixed world built by ComputeKnownStateHash() ──────────────────────────
            // An intentional SimChecksum algorithm change must update this value AND bump SimChecksum.AlgoVersion.
            // The known-state world's two entities are at their Create-default Elevation (0, no grid injected), so the
            // v15 fold moves the hash from v14 purely by the added Mix(0) elevation per alive entity — the intentional
            // Elevation-fold re-baseline (Story 6.3).
            const uint ExpectedV15Hash = 0xB1E4E662; // recorded from a green v15 run; re-pin only on an intentional algo change
            Assert.True(actual == ExpectedV15Hash,
                $"Known-state v15 checksum changed: expected 0x{ExpectedV15Hash:X8}, actual 0x{actual:X8}. " +
                $"If this is an INTENTIONAL algorithm change, re-pin ExpectedV15Hash to 0x{actual:X8} and bump " +
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

            // ── v6 (Story 2.2b): effective stats + ability resource + status are folded ──
            AssertFieldFoldedIntoChecksum(buildings, resources, registry, w =>
            {
                int e = w.Create(new FixedVec3(Fixed.FromInt(1), Fixed.Zero, Fixed.FromInt(2)),
                                 Faction.Player1, Fixed.FromInt(10), Fixed.FromInt(3));
                return () => w.EffectiveAttackDamage[e] = Fixed.FromInt(99);
            });
            AssertFieldFoldedIntoChecksum(buildings, resources, registry, w =>
            {
                int e = w.Create(new FixedVec3(Fixed.FromInt(1), Fixed.Zero, Fixed.FromInt(2)),
                                 Faction.Player1, Fixed.FromInt(10), Fixed.FromInt(3));
                return () => w.EffectiveMaxHealth[e] = Fixed.FromInt(99);
            });
            AssertFieldFoldedIntoChecksum(buildings, resources, registry, w =>
            {
                int e = w.Create(new FixedVec3(Fixed.FromInt(1), Fixed.Zero, Fixed.FromInt(2)),
                                 Faction.Player1, Fixed.FromInt(10), Fixed.FromInt(3));
                return () => w.EffectiveMoveSpeed[e] = Fixed.FromInt(99);
            });
            // EffectiveArmor folded (v8, Story 2.6) — the buffable armor stat. A non-zero EffectiveArmor MUST move
            // the hash; a no-move means the v8 fold is not reading the field. (BaseArmor is deliberately NOT proven
            // here: it is authored/unfolded, the BaseAttackDamage posture — only EffectiveArmor is sim truth.)
            AssertFieldFoldedIntoChecksum(buildings, resources, registry, w =>
            {
                int e = w.Create(new FixedVec3(Fixed.FromInt(1), Fixed.Zero, Fixed.FromInt(2)),
                                 Faction.Player1, Fixed.FromInt(10), Fixed.FromInt(3));
                return () => w.EffectiveArmor[e] = Fixed.FromInt(5);
            });
            AssertFieldFoldedIntoChecksum(buildings, resources, registry, w =>
            {
                int e = w.Create(new FixedVec3(Fixed.FromInt(1), Fixed.Zero, Fixed.FromInt(2)),
                                 Faction.Player1, Fixed.FromInt(10), Fixed.FromInt(3));
                return () => w.Energy[e] = Fixed.FromInt(7);
            });
            AssertFieldFoldedIntoChecksum(buildings, resources, registry, w =>
            {
                int e = w.Create(new FixedVec3(Fixed.FromInt(1), Fixed.Zero, Fixed.FromInt(2)),
                                 Faction.Player1, Fixed.FromInt(10), Fixed.FromInt(3));
                return () => w.StatusFlagsOf[e] = StatusFlags.Stunned;
            });

            // ── v7 (Story 2.4a): AbilityCooldownTicks is folded (count-driven — set AbilityCount > 0 first so the
            //    slot is part of the hashed set, exactly like PatrolWaypoints needs PatrolCount > 0). A non-zero
            //    cooldown slot MUST move the hash; a no-move means the fold is not reading the field. ──
            AssertFieldFoldedIntoChecksum(buildings, resources, registry, w =>
            {
                int e = w.Create(new FixedVec3(Fixed.FromInt(1), Fixed.Zero, Fixed.FromInt(2)),
                                 Faction.Player1, Fixed.FromInt(10), Fixed.FromInt(3));
                w.AbilityCount[e] = 1; // make slot 0 part of the count-driven hashed set
                return () => w.AbilityCooldownTicks[e * EntityWorld.MAX_ABILITIES_PER_UNIT + 0] = 42;
            });

            // ── v9 (Story 2.12): the shift-queue order ring is folded (count-driven — set OrderQueueCount > 0 first so
            //    slot 0 is part of the hashed set, exactly like AbilityCooldownTicks needs AbilityCount > 0). A non-zero
            //    command byte AND a non-zero target field each MUST move the hash; a no-move means the fold is not
            //    reading the field. Two separate assertions so a fold that reads Cmd but forgets a Target (or vice versa)
            //    still goes RED. ──
            AssertFieldFoldedIntoChecksum(buildings, resources, registry, w =>
            {
                int e = w.Create(new FixedVec3(Fixed.FromInt(1), Fixed.Zero, Fixed.FromInt(2)),
                                 Faction.Player1, Fixed.FromInt(10), Fixed.FromInt(3));
                w.OrderQueueCount[e] = 1; // make slot 0 part of the count-driven hashed set
                return () => w.OrderQueueCmd[e * EntityWorld.MAX_ORDER_QUEUE + 0] = (byte)UnitCommand.Move;
            });
            AssertFieldFoldedIntoChecksum(buildings, resources, registry, w =>
            {
                int e = w.Create(new FixedVec3(Fixed.FromInt(1), Fixed.Zero, Fixed.FromInt(2)),
                                 Faction.Player1, Fixed.FromInt(10), Fixed.FromInt(3));
                w.OrderQueueCount[e] = 1;
                return () => w.OrderQueueTargetX[e * EntityWorld.MAX_ORDER_QUEUE + 0] = 12345;
            });
            AssertFieldFoldedIntoChecksum(buildings, resources, registry, w =>
            {
                int e = w.Create(new FixedVec3(Fixed.FromInt(1), Fixed.Zero, Fixed.FromInt(2)),
                                 Faction.Player1, Fixed.FromInt(10), Fixed.FromInt(3));
                w.OrderQueueCount[e] = 1;
                return () => w.OrderQueueTargetZ[e * EntityWorld.MAX_ORDER_QUEUE + 0] = -6789;
            });

            // ── v10 (Story 3.12): Delivery + ProjectileSpeed are folded — mutate each to a value != its Create default
            //    (Delivery default Hitscan → Projectile; ProjectileSpeed default 18 → 6). A non-default value MUST move
            //    the hash; a no-move means the v10 fold is not reading the field. ──
            AssertFieldFoldedIntoChecksum(buildings, resources, registry, w =>
            {
                int e = w.Create(new FixedVec3(Fixed.FromInt(1), Fixed.Zero, Fixed.FromInt(2)),
                                 Faction.Player1, Fixed.FromInt(10), Fixed.FromInt(3));
                return () => w.Delivery[e] = ProjectChimera.Combat.AttackDelivery.Projectile;
            });
            AssertFieldFoldedIntoChecksum(buildings, resources, registry, w =>
            {
                int e = w.Create(new FixedVec3(Fixed.FromInt(1), Fixed.Zero, Fixed.FromInt(2)),
                                 Faction.Player1, Fixed.FromInt(10), Fixed.FromInt(3));
                return () => w.ProjectileSpeed[e] = Fixed.FromInt(6);
            });

            // ── v11 (Story 3.13): the per-entity XpBounty is folded — mutate to a value != its Create default (0). A
            //    non-zero bounty MUST move the hash; a no-move means the v11 entity-loop fold is not reading the field. ──
            AssertFieldFoldedIntoChecksum(buildings, resources, registry, w =>
            {
                int e = w.Create(new FixedVec3(Fixed.FromInt(1), Fixed.Zero, Fixed.FromInt(2)),
                                 Faction.Player1, Fixed.FromInt(10), Fixed.FromInt(3));
                return () => w.XpBounty[e] = Fixed.FromInt(50);
            });

            // ── v15 (Story 6.3): the per-entity Elevation is folded — mutate to a value != its Create default (0, the
            //    flat-map / no-grid state). A non-zero elevation MUST move the hash; a no-move means the v15 entity-loop
            //    fold is not reading the field. ──
            AssertFieldFoldedIntoChecksum(buildings, resources, registry, w =>
            {
                int e = w.Create(new FixedVec3(Fixed.FromInt(1), Fixed.Zero, Fixed.FromInt(2)),
                                 Faction.Player1, Fixed.FromInt(10), Fixed.FromInt(3));
                return () => w.Elevation[e] = Fixed.FromInt(4);
            });

            // ── v11 (Story 3.13): the mutable HeroStore state is folded — minting a hero, then mutating Level / Xp /
            //    GrowthStacksApplied, each MUST move the hash. Rally-point-style dedicated teeth (the store lives outside
            //    EntityWorld, so the entity helper cannot reach it). ──
            AssertHeroStoreFoldedIntoChecksum(registry);

            // ── v12 (Story 3.15): the mutable ItemStore is folded — creating an item, then mutating DefId / Charges /
            //    PosX / PosZ / Held / CarrierHeroSlot, each MUST move the hash. Dedicated teeth (the store lives outside
            //    EntityWorld/HeroStore). ──
            AssertItemStoreFoldedIntoChecksum(registry);

            // ── v9 (Story 2.12, D-1): the per-building rally point is folded — HasRallyPoint AND the RallyPoint X/Z.
            //    Rally lives on BuildingStore (not EntityWorld), so it needs its own teeth (the EntityWorld helper above
            //    only mutates entity fields). Each of the three mixes must move the hash. ──
            AssertRallyPointFoldedIntoChecksum(registry);

            // ── v6 (Story 2.2b): the ModifierStore per-instance state is folded ──
            // Installing a modifier on a live entity MUST move the hash; advancing a tick (which changes
            // remainingTicks/ticksUntilPeriod) MUST move it again. A no-move means store state escaped the fold.
            AssertModifierStoreFoldedIntoChecksum(buildings, resources, registry);

            // ── v13 (Story 4.7): the mutable ResourceNodeStore is folded (first-ever fold of this store) ──
            AssertResourceNodeStoreFoldedIntoChecksum(registry);

            // ── v14 (Story 4.10): the mutable ResearchStore is folded (first-ever fold of this store) ──
            AssertResearchStoreFoldedIntoChecksum(registry);
        }

        /// <summary>
        /// Story 4.10 (v14) coverage teeth: the mutable <see cref="ResearchStore"/> state must move the
        /// checksum — the FIRST-EVER fold of this store. Grows the store's per-research inner arrays on an active
        /// faction slot, then mutates each folded field (InProgressIndex / RemainingTicks / CompletedLevels[idx][0] /
        /// each of the four cumulative deltas) in turn — each MUST move the hash. A no-move means a folded research
        /// field escaped <see cref="SimChecksum"/> (a silent desync surface, since <c>ResearchSystem</c> mutates all
        /// of these mid-match). Also proves: (a) a SECOND active faction's (Player2) mutation moves the hash
        /// INDEPENDENTLY of Player1's state (a hardcoded-index/mis-ordered-loop bug would fold only Player1 and pass
        /// every other assertion here undetected); (b) a SECOND research index (r=1) within the same faction folds
        /// too (an r&gt;0 indexing bug would pass with only one grown entry); (c) resetting an in-progress order back
        /// to idle (InProgressIndex=-1, RemainingTicks=0 — the state <c>CancelResearchCommand</c> leaves behind) also
        /// moves the hash, so the checksum genuinely reflects a cancel's effect and not just the forward direction.
        /// Lives outside EntityWorld/HeroStore/ItemStore/ResourceNodeStore, so it needs its own teeth (passed as the
        /// trailing Compute param), mirroring <see cref="AssertResourceNodeStoreFoldedIntoChecksum"/>.
        /// </summary>
        private static void AssertResearchStoreFoldedIntoChecksum(FactionRegistry registry)
        {
            var world     = new EntityWorld();          // empty — isolates the research contribution
            var resources = new ResourceStore(Fixed.Zero);
            var buildings = new BuildingStore();
            var research  = new ResearchStore();
            const int slot  = (int)Faction.Player1; // an active slot the loop reads
            const int slot2 = (int)Faction.Player2; // a SECOND active slot — proves the fold isn't Player1-only

            research.EnsureCapacity(Faction.Player1, 2); // two research entries so the r=1 index is also exercised
            research.EnsureCapacity(Faction.Player2, 1);

            uint empty = SimChecksum.Compute(world, buildings, resources, registry, null, null, null, null, research);

            research.InProgressIndex[slot] = 0;
            uint inProgressMoved = SimChecksum.Compute(world, buildings, resources, registry, null, null, null, null, research);
            Assert.True(empty != inProgressMoved,
                "ResearchStore.InProgressIndex is NOT folded into SimChecksum (v14).");

            research.RemainingTicks[slot] = 5;
            uint remainingMoved = SimChecksum.Compute(world, buildings, resources, registry, null, null, null, null, research);
            Assert.True(inProgressMoved != remainingMoved,
                "ResearchStore.RemainingTicks is NOT folded into SimChecksum (v14).");

            research.CompletedLevels[slot][0] = 1;
            uint completedMoved = SimChecksum.Compute(world, buildings, resources, registry, null, null, null, null, research);
            Assert.True(remainingMoved != completedMoved,
                "ResearchStore.CompletedLevels is NOT folded into SimChecksum (v14).");

            research.CumulativeMaxHealthDelta[slot][0] = Fixed.FromInt(10);
            uint hpMoved = SimChecksum.Compute(world, buildings, resources, registry, null, null, null, null, research);
            Assert.True(completedMoved != hpMoved,
                "ResearchStore.CumulativeMaxHealthDelta is NOT folded into SimChecksum (v14).");

            research.CumulativeAttackDamageDelta[slot][0] = Fixed.FromInt(3);
            uint atkMoved = SimChecksum.Compute(world, buildings, resources, registry, null, null, null, null, research);
            Assert.True(hpMoved != atkMoved,
                "ResearchStore.CumulativeAttackDamageDelta is NOT folded into SimChecksum (v14).");

            research.CumulativeMoveSpeedDelta[slot][0] = Fixed.FromInt(1);
            uint speedMoved = SimChecksum.Compute(world, buildings, resources, registry, null, null, null, null, research);
            Assert.True(atkMoved != speedMoved,
                "ResearchStore.CumulativeMoveSpeedDelta is NOT folded into SimChecksum (v14).");

            research.CumulativeArmorDelta[slot][0] = Fixed.FromInt(2);
            uint armorMoved = SimChecksum.Compute(world, buildings, resources, registry, null, null, null, null, research);
            Assert.True(speedMoved != armorMoved,
                "ResearchStore.CumulativeArmorDelta is NOT folded into SimChecksum (v14).");

            // ── r=1 (a SECOND research index on the SAME faction) — proves the inner loop isn't index-0-only ──
            research.CompletedLevels[slot][1] = 2;
            uint secondIndexMoved = SimChecksum.Compute(world, buildings, resources, registry, null, null, null, null, research);
            Assert.True(armorMoved != secondIndexMoved,
                "ResearchStore.CompletedLevels[.][1] (a second research index, r=1) is NOT folded into SimChecksum (v14) — the inner loop may be index-0-only.");

            // ── A SECOND active faction (Player2) — proves the outer loop isn't Player1-only ──
            research.InProgressIndex[slot2] = 0;
            uint secondFactionMoved = SimChecksum.Compute(world, buildings, resources, registry, null, null, null, null, research);
            Assert.True(secondIndexMoved != secondFactionMoved,
                "ResearchStore state on a SECOND active faction (Player2) is NOT folded into SimChecksum (v14) — the outer per-faction loop may be Player1-only.");

            research.CumulativeArmorDelta[slot2][0] = Fixed.FromInt(7);
            uint secondFactionDeltaMoved = SimChecksum.Compute(world, buildings, resources, registry, null, null, null, null, research);
            Assert.True(secondFactionMoved != secondFactionDeltaMoved,
                "ResearchStore.CumulativeArmorDelta on a SECOND active faction (Player2) is NOT folded into SimChecksum (v14).");

            // ── Cancel-shaped transition: reset Player1's in-progress order back to idle (what CancelResearchCommand
            //    leaves behind) — must ALSO move the hash, proving the fold reflects a cancel, not just the forward
            //    start/tick/complete direction. ──
            research.InProgressIndex[slot] = -1;
            research.RemainingTicks[slot]  = 0;
            uint cancelledMoved = SimChecksum.Compute(world, buildings, resources, registry, null, null, null, null, research);
            Assert.True(secondFactionDeltaMoved != cancelledMoved,
                "Resetting ResearchStore.InProgressIndex/RemainingTicks to idle (the CancelResearchCommand shape) does NOT move SimChecksum (v14).");
        }

        /// <summary>
        /// Story 4.7 (v13) coverage teeth: the mutable <see cref="ResourceNodeStore"/> state must move the
        /// checksum — the FIRST-EVER fold of this store. Creates a node, then mutates each folded field
        /// (SupplyRemaining / Active / AssignedGatherers / IncomeTicksElapsed) in turn — each MUST move the hash.
        /// A no-move means a folded node field escaped <see cref="SimChecksum"/> (a silent desync surface, since
        /// GatheringSystem mutates all four mid-match). Lives outside EntityWorld/HeroStore/ItemStore, so it needs
        /// its own teeth (passed as the trailing Compute param), mirroring <see cref="AssertItemStoreFoldedIntoChecksum"/>.
        /// </summary>
        private static void AssertResourceNodeStoreFoldedIntoChecksum(FactionRegistry registry)
        {
            var world     = new EntityWorld();          // empty — isolates the node contribution
            var resources = new ResourceStore(Fixed.Zero);
            var buildings = new BuildingStore();
            var nodes     = new ResourceNodeStore();

            uint empty = SimChecksum.Compute(world, buildings, resources, registry, null, null, null, nodes);

            int n = nodes.Create(new FixedVec3(Fixed.FromInt(5), Fixed.Zero, Fixed.FromInt(-4)),
                                 Fixed.FromInt(100), Fixed.FromInt(5), maxGatherers: 4);
            uint created = SimChecksum.Compute(world, buildings, resources, registry, null, null, null, nodes);
            Assert.True(empty != created,
                "Creating a resource node did NOT move the checksum — the ResourceNodeStore live count / rows are not folded into SimChecksum (v13).");

            nodes.SupplyRemaining[n] = Fixed.FromInt(50);
            uint supplyMoved = SimChecksum.Compute(world, buildings, resources, registry, null, null, null, nodes);
            Assert.True(created != supplyMoved, "ResourceNodeStore.SupplyRemaining is NOT folded into SimChecksum (v13).");

            nodes.Active[n] = false;
            uint activeMoved = SimChecksum.Compute(world, buildings, resources, registry, null, null, null, nodes);
            Assert.True(supplyMoved != activeMoved, "ResourceNodeStore.Active is NOT folded into SimChecksum (v13).");

            nodes.AssignedGatherers[n] = 2;
            uint assignedMoved = SimChecksum.Compute(world, buildings, resources, registry, null, null, null, nodes);
            Assert.True(activeMoved != assignedMoved, "ResourceNodeStore.AssignedGatherers is NOT folded into SimChecksum (v13).");

            nodes.IncomeTicksElapsed[n] = 7;
            uint incomeMoved = SimChecksum.Compute(world, buildings, resources, registry, null, null, null, nodes);
            Assert.True(assignedMoved != incomeMoved, "ResourceNodeStore.IncomeTicksElapsed is NOT folded into SimChecksum (v13).");
        }

        /// <summary>
        /// Story 2.2b coverage teeth: the <see cref="ModifierStore"/> instance state must move the checksum. Builds a
        /// live entity, hashes empty, installs a modifier (hash must move), then advances one tick so its countdown
        /// fields change (hash must move again). A no-move means a folded store field escaped <see cref="SimChecksum"/>.
        /// </summary>
        private static void AssertModifierStoreFoldedIntoChecksum(BuildingStore buildings, ResourceStore resources,
            FactionRegistry registry)
        {
            var world = new EntityWorld();
            var sys = new ModifierSystem();
            var store = new ModifierStore(world, sys);
            sys.AttachStore(store);
            int e = world.Create(new FixedVec3(Fixed.FromInt(1), Fixed.Zero, Fixed.FromInt(2)),
                                 Faction.Player1, Fixed.FromInt(50), Fixed.FromInt(3));

            uint empty = SimChecksum.Compute(world, buildings, resources, registry, store);

            // A finite stat modifier + a DoT → store slots become non-empty.
            store.Apply(e, new Modifier(1, 20, StackRule.Refresh, 1, Fixed.Zero, Fixed.FromInt(5), Fixed.Zero,
                                        StatusFlags.None, null, 0), e, Faction.Player1);
            store.InstallPersistent(e, new PersistentEffect(null, new DirectHpDeltaEffect(Fixed.FromInt(-1)), null, 3, 5),
                                    e, Faction.Player1);
            uint installed = SimChecksum.Compute(world, buildings, resources, registry, store);
            Assert.True(empty != installed,
                "ModifierStore install did NOT move the checksum — the store instance state is not folded into SimChecksum.");

            sys.Tick(world, Fixed.Zero); // advances ticksUntilPeriod / remainingTicks
            uint advanced = SimChecksum.Compute(world, buildings, resources, registry, store);
            Assert.True(installed != advanced,
                "Advancing the ModifierStore one tick did NOT move the checksum — countdown fields are not folded.");
        }

        /// <summary>
        /// Story 2.12 (D-1) coverage teeth: the per-building rally point must move the checksum. Builds an empty world
        /// + a single building, hashes with no rally, then (1) sets HasRallyPoint (hash must move) and (2) moves the
        /// RallyPoint X/Z (hash must move again). A no-move at either step means a rally field escaped the v9 fold — a
        /// silent desync surface, since SpawnTrainedUnit reads rally in-tick to send a trained unit Move→rally.
        /// </summary>
        private static void AssertRallyPointFoldedIntoChecksum(FactionRegistry registry)
        {
            var world     = new EntityWorld();          // empty — isolates the building contribution
            var resources = new ResourceStore(Fixed.Zero);
            var buildings = new BuildingStore();
            int b = buildings.Create(new FixedVec3(Fixed.FromInt(-14), Fixed.Zero, Fixed.Zero),
                                     Faction.Player1, BuildingType.Barracks);

            uint noRally = SimChecksum.Compute(world, buildings, resources, registry);

            // HasRallyPoint flips the folded flag.
            buildings.HasRallyPoint[b] = true;
            uint flagged = SimChecksum.Compute(world, buildings, resources, registry);
            Assert.True(noRally != flagged,
                "BuildingStore.HasRallyPoint is NOT folded into SimChecksum: setting it left the checksum unchanged (v9 D-1 fold).");

            // Moving each rally coordinate must move the hash — split X-alone then Z-alone (mirroring the OrderQueue
            // teeth above) so a fold that reads one coordinate but forgets the other still goes RED (review R4).
            buildings.RallyPoint[b] = new FixedVec3(Fixed.FromInt(9), Fixed.Zero, Fixed.Zero);
            uint movedX = SimChecksum.Compute(world, buildings, resources, registry);
            Assert.True(flagged != movedX,
                "BuildingStore.RallyPoint.X is NOT folded into SimChecksum: moving X alone left the checksum unchanged (v9 D-1 fold).");

            buildings.RallyPoint[b] = new FixedVec3(Fixed.FromInt(9), Fixed.Zero, Fixed.FromInt(-7));
            uint movedZ = SimChecksum.Compute(world, buildings, resources, registry);
            Assert.True(movedX != movedZ,
                "BuildingStore.RallyPoint.Z is NOT folded into SimChecksum: moving Z alone left the checksum unchanged (v9 D-1 fold).");
        }

        /// <summary>
        /// Story 3.13 (v11) coverage teeth: the mutable <see cref="HeroStore"/> state must move the checksum. Mints a
        /// hero, hashes, then mutates Level / Xp / GrowthStacksApplied in turn — each MUST move the hash. A no-move means
        /// a folded hero field escaped <see cref="SimChecksum"/> (a silent desync surface, since the XP runtime mutates
        /// Level/Xp mid-match). The hero is folded in <see cref="HeroStore.FoldOrder"/> order, count-driven.
        /// </summary>
        private static void AssertHeroStoreFoldedIntoChecksum(FactionRegistry registry)
        {
            var world     = new EntityWorld();          // empty — isolates the hero contribution
            var resources = new ResourceStore(Fixed.Zero);
            var buildings = new BuildingStore();
            var heroes    = new HeroStore();

            uint empty = SimChecksum.Compute(world, buildings, resources, registry, null, heroes);

            int slot = heroes.Mint(new HeroId(9_000_000_042UL), entityId: 3, level: 1, xp: Fixed.Zero);
            uint minted = SimChecksum.Compute(world, buildings, resources, registry, null, heroes);
            Assert.True(empty != minted,
                "Minting a hero did NOT move the checksum — the HeroStore live count / rows are not folded into SimChecksum (v11).");

            heroes.Level[slot] = 5;
            uint leveled = SimChecksum.Compute(world, buildings, resources, registry, null, heroes);
            Assert.True(minted != leveled,
                "HeroStore.Level is NOT folded into SimChecksum: changing it left the checksum unchanged (v11 fold).");

            heroes.Xp[slot] = Fixed.FromInt(123);
            uint xpMoved = SimChecksum.Compute(world, buildings, resources, registry, null, heroes);
            Assert.True(leveled != xpMoved,
                "HeroStore.Xp is NOT folded into SimChecksum: changing it left the checksum unchanged (v11 fold).");

            heroes.GrowthStacksApplied[slot] = 4;
            uint growthMoved = SimChecksum.Compute(world, buildings, resources, registry, null, heroes);
            Assert.True(xpMoved != growthMoved,
                "HeroStore.GrowthStacksApplied is NOT folded into SimChecksum: changing it left the checksum unchanged (v11 fold).");

            // Story 3.14 — the four reserved revival fields now mutate mid-match (death → awaiting → countdown → respawn),
            // so each must fold (they were declared + folded at defaults in v11; 3.14 needs no second bump). Coverage teeth.
            heroes.Alive3_14[slot] = false;
            uint aliveMoved = SimChecksum.Compute(world, buildings, resources, registry, null, heroes);
            Assert.True(growthMoved != aliveMoved,
                "HeroStore.Alive3_14 is NOT folded into SimChecksum: changing it left the checksum unchanged (v11 fold).");

            heroes.AwaitingRevival[slot] = true;
            uint awaitingMoved = SimChecksum.Compute(world, buildings, resources, registry, null, heroes);
            Assert.True(aliveMoved != awaitingMoved,
                "HeroStore.AwaitingRevival is NOT folded into SimChecksum: changing it left the checksum unchanged (v11 fold).");

            heroes.RevivalTimer[slot] = Fixed.FromInt(7);
            uint timerMoved = SimChecksum.Compute(world, buildings, resources, registry, null, heroes);
            Assert.True(awaitingMoved != timerMoved,
                "HeroStore.RevivalTimer is NOT folded into SimChecksum: changing it left the checksum unchanged (v11 fold).");

            heroes.RevivalLink[slot] = 9;
            uint linkMoved = SimChecksum.Compute(world, buildings, resources, registry, null, heroes);
            Assert.True(timerMoved != linkMoved,
                "HeroStore.RevivalLink is NOT folded into SimChecksum: changing it left the checksum unchanged (v11 fold).");

            // Story 3.15 (v12) — the per-hero inventory refs fold in the same hero-row loop. Changing one slot's ref MUST
            // move the hash (a pickup/drop mutates these). Fixed-stride (not count-driven), so any slot moves it.
            heroes.Inventory[slot * HeroStore.INVENTORY_SLOTS + 0] = 7;
            uint invMoved = SimChecksum.Compute(world, buildings, resources, registry, null, heroes);
            Assert.True(linkMoved != invMoved,
                "HeroStore.Inventory is NOT folded into SimChecksum: changing an inventory ref left the checksum unchanged (v12 fold).");
        }

        /// <summary>
        /// Story 3.15 (v12) coverage teeth: the mutable <see cref="ItemStore"/> state must move the checksum. Creates a
        /// ground item, then mutates each folded field (DefId / Charges / PosX / PosZ / Held / CarrierHeroSlot) in turn —
        /// each MUST move the hash. A no-move means a folded item field escaped <see cref="SimChecksum"/> (a silent desync
        /// surface, since pickup/use/drop mutate the store mid-match). The ItemStore lives outside EntityWorld/HeroStore,
        /// so it needs its own teeth (passed as the trailing Compute param).
        /// </summary>
        private static void AssertItemStoreFoldedIntoChecksum(FactionRegistry registry)
        {
            var world     = new EntityWorld();          // empty — isolates the item contribution
            var resources = new ResourceStore(Fixed.Zero);
            var buildings = new BuildingStore();
            var items     = new ItemStore();

            uint empty = SimChecksum.Compute(world, buildings, resources, registry, null, null, items);

            int itemRef = items.Create(defId: 2, charges: 3, new FixedVec3(Fixed.FromInt(5), Fixed.Zero, Fixed.FromInt(-4)));
            Assert.True(items.TryResolveRef(itemRef, out int s));
            uint created = SimChecksum.Compute(world, buildings, resources, registry, null, null, items);
            Assert.True(empty != created,
                "Creating an item did NOT move the checksum — the ItemStore live count / rows are not folded into SimChecksum (v12).");

            items.DefId[s] = 4;
            uint defMoved = SimChecksum.Compute(world, buildings, resources, registry, null, null, items);
            Assert.True(created != defMoved, "ItemStore.DefId is NOT folded into SimChecksum (v12).");

            items.Charges[s] = 1;
            uint chMoved = SimChecksum.Compute(world, buildings, resources, registry, null, null, items);
            Assert.True(defMoved != chMoved, "ItemStore.Charges is NOT folded into SimChecksum (v12).");

            items.PosX[s] = Fixed.FromInt(9);
            uint pxMoved = SimChecksum.Compute(world, buildings, resources, registry, null, null, items);
            Assert.True(chMoved != pxMoved, "ItemStore.PosX is NOT folded into SimChecksum (v12).");

            items.PosZ[s] = Fixed.FromInt(-9);
            uint pzMoved = SimChecksum.Compute(world, buildings, resources, registry, null, null, items);
            Assert.True(pxMoved != pzMoved, "ItemStore.PosZ is NOT folded into SimChecksum (v12).");

            items.Held[s] = true;
            uint heldMoved = SimChecksum.Compute(world, buildings, resources, registry, null, null, items);
            Assert.True(pzMoved != heldMoved, "ItemStore.Held is NOT folded into SimChecksum (v12).");

            items.CarrierHeroSlot[s] = 2;
            uint carrierMoved = SimChecksum.Compute(world, buildings, resources, registry, null, null, items);
            Assert.True(heldMoved != carrierMoved, "ItemStore.CarrierHeroSlot is NOT folded into SimChecksum (v12).");
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
        /// Build a small fixed world by hand and compute its v8 checksum. Fully self-contained: every hashed
        /// field is set explicitly with <see cref="Fixed"/> so the pinned hash does not silently depend on store
        /// constructor defaults a future story might change. The shared <see cref="SimRng"/> is reseeded to a
        /// fixed known value so the RNG fold is pinned independently of EntityWorld.DEFAULT_RNG_SEED. The v5
        /// separation fields are at their Create() defaults (CollisionRadius=1.0, SeparationPriorityOf=Normal), the
        /// v6 fields are at theirs (Effective* == Base / Energy == 0 / StatusFlagsOf == None) with an EMPTY
        /// <see cref="ModifierStore"/> (count 0 per entity), and the v7 ability fields are at theirs (AbilityCount == 0,
        /// no cooldowns) — so the hash moves from v6 purely by the added count mixes.
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

            // v6: pass an EMPTY ModifierStore (count 0 per entity) — the live host always passes a real store, so the
            // pin reflects the production fold path (null would hash identically via the ?? 0 count, but be explicit).
            // v11 (Story 3.13): pass an EMPTY HeroStore (no heroes → Mix(0) hero-count) — same explicit-production-path rationale.
            // v12 (Story 3.15): pass an EMPTY ItemStore (no items → Mix(0) item-count) — same explicit-production-path rationale.
            // v13 (Story 4.7): pass an EMPTY ResourceNodeStore (no nodes → Mix(0) node-count) — same rationale.
            // v14 (Story 4.10): pass an EMPTY ResearchStore (both active factions idle, no research authored) —
            // same explicit-production-path rationale.
            return SimChecksum.Compute(world, buildings, resources, new FactionRegistry(2), new ModifierStore(world), new HeroStore(), new ItemStore(), new ResourceNodeStore(), new ResearchStore());
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
