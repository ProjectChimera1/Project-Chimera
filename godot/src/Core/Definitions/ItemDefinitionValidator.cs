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
    /// SATISFY the charset yet make the <c>&lt;id&gt;.json</c> write throw on Windows; both folded into the single
    /// <see cref="IsFilenameSafeId"/> decision (DW-456) that <c>ItemCardPanel.DoDelete</c> also guards its
    /// <c>File.Delete</c> with — see the charset note below for which gate protects the filesystem); <c>charges &gt;= 0</c>; each of the four modifier deltas finite &amp; within <see cref="Range"/> (the
    /// <see cref="UnitDefinitionValidator"/> 16.16 ceiling) AND within its per-stat magnitude cap
    /// (<see cref="MAX_ITEM_STAT_DELTA"/> for max_health/attack/armor, the much tighter <see cref="MAX_MOVE_SPEED_DELTA"/>
    /// for move_speed so a validated item cannot tunnel a hero through pathing); the effect-graph COHERENCE
    /// rule — a charged consumable (<c>charges &gt; 0</c>) must declare an effect graph and a pure stat item
    /// (<c>charges == 0</c>) must NOT (a dangling effect graph never fires — reject fail-closed); and if an effect graph
    /// is present it must pass <see cref="EffectBounds.Validate"/> (depth/per-Sequence caps) verbatim; and (DW-650) the
    /// carried-modifier descriptor <c>ItemSystem.ApplyItemStatModifier</c> would mint from those deltas must satisfy the
    /// shared DW-488 accumulator bound <see cref="Modifier.CheckAuthoringBounds"/> (see <see cref="CarriedModifier"/>).
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

        /// <summary>
        /// DW-452: the item editor's "Speed" spinner bounds — the SINGLE Godot-free truth for the UX clamp, pinned by
        /// Tier-1 (<c>ItemEditorIdAndClampTests</c>) to ±<see cref="MAX_MOVE_SPEED_DELTA"/> (Story 3.16 AC4). The clamp
        /// is UX-only (<see cref="ValidateFields"/> fail-closes an over-cap value regardless), but <c>ItemCardPanel</c>
        /// reads its "Speed" spinner range FROM HERE, so the range can no longer silently decouple from the validator
        /// cap with every test still green — decoupling now requires changing this helper (a test fails) or bypassing
        /// it in the panel (visible in review).
        /// </summary>
        public static (int Min, int Max) MoveSpeedSpinnerRange()
        {
            int cap = MAX_MOVE_SPEED_DELTA.ToInt();
            return (-cap, cap);
        }

        /// <summary>
        /// DW-456: THE single "may this id touch an on-disk item file?" decision — extracted Godot-free so the DW-47
        /// traversal guard on <c>ItemCardPanel.DoDelete</c>'s <c>File.Delete</c> (previously an untestable inline check
        /// in a Godot <c>Node</c> lambda) is Tier-1 tested instead of verified only by reading. True iff the id is
        /// non-empty, charset-clean under the shared <see cref="UnitDefinitionValidator.SanitizeId"/> convention
        /// (<c>[a-z0-9_]</c>, so a traversal id like <c>../../foo</c> is refused) and not a Win32 reserved device
        /// basename (<see cref="UnitDefinitionValidator.IsReservedDeviceName"/>, DW-454). Fail-closed on null/empty:
        /// such an id can never have produced a legit on-disk file (both id gates reject it before Save), so it must
        /// never reach a filesystem sink. Load-bearing in all three surfaces — <see cref="Validate"/>,
        /// <see cref="ValidateFields"/> and <c>ItemCardPanel.DoDelete</c> — so they cannot drift apart.
        /// </summary>
        public static bool IsFilenameSafeId(string? id) =>
            !string.IsNullOrEmpty(id)
            && UnitDefinitionValidator.SanitizeId(id) == id
            && !UnitDefinitionValidator.IsReservedDeviceName(id);

        /// <summary>
        /// DW-453: the item editor's Create/Duplicate id mint — sanitize through the SHARED
        /// <see cref="UnitDefinitionValidator.SanitizeId"/> convention (NOT the panel's old Unicode-aware local
        /// sanitizer, whose <c>char.IsLetterOrDigit</c> kept letters like <c>é</c> that the DW-47 id gate rejects,
        /// minting un-saveable ids from a base like <c>café</c>), substitute the item noun for an empty mint, then
        /// dedup + reserved-basename-avoid via <see cref="UnitDefinitionValidator.MakeUniqueId"/>. Every minted id
        /// satisfies <see cref="IsFilenameSafeId"/> (Tier-1 pinned), so New/Duplicate can never hand back an id its
        /// own Save gate refuses.
        /// </summary>
        public static string MakeUniqueItemId(System.Collections.Generic.IEnumerable<string> existingIds, string? baseId)
        {
            string s = UnitDefinitionValidator.SanitizeId(baseId);
            if (s.Length == 0) s = "item";
            return UnitDefinitionValidator.MakeUniqueId(existingIds, s);
        }

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
            // Filename-safe id — DW-47 charset + DW-454 reserved basename, through THE single extracted decision
            // (IsFilenameSafeId, DW-456) shared with ValidateFields and ItemCardPanel.DoDelete's File.Delete guard.
            // The id becomes the JSON file name in Persist()'s Path.Combine/File.Move/File.Delete, so an id like
            // "../../foo" would escape the items directory and a reserved basename like "con" makes the written file
            // a DOS device name (DW-694: a PORTABILITY defect on this platform, not a local write failure). This sim
            // check is defense-in-depth for the sole-Validated<>-minter / content-load path; the guard that actually
            // blocks Persist() is the editor ValidateFields gate (via DoSave→Revalidate). Message selection keeps the
            // order the gates always had: charset first, reserved basename second.
            if (!IsFilenameSafeId(id))
                return UnitDefinitionValidator.SanitizeId(id) != id
                    ? Fail(id, "id", "contains characters outside [a-z0-9_]; rename before saving.")
                    : Fail(id, "id", UnitDefinitionValidator.ReservedDeviceNameMessage(id));

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

            // ── (c1b) DW-650: the SAME descriptor ItemSystem.ApplyItemStatModifier mints for a carried stat item, run
            //         through DW-488's shared accumulator bound (Modifier.CheckAuthoringBounds). See CarriedModifier. ──
            (string Field, string Reason)? overBound = CarriedModifier(def).CheckAuthoringBounds();
            if (overBound is not null)
                return Fail(id, overBound.Value.Field, overBound.Value.Reason);

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
            else if (!IsFilenameSafeId(id))
                // Filename-safe id — DW-47 charset + DW-454 reserved basename, through THE single extracted decision
                // (IsFilenameSafeId, DW-456) shared with the sim Validate and ItemCardPanel.DoDelete's File.Delete
                // guard, keyed to the "id" field so the editor badges it before Persist() can use the id in a path.
                // THIS is the gate that actually protects the filesystem — ItemCardPanel.DoSave→Revalidate keeps Save
                // disabled while invalid, whereas the sim Validate only runs AFTER Persist() has already written the
                // temp file. One badge per id, never two (the D-9 per-field-badge contract): charset message first,
                // reserved-basename message second.
                errors.Add(("id", UnitDefinitionValidator.SanitizeId(id) != id
                    ? Located(id, "id", "contains characters outside [a-z0-9_]; rename before saving.")
                    : Located(id, "id", UnitDefinitionValidator.ReservedDeviceNameMessage(id))));

            if (def.Charges < 0)
                errors.Add(("charges", Located(id, "charges",
                    $"={def.Charges} must be >= 0 (0 = a stat item; >0 = a consumable).")));

            int deltaErrorsBefore = errors.Count;
            AddDelta(errors, id, "max_health_delta", def.MaxHealthDelta, MAX_ITEM_STAT_DELTA, nameof(MAX_ITEM_STAT_DELTA));
            AddDelta(errors, id, "attack_damage_delta", def.AttackDamageDelta, MAX_ITEM_STAT_DELTA, nameof(MAX_ITEM_STAT_DELTA));
            AddDelta(errors, id, "move_speed_delta", def.MoveSpeedDelta, MAX_MOVE_SPEED_DELTA, nameof(MAX_MOVE_SPEED_DELTA));
            AddDelta(errors, id, "armor_delta", def.ArmorDelta, MAX_ITEM_STAT_DELTA, nameof(MAX_ITEM_STAT_DELTA));
            // DW-650: the same DW-488 accumulator bound the sim Validate applies, on the editor surface. Runs ONLY when
            // no per-stat cap already badged a delta, so the D-9 "one badge per field" contract holds (a delta over BOTH
            // its per-stat cap and the accumulator bound reports the tighter, more actionable cap).
            if (errors.Count == deltaErrorsBefore)
            {
                (string Field, string Reason)? overBound = CarriedModifier(def).CheckAuthoringBounds();
                if (overBound is not null)
                    errors.Add((overBound.Value.Field, Located(id, overBound.Value.Field, overBound.Value.Reason)));
            }

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

        /// <summary>
        /// DW-650 — the EXACT <see cref="Modifier"/> descriptor <c>ItemSystem.ApplyItemStatModifier</c> mints for a
        /// carried stat item (permanent, <see cref="StackRule.Ignore"/>, one stack, the four authored deltas), rebuilt
        /// here so this validator can run it through the shared DW-488 authoring bound
        /// (<see cref="Modifier.CheckAuthoringBounds"/>) instead of re-deriving that rule. The id is irrelevant to the
        /// bound (the runtime one is per-item-instance, <c>ItemModifierId(itemRef)</c>) so a placeholder 0 is used.
        ///
        /// <para><b>Why the check exists even though it cannot fire today.</b> DW-488 closed the
        /// <c>ModifierSystem.AccumulateBonus</c> wrap on the ABILITY path only; items mint a Modifier directly and never
        /// reach <c>AbilityValidator</c>, which is the gap DW-650 names. On the CURRENT constants the gap is latent
        /// rather than open: <see cref="MAX_ITEM_STAT_DELTA"/> (1000) and <see cref="MAX_MOVE_SPEED_DELTA"/> (50) are
        /// both far below <see cref="Modifier.MaxStatDeltaTotalRaw"/> (≈4096 stat units), so every item that reaches
        /// here already satisfies the bound and this check is a pass-through. It is adopted anyway because that
        /// implication is a coincidence of two independently-owned numbers, not an invariant: raising the per-stat cap
        /// past ≈4096 (a plausible balance change — it is a <c>public static readonly</c> knob) would silently re-open
        /// DW-488 on the item path. With this line the bound holds whatever the caps become, and
        /// <c>ModifierMinterBoundsTests</c> pins the implication so a cap change surfaces as a RED test rather than as a
        /// wrapped accumulator in a match.</para>
        /// </summary>
        private static Modifier CarriedModifier(ItemDefinition def) =>
            new Modifier(0,
                         durationTicks: -1,
                         StackRule.Ignore,
                         maxStacks: 1,
                         maxHealthDelta:    def.MaxHealthDelta,
                         attackDamageDelta: def.AttackDamageDelta,
                         moveSpeedDelta:    def.MoveSpeedDelta,
                         status: StatusFlags.None,
                         periodEffect: null,
                         periodTicks: 0,
                         armorDelta:        def.ArmorDelta);

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
