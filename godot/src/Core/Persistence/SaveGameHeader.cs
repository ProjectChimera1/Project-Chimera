#nullable enable
using System.IO;
using System.Text;

namespace ProjectChimera.Core.Persistence
{
    /// <summary>
    /// Story 11.3 — the cheap, LENIENT header-only reader for the save-slot browser (mirrors <c>ReplayHeader</c>).
    /// Reads just enough to render a slot row (map id, tick/duration, readability) WITHOUT parsing the full body, and
    /// NEVER throws — a corrupt/foreign/older file returns an "unreadable" sentinel so the browser lists it as such
    /// instead of crashing. Contrast <see cref="SaveGameFile.Read"/>, which is the fail-closed full load.
    /// </summary>
    public readonly struct SaveGameHeader
    {
        /// <summary>True when the file's magic + format version parsed cleanly (the slot is loadable-shaped).</summary>
        public bool IsReadable { get; }
        /// <summary>The map id the save was taken on ("" when unreadable).</summary>
        public string MapId { get; }
        /// <summary>The sim tick the save was taken at (0 when unreadable).</summary>
        public uint Tick { get; }
        /// <summary>The number of player slots in the launch record (0 when unreadable).</summary>
        public int SlotCount { get; }

        private SaveGameHeader(bool readable, string mapId, uint tick, int slotCount)
        {
            IsReadable = readable; MapId = mapId; Tick = tick; SlotCount = slotCount;
        }

        /// <summary>The sentinel for a file that is missing, foreign, corrupt, or a different format version.</summary>
        public static SaveGameHeader Unreadable() => new(false, "", 0u, 0);

        /// <summary>Read the header metadata from <paramref name="path"/>, never throwing.</summary>
        public static SaveGameHeader Read(string path)
        {
            try
            {
                if (!File.Exists(path)) return Unreadable();
                using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var r = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
                if (r.ReadUInt32() != SaveGameFile.MAGIC) return Unreadable();
                ushort version = r.ReadUInt16();
                if (version != SaveGameFile.FormatVersion) return Unreadable();
                r.ReadInt32(); r.ReadInt32(); r.ReadInt32();   // algo pins (unused for the lenient row)
                r.ReadUInt64(); r.ReadUInt64(); r.ReadUInt64(); // model + content + body hashes (unused here)
                uint tick = r.ReadUInt32();
                string mapId = r.ReadString();
                int slotCount = r.ReadInt32();
                if (slotCount < 0 || slotCount > SaveGameFile.MaxSlots) return Unreadable();
                return new SaveGameHeader(true, mapId, tick, slotCount);
            }
            catch
            {
                return Unreadable(); // any read/parse failure → unreadable row (never throws)
            }
        }
    }
}
