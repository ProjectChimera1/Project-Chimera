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

        private static ItemValidationResult Fail(string id, string path, string reason) =>
            ItemValidationResult.Fail(Located(id, path, reason));

        private static string Located(string id, string path, string reason) =>
            $"item '{id}'.{path}: {reason}";
    }
}
