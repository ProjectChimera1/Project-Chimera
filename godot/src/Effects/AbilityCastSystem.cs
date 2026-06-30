#nullable enable
using ProjectChimera.Combat;            // DamageTable / CombatEventQueue / MatchStats (effect-resolution sinks)
using ProjectChimera.Core;              // EntityWorld, Fixed, Faction, ISimSystem, ResourceStore, SimulationLoop
using ProjectChimera.Core.Definitions;  // AbilityRegistry, AbilityDefinition, AbilityTargeting
using ProjectChimera.Navigation;        // SpatialHash (SearchArea fan-out)

namespace ProjectChimera.Effects
{
    /// <summary>
    /// Story 2.4a (FR-11 / AR-8) — the runtime ability-cast spine. An <see cref="ISimSystem"/> registered at index
    /// 3 (immediately BEFORE <see cref="ModifierSystem"/>) so a cast that installs a buff is recomputed by
    /// ModifierSystem and read by <c>CombatSystem</c> the SAME tick. Each tick it (a) ticks every populated per-slot
    /// cooldown down by one integer tick, then (b) consumes at most one queued cast per unit — written pre-tick by
    /// <see cref="ProjectChimera.Multiplayer.OrderApplier"/> into <see cref="EntityWorld.PendingCastSlot"/>/<see
    /// cref="EntityWorld.PendingCastTarget"/> — running the validated 2.3 effect graph through the shared 2.1
    /// <see cref="EffectExecutor"/> + 2.2b <see cref="ModifierStore"/>.
    ///
    /// <para><b>Atomic cast (AC6).</b> Cooldown + affordability are CHECKED before anything mutates; only when ALL
    /// pass are energy/ore/crystal debited, the graph run, and the cooldown started. An insufficient or on-cooldown
    /// cast is refused with ZERO side effects (no partial spend, no cooldown started, no effect run).</para>
    ///
    /// <para><b>Determinism.</b> Pure C# (no <c>using Godot;</c>, no <c>float</c>/<c>double</c>, no
    /// <c>Fixed.FromFloat</c>/<c>ToFloat</c> — cooldown seconds→ticks is integer truncation). Ascending-id,
    /// <see cref="EntityWorld.IsAlive"/>-guarded. Owns its OWN <see cref="EffectExecutor"/> (never the
    /// ModifierStore's dedicated one — re-entrancy safety: an <c>ApplyModifier</c> leaf re-enters the STORE's
    /// executor, not this one) and its OWN <see cref="SpatialHash"/> (rebuilt before a <c>SearchArea</c> fan-out).
    /// The pending-cast intent is consumed + cleared every tick before the checksum, so it never folds.</para>
    /// </summary>
    public sealed class AbilityCastSystem : ISimSystem
    {
        private readonly AbilityRegistry _registry;
        private readonly ResourceStore _resources;
        private readonly ModifierStore _modifiers;
        private readonly DamageTable _damageTable;
        private readonly CombatEventQueue? _events;
        private readonly MatchStats? _stats;

        // Graph-running executor — NOT the ModifierStore's dedicated period executor (re-entrancy safety). Its own
        // pre-allocated work-stack; an ApplyModifier/Persistent leaf in a cast graph re-enters the STORE's executor.
        private readonly EffectExecutor _executor = new EffectExecutor();
        // Own spatial hash for SearchArea fan-out (e.g. fireball), rebuilt from current positions before each run.
        private readonly SpatialHash _spatial = new SpatialHash();

        /// <summary>
        /// Construct the cast system. <paramref name="registry"/>/<paramref name="resources"/>/<paramref
        /// name="modifiers"/> are required; <paramref name="damageTable"/> resolves to <see cref="DamageTable.Default"/>
        /// (mirrors <c>CombatSystem</c>/<c>ProjectileSystem</c>); the event/stats sinks are optional. (No
        /// <c>FactionRegistry</c> dep: the caster's faction is read directly from <see cref="EntityWorld.FactionOf"/>.)
        /// </summary>
        public AbilityCastSystem(AbilityRegistry registry, ResourceStore resources, ModifierStore modifiers,
                                 DamageTable? damageTable = null, CombatEventQueue? events = null, MatchStats? stats = null)
        {
            _registry    = registry;
            _resources   = resources;
            _modifiers   = modifiers;
            _damageTable = damageTable ?? DamageTable.Default;
            _events      = events;
            _stats       = stats;
        }

        /// <summary>Ticks per second for the seconds→ticks cooldown conversion (the named sim rate, CHM0004-clean).</summary>
        private const int TicksPerSecond = SimulationLoop.TICKS_PER_SECOND;

        /// <summary>
        /// Convert an ability cooldown in Fixed SECONDS to integer ticks (Fixed multiply then <see cref="Fixed.ToInt"/>
        /// truncation — deterministic + drift-free; never <c>ToFloat</c>). At 30 tps: 3s→90, 6s→180, 12s→360.
        /// </summary>
        public static int SecondsToTicks(Fixed seconds) => (seconds * Fixed.FromInt(TicksPerSecond)).ToInt();

        /// <inheritdoc />
        public void Tick(EntityWorld world, Fixed dt)
        {
            int cap = world.HighWaterMark;
            for (int id = 0; id < cap; id++)
            {
                if (!world.IsAlive(id)) continue;

                int abBase = id * EntityWorld.MAX_ABILITIES_PER_UNIT;
                int n = world.AbilityCount[id];
                if (n > EntityWorld.MAX_ABILITIES_PER_UNIT) n = EntityWorld.MAX_ABILITIES_PER_UNIT; // defensive bound

                // (a) Tick every populated cooldown down by one (the CombatSystem.TickCooldown precedent — but integer
                //     ticks, not Fixed-seconds-by-dt, so there is no inexact-1/30 drift). Decrement BEFORE the consume
                //     so a cooldown that reaches 0 this tick is castable this tick (the exact re-enable boundary).
                for (int s = 0; s < n; s++)
                    if (world.AbilityCooldownTicks[abBase + s] > 0)
                        world.AbilityCooldownTicks[abBase + s]--;

                // (b) Consume at most one queued cast (one-shot: ALWAYS cleared, whether it fired or was refused).
                if (world.PendingCastSlot[id] != EntityWorld.NO_PENDING_CAST)
                {
                    TryCast(world, id, abBase, n);
                    world.PendingCastSlot[id]   = EntityWorld.NO_PENDING_CAST;
                    world.PendingCastTarget[id] = -1;
                }
            }

            // (c) Story 2.6 — the while-alive AURA pass. Every tick each aura owner re-grants a short Refresh modifier
            //     to its SearchArea matches. Runs here at index [3] so ModifierSystem[4] recomputes the grant and
            //     CombatSystem[5] reads the buffed Effective* the SAME tick (AC1). Separate ascending-owner-id loop,
            //     after the cast loop; no new per-entity counter (expiry is by non-refresh — the no-fold design).
            TickAuras(world);
        }

        /// <summary>
        /// The Story 2.6 while-alive AURA driver (AC1). For each alive owner (ascending id) whose
        /// <see cref="EntityWorld.AuraAbilityIndex"/> is set, run its aura graph — a <c>SearchArea</c> →
        /// <c>ApplyModifier</c> that re-grants a SHORT-duration <see cref="StackRule.Refresh"/> modifier to every
        /// in-radius match. A target that leaves the radius (or an owner that dies) simply stops being re-applied to,
        /// and the modifier expires on its own within its duration — so there is NO "remove" bookkeeping and NO new
        /// folded counter (architecture: "an aura = a short Modifier re-applied each tick"). The spatial hash is built
        /// LAZILY on the first aura owner, so a scenario with no auras pays nothing and its checksum is untouched
        /// (the existing goldens never enter this path). Reuses this system's own executor + spatial + store — never
        /// the ModifierStore's dedicated period executor (re-entrancy safety, as for player casts).
        /// </summary>
        private void TickAuras(EntityWorld world)
        {
            int cap = world.HighWaterMark;
            bool spatialBuilt = false;
            for (int id = 0; id < cap; id++)
            {
                if (!world.IsAlive(id)) continue;
                int auraIdx = world.AuraAbilityIndex[id];
                if (auraIdx < 0 || auraIdx >= _registry.Count) continue;

                // Build the spatial hash once, only when an aura actually exists (current post-cast positions).
                if (!spatialBuilt) { _spatial.Rebuild(world); spatialBuilt = true; }

                AbilityDefinition aura = _registry.Get(auraIdx);
                // primaryTarget = the owner → the SearchArea centers on the owner's position; the owner's faction
                // drives the Ally/Enemy filter. The store is MANDATORY (the aura's ApplyModifier leaf needs it).
                var ctx = new EffectContext(world, casterId: id, primaryTargetId: id, casterFaction: world.FactionOf[id],
                                            _damageTable, spatial: _spatial, _events, _stats, modifierStore: _modifiers);
                _executor.Run(aura.EffectGraph, in ctx);
            }
        }

        /// <summary>
        /// Story 2.6 — the WHILE-ALIVE self-passive installer (AC3). Subscribed to
        /// <see cref="EntityWorld.OnUnitDefinitionApplied"/> (fired once per def-based spawn, AFTER the SoA is written),
        /// it installs the unit's self-passive — a <c>Persistent</c> (DoT/HoT) or a permanent <c>ApplyModifier</c> — by
        /// running its graph with the owner as both caster and primary target. Installed exactly once per live spawn
        /// (the seam fires once per <see cref="EntityWorld.ApplyUnitDefinition"/>); reverted by
        /// <c>ModifierStore.ClearEntity</c> on death (the OnDestroy subscriber). No spatial needed — a while_alive root
        /// is an <c>ApplyModifier</c>/<c>Persistent</c>, never a <c>SearchArea</c> (the validator guarantees it).
        /// </summary>
        public void InstallSelfPassive(EntityWorld world, int id)
        {
            if (!world.IsAlive(id)) return;
            int idx = world.SelfPassiveAbilityIndex[id];
            if (idx < 0 || idx >= _registry.Count) return;
            AbilityDefinition passive = _registry.Get(idx);
            var ctx = new EffectContext(world, casterId: id, primaryTargetId: id, casterFaction: world.FactionOf[id],
                                        _damageTable, spatial: null, _events, _stats, modifierStore: _modifiers);
            _executor.Run(passive.EffectGraph, in ctx);
        }

        /// <summary>
        /// The atomic cast pipeline: validate slot → gate cooldown → check affordability → (all passed) debit all →
        /// run the effect graph → start the cooldown. Any failed gate aborts BEFORE any mutation (AC6 refuse-atomic).
        /// </summary>
        private void TryCast(EntityWorld world, int id, int abBase, int abilityCount)
        {
            int slot = world.PendingCastSlot[id];
            if (slot < 0 || slot >= abilityCount) return;           // no such slot
            int regIdx = world.AbilityId[abBase + slot];
            if (regIdx < 0 || regIdx >= _registry.Count) return;    // empty / out-of-range slot
            AbilityDefinition ab = _registry.Get(regIdx);

            // Gate cooldown (the command card greys the button on the identical predicate — AC1 parity).
            if (world.AbilityCooldownTicks[abBase + slot] > 0) return;

            // Affordability — CHECK ALL three, mutate NOTHING yet (AC6 atomic: a failed crystal check must not have
            // debited energy/ore). Costs are int on the ability → Fixed.FromInt.
            Faction faction = world.FactionOf[id];
            Fixed oreCost     = Fixed.FromInt(ab.CostOre);
            Fixed crystalCost = Fixed.FromInt(ab.CostCrystal);
            if (world.Energy[id] < ab.CostEnergy) return;
            if (!_resources.CanAffordOre(faction, oreCost)) return;
            if (!_resources.CanAffordCrystal(faction, crystalCost)) return;

            // Resolve + VALIDATE the target BEFORE any debit, so an unfulfillable cast refuses atomically (nothing
            // debited, no cooldown started) — the same contract as the cooldown/affordability refusals (AC6).
            //   Self/None  → the caster is the primary target (auras, self-buffs like battle_fury).
            //   TargetUnit/GroundPoint with no valid LIVING target → atomic no-op. NEVER redirect an offensive cast
            //   (e.g. fireball) onto the caster — a target can die in the lockstep input-delay window before this
            //   tick consumes the intent, or the order can carry -1; self-harming the caster + spending the cost is
            //   wrong (it would violate AC6's "an unfulfillable cast changes nothing").
            int target = world.PendingCastTarget[id];
            if (ab.ParsedTargeting == AbilityTargeting.Self || ab.ParsedTargeting == AbilityTargeting.None)
                target = id;
            else if (target < 0 || !world.IsAlive(target))
                return;

            // Debit ALL (every gate passed → each refuse-when-insufficient call necessarily succeeds; atomic).
            _modifiers.TryDebitEnergy(id, ab.CostEnergy);
            _resources.SpendOre(faction, oreCost);
            _resources.SpendCrystal(faction, crystalCost);

            // Execute the validated effect graph (mirrors ModifierStore.RunEffect). Rebuild the spatial hash so a
            // SearchArea (e.g. fireball) queries CURRENT positions; harmless for non-SearchArea graphs (they ignore
            // ctx.Spatial). Passing the modifier store is MANDATORY — an ApplyModifier/Persistent leaf throws on a
            // null store (battle_fury is one).
            _spatial.Rebuild(world);
            var ctx = new EffectContext(world, casterId: id, primaryTargetId: target, casterFaction: faction,
                                        _damageTable, spatial: _spatial, _events, _stats, modifierStore: _modifiers);
            _executor.Run(ab.EffectGraph, in ctx);

            // Start the cooldown (integer remaining-ticks; Decision #4). Next tick's (a) begins counting it down.
            world.AbilityCooldownTicks[abBase + slot] = SecondsToTicks(ab.Cooldown);

            // Story 2.7 (SD-3): the cast fired → push a presentation-only AbilityCast feedback event carrying the
            // ability's profile (the Story 2.10 "cast plays its CombatFeedbackProfile" / "no new engine code"
            // contract). Position = the primary target (self/none casts resolved target=id above, so it's the caster).
            // Emits exactly ONCE per committed cast — every refusal already returned. Null profile ⇒ no extra juice.
            // Never folded: CombatEventQueue is not a SimChecksum input, so this cannot perturb the deterministic tick.
            _events?.Push(CombatEventType.AbilityCast, world.Position[target], ab.CombatFeedback);
        }
    }
}
