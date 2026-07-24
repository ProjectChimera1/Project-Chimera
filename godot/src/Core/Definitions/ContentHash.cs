#nullable enable
using System.Collections.Generic;
using System.Linq;
using ProjectChimera.Core;    // Fixed
using ProjectChimera.Combat;  // DamageTable, DamageType, ArmorType

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// Story 9.16 — a Godot-free FNV-64 canonical fold over the loaded CONTENT DEFINITIONS a scenario references:
    /// the distinct loaded faction defs (incl. their inline units/buildings/research), the full
    /// <see cref="AbilityRegistry"/>, the full <see cref="ItemRegistry"/>, and the <see cref="DamageTable"/> cells.
    /// The pre-match handshake proved SCENARIO agreement (<see cref="CanonicalModelHash"/>) and the effect-graph
    /// structural caps (<see cref="RulesetHash"/>), but NOT the content bytes the scenario loads — so two peers whose
    /// <c>damage_table.json</c> or a single unit's <c>attack_damage</c> differed passed every gate then desynced from
    /// the first combat tick (the "handshake does not cover faction and ability JSON" known-desync vector). Folded
    /// into <see cref="MatchAgreementHash"/> (bumping ITS AlgoVersion), this makes any content-byte difference reject
    /// fail-closed at the existing <see cref="ProjectChimera.Multiplayer.HandshakeGate"/> pre-tick.
    ///
    /// <para><b>Family conventions (identical to <see cref="CanonicalModelHash"/>).</b>
    ///   • <c>AlgoVersion</c> mixed FIRST (a bump moves the value alone);
    ///   • authoring floats quantized via <c>Fixed.FromFloat(v).Raw</c>, <c>Fixed</c> fields via <c>.Raw</c> — the
    ///     exact integer the sim uses (so "1.0" and "1" fold equal while a real change diverges);
    ///   • enums by <c>.ToString()</c> NAME; strings via a length-prefix + UTF-8 bytes (null distinct from "");
    ///   • collections SORTED by a total order (distinct factions by <c>Id</c> ordinal; units/buildings/research by
    ///     <c>Id</c> ordinal — a duplicate id is a validator reject, so <c>Id</c> is a total key) so JSON array order
    ///     cannot move the hash; the registries fold in their own INDEX order (ascending-<c>Id</c>, which IS the sim's
    ///     runtime id assignment — an extra ability file that shifts indices moves the hash, catching the id-reindex
    ///     desync);
    ///   • ability/item <c>EffectGraph</c>s fold through the SHARED <see cref="CanonicalFold.MixEffect"/> walk (the
    ///     same one <c>CanonicalModelHash</c> uses for DSL <c>run_effect</c> embeds — one implementation, no drift);
    ///   • a <c>0 → 1</c> sentinel so valid content never hashes to the fail-open "no hash" value;
    ///   • NEVER folds JSON/file bytes or a re-serialized canonical string (the cross-runtime string-format risk +
    ///     the AI-gen stale-file lesson).</para>
    ///
    /// <para><b>EXCLUDED (presentation-only, documented per the 2.7 no-fold rule — a presentation edit must not
    /// false-positive-reject a match).</b> <c>CombatFeedbackProfile</c> (already excluded from <c>SimChecksum</c>),
    /// <c>DisplayName</c>, <c>MeshPath</c>/<c>MeshScale</c>, <c>Icon</c>, faction <c>Color</c>,
    /// <c>SignatureMechanicDisplay</c>, and the AI preset (<c>AiPreset</c> — excluded WHILE the AI is not
    /// lockstep-deterministic (float, D2 debt, not an MP slot), so folding it today would false-positive-reject;
    /// RE-FOLD it here when the AI enters lockstep MP). <c>[JsonIgnore]</c> computed/derived props (<c>ResolvedCost</c>,
    /// <c>ParsedTargeting</c>, ability indices, …) are never folded — they derive from folded fields. The
    /// authoring-only <c>Behaviors</c> + <c>Hero</c> block are ALLOWLISTED (no sim system reads them yet — fold when a
    /// story does). Every JSON-mapped field's fold-or-exclude decision is guarded by <c>ContentFoldCompletenessTests</c>.</para>
    ///
    /// Godot-free (src/Core/Definitions) — int/ulong/<c>Fixed.Raw</c> only (analyzer-clean; <c>Fixed.FromFloat</c> is
    /// the sanctioned load-time quantize, called once per match load, never in-tick).
    /// </summary>
    public static class ContentHash
    {
        /// <summary>Algorithm version of THIS hash. Mixed FIRST so a bump moves the value alone. Independent of
        /// <see cref="CanonicalModelHash.AlgoVersion"/>/<see cref="RulesetHash.AlgoVersion"/> — new at 1.</summary>
        public const int AlgoVersion = 1;

        /// <summary>
        /// The LOCAL per-domain content fingerprint (ruleset-caps, factions, abilities, items, damage-table). Each
        /// domain hash is an INDEPENDENT fingerprint (its own <c>Offset</c> + <c>AlgoVersion</c> + that domain's fold).
        /// It is surfaced on a handshake block so a human can COMPARE it line-by-line with the peer's — NOT an
        /// automatic remote-domain naming: the wire carries one combined 64-bit value (no sub-hash exchange), so this
        /// side only knows its OWN fingerprint. It also covers ONLY the 5 content domains — a handshake mismatch caused
        /// by a NON-content component (roster / teams / start-state / scenario / initial-delay) will show all-matching
        /// content here and is NOT attributable from this alone. This is the hook Story 12.4's mod.io re-download flow
        /// will consume; the re-download OFFER itself is out of scope here. <see cref="Combined"/> is the
        /// <see cref="Compute"/> value folded into <see cref="MatchAgreementHash"/>.
        /// </summary>
        public readonly struct Breakdown
        {
            public readonly ulong RulesetCaps;
            public readonly ulong Factions;
            public readonly ulong Abilities;
            public readonly ulong Items;
            public readonly ulong DamageTable;
            public readonly ulong Combined;

            public Breakdown(ulong rulesetCaps, ulong factions, ulong abilities, ulong items, ulong damageTable, ulong combined)
            {
                RulesetCaps = rulesetCaps;
                Factions    = factions;
                Abilities   = abilities;
                Items       = items;
                DamageTable = damageTable;
                Combined    = combined;
            }

            /// <summary>A compact human-readable per-domain line for the lobby block message / log (presentation).</summary>
            public override string ToString() =>
                $"ruleset-caps=0x{RulesetCaps:X16} factions=0x{Factions:X16} abilities=0x{Abilities:X16} " +
                $"items=0x{Items:X16} damage-table=0x{DamageTable:X16}";
        }

        /// <summary>
        /// The single 64-bit content hash folded into <see cref="MatchAgreementHash"/>: AlgoVersion → Factions →
        /// Abilities → Items → DamageTable. Never returns 0 (sentinel). Deterministic + Godot-free so Tier-1 computes
        /// it headless. Null inputs fold as empty (an empty registry / no factions / the default table).
        /// </summary>
        public static ulong Compute(
            IReadOnlyList<FactionDefinition>? loadedFactions,
            AbilityRegistry? abilities,
            ItemRegistry? items,
            DamageTable? damage)
        {
            ulong h = CanonicalFold.Offset;
            h = CanonicalFold.MixInt(h, AlgoVersion); // namespaces the hash

            h = FoldFactions(h, loadedFactions);
            h = FoldAbilities(h, abilities);
            h = FoldItems(h, items);
            h = FoldDamageTable(h, damage);

            return h == 0UL ? 1UL : h; // sentinel: valid content never hashes to the fail-open "no hash" value
        }

        /// <summary>
        /// The per-domain <see cref="Breakdown"/> (each an independent fingerprint) plus the combined
        /// <see cref="Compute"/> value and the composed <see cref="RulesetHash"/> caps. Surfaced locally on a
        /// handshake block so the two humans (and Story 12.4) can compare domain-by-domain.
        /// </summary>
        public static Breakdown Describe(
            IReadOnlyList<FactionDefinition>? loadedFactions,
            AbilityRegistry? abilities,
            ItemRegistry? items,
            DamageTable? damage)
        {
            ulong factions = Seal(FoldFactions(Seed(), loadedFactions));
            ulong abils    = Seal(FoldAbilities(Seed(), abilities));
            ulong itms     = Seal(FoldItems(Seed(), items));
            ulong dmg      = Seal(FoldDamageTable(Seed(), damage));
            ulong combined = Compute(loadedFactions, abilities, items, damage);
            return new Breakdown(RulesetHash.Compute(), factions, abils, itms, dmg, combined);
        }

        // ── Per-domain independent-fingerprint seed/seal (for Describe) ──────────────────────────────────────────
        private static ulong Seed() => CanonicalFold.MixInt(CanonicalFold.Offset, AlgoVersion);
        private static ulong Seal(ulong h) => h == 0UL ? 1UL : h;

        // ── Factions ─────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The DISTINCT loaded faction set — dedup by <c>Id</c>, sorted by <c>Id</c> ordinal (the per-slot
        /// faction ASSIGNMENT is already <c>CanonicalModelHash</c>'s job, not re-folded here). A count prefix then
        /// each faction.</summary>
        private static ulong FoldFactions(ulong h, IReadOnlyList<FactionDefinition>? loadedFactions)
        {
            // Sort by Id (ordinal) then drop consecutive-duplicate Ids → a deterministic distinct set with no
            // Dictionary enumeration. A duplicate Id with divergent content is a validator reject upstream; the
            // first-in-sorted-order wins (OrderBy is stable).
            List<FactionDefinition> sorted = (loadedFactions ?? System.Array.Empty<FactionDefinition>())
                .Where(f => f != null)
                .OrderBy(f => f.Id, System.StringComparer.Ordinal)
                .ToList();
            var distinct = new List<FactionDefinition>(sorted.Count);
            string? prevId = null;
            foreach (FactionDefinition f in sorted)
            {
                if (distinct.Count == 0 || !System.StringComparer.Ordinal.Equals(f.Id, prevId))
                {
                    distinct.Add(f);
                    prevId = f.Id;
                }
            }

            h = CanonicalFold.MixInt(h, distinct.Count);
            foreach (FactionDefinition f in distinct)
                h = FoldFaction(h, f);
            return h;
        }

        /// <summary>One faction: <c>Id</c>, its units/buildings/research (each sorted by <c>Id</c>), the signature-
        /// mechanic ids, hero-unit ref, persistence flag, and the starting balances. EXCLUDES <c>DisplayName</c>,
        /// <c>Color</c>, <c>AiPreset</c>, <c>SignatureMechanicDisplay</c>.</summary>
        private static ulong FoldFaction(ulong h, FactionDefinition f)
        {
            h = CanonicalFold.MixStr(h, f.Id);

            List<UnitDefinition> units = (f.Units ?? new List<UnitDefinition>())
                .Where(u => u != null).OrderBy(u => u.Id, System.StringComparer.Ordinal).ToList();
            h = CanonicalFold.MixInt(h, units.Count);
            foreach (UnitDefinition u in units) h = FoldUnitCommon(h, u);

            List<BuildingDefinition> buildings = (f.Buildings ?? new List<BuildingDefinition>())
                .Where(b => b != null).OrderBy(b => b.Id, System.StringComparer.Ordinal).ToList();
            h = CanonicalFold.MixInt(h, buildings.Count);
            foreach (BuildingDefinition b in buildings) h = FoldBuilding(h, b);

            List<ResearchDefinition> research = (f.Research ?? new List<ResearchDefinition>())
                .Where(r => r != null).OrderBy(r => r.Id, System.StringComparer.Ordinal).ToList();
            h = CanonicalFold.MixInt(h, research.Count);
            foreach (ResearchDefinition r in research) h = FoldResearch(h, r);

            h = CanonicalFold.MixStr(h, f.SignatureMechanicId);
            h = CanonicalFold.MixStr(h, f.SignatureMechanicEffectId);
            h = CanonicalFold.MixStr(h, f.HeroUnitId);
            h = CanonicalFold.MixInt(h, f.PersistenceEnabled ? 1 : 0);
            h = CanonicalFold.MixInt(h, Fixed.FromFloat(f.StartingOre).Raw);
            h = CanonicalFold.MixInt(h, Fixed.FromFloat(f.StartingCrystal).Raw);
            return h;
        }

        /// <summary>Every sim stat/gameplay field of a unit (authoring floats quantized via <c>Fixed.FromFloat.Raw</c>,
        /// so the fold matches the value the sim reads). EXCLUDES <c>DisplayName</c>/<c>MeshPath</c>/<c>MeshScale</c>/
        /// <c>CombatFeedback</c>; ALLOWLISTS the authoring-only <c>Behaviors</c> + <c>Hero</c> block (no sim system
        /// reads them yet).</summary>
        private static ulong FoldUnitCommon(ulong h, UnitDefinition u)
        {
            h = CanonicalFold.MixStr(h, u.Id);
            h = CanonicalFold.MixStr(h, u.Category);
            h = CanonicalFold.MixInt(h, Fixed.FromFloat(u.Hp).Raw);
            h = CanonicalFold.MixInt(h, Fixed.FromFloat(u.Speed).Raw);
            h = CanonicalFold.MixInt(h, Fixed.FromFloat(u.AttackDamage).Raw);
            h = CanonicalFold.MixInt(h, Fixed.FromFloat(u.AttackRange).Raw);
            h = CanonicalFold.MixInt(h, Fixed.FromFloat(u.AttackSpeed).Raw);
            h = CanonicalFold.MixStr(h, u.DamageType);
            h = CanonicalFold.MixStr(h, u.ArmorType);
            h = CanonicalFold.MixInt(h, Fixed.FromFloat(u.Armor).Raw);
            // cost: fold the RESOLVED sparse map the sim actually trains/constructs with (UnitDefinition.ResolvedCost
            // = the authored `cost` map verbatim, else the legacy {ore:CostOre, crystal:CostCrystal} with zeros
            // omitted). Folding the resolved map — not the raw CostOre + CostCrystal + Cost triple — makes an authored
            // `cost:{ore:X,crystal:Y}` and the legacy `cost_ore:X`/`cost_crystal:Y` (which resolve to the SAME sparse
            // map) fold IDENTICALLY, so a logically-equal authoring choice never false-positive-rejects at the lobby.
            h = FoldResolvedCost(h, u.ResolvedCost);
            h = CanonicalFold.MixInt(h, u.Supply);
            h = CanonicalFold.MixInt(h, Fixed.FromFloat(u.TrainTime).Raw);
            h = CanonicalFold.MixInt(h, Fixed.FromFloat(u.VisionRange).Raw);
            h = CanonicalFold.MixInt(h, Fixed.FromFloat(u.SplashRadius).Raw);
            h = CanonicalFold.MixStr(h, u.Delivery);
            h = CanonicalFold.MixInt(h, Fixed.FromFloat(u.ProjectileSpeed).Raw);
            // xp_bounty: fold the RESOLVED bounty the sim awards (ResolveXpBounty = the authored value if set, else the
            // derived CostOre+CostCrystal default, clamped) — NOT the raw nullable + presence bit. So an OMITTED
            // xp_bounty and one AUTHORED to its resolved default fold IDENTICALLY (they are sim-identical).
            h = CanonicalFold.MixInt(h, Fixed.FromFloat(u.ResolveXpBounty()).Raw);
            h = CanonicalFold.MixInt(h, Fixed.FromFloat(u.CollisionRadius).Raw);
            h = CanonicalFold.MixStr(h, u.SeparationPriority);
            h = FoldStringArray(h, u.Prerequisites);
            h = FoldStringArray(h, u.Abilities);       // declaration order IS semantic (ResolveAbilities fills capped slots in order)
            h = FoldStringArray(h, u.AttackDomains);
            h = FoldStringArray(h, u.Tags);
            h = CanonicalFold.MixInt(h, u.IsHero ? 1 : 0);
            h = CanonicalFold.MixInt(h, u.RevivesHeroes ? 1 : 0);
            h = CanonicalFold.MixInt(h, u.SellsItems ? 1 : 0);
            h = FoldStringArray(h, u.ShopStock);
            h = CanonicalFold.MixInt(h, Fixed.FromFloat(u.ShopRadius).Raw);
            h = CanonicalFold.MixInt(h, Fixed.FromFloat(u.MaxEnergy).Raw);
            return h;
        }

        /// <summary>A building = the unit-common fold + the four building-only fields. <c>construction_time</c> and
        /// <c>supply_bonus</c> fold the RESOLVED value the sim reads (not a presence bit + raw nullable): the load
        /// gate (<see cref="BuildingDefinitionValidator"/>) REQUIRES both, so every validly-loaded building has them
        /// authored and <c>BuildingStore.Create</c>'s all-or-none gate always passes → the sim reads the authored
        /// value. Folding it directly makes the resolved value the hash sees match what the sim runs (no
        /// omit-vs-default false-positive; an omitted field is a load REJECT, never a peer that reaches the handshake).
        /// The per-BuildingType switch fallback in <c>BuildingStore.Create</c> is deliberately NOT duplicated here — it
        /// is unreachable for loaded content, and copying it would add a silent-drift touch-site the completeness guard
        /// cannot cover. The <c>?? 0f</c>/<c>?? 0</c> keeps <see cref="Compute"/> total for a hand-built (unvalidated,
        /// non-loadable) def.</summary>
        private static ulong FoldBuilding(ulong h, BuildingDefinition b)
        {
            h = FoldUnitCommon(h, b);
            h = CanonicalFold.MixInt(h, Fixed.FromFloat(b.ConstructionTime ?? 0f).Raw); // resolved (validator guarantees authored)
            h = CanonicalFold.MixInt(h, b.SupplyBonus ?? 0);                            // resolved (validator guarantees authored)
            h = CanonicalFold.MixStr(h, b.ProducesCategory);
            h = FoldStringArray(h, b.AvailableResearch);
            return h;
        }

        /// <summary>One research: <c>Id</c>, cancel-refund fraction (quantized), prerequisites, and the level ladder
        /// (declaration order — 4.9 applies levels in order). EXCLUDES <c>DisplayName</c>.</summary>
        private static ulong FoldResearch(ulong h, ResearchDefinition r)
        {
            h = CanonicalFold.MixStr(h, r.Id);
            h = CanonicalFold.MixInt(h, Fixed.FromFloat(r.CancelRefundFraction).Raw);
            h = FoldStringArray(h, r.Prerequisites);
            List<ResearchLevel> levels = (r.Levels ?? new List<ResearchLevel>()).Where(l => l != null).ToList();
            h = CanonicalFold.MixInt(h, levels.Count);
            foreach (ResearchLevel lvl in levels)
            {
                h = FoldCostMap(h, lvl.Cost);
                h = CanonicalFold.MixInt(h, lvl.TimeTicks);
                // modifier_delta: nullable — presence bit then the four quantized deltas.
                h = CanonicalFold.MixInt(h, lvl.ModifierDelta != null ? 1 : 0);
                if (lvl.ModifierDelta != null)
                {
                    h = CanonicalFold.MixInt(h, Fixed.FromFloat(lvl.ModifierDelta.MaxHealthDelta).Raw);
                    h = CanonicalFold.MixInt(h, Fixed.FromFloat(lvl.ModifierDelta.AttackDamageDelta).Raw);
                    h = CanonicalFold.MixInt(h, Fixed.FromFloat(lvl.ModifierDelta.MoveSpeedDelta).Raw);
                    h = CanonicalFold.MixInt(h, Fixed.FromFloat(lvl.ModifierDelta.ArmorDelta).Raw);
                }
            }
            return h;
        }

        // ── Abilities ────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The WHOLE ability registry in INDEX (ascending-<c>Id</c>) order — which IS the sim's runtime id
        /// assignment, so an extra/missing ability file that shifts indices moves the hash (catches the id-reindex
        /// desync). A count prefix then each ability.</summary>
        private static ulong FoldAbilities(ulong h, AbilityRegistry? abilities)
        {
            IReadOnlyList<AbilityDefinition> all = abilities?.All ?? (IReadOnlyList<AbilityDefinition>)System.Array.Empty<AbilityDefinition>();
            h = CanonicalFold.MixInt(h, all.Count);
            foreach (AbilityDefinition a in all)
            {
                h = CanonicalFold.MixStr(h, a.Id);
                h = CanonicalFold.MixStr(h, a.Targeting);
                h = CanonicalFold.MixStr(h, a.Activation);
                h = CanonicalFold.MixInt(h, a.CostEnergy.Raw);
                h = CanonicalFold.MixInt(h, a.CostOre);
                h = CanonicalFold.MixInt(h, a.CostCrystal);
                h = CanonicalFold.MixInt(h, a.CostHealth);
                h = CanonicalFold.MixInt(h, a.AllowSelfLethal ? 1 : 0);
                h = CanonicalFold.MixInt(h, a.Cooldown.Raw);
                h = CanonicalFold.MixEffect(h, a.EffectGraph); // the SHARED typed effect walk (parity with run_effect embeds)
            }
            return h;
        }

        // ── Items ────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The WHOLE item registry in INDEX (ascending-<c>Id</c>) order — same id-reindex protection as the
        /// ability registry (<c>ItemStore.DefId</c> is a registry index). A count prefix then each item.</summary>
        private static ulong FoldItems(ulong h, ItemRegistry? items)
        {
            IReadOnlyList<ItemDefinition> all = items?.All ?? (IReadOnlyList<ItemDefinition>)System.Array.Empty<ItemDefinition>();
            h = CanonicalFold.MixInt(h, all.Count);
            foreach (ItemDefinition it in all)
            {
                h = CanonicalFold.MixStr(h, it.Id);
                h = CanonicalFold.MixInt(h, it.Charges);
                h = CanonicalFold.MixInt(h, it.MaxHealthDelta.Raw);
                h = CanonicalFold.MixInt(h, it.AttackDamageDelta.Raw);
                h = CanonicalFold.MixInt(h, it.MoveSpeedDelta.Raw);
                h = CanonicalFold.MixInt(h, it.ArmorDelta.Raw);
                h = CanonicalFold.MixEffect(h, it.EffectGraph); // shared effect walk
                h = CanonicalFold.MixInt(h, it.CostOre.Raw);
                h = CanonicalFold.MixInt(h, it.CostCrystal.Raw);
            }
            return h;
        }

        // ── DamageTable ──────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Every <c>Get(d,a)</c> cell over <c>[0,DamageType.COUNT) × [0,ArmorType.COUNT)</c> in enum-index
        /// order (the <c>Fixed</c> cells via <c>.Raw</c>). A null table folds the in-code <see cref="DamageTable.Default"/>.</summary>
        private static ulong FoldDamageTable(ulong h, DamageTable? damage)
        {
            DamageTable table = damage ?? DamageTable.Default;
            h = CanonicalFold.MixInt(h, (int)DamageType.COUNT);
            h = CanonicalFold.MixInt(h, (int)ArmorType.COUNT);
            for (int d = 0; d < (int)DamageType.COUNT; d++)
                for (int a = 0; a < (int)ArmorType.COUNT; a++)
                    h = CanonicalFold.MixInt(h, table.Get((DamageType)d, (ArmorType)a).Raw);
            return h;
        }

        // ── Shared field folds ───────────────────────────────────────────────────────────────────────────────────

        /// <summary>A sparse resource cost map: a present marker (null distinct from empty), a count prefix, then each
        /// (key,value) SORTED by key ordinal — so JSON key order cannot move the hash and no Dictionary is enumerated
        /// unsorted. Used for the RESEARCH-level cost (which has no legacy fallback — null means "free for this
        /// field").</summary>
        private static ulong FoldCostMap(ulong h, Dictionary<string, int>? cost)
        {
            if (cost == null) return CanonicalFold.MixInt(h, -1); // null (unauthored) — distinct from an empty map
            h = CanonicalFold.MixInt(h, cost.Count);
            foreach (KeyValuePair<string, int> kv in cost.OrderBy(kv => kv.Key, System.StringComparer.Ordinal))
            {
                h = CanonicalFold.MixStr(h, kv.Key);
                h = CanonicalFold.MixInt(h, kv.Value);
            }
            return h;
        }

        /// <summary>The RESOLVED unit/building cost map (<see cref="UnitDefinition.ResolvedCost"/>) — the sparse map
        /// the sim actually trains/constructs with. Never null (the resolver returns an empty map, not null), so no
        /// present marker is needed. A count prefix then each (key,value) SORTED by key ordinal (deterministic, no
        /// unsorted Dictionary enumeration).</summary>
        private static ulong FoldResolvedCost(ulong h, System.Collections.Generic.IReadOnlyDictionary<string, int> cost)
        {
            h = CanonicalFold.MixInt(h, cost.Count);
            foreach (KeyValuePair<string, int> kv in cost.OrderBy(kv => kv.Key, System.StringComparer.Ordinal))
            {
                h = CanonicalFold.MixStr(h, kv.Key);
                h = CanonicalFold.MixInt(h, kv.Value);
            }
            return h;
        }

        /// <summary>A JSON string array in DECLARATION order (both peers load the same file; a reorder is a genuine
        /// content edit — e.g. <c>abilities</c> order drives the capped active-slot assignment). Null folds a -1
        /// marker (distinct from an empty array).</summary>
        private static ulong FoldStringArray(ulong h, string[]? arr)
        {
            if (arr == null) return CanonicalFold.MixInt(h, -1);
            h = CanonicalFold.MixInt(h, arr.Length);
            foreach (string s in arr) h = CanonicalFold.MixStr(h, s);
            return h;
        }
    }
}
