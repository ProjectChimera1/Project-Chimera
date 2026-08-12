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
    /// gate, so <see cref="CollectModifierDiagnostics"/> / <see cref="CollectPersistentDiagnostics"/> ride along on the SAME
    /// iterative walk and emit located <see cref="AbilityValidationResult.Warnings"/> instead — <see cref="AbilityValidationResult.Ok"/>
    /// stays true and the token is still minted. Warnings are collected only on the path to a PASS (a rejected graph
    /// was not walked to completion, so its warnings would be arbitrary), and the hard passive rules take precedence:
    /// where <see cref="ValidatePassiveShape"/> already REJECTS a shape (a while_alive Persistent with
    /// <c>period_ticks</c>/<c>period_count</c> ≤ 0, a lifelong with no period, an aura's 0-duration grant) the result
    /// fails and no warning is reported — so the same defect is never double-reported.</para>
    ///
    /// <para><b>DW-504 — the period MISMATCH is a hard reject (owner decision, 2026-08-04).</b> DW-278's remit was to
    /// make the asymmetry visible, not to finish the rule, and it left one class diagnosed-but-shippable: a
    /// <c>period_ticks</c>/<c>period_effect</c> MISMATCH — a declared period that provably cannot pulse, or a
    /// <c>period_ticks</c> with nothing to pulse. A while_alive Persistent has always been REJECTED for it
    /// (<see cref="ValidatePassiveShape"/>); a Modifier anywhere, and a Persistent on an active/aura/on_hit ability,
    /// were only warned, so an author who ignored the warning still shipped an inert period. Those three cases are now
    /// hard rejects everywhere — see <see cref="CollectModifierDiagnostics"/> / <see cref="CollectPersistentDiagnostics"/>,
    /// which route them into a fatal list instead of <c>Warnings</c>. This was taken deliberately while it is FREE: no
    /// shipped ability carries the shape, so nothing in <c>resources/data/abilities</c> moves.
    /// Precedence is unchanged — the promoted rejects are reported only AFTER <see cref="ValidatePassiveShape"/> has had
    /// its say, so a while_alive Persistent still fails with its own activation-specific message and the same defect is
    /// still never double-reported. The remaining warnings (0-duration modifier, non-scaling stacked period, phaseless
    /// Persistent, <c>period_count</c> ≤ 0, ignored <c>lifelong</c>) stay non-fatal.</para>
    ///
    /// <para><b>DW-618 — the POLARITY lint.</b> Every rule above reads one descriptor in isolation. The
    /// self-stunning-Guardian-Aura class is different: each half is individually well-formed and only their
    /// COMBINATION is wrong. <c>aura_guard.json</c> shipped a <c>SearchArea(filter: Ally) → ApplyModifier(+5 armor,
    /// status: Stunned)</c> — a valid filter, a valid modifier — and passed every gate, because nothing cross-checked
    /// a granted modifier's <see cref="StatusFlags"/> against the allegiance its enclosing search selects. DW-266
    /// corrected that one content file; <see cref="CollectPolarityDiagnostics"/> closes the authoring hole that let it
    /// through, warning when an ApplyModifier under a provably friendly-only SearchArea grants a
    /// <see cref="StatusPolarity.Harmful"/> status. WARNING-channel by design (the DW-618 closure note): "buff my
    /// allies and root them in place" is a strange but conceivable design, and the flags are now live, so the cost of
    /// a false reject is higher than the cost of a false warning. The harmful set is NOT re-declared here — it is the
    /// same <see cref="StatusPolarity"/> partition the Story-11.5 buff/debuff icon classifier reads, so the tooltip
    /// that paints a status red and the validator that warns about it can never disagree.</para>
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
            // DW-695: the reserved-device rule reaches the FOURTH authoring surface. DW-454 wired
            // IsReservedDeviceName into the item sim gate, the item editor gate and the unit/building gate, and
            // DW-528 added the filename-level companion for the faction wizard — the ability editor was covered by
            // none of them, and here the case is strictly WORSE than the wizard's: AbilityEditorPanel writes
            // `{SanitizeId(def.Id)}.json`, so there is no `_faction`-style suffix decorating the basename. The
            // reserved word IS the whole basename before the first dot. Homed on the SIM validator (not just the
            // panel) so both the editor Save gate — which is validate-gated and inherits this for free — and the
            // content-load path reject it, mirroring exactly how the item rule is split. Per DW-694 the cost is
            // PORTABILITY of a shared artifact, not a local write failure; the message is single-sourced from the
            // same helper as the other three gates so all four cannot drift.
            if (UnitDefinitionValidator.IsReservedDeviceName(id))
                return Fail(id, "id", UnitDefinitionValidator.ReservedDeviceNameMessage(id));
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

            // ── (a3) Story 15.11 (DW-286): target_affinity is OPTIONAL. Reject an UNKNOWN string fail-closed (a
            //    non-null string that does not parse), mirroring the targeting/activation gates. Absent (null) is valid
            //    and means the historical enemy-only default. The "affinity on Self/None is meaningless" case is a
            //    non-fatal WARNING collected later (it loads and runs; the hint is simply ignored by the picker). ──
            if (def.TargetAffinity is not null && def.ParsedTargetAffinity is null)
                return Fail(id, "target_affinity",
                    $"'{def.TargetAffinity}' is not a known affinity (Enemy|Ally|Any).");

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
            //    on the same walk, the DW-278 non-fatal inert-content warnings PLUS the DW-504 period-mismatch rejects. ──
            var warnings = new List<(string FieldPath, string Message)>();
            var periodShapeErrors = new List<string>();

            // Story 15.11 (DW-286, review P8): target_affinity steers the TargetUnit CLICK-PICKER only. It is ignored on
            // Self/None (no target is picked) AND on GroundPoint (the ground pick's allegiance is governed by the
            // SearchArea Filter, not the picker). Non-fatal (it loads and runs), so warn rather than reject (DW-278 channel).
            if (def.TargetAffinity is not null && def.ParsedTargeting != AbilityTargeting.TargetUnit)
                Warn(warnings, id, "target_affinity",
                    $"target_affinity is only used by the TargetUnit click-picker; it is ignored for a '{def.Targeting}' ability. Remove it, or use targeting TargetUnit.");

            // Story 15.11 (review P3): a GroundPoint cast resolves at the clicked point ONLY through a SearchArea (which
            // centres on the point). Every other leaf reads the primary target, which is absent for a ground cast — so a
            // bare damage/heal/modifier root silently no-ops. Warn when a GroundPoint ability has no top-level SearchArea
            // to consume the point. Non-fatal (the content loads; the author may still intend a pure presentation cast).
            if (def.ParsedTargeting == AbilityTargeting.GroundPoint && !GroundPointResolvesAtPoint(root))
                Warn(warnings, id, "effect",
                    "a GroundPoint ability resolves at the clicked point only through a point-aware node — a SearchArea (which centres on the point), a teleport (which blinks the caster to it), or a presentation cue (which fires at it); its effect root reads the absent primary target, so a bare damage/heal/modifier leaf will no-op. Wrap the effect in a SearchArea.");

            string? walkError = WalkGraph(id, root, warnings, periodShapeErrors);
            if (walkError is not null)
                return AbilityValidationResult.Fail(walkError);

            // ── (g) Passive effect-root shape (Story 2.6 AC5) — the activation dictates the admissible root shape ──
            if (isPassive)
            {
                string? shapeError = ValidatePassiveShape(id, activation, root);
                if (shapeError is not null)
                    return AbilityValidationResult.Fail(shapeError);
            }

            // ── (h) DW-504: the promoted period_ticks/period_effect MISMATCH rejects. Reported LAST so the
            //    activation-specific while_alive messages in (g) keep precedence over the generic ones collected on the
            //    walk — the same defect is reported once, by the most specific rule that owns it. Only the first is
            //    surfaced (the result carries a single located error); walk order is deterministic for a given graph. ──
            if (periodShapeErrors.Count > 0)
                return AbilityValidationResult.Fail(periodShapeErrors[0]);

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
        /// <para>DW-504: the period_ticks/period_effect MISMATCH cases are appended to <paramref name="periodShapeErrors"/>
        /// instead — FATAL, but not returned inline, because the caller reports them only after
        /// <see cref="ValidatePassiveShape"/> so the activation-specific while_alive message keeps precedence.</para>
        /// <para>DW-488: every <c>ApplyModifierEffect</c>'s descriptor is additionally run through
        /// <see cref="Modifier.CheckAuthoringBounds"/> — a FATAL bound on <c>|delta| × max_stacks</c> (and on
        /// <c>max_stacks</c> itself), because a modifier over that bound can wrap <c>ModifierSystem</c>'s int stat
        /// accumulator negative, which DW-28's saturating read cannot recover.</para>
        /// <para>DW-618: the frame additionally carries the innermost enclosing FRIENDLY-ONLY SearchArea (see
        /// <see cref="WalkFrame.FriendlyOnlySearch"/>) so an ApplyModifier leaf knows whose side its grant lands on —
        /// the one piece of context no single descriptor can see, and the reason the self-stunning aura was
        /// unauthorable-to-catch before.</para>
        /// </summary>
        private static string? WalkGraph(string id, EffectNode root, List<(string FieldPath, string Message)> warnings,
                                         List<string> periodShapeErrors)
        {
            var stack = new Stack<WalkFrame>();
            stack.Push(new WalkFrame(root, "effect", searchAreaDepth: 0, inPersistentPhase: false, inPersistentPeriod: false,
                                     friendlyOnlySearch: null));
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
                        // DW-488: fail-closed authoring bound on |delta| x max_stacks (and on max_stacks itself). The
                        // rule lives on Modifier so any future minter can adopt it verbatim; this is its enforcement.
                        if (am.Modifier is not null)
                        {
                            (string Field, string Reason)? overBound = am.Modifier.CheckAuthoringBounds();
                            if (overBound is not null)
                                return Located(id, $"{f.Path}.modifier.{overBound.Value.Field}", overBound.Value.Reason);
                        }
                        // A Modifier.period_effect is store-run per-tick on that same dedicated executor with
                        // spatial:null — structurally identical to a PersistentEffect.period_effect. EffectBounds and
                        // this walk otherwise treat ApplyModifier as a leaf, so descend here (inPersistentPhase +
                        // inPersistentPeriod = true) to extend the AC4 node/SearchArea counts and the AC5
                        // re-entrancy / period-shape rules over the modifier's period subtree.
                        if (am.Modifier?.PeriodEffect is not null)
                            stack.Push(new WalkFrame(am.Modifier.PeriodEffect, $"{f.Path}.modifier.period_effect",
                                f.SearchAreaDepth, inPersistentPhase: true, inPersistentPeriod: true,
                                friendlyOnlySearch: f.FriendlyOnlySearch));
                        // DW-278 warnings + DW-504 period-mismatch rejects for this modifier descriptor.
                        if (am.Modifier is not null)
                        {
                            CollectModifierDiagnostics(id, f.Path, am.Modifier, warnings, periodShapeErrors);
                            // DW-618: the CROSS-descriptor rule — this grant's status polarity vs. the allegiance the
                            // enclosing search selects. Only reachable with a friendly-only SearchArea above it.
                            CollectPolarityDiagnostics(id, f.Path, am.Modifier, f.FriendlyOnlySearch, warnings);
                        }
                        break;

                    case PersistentEffect p:
                        // AC5(a): a nested Persistent inside a Persistent phase is the same re-entrancy hazard.
                        if (f.InPersistentPhase)
                            return Located(id, f.Path,
                                "nested PersistentEffect is not allowed inside a PersistentEffect phase (install re-entrancy).");
                        // Descend each phase: everything below is "in a phase"; the period subtree is also "in a period".
                        if (p.InitialEffect is not null)
                            stack.Push(new WalkFrame(p.InitialEffect, $"{f.Path}.initial_effect", f.SearchAreaDepth, true, false,
                                                     f.FriendlyOnlySearch));
                        if (p.PeriodEffect is not null)
                            stack.Push(new WalkFrame(p.PeriodEffect, $"{f.Path}.period_effect", f.SearchAreaDepth, true, true,
                                                     f.FriendlyOnlySearch));
                        if (p.ExpireEffect is not null)
                            stack.Push(new WalkFrame(p.ExpireEffect, $"{f.Path}.expire_effect", f.SearchAreaDepth, true, false,
                                                     f.FriendlyOnlySearch));
                        // DW-278 warnings + DW-504 period-mismatch rejects for this persistent descriptor.
                        CollectPersistentDiagnostics(id, f.Path, p, warnings, periodShapeErrors);
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
                        // DW-618: the child subtree's allegiance is decided by THIS filter, not by an outer one —
                        // EffectContext.WithTarget re-centres the search on each match but keeps the ORIGINAL
                        // CasterFaction, so a nested SearchArea's Ally/Enemy bits are still resolved against the
                        // caster. Hence OVERRIDE rather than inherit: the innermost enclosing search wins.
                        if (s.Child is not null)
                            stack.Push(new WalkFrame(s.Child, $"{f.Path}.child", sad, f.InPersistentPhase, f.InPersistentPeriod,
                                                     FriendlyOnlyDescription(f.Path, s.Filter)));
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
                                    f.SearchAreaDepth, f.InPersistentPhase, f.InPersistentPeriod, f.FriendlyOnlySearch));
                        break;

                    // Story 15.13 (DW-248): the presentation leaves are counted leaves with no children. Non-fatal
                    // (DW-278 channel): warn when the authored cue is genuinely INERT — a play_sound with no sound id,
                    // or a shake_screen with no shake spec, plays/does nothing. play_vfx is NOT warned: it always
                    // renders at least the bridge's default flash, so it is never inert.
                    case PlaySoundEffect ps:
                        if (string.IsNullOrEmpty(ps.Feedback?.ImpactSoundId))
                            Warn(warnings, id, f.Path,
                                "a play_sound leaf carries no impact_sound id (feedback.impact_sound is null/empty), so it plays nothing — author feedback.impact_sound, or remove the leaf.");
                        break;

                    case ShakeScreenEffect ss:
                        if (ss.Feedback?.Shake is null)
                            Warn(warnings, id, f.Path,
                                "a shake_screen leaf carries no shake spec (feedback.shake is null), so no screen shake occurs — author feedback.shake, or remove the leaf.");
                        break;

                    // DirectHpDelta / Heal / Damage / Teleport / PlayVfx — counted leaves with no children.
                    default:
                        break;
                }
            }

            return null;
        }

        // ── DW-278 warning collectors + the DW-504 period-mismatch rejects that ride the same walk ──

        /// <summary>
        /// The <see cref="Modifier"/> half of the ride-along diagnostics — the authorable-but-inert /
        /// surprising-semantics cases the 2.2b + 2.5b reviews surfaced.
        /// <para><b>DW-504 — FATAL (appended to <paramref name="periodShapeErrors"/>):</b> the two
        /// <c>period_ticks</c>/<c>period_effect</c> MISMATCHES. Both are contradictions the author cannot have meant,
        /// and neither has a runtime reading that does what the JSON says, so they are rejected rather than warned
        /// (matching the while_alive Persistent rule in <see cref="ValidatePassiveShape"/>, which has always rejected
        /// the first of them):</para>
        /// <list type="bullet">
        /// <item>a <c>period_effect</c> with <c>period_ticks &lt;= 0</c> — <c>ModifierStore.HasPeriod</c> is false, so
        ///   the pulse NEVER fires (the modifier still grants its stat deltas: validated, half-dead content, which is
        ///   exactly what makes it invisible).</item>
        /// <item><c>period_ticks &gt; 0</c> with NO <c>period_effect</c> — the period is silently ignored.</item>
        /// </list>
        /// <para><b>DW-278 — non-fatal (appended to <paramref name="warnings"/>):</b> the cases that LOAD, RUN and have
        /// a defensible authored reading, so rejecting them would overreach the gate:</para>
        /// <list type="bullet">
        /// <item><c>duration_ticks: 0</c> — the DW-270 semantics gap. It is a ONE-TICK modifier, not an instantaneous
        ///   one (the store stores 0 verbatim, then <c>Advance</c> takes it to −1 and expires it), so an author who
        ///   wrote 0 meaning "apply once and be done" gets a full tick of stat bonus they did not ask for.</item>
        /// <item>a STACKING periodic modifier — the stat deltas scale per stack (<c>Apply</c> re-adds them) but the
        ///   period fires ONCE per boundary regardless of <c>_stackCount</c>, so a "stacking DoT" does not scale its
        ///   damage. The non-scaling-stacked-DoT footgun.</item>
        /// </list>
        /// (The 2.2b &gt;256-pulse truncation is NOT warned about — DW-271 fixed it in <c>ModifierStore.Advance</c>,
        /// which now re-arms a still-active modifier's pulse budget, so a long periodic modifier is no longer inert.)
        /// </summary>
        private static void CollectModifierDiagnostics(string id, string path, Modifier mod,
                                                       List<(string FieldPath, string Message)> warnings,
                                                       List<string> periodShapeErrors)
        {
            string modPath = $"{path}.modifier";

            if (mod.DurationTicks == 0)
                Warn(warnings, id, $"{modPath}.duration_ticks",
                    "duration_ticks 0 is NOT instantaneous — the modifier installs, holds its stat deltas/status for one full tick, then expires. Use a direct heal/damage/direct_hp_delta leaf for a true one-shot, duration_ticks > 0 for a timed buff, or < 0 for permanent.");

            bool hasPeriodEffect = mod.PeriodEffect is not null;
            // DW-504: promoted from a warning to a hard reject.
            if (hasPeriodEffect && mod.PeriodTicks <= 0)
                Reject(periodShapeErrors, id, $"{modPath}.period_ticks",
                    $"the modifier declares a period_effect but period_ticks={mod.PeriodTicks} (must be > 0), so the periodic pulse never fires — only its stat deltas would apply. Set period_ticks > 0, or remove the period_effect.");
            // DW-504: promoted from a warning to a hard reject.
            if (!hasPeriodEffect && mod.PeriodTicks > 0)
                Reject(periodShapeErrors, id, $"{modPath}.period_ticks",
                    $"period_ticks={mod.PeriodTicks} is ignored — the modifier declares no period_effect to pulse. Add a period_effect, or set period_ticks to 0.");

            // DW-272 / Story 15.12: the period pulse now SCALES with stacks iff the grouped Stack rule holds multiple
            // stacks over a live period AND the author opted into a scaling mode. `scales` is exactly the shape a
            // Multiply/Repeat periodic_stack_mode is meaningful on.
            bool pulsesFire = hasPeriodEffect && mod.PeriodTicks > 0;
            bool scales = mod.Stacking == StackRule.Stack && mod.MaxStacks > 1 && pulsesFire;

            // The non-scaling-stacked-DoT footgun — now ONLY when the author left periodic_stack_mode at None (with
            // Multiply/Repeat the pulse DOES scale, so there is nothing to warn about).
            if (scales && mod.PeriodicStacking == PeriodicStackMode.None)
                Warn(warnings, id, $"{modPath}.period_effect",
                    $"the modifier stacks (stacking Stack, max_stacks={mod.MaxStacks}) but periodic_stack_mode is None, so its period_effect fires ONCE per period regardless of stack count — the stat deltas scale with stacks, the periodic pulse does not. Set periodic_stack_mode to Multiply or Repeat to scale the pulse.");

            // DW-272 — a periodic_stack_mode that can never take effect here (nothing to scale): a non-stacking rule
            // (Refresh/Ignore) or StackIndependent (each stack is its OWN single pulse), max_stacks==1, or no live
            // period. Non-fatal (the DW-278 Warnings channel): it loads and runs, just as None would.
            if (mod.PeriodicStacking != PeriodicStackMode.None && !scales)
                Warn(warnings, id, $"{modPath}.periodic_stack_mode",
                    $"periodic_stack_mode={mod.PeriodicStacking} has no effect here — it scales a stacked pulse only when stacking is Stack with max_stacks>1 and a live period_effect. This modifier {PeriodicModeInertReason(mod, pulsesFire)}, so the pulse behaves as None.");
        }

        /// <summary>DW-272 helper — WHY a periodic_stack_mode is inert on this modifier (for the located warning above).</summary>
        private static string PeriodicModeInertReason(Modifier mod, bool pulsesFire)
        {
            if (!pulsesFire) return "has no live period_effect to pulse";
            if (mod.Stacking == StackRule.StackIndependent)
                return "uses StackIndependent (each stack is its own single pulse, so the pulse count scales via the stacks themselves)";
            if (mod.Stacking != StackRule.Stack) return $"does not stack (stacking {mod.Stacking})";
            return $"holds at most one stack (max_stacks={mod.MaxStacks})";
        }

        /// <summary>
        /// The <see cref="PersistentEffect"/> half. These mirror the hard while_alive rules in
        /// <see cref="ValidatePassiveShape"/>, which is why they exist at all: on an ACTIVE (or aura / on_hit) ability
        /// the same shapes were completely ungated.
        /// <para><b>DW-504 — FATAL (appended to <paramref name="periodShapeErrors"/>):</b></para>
        /// <list type="bullet">
        /// <item>a <c>period_effect</c> with <c>period_ticks &lt;= 0</c> — <c>HasPeriod</c> false ⇒ never pulses, and a
        ///   period-only persistent is then expired on its first <c>Advance</c>. A while_alive passive has always been
        ///   rejected for this; an active-ability Persistent now is too, so the rule no longer depends on activation.</item>
        /// </list>
        /// <para><b>DW-278 — non-fatal (appended to <paramref name="warnings"/>):</b></para>
        /// <list type="bullet">
        /// <item>no phase at all (<c>initial</c>/<c>period</c>/<c>expire</c> all null) — the install is a pure no-op.</item>
        /// <item>a <c>period_effect</c> with <c>period_count &lt;= 0</c> and NOT lifelong — <c>InstallPersistent</c>
        ///   writes <c>_periodsRemaining = 0</c>, so it expires before its first pulse. A <c>lifelong</c> one is exempt:
        ///   its expiry-path re-arm refills the budget on tick 1, so it pulses correctly. (Out of DW-504's scope, which
        ///   named the period_ticks cases only — this one keeps a live non-inert reading via the lifelong exemption.)</item>
        /// <item><c>lifelong</c> with no <c>period_effect</c> — the flag only re-arms a periodic pulse, so it does nothing.</item>
        /// </list>
        /// </summary>
        private static void CollectPersistentDiagnostics(string id, string path, PersistentEffect p,
                                                         List<(string FieldPath, string Message)> warnings,
                                                         List<string> periodShapeErrors)
        {
            if (p.InitialEffect is null && p.PeriodEffect is null && p.ExpireEffect is null)
            {
                Warn(warnings, id, path,
                    "the persistent effect declares no initial_effect, period_effect or expire_effect — installing it does nothing.");
                return; // every check below is about a period this descriptor does not have
            }

            bool hasPeriodEffect = p.PeriodEffect is not null;
            // DW-504: promoted from a warning to a hard reject (the while_alive rule, now activation-independent).
            if (hasPeriodEffect && p.PeriodTicks <= 0)
                Reject(periodShapeErrors, id, $"{path}.period_ticks",
                    $"the persistent effect declares a period_effect but period_ticks={p.PeriodTicks} (must be > 0), so the periodic pulse never fires. Set period_ticks > 0, or remove the period_effect.");
            if (hasPeriodEffect && p.PeriodCount <= 0 && !p.Lifelong)
                Warn(warnings, id, $"{path}.period_count",
                    $"the persistent effect declares a period_effect but period_count={p.PeriodCount} (must be > 0), so it expires before its first pulse.");
            if (!hasPeriodEffect && p.Lifelong)
                Warn(warnings, id, $"{path}.lifelong",
                    "lifelong is ignored — it re-arms the periodic pulse and this persistent effect declares no period_effect.");
        }

        // ── DW-618: the cross-descriptor status-polarity lint ──

        /// <summary>
        /// DW-618 — warn when a modifier granted to FRIENDLY units imposes a
        /// <see cref="StatusPolarity.Harmful"/> status: the self-stunning-Guardian-Aura class.
        /// <para>Every other rule in this validator reads one descriptor alone, and that is exactly why this class
        /// shipped: <c>filter: Ally</c> is a fine filter, <c>status: Stunned</c> is a fine status, and only their
        /// pairing is wrong. <paramref name="friendlyOnlySearch"/> is non-null only when the walk is inside a
        /// SearchArea subtree that provably selects allies and provably excludes enemies (see
        /// <see cref="FriendlyOnlyDescription"/>), so the grant cannot reach a hostile unit — meaning a
        /// capability-removing status on it lands on the caster's own side.</para>
        /// <para>NON-FATAL by the DW-618 closure note. Rejecting would overreach: a creator platform must let someone
        /// author "shield my allies but root them in place", and the status flags only became live recently, so a false
        /// reject would cost more than a false warning. <see cref="StatusPolarity.Beneficial"/> statuses are silent —
        /// an Ally-filtered Invulnerable grant is the point of such an ability, not a bug — which makes this a POLARITY
        /// check rather than a blanket "no status on an ally grant".</para>
        /// </summary>
        private static void CollectPolarityDiagnostics(string id, string path, Modifier mod, string? friendlyOnlySearch,
                                                       List<(string FieldPath, string Message)> warnings)
        {
            if (friendlyOnlySearch is null) return;
            StatusFlags harmful = mod.Status & StatusPolarity.Harmful;
            if (harmful == StatusFlags.None) return;

            Warn(warnings, id, $"{path}.modifier.status",
                $"the modifier imposes the harmful status '{harmful}' but its enclosing {friendlyOnlySearch} selects " +
                "only friendly units, so the debuff lands on the caster's own side (the self-stunning-aura class). " +
                "Grant a beneficial status instead, widen the filter to include Enemy, or drop the status flags.");
        }

        /// <summary>
        /// DW-618 — describe <paramref name="filter"/> for the warning message when it selects FRIENDLIES ONLY,
        /// otherwise null.
        /// <para>The predicate is deliberately narrow: <c>Ally</c> set (so the grant demonstrably reaches the caster's
        /// own faction) AND <c>Enemy</c> clear (so it demonstrably cannot reach a hostile one). Both halves matter —
        /// without the first, <see cref="TargetFilter.None"/> ("every allegiance is eligible") would trip the lint, and
        /// without the second an <c>Ally|Enemy</c> friendly-fire AoE stun — a legitimate design — would.
        /// <see cref="TargetFilter.Self"/> is NOT treated as friendly here: a self-imposed root or silence is a
        /// well-established cost/trade-off (siege-mode immobility, berserk self-silence), so warning about it would be
        /// noise in the one channel whose value depends on staying signal.</para>
        /// </summary>
        private static string? FriendlyOnlyDescription(string searchPath, TargetFilter filter) =>
            (filter & TargetFilter.Ally) != 0 && (filter & TargetFilter.Enemy) == 0
                ? $"SearchArea at '{searchPath}' (filter {filter})"
                : null;

        /// <summary>
        /// Story 15.11 (review P3): does a GroundPoint ability's effect root actually consume the ground point? Only a
        /// <see cref="SearchAreaEffect"/> centres on <c>ctx.TargetPoint</c>; every other node reads the (absent) primary
        /// target. Conservative + low-false-positive: true iff the root IS a SearchArea, or is a Sequence with at least
        /// one direct SearchArea child (the top level, before any per-match <c>WithTarget</c> re-centring). A false here
        /// drives a non-fatal warning, never a reject.
        /// </summary>
        private static bool GroundPointResolvesAtPoint(EffectNode root)
        {
            if (ConsumesTargetPoint(root)) return true;
            if (root is SequenceEffect seq)
                foreach (EffectNode child in seq.Children)
                    if (ConsumesTargetPoint(child)) return true;
            return false;
        }

        /// <summary>
        /// Does this node read <c>ctx.TargetPoint</c> itself? Story 15.11 knew only one such node — SearchArea, which
        /// CENTRES on the clicked point. Story 15.13 (DW-248) added four more, and they are the reason this predicate
        /// exists as a named helper: <c>TeleportEffect</c> blinks the caster TO the point (its FIRST destination branch
        /// is <c>ctx.HasTargetPoint</c>), and the three presentation leaves resolve their event position from the point
        /// before falling back to the target/caster. A GroundPoint ability rooted at any of them consumes the click
        /// correctly and must NOT be warned about — the warning is for leaves that read the PrimaryTargetId a ground
        /// cast deliberately leaves at −1 (damage / heal / apply_modifier).
        /// </summary>
        private static bool ConsumesTargetPoint(EffectNode node)
            => node is SearchAreaEffect
                    or TeleportEffect
                    or PlayVfxEffect
                    or PlaySoundEffect
                    or ShakeScreenEffect;

        /// <summary>Append one located non-fatal warning (same <c>"ability '&lt;id&gt;'.&lt;path&gt;: &lt;reason&gt;"</c> shape as an error).</summary>
        private static void Warn(List<(string FieldPath, string Message)> warnings, string id, string path, string reason) =>
            warnings.Add((path, Located(id, path, reason)));

        /// <summary>DW-504: append one located FATAL period-shape error, deferred so <see cref="ValidatePassiveShape"/>
        /// reports first (the caller surfaces only <c>[0]</c> — the validator's contract is a single located error).</summary>
        private static void Reject(List<string> errors, string id, string path, string reason) =>
            errors.Add(Located(id, path, reason));

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
        /// <para>DW-504: the <c>period_ticks &gt; 0</c> half is no longer while_alive-only — it is now a hard reject for
        /// every Modifier and every Persistent, wherever they appear (see <see cref="CollectModifierDiagnostics"/> /
        /// <see cref="CollectPersistentDiagnostics"/>). This branch is KEPT so a while_alive passive still fails with the
        /// message that names its activation; it runs first, so the generic reject never shadows it.</para>
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

            /// <summary>DW-618: a human-readable description of the INNERMOST enclosing SearchArea when that search
            /// selects friendlies only (<see cref="FriendlyOnlyDescription"/>), else null. Inherited unchanged through
            /// Sequence children / Persistent phases / a modifier's period subtree, and OVERWRITTEN when the walk
            /// enters another SearchArea's child — allegiance is resolved against the original caster at every depth,
            /// so the innermost filter is the one that decides who a leaf below it reaches.</summary>
            public readonly string? FriendlyOnlySearch;

            public WalkFrame(EffectNode node, string path, int searchAreaDepth, bool inPersistentPhase,
                             bool inPersistentPeriod, string? friendlyOnlySearch)
            {
                Node = node;
                Path = path;
                SearchAreaDepth = searchAreaDepth;
                InPersistentPhase = inPersistentPhase;
                InPersistentPeriod = inPersistentPeriod;
                FriendlyOnlySearch = friendlyOnlySearch;
            }
        }
    }
}
