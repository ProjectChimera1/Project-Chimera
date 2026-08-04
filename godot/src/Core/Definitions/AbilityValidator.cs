#nullable enable
using System.Collections.Generic;
using ProjectChimera.Effects; // EffectNode vocabulary + EffectBounds + EffectCaps

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// The fail-closed static content-validator for abilities (Story 2.3, AR-39 / AR-13). Composes the 2.1
    /// <see cref="EffectBounds"/> gate (depth ≤ 8, per-Sequence ≤ 8 — reused verbatim) with three NET-NEW gate
    /// families that discharge the 2.1/2.2b review carve-offs, and on success mints a
    /// <see cref="Validated{T}"/> via the shared <see cref="ScenarioValidator.Proof"/> token. Pure C#: NEVER throws,
    /// NEVER logs; every reject returns a single LOCATED error (<c>"ability '&lt;id&gt;'.&lt;path&gt;: &lt;reason&gt;"</c>).
    ///
    /// The three added families:
    ///   • AC4 total-work caps — <see cref="EffectCaps.MaxTotalEffectNodes"/> + <see cref="EffectCaps.MaxSearchAreaDepth"/>
    ///     (EffectBounds bounds size but not worst-case execution count — the 64⁸ nested-SearchArea hang).
    ///   • AC5 re-entrancy / period-shape — reject an <c>ApplyModifierEffect</c> or nested <c>PersistentEffect</c>
    ///     inside ANY PersistentEffect phase (the store's dedicated executor would re-enter on a nested install), and
    ///     a <c>SearchAreaEffect</c> inside a <c>PersistentEffect.period_effect</c> subtree (periods are
    ///     direct-target only — no per-tick spatial rebuild exists). A TOP-LEVEL ApplyModifier/Persistent is
    ///     ACCEPTED (both execute since 2.2b).
    ///   • The FR-12 model floor — id present, targeting in the closed set, costs/cooldown ≥ 0, ≥ 1 effect node, and
    ///     (DW-284) cooldown ≤ <see cref="AbilityCastSystem.MaxCooldownSeconds"/>, the 16.16 seconds→ticks limit.
    ///
    /// <para><b>DW-278 — the non-fatal WARNING channel.</b> Everything above is fail-closed: a violation rejects the
    /// ability. But the 2.2b/2.5b footgun class is <i>valid, authorable and INERT</i> — content that loads, runs, and
    /// quietly does nothing (or not what the author wrote). Rejecting it would break existing content and overreach the
    /// gate, so <see cref="CollectModifierWarnings"/> / <see cref="CollectPersistentWarnings"/> ride along on the SAME
    /// iterative walk and emit located <see cref="AbilityValidationResult.Warnings"/> instead — <see cref="AbilityValidationResult.Ok"/>
    /// stays true and the token is still minted. Warnings are collected only on the path to a PASS (a rejected graph
    /// was not walked to completion, so its warnings would be arbitrary), and the hard passive rules take precedence:
    /// where <see cref="ValidatePassiveShape"/> already REJECTS a shape (a while_alive Persistent with
    /// <c>period_ticks</c>/<c>period_count</c> ≤ 0, a lifelong with no period, an aura's 0-duration grant) the result
    /// fails and no warning is reported — so the same defect is never double-reported.</para>
    ///
    /// AR-13 ("a random effect requires SimRng") is OWNED here and discharged by RESERVATION: the 2.1 vocabulary has
    /// no random leaf, so a random kind is unauthorable today (rejected as unknown by the converter); the mature
    /// accept-if-present / reject-if-absent enforcement lands with the story that first adds a random leaf.
    /// </summary>
    public sealed class AbilityValidator
    {
        /// <summary>
        /// Validate an <see cref="AbilityDefinition"/>. Returns <see cref="AbilityValidationResult.Pass"/> with a
        /// minted <see cref="Validated{T}"/> on success, or <see cref="AbilityValidationResult.Fail"/> with a single
        /// located error on the FIRST failed check. Pure — never throws, never logs. A PASSING result may still carry
        /// non-fatal <see cref="AbilityValidationResult.Warnings"/> (DW-278) for authorable-but-inert content.
        /// </summary>
        public AbilityValidationResult Validate(AbilityDefinition? def)
        {
            if (def is null) return AbilityValidationResult.Fail("ability is null.");

            string id = def.Id ?? "";

            // ── (a) Identity + targeting ──
            if (string.IsNullOrEmpty(id))
                return AbilityValidationResult.Fail("ability.id is null or empty.");
            if (def.ParsedTargeting is null)
                return Fail(id, "targeting",
                    $"'{def.Targeting}' is not a known targeting type (None|Self|TargetUnit|GroundPoint).");

            // ── (a2) Activation (Story 2.6): the closed passive set, fail-closed on an unknown string ──
            PassiveActivation? parsedActivation = def.ParsedActivation;
            if (parsedActivation is null)
                return Fail(id, "activation",
                    $"'{def.Activation}' is not a known activation (active|aura|on_hit|while_alive).");
            PassiveActivation activation = parsedActivation.Value;
            bool isPassive = activation != PassiveActivation.Active;

            // Passive targeting shape (AC5): aura/on_hit acquire no player target; while_alive is Self/None.
            if (activation is PassiveActivation.Aura or PassiveActivation.OnHit)
            {
                if (def.ParsedTargeting != AbilityTargeting.None)
                    return Fail(id, "targeting",
                        $"a '{def.Activation}' passive must use targeting None (it acquires no player-selected target).");
            }
            else if (activation == PassiveActivation.WhileAlive)
            {
                if (def.ParsedTargeting is not (AbilityTargeting.Self or AbilityTargeting.None))
                    return Fail(id, "targeting", "a 'while_alive' passive must use targeting Self or None.");
            }

            // ── (b) Costs + cooldown sign (FixedJsonConverter already rejected NaN/Inf/over-range at parse; this
            //        guards SIGN and the int costs). Fixed sign via .Raw (the underlying 16.16 int). ──
            if (def.CostEnergy.Raw < 0)
                return Fail(id, "cost_energy", $"raw {def.CostEnergy.Raw} must be >= 0.");
            if (def.CostOre < 0)
                return Fail(id, "cost_ore", $"={def.CostOre} must be >= 0.");
            if (def.CostCrystal < 0)
                return Fail(id, "cost_crystal", $"={def.CostCrystal} must be >= 0.");
            // Story 2.13 (AC5.2): a negative self HP-cost would be a heal — that is the direct_hp_delta/heal primitive's
            // job, not cost_health. Reject it fail-closed (cost_health is a lethal-CAPABLE debit, never a grant).
            if (def.CostHealth < 0)
                return Fail(id, "cost_health", $"={def.CostHealth} must be >= 0 (a negative self-cost is a heal — use direct_hp_delta/heal).");
            if (def.Cooldown.Raw < 0)
                return Fail(id, "cooldown", $"raw {def.Cooldown.Raw} must be >= 0.");
            // DW-284: the MISSING upper bound. `cooldown` was gated only for sign, so an authored value above the
            // 16.16 seconds→ticks conversion limit (AbilityCastSystem.MaxCooldownSeconds ≈ 1092.27 s at 30 tps)
            // shipped as valid content while the runtime conversion wrapped it into a NEGATIVE tick count — which the
            // `> 0` countdown gate reads as "ready", so the longest cooldown an author can write became NO cooldown at
            // all. The bound is REFERENCED from the runtime constant, never hand-copied (the DW-125 lesson), so the
            // gate and the conversion can never drift apart.
            if (def.Cooldown > AbilityCastSystem.MaxCooldownSeconds)
                return Fail(id, "cooldown",
                    $"raw {def.Cooldown.Raw} exceeds the maximum representable cooldown " +
                    $"(raw {AbilityCastSystem.MaxCooldownSeconds.Raw}, ≈ {AbilityCastSystem.MaxCooldownSeconds.ToInt()} seconds " +
                    $"at {SimulationLoop.TICKS_PER_SECOND} ticks/s) — the seconds→ticks conversion cannot carry it.");

            // Decision #4: a passive is never player-cast, so the runtime never debits it — reject any non-zero
            // cost/cooldown fail-closed (rather than silently ignore an authored value that implies a cost gate).
            if (isPassive)
            {
                if (def.CostEnergy.Raw != 0)
                    return Fail(id, "cost_energy", "a passive ability must have zero cost_energy (passives are not cast).");
                if (def.CostOre != 0)
                    return Fail(id, "cost_ore", "a passive ability must have zero cost_ore (passives are not cast).");
                if (def.CostCrystal != 0)
                    return Fail(id, "cost_crystal", "a passive ability must have zero cost_crystal (passives are not cast).");
                if (def.Cooldown.Raw != 0)
                    return Fail(id, "cooldown", "a passive ability has no cooldown (it is not cast).");
            }

            // ── (c) ≥ 1 effect node (AC1's floor) ──
            EffectNode? root = def.EffectGraph;
            if (root is null)
                return Fail(id, "effect", "an ability must declare at least one effect node ('effect' is missing).");

            // ── (d) Structural bounds — reuse the 2.1 gate verbatim (depth ≤ 8, per-Sequence ≤ 8) ──
            EffectBoundsResult bounds = EffectBounds.Validate(root);
            if (!bounds.IsValid)
                return Fail(id, "effect", bounds.Error!);

            // ── (e)+(f) One iterative walk: total-work caps (AC4) + re-entrancy / period-shape (AC5), and riding along
            //    on the same walk, the DW-278 non-fatal inert-content warnings. ──
            var warnings = new List<(string FieldPath, string Message)>();
            string? walkError = WalkGraph(id, root, warnings);
            if (walkError is not null)
                return AbilityValidationResult.Fail(walkError);

            // ── (g) Passive effect-root shape (Story 2.6 AC5) — the activation dictates the admissible root shape ──
            if (isPassive)
            {
                string? shapeError = ValidatePassiveShape(id, activation, root);
                if (shapeError is not null)
                    return AbilityValidationResult.Fail(shapeError);
            }

            // ── Success: mint the proof-of-validation token (the codebase's SECOND `new Validated<`; the sole-minter
            //    source scan allow-lists {ScenarioValidator.cs, AbilityValidator.cs}). Warnings (if any) ride the PASS. ──
            return AbilityValidationResult.Pass(
                new Validated<AbilityDefinition>(def, new ScenarioValidator.Proof()), warnings);
        }

        /// <summary>
        /// Iterative graph walk (explicit stack, like <see cref="EffectBounds"/>) enforcing AC4 + AC5. Returns a
        /// located error string, or null when the graph is admissible. Traverses the SAME structure EffectBounds
        /// does (Sequence children, SearchArea child, Persistent initial/period/expire) PLUS an
        /// <c>ApplyModifierEffect</c>'s <c>modifier.period_effect</c> — which <c>ModifierStore</c> runs per-tick on
        /// its dedicated single-work-stack executor (spatial:null), so it carries the SAME AC4 counts and AC5
        /// re-entrancy/period-shape rules as a Persistent period. EffectBounds treats ApplyModifier as a leaf, so
        /// this walk is the ONLY coverage of that subtree (code-review of story-2.3: the install-re-entrancy fix —
        /// an install-leaf there would re-enter the dedicated executor and clobber its shared work-stack).
        /// <para>DW-278: the walk also appends the non-fatal inert-content warnings for every Modifier / Persistent
        /// descriptor it visits into <paramref name="warnings"/>. The caller DISCARDS them when this returns an error.</para>
        /// </summary>
        private static string? WalkGraph(string id, EffectNode root, List<(string FieldPath, string Message)> warnings)
        {
            var stack = new Stack<WalkFrame>();
            stack.Push(new WalkFrame(root, "effect", searchAreaDepth: 0, inPersistentPhase: false, inPersistentPeriod: false));
            int total = 0;

            while (stack.Count > 0)
            {
                WalkFrame f = stack.Pop();

                total++;
                if (total > EffectCaps.MaxTotalEffectNodes)
                    return Located(id, "effect",
                        $"effect graph node count exceeds MaxTotalEffectNodes={EffectCaps.MaxTotalEffectNodes}.");

                switch (f.Node)
                {
                    case ApplyModifierEffect am:
                        // AC5(a): an install leaf inside a Persistent phase (or another modifier's period — below)
                        // would re-enter the store's dedicated, single-work-stack executor.
                        if (f.InPersistentPhase)
                            return Located(id, f.Path,
                                "ApplyModifierEffect is not allowed inside a PersistentEffect phase (install re-entrancy).");
                        // A Modifier.period_effect is store-run per-tick on that same dedicated executor with
                        // spatial:null — structurally identical to a PersistentEffect.period_effect. EffectBounds and
                        // this walk otherwise treat ApplyModifier as a leaf, so descend here (inPersistentPhase +
                        // inPersistentPeriod = true) to extend the AC4 node/SearchArea counts and the AC5
                        // re-entrancy / period-shape rules over the modifier's period subtree.
                        if (am.Modifier?.PeriodEffect is not null)
                            stack.Push(new WalkFrame(am.Modifier.PeriodEffect, $"{f.Path}.modifier.period_effect",
                                f.SearchAreaDepth, inPersistentPhase: true, inPersistentPeriod: true));
                        // DW-278: non-fatal diagnostics for this modifier descriptor (nothing here rejects).
                        if (am.Modifier is not null) CollectModifierWarnings(id, f.Path, am.Modifier, warnings);
                        break;

                    case PersistentEffect p:
                        // AC5(a): a nested Persistent inside a Persistent phase is the same re-entrancy hazard.
                        if (f.InPersistentPhase)
                            return Located(id, f.Path,
                                "nested PersistentEffect is not allowed inside a PersistentEffect phase (install re-entrancy).");
                        // Descend each phase: everything below is "in a phase"; the period subtree is also "in a period".
                        if (p.InitialEffect is not null)
                            stack.Push(new WalkFrame(p.InitialEffect, $"{f.Path}.initial_effect", f.SearchAreaDepth, true, false));
                        if (p.PeriodEffect is not null)
                            stack.Push(new WalkFrame(p.PeriodEffect, $"{f.Path}.period_effect", f.SearchAreaDepth, true, true));
                        if (p.ExpireEffect is not null)
                            stack.Push(new WalkFrame(p.ExpireEffect, $"{f.Path}.expire_effect", f.SearchAreaDepth, true, false));
                        // DW-278: non-fatal diagnostics for this persistent descriptor (nothing here rejects).
                        CollectPersistentWarnings(id, f.Path, p, warnings);
                        break;

                    case SearchAreaEffect s:
                    {
                        int sad = f.SearchAreaDepth + 1;
                        // AC4: bound SearchArea nesting (the worst-case execution-count multiplier).
                        if (sad > EffectCaps.MaxSearchAreaDepth)
                            return Located(id, f.Path,
                                $"SearchArea nesting reaches {sad}, exceeds MaxSearchAreaDepth={EffectCaps.MaxSearchAreaDepth}.");
                        // AC5(b): no SearchArea inside ANY store-run Persistent phase (initial/period/expire). The
                        // store runs all three with spatial:null, so a SearchArea there silently matches nothing —
                        // reject fail-closed rather than ship validated content that no-ops.
                        if (f.InPersistentPhase)
                            return Located(id, f.Path,
                                "SearchAreaEffect is not allowed inside a PersistentEffect phase (initial/period/expire run direct-target only — no spatial hash).");
                        if (s.Child is not null)
                            stack.Push(new WalkFrame(s.Child, $"{f.Path}.child", sad, f.InPersistentPhase, f.InPersistentPeriod));
                        break;
                    }

                    case SequenceEffect seq:
                        // A 0-child Sequence is a validated no-op ability — reject it (the story testing-discipline
                        // boundary "Sequence 0 children → reject"; EffectBounds only caps the UPPER child count).
                        if (seq.Children.Length == 0)
                            return Located(id, f.Path, "SequenceEffect has 0 children (an effect must do something).");
                        for (int k = 0; k < seq.Children.Length; k++)
                            if (seq.Children[k] is not null)
                                stack.Push(new WalkFrame(seq.Children[k], $"{f.Path}.children[{k}]",
                                    f.SearchAreaDepth, f.InPersistentPhase, f.InPersistentPeriod));
                        break;

                    // DirectHpDelta / Heal / Damage — counted leaves with no children.
                    default:
                        break;
                }
            }

            return null;
        }

        // ── DW-278: non-fatal warning collectors ──

        /// <summary>
        /// DW-278: the <see cref="Modifier"/> half of the non-fatal warning channel — the authorable-but-inert /
        /// surprising-semantics cases the 2.2b + 2.5b reviews surfaced. Every one of these LOADS and RUNS; none is an
        /// error, so each appends a located warning and nothing more:
        /// <list type="bullet">
        /// <item><c>duration_ticks: 0</c> — the DW-270 semantics gap. It is a ONE-TICK modifier, not an instantaneous
        ///   one (the store stores 0 verbatim, then <c>Advance</c> takes it to −1 and expires it), so an author who
        ///   wrote 0 meaning "apply once and be done" gets a full tick of stat bonus they did not ask for.</item>
        /// <item>a <c>period_effect</c> with <c>period_ticks &lt;= 0</c> — <c>ModifierStore.HasPeriod</c> is false, so
        ///   the pulse NEVER fires (the modifier still grants its stat deltas: validated, half-dead content). The
        ///   while_alive Persistent sibling is a hard reject; a modifier's is not, so it needs this.</item>
        /// <item><c>period_ticks &gt; 0</c> with NO <c>period_effect</c> — the period is silently ignored.</item>
        /// <item>a STACKING periodic modifier — the stat deltas scale per stack (<c>Apply</c> re-adds them) but the
        ///   period fires ONCE per boundary regardless of <c>_stackCount</c>, so a "stacking DoT" does not scale its
        ///   damage. The non-scaling-stacked-DoT footgun.</item>
        /// </list>
        /// (The 2.2b &gt;256-pulse truncation is NOT warned about — DW-271 fixed it in <c>ModifierStore.Advance</c>,
        /// which now re-arms a still-active modifier's pulse budget, so a long periodic modifier is no longer inert.)
        /// </summary>
        private static void CollectModifierWarnings(string id, string path, Modifier mod,
                                                    List<(string FieldPath, string Message)> warnings)
        {
            string modPath = $"{path}.modifier";

            if (mod.DurationTicks == 0)
                Warn(warnings, id, $"{modPath}.duration_ticks",
                    "duration_ticks 0 is NOT instantaneous — the modifier installs, holds its stat deltas/status for one full tick, then expires. Use a direct heal/damage/direct_hp_delta leaf for a true one-shot, duration_ticks > 0 for a timed buff, or < 0 for permanent.");

            bool hasPeriodEffect = mod.PeriodEffect is not null;
            if (hasPeriodEffect && mod.PeriodTicks <= 0)
                Warn(warnings, id, $"{modPath}.period_ticks",
                    $"the modifier declares a period_effect but period_ticks={mod.PeriodTicks} (must be > 0), so the periodic pulse never fires — only its stat deltas apply.");
            if (!hasPeriodEffect && mod.PeriodTicks > 0)
                Warn(warnings, id, $"{modPath}.period_ticks",
                    $"period_ticks={mod.PeriodTicks} is ignored — the modifier declares no period_effect to pulse.");

            if (hasPeriodEffect && mod.PeriodTicks > 0 && mod.Stacking == StackRule.Stack && mod.MaxStacks > 1)
                Warn(warnings, id, $"{modPath}.period_effect",
                    $"the modifier stacks (stacking Stack, max_stacks={mod.MaxStacks}) but its period_effect fires ONCE per period regardless of stack count — the stat deltas scale with stacks, the periodic pulse does not.");
        }

        /// <summary>
        /// DW-278: the <see cref="PersistentEffect"/> half of the warning channel. These mirror the hard while_alive
        /// rules in <see cref="ValidatePassiveShape"/>, which is exactly why they are warnings HERE: on an ACTIVE
        /// (or aura / on_hit) ability the same shapes are completely ungated today and silently do nothing.
        /// <list type="bullet">
        /// <item>no phase at all (<c>initial</c>/<c>period</c>/<c>expire</c> all null) — the install is a pure no-op.</item>
        /// <item>a <c>period_effect</c> with <c>period_ticks &lt;= 0</c> — <c>HasPeriod</c> false ⇒ never pulses, and a
        ///   period-only persistent is then expired on its first <c>Advance</c>.</item>
        /// <item>a <c>period_effect</c> with <c>period_count &lt;= 0</c> and NOT lifelong — <c>InstallPersistent</c>
        ///   writes <c>_periodsRemaining = 0</c>, so it expires before its first pulse. A <c>lifelong</c> one is exempt:
        ///   its expiry-path re-arm refills the budget on tick 1, so it pulses correctly.</item>
        /// <item><c>lifelong</c> with no <c>period_effect</c> — the flag only re-arms a periodic pulse, so it does nothing.</item>
        /// </list>
        /// </summary>
        private static void CollectPersistentWarnings(string id, string path, PersistentEffect p,
                                                      List<(string FieldPath, string Message)> warnings)
        {
            if (p.InitialEffect is null && p.PeriodEffect is null && p.ExpireEffect is null)
            {
                Warn(warnings, id, path,
                    "the persistent effect declares no initial_effect, period_effect or expire_effect — installing it does nothing.");
                return; // every check below is about a period this descriptor does not have
            }

            bool hasPeriodEffect = p.PeriodEffect is not null;
            if (hasPeriodEffect && p.PeriodTicks <= 0)
                Warn(warnings, id, $"{path}.period_ticks",
                    $"the persistent effect declares a period_effect but period_ticks={p.PeriodTicks} (must be > 0), so the periodic pulse never fires.");
            if (hasPeriodEffect && p.PeriodCount <= 0 && !p.Lifelong)
                Warn(warnings, id, $"{path}.period_count",
                    $"the persistent effect declares a period_effect but period_count={p.PeriodCount} (must be > 0), so it expires before its first pulse.");
            if (!hasPeriodEffect && p.Lifelong)
                Warn(warnings, id, $"{path}.lifelong",
                    "lifelong is ignored — it re-arms the periodic pulse and this persistent effect declares no period_effect.");
        }

        /// <summary>Append one located non-fatal warning (same <c>"ability '&lt;id&gt;'.&lt;path&gt;: &lt;reason&gt;"</c> shape as an error).</summary>
        private static void Warn(List<(string FieldPath, string Message)> warnings, string id, string path, string reason) =>
            warnings.Add((path, Located(id, path, reason)));

        // ── Located-error helpers ──

        private static AbilityValidationResult Fail(string id, string path, string reason) =>
            AbilityValidationResult.Fail(Located(id, path, reason));

        private static string Located(string id, string path, string reason) =>
            $"ability '{id}'.{path}: {reason}";

        /// <summary>
        /// Story 2.6 AC5 passive effect-root shape rules (only reached for a non-Active activation). The generic
        /// checks (≥ 1 node, EffectBounds, WalkGraph re-entrancy/period-shape) have already passed; this constrains
        /// the ROOT shape to what each passive driver can actually run:
        ///   • aura ⇒ a <c>SearchArea</c> whose <c>Child</c> is an <c>ApplyModifier</c> granting a SHORT, Refresh
        ///     modifier (finite positive <c>duration_ticks</c>, <c>stacking Refresh</c>) — the per-tick re-apply
        ///     contract (Story 2.6 review; the mirror of the while_alive permanence rule).
        ///   • on_hit ⇒ any non-empty rider (already guaranteed by the ≥ 1-node floor) — no further constraint.
        ///   • while_alive ⇒ a PERMANENT <c>ApplyModifier</c> (duration_ticks &lt; 0) OR a <c>Persistent</c> with ≥ 1
        ///     phase, and (when a <c>period_effect</c> is present) both <c>period_ticks &gt; 0</c> AND
        ///     <c>period_count &gt; 0</c> (closes 2.5b deferred #1/#2 + the period_count sibling at the validator).
        /// </summary>
        private static string? ValidatePassiveShape(string id, PassiveActivation activation, EffectNode root)
        {
            switch (activation)
            {
                case PassiveActivation.Aura:
                    if (root is not SearchAreaEffect sa)
                        return Located(id, "effect",
                            "an 'aura' passive's effect root must be a SearchArea (it grants a modifier to units in radius each tick).");
                    if (sa.Child is not ApplyModifierEffect auraGrant)
                        return Located(id, "effect.child",
                            "an 'aura' passive's SearchArea must apply a modifier to each match (SearchArea → ApplyModifier).");
                    // Story 2.6 review: the aura re-applies its modifier EVERY tick (AbilityCastSystem.TickAuras), and
                    // expiry-by-non-refresh — the no-fold design (architecture: "a short Modifier re-applied each tick") —
                    // only holds if that modifier is a SHORT, Refresh grant. A permanent (duration_ticks < 0) grant never
                    // lapses when an ally leaves the radius (breaks AC1's "removes it when they leave"); a 0 grant is a
                    // ONE-TICK modifier (DW-270 — never an instantaneous one), so the buff's presence would hinge on the
                    // re-apply/Advance ordering rather than on the radius. Neither tracks the radius. A Stack rule
                    // escalates the buff every tick. This is the MIRROR of the while_alive ApplyModifier rule below,
                    // which REQUIRES permanence — the aura requires the opposite.
                    if (auraGrant.Modifier is null || auraGrant.Modifier.DurationTicks <= 0)
                        return Located(id, "effect.child",
                            "an 'aura' passive's modifier must have a finite positive duration_ticks (it is re-applied each tick; a permanent grant never lapses when a unit leaves the radius, and a 0 grant lives only the tick it is applied — neither tracks the radius).");
                    if (auraGrant.Modifier.Stacking != StackRule.Refresh)
                        return Located(id, "effect.child",
                            "an 'aura' passive's modifier must use stacking Refresh (the per-tick re-apply refreshes the buff; Stack would escalate it and Ignore would pin the first grant).");
                    return null;

                case PassiveActivation.OnHit:
                    return null; // a non-empty rider graph is sufficient (the ≥ 1-node floor already passed).

                case PassiveActivation.WhileAlive:
                    if (root is ApplyModifierEffect am)
                    {
                        if (am.Modifier is null || am.Modifier.DurationTicks >= 0)
                            return Located(id, "effect",
                                "a 'while_alive' ApplyModifier passive must be permanent (modifier.duration_ticks < 0).");
                        return null;
                    }
                    if (root is PersistentEffect p)
                    {
                        if (p.InitialEffect is null && p.PeriodEffect is null && p.ExpireEffect is null)
                            return Located(id, "effect",
                                "a 'while_alive' Persistent passive must declare at least one phase (initial/period/expire).");
                        if (p.PeriodEffect is not null && p.PeriodTicks <= 0)
                            return Located(id, "effect.period_ticks",
                                "a 'while_alive' Persistent with a period_effect must set period_ticks > 0 (else the period never fires).");
                        // Story 2.6 review: period_count is the sibling of period_ticks — it defaults to 0 in the
                        // converter, and InstallPersistent sets _periodsRemaining = period_count, so a 0 count expires
                        // the Persistent immediately (a validated-but-dead HoT/DoT). Reject it the same way.
                        if (p.PeriodEffect is not null && p.PeriodCount <= 0)
                            return Located(id, "effect.period_count",
                                "a 'while_alive' Persistent with a period_effect must set period_count > 0 (else the period never fires).");
                        // Story 2.13 (AC4.2): the lifelong flag only re-arms a PERIODIC pulse — reject it on a persistent
                        // with no period_effect (nothing to keep alive; the flag would silently do nothing).
                        if (p.Lifelong && p.PeriodEffect is null)
                            return Located(id, "effect.lifelong",
                                "a 'lifelong' Persistent must declare a period_effect (lifelong re-arms the periodic pulse).");
                        return null;
                    }
                    return Located(id, "effect",
                        "a 'while_alive' passive's effect root must be a permanent ApplyModifier or a Persistent.");

                default:
                    return null;
            }
        }

        /// <summary>Per-path walk state: the node, its located path, and the inherited AC4/AC5 context flags.</summary>
        private readonly struct WalkFrame
        {
            public readonly EffectNode Node;
            public readonly string Path;
            public readonly int SearchAreaDepth;     // count of SearchArea ancestors on this path (excl. self)
            public readonly bool InPersistentPhase;  // anywhere under a Persistent initial/period/expire subtree
            public readonly bool InPersistentPeriod; // specifically under a Persistent period_effect subtree

            public WalkFrame(EffectNode node, string path, int searchAreaDepth, bool inPersistentPhase, bool inPersistentPeriod)
            {
                Node = node;
                Path = path;
                SearchAreaDepth = searchAreaDepth;
                InPersistentPhase = inPersistentPhase;
                InPersistentPeriod = inPersistentPeriod;
            }
        }
    }
}
