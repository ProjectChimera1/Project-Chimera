#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using ProjectChimera.Multiplayer; // UnitOrder

namespace ProjectChimera.Core.Sim
{
    /// <summary>
    /// Unreal trial A3 (plan A §3.2): one scripted order, exactly the integers that cross the wire —
    /// <see cref="UnitOrder"/>'s fields plus the tick and the issuing faction. <see cref="Command"/> is the raw
    /// wire byte, so the Shift-queued flag (<see cref="UnitOrderFlags.Queued"/>, 0x80) rides inside it unchanged.
    /// </summary>
    public readonly struct OrderRow : IEquatable<OrderRow>
    {
        /// <summary>The <see cref="SimulationHost.CurrentTick"/> at which the order is applied (before the step that
        /// produces tick + 1).</summary>
        public readonly int Tick;
        /// <summary>The issuing faction's ordinal (<see cref="Faction"/>), the <c>expectedFaction</c> of
        /// <see cref="OrderApplier.Apply"/>.</summary>
        public readonly byte Faction;
        /// <summary><see cref="UnitOrder.UnitId"/>: a packed entity ref for entity commands.</summary>
        public readonly int UnitRef;
        /// <summary>The wire command byte (low 7 bits <see cref="UnitCommand"/>, 0x80 = queued).</summary>
        public readonly byte Command;
        /// <summary><see cref="UnitOrder.TargetX"/> (<see cref="Fixed.Raw"/>).</summary>
        public readonly int XRaw;
        /// <summary><see cref="UnitOrder.TargetZ"/> (<see cref="Fixed.Raw"/>).</summary>
        public readonly int ZRaw;
        /// <summary><see cref="UnitOrder.Slot"/>.</summary>
        public readonly byte Slot;

        public OrderRow(int tick, byte faction, int unitRef, byte command, int xRaw, int zRaw, byte slot)
        {
            Tick = tick; Faction = faction; UnitRef = unitRef; Command = command; XRaw = xRaw; ZRaw = zRaw; Slot = slot;
        }

        /// <summary>The issuing faction as the enum.</summary>
        public Faction IssuingFaction => (Faction)Faction;

        /// <summary>The wire order this row describes (no conversion: raw ints in, raw ints out).</summary>
        public UnitOrder ToUnitOrder() =>
            new UnitOrder(UnitRef, (UnitCommand)Command, Fixed.FromRaw(XRaw), Fixed.FromRaw(ZRaw), Slot);

        public bool Equals(OrderRow o) =>
            Tick == o.Tick && Faction == o.Faction && UnitRef == o.UnitRef && Command == o.Command
            && XRaw == o.XRaw && ZRaw == o.ZRaw && Slot == o.Slot;

        public override bool Equals(object? obj) => obj is OrderRow o && Equals(o);

        public override int GetHashCode() => unchecked(Tick ^ (UnitRef * 31) ^ (Command << 8) ^ XRaw ^ (ZRaw * 17));

        public override string ToString() =>
            string.Join(",",
                Tick.ToString(CultureInfo.InvariantCulture), Faction.ToString(CultureInfo.InvariantCulture),
                UnitRef.ToString(CultureInfo.InvariantCulture), Command.ToString(CultureInfo.InvariantCulture),
                XRaw.ToString(CultureInfo.InvariantCulture), ZRaw.ToString(CultureInfo.InvariantCulture),
                Slot.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Unreal trial A3 (plan A §3.2, R2 §11 "Orders as data"): a frozen order list in the CSV form every trial leg
    /// replays — <c>tick,faction,unit_ref,cmd,x_raw,z_raw,slot</c>, all base-10 integers (<c>cmd</c> includes the
    /// 0x80 queued flag, so it ranges 0-255; the raws may be negative). The first line is that header; blank lines
    /// are ignored; rows must be in non-decreasing tick order. Every leg applies all rows whose tick equals
    /// <see cref="SimulationHost.CurrentTick"/>, in file order, then steps (<see cref="ApplyDue"/>), so the script is
    /// written once and never re-implemented per language. C++ legs parse the same file themselves.
    /// </summary>
    public sealed class OrderScript
    {
        /// <summary>The exact header line.</summary>
        public const string HEADER = "tick,faction,unit_ref,cmd,x_raw,z_raw,slot";

        private const int COLUMN_COUNT = 7;
        private const ulong FnvOffset = 14695981039346656037UL; // FNV-1a 64 offset basis
        private const ulong FnvPrime  = 1099511628211UL;        // FNV-1a 64 prime

        private readonly OrderRow[] _rows;

        /// <summary>Build a script from rows already in non-decreasing tick order.</summary>
        public OrderScript(IReadOnlyList<OrderRow> rows)
        {
            if (rows == null) throw new ArgumentNullException(nameof(rows));
            _rows = new OrderRow[rows.Count];
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i].Tick < 0)
                    throw new ArgumentException($"row {i}: negative tick {rows[i].Tick}", nameof(rows));
                if (i > 0 && rows[i].Tick < rows[i - 1].Tick)
                    throw new ArgumentException($"row {i}: tick {rows[i].Tick} after tick {rows[i - 1].Tick} (rows must be in non-decreasing tick order)", nameof(rows));
                _rows[i] = rows[i];
            }
        }

        /// <summary>The rows, in file (= submit) order.</summary>
        public IReadOnlyList<OrderRow> Rows => _rows;

        /// <summary>Number of rows.</summary>
        public int Count => _rows.Length;

        /// <summary>Parse CSV text (LF or CRLF). Throws <see cref="FormatException"/> with the 1-based line number on
        /// a bad header, a wrong column count, a non-integer, an out-of-range byte or a tick out of order.</summary>
        public static OrderScript Parse(string text)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            string[] lines = text.Split('\n');
            var rows = new List<OrderRow>();
            bool sawHeader = false;
            for (int li = 0; li < lines.Length; li++)
            {
                string line = lines[li].TrimEnd('\r');
                if (line.Trim().Length == 0) continue;
                if (!sawHeader)
                {
                    if (!string.Equals(line.Trim(), HEADER, StringComparison.Ordinal))
                        throw new FormatException($"line {li + 1}: expected header '{HEADER}', got '{line}'");
                    sawHeader = true;
                    continue;
                }
                string[] f = line.Split(',');
                if (f.Length != COLUMN_COUNT)
                    throw new FormatException($"line {li + 1}: expected {COLUMN_COUNT} columns, got {f.Length}");
                int tick    = ParseInt(f[0], li, "tick");
                int faction = ParseInt(f[1], li, "faction");
                int unitRef = ParseInt(f[2], li, "unit_ref");
                int cmd     = ParseInt(f[3], li, "cmd");
                int x       = ParseInt(f[4], li, "x_raw");
                int z       = ParseInt(f[5], li, "z_raw");
                int slot    = ParseInt(f[6], li, "slot");
                if (tick < 0) throw new FormatException($"line {li + 1}: negative tick {tick}");
                CheckByte(faction, li, "faction");
                CheckByte(cmd, li, "cmd");
                CheckByte(slot, li, "slot");
                if (rows.Count > 0 && tick < rows[rows.Count - 1].Tick)
                    throw new FormatException($"line {li + 1}: tick {tick} after tick {rows[rows.Count - 1].Tick} (rows must be in non-decreasing tick order)");
                rows.Add(new OrderRow(tick, (byte)faction, unitRef, (byte)cmd, x, z, (byte)slot));
            }
            if (!sawHeader) throw new FormatException($"empty order script: expected header '{HEADER}'");
            return new OrderScript(rows);
        }

        /// <summary>Read and <see cref="Parse"/> a CSV file.</summary>
        public static OrderScript Load(string path) => Parse(File.ReadAllText(path));

        /// <summary>The CSV text: the header, then one row per line, LF endings, trailing LF.</summary>
        public string Format()
        {
            var sb = new StringBuilder(HEADER.Length + 1 + _rows.Length * 24);
            sb.Append(HEADER).Append('\n');
            for (int i = 0; i < _rows.Length; i++) sb.Append(_rows[i].ToString()).Append('\n');
            return sb.ToString();
        }

        /// <summary>Write <see cref="Format"/> to <paramref name="path"/> (UTF-8, no BOM).</summary>
        public void Save(string path) => File.WriteAllText(path, Format(), new UTF8Encoding(false));

        /// <summary>
        /// The trace trailer's <c>orders_digest</c> (plan A §3.5): FNV-1a 64 over every parsed row in submit order,
        /// each of the seven fields as a little-endian int32 (tick, faction, unit_ref, cmd, x_raw, z_raw, slot).
        /// </summary>
        public ulong Digest()
        {
            ulong h = FnvOffset;
            for (int i = 0; i < _rows.Length; i++)
            {
                OrderRow r = _rows[i];
                h = MixInt(h, r.Tick);
                h = MixInt(h, r.Faction);
                h = MixInt(h, r.UnitRef);
                h = MixInt(h, r.Command);
                h = MixInt(h, r.XRaw);
                h = MixInt(h, r.ZRaw);
                h = MixInt(h, r.Slot);
            }
            return h;
        }

        /// <summary>
        /// Apply, in file order, every row from <paramref name="cursor"/> on whose tick equals
        /// <paramref name="session"/>'s current tick, advancing the cursor past them; counts what
        /// <see cref="SimSession.ApplyOrder"/> reported. Rows for an EARLIER tick (a cursor left behind) throw: a leg
        /// that skipped a tick must fail loudly, not replay late. Returns the number of rows applied this call.
        /// </summary>
        public int ApplyDue(SimSession session, ref int cursor, ref int applied, ref int dropped)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            uint now = session.Host.CurrentTick;
            int n = 0;
            while (cursor < _rows.Length)
            {
                OrderRow r = _rows[cursor];
                if ((uint)r.Tick > now) break;
                if ((uint)r.Tick < now)
                    throw new InvalidOperationException($"order row {cursor} is for tick {r.Tick} but the session is at tick {now}");
                UnitOrder o = r.ToUnitOrder();
                if (session.ApplyOrder(r.IssuingFaction, in o)) applied++; else dropped++;
                cursor++;
                n++;
            }
            return n;
        }

        private static int ParseInt(string s, int lineIndex, string column)
        {
            if (!int.TryParse(s.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int v))
                throw new FormatException($"line {lineIndex + 1}: column '{column}' is not an int32: '{s}'");
            return v;
        }

        private static void CheckByte(int v, int lineIndex, string column)
        {
            if (v < byte.MinValue || v > byte.MaxValue)
                throw new FormatException($"line {lineIndex + 1}: column '{column}' = {v} is outside 0-255");
        }

        /// <summary>FNV-1a 64 over the 4 little-endian bytes of <paramref name="value"/>.</summary>
        private static ulong MixInt(ulong h, int value)
        {
            unchecked
            {
                uint u = (uint)value;
                h = (h ^ (u & 0xFF)) * FnvPrime;
                h = (h ^ ((u >> 8) & 0xFF)) * FnvPrime;
                h = (h ^ ((u >> 16) & 0xFF)) * FnvPrime;
                h = (h ^ (u >> 24)) * FnvPrime;
                return h;
            }
        }
    }
}
