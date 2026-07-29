#nullable enable
using ProjectChimera.Combat; // DamageTable / CombatEventQueue / MatchStats (period-effect resolution sinks)
using ProjectChimera.Core;   // EntityWorld, Fixed, Faction

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
    /// <c>UnitDefinition</c> reference).</para>
    ///
    /// <para><b>Re-entrancy.</b> The store runs ALL THREE effect phases — <c>InitialEffect</c> (on install),
    /// <c>PeriodEffect</c> (each pulse), and <c>ExpireEffect</c> (on removal) — on its OWN dedicated
    /// <see cref="EffectExecutor"/>, never shared with a graph-running executor, whose single pre-allocated work-stack
    /// running re-entrantly would clobber. In 2.2b all three phases use only direct-target leaves
    /// (DirectHpDelta/Heal/Damage, <c>spatial: null</c>) so no nesting occurs. An install-leaf
    /// (<see cref="ApplyModifierEffect"/>/<see cref="PersistentEffect"/>) nested inside ANY of the three phases — not
    /// just a period — would re-enter the dedicated executor AND mutate <c>_count</c> mid-<see cref="Advance"/>; that
    /// case is unsupported in 2.2b and is kept off the executor by the Story 2.3 content validator. A future phase that
    /// itself installs a modifier needs a fail-closed re-entrancy guard or a deferred-application queue (documented in
    /// deferred-work, code-review 2.2b W1).</para>
    /// </summary>
    public sealed class ModifierStore
    {
        /// <summary>
        /// <c>_remainingTicks</c> sentinel for a PERMANENT modifier (<c>Modifier.DurationTicks &lt; 0</c>): never
        /// decremented, removed only explicitly or on recycle. A fixed constant so the fold mixes it deterministically.
        /// </summary>
        public const int PERMANENT = int.MinValue;

        // ── Foldable per-instance numeric state (all int, ascending owner-id then slot — the determinism contract) ──
        private readonly int[] _modifierId;        // Modifier.Id (0 for a pure PersistentEffect instance — see Apply scan)
        private readonly int[] _remainingTicks;    // duration countdown (PERMANENT sentinel = never expires by duration)
        private readonly int[] _ticksUntilPeriod;  // ticks until the next DoT/HoT pulse (0 when the instance has no period)
        private readonly int[] _periodsRemaining;  // remaining pulses (Persistent: lifetime; Modifier: a MaxPersistentPeriods cap)
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
        private readonly EffectExecutor _executor;  // DEDICATED — never shared with a graph-running executor

        /// <summary>
        /// Construct the store, wire deps, and subscribe the destroy hook. <paramref name="system"/>/<paramref
        /// name="events"/>/<paramref name="stats"/> are nullable so a cheap FOLD-ONLY store can be built
        /// (<c>new ModifierStore(world)</c>) for a checksum-only call site; the live host wires the full set. The
        /// <paramref name="system"/> ref is required for any real apply/remove (it calls <c>AccumulateBonus</c>); a
        /// fold-only store never applies a modifier. <paramref name="damageTable"/> resolves to
        /// <see cref="DamageTable.Default"/> (mirrors <c>CombatSystem</c>/<c>ProjectileSystem</c>).
        /// </summary>
        public ModifierStore(EntityWorld world, ModifierSystem? system = null, DamageTable? damageTable = null,
                             CombatEventQueue? events = null, MatchStats? stats = null)
        {
            _world = world;
            _system = system;
            _damageTable = damageTable ?? DamageTable.Default;
            _events = events;
            _stats = stats;
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
        /// DETERMINISTICALLY (drops it; never overflows the per-entity ring). Persistent instances carry
        /// <c>_modifier == null</c> so they never match the same-id stacking scan (a <c>Modifier.Id == 0</c> can't
        /// collide with one).
        /// <para><b>Returns</b> <c>true</c> when the modifier was installed OR an existing same-id instance was handled
        /// (Refresh/Stack/Ignore); <c>false</c> when it was REFUSED because the target is dead/stale or the per-entity
        /// ring is full. The return value is not folded into any checksum — every path's behavior/state is unchanged;
        /// callers that ignore the result (the pre-DW-34 default) are byte-identical. The DW-34 pickup site reads it to
        /// deny a ground-item claim when the carrier is at the modifier cap.</para>
        /// </summary>
        public bool Apply(int targetId, Modifier mod, int casterId, Faction casterFaction)
        {
            if (!_world.IsAlive(targetId)) return false; // IsAlive also bounds-checks the id

            int @base = targetId * EffectCaps.MaxModifiersPerEntity;
            int n = _count[targetId];

            int existing = -1;
            for (int s = 0; s < n; s++)
            {
                int sl = @base + s;
                if (_modifier[sl] != null && _modifierId[sl] == mod.Id) { existing = s; break; }
            }

            if (existing < 0)
            {
                if (n >= EffectCaps.MaxModifiersPerEntity) return false; // full → refuse (drop), never overflow
                int slot = @base + n;
                _modifierId[slot]    = mod.Id;
                _modifier[slot]      = mod;
                _persistent[slot]    = null;
                _casterId[slot]      = casterId;
                _casterFaction[slot] = casterFaction;
                _stackCount[slot]    = 1;
                _remainingTicks[slot] = mod.DurationTicks < 0 ? PERMANENT : mod.DurationTicks;
                ResetPeriodSchedule(slot, mod);
                _count[targetId] = n + 1;

                ApplyStatDeltas(targetId, mod.AttackDamageDelta, mod.MaxHealthDelta, mod.MoveSpeedDelta, mod.ArmorDelta, isApply: true);
                _world.StatusFlagsOf[targetId] |= mod.Status;
                return true; // fresh install accepted
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
                        ApplyStatDeltas(targetId, mod.AttackDamageDelta, mod.MaxHealthDelta, mod.MoveSpeedDelta, mod.ArmorDelta, isApply: true); // each stack re-adds
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
            if (n >= EffectCaps.MaxModifiersPerEntity) return; // full → refuse

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
                            RunEffect(i, slot, PeriodEffectOf(slot)!);
                            // Defensive: a future LETHAL period (DamageEffect) could destroy the host mid-pulse →
                            // OnDestroy → ClearEntity already wiped this entity's slots. 2.2b periods are non-lethal
                            // (DirectHpDelta/Heal clamp), so this never fires today; the guard keeps the walk safe.
                            if (!_world.IsAlive(i)) break;
                            _ticksUntilPeriod[slot] = PeriodLengthOf(slot);
                            _periodsRemaining[slot]--;
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
                if (!_world.IsAlive(hostId)) return; // expire-effect killed the host → ClearEntity already wiped slots
            }

            Modifier? mod = _modifier[slot];
            if (mod != null)
            {
                Fixed stacks = Fixed.FromInt(_stackCount[slot]); // exact for an int multiplier (no Fixed rounding)
                ApplyStatDeltas(hostId, -(mod.AttackDamageDelta * stacks),
                                        -(mod.MaxHealthDelta * stacks),
                                        -(mod.MoveSpeedDelta * stacks),
                                        -(mod.ArmorDelta * stacks), isApply: false);
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
            _system?.ClearAll(); // zero the external stat-bonus accumulators + dirty flags (the store's driver half)
        }

        // ─────────────────────────────────────────────── Energy ─────────────────────────────────────────────────

        /// <summary>
        /// Debit a <see cref="Fixed"/> <paramref name="cost"/> from <see cref="EntityWorld.Energy"/> for ability
        /// affordability. Succeeds (and subtracts) ONLY when <c>Energy[id] &gt;= cost</c>; otherwise REFUSES and leaves
        /// <c>Energy</c> untouched (no partial spend, no negative balance). A negative <paramref name="cost"/> is a
        /// programmer error — refused, never refunded. Dead/stale id → false (no throw). The affordability primitive
        /// 2.4's ability-cast consumes; proven in isolation here (no ability exists in 2.2b).
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
        /// </summary>
        private void ApplyStatDeltas(int id, Fixed attackChange, Fixed maxHealthChange, Fixed moveChange, Fixed armorChange, bool isApply)
        {
            _system?.AccumulateBonus(id, attackChange, maxHealthChange, moveChange, armorChange);
            _system?.RecomputeEntity(_world, id);

            if (maxHealthChange.Raw != 0)
            {
                // Heal-up ONLY when a positive MaxHealth modifier is APPLIED. A removal or a debuff clamps down only.
                if (isApply && maxHealthChange > Fixed.Zero) _world.Health[id] += maxHealthChange;
                _world.Health[id] = Fixed.Clamp(_world.Health[id], Fixed.Zero, _world.EffectiveMaxHealth[id]);
            }
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

        /// <summary>Reset a Modifier slot's period schedule on (re)apply: arm the period timer or clear it for a periodless modifier.</summary>
        private void ResetPeriodSchedule(int slot, Modifier mod)
        {
            bool hasPeriod = mod.PeriodEffect != null && mod.PeriodTicks > 0;
            _ticksUntilPeriod[slot] = hasPeriod ? mod.PeriodTicks : 0;
            _periodsRemaining[slot] = hasPeriod ? EffectCaps.MaxPersistentPeriods : 0; // a Modifier's periods are bounded by the cap; duration governs expiry
        }

        /// <summary>Run an effect node against a fresh, direct-target (<c>spatial: null</c>) context for the host, on the dedicated executor.</summary>
        private void RunEffect(int hostId, int slot, EffectNode effect)
        {
            var ctx = new EffectContext(_world, _casterId[slot], hostId, _casterFaction[slot],
                                        _damageTable, spatial: null, _events, _stats, modifierStore: this);
            _executor.Run(effect, in ctx);
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
