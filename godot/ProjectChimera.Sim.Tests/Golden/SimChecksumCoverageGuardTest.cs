#nullable enable
using System;
using System.Linq;
using System.Reflection;
using ProjectChimera.Core;
using ProjectChimera.Dsl;     // DslVarTable / DslVarDecl / DslTimerDecl (v16 fold coverage)
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
    ///   2. <see cref="KnownWorldState_ProducesPinnedV24Hash"/> — a snapshot/tripwire: a hand-built fixed world
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
        public void KnownWorldState_ProducesPinnedV24Hash()
        {
            // Algorithm version must be exactly 24 (Story 15-22 Phase C's RE-RECORD GENERATION MARKER — a bump
            // with NO fold change at all, on top of DW-78's bounded worker-gather-state fold at v23 and 11.6's
            // production-queue + head-timer fold at v22). If this fails, the const below is stale.
            Assert.Equal(24, SimChecksum.AlgoVersion);

            uint actual = ComputeKnownStateHash();

            // ── Pinned v24 hash for the fixed world built by ComputeKnownStateHash() ──────────────────────────
            // An intentional SimChecksum algorithm change must update this value AND bump SimChecksum.AlgoVersion.
            // The value below is DELIBERATELY UNCHANGED across two consecutive bumps now, for two DIFFERENT reasons,
            // and both are load-bearing:
            //   v22→v23: DW-78's fold is BOUNDED (an entity at the gatherer-inactive default folds ZERO Mix calls)
            //            and the known-state world holds NO gatherer, so the added fold was a no-op here. If a
            //            future edit gives that world a worker, this pin MUST move — correctly, not as a regression.
            //   v23→v24: Phase C changed NO fold whatsoever — twelve bounded corrections moved folded VALUES only.
            //            This pin is the instrument that PROVES that claim: the known world state is hand-built and
            //            touches none of the twelve code paths, so if v24 had quietly added/removed/reordered a
            //            folded field, this hash would have moved. It did not. Do not "re-pin to make it pass" —
            //            that would destroy the only standing evidence the Phase C fold set is intact.
            const uint ExpectedV24Hash = 0x32911831; // unchanged since v22 — see the two reasons above
            Assert.True(actual == ExpectedV24Hash,
                $"Known-state v24 checksum changed: expected 0x{ExpectedV24Hash:X8}, actual 0x{actual:X8}. " +
                $"If this is an INTENTIONAL algorithm change, re-pin ExpectedV24Hash to 0x{actual:X8} and bump " +
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

            // ── v22 (Story 11.6): the depth-5 production queue + head timer are folded — every queue slot AND the head
            //    ProductionTimer must move the hash. Lives on BuildingStore (not EntityWorld), so it needs its own teeth. ──
            AssertProductionQueueFoldedIntoChecksum(registry);

            // ── v6 (Story 2.2b): the ModifierStore per-instance state is folded ──
            // Installing a modifier on a live entity MUST move the hash; advancing a tick (which changes
            // remainingTicks/ticksUntilPeriod) MUST move it again. A no-move means store state escaped the fold.
            AssertModifierStoreFoldedIntoChecksum(buildings, resources, registry);

            // ── v13 (Story 4.7): the mutable ResourceNodeStore is folded (first-ever fold of this store) ──
            AssertResourceNodeStoreFoldedIntoChecksum(registry);

            // ── v14 (Story 4.10): the mutable ResearchStore is folded (first-ever fold of this store) ──
            AssertResearchStoreFoldedIntoChecksum(registry);

            // ── v16 (Story 7.3): the mutable DslVarTable is folded (first-ever fold of this store) ──
            AssertDslVarTableFoldedIntoChecksum(registry);

            // ── v16 follow-up (DW-341): Point-typed variables fold BOTH raw components (Raw0/X AND Raw1/Z) —
            //    teeth deferred until the Raw1 population path (Story 7.4's SetRaw) landed ──
            AssertDslPointVarsFoldedIntoChecksum(registry);

            // ── v17 (Story 7.6): declared arrays fold inside the DslVarTable, and the DslLoopState (batched
            //    continuation rows + per-tick fuel) folds after it ──
            AssertDslArraysFoldedIntoChecksum(registry);
            AssertDslLoopStateFoldedIntoChecksum(registry);

            // ── v18 (Story 7.5, landed via merge): the pending next-tick DslEventQueue is folded (first-ever
            //    fold of this store) ──
            AssertDslEventQueueFoldedIntoChecksum(registry);

            // ── v19 (Story 7.11): the mutable WinStateStore is folded (first-ever fold of this store) ──
            AssertWinStateStoreFoldedIntoChecksum(registry);

            // ── v20 (Story 7.12): the AllianceStore team-id mask is folded (first-ever fold of this store) ──
            AssertAllianceStoreFoldedIntoChecksum(registry);
        }

        /// <summary>
        /// Story 7.12 (v20) coverage teeth: the <see cref="AllianceStore"/> team-id mask must move the checksum — the
        /// FIRST-EVER fold of this store. A changed team id on Player1 AND independently on Player2 each move the hash
        /// (a fold reading only one slot, or ignoring the store, would hide a team divergence — and a peer with a
        /// different mask resolves last-team-standing victory differently, so it MUST desync detectably). Also proves
        /// the null≡default-FFA interchangeability promise (a null store folds byte-identically to a default mask,
        /// where team id == slot index — the DslEventQueue/WinStateStore null≡empty pattern applied to FFA).
        /// </summary>
        private static void AssertAllianceStoreFoldedIntoChecksum(FactionRegistry registry)
        {
            var world     = new EntityWorld();          // empty — isolates the store contribution
            var resources = new ResourceStore(Fixed.Zero);
            var buildings = new BuildingStore();

            // NAMED trailing arg (never positional) — pin the store to its parameter by NAME, immune to the next widening.
            static uint Hash(EntityWorld w, BuildingStore b, ResourceStore r, FactionRegistry reg, AllianceStore? a) =>
                SimChecksum.Compute(w, b, r, reg, alliances: a);

            var store = new AllianceStore();
            uint ffa = Hash(world, buildings, resources, registry, store);

            // Ally Player1 with Player2 (put both on team id == Player1's slot) — a changed Player2 team id moves the hash.
            var allied = new AllianceStore();
            allied.TeamId[(int)Faction.Player2] = (int)Faction.Player1;
            Assert.True(ffa != Hash(world, buildings, resources, registry, allied),
                "A changed Player2 team id did not move the checksum — the v20 AllianceStore fold is missing (or reads only one slot).");

            // A changed Player1 team id (independently) also moves the hash — proves the fold isn't Player2-only.
            var allied2 = new AllianceStore();
            allied2.TeamId[(int)Faction.Player1] = 3;
            Assert.True(ffa != Hash(world, buildings, resources, registry, allied2),
                "A changed Player1 team id did not move the checksum — the v20 fold reads only one faction slot.");

            // Null ≡ default FFA: a null AllianceStore folds byte-identically to a freshly-constructed (FFA) one.
            Assert.True(Hash(world, buildings, resources, registry, new AllianceStore())
                     == Hash(world, buildings, resources, registry, null),
                "A null AllianceStore does NOT fold byte-identically to a default-FFA store (v20 null≡FFA promise broken).");

            // AreAllied semantics teeth: FFA = no two distinct factions allied; a shared team id makes them allied;
            // a faction is always allied with itself. (Pure API — no checksum move, but the mask's meaning is load-bearing.)
            Assert.True(store.AreAllied(Faction.Player1, Faction.Player1), "A faction must be allied with itself.");
            Assert.False(store.AreAllied(Faction.Player1, Faction.Player2), "FFA default: distinct factions are NOT allied.");
            Assert.True(allied.AreAllied(Faction.Player1, Faction.Player2), "A shared team id must make two factions allied.");

            // Clear() restores FFA.
            allied.Clear();
            Assert.True(ffa == Hash(world, buildings, resources, registry, allied),
                "AllianceStore.Clear() did not restore the default-FFA fold shape.");
        }

        /// <summary>
        /// Story 7.6 (v17) coverage teeth: declared-array state must move the checksum. Declares an Int array,
        /// then (a) pushing an element, (b) mutating an element in place (array_set), and (c) clearing the array
        /// each MUST move the hash. Negative teeth: (d) a push AT CAPACITY is a deterministic no-op and must NOT
        /// move the hash (slots beyond the live count never fold), and (e) an OOB array_set is a no-op too.
        /// </summary>
        private static void AssertDslArraysFoldedIntoChecksum(FactionRegistry registry)
        {
            var world     = new EntityWorld();
            var resources = new ResourceStore(Fixed.Zero);
            var buildings = new BuildingStore();
            var vars      = new DslVarTable();
            vars.InitFromDeclarations(new[]
            {
                new DslVarDecl("arr", DslValueType.Array, VarScope.Global, 0, 0, elementType: DslValueType.Int, capacity: 2),
            }, System.Array.Empty<DslTimerDecl>());

            uint baseline = SimChecksum.Compute(world, buildings, resources, registry, null, null, null, null, null, vars);

            vars.ArrayPush("arr", 5);
            uint pushed = SimChecksum.Compute(world, buildings, resources, registry, null, null, null, null, null, vars);
            Assert.True(baseline != pushed, "DslVarTable array_push is NOT folded into SimChecksum (v17).");

            vars.ArraySet("arr", 0, 9);
            uint set = SimChecksum.Compute(world, buildings, resources, registry, null, null, null, null, null, vars);
            Assert.True(pushed != set, "DslVarTable array_set element mutation is NOT folded into SimChecksum (v17).");

            // Fill to capacity, then a push AT capacity must be a no-op (negative tooth: no silent state, no fold move).
            vars.ArrayPush("arr", 7);
            uint full = SimChecksum.Compute(world, buildings, resources, registry, null, null, null, null, null, vars);
            vars.ArrayPush("arr", 42); // capacity 2 → deterministic no-op
            uint afterOverflowPush = SimChecksum.Compute(world, buildings, resources, registry, null, null, null, null, null, vars);
            Assert.True(full == afterOverflowPush,
                "An array_push AT CAPACITY moved the checksum — it must be a deterministic no-op (v17).");

            vars.ArraySet("arr", 99, 1); // OOB → deterministic no-op
            uint afterOobSet = SimChecksum.Compute(world, buildings, resources, registry, null, null, null, null, null, vars);
            Assert.True(full == afterOobSet,
                "An out-of-bounds array_set moved the checksum — it must be a deterministic no-op (v17).");

            vars.ArrayClear("arr");
            uint cleared = SimChecksum.Compute(world, buildings, resources, registry, null, null, null, null, null, vars);
            Assert.True(full != cleared, "DslVarTable array_clear is NOT folded into SimChecksum (v17).");
        }

        /// <summary>
        /// Story 7.6 (v17) coverage teeth: the <see cref="DslLoopState"/> continuation rows + fuel counter must
        /// move the checksum. Configures one batched row, then (a) activating a snapshot, (b) appending snapshot
        /// ids, (c) advancing the cursor, (d) completing the row, and (e) charging fuel each MUST move the hash.
        /// Also proves the null ≡ empty FoldEmpty promise.
        /// </summary>
        private static void AssertDslLoopStateFoldedIntoChecksum(FactionRegistry registry)
        {
            var world     = new EntityWorld();
            var resources = new ResourceStore(Fixed.Zero);
            var buildings = new BuildingStore();
            var loop      = new DslLoopState();
            loop.ConfigureRows(new[] { 2 });

            uint configured = SimChecksum.Compute(world, buildings, resources, registry, null, null, null, null, null, null, loop);

            loop.BeginSnapshot(0);
            uint active = SimChecksum.Compute(world, buildings, resources, registry, null, null, null, null, null, null, loop);
            Assert.True(configured != active, "DslLoopState row ACTIVE flag is NOT folded into SimChecksum (v17).");

            loop.SnapshotAppend(0, 3);
            loop.SnapshotAppend(0, 7);
            uint snapped = SimChecksum.Compute(world, buildings, resources, registry, null, null, null, null, null, null, loop);
            Assert.True(active != snapped, "DslLoopState snapshot ids are NOT folded into SimChecksum (v17).");

            loop.SetCursor(0, 1);
            uint advanced = SimChecksum.Compute(world, buildings, resources, registry, null, null, null, null, null, null, loop);
            Assert.True(snapped != advanced, "DslLoopState row CURSOR is NOT folded into SimChecksum (v17).");

            loop.CompleteRow(0);
            uint completed = SimChecksum.Compute(world, buildings, resources, registry, null, null, null, null, null, null, loop);
            Assert.True(advanced != completed, "DslLoopState row completion is NOT folded into SimChecksum (v17).");

            loop.Charge(5);
            uint charged = SimChecksum.Compute(world, buildings, resources, registry, null, null, null, null, null, null, loop);
            Assert.True(completed != charged, "DslLoopState fuel consumed is NOT folded into SimChecksum (v17).");

            loop.ResetFuel();
            uint reset = SimChecksum.Compute(world, buildings, resources, registry, null, null, null, null, null, null, loop);
            Assert.True(reset == completed,
                "Resetting the fuel counter must restore the pre-charge fold (fuel folds by VALUE, v17).");

            // Null ≡ empty: a null DslLoopState folds byte-identically to a fresh (row-less, fuel-0) one.
            uint withEmpty = SimChecksum.Compute(world, buildings, resources, registry, null, null, null, null, null, null, new DslLoopState());
            uint withNull  = SimChecksum.Compute(world, buildings, resources, registry, null, null, null, null, null, null, null);
            Assert.True(withEmpty == withNull,
                "A null DslLoopState does NOT fold byte-identically to an empty one (v17 null≡empty promise broken).");
        }

        /// <summary>
        /// Story 7.5 (v18, landed via merge) coverage teeth: the pending next-tick <see cref="DslEventQueue"/>
        /// must move the checksum — the FIRST-EVER fold of this store. Enqueuing an event moves the hash (the
        /// count + entry fold); a different EVENT INDEX, a different RAISER, and a different PARAM RAW each move
        /// it independently (a fold reading only the count would pass the first assertion and hide a payload
        /// divergence — a silent desync surface, since next-tick feedback is live cross-tick sim state). Also
        /// proves the null≡empty interchangeability promise (the DslVarTable v16 pattern).
        /// </summary>
        private static void AssertDslEventQueueFoldedIntoChecksum(FactionRegistry registry)
        {
            var world     = new EntityWorld();          // empty — isolates the queue contribution
            var resources = new ResourceStore(Fixed.Zero);
            var buildings = new BuildingStore();

            // NAMED trailing arg (never positional) — the Compute tail has widened on every DSL story
            // (vars → loopState → dslEvents), so pin the queue to its parameter by NAME and stay immune
            // to the next widening.
            static uint Hash(EntityWorld w, BuildingStore b, ResourceStore r, FactionRegistry reg, DslEventQueue? q) =>
                SimChecksum.Compute(w, b, r, reg, dslEvents: q);

            var queue = new DslEventQueue();
            uint empty = Hash(world, buildings, resources, registry, queue);

            Assert.True(queue.Enqueue(0, -1, new[] { 5, 0, 0, 0 }, 1));
            uint enqueued = Hash(world, buildings, resources, registry, queue);
            Assert.True(empty != enqueued,
                "Enqueuing a next-tick event did NOT move the checksum — the DslEventQueue is not folded into SimChecksum (v18).");

            var queueOtherEvent = new DslEventQueue();
            queueOtherEvent.Enqueue(1, -1, new[] { 5, 0, 0, 0 }, 1);
            Assert.True(enqueued != Hash(world, buildings, resources, registry, queueOtherEvent),
                "A different pending EVENT INDEX did not move the checksum — the v18 fold is not reading the event index.");

            var queueOtherRaiser = new DslEventQueue();
            queueOtherRaiser.Enqueue(0, 2, new[] { 5, 0, 0, 0 }, 1);
            Assert.True(enqueued != Hash(world, buildings, resources, registry, queueOtherRaiser),
                "A different pending RAISER did not move the checksum — the v18 fold is not reading the raiser slot.");

            var queueOtherParam = new DslEventQueue();
            queueOtherParam.Enqueue(0, -1, new[] { 6, 0, 0, 0 }, 1);
            Assert.True(enqueued != Hash(world, buildings, resources, registry, queueOtherParam),
                "A different pending PARAM RAW did not move the checksum — the v18 fold is not reading the payload stride.");

            // Clearing (the tick-start dequeue) returns the fold to the empty shape.
            queue.Clear();
            Assert.True(empty == Hash(world, buildings, resources, registry, queue),
                "A cleared DslEventQueue does not fold like an empty one (the tick-start dequeue would leave residue).");

            // Null ≡ empty (the DslVarTable v16 promise, applied to the queue).
            Assert.True(Hash(world, buildings, resources, registry, new DslEventQueue())
                     == Hash(world, buildings, resources, registry, null),
                "A null DslEventQueue does NOT fold byte-identically to an empty queue (v18 null≡empty promise broken).");
        }

        /// <summary>
        /// Story 7.11 (v19) coverage teeth: the mutable <see cref="WinStateStore"/> state must move the checksum —
        /// the FIRST-EVER fold of this store. The scalar MatchTicks moves it; then each PER-FACTION field
        /// (KothHoldTicks / SurvivalRemaining / Verdict) mutated on Player1 AND independently on Player2 moves it (a
        /// fold reading only Player1's slot, or only the scalar, would hide a per-faction divergence). Also proves
        /// the null≡empty interchangeability promise (the DslEventQueue v18 pattern).
        /// </summary>
        private static void AssertWinStateStoreFoldedIntoChecksum(FactionRegistry registry)
        {
            var world     = new EntityWorld();          // empty — isolates the store contribution
            var resources = new ResourceStore(Fixed.Zero);
            var buildings = new BuildingStore();

            // NAMED trailing arg (never positional) — pin the store to its parameter by NAME, immune to the next widening.
            static uint Hash(EntityWorld w, BuildingStore b, ResourceStore r, FactionRegistry reg, WinStateStore? s) =>
                SimChecksum.Compute(w, b, r, reg, winState: s);

            var store = new WinStateStore();
            uint empty = Hash(world, buildings, resources, registry, store);

            store.MatchTicks = 42;
            uint ticked = Hash(world, buildings, resources, registry, store);
            Assert.True(empty != ticked,
                "Advancing WinStateStore.MatchTicks did NOT move the checksum — the scalar grace counter is not folded (v19).");

            var koth1 = new WinStateStore(); koth1.KothHoldTicks[(int)Faction.Player1] = 7;
            Assert.True(empty != Hash(world, buildings, resources, registry, koth1),
                "A Player1 KothHoldTicks change did not move the checksum — the v19 per-faction fold is missing.");
            var koth2 = new WinStateStore(); koth2.KothHoldTicks[(int)Faction.Player2] = 7;
            Assert.True(empty != Hash(world, buildings, resources, registry, koth2),
                "A Player2 KothHoldTicks change did not move the checksum — the v19 fold reads only one faction slot.");

            var surv1 = new WinStateStore(); surv1.SurvivalRemaining[(int)Faction.Player1] = 300;
            Assert.True(empty != Hash(world, buildings, resources, registry, surv1),
                "A Player1 SurvivalRemaining change did not move the checksum — the v19 fold is missing the survival countdown.");
            var surv2 = new WinStateStore(); surv2.SurvivalRemaining[(int)Faction.Player2] = 300;
            Assert.True(empty != Hash(world, buildings, resources, registry, surv2),
                "A Player2 SurvivalRemaining change did not move the checksum — the v19 fold reads only one faction slot.");

            var verdict1 = new WinStateStore(); verdict1.Verdict[(int)Faction.Player1] = WinStateStore.VERDICT_WON;
            Assert.True(empty != Hash(world, buildings, resources, registry, verdict1),
                "A Player1 Verdict latch did not move the checksum — the v19 fold is missing the verdict field.");
            var verdict2 = new WinStateStore(); verdict2.Verdict[(int)Faction.Player2] = WinStateStore.VERDICT_LOST;
            Assert.True(empty != Hash(world, buildings, resources, registry, verdict2),
                "A Player2 Verdict latch did not move the checksum — the v19 fold reads only one faction slot.");

            // Clearing returns the fold to the empty shape.
            store.Clear();
            Assert.True(empty == Hash(world, buildings, resources, registry, store),
                "A cleared WinStateStore does not fold like an empty one (Clear left residue).");

            // Null ≡ empty (the DslEventQueue v18 promise, applied to the win-state store).
            Assert.True(Hash(world, buildings, resources, registry, new WinStateStore())
                     == Hash(world, buildings, resources, registry, null),
                "A null WinStateStore does NOT fold byte-identically to an empty store (v19 null≡empty promise broken).");
        }

        /// <summary>
        /// Story 7.3 (v16) coverage teeth: the mutable <see cref="DslVarTable"/> state must move the checksum — the
        /// FIRST-EVER fold of this store. Declares a Global Int var, a Per-player Int var, and a timer, then mutates
        /// each folded surface in turn — each MUST move the hash. A no-move means a folded variable/timer escaped
        /// <see cref="SimChecksum"/> (a silent desync surface, since triggers mutate all of these mid-match). Also
        /// proves: (a) an UNDECLARED set_variable append (a runtime-grown Global slot) folds; (b) a Per-player write
        /// on a SECOND active faction (Player2) folds INDEPENDENTLY of Player1's slot; (c) a trigger-local write is
        /// NEVER folded (Enter/write/Exit leaves the hash unchanged). The table lives outside EntityWorld, so it needs
        /// its own teeth (passed as the trailing Compute param), mirroring <see cref="AssertResearchStoreFoldedIntoChecksum"/>.
        /// </summary>
        private static void AssertDslVarTableFoldedIntoChecksum(FactionRegistry registry)
        {
            var world     = new EntityWorld();          // empty — isolates the DslVarTable contribution
            var resources = new ResourceStore(Fixed.Zero);
            var buildings = new BuildingStore();
            var vars      = new DslVarTable();
            vars.InitFromDeclarations(new[]
            {
                new DslVarDecl("g",  DslValueType.Int, VarScope.Global,       0),
                new DslVarDecl("pp", DslValueType.Int, VarScope.PerPlayer,    0),
                new DslVarDecl("tl", DslValueType.Int, VarScope.TriggerLocal, 0),
            }, new[] { new DslTimerDecl("t", 10) });

            uint baseline = SimChecksum.Compute(world, buildings, resources, registry, null, null, null, null, null, vars);

            vars.SetInt("g", 0, 5);
            uint gMoved = SimChecksum.Compute(world, buildings, resources, registry, null, null, null, null, null, vars);
            Assert.True(baseline != gMoved, "DslVarTable Global variable is NOT folded into SimChecksum (v16).");

            // Per-player slot 0 (Player1, active) — must move the hash.
            vars.SetInt("pp", 0, 7);
            uint ppMoved = SimChecksum.Compute(world, buildings, resources, registry, null, null, null, null, null, vars);
            Assert.True(gMoved != ppMoved, "DslVarTable Per-player variable (Player1) is NOT folded into SimChecksum (v16).");

            // Per-player slot 1 (Player2, a SECOND active faction) — proves the per-player fold isn't Player1-only.
            vars.SetInt("pp", 1, 9);
            uint pp2Moved = SimChecksum.Compute(world, buildings, resources, registry, null, null, null, null, null, vars);
            Assert.True(ppMoved != pp2Moved, "DslVarTable Per-player variable on a SECOND active faction (Player2) is NOT folded into SimChecksum (v16).");

            // P2: a write to an INACTIVE player slot (faction 5 — NOT in the 2-player active set {0,1}) must STILL
            // fold. SetInt/ClampSlot can target ANY slot 0..7, so the v16 fold covers every slot, not only active
            // factions — otherwise a write to an inactive slot would silently escape the checksum (a desync surface).
            // Under the pre-fix active-slots-only fold this write left the hash UNCHANGED; now it must move it.
            vars.SetInt("pp", 5, 13);
            uint inactiveSlotMoved = SimChecksum.Compute(world, buildings, resources, registry, null, null, null, null, null, vars);
            Assert.True(pp2Moved != inactiveSlotMoved,
                "DslVarTable Per-player write to an INACTIVE slot (faction 5) is NOT folded into SimChecksum (v16) — the fold must cover EVERY slot 0..7, not only active factions.");

            // Timer remaining-ticks — must move the hash.
            vars.TimerSet("t", 3);
            uint timerMoved = SimChecksum.Compute(world, buildings, resources, registry, null, null, null, null, null, vars);
            Assert.True(inactiveSlotMoved != timerMoved, "DslVarTable timer remaining-ticks is NOT folded into SimChecksum (v16).");

            // An UNDECLARED set_variable append (a runtime-grown Global/Int slot) must fold too.
            vars.SetInt("undeclared", 0, 11);
            uint undeclMoved = SimChecksum.Compute(world, buildings, resources, registry, null, null, null, null, null, vars);
            Assert.True(timerMoved != undeclMoved, "An undeclared set_variable append (runtime Global slot) is NOT folded into SimChecksum (v16).");

            // A TriggerLocal write must NEVER fold: Enter/write/Exit leaves the hash exactly where it was.
            uint beforeLocal = undeclMoved;
            vars.Enter();
            vars.SetInt("tl", 0, 999);
            vars.Exit();
            uint afterLocal = SimChecksum.Compute(world, buildings, resources, registry, null, null, null, null, null, vars);
            Assert.True(beforeLocal == afterLocal, "A TriggerLocal write MOVED the checksum — trigger-local scratch must never fold (v16).");

            // P2: a NULL DslVarTable must fold BYTE-IDENTICALLY to a non-null EMPTY table — the two are
            // interchangeable in Compute (production always passes a real table; legacy/test callers may pass null).
            uint withEmpty = SimChecksum.Compute(world, buildings, resources, registry, null, null, null, null, null, new DslVarTable());
            uint withNull  = SimChecksum.Compute(world, buildings, resources, registry, null, null, null, null, null, null);
            Assert.True(withEmpty == withNull,
                "A null DslVarTable does NOT fold byte-identically to an empty table (v16 null≡empty promise broken).");
        }

        /// <summary>
        /// DW-341 (Story 7.3 v16 follow-up) coverage teeth: a Point-typed variable must fold BOTH raw components.
        /// The v16 fold mixes Raw0 for every folded slot but Raw1 (the Point Z lane) ONLY for Point-typed slots —
        /// and in 7.3 nothing could populate Raw1, so the original teeth above are Int-only and could not catch a
        /// future Point write escaping the checksum. The population path has since landed (Story 7.4's
        /// <see cref="DslVarTable.SetRaw"/> — reachable from compiled expressions and the 11.3 save-restore overlay),
        /// so these teeth close the gap: on a declared Global Point AND a declared PerPlayer Point, moving X (Raw0)
        /// alone moves the hash, then moving Z (Raw1) ALONE moves it again — the Z-only step is precisely the write
        /// a Raw0-only fold would miss (a silent Point-Z desync surface). The PerPlayer Z tooth also runs on a
        /// SECOND player slot so the per-slot inner loop cannot mix Raw1 for slot 0 only.
        /// </summary>
        private static void AssertDslPointVarsFoldedIntoChecksum(FactionRegistry registry)
        {
            var world     = new EntityWorld();          // empty — isolates the DslVarTable contribution
            var resources = new ResourceStore(Fixed.Zero);
            var buildings = new BuildingStore();

            // NAMED trailing arg (never positional) — the DslEventQueue/AllianceStore precedent: pin the table to
            // its parameter by NAME, immune to the next Compute-tail widening.
            static uint Hash(EntityWorld w, BuildingStore b, ResourceStore r, FactionRegistry reg, DslVarTable v) =>
                SimChecksum.Compute(w, b, r, reg, vars: v);

            var vars = new DslVarTable();
            vars.InitFromDeclarations(new[]
            {
                new DslVarDecl("gpt", DslValueType.Point, VarScope.Global,    0, 0),
                new DslVarDecl("ppt", DslValueType.Point, VarScope.PerPlayer, 0, 0),
            }, System.Array.Empty<DslTimerDecl>());

            uint baseline = Hash(world, buildings, resources, registry, vars);

            // Global Point: X (Raw0) alone moves the hash…
            vars.SetRaw("gpt", 0, Fixed.FromInt(3).Raw, 0);
            uint gx = Hash(world, buildings, resources, registry, vars);
            Assert.True(baseline != gx,
                "A Global Point variable's X (Raw0) write did not move the checksum — the v16 fold is not reading Point Raw0.");

            // …then Z (Raw1) ALONE moves it again — the lane a Raw0-only fold would silently drop (DW-341).
            vars.SetRaw("gpt", 0, Fixed.FromInt(3).Raw, Fixed.FromInt(7).Raw);
            uint gz = Hash(world, buildings, resources, registry, vars);
            Assert.True(gx != gz,
                "A Global Point variable's Z (Raw1) lane is NOT folded into SimChecksum (v16) — a Point Z divergence would desync silently (DW-341).");

            // PerPlayer Point, slot 0: X alone…
            vars.SetRaw("ppt", 0, Fixed.FromInt(-2).Raw, 0);
            uint px = Hash(world, buildings, resources, registry, vars);
            Assert.True(gz != px,
                "A PerPlayer Point variable's X (Raw0) write did not move the checksum — the v16 per-player fold is not reading Point Raw0.");

            // …then Z ALONE on the same slot.
            vars.SetRaw("ppt", 0, Fixed.FromInt(-2).Raw, Fixed.FromInt(11).Raw);
            uint pz = Hash(world, buildings, resources, registry, vars);
            Assert.True(px != pz,
                "A PerPlayer Point variable's Z (Raw1) lane is NOT folded into SimChecksum (v16) (DW-341).");

            // Z ALONE on a SECOND player slot (slot 3, an inactive faction in the 2-player registry) — proves the
            // per-slot inner loop mixes Raw1 for EVERY slot 0..7 (the v16 all-slots contract), not slot 0 only.
            vars.SetRaw("ppt", 3, 0, Fixed.FromInt(5).Raw);
            uint pz3 = Hash(world, buildings, resources, registry, vars);
            Assert.True(pz != pz3,
                "A PerPlayer Point Z (Raw1) write on a SECOND slot (3) is NOT folded — the per-slot Raw1 mix may be slot-0-only (DW-341).");
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
        /// Story 11.6 (v22) coverage teeth: the depth-5 production queue + head timer must move the checksum — the
        /// FIRST-EVER fold of this store's queue/timer (the 2.8 depth-1 byte was left unfolded-while-dormant). Builds
        /// an empty world + a single producer, hashes with an empty queue, then mutates EACH of the QUEUE_DEPTH slots
        /// in turn (head AND every waiting slot) AND the head ProductionTimer — each MUST move the hash. A no-move at
        /// any slot means a fold that reads only the head (or only some slots) would hide a queue divergence — a silent
        /// desync surface, since the queue now feeds ResourceStore via cancel/refund and drives what spawns.
        /// </summary>
        private static void AssertProductionQueueFoldedIntoChecksum(FactionRegistry registry)
        {
            var world     = new EntityWorld();          // empty — isolates the building contribution
            var resources = new ResourceStore(Fixed.Zero);
            var buildings = new BuildingStore();
            int b = buildings.Create(new FixedVec3(Fixed.FromInt(-14), Fixed.Zero, Fixed.Zero),
                                     Faction.Player1, BuildingType.Barracks);
            int head = buildings.HeadIndex(b);

            uint prev = SimChecksum.Compute(world, buildings, resources, registry);

            // Every one of the QUEUE_DEPTH slots must independently move the hash (a fold reading only the head — or
            // only a subset — would pass on slot 0 and silently hide a waiting-slot divergence).
            for (int k = 0; k < BuildingStore.QUEUE_DEPTH; k++)
            {
                buildings.ProductionQueue[head + k] = (byte)(k + 1);
                uint moved = SimChecksum.Compute(world, buildings, resources, registry);
                Assert.True(prev != moved,
                    $"BuildingStore.ProductionQueue slot {k} is NOT folded into SimChecksum: setting it left the checksum unchanged (v22 fold).");
                prev = moved;
            }

            // The head ProductionTimer (the completion countdown) must also move the hash — it was never folded before v22.
            buildings.ProductionTimer[b] = Fixed.FromInt(3);
            uint timerMoved = SimChecksum.Compute(world, buildings, resources, registry);
            Assert.True(prev != timerMoved,
                "BuildingStore.ProductionTimer is NOT folded into SimChecksum: moving the head timer left the checksum unchanged (v22 fold).");
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
            // v16 (Story 7.3): pass an EMPTY DslVarTable (no declared vars/timers) — the fold adds Mix(0) global-count
            // + Mix(0) timer-count (per active faction the per-player loop is empty), same explicit-production-path rationale.
            // v17 (Story 7.6): pass an EMPTY DslLoopState (no batched rows, zero fuel) — same explicit-production-path rationale.
            // v18 (Story 7.5, landed via merge): pass an EMPTY DslEventQueue (no pending next-tick events) — the
            // fold adds one Mix(0) count, same explicit-production-path rationale.
            // v19 (Story 7.11): pass an EMPTY WinStateStore (MatchTicks == 0, no KotH/survival counters, no verdict)
            // — the fold adds Mix(0) MatchTicks + the per-active-faction Mix(0) triples, same explicit-production-path rationale.
            return SimChecksum.Compute(world, buildings, resources, new FactionRegistry(2), new ModifierStore(world), new HeroStore(), new ItemStore(), new ResourceNodeStore(), new ResearchStore(), new DslVarTable(), new DslLoopState(), new DslEventQueue(), new WinStateStore());
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
