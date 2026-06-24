#nullable enable
using System;
using System.Linq;
using System.Text;
using ProjectChimera.Core; // Fixed

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
    ///     canonicalization is Epic 7 / D3.4 — a known, bounded handshake gap documented in the story).
    /// A <c>0 → 1</c> sentinel guarantees a valid model never hashes to the "no hash" value the fail-open
    /// handshake treats as a skip. The 64-bit <see cref="Compute"/> is exposed for Epic 9 to attest later;
    /// <see cref="ToWire"/> folds it to the existing 32-bit Ready-packet wire used today.
    ///
    /// Godot-free (src/Core/Definitions). <c>Fixed.FromFloat</c> here is the sanctioned load-time quantize
    /// (called once per match load, never in-tick).
    /// </summary>
    public static class CanonicalModelHash
    {
        /// <summary>Algorithm version. 1 = the retired byte-FNV file hash; 2 = this canonical-model hash.</summary>
        public const int AlgoVersion = 2;

        private const ulong Offset = 14695981039346656037UL; // FNV-64 offset basis
        private const ulong Prime  = 1099511628211UL;        // FNV-64 prime

        /// <summary>Compute the 64-bit canonical hash of <paramref name="m"/>. Never returns 0 (sentinel).</summary>
        public static ulong Compute(ScenarioData m)
        {
            ulong h = Offset;

            h = MixInt(h, AlgoVersion);                          // namespaces the hash (algo-1 was the byte-FNV)
            h = MixInt(h, Fixed.FromFloat(m.MapBounds).Raw);
            h = MixStr(h, m.WinCondition.ToString());            // enum by NAME, not ordinal
            h = MixStr(h, m.TerrainRef);

            // Sort each collection by a stable key so input/file order cannot move the hash. String keys use an
            // ORDINAL comparer (the default string comparer is culture-sensitive — a determinism hazard).
            foreach (ScenarioPlayerSlot s in (m.PlayerSlots ?? Array.Empty<ScenarioPlayerSlot>())
                         .OrderBy(x => x.Slot))
            {
                h = MixInt(h, s.Slot);
                h = MixStr(h, s.FactionJson);
                h = MixInt(h, Fixed.FromFloat(s.StartOre).Raw);
                h = MixInt(h, Fixed.FromFloat(s.BaseX).Raw);
                h = MixInt(h, Fixed.FromFloat(s.BaseZ).Raw);
            }

            foreach (ScenarioResourceNode n in (m.ResourceNodes ?? Array.Empty<ScenarioResourceNode>())
                         .OrderBy(x => x.X).ThenBy(x => x.Z).ThenBy(x => x.Supply)
                         .ThenBy(x => x.Rate).ThenBy(x => x.MaxGatherers))
            {
                h = MixInt(h, Fixed.FromFloat(n.X).Raw);
                h = MixInt(h, Fixed.FromFloat(n.Z).Raw);
                h = MixInt(h, Fixed.FromFloat(n.Supply).Raw);
                h = MixInt(h, Fixed.FromFloat(n.Rate).Raw);
                h = MixInt(h, n.MaxGatherers);
            }

            foreach (ScenarioBuilding b in (m.Buildings ?? Array.Empty<ScenarioBuilding>())
                         .OrderBy(x => x.Slot).ThenBy(x => x.Type, StringComparer.Ordinal)
                         .ThenBy(x => x.X).ThenBy(x => x.Z))
            {
                h = MixStr(h, b.Type);
                h = MixInt(h, b.Slot);
                h = MixInt(h, Fixed.FromFloat(b.X).Raw);
                h = MixInt(h, Fixed.FromFloat(b.Z).Raw);
                h = MixInt(h, b.PreBuilt ? 1 : 0);
            }

            foreach (ScenarioUnit u in (m.Units ?? Array.Empty<ScenarioUnit>())
                         .OrderBy(x => x.Slot).ThenBy(x => x.UnitId, StringComparer.Ordinal)
                         .ThenBy(x => x.X).ThenBy(x => x.Z))
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
