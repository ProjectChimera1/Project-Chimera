#nullable enable
using ProjectChimera.Core;
using ProjectChimera.Effects;   // StatusFlags (DW-266 invulnerability gate; a value enum, same sim layer)

namespace ProjectChimera.Combat
{
    /// <summary>
    /// Inputs for one damage application. The CALLER supplies <see cref="TargetArmor"/> (live world armor
    /// for melee/splash, or the spawn-time SNAPSHOT armor for a projectile primary hit) and
    /// <see cref="Killer"/>, so the resolver never re-reads live armor and the projectile snapshot is
    /// preserved bit-for-bit (Story 1.6 AC2). A plain <c>readonly struct</c> passed <c>in</c> — no Span/ref field.
    /// </summary>
    public readonly struct DamageContext
    {
        public readonly EntityWorld World;
        public readonly int TargetId;
        public readonly ArmorType TargetArmor;
        public readonly Faction Killer;
        public readonly DamageTable Table;
        public readonly CombatEventQueue? Events;
        public readonly MatchStats? Stats;
        /// <summary>Story 3.13: optional transient death feed. On a lethal hit, <see cref="KillEntity"/> records the
        /// victim's <c>{position, faction, XpBounty}</c> here (BEFORE Destroy) so <see cref="HeroXpSystem"/> can credit
        /// hostile heroes in range. Null in bare combat tests / non-XP call sites (no death is recorded).</summary>
        public readonly DeathFeed? Deaths;

        /// <summary>Story 7.5: the attacker ENTITY id (−1 = unknown, the default). Hitscan passes the striking
        /// unit; the projectile path passes the source id snapshotted at spawn; call sites that don't know an
        /// attacker leave −1. On lethal damage <see cref="DamageResolver.KillEntity"/> writes it into the victim's
        /// killer-attribution SoA for the <c>unit_dies</c> event payload.</summary>
        public readonly int AttackerId;

        /// <summary>Story 7.13: optional transient sim-event feed. On any hit, <see cref="DamageResolver.Apply"/>
        /// raises a <c>unit_damaged</c> occurrence here (victim / attacker / amount) for the trigger DSL. Null in bare
        /// combat tests / non-DSL call sites (no event is raised).</summary>
        public readonly DslSimEventFeed? DslSimEvents;

        /// <summary>
        /// Story 15-24b — TRUE only for a WEAPON attack's primary hit (melee hitscan; a projectile's tracked
        /// primary impact). This is the dodge roll's provenance gate: only weapon hits can be dodged — splash
        /// (area damage, not an aimed attack) and every effect-graph <c>damage</c> leaf (ability casts, on-hit
        /// riders, DoT periods — spell dodge/spell crit are their own future stats) pass the default FALSE and
        /// never roll, never draw. An optional TRAILING ctor param, so every pre-15-24b construction site is
        /// source-compatible and provenance-correct by default.
        /// </summary>
        public readonly bool IsWeaponHit;

        public DamageContext(EntityWorld world, int targetId, ArmorType targetArmor, Faction killer,
                             DamageTable table, CombatEventQueue? events, MatchStats? stats, DeathFeed? deaths = null,
                             int attackerId = -1, DslSimEventFeed? dslSimEvents = null, bool isWeaponHit = false)
        {
            World = world;
            TargetId = targetId;
            TargetArmor = targetArmor;
            Killer = killer;
            Table = table;
            Events = events;
            Stats = stats;
            Deaths = deaths;
            AttackerId = attackerId;
            DslSimEvents = dslSimEvents;
            IsWeaponHit = isWeaponHit;
        }
    }

    /// <summary>
    /// The single damage code path (AR-26 / FR-44). final = max(0, amount * matrix[type][armor] − EffectiveArmor)
    /// (Story 2.6 added the flat post-matrix armor term, floored at 0; with the default BaseArmor=0 it is a no-op, so
    /// pre-2.6 combat outcomes are unchanged). Unifies the formula + Health subtraction + death sequence (UnitKilled
    /// event, RecordKill, Destroy) across the three call sites.
    /// The pre-hit feedback event and the melee attacker-cleanup stay at the call sites (they differ per
    /// site), so this preserves the exact event/death order the golden checksums pin (Story 1.6 AC2).
    /// </summary>
    public static class DamageResolver
    {
        /// <summary>
        /// Apply <paramref name="amount"/> of <paramref name="type"/> damage to <c>ctx.TargetId</c>,
        /// scaled by the table multiplier for the caller-supplied <c>ctx.TargetArmor</c>. Subtracts Health,
        /// and on lethal damage pushes <see cref="CombatEventType.UnitKilled"/>, records the kill, and
        /// destroys the target. Returns <c>true</c> if the target died (so a melee caller can clear its
        /// attack state); projectile callers ignore the return value.
        /// </summary>
        public static bool Apply(in DamageContext ctx, Fixed amount, DamageType type) =>
            Apply(in ctx, amount, type, out _);

        /// <summary>
        /// Story 15-24b overload — same contract as <see cref="Apply(in DamageContext, Fixed, DamageType)"/>,
        /// with <paramref name="dodged"/> reporting whether the victim's dodge roll negated this hit (always
        /// false for non-weapon damage). The hitscan caller reads it to skip the on-hit rider — a dodged
        /// attack landed nothing, so it procs nothing.
        /// </summary>
        public static bool Apply(in DamageContext ctx, Fixed amount, DamageType type, out bool dodged)
        {
            dodged = false;
            EntityWorld world = ctx.World;
            int t = ctx.TargetId;
            // Defensive guard for the single reusable damage path: never apply to a dead/destroyed
            // slot. No current caller reaches this (melee, projectile-primary, and splash all check
            // aliveness upstream), so it is a no-op for the golden checksums (AC2). It exists so a
            // FUTURE caller (ability, DoT, second same-tick hit) can't produce a phantom UnitKilled
            // event or an inflated RecordKill by hitting an already-dead target.
            if (!world.IsAlive(t)) return false;
            // DW-266 — INVULNERABILITY GATE. Placed at the SINGLE entity-damage path so it covers every source at
            // once: melee hitscan, projectile primary + splash impacts, and every DamageEffect leaf (ability casts,
            // on-hit riders, modifier DoT periods). Refused BEFORE Health is touched, so an invulnerable target takes
            // no damage, raises no unit_damaged DSL occurrence, and can never be killed by this call. Returns false
            // (= "did not die"), so the attacker keeps its target and keeps swinging. Deliberately NOT applied to
            // direct_hp_delta / cost_health self-costs — those are not damage. Every recorded golden leaves
            // StatusFlagsOf at None, so this branch is never entered there and no checksum moves.
            if ((world.StatusFlagsOf[t] & StatusFlags.Invulnerable) != 0) return false;
            // Story 15-24b — THE DODGE ROLL: the damage-ARRIVAL half of the combat dice, at the single entity-
            // damage path so hitscan and projectile-primary hits share one roll site. Rolls ONLY when this is a
            // weapon hit (ctx.IsWeaponHit — splash/ability damage never dodges) AND the victim actually has
            // dodge (a zero chance draws NOTHING, so content without the stat leaves the SimRng stream
            // byte-identical — the golden-neutrality gate, same posture as the v23/v26 bounded folds).
            // DRAW-ORDER DISCIPLINE (the 15-24b spec's core concern): draws ride the shared world.Rng stream
            // inside the deterministic tick — CombatSystem iterates ascending id and ProjectileSystem resolves
            // impacts in ascending projectile order, so every peer reaches the rolls in the identical sequence.
            // Rolls live in the SIM, never presentation. A dodge negates the ENTIRE hit before any state moves:
            // no damage, no unit_damaged occurrence, no kill — and returns false ("did not die"), so the
            // attacker keeps its target and keeps swinging, exactly like the invulnerability refuse above. The
            // AttackDodged event is presentation-only (the unfolded CombatEventQueue).
            if (ctx.IsWeaponHit && world.EffectiveDodgeChance[t].Raw > 0
                && world.Rng.NextFixed() < world.EffectiveDodgeChance[t])
            {
                dodged = true;
                // NULL profile (review fix): AudioManager's profile-first route is type-agnostic, so a
                // profile-carrying push would play the victim's IMPACT sound for the hit it just dodged.
                // The DW-996 render arms decide their own payload.
                ctx.Events?.Push(CombatEventType.AttackDodged, world.Position[t], world.FactionOf[t]);
                return false;
            }
            // Story 2.6 (Decision #6): flat post-matrix armor subtraction, floored at 0 so a hit never heals. With the
            // default BaseArmor=0 (and no armor modifier) EffectiveArmor=0 → the term is −0, leaving every pre-2.6
            // combat outcome unchanged; the goldens move ONLY from the EffectiveArmor checksum fold (v8), not the math.
            // Story 2.9a: the matrix+floor math is now the shared DamageTable.FinalDamage helper (building damage reuses it).
            Fixed damage = ctx.Table.FinalDamage(amount, type, ctx.TargetArmor, world.EffectiveArmor[t]);
            world.Health[t] = world.Health[t] - damage;
            // Story 7.13 — raise unit_damaged at the single damage-application site (victim / attacker / amount),
            // deterministically into the trigger DSL sim-event feed (fires even if this hit is lethal — the death
            // sequence below then also raises unit_dies). Null feed (bare combat tests) → no-op.
            // Story 15-23: the attacker rides PACKED (the KillerOf posture) — HeroXpSystem's revival respawn
            // (system index 11) can Create between this push (index 9/10) and the director's drain (index 17), so a
            // dead attacker's slot CAN recycle within the tick; the drain resolves it (dead keeps credit, recycled
            // degrades to -1). The victim (p0) stays raw: it is the occurrence's identity payload, the unit_dies
            // victim posture.
            ctx.DslSimEvents?.Push(DslSimEventFeed.KindUnitDamaged,
                (int)world.FactionOf[t] - 1, t, world.PackRefOrNone(ctx.AttackerId), damage.ToInt());
            if (world.Health[t] <= Fixed.Zero)
            {
                KillEntity(world, t, ctx.Killer, ctx.Events, ctx.Stats, ctx.Deaths, ctx.AttackerId);
                return true;
            }
            return false;
        }

        /// <summary>
        /// The single ENTITY-DEATH sequence (Story 2.13 extract): push <see cref="CombatEventType.UnitKilled"/> (reading
        /// the dying unit's Position/feedback override BEFORE Destroy — Story 2.7), record the kill (victim, killer), and
        /// <see cref="EntityWorld.Destroy"/> the entity. Reused verbatim by <see cref="Apply"/> on lethal damage AND by
        /// <c>AbilityCastSystem</c> when a <c>cost_health</c> self-cost brings the caster to ≤0 (AC5.4) — so a self-lethal
        /// cast dies through the EXACT same path, never an invented one. Caller ensures Health has already reached ≤0.
        /// DW-493: fail-closed on a dead/recycled <paramref name="id"/> (a no-op — no event, no stats, no death record),
        /// so the primitive itself can never double-kill even if a future caller forgets its own aliveness check.
        /// DW-620: also fail-closed on <see cref="StatusFlags.Invulnerable"/> — the recorded ruling makes that flag
        /// DEATH-immunity, so every direct-kill caller (ceiling collapse, self-lethal cast) is refused here rather
        /// than each having to remember the check. Callers must therefore re-check <c>IsAlive</c> if they intend to
        /// act on "it died"; a refused kill leaves the host alive and otherwise untouched.
        /// </summary>
        public static void KillEntity(EntityWorld world, int id, Faction killer, CombatEventQueue? events,
                                      MatchStats? stats, DeathFeed? deaths = null, int attackerId = -1)
        {
            // DW-493: fail-closed entry guard on the reused death primitive itself. Every CURRENT caller checks
            // aliveness at its own site (Apply's entry guard above; AbilityCastSystem's self-lethal pre-check;
            // ModifierStore's ceiling-collapse gate), so this is a behavior-preserving no-op today — it exists so a
            // FUTURE caller (or a double-collapse in one tick) that reaches the kill without re-checking cannot
            // double-Destroy, emit a phantom UnitKilled/HeroFell, inflate RecordKill, or push a ghost death record.
            if (!world.IsAlive(id)) return;
            // DW-620 (decision 2026-08-05 — "Invulnerable = DEATH-immunity, not merely damage-immunity"). The DW-266
            // gate lives on Apply, which stops every DAMAGE source; but two callers reach the death sequence WITHOUT
            // going through Apply and so used to kill an invulnerable unit outright:
            //   • ModifierStore's DW-325 ceiling-collapse kill — a net-negative −MaxHealth debuff that drives
            //     EffectiveMaxHealth from above zero to zero calls KillEntity directly, and
            //   • AbilityCastSystem's deferred self-lethal cost_health death.
            // The ruling puts the guard on the single death PRIMITIVE, so it covers those two and any future direct
            // caller for free. Self-costs stay SPEND-ABLE: this refuses the DEATH, never the HP debit — direct_hp_delta
            // still clamps the pool to 0 and cost_health is still paid; the caster simply survives at 0 HP.
            // Refused BEFORE any mutation ⇒ a refused kill is a strict no-op: no killer attribution, no death-log
            // record, no UnitKilled/HeroFell event, no RecordKill, no DeathFeed push, no Destroy.
            // Unreachable from Apply (its own DW-266 guard returns first). Every recorded golden leaves StatusFlagsOf
            // at None, so this branch is never entered there and no checksum moves.
            if ((world.StatusFlagsOf[id] & StatusFlags.Invulnerable) != 0) return;
            // Story 7.5: the SINGLE write point for the killer-attribution SoA — the attacker ref (−1 = unknown)
            // plus the killer faction SNAPSHOTTED as a slot (Neutral → −1; Player1 → 0), written BEFORE Destroy
            // recycles the slot so ScenarioDirector's death diff can read the unit_dies payload this tick.
            // Story 15-23 (DW-775): stored PACKED (PackRefOrNone), because the payload is read up to one tick
            // LATER (the DW-548 carry rail) and a train-spawn can recycle the killer's slot even within this tick —
            // the event build resolves it with the attribution rule (dead attacker keeps credit; a recycled slot
            // degrades to −1 rather than crediting the new occupant). Golden-neutral at generation 0.
            // Derived attribution state, NOT folded into SimChecksum (the _prevFlags basis).
            world.KillerOf[id]        = world.PackRefOrNone(attackerId);
            world.KillerFactionOf[id] = (int)killer - 1; // Neutral (0) → −1; player factions → slot
            // DW-367: record this death in the world's per-tick death LOG (victim id, victim faction slot, killer
            // ref (packed — the KillerOf posture above), killer faction slot — snapshotted BEFORE Destroy recycles
            // the slot). ScenarioDirector's unit_dies source drains the log so a same-tick die→recycle→die on one
            // slot surfaces BOTH kills with their own attribution — the per-slot SoA above can only ever carry the
            // last one. On capacity overflow the record deterministically drops and the director's flags-diff
            // fallback covers the slot exactly as before.
            world.DeathLog.Push(id, (int)world.FactionOf[id] - 1, world.PackRefOrNone(attackerId), (int)killer - 1);
            events?.Push(CombatEventType.UnitKilled, world.Position[id], world.FactionOf[id], world.FeedbackProfile[id]); // Story 11.4: stamp the victim faction
            stats?.RecordKill(world.FactionOf[id], killer);
            // Story 3.13: record the death for the XP runtime BEFORE Destroy recycles the slot (the corpse's
            // position/faction/bounty are unobservable afterward — D-1). Uniform for hitscan/projectile/self-lethal.
            deaths?.Push(world.Position[id], world.FactionOf[id], world.XpBounty[id]);
            // Story 3.14 (D-1): announce a HERO's fall at its death position (still valid pre-Destroy). HeroIndex cheaply
            // identifies a hero entity with no HeroStore threading into this static kill path; the awaiting-revival STATE
            // transition is done separately (deterministically) by HeroXpSystem's link-based scan. Presentation-only event.
            if (world.HeroIndex[id] != EntityWorld.HERO_NONE)
                events?.Push(CombatEventType.HeroFell, world.Position[id], world.FactionOf[id], world.FeedbackProfile[id]); // Story 11.4: stamp the victim faction
            world.Destroy(id);
        }

        /// <summary>
        /// Story 2.9a (AC2): apply matrix damage to a <b>building</b> in <paramref name="buildings"/> — the parallel
        /// path to <see cref="Apply"/> (which is hard-bound to <see cref="EntityWorld"/> and cannot target a building).
        /// This is the SINGLE building-damage entry point shared by the melee instant path (Task 6) and the ranged
        /// projectile impact (Task 4b), so the two can never drift. Buildings use <see cref="ArmorType.Fortified"/> and
        /// have no flat armor (flat term = <see cref="Fixed.Zero"/>). Bounds/Alive are guarded defensively here so a
        /// stale id is a harmless no-op; the friendly-faction / domain checks live at the CALL site (in-tick, before
        /// this is reached). Returns <c>true</c> if the building died this call. Writes only <c>buildings.Health</c> /
        /// <c>buildings.Alive</c>, which are already folded into <see cref="SimChecksum"/> — no new fold.
        /// </summary>
        public static bool ApplyToBuilding(BuildingStore buildings, int b, Fixed amount, DamageType type,
                                           DamageTable table, CombatEventQueue? events = null,
                                           Faction razer = Faction.Neutral, MatchStats? stats = null)
        {
            if (b < 0 || b >= buildings.Count || !buildings.Alive[b]) return false;
            Fixed damage = table.FinalDamage(amount, type, ArmorType.Fortified, Fixed.Zero);
            buildings.Health[b] = buildings.Health[b] - damage;
            if (buildings.Health[b] <= Fixed.Zero)
            {
                // Story 11.2 — the razed column means "ENEMY buildings razed": read the victim's owner BEFORE Destroy
                // recycles the slot, then credit the razer only when it is neither Neutral nor the building's OWN owner
                // (a faction demolishing its own building — or a neutral building razed by nobody — must not count).
                Faction owner = buildings.FactionOf[b];
                events?.Push(CombatEventType.BuildingDestroyed, buildings.Position[b], owner); // Story 11.4: stamp the razed building's owner faction
                buildings.Destroy(b);
                // `stats` null / Neutral razer / self-raze ⇒ no-op; the counter is observational (unfolded).
                if (razer != Faction.Neutral && razer != owner)
                    stats?.RecordBuildingRazed(razer);
                return true;
            }
            return false;
        }
    }
}
