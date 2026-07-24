#nullable enable
using System;
using System.Linq;
using System.Text;
using ProjectChimera.Core; // Fixed
using ProjectChimera.Navigation; // PathabilityGrid.DigestOfBase64 (Story 6.5 fold)

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// Canonical start-state hash over the in-memory scenario model (Story 1.7, AR-23) — replaces the byte-FNV
    /// file hash (<see cref="ScenarioSerializer.ComputeFileHash"/>, the retired algo-1) as the lobby handshake
    /// value. FNV-64 folding, in a FIXED field order, of:
    ///   • numeric fields QUANTIZED via <c>Fixed.FromFloat(v).Raw</c> — the exact integer the sim will use, so
    ///     "1.0" and "1" (same float) hash equal while a real value change diverges;
    ///   • collections SORTED by a stable key, so JSON array order cannot change the hash;
    ///   • enums folded by NAME and strings by UTF-8 bytes (ordinal drifts on enum insert);
    ///   • cosmetic <c>Id</c>/<c>DisplayName</c> EXCLUDED; <c>Triggers</c> FOLDED since v8 (Story 7.7 closed the
    ///     long-ledgered "until 7.7" gap — see the <see cref="AlgoVersion"/> doc);
    ///   • the Story 6.7 authoring metadata <c>Author</c>/<c>Description</c>/<c>SuggestedPlayers</c> EXCLUDED on the
    ///     exact <c>DisplayName</c> basis — pure cosmetic/authoring fields that no sim system reads, so they are
    ///     folded into NEITHER this hash NOR <c>StartStateHash</c>. Each is omit-when-default, so an absent field is
    ///     byte-identical to the pre-6.7 fold; there is NO fold below and <c>AlgoVersion</c> stays 7 (this story adds
    ///     no hash-folded field). <c>MapBounds</c> — which Story 6.7's New-Map flow now sets per-scenario from the
    ///     <c>MapSize</c> set — was ALREADY folded (below); authoring a different playable extent is expected map
    ///     identity, not an algorithm change, so no re-baseline.
    ///   • <c>Variables</c>/<c>Timers</c>/<c>TriggerGraphJson</c> (Story 7.3) and <c>Regions</c> (Story 6.4) —
    ///     FOLDED since v8 (Story 7.7): the deferred "these fold with Triggers when 7.7 lands" promise is
    ///     discharged; declaration structure, region geometry, and the full parsed graph IR are now
    ///     handshake-covered (see the <see cref="AlgoVersion"/> doc for the fold layout). The per-node
    ///     <c>_editor</c> annotation bag and the <c>schema_version</c>/<c>checksum_algo_version</c> stamps remain
    ///     EXCLUDED (cosmetic / re-save-neutral by design).
    ///   • <c>CustomEvents</c> (Story 7.5, landed via merge) — FOLDED since v10, on the SAME declarations basis as
    ///     Variables/Timers/TriggerGraphJson: the custom-event registry is sim-semantic trigger-DSL model (param
    ///     slots/types drive the dispatch frames), so two peers with divergent registries must be rejected at the
    ///     handshake rather than first desyncing mid-match through the live <c>SimChecksum</c> queue fold. The
    ///     three 7.5 graph kinds fold in the v8 typed graph walk (see the <see cref="AlgoVersion"/> doc).
        ///   • <c>PathabilityBlocked</c> + slope config (Story 6.5) FOLDED (AlgoVersion 5→6) — the deliberate INVERSE
        ///     of the Regions decision: pathability feeds MOVEMENT (Position is checksummed), so a mismatched blocked
        ///     layer desyncs in-sim and must be rejected at the handshake. See the <see cref="AlgoVersion"/> doc.
    ///   • <c>ScenarioPlayerSlot.StartCrystal</c> FOLDED (Story 2.9b follow-up, AlgoVersion 2→3): it is sim-affecting
    ///     (Crystal is folded in SimChecksum, and <c>alpha_map_01.json</c> now ships a nonzero start_crystal), so two
    ///     clients with mismatched start_crystal now hash DIFFERENTLY here and are rejected at the handshake instead
    ///     of desyncing in-sim. Folded right after StartOre, in BOTH the sort key and the mixed byte stream.
    ///   • <c>ScenarioData.Supply</c> FOLDED via <see cref="SupplyConfig.Resolve"/>'s resolved+clamped values
    ///     (Story 4.4, AlgoVersion 3→4): it is sim-affecting (folds into SimChecksum via SupplyCap/SupplyUsed and
    ///     gates TrainUnit), so two clients with mismatched supply config now hash DIFFERENTLY here and are
    ///     rejected at the handshake instead of desyncing in-sim — the same StartCrystal-class fix, applied to
    ///     Supply. <c>SupplyConfig.Resolve</c> is the SAME method <see cref="ResourceStore.ConfigureSupply"/> calls
    ///     for the runtime resolution, so hash-equality ⇔ post-resolution runtime-equality holds both ways: an
    ///     omitted <c>supply</c> block and an explicitly-authored all-default <see cref="SupplyConfig"/> hash
    ///     IDENTICALLY (no false-positive mismatch), and a shadow-mode-reachable invalid negative value can never
    ///     collide with — nor silently diverge in meaning from — a legitimately distinct resolved value (no
    ///     false-negative mismatch). <c>HardCeiling</c>'s presence is folded as an explicit bit BEFORE its value
    ///     (not a sentinel int) since, unlike <see cref="MixStr"/>'s null-length <c>-1</c> (intrinsically
    ///     impossible for a real string), an authored <c>HardCeiling</c> can legitimately BE any non-negative int
    ///     including values a naive sentinel might collide with.
    /// A <c>0 → 1</c> sentinel guarantees a valid model never hashes to the "no hash" value the fail-open
    /// handshake treats as a skip. The 64-bit <see cref="Compute"/> is exposed for Epic 9 to attest later;
    /// <see cref="ToWire"/> folds it to the existing 32-bit Ready-packet wire used today.
    ///
    /// Godot-free (src/Core/Definitions). <c>Fixed.FromFloat</c> here is the sanctioned load-time quantize
    /// (called once per match load, never in-tick).
    /// </summary>
    public static class CanonicalModelHash
    {
        /// <summary>Algorithm version. 1 = the retired byte-FNV file hash; 2 = canonical-model hash;
        /// 3 = additionally folds <see cref="ScenarioPlayerSlot.StartCrystal"/> (Story 2.9b follow-up);
        /// 4 = additionally folds <see cref="ScenarioData.Supply"/>'s resolved values (Story 4.4);
        /// 5 = additionally folds <see cref="ScenarioResourceNode"/>'s 6 new Story 4.7 fields (CollectionModel,
        /// ResourceType, RequiresStructure, RequiresStructureRadius, OwnerSlot, IncomePeriodTicks) — all
        /// sim-affecting (collection model / resource routing / the requires_structure gate / Income's credit
        /// destination), so a lobby mismatch on any of them must reject at the handshake instead of desyncing
        /// in-sim. An omitted field hashes identically to its documented default (every existing scenario is
        /// unaffected).
        /// 6 = additionally folds the Story 6.5 authored PATHABILITY layer — the painted blocked bitset
        /// (<see cref="ScenarioData.PathabilityBlocked"/> via <c>PathabilityGrid.DigestOfBase64</c>) plus the slope-
        /// auto-block config (<see cref="ScenarioData.SlopeAutoBlock"/> + quantized
        /// <see cref="ScenarioData.SlopeBlockThreshold"/>). This is the DELIBERATE INVERSE of the Regions decision
        /// (regions feed only triggers → excluded): pathability feeds MOVEMENT (a core sim system whose output,
        /// Position, is checksummed), so two peers with mismatched blocked layers produce divergent paths and desync
        /// from the first move order — the established fix is handshake rejection, not in-sim detection. An absent
        /// paint + slope-off hashes IDENTICALLY to the pre-6.5 v5 fold (digest 0, toggle 0, threshold 0), so every
        /// existing scenario is unaffected; a real painted/slope change moves the handshake hash. This forces a
        /// ONE-TIME re-baseline of the handshake fixtures (human-authorized 2026-07-14).
        /// 7 = additionally folds the Story 6.6 BLOCKING-prop + WATER footprints (their union of
        /// <see cref="ProjectChimera.Navigation.FlowField.WorldToCell"/> cells, as a canonical content digest). This is
        /// the exact INVERSE-FREE extension of the 5→6 pathability fold: a blocking prop / water volume becomes blocked
        /// cells in the very same <c>PathabilityGrid</c>, so it feeds MOVEMENT (Position → SimChecksum) and a mismatch
        /// must be rejected at the handshake rather than desyncing in-sim. NON-blocking props, cameras, and every
        /// rotation/scale are COSMETIC (like <c>DisplayName</c>) and remain EXCLUDED. An absent/empty props+water set
        /// digests to 0, byte-identical to the v6 fold, so every existing scenario is unaffected; a blocking-footprint
        /// change moves the handshake hash and propagates into <c>StartStateHash</c> via the seed. This forces a
        /// ONE-TIME re-baseline of the handshake fixtures (human-authorized 2026-07-14).
        /// 8 = Story 7.7 — additionally folds the FULL trigger/DSL sim-semantic model, closing the long-ledgered
        /// "Triggers excluded until 7.7" handshake gap in ONE named bump: <c>Regions</c> (total-ordered), the flat
        /// <c>Triggers</c> (DECLARATION order — it is semantic: priority ties break by declaration index and the
        /// live SimChecksum var fold follows declaration order), <c>Variables</c>, <c>Timers</c>, and the PARSED
        /// <c>TriggerGraphJson</c> graph IR as a TYPED fold — nodes ascending id (kind string + each semantic field
        /// in fixed order; <c>Fixed</c> via <c>.Raw</c>; enums as stable names; run_effect embeds folded as a typed
        /// effect-tree walk), exec edges sorted <c>(Src,SrcPort,Dst,DstPort)</c>, data edges sorted likewise + wire
        /// name. NEVER the <c>ToCanonicalJson</c> bytes (the ledgered cross-runtime string-format risk). An
        /// unparseable graph folds a fixed sentinel (<see cref="Compute"/> stays pure/never-throw); the per-node
        /// <c>_editor</c> annotation bag and the <c>schema_version</c>/<c>checksum_algo_version</c> stamps are
        /// EXCLUDED by construction (cosmetic edits and legacy re-saves must not move the handshake). Hash 0 now
        /// BLOCKS the lobby start (<c>HandshakeGate</c>). This forces the story's ONE named re-baseline
        /// (hero-start-state golden + version pins, human-authorized via the Story 7.7 spec).
        /// 9 = Story 7.8 — additionally folds the <see cref="ScenarioData.CustomUi"/> widget tree as a TYPED
        /// recursive walk (present/absent marker, count prefix, per-widget id + kind name + anchor name + int
        /// offset/size + optional binds, then a per-child present bit recursing children), appended AFTER the v8
        /// trigger-graph fold in fixed order. This is sim-semantic for the MP handshake: two peers with divergent
        /// widget trees produce different hashes so <c>HandshakeGate</c> BLOCKS the lobby start rather than starting
        /// a match that shows each player a different UI. The per-widget <c>_editor</c> annotation bag is EXCLUDED
        /// BY CONSTRUCTION (this typed walk reads typed fields only, never the bag), so a cosmetic layout move / a
        /// re-save does NOT move the hash while a bind/kind/layout-field change DOES. An absent/empty <c>custom_ui</c>
        /// folds a 0 marker, byte-identical to the pre-7.8 v8 fold, so every existing scenario is unaffected. This
        /// forces the story's ONE named re-baseline (hero-start-state golden re-recorded — its <c>StartStateHash</c>
        /// value moves via the canonical seed; <c>StartStateHash.AlgoVersion</c> stays 2 — plus the AlgoVersion pin,
        /// in one commit). <c>SimChecksum</c> stays 17 and the 24 world goldens are untouched (the read rail is
        /// presentation-only, NOT folded into <c>SimChecksum</c>).
        /// 10 = Story 7.5 (landed via merge) — additionally folds (a) the <see cref="ScenarioData.CustomEvents"/>
        /// registry as a TYPED fold appended AFTER the v9 custom-UI fold: count prefix (null ≡ empty ⇒ 0 — the
        /// serializer chokepoint normalizes empty→null), DECLARATION order (order is semantic: the registry event
        /// INDEX is what the live SimChecksum queue fold and the dispatch plan key on); per event its Name, a
        /// Params count then per param Name + Type (enum by NAME — the MixVariables conventions), an
        /// AllowedRaisers count then each declared slot value as-declared (null ≡ empty ⇒ count 0); and (b) the
        /// three Story 7.5 graph kinds in the v8 typed trigger-graph node fold — raise_event (Name, Raiser,
        /// NextTick), expr_event_param (Name), and the custom_event subscription's <c>EventNode.EventName</c>
        /// appended after the frozen v8 EventNode fields (absent folds the MixStr null marker) — without which two
        /// graphs differing only in a raise target would hash identically. The registry is sim-semantic trigger-DSL
        /// model (param slots/types drive the dispatch frames), so a lobby mismatch on any of it must reject at the
        /// handshake instead of desyncing mid-match via the SimChecksum v18 queue fold — the exact declaration-fold
        /// class 7.7's v8 closed (the 7.5-era exclusion premise "declarations stay out until 7.7's fold" died when
        /// v8 landed). No existing scenario declares events or carries the new kinds, but the leading AlgoVersion
        /// mix moves the hash for EVERY scenario — the merge's ONE named re-baseline (hero-start-state golden +
        /// version pins, one commit); <c>StartStateHash.AlgoVersion</c> stays 2 (the 7.7/7.8 precedent).
        /// 11 = Story 7.9 — the custom-UI WRITE RAIL enters the folded widget tree: <see cref="MixWidget"/> gains a
        /// <c>Button</c> case folding its <c>Text</c>, <c>EventName</c>, an arg-count prefix then each authored arg
        /// RAW (Int/Bool value or <c>Fixed.Raw</c> — the parallel authored-type array is authoring/gate-only and is
        /// NOT folded), the <c>LocalAction</c> (enum by stable NAME) and its target (<c>LocalTargetWidgetId</c> +
        /// <c>LocalVarName</c> + <c>LocalVarValue</c>), in fixed field order. The per-widget <c>_editor</c> bag stays
        /// EXCLUDED by construction (this walk reads typed fields only), so a bind/event/arg/local-action/text/layout
        /// change moves the hash while a cosmetic <c>_editor</c>/re-save does NOT. Divergent widget trees (one peer
        /// has the button, one doesn't) hash differently → <c>HandshakeGate</c> BLOCKS the lobby start. No existing
        /// scenario declares a Button (the pre-7.9 fold of a Button-free tree is byte-identical modulo this leading
        /// AlgoVersion mix), but the AlgoVersion bump moves the hash for EVERY scenario — the story's ONE named
        /// re-baseline (hero-start-state golden re-recorded + version pins, one commit); <c>StartStateHash.AlgoVersion</c>
        /// stays 2, <c>SimChecksum.AlgoVersion</c> stays 18, and the 24 world replay goldens are byte-identical (the
        /// write rail enters the EXISTING checksum-folded DslEventQueue — no new folded sim state).
        /// 12 = Story 7.11 — additionally folds the <see cref="ScenarioData.WinConditionSpec"/> win-condition preset
        /// params, appended IMMEDIATELY AFTER the built-in <see cref="ScenarioData.WinCondition"/> enum fold. A null
        /// or <see cref="WinPresetKind.None"/> spec (every scenario using only the built-in enum) folds NOTHING extra
        /// — so a no-preset scenario hashes BYTE-IDENTICALLY to the pre-7.11 v11 fold apart from this leading
        /// AlgoVersion bump (the omit-when-default discipline; the serializer normalizes a None spec to null). A
        /// preset folds a present marker, the preset kind name (enum by stable NAME), and its params (KotH region_id
        /// + hold_ticks; Survival faction_slot + survive_ticks; Assassination leader_unit_index; Landmark
        /// structure_index) — sim-semantic win config, so two peers with divergent presets hash differently and
        /// <c>HandshakeGate</c> BLOCKS the lobby start rather than starting a match with mismatched victory rules. No
        /// existing scenario declares a preset, but the leading AlgoVersion mix moves the hash for EVERY scenario —
        /// the story's ONE named re-baseline (hero-start-state golden re-recorded + version pins, one commit);
        /// <c>StartStateHash.AlgoVersion</c> stays 2 (the 7.7–7.9 precedent), and the SimChecksum bump (v19) covers
        /// the 24 world replay goldens separately.
        /// 13 = Story 7.13 — the typed graph walk (<see cref="MixGraphNode"/>) is EXTENDED to fold the field values of
        /// every new node kind, INCLUDING Arm A's deferred leaves: <c>order_units</c> (command + faction + region + x/z),
        /// <c>move_camera</c> (camera name), <c>cinematic_mode</c> (flag), <c>play_vfx</c> (vfx id + x/z) — all four rode
        /// Arm A's type-name <c>default</c> arm (folding presence but NOT field values, a handshake gap) — plus the
        /// <c>ExprCallNode.Selector</c> for the state reads, the weighted-branch structure for <c>random_choice</c>, and
        /// the target trigger id for <c>enable_trigger</c>/<c>disable_trigger</c>/<c>run_trigger</c>. A scenario carrying
        /// NONE of the new kinds folds BYTE-IDENTICALLY apart from this leading AlgoVersion mix (the new arms are only
        /// reached by the new kinds; omit-when-default discipline verified) — so only the <c>hero-start-state</c> golden
        /// re-records, exactly like the 7.5/7.11 graph-walk-extension precedent. <c>StartStateHash.AlgoVersion</c> stays 2.
        /// 14 = Story 7.14 — the typed graph walk (<see cref="MixGraphNode"/>) is EXTENDED to fold the objective-id
        /// semantic field of the three new node kinds <c>show_objective</c>/<c>complete_objective</c>/<c>fail_objective</c>
        /// (an explicit arm each — the <c>default</c> arm folds only the type name and would let two objective actions
        /// differing only by target collide → MP handshake desync). The authored <c>objectives</c> array itself is
        /// hash-EXCLUDED (authoring/presentation data on the <c>variables</c>/<c>display_name</c> basis). A scenario
        /// carrying NONE of the three kinds folds BYTE-IDENTICALLY apart from this leading AlgoVersion mix — so only the
        /// <c>hero-start-state</c> golden re-records (the 7.5/7.11/7.13 graph-walk-extension precedent).
        /// <c>StartStateHash.AlgoVersion</c> stays 2.</summary>
        public const int AlgoVersion = 14;

        private const ulong Offset = 14695981039346656037UL; // FNV-64 offset basis
        private const ulong Prime  = 1099511628211UL;        // FNV-64 prime

        /// <summary>Compute the 64-bit canonical hash of <paramref name="m"/>. Never returns 0 (sentinel).</summary>
        public static ulong Compute(ScenarioData m)
        {
            ulong h = Offset;

            h = MixInt(h, AlgoVersion);                          // namespaces the hash (algo-1 was the byte-FNV)
            h = MixInt(h, Fixed.FromFloat(m.MapBounds).Raw);
            h = MixStr(h, m.WinCondition.ToString());            // enum by NAME, not ordinal
            // Story 7.11 (v12): fold the win-condition PRESET spec immediately after the built-in WinCondition enum.
            // A null / None spec folds NOTHING (byte-identical to the pre-7.11 fold apart from the AlgoVersion bump —
            // the omit-when-default discipline); a preset folds a present marker + kind name + its params so two
            // peers with divergent victory rules reject at the lobby handshake.
            h = MixWinConditionSpec(h, m.WinConditionSpec);
            // Story 6.2: TerrainRef is NEUTRALIZED (a fixed "" constant, never the field value). The sculpted
            // terrain CONTENT lives in separate terrain3d_*.res files referenced by this path — it is NEVER folded
            // into the scenario model — so the ref string itself is machine-specific noise: an author's map carries
            // res://…/{stem}_terrain while a friend's imported copy carries res://…/{id}_terrain/ (different stem +
            // trailing slash) for the IDENTICAL logical map. Folding the raw value would make those two hash
            // differently and false-positive-reject the map at the MP lobby handshake (LobbyUi.ScenarioHash) and
            // desync StartStateHash. Mixing a fixed "" is byte-identical to today's fold for every existing scenario
            // (all have TerrainRef==""), so this is golden-preserving — AlgoVersion stays 5, no re-baseline of any
            // shipped-scenario golden (only the HeroStartStateScenario test fixture, uniquely carrying a non-empty
            // TerrainRef, re-records once by design — human-authorized 2026-07-14).
            h = MixStr(h, "");

            // Story 6.5 (v6): fold the authored PATHABILITY layer — lockstep-critical because it feeds MOVEMENT
            // (unit paths → Position, which IS in SimChecksum). The painted bitset folds via its content DIGEST
            // (PathabilityGrid.DigestOfBase64: FNV over the packed bytes, normalized so an all-clear / absent layer
            // digests to 0), NOT the raw base64 string — so two encodings of the same blocked set hash equally and an
            // omitted layer hashes identically to an all-clear one (every existing scenario is unaffected). The slope
            // CONFIG (toggle + quantized threshold) folds alongside so a mismatched auto-block setting is handshake-
            // rejectable; the slope-DERIVED cells themselves ride the terrain heightmap (TerrainRef is neutralized
            // above) and are recomputed deterministically at load, inheriting 6.3's accepted terrain-not-in-handshake
            // residual. This is the deliberate INVERSE of the Regions exclusion (see the AlgoVersion doc).
            h = MixInt(h, unchecked((int)PathabilityGrid.DigestOfBase64(m.PathabilityBlocked)));
            h = MixInt(h, m.SlopeAutoBlock ? 1 : 0);
            h = MixInt(h, Fixed.FromFloat(m.SlopeBlockThreshold).Raw);

            // Story 6.6 (v7): fold the BLOCKING-prop + WATER footprints as a single content DIGEST over their union of
            // FlowField.WorldToCell cells (canonical, order-independent — the packed-bitset FNV normalizes to 0 when
            // nothing blocks). Only blocks_pathing props and water volumes contribute; a non-blocking prop, a camera,
            // and every rotation/scale are cosmetic and never reach this fold. This is the deliberate INVERSE-free
            // extension of the 5→6 pathability fold (see the AlgoVersion doc): blocking props/water become blocked
            // cells in the same PathabilityGrid, so they are lockstep-critical exactly as painted cells are.
            h = MixInt(h, unchecked((int)BlockingFootprintDigest(m.Props, m.Water)));

            // Story 4.4: fold Supply via the SAME SupplyConfig.Resolve ResourceStore.ConfigureSupply uses — the
            // single resolution+clamp boundary, so hash-equality <=> post-resolution runtime-equality holds both
            // ways (see class doc). HardCeiling's presence is folded as an explicit bit BEFORE its value: null and
            // any concrete int (including an authored, shadow-mode-reachable invalid negative one, now clamped to
            // 0) are unambiguously distinguishable — unlike a bare `?? sentinel`, which would collide.
            (int supplyStartingCap, int? supplyHardCeiling, bool supplyEnabled) = SupplyConfig.Resolve(m.Supply);
            h = MixInt(h, supplyStartingCap);
            h = MixInt(h, supplyHardCeiling.HasValue ? 1 : 0);
            h = MixInt(h, supplyHardCeiling ?? 0);
            h = MixInt(h, supplyEnabled ? 1 : 0);

            // Sort each collection by a TOTAL order over EVERY folded field (not just a primary key) so neither
            // input/file order NOR a tie on a partial key can move the hash. Numeric sort keys use the same
            // quantized Fixed.Raw the payload folds (a raw-float key could order two values that quantize equal);
            // string keys use an ORDINAL comparer (the default string comparer is culture-sensitive). [Story 1.7 review]
            foreach (ScenarioPlayerSlot s in (m.PlayerSlots ?? Array.Empty<ScenarioPlayerSlot>())
                         .OrderBy(x => x.Slot).ThenBy(x => x.FactionJson, StringComparer.Ordinal)
                         .ThenBy(x => Fixed.FromFloat(x.StartOre).Raw)
                         .ThenBy(x => Fixed.FromFloat(x.StartCrystal).Raw)
                         .ThenBy(x => Fixed.FromFloat(x.BaseX).Raw).ThenBy(x => Fixed.FromFloat(x.BaseZ).Raw))
            {
                h = MixInt(h, s.Slot);
                h = MixStr(h, s.FactionJson);
                h = MixInt(h, Fixed.FromFloat(s.StartOre).Raw);
                h = MixInt(h, Fixed.FromFloat(s.StartCrystal).Raw); // Story 2.9b follow-up: sim-affecting start-state (v3)
                h = MixInt(h, Fixed.FromFloat(s.BaseX).Raw);
                h = MixInt(h, Fixed.FromFloat(s.BaseZ).Raw);
            }

            foreach (ScenarioResourceNode n in (m.ResourceNodes ?? Array.Empty<ScenarioResourceNode>())
                         .OrderBy(x => Fixed.FromFloat(x.X).Raw).ThenBy(x => Fixed.FromFloat(x.Z).Raw)
                         .ThenBy(x => Fixed.FromFloat(x.Supply).Raw)
                         .ThenBy(x => Fixed.FromFloat(x.Rate).Raw).ThenBy(x => x.MaxGatherers)
                         // Story 4.7 — the 6 new fields complete the total order (class-doc requirement: EVERY
                         // folded field, not just a partial key). Nullable RequiresStructure sorts via Ordinal
                         // (null-safe).
                         .ThenBy(x => x.CollectionModel, StringComparer.Ordinal)
                         .ThenBy(x => x.ResourceType, StringComparer.Ordinal)
                         .ThenBy(x => x.RequiresStructure, StringComparer.Ordinal)
                         .ThenBy(x => Fixed.FromFloat(x.RequiresStructureRadius).Raw)
                         .ThenBy(x => x.OwnerSlot)
                         .ThenBy(x => x.IncomePeriodTicks))
            {
                h = MixInt(h, Fixed.FromFloat(n.X).Raw);
                h = MixInt(h, Fixed.FromFloat(n.Z).Raw);
                h = MixInt(h, Fixed.FromFloat(n.Supply).Raw);
                h = MixInt(h, Fixed.FromFloat(n.Rate).Raw);
                h = MixInt(h, n.MaxGatherers);
                // Story 4.7 (v5): the 6 new authored fields.
                h = MixStr(h, n.CollectionModel);
                h = MixStr(h, n.ResourceType);
                // Review patch: normalize "" the SAME way ScenarioApplier.cs does (IsNullOrEmpty -> null) so an
                // omitted requires_structure and an explicitly-authored "" (both mean "no gate") hash IDENTICALLY —
                // otherwise two behaviorally-equivalent scenarios would false-positive-mismatch at the lobby handshake.
                h = MixStr(h, string.IsNullOrEmpty(n.RequiresStructure) ? null : n.RequiresStructure);
                h = MixInt(h, Fixed.FromFloat(n.RequiresStructureRadius).Raw);
                h = MixInt(h, n.OwnerSlot);
                h = MixInt(h, n.IncomePeriodTicks);
            }

            foreach (ScenarioBuilding b in (m.Buildings ?? Array.Empty<ScenarioBuilding>())
                         .OrderBy(x => x.Slot).ThenBy(x => x.Type, StringComparer.Ordinal)
                         .ThenBy(x => Fixed.FromFloat(x.X).Raw).ThenBy(x => Fixed.FromFloat(x.Z).Raw)
                         .ThenBy(x => x.PreBuilt))
            {
                h = MixStr(h, b.Type);
                h = MixInt(h, b.Slot);
                h = MixInt(h, Fixed.FromFloat(b.X).Raw);
                h = MixInt(h, Fixed.FromFloat(b.Z).Raw);
                h = MixInt(h, b.PreBuilt ? 1 : 0);
            }

            foreach (ScenarioUnit u in (m.Units ?? Array.Empty<ScenarioUnit>())
                         .OrderBy(x => x.Slot).ThenBy(x => x.UnitId, StringComparer.Ordinal)
                         .ThenBy(x => Fixed.FromFloat(x.X).Raw).ThenBy(x => Fixed.FromFloat(x.Z).Raw))
            {
                h = MixStr(h, u.UnitId);
                h = MixInt(h, u.Slot);
                h = MixInt(h, Fixed.FromFloat(u.X).Raw);
                h = MixInt(h, Fixed.FromFloat(u.Z).Raw);
            }

            // ── Story 7.7 (v8): the trigger/DSL sim-semantic model, appended after the v7 fields in fixed order.
            //    Each collection folds a count prefix (so adjacent collections cannot alias) and null ≡ empty
            //    (count 0 — a legacy scenario without a block hashes identically to one carrying an empty one). ──
            h = MixRegions(h, m.Regions);
            h = MixTriggers(h, m.Triggers);
            h = MixVariables(h, m.Variables);
            h = MixTimers(h, m.Timers);
            h = MixTriggerGraph(h, m.TriggerGraphJson);

            // ── Story 7.8 (v9): the custom-UI widget tree, appended after the v8 trigger-graph fold. A typed
            //    recursive walk (never JSON bytes; the per-widget `_editor` bag excluded by construction). ──
            h = MixCustomUi(h, m.CustomUi);

            // ── Story 7.5 (v10, landed via merge): the custom-event registry, appended after the v9 custom-UI
            //    fold. Sim-semantic trigger-DSL model (param slots/types drive the dispatch frames) — a divergent
            //    registry must reject at the handshake, the exact declaration-fold class 7.7's v8 closed. ──
            h = MixCustomEvents(h, m.CustomEvents);

            return h == 0UL ? 1UL : h; // sentinel: a valid model must never hash to the fail-open "no hash" value
        }

        /// <summary>
        /// Story 7.11 (v12): the win-condition PRESET spec — a typed fold appended after the built-in
        /// <see cref="ScenarioData.WinCondition"/> enum. A null or <see cref="WinPresetKind.None"/> spec folds
        /// NOTHING (returns h unchanged) so a no-preset scenario is byte-identical to the pre-7.11 fold apart from the
        /// AlgoVersion bump — the omit-when-default discipline (the serializer normalizes a None spec to null). A
        /// preset folds a 1 present-marker, the kind NAME (enum by stable name), then that preset's params in fixed
        /// order. The optional <c>RegionId</c> string folds via <see cref="MixStr"/>'s null-length marker.
        /// </summary>
        private static ulong MixWinConditionSpec(ulong h, WinConditionSpec? spec)
        {
            if (spec is null || spec.Preset == WinPresetKind.None) return h; // no preset — fold nothing

            h = MixInt(h, 1);                        // present marker
            h = MixStr(h, spec.Preset.ToString());   // enum by stable NAME
            switch (spec.Preset)
            {
                case WinPresetKind.KingOfTheHill:
                    h = MixStr(h, spec.RegionId);
                    h = MixInt(h, spec.HoldTicks);
                    break;
                case WinPresetKind.TimedSurvival:
                    h = MixInt(h, spec.FactionSlot);
                    h = MixInt(h, spec.SurviveTicks);
                    break;
                case WinPresetKind.Assassination:
                    h = MixInt(h, spec.LeaderUnitIndex);
                    break;
                case WinPresetKind.LandmarkDestruction:
                    h = MixInt(h, spec.StructureIndex);
                    break;
            }
            return h;
        }

        // ── Story 7.7 (v8) folds ─────────────────────────────────────────────────────────────────────────────

        /// <summary>Regions (v8): TOTAL-ordered by (Id ordinal, then every quantized corner) so array order can
        /// never move the hash. <c>Name</c> is a cosmetic overlay label (the <c>DisplayName</c> basis) — excluded.
        /// Null rows fold a -1 marker (never throw; the validator rejects them separately).</summary>
        private static ulong MixRegions(ulong h, ScenarioRegion[]? regions)
        {
            ScenarioRegion[] rs = regions ?? Array.Empty<ScenarioRegion>();
            h = MixInt(h, rs.Length);
            foreach (ScenarioRegion? r in rs
                         .OrderBy(x => x?.Id, StringComparer.Ordinal)
                         .ThenBy(x => x is null ? 0 : Fixed.FromFloat(x.MinX).Raw)
                         .ThenBy(x => x is null ? 0 : Fixed.FromFloat(x.MinZ).Raw)
                         .ThenBy(x => x is null ? 0 : Fixed.FromFloat(x.MaxX).Raw)
                         .ThenBy(x => x is null ? 0 : Fixed.FromFloat(x.MaxZ).Raw))
            {
                if (r is null) { h = MixInt(h, -1); continue; }
                h = MixStr(h, r.Id);
                h = MixInt(h, Fixed.FromFloat(r.MinX).Raw);
                h = MixInt(h, Fixed.FromFloat(r.MinZ).Raw);
                h = MixInt(h, Fixed.FromFloat(r.MaxX).Raw);
                h = MixInt(h, Fixed.FromFloat(r.MaxZ).Raw);
            }
            return h;
        }

        /// <summary>Flat triggers (v8): folded in DECLARATION order — trigger order IS semantic (priority ties
        /// break by declaration index), so reordering triggers legitimately moves the hash. Every semantic field
        /// of every event/condition/action folds in fixed order (Fixed via .Raw). Null rows fold -1 markers.</summary>
        private static ulong MixTriggers(ulong h, TriggerDefinition[]? triggers)
        {
            TriggerDefinition[] ts = triggers ?? Array.Empty<TriggerDefinition>();
            h = MixInt(h, ts.Length);
            foreach (TriggerDefinition? t in ts)
            {
                if (t is null) { h = MixInt(h, -1); continue; }
                h = MixStr(h, t.Name);
                h = MixInt(h, t.Enabled ? 1 : 0);
                h = MixInt(h, t.RunOnce ? 1 : 0);
                h = MixInt(h, t.CooldownSeconds.Raw);
                h = MixInt(h, t.Priority);

                TriggerEvent[] events = t.Events ?? Array.Empty<TriggerEvent>();
                h = MixInt(h, events.Length);
                foreach (TriggerEvent? e in events)
                {
                    if (e is null) { h = MixInt(h, -1); continue; }
                    h = MixStr(h, e.Type);
                    h = MixInt(h, e.Faction);
                    h = MixStr(h, e.BuildingType);
                    h = MixStr(h, e.TimerName);
                    h = MixInt(h, e.Amount.Raw);
                    h = MixInt(h, e.Count);
                    h = MixStr(h, e.Operator);
                }

                TriggerCondition[] conds = t.Conditions ?? Array.Empty<TriggerCondition>();
                h = MixInt(h, conds.Length);
                foreach (TriggerCondition? c in conds)
                {
                    if (c is null) { h = MixInt(h, -1); continue; }
                    h = MixStr(h, c.Type);
                    h = MixInt(h, c.Faction);
                    h = MixStr(h, c.BuildingType);
                    h = MixInt(h, c.Amount.Raw);
                    h = MixInt(h, c.Count);
                    h = MixStr(h, c.Variable);
                    h = MixStr(h, c.RegionId);
                    h = MixInt(h, c.Value);
                    h = MixStr(h, c.Operator);
                }

                TriggerAction[] actions = t.Actions ?? Array.Empty<TriggerAction>();
                h = MixInt(h, actions.Length);
                foreach (TriggerAction? a in actions)
                {
                    if (a is null) { h = MixInt(h, -1); continue; }
                    // NOTE: this FLAT-channel action fold order (…Amount, Variable, Value, SoundId) differs from
                    // MixGraphNode's graph-channel order (…Amount, Value, Variable, SoundId). Both are channel-local
                    // and FROZEN as-shipped in v8 — reordering either is an AlgoVersion bump, not a cleanup.
                    h = MixStr(h, a.Type);
                    h = MixStr(h, a.UnitId);
                    h = MixInt(h, a.Faction);
                    h = MixInt(h, a.X.Raw);
                    h = MixInt(h, a.Z.Raw);
                    h = MixInt(h, a.Count);
                    h = MixStr(h, a.Text);
                    h = MixInt(h, a.Duration.Raw);
                    h = MixStr(h, a.TimerName);
                    h = MixInt(h, a.TimerSeconds.Raw);
                    h = MixInt(h, a.Amount.Raw);
                    h = MixStr(h, a.Variable);
                    h = MixInt(h, a.Value);
                    h = MixStr(h, a.SoundId);
                }
            }
            return h;
        }

        /// <summary>Variable declarations (v8): DECLARATION order (the live-value SimChecksum fold follows it, so
        /// order is semantic). Enums by NAME; the Fixed initial by .Raw; the Array element/capacity fields fold a
        /// presence bit before their value (the HardCeiling convention).</summary>
        private static ulong MixVariables(ulong h, ScenarioVariable[]? variables)
        {
            ScenarioVariable[] vs = variables ?? Array.Empty<ScenarioVariable>();
            h = MixInt(h, vs.Length);
            foreach (ScenarioVariable? v in vs)
            {
                if (v is null) { h = MixInt(h, -1); continue; }
                h = MixStr(h, v.Name);
                h = MixStr(h, v.Type.ToString());
                h = MixStr(h, v.Scope.ToString());
                h = MixInt(h, v.Initial.Raw);
                h = MixInt(h, v.ElementType.HasValue ? 1 : 0);
                h = MixStr(h, v.ElementType?.ToString());
                h = MixInt(h, v.Capacity.HasValue ? 1 : 0);
                h = MixInt(h, v.Capacity ?? 0);
            }
            return h;
        }

        /// <summary>Timer declarations (v8): DECLARATION order; the Fixed duration by .Raw.</summary>
        private static ulong MixTimers(ulong h, ScenarioTimer[]? timers)
        {
            ScenarioTimer[] ts = timers ?? Array.Empty<ScenarioTimer>();
            h = MixInt(h, ts.Length);
            foreach (ScenarioTimer? t in ts)
            {
                if (t is null) { h = MixInt(h, -1); continue; }
                h = MixStr(h, t.Name);
                h = MixInt(h, t.Seconds.Raw);
            }
            return h;
        }

        /// <summary>Fixed sentinel folded when <c>TriggerGraphJson</c> is present but unparseable — Compute stays
        /// pure/never-throw (the gate rejects the scenario separately; this hash just never matches a real one).</summary>
        private const int UnparseableGraphSentinel = -2;

        /// <summary>
        /// Story 7.7 perf — the single-entry PARSE MEMO behind <see cref="MixTriggerGraph"/>. Parsing a max-caps
        /// graph dominates the v8 fold (tens of ms), and <see cref="Compute"/> runs several times per load over the
        /// SAME immutable <c>TriggerGraphJson</c> string (the wire hash, the <see cref="StartStateHash"/> content
        /// seed, the F5 recompute) — so the last (json → parsed graph) pair is cached. PURE: the parse is a
        /// deterministic function of the immutable string, so the fold output is byte-identical with or without the
        /// memo; a null entry means "unparseable" (the sentinel re-folds without re-throwing the parse). Benign
        /// race under concurrency (worst case a duplicate parse); memory is bounded by one live graph.
        /// </summary>
        private static volatile GraphParseMemo? _graphMemo;

        private sealed class GraphParseMemo
        {
            public readonly string Json;
            public readonly ProjectChimera.Dsl.TriggerGraph? Graph; // null = unparseable (folds the sentinel)
            public GraphParseMemo(string json, ProjectChimera.Dsl.TriggerGraph? graph) { Json = json; Graph = graph; }
        }

        /// <summary>TEST HOOK (Story 7.7 review) — drop the parse memo so a perf test can measure the COLD
        /// (first-parse) compute per sample; the memo's ordinal CONTENT equality means distinct string instances
        /// alone cannot defeat it. Never called by production code; the memo refills on the next Compute.</summary>
        internal static void ClearGraphMemoForTests() => _graphMemo = null;

        /// <summary>
        /// The parsed <c>TriggerGraphJson</c> graph IR (v8): a TYPED fold — nodes ascending id (kind string + each
        /// semantic field in fixed order; <c>Fixed</c> via .Raw; enums as stable names), exec edges sorted
        /// <c>(Src,SrcPort,Dst,DstPort)</c>, data edges sorted likewise + wire name. NEVER the ToCanonicalJson
        /// bytes (the ledgered cross-runtime string-format risk). Absent/whitespace folds a 0 marker; an
        /// unparseable payload folds <see cref="UnparseableGraphSentinel"/>. The per-node <c>_editor</c> bag is
        /// excluded BY CONSTRUCTION — this fold reads typed fields only.
        /// </summary>
        private static ulong MixTriggerGraph(ulong h, string? triggerGraphJson)
        {
            if (string.IsNullOrWhiteSpace(triggerGraphJson)) return MixInt(h, 0); // absent (≡ the serializer's empty→null normalization)

            // Parse-memo hit? (Ordinal string equality — negligible next to a re-parse; see _graphMemo doc.)
            GraphParseMemo? memo = _graphMemo;
            if (memo is null || !string.Equals(memo.Json, triggerGraphJson, StringComparison.Ordinal))
            {
                ProjectChimera.Dsl.TriggerGraph? parsed;
                try
                {
                    parsed = ProjectChimera.Dsl.TriggerGraph.FromJson(triggerGraphJson!);
                }
                catch (Exception)
                {
                    parsed = null; // never throw — the validator rejects it located; the fold uses the sentinel
                }
                memo = new GraphParseMemo(triggerGraphJson!, parsed);
                _graphMemo = memo;
            }

            ProjectChimera.Dsl.TriggerGraph? graph = memo.Graph;
            if (graph is null)
                return MixInt(h, UnparseableGraphSentinel);

            // Review follow-up: a hand-authored EMPTY graph ({"nodes":[]…}) is semantically identical to an absent
            // one — fold the SAME absent marker so the two can never false-positive-mismatch at the lobby (the
            // serializer already normalizes whitespace-only to null; this closes the parsed-empty case). No
            // committed fixture authors an empty graph, so no golden/pin moves.
            if (graph.Nodes.Count == 0 && graph.ExecEdges.Count == 0 && graph.DataEdges.Count == 0)
                return MixInt(h, 0);

            h = MixInt(h, 1); // present marker

            h = MixInt(h, graph.Nodes.Count);
            foreach (ProjectChimera.Dsl.NodeBase n in graph.Nodes.OrderBy(x => x.Id))
                h = MixGraphNode(h, n);

            h = MixInt(h, graph.ExecEdges.Count);
            foreach (ProjectChimera.Dsl.ExecEdge e in graph.ExecEdges.OrderBy(x => x))
            {
                h = MixInt(h, e.Src);
                h = MixInt(h, e.SrcPort);
                h = MixInt(h, e.Dst);
                h = MixInt(h, e.DstPort);
            }

            h = MixInt(h, graph.DataEdges.Count);
            // DataEdge.CompareTo is total on the (Src,SrcPort,Dst,DstPort) topology tuple only (wire is
            // deliberately not a sort key there). Since the fold ALSO emits the wire, add wire as an explicit
            // tiebreaker so the ordering is total over every folded field — delivering this method's documented
            // "data edges sorted likewise + wire name" guarantee. Duplicate-topology data edges are rejected
            // upstream by GraphStructureGate's forked-data-in check, so this tie is unreachable on any gate-passed
            // model and moves no sanctioned hash; it only makes the fold order-independent on un-gated paths.
            foreach (ProjectChimera.Dsl.DataEdge e in graph.DataEdges.OrderBy(x => x).ThenBy(x => x.Wire))
            {
                h = MixInt(h, e.Src);
                h = MixInt(h, e.SrcPort);
                h = MixInt(h, e.Dst);
                h = MixInt(h, e.DstPort);
                h = MixStr(h, e.Wire.ToString()); // wire name (enum by stable NAME)
            }

            return h;
        }

        /// <summary>One graph node (v8): id, kind string, then that kind's semantic fields in the converter's
        /// field order (Fixed via .Raw, enums as names). The <c>_editor</c> bag is never read here.</summary>
        private static ulong MixGraphNode(ulong h, ProjectChimera.Dsl.NodeBase n)
        {
            h = MixInt(h, n.Id);
            switch (n)
            {
                case ProjectChimera.Dsl.TriggerNode t:
                    h = MixStr(h, t.Kind);
                    h = MixStr(h, t.Name);
                    h = MixInt(h, t.Enabled ? 1 : 0);
                    h = MixInt(h, t.RunOnce ? 1 : 0);
                    h = MixInt(h, t.CooldownSeconds.Raw);
                    h = MixInt(h, t.Priority);
                    break;
                case ProjectChimera.Dsl.EventNode e:
                    h = MixStr(h, e.Kind);
                    h = MixInt(h, e.Faction);
                    h = MixStr(h, e.BuildingType);
                    h = MixStr(h, e.TimerName);
                    h = MixInt(h, e.Amount.Raw);
                    h = MixInt(h, e.Count);
                    h = MixStr(h, e.Operator);
                    // Story 7.5 (v10): the custom_event subscription target, appended AFTER the frozen v8 fields.
                    // Built-in event kinds carry a null EventName (folds MixStr's -1 null marker — byte-stable for
                    // every pre-7.5 graph modulo the version bump); two graphs differing only in the subscribed
                    // custom event must hash differently.
                    h = MixStr(h, e.EventName);
                    break;
                case ProjectChimera.Dsl.ConditionNode c:
                    h = MixStr(h, c.Kind);
                    h = MixInt(h, c.Faction);
                    h = MixStr(h, c.BuildingType);
                    h = MixInt(h, c.Amount.Raw);
                    h = MixInt(h, c.Count);
                    h = MixStr(h, c.Variable);
                    h = MixStr(h, c.RegionId);
                    h = MixInt(h, c.Value);
                    h = MixStr(h, c.Operator);
                    break;
                case ProjectChimera.Dsl.ActionNode a:
                    // NOTE: this GRAPH-channel action fold order (…Amount, Value, Variable, SoundId) differs from
                    // MixTriggers' flat-channel order (…Amount, Variable, Value, SoundId). Both are channel-local
                    // and FROZEN as-shipped in v8 — reordering either is an AlgoVersion bump, not a cleanup.
                    h = MixStr(h, a.Kind);
                    h = MixStr(h, a.UnitId);
                    h = MixInt(h, a.Faction);
                    h = MixInt(h, a.X.Raw);
                    h = MixInt(h, a.Z.Raw);
                    h = MixInt(h, a.Count);
                    h = MixStr(h, a.Text);
                    h = MixInt(h, a.Duration.Raw);
                    h = MixStr(h, a.TimerName);
                    h = MixInt(h, a.TimerSeconds.Raw);
                    h = MixInt(h, a.Amount.Raw);
                    h = MixInt(h, a.Value);
                    h = MixStr(h, a.Variable);
                    h = MixStr(h, a.SoundId);
                    break;
                case ProjectChimera.Dsl.EffectActionNode ea:
                    h = MixStr(h, ea.Kind);
                    // Story 9.16: the embedded effect subgraph folds through the SHARED CanonicalFold walk (extracted
                    // verbatim from this class), so a run_effect embed and an ability/item EffectGraph hash identically.
                    // Byte-preserving — CanonicalFold's MixInt/MixStr are the same FNV primitives this class uses.
                    h = CanonicalFold.MixEffect(h, ea.Effect);
                    break;
                case ProjectChimera.Dsl.ExprLiteralNode lit:
                    h = MixStr(h, lit.Kind);
                    h = MixStr(h, lit.ValueType.ToString());
                    h = MixInt(h, lit.Raw);
                    break;
                case ProjectChimera.Dsl.ExprVarNode ev:
                    h = MixStr(h, ev.Kind);
                    h = MixStr(h, ev.Name);
                    h = MixInt(h, ev.Faction);
                    break;
                case ProjectChimera.Dsl.ExprUnaryNode eu:
                    h = MixStr(h, eu.Kind);
                    h = MixStr(h, eu.Op);
                    break;
                case ProjectChimera.Dsl.ExprBinaryNode eb:
                    h = MixStr(h, eb.Kind);
                    h = MixStr(h, eb.Op);
                    break;
                case ProjectChimera.Dsl.ExprCallNode ec:
                    h = MixStr(h, ec.Kind);
                    h = MixStr(h, ec.Fn);
                    // Story 7.13 (v13): the state-read SELECTOR (unit_count_tag/category/player_resource/
                    // region_unit_count), appended after Fn — OMIT-WHEN-DEFAULT (the discipline every other fold
                    // observes). A state read always carries a non-empty validated selector, so it stays
                    // discriminated; a count()/distance node (empty selector) mixes NOTHING here, folding
                    // byte-identically to the v12 arm apart from the version bump. Two graphs differing only in a
                    // (present) state-read selector still hash differently (closes the Arm A handshake gap).
                    if (!string.IsNullOrEmpty(ec.Selector)) h = MixStr(h, ec.Selector);
                    break;
                case ProjectChimera.Dsl.ForEachNode fe:
                    h = MixStr(h, fe.Kind);
                    h = MixStr(h, fe.Source);
                    h = MixStr(h, fe.ArrayName);
                    h = MixInt(h, fe.Faction);
                    h = MixStr(h, fe.RegionId);
                    h = MixInt(h, fe.UpTo);
                    h = MixStr(h, fe.LoopVar);
                    break;
                case ProjectChimera.Dsl.ForEachBatchedNode fb:
                    h = MixStr(h, fb.Kind);
                    h = MixStr(h, fb.Source);
                    h = MixInt(h, fb.Faction);
                    h = MixStr(h, fb.RegionId);
                    h = MixInt(h, fb.BatchSize);
                    break;
                case ProjectChimera.Dsl.BranchNode br:
                    h = MixStr(h, br.Kind);
                    break;
                case ProjectChimera.Dsl.ExprArrayGetNode ag:
                    h = MixStr(h, ag.Kind);
                    h = MixStr(h, ag.Name);
                    break;
                case ProjectChimera.Dsl.ExprArrayLenNode al:
                    h = MixStr(h, al.Kind);
                    h = MixStr(h, al.Name);
                    break;
                case ProjectChimera.Dsl.RaiseEventNode re:
                    // Story 7.5 (v10): every semantic raise field folds — Name (the raise target), Raiser slot,
                    // and the same-tick/next-tick edge bit (bool as 0/1, the sibling-case primitive).
                    h = MixStr(h, re.Kind);
                    h = MixStr(h, re.Name);
                    h = MixInt(h, re.Raiser);
                    h = MixInt(h, re.NextTick ? 1 : 0);
                    break;
                case ProjectChimera.Dsl.ExprEventParamNode ep:
                    // Story 7.5 (v10): the event.<param> leaf — the param name is its only semantic field.
                    h = MixStr(h, ep.Kind);
                    h = MixStr(h, ep.Name);
                    break;

                // ── Story 7.13 (v13): the four action-leaf kinds (Arm A left them on the type-name `default` arm,
                //    folding presence but NOT field values — a handshake gap) + the weighted container + the three
                //    trigger-control leaves. Each folds its semantic fields so two scenarios differing only in an
                //    order target / camera / vfx / weighted structure / target trigger hash differently. ──
                case ProjectChimera.Dsl.OrderUnitsNode ou:
                    h = MixStr(h, ou.Kind);
                    h = MixStr(h, ou.Command);
                    h = MixInt(h, ou.Faction);
                    h = MixStr(h, ou.RegionId);
                    h = MixInt(h, ou.X.Raw);
                    h = MixInt(h, ou.Z.Raw);
                    break;
                case ProjectChimera.Dsl.MoveCameraNode mc:
                    h = MixStr(h, mc.Kind);
                    h = MixStr(h, mc.CameraName);
                    break;
                case ProjectChimera.Dsl.CinematicModeNode cm:
                    h = MixStr(h, cm.Kind);
                    h = MixInt(h, cm.Enabled ? 1 : 0);
                    break;
                case ProjectChimera.Dsl.PlayVfxNode pv:
                    h = MixStr(h, pv.Kind);
                    h = MixStr(h, pv.VfxId);
                    h = MixInt(h, pv.X.Raw);
                    h = MixInt(h, pv.Z.Raw);
                    break;
                case ProjectChimera.Dsl.RandomChoiceNode rc:
                    h = MixStr(h, rc.Kind);
                    h = MixInt(h, rc.Weights.Length);           // the weighted-branch structure
                    foreach (int w in rc.Weights) h = MixInt(h, w);
                    break;
                case ProjectChimera.Dsl.EnableTriggerNode en:
                    h = MixStr(h, en.Kind);
                    h = MixInt(h, en.TargetTriggerId);
                    break;
                case ProjectChimera.Dsl.DisableTriggerNode di:
                    h = MixStr(h, di.Kind);
                    h = MixInt(h, di.TargetTriggerId);
                    break;
                case ProjectChimera.Dsl.RunTriggerNode rt:
                    h = MixStr(h, rt.Kind);
                    h = MixInt(h, rt.TargetTriggerId);
                    break;

                // ── Story 7.14 (v14): the three objective action-leaf kinds. Each folds its objective-id semantic
                //    field (an explicit arm — never the type-name-only `default` fold) so two objective actions
                //    differing only by target hash differently at the handshake. ──
                case ProjectChimera.Dsl.ShowObjectiveNode so:
                    h = MixStr(h, so.Kind);
                    h = MixStr(h, so.ObjectiveId);
                    break;
                case ProjectChimera.Dsl.CompleteObjectiveNode co:
                    h = MixStr(h, co.Kind);
                    h = MixStr(h, co.ObjectiveId);
                    break;
                case ProjectChimera.Dsl.FailObjectiveNode fo:
                    h = MixStr(h, fo.Kind);
                    h = MixStr(h, fo.ObjectiveId);
                    break;

                default:
                    // Unreachable through FromJson (closed registry); fold the runtime type name so Compute stays
                    // total/never-throw even for a hand-constructed future node.
                    h = MixStr(h, n.GetType().Name);
                    break;
            }
            return h;
        }

        // Story 9.16: the typed effect-tree walk (MixEffect/MixModifier) moved verbatim to the shared
        // CanonicalFold helper so this class and ContentHash fold an effect subgraph byte-identically (one
        // implementation, no drift). CanonicalFold.MixEffect/MixModifier replace the former private methods here;
        // the call site above (EffectActionNode) delegates to it. Output is unchanged (goldens guard it).

        /// <summary>
        /// Story 7.8 (v9): the custom-UI widget tree — a TYPED recursive fold (never JSON bytes). Absent OR empty
        /// (no widgets) folds a 0 marker (byte-identical to the pre-7.8 v8 fold, and identical to a normalized-empty
        /// tree); a present tree folds a 1 marker, the canvas dims, a widget count prefix, then each root widget in
        /// AUTHORED (render) order — order is semantic (z-order). The per-widget <c>_editor</c> annotation bag is
        /// EXCLUDED by construction (<see cref="MixWidget"/> reads typed fields only).
        /// </summary>
        private static ulong MixCustomUi(ulong h, CustomUiTree? tree)
        {
            WidgetBase[] widgets = tree?.Widgets ?? Array.Empty<WidgetBase>();
            if (tree is null || widgets.Length == 0) return MixInt(h, 0); // absent/empty (≡ the serializer's empty→null)

            h = MixInt(h, 1); // present marker
            h = MixInt(h, tree.CanvasWidth);
            h = MixInt(h, tree.CanvasHeight);
            h = MixInt(h, widgets.Length);
            foreach (WidgetBase w in widgets)
                h = MixWidget(h, w);
            return h;
        }

        /// <summary>One widget (v9): id, kind name, anchor name, int offset/size, optional visibility bind, then that
        /// kind's semantic fields in fixed order, then a child count prefix and each child recursed. Null child folds
        /// a -1 marker. The <c>_editor</c> bag is never read here.</summary>
        private static ulong MixWidget(ulong h, WidgetBase? w)
        {
            if (w is null) return MixInt(h, -1);
            h = MixInt(h, w.Id);
            h = MixStr(h, w.Kind.ToString());   // enum by stable NAME
            h = MixStr(h, w.Anchor.ToString()); // enum by stable NAME
            h = MixInt(h, w.X);
            h = MixInt(h, w.Y);
            h = MixInt(h, w.W);
            h = MixInt(h, w.H);
            h = MixStr(h, w.VisibleBind);
            switch (w)
            {
                case PanelWidget:
                    break;
                case LabelWidget l:
                    h = MixStr(h, l.Text);
                    h = MixStr(h, l.Bind);
                    break;
                case CounterWidget c:
                    h = MixStr(h, c.Bind);
                    break;
                case ProgressBarWidget p:
                    h = MixStr(h, p.Bind);
                    h = MixInt(h, p.Max);
                    break;
                case TimerWidget t:
                    h = MixStr(h, t.Bind);
                    break;
                case LeaderboardWidget lb:
                    h = MixStr(h, lb.Bind);
                    h = MixInt(h, lb.Rows);
                    break;
                case FloatingTextWidget ft:
                    h = MixStr(h, ft.Text);
                    h = MixStr(h, ft.Bind);
                    break;
                case ItemListWidget il:
                    h = MixStr(h, il.Bind);
                    h = MixInt(h, il.Rows);
                    break;
                case ButtonWidget btn:
                    // Story 7.9 (v11): the write-rail button. Fold Text, EventName, an arg-count prefix + each authored
                    // arg RAW (ArgTypes is authoring/gate-only — NOT folded), the LocalAction enum by NAME, then its
                    // target (widget id / local var name+value). Divergent buttons must reject at the handshake.
                    h = MixStr(h, btn.Text);
                    h = MixStr(h, btn.EventName);
                    int[] args = btn.ArgRaws ?? Array.Empty<int>();
                    h = MixInt(h, args.Length);
                    foreach (int a in args) h = MixInt(h, a);
                    h = MixStr(h, btn.LocalAction.ToString()); // enum by stable NAME
                    h = MixInt(h, btn.LocalTargetWidgetId);
                    h = MixStr(h, btn.LocalVarName);
                    h = MixInt(h, btn.LocalVarValue);
                    break;
                default:
                    h = MixStr(h, w.GetType().Name); // total/never-throw for a future kind
                    break;
            }
            WidgetBase[] children = w.Children ?? Array.Empty<WidgetBase>();
            h = MixInt(h, children.Length);
            foreach (WidgetBase child in children)
                h = MixWidget(h, child);
            return h;
        }

        /// <summary>
        /// Story 7.5 (v10, landed via merge): the custom-event registry — a TYPED fold following the
        /// <see cref="MixVariables"/> conventions: count prefix (null ≡ empty ⇒ 0 — the serializer chokepoint
        /// normalizes empty→null, so both states hash identically), DECLARATION order (order is semantic: the
        /// registry event index is what the live SimChecksum queue fold and the EventDispatchPlan key on). Per
        /// event: Name, a Params count then per param Name + Type (enum by NAME), an AllowedRaisers count then
        /// each declared slot value as-declared (null ≡ empty ⇒ count 0). Null rows fold the -1 marker.
        /// </summary>
        private static ulong MixCustomEvents(ulong h, ScenarioCustomEvent[]? events)
        {
            ScenarioCustomEvent[] es = events ?? Array.Empty<ScenarioCustomEvent>();
            h = MixInt(h, es.Length);
            foreach (ScenarioCustomEvent? ev in es)
            {
                if (ev is null) { h = MixInt(h, -1); continue; }
                h = MixStr(h, ev.Name);

                ScenarioEventParam[] ps = ev.Params ?? Array.Empty<ScenarioEventParam>();
                h = MixInt(h, ps.Length);
                foreach (ScenarioEventParam? p in ps)
                {
                    if (p is null) { h = MixInt(h, -1); continue; }
                    h = MixStr(h, p.Name);
                    h = MixStr(h, p.Type.ToString()); // enum by NAME (the MixVariables convention)
                }

                int[] raisers = ev.AllowedRaisers ?? Array.Empty<int>();
                h = MixInt(h, raisers.Length);
                foreach (int r in raisers)
                    h = MixInt(h, r); // as-declared order (the gate rejects duplicates/out-of-range)
            }
            return h;
        }

        /// <summary>
        /// Fold the 64-bit canonical hash into the existing 32-bit Ready-packet wire (re-applying the 0→1
        /// sentinel). Widening the wire to 64-bit is Epic 9.
        /// </summary>
        public static uint ToWire(ulong h)
        {
            uint w = (uint)(h ^ (h >> 32));
            return w == 0u ? 1u : w;
        }

        /// <summary>
        /// Story 6.6 — the content digest of the BLOCKING-prop + WATER footprint union. Stamps each
        /// <c>blocks_pathing</c> prop's single cell and each water volume's rect cells into one shared
        /// <see cref="ProjectChimera.Navigation.FlowField.WorldToCell"/> mask (the ONE derivation
        /// <c>PathabilityGrid.StampPropInto</c>/<c>StampWaterInto</c> also feed the load-time union and the validator),
        /// then folds it with <see cref="PathabilityGrid.Digest"/>. Order-independent (a mask union) and 0 when nothing
        /// blocks — so an omitted/empty props+water set, or a set with only NON-blocking props, digests to 0
        /// (byte-identical to the pre-6.6 fold). The single float→Fixed quantize here is the sanctioned load-time
        /// boundary (called once per match load).
        /// </summary>
        private static uint BlockingFootprintDigest(ScenarioProp[]? props, ScenarioWater[]? water)
        {
            // Story 6.6 (review V1): the SAME shared derivation the load-time grid + validator use — behaviour-identical
            // to the prior inline loop (same props-then-water stamp order), so the handshake baseline is unmoved.
            bool[]? mask = PathabilityGrid.BuildBlockingFootprint(props, water);
            return mask == null ? 0u : new PathabilityGrid(mask).Digest();
        }

        /// <summary>FNV-64 fold of a 32-bit int as 4 little-endian bytes (mirrors SimChecksum.Mix, 64-bit).</summary>
        private static ulong MixInt(ulong h, int value)
        {
            uint v = (uint)value;
            h ^= v & 0xFF;         h *= Prime;
            h ^= (v >> 8) & 0xFF;  h *= Prime;
            h ^= (v >> 16) & 0xFF; h *= Prime;
            h ^= (v >> 24) & 0xFF; h *= Prime;
            return h;
        }

        /// <summary>
        /// FNV-64 fold of a string: a length prefix (so "ab"+"c" != "a"+"bc", and null != "") followed by the
        /// UTF-8 bytes. Null length is folded as -1.
        /// </summary>
        private static ulong MixStr(ulong h, string? s)
        {
            h = MixInt(h, s?.Length ?? -1);
            if (s == null) return h;
            foreach (byte by in Encoding.UTF8.GetBytes(s))
            {
                h ^= by;
                h *= Prime;
            }
            return h;
        }
    }
}
