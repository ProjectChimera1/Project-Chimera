#nullable enable
using ProjectChimera.Combat;            // DamageTable / CombatEventQueue / MatchStats (effect-resolution sinks)
using ProjectChimera.Core;              // EntityWorld, Fixed, Faction, ISimSystem, ResourceStore, SimulationLoop
using ProjectChimera.Core.Definitions;  // AbilityRegistry, AbilityDefinition, AbilityTargeting
using ProjectChimera.Core.Sim;          // ILogSink (DW-285: unresolvable-ability drops warn instead of vanishing)
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
    /// cast is refused with ZERO side effects (no partial spend, no cooldown started, no effect run). <b>DW-283</b>
    /// closes the one hole in that argument: the pre-checks are all <c>have &gt;= cost</c> comparisons, which a
    /// NEGATIVE authored cost passes while the debits disagree about it (energy refuses, ore/crystal grant) — so a
    /// negative cost is now refused up front, and the debit RETURNS are checked and rolled back rather than discarded.</para>
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
        private readonly DeathFeed? _deaths;
        // Story 9.14 — the sim-owned alliance mask, threaded onto every cast EffectContext so an ability's Ally/Enemy
        // SearchArea filter is TEAM-aware. Optional: null ⇒ strict faction equality (byte-identical to pre-9.14 / FFA).
        private readonly AllianceStore? _alliances;
        // DW-285 — the injected diagnostic seam (AR-4 ILogSink, NEVER a static ambient sink / Console / GD.Print).
        // Every "this cast/passive cannot resolve" branch below used to be a BARE return: the intent vanished with no
        // trace, so an unwired registry / a stale AbilityIndices back-fill was indistinguishable from a player simply
        // not casting. The branches stay the SAME deterministic no-ops; they now WARN through this sink. Null (goldens,
        // bare tests, the host before wiring) ⇒ exactly the pre-DW-285 behavior. Diagnostics only: the sink must never
        // mutate sim state, so a peer with a sink and a peer without stay byte-identical in lockstep.
        private readonly ILogSink? _log;
        // DW-285 — per-owner de-duplication for the ONE per-tick diagnostic site (TickAuras). A broken aura index is a
        // wiring bug that would otherwise re-warn 30x/second for the unit's whole life; the cast + self-passive sites
        // are inherently one-shot and need no de-dupe. Allocated lazily and ONLY when a sink is attached, so a
        // sink-less (golden/headless) run allocates nothing. Never read by the sim — not folded, not a tick input.
        private System.Collections.Generic.HashSet<int>? _warnedAuraOwners;

        // Graph-running executor — NOT the ModifierStore's dedicated period executor (re-entrancy safety). Its own
        // pre-allocated work-stack; an ApplyModifier/Persistent leaf in a cast graph re-enters the STORE's executor.
        private readonly EffectExecutor _executor = new EffectExecutor();
        // Own spatial hash for SearchArea fan-out (e.g. fireball), rebuilt from current positions before each run.
        private readonly SpatialHash _spatial = new SpatialHash();

        // Story 7.13 — the trigger-DSL sim-event feed (ability_cast raised at the atomic-success point). Wired by
        // SimulationHost after construction; null ⇒ no raise.
        private DslSimEventFeed? _dslSimEvents;
        /// <summary>Story 7.13 — wire the trigger-DSL sim-event feed so a committed cast raises ability_cast.</summary>
        public void SetDslSimEvents(DslSimEventFeed? feed) => _dslSimEvents = feed;

        /// <summary>
        /// Construct the cast system. <paramref name="registry"/>/<paramref name="resources"/>/<paramref
        /// name="modifiers"/> are required; <paramref name="damageTable"/> resolves to <see cref="DamageTable.Default"/>
        /// (mirrors <c>CombatSystem</c>/<c>ProjectileSystem</c>); the event/stats sinks are optional. (No
        /// <c>FactionRegistry</c> dep: the caster's faction is read directly from <see cref="EntityWorld.FactionOf"/>.)
        /// </summary>
        public AbilityCastSystem(AbilityRegistry registry, ResourceStore resources, ModifierStore modifiers,
                                 DamageTable? damageTable = null, CombatEventQueue? events = null, MatchStats? stats = null,
                                 DeathFeed? deaths = null, AllianceStore? alliances = null, ILogSink? log = null)
        {
            _registry    = registry;
            _resources   = resources;
            _modifiers   = modifiers;
            _damageTable = damageTable ?? DamageTable.Default;
            _events      = events;
            _stats       = stats;
            _deaths      = deaths;
            _alliances   = alliances; // Story 9.14 (optional — FFA/null → strict faction equality, byte-identical)
            _log         = log;       // DW-285 (optional — null → the pre-DW-285 silent no-op branches)
        }

        /// <summary>Ticks per second for the seconds→ticks cooldown conversion (the named sim rate, CHM0004-clean).</summary>
        private const int TicksPerSecond = SimulationLoop.TICKS_PER_SECOND;

        /// <summary>
        /// DW-266 — the statuses that FORBID an active cast. <see cref="StatusFlags.Silenced"/> is exactly this
        /// ("this unit cannot cast abilities", the meaning the HUD status icon has always shown);
        /// <see cref="StatusFlags.Stunned"/> subsumes it (a stunned unit can do nothing at all). AURAS and
        /// while-alive SELF-PASSIVES are deliberately NOT gated by this — silence stops ACTIVE casting only.
        /// </summary>
        private const StatusFlags CAST_BLOCKING = StatusFlags.Silenced | StatusFlags.Stunned;

        /// <summary>
        /// DW-284 — the authoring UPPER BOUND for an ability cooldown, in Fixed SECONDS: the exact value above which
        /// the ORIGINAL 16.16 conversion overflowed. That conversion was <c>(seconds * Fixed.FromInt(TicksPerSecond))
        /// .ToInt()</c>, whose 16.16 product is cast back to <c>int</c> — so it wrapped once
        /// <c>seconds.Raw * TicksPerSecond</c> left int32, i.e. above raw <c>int.MaxValue / TicksPerSecond</c>
        /// (71 582 788 raw ≈ 1092.266 s ≈ 18.2 min at 30 tps), turning a very long cooldown into a NEGATIVE tick count
        /// — which the <c>&gt; 0</c> countdown gate reads as "ready", i.e. unlimited spam.
        ///
        /// <para><see cref="AbilityValidator"/> rejects authored content above this bound by referencing THIS constant
        /// — never a hand-copied number (the DW-125 duplicated-constant lesson) — and
        /// <see cref="SecondsToTicks"/> is independently overflow-proof (64-bit intermediate) so an UNVALIDATED def
        /// still degrades to a huge-but-positive cooldown instead of wrapping. Belt and braces: the validator is the
        /// content gate, the widened arithmetic is the runtime backstop.</para>
        /// </summary>
        public static readonly Fixed MaxCooldownSeconds = Fixed.FromRaw(int.MaxValue / TicksPerSecond);

        /// <summary>
        /// Convert an ability cooldown in Fixed SECONDS to integer ticks (16.16 multiply then truncation —
        /// deterministic + drift-free; never <c>ToFloat</c>). At 30 tps: 3s→90, 6s→180, 12s→360.
        ///
        /// <para>DW-284: the multiply runs in a 64-bit intermediate. <c>seconds.Raw * TicksPerSecond &gt;&gt; 16</c> is
        /// algebraically IDENTICAL to the former <c>(seconds * Fixed.FromInt(TicksPerSecond)).ToInt()</c> — the 16.16
        /// product's low 16 fractional bits are shifted away by the same <c>ToInt</c> truncation either way — minus the
        /// int32 cast inside <c>Fixed.operator*</c> that used to WRAP above <see cref="MaxCooldownSeconds"/>. Every
        /// value that already fit converts byte-identically (all shipped cooldowns are 0-20 s), so no folded
        /// <c>AbilityCooldownTicks</c> value moves; the only behavior change is above the bound, where a 2000 s
        /// cooldown produced −5536 ticks (permanently "ready") and now correctly produces 60 000. The widened
        /// intermediate covers the WHOLE Fixed range (max ≈ 983 039 ticks ≈ 9.1 h), so no clamp is reachable.</para>
        /// </summary>
        public static int SecondsToTicks(Fixed seconds) =>
            (int)(((long)seconds.Raw * TicksPerSecond) >> Fixed.FRACTIONAL_BITS);

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
                // DW-285: auraIdx < 0 is the NORMAL "this unit has no aura" case — never diagnosed. An index at or
                // beyond the registry is a WIRING BUG (the def was resolved against a different/larger registry than
                // this system holds), which silently disabled the aura for the whole match; warn once per owner.
                if (auraIdx >= _registry.Count) { WarnBrokenAura(id, auraIdx); continue; }
                if (auraIdx < 0) continue;

                // Build the spatial hash once, only when an aura actually exists (current post-cast positions).
                if (!spatialBuilt) { _spatial.Rebuild(world); spatialBuilt = true; }

                AbilityDefinition aura = _registry.Get(auraIdx);
                // primaryTarget = the owner → the SearchArea centers on the owner's position; the owner's faction
                // drives the Ally/Enemy filter. The store is MANDATORY (the aura's ApplyModifier leaf needs it).
                var ctx = new EffectContext(world, casterId: id, primaryTargetId: id, casterFaction: world.FactionOf[id],
                                            _damageTable, spatial: _spatial, _events, _stats, modifierStore: _modifiers, deaths: _deaths,
                                            alliances: _alliances); // Story 9.14: team-aware aura Ally/Enemy filter
                _executor.Run(aura.EffectGraph, in ctx);
            }
        }

        /// <summary>
        /// DW-285 — warn ONCE per owner that its <see cref="EntityWorld.AuraAbilityIndex"/> points past the registry
        /// (so its aura silently never runs). De-duplicated because <see cref="TickAuras"/> is a per-tick loop; the set
        /// is allocated only when a sink is attached and is pure diagnostics state (never read by the sim, never
        /// folded), so a run with a sink is byte-identical to one without.
        /// </summary>
        private void WarnBrokenAura(int ownerId, int auraIdx)
        {
            if (_log is null) return;
            _warnedAuraOwners ??= new System.Collections.Generic.HashSet<int>();
            if (!_warnedAuraOwners.Add(ownerId)) return;
            _log.Warn($"[AbilityCastSystem] entity {ownerId} carries AuraAbilityIndex={auraIdx} but the registry holds " +
                      $"{_registry.Count} abilities — the aura will never run. Its UnitDefinition was resolved against a " +
                      "different AbilityRegistry than this system was constructed with (ResolveAbilities/registry wiring).");
        }

        /// <summary>
        /// Story 2.6 — the WHILE-ALIVE self-passive installer (AC3). Subscribed to
        /// <see cref="EntityWorld.OnUnitDefinitionApplied"/> (fired once per def-based spawn, AFTER the SoA is written),
        /// it installs the unit's self-passive — a <c>Persistent</c> (DoT/HoT) or a permanent <c>ApplyModifier</c> — by
        /// running its graph with the owner as both caster and primary target. Installed exactly once per live spawn
        /// (the seam fires once per <see cref="EntityWorld.ApplyUnitDefinition"/>); reverted by
        /// <c>ModifierStore.ClearEntity</c> on death (the OnDestroy subscriber). No spatial needed — a while_alive root
        /// is an <c>ApplyModifier</c>/<c>Persistent</c>, never a <c>SearchArea</c> (the validator guarantees it).
        ///
        /// <para><b>DW-300 — idempotent against a LIVE re-apply.</b> "Once per spawn" used to rest entirely on the
        /// precondition that every <see cref="EntityWorld.ApplyUnitDefinition"/> caller runs on a FRESH
        /// <c>Create</c>/<c>RestoreUnit</c> slot (whose modifiers <c>ClearEntity</c> already wiped). A future in-place
        /// re-apply on a living unit — an upgrade/morph/tech re-map — re-fires this seam, and neither install path
        /// self-dedups: a <c>Persistent</c> root lands a SECOND concurrent DoT/HoT in a new slot (8 re-applies exhaust
        /// the per-entity ring and evict real buffs), and a <c>StackRule.Stack</c> <c>ApplyModifier</c> root re-adds
        /// its stat deltas. So the install is now GUARDED: if this entity already hosts what this passive's root would
        /// install (<see cref="ModifierStore.HostsInstanceFrom"/>), the re-run is skipped outright — a true no-op that
        /// touches no folded field, never a "refresh" (re-arming the pulse schedule on every re-apply could stall the
        /// cadence indefinitely). A genuine spawn is unaffected: a fresh slot hosts nothing, so the probe is false and
        /// the install runs exactly as before — no golden moves.</para>
        /// </summary>
        public void InstallSelfPassive(EntityWorld world, int id)
        {
            if (!world.IsAlive(id)) return;
            int idx = world.SelfPassiveAbilityIndex[id];
            // DW-285: idx < 0 is the NORMAL "no self-passive" case. An index at/beyond the registry is a wiring bug
            // that silently drops the unit's while_alive passive for its entire life — warn (once per spawn: this
            // installer is called from the OnUnitDefinitionApplied seam, not per tick).
            if (idx >= _registry.Count)
            {
                _log?.Warn($"[AbilityCastSystem] entity {id} carries SelfPassiveAbilityIndex={idx} but the registry holds " +
                           $"{_registry.Count} abilities — the while_alive passive was NOT installed (ResolveAbilities/registry wiring).");
                return;
            }
            if (idx < 0) return;
            AbilityDefinition passive = _registry.Get(idx);
            // DW-300 install-once guard — see the <para> above. Fails OPEN on any unrecognized root shape. The skip
            // re-derives Effective* first: ApplyUnitDefinition just re-mirrored Base*→Effective* (discarding every
            // installed modifier's contribution), and ModifierSystem.Tick only recomputes entities something DIRTIED —
            // which, before this guard, only the duplicate install itself happened to do. Unreachable on a genuine
            // spawn (a fresh slot hosts nothing), so no live tick and no golden observes it.
            if (_modifiers.HostsInstanceFrom(id, passive.EffectGraph))
            {
                _modifiers.RecomputeEffectiveStats(id);
                return;
            }
            var ctx = new EffectContext(world, casterId: id, primaryTargetId: id, casterFaction: world.FactionOf[id],
                                        _damageTable, spatial: null, _events, _stats, modifierStore: _modifiers, deaths: _deaths,
                                        alliances: _alliances); // Story 9.14: team-aware self-passive Ally/Enemy filter
            _executor.Run(passive.EffectGraph, in ctx);
        }

        /// <summary>
        /// The atomic cast pipeline: validate slot → gate cooldown → check affordability → (all passed) debit all →
        /// run the effect graph → start the cooldown. Any failed gate aborts BEFORE any mutation (AC6 refuse-atomic).
        /// </summary>
        private void TryCast(EntityWorld world, int id, int abBase, int abilityCount)
        {
            int slot = world.PendingCastSlot[id];
            // DW-285: both resolution failures below were BARE returns — a queued cast intent vanished with no denial
            // cue and no log, so a mis-addressed order and a stale/unresolved AbilityId slot looked exactly like "the
            // player did not cast". They stay the same deterministic no-op; they now surface an OrderDenied cue (the
            // Story 11.4 guard-emits-reason contract; presentation-only, never folded) AND warn through the sink.
            // One diagnostic per attempt at most — the pending intent is consumed and cleared every tick.
            if (slot < 0 || slot >= abilityCount)                   // no such slot
            {
                _events?.PushDenied(world.Position[id], world.FactionOf[id], DenialReason.InvalidTarget);
                _log?.Warn($"[AbilityCastSystem] entity {id} requested cast slot {slot} but holds {abilityCount} " +
                           "ability slot(s) — cast dropped.");
                return;
            }
            int regIdx = world.AbilityId[abBase + slot];
            if (regIdx < 0 || regIdx >= _registry.Count)            // empty / out-of-range slot
            {
                _events?.PushDenied(world.Position[id], world.FactionOf[id], DenialReason.InvalidTarget);
                _log?.Warn($"[AbilityCastSystem] entity {id} slot {slot} holds AbilityId={regIdx}, outside the " +
                           $"{_registry.Count}-ability registry — cast dropped (unresolved ability id, or a def " +
                           "resolved against a different AbilityRegistry).");
                return;
            }
            AbilityDefinition ab = _registry.Get(regIdx);

            // DW-266 — SILENCE / STUN GATE. Refused FIRST, so a silenced caster is told it is silenced rather than
            // being handed a less relevant cooldown/resource reason. Atomic like every other refusal (AC6): nothing
            // debited, no cooldown started, no effect graph run. The pending intent is still consumed by the caller
            // (one-shot), so a queued cast is DROPPED by the debuff rather than deferred — pressing again after it
            // expires re-issues it. StatusFlagsOf is None throughout every recorded golden ⇒ no checksum moves.
            StatusFlags status = world.StatusFlagsOf[id];
            if ((status & CAST_BLOCKING) != 0)
            {
                _events?.PushDenied(world.Position[id], world.FactionOf[id],
                    (status & StatusFlags.Silenced) != 0 ? DenialReason.Silenced : DenialReason.Stunned);
                return;
            }

            // Gate cooldown (the command card greys the button on the identical predicate — AC1 parity). Story 11.4
            // (FR-74): a rejected cast surfaces a guard-sourced OrderDenied cue at the caster (previously SILENT). The
            // event is presentation-only (CombatEventQueue is not a SimChecksum input) and MatchAlertBridge filters it
            // to the local faction, so an enemy/AI failed cast produces no local feedback. `_events` null → no-op.
            if (world.AbilityCooldownTicks[abBase + slot] > 0)
            {
                _events?.PushDenied(world.Position[id], world.FactionOf[id], DenialReason.OnCooldown);
                return;
            }

            Faction faction = world.FactionOf[id];

            // DW-283 — fail-closed on a NEGATIVE authored cost, BEFORE any check-then-debit reasoning. Every
            // affordability pre-check below is a "have >= cost" comparison, which a negative cost passes trivially —
            // but the DEBITS then DISAGREE: ModifierStore.TryDebitEnergy explicitly REFUSES a cost < 0 (returns false,
            // mutates nothing — "never refund a negative cost"), while ResourceStore.SpendOre/SpendCrystal happily
            // subtract it and therefore GRANT resources. That asymmetry IS a partial spend: no energy debited,
            // ore/crystal credited, the graph run and the cooldown started. AbilityValidator already rejects a
            // negative cost at authoring time ((b) cost sign); this is the runtime's fail-closed backstop for a def
            // that never went through it (hand-built, editor-draft, or a future non-validated path).
            if (ab.CostEnergy < Fixed.Zero || ab.CostOre < 0 || ab.CostCrystal < 0 || ab.CostHealth < 0)
            {
                _events?.PushDenied(world.Position[id], faction, DenialReason.InvalidTarget);
                _log?.Warn($"[AbilityCastSystem] cast refused: ability '{ab.Id}' declares a NEGATIVE cost " +
                           $"(energy raw={ab.CostEnergy.Raw}, ore={ab.CostOre}, crystal={ab.CostCrystal}, health={ab.CostHealth}) — " +
                           "the content validator rejects this shape; refusing rather than partial-spending.");
                return;
            }

            // Affordability — CHECK ALL three, mutate NOTHING yet (AC6 atomic: a failed crystal check must not have
            // debited energy/ore). Costs are int on the ability → Fixed.FromInt.
            Fixed oreCost     = Fixed.FromInt(ab.CostOre);
            Fixed crystalCost = Fixed.FromInt(ab.CostCrystal);
            if (world.Energy[id] < ab.CostEnergy)
            {
                _events?.PushDenied(world.Position[id], faction, DenialReason.NoEnergy);
                return;
            }
            if (!_resources.CanAffordOre(faction, oreCost))
            {
                _events?.PushDenied(world.Position[id], faction, DenialReason.NeedOre);
                return;
            }
            if (!_resources.CanAffordCrystal(faction, crystalCost))
            {
                _events?.PushDenied(world.Position[id], faction, DenialReason.NeedCrystal);
                return;
            }
            // Story 2.13 (AC5.3, D-4): a self HP-cost cast that would bring the caster to ≤0 HP is REFUSED — UNLESS the
            // ability is an intentional self-lethal ("suicide-bomber"). Checked BEFORE any debit (atomic refuse, reading
            // the folded Health), closing the §2.10 repeated-self-cast 0-HP-alive strand for every protected ability.
            if (!ab.AllowSelfLethal && world.Health[id] <= Fixed.FromInt(ab.CostHealth))
            {
                _events?.PushDenied(world.Position[id], faction, DenialReason.InvalidTarget); // would be self-lethal
                return;
            }

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
            {
                _events?.PushDenied(world.Position[id], faction, DenialReason.InvalidTarget); // Story 11.4: no valid target
                return;
            }

            // Debit ALL (every gate passed → each refuse-when-insufficient call necessarily succeeds; atomic).
            // DW-283: the returns are the PROOF of that claim, not decoration — DISCARDING them is what let a
            // check/debit disagreement (see the negative-cost gate above) run the graph on a partially-spent cast.
            // Short-circuit so a refusal never even attempts the later debits, then UNDO whatever already landed and
            // refuse with ZERO net mutation (the AC6 refuse-atomic contract). Both undos are exact — Fixed is integer
            // arithmetic, and each is the precise inverse of the debit that ran. Unreachable for validated content;
            // this is the fail-closed backstop that makes the "necessarily succeeds" comment enforceable.
            bool spentEnergy  = _modifiers.TryDebitEnergy(id, ab.CostEnergy);
            bool spentOre     = spentEnergy && _resources.SpendOre(faction, oreCost);
            bool spentCrystal = spentOre    && _resources.SpendCrystal(faction, crystalCost);
            if (!spentCrystal)
            {
                if (spentOre)    _resources.AddOre(faction, oreCost);   // exact inverse of SpendOre
                if (spentEnergy) world.Energy[id] += ab.CostEnergy;     // exact inverse of TryDebitEnergy
                _events?.PushDenied(world.Position[id], faction, DenialReason.InsufficientResources);
                _log?.Warn($"[AbilityCastSystem] cast of '{ab.Id}' by entity {id} refused at the debit step " +
                           $"(energy={spentEnergy}, ore={spentOre}, crystal={spentCrystal}) after every affordability " +
                           "gate passed — debits rolled back. This is a check/debit disagreement, not a shortfall.");
                return;
            }

            // Execute the validated effect graph (mirrors ModifierStore.RunEffect). Rebuild the spatial hash so a
            // SearchArea (e.g. fireball) queries CURRENT positions; harmless for non-SearchArea graphs (they ignore
            // ctx.Spatial). Passing the modifier store is MANDATORY — an ApplyModifier/Persistent leaf throws on a
            // null store (battle_fury is one).
            _spatial.Rebuild(world);
            var ctx = new EffectContext(world, casterId: id, primaryTargetId: target, casterFaction: faction,
                                        _damageTable, spatial: _spatial, _events, _stats, modifierStore: _modifiers, deaths: _deaths,
                                        alliances: _alliances); // Story 9.14: team-aware cast Ally/Enemy filter
            _executor.Run(ab.EffectGraph, in ctx);

            // Story 7.13 — raise ability_cast at the atomic-success point (every gate passed, all costs debited, the
            // effect graph executed): caster entity id + the ability registry index, keyed on the caster's faction
            // slot. Captured even for a self-lethal cast (the effect already ran; the caster id is still valid here).
            // Null feed (bare tests) → no-op.
            _dslSimEvents?.Push(DslSimEventFeed.KindAbilityCast, (int)faction - 1, id, regIdx, 0);

            // Story 2.13 (AC5.4, D-4): the self HP-cost is debited AFTER the effect graph resolves (matching the
            // migrated abilities' old graph-tail direct_hp_delta point → the Health trajectory stays byte-identical),
            // then the DEFERRED self-death fires via the SAME entity-death sequence combat uses (the suicide's effect
            // already ran; the caster is never destroyed mid-its-own-effect). The gate above guarantees this only
            // reaches ≤0 when allow_self_lethal. A killed caster starts no cooldown and emits no cast feedback.
            if (ab.CostHealth > 0)
            {
                // Story 2.13 review (patch): the effect graph above could already have killed the caster (a future
                // creator-authored Self/SearchArea damage leaf). Mirror ModifierStore.Advance's IsAlive guard so we
                // never debit a dead/recycled slot or fire a SECOND entity-death (duplicate UnitKilled + double RecordKill).
                if (!world.IsAlive(id)) return;
                world.Health[id] -= Fixed.FromInt(ab.CostHealth);
                if (world.Health[id] <= Fixed.Zero)
                {
                    DamageResolver.KillEntity(world, id, faction, _events, _stats, _deaths, attackerId: id); // Story 7.5 — a self-lethal cast credits the caster
                    // DW-620: KillEntity now REFUSES the death of an Invulnerable caster (the recorded "Invulnerable =
                    // death-immunity" ruling), so this is no longer unconditionally fatal — re-check before assuming it
                    // died. The cost stays SPENT (that is the ruling's "self-costs remain spend-able" half), but the
                    // raw debit above can leave Health NEGATIVE, and returning here would skip the cooldown, letting a
                    // death-immune caster re-cast every tick and walk Health down toward the Fixed underflow. So floor
                    // the pool at 0 — the same [0, MaxHealth] convention DirectHpDeltaEffect uses for a self-cost — and
                    // fall through to the normal cooldown/feedback tail: the cast succeeded, it just did not kill its
                    // caster. Byte-identical for a killable caster (IsAlive is false ⇒ the original early return).
                    if (!world.IsAlive(id)) return;
                    world.Health[id] = Fixed.Zero;
                }
            }

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
