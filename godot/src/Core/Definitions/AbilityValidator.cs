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
    ///   • The FR-12 model floor — id present, targeting in the closed set, costs/cooldown ≥ 0, ≥ 1 effect node.
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
        /// located error on the FIRST failed check. Pure — never throws, never logs.
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

            // ── (b) Costs + cooldown sign (FixedJsonConverter already rejected NaN/Inf/over-range at parse; this
            //        guards SIGN and the int costs). Fixed sign via .Raw (the underlying 16.16 int). ──
            if (def.CostEnergy.Raw < 0)
                return Fail(id, "cost_energy", $"={def.CostEnergy.ToFloat()} must be >= 0.");
            if (def.CostOre < 0)
                return Fail(id, "cost_ore", $"={def.CostOre} must be >= 0.");
            if (def.CostCrystal < 0)
                return Fail(id, "cost_crystal", $"={def.CostCrystal} must be >= 0.");
            if (def.Cooldown.Raw < 0)
                return Fail(id, "cooldown", $"={def.Cooldown.ToFloat()} must be >= 0.");

            // ── (c) ≥ 1 effect node (AC1's floor) ──
            EffectNode? root = def.EffectGraph;
            if (root is null)
                return Fail(id, "effect", "an ability must declare at least one effect node ('effect' is missing).");

            // ── (d) Structural bounds — reuse the 2.1 gate verbatim (depth ≤ 8, per-Sequence ≤ 8) ──
            EffectBoundsResult bounds = EffectBounds.Validate(root);
            if (!bounds.IsValid)
                return Fail(id, "effect", bounds.Error!);

            // ── (e)+(f) One iterative walk: total-work caps (AC4) + re-entrancy / period-shape (AC5) ──
            string? walkError = WalkGraph(id, root);
            if (walkError is not null)
                return AbilityValidationResult.Fail(walkError);

            // ── Success: mint the proof-of-validation token (the codebase's SECOND `new Validated<`; the sole-minter
            //    source scan allow-lists {ScenarioValidator.cs, AbilityValidator.cs}). ──
            return AbilityValidationResult.Pass(
                new Validated<AbilityDefinition>(def, new ScenarioValidator.Proof()));
        }

        /// <summary>
        /// Iterative graph walk (explicit stack, like <see cref="EffectBounds"/>) enforcing AC4 + AC5. Returns a
        /// located error string, or null when the graph is admissible. Traverses the SAME structure EffectBounds
        /// does (Sequence children, SearchArea child, Persistent initial/period/expire); an ApplyModifier is a
        /// structural leaf (its <c>modifier.period_effect</c> is bounded for nesting by the converter's parse guard
        /// and for breadth by the executor's frame cap — see the story-2.3 deferral note).
        /// </summary>
        private static string? WalkGraph(string id, EffectNode root)
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
                    case ApplyModifierEffect:
                        // AC5(a): an install leaf inside a Persistent phase would re-enter the store's dedicated executor.
                        if (f.InPersistentPhase)
                            return Located(id, f.Path,
                                "ApplyModifierEffect is not allowed inside a PersistentEffect phase (install re-entrancy).");
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
                        break;

                    case SearchAreaEffect s:
                    {
                        int sad = f.SearchAreaDepth + 1;
                        // AC4: bound SearchArea nesting (the worst-case execution-count multiplier).
                        if (sad > EffectCaps.MaxSearchAreaDepth)
                            return Located(id, f.Path,
                                $"SearchArea nesting reaches {sad}, exceeds MaxSearchAreaDepth={EffectCaps.MaxSearchAreaDepth}.");
                        // AC5(b): no SearchArea inside a Persistent period subtree (no per-tick SpatialHash rebuild).
                        if (f.InPersistentPeriod)
                            return Located(id, f.Path,
                                "SearchAreaEffect is not allowed inside a PersistentEffect.period_effect (periods are direct-target only).");
                        if (s.Child is not null)
                            stack.Push(new WalkFrame(s.Child, $"{f.Path}.child", sad, f.InPersistentPhase, f.InPersistentPeriod));
                        break;
                    }

                    case SequenceEffect seq:
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

        // ── Located-error helpers ──

        private static AbilityValidationResult Fail(string id, string path, string reason) =>
            AbilityValidationResult.Fail(Located(id, path, reason));

        private static string Located(string id, string path, string reason) =>
            $"ability '{id}'.{path}: {reason}";

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
