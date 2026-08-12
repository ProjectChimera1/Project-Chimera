#nullable enable
using ProjectChimera.Core;

namespace ProjectChimera.Effects
{
    /// <summary>How a re-applied <see cref="Modifier"/> with the same <see cref="Modifier.Id"/> stacks.</summary>
    public enum StackRule : byte
    {
        /// <summary>Re-applying resets the existing instance's duration; no second stack.</summary>
        Refresh = 0,
        /// <summary>Re-applying adds a stack that SHARES ONE duration with the others (all expire together), up to
        /// <see cref="Modifier.MaxStacks"/>. One ring slot, a <c>_stackCount</c>, re-adding the deltas per stack.</summary>
        Stack = 1,
        /// <summary>Re-applying is ignored while an instance is active.</summary>
        Ignore = 2,
        /// <summary>
        /// DW-264 / Story 15.12 — re-applying adds an INDEPENDENT stack with its OWN duration: each application installs
        /// a fresh ring slot (same <see cref="Modifier.Id"/>, <c>_stackCount=1</c>), up to <see cref="Modifier.MaxStacks"/>
        /// same-id slots AND the per-entity ring capacity. Each slot expires on its own <c>_remainingTicks</c>, reverting
        /// one stack's contribution at a time — the per-stack-expiry model <see cref="Stack"/> (single shared duration)
        /// cannot express. Reuses <c>ModifierStore</c>'s existing per-slot machinery (no new per-stack-timer array); at the
        /// cap a further application is ignored (no refresh).
        /// </summary>
        StackIndependent = 3,
    }

    /// <summary>
    /// DW-272 / Story 15.12 — how a STACKED periodic modifier's pulse scales with its stack count. The default
    /// <see cref="None"/> preserves today's byte-for-byte behavior (the pulse fires once per period regardless of
    /// stacks — only the flat stat deltas scaled with stacks before this story), so no shipped content changes meaning.
    /// The effective scale is always bounded by <see cref="EffectCaps.MaxPeriodicStackScale"/> (runaway protection).
    /// On a <see cref="StackRule.StackIndependent"/> modifier each stack is its own slot pulsing once, so the pulse
    /// count scales naturally and this mode is a documented no-op.
    /// </summary>
    public enum PeriodicStackMode : byte
    {
        /// <summary>Pulse runs ONCE per period regardless of stack count (today's behavior, byte-for-byte).</summary>
        None = 0,
        /// <summary>Pulse runs ONCE per period with its magnitude multiplied by <c>min(stacks, cap)</c> (one big hit;
        /// for a <c>damage</c> leaf, armor is subtracted once).</summary>
        Multiply = 1,
        /// <summary>The pulse graph runs <c>min(stacks, cap)</c> times per period (N smaller hits; for a <c>damage</c>
        /// leaf, armor is subtracted per hit).</summary>
        Repeat = 2,
    }

    /// <summary>
    /// Boolean status effects a <see cref="Modifier"/> may impose. OR-able.
    ///
    /// <para>DW-786 (re-verifying DW-324's doc sweep): this used to call itself a "Reserved set — the systems that
    /// honour each flag land with the ModifierStore (Story 2.2b)". It is no longer reserved and has not been for a
    /// long time: every flag is LIVE and enforced. <c>ModifierStore</c> aggregates the imposed set per entity;
    /// <c>CombatSystem</c> honours Stunned/Disarmed (no acquisition, no swing) and Rooted (no movement),
    /// <c>AbilityCastSystem</c> honours Silenced, <c>DamageResolver</c> honours Invulnerable, and
    /// <c>AbilityValidator</c>'s DW-618 polarity lint classifies them via <c>StatusPolarity</c> below. Treat the set
    /// as CLOSED and append-only — a new member needs an enforcing system, a polarity classification, and the
    /// vocabulary/validator arms that go with it.</para>
    /// </summary>
    [System.Flags]
    public enum StatusFlags : byte
    {
        None = 0,
        Stunned = 1 << 0,
        Rooted = 1 << 1,
        Silenced = 1 << 2,
        Disarmed = 1 << 3,
        Invulnerable = 1 << 4,
    }

    /// <summary>
    /// DW-618 — the CANONICAL buff/debuff polarity of the <see cref="StatusFlags"/> set, hosted here beside the enum
    /// it classifies so every consumer reads the SAME partition.
    ///
    /// <para>The classification itself is not new: Story 11.5's presentation classifier
    /// (<c>ProjectChimera.UI.ModifierPolarity</c>) has always encoded it, and it now reads these constants instead of
    /// its own private copy. The reason it moved is that a SECOND consumer appeared on the far side of the
    /// simulation/presentation boundary — <c>AbilityValidator</c>'s Ally-filtered-harmful-grant lint — and a sim-layer
    /// content gate must not reach into <c>ProjectChimera.UI</c> for a rule. Two hand-kept copies of "which flags are
    /// harmful" would drift the moment a flag is added, and the drift would be silent in both directions (an icon
    /// tinted green for a debuff, or a validator that stops warning about it).</para>
    ///
    /// <para>Deliberately NOT members of <see cref="StatusFlags"/> itself: the content loader parses the authored
    /// <c>status</c> string against that enum, so a combined member would become authorable content
    /// (<c>"status": "Harmful"</c>) and widen the wire vocabulary. Pure compile-time constants — never folded into any
    /// checksum, never a runtime input.</para>
    /// </summary>
    public static class StatusPolarity
    {
        /// <summary>
        /// The statuses that are imposed ON a victim: they take capability away (act / move / cast / attack). A
        /// modifier carrying any of these is a DEBUFF on its host however generous its stat deltas are.
        /// </summary>
        public const StatusFlags Harmful =
            StatusFlags.Stunned | StatusFlags.Rooted | StatusFlags.Silenced | StatusFlags.Disarmed;

        /// <summary>The statuses that are granted TO an ally — the complement of <see cref="Harmful"/> within the
        /// set. <see cref="StatusFlags.Invulnerable"/> is the only one today.</summary>
        public const StatusFlags Beneficial = StatusFlags.Invulnerable;
    }

    /// <summary>
    /// A first-class, timed stat/status descriptor applied to an entity (AR-9). It is NOT an
    /// <see cref="EffectNode"/> — it is the typed payload an <see cref="ApplyModifierEffect"/> leaf installs into
    /// the ModifierStore. Both the type AND its fields are DEFINED in Story 2.1 (so the closed vocabulary is
    /// complete and AC1's "first-class Modifier" holds); its STORE RESOLUTION — registering, stacking, ticking
    /// the period, recomputing effective stats, and expiry — lands in Story 2.2b alongside the ModifierStore.
    ///
    /// All magnitudes are <see cref="Fixed"/> (deterministic). The optional periodic effect re-uses the same
    /// closed Effect-Graph (a <see cref="PersistentEffect"/>-style time axis), so modifiers introduce no new
    /// execution surface.
    /// </summary>
    public sealed class Modifier
    {
        /// <summary>Identity used for stacking — two modifiers stack against each other iff their ids match.</summary>
        public readonly int Id;

        /// <summary>
        /// Lifetime in sim ticks. <c>&gt; 0</c> = that many ticks of effect; <c>&lt; 0</c> = PERMANENT until dispelled
        /// (stored as the <c>ModifierStore.PERMANENT</c> sentinel and never decremented).
        /// <para><b><c>0</c> is a ONE-TICK modifier, NOT an instantaneous one</b> (DW-270 — this doc used to claim
        /// "instantaneous/one-shot", which the store has never implemented). A 0-duration modifier installs exactly
        /// like any other: it takes a slot in the target's ring, its stat deltas and <see cref="Status"/> flags go live
        /// immediately (so combat later in the SAME tick reads them, and a <see cref="PeriodTicks"/> of 1 fires one
        /// pulse), and the next <c>ModifierStore.Advance</c> decrements <c>0 → −1</c> and expires it. So the bonus is
        /// observable for one full sim tick. For a genuinely instantaneous change use a direct leaf
        /// (<c>direct_hp_delta</c> / <c>heal</c> / <c>damage</c>) — a modifier's job is a TIMED effect. Authoring a
        /// <c>duration_ticks: 0</c> raises a non-fatal <c>AbilityValidator</c> warning so the one-tick semantics are
        /// never a silent surprise (DW-278).</para>
        /// </summary>
        public readonly int DurationTicks;

        /// <summary>Behaviour when a same-id modifier is re-applied.</summary>
        public readonly StackRule Stacking;

        /// <summary>Maximum simultaneous stacks when <see cref="Stacking"/> is <see cref="StackRule.Stack"/>.</summary>
        public readonly int MaxStacks;

        // ── Fixed stat deltas (additive; the effective-stat recompute lives in 2.2b) ──
        /// <summary>Flat max-health delta while active.</summary>
        public readonly Fixed MaxHealthDelta;
        /// <summary>Flat attack-damage delta while active.</summary>
        public readonly Fixed AttackDamageDelta;
        /// <summary>Flat move-speed delta while active.</summary>
        public readonly Fixed MoveSpeedDelta;
        /// <summary>Flat armor delta while active (Story 2.6). Folds into <c>EffectiveArmor</c>; reduces incoming
        /// post-matrix damage (<c>DamageResolver</c> subtracts <c>EffectiveArmor</c>, floored at 0).</summary>
        public readonly Fixed ArmorDelta;

        /// <summary>Status flags imposed while active.</summary>
        public readonly StatusFlags Status;

        /// <summary>Optional effect run every <see cref="PeriodTicks"/> ticks (DoT/HoT). Null = no periodic tick.</summary>
        public readonly EffectNode? PeriodEffect;

        /// <summary>Period length in ticks for <see cref="PeriodEffect"/> (0 when there is none).</summary>
        public readonly int PeriodTicks;

        /// <summary>
        /// DW-272 / Story 15.12 — how this modifier's periodic <see cref="PeriodEffect"/> pulse scales with stack count
        /// (<see cref="PeriodicStackMode"/>). Default <see cref="PeriodicStackMode.None"/> = today's non-scaling pulse,
        /// byte-for-byte. A <c>public readonly</c> FIELD (not a property) so it keeps the fold-shaped vocabulary
        /// <c>EffectFoldCompletenessTests</c> pins — it is folded by NAME into <c>CanonicalFold.MixModifier</c> (and thus
        /// <see cref="ProjectChimera.Core.Definitions.CanonicalModelHash"/> / <c>ContentHash</c>), so no piece of semantic
        /// state hides behind a property and escapes the handshake hash.
        /// </summary>
        public readonly PeriodicStackMode PeriodicStacking;

        /// <summary>
        /// DW-678 — true iff installing this descriptor could not change ANY observable state: all four stat deltas are
        /// exactly zero, <see cref="Status"/> is <see cref="StatusFlags.None"/>, and there is no
        /// <see cref="PeriodEffect"/> to pulse. Such an instance still consumes one of the
        /// <see cref="EffectCaps.MaxModifiersPerEntity"/> slots in its host's ring — and does nothing else with it.
        ///
        /// <para>Compared on <see cref="Fixed.Raw"/> so this is an exact integer test, never a float epsilon.
        /// <see cref="DurationTicks"/> is deliberately NOT part of the test — a permanent all-zero modifier and a
        /// one-tick all-zero modifier are equally inert. Neither is <see cref="PeriodTicks"/>: a period with a null
        /// <see cref="PeriodEffect"/> schedules pulses that run nothing (<c>ModifierStore.ResetPeriodSchedule</c>
        /// leaves the slot's period counters at rest), so it is inert too.</para>
        ///
        /// <para><b>A method, not a property</b> — deliberately, and for the same reason
        /// <see cref="CheckAuthoringBounds"/> is: <c>EffectFoldCompletenessTests</c> pins this vocabulary to
        /// public-readonly-FIELD shape so no piece of STATE can hide behind a property and escape
        /// <c>CanonicalFold.MixModifier</c>'s handshake hash. This holds no state at all — it is a pure read of the
        /// fields already folded — so it stays out of that shape on purpose.</para>
        ///
        /// <para><b>A predicate, not a policy.</b> <c>ModifierStore.Apply</c> deliberately does NOT consult this:
        /// content-authored modifiers are observable through channels this predicate cannot see — the Story 11.5 buff
        /// bar renders one row per installed instance, <c>SelectionSubgroupPanel</c> digests the ring by id, a save
        /// round-trips the slot, and an ability may install a zero-stat MARKER purely so a later effect's
        /// <c>RemoveByModifierId</c> has something to find. The one caller is <c>ResearchSystem</c>, whose cumulative
        /// modifier is MINTED, never authored: its entire payload is the four deltas
        /// (<c>BuildCumulativeModifier</c> hard-codes <see cref="StatusFlags.None"/>, a null period effect and period
        /// 0), so for that one minter "all-zero" really does mean "no reason to hold a slot".</para>
        /// </summary>
        public bool HasNoEffect() =>
            MaxHealthDelta.Raw    == 0 &&
            AttackDamageDelta.Raw == 0 &&
            MoveSpeedDelta.Raw    == 0 &&
            ArmorDelta.Raw        == 0 &&
            Status == StatusFlags.None &&
            PeriodEffect == null;

        // ────────────────────── DW-488: content-authoring bounds on a modifier's stat contribution ──────────────────────

        /// <summary>
        /// DW-488 — the per-modifier authoring ceiling on <c>|delta| × MaxStacks</c>, in 16.16 RAW units, for EACH of the
        /// four stat deltas.
        /// <para><c>ModifierSystem.AccumulateBonus</c> sums a host's live modifier contributions with the WRAPPING int
        /// <c>+=</c>; DW-28's <see cref="Fixed.AddSaturating"/> only saturates the later <c>Base + Σbonus</c> READ, and (per
        /// its own docstring) a widen-then-clamp cannot recover a value that has ALREADY wrapped in the int add. Saturating
        /// the accumulator per-step instead would break <c>AccumulateBonus</c>'s order-independence invariant at the
        /// boundary — so the wrap is closed on the AUTHORING side, as DW-28's intent proposed: bound each modifier's
        /// worst-case contribution to <c>int.MaxValue / <see cref="EffectCaps.MaxModifiersPerEntity"/></c>. At most
        /// <see cref="EffectCaps.MaxModifiersPerEntity"/> instances can be live on one host, so the summed magnitude is at
        /// most <c>MaxModifiersPerEntity × this</c> ≤ <c>int.MaxValue</c> — un-wrappable by construction.</para>
        /// <para>≈ 4096.0 in stat units: orders of magnitude above any shipped delta (the largest today is the
        /// <c>ring_of_vigor</c> +50 max health), so this rejects only content that was already pathological. Derived from
        /// named constants, never a bare literal (CHM0004). Deliberately NOT an <see cref="EffectCaps"/> entry: it is a
        /// LOAD-TIME AUTHORING bound, not a runtime execution cap, so it stays out of the <c>RulesetHash</c> wire
        /// fingerprint (adding an EffectCaps entry moves the handshake agreement hash).</para>
        /// </summary>
        public const int MaxStatDeltaTotalRaw = int.MaxValue / EffectCaps.MaxModifiersPerEntity;

        /// <summary>
        /// DW-488 — the authoring ceiling on <see cref="MaxStacks"/> itself, without which the product bound above is
        /// meaningless. <c>ModifierStore.RemoveSlot</c> reverts a slot's contribution as
        /// <c>delta × Fixed.FromInt(stackCount)</c>, and <see cref="Fixed.FromInt"/> shifts left by
        /// <see cref="Fixed.FRACTIONAL_BITS"/> — so a stack count above this limit is not representable in 16.16 and the
        /// revert would subtract the WRONG amount (a permanently-poisoned accumulator). This constant IS that exact
        /// representable limit, derived from <see cref="Fixed.FRACTIONAL_BITS"/>.
        /// </summary>
        public const int MaxAuthorableStacks = int.MaxValue >> Fixed.FRACTIONAL_BITS;

        /// <summary>
        /// DW-488 — the load-time authoring check behind <see cref="MaxStatDeltaTotalRaw"/> /
        /// <see cref="MaxAuthorableStacks"/>. Returns <c>null</c> when this descriptor is within bounds, or the offending
        /// <c>(field, reason)</c> pair for the caller to LOCATE into its own error shape (so the same rule can be adopted
        /// by any validator that mints modifiers, not just <c>AbilityValidator</c>).
        /// <para>Only the two STACKING rules — <see cref="StackRule.Stack"/> (one shared-duration slot,
        /// <c>_stackCount</c> up to <see cref="MaxStacks"/>) and <see cref="StackRule.StackIndependent"/> (up to
        /// <see cref="MaxStacks"/> independent same-id slots) — multiply a modifier's summed contribution;
        /// <see cref="StackRule.Refresh"/> and <see cref="StackRule.Ignore"/> hold exactly one instance whatever
        /// <see cref="MaxStacks"/> says (the store re-adds deltas only on those two branches). Pure, never throws;
        /// allocates a string only on a REJECT.</para>
        /// </summary>
        public (string Field, string Reason)? CheckAuthoringBounds()
        {
            long stacks = 1;
            if (Stacking == StackRule.Stack || Stacking == StackRule.StackIndependent)
            {
                if (MaxStacks < 1)
                    return ("max_stacks",
                        $"={MaxStacks} must be >= 1 when stacking is {Stacking} (a stacking modifier that can hold no stack is inert).");
                if (MaxStacks > MaxAuthorableStacks)
                    return ("max_stacks",
                        $"={MaxStacks} exceeds MaxAuthorableStacks={MaxAuthorableStacks} — expiry reverts the slot as " +
                        $"delta x Fixed.FromInt(stackCount), which is not representable in 16.16 past that.");
                stacks = MaxStacks;
            }

            return CheckDelta("max_health_delta",    MaxHealthDelta,    stacks)
                ?? CheckDelta("attack_damage_delta", AttackDamageDelta, stacks)
                ?? CheckDelta("move_speed_delta",    MoveSpeedDelta,    stacks)
                ?? CheckDelta("armor_delta",         ArmorDelta,        stacks);
        }

        /// <summary>
        /// DW-488 helper: reject one stat delta whose worst-case contribution (<c>|delta| × stacks</c>) would let
        /// <see cref="EffectCaps.MaxModifiersPerEntity"/> such instances wrap <c>ModifierSystem</c>'s int accumulator.
        /// The product is computed in <c>long</c> so the CHECK itself can never overflow.
        /// </summary>
        private static (string Field, string Reason)? CheckDelta(string field, Fixed delta, long stacks)
        {
            long magnitude = delta.Raw < 0 ? -(long)delta.Raw : delta.Raw; // long-widened: |int.MinValue| is representable
            long total = magnitude * stacks;
            if (total > MaxStatDeltaTotalRaw)
                return (field,
                    $"|delta| x max_stacks = {total} raw exceeds MaxStatDeltaTotalRaw={MaxStatDeltaTotalRaw} — " +
                    $"{EffectCaps.MaxModifiersPerEntity} such modifiers on one host would wrap ModifierSystem's int stat accumulator negative.");
            return null;
        }

        /// <summary>Construct a modifier descriptor. Pure data; no execution happens here.
        /// <paramref name="armorDelta"/> is an optional trailing parameter (Story 2.6) so every pre-2.6
        /// <c>new Modifier(...)</c> call site stays source-compatible (defaults to <see cref="Fixed.Zero"/>).
        /// <paramref name="periodicStacking"/> is likewise an optional trailing parameter (DW-272 / Story 15.12) so every
        /// pre-15.12 call site stays source-compatible (defaults to <see cref="PeriodicStackMode.None"/> = today's
        /// non-scaling pulse).</summary>
        public Modifier(int id, int durationTicks, StackRule stacking, int maxStacks,
                        Fixed maxHealthDelta, Fixed attackDamageDelta, Fixed moveSpeedDelta,
                        StatusFlags status, EffectNode? periodEffect, int periodTicks,
                        Fixed armorDelta = default, PeriodicStackMode periodicStacking = PeriodicStackMode.None)
        {
            Id = id;
            DurationTicks = durationTicks;
            Stacking = stacking;
            MaxStacks = maxStacks;
            MaxHealthDelta = maxHealthDelta;
            AttackDamageDelta = attackDamageDelta;
            MoveSpeedDelta = moveSpeedDelta;
            Status = status;
            PeriodEffect = periodEffect;
            PeriodTicks = periodTicks;
            ArmorDelta = armorDelta;
            PeriodicStacking = periodicStacking;
        }
    }
}
