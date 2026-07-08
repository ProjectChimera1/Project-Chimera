#nullable enable
using ProjectChimera.Effects; // ModifierStore (Option B fold param, Story 2.2b) + StatusFlags — same sim layer

namespace ProjectChimera.Core
{
    /// <summary>
    /// Computes a deterministic FNV-1a checksum over the full simulation world state.
    ///
    /// Used for desync detection in deterministic lockstep multiplayer (P2.4).
    /// Both peers compute this every N ticks and compare; a mismatch indicates divergence.
    ///
    /// Hashed state (in order, ascending entity ID):
    ///   - EntityWorld: Position (X, Y, Z) and Health for every alive entity
    ///   - BuildingStore: Alive flag, Health, ConstructionTimer for every building slot
    ///   - ResourceStore: Ore, Crystal, SupplyUsed, SupplyCap, FactionBase for each active
    ///     faction (via FactionRegistry, ascending)
    ///   - SimRng: the shared generator's 64-bit State (low 32 bits then high 32 bits) — added v3 (Story 1.5)
    ///   - EntityWorld command state: per alive entity, CommandTarget + the patrol-route ring (PatrolCount,
    ///     PatrolIndex, PatrolDir, then count-driven PatrolWaypoints X/Y/Z) — added v4 (Story 1.12)
    ///   - EntityWorld separation config: per alive entity, CollisionRadius (Raw) + SeparationPriorityOf (int) —
    ///     added v5 (Story 1.13). CategoryOf is deliberately NOT hashed (presentation-read, like MeshType).
    ///   - EntityWorld attack delivery: per alive entity, Delivery (int) + ProjectileSpeed (Raw) — added v10
    ///     (Story 3.12), the authorable Hitscan/Projectile axis + per-unit projectile speed that combat reads in-tick.
    ///   - EntityWorld XP bounty: per alive entity, XpBounty (Raw) — added v11 (Story 3.13), the def-derived XP a unit
    ///     awards on death (HeroXpSystem reads it via the DeathFeed).
    ///   - HeroStore mutable state: ascending HeroId (FoldOrder) — live count, then per slot HeroId.Value (low/high),
    ///     Level, Xp (Raw), GrowthStacksApplied, and the reserved Story 3.14 revival fields — added v11 (Story 3.13),
    ///     now that the XP runtime mutates Level/Xp mid-match (dormant/not-folded since 3.2). A null store folds Mix(0).
    ///   - EntityWorld effective stats + ability/status: per alive entity, EffectiveAttackDamage, EffectiveMaxHealth,
    ///     EffectiveMoveSpeed, Energy (all Raw), and StatusFlagsOf (int) — added v6 (Story 2.2b), now that the
    ///     ModifierStore MUTATES them mid-match. Base* stays UNFOLDED (authored, in-tick-immutable).
    ///   - EntityWorld effective armor: per alive entity, EffectiveArmor (Raw) — added v8 (Story 2.6), the buffable
    ///     armor stat. ModifierSystem recomputes it mid-match (an aura grants +armor; DamageResolver subtracts it),
    ///     so it is peer-divergent sim truth. BaseArmor stays UNFOLDED (authored, the BaseAttackDamage posture).
    ///   - ModifierStore: per alive entity (ascending owner-id then slot), the active-instance count then, per slot,
    ///     modifierId / remainingTicks / ticksUntilPeriod / periodsRemaining / stackCount — added v6 (Story 2.2b).
    ///     The descriptor refs + caster id/faction are NOT folded (authored / peer-identical). A null store folds an
    ///     identical Mix(0) count per entity (≡ an empty store), so legacy callers and an empty store agree.
    ///   - EntityWorld ability cooldowns: per alive entity, AbilityCount then count-driven AbilityCooldownTicks
    ///     (int ticks) — added v7 (Story 2.4a), the first ability array that mutates mid-match (a cast starts a
    ///     cooldown; it ticks down each frame). AbilityId / MaxEnergy / PendingCast* are NOT hashed (authored /
    ///     transient); AbilityCount is folded ONLY as the cross-platform-safe count-driven loop bound (like PatrolCount).
    ///   - EntityWorld shift-queue: per alive entity, OrderQueueCount then count-driven OrderQueueCmd/TargetX/TargetZ
    ///     — added v9 (Story 2.12), the per-entity pending-order ring (OrderApplier appends a Shift order; OrderQueueSystem
    ///     pops the head on completion) → runtime-mutable sim truth. Count-driven (only populated slots hashed).
    ///   - BuildingStore rally point: per building, HasRallyPoint then RallyPoint X/Z — added v9 (Story 2.12, D-1), now
    ///     that rally is wire-driven (UnitCommand.SetRally) and read in-tick by SpawnTrainedUnit (mutable sim truth).
    ///
    /// Versioned by <see cref="AlgoVersion"/> — bump on any change to the hashed set/order
    /// (forces an intentional golden re-baseline). MatchStats is deliberately NOT hashed
    /// (private, write-only scoreboard derived from already-hashed deaths — observational only).
    ///
    /// All values are Fixed (int Raw) — platform-independent, no float arithmetic.
    /// </summary>
    public static class SimChecksum
    {
        // FNV-1a 32-bit constants
        private const uint FNV_OFFSET = 2166136261u;
        private const uint FNV_PRIME  = 16777619u;

        /// <summary>
        /// Version of the checksum ALGORITHM (which sim state is hashed, and in what order) — distinct from
        /// the 32-bit hash width. Stamped into every golden header so a baseline self-identifies, and pinned
        /// by the known-state guard test. Bump this by exactly one whenever the hashed set/order changes, and
        /// re-baseline the goldens in the SAME commit.
        ///   v1 — implicit, pre-1.3b: Ore only, per active faction (Stories 1.1–1.3a).
        ///   v2 — Story 1.3b: full per-faction coverage (Ore, Crystal, SupplyUsed, SupplyCap, FactionBase).
        ///   v3 — Story 1.5: fold the shared SimRng.State (low then high 32 bits) so a divergent RNG stream desyncs.
        ///   v4 — Story 1.12: fold per-entity CommandTarget + the patrol-route ring (PatrolCount/Index/Dir +
        ///        count-driven PatrolWaypoints) so the full RTS command vocabulary is hashed sim truth.
        ///   v5 — Story 1.13: fold per-entity CollisionRadius + SeparationPriorityOf (separation config is sim
        ///        truth — a peer divergence in either changes movement and must desync detectably). CategoryOf is
        ///        NOT folded (presentation-read formation input; its effect reaches the hash via Position).
        ///   v6 — Story 2.2b: the ModifierStore now mutates Effective* / Energy / StatusFlagsOf mid-match, so fold
        ///        per-entity EffectiveAttackDamage/EffectiveMaxHealth/EffectiveMoveSpeed/Energy + StatusFlagsOf, AND
        ///        the ModifierStore per-instance state (count-driven, ascending owner-id then slot). Base* stays
        ///        UNFOLDED (authored, in-tick-immutable). The ONE scheduled re-baseline of all goldens.
        ///   v7 — Story 2.4a: fold per-entity AbilityCooldownTicks (count-driven by AbilityCount, ascending slot) —
        ///        the first ability array that mutates mid-match (a cast starts the cooldown; it ticks down each
        ///        frame). AbilityId / MaxEnergy / PendingCast* stay UNFOLDED (authored / transient). The fold is
        ///        count-driven, so raising EntityWorld.MAX_ABILITIES_PER_UNIT later moves no golden. One scheduled re-baseline.
        ///   v8 — Story 2.6: fold per-entity EffectiveArmor — the buffable armor stat (Decision #6). The
        ///        ModifierSystem recomputes it mid-match (an aura grants +armor) and DamageResolver subtracts it,
        ///        so it is peer-divergent sim truth. BaseArmor stays UNFOLDED (authored, the BaseAttackDamage
        ///        posture). The passive DRIVERS (aura / on-hit / self-passive) add NO new folded state — they reuse
        ///        ModifierStore v6 / Health / Effective* and register passives via authored, not-folded SoA. One
        ///        scheduled re-baseline of all goldens (the existing combat is unchanged since BaseArmor=0 → −0).
        ///   v9 — Story 2.12: fold (a) the per-entity shift-queued order ring (OrderQueueCount + count-driven
        ///        OrderQueueCmd/TargetX/TargetZ) — runtime-mutable pending orders (append/pop mid-match); AND
        ///        (b) the per-building rally point (HasRallyPoint + RallyPoint X/Z) — now wire-driven (UnitCommand.SetRally)
        ///        and read in-tick by SpawnTrainedUnit, so genuinely mutable sim truth (D-1). Both count/flag-driven,
        ///        all int/Fixed.Raw → cross-platform. One scheduled re-baseline of ALL goldens (the known-state world
        ///        has an empty queue + no rally, so the pin moves purely by the added Mix(0) per entity/building).
        ///   v10 — Story 3.12: fold per-entity Delivery ((int) — Hitscan/Projectile) + ProjectileSpeed (.Raw) — the
        ///        authorable attack-delivery axis. Like AttackRange/SplashRadius/CategoryOf these are authored spawn-
        ///        constants whose effect also reaches the hash transitively (via Position/Health), but the story MANDATES
        ///        a direct fold: CombatSystem branches instant-vs-projectile on Delivery, and ProjectileSystem advances at
        ///        ProjectileSpeed, so a peer divergence in either changes combat resolution and must desync detectably.
        ///        Both int/Fixed.Raw → cross-platform safe. One scheduled re-baseline of ALL goldens (existing units keep
        ///        their exact behaviour — Delivery infers the old MELEE_THRESHOLD partition, ProjectileSpeed defaults to 18).
        ///   v11 — Story 3.13: the XP runtime first MUTATES HeroStore.Level/Xp mid-match, so fold (a) the per-entity
        ///        def-derived XpBounty (.Raw) in the entity loop (the Story 3.12 spawn-constant convention), AND (b) the
        ///        mutable HeroStore state — ascending HeroId (FoldOrder): live count, then per slot HeroId.Value (low/high),
        ///        Level, Xp.Raw, GrowthStacksApplied, plus Story 3.14's RESERVED revival fields (Alive3_14/AwaitingRevival
        ///        as int, RevivalTimer.Raw, RevivalLink) declared + folded now at their defaults so 3.14 needs no second
        ///        bump. Curve constants (MaxLevelOf/BaseXpOf/…) are NOT folded (authored/def-derived, the Delivery posture).
        ///        A null HeroStore folds Mix(0) count (dormant/legacy callers agree). All int/Fixed.Raw → cross-platform.
        ///        One scheduled re-baseline of ALL goldens (existing goldens have no heroes + XpBounty defaults to 0, so
        ///        the pin moves purely by the added Mix(0) hero-count + Mix(0) XpBounty per entity).
        /// </summary>
        public const int AlgoVersion = 11;

        /// <summary>
        /// Compute a full-state checksum for desync detection.
        /// Call after all systems have ticked for the current frame.
        /// </summary>
        public static uint Compute(EntityWorld world, BuildingStore buildings, ResourceStore resources,
                                   FactionRegistry factions, ModifierStore? modifiers = null, HeroStore? heroes = null)
        {
            // Contract guard for the registry param added in Story 1.3a: a future direct caller (e.g. the
            // 1.9a/9.1 server checksum collector) gets a clear error instead of an opaque NRE in the Ore loop.
            System.ArgumentNullException.ThrowIfNull(factions);

            uint hash = FNV_OFFSET;

            // ── Entity positions and health ───────────────────────────────────────
            int cap = world.HighWaterMark;
            for (int i = 0; i < cap; i++)
            {
                if (!world.IsAlive(i)) continue;

                hash = Mix(hash, world.Position[i].X.Raw);
                hash = Mix(hash, world.Position[i].Y.Raw);
                hash = Mix(hash, world.Position[i].Z.Raw);
                hash = Mix(hash, world.Health[i].Raw);

                // ── Command state (v4, Story 1.12) ────────────────────────────────
                // The full RTS command vocabulary's persistent per-entity state IS sim truth: a peer divergence
                // in a forced/follow target or a patrol route must desync detectably. Count-driven + ascending,
                // all int / Fixed.Raw → cross-platform safe: the Story 1.12 golden IS compared on both CI legs
                // (NOT Windows-gated, unlike the float-scoring AI golden).
                hash = Mix(hash, world.CommandTarget[i]);
                hash = Mix(hash, world.PatrolCount[i]);
                hash = Mix(hash, world.PatrolIndex[i]);
                hash = Mix(hash, world.PatrolDir[i]);
                int wpBase  = i * EntityWorld.MAX_PATROL_WAYPOINTS;
                int wpCount = world.PatrolCount[i];
                // Defensive (Review, Story 1.12): never read past the per-entity ring. OrderApplier caps the
                // count at MAX_PATROL_WAYPOINTS today, so this can't fire — but a future writer that sets a
                // larger count must not turn a logic slip into an OOB read inside per-tick desync detection.
                if (wpCount > EntityWorld.MAX_PATROL_WAYPOINTS) wpCount = EntityWorld.MAX_PATROL_WAYPOINTS;
                for (int k = 0; k < wpCount; k++)
                {
                    hash = Mix(hash, world.PatrolWaypoints[wpBase + k].X.Raw);
                    hash = Mix(hash, world.PatrolWaypoints[wpBase + k].Y.Raw);
                    hash = Mix(hash, world.PatrolWaypoints[wpBase + k].Z.Raw);
                }

                // ── Separation / formation config (v5, Story 1.13) ────────────────
                // CollisionRadius + SeparationPriorityOf are read in-sim by MovementSystem every tick on every
                // peer, so a content divergence in either must desync detectably. CategoryOf is NOT folded: it is
                // presentation-read (formation planning, like MeshType) and its effect reaches the hash only
                // transitively via Position, so a divergent local CategoryOf cannot desync. Both are int/Fixed.Raw
                // → cross-platform safe (the new formation-separation golden is compared on BOTH CI legs).
                hash = Mix(hash, world.CollisionRadius[i].Raw);
                hash = Mix(hash, (int)world.SeparationPriorityOf[i]);

                // ── Attack delivery (v10, Story 3.12) ─────────────────────────────
                // Delivery (Hitscan/Projectile) branches CombatSystem's instant-vs-projectile resolution, and
                // ProjectileSpeed drives the projectile advance step — both read in-sim, so a peer divergence in
                // either changes combat outcomes and must desync detectably. Authored spawn-constants folded by story
                // mandate (like AttackRange transitively via Position). int / Fixed.Raw → cross-platform safe.
                hash = Mix(hash, (int)world.Delivery[i]);
                hash = Mix(hash, world.ProjectileSpeed[i].Raw);

                // ── XP bounty (v11, Story 3.13) ───────────────────────────────────
                // The def-derived per-unit XP bounty this unit awards on death — read by HeroXpSystem when it drains the
                // DeathFeed, so a peer divergence changes hero XP/level outcomes. Folded directly (the Story 3.12
                // spawn-constant-folding convention: authored, but folded for a uniform re-baseline + coverage teeth).
                // Fixed.Raw → cross-platform safe.
                hash = Mix(hash, world.XpBounty[i].Raw);

                // ── Effective stats + ability resource + status (v6, Story 2.2b) ──
                // The ModifierStore now MUTATES these mid-match (a modifier changes Effective*; an ability debits
                // Energy; a status modifier sets StatusFlagsOf), so they are peer-divergent sim truth and must fold.
                // Base* is deliberately NOT folded (authored, in-tick-immutable — exactly as the pre-2.2a
                // AttackDamage/MaxHealth/Speed were never folded). All int / Fixed.Raw → cross-platform safe.
                hash = Mix(hash, world.EffectiveAttackDamage[i].Raw);
                hash = Mix(hash, world.EffectiveMaxHealth[i].Raw);
                hash = Mix(hash, world.EffectiveMoveSpeed[i].Raw);
                // EffectiveArmor (v8, Story 2.6): the buffable armor stat. ModifierSystem recomputes it mid-match
                // (an aura grants +armor via Modifier.ArmorDelta) and DamageResolver subtracts it, so it is
                // peer-divergent sim truth and must fold. BaseArmor stays UNFOLDED (authored, in-tick-immutable —
                // the BaseAttackDamage posture). Fixed.Raw → cross-platform safe (the passive goldens are compared
                // on both CI legs). Existing content has BaseArmor=0 → EffectiveArmor=0 → a single Mix(0) per
                // entity moves the hash from v7 even though no real armor exists yet (the scheduled re-baseline).
                hash = Mix(hash, world.EffectiveArmor[i].Raw);
                hash = Mix(hash, world.Energy[i].Raw);
                hash = Mix(hash, (int)world.StatusFlagsOf[i]);

                // ── Active modifier instances (v6, Story 2.2b) — count-driven, ascending slot ──
                // A null store folds Mix(0) count (≡ an empty store), so a legacy 4-arg caller and a real empty store
                // hash identically. The descriptor refs + caster id/faction are NOT folded (authored/peer-identical).
                int modCount = modifiers?.CountAt(i) ?? 0;
                hash = Mix(hash, modCount);
                for (int s = 0; s < modCount; s++)
                {
                    hash = Mix(hash, modifiers!.ModifierIdAt(i, s));
                    hash = Mix(hash, modifiers!.RemainingTicksAt(i, s));
                    hash = Mix(hash, modifiers!.TicksUntilPeriodAt(i, s));
                    hash = Mix(hash, modifiers!.PeriodsRemainingAt(i, s));
                    hash = Mix(hash, modifiers!.StackCountAt(i, s));
                }

                // ── Ability cooldowns (v7, Story 2.4a) — count-driven, ascending slot ──
                // AbilityCooldownTicks is the first per-entity ability array that MUTATES mid-match (a cast starts
                // it; AbilityCastSystem ticks it down each frame), so it is peer-divergent sim truth and must fold.
                // Count-driven by AbilityCount (the cross-platform-safe loop bound, like PatrolCount) — only the
                // populated slots are hashed, never the stride or empty slots, so raising MAX_ABILITIES_PER_UNIT
                // later moves no golden. AbilityId / MaxEnergy are authored/peer-identical (NOT folded, like MeshType);
                // PendingCast* are transient (cleared by AbilityCastSystem before this checksum). All int → x-platform.
                int abCount = world.AbilityCount[i];
                if (abCount > EntityWorld.MAX_ABILITIES_PER_UNIT) abCount = EntityWorld.MAX_ABILITIES_PER_UNIT; // defensive
                hash = Mix(hash, abCount);
                int abBase = i * EntityWorld.MAX_ABILITIES_PER_UNIT;
                for (int s = 0; s < abCount; s++)
                    hash = Mix(hash, world.AbilityCooldownTicks[abBase + s]);

                // ── Shift-queued order ring (v9, Story 2.12) — count-driven, ascending slot ──
                // The per-entity order queue MUTATES mid-match (OrderApplier appends a Shift order; OrderQueueSystem
                // pops the head on completion), so it is peer-divergent sim truth and must fold. Count-driven by
                // OrderQueueCount (the cross-platform-safe loop bound, like PatrolCount) — only the populated slots are
                // hashed, never the stride or empty slots, so raising MAX_ORDER_QUEUE later moves no golden. The command
                // byte is the masked 0-13 value (the wire's 0x80 queued flag never reaches here); targets are Fixed.Raw /
                // packed ints. All int → cross-platform safe (the shift-queue golden is compared on both CI legs).
                int oqCount = world.OrderQueueCount[i];
                if (oqCount > EntityWorld.MAX_ORDER_QUEUE) oqCount = EntityWorld.MAX_ORDER_QUEUE; // defensive
                hash = Mix(hash, oqCount);
                int oqBase = i * EntityWorld.MAX_ORDER_QUEUE;
                for (int s = 0; s < oqCount; s++)
                {
                    hash = Mix(hash, world.OrderQueueCmd[oqBase + s]);
                    hash = Mix(hash, world.OrderQueueTargetX[oqBase + s]);
                    hash = Mix(hash, world.OrderQueueTargetZ[oqBase + s]);
                }
            }

            // ── Building state ────────────────────────────────────────────────────
            int bCount = buildings.Count;
            for (int i = 0; i < bCount; i++)
            {
                hash = Mix(hash, buildings.Alive[i] ? 1 : 0);
                hash = Mix(hash, buildings.Health[i].Raw);
                hash = Mix(hash, buildings.ConstructionTimer[i].Raw);
                // ── Rally point (v9, Story 2.12, D-1) — the one in-tick-read BuildingStore field that was NOT folded.
                // Once rally is wire-driven (UnitCommand.SetRally) it becomes genuinely mutable-mid-match sim truth:
                // SpawnTrainedUnit reads it to send a trained unit Move→rally, so a peer divergence must desync
                // detectably (and directly, not only once a unit spawns and its Position drifts). RallyPoint is reset
                // to Zero in BuildingStore.Create, so an unset rally folds a stable Zero. All Fixed.Raw/int → x-platform.
                hash = Mix(hash, buildings.HasRallyPoint[i] ? 1 : 0);
                hash = Mix(hash, buildings.RallyPoint[i].X.Raw);
                hash = Mix(hash, buildings.RallyPoint[i].Z.Raw);
            }

            // ── Faction resources (all per-faction stores, active factions, ascending slot order) ──
            // Story 1.3b widened this from Ore-only to full coverage; checksum_algo_version bumped to 2.
            // Every public per-faction ResourceStore array is folded in here (proven by
            // SimChecksumCoverageGuardTest). MatchStats stays OUT by design (private observational scoreboard).
            // FactionBase is read in-tick by GatheringSystem (workers path to it to deposit), so a peer
            // divergence there would desync — it belongs in the hash even though it is constant within a match.
            foreach (Faction f in factions.ActiveFactions)
            {
                int idx = (int)f;
                hash = Mix(hash, resources.Ore[idx].Raw);
                hash = Mix(hash, resources.Crystal[idx].Raw);
                hash = Mix(hash, resources.SupplyUsed[idx]);        // int[] — pass directly, no .Raw
                hash = Mix(hash, resources.SupplyCap[idx]);         // int[]
                hash = Mix(hash, resources.FactionBase[idx].X.Raw); // FixedVec3 → three Fixed.Raw mixes
                hash = Mix(hash, resources.FactionBase[idx].Y.Raw);
                hash = Mix(hash, resources.FactionBase[idx].Z.Raw);
            }

            // ── HeroStore mutable state (v11, Story 3.13) — ascending HeroId (FoldOrder), count-driven ──
            // The Story 3.13 XP runtime mutates HeroStore.Level/Xp mid-match (the store was DORMANT since 3.2), so it is
            // now folded per-tick. Fold the live count, then per live slot IN FoldOrder ORDER (ascending HeroId — producer-
            // independent): HeroId.Value (low/high 32 bits, the SimRng pattern), Level, Xp.Raw, GrowthStacksApplied, and
            // the reserved Story 3.14 revival fields (declared + folded now at their defaults). Curve constants are NOT
            // folded (authored/def-derived spawn constants — the Delivery/AttackDamage posture). A null store folds a
            // single Mix(0) count (≡ an empty/dormant store), so a legacy 5-arg caller and an empty store agree.
            if (heroes != null)
            {
                int[] hOrder = heroes.FoldOrder();
                hash = Mix(hash, hOrder.Length);
                for (int k = 0; k < hOrder.Length; k++)
                {
                    int slot = hOrder[k];
                    ulong hid = heroes.Id[slot].Value;
                    hash = Mix(hash, (int)(hid & 0xFFFFFFFFUL)); // low 32 bits
                    hash = Mix(hash, (int)(hid >> 32));          // high 32 bits
                    hash = Mix(hash, heroes.Level[slot]);
                    hash = Mix(hash, heroes.Xp[slot].Raw);
                    hash = Mix(hash, heroes.GrowthStacksApplied[slot]);
                    // Story 3.14 reserved fields — folded at their Mint defaults (Alive3_14 == true) so 3.14 needs no bump.
                    hash = Mix(hash, heroes.Alive3_14[slot] ? 1 : 0);
                    hash = Mix(hash, heroes.AwaitingRevival[slot] ? 1 : 0);
                    hash = Mix(hash, heroes.RevivalTimer[slot].Raw);
                    hash = Mix(hash, heroes.RevivalLink[slot]);
                }
            }
            else
            {
                hash = Mix(hash, 0); // null store ≡ empty (dormant): fold an identical count-0 mix
            }

            // ── RNG state (v3, Story 1.5) ─────────────────────────────────────────
            // The single shared SimRng's state IS sim truth: once Epic 2 effects draw from it, a divergent
            // draw stream between peers must desync detectably. Folded as two int mixes (low/high 32 bits)
            // via the existing Mix primitive. State is constant (== seed) until something draws.
            ulong rng = world.Rng.State;
            hash = Mix(hash, (int)(rng & 0xFFFFFFFFUL)); // low 32 bits
            hash = Mix(hash, (int)(rng >> 32));          // high 32 bits

            return hash;
        }

        /// <summary>
        /// FNV-1a mix: feed a single int (4 bytes, little-endian) into the hash.
        /// </summary>
        private static uint Mix(uint hash, int value)
        {
            uint v = (uint)value;
            hash ^= v & 0xFF;         hash *= FNV_PRIME;
            hash ^= (v >> 8) & 0xFF;  hash *= FNV_PRIME;
            hash ^= (v >> 16) & 0xFF; hash *= FNV_PRIME;
            hash ^= (v >> 24) & 0xFF; hash *= FNV_PRIME;
            return hash;
        }
    }
}
