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
    ///   - EntityWorld effective stats + ability/status: per alive entity, EffectiveAttackDamage, EffectiveMaxHealth,
    ///     EffectiveMoveSpeed, Energy (all Raw), and StatusFlagsOf (int) — added v6 (Story 2.2b), now that the
    ///     ModifierStore MUTATES them mid-match. Base* stays UNFOLDED (authored, in-tick-immutable).
    ///   - ModifierStore: per alive entity (ascending owner-id then slot), the active-instance count then, per slot,
    ///     modifierId / remainingTicks / ticksUntilPeriod / periodsRemaining / stackCount — added v6 (Story 2.2b).
    ///     The descriptor refs + caster id/faction are NOT folded (authored / peer-identical). A null store folds an
    ///     identical Mix(0) count per entity (≡ an empty store), so legacy callers and an empty store agree.
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
        /// </summary>
        public const int AlgoVersion = 6;

        /// <summary>
        /// Compute a full-state checksum for desync detection.
        /// Call after all systems have ticked for the current frame.
        /// </summary>
        public static uint Compute(EntityWorld world, BuildingStore buildings, ResourceStore resources,
                                   FactionRegistry factions, ModifierStore? modifiers = null)
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

                // ── Effective stats + ability resource + status (v6, Story 2.2b) ──
                // The ModifierStore now MUTATES these mid-match (a modifier changes Effective*; an ability debits
                // Energy; a status modifier sets StatusFlagsOf), so they are peer-divergent sim truth and must fold.
                // Base* is deliberately NOT folded (authored, in-tick-immutable — exactly as the pre-2.2a
                // AttackDamage/MaxHealth/Speed were never folded). All int / Fixed.Raw → cross-platform safe.
                hash = Mix(hash, world.EffectiveAttackDamage[i].Raw);
                hash = Mix(hash, world.EffectiveMaxHealth[i].Raw);
                hash = Mix(hash, world.EffectiveMoveSpeed[i].Raw);
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
            }

            // ── Building state ────────────────────────────────────────────────────
            int bCount = buildings.Count;
            for (int i = 0; i < bCount; i++)
            {
                hash = Mix(hash, buildings.Alive[i] ? 1 : 0);
                hash = Mix(hash, buildings.Health[i].Raw);
                hash = Mix(hash, buildings.ConstructionTimer[i].Raw);
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
