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
                    c.Derived.Add(new DerivedStatRule { Attribute = d.Attribute, Stat = d.Stat, PerPoint = d.PerPoint });
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

    /// <summary>One derived-stat contribution row: <c>stat += per_point × attribute value</c>.</summary>
    public sealed class DerivedStatRule
    {
        /// <summary>A declared attribute id, or the reserved selector <c>"primary"</c> (the hero's flagged
        /// primary attribute — the WC3 primary-feeds-attack-damage lineage).</summary>
        [JsonPropertyName("attribute")] public string? Attribute { get; set; }

        /// <summary>A member of the CLOSED derived-stat vocabulary (<see cref="AttributeStats"/>). Fail-closed
        /// at validation — never an open reflection over effective-stat fields.</summary>
        [JsonPropertyName("stat")] public string? Stat { get; set; }

        /// <summary>Contribution per attribute point (authoring float; quantized once at the resolve boundary).</summary>
        [JsonPropertyName("per_point")] public float PerPoint { get; set; }
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
    }
}
