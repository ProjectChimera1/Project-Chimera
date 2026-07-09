#nullable enable
using System.Collections.Generic;
using System.Text;

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// The result of a <see cref="UnitDefinitionValidator"/> pass (Story 3.4, AR-39 / UX-DR55) — pure, no logging, no
    /// throw. Deliberately diverges from <see cref="AbilityValidationResult"/> / <see cref="ValidationResult"/>'s
    /// FIRST-FAIL single-<c>Error</c> shape: it returns a LIST of located <c>(FieldPath, Message)</c> errors so the Unit
    /// Card Editor can badge <b>every</b> offending field at once (the per-field-badge UX, D-9). <see cref="FieldPath"/>
    /// is the offending field's JSON key (e.g. <c>"cost_crystal"</c>, <c>"abilities"</c>) so the panel can map an error
    /// to the control that authored it; <see cref="string"/> Message is the full located sentence
    /// (<c>"unit '&lt;id&gt;'.&lt;path&gt;: &lt;reason&gt;"</c>). Godot-free (<c>src/Core/Definitions</c>) so it runs in
    /// the Tier-1 test assembly and the sim layer.
    /// </summary>
    public readonly struct UnitValidationResult
    {
        /// <summary>True when the unit passed every check (no errors).</summary>
        public bool Ok => Errors.Count == 0;

        /// <summary>Every located field error found (NOT just the first — D-9). Empty when the unit is valid.</summary>
        public IReadOnlyList<(string FieldPath, string Message)> Errors { get; }

        internal UnitValidationResult(IReadOnlyList<(string, string)> errors) => Errors = errors;

        /// <summary>The always-valid result (no errors) — a shared empty instance.</summary>
        public static readonly UnitValidationResult Valid =
            new UnitValidationResult(System.Array.Empty<(string, string)>());
    }

    /// <summary>
    /// The first fail-closed content validator for authored <see cref="UnitDefinition"/>s (Story 3.4, AR-39). Units are
    /// NEVER content-validated today — <see cref="UnitTagValidator"/> states it verbatim, and the lenient faction loader
    /// silently fail-opens bad enums (an unknown <c>category</c> parses to <c>Melee</c>, etc.). 3.4 closes that: the Unit
    /// Card Editor runs this gate on Save/Playtest and refuses to persist a unit with an out-of-range / negative /
    /// invalid value, badging each offending field (UX-DR55). It closes a real defect class parked since 1.3b/2.9b (a
    /// negative <c>cost_crystal</c> ADDS crystal each train) and the Fixed-16.16 ≥32768 overflow (epic-2-retro D-2).
    ///
    /// <para><b>Design (D-9).</b> Returns ALL located field errors, not the first (a deliberate divergence from
    /// <see cref="ScenarioValidator"/>/<see cref="AbilityValidator"/>'s first-fail shape) so every bad field badges at
    /// once. It does NOT mint <see cref="Validated{T}"/> — no applier consumes such a token; like
    /// <see cref="UnitTagValidator"/> this is a lightweight authoring-time gate, so the sole-minter allow-list is
    /// untouched. The one rule that needs Godot — <c>mesh_path</c> resolvability via <c>ResourceLoader.Exists</c> — is a
    /// thin presentation-side check the panel layers on top (this validator does not touch it).</para>
    ///
    /// <para><b>Determinism.</b> Pure C#, Godot-free, no <c>float</c> gameplay math — it reads authoring floats and
    /// reports strings; it touches no sim array and moves no checksum (the 3.4 pure-authoring-time posture).</para>
    ///
    /// <para><b>Story 4.5.</b> The terminal 5-arg <see cref="Validate(UnitDefinition,AbilityRegistry?,BehaviorRegistry?,ItemRegistry?,IReadOnlyList{UnitDefinition}?,string)"/>
    /// overload accepts a <c>kind</c> parameter (default <c>"unit"</c>) threaded into every <see cref="Located"/>
    /// message, so <see cref="BuildingDefinitionValidator"/> can reuse this whole gate — id/dup-id/enum/cost-range
    /// checks included — over a <see cref="BuildingDefinition"/> list with an accurate <c>"building '&lt;id&gt;'…"</c>
    /// message instead of duplicating ~20 rules. Every pre-4.5 call site omits <c>kind</c> and is therefore unaffected
    /// (the default keeps every existing "unit '&lt;id&gt;'…" message byte-identical).</para>
    /// </summary>
    public sealed class UnitDefinitionValidator
    {
        /// <summary>The 16.16 representable ceiling (mirrors <see cref="ScenarioValidator"/>'s <c>Range</c> and
        /// <c>FixedJsonConverter.FixedRangeLimit</c>). A stat ≥ this overflows the single <c>float→Fixed</c> quantize at
        /// spawn (<see cref="ProjectChimera.Core.EntityWorld.ApplyUnitDefinition"/>) — deferred-work #2.</summary>
        private const float Range = 32768f;

        /// <summary>Minimum authorable hero <c>max_level</c> (Story 3.7) — a level-1 "hero" cannot level, so 2 is the
        /// floor. See <see cref="HeroLevelMax"/>.</summary>
        private const int HeroLevelMin = 2;

        /// <summary>Maximum authorable hero <c>max_level</c> (Story 3.7). A sane ceiling well below the int/Fixed
        /// bounds — a creator wanting more edits the raw JSON and re-tests balance.</summary>
        private const int HeroLevelMax = 100;

        /// <summary>Exclusive upper bound on hero <c>xp_growth</c> (Story 3.7). A per-level geometric multiplier this
        /// large already makes the top level unreachable; the floor is 1 (no shrink).</summary>
        private const float HeroGrowthCap = 100f;

        /// <summary>Exclusive upper bound on hero <c>xp_share_radius</c> (Story 3.13). Tighter than the generic
        /// <see cref="Range"/>: <see cref="ProjectChimera.Combat.HeroXpSystem"/> compares squared distances, so a legal
        /// radius must satisfy <c>r*r &lt; 32768</c> in 16.16 <see cref="ProjectChimera.Core.Fixed"/> (r &lt; ~181) or the
        /// range test overflows and inverts. 128 is well inside that AND far larger than any authored attack range.</summary>
        private const float HeroShareRadiusMax = 128f;

        /// <summary>Exclusive upper bound on each hero <c>*_per_level</c> growth delta (Story 3.13). Tighter than the
        /// generic <see cref="Range"/>: growth is applied as up to <c>HeroLevelMax-1</c> (99) stacks summed into an
        /// <c>Effective*</c> stat, so a legal delta must satisfy <c>99 * delta &lt; 32768</c> (delta &lt; ~331) or the
        /// stat overflows. 256 keeps the summed growth in range with margin AND dwarfs any realistic per-level gain.</summary>
        private const float HeroStatGrowthMax = 256f;

        // The closed authorable sets, mirroring the string switches in UnitDefinition's Parsed* getters + the enum
        // members. Static → allocated once (the ScenarioValidator closed-set idiom), so the per-unit scan allocates
        // nothing. Case-sensitive exact match (an authored "melee" ≠ "Melee"; the lenient loader would fail-open it).
        private static readonly string[] _categories = { "Worker", "Melee", "Ranged", "Siege", "Air", "Structure" };
        private static readonly string[] _damageTypes = { "Normal", "Pierce", "Siege", "Magic", "Hero" };
        private static readonly string[] _armorTypes = { "Unarmored", "Light", "Medium", "Heavy", "Fortified", "Hero" };
        private static readonly string[] _separationPriorities = { "Yield", "Normal", "Push" };
        /// <summary>The closed authorable attack-delivery set (Story 3.12), mirroring the <c>AttackDelivery</c> enum + the
        /// <c>UnitDefinition.ResolveDelivery</c> string switch. A null <c>delivery</c> is legal (legacy range inference).</summary>
        private static readonly string[] _deliveries = { "Hitscan", "Projectile" };


        /// <summary>
        /// Validate a <paramref name="def"/> against its <paramref name="siblings"/> (the faction's <c>Units</c> list,
        /// for the uniqueness rule) and the loaded <paramref name="registry"/> (for undefined-ability refs). Returns a
        /// <see cref="UnitValidationResult"/> carrying EVERY located field error (D-9). Pure — never throws, never logs.
        /// A null <paramref name="registry"/> skips the ability-reference check (the caller has no registry to validate
        /// against); a real empty registry still rejects any ability ref (fail-closed).
        /// </summary>
        public UnitValidationResult Validate(
            UnitDefinition def,
            AbilityRegistry? registry,
            IReadOnlyList<UnitDefinition>? siblings)
            => Validate(def, registry, null, siblings);

        /// <summary>
        /// Story 3.6 overload: additionally validate the composed <c>behaviors[]</c> against
        /// <paramref name="behaviorRegistry"/> (undefined ref + archetype-incompatible). A null
        /// <paramref name="behaviorRegistry"/> SKIPS the behavior checks (mirrors the ability-null guard) so existing
        /// callers/tests that pass no registry compile + behave unchanged; only the editor supplies it. Every rule of the
        /// base overload still runs.
        /// </summary>
        public UnitValidationResult Validate(
            UnitDefinition def,
            AbilityRegistry? registry,
            BehaviorRegistry? behaviorRegistry,
            IReadOnlyList<UnitDefinition>? siblings)
            => Validate(def, registry, behaviorRegistry, null, siblings);

        /// <summary>
        /// Story 3.16 overload: additionally validate the shop <c>shop_stock[]</c> ids against
        /// <paramref name="itemRegistry"/> (a dangling id is a located error). A null <paramref name="itemRegistry"/>
        /// SKIPS the stock-ref check (mirrors the ability-null guard) so existing callers/tests that pass no registry
        /// compile + behave unchanged; only the item editor / item-aware faction load supplies it. Every base rule still
        /// runs, including the Structure-gating of the shop trio (which needs no registry).
        ///
        /// <para>Story 4.5: <paramref name="kind"/> defaults to <c>"unit"</c> so every pre-4.5 call site (all of which
        /// omit it) is unaffected; <see cref="BuildingDefinitionValidator"/> passes <c>"building"</c> so every located
        /// message reads <c>"building '&lt;id&gt;'…"</c> instead of <c>"unit '&lt;id&gt;'…"</c>.</para>
        /// </summary>
        public UnitValidationResult Validate(
            UnitDefinition def,
            AbilityRegistry? registry,
            BehaviorRegistry? behaviorRegistry,
            ItemRegistry? itemRegistry,
            IReadOnlyList<UnitDefinition>? siblings,
            string kind = "unit")
        {
            var errors = new List<(string, string)>();
            if (def is null)
            {
                errors.Add(("unit", "unit is null."));
                return new UnitValidationResult(errors);
            }

            string id = def.Id ?? "";

            // ── id: non-empty, sanitized (filename/render-slot safe), unique among sibling Units (D-7) ──
            if (string.IsNullOrEmpty(id))
            {
                errors.Add(("id", Located(kind, id, "id", "must be a non-empty id.")));
            }
            else
            {
                if (SanitizeId(id) != id)
                    errors.Add(("id", Located(kind, id, "id",
                        "contains characters outside [a-z0-9_]; rename before saving.")));
                if (siblings != null && IsDuplicateId(def, id, siblings))
                    errors.Add(("id", Located(kind, id, "id",
                        "is a duplicate — another unit in this faction already uses this id.")));
            }

            // ── enums: fail-closed on any value the loader would silently fail-open (AC2 "invalid archetype/category") ──
            if (!InSet(_categories, def.Category))
                errors.Add(("category", Located(kind, id, "category",
                    $"'{def.Category}' is not a known archetype (Worker|Melee|Ranged|Siege|Air|Structure).")));
            if (!InSet(_damageTypes, def.DamageType))
                errors.Add(("damage_type", Located(kind, id, "damage_type",
                    $"'{def.DamageType}' is not a known damage type (Normal|Pierce|Siege|Magic|Hero).")));
            if (!InSet(_armorTypes, def.ArmorType))
                errors.Add(("armor_type", Located(kind, id, "armor_type",
                    $"'{def.ArmorType}' is not a known armor type (Unarmored|Light|Medium|Heavy|Fortified|Hero).")));
            if (!InSet(_separationPriorities, def.SeparationPriority))
                errors.Add(("separation_priority", Located(kind, id, "separation_priority",
                    $"'{def.SeparationPriority}' is not a known separation priority (Yield|Normal|Push).")));

            // ── delivery: nullable — null is the legacy range-inference default (valid); a non-null value must be in
            //    the closed set. The fail-closed reject the ResolveDelivery accessor fails OPEN on (Story 3.12, AC4). ──
            if (def.Delivery != null && !InSet(_deliveries, def.Delivery))
                errors.Add(("delivery", Located(kind, id, "delivery",
                    $"'{def.Delivery}' is not a known delivery (Hitscan|Projectile).")));

            // ── projectile_speed — TWO rules. (1) FINITE & in [0, 32768) for EVERY unit regardless of delivery: it
            //    is quantized (Fixed.FromFloat) and FOLDED into SimChecksum (v10) for all entities, so a NaN/Inf/out-
            //    of-range value must never reach the deterministic hash — the same [0, Range) invariant CheckStat
            //    enforces on every other folded numeric stat (a Hitscan unit ignores the value at combat, but the fold
            //    is unconditional, so the validation must be too). (2) STRICTLY-POSITIVE additionally when this unit's
            //    EFFECTIVE delivery is Projectile — an authored projectile OR a legacy unit that OMITS delivery (null)
            //    but has attack_range > 2.5 (infers Projectile) actually travels at this speed, so 0 is invalid there.
            //    Gate rule (2) on the RESOLVED delivery, not the literal string; an omitted speed defaults to 18 (valid). ──
            if (!float.IsFinite(def.ProjectileSpeed) || def.ProjectileSpeed < 0f || def.ProjectileSpeed >= Range)
                errors.Add(("projectile_speed", Located(kind, id, "projectile_speed",
                    $"={def.ProjectileSpeed} must be finite and in [0, {(int)Range}) (it is folded into the deterministic checksum).")));
            else if (def.EffectiveDeliveryString() == "Projectile" && def.ProjectileSpeed <= 0f)
                errors.Add(("projectile_speed", Located(kind, id, "projectile_speed",
                    $"={def.ProjectileSpeed} must be strictly positive for a Projectile-delivery unit (authored or inferred from range).")));

            // ── numeric stats: finite & [0, 32768) — the 16.16 Fixed ceiling (AC2 "out-of-range/missing stat") ──
            CheckStat(errors, kind, id, "hp", def.Hp);
            CheckStat(errors, kind, id, "speed", def.Speed);
            CheckStat(errors, kind, id, "attack_damage", def.AttackDamage);
            CheckStat(errors, kind, id, "attack_range", def.AttackRange);
            CheckStat(errors, kind, id, "attack_speed", def.AttackSpeed);
            CheckStat(errors, kind, id, "armor", def.Armor);
            CheckStat(errors, kind, id, "splash_radius", def.SplashRadius);
            CheckStat(errors, kind, id, "collision_radius", def.CollisionRadius);
            CheckStat(errors, kind, id, "mesh_scale", def.MeshScale);
            CheckStat(errors, kind, id, "max_energy", def.MaxEnergy);
            CheckStat(errors, kind, id, "vision_range", def.VisionRange);
            CheckStat(errors, kind, id, "train_time", def.TrainTime);

            // ── supply: an int count, but the same [0, 32768) bound (AC2 "(+ supply)") ──
            CheckIntBound(errors, kind, id, "supply", def.Supply);

            // ── costs: ≥ 0 (a negative cost ADDS resource each train — the parked 1.3b/2.9b defect) AND < 32768 (the
            //    resource bound the epic-2-retro homed here). One error per cost — the negative case wins the message. ──
            CheckCost(errors, kind, id, "cost_ore", def.CostOre);
            CheckCost(errors, kind, id, "cost_crystal", def.CostCrystal);

            // ── cost (Story 4.5): the sparse authored resource map — each key must be a known resource id and each
            //    value the same [0, 32768) range rule as the legacy cost_ore/cost_crystal fields above. Skips
            //    entirely when unauthored (null) — the legacy fields already cover that case. ──
            CheckCostMap(errors, kind, id, def.Cost);

            // ── xp_bounty (Story 3.13): when AUTHORED, an int in [0, 32768) — it is quantized to Fixed + folded into
            //    SimChecksum (v11), so it must satisfy the same [0, Range) invariant every other folded stat has.
            //    Omitted (null) ⇒ derived from cost_ore+cost_crystal (already cost-validated) ⇒ always valid. ──
            if (def.XpBounty.HasValue)
                CheckIntBound(errors, kind, id, "xp_bounty", def.XpBounty.Value);

            // ── every abilities[] id must resolve in the registry (AC2 "undefined ability reference") ──
            string[]? abilities = def.Abilities;
            if (abilities != null && registry != null)
            {
                for (int i = 0; i < abilities.Length; i++)
                {
                    string aid = abilities[i] ?? "";
                    if (registry.IndexOf(aid) < 0)
                        errors.Add(("abilities", Located(kind, id, $"abilities[{i}]",
                            $"'{aid}' is not a defined ability (no matching ability in the loaded set).")));
                }
            }

            // ── behaviors: each ref must resolve AND be compatible with this unit's archetype (Story 3.6, AC2) ──
            string[]? behaviors = def.Behaviors;
            if (behaviors != null && behaviorRegistry != null)
            {
                for (int i = 0; i < behaviors.Length; i++)
                {
                    string bid = behaviors[i] ?? "";
                    int idx = behaviorRegistry.IndexOf(bid);
                    if (idx < 0)
                    {
                        errors.Add(("behaviors", Located(kind, id, $"behaviors[{i}]",
                            $"'{bid}' is not a defined behavior (no matching behavior in the loaded set).")));
                    }
                    else if (!behaviorRegistry.Get(idx).IsCompatibleWith(def.Category))
                    {
                        errors.Add(("behaviors", Located(kind, id, $"behaviors[{i}]",
                            $"behavior '{bid}' is not compatible with the {def.Category} archetype.")));
                    }
                }
            }

            // ── hero: is_hero↔hero coherence + leveling-curve range + ability-slot refs + composition (Story 3.7, AC2) ──
            ValidateHero(errors, kind, id, def, registry);

            // ── revives_heroes: a HERO-REVIVAL capability that only makes sense on a Structure building (Story 3.14). A
            //    Worker/Melee/etc. unit can't host a revive command card, so the flag on a non-Structure unit is an
            //    authoring error — fail closed with a located badge (the is_hero-coherence precedent). Omitted (false)
            //    is always valid, so every existing unit is unaffected. ──
            if (def.RevivesHeroes && def.Category != "Structure")
                errors.Add(("revives_heroes", Located(kind, id, "revives_heroes",
                    $"is set on a {def.Category} unit — only a Structure building can revive heroes.")));

            // ── sells_items / shop_stock / shop_radius: an item-SHOP capability that only makes sense on a Structure
            //    building (Story 3.16, mirroring revives_heroes). The trio is Structure-gated; a non-empty stock/radius on
            //    a non-shop unit is an authoring error; and each shop_stock id must resolve in the loaded ItemRegistry
            //    (a dangling id fails closed). Omitted (false/null/0) is always valid, so every existing unit is unaffected. ──
            bool hasStock  = def.ShopStock != null && def.ShopStock.Length > 0;
            bool hasRadius = def.ShopRadius != 0f;
            if ((def.SellsItems || hasStock || hasRadius) && def.Category != "Structure")
                errors.Add(("sells_items", Located(kind, id, "sells_items",
                    $"is set on a {def.Category} unit — only a Structure building can sell items.")));
            if (!float.IsFinite(def.ShopRadius) || def.ShopRadius < 0f || def.ShopRadius >= Range)
                errors.Add(("shop_radius", Located(kind, id, "shop_radius",
                    $"={def.ShopRadius} must be finite and in [0, {(int)Range}).")));
            if (def.ShopStock != null)
            {
                for (int i = 0; i < def.ShopStock.Length; i++)
                {
                    string sid = def.ShopStock[i] ?? "";
                    if (string.IsNullOrEmpty(sid))
                        errors.Add(("shop_stock", Located(kind, id, $"shop_stock[{i}]", "is an empty item id.")));
                    else if (itemRegistry != null && itemRegistry.IndexOf(sid) < 0)
                        errors.Add(("shop_stock", Located(kind, id, $"shop_stock[{i}]",
                            $"'{sid}' is not a defined item (no matching item in the loaded set).")));
                }
            }

            // ── tags: closed set — compose the existing UnitTagValidator so the two axes agree (AC2 "unknown tag") ──
            if (UnitTagValidator.TryFindInvalidTag(def, out string? badTag))
                errors.Add(("tags", UnitTagValidator.Located(id, badTag)));

            return new UnitValidationResult(errors);
        }

        // ── Rule helpers ─────────────────────────────────────────────────────────

        /// <summary>Finite &amp; in [0, 32768) — the float stat rule. Appends a located error when it fails.</summary>
        private static void CheckStat(List<(string, string)> errors, string kind, string id, string path, float v)
        {
            // Interpolate the value directly (the ScenarioValidator idiom) — an error string is display-only, never a
            // checksum input, so its number format is determinism-irrelevant; avoids the explicit float.ToString.
            if (!float.IsFinite(v) || v < 0f || v >= Range)
                errors.Add((path, Located(kind, id, path, $"={v} must be finite and in [0, {(int)Range}).")));
        }

        /// <summary>Finite &amp; in [0, <paramref name="max"/>) — a float stat with a tighter-than-<see cref="Range"/> ceiling
        /// (Story 3.13 hero runtime fields whose downstream squaring/stacking would overflow Fixed at the generic Range).</summary>
        private static void CheckStatMax(List<(string, string)> errors, string kind, string id, string path, float v, float max)
        {
            if (!float.IsFinite(v) || v < 0f || v >= max)
                errors.Add((path, Located(kind, id, path, $"={v} must be finite and in [0, {(int)max}).")));
        }

        /// <summary>An int stat bounded to [0, 32768) (supply).</summary>
        private static void CheckIntBound(List<(string, string)> errors, string kind, string id, string path, int v)
        {
            if (v < 0 || v >= (int)Range)
                errors.Add((path, Located(kind, id, path, $"={v} must be in [0, {(int)Range}).")));
        }

        /// <summary>A resource cost: ≥ 0 (negative-cost defect) and &lt; 32768 (resource bound). One error max.</summary>
        private static void CheckCost(List<(string, string)> errors, string kind, string id, string path, int v)
        {
            if (v < 0)
                errors.Add((path, Located(kind, id, path,
                    $"={v} must be >= 0 (a negative cost ADDS that resource each time the unit is trained).")));
            else if (v >= (int)Range)
                errors.Add((path, Located(kind, id, path, $"={v} exceeds the maximum resource cost ({(int)Range}).")));
        }

        /// <summary>
        /// Story 4.5: the authored sparse <c>cost</c> map (Story 4.3) — a per-field editor check that was missing even
        /// for units (only the whole-faction <see cref="ResourceCostValidator"/> covered it, with no badge target). For
        /// each authored <c>(key,value)</c> pair (skipped entirely when <paramref name="cost"/> is null — unauthored,
        /// the legacy <c>cost_ore</c>/<c>cost_crystal</c> fields already cover that case): a key outside
        /// <c>{"ore","crystal"}</c> is a located unknown-resource-id error (mirrors
        /// <see cref="ResourceCostValidator"/>'s <c>ValidateEntry</c> message); a known key's value is range-checked
        /// exactly like <see cref="CheckCost"/> (&gt;= 0 and &lt; 32768). Every error is keyed <c>"cost"</c> so the
        /// editor's single cost-map control receives every offending entry's badge.
        /// </summary>
        private static void CheckCostMap(List<(string, string)> errors, string kind, string id, Dictionary<string, int>? cost)
        {
            if (cost == null) return;   // unauthored — the legacy cost_ore/cost_crystal fields already validated above

            foreach (var (key, value) in cost)
            {
                if (!InSet(ResourceCostValidator.KnownResourceIds, key))
                {
                    errors.Add(("cost", Located(kind, id, "cost",
                        $"references unknown resource id '{key}' (no runtime resource registered for it yet).")));
                    continue;   // an unknown key's value is meaningless to range-check
                }

                if (value < 0)
                    errors.Add(("cost", Located(kind, id, "cost",
                        $"['{key}']={value} must be >= 0 (a negative cost ADDS that resource each time it is spent).")));
                else if (value >= (int)Range)
                    errors.Add(("cost", Located(kind, id, "cost",
                        $"['{key}']={value} exceeds the maximum resource cost ({(int)Range}).")));
            }
        }

        /// <summary>
        /// The Story 3.7 hero rules (multi-error, D-9): (1) <c>is_hero</c>↔<c>hero</c> coherence — a hero MUST carry a
        /// <c>hero</c> block and a <c>hero</c> block MUST have <c>is_hero:true</c> (fail-closed on either mismatch);
        /// (2) the leveling curve in range; (3) each SET signature/ultimate ability ref must resolve in the registry
        /// (skipped when <paramref name="registry"/> is null, mirroring the ability guard — an EMPTY slot is "not
        /// authored yet" and always valid); (4) signature ≠ ultimate when both are set. A non-hero unit (Hero null,
        /// IsHero false) adds no hero errors.
        /// </summary>
        private static void ValidateHero(List<(string, string)> errors, string kind, string id, UnitDefinition def, AbilityRegistry? registry)
        {
            HeroDefinition? h = def.Hero;

            // Coherence: the flag and the block must agree. Report on `is_hero` and stop (the curve/slot rules below
            // only make sense once the two are consistent).
            if (def.IsHero && h == null)
            {
                errors.Add(("is_hero", Located(kind, id, "is_hero",
                    "is a hero (is_hero:true) but has no 'hero' block — author its leveling/abilities or turn the hero flag off.")));
                return;
            }
            if (!def.IsHero && h != null)
            {
                errors.Add(("is_hero", Located(kind, id, "is_hero",
                    "has a 'hero' block but is not marked is_hero:true — set is_hero:true or remove the 'hero' block.")));
                return;
            }
            if (h == null) return;   // non-hero unit — no hero rules apply

            // Leveling curve — each field on its own located key.
            if (h.MaxLevel < HeroLevelMin || h.MaxLevel > HeroLevelMax)
                errors.Add(("hero.max_level", Located(kind, id, "hero.max_level",
                    $"={h.MaxLevel} must be in [{HeroLevelMin}, {HeroLevelMax}].")));
            if (!float.IsFinite(h.BaseXp) || h.BaseXp <= 0f || h.BaseXp >= Range)
                errors.Add(("hero.base_xp", Located(kind, id, "hero.base_xp",
                    $"={h.BaseXp} must be finite and in (0, {(int)Range}).")));
            if (!float.IsFinite(h.XpGrowth) || h.XpGrowth < 1f || h.XpGrowth >= HeroGrowthCap)
                errors.Add(("hero.xp_growth", Located(kind, id, "hero.xp_growth",
                    $"={h.XpGrowth} must be finite and in [1, {(int)HeroGrowthCap}).")));
            if (!float.IsFinite(h.XpPerKill) || h.XpPerKill < 0f || h.XpPerKill >= Range)
                errors.Add(("hero.xp_per_kill", Located(kind, id, "hero.xp_per_kill",
                    $"={h.XpPerKill} must be finite and in [0, {(int)Range}).")));

            // Story 3.13 runtime fields — finite & fail-closed to a Fixed-SAFE range (AR-39). Quantized to Fixed at the
            // applier load boundary and consumed by HeroXpSystem (share radius, squared → r*r) / ModifierStore growth
            // stacks (per-level deltas, summed up to 99×). Their ceilings are TIGHTER than the generic Range so the
            // downstream squaring/stacking cannot overflow 16.16 Fixed (the pre-3.13 CheckStat allowed up to 32767, which
            // r*r and 99× overflow — reviewer-found).
            CheckStatMax(errors, kind, id, "hero.xp_share_radius", h.XpShareRadius, HeroShareRadiusMax);
            CheckStatMax(errors, kind, id, "hero.health_per_level", h.HealthPerLevel, HeroStatGrowthMax);
            CheckStatMax(errors, kind, id, "hero.damage_per_level", h.DamagePerLevel, HeroStatGrowthMax);
            CheckStatMax(errors, kind, id, "hero.armor_per_level", h.ArmorPerLevel, HeroStatGrowthMax);

            // Ability slots — a SET-but-undefined ref is rejected; an empty (null/"") slot is valid (not authored yet).
            // Skip the ref lookup when there is no registry to validate against (mirrors the abilities[] guard).
            string sig = h.SignatureAbility ?? "";
            string ult = h.UltimateAbility ?? "";
            if (registry != null)
            {
                if (sig.Length > 0 && registry.IndexOf(sig) < 0)
                    errors.Add(("hero.signature_ability", Located(kind, id, "hero.signature_ability",
                        $"'{sig}' is not a defined ability (no matching ability in the loaded set).")));
                if (ult.Length > 0 && registry.IndexOf(ult) < 0)
                    errors.Add(("hero.ultimate_ability", Located(kind, id, "hero.ultimate_ability",
                        $"'{ult}' is not a defined ability (no matching ability in the loaded set).")));
            }

            // Composition rule: the signature and the ultimate must differ when both are authored.
            if (sig.Length > 0 && ult.Length > 0 && sig == ult)
                errors.Add(("hero.ultimate_ability", Located(kind, id, "hero.ultimate_ability",
                    "signature and ultimate ability must differ.")));
        }

        private static bool IsDuplicateId(UnitDefinition def, string id, IReadOnlyList<UnitDefinition> siblings)
        {
            // A sibling with the same id that is NOT this exact instance = a duplicate. Reference-equality excludes the
            // unit's own entry when it is already in the list (edit path), while still catching a create/duplicate that
            // reuses an existing id (the def is a fresh instance not yet — or never — in the list).
            for (int i = 0; i < siblings.Count; i++)
            {
                UnitDefinition s = siblings[i];
                if (s != null && !ReferenceEquals(s, def) && s.Id == id) return true;
            }
            return false;
        }

        // ── Shared string helpers ──────────────────────────────────────────────────

        /// <summary>Exact-match membership in a closed set (case-sensitive; null is never a member) — the
        /// <see cref="ScenarioValidator"/>/<see cref="UnitTagValidator"/> <c>InSet</c> idiom.</summary>
        private static bool InSet(string[] set, string? value)
        {
            if (value is null) return false;
            for (int i = 0; i < set.Length; i++)
                if (set[i] == value) return true;
            return false;
        }

        /// <summary>The located error idiom — names the entity <paramref name="kind"/> ("unit" or, since Story 4.5,
        /// "building") + id + field path + reason, mirroring <see cref="UnitTagValidator.Located"/> and
        /// <c>AbilityValidator.Located</c>. Every pre-4.5 caller omits <paramref name="kind"/> at the public API
        /// surface (the terminal <see cref="Validate"/> overload defaults it to <c>"unit"</c>), so every existing
        /// message is byte-identical.</summary>
        private static string Located(string kind, string id, string path, string reason) =>
            $"{kind} '{id}'.{path}: {reason}";

        /// <summary>
        /// Filename/id sanitiser: lowercase, keep <c>[a-z0-9_]</c>, collapse everything else to <c>'_'</c>. Godot-free
        /// (pure string ops) — homed HERE (the shared validator) so the Unit Card Editor's Create/Duplicate id-minting
        /// and the save-time id guard reuse the SAME rule the validator enforces (mirrors <c>AbilityEditorPanel.SanitizeId</c>,
        /// which is presentation-private and thus unreachable from this Tier-1 gate). An empty/whitespace input → "".
        /// </summary>
        public static string SanitizeId(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            var sb = new StringBuilder(raw.Length);
            foreach (char ch in raw.Trim().ToLowerInvariant())
                sb.Append(ch is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' ? ch : '_');
            return sb.ToString();
        }
    }
}
