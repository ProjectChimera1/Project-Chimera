#nullable enable
using System.Collections.Generic;
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

        /// <summary>
        /// The authored sparse N-resource cost map (Story 4.3) — JSON keys are resource ids (today only
        /// <c>"ore"</c>/<c>"crystal"</c> have runtime backing; see <see cref="ResourceCostValidator"/>), values are
        /// non-negative int amounts. Null (the default) means "not authored" — <see cref="ResolvedCost"/> then
        /// derives the legacy <see cref="CostOre"/>/<see cref="CostCrystal"/> map, so every existing unit/building
        /// with no <c>cost</c> key behaves byte-identically to today. An authored EMPTY map (<c>"cost": {}</c>) is
        /// deliberately distinct from null — it means "free" (verbatim, no legacy fallback).
        /// </summary>
        [JsonPropertyName("cost")]
        public Dictionary<string, int>? Cost { get; set; }

        /// <summary>
        /// The resolved sparse cost map this unit/building actually trains/constructs for: <see cref="Cost"/>
        /// verbatim when authored (even if empty ⇒ free), else the legacy <c>{ "ore": CostOre, "crystal": CostCrystal }</c>
        /// map with zero-valued entries omitted — the exact derivation <see cref="BuildingDefinition.ConstructionCost"/>
        /// used before Story 4.3 generalized it from Building-only to every <see cref="UnitDefinition"/> (units train
        /// with the same sparse map buildings construct with). Excluded from JSON (computed, never authored directly).
        /// </summary>
        [JsonIgnore]
        public IReadOnlyDictionary<string, int> ResolvedCost => Cost ?? LegacyCost();

        /// <summary>Builds <c>{ "ore": CostOre, "crystal": CostCrystal }</c>, omitting zero-valued entries (a free
        /// resource is simply absent from the map, not a zero entry) — the pre-4.3 <c>BuildingDefinition.ConstructionCost</c>
        /// derivation, moved here verbatim so it now backs <see cref="ResolvedCost"/> for every unit, not just buildings.</summary>
        private Dictionary<string, int> LegacyCost()
        {
            var map = new Dictionary<string, int>();
            if (CostOre != 0) map["ore"] = CostOre;
            if (CostCrystal != 0) map["crystal"] = CostCrystal;
            return map;
        }

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
        /// The EXPLICIT, authorable attack-delivery mode (Story 3.12) — <c>"Hitscan"</c> | <c>"Projectile"</c>,
        /// decoupled from <see cref="AttackRange"/>. Resolved to <see cref="ProjectChimera.Combat.AttackDelivery"/>
        /// via <see cref="ResolveDelivery"/> and written to <c>EntityWorld.Delivery</c> in
        /// <see cref="ProjectChimera.Core.EntityWorld.ApplyUnitDefinition"/> (the single def→SoA mapper).
        ///
        /// <para>Nullable, default <b>null</b> (NOT <c>"Hitscan"</c>): a flat Hitscan default would silently turn every
        /// shipped ranged unit (archer, mage, siege, …) into a hitscan attacker. Instead, when <c>delivery</c> is
        /// omitted/null (or an unknown string), <see cref="ResolveDelivery"/> falls back to the EXACT old range
        /// inference — <c>quantizedRange &gt; </c><see cref="LegacyDeliveryThreshold"/> ⇒ Projectile, else Hitscan —
        /// the identical Fixed comparison the deleted <c>CombatSystem.MELEE_THRESHOLD</c> used, so the partition is
        /// byte-identical for all existing data. Loaded through the LENIENT faction loader, so it MUST be
        /// <c>string?</c> (an enum-typed field would throw on that path); an unknown token is rejected fail-closed by
        /// <see cref="UnitDefinitionValidator"/> (the accessor/validator split), NOT silently by
        /// <see cref="ResolveDelivery"/>.</para>
        /// </summary>
        [JsonPropertyName("delivery")]
        public string? Delivery { get; set; }

        /// <summary>
        /// Optional per-unit projectile speed in world units/second (Story 3.12) — applied only when the unit's
        /// effective delivery is <see cref="ProjectChimera.Combat.AttackDelivery.Projectile"/>. Authored float,
        /// quantized ONCE to <see cref="ProjectChimera.Core.Fixed"/> in
        /// <see cref="ProjectChimera.Core.EntityWorld.ApplyUnitDefinition"/> (the single float→Fixed boundary, like the
        /// other stats) and stored per-entity in <c>EntityWorld.ProjectileSpeed</c>. Default <b>18</b> == the old
        /// hardcoded global (<c>ProjectileSystem.PROJECTILE_SPEED</c>), so omitting it leaves every existing ranged
        /// unit's projectiles flying at the exact same speed (and <c>FactionWriter</c> omits the key on round-trip).
        /// </summary>
        [JsonPropertyName("projectile_speed")]
        public float ProjectileSpeed { get; set; } = 18f;

        /// <summary>
        /// The XP bounty (Story 3.13, D7) this unit awards to every hostile hero within that hero's
        /// <c>xp_share_radius</c> when it dies. Nullable <c>int?</c>, default <b>null</b> (omitted) — resolved via
        /// <see cref="ResolveXpBounty"/> to the authored value if set, else the derived default
        /// <c>CostOre + CostCrystal</c> (both int gold-equivalents). Written to the per-entity
        /// <c>EntityWorld.XpBounty</c> SoA (quantized to <see cref="ProjectChimera.Core.Fixed"/>) through the single
        /// <see cref="ProjectChimera.Core.EntityWorld.ApplyUnitDefinition"/> mapper, folded into
        /// <see cref="ProjectChimera.Core.SimChecksum"/> (v11). A non-finite/out-of-range authored value is rejected
        /// fail-closed by <see cref="UnitDefinitionValidator"/> (AR-39).
        /// </summary>
        [JsonPropertyName("xp_bounty")]
        public int? XpBounty { get; set; }

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
        /// Role/AI <b>behavior</b> ids this unit composes (each references a behavior JSON in
        /// <c>resources/data/behaviors/</c>, indexed by <see cref="BehaviorRegistry"/>). The orthogonal composition axis
        /// (Story 3.6): a "healer" = Ranged archetype + a heal ability + a <c>support</c> behavior — never a subclass.
        /// Mirrors <see cref="Abilities"/> — a snake_case JSON string array, empty = no behaviors. Purely AUTHORING DATA:
        /// no sim system resolves or consumes it yet (D-2 — no <c>Parsed*</c>/<c>Resolve</c>/<c>[JsonIgnore]</c> index and
        /// no SoA fold), so an unread <c>behaviors</c> field moves no golden and no checksum. The
        /// <see cref="UnitDefinitionValidator"/> rejects an undefined ref or an archetype-incompatible behavior at
        /// authoring time (AC2).
        /// </summary>
        [JsonPropertyName("behaviors")]
        public string[] Behaviors { get; set; } = System.Array.Empty<string>();

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
        /// Marks this (Structure-category) building as able to REVIVE fallen heroes (Story 3.14). A creator-authored
        /// capability toggle: a building with this flag offers a revive affordance on its command card for any of its
        /// faction's awaiting heroes, and the revive order executes at it. Only valid on a Structure unit — the
        /// <see cref="UnitDefinitionValidator"/> fails closed with a located error otherwise (the <see cref="IsHero"/>
        /// coherence precedent). A plain settable bool auto-prop, default false — lenient-loader-safe (an omitted key
        /// stays false, so no existing building gains the capability by omission and no faction JSON changes). NOT
        /// combat/checksum state: threaded to <see cref="ProjectChimera.Core.BuildingStore.RevivesHeroes"/> at placement,
        /// which is a non-folded placement constant.
        /// </summary>
        [JsonPropertyName("revives_heroes")]
        public bool RevivesHeroes { get; set; } = false;

        /// <summary>
        /// Marks this (Structure-category) building as an item SHOP (Story 3.16) — mirrors <see cref="RevivesHeroes"/>.
        /// A creator-authored capability toggle: a shop building offers a per-item Buy affordance on its command card for
        /// a selected owned hero within <see cref="ShopRadius"/>, and the buy order executes at it. Only valid on a
        /// Structure unit — the <see cref="UnitDefinitionValidator"/> fails closed otherwise (the <see cref="RevivesHeroes"/>
        /// precedent). Default false — an omitted key stays false so no existing building becomes a shop and no faction
        /// JSON changes. NOT combat/checksum state: threaded to <see cref="ProjectChimera.Core.BuildingStore.SellsItems"/>
        /// at placement (a non-folded placement constant).
        /// </summary>
        [JsonPropertyName("sells_items")]
        public bool SellsItems { get; set; } = false;

        /// <summary>
        /// The authored item-definition ids this shop stocks (Story 3.16). Each entry names an <c>ItemDefinition.Id</c>
        /// resolvable in the loaded <c>ItemRegistry</c>; a dangling id is a located <see cref="UnitDefinitionValidator"/>
        /// error. Resolved to the per-building stock at placement. Null/empty ⇒ the shop stocks nothing. Only meaningful
        /// when <see cref="SellsItems"/> is set (the validator gates the trio to Structure).
        /// </summary>
        [JsonPropertyName("shop_stock")]
        public string[]? ShopStock { get; set; }

        /// <summary>
        /// The world-unit radius within which an owned hero may buy from this shop (Story 3.16). An AUTHORING float
        /// (like every other <see cref="UnitDefinition"/> stat — the POCO carries no <see cref="ProjectChimera.Core.Fixed"/>,
        /// the lenient faction loader has no <c>FixedJsonConverter</c>); quantized to <see cref="ProjectChimera.Core.Fixed"/>
        /// once at placement into <c>BuildingStore.ShopRadius</c>, never in a tick. 0 (default) ⇒ a fallback radius is used
        /// at buy time. The proximity check runs in-sim (anti-cheat), not just as a UI gate.
        /// </summary>
        [JsonPropertyName("shop_radius")]
        public float ShopRadius { get; set; } = 0f;

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
        /// The authored HERO definition (Story 3.7, JSON <c>hero</c>) — the leveling curve, XP-gain rule, and
        /// signature/ultimate ability slots the Promote-to-Hero switch reveals. Null on a non-hero unit; instantiated
        /// with the Standard leveling preset when the creator promotes. Coupled to <see cref="IsHero"/> by the validator
        /// (a hero MUST have this block and vice-versa), but ORTHOGONAL to it: <see cref="IsHero"/> stays the spawn-time
        /// designation Story 3.2 reads to mint a <see cref="ProjectChimera.Core.HeroStore"/> row; this block is the
        /// definition data Story 3.13 consumes at runtime. Purely AUTHORING DATA like <see cref="Behaviors"/> /
        /// <see cref="CombatFeedback"/> (D-2): no <c>Parsed*</c>/<c>Resolve</c>/<c>[JsonIgnore]</c> index, no SoA fold —
        /// nothing reads it this story, so an unread nullable POCO moves no golden and no checksum. Nullable ⇒ omittable
        /// ⇒ existing faction JSON is unaffected (non-hero units carry no <c>hero</c> block).
        /// </summary>
        [JsonPropertyName("hero")]
        public HeroDefinition? Hero { get; set; }

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

        /// <summary>
        /// The legacy delivery threshold (Story 3.12) — the EXACT Fixed value the deleted
        /// <c>CombatSystem.MELEE_THRESHOLD</c> used (<c>Fixed.FromFloat(2.5f)</c>). A quantized attack range STRICTLY
        /// GREATER than this inferred a ranged/projectile attacker under the old range-based rule. Kept here (not in
        /// CombatSystem) because <see cref="ResolveDelivery"/> is the one place that inference now lives. A named
        /// <c>static readonly Fixed</c> so the determinism analyzer's CHM0004 magic-number advisory stays clean.
        /// </summary>
        public static readonly Fixed LegacyDeliveryThreshold = Fixed.FromFloat(2.5f);

        /// <summary>
        /// Resolve the authored <c>delivery</c> string to <see cref="ProjectChimera.Combat.AttackDelivery"/> (Story
        /// 3.12). An explicit <c>"Hitscan"</c>/<c>"Projectile"</c> wins; a null OR unknown string FAILS OPEN to the
        /// exact old range inference (<paramref name="quantizedRange"/> &gt; <see cref="LegacyDeliveryThreshold"/> ⇒
        /// Projectile, else Hitscan) — so legacy data with no <c>delivery</c> key keeps its byte-identical behaviour
        /// (the deleted MELEE_THRESHOLD partition). The lenient fail-open mirrors <see cref="ParsedTags"/>; the
        /// fail-closed REJECT of an invalid string is <see cref="UnitDefinitionValidator"/>'s job (the accessor/
        /// validator split). Pass the SoA-quantized range (<c>EntityWorld.AttackRange[id]</c>) so the inference matches
        /// the value combat actually reads.
        /// </summary>
        public Combat.AttackDelivery ResolveDelivery(Fixed quantizedRange) => Delivery switch
        {
            "Hitscan"    => Combat.AttackDelivery.Hitscan,
            "Projectile" => Combat.AttackDelivery.Projectile,
            _            => quantizedRange > LegacyDeliveryThreshold
                                ? Combat.AttackDelivery.Projectile
                                : Combat.AttackDelivery.Hitscan,
        };

        /// <summary>
        /// Editor convenience (Story 3.12): the effective delivery as the canonical <c>"Hitscan"</c>/<c>"Projectile"</c>
        /// string, resolving a null/unknown authored value through the SAME <see cref="ResolveDelivery"/> inference over
        /// this def's authored <see cref="AttackRange"/>. The Unit Card Editor displays this so an unauthored ranged unit
        /// still shows "Projectile", and drives the conditional <c>projectile_speed</c> row's visibility.
        /// </summary>
        public string EffectiveDeliveryString() =>
            ResolveDelivery(Fixed.FromFloat(AttackRange)) == Combat.AttackDelivery.Projectile ? "Projectile" : "Hitscan";

        /// <summary>The largest XP bounty representable as a 16.16 <see cref="ProjectChimera.Core.Fixed"/> via
        /// <c>FromInt</c> (<c>32768 &lt;&lt; 16</c> overflows int32). The authored <see cref="XpBounty"/> is validator-bounded
        /// to this range, but the DERIVED default (a cost SUM, each cost only individually bounded) can exceed it, so the
        /// resolved value is clamped here — a very expensive unit simply awards the max bounty rather than overflowing to
        /// a negative Fixed. Mirrors the authored-field fail-closed invariant (AR-39).</summary>
        public const int XpBountyMax = 32767;

        /// <summary>
        /// Resolve the XP bounty (Story 3.13, D7) this unit awards on death: the authored <see cref="XpBounty"/> if set,
        /// else the derived default <c>CostOre + CostCrystal</c>, clamped to <c>[0, <see cref="XpBountyMax"/>]</c> so the
        /// derived cost-sum can never overflow the load-boundary <c>Fixed.FromInt</c> into a negative bounty. Returns a
        /// plain <c>int</c> (gold-equivalent); the single float→<see cref="Fixed"/> quantization happens at the load
        /// boundary (<see cref="ProjectChimera.Core.EntityWorld.ApplyUnitDefinition"/>), never inside a tick.
        /// </summary>
        public int ResolveXpBounty()
        {
            int b = XpBounty ?? (CostOre + CostCrystal);
            if (b < 0) return 0;
            return b > XpBountyMax ? XpBountyMax : b;
        }

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
