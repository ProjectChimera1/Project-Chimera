#nullable enable
using ProjectChimera.Effects; // EffectNode + EffectBounds (reused verbatim, the AbilityValidator posture)

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// The fail-closed static content-validator for items (Story 3.15, AR-39) — mirrors <see cref="AbilityValidator"/>.
    /// Pure C#: NEVER throws, NEVER logs; every reject returns a single LOCATED error
    /// (<c>"item '&lt;id&gt;'.&lt;path&gt;: &lt;reason&gt;"</c>). On success mints a
    /// <see cref="Validated{T}"/> via the shared <see cref="ScenarioValidator.Proof"/> token (this file is on the
    /// ValidatedSoleMinterTest allow-list, alongside ScenarioValidator + AbilityValidator).
    ///
    /// Checks: id present; <c>charges &gt;= 0</c>; each of the four modifier deltas finite &amp; within
    /// <see cref="Range"/> (the <see cref="UnitDefinitionValidator"/> 16.16 ceiling); a charged consumable
    /// (<c>charges &gt; 0</c>) must declare an effect graph and a stat item (<c>charges == 0</c>) must NOT (a dangling
    /// effect graph never fires — reject fail-closed); and if an effect graph is present it must pass
    /// <see cref="EffectBounds.Validate"/> (depth/per-Sequence caps) verbatim. Non-finite / over-range deltas were
    /// already rejected at parse by <c>FixedJsonConverter</c>; this re-guards them for in-code definitions (tests).
    /// </summary>
    public sealed class ItemDefinitionValidator
    {
        /// <summary>The 16.16 representable ceiling (mirrors <see cref="UnitDefinitionValidator"/>'s <c>Range</c>): a
        /// modifier delta at/beyond this cannot round-trip through the single <c>Fixed</c> apply without risking an
        /// Effective* overflow when stacked, so it fails closed here.</summary>
        private static readonly Fixed Range = Fixed.FromInt(32767);

        /// <summary>The per-item stat-delta magnitude cap (Story 3.15 review). A carried item's modifier delta may not
        /// exceed ±1000: with <see cref="HeroStore.INVENTORY_SLOTS"/> = 6, the worst-case carried/stacked sum
        /// (6 × 1000 = 6000) stays far below the 32767 <see cref="Fixed"/> integer ceiling, so a full inventory of
        /// extreme items can never wrap an <c>Effective*</c> stat negative. Tighter than <see cref="Range"/> and checked
        /// per delta. NOTE: the general UNSATURATED-effective-stat overflow class (an extreme unit BASE stat + level
        /// growth, NOT items) remains a pre-existing, deferred <c>ModifierSystem</c> concern — this cap only closes the
        /// item-contributed portion.</summary>
        public static readonly Fixed MAX_ITEM_STAT_DELTA = Fixed.FromInt(1000);

        /// <summary>Validate an <see cref="ItemDefinition"/>. Returns <see cref="ItemValidationResult.Pass"/> with a
        /// minted <see cref="Validated{T}"/> on success, or <see cref="ItemValidationResult.Fail"/> with a single
        /// located error on the FIRST failed check. Pure — never throws, never logs.</summary>
        public ItemValidationResult Validate(ItemDefinition? def)
        {
            if (def is null) return ItemValidationResult.Fail("item is null.");

            string id = def.Id ?? "";

            // ── (a) Identity ──
            if (string.IsNullOrEmpty(id))
                return ItemValidationResult.Fail("item.id is null or empty.");

            // ── (b) Charges sign ──
            if (def.Charges < 0)
                return Fail(id, "charges", $"={def.Charges} must be >= 0 (0 = a stat item; >0 = a consumable).");

            // ── (c) Modifier deltas within the representable range (finite already guaranteed by FixedJsonConverter
            //        at parse; re-guarded here for in-code definitions). ──
            string? deltaErr = CheckDelta(id, "max_health_delta", def.MaxHealthDelta)
                            ?? CheckDelta(id, "attack_damage_delta", def.AttackDamageDelta)
                            ?? CheckDelta(id, "move_speed_delta", def.MoveSpeedDelta)
                            ?? CheckDelta(id, "armor_delta", def.ArmorDelta);
            if (deltaErr != null) return ItemValidationResult.Fail(deltaErr);

            // ── (c2) Shop costs within [0, Range] (Story 3.16). A negative cost would ADD resource on buy
            //        (BuildingSystem.BuyItemCommand SpendOre(faction, -CostOre) refunds), so it must fail CLOSED here —
            //        the editor ValidateFields already rejects it; the sim gate must too (this is the sole minter). ──
            string? costErr = CheckCost(id, "cost_ore", def.CostOre)
                           ?? CheckCost(id, "cost_crystal", def.CostCrystal);
            if (costErr != null) return ItemValidationResult.Fail(costErr);

            // ── (d) Consumable ⇔ effect graph coherence (fail-closed on a dangling or missing graph) ──
            EffectNode? root = def.EffectGraph;
            if (def.Charges > 0 && root is null)
                return Fail(id, "effect", "a charged consumable (charges > 0) must declare an effect graph.");
            if (def.Charges == 0 && root is not null)
                return Fail(id, "effect", "a stat item (charges == 0) must NOT declare an effect graph (it would never fire).");

            // ── (e) Effect structural bounds — reuse the 2.1 gate verbatim (depth/per-Sequence caps) ──
            if (root is not null)
            {
                EffectBoundsResult bounds = EffectBounds.Validate(root);
                if (!bounds.IsValid)
                    return Fail(id, "effect", bounds.Error!);
            }

            // ── Success: mint the proof-of-validation token (the codebase's THIRD `new Validated<`; the sole-minter
            //    source scan allow-lists {ScenarioValidator.cs, AbilityValidator.cs, ItemDefinitionValidator.cs}). ──
            return ItemValidationResult.Pass(
                new Validated<ItemDefinition>(def, new ScenarioValidator.Proof()));
        }

        /// <summary>
        /// Story 3.16 EDITOR surface (D-2): the SAME rules as <see cref="Validate"/> but collecting EVERY located field
        /// error (keyed by JSON <c>FieldPath</c>) for the item editor's per-field badges — mirrors
        /// <c>UnitDefinitionValidator</c>. Additionally rejects a non-empty <see cref="ItemDefinition.Icon"/> whose file
        /// does not exist (via the injected <paramref name="iconExists"/> presentation delegate — Godot's
        /// <c>ResourceLoader.Exists</c>; null skips the icon check so Tier-1 callers stay Godot-free). Mints NO token —
        /// the sim <see cref="Validate"/> is the sole minter; this is an authoring gate only.
        /// </summary>
        public ItemValidationResult ValidateFields(ItemDefinition? def, System.Func<string, bool>? iconExists = null)
        {
            var errors = new System.Collections.Generic.List<(string FieldPath, string Message)>();
            if (def is null)
            {
                errors.Add(("item", "item is null."));
                return ItemValidationResult.Fields(errors);
            }

            string id = def.Id ?? "";

            if (string.IsNullOrEmpty(id))
                errors.Add(("id", "item.id is null or empty."));

            if (def.Charges < 0)
                errors.Add(("charges", Located(id, "charges",
                    $"={def.Charges} must be >= 0 (0 = a stat item; >0 = a consumable).")));

            AddDelta(errors, id, "max_health_delta", def.MaxHealthDelta);
            AddDelta(errors, id, "attack_damage_delta", def.AttackDamageDelta);
            AddDelta(errors, id, "move_speed_delta", def.MoveSpeedDelta);
            AddDelta(errors, id, "armor_delta", def.ArmorDelta);

            // Costs (Story 3.16 shops): a negative cost would ADD resource on buy; keep them in the representable range.
            if (def.CostOre < -Range || def.CostOre > Range)
                errors.Add(("cost_ore", Located(id, "cost_ore", $"raw {def.CostOre.Raw} is out of range.")));
            else if (def.CostOre < Fixed.Zero)
                errors.Add(("cost_ore", Located(id, "cost_ore", "must be >= 0 (a negative cost ADDS resource on buy).")));
            if (def.CostCrystal < -Range || def.CostCrystal > Range)
                errors.Add(("cost_crystal", Located(id, "cost_crystal", $"raw {def.CostCrystal.Raw} is out of range.")));
            else if (def.CostCrystal < Fixed.Zero)
                errors.Add(("cost_crystal", Located(id, "cost_crystal", "must be >= 0 (a negative cost ADDS resource on buy).")));

            EffectNode? root = def.EffectGraph;
            if (def.Charges > 0 && root is null)
                errors.Add(("effect", Located(id, "effect", "a charged consumable (charges > 0) must declare an effect graph.")));
            if (def.Charges == 0 && root is not null)
                errors.Add(("effect", Located(id, "effect",
                    "a stat item (charges == 0) must NOT declare an effect graph (it would never fire).")));
            if (root is not null)
            {
                EffectBoundsResult bounds = EffectBounds.Validate(root);
                if (!bounds.IsValid)
                    errors.Add(("effect", Located(id, "effect", bounds.Error!)));
            }

            // Missing-icon-file rejection (new, Story 3.16): a non-empty icon whose file does not exist fails closed with a
            // field-located message. The check is presentation-supplied (ResourceLoader.Exists) so the sim stays Godot-free.
            if (iconExists != null && !string.IsNullOrEmpty(def.Icon) && !iconExists(def.Icon))
                errors.Add(("icon", Located(id, "icon", $"'{def.Icon}' does not exist under res:// (missing icon file).")));

            return ItemValidationResult.Fields(errors);
        }

        private static void AddDelta(System.Collections.Generic.List<(string, string)> errors, string id, string path, Fixed delta)
        {
            string? err = CheckDelta(id, path, delta);
            if (err != null) errors.Add((path, err));
        }

        private static string? CheckDelta(string id, string path, Fixed delta)
        {
            if (delta > Range || delta < -Range)
                return Located(id, path, $"raw {delta.Raw} is out of range (|value| must be < 32768).");
            // Story 3.15 review: the tighter per-item cap. INVENTORY_SLOTS × MAX_ITEM_STAT_DELTA stays well below the
            // Fixed integer ceiling, so a stacked/carried item can never wrap an Effective* stat negative.
            if (delta > MAX_ITEM_STAT_DELTA || delta < -MAX_ITEM_STAT_DELTA)
                return Located(id, path,
                    $"|delta| {Fixed.Abs(delta).ToInt()} exceeds MAX_ITEM_STAT_DELTA {MAX_ITEM_STAT_DELTA.ToInt()}");
            return null;
        }

        /// <summary>Reject a shop cost outside <c>[0, Range]</c> — negative (would ADD resource on buy) or over the 16.16
        /// representable ceiling — mirroring the <see cref="ValidateFields"/> editor rule. Returns a located error or null.</summary>
        private static string? CheckCost(string id, string path, Fixed cost)
        {
            if (cost < -Range || cost > Range)
                return Located(id, path, $"raw {cost.Raw} is out of range.");
            if (cost < Fixed.Zero)
                return Located(id, path, "must be >= 0 (a negative cost ADDS resource on buy).");
            return null;
        }

        private static ItemValidationResult Fail(string id, string path, string reason) =>
            ItemValidationResult.Fail(Located(id, path, reason));

        private static string Located(string id, string path, string reason) =>
            $"item '{id}'.{path}: {reason}";
    }
}
