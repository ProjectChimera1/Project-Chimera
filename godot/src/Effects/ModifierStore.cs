#nullable enable
using ProjectChimera.Combat;   // DamageTable / CombatEventQueue / MatchStats (period-effect resolution sinks)
using ProjectChimera.Core;     // EntityWorld, Fixed, Faction
using ProjectChimera.Core.Sim; // ILogSink — the AR-4 injected diagnostic seam (DW-83 refused-install warning)

namespace ProjectChimera.Effects
{
    /// <summary>
    /// The AR-9 <b>ModifierStore</b> (Story 2.2b) — the net-new SoA store of active, timed <see cref="Modifier"/> /
    /// <see cref="PersistentEffect"/> instances that DRIVES the Story 2.2a effective-stat pipeline. It installs,
    /// stacks, refreshes, and expires modifiers; runs the <c>PersistentEffect</c> time-axis (DoT/HoT); debits
    /// <see cref="EntityWorld.Energy"/> with refuse-when-insufficient; clears an entity's modifier state on
    /// death/recycle (via the <see cref="EntityWorld.OnDestroy"/> hook); and folds its mutable state into
    /// <see cref="SimChecksum"/> (the one scheduled <c>AlgoVersion 5→6</c> re-baseline). The primitive without which
    /// MOBA/TD/RPG content (buffs, debuffs, auras, DoT/HoT, stat modifiers) cannot exist.
    ///
    /// <para><b>Self-contained SoA (Decision #2 = Option B).</b> The store owns its slot arrays (it is not slots on
    /// <see cref="EntityWorld"/>); its numeric state folds into the checksum via a new <c>SimChecksum.Compute(…,
    /// ModifierStore)</c> parameter. Per-entity fixed-capacity slot ring: flat arrays sized
    /// <c>MAX_ENTITIES * <see cref="EffectCaps.MaxModifiersPerEntity"/></c>, plus a per-entity <c>_count</c>. Slot
    /// index for entity <c>id</c>, slot <c>s</c> is <c>id * MaxModifiersPerEntity + s</c>. The iteration contract for
    /// BOTH the fold and <see cref="Advance"/> is <b>ascending owner-id then ascending slot</b>.</para>
    ///
    /// <para><b>Determinism.</b> Pure C#: no <c>using Godot;</c>, no <c>float</c>/<c>double</c>, no
    /// <c>Fixed.FromFloat</c> (Fixed arithmetic only), no <c>System.Random</c> (the shared <see cref="SimRng"/> via
    /// the context), no <c>Dictionary</c>/<c>HashSet</c> enumeration (flat-array SoA; index accessors expose the fold
    /// fields), named caps only. The foldable per-instance fields are all <c>int</c>: <c>_modifierId</c>,
    /// <c>_remainingTicks</c>, <c>_ticksUntilPeriod</c>, <c>_periodsRemaining</c>, <c>_stackCount</c>. The descriptor
    /// references + caster id/faction are NOT folded — authored / peer-identical by construction (like a
    /// <c>UnitDefinition</c> reference). Nor is the diagnostic set (<c>_log</c>, <c>_refusedInstalls</c> — DW-83;
    /// <c>_skippedPulses</c> — DW-662): no sim branch reads them, so they cannot move a checksum or a golden.</para>
    ///
    /// <para><b>Re-entrancy.</b> The store runs ALL THREE <see cref="PersistentEffect"/> phases —
    /// <c>InitialEffect</c> (on install), <c>PeriodEffect</c> (each pulse) and <c>ExpireEffect</c> (on removal) — plus a
    /// <see cref="Modifier.PeriodEffect"/> pulse (DW-271), on its OWN dedicated <see cref="EffectExecutor"/>, never
    /// shared with a graph-running executor, whose single pre-allocated work-stack running re-entrantly would clobber.
    /// Every one of those runs resolves DIRECT-TARGET: <c>RunEffectAgainst</c> is the only place a store
    /// <see cref="EffectContext"/> is built and it always passes <c>spatial: null</c>, so a phase subtree is
    /// <see cref="SequenceEffect"/>s of direct-target leaves (DirectHpDelta/Heal/Damage) and nothing else.</para>
    ///
    /// <para>What holds that line is a MECHANISM, not a version. The Story 2.3 content validator
    /// (<c>AbilityValidator</c>'s AC5 walk) fail-closed REJECTS an install leaf — an
    /// <see cref="ApplyModifierEffect"/> or a nested <see cref="PersistentEffect"/> — anywhere inside a Persistent
    /// phase OR inside a <c>Modifier.period_effect</c> subtree, and rejects a <see cref="SearchAreaEffect"/> in those
    /// same places (no per-tick spatial rebuild exists). So no loadable ability can put one on this executor. The
    /// hazard being fenced off is not period-specific: an install leaf in ANY of the phases would re-enter the
    /// dedicated executor AND mutate <c>_count</c> mid-<see cref="Advance"/>. A future phase that itself installs
    /// needs a fail-closed re-entrancy guard or a deferred-application queue HERE, with the validator fence relaxed in
    /// the same change (deferred-work, code-review 2.2b W1).</para>
    /// </summary>
    public sealed class ModifierStore
    {
        /// <summary>
        /// <c>_remainingTicks</c> sentinel for a PERMANENT modifier (<c>Modifier.DurationTicks &lt; 0</c>): never
        /// decremented, removed only explicitly or on recycle. A fixed constant so the fold mixes it deterministically.
        /// </summary>
        public const int PERMANENT = int.MinValue;

        /// <summary>
        /// DW-83 log throttle: the FIRST refused install is always warned, then one line per this many further
        /// refusals. Not a gameplay cap (so deliberately NOT in <see cref="EffectCaps"/>, which is folded into the
        /// ruleset hash) — a pure diagnostic cadence. Needed because an aura RE-GRANTS its modifier every tick
        /// (<c>AbilityCastSystem.TickAuras</c>): an un-throttled warn on one ring-full aura target would be 30
        /// lines per second.
        /// </summary>
        public const int RefusedInstallLogEvery = 64;

        // ── Foldable per-instance numeric state (all int, ascending owner-id then slot — the determinism contract) ──
        private readonly int[] _modifierId;        // Modifier.Id (0 for a pure PersistentEffect instance — see Apply scan)
        private readonly int[] _remainingTicks;    // duration countdown (PERMANENT sentinel = never expires by duration)
        private readonly int[] _ticksUntilPeriod;  // ticks until the next DoT/HoT pulse (0 when the instance has no period)
        private readonly int[] _periodsRemaining;  // remaining pulses (Persistent: its lifetime; Modifier: a re-armed schedule width — DW-271)
        private readonly int[] _stackCount;         // simultaneous stacks (shared-duration model; all expire together)

        // ── Non-folded per-instance state (authored / peer-identical by construction; like a UnitDefinition ref) ──
        private readonly Modifier?[] _modifier;             // the installing Modifier descriptor (null for a Persistent instance)
        private readonly PersistentEffect?[] _persistent;   // the installing PersistentEffect descriptor (null for a Modifier instance)
        private readonly int[] _casterId;
        private readonly Faction[] _casterFaction;

        private readonly int[] _count; // active slots per entity (the [0,_count) dense window the fold + Advance read)

        // ── Wired deps ──
        private readonly EntityWorld _world;
        private readonly ModifierSystem? _system;   // required for apply/remove (it owns AccumulateBonus/RecomputeEntity)
        private readonly DamageTable _damageTable;
        private readonly CombatEventQueue? _events;
        private readonly MatchStats? _stats;
        private readonly DeathFeed? _deaths;        // DW-490 — the XP death feed EVERY other lethal path passes to KillEntity
        private readonly EffectExecutor _executor;  // DEDICATED — never shared with a graph-running executor

        // ── DW-83 diagnostics (NEVER folded into SimChecksum; never read by any sim branch) ──
        private readonly ILogSink? _log;   // injected AR-4 seam (never a static ambient sink); null ⇒ the pre-DW-83 silent refusal
        private int _refusedInstalls;      // monotonic tally of ring-full refusals since construction / Clear
        private int _skippedPulses;        // DW-662 tally of pulses skipped because the host/target was dead (see SkippedPulseCount)

        /// <summary>
        /// Construct the store, wire deps, and subscribe the destroy hook. <paramref name="system"/>/<paramref
        /// name="events"/>/<paramref name="stats"/> are nullable so a cheap FOLD-ONLY store can be built
        /// (<c>new ModifierStore(world)</c>) for a checksum-only call site; the live host wires the full set. The
        /// <paramref name="system"/> ref is required for any real apply/remove (it calls <c>AccumulateBonus</c>); a
        /// fold-only store never applies a modifier. <paramref name="damageTable"/> resolves to
        /// <see cref="DamageTable.Default"/> (mirrors <c>CombatSystem</c>/<c>ProjectileSystem</c>).
        /// <para>DW-83: <paramref name="log"/> is the injected diagnostic seam a REFUSED (ring-full) install warns
        /// through — the AR-4 <see cref="ILogSink"/>, never a static ambient sink / <c>Console</c> / <c>GD.Print</c>.
        /// Null (every golden/headless/fold-only construction) leaves a refusal byte-identical to its pre-DW-83
        /// silent self; the live host wires its own sink. Diagnostics only: a sink must never mutate sim state.</para>
        /// <para>DW-490: <paramref name="deaths"/> is the shared <see cref="DeathFeed"/> the XP runtime drains — the
        /// SAME argument the hitscan, projectile and self-lethal-cast death paths already pass to
        /// <see cref="DamageResolver.KillEntity"/>. The store needs it because the DW-325 ceiling-collapse death is a
        /// real lethal path: without the feed an ability-driven "reduce max HP to 0" finisher would be the ONLY kill in
        /// the game that grants no hero XP. Deliberately LAST in the parameter list so every existing positional
        /// construction (tests, fold-only stores) is unchanged; a null feed simply records no death, exactly as before.</para>
        /// </summary>
        public ModifierStore(EntityWorld world, ModifierSystem? system = null, DamageTable? damageTable = null,
                             CombatEventQueue? events = null, MatchStats? stats = null, ILogSink? log = null,
                             DeathFeed? deaths = null)
        {
            _world = world;
            _system = system;
            _damageTable = damageTable ?? DamageTable.Default;
            _events = events;
            _stats = stats;
            _deaths = deaths;
            _log = log;
            _executor = new EffectExecutor(); // its own pre-allocated stack (re-entrancy-safe)

            int cap = EntityWorld.MAX_ENTITIES * EffectCaps.MaxModifiersPerEntity;
            _modifierId       = new int[cap];
            _remainingTicks   = new int[cap];
            _ticksUntilPeriod = new int[cap];
            _periodsRemaining = new int[cap];
            _stackCount       = new int[cap];
            _modifier         = new Modifier?[cap];
            _persistent       = new PersistentEffect?[cap];
            _casterId         = new int[cap];
            _casterFaction    = new Faction[cap];
            _count            = new int[EntityWorld.MAX_ENTITIES];

            world.OnDestroy += ClearEntity; // recycle safety: revert this entity's modifiers on Destroy
        }

        // ─────────────────────────────────── Apply / stack / refresh / ignore ───────────────────────────────────

        /// <summary>
        /// Install (or stack/refresh/ignore) <paramref name="mod"/> on <paramref name="targetId"/>. Stacking against
        /// an existing same-<see cref="Modifier.Id"/> instance follows the <see cref="StackRule"/> enum docs verbatim:
        /// <list type="bullet">
        /// <item><b>Refresh</b> — reset the existing instance's duration (and period schedule); no second stack/bonus.</item>
        /// <item><b>Stack</b> — up to <see cref="Modifier.MaxStacks"/>, increment <c>_stackCount</c> and re-add the deltas;
        ///   shared duration refreshed (all stacks expire together); at the cap, refresh duration only.</item>
        /// <item><b>Ignore</b> — a no-op while an instance is active (no refresh, no stack).</item>
        /// </list>
        /// A dead/stale <paramref name="targetId"/> is a no-op (no throw). A slot-full target refuses the new install
        /// DETERMINISTICALLY (drops it; never overflows the per-entity ring) — and, since DW-83, that drop is no
        /// longer SILENT: it bumps <see cref="RefusedInstallCount"/> and warns through the injected
        /// <see cref="ILogSink"/> (throttled). The refusal's behavior and state are unchanged. Persistent instances carry
        /// <c>_modifier == null</c> so they never match the same-id stacking scan (a <c>Modifier.Id == 0</c> can't
        /// collide with one).
        /// <para><b>Returns</b> <c>true</c> when the modifier was installed OR an existing same-id instance was handled
        /// (Refresh/Stack/Ignore); <c>false</c> when it was REFUSED because the target is dead/stale or the per-entity
        /// ring is full. The return value is not folded into any checksum — every path's behavior/state is unchanged;
        /// callers that ignore the result (the pre-DW-34 default) are byte-identical. The DW-34 pickup site reads it to
        /// deny a ground-item claim when the carrier is at the modifier cap.</para>
        /// <para><b>POST-CONDITION (DW-325/DW-491, audited in DW-489): this method can DESTROY
        /// <paramref name="targetId"/>.</b> A modifier whose MaxHealth delta is NET-NEGATIVE and collapses the host's
        /// <c>EffectiveMaxHealth</c> from above zero to exactly zero raises the ceiling-collapse death inside
        /// <see cref="ApplyStatDeltas"/> — a synchronous <c>EntityWorld.Destroy</c>, which fires <c>OnDestroy</c>
        /// (ClearEntity wipes this host's ring; ItemSystem drops its carried items) and returns the id to the recycle
        /// free list. The returned <c>true</c> therefore means "installed", NOT "installed AND the host is still
        /// alive" — the two are deliberately NOT distinguished in the return value (recorded decision 2026-08-03: no
        /// tri-state API for a latent, content-gated case). EVERY caller that writes further state for
        /// <paramref name="targetId"/> after this returns MUST re-check <see cref="EntityWorld.IsAlive"/> first. The
        /// three internal <see cref="ApplyStatDeltas"/> callers do so inline; the external callers are
        /// <c>ItemSystem.ApplyItemStatModifier</c>'s call sites (guarded / audited per DW-489),
        /// <c>EffectExecutor</c>'s apply_modifier case and <see cref="ApplyModifierEffect.Apply"/> (both return
        /// immediately and every subsequent leaf re-guards IsAlive), and the aura re-grant walk.</para>
        /// </summary>
        public bool Apply(int targetId, Modifier mod, int casterId, Faction casterFaction)
        {
            if (!_world.IsAlive(targetId)) return false; // IsAlive also bounds-checks the id

            int @base = targetId * EffectCaps.MaxModifiersPerEntity;
            int n = _count[targetId];

            int existing = -1;
            int sameIdCount = 0; // DW-264: StackIndependent counts same-id slots (each is its own independent stack)
            for (int s = 0; s < n; s++)
            {
                int sl = @base + s;
                if (_modifier[sl] != null && _modifierId[sl] == mod.Id)
                {
                    if (existing < 0) existing = s;
                    sameIdCount++;
                }
            }

            // DW-264 / Story 15.12: StackIndependent NEVER merges into an existing same-id slot — each application
            // installs a FRESH slot with its own duration (same Id, _stackCount=1), bounded by MaxStacks same-id slots
            // AND the per-entity ring. At the MaxStacks cap a further application is ignored (no refresh); a ring-full
            // target is refused (drop), exactly like a fresh install of any rule.
            if (mod.Stacking == StackRule.StackIndependent)
            {
                if (sameIdCount >= mod.MaxStacks) return true;          // at the per-modifier stack cap → ignore (no refresh)
                if (n >= EffectCaps.MaxModifiersPerEntity)
                {
                    NoteRefusedInstall(targetId, mod.Id, casterId, persistent: false); // ring full → refuse (DW-83 observable)
                    return false;
                }
                return InstallNewSlot(targetId, mod, casterId, casterFaction, n);
            }

            if (existing < 0)
            {
                if (n >= EffectCaps.MaxModifiersPerEntity)
                {
                    // DW-83: full → refuse (drop), never overflow. The DROP itself is unchanged (same deterministic
                    // false, same untouched state) — what changes is OBSERVABILITY: tally + a throttled warn, so an
                    // earned research/item/hero-growth buff silently lost to a full ring is debuggable.
                    NoteRefusedInstall(targetId, mod.Id, casterId, persistent: false);
                    return false;
                }
                return InstallNewSlot(targetId, mod, casterId, casterFaction, n); // fresh install accepted
            }

            int eslot = @base + existing;
            switch (mod.Stacking)
            {
                case StackRule.Refresh:
                    _remainingTicks[eslot] = mod.DurationTicks < 0 ? PERMANENT : mod.DurationTicks;
                    ResetPeriodSchedule(eslot, mod);
                    break;

                case StackRule.Stack:
                    if (_stackCount[eslot] < mod.MaxStacks)
                    {
                        _stackCount[eslot]++;
                        // DW-490: attribution follows the INSTANCE, not the re-caster — _casterId/_casterFaction are
                        // recorded once at install and a stack never rewrites them, so a collapsing stack credits the
                        // same caster the instance's period pulses (RunEffectAgainst) already resolve with.
                        ApplyStatDeltas(targetId, mod.AttackDamageDelta, mod.MaxHealthDelta, mod.MoveSpeedDelta, mod.ArmorDelta,
                                        isApply: true, _casterId[eslot], _casterFaction[eslot]); // each stack re-adds
                        // DW-325 re-entrancy: a stack that collapsed the ceiling to 0 killed the host → ClearEntity
                        // already wiped its slots; don't write status (or refresh eslot's duration below) on a dead slot.
                        if (!_world.IsAlive(targetId)) return true;
                        _world.StatusFlagsOf[targetId] |= mod.Status; // idempotent re-OR
                    }
                    // Shared duration refreshed on every (re)apply — at the cap this is the only effect (refresh-only).
                    _remainingTicks[eslot] = mod.DurationTicks < 0 ? PERMANENT : mod.DurationTicks;
                    ResetPeriodSchedule(eslot, mod);
                    break;

                case StackRule.Ignore:
                    break; // active instance → ignore the re-apply entirely
            }
            return true; // an existing same-id instance was handled (Refresh/Stack/Ignore)
        }

        /// <summary>
        /// Install <paramref name="mod"/> into a FRESH slot at index <paramref name="n"/> of <paramref name="targetId"/>'s
        /// ring (the shared install path for a first-ever apply of any rule AND every <see cref="StackRule.StackIndependent"/>
        /// application). Writes the slot's folded state, arms its period schedule, applies its stat deltas + status, and
        /// re-checks <see cref="EntityWorld.IsAlive"/> after the (possibly lethal) stat apply. Returns <c>true</c> (installed).
        /// The caller has already verified the ring has room. Extracted verbatim from the pre-15.12 inline install so both
        /// call paths stay byte-identical.
        /// </summary>
        private bool InstallNewSlot(int targetId, Modifier mod, int casterId, Faction casterFaction, int n)
        {
            int slot = targetId * EffectCaps.MaxModifiersPerEntity + n;
            _modifierId[slot]    = mod.Id;
            _modifier[slot]      = mod;
            _persistent[slot]    = null;
            _casterId[slot]      = casterId;
            _casterFaction[slot] = casterFaction;
            _stackCount[slot]    = 1;
            // DW-270: 0 is stored VERBATIM, so Advance decrements it to −1 and expires it at the end of the next
            // tick — a 0-duration modifier is a ONE-TICK modifier, never an instantaneous one (see Modifier.DurationTicks).
            _remainingTicks[slot] = mod.DurationTicks < 0 ? PERMANENT : mod.DurationTicks;
            ResetPeriodSchedule(slot, mod);
            _count[targetId] = n + 1;

            // DW-490: the instance's OWN recorded caster (just written above) is the attribution a ceiling-collapse
            // death inside ApplyStatDeltas credits — the same pair a period pulse from this slot resolves with.
            ApplyStatDeltas(targetId, mod.AttackDamageDelta, mod.MaxHealthDelta, mod.MoveSpeedDelta, mod.ArmorDelta,
                            isApply: true, _casterId[slot], _casterFaction[slot]);
            // DW-325 re-entrancy: a ceiling-collapse kill inside ApplyStatDeltas fired OnDestroy→ClearEntity, wiping
            // this host's slots/status/accumulators. Bail before writing status onto the dead (recycled) slot.
            if (!_world.IsAlive(targetId)) return true;
            _world.StatusFlagsOf[targetId] |= mod.Status;
            return true;
        }

        /// <summary>
        /// Install a pure time-axis <see cref="PersistentEffect"/> (the AC1 DoT/HoT path) into a fresh slot: runs
        /// <see cref="PersistentEffect.InitialEffect"/> now (on the dedicated executor), schedules
        /// <see cref="PersistentEffect.PeriodEffect"/> every <see cref="PersistentEffect.PeriodTicks"/> for
        /// <see cref="PersistentEffect.PeriodCount"/> pulses (clamped to <see cref="EffectCaps.MaxPersistentPeriods"/>),
        /// and runs <see cref="PersistentEffect.ExpireEffect"/> on the final period. Carries no stat deltas/status
        /// (its lifetime is the period count, so <c>_remainingTicks = PERMANENT</c>). Dead/stale or slot-full target →
        /// no-op / deterministic refuse.
        /// </summary>
        public void InstallPersistent(int targetId, PersistentEffect pe, int casterId, Faction casterFaction)
        {
            if (!_world.IsAlive(targetId)) return;

            int @base = targetId * EffectCaps.MaxModifiersPerEntity;
            int n = _count[targetId];
            if (n >= EffectCaps.MaxModifiersPerEntity)
            {
                NoteRefusedInstall(targetId, modifierId: 0, casterId, persistent: true); // DW-83: observable, not silent
                return; // full → refuse
            }

            int slot = @base + n;
            _modifierId[slot]    = 0;     // no stacking identity (scanned out of Apply via _modifier == null)
            _modifier[slot]      = null;
            _persistent[slot]    = pe;
            _casterId[slot]      = casterId;
            _casterFaction[slot] = casterFaction;
            _stackCount[slot]    = 1;
            _remainingTicks[slot] = PERMANENT; // lifetime is governed by the period count, not a duration countdown
            bool hasPeriod = pe.PeriodEffect != null && pe.PeriodTicks > 0;
            _ticksUntilPeriod[slot] = hasPeriod ? pe.PeriodTicks : 0;
            int periods = pe.PeriodCount;
            if (periods > EffectCaps.MaxPersistentPeriods) periods = EffectCaps.MaxPersistentPeriods; // named cap
            if (periods < 0) periods = 0;
            _periodsRemaining[slot] = periods;
            _count[targetId] = n + 1;

            if (pe.InitialEffect != null) RunEffect(targetId, slot, pe.InitialEffect); // one-shot install pulse
        }

        /// <summary>
        /// DW-300 — the <b>install-once probe</b> for a re-runnable install graph: does <paramref name="targetId"/>
        /// ALREADY host an instance that <paramref name="root"/> would install? Read-only; folds nothing; allocates
        /// nothing. It answers only for the two shapes a <c>while_alive</c> self-passive root is validated to be
        /// (<c>AbilityValidator</c>: "a 'while_alive' passive's effect root must be a permanent ApplyModifier or a
        /// Persistent"):
        /// <list type="bullet">
        /// <item><see cref="PersistentEffect"/> — matched by DESCRIPTOR REFERENCE. A persistent instance carries no
        ///   stacking identity at all (<c>_modifierId = 0</c>, <c>_modifier = null</c>) — the gap Story 2.13's own
        ///   "never re-InstallPersistent, which has no same-id dedup" comment names — so the authored descriptor
        ///   (one shared instance per registry entry, peer-identical by construction, like a <c>UnitDefinition</c>
        ///   reference) IS its identity.</item>
        /// <item><see cref="ApplyModifierEffect"/> — matched by <see cref="Modifier.Id"/>, i.e. exactly the same
        ///   identity <see cref="Apply"/>'s own stacking scan uses.</item>
        /// </list>
        /// Any other root shape (or a null one) returns <c>false</c> — FAIL OPEN, i.e. the caller installs exactly as
        /// it did before this probe existed. Deterministic: a pure read of <c>[0,_count)</c> in ascending slot order.
        /// </summary>
        /// <param name="targetId">The prospective host entity.</param>
        /// <param name="root">The install graph's root node (an ability's <c>EffectGraph</c>).</param>
        /// <returns><c>true</c> only when a re-run of <paramref name="root"/> would duplicate a LIVE instance.</returns>
        public bool HostsInstanceFrom(int targetId, EffectNode? root)
        {
            if (root is null || !_world.IsAlive(targetId)) return false; // IsAlive also bounds-checks the id

            int @base = targetId * EffectCaps.MaxModifiersPerEntity;
            int n = _count[targetId];
            switch (root)
            {
                case PersistentEffect pe:
                    for (int s = 0; s < n; s++)
                        if (ReferenceEquals(_persistent[@base + s], pe)) return true;
                    return false;

                case ApplyModifierEffect am when am.Modifier != null:
                    for (int s = 0; s < n; s++)
                    {
                        int sl = @base + s;
                        if (_modifier[sl] != null && _modifierId[sl] == am.Modifier.Id) return true;
                    }
                    return false;

                default:
                    return false; // unknown/unsupported root shape → never claims "already installed"
            }
        }

        /// <summary>
        /// DW-300 companion — re-derive entity <paramref name="id"/>'s <c>Effective*</c> stats from its CURRENT
        /// <c>Base*</c> plus the live modifier accumulators (<see cref="ModifierSystem.RecomputeEntity"/>, which is
        /// idempotent: <c>Effective = max(0, Base + Σbonus)</c>). Needed because
        /// <see cref="EntityWorld.ApplyUnitDefinition"/> re-mirrors <c>Base*</c> into <c>Effective*</c>, discarding
        /// every installed modifier's contribution — and <see cref="ModifierSystem.Tick"/> only recomputes entities
        /// something DIRTIED, so on a live re-apply whose install is skipped as a duplicate nothing would restore it.
        /// No-op on a fold-only store (no <see cref="ModifierSystem"/> wired); bounds/IsAlive-guarded downstream.
        /// </summary>
        public void RecomputeEffectiveStats(int id) => _system?.RecomputeEntity(_world, id);

        // ─────────────────────────────────────────── Per-tick advance ───────────────────────────────────────────

        /// <summary>
        /// The per-tick store update, called by <see cref="ModifierSystem.Tick"/> BEFORE its effective-stat recompute.
        /// Iterates ascending owner-id then ascending slot. For each active instance: fire a period pulse when its
        /// timer reaches the boundary, then count down its lifetime and <see cref="RemoveSlot"/> it on expiry. Removal
        /// swap-compacts the slot out and the walk re-tests the same index (so a sibling is never skipped or
        /// double-processed). <paramref name="dt"/> is unused (periods are tick-counted, not time-based; the 30 Hz
        /// fixed step makes one Advance == one tick).
        /// </summary>
        public void Advance(EntityWorld world, Fixed dt)
        {
            int cap = _world.HighWaterMark;
            for (int i = 0; i < cap; i++)
            {
                if (!_world.IsAlive(i)) continue;

                int @base = i * EffectCaps.MaxModifiersPerEntity;
                int n = _count[i];
                for (int s = 0; s < n; )
                {
                    int slot = @base + s;

                    // 1. Period pulse — fire on the boundary, reset the timer, consume one scheduled period.
                    if (HasPeriod(slot) && _periodsRemaining[slot] != 0)
                    {
                        _ticksUntilPeriod[slot]--;
                        if (_ticksUntilPeriod[slot] <= 0)
                        {
                            // DW-272: honor the modifier's PeriodicStacking (None/Repeat/Multiply) + stack count, cap-bounded.
                            // None / a single stack / a Persistent slot run exactly one pulse — byte-for-byte with pre-15.12.
                            RunScaledPulse(i, slot);
                            // DW-267: a LETHAL period — an authored `damage` leaf, which the ability validator accepts
                            // in a period_effect — destroys the host MID-PULSE, so OnDestroy → ClearEntity has ALREADY
                            // wiped this entity's slots and zeroed its count. Bail out of the slot loop before touching
                            // them: rewriting the schedule below onto a cleared slot expires it and then CompactSlots a
                            // ring whose _count is already 0, indexing `base − 1` (a throw at owner id 0; a _count of
                            // −1 that lands the next install in the PREVIOUS entity's ring at any higher id). BREAK,
                            // never `return` — the dead host's slot loop ends, but every higher-id entity must still
                            // pulse this same tick. No shipped content authors a lethal period yet; the guard's teeth
                            // are LethalPeriodMidAdvanceTests (which also covers RemoveSlot's expire-effect twin).
                            // DW-662: this POST-condition covers the HOST only. Its companion PRE-condition — the
                            // dead-host/dead-target refusal that fires before the executor is entered, for a pulse
                            // resolving against an entity that is not its host — lives in RunEffectAgainst.
                            if (!_world.IsAlive(i)) break;
                            _ticksUntilPeriod[slot] = PeriodLengthOf(slot);
                            _periodsRemaining[slot]--;

                            // DW-271: a MODIFIER's lifetime is its DURATION — `_periodsRemaining` is only the pulse
                            // BUDGET that ResetPeriodSchedule armed (MaxPersistentPeriods, the store's schedule width),
                            // never a lifetime. Draining it used to leave a still-active modifier silently pulse-less
                            // while it kept its stat bonus (periodTicks 1 + duration > 256, or any PERMANENT periodic
                            // modifier, went dead at pulse 256). Re-arm the SAME slot's already-folded budget in place —
                            // the Story 2.13 lifelong-persistent pattern — so the pulse cadence lasts exactly as long as
                            // the modifier does and expiry stays duration-governed (step 2 below). Fold-safe: the first
                            // MaxPersistentPeriods pulses are byte-identical to the pre-fix schedule; only the tick that
                            // used to write a terminal 0 differs. A PERSISTENT instance is NOT re-armed here — its period
                            // count IS its lifetime, and only `Lifelong` refills it (step 2).
                            if (_modifier[slot] != null && _periodsRemaining[slot] <= 0)
                                _periodsRemaining[slot] = EffectCaps.MaxPersistentPeriods;
                        }
                    }

                    // 2. Expiry — Persistent: lifetime = its period count; Modifier: its duration countdown.
                    bool expired;
                    if (_persistent[slot] != null)
                    {
                        // Story 2.13 (AC4.1): a LIFELONG persistent (a while_alive self-passive, e.g. the Sanguine
                        // Furnace HoT) never expires at the MaxPersistentPeriods cap — RE-ARM the SAME slot's already-
                        // folded period fields in place (never re-InstallPersistent, which has no same-id dedup and
                        // would stack a second concurrent HoT + exhaust the 8-slot ring). Death/recycle still clears it
                        // via OnDestroy→ClearEntity; a periodless persistent still expires at once (lifelong ignored).
                        if (_persistent[slot]!.Lifelong && HasPeriod(slot) && _periodsRemaining[slot] <= 0)
                        {
                            _periodsRemaining[slot] = EffectCaps.MaxPersistentPeriods;
                            _ticksUntilPeriod[slot] = PeriodLengthOf(slot);
                            expired = false;
                        }
                        else
                            expired = _periodsRemaining[slot] <= 0 || !HasPeriod(slot); // periodless persistent expires at once
                    }
                    else if (_remainingTicks[slot] == PERMANENT)
                        expired = false;
                    else
                    {
                        // A Modifier expires purely by DURATION (its pulse budget is re-armed above, DW-271). DW-270:
                        // an authored duration of 0 lands here as 0 → −1 → expired, i.e. one full tick of effect.
                        _remainingTicks[slot]--;
                        expired = _remainingTicks[slot] <= 0;
                    }

                    if (expired)
                    {
                        RemoveSlot(i, slot);
                        n = _count[i]; // count shrank; re-read. s unchanged → re-test the swapped-in instance here.
                        continue;
                    }
                    s++;
                }
            }
        }

        // ─────────────────────────────────────────── Remove / expiry ────────────────────────────────────────────

        /// <summary>
        /// Remove the instance at <paramref name="slot"/> from host <paramref name="hostId"/>: run its
        /// <see cref="PersistentEffect.ExpireEffect"/> (final pulse, while still applied), revert the FULL stat
        /// contribution (deltas × <c>_stackCount</c>) through the 2.2a <c>AccumulateBonus</c> seam, recompute the host's
        /// status union WITHOUT this slot (never blindly clearing a flag another modifier still holds), then
        /// swap-compact the slot out so <c>[0,_count)</c> stays dense. Deterministic given identical apply/remove order
        /// across peers (the fold reads <c>[0,_count)</c>, so a swap-compact needs no re-sort).
        /// </summary>
        private void RemoveSlot(int hostId, int slot)
        {
            EffectNode? expireEffect = _persistent[slot]?.ExpireEffect;
            if (expireEffect != null)
            {
                RunEffect(hostId, slot, expireEffect);
                // DW-267 (the period guard's twin, same walk): a lethal expire-effect killed the host → ClearEntity
                // already wiped its slots + count, so the revert/status-union/compact below would touch a dead ring.
                if (!_world.IsAlive(hostId)) return;
            }

            Modifier? mod = _modifier[slot];
            if (mod != null)
            {
                Fixed stacks = Fixed.FromInt(_stackCount[slot]); // exact for an int multiplier (no Fixed rounding)
                // DW-490: read the slot's caster BEFORE the revert — CompactSlot (below) may overwrite this slot, and a
                // collapse kill inside ApplyStatDeltas wipes the whole ring. Reverting a +MaxHealth grant to zero is
                // still "this instance's stat change killed the host", so it credits this instance's caster.
                ApplyStatDeltas(hostId, -(mod.AttackDamageDelta * stacks),
                                        -(mod.MaxHealthDelta * stacks),
                                        -(mod.MoveSpeedDelta * stacks),
                                        -(mod.ArmorDelta * stacks), isApply: false, _casterId[slot], _casterFaction[slot]);
                // DW-325 re-entrancy: reverting a +MaxHealth contribution can drop the ceiling to 0 and kill the host →
                // OnDestroy→ClearEntity already wiped its slots; bail before the status-union/compact touch dead slots
                // (mirrors the existing post-expire-effect guard above).
                if (!_world.IsAlive(hostId)) return;
            }
            RecomputeStatusUnion(hostId, excludeSlot: slot);

            CompactSlot(hostId, slot);
        }

        /// <summary>
        /// Subscriber for <see cref="EntityWorld.OnDestroy"/> — clear ALL of <paramref name="id"/>'s modifier state on
        /// death/recycle so a recycled slot can never inherit the prior occupant's modifiers (the 1.12/1.13/2.2a
        /// SoA-recycle trap). Zeroes the store slots + count + status, then zeroes the EXTERNAL
        /// <see cref="ModifierSystem"/> stat-bonus accumulators (which live outside <see cref="EntityWorld"/> and so
        /// <see cref="EntityWorld.Create"/> cannot reset on recycle — the exact gap the 2.2a code review flagged).
        /// Runs synchronously inside <see cref="EntityWorld.Destroy"/>, in deterministic order. Bounds-guarded.
        /// </summary>
        public void ClearEntity(int id)
        {
            if (id < 0 || id >= EntityWorld.MAX_ENTITIES) return;

            int @base = id * EffectCaps.MaxModifiersPerEntity;
            int n = _count[id];
            for (int s = 0; s < n; s++) ClearSlotFields(@base + s);
            _count[id] = 0;
            _world.StatusFlagsOf[id] = StatusFlags.None;

            // The external accumulators get zeroed wholesale here (cheaper + simpler than a per-slot −delta revert, and
            // equivalent — the entity is gone). This + the OnDestroy subscription are the two recycle teeth (4.3).
            _system?.ClearEntity(id);
        }

        /// <summary>
        /// Story 3.10 (UX-DR62): restore the store to its EXACT post-construction state for the Edit↔Play reset —
        /// empty every per-slot array + per-entity count AND zero the external <see cref="ModifierSystem"/>
        /// accumulators it drives (via <see cref="ModifierSystem.ClearAll"/>), so no residual buff can drift the
        /// SimChecksum after a reset (the store IS folded). It does NOT touch <see cref="EntityWorld.StatusFlagsOf"/>
        /// — <see cref="EntityWorld.Clear"/> owns that array and zeroes it in the same <c>ClearForReset</c>. A cleared
        /// store is byte-for-byte equal to a freshly-constructed one (its fold reads only <c>[0,_count)</c>, now 0).
        /// </summary>
        public void Clear()
        {
            System.Array.Clear(_modifierId);
            System.Array.Clear(_remainingTicks);
            System.Array.Clear(_ticksUntilPeriod);
            System.Array.Clear(_periodsRemaining);
            System.Array.Clear(_stackCount);
            System.Array.Clear(_modifier);
            System.Array.Clear(_persistent);
            System.Array.Clear(_casterId);
            System.Array.Clear(_casterFaction);
            System.Array.Clear(_count);
            _refusedInstalls = 0; // DW-83: the refusal tally is PER-MATCH diagnostics — a re-Play starts it clean
            _skippedPulses   = 0; // DW-662: same per-match contract for the dead-end pulse tally
            _system?.ClearAll(); // zero the external stat-bonus accumulators + dirty flags (the store's driver half)
        }

        // ─────────────────────────────────── DW-83 refused-install diagnostics ──────────────────────────────────

        /// <summary>
        /// DW-83 — monotonic count of installs REFUSED because the target's per-entity ring was already full
        /// (<see cref="EffectCaps.MaxModifiersPerEntity"/>), since construction or the last <see cref="Clear"/>.
        /// A dead/stale target is NOT counted (that is a normal race, not a lost buff). Diagnostics only: never
        /// folded into <see cref="SimChecksum"/>, never read by any sim branch, so reading or ignoring it is
        /// byte-identical. Lets a caller attribute its OWN refusal exactly (compare the value across an
        /// <see cref="Apply"/> — the seam <c>ResearchSystem</c> uses) without re-deriving the ring-full test.
        /// </summary>
        public int RefusedInstallCount => _refusedInstalls;

        /// <summary>
        /// DW-662 — monotonic count of effect pulses the store REFUSED to run because the instance's host or its
        /// resolved target was dead/stale (see <see cref="RunEffectAgainst"/>), since construction or the last
        /// <see cref="Clear"/>. Diagnostics only, exactly like <see cref="RefusedInstallCount"/>: never folded into
        /// <see cref="SimChecksum"/>, never read by any sim branch, so reading or ignoring it is byte-identical.
        /// <para>Its value on a shipped schedule is its point: every production path resolves host == target with a live
        /// host, so this stays <b>0</b>. A non-zero reading means an instance resolved against a corpse — the signal the
        /// DW-267 <c>CompactSlot</c>/<c>_count</c> corruption class was headed off, and the first thing to look at when a
        /// future <c>SpatialHash</c>-threaded AoE period starts losing pulses.</para>
        /// </summary>
        public int SkippedPulseCount => _skippedPulses;

        /// <summary>
        /// DW-83 — record a refused (ring-full) install and surface it: bump <see cref="RefusedInstallCount"/> and
        /// WARN through the injected <see cref="ILogSink"/>, naming the host, the dropped modifier, the caster, and
        /// the ids ALREADY holding the ring (so the producer that starved this install — item / hero growth /
        /// self-passive / research — is identifiable from the one line). THROTTLED to the first refusal plus one
        /// line per <see cref="RefusedInstallLogEvery"/>: an aura re-grants its modifier every tick, so a ring-full
        /// aura target would otherwise emit 30 warn lines a second. Mutates no sim state and allocates only on the
        /// throttled, sink-wired path.
        /// </summary>
        private void NoteRefusedInstall(int hostId, int modifierId, int casterId, bool persistent)
        {
            _refusedInstalls++;
            if (_log == null) return; // sink-less (goldens/headless/tests) ⇒ exactly the pre-DW-83 silent behavior
            if (_refusedInstalls != 1 && _refusedInstalls % RefusedInstallLogEvery != 0) return;

            string dropped = persistent ? "a PersistentEffect" : $"modifier id 0x{modifierId:X8}";
            _log.Warn($"[ModifierStore] install REFUSED (ring full): {dropped} from caster {casterId} was DROPPED on " +
                      $"entity {hostId} ({_world.FactionOf[hostId]}) — it already holds all " +
                      $"{EffectCaps.MaxModifiersPerEntity} modifier slots [{DescribeRing(hostId)}]. " +
                      $"Refused installs so far: {_refusedInstalls} (throttled to 1 line per {RefusedInstallLogEvery}).");
        }

        /// <summary>DW-83 — render host <paramref name="hostId"/>'s occupied ring as its per-slot
        /// <see cref="Modifier.Id"/>s in hex (a <see cref="PersistentEffect"/> instance carries no stacking identity
        /// and renders as "persistent"), ascending slot. Diagnostic string-building only — called solely from the
        /// throttled warn path.</summary>
        private string DescribeRing(int hostId)
        {
            int @base = hostId * EffectCaps.MaxModifiersPerEntity;
            int n = _count[hostId];
            var sb = new System.Text.StringBuilder(n * 12);
            for (int s = 0; s < n; s++)
            {
                if (s > 0) sb.Append(", ");
                int sl = @base + s;
                if (_persistent[sl] != null) sb.Append("persistent");
                else sb.Append("0x").Append(_modifierId[sl].ToString("X8"));
            }
            return sb.ToString();
        }

        // ─────────────────────────────────────────────── Energy ─────────────────────────────────────────────────

        /// <summary>
        /// Debit a <see cref="Fixed"/> <paramref name="cost"/> from <see cref="EntityWorld.Energy"/> for ability
        /// affordability. Succeeds (and subtracts) ONLY when <c>Energy[id] &gt;= cost</c>; otherwise REFUSES and leaves
        /// <c>Energy</c> untouched (no partial spend, no negative balance). A negative <paramref name="cost"/> is a
        /// programmer error — refused, never refunded. Dead/stale id → false (no throw). The affordability primitive the
        /// ability cast consumes: <see cref="AbilityCastSystem"/> debits through here and, when a later gate in the same
        /// cast refuses, adds back the exact same <see cref="Fixed"/> as its inverse.
        /// </summary>
        public bool TryDebitEnergy(int id, Fixed cost)
        {
            if (!_world.IsAlive(id)) return false;
            if (cost < Fixed.Zero) return false; // never refund a negative cost
            if (_world.Energy[id] >= cost)
            {
                _world.Energy[id] -= cost;
                return true;
            }
            return false; // insufficient → refuse WITHOUT mutating Energy
        }

        /// <summary>
        /// Story 3.15: remove exactly the one active <see cref="Modifier"/> instance on <paramref name="hostId"/> whose
        /// <see cref="Modifier.Id"/> equals <paramref name="modifierId"/> — reverting its full stat contribution through
        /// the shared <see cref="RemoveSlot"/> path (deltas × stack count, status-union recompute, swap-compact) WITHOUT
        /// destroying the host. Used when a carried stat item leaves the inventory (manual drop / death / consume-to-zero):
        /// the item's modifier uses a deterministic per-item <see cref="Modifier.Id"/>, so this reverts precisely that
        /// item's bonus. Returns true iff a matching instance was found + removed. A dead/stale host or an absent id is a
        /// harmless no-op (false) — e.g. death-drop after <see cref="ClearEntity"/> already wiped the entity's modifiers.
        /// <para><b>POST-CONDITION (DW-325/DW-491, audited in DW-489): this method can DESTROY
        /// <paramref name="hostId"/>.</b> Reverting a POSITIVE +MaxHealth contribution is a net-negative change, so a
        /// removal that takes the host's <c>EffectiveMaxHealth</c> from above zero to exactly zero raises the
        /// ceiling-collapse death inside <see cref="ApplyStatDeltas"/> — the same synchronous <c>Destroy</c> +
        /// <c>OnDestroy</c> re-entrancy described on <see cref="Apply"/>. Removing a carried item's modifier is
        /// therefore RE-ENTRANT into <c>ItemSystem</c>: the death hook runs <c>OnEntityDestroyed → DropAll → DropOne</c>
        /// over the SAME inventory ring the caller is mid-way through mutating. Every caller must (a) re-check
        /// <see cref="EntityWorld.IsAlive"/> before its next write for <paramref name="hostId"/>, and (b) leave no
        /// half-updated ring slot live across this call — <c>ItemSystem.UseItemCommand</c>/<c>DropOne</c> clear the
        /// in-flight slot BEFORE calling in, so the re-entrant sweep skips it instead of double-dropping it.</para>
        /// </summary>
        public bool RemoveByModifierId(int hostId, int modifierId)
        {
            if (!_world.IsAlive(hostId)) return false; // IsAlive also bounds-checks the id
            int @base = hostId * EffectCaps.MaxModifiersPerEntity;
            int n = _count[hostId];
            for (int s = 0; s < n; s++)
            {
                int sl = @base + s;
                if (_modifier[sl] != null && _modifierId[sl] == modifierId)
                {
                    RemoveSlot(hostId, sl);
                    return true;
                }
            }
            return false;
        }

        // ─────────────────────────────────────── Checksum fold accessors ────────────────────────────────────────
        // Cheap flat-array index reads (CHM0002-clean — no Dictionary/HashSet enumeration). SimChecksum.Compute folds
        // [0, CountAt(id)) per entity, ascending owner-id then slot. The descriptor refs + caster id/faction are NOT
        // folded (authored / peer-identical). The fold loop guarantees slot in [0,count), so the *At accessors index
        // directly.

        /// <summary>Active modifier-instance count for entity <paramref name="id"/> (0 outside bounds).</summary>
        public int CountAt(int id) => (uint)id < (uint)EntityWorld.MAX_ENTITIES ? _count[id] : 0;

        /// <summary>Folded: the installing <see cref="Modifier.Id"/> at this slot (0 for a Persistent instance).</summary>
        public int ModifierIdAt(int id, int slot) => _modifierId[id * EffectCaps.MaxModifiersPerEntity + slot];

        /// <summary>Folded: the duration countdown at this slot (<see cref="PERMANENT"/> sentinel never expires).</summary>
        public int RemainingTicksAt(int id, int slot) => _remainingTicks[id * EffectCaps.MaxModifiersPerEntity + slot];

        /// <summary>Folded: ticks until the next period pulse at this slot.</summary>
        public int TicksUntilPeriodAt(int id, int slot) => _ticksUntilPeriod[id * EffectCaps.MaxModifiersPerEntity + slot];

        /// <summary>Folded: remaining scheduled periods at this slot.</summary>
        public int PeriodsRemainingAt(int id, int slot) => _periodsRemaining[id * EffectCaps.MaxModifiersPerEntity + slot];

        /// <summary>Folded: the stack count at this slot.</summary>
        public int StackCountAt(int id, int slot) => _stackCount[id * EffectCaps.MaxModifiersPerEntity + slot];

        // ─────────────────────────────────── Story 11.3 — SP save/load capture/restore ────────────────────────────
        // The descriptor refs + caster id/faction are NOT folded (authored / peer-identical), but a mid-match SAVE must
        // still round-trip them: the save serializes each descriptor by its CanonicalEffectDescriptorTable index (a
        // Modifier vs a PersistentEffect), and its caster id/faction as ints. These accessors expose the un-folded slot
        // state for capture; RestoreSlot/SetCount rebuild [0,_count) on load WITHOUT re-running InitialEffect.

        /// <summary>Story 11.3 capture: the installing <see cref="Modifier"/> descriptor at this slot (null for a
        /// PersistentEffect instance). Used to serialize the slot by its canonical descriptor index.</summary>
        public Modifier? ModifierRefAt(int id, int slot) => _modifier[id * EffectCaps.MaxModifiersPerEntity + slot];

        /// <summary>Story 11.3 capture: the installing <see cref="PersistentEffect"/> descriptor at this slot (null for
        /// a Modifier instance).</summary>
        public PersistentEffect? PersistentRefAt(int id, int slot) => _persistent[id * EffectCaps.MaxModifiersPerEntity + slot];

        /// <summary>Story 11.3 capture: the caster entity id recorded at this slot.</summary>
        public int CasterIdAt(int id, int slot) => _casterId[id * EffectCaps.MaxModifiersPerEntity + slot];

        /// <summary>Story 11.3 capture: the caster faction recorded at this slot.</summary>
        public Faction CasterFactionAt(int id, int slot) => _casterFaction[id * EffectCaps.MaxModifiersPerEntity + slot];

        /// <summary>
        /// Story 11.3 (SP save/load): OVERLAY one active modifier/persistent instance at <paramref name="slot"/> on
        /// <paramref name="hostId"/> from a save, re-pointing its descriptor and writing its folded fields directly —
        /// WITHOUT re-running <c>InitialEffect</c> (a restore is not a re-cast). For a <see cref="Modifier"/> instance it
        /// re-accumulates the stat contribution (deltas × <paramref name="stackCount"/>) through the same
        /// <c>ModifierSystem.AccumulateBonus</c> seam <see cref="Apply"/> uses and re-ORs the status union, so the next
        /// tick's recompute reproduces the saved <c>Effective*</c> exactly. The caller writes slots ascending
        /// <c>0..count</c> then calls <see cref="SetCount"/>. Must run AFTER <see cref="Clear"/> (which zeroes the
        /// re-applied self-passives + accumulators) and after the EntityWorld overlay (so <c>StatusFlagsOf</c> is set).
        /// <para><b>DW-492 — deliberately NON-LETHAL, and no longer a gap.</b> This accumulates without recomputing, so
        /// it can neither clamp Health nor raise the DW-325/DW-491 ceiling-collapse death, and it must not: a per-slot
        /// check would destroy a host whose slot 0 restores a −MaxHealth debuff before slot 1 restores the +MaxHealth
        /// grant that offsets it. The dirty flag this leaves set is the handoff — <c>ModifierSystem.Tick</c> recomputes
        /// the entity once the WHOLE ring is back and routes the result through
        /// <see cref="RaiseExternalCeilingCollapse"/>, so a loaded ring that genuinely floors the ceiling at zero kills
        /// its host at the first resumed tick instead of reconstituting a living zombie. A save whose stored
        /// <c>Effective*</c> already agrees with its bonuses (every consistent save) recomputes to the same ceiling and
        /// that reconciliation is a no-op.</para>
        /// </summary>
        public void RestoreSlot(int hostId, int slot, int modifierId, int remainingTicks, int ticksUntilPeriod,
                                int periodsRemaining, int stackCount, int casterId, Faction casterFaction,
                                Modifier? modifier, PersistentEffect? persistent)
        {
            // Fail-closed against a corrupt save: reject an out-of-range host id or slot rather than writing OOB.
            if ((uint)hostId >= (uint)EntityWorld.MAX_ENTITIES || (uint)slot >= (uint)EffectCaps.MaxModifiersPerEntity)
                throw new System.IO.InvalidDataException($"SP load: modifier slot out of range (host {hostId}, slot {slot}).");
            int sl = hostId * EffectCaps.MaxModifiersPerEntity + slot;
            _modifierId[sl]       = modifierId;
            _remainingTicks[sl]   = remainingTicks;
            _ticksUntilPeriod[sl] = ticksUntilPeriod;
            _periodsRemaining[sl] = periodsRemaining;
            _stackCount[sl]       = stackCount;
            _modifier[sl]         = modifier;
            _persistent[sl]       = persistent;
            _casterId[sl]         = casterId;
            _casterFaction[sl]    = casterFaction;

            // Rebuild the external stat-bonus accumulators for a Modifier instance (Persistent instances carry no stat
            // deltas). Marks the entity dirty; the next ModifierSystem.Tick recomputes Effective = Base + Σbonus,
            // reproducing the saved (directly-restored) Effective* — never touches Health (already restored).
            if (modifier != null)
            {
                Fixed stacks = Fixed.FromInt(stackCount);
                _system?.AccumulateBonus(hostId, modifier.AttackDamageDelta * stacks, modifier.MaxHealthDelta * stacks,
                                         modifier.MoveSpeedDelta * stacks, modifier.ArmorDelta * stacks);
                if ((uint)hostId < (uint)EntityWorld.MAX_ENTITIES) _world.StatusFlagsOf[hostId] |= modifier.Status;
            }
        }

        /// <summary>Story 11.3 (SP save/load): set the active-instance count for <paramref name="id"/> after
        /// <see cref="RestoreSlot"/> has written its <c>[0,count)</c> slots. Bounds-guarded.</summary>
        public void SetCount(int id, int count)
        {
            if ((uint)id >= (uint)EntityWorld.MAX_ENTITIES) return;
            if (count < 0) count = 0;
            if (count > EffectCaps.MaxModifiersPerEntity) count = EffectCaps.MaxModifiersPerEntity;
            _count[id] = count;
        }

        // ────────────────────────────────────────────── Internals ───────────────────────────────────────────────

        /// <summary>
        /// Apply signed stat deltas through the 2.2a <c>AccumulateBonus</c> seam, EAGERLY recompute the host's
        /// effective stats (so <c>EffectiveMaxHealth</c> is fresh for the clamp and combat at index 4 reads the change
        /// the same tick), then adjust current Health for a max-health change. <b>Decision #3 (Alec), refined in the
        /// 2.2b code review: heal ONLY on a buff's APPLICATION.</b> When a <b>positive</b> <paramref name="maxHealthChange"/>
        /// is <b>applied</b> (<paramref name="isApply"/> = true — a +MaxHealth buff installed/stacked) current Health
        /// rises by the same amount: the buff doubles as a burst heal. Every <b>removal</b> (<paramref name="isApply"/>
        /// = false — buff expiry / dispel) and every <b>debuff</b> (negative change) only ever clamps Health DOWN —
        /// never heals. This kills the earlier symmetric-model exploit where a wearing-off −MaxHealth debuff net-healed
        /// the host (an enemy debuff that grants HP); a debuff round-trip now restores the ceiling without restoring HP.
        /// Health is always re-clamped into <c>[0, EffectiveMaxHealth]</c> (no phantom HP, never a death-on-expiry).
        /// <para><b>DW-491 post-condition.</b> This method can DESTROY <paramref name="id"/> (the DW-325 ceiling-collapse
        /// death) — but only on a genuine downward collapse: a NET-NEGATIVE <paramref name="maxHealthChange"/> that takes
        /// the host's <c>EffectiveMaxHealth</c> from above zero to exactly zero. A positive grant, and any change on a host
        /// whose ceiling was ALREADY zero, are never lethal. Every caller must re-check <c>IsAlive</c> before its next
        /// slot/status write.</para>
        /// <para><b>DW-490 attribution.</b> <paramref name="casterId"/>/<paramref name="casterFaction"/> are the
        /// instance's OWN recorded caster (<c>_casterId</c>/<c>_casterFaction</c> at the slot whose stat change is being
        /// applied or reverted) — the same pair a period pulse from that instance resolves with. They become the killer
        /// attribution of a collapse death, so a creator-authored lethal −MaxHealth debuff credits its real caster for
        /// scoring/hero XP instead of the hardcoded <see cref="Faction.Neutral"/> the DW-325 spec shipped. A store with
        /// no caster context (the external-recompute catch-all) still uses Neutral / attacker −1.</para>
        /// </summary>
        private void ApplyStatDeltas(int id, Fixed attackChange, Fixed maxHealthChange, Fixed moveChange, Fixed armorChange,
                                     bool isApply, int casterId, Faction casterFaction)
        {
            // DW-491: snapshot the PRIOR ceiling before the accumulate/recompute, so the collapse test below can be a
            // downward TRANSITION (>0 → 0) instead of the absolute `== 0` it used to be. This read is the pipeline's own
            // invariant: EntityWorld.Create seeds EffectiveMaxHealth from the ctor health and every store apply/remove
            // recomputes eagerly a few lines down, so the value here is always the host's current ceiling. (DW-492: the
            // SP-load RestoreSlot path is the one producer that accumulates WITHOUT recomputing — deliberately, because a
            // per-slot check would kill a host mid-restore before a later slot's +MaxHealth grant is back. That ring is
            // reconciled by RaiseExternalCeilingCollapse at the first ModifierSystem.Tick after the load, when the whole
            // ring is present; the residual window before that tick can only under-read the ceiling, i.e. fail SAFE.)
            Fixed ceilingBefore = _world.EffectiveMaxHealth[id];

            _system?.AccumulateBonus(id, attackChange, maxHealthChange, moveChange, armorChange);
            _system?.RecomputeEntity(_world, id);

            if (maxHealthChange.Raw != 0)
            {
                // Heal-up ONLY when a positive MaxHealth modifier is APPLIED. A removal or a debuff clamps down only.
                // DW-28: saturate the heal (equivalent to += for all realistic values) so a large/stacked +MaxHealth
                // heal can't wrap Health negative near Fixed.MaxValue → clamp to 0 → a live 0-HP zombie with a non-zero
                // ceiling (the DW-325 kill never fires because EffectiveMaxHealth != 0). No golden moves.
                if (isApply && maxHealthChange > Fixed.Zero) _world.Health[id] = Fixed.AddSaturating(_world.Health[id], maxHealthChange);
                _world.Health[id] = Fixed.Clamp(_world.Health[id], Fixed.Zero, _world.EffectiveMaxHealth[id]);

                // DW-325 (decision 2026-07-30 "Raise death on ceiling==0"): a net-negative-MaxHealth modifier that
                // drives the ceiling to 0 (any ≤0 computed ceiling, floored to 0 by RecomputeEntity's Zero-floor — the
                // `== Fixed.Zero` test below matches the floored result) leaves the host clamped to 0 HP but still
                // alive — a "zombie". Kill it
                // ONCE, through the SINGLE combat death sequence (UnitKilled event + Destroy) so no invented death path
                // exists. The kill fires
                // OnDestroy→ClearEntity, wiping this host's slots + accumulators — every ApplyStatDeltas caller
                // re-checks IsAlive before its next slot/status write.
                //
                // DW-490 — ATTRIBUTION. The DW-325 spec hardcoded `Faction.Neutral` with the DeathFeed argument omitted,
                // on the reasoning that a ceiling collapse is an attacker-less rules death. That holds for a rules-driven
                // collapse, but a creator CAN author a lethal −MaxHealth debuff cast by a real player: the instance
                // already records who cast it, so that kill is no more attacker-less than a DoT tick. The collapse now
                // credits the instance's own caster and pushes the victim into the shared DeathFeed, making it the same
                // shape as EVERY other lethal path (hitscan, projectile, splash, self-lethal cast) — kill/loss counted
                // for the right factions, hostile heroes in range earning the victim's XpBounty. When the caster is
                // genuinely unknown the recorded pair IS `(-1, Faction.Neutral)` — the slot default and what
                // ClearSlotFields writes — so a rules-driven collapse is byte-identical to the pre-DW-490 behaviour
                // apart from the DeathFeed push (XP-bounty policy: a collapse death is worth exactly what any other
                // death of that victim is worth; the bounty is the VICTIM's, so no new policy is invented here).
                //
                // DW-491 — the gate is a COLLAPSE, not an absolute reading. It used to be `IsAlive && ceiling == 0`,
                // which made two non-collapses lethal and contradicted this comment's own "net-negative-MaxHealth" wording:
                //   • a host that legitimately SITS at ceiling 0 (a base-0 / item-sustained unit) was killed by ANY
                //     MaxHealth-touching modifier, because 0 → 0 satisfied the absolute test; and
                //   • even a POSITIVE +MaxHealth grant on such a host was lethal (its ceiling can stay floored at 0 while
                //     a net-negative bonus still dominates), so a heal killed its target.
                // Three conjuncts now, all required: the change is NET-NEGATIVE (a buff/heal is never lethal — which also
                // keeps the DW-488 accumulator-wrap outcome the benign 0-ceiling zombie it was before DW-325, instead of
                // an outright kill), the ceiling WAS above zero, and it is zero NOW. Every real collapse (fresh install,
                // collapsing stack, expiry-driven revert) still satisfies all three.
                //
                // DW-620 — a FOURTH, implicit conjunct now lives inside the primitive: KillEntity refuses the death of
                // an Invulnerable host (decision 2026-08-05, "Invulnerable = death-immunity"). This call site is left
                // deliberately unguarded so the flag check has exactly one home; the collapse still happens (ceiling 0,
                // Health clamped to 0) and the host simply survives it as a death-immune 0-HP unit until the flag drops
                // and a FRESH collapse (or any damage) kills it. Pinned by InvulnerableDeathImmunityTests.
                if (_world.IsAlive(id) && maxHealthChange < Fixed.Zero
                    && ceilingBefore > Fixed.Zero && _world.EffectiveMaxHealth[id] == Fixed.Zero)
                    DamageResolver.KillEntity(_world, id, casterFaction, _events, _stats, _deaths, casterId);
            }
        }

        /// <summary>
        /// DW-492 — the ceiling-collapse CATCH-ALL for every recompute the store did not drive itself.
        ///
        /// <para>The DW-325/DW-491 death lives inside <see cref="ApplyStatDeltas"/>, which is only reached on the
        /// apply / stack / remove paths. Two other producers move <c>EffectiveMaxHealth</c> without it:
        /// <see cref="RestoreSlot"/> (an SP load re-accumulates every saved bonus but deliberately does NOT recompute)
        /// and any bonus dirtied through <c>ModifierSystem.AccumulateBonus</c> from outside the store, both of which are
        /// reconciled by <c>ModifierSystem.Tick</c>'s dirty loop. Without this hook that loop could recompute a live
        /// unit's ceiling down to zero and leave it standing — the exact 0-HP zombie DW-325 advertises as impossible,
        /// and a state divergence between a freshly-applied and a loaded match.</para>
        ///
        /// <para><b>Why here and not per-slot in <see cref="RestoreSlot"/>.</b> A restore rebuilds one ring slot at a
        /// time. Checking after each slot would kill a host whose slot 0 is a −100 MaxHealth debuff before its slot 1
        /// +100 grant is back — destroying a unit the save shows alive. <c>Tick</c> runs after the WHOLE ring (and the
        /// entity overlay) is restored, so it is the first point at which the recomputed ceiling is the host's real one.</para>
        ///
        /// <para><b>Same rule, same shape.</b> Health is re-clamped into <c>[0, EffectiveMaxHealth]</c> whenever the
        /// recompute MOVED the ceiling (so an external drop cannot leave phantom HP above it), and the death fires only
        /// on the DW-491 downward TRANSITION — the ceiling was above zero and is exactly zero now. A host that
        /// legitimately SITS at ceiling 0 across the recompute is untouched, exactly as on the apply path. No caster
        /// exists for an external recompute, so the kill is the attacker-less <see cref="Faction.Neutral"/> / attacker
        /// −1 form — but it still records into the <see cref="DeathFeed"/> like every other death (DW-490).</para>
        ///
        /// <para><b>Golden-neutral by construction.</b> Every store path recomputes EAGERLY and clears the dirty flag,
        /// so <c>ModifierSystem.Tick</c>'s dirty loop body is unreachable in a match that never loads a save and never
        /// accumulates a bonus outside the store — which is every recorded golden. Nothing here can move a checksum.</para>
        /// </summary>
        /// <param name="id">The entity <c>ModifierSystem.Tick</c> just recomputed.</param>
        /// <param name="ceilingBefore">Its <c>EffectiveMaxHealth</c> snapshotted immediately BEFORE that recompute.</param>
        internal void RaiseExternalCeilingCollapse(int id, Fixed ceilingBefore)
        {
            if (!_world.IsAlive(id)) return; // IsAlive also bounds-checks the id
            Fixed ceilingAfter = _world.EffectiveMaxHealth[id];
            if (ceilingAfter == ceilingBefore) return; // the recompute was a no-op for the ceiling — nothing to reconcile

            // Mirrors ApplyStatDeltas: clamp FIRST (so a refused kill — DW-620 invulnerability — still lands the host at
            // 0 HP rather than phantom HP above a zero ceiling), then raise the collapse death.
            _world.Health[id] = Fixed.Clamp(_world.Health[id], Fixed.Zero, ceilingAfter);
            if (ceilingBefore > Fixed.Zero && ceilingAfter == Fixed.Zero)
                DamageResolver.KillEntity(_world, id, Faction.Neutral, _events, _stats, _deaths);
        }

        /// <summary>Recompute and write the host's status-flag union from all active slots EXCEPT <paramref name="excludeSlot"/>.</summary>
        private void RecomputeStatusUnion(int hostId, int excludeSlot)
        {
            StatusFlags union = StatusFlags.None;
            int @base = hostId * EffectCaps.MaxModifiersPerEntity;
            int n = _count[hostId];
            for (int s = 0; s < n; s++)
            {
                int sl = @base + s;
                if (sl == excludeSlot) continue;
                Modifier? m = _modifier[sl];
                if (m != null) union |= m.Status;
            }
            _world.StatusFlagsOf[hostId] = union;
        }

        /// <summary>Swap the last active slot into <paramref name="slot"/> (keeps <c>[0,_count)</c> dense), clear the vacated tail, drop the count.</summary>
        private void CompactSlot(int hostId, int slot)
        {
            int @base = hostId * EffectCaps.MaxModifiersPerEntity;
            int n = _count[hostId];
            int last = @base + (n - 1);
            if (slot != last)
            {
                _modifierId[slot]       = _modifierId[last];
                _remainingTicks[slot]   = _remainingTicks[last];
                _ticksUntilPeriod[slot] = _ticksUntilPeriod[last];
                _periodsRemaining[slot] = _periodsRemaining[last];
                _stackCount[slot]       = _stackCount[last];
                _modifier[slot]         = _modifier[last];
                _persistent[slot]       = _persistent[last];
                _casterId[slot]         = _casterId[last];
                _casterFaction[slot]    = _casterFaction[last];
            }
            ClearSlotFields(last);
            _count[hostId] = n - 1;
        }

        /// <summary>Zero a slot (foldable ints → 0; refs → null). Hygiene — a cleared slot is outside <c>[0,_count)</c> so it is never folded.</summary>
        private void ClearSlotFields(int slot)
        {
            _modifierId[slot]       = 0;
            _remainingTicks[slot]   = 0;
            _ticksUntilPeriod[slot] = 0;
            _periodsRemaining[slot] = 0;
            _stackCount[slot]       = 0;
            _modifier[slot]         = null;
            _persistent[slot]       = null;
            _casterId[slot]         = 0;
            _casterFaction[slot]    = Faction.Neutral;
        }

        /// <summary>
        /// Reset a Modifier slot's period schedule on (re)apply: arm the period timer or clear it for a periodless
        /// modifier. <c>_periodsRemaining</c> is armed to <see cref="EffectCaps.MaxPersistentPeriods"/> — for a Modifier
        /// that is a SCHEDULE WIDTH, not a lifetime: duration alone governs expiry, and <see cref="Advance"/> re-arms
        /// the budget whenever it drains while the modifier is still active (DW-271), so the pulses last exactly as long
        /// as the modifier.
        /// </summary>
        private void ResetPeriodSchedule(int slot, Modifier mod)
        {
            bool hasPeriod = mod.PeriodEffect != null && mod.PeriodTicks > 0;
            _ticksUntilPeriod[slot] = hasPeriod ? mod.PeriodTicks : 0;
            _periodsRemaining[slot] = hasPeriod ? EffectCaps.MaxPersistentPeriods : 0;
        }

        /// <summary>Run an effect node against a fresh, direct-target (<c>spatial: null</c>) context for the host, on the
        /// dedicated executor. Every production path resolves <b>host == target</b>; the explicitly re-targeted form is
        /// <see cref="RunEffectAgainst"/>, which carries the DW-662 fail-closed guard both share.</summary>
        private void RunEffect(int hostId, int slot, EffectNode effect) => RunEffectAgainst(hostId, slot, hostId, effect);

        /// <summary>
        /// DW-272 / Story 15.12 — run ONE period-boundary pulse for <paramref name="slot"/>, honoring the installing
        /// <see cref="Modifier.PeriodicStacking"/> and the slot's stack count, bounded by
        /// <see cref="EffectCaps.MaxPeriodicStackScale"/>:
        /// <list type="bullet">
        /// <item><see cref="PeriodicStackMode.None"/> (the default, every Persistent slot, and any single-stack slot):
        ///   exactly ONE pulse at scale 1 — byte-for-byte with the pre-15.12 pulse.</item>
        /// <item><see cref="PeriodicStackMode.Repeat"/>: the pulse graph runs <c>min(stacks, cap)</c> times (N hits;
        ///   armor subtracted per hit for a <c>damage</c> leaf). Stops the instant a hit kills the host — the
        ///   subsequent runs would self-skip anyway (<see cref="RunEffectAgainst"/> guards a dead host), but returning
        ///   early keeps the DW-267 wiped-ring contract crisp.</item>
        /// <item><see cref="PeriodicStackMode.Multiply"/>: ONE pulse at magnitude × <c>min(stacks, cap)</c> (one big
        ///   hit; armor subtracted once) via <see cref="EffectContext.PulseScale"/>.</item>
        /// </list>
        /// A <see cref="StackRule.StackIndependent"/> instance always has <c>_stackCount == 1</c>, so its scale is 1 and
        /// its <see cref="Modifier.PeriodicStacking"/> is a documented no-op — the pulse count scales via its multiple
        /// same-id slots each pulsing once.
        /// </summary>
        private void RunScaledPulse(int hostId, int slot)
        {
            EffectNode effect = PeriodEffectOf(slot)!;
            PeriodicStackMode mode = _modifier[slot]?.PeriodicStacking ?? PeriodicStackMode.None;

            int stacks = _stackCount[slot];
            int scale = stacks < EffectCaps.MaxPeriodicStackScale ? stacks : EffectCaps.MaxPeriodicStackScale;
            if (scale < 1) scale = 1; // defensive: a well-formed slot always has _stackCount >= 1

            if (mode == PeriodicStackMode.Repeat && scale > 1)
            {
                for (int r = 0; r < scale; r++)
                {
                    RunEffect(hostId, slot, effect);
                    if (!_world.IsAlive(hostId)) return; // a hit killed the host → stop repeating (ring already wiped)
                }
                return;
            }
            if (mode == PeriodicStackMode.Multiply && scale > 1)
            {
                RunEffectAgainst(hostId, slot, hostId, effect, Fixed.FromInt(scale)); // one pulse at magnitude × scale
                return;
            }
            RunEffect(hostId, slot, effect); // None / scale 1 — today's single pulse, byte-for-byte
        }

        /// <summary>
        /// DW-662 — run the instance at <paramref name="slot"/> (owned by <paramref name="hostId"/>) against an EXPLICIT
        /// <paramref name="targetId"/>, on the dedicated executor, and FAIL CLOSED when either end of that pair is
        /// dead/stale.
        ///
        /// <para><b>Why the target needs its own guard.</b> <see cref="Advance"/>'s DW-267 bail
        /// (<c>if (!_world.IsAlive(i)) break;</c>) is a POST-condition and it covers only the HOST — it exists because a
        /// lethal period destroys the host mid-pulse and the walk must not then rewrite/compact a ring
        /// <see cref="ClearEntity"/> has already wiped (the <c>CompactSlot</c>/<c>_count</c> corruption class: an
        /// <c>IndexOutOfRangeException</c> at owner id 0, a <c>_count</c> of −1 at higher ids). Nothing covered the other
        /// end: an instance resolving against an entity that is NOT its host. That is unreachable today — this method is
        /// the ONLY place a store context is built and every production caller passes <c>targetId == hostId</c> with
        /// <c>spatial: null</c>, so a <c>SearchArea</c> inside a period fans out to nobody and every period leaf is
        /// direct-target. The moment a future story threads a real <c>SpatialHash</c> into the store's period executor
        /// (or fans a period out per matched entity through <see cref="RunSlotEffectAgainst"/>), a pulse can resolve
        /// against a corpse. This PRE-condition makes that a deterministic, tallied skip instead of an executor run over
        /// a dead/recycled slot.</para>
        ///
        /// <para><b>Golden-neutral.</b> Both conjuncts are no-ops on every shipped path: the host is alive at every call
        /// site (<see cref="InstallPersistent"/> and <see cref="RemoveByModifierId"/> guard at entry;
        /// <see cref="Advance"/> guards at the top of the owner walk and breaks out of the slot loop the instant a pulse
        /// kills the host), and the target IS the host. <see cref="SkippedPulseCount"/> pins that mechanically — it stays
        /// 0 across every production schedule.</para>
        /// </summary>
        private void RunEffectAgainst(int hostId, int slot, int targetId, EffectNode effect, Fixed pulseScale = default)
        {
            // IsAlive also bounds-checks both ids. ONE tally for both conjuncts — a dead host and a dead target are the
            // same failure to a reader (an instance resolved against something that no longer exists), and splitting it
            // would fold nothing extra. Diagnostics only: never folded, never read by a sim branch.
            if (!_world.IsAlive(hostId) || !_world.IsAlive(targetId)) { _skippedPulses++; return; }

            // DW-272: pulseScale is the Multiply-mode magnitude multiplier (default(Fixed) ⇒ the EffectContext ctor
            // normalizes it to Fixed.One, so every non-Multiply pulse is byte-identical to the pre-15.12 run).
            var ctx = new EffectContext(_world, _casterId[slot], targetId, _casterFaction[slot],
                                        _damageTable, spatial: null, _events, _stats, modifierStore: this,
                                        pulseScale: pulseScale);
            _executor.Run(effect, in ctx);
        }

        /// <summary>
        /// DW-662 — resolve host <paramref name="hostId"/>'s instance at ring slot <paramref name="slotIndex"/> against an
        /// explicit <paramref name="targetId"/>. This is the seam a future <c>SpatialHash</c>-threaded AoE period fans
        /// out through (one call per matched entity, ascending id), and it is where the
        /// <see cref="RunEffectAgainst"/> dead-end guard has its teeth today — no shipped effect graph can reach a
        /// non-host target, so without this entry the guard would be untestable. Deliberately <c>internal</c>: it is not
        /// part of the store's public surface and no production caller uses it yet (the <c>EffectExecutor</c>
        /// frame-capacity ctor is the same pattern).
        /// <para>Fail-closed on an out-of-range host or a slot index outside the per-entity ring — those are programmer
        /// errors, not skips, so they do NOT bump <see cref="SkippedPulseCount"/>. The slot's recorded caster
        /// id/faction (not the target) still drive attribution, exactly as a period pulse does.</para>
        /// </summary>
        internal void RunSlotEffectAgainst(int hostId, int slotIndex, int targetId, EffectNode effect)
        {
            if ((uint)hostId >= (uint)EntityWorld.MAX_ENTITIES) return;
            if ((uint)slotIndex >= (uint)EffectCaps.MaxModifiersPerEntity) return;
            RunEffectAgainst(hostId, hostId * EffectCaps.MaxModifiersPerEntity + slotIndex, targetId, effect);
        }

        private bool HasPeriod(int slot)
        {
            PersistentEffect? pe = _persistent[slot];
            if (pe != null) return pe.PeriodEffect != null && pe.PeriodTicks > 0;
            Modifier? m = _modifier[slot];
            return m != null && m.PeriodEffect != null && m.PeriodTicks > 0;
        }

        private EffectNode? PeriodEffectOf(int slot) =>
            _persistent[slot] != null ? _persistent[slot]!.PeriodEffect : _modifier[slot]?.PeriodEffect;

        private int PeriodLengthOf(int slot) =>
            _persistent[slot] != null ? _persistent[slot]!.PeriodTicks : (_modifier[slot]?.PeriodTicks ?? 0);
    }
}
