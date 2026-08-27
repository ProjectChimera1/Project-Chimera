#nullable enable
using System.Collections.Generic;      // List (the load/rank-build scratch — never per-tick for an un-ranked unit)
using ProjectChimera.Core;             // EntityWorld, Fixed, ISimSystem
using ProjectChimera.Core.Definitions; // VeterancyDefinition / VeterancyRank
using ProjectChimera.Core.Stats;       // StatId / StatDelta / StatVocabulary
using ProjectChimera.Effects;          // ModifierStore / Modifier / StackRule / StatusFlags

namespace ProjectChimera.Combat
{
    /// <summary>
    /// Story 15-24d — the deterministic VETERANCY runtime: plain units earn rank growth from kills.
    ///
    /// <para>Each tick it sweeps entities in ASCENDING id, and for every alive unit that has (a) a non-zero
    /// <see cref="EntityWorld.VeterancyKills"/> count and (b) an authored <c>veterancy</c> ladder, derives the
    /// CURRENT rank's cumulative stat vector and installs it on ONE swap-on-change modifier slot
    /// (<see cref="VeterancyModifierId"/>) — the <c>HeroXpSystem.ReconcileThresholds</c> /
    /// <c>ResearchSystem.ApplyCumulativeModifier</c> pattern, verbatim.</para>
    ///
    /// <para><b>No new folded state beyond the counter.</b> <c>HeroXpSystem</c> folds
    /// <c>GrowthStacksApplied</c> because its growth is ADDITIVE (desired − applied stacks). Veterancy is
    /// CUMULATIVE PER RANK — the installed vector is a pure function of <see cref="EntityWorld.VeterancyKills"/>
    /// and the definition — so this compares DESIRED against what is actually INSTALLED on the slot and needs no
    /// applied-rank memory. That also makes save/load self-healing: the counter round-trips, the rank re-derives on
    /// the first post-load tick, and no second save lane exists to drift.</para>
    ///
    /// <para><b>Golden-neutral by construction.</b> No shipped unit authors a <c>veterancy</c> block, so
    /// <c>DamageResolver</c> never increments the counter, the <c>VeterancyKills[i] == 0</c> early-out fires for
    /// every entity in every recorded scenario, and this whole Tick is a byte-identical no-op. If a golden moves,
    /// the opt-in gate has leaked — diagnose it, never re-record.</para>
    ///
    /// <para><b>Determinism.</b> Pure C# (no <c>using Godot;</c>, no <c>float</c>, no <c>System.Random</c>,
    /// <see cref="Fixed"/>-only), ascending-id iteration. The authored <c>float</c> map is quantized to
    /// <see cref="Fixed"/> exactly ONCE per stat at this single build boundary, and the walk is over the
    /// <see cref="StatVocabulary"/> REGISTRY (looking each stat's <c>JsonName</c> up in the map) rather than over
    /// the map's own enumeration, so the result cannot depend on authored key order (S-CORE-1 / CHM0002). Each
    /// <see cref="StatId"/> owns exactly one <c>JsonName</c>, so a rank carries at most one term per stat and
    /// there is no multi-term sum to widen into <c>double</c> — see
    /// <see cref="QuantizeDeltas"/>. Growth rides the FOLDED <see cref="ModifierStore"/> — never an unfolded
    /// accumulator — so a rank-up is peer-visible through the already-folded <c>Effective*</c> channels.</para>
    ///
    /// <para><b>Placement (SimulationHost).</b> Registered immediately AFTER <c>HeroXpSystem</c>: the two
    /// progression runtimes tick as neighbours, both consuming the kills the combat cluster just recorded. It runs
    /// strictly BEFORE <c>DeathFeedDrainSystem</c> because <see cref="ModifierStore.RemoveByModifierId"/> can raise
    /// the DW-325 ceiling-collapse death (reverting a <c>+max_health</c> rank), which pushes a
    /// <c>DeathFeed</c> record.</para>
    /// </summary>
    public sealed class VeterancySystem : ISimSystem
    {
        /// <summary>
        /// Reserved <see cref="Modifier.Id"/> for the per-unit VETERANCY modifier — the CUMULATIVE vector of the
        /// unit's current rank. One slot, SWAPPED on a rank change (remove + re-apply, the
        /// <c>ResearchSystem</c> cumulative pattern), because a rank ladder is a step function that cannot be
        /// expressed as N identical stacks and one-modifier-per-rank would exhaust the 8-slot ring.
        /// <para>Follows the ASCII-digit story-number scheme (<c>HeroXpSystem.HeroThresholdModifierId</c> =
        /// <c>0x3135_2400</c> ~ "15 24"): <c>0x3135_2464</c> is "15" "24" "d" (<c>0x64</c> = 'd'), the leg letter.
        /// Collision-free against every other minted id — asserted by <c>VeterancyTests</c>.</para>
        /// </summary>
        public const int VeterancyModifierId = 0x3135_2464; // "31 35 24 64" ~ 15.24d

        private readonly ModifierStore _modifiers;

        /// <summary>
        /// Story 15-24d — how many times this system's rank install has been REFUSED by a full per-entity modifier
        /// ring (<c>EffectCaps.MaxModifiersPerEntity</c>). Pure DIAGNOSTIC: nothing in the sim branches on it, it is
        /// NOT folded into <see cref="SimChecksum"/> and NOT persisted — the
        /// <see cref="ModifierStore.RefusedInstallCount"/> / <see cref="ModifierStore.SkippedPulseCount"/> posture.
        ///
        /// <para>It exists because a refusal is otherwise INVISIBLE: the unit simply never gains its rank, and the
        /// sweep re-attempts (deliberately — a rank must land the moment an unrelated buff expires and frees a
        /// slot), so a permanently-starved veteran looks identical to an un-blooded one. The store's own tally
        /// counts refusals from every minter at once; this one attributes the veterancy share, exactly as DW-83's
        /// <c>refusedBefore</c> delta attributes the research share.</para>
        ///
        /// <para>HOST-lifetime, not per-match: <c>SimulationHost.ClearForReset</c> rebuilds the stores in place but
        /// keeps the system instances, so this accumulates across an Edit&#8596;Play toggle. That is deliberate and
        /// harmless — it is a debugging counter, never a per-match statistic — but do not read it as one.</para>
        /// </summary>
        public int RefusedRankInstalls { get; private set; }

        public VeterancySystem(ModifierStore modifiers) => _modifiers = modifiers;

        /// <inheritdoc/>
        /// <remarks><paramref name="dt"/> is unused — rank is a function of a kill COUNT, not of elapsed time.</remarks>
        public void Tick(EntityWorld world, Fixed dt)
        {
            int cap = world.HighWaterMark;
            for (int i = 0; i < cap; i++)
            {
                if (!world.IsAlive(i)) continue;
                // The golden-neutrality gate, cheapest test first: an un-blooded unit holds no rank, and no shipped
                // unit can ever leave 0 (DamageResolver's opt-in gate). One int compare per entity.
                //
                // NEITHER of these two exits may simply `continue`: both describe a unit that must hold NO rank, and
                // an already-installed veterancy modifier keeps applying its stats until something removes it (a
                // permanent StackRule.Ignore descriptor expires on nothing). Both states are REACHABLE across a
                // save/load, because the minted descriptor round-trips BY VALUE (DW-997's KindMintedModifier) while
                // the two inputs the rank derives from are restored independently: the v13 lane floors a tampered
                // negative count to 0, and the def is re-resolved by id — so a `veterancy` block edited out of the
                // faction file between save and load lands a live rank vector with no ladder behind it. StripOrphan
                // reconciles both to the truth; on the overwhelmingly common path (nothing installed) it costs one
                // array read.
                if (world.VeterancyKills[i] == 0) { StripOrphan(world, i); continue; }
                VeterancyDefinition? vet = world.SourceDefinition[i]?.Veterancy;
                if (vet == null) { StripOrphan(world, i); continue; }
                Reconcile(world, i, vet);
            }
        }

        /// <summary>
        /// Remove an ORPHANED veterancy modifier — one installed on a unit that, as of this tick, must hold no rank
        /// at all (kill count back to 0, or the authored ladder gone). Returns immediately when nothing is
        /// installed, which is every entity of every recorded scenario, so this adds one <c>_count</c> read per
        /// entity per tick and no behaviour.
        ///
        /// <para>Same discipline as <see cref="Reconcile"/>'s swap: Health is snapshotted BEFORE the remove and
        /// restored (re-clamped into the freshly-lowered ceiling) after it, and liveness is re-checked AFTER the
        /// remove because <see cref="ModifierStore.RemoveByModifierId"/> can raise the DW-325 ceiling-collapse
        /// death when it reverts a <c>+max_health</c> rank. The snapshot is not redundant on a lowering remove (the
        /// clamp lands on the same value either way) — it is load-bearing for a rank whose vector was NEGATIVE,
        /// where the remove RAISES the ceiling and an unrestored Health would be a free heal.</para>
        /// </summary>
        private void StripOrphan(EntityWorld world, int entityId)
        {
            if (FindInstalled(entityId) == null) return;   // the hot path: nothing installed, nothing to strip

            Fixed healthBefore = world.Health[entityId];
            _modifiers.RemoveByModifierId(entityId, VeterancyModifierId);
            if (world.IsAlive(entityId))
                world.Health[entityId] = Fixed.Clamp(healthBefore, Fixed.Zero, world.EffectiveMaxHealth[entityId]);
        }

        /// <summary>
        /// Install or SWAP entity <paramref name="entityId"/>'s single veterancy slot so it carries the CUMULATIVE
        /// vector of the rank its kill count has reached.
        ///
        /// <para><b>Idempotent by comparison, not by memory.</b> The desired vector is compared against the vector
        /// actually installed on the slot; when they agree the tick does nothing at all (no remove, no apply, no
        /// Health write) — that is the "never re-applied on a tick where the rank did not change" contract, and it
        /// is what makes a per-tick sweep cost one vector compare instead of a modifier churn.</para>
        ///
        /// <para><b>DW-85 heal suppression.</b> A swap is a RE-STATEMENT of a total, not a grant, so current Health
        /// is snapshotted and restored (re-clamped into the freshly-computed ceiling) around it.
        /// <see cref="ModifierStore"/> heals by the REALIZED ceiling change on any ceiling-raising apply, so without
        /// the snapshot a <c>+max_health</c> rank-up would heal by the whole new rank's ceiling (the remove having
        /// first clamped the unit down) — turning every rank-up into a free full heal, and making a unit that sits
        /// on a threshold boundary a healing exploit.</para>
        ///
        /// <para><b>The remove is lethal.</b> <see cref="ModifierStore.RemoveByModifierId"/> can raise the DW-325
        /// ceiling-collapse death when it reverts a <c>+max_health</c> rank, so liveness is re-checked AFTER the
        /// remove before anything else is written for this host — the ModifierStore post-condition every caller
        /// owes. An all-zero/empty desired vector REMOVES the slot rather than installing an inert one (the DW-678
        /// rule), which is also how a ladder whose top rank nets to zero is handled.</para>
        ///
        /// <para><b>The ring can REFUSE the install.</b> <see cref="ModifierStore.Apply"/> returns <c>false</c> when
        /// the host's <c>EffectCaps.MaxModifiersPerEntity</c> ring is full, and that return is honoured here rather
        /// than discarded: nothing was installed, so the Health re-statement is SKIPPED too (writing it would be
        /// pure noise on a tick where no ring slot moved). The refusal is attributed exactly as
        /// <c>ResearchSystem.ApplyCumulativeModifier</c> attributes its own — through the store's
        /// <see cref="ModifierStore.RefusedInstallCount"/> tally and its DW-83 warn, plus this system's own
        /// <see cref="RefusedRankInstalls"/> so the veterancy share is separable. A refusal can only occur on the
        /// INSTALL path (<paramref name="vet"/>'s first rank, with nothing of ours installed): the SWAP path frees
        /// our own slot before re-applying, so a rank a unit already holds can never be lost to a full ring. The
        /// retry on the next tick is deliberate — a rank must install the moment an unrelated buff expires and frees
        /// a slot, and there is no folded state in which a refusal could be remembered.</para>
        /// </summary>
        private void Reconcile(EntityWorld world, int entityId, VeterancyDefinition vet)
        {
            StatDelta[] desired = BuildRankVector(vet, world.VeterancyKills[entityId]);
            Modifier? installed = FindInstalled(entityId);

            if (installed == null)
            {
                if (desired.Length == 0) return;              // nothing installed, nothing to install (DW-678)
            }
            else if (SameVector(installed.StatDeltas, desired))
            {
                return;                                      // rank unchanged this tick — no churn, no Health write
            }

            Fixed healthBefore = world.Health[entityId];      // DW-85 snapshot (see the remarks)
            // Did the ring actually move this tick? A remove always does; an Apply only when it is not REFUSED.
            bool ringMoved = installed != null;
            if (installed != null) _modifiers.RemoveByModifierId(entityId, VeterancyModifierId);

            if (desired.Length != 0)
            {
                // The remove above can raise the DW-325 ceiling-collapse death (reverting a +max_health rank), so
                // re-check liveness before writing anything further for this host.
                if (!world.IsAlive(entityId)) return;
                var mod = new Modifier(
                    VeterancyModifierId,
                    durationTicks: -1,       // permanent, non-dispellable (the growth-modifier posture)
                    StackRule.Ignore,        // install-once idempotence; the swap path removes first
                    maxStacks: 1,
                    desired,
                    status: StatusFlags.None,
                    periodEffect: null,      // null ⇒ this descriptor round-trips free through DW-997's KindMintedModifier
                    periodTicks: 0);
                // The return is a REFUSAL signal (ring full), not decoration — see the remarks. `|=` because a swap
                // has already moved the ring via its remove, whatever the re-apply then does.
                bool applied = _modifiers.Apply(entityId, mod, entityId, world.FactionOf[entityId]);
                if (!applied) RefusedRankInstalls++;   // the otherwise-invisible outcome, tallied (see the property)
                ringMoved |= applied;
            }

            // Re-state Health only when the ring genuinely moved. On a refused first install nothing changed at all,
            // so this would write the value it just read every tick for as long as the ring stays full.
            if (ringMoved && world.IsAlive(entityId))
                world.Health[entityId] = Fixed.Clamp(healthBefore, Fixed.Zero, world.EffectiveMaxHealth[entityId]);
        }

        /// <summary>
        /// The CUMULATIVE stat vector of the highest rank whose <c>kills</c> threshold <paramref name="kills"/> has
        /// reached — the empty vector when no rank has been reached yet (or the ladder is unauthored). Kills beyond
        /// the last threshold keep counting but resolve to the last rank, so a maxed unit's vector stops moving.
        ///
        /// <para>The ladder is walked ASCENDING and the LAST reached rank wins, so a mis-ordered ladder (which the
        /// validator rejects) degrades to "the last row that matched" rather than throwing inside the tick.</para>
        /// </summary>
        private static StatDelta[] BuildRankVector(VeterancyDefinition vet, int kills)
        {
            List<VeterancyRank>? ranks = vet.Ranks;
            if (ranks == null || ranks.Count == 0) return StatVocabulary.EmptyDeltas;

            VeterancyRank? current = null;
            for (int r = 0; r < ranks.Count; r++)
            {
                VeterancyRank? row = ranks[r];
                if (row == null || row.Kills < 1 || kills < row.Kills) continue;
                current = row;   // ascending ladder ⇒ the last row that matched is the highest reached
            }
            if (current?.StatDeltas == null || current.StatDeltas.Count == 0) return StatVocabulary.EmptyDeltas;

            return QuantizeDeltas(current.StatDeltas);
        }

        /// <summary>
        /// Quantize one rank's authored <c>float</c> map into a canonical sparse <see cref="StatDelta"/> vector —
        /// the SINGLE float→<see cref="Fixed"/> boundary for this feature, exactly ONE quantize per stat.
        ///
        /// <para><b>Registry-driven, never dictionary-driven.</b> The walk is over <see cref="StatVocabulary.All"/>
        /// in ascending <see cref="StatId"/> order, looking each stat's <c>JsonName</c> UP in the authored map
        /// (<c>TryGetValue</c>); the map itself is never enumerated. That makes the result independent of the
        /// authored key order on every peer (the S-CORE-1 / CHM0002 rule) and needs no sort. Because each
        /// <see cref="StatId"/> owns exactly one <c>JsonName</c>, a rank contributes at most ONE term per stat —
        /// so the "accumulate wide, quantize once" rule (<c>AttributeModelDefinition.Resolve</c>) is satisfied here
        /// by there being a single <see cref="Fixed.FromFloat"/> per stat and none per term; there is no multi-term
        /// sum to widen into <c>double</c>.</para>
        ///
        /// <para>Keys outside the closed vocabulary, keys whose stat is not <c>ModifierAuthorable</c> (the 15.12
        /// energy pair's read-seam lane) and non-finite values are DROPPED — <see cref="UnitDefinitionValidator"/>
        /// already fail-closes each of them at authoring time, so this is the runtime's fail-safe, never its
        /// policy. A stat that quantizes to 0 is dropped as well (the DW-678 rule).</para>
        ///
        /// <para>Allocation happens only for an opted-in unit that has actually killed something; the sweep is
        /// bounded by that population, which is 0 in all shipped content.</para>
        /// </summary>
        private static StatDelta[] QuantizeDeltas(Dictionary<string, float> authored)
        {
            var scratch = new List<StatDelta>(4);
            for (int s = 0; s < StatVocabulary.Count; s++)   // ascending StatId — already the canonical order
            {
                StatDefinition def = StatVocabulary.All[s];
                if (!def.ModifierAuthorable) continue;                          // validator rejects; drop
                if (!authored.TryGetValue(def.JsonName, out float v)) continue; // stat not authored at this rank
                if (!float.IsFinite(v)) continue;                               // validator rejects; drop
                Fixed d = Fixed.FromFloat(v);                                   // the ONE quantize for this stat
                if (d.Raw != 0) scratch.Add(new StatDelta(def.Id, d));
            }
            return StatVocabulary.Canonicalize(scratch);
        }

        /// <summary>The <see cref="Modifier"/> currently occupying this host's veterancy slot, or null when none is
        /// installed. Linear over the host's ≤8 live slots — the ring is tiny and this runs only for a unit that
        /// has actually killed something.</summary>
        private Modifier? FindInstalled(int entityId)
        {
            int n = _modifiers.CountAt(entityId);
            for (int s = 0; s < n; s++)
                if (_modifiers.ModifierIdAt(entityId, s) == VeterancyModifierId)
                    return _modifiers.ModifierRefAt(entityId, s);
            return null;
        }

        /// <summary>Exact equality of two CANONICAL sparse vectors (ascending ids, no zero entries, no duplicates —
        /// so a positional walk is a total comparison). Compared on <see cref="Fixed.Raw"/>: an integer test, never
        /// a float epsilon.</summary>
        private static bool SameVector(StatDelta[] a, StatDelta[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i].Stat != b[i].Stat || a[i].Delta.Raw != b[i].Delta.Raw) return false;
            return true;
        }
    }
}
