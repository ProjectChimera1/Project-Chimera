#nullable enable
using ProjectChimera.Core;
using ProjectChimera.Effects; // ModifierStore / Modifier / StackRule (permanent growth modifier)

namespace ProjectChimera.Combat
{
    /// <summary>
    /// Story 3.13 — the deterministic hero XP / leveling / stat-growth runtime. Runs at tick index 8 (AFTER
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
    /// Finally <see cref="DeathFeed.Clear"/> the feed (so it is empty at the checksum boundary — NOT folded).
    ///
    /// <para>Determinism: <see cref="Fixed"/> (16.16) only, no <c>float</c>/<c>Mathf</c>/RNG/wall-clock; deaths in recorded
    /// order, heroes in <see cref="HeroStore.FoldOrder"/>. Growth NEVER goes through the unfolded
    /// <c>ModifierSystem.AccumulateBonus</c> — only the folded <see cref="ModifierStore.Apply"/> (bypassing the store
    /// would mutate unhashed sim truth → desync).</para>
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

        /// <summary>Max growth stacks = the hero level ceiling (100) minus 1, so a valid hero never saturates the stack cap.</summary>
        public const int MaxGrowthStacks = 99;

        private readonly HeroStore     _heroes;
        private readonly ModifierStore _modifiers;
        private readonly DeathFeed     _deaths;

        public HeroXpSystem(HeroStore heroes, ModifierStore modifiers, DeathFeed deaths)
        {
            _heroes    = heroes;
            _modifiers = modifiers;
            _deaths    = deaths;
        }

        public void Tick(EntityWorld world, Fixed dt)
        {
            int[] order = _heroes.FoldOrder(); // ascending HeroId — the deterministic hero iteration order

            // ── 1. Credit XP for each recorded death, to every hostile hero in range ──────────────────────────────
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

                    // Widen to long so the raw add can never wrap int32 (a bounty near the ceiling added to an
                    // already-near-ceiling Xp, or ≥2 deaths in one tick, would overflow a Fixed '+' and wrap NEGATIVE —
                    // the one-sided '> XpCeiling' check could not catch that). Saturate high, floor at 0 (defensive; the
                    // resolved bounty is clamped ≥ 0). D4: "clamped, no overflow, no exception".
                    long sum = (long)_heroes.Xp[slot].Raw + death.Bounty.Raw;
                    if (sum > XpCeiling.Raw) sum = XpCeiling.Raw;
                    else if (sum < 0) sum = 0;
                    _heroes.Xp[slot] = Fixed.FromRaw((int)sum);
                }
            }

            // ── 2 + 3. Advance levels and reconcile growth for every live hero (runs even with no deaths so the
            //           deploy-at-level-N catch-up applies growth on the first tick) ────────────────────────────────
            for (int oi = 0; oi < order.Length; oi++)
            {
                int slot = order[oi];
                // A hero whose entity is dead / link-stale (awaiting revival is Story 3.14) must NOT keep leveling from
                // previously-banked XP — that would mutate (and fold) the level of a hero not on the field. Gate the
                // whole level+grow step on the live link (ReconcileGrowth re-checks internally; this also gates AdvanceLevels).
                if (!IsLiveLinkedHero(world, slot, _heroes.EntityId[slot])) continue;
                AdvanceLevels(slot);
                ReconcileGrowth(world, slot);
            }

            // ── Feed is per-tick transient: clear so it is empty at the checksum boundary (NOT folded). ──
            _deaths.Clear();
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
        private void AdvanceLevels(int slot)
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
        /// is Story 3.14). Idempotent; covers mid-match level-ups AND the deploy-at-level-N first-tick catch-up.</summary>
        private void ReconcileGrowth(EntityWorld world, int slot)
        {
            int desired = _heroes.Level[slot] - 1;
            if (desired < 0) desired = 0;
            int applied = _heroes.GrowthStacksApplied[slot];
            if (desired <= applied) return; // already reconciled (growth is never removed by this system)

            int entityId = _heroes.EntityId[slot];
            if (!IsLiveLinkedHero(world, slot, entityId)) return; // dead/stale hero → apply no growth this tick

            var growthMod = new Modifier(
                HeroGrowthModifierId,
                durationTicks: -1,               // permanent (never expires by duration; non-dispellable)
                StackRule.Stack,
                maxStacks: MaxGrowthStacks,
                maxHealthDelta:    _heroes.HealthPerLevelOf[slot],
                attackDamageDelta: _heroes.DamagePerLevelOf[slot],
                moveSpeedDelta:    Fixed.Zero,   // no move-speed growth channel (Story 3.13 scope)
                status: StatusFlags.None,
                periodEffect: null,
                periodTicks: 0,
                armorDelta:        _heroes.ArmorPerLevelOf[slot]);

            Faction faction = world.FactionOf[entityId];
            for (int k = applied; k < desired; k++)
                _modifiers.Apply(entityId, growthMod, entityId, faction); // each Apply adds one stack + re-adds the deltas

            _heroes.GrowthStacksApplied[slot] = desired;
        }
    }
}
