#nullable enable
using System; // Func (the injected respawn delegate — Story 3.14)
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions; // UnitDefinition (respawn def)
using ProjectChimera.Core.Stats; // Story 15-24a — StatId / StatDelta / StatVocabulary (the sparse mint vectors)
using ProjectChimera.Effects; // ModifierStore / Modifier / StackRule (permanent growth modifier)

namespace ProjectChimera.Combat
{
    /// <summary>
    /// Story 3.13 — the deterministic hero XP / leveling / stat-growth runtime. Runs at tick index 9 (AFTER
    /// <see cref="CombatSystem"/> + <see cref="ProjectileSystem"/> have recorded deaths into the <see cref="DeathFeed"/>).
    /// Each tick:
    ///   1. <b>Credit</b>: drain the <see cref="DeathFeed"/> in recorded order; for each death, for each live hero in
    ///      <see cref="HeroStore.FoldOrder"/> (ascending <see cref="HeroId"/>) on a faction != the victim's, whose entity
    ///      is alive and link-valid, within its <see cref="HeroStore.XpShareRadiusOf"/>, add the victim's bounty to
    ///      <see cref="HeroStore.Xp"/> (full bounty per hero — proximity credit, not split), saturated at
    ///      <see cref="XpCeiling"/> and only while below <see cref="HeroStore.MaxLevelOf"/>.
    ///   2. <b>Level</b>: advance <see cref="HeroStore.Level"/> against the geometric curve
    ///      (<c>BaseXp × XpGrowth^(level-1)</c>), consuming XP per level, with a total max-level clamp (no overflow/throw).
    ///   3. <b>Grow</b>: reconcile per-level stat growth — desired stacks = <c>Level-1</c> — applying the delta through the
    ///      FOLDED <see cref="ModifierStore.Apply"/> (permanent, <see cref="StackRule.Stack"/>), then set
    ///      <see cref="HeroStore.GrowthStacksApplied"/>. Covers both mid-match level-ups and deploy-at-level-N catch-up.
    /// Finally <see cref="DeathFeed.Clear"/> the feed.
    ///
    /// <para>Determinism: <see cref="Fixed"/> (16.16) only, no <c>float</c>/<c>Mathf</c>/RNG/wall-clock; deaths in recorded
    /// order, heroes in <see cref="HeroStore.FoldOrder"/>. Growth NEVER goes through the unfolded
    /// <c>ModifierSystem.AccumulateBonus</c> — only the folded <see cref="ModifierStore.Apply"/> (bypassing the store
    /// would mutate unhashed sim truth → desync).</para>
    ///
    /// <para><b>DW-766 — the drain is NOT the last word on the feed.</b> This system sits at index [9], but the
    /// <see cref="DeathFeed"/> has producers AFTER it: <c>ItemSystem</c> at [10] reaches
    /// <see cref="ModifierStore.Apply"/>, whose DW-325 ceiling-collapse kill pushes a record (DW-490 threaded the shared
    /// feed into the store), and <c>ScenarioDirector</c> at [15] holds the feed in its <c>run_effect</c>
    /// <c>EffectContext</c>. So this <see cref="Tick"/>'s <see cref="DeathFeed.Clear"/> could NOT make the feed empty at
    /// the checksum boundary, falsifying the "provably drained ⇒ not folded" premise that
    /// <see cref="DeathFeed"/>/<see cref="SimChecksum"/> both state, and landing the residue's hero XP a tick late.
    /// <see cref="DrainResidue"/> closes it: a second, credit-only pass run by <see cref="DeathFeedDrainSystem"/> AFTER
    /// the last producer.</para>
    /// </summary>
    public sealed class HeroXpSystem : ISimSystem
    {
        /// <summary>XP saturation ceiling (Story 3.13, D4) — guards the 16.16 <see cref="Fixed"/> range (~32767) against
        /// the validator's extreme authored curves (MaxLevel up to 100, XpGrowth up to 100). Accumulated XP and computed
        /// curve thresholds saturate here, so leveling is "clamped, no overflow, no exception".</summary>
        public static readonly Fixed XpCeiling = Fixed.FromInt(30000);

        /// <summary>Reserved <see cref="Modifier.Id"/> for the per-hero permanent growth modifier (Story 3.13, D3). A
        /// distinctive high constant so it can never collide with an authored ability-modifier id.</summary>
        public const int HeroGrowthModifierId = 0x3133_0000; // "31 33" ~ 3.13

        /// <summary>Story 15-21: reserved <see cref="Modifier.Id"/> for the per-hero permanent BASE-attribute
        /// modifier — the level-1 attribute contributions (str→hp etc.), one stack, installed once at the first
        /// reconcile (idempotent via <see cref="StackRule.Ignore"/>: a live same-id instance ignores re-applies).
        /// Skipped entirely when all four modifier-channel base contributions are zero (the DW-678 lesson — never
        /// burn a ring slot on an all-zero modifier). Per-LEVEL attribute contributions ride the
        /// <see cref="HeroGrowthModifierId"/> stacks instead (one modifier, not one per level).</summary>
        public const int HeroAttrBaseModifierId = 0x3135_2100; // "31 35 21" ~ 15.21

        /// <summary>Max growth stacks = the hero level ceiling (100) minus 1, so a valid hero never saturates the stack cap.</summary>
        public const int MaxGrowthStacks = 99;

        private readonly HeroStore     _heroes;
        private readonly ModifierStore _modifiers;
        private readonly DeathFeed     _deaths;

        // ── Story 3.14 (hero death & revival) — nullable so the pre-3.14 XP-only callers (HeroXpTests, HeroXpScenario)
        //    that pass none still compile and the revival state machine stays inert for them (behaves exactly as 3.13). ──
        private readonly BuildingStore?      _buildings;
        private readonly RevivalRuleRuntime? _revival;
        /// <summary>The injected respawn delegate — the shared unit-spawn path (world.Create + ApplyUnitDefinition +
        /// MeshType, as ScenarioApplier.SpawnUnit does), taking (def, faction, x, z) and returning the new entity id or
        /// -1. Injected (never duplicated) for Godot-free testability.</summary>
        private readonly Func<UnitDefinition, Faction, Fixed, Fixed, int>? _spawn;
        private readonly CombatEventQueue?   _events;

        // Story 7.13 — the trigger-DSL sim-event feed (hero_level raised when a hero advances a level). Wired by
        // SimulationHost after construction; null ⇒ no raise.
        private DslSimEventFeed? _dslSimEvents;
        /// <summary>Story 7.13 — wire the trigger-DSL sim-event feed so a hero level-up raises hero_level.</summary>
        public void SetDslSimEvents(DslSimEventFeed? feed) => _dslSimEvents = feed;

        public HeroXpSystem(HeroStore heroes, ModifierStore modifiers, DeathFeed deaths,
                            BuildingStore? buildings = null, RevivalRuleRuntime? revival = null,
                            Func<UnitDefinition, Faction, Fixed, Fixed, int>? spawn = null,
                            CombatEventQueue? events = null)
        {
            _heroes    = heroes;
            _modifiers = modifiers;
            _deaths    = deaths;
            _buildings = buildings;
            _revival   = revival;
            _spawn     = spawn;
            _events    = events;
        }

        public void Tick(EntityWorld world, Fixed dt)
        {
            int[] order = _heroes.FoldOrder(); // ascending HeroId — the deterministic hero iteration order

            // ── 1. Credit XP for each recorded death, to every hostile hero in range ──────────────────────────────
            CreditRecordedDeaths(world, order);

            // ── 2 + 3 (+ Story 3.14 death/countdown/respawn). Advance levels + reconcile growth for on-field heroes; run
            //           the revival state machine for the rest. Runs even with no deaths so the deploy-at-level-N growth
            //           catch-up applies on the first tick. ────────────────────────────────────────────────────────────
            for (int oi = 0; oi < order.Length; oi++)
            {
                int slot = order[oi];
                bool live = IsLiveLinkedHero(world, slot, _heroes.EntityId[slot]);

                // Story 3.14: the revival state machine is active only when a RevivalRuleRuntime is wired (the pre-3.14
                // XP-only callers pass none → the store stays inert and behaves exactly as Story 3.13).
                if (_revival != null)
                {
                    // (a) DEATH DETECTION — an on-field hero whose entity just died (link stale) transitions off-field.
                    if (_heroes.Alive3_14[slot] && !live) { HandleHeroDeath(slot); continue; }
                    // (b) A fallen hero: run the revival countdown (only when awaiting AND a revive was ordered).
                    if (!_heroes.Alive3_14[slot])
                    {
                        if (_heroes.AwaitingRevival[slot] && _heroes.RevivalLink[slot] != HeroStore.REVIVAL_NONE)
                            TickRevivalCountdown(world, slot, dt);
                        continue;
                    }
                }
                else if (!live)
                {
                    // Pre-3.14 behaviour: a hero whose entity is dead / link-stale must NOT keep leveling from banked XP.
                    continue;
                }

                // On-field & live: level + reconcile growth (ReconcileGrowth re-checks the live link internally).
                AdvanceLevels(world, slot, _heroes.EntityId[slot]);
                ReconcileGrowth(world, slot);
            }

            // ── Feed is per-tick transient: clear the records this pass consumed. Producers registered AFTER this
            //    system (ItemSystem [10], ScenarioDirector [15]) can still push afterwards — DrainResidue, run past
            //    the last producer by DeathFeedDrainSystem, is what actually makes the feed empty at the checksum
            //    boundary (DW-766). ──
            _deaths.Clear();
        }

        /// <summary>
        /// DW-766 — the SECOND, credit-only drain pass, run by <see cref="DeathFeedDrainSystem"/> AFTER the last
        /// <see cref="DeathFeed"/> producer (past <c>ScenarioDirector</c> at index [15], not merely past
        /// <c>ItemSystem</c> at [10]). Credits every record pushed after this system's own <see cref="Tick"/> to the
        /// hostile heroes in range — through the SAME <see cref="CreditRecordedDeaths"/> implementation, never a second
        /// copy of the rule — and then <see cref="DeathFeed.Clear"/>s the feed, so it is genuinely EMPTY at the checksum
        /// boundary. That is the invariant <see cref="DeathFeed"/> and <see cref="SimChecksum"/> both cite as the reason
        /// the feed is excluded from the fold; before this pass the claim was false for an
        /// <c>ItemSystem</c>-driven ceiling collapse or a director <c>run_effect</c> kill.
        ///
        /// <para><b>Credit only — level/growth deliberately stay at index [9] next tick.</b> Running
        /// <see cref="AdvanceLevels"/>/<see cref="ReconcileGrowth"/> here would (a) re-enter
        /// <see cref="ModifierStore.Apply"/> AFTER the feed was cleared, whose own ceiling-collapse kill would push a
        /// NEW record and re-open the very hole this closes, and (b) raise <c>hero_level</c> into the transient
        /// <c>DslSimEventFeed</c> after <c>ScenarioDirector</c> already drained and cleared it — breaking that feed's
        /// identical empty-at-the-boundary posture. Banking the XP is what the ruling requires ("hero XP must not land
        /// a tick late"); the level advance lands at [9] on the next tick, exactly when it did before this fix, and the
        /// credit-only pass writes nothing but the already-folded <see cref="HeroStore.Xp"/>.</para>
        ///
        /// <para>A tick with no residue is a strict no-op (the early return), so every tick whose deaths were all
        /// recorded before index [9] — which is every recorded golden — is bit-identical to the pre-fix runtime.</para>
        /// </summary>
        public void DrainResidue(EntityWorld world)
        {
            if (_deaths.Count == 0) return; // no post-[9] producer fired this tick → strict no-op

            CreditRecordedDeaths(world, _heroes.FoldOrder());
            _deaths.Clear();
        }

        /// <summary>
        /// The single XP-credit implementation (Story 3.13 step 1), shared by <see cref="Tick"/> and
        /// <see cref="DrainResidue"/>: drain the <see cref="DeathFeed"/> in recorded order and, for each death, credit
        /// every live link-valid hero in <paramref name="order"/> on a hostile faction, in range and below its max
        /// level. Writes ONLY <see cref="HeroStore.Xp"/> — no modifier install, no event push — so it can never itself
        /// produce a death record.
        /// </summary>
        private void CreditRecordedDeaths(EntityWorld world, int[] order)
        {
            int deaths = _deaths.Count;
            for (int d = 0; d < deaths; d++)
            {
                DeathRecord death = _deaths.Get(d);
                for (int oi = 0; oi < order.Length; oi++)
                {
                    int slot = order[oi];
                    int entityId = _heroes.EntityId[slot];
                    if (!IsLiveLinkedHero(world, slot, entityId)) continue;           // dead hero / stale link → skip
                    if (world.FactionOf[entityId] == death.Faction) continue;         // friendly/own death → no XP
                    if (_heroes.Level[slot] >= _heroes.MaxLevelOf[slot]) continue;    // at max level → XP ignored

                    Fixed r = _heroes.XpShareRadiusOf[slot];
                    // Range test in long-widened raw units. The hero↔death separation is bounded only by map size
                    // (coords up to ±map_bounds), NOT by the validator-capped radius — so a single-axis gap past
                    // ~181 units overflows the int32 truncation inside Fixed '*'/'+' (SqrDistance = X²+Y²+Z²) and
                    // wraps NEGATIVE, which reads as "in range" and would credit a kill across the whole map,
                    // defeating xp_share_radius. Compute each squared term as a shifted long (matching Fixed '*' =
                    // (raw*raw)>>16) and sum in long so the comparison can never wrap. Pure integer math →
                    // deterministic; for in-range distances the result is identical to the Fixed path (goldens stable).
                    FixedVec3 hp = world.Position[entityId];
                    long dxr = (long)hp.X.Raw - death.Position.X.Raw;
                    long dyr = (long)hp.Y.Raw - death.Position.Y.Raw;
                    long dzr = (long)hp.Z.Raw - death.Position.Z.Raw;
                    long sqrDist = ((dxr * dxr) >> 16) + ((dyr * dyr) >> 16) + ((dzr * dzr) >> 16);
                    long rr = ((long)r.Raw * r.Raw) >> 16; // r validator-capped (<128) → safe, widened for uniformity
                    if (sqrDist > rr) continue; // out of range

                    // DW-26: scale the victim bounty by THIS hero's XP-gain multiplier (xp_per_kill / 100, resolved to
                    // Fixed at the applier boundary). Compute in WIDENED long raw — a large factor × a near-ceiling bounty
                    // overflows a 16.16 Fixed '*' — and saturate the credit to [0, XpCeiling] before it is banked. The
                    // neutral Fixed.One (raw 65536) makes (bountyRaw × 65536) >> 16 == bountyRaw EXACTLY, so every hero
                    // authored 100 (or minted without a factor) credits the full bounty, bit-identical to the pre-DW-26
                    // runtime — no golden move, no SimChecksum fold.
                    long factorRaw = _heroes.XpGainFactorOf[slot].Raw;
                    long creditedRaw = ((long)death.Bounty.Raw * factorRaw) >> 16;
                    if (creditedRaw > XpCeiling.Raw) creditedRaw = XpCeiling.Raw;
                    else if (creditedRaw < 0) creditedRaw = 0;

                    // Widen to long so the raw add can never wrap int32 (a credit near the ceiling added to an
                    // already-near-ceiling Xp, or ≥2 deaths in one tick, would overflow a Fixed '+' and wrap NEGATIVE —
                    // the one-sided '> XpCeiling' check could not catch that). Saturate high, floor at 0 (defensive; the
                    // resolved credit is clamped ≥ 0 above). D4: "clamped, no overflow, no exception".
                    long sum = (long)_heroes.Xp[slot].Raw + creditedRaw;
                    if (sum > XpCeiling.Raw) sum = XpCeiling.Raw;
                    else if (sum < 0) sum = 0;
                    _heroes.Xp[slot] = Fixed.FromRaw((int)sum);
                }
            }
        }

        /// <summary>Story 3.14: an on-field hero's entity has died. Transition the PERSISTED row (never recycled) into
        /// the awaiting-revival state (revival enabled) or simply off-field (disabled) — leaving Level/Xp intact so the
        /// row still finalizes for persistence (FR-7a). The fall's presentation announcement is pushed separately at
        /// <see cref="DamageResolver.KillEntity"/> (which owns the death position, D-1); here we only mutate the folded
        /// state deterministically off the entity↔hero link scan.</summary>
        private void HandleHeroDeath(int slot)
        {
            _heroes.Alive3_14[slot] = false; // off the field either way
            if (_revival!.Enabled)
            {
                _heroes.AwaitingRevival[slot] = true;
                _heroes.RevivalTimer[slot]    = Fixed.Zero;
                _heroes.RevivalLink[slot]     = HeroStore.REVIVAL_NONE; // dead-no-order (0 is a valid PackRef → use -1)
            }
            else
            {
                // Disabled: leaves the field like any unit; NOT awaiting. The row stays HeroStore.Alive so persistence
                // still snapshots its grown Level/Xp at match end (D-7).
                _heroes.AwaitingRevival[slot] = false;
                _heroes.RevivalTimer[slot]    = Fixed.Zero;
                _heroes.RevivalLink[slot]     = HeroStore.REVIVAL_NONE;
            }
        }

        /// <summary>Story 3.14: tick one awaiting hero's revival countdown. Cancels deterministically (no refund, D-8) if
        /// the linked revive building is gone; otherwise decrements <see cref="HeroStore.RevivalTimer"/> by the shared
        /// <paramref name="dt"/> and, on reaching ≤0, respawns the hero at the building.</summary>
        private void TickRevivalCountdown(EntityWorld world, int slot, Fixed dt)
        {
            // The revive building must still exist (ABA-safe via the packed ref). Lost mid-countdown → cancel, stay
            // awaiting so the player can re-order elsewhere. No gold refund (storing the committed cost would need a
            // fifth folded field → v12 bump, out of bounds — D-8).
            if (_buildings == null || !_buildings.TryResolveRef(_heroes.RevivalLink[slot], out int bId))
            {
                _heroes.RevivalTimer[slot] = Fixed.Zero;
                _heroes.RevivalLink[slot]  = HeroStore.REVIVAL_NONE;
                return;
            }

            _heroes.RevivalTimer[slot] = _heroes.RevivalTimer[slot] - dt;
            if (_heroes.RevivalTimer[slot] > Fixed.Zero) return; // still counting

            RespawnHero(world, slot, bId);
        }

        /// <summary>Story 3.14: the countdown reached zero with the building alive — respawn a FRESH entity at the
        /// building through the shared spawn path, restore the hero's identity/Level/Xp onto it at the authored HP
        /// fraction, reset <see cref="HeroStore.GrowthStacksApplied"/> to 0 then re-apply Level-1 growth onto the new
        /// entity in-tick (the D-3 binding obligation), re-link <c>EntityId</c>/<c>HeroIndex</c>, and clear the
        /// revival state back to on-field. A spawn failure (no def / delegate / world full) cancels deterministically
        /// (stays awaiting, no refund) so the player can re-order.</summary>
        private void RespawnHero(EntityWorld world, int slot, int bId)
        {
            UnitDefinition? def = _heroes.SourceDef[slot];
            if (def == null || _spawn == null)
            {
                // Cannot respawn (Tier-1 mint without a def, or no delegate) → cancel this attempt deterministically.
                _heroes.RevivalTimer[slot] = Fixed.Zero;
                _heroes.RevivalLink[slot]  = HeroStore.REVIVAL_NONE;
                return;
            }

            FixedVec3 bpos = _buildings!.Position[bId];
            Faction faction = _buildings.FactionOf[bId]; // equals the validated OwnerFaction[slot]
            int newEntity = _spawn(def, faction, bpos.X, bpos.Z);
            if (newEntity < 0)
            {
                // World full THIS tick — do NOT cancel: that would silently drop an already-paid revival and force the
                // player to pay again. Keep the building link and pin the timer at zero so the countdown re-attempts the
                // respawn next tick at no extra cost, reviving as soon as an entity slot frees (or cancelling if the
                // building is lost, handled in TickRevivalCountdown).
                _heroes.RevivalTimer[slot] = Fixed.Zero;
                return;
            }

            // Re-link the persisted row to the fresh entity + reset growth so it re-materializes onto the new entity.
            _heroes.EntityId[slot]            = newEntity;
            world.HeroIndex[newEntity]        = _heroes.PackRef(slot);
            _heroes.GrowthStacksApplied[slot] = 0;

            // Back on the field; clear the revival state.
            _heroes.Alive3_14[slot]       = true;
            _heroes.AwaitingRevival[slot] = false;
            _heroes.RevivalTimer[slot]    = Fixed.Zero;
            _heroes.RevivalLink[slot]     = 0; // on-field default (matches Mint); the countdown reads REVIVAL_NONE only

            // Re-materialize per-level growth onto the fresh entity NOW (same tick), THEN set current Health to the
            // authored fraction of the hero's GROWN max. Order is load-bearing: ReconcileGrowth applies (Level-1) stacks
            // and each positive-MaxHealth stack HEALS current Health by +HealthPerLevel (ModifierStore.ApplyStatDeltas,
            // Decision #3). If the fraction were applied first (growth deferred to next tick), those (Level-1) heals would
            // stack on top of the already fraction-scaled Health and the hero would settle FAR above the authored fraction
            // (a level-N hero at 0.5 would end near full). Reconciling first makes EffectiveMaxHealth the true grown max;
            // scaling by the fraction lands the settled HP at exactly fraction × grown max, and next tick's ReconcileGrowth
            // is a no-op. EffectiveMaxHealth (already saturated + stack-capped) is the single grown-max source — no second
            // HP formula that could drift from ReconcileGrowth's stack cap.
            ReconcileGrowth(world, slot);
            world.Health[newEntity] = world.EffectiveMaxHealth[newEntity] * _revival!.HpFraction;

            _events?.Push(CombatEventType.HeroRevived, bpos);
        }

        /// <summary>True iff the hero at <paramref name="slot"/> is alive and its entity link still resolves to THIS
        /// row (ABA-safe via <see cref="HeroStore.TryResolveRef"/>) — so growth/XP never touch a recycled entity.</summary>
        private bool IsLiveLinkedHero(EntityWorld world, int slot, int entityId)
        {
            if (!_heroes.Alive[slot]) return false;
            if (!world.IsAlive(entityId)) return false;
            return _heroes.TryResolveRef(world.HeroIndex[entityId], out int linked) && linked == slot;
        }

        /// <summary>Advance <see cref="HeroStore.Level"/> while accumulated XP covers the next geometric threshold,
        /// consuming that threshold each level, up to <see cref="HeroStore.MaxLevelOf"/>. Degenerate curves
        /// (BaseXp &lt;= 0 or XpGrowth &lt; 1 — e.g. an un-minted default) are skipped (no throw, no instant-max).</summary>
        private void AdvanceLevels(EntityWorld world, int slot, int entityId)
        {
            Fixed baseXp = _heroes.BaseXpOf[slot];
            Fixed growth = _heroes.XpGrowthOf[slot];
            if (baseXp <= Fixed.Zero || growth < Fixed.One) return; // degenerate/un-minted curve → no leveling

            int maxLevel = _heroes.MaxLevelOf[slot];
            while (_heroes.Level[slot] < maxLevel)
            {
                Fixed threshold = ThresholdFor(_heroes.Level[slot], baseXp, growth);
                if (_heroes.Xp[slot] >= threshold)
                {
                    _heroes.Xp[slot]  = _heroes.Xp[slot] - threshold;
                    _heroes.Level[slot]++;
                    // Story 7.13 — raise hero_level at the level-advance site: the hero's entity id + the NEW level,
                    // keyed on the hero's faction slot. Once per level gained (a multi-level tick raises each).
                    // Null feed (bare tests) → no-op.
                    _dslSimEvents?.Push(DslSimEventFeed.KindHeroLevel,
                        entityId >= 0 && entityId < world.HighWaterMark ? (int)world.FactionOf[entityId] - 1 : -1,
                        entityId, _heroes.Level[slot], 0);
                }
                else break;
            }
        }

        /// <summary>The XP required to advance FROM <paramref name="level"/> to level+1 = <c>baseXp × growth^(level-1)</c>,
        /// computed with saturation at <see cref="XpCeiling"/> so an extreme authored curve never overflows the 16.16
        /// <see cref="Fixed"/> during the multiply (the check divides the ceiling by growth — safe since growth &gt;= 1).</summary>
        private static Fixed ThresholdFor(int level, Fixed baseXp, Fixed growth)
        {
            Fixed t = baseXp;
            if (t > XpCeiling) t = XpCeiling;
            Fixed ceilOverGrowth = XpCeiling / growth; // growth >= 1 (guaranteed by the caller) → result <= XpCeiling
            for (int i = 1; i < level; i++)            // multiply (level-1) times
            {
                if (t >= ceilOverGrowth) { t = XpCeiling; break; } // t * growth would exceed the ceiling → saturate
                t = t * growth;
            }
            return t;
        }

        /// <summary>Reconcile per-level stat growth to <c>desired = Level-1</c> stacks of the permanent growth modifier
        /// via the FOLDED <see cref="ModifierStore.Apply"/> (D3). Applies <c>desired - GrowthStacksApplied</c> more stacks
        /// and records the new count. No-op when already reconciled, or when the hero entity is dead/link-stale (revival
        /// is Story 3.14). Idempotent; covers mid-match level-ups AND the deploy-at-level-N first-tick catch-up.
        /// <para>DW-650: the descriptor minted below is one of the three <see cref="Modifier"/> minters that never reach
        /// <c>AbilityValidator</c>, so DW-488's <see cref="Modifier.CheckAuthoringBounds"/> accumulator bound is adopted
        /// at this path's own content gate — <c>UnitDefinitionValidator.CheckHeroGrowth</c> runs THIS exact shape
        /// (<see cref="StackRule.Stack"/> × the hero's <c>max_level - 1</c> worst case) over the authored
        /// <c>hero.*_per_level</c> fields. Changing the shape here (a new delta channel, a different stack rule) must be
        /// mirrored there or the bound stops covering what this actually installs.</para></summary>
        private void ReconcileGrowth(EntityWorld world, int slot)
        {
            int entityId = _heroes.EntityId[slot];

            // ── Story 15-21: the BASE attribute contributions (level-1 values × the faction's derived mapping) ────
            // Installed once as a permanent one-stack modifier; StackRule.Ignore makes every later reconcile a
            // no-op against the live instance (no folded "applied" flag needed — idempotence is the stack rule's).
            // All-zero contributions install NOTHING (DW-678: an empty modifier must never burn a ring slot).
            // Ordered BEFORE the growth early-out so a LEVEL-1 hero (desired == applied == 0) still gets its base.
            // Story 15-24a: the channel split generalizes over the WHOLE registry — every attribute index whose
            // stat is modifier-authorable contributes to the minted vector (the index IS the StatId; the energy
            // pair is declared NOT modifier-authorable, so it stays on the AttributeStatAt read seam exactly as
            // 15-21 built it — the "never double-read the modifier-channel stats" rule, now enforced by the
            // registry flag instead of a hand-kept four-index list).
            int aBase = slot * AttributeStats.Count;
            StatDelta[] baseDeltas = BuildAttrVector(_heroes.AttrStatBase, aBase);
            if (baseDeltas.Length != 0) // DW-678 generalized: an empty vector must never burn a ring slot
            {
                if (!IsLiveLinkedHero(world, slot, entityId)) return; // dead/stale hero → nothing this tick
                var baseMod = new Modifier(
                    HeroAttrBaseModifierId,
                    durationTicks: -1,           // permanent, non-dispellable (the growth-modifier posture)
                    StackRule.Ignore,            // a live instance ignores re-applies → install-once idempotence
                    maxStacks: 1,
                    baseDeltas,
                    status: StatusFlags.None,
                    periodEffect: null,
                    periodTicks: 0);
                _modifiers.Apply(entityId, baseMod, entityId, world.FactionOf[entityId]);
            }

            int desired = _heroes.Level[slot] - 1;
            if (desired < 0) desired = 0;
            int applied = _heroes.GrowthStacksApplied[slot];
            if (desired <= applied) return; // already reconciled (growth is never removed by this system)

            if (!IsLiveLinkedHero(world, slot, entityId)) return; // dead/stale hero → apply no growth this tick

            // Story 15-21: the per-stack deltas are the FLAT authored growth PLUS the per-level attribute
            // contributions (attr per-level gains × the faction's derived mapping) — one modifier, (Level−1)
            // stacks, exactly the Story 3.13 channel; 15-24a: built as the canonical sparse vector (the three
            // flat lanes keep their historical channels; every other registry stat rides the attr term).
            // DW-650: UnitDefinitionValidator.CheckHeroGrowth mirrors the flat-lane shape — change both together.
            var growthScratch = new System.Collections.Generic.List<StatDelta>(4)
            {
                new StatDelta(StatId.MaxHealth, _heroes.HealthPerLevelOf[slot]),
                new StatDelta(StatId.AttackDamage, _heroes.DamagePerLevelOf[slot]),
                new StatDelta(StatId.Armor, _heroes.ArmorPerLevelOf[slot]),
                // no flat move-speed growth lane — Story 3.13 scope; the attr term below carries move speed
            };
            AppendAttrVector(growthScratch, _heroes.AttrStatPerLevel, aBase);
            var growthMod = new Modifier(
                HeroGrowthModifierId,
                durationTicks: -1,               // permanent (never expires by duration; non-dispellable)
                StackRule.Stack,
                maxStacks: MaxGrowthStacks,
                StatVocabulary.Canonicalize(growthScratch),
                status: StatusFlags.None,
                periodEffect: null,
                periodTicks: 0);

            Faction faction = world.FactionOf[entityId];
            for (int k = applied; k < desired; k++)
                _modifiers.Apply(entityId, growthMod, entityId, faction); // each Apply adds one stack + re-adds the deltas

            _heroes.GrowthStacksApplied[slot] = desired;
        }

        /// <summary>
        /// Story 15-24a — one hero's attribute contributions from a stride-<c>AttributeStats.Count</c> lane as a
        /// canonical sparse vector, taking every index whose registry stat is MODIFIER-AUTHORABLE (the index is
        /// the StatId — one shared index space). The energy pair (declared, targetable, NOT modifier-authorable)
        /// is skipped by the flag, preserving its 15.12 read-seam lane.
        /// </summary>
        private static StatDelta[] BuildAttrVector(Fixed[] lane, int aBase)
        {
            var scratch = new System.Collections.Generic.List<StatDelta>(4);
            AppendAttrVector(scratch, lane, aBase);
            return StatVocabulary.Canonicalize(scratch);
        }

        /// <inheritdoc cref="BuildAttrVector"/>
        private static void AppendAttrVector(System.Collections.Generic.List<StatDelta> scratch, Fixed[] lane, int aBase)
        {
            for (int s = 0; s < StatVocabulary.Count; s++)
            {
                if (!StatVocabulary.All[s].ModifierAuthorable) continue; // the energy pair's read-seam lane
                Fixed v = lane[aBase + s];
                if (v.Raw != 0) scratch.Add(new StatDelta((StatId)s, v));
            }
        }
    }
}
