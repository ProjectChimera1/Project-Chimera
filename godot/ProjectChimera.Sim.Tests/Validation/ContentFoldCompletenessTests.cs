#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Serialization;
using ProjectChimera.Core;             // Fixed
using ProjectChimera.Core.Definitions;
using ProjectChimera.Combat;           // DamageTable
using ProjectChimera.Effects;          // EffectNode, DirectHpDeltaEffect
using Xunit;

namespace ProjectChimera.Sim.Tests.Validation
{
    /// <summary>
    /// Story 9.16 — the fold-completeness GUARD. Hand-folding is the hash family's established pattern but risks
    /// forgetting a newly-added field (the enum-indexed-array touch-site class). This reflection guard makes every
    /// JSON-mapped (<c>[JsonPropertyName]</c>, settable) field of each folded definition a CONSCIOUS decision:
    /// it must be in <see cref="ContentHash"/>'s FOLDED set, its EXCLUDED (presentation-only) set, or the ALLOWLIST
    /// (authoring-only, not sim-read). A dev who adds a new stat field to a folded def and folds neither nor
    /// allowlists it turns this test RED — so a stat field can never silently escape the handshake.
    ///
    /// <para>The classification below is the guard's source of truth; the actual fold behaviour (a listed FOLDED
    /// field really moves the hash) is proven separately by <see cref="ContentHashTests"/>.</para>
    /// </summary>
    public class ContentFoldCompletenessTests
    {
        /// <summary>Every JSON-mapped member's JSON name on <paramref name="t"/> (incl. inherited), excluding
        /// <c>[JsonIgnore]</c> computed/derived getters — i.e. exactly the System.Text.Json deserialization surface.
        /// Covers a <c>[JsonPropertyName]</c> prop with a public setter AND — the STJ opt-in surfaces the guard must not
        /// be blind to — a non-public/init setter or a field explicitly included via <c>[JsonInclude]</c>. Without the
        /// latter, a future stat added as a <c>[JsonInclude]</c> field or an <c>init</c>-only prop would deserialize yet
        /// stay invisible to the completeness guard and silently escape the handshake.</summary>
        private static HashSet<string> JsonMappedFields(Type t)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (PropertyInfo p in t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (p.GetCustomAttribute<JsonIgnoreAttribute>() != null) continue;
                JsonPropertyNameAttribute? attr = p.GetCustomAttribute<JsonPropertyNameAttribute>();
                if (attr == null) continue;
                bool publicSet   = p.SetMethod != null && p.SetMethod.IsPublic;
                bool includedSet = p.SetMethod != null && p.GetCustomAttribute<JsonIncludeAttribute>() != null;
                if (!publicSet && !includedSet) continue; // read-only / not an STJ-deserialized target
                names.Add(attr.Name);
            }
            // Fields: STJ only deserializes a field when it carries [JsonInclude]; its JSON name is [JsonPropertyName]
            // (or the field name). Including these closes the guard's field/init blind spot.
            foreach (FieldInfo fi in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (fi.GetCustomAttribute<JsonIgnoreAttribute>() != null) continue;
                if (fi.GetCustomAttribute<JsonIncludeAttribute>() == null) continue;
                JsonPropertyNameAttribute? attr = fi.GetCustomAttribute<JsonPropertyNameAttribute>();
                names.Add(attr?.Name ?? fi.Name);
            }
            return names;
        }

        private static void AssertClassified(Type t, string[] folded, string[] excluded, string[] allowlist)
        {
            HashSet<string> mapped = JsonMappedFields(t);

            // (0) Non-vacuity (P7): the guard skips props lacking [JsonPropertyName]; if a folded def ever switched to
            // name-policy/ctor-based mapping, JsonMappedFields would be EMPTY and every check below would pass
            // vacuously over a type it never actually saw. Assert the type really uses attribute-based mapping.
            Assert.True(mapped.Count > 0,
                $"{t.Name}: no [JsonPropertyName]-mapped settable fields found — the completeness guard would pass " +
                "vacuously. This def must use attribute-based JSON mapping for the guard to be meaningful.");

            var classified = new HashSet<string>(folded.Concat(excluded).Concat(allowlist), StringComparer.Ordinal);

            // (1) Every JSON-mapped field is consciously classified — RED on a new unclassified field.
            string[] unclassified = mapped.Where(n => !classified.Contains(n)).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            Assert.True(unclassified.Length == 0,
                $"{t.Name}: JSON-mapped field(s) [{string.Join(", ", unclassified)}] are neither folded, excluded, " +
                "nor allowlisted in ContentHash. Fold them into ContentHash (sim-relevant) or add to the exclusion/" +
                "allowlist (presentation-only / authoring-only) — a stat field must not silently escape the handshake.");

            // (2) No stale classification: every classified name must still be a real JSON-mapped field (catches typos
            //     and fields that were renamed/removed without updating the guard).
            string[] stale = classified.Where(n => !mapped.Contains(n)).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            Assert.True(stale.Length == 0,
                $"{t.Name}: classified name(s) [{string.Join(", ", stale)}] are no longer JSON-mapped fields — update the guard.");
        }

        // ── Classification (mirrors ContentHash's fold decisions) ───────────────────────────────────────────────

        private static readonly string[] UnitFolded =
        {
            "id", "category", "hp", "speed", "attack_damage", "attack_range", "attack_speed", "damage_type",
            "armor_type", "armor", "cost_ore", "cost_crystal", "cost", "supply", "train_time", "vision_range",
            "splash_radius", "delivery", "projectile_speed", "xp_bounty", "collision_radius", "separation_priority",
            "prerequisites", "abilities", "attack_domains", "tags", "is_hero", "revives_heroes", "sells_items",
            "shop_stock", "shop_radius", "max_energy", "regen_rate",
            "health_regen", // Story 15-24a: sim-read (drives HealthRegenSystem → folded Health) — the regen_rate posture

            // Story 15-21: the hero block folds (ContentHash v2 — its curve was ALREADY sim-read since 3.13, and
            // the new attributes block drives stats through HeroAttributeResolver). Leaves the allowlist.
            "hero",
        };
        private static readonly string[] UnitExcluded = { "display_name", "mesh_path", "mesh_scale", "combat_feedback" };
        private static readonly string[] UnitAllowlist = { "behaviors" }; // authoring-only, not sim-read (fold when a story reads them)

        [Fact]
        public void UnitDefinition_EveryFieldClassified()
            => AssertClassified(typeof(UnitDefinition), UnitFolded, UnitExcluded, UnitAllowlist);

        [Fact]
        public void BuildingDefinition_EveryFieldClassified()
            => AssertClassified(typeof(BuildingDefinition),
                UnitFolded.Concat(new[] { "construction_time", "supply_bonus", "produces_category", "available_research" }).ToArray(),
                // command_card_producer is presentation-only: it selects which command-card UI surface renders, never
                // deterministic sim state (see spec-command-card-producer-surfaces — not folded into SimChecksum, no
                // goldens move), so it is EXCLUDED like display_name/combat_feedback, not folded into ContentHash.
                UnitExcluded.Concat(new[] { "command_card_producer" }).ToArray(),
                UnitAllowlist);

        [Fact]
        public void FactionDefinition_EveryFieldClassified()
            => AssertClassified(typeof(FactionDefinition),
                folded: new[] { "id", "units", "buildings", "research", "signature_mechanic", "signature_mechanic_effect_id", "hero_unit_id", "persistence_enabled", "starting_ore", "starting_crystal",
                                "attribute_model" }, // Story 15-21: sim-read via HeroAttributeResolver at apply (ContentHash v2)
                excluded: new[] { "display_name", "color", "ai_preset", "signature_mechanic_display" },
                allowlist: Array.Empty<string>());

        [Fact]
        public void AbilityDefinition_EveryFieldClassified()
            => AssertClassified(typeof(AbilityDefinition),
                folded: new[] { "id", "targeting", "activation", "cost_energy", "cost_ore", "cost_crystal", "cost_health", "allow_self_lethal", "cooldown", "effect" },
                // Story 15.11 (DW-286): target_affinity is EXCLUDED (a UI click-picker hint, not sim state) — deliberately
                // NOT folded so adding it moves no ContentHash/CanonicalModelHash for any shipped ability (absent → identical).
                excluded: new[] { "display_name", "combat_feedback", "target_affinity" },
                allowlist: Array.Empty<string>());

        [Fact]
        public void ItemDefinition_EveryFieldClassified()
            => AssertClassified(typeof(ItemDefinition),
                folded: new[] { "id", "charges", "max_health_delta", "attack_damage_delta", "move_speed_delta", "armor_delta",
                                "stat_deltas", // Story 15-24a: folds via the canonical sparse vector (BuildStatDeltaVector, ContentHash v3)
                                "effect", "cost_ore", "cost_crystal" },
                excluded: new[] { "display_name", "icon" },
                allowlist: Array.Empty<string>());

        [Fact]
        public void ResearchDefinition_EveryFieldClassified()
            => AssertClassified(typeof(ResearchDefinition),
                folded: new[] { "id", "cancel_refund_fraction", "prerequisites", "levels" },
                excluded: new[] { "display_name" },
                allowlist: Array.Empty<string>());

        [Fact]
        public void ResearchLevel_EveryFieldClassified()
            => AssertClassified(typeof(ResearchLevel),
                folded: new[] { "cost", "time_ticks", "modifier_delta" },
                excluded: Array.Empty<string>(),
                allowlist: Array.Empty<string>());

        [Fact]
        public void ResearchModifierDelta_EveryFieldClassified()
            => AssertClassified(typeof(ResearchModifierDelta),
                folded: new[] { "max_health_delta", "attack_damage_delta", "move_speed_delta", "armor_delta",
                                "stat_deltas" }, // Story 15-24a: folds via the canonical sparse vector (BuildStatDeltaVector, ContentHash v3)
                excluded: Array.Empty<string>(),
                allowlist: Array.Empty<string>());

        // ── P2: fold-actuality sweep — every "folded"-classified field ACTUALLY moves ContentHash ────────────────
        //
        // The classification tests above only prove a field is CLASSIFIED as folded, not that ContentHash actually
        // mixes it — a field listed "folded" but missing its Mix line would escape. This sweep perturbs each folded
        // field on a fresh fixture and asserts the hash MOVES; it goes RED if a folded-classified field is unmixed.

        /// <summary>Two distinct values for a folded property's type (nonzero/distinct so no resolver masks the move).
        /// Throws on an unhandled type so a NEW folded-field type must be consciously supported here (not skipped).</summary>
        private static (object a, object b) TwoDistinct(Type propType)
        {
            Type u = Nullable.GetUnderlyingType(propType) ?? propType;
            if (propType == typeof(string)) return ("Zx_alpha_1", "Zx_beta_2");
            if (u == typeof(bool)) return (false, true);
            if (u == typeof(int)) return (7, 9);
            if (u == typeof(float)) return (7f, 9f);
            if (u == typeof(Fixed)) return (Fixed.FromInt(7), Fixed.FromInt(9));
            if (propType == typeof(string[])) return (new[] { "q_aa" }, new[] { "q_bb" });
            if (propType == typeof(Dictionary<string, int>))
                return (new Dictionary<string, int> { { "ore", 1 } }, new Dictionary<string, int> { { "ore", 2 } });
            if (propType == typeof(EffectNode))
                return (new DirectHpDeltaEffect(Fixed.FromInt(7)), new DirectHpDeltaEffect(Fixed.FromInt(9)));
            if (propType == typeof(List<UnitDefinition>))
                return (new List<UnitDefinition> { new UnitDefinition { Id = "ua" } }, new List<UnitDefinition> { new UnitDefinition { Id = "ub" } });
            if (propType == typeof(List<BuildingDefinition>))
                return (new List<BuildingDefinition> { new BuildingDefinition { Id = "ba" } }, new List<BuildingDefinition> { new BuildingDefinition { Id = "bb" } });
            if (propType == typeof(List<ResearchDefinition>))
                return (new List<ResearchDefinition> { new ResearchDefinition { Id = "ra" } }, new List<ResearchDefinition> { new ResearchDefinition { Id = "rb" } });
            if (propType == typeof(List<ResearchLevel>))
                return (new List<ResearchLevel> { new ResearchLevel { TimeTicks = 7 } }, new List<ResearchLevel> { new ResearchLevel { TimeTicks = 9 } });
            if (propType == typeof(ResearchModifierDelta))
                return (new ResearchModifierDelta { MaxHealthDelta = 7f }, new ResearchModifierDelta { MaxHealthDelta = 9f });
            // Story 15-24a: the sparse stat_deltas lanes (ContentHash v3 — fold via the canonical vector).
            if (propType == typeof(Dictionary<string, float>))
                return (new Dictionary<string, float> { { "attack_speed", 0.1f } },
                        new Dictionary<string, float> { { "attack_speed", 0.2f } });
            if (propType == typeof(Dictionary<string, Fixed>))
                return (new Dictionary<string, Fixed> { { "attack_speed", Fixed.FromRaw(6554) } },
                        new Dictionary<string, Fixed> { { "attack_speed", Fixed.FromRaw(13107) } });
            // Story 15-21: the hero block (ContentHash v2) + the faction attribute model are folded types now.
            if (propType == typeof(HeroDefinition))
                return (new HeroDefinition { MaxLevel = 7 }, new HeroDefinition { MaxLevel = 9 });
            if (propType == typeof(AttributeModelDefinition))
                return (new AttributeModelDefinition { Attributes = new List<AttributeDeclaration> { new() { Id = "str_a" } } },
                        new AttributeModelDefinition { Attributes = new List<AttributeDeclaration> { new() { Id = "str_b" } } });
            throw new Xunit.Sdk.XunitException(
                $"TwoDistinct: unhandled folded-field type {propType} — add a case so the fold-actuality sweep covers it.");
        }

        private static PropertyInfo PropByJsonName(Type t, string jsonName) =>
            t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
             .First(p => p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name == jsonName);

        /// <summary>For each folded field: build two fresh <typeparamref name="_"/>-typed instances differing ONLY in
        /// that field, embed each via <paramref name="compute"/>, and assert the hash MOVES.</summary>
        private static void AssertEveryFoldedFieldMoves(Type t, string[] folded, Func<object, ulong> compute)
        {
            foreach (string name in folded)
            {
                PropertyInfo p = PropByJsonName(t, name);
                (object va, object vb) = TwoDistinct(p.PropertyType);

                object insA = Activator.CreateInstance(t)!;
                object insB = Activator.CreateInstance(t)!;
                p.SetValue(insA, va);
                p.SetValue(insB, vb);

                ulong ha = compute(insA);
                ulong hb = compute(insB);
                Assert.True(ha != hb,
                    $"{t.Name}.{name} is classified FOLDED but perturbing it did not move ContentHash — its Mix line " +
                    "is missing (or a resolver masks it). Fold it, or reclassify it excluded/allowlisted.");
            }
        }

        // Embedding helpers: wrap a single perturbed def into the minimal content set that folds it.
        private static ulong Faction(FactionDefinition f) => ContentHash.Compute(new List<FactionDefinition> { f }, AbilityRegistry.Empty, ItemRegistry.Empty, DamageTable.Default);
        private static ulong Unit(object o) => Faction(new FactionDefinition { Id = "f", Units = new List<UnitDefinition> { (UnitDefinition)o } });
        private static ulong Building(object o) => Faction(new FactionDefinition { Id = "f", Buildings = new List<BuildingDefinition> { (BuildingDefinition)o } });
        private static ulong Research(object o) => Faction(new FactionDefinition { Id = "f", Research = new List<ResearchDefinition> { (ResearchDefinition)o } });
        private static ulong Level(object o) => Research(new ResearchDefinition { Id = "r", Levels = new List<ResearchLevel> { (ResearchLevel)o } });
        private static ulong ModDelta(object o) => Level(new ResearchLevel { TimeTicks = 1, ModifierDelta = (ResearchModifierDelta)o });
        private static ulong Ability(object o) => ContentHash.Compute(new List<FactionDefinition>(), new AbilityRegistry(new List<AbilityDefinition> { (AbilityDefinition)o }), ItemRegistry.Empty, DamageTable.Default);
        private static ulong Item(object o) => ContentHash.Compute(new List<FactionDefinition>(), AbilityRegistry.Empty, new ItemRegistry(new List<ItemDefinition> { (ItemDefinition)o }), DamageTable.Default);

        [Fact]
        public void FoldActuality_UnitDefinition()
            => AssertEveryFoldedFieldMoves(typeof(UnitDefinition), UnitFolded, Unit);

        [Fact]
        public void FoldActuality_BuildingDefinition()
            => AssertEveryFoldedFieldMoves(typeof(BuildingDefinition),
                UnitFolded.Concat(new[] { "construction_time", "supply_bonus", "produces_category", "available_research" }).ToArray(),
                Building);

        [Fact]
        public void FoldActuality_FactionDefinition()
            => AssertEveryFoldedFieldMoves(typeof(FactionDefinition),
                new[] { "id", "units", "buildings", "research", "signature_mechanic", "signature_mechanic_effect_id", "hero_unit_id", "persistence_enabled", "starting_ore", "starting_crystal" },
                o => Faction((FactionDefinition)o));

        [Fact]
        public void FoldActuality_AbilityDefinition()
            => AssertEveryFoldedFieldMoves(typeof(AbilityDefinition),
                new[] { "id", "targeting", "activation", "cost_energy", "cost_ore", "cost_crystal", "cost_health", "allow_self_lethal", "cooldown", "effect" },
                Ability);

        [Fact]
        public void FoldActuality_ItemDefinition()
            => AssertEveryFoldedFieldMoves(typeof(ItemDefinition),
                new[] { "id", "charges", "max_health_delta", "attack_damage_delta", "move_speed_delta", "armor_delta", "effect", "cost_ore", "cost_crystal" },
                Item);

        [Fact]
        public void FoldActuality_ResearchDefinition()
            => AssertEveryFoldedFieldMoves(typeof(ResearchDefinition),
                new[] { "id", "cancel_refund_fraction", "prerequisites", "levels" },
                Research);

        [Fact]
        public void FoldActuality_ResearchLevel()
            => AssertEveryFoldedFieldMoves(typeof(ResearchLevel),
                new[] { "cost", "time_ticks", "modifier_delta" },
                Level);

        [Fact]
        public void FoldActuality_ResearchModifierDelta()
            => AssertEveryFoldedFieldMoves(typeof(ResearchModifierDelta),
                new[] { "max_health_delta", "attack_damage_delta", "move_speed_delta", "armor_delta" },
                ModDelta);
    }
}
