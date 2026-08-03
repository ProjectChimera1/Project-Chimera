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
    /// Checks: id present AND filename-safe (charset <c>[a-z0-9_]</c> via <see cref="UnitDefinitionValidator.SanitizeId"/>,
    /// so a traversal id like <c>../../foo</c> is rejected, AND not a Win32 reserved device basename via
    /// <see cref="UnitDefinitionValidator.IsReservedDeviceName"/> — DW-454, since <c>con</c>/<c>nul</c>/<c>com1</c>… all
    /// SATISFY the charset yet make the <c>&lt;id&gt;.json</c> write throw on Windows — see the charset note below for
    /// which gate protects the filesystem); <c>charges &gt;= 0</c>; each of the four modifier deltas finite &amp; within <see cref="Range"/> (the
    /// <see cref="UnitDefinitionValidator"/> 16.16 ceiling) AND within its per-stat magnitude cap
    /// (<see cref="MAX_ITEM_STAT_DELTA"/> for max_health/attack/armor, the much tighter <see cref="MAX_MOVE_SPEED_DELTA"/>
    /// for move_speed so a validated item cannot tunnel a hero through pathing); the effect-graph COHERENCE
    /// rule — a charged consumable (<c>charges &gt; 0</c>) must declare an effect graph and a pure stat item
    /// (<c>charges == 0</c>) must NOT (a dangling effect graph never fires — reject fail-closed); and if an effect graph
    /// is present it must pass <see cref="EffectBounds.Validate"/> (depth/per-Sequence caps) verbatim.
    ///
    /// <para>A charged consumable MAY ALSO carry the four stat deltas as a permanent carried modifier — a WC3-style
    /// HYBRID buff-consumable (e.g. a potion that buffs while held and heals on use). This is deliberately permitted
    /// (2026-07-25 decision): the coherence rule constrains only the effect graph vs charges, NOT stat deltas vs charges;
    /// <see cref="ProjectChimera.Combat.ItemSystem"/> already applies such a modifier on carry and removes it when the
    /// last charge is consumed. There is NO stat-item-XOR-consumable rule.</para>
    ///
    /// <para><b>Charset gate — which one protects the filesystem.</b> BOTH surfaces enforce the <c>[a-z0-9_]</c> charset:
    /// this sim <see cref="Validate"/> and the editor <see cref="ValidateFields"/>. The gate that actually blocks the
    /// item editor's <c>Persist()</c> before any <c>Path.Combine</c>/<c>File.Move</c> is <see cref="ValidateFields"/>,
    /// through <c>ItemCardPanel.DoSave</c> → <c>Revalidate</c> (Save stays disabled while invalid) — NOT this sim
    /// <see cref="Validate"/>, which <c>Persist()</c> only calls AFTER writing the temp file (its reload self-check).
    /// The sim <see cref="Validate"/> charset check is defense-in-depth for the sole-<see cref="Validated{T}"/>-minter /
    /// content-load path (nothing runnable escapes the gate), not the pre-<c>Persist</c> guard.</para>
    ///
    /// Non-finite / over-range deltas were already rejected at parse by <c>FixedJsonConverter</c>; this re-guards them
    /// for in-code definitions (tests).
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

        /// <summary>The per-item <c>move_speed_delta</c> magnitude cap (DW-42) — much tighter than
        /// <see cref="MAX_ITEM_STAT_DELTA"/> because move speed is on a single-digit scale, not the hundreds/thousands the
        /// other deltas live on. A symmetric magnitude bound (<c>-50</c> floor, <c>+50</c> ceiling) applied ONLY to
        /// <c>move_speed_delta</c>; the other three deltas keep <see cref="MAX_ITEM_STAT_DELTA"/>. It replaces the old
        /// uniform ±1000, blocking BOTH the ~1000-scale positive tunnel and the -1000-scale freeze extreme.
        /// <para><b>Units.</b> Base unit speeds in <c>resources/data</c> range 0–6.5 wu/s and the largest authored ability
        /// speed buff is +1. The sim runs ~30 ticks/s, so <c>+50</c> wu/s ≈ 1.7 wu/tick, and even a full inventory
        /// (<see cref="HeroStore.INVENTORY_SLOTS"/> × 50 = 300 wu/s ≈ 10 wu/tick) stays far below the ~1000 wu/tick that
        /// tunnels a hero through pathing/obstacles — so the cap decisively closes the positive tunnel class.</para>
        /// <para><b>On "freeze".</b> This cap does NOT make a hero un-freezable: <c>ModifierSystem</c> floors effective
        /// speed at 0 (<c>Max(0, base + delta)</c>), so any negative delta beyond roughly <c>-base</c> (well inside the
        /// ±50 window) still drives a carrier to 0. That is intentional — a curse/slow item that reduces a carrier toward
        /// 0 remains authorable BY DESIGN; the cap only bars the -1000-scale extreme, not the floor itself.</para>
        /// The general unsaturated-effective-stat overflow class (extreme BASE stat + level growth, NOT items) remains a
        /// pre-existing <c>ModifierSystem</c> deferral — this cap closes only the item-contributed move-speed portion.</summary>
        public static readonly Fixed MAX_MOVE_SPEED_DELTA = Fixed.FromInt(50);

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
            // Filename-safe charset (DW-47): the id becomes the JSON file name in Persist()'s Path.Combine/File.Move/
            // File.Delete, so an id like "../../foo" would escape the items directory. This sim check is defense-in-depth
            // for the sole-Validated<>-minter / content-load path; the guard that actually blocks Persist() is the editor
            // ValidateFields gate (via DoSave→Revalidate). Reuse the SINGLE shared charset convention
            // (UnitDefinitionValidator.SanitizeId) so both gates enforce the same rule.
            if (UnitDefinitionValidator.SanitizeId(id) != id)
                return Fail(id, "id", "contains characters outside [a-z0-9_]; rename before saving.");
            // Reserved Windows device basename (DW-454): the charset rule above ADMITS con/prn/aux/nul/com1-9/lpt1-9 —
            // they are all [a-z0-9_] — but Persist() then writes "<id>.json.tmp", which Win32 rejects for a reserved
            // basename (with or without an extension), surfacing as an opaque generic "Save failed" with no field badge
            // and no way to save the item. Rejected through the SHARED convention helper so this gate, ValidateFields
            // and the unit/building gate cannot drift.
            if (UnitDefinitionValidator.IsReservedDeviceName(id))
                return Fail(id, "id",
                    "is a Windows reserved device name (con|prn|aux|nul|com1-com9|lpt1-lpt9); the filesystem rejects '<id>.json' as a file name, so rename before saving.");

            // ── (b) Charges sign ──
            if (def.Charges < 0)
                return Fail(id, "charges", $"={def.Charges} must be >= 0 (0 = a stat item; >0 = a consumable).");

            // ── (c) Modifier deltas within the representable range (finite already guaranteed by FixedJsonConverter
            //        at parse; re-guarded here for in-code definitions). ──
            string? deltaErr = CheckDelta(id, "max_health_delta", def.MaxHealthDelta, MAX_ITEM_STAT_DELTA, nameof(MAX_ITEM_STAT_DELTA))
                            ?? CheckDelta(id, "attack_damage_delta", def.AttackDamageDelta, MAX_ITEM_STAT_DELTA, nameof(MAX_ITEM_STAT_DELTA))
                            ?? CheckDelta(id, "move_speed_delta", def.MoveSpeedDelta, MAX_MOVE_SPEED_DELTA, nameof(MAX_MOVE_SPEED_DELTA))
                            ?? CheckDelta(id, "armor_delta", def.ArmorDelta, MAX_ITEM_STAT_DELTA, nameof(MAX_ITEM_STAT_DELTA));
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
            else if (UnitDefinitionValidator.SanitizeId(id) != id)
                // Filename-safe charset (DW-47): same rule as the sim Validate, keyed to the "id" field so the editor
                // badges it before Persist() can use the id in a path.
                errors.Add(("id", Located(id, "id", "contains characters outside [a-z0-9_]; rename before saving.")));
            else if (UnitDefinitionValidator.IsReservedDeviceName(id))
                // Reserved Windows device basename (DW-454): same rule as the sim Validate. THIS is the gate that
                // actually protects the filesystem — ItemCardPanel.DoSave→Revalidate keeps Save disabled while invalid,
                // whereas the sim Validate only runs AFTER Persist() has already written the temp file. Without it the
                // reserved-name write throws and surfaces as a generic "Save failed" with no field badge.
                errors.Add(("id", Located(id, "id",
                    "is a Windows reserved device name (con|prn|aux|nul|com1-com9|lpt1-lpt9); the filesystem rejects '<id>.json' as a file name, so rename before saving.")));

            if (def.Charges < 0)
                errors.Add(("charges", Located(id, "charges",
                    $"={def.Charges} must be >= 0 (0 = a stat item; >0 = a consumable).")));

            AddDelta(errors, id, "max_health_delta", def.MaxHealthDelta, MAX_ITEM_STAT_DELTA, nameof(MAX_ITEM_STAT_DELTA));
            AddDelta(errors, id, "attack_damage_delta", def.AttackDamageDelta, MAX_ITEM_STAT_DELTA, nameof(MAX_ITEM_STAT_DELTA));
            AddDelta(errors, id, "move_speed_delta", def.MoveSpeedDelta, MAX_MOVE_SPEED_DELTA, nameof(MAX_MOVE_SPEED_DELTA));
            AddDelta(errors, id, "armor_delta", def.ArmorDelta, MAX_ITEM_STAT_DELTA, nameof(MAX_ITEM_STAT_DELTA));

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

        private static void AddDelta(System.Collections.Generic.List<(string, string)> errors, string id, string path,
                                     Fixed delta, Fixed cap, string capName)
        {
            string? err = CheckDelta(id, path, delta, cap, capName);
            if (err != null) errors.Add((path, err));
        }

        /// <summary>Range- and magnitude-check a single modifier delta against a PER-STAT cap
        /// (<paramref name="cap"/>/<paramref name="capName"/>): <see cref="MAX_ITEM_STAT_DELTA"/> for the three
        /// hundreds-scale deltas, the tighter <see cref="MAX_MOVE_SPEED_DELTA"/> for <c>move_speed_delta</c> (DW-42). The
        /// error names the exceeded constant so the reject points at the right ceiling.</summary>
        private static string? CheckDelta(string id, string path, Fixed delta, Fixed cap, string capName)
        {
            if (delta > Range || delta < -Range)
                return Located(id, path, $"raw {delta.Raw} is out of range (|value| must be < 32768).");
            // Story 3.15 review + DW-42: the tighter per-stat cap. INVENTORY_SLOTS × MAX_ITEM_STAT_DELTA stays well below
            // the Fixed integer ceiling, so a stacked/carried item can never wrap an Effective* stat negative; move_speed
            // uses a far tighter cap so a validated item cannot tunnel/freeze a hero.
            if (delta > cap || delta < -cap)
                return Located(id, path,
                    $"|delta| {Fixed.Abs(delta).ToInt()} exceeds {capName} {cap.ToInt()}");
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
