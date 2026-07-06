#nullable enable
using System.Text.Json.Serialization;
using ProjectChimera.Combat;
using ProjectChimera.Core; // UnitCategory, SeparationPriority (sim enums)

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// Data-driven unit definition loaded from JSON.
    /// One entry per unit type in a faction.
    /// </summary>
    public class UnitDefinition
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("display_name")]
        public string DisplayName { get; set; } = "";

        /// <summary>One of: Worker, Melee, Ranged, Siege, Air, Structure</summary>
        [JsonPropertyName("category")]
        public string Category { get; set; } = "Melee";

        /// <summary>
        /// Res path to the GLB file (e.g. "res://assets/models/factions/alpha/warrior.glb").
        /// If null or file missing, MeshLoader falls back to a box placeholder.
        /// </summary>
        [JsonPropertyName("mesh_path")]
        public string? MeshPath { get; set; }

        [JsonPropertyName("hp")]
        public float Hp { get; set; } = 100f;

        [JsonPropertyName("speed")]
        public float Speed { get; set; } = 4f;

        [JsonPropertyName("attack_damage")]
        public float AttackDamage { get; set; } = 10f;

        [JsonPropertyName("attack_range")]
        public float AttackRange { get; set; } = 5f;

        /// <summary>Seconds between attacks.</summary>
        [JsonPropertyName("attack_speed")]
        public float AttackSpeed { get; set; } = 1f;

        /// <summary>Normal | Pierce | Siege | Magic</summary>
        [JsonPropertyName("damage_type")]
        public string DamageType { get; set; } = "Normal";

        /// <summary>Unarmored | Light | Medium | Heavy | Fortified</summary>
        [JsonPropertyName("armor_type")]
        public string ArmorType { get; set; } = "Unarmored";

        /// <summary>Flat base armor (Story 2.6, Decision #6) — subtracted from post-matrix damage in
        /// <c>DamageResolver</c> (floored at 0). Authored float like the other stats; quantized once to
        /// <see cref="ProjectChimera.Core.Fixed"/> in <c>EntityWorld.ApplyUnitDefinition</c>. 0 = no armor (default →
        /// combat outcomes unchanged). Distinct from <see cref="ArmorType"/> (the damage-matrix axis).</summary>
        [JsonPropertyName("armor")]
        public float Armor { get; set; } = 0f;

        /// <summary>Ore cost.</summary>
        [JsonPropertyName("cost_ore")]
        public int CostOre { get; set; } = 50;

        /// <summary>Crystal cost (advanced units only).</summary>
        [JsonPropertyName("cost_crystal")]
        public int CostCrystal { get; set; } = 0;

        /// <summary>Supply consumed by one of these units.</summary>
        [JsonPropertyName("supply")]
        public int Supply { get; set; } = 1;

        /// <summary>Visual scale applied to the unit mesh at import time.</summary>
        [JsonPropertyName("mesh_scale")]
        public float MeshScale { get; set; } = 1f;

        /// <summary>Seconds to train this unit at a producing building.</summary>
        [JsonPropertyName("train_time")]
        public float TrainTime { get; set; } = 8f;

        /// <summary>Vision radius in world units. Stamped each tick by FogOfWarSystem.</summary>
        [JsonPropertyName("vision_range")]
        public float VisionRange { get; set; } = 8f;

        /// <summary>
        /// AoE splash radius on projectile hit (world units). 0 = no splash (default).
        /// Applies to Siege archetype; dealt at full damage to all enemies in radius.
        /// </summary>
        [JsonPropertyName("splash_radius")]
        public float SplashRadius { get; set; } = 0f;

        /// <summary>
        /// Per-unit collision/separation radius in world units (Story 1.13, DG-2 / FR-54). Summed with a
        /// neighbour's radius to form the per-pair contact threshold in <c>MovementSystem</c>'s separation
        /// (replacing the old flat constant). Default 1.0 so two unauthored units sum to a 2.0 contact distance
        /// = the legacy flat separation radius (backward-compatible). Omitted, &lt;= 0, or &gt; the engine cap is
        /// clamped to the documented default/max at spawn (see <c>ScenarioApplier.SpawnUnit</c>).
        /// </summary>
        [JsonPropertyName("collision_radius")]
        public float CollisionRadius { get; set; } = 1.0f;

        /// <summary>
        /// Crowd-steering precedence: Yield | Normal | Push (Story 1.13). A Push unit holds its ground against a
        /// Yield neighbour it contacts. Default "Normal" → symmetric separation, so existing factions are
        /// unchanged. Parsed to <see cref="SeparationPriority"/> via <see cref="ParsedSeparationPriority"/>.
        /// </summary>
        [JsonPropertyName("separation_priority")]
        public string SeparationPriority { get; set; } = "Normal";

        /// <summary>
        /// Building-type IDs that must be alive and fully constructed (for the same faction)
        /// before this unit can be trained or this building can be placed.
        /// Example: ["barracks"] means a completed Barracks is required.
        /// Empty array = no prerequisites.
        /// </summary>
        [JsonPropertyName("prerequisites")]
        public string[] Prerequisites { get; set; } = System.Array.Empty<string>();

        /// <summary>
        /// Active-ability ids this unit type can cast (each references an ability JSON in
        /// <c>resources/data/abilities/</c>). Mirrors <see cref="Prerequisites"/> — a snake_case JSON string array,
        /// empty = no abilities. Resolved to <see cref="AbilityRegistry"/> indices via <see cref="ResolveAbilities"/>
        /// at scenario link time (see <see cref="AbilityIndices"/>); cast through the deterministic effect engine (Story 2.4a).
        /// </summary>
        [JsonPropertyName("abilities")]
        public string[] Abilities { get; set; } = System.Array.Empty<string>();

        /// <summary>
        /// The target domains this unit can attack, as a snake_case JSON string array of <c>"Ground"</c> / <c>"Air"</c> /
        /// <c>"Structure"</c> (Story 2.9a, FR-11/FR-12). This is an <b>attacker-capability</b> axis, independent of the
        /// unit's own <see cref="Category"/>: <c>["Air"]</c> makes an anti-air-only unit; omitting the field (or an empty
        /// array, or listing all three) means <see cref="AttackDomain.All"/> — every domain, exactly as before. Resolved
        /// via <see cref="ParsedAttackDomains"/> and written to <c>EntityWorld.AttackDomainOf</c> in
        /// <see cref="ProjectChimera.Core.EntityWorld.ApplyUnitDefinition"/> (the single def→SoA mapper). Nullable so
        /// "unauthored" is distinguishable from "authored empty" — both fold to <see cref="AttackDomain.All"/>.
        /// </summary>
        [JsonPropertyName("attack_domains")]
        public string[]? AttackDomains { get; set; }

        /// <summary>
        /// The classification tags this unit carries, as a snake_case JSON string array of <c>"Organic"</c> /
        /// <c>"Mechanical"</c> / <c>"Magical"</c> (Story 2.11, DG-4 / FR-56). An orthogonal effect-targeting &amp;
        /// counter axis, independent of <see cref="DamageType"/> / <see cref="ArmorType"/> / <see cref="Category"/>:
        /// the effect engine filters/counters by it (<c>SearchArea.require_tag</c> + the single-target leaf gate)
        /// WITHOUT abusing the damage/armor matrix. A unit MAY carry several tags (OR-folded via <see cref="ParsedTags"/>).
        /// Nullable so "unauthored" is distinguishable from "authored empty" — BOTH fold to <see cref="UnitTag.None"/>
        /// (the CRITICAL divergence from <see cref="AttackDomains"/>, which defaults to <c>All</c>: an untagged unit is
        /// matched by NO tag predicate, so no shipping unit gains a tag by omission). Resolved via <see cref="ParsedTags"/>
        /// and written to <c>EntityWorld.TagsOf</c> in <see cref="ProjectChimera.Core.EntityWorld.ApplyUnitDefinition"/>
        /// (the single def→SoA mapper). Loaded through the LENIENT faction loader, so it MUST be <c>string[]?</c> (an
        /// enum-typed field would throw on that path); an unknown token is rejected fail-closed by the closed-set
        /// unit-tag validator (AC2), NOT silently by <see cref="ParsedTags"/>.
        /// </summary>
        [JsonPropertyName("tags")]
        public string[]? Tags { get; set; }

        /// <summary>
        /// Marks this unit type as a HERO (Story 3.2, D-7 / AR-12) — an orthogonal designation that gates minting a
        /// persistent <see cref="ProjectChimera.Core.HeroStore"/> row (with a stable cross-match identity) when the
        /// unit spawns. Deliberately NOT a 7th <see cref="ProjectChimera.Core.UnitCategory"/>: a hero is orthogonal to
        /// the movement/formation archetype (a hero may be Melee or Ranged), and a new category member would also trip
        /// the enum-indexed-array touch-site rule. Mirrors the Story-2.11 <see cref="Tags"/> precedent (an orthogonal
        /// axis added without abusing an existing enum). A plain settable value-type auto-prop (like <see cref="Armor"/>),
        /// default false — lenient-loader-safe (no enum-typed field on the faction-loader path; an omitted key stays
        /// false = not a hero, so no existing unit becomes a hero by omission and no faction JSON changes). NOT
        /// combat/checksum state: read ONLY at spawn to decide whether to mint a hero row. A hero unit MAY additionally
        /// author <c>damage_type:"Hero"</c> / <c>armor_type:"Hero"</c> (the Hero rows reserved by Story 1.6), but that
        /// is independent of this flag. The full load-and-populate path (from PlayerProfile) is Story 3.9; 3.2 defines
        /// the designation + the minting API.
        /// </summary>
        [JsonPropertyName("is_hero")]
        public bool IsHero { get; set; } = false;

        /// <summary>
        /// Maximum ability-resource (energy) pool for this unit type (authored float, quantized once to
        /// <see cref="ProjectChimera.Core.Fixed"/> in <see cref="ProjectChimera.Core.EntityWorld.ApplyUnitDefinition"/>
        /// — the single float→Fixed boundary, like the other stats). 0 = no energy pool (cannot cast energy-cost
        /// abilities). Story 2.4a: the unit starts FULL (Energy = MaxEnergy) and there is no regen yet.
        /// </summary>
        [JsonPropertyName("max_energy")]
        public float MaxEnergy { get; set; } = 0f;

        /// <summary>
        /// Optional per-unit combat-feedback override (Story 2.7, FR-12a). Presentation-domain — EXCLUDED from
        /// <see cref="ProjectChimera.Core.SimChecksum"/> and the canonical hash; the sim only carries the reference
        /// (copied to <c>EntityWorld.FeedbackProfile</c> in <see cref="ProjectChimera.Core.EntityWorld.ApplyUnitDefinition"/>),
        /// it never reads the values. Nullable ⇒ omittable ⇒ existing faction JSON is unaffected; null ⇒ the tuned
        /// event-type defaults play. A declared auto-prop (not a computed getter) so it round-trips cleanly on the
        /// strict ability path too (the same POCO rides <see cref="AbilityDefinition.CombatFeedback"/>).
        /// </summary>
        [JsonPropertyName("combat_feedback")]
        public CombatFeedbackProfile? CombatFeedback { get; set; }

        /// <summary>
        /// Registry indices of <see cref="Abilities"/>, back-filled ONCE at scenario link by
        /// <see cref="ResolveAbilities"/>. Unlike <see cref="ParsedCategory"/> (a pure computed prop) this needs the
        /// <see cref="AbilityRegistry"/>, so it is an explicit resolve step run before any spawn. Excluded from JSON.
        /// </summary>
        [JsonIgnore]
        public int[] AbilityIndices { get; private set; } = System.Array.Empty<int>();

        /// <summary>Registry index of this unit's while-alive AURA passive (−1 = none), back-filled by
        /// <see cref="ResolveAbilities"/>. Copied to <c>EntityWorld.AuraAbilityIndex</c> via ApplyUnitDefinition. Excluded from JSON.</summary>
        [JsonIgnore]
        public int AuraAbilityIndex { get; private set; } = -1;

        /// <summary>Registry index of this unit's ON-HIT rider passive (−1 = none), back-filled by
        /// <see cref="ResolveAbilities"/>. Copied to <c>EntityWorld.OnHitAbilityIndex</c> via ApplyUnitDefinition. Excluded from JSON.</summary>
        [JsonIgnore]
        public int OnHitAbilityIndex { get; private set; } = -1;

        /// <summary>Registry index of this unit's WHILE-ALIVE self-passive (−1 = none), back-filled by
        /// <see cref="ResolveAbilities"/>. Copied to <c>EntityWorld.SelfPassiveAbilityIndex</c> via ApplyUnitDefinition. Excluded from JSON.</summary>
        [JsonIgnore]
        public int SelfPassiveAbilityIndex { get; private set; } = -1;

        /// <summary>
        /// Resolve each <see cref="Abilities"/> id to its <paramref name="registry"/> index, DROPPING any id the
        /// registry does not contain (a unit referencing an unknown ability gets fewer slots, never a crash — the
        /// validator already guaranteed each registry ability is valid), and PARTITION by activation (Story 2.6): an
        /// ACTIVE ability fills a player-cast slot (<see cref="AbilityIndices"/>, clamped to
        /// <see cref="ProjectChimera.Core.EntityWorld.MAX_ABILITIES_PER_UNIT"/>); a passive fills its single dedicated
        /// slot (<see cref="AuraAbilityIndex"/>/<see cref="OnHitAbilityIndex"/>/<see cref="SelfPassiveAbilityIndex"/>;
        /// the FIRST of each kind wins). Passives are kept OUT of <see cref="AbilityIndices"/> so they never appear as
        /// castable on the command card. Run once at scenario link, before any spawn; idempotent. (Allocation here is
        /// fine — link-time, not the tick.)
        /// </summary>
        public void ResolveAbilities(AbilityRegistry registry)
        {
            AbilityIndices          = System.Array.Empty<int>();
            AuraAbilityIndex        = -1;
            OnHitAbilityIndex       = -1;
            SelfPassiveAbilityIndex = -1;

            if (registry is null || Abilities.Length == 0)
                return;

            int max = ProjectChimera.Core.EntityWorld.MAX_ABILITIES_PER_UNIT;
            var active = new System.Collections.Generic.List<int>(Abilities.Length);
            for (int i = 0; i < Abilities.Length; i++)
            {
                int idx = registry.IndexOf(Abilities[i]);
                if (idx < 0) continue; // drop unknown ids (never crash)

                // The registry holds only validated abilities → ParsedActivation is non-null; default-to-Active is defensive.
                switch (registry.Get(idx).ParsedActivation ?? PassiveActivation.Active)
                {
                    case PassiveActivation.Aura:
                        if (AuraAbilityIndex < 0) AuraAbilityIndex = idx;
                        break;
                    case PassiveActivation.OnHit:
                        if (OnHitAbilityIndex < 0) OnHitAbilityIndex = idx;
                        break;
                    case PassiveActivation.WhileAlive:
                        if (SelfPassiveAbilityIndex < 0) SelfPassiveAbilityIndex = idx;
                        break;
                    default: // Active — a player-cast slot (capped)
                        if (active.Count < max) active.Add(idx);
                        break;
                }
            }
            AbilityIndices = active.ToArray();
        }

        // ── Enum conversions ────────────────────────────────────────────────────

        /// <summary>DamageType string from JSON resolved to enum.</summary>
        public DamageType ParsedDamageType => DamageType switch
        {
            "Pierce" => Combat.DamageType.Pierce,
            "Siege"  => Combat.DamageType.Siege,
            "Magic"  => Combat.DamageType.Magic,
            _        => Combat.DamageType.Normal,
        };

        /// <summary>ArmorType string from JSON resolved to enum.</summary>
        public ArmorType ParsedArmorType => ArmorType switch
        {
            "Light"     => Combat.ArmorType.Light,
            "Medium"    => Combat.ArmorType.Medium,
            "Heavy"     => Combat.ArmorType.Heavy,
            "Fortified" => Combat.ArmorType.Fortified,
            _           => Combat.ArmorType.Unarmored,
        };

        /// <summary>
        /// separation_priority string from JSON resolved to enum. Exact-string match (mirrors
        /// <see cref="ParsedDamageType"/>); unknown / unset → Normal (symmetric separation). The enum is
        /// qualified with <c>Core.</c> because the string property above shares its name (the same disambiguation
        /// <see cref="ParsedDamageType"/> uses for the <c>DamageType</c> property/enum clash).
        /// </summary>
        public SeparationPriority ParsedSeparationPriority => SeparationPriority switch
        {
            "Yield" => Core.SeparationPriority.Yield,
            "Push"  => Core.SeparationPriority.Push,
            _       => Core.SeparationPriority.Normal,
        };

        /// <summary>category string from JSON resolved to the archetype enum. Unknown / unset → Melee.</summary>
        public UnitCategory ParsedCategory => Category switch
        {
            "Worker"    => UnitCategory.Worker,
            "Ranged"    => UnitCategory.Ranged,
            "Siege"     => UnitCategory.Siege,
            "Air"       => UnitCategory.Air,
            "Structure" => UnitCategory.Structure,
            _           => UnitCategory.Melee,
        };

        /// <summary>
        /// attack_domains strings OR-folded to the capability flag set (Story 2.9a). Null / empty / all-unknown →
        /// <see cref="AttackDomain.All"/> (attacks every domain, the unauthored default — so no existing unit changes
        /// behaviour and no golden moves); any recognized subset restricts to exactly those domains (author opt-in).
        /// </summary>
        public AttackDomain ParsedAttackDomains
        {
            get
            {
                if (AttackDomains == null || AttackDomains.Length == 0) return AttackDomain.All;
                AttackDomain result = AttackDomain.None;
                foreach (string s in AttackDomains)
                {
                    result |= s switch
                    {
                        "Ground"    => AttackDomain.Ground,
                        "Air"       => AttackDomain.Air,
                        "Structure" => AttackDomain.Structure,
                        _           => AttackDomain.None,
                    };
                }
                return result == AttackDomain.None ? AttackDomain.All : result;
            }
        }

        /// <summary>
        /// tags strings OR-folded to the <see cref="UnitTag"/> classification set (Story 2.11). Null / empty /
        /// all-unknown → <see cref="UnitTag.None"/> — the CRITICAL divergence from <see cref="ParsedAttackDomains"/>
        /// (which defaults to <c>All</c>): an untagged unit is matched by NO tag predicate, so no existing unit gains a
        /// tag by omission and no golden moves. Any recognized subset ORs to exactly those bits (author opt-in; a unit
        /// can be <c>Organic|Magical</c>). LENIENT — an unknown token contributes <see cref="UnitTag.None"/> (fail-open);
        /// the closed-set REJECT is the validator's job (AC2), NOT this accessor's (the deliberate accessor/validator
        /// split the codebase already uses for <c>AbilityDefinition.ParsedTargeting</c>). Getter-only, and the lenient
        /// faction loader never re-serializes it, so no <c>[JsonIgnore]</c> is needed (matches <see cref="ParsedDamageType"/>).
        /// </summary>
        public UnitTag ParsedTags
        {
            get
            {
                if (Tags == null || Tags.Length == 0) return UnitTag.None;
                UnitTag result = UnitTag.None;
                foreach (string s in Tags)
                {
                    result |= s switch
                    {
                        "Organic"    => UnitTag.Organic,
                        "Mechanical" => UnitTag.Mechanical,
                        "Magical"    => UnitTag.Magical,
                        _            => UnitTag.None,
                    };
                }
                return result;   // None if nothing recognized (NOT All) — the untagged/back-compat default (AC5.1).
            }
        }
    }
}
