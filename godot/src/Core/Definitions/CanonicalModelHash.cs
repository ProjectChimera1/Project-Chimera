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
    ///   • cosmetic <c>Id</c>/<c>DisplayName</c> EXCLUDED, and <c>Triggers</c> EXCLUDED (trigger/effect
    ///     canonicalization is Epic 7 / D3.4 — a known, bounded handshake gap documented in the story);
    ///   • <c>Regions</c> (Story 6.4) EXCLUDED on the SAME basis as <c>Triggers</c>: regions are a *trigger input*
    ///     — referenced by the <c>unit_in_region</c> condition, which CAN gate trigger actions (spawn_unit /
    ///     add_resources / set_variable) that DO mutate <c>SimChecksum</c>-folded state — and Triggers are an
    ///     already-accepted, bounded handshake gap (deferred to Epic 7). When Triggers are folded into the
    ///     handshake, Regions fold with them (and the version bumps then). The Block-If tripwire is a NON-trigger
    ///     sim consumer of region containment. No fold below, <c>AlgoVersion</c> stays excluded (the v6 bump is Story 6.5's
        ///     pathability layer, a separate concern — Regions themselves stay excluded on the Triggers basis).
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
        /// ONE-TIME re-baseline of the handshake fixtures (human-authorized 2026-07-14).</summary>
        public const int AlgoVersion = 7;

        private const ulong Offset = 14695981039346656037UL; // FNV-64 offset basis
        private const ulong Prime  = 1099511628211UL;        // FNV-64 prime

        /// <summary>Compute the 64-bit canonical hash of <paramref name="m"/>. Never returns 0 (sentinel).</summary>
        public static ulong Compute(ScenarioData m)
        {
            ulong h = Offset;

            h = MixInt(h, AlgoVersion);                          // namespaces the hash (algo-1 was the byte-FNV)
            h = MixInt(h, Fixed.FromFloat(m.MapBounds).Raw);
            h = MixStr(h, m.WinCondition.ToString());            // enum by NAME, not ordinal
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

            return h == 0UL ? 1UL : h; // sentinel: a valid model must never hash to the fail-open "no hash" value
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
