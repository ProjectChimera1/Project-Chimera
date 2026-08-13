#nullable enable
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// Story 15-21 — the creator-authored HERO ATTRIBUTE MODEL, declared per faction (JSON
    /// <c>attribute_model</c> on the faction root). The attribute SET is data, not an enum: a creator authors
    /// which attributes exist (<see cref="Attributes"/>) and what each contributes to derived stats
    /// (<see cref="Derived"/>); shipped presets (<c>resources/data/attribute-models/*.json</c>) seed the seven
    /// common ARPG models (WC3, PoE, D3, D4, Last Epoch, Grim Dawn, Torchlight) without constraining them.
    ///
    /// <para><b>Dual-path DTO rules</b> (the CombatFeedbackProfile contract): plain <c>float</c>/<c>string</c>/
    /// lists, settable auto-props, no enums, no <see cref="Fixed"/> — loads on the lenient faction path;
    /// strictness lives in <c>FactionValidator</c> (closed stat vocabulary fail-closed, bounds, reference
    /// coherence). Resolution to <see cref="Fixed"/> happens ONCE at the scenario-apply boundary
    /// (<see cref="HeroAttributeResolver"/>), never inside a tick.</para>
    ///
    /// <para><b>Determinism.</b> The model feeds effective stats only through the resolver's flattened
    /// per-hero, per-stat contributions — folded transitively via the ModifierStore (hp/dmg/armor/speed) and
    /// the folded Energy (max_energy/energy_regen). The authored fields fold into <c>ContentHash</c> (v2), so
    /// a lobby content mismatch handshake-rejects.</para>
    /// </summary>
    public sealed class AttributeModelDefinition
    {
        /// <summary>The declared attributes, in authoring order (order is presentation + resolver-deterministic).</summary>
        [JsonPropertyName("attributes")]
        public List<AttributeDeclaration>? Attributes { get; set; }

        /// <summary>The derived-stat mapping rows: each contributes <c>per_point</c> of the named stat per point
        /// of the named attribute (or of the hero's PRIMARY attribute when <c>attribute == "primary"</c> — the
        /// WC3 rule that lets the primary attribute feed attack damage).</summary>
        [JsonPropertyName("derived")]
        public List<DerivedStatRule>? Derived { get; set; }

        /// <summary>Deep copy (Duplicate-path safety — mirrors <see cref="HeroDefinition.Clone"/>).</summary>
        public AttributeModelDefinition Clone()
        {
            var c = new AttributeModelDefinition();
            if (Attributes != null)
            {
                c.Attributes = new List<AttributeDeclaration>(Attributes.Count);
                foreach (var a in Attributes)
                    c.Attributes.Add(new AttributeDeclaration { Id = a.Id, Name = a.Name, Description = a.Description });
            }
            if (Derived != null)
            {
                c.Derived = new List<DerivedStatRule>(Derived.Count);
                foreach (var d in Derived)
                    // EVERY field, or the editor's Duplicate path silently drops it (the Story 4.5 class).
                    c.Derived.Add(new DerivedStatRule
                    {
                        Attribute = d.Attribute, Stat = d.Stat, PerPoint = d.PerPoint,
                        Shape = d.Shape, Threshold = d.Threshold, // Story 15-24c
                    });
            }
            return c;
        }
    }

    /// <summary>One declared attribute (id is the reference key; name/description are presentation).</summary>
    public sealed class AttributeDeclaration
    {
        [JsonPropertyName("id")]          public string? Id { get; set; }
        [JsonPropertyName("name")]        public string? Name { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
    }

    /// <summary>
    /// Story 15-24c — the derivation SHAPE of one row. Deliberately a small closed vocabulary rather than a
    /// bool: the Q3b ruling requires rows to stay "a graph-friendly data shape — a row is a degenerate
    /// one-node graph … thresholds become trigger-like nodes", so the kind names the NODE the effect-graph
    /// composer will grow into (source → [condition] → contribution leaf).
    /// </summary>
    public enum DerivationShape
    {
        /// <summary>The 15-21 shape (the JSON default, so every pre-15-24c row keeps its meaning byte-for-byte):
        /// <c>stat += per_point × attributeValue</c> — linear, no condition node.</summary>
        Linear = 0,
        /// <summary>"every N points of X → +V of S": <c>stat += per_point × floor(attributeValue / step)</c>.
        /// A STEP function of the attribute total — the shape that cannot ride the 15-21 linear flatten.</summary>
        PerStep = 1,
        /// <summary>"at ≥ N points of X → +V of S": <c>stat += per_point</c> once, iff
        /// <c>attributeValue ≥ threshold</c>. A gate node with a single contribution leaf.</summary>
        AtLeast = 2,
    }

    /// <summary>
    /// One derived-stat contribution row. The 15-21 default is <c>stat += per_point × attribute value</c>;
    /// Story 15-24c adds the two THRESHOLD shapes (<see cref="DerivationShape"/>), which are step functions of
    /// the attribute total and are therefore evaluated at RESOLVE time against a concrete attribute snapshot
    /// (<see cref="HeroAttributeResolver.EvaluateAt"/>) rather than flattened into the linear (base, perLevel)
    /// pair — see that method's remarks for the arithmetic reason.
    /// </summary>
    public sealed class DerivedStatRule
    {
        /// <summary>A declared attribute id, or the reserved selector <c>"primary"</c> (the hero's flagged
        /// primary attribute — the WC3 primary-feeds-attack-damage lineage).</summary>
        [JsonPropertyName("attribute")] public string? Attribute { get; set; }

        /// <summary>A member of the CLOSED derived-stat vocabulary (<see cref="AttributeStats"/>). Fail-closed
        /// at validation — never an open reflection over effective-stat fields.</summary>
        [JsonPropertyName("stat")] public string? Stat { get; set; }

        /// <summary>Contribution per attribute point (<see cref="DerivationShape.Linear"/>), per completed STEP
        /// (<see cref="DerivationShape.PerStep"/>), or the one-shot grant (<see cref="DerivationShape.AtLeast"/>).
        /// Authoring float; quantized once at the resolve boundary.</summary>
        [JsonPropertyName("per_point")] public float PerPoint { get; set; }

        /// <summary>Story 15-24c — the row's derivation shape. Omitted ⇒ <see cref="DerivationShape.Linear"/>,
        /// so every pre-15-24c authored row is unchanged (and the writer omits the key at the default, keeping
        /// existing faction/preset JSON byte-stable).</summary>
        [JsonPropertyName("shape")] public string? Shape { get; set; }

        /// <summary>Story 15-24c — the step size (<see cref="DerivationShape.PerStep"/>: "every N points") or
        /// the gate (<see cref="DerivationShape.AtLeast"/>: "at ≥ N points"). NULLABLE (the <c>xp_bounty</c>
        /// precedent) so an absent threshold is distinguishable from an authored 0 — which is invalid for a step
        /// row — AND so the writer's omit-when-null discipline leaves every pre-15-24c row byte-identical
        /// (a non-nullable float would serialize <c>"threshold": 0</c> into every row of every shipped preset).
        /// Ignored by <see cref="DerivationShape.Linear"/> rows; validator-required (&gt; 0) for the other two.</summary>
        [JsonPropertyName("threshold")] public float? Threshold { get; set; }

        /// <summary>The parsed <see cref="Shape"/> — fail-OPEN to <see cref="DerivationShape.Linear"/> exactly
        /// like <c>UnitDefinition</c>'s other Parsed* accessors (the validator is the fail-CLOSED gate, so a
        /// bad token is rejected at load and never reaches the resolver).</summary>
        [JsonIgnore]
        public DerivationShape ParsedShape =>
            string.Equals(Shape, "per_step", System.StringComparison.OrdinalIgnoreCase) ? DerivationShape.PerStep
            : string.Equals(Shape, "at_least", System.StringComparison.OrdinalIgnoreCase) ? DerivationShape.AtLeast
            : DerivationShape.Linear;

        /// <summary>True for a row whose contribution is a STEP function of the attribute total — the rows the
        /// linear flatten cannot represent and <see cref="HeroAttributeResolver.EvaluateAt"/> owns.</summary>
        [JsonIgnore]
        public bool IsThreshold => ParsedShape != DerivationShape.Linear;
    }

    /// <summary>
    /// Story 15-21 — the per-hero attribute block (JSON <c>attributes</c> on <see cref="HeroDefinition"/>).
    /// Values are AUTHORED auto-growth (the WC3 model, decision D-2): a hero's live attribute value is
    /// <c>base + per_level × (Level − 1)</c> — a pure function of the folded hero Level, so the attribute
    /// table itself needs no folded state.
    /// </summary>
    public sealed class HeroAttributesDefinition
    {
        /// <summary>The hero's PRIMARY attribute id (must be a declared attribute). Null = no primary —
        /// <c>"primary"</c>-selector derived rules then contribute nothing for this hero.</summary>
        [JsonPropertyName("primary")] public string? Primary { get; set; }

        /// <summary>Base value per declared attribute id (level 1).</summary>
        [JsonPropertyName("base")] public Dictionary<string, float>? Base { get; set; }

        /// <summary>Per-level gain per declared attribute id (applied automatically on level-up — D-2).</summary>
        [JsonPropertyName("per_level")] public Dictionary<string, float>? PerLevel { get; set; }

        /// <summary>Deep copy (Duplicate-path safety — the <see cref="HeroDefinition.Clone"/> contract).</summary>
        public HeroAttributesDefinition Clone() => new HeroAttributesDefinition
        {
            Primary  = Primary,
            Base     = Base     == null ? null : new Dictionary<string, float>(Base),
            PerLevel = PerLevel == null ? null : new Dictionary<string, float>(PerLevel),
        };
    }

    /// <summary>
    /// Story 15-21's closed derived-stat vocabulary, since 15-24a a FACADE over the
    /// <see cref="ProjectChimera.Core.Stats.StatVocabulary"/> registry — the attribute lane's index space IS
    /// the <see cref="ProjectChimera.Core.Stats.StatId"/> space (the registry's first six members pin the
    /// 15-21 order exactly, guard-tested), so growing the registry grows this vocabulary with NO further
    /// change here: <see cref="Count"/> sizes every stride-<c>Count</c> contribution array (HeroStore lanes,
    /// resolver output — the save's v10 bump covers the re-stride), <see cref="Ids"/> feeds the mapping
    /// editor's dropdown and validator messages (attribute-TARGETABLE stats only, in StatId order), and
    /// <see cref="TryIndexOf"/> stays the single string→index mapping (fail-closed outside the targetable
    /// set). The six legacy constants keep their pre-15-24a names for the readers the compiler can find
    /// (HeroXpSystem's channel split, the energy pair's runtime reads).
    /// </summary>
    public static class AttributeStats
    {
        public const int MaxHealth    = (int)ProjectChimera.Core.Stats.StatId.MaxHealth;    // → hero-growth modifier channel
        public const int AttackDamage = (int)ProjectChimera.Core.Stats.StatId.AttackDamage; // → the WC3 primary rule's usual target
        public const int Armor        = (int)ProjectChimera.Core.Stats.StatId.Armor;
        public const int MoveSpeed    = (int)ProjectChimera.Core.Stats.StatId.MoveSpeed;
        public const int MaxEnergy    = (int)ProjectChimera.Core.Stats.StatId.MaxEnergy;    // → EnergyRegenSystem clamp ceiling
        public const int EnergyRegen  = (int)ProjectChimera.Core.Stats.StatId.EnergyRegen;  // → EnergyRegenSystem.RegenPerTick (15.12 seam)

        /// <summary>The attribute index space's size == the registry's (one shared index space; 15-24a).</summary>
        public static int Count => ProjectChimera.Core.Stats.StatVocabulary.Count;

        /// <summary>The attribute-TARGETABLE vocabulary in StatId order (registry-derived; feeds the 15-21
        /// mapping editor's dropdown and validator messages). Built once — the registry is compile-time data.</summary>
        public static readonly string[] Ids = BuildTargetableIds();

        private static string[] BuildTargetableIds()
        {
            var all = ProjectChimera.Core.Stats.StatVocabulary.All;
            int n = 0;
            for (int i = 0; i < all.Length; i++) if (all[i].AttributeTargetable) n++;
            var ids = new string[n];
            int w = 0;
            for (int i = 0; i < all.Length; i++) if (all[i].AttributeTargetable) ids[w++] = all[i].JsonName;
            return ids;
        }

        /// <summary>The single string→index mapping (registry-backed; the index IS the (int)StatId). False for
        /// anything outside the closed set OR a declared-but-not-attribute-targetable stat — the validator
        /// fail-closes on it, so the resolver never sees an unknown stat.</summary>
        public static bool TryIndexOf(string? stat, out int index)
        {
            if (ProjectChimera.Core.Stats.StatVocabulary.TryByJsonName(stat, out var def) && def.AttributeTargetable)
            {
                index = (int)def.Id;
                return true;
            }
            index = -1;
            return false;
        }
    }

    /// <summary>
    /// Story 15-21 — the SINGLE float→<see cref="Fixed"/> resolve boundary for hero attributes: flattens a
    /// faction's <see cref="AttributeModelDefinition"/> × a hero's <see cref="HeroAttributesDefinition"/> into
    /// per-stat (base, per-level) contribution pairs, in the <see cref="AttributeStats"/> index order. Called
    /// once per placed hero at scenario apply (never inside a tick). Deterministic: iterates the DECLARED rule
    /// list in authoring order and reads the hero dictionaries by key only (no dictionary enumeration).
    /// </summary>
    public static class HeroAttributeResolver
    {
        /// <summary>
        /// The saturation bound <see cref="EvaluateAt"/> clamps to before quantizing: the largest WHOLE stat unit a
        /// <see cref="Fixed"/> can hold (<c>int.MaxValue >> FRACTIONAL_BITS</c> = 32767). Deliberately the whole-unit
        /// floor rather than the exact maximum (32767.99998…): that exact value is not representable in <c>float</c>
        /// and rounds UP to 32768 in the <c>(float)</c> cast <see cref="Fixed.FromFloat"/> takes, whose
        /// <c>× 65536</c> then overflows <c>int</c> and wraps to <see cref="Fixed.MinValue"/> — i.e. the clamp
        /// meant to prevent a wrap would itself cause one. Losing a fractional unit at the saturation ceiling is
        /// irrelevant: only content the validator already rejects can reach it.
        /// </summary>
        private const double MaxRepresentable = int.MaxValue >> Fixed.FRACTIONAL_BITS;

        /// <summary>All-zero contribution pair (no model / no attributes / a non-hero) — the inert default.</summary>
        public static (Fixed[] Base, Fixed[] PerLevel) Zero()
            => (new Fixed[AttributeStats.Count], new Fixed[AttributeStats.Count]);

        /// <summary>
        /// Flatten <paramref name="model"/> × <paramref name="hero"/>. A null model or null hero block yields
        /// all zeros (byte-identical to a pre-15-21 hero — golden-neutral by construction). Accumulates in
        /// <c>double</c> and quantizes ONCE per stat at the end (a single <see cref="Fixed.FromFloat"/> per
        /// stat — the fewest quantization points, and the same value on every peer because the inputs are
        /// authored floats and the rule order is the authored list order).
        /// </summary>
        public static (Fixed[] Base, Fixed[] PerLevel) Resolve(AttributeModelDefinition? model, HeroAttributesDefinition? hero)
        {
            if (model?.Derived == null || model.Derived.Count == 0 || hero == null) return Zero();

            var accBase     = new double[AttributeStats.Count];
            var accPerLevel = new double[AttributeStats.Count];

            for (int i = 0; i < model.Derived.Count; i++)
            {
                DerivedStatRule rule = model.Derived[i];
                if (rule.IsThreshold) continue; // Story 15-24c: step rows are EvaluateAt's — see its remarks
                if (!AttributeStats.TryIndexOf(rule.Stat, out int stat)) continue; // validator fail-closes; defensive here

                // "primary" is the WC3 selector: the row applies to the hero's flagged primary attribute.
                string? attr = string.Equals(rule.Attribute, "primary", System.StringComparison.Ordinal)
                    ? hero.Primary
                    : rule.Attribute;
                if (string.IsNullOrEmpty(attr)) continue; // no primary flagged → primary rules contribute nothing

                float b = 0f, p = 0f;
                if (hero.Base?.TryGetValue(attr!, out float hb) == true)     b = hb;
                if (hero.PerLevel?.TryGetValue(attr!, out float hp) == true) p = hp;

                accBase[stat]     += (double)rule.PerPoint * b;
                accPerLevel[stat] += (double)rule.PerPoint * p;
            }

            var outBase     = new Fixed[AttributeStats.Count];
            var outPerLevel = new Fixed[AttributeStats.Count];
            for (int s = 0; s < AttributeStats.Count; s++)
            {
                outBase[s]     = Fixed.FromFloat((float)accBase[s]);
                outPerLevel[s] = Fixed.FromFloat((float)accPerLevel[s]);
            }
            return (outBase, outPerLevel);
        }

        /// <summary>
        /// Story 15-24c — does <paramref name="model"/> carry any THRESHOLD row at all? Lets every consumer
        /// skip the whole step-evaluation path (and its allocation) for the shipped/linear-only models, which
        /// is what keeps 15-24c golden-neutral: a model with no threshold row behaves byte-for-byte as it did.
        /// </summary>
        public static bool HasThresholdRows(AttributeModelDefinition? model)
        {
            if (model?.Derived == null) return false;
            for (int i = 0; i < model.Derived.Count; i++)
                if (model.Derived[i].IsThreshold) return true;
            return false;
        }

        /// <summary>
        /// Story 15-24c — evaluate the model's THRESHOLD rows against a concrete attribute snapshot: the hero's
        /// live attribute totals at <paramref name="level"/> (<c>base + per_level × (level − 1)</c>, the D-2
        /// rule). Returns a per-stat <see cref="Fixed"/> contribution vector (length
        /// <see cref="AttributeStats.Count"/>), or null when the model carries no threshold row.
        ///
        /// <para><b>Why thresholds cannot ride <see cref="Resolve"/>'s output.</b> That method returns an
        /// affine coefficient pair — the contribution it describes is <c>base + perLevel × (L−1)</c>, a
        /// degree-1 polynomial in level, delivered as one install-once modifier plus (L−1) IDENTICAL stacks.
        /// A "every N points" row contributes <c>V × floor(A(L)/N)</c> whose first difference alternates
        /// between <c>floor(p/N)</c> and <c>ceil(p/N)</c>, while an affine function's first difference is
        /// CONSTANT — so no (base, perLevel) pair reproduces it except in two degenerate cases (a flat
        /// attribute, or a per-level gain that is an exact multiple of the step). The shape is one polynomial
        /// degree too low, which is arithmetic, not an implementation gap. Evaluating against the CURRENT
        /// total instead keeps the 15-24c promise exactly: the result stays a pure function of the folded
        /// Level, so no new folded state is introduced — the caller re-evaluates when Level changes and swaps
        /// one modifier slot (<c>HeroXpSystem.ReconcileThresholds</c>, the ResearchSystem cumulative pattern).</para>
        ///
        /// <para>Deterministic: iterates the authored rule list in order, reads hero dictionaries BY KEY (never
        /// a dictionary walk), accumulates in <c>double</c> and quantizes ONCE per stat — the same discipline
        /// <see cref="Resolve"/> uses. <c>floor</c> is <see cref="System.Math.Floor"/> on a non-negative
        /// authored total (the validator rejects negative attribute values and non-positive thresholds), so it
        /// is exact and platform-independent for every authorable input.</para>
        /// </summary>
        public static Fixed[]? EvaluateAt(AttributeModelDefinition? model, HeroAttributesDefinition? hero, int level)
        {
            if (model?.Derived == null || hero == null || !HasThresholdRows(model)) return null;
            if (level < 1) level = 1;

            var acc = new double[AttributeStats.Count];
            double levelsGained = level - 1;

            for (int i = 0; i < model.Derived.Count; i++)
            {
                DerivedStatRule rule = model.Derived[i];
                if (!rule.IsThreshold) continue;                                  // linear rows: Resolve's half
                if (!AttributeStats.TryIndexOf(rule.Stat, out int stat)) continue; // validator fail-closes

                string? attr = string.Equals(rule.Attribute, "primary", System.StringComparison.Ordinal)
                    ? hero.Primary
                    : rule.Attribute;
                if (string.IsNullOrEmpty(attr)) continue;

                float b = 0f, p = 0f;
                if (hero.Base?.TryGetValue(attr!, out float hb) == true)     b = hb;
                if (hero.PerLevel?.TryGetValue(attr!, out float hp) == true) p = hp;

                // The hero's LIVE attribute total at this level — the D-2 rule, the quantity a threshold tests.
                double total = (double)b + (double)p * levelsGained;
                float step = rule.Threshold ?? 0f;
                if (step <= 0f) continue; // validator rejects; defensive (never divide by ≤ 0)

                if (rule.ParsedShape == DerivationShape.PerStep)
                    acc[stat] += (double)rule.PerPoint * System.Math.Floor(total / step);
                else if (total >= step) // AtLeast — the gate node
                    acc[stat] += rule.PerPoint;
            }

            var outVec = new Fixed[AttributeStats.Count];
            for (int s = 0; s < AttributeStats.Count; s++)
            {
                // SATURATE at the 16.16 range instead of wrapping. A step row multiplies per_point by a step
                // COUNT that grows with the attribute total, so an over-authored row reaches values far outside
                // Fixed long before anything else does — and Fixed.FromFloat's `(int)(v * ONE)` cast on an
                // out-of-range value is a silent wrap (a huge positive total would land NEGATIVE, i.e. a
                // stat-destroying "bonus", and would slip the validator's own cap check by looking small).
                // Clamping here is what makes that cap check meaningful; the validator still rejects the
                // content, this just guarantees the value it inspects is monotonic in the authored magnitude.
                double v = acc[s];
                if (v > MaxRepresentable) v = MaxRepresentable;
                else if (v < -MaxRepresentable) v = -MaxRepresentable;
                outVec[s] = Fixed.FromFloat((float)v);
            }
            return outVec;
        }
    }
}
