#nullable enable
using System;
using System.Collections.Generic;
using ProjectChimera.Dsl; // DslVarTable, DslVarDecl, DslValueType, VarScope

namespace ProjectChimera.Core.Sim
{
    /// <summary>
    /// Story 7.8 — the presentation read rail. A Godot-free, double-buffered, per-variable VERSION-STAMPED copy of
    /// already-checksummed <see cref="DslVarTable"/> state, published exactly once per tick at the tick boundary.
    ///
    /// <para><b>NOT folded into <c>SimChecksum</c>.</b> It is DERIVED from state that is already checksummed (the
    /// <see cref="DslVarTable"/>), so folding it would be redundant — and, crucially, because it is UNFOLDED a UI
    /// mismatch cannot desync (AR-32 read rail). <see cref="Publish"/> writes only the readback; it never touches
    /// any sim state.</para>
    ///
    /// <para><b>Version derivation without sim-side dirty tracking.</b> The <see cref="DslVarTable"/> has no change
    /// signal, so the readback computes versions itself: on <see cref="Publish"/> it compares each variable's
    /// current raw(s) to the last-published raw(s) and increments that variable's monotonic <c>version</c> only when
    /// it changed. All change-tracking lives here (unfolded) — no new sim state, no <c>SimChecksum</c> impact.</para>
    ///
    /// <para><b>Double-buffer.</b> <see cref="Publish"/> fills the spare snapshot from the freshly-updated committed
    /// state, then swaps the published reference — so a presentation reader (<c>CustomUiBridge._Process</c>) that
    /// grabbed <see cref="Published"/> once keeps reading that same internally-consistent snapshot across a later
    /// publish/swap (the reference it holds is never mutated in place). Both buffers are preallocated at
    /// <see cref="InitFromDeclarations"/> (zero per-tick heap allocation). NOTE: this is a two-buffer scheme, so it is
    /// tear-free only because <see cref="Publish"/> and the reader both run single-threaded on the <c>_Process</c>
    /// main thread (never concurrently) — a reader that held a snapshot across TWO publishes would see its buffer
    /// reused. The <c>volatile</c> on <c>_published</c> guards only the reference read, not a cross-thread contract.</para>
    ///
    /// Only DECLARED Global and Per-player scalars, plus declared Global arrays, are published (UI binds must
    /// reference declared variables — <c>CustomUiGate</c>). TriggerLocal scratch is never in the read rail.
    /// </summary>
    public sealed class DslVarReadback
    {
        private const int PlayerSlots = DslVarTable.PlayerSlots; // 8

        // ── Immutable structure captured at InitFromDeclarations ──
        private string[]       _gNames = Array.Empty<string>();
        private DslValueType[] _gTypes = Array.Empty<DslValueType>();
        private int _gCount;

        private string[]       _pNames = Array.Empty<string>();
        private DslValueType[] _pTypes = Array.Empty<DslValueType>();
        private int _pCount;

        private string[]       _aNames = Array.Empty<string>();
        private DslValueType[] _aElem  = Array.Empty<DslValueType>();
        private int[]          _aCap   = Array.Empty<int>();
        private int _aCount;

        // Name → (which store, decl index) resolution (presentation-side reads only).
        private readonly Dictionary<string, int> _gIndex = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _pIndex = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _aIndex = new(StringComparer.Ordinal);

        // ── Committed (version-tracking) state: last-published raws + monotonic versions ──
        private int[]  _gRaw0 = Array.Empty<int>(), _gRaw1 = Array.Empty<int>();
        private uint[] _gVer  = Array.Empty<uint>();
        private int[]  _pRaw0 = Array.Empty<int>(), _pRaw1 = Array.Empty<int>(); // [_pCount * PlayerSlots]
        private uint[] _pVer  = Array.Empty<uint>();
        private int[]  _aLen  = Array.Empty<int>();
        private int[][] _aRaw = Array.Empty<int[]>();
        private uint[] _aVer  = Array.Empty<uint>();

        // ── Double-buffer: two immutable-per-read snapshots; Published is the tear-free front. ──
        private Snapshot _bufferA = Snapshot.Empty;
        private Snapshot _bufferB = Snapshot.Empty;
        private volatile Snapshot _published = Snapshot.Empty;

        /// <summary>The current tear-free published snapshot. Grab ONCE per read (a later swap never mutates it).</summary>
        public Snapshot Published => _published;

        /// <summary>
        /// (Re)initialize the readback from a scenario's declarations (mirrors <see cref="DslVarTable.InitFromDeclarations"/>).
        /// Publishes an initial snapshot at version 1 so a presentation reader sees the declared initials before the
        /// first tick. Only Global/Per-player scalars and Global arrays are tracked; TriggerLocal scratch is skipped.
        /// </summary>
        public void InitFromDeclarations(IReadOnlyList<DslVarDecl> variables)
        {
            int g = 0, p = 0, a = 0;
            for (int i = 0; i < variables.Count; i++)
            {
                DslVarDecl d = variables[i];
                if (d.Type == DslValueType.Array) a++;
                else if (d.Scope == VarScope.Global) g++;
                else if (d.Scope == VarScope.PerPlayer) p++;
                // TriggerLocal — never in the read rail.
            }

            _gCount = g; _pCount = p; _aCount = a;
            _gNames = new string[g]; _gTypes = new DslValueType[g];
            _pNames = new string[p]; _pTypes = new DslValueType[p];
            _aNames = new string[a]; _aElem = new DslValueType[a]; _aCap = new int[a];
            _gIndex.Clear(); _pIndex.Clear(); _aIndex.Clear();

            _gRaw0 = new int[g]; _gRaw1 = new int[g]; _gVer = new uint[g];
            _pRaw0 = new int[p * PlayerSlots]; _pRaw1 = new int[p * PlayerSlots]; _pVer = new uint[p];
            _aLen = new int[a]; _aRaw = new int[a][]; _aVer = new uint[a];

            int gi = 0, pi = 0, ai = 0;
            for (int i = 0; i < variables.Count; i++)
            {
                DslVarDecl d = variables[i];
                if (d.Type == DslValueType.Array)
                {
                    _aNames[ai] = d.Name; _aElem[ai] = d.ElementType; _aCap[ai] = d.Capacity < 1 ? 1 : d.Capacity;
                    _aRaw[ai] = new int[_aCap[ai]];
                    _aVer[ai] = 1; // initial published version (mirrors the scalar init at version 1)
                    _aIndex[d.Name] = ai;
                    ai++;
                }
                else if (d.Scope == VarScope.Global)
                {
                    _gNames[gi] = d.Name; _gTypes[gi] = d.Type;
                    _gRaw0[gi] = d.Raw0; _gRaw1[gi] = d.Raw1; _gVer[gi] = 1;
                    _gIndex[d.Name] = gi;
                    gi++;
                }
                else if (d.Scope == VarScope.PerPlayer)
                {
                    _pNames[pi] = d.Name; _pTypes[pi] = d.Type; _pVer[pi] = 1;
                    for (int s = 0; s < PlayerSlots; s++)
                    {
                        _pRaw0[pi * PlayerSlots + s] = d.Raw0;
                        _pRaw1[pi * PlayerSlots + s] = d.Raw1;
                    }
                    _pIndex[d.Name] = pi;
                    pi++;
                }
            }

            _bufferA = Snapshot.Allocate(_gCount, _pCount, _aCount, _aCap);
            _bufferB = Snapshot.Allocate(_gCount, _pCount, _aCount, _aCap);
            FillFrom(_bufferA);
            _published = _bufferA;
        }

        /// <summary>Reset to empty (Edit↔Play reset, before an <see cref="InitFromDeclarations"/> re-apply).</summary>
        public void Clear()
        {
            _gCount = _pCount = _aCount = 0;
            _gNames = Array.Empty<string>(); _gTypes = Array.Empty<DslValueType>();
            _pNames = Array.Empty<string>(); _pTypes = Array.Empty<DslValueType>();
            _aNames = Array.Empty<string>(); _aElem = Array.Empty<DslValueType>(); _aCap = Array.Empty<int>();
            _gIndex.Clear(); _pIndex.Clear(); _aIndex.Clear();
            _gRaw0 = _gRaw1 = Array.Empty<int>(); _gVer = Array.Empty<uint>();
            _pRaw0 = _pRaw1 = Array.Empty<int>(); _pVer = Array.Empty<uint>();
            _aLen = Array.Empty<int>(); _aRaw = Array.Empty<int[]>(); _aVer = Array.Empty<uint>();
            _bufferA = _bufferB = Snapshot.Empty;
            _published = Snapshot.Empty;
        }

        /// <summary>
        /// Publish once per completed tick at the tick boundary, reading the FINAL post-tick <see cref="DslVarTable"/>
        /// state. Bumps a per-variable monotonic version only where the raw changed, then swaps the double-buffer so
        /// presentation always reads a consistent snapshot. Writes ONLY the readback. <paramref name="tick"/> is the
        /// completed tick (recorded on the snapshot for diagnostics; not folded anywhere).
        /// </summary>
        public void Publish(DslVarTable table, uint tick)
        {
            if (table is null) return;

            // ── Globals ──
            for (int i = 0; i < _gCount; i++)
            {
                table.GetRaw(_gNames[i], 0, out int r0, out int r1);
                if (r0 != _gRaw0[i] || r1 != _gRaw1[i]) { _gRaw0[i] = r0; _gRaw1[i] = r1; _gVer[i]++; }
            }

            // ── Per-player (every slot 0..7) ──
            for (int i = 0; i < _pCount; i++)
            {
                bool changed = false;
                for (int s = 0; s < PlayerSlots; s++)
                {
                    table.GetRaw(_pNames[i], s, out int r0, out int r1);
                    int idx = i * PlayerSlots + s;
                    if (r0 != _pRaw0[idx] || r1 != _pRaw1[idx]) { _pRaw0[idx] = r0; _pRaw1[idx] = r1; changed = true; }
                }
                if (changed) _pVer[i]++;
            }

            // ── Global arrays ──
            for (int i = 0; i < _aCount; i++)
            {
                int len = table.ArrayLen(_aNames[i]);
                if (len > _aCap[i]) len = _aCap[i];
                bool changed = len != _aLen[i];
                for (int k = 0; k < len; k++)
                {
                    int raw = table.ArrayGet(_aNames[i], k);
                    if (raw != _aRaw[i][k]) { _aRaw[i][k] = raw; changed = true; }
                }
                _aLen[i] = len;
                if (changed) _aVer[i]++;
            }

            // Fill the SPARE buffer from committed state, then swap the published reference (tear-free).
            Snapshot spare = ReferenceEquals(_published, _bufferA) ? _bufferB : _bufferA;
            spare.Tick = tick;
            FillFrom(spare);
            _published = spare;
        }

        private void FillFrom(Snapshot s)
        {
            Array.Copy(_gRaw0, s.GRaw0, _gCount);
            Array.Copy(_gRaw1, s.GRaw1, _gCount);
            Array.Copy(_gVer,  s.GVer,  _gCount);
            Array.Copy(_pRaw0, s.PRaw0, _pCount * PlayerSlots);
            Array.Copy(_pRaw1, s.PRaw1, _pCount * PlayerSlots);
            Array.Copy(_pVer,  s.PVer,  _pCount);
            for (int i = 0; i < _aCount; i++)
            {
                s.ALen[i] = _aLen[i];
                s.AVer[i] = _aVer[i];
                Array.Copy(_aRaw[i], s.ARaw[i], _aLen[i]);
            }
        }

        // ── Presentation-side read API (call on the front snapshot handle) ──

        /// <summary>
        /// Map an ENGINE <c>Faction</c> (as an int: Neutral=0, Player1=1 … Player4=4) to the 0-based per-player DSL
        /// slot this readback (and <see cref="DslVarTable"/>) index by. The DSL per-player store is 0-based with
        /// <b>slot 0 = Player1</b>: <c>set_variable</c>/<c>variable_comparison</c> pass the trigger's 0-based
        /// <c>Faction</c> field straight through as the slot (whereas engine-faction ops convert with
        /// <c>(Faction)(field + 1)</c>). So the local player's own slot is <c>engineFaction - 1</c>. Neutral/spectator
        /// (0 or below) has no player slot and maps to slot 0 (Player1) — the sensible default, and also what
        /// <see cref="TryGetScalar"/>'s negative-clamp would produce. This is the ONE conversion presentation must use;
        /// passing the raw engine faction int would read the NEXT player's slot (off-by-one).
        /// </summary>
        public static int PlayerSlotForFaction(int engineFaction) => engineFaction <= 0 ? 0 : engineFaction - 1;

        /// <summary>
        /// Read a declared scalar variable's value + version. Resolves a Global (the <paramref name="faction"/> slot
        /// is ignored) or a Per-player variable (uses the slot, clamped 0..7 — pass a 0-based DSL slot, e.g. via
        /// <see cref="PlayerSlotForFaction"/>). False for an unknown/array/TriggerLocal name. Uses the
        /// currently-published tear-free snapshot.
        /// </summary>
        public bool TryGetScalar(string name, int faction, out DslValueType type, out int raw0, out int raw1, out uint version)
        {
            Snapshot s = _published;
            if (_gIndex.TryGetValue(name, out int gi))
            {
                type = _gTypes[gi]; raw0 = s.GRaw0[gi]; raw1 = s.GRaw1[gi]; version = s.GVer[gi];
                return true;
            }
            if (_pIndex.TryGetValue(name, out int pi))
            {
                int slot = faction < 0 ? 0 : (faction >= PlayerSlots ? PlayerSlots - 1 : faction);
                int idx = pi * PlayerSlots + slot;
                type = _pTypes[pi]; raw0 = s.PRaw0[idx]; raw1 = s.PRaw1[idx]; version = s.PVer[pi];
                return true;
            }
            type = DslValueType.Int; raw0 = 0; raw1 = 0; version = 0;
            return false;
        }

        /// <summary>
        /// Story 7.15 — one declared-variable descriptor for the trigger-debug variable watch. Carries the current
        /// value read off the published tear-free snapshot: for a scalar, <see cref="Raw0"/>/<see cref="Raw1"/> are
        /// the value (per-player uses the requested faction slot); for an array, <see cref="IsArray"/> is true and
        /// <see cref="ArrayCount"/> is the live element count. Presentation formats the raw(s) into text; the
        /// <see cref="Version"/> lets the overlay re-format a row only on change (the CustomUiBridge idiom).
        /// </summary>
        public readonly struct WatchVar
        {
            public readonly string       Name;
            public readonly VarScope     Scope;
            public readonly DslValueType Type;    // scalar value type, or the ELEMENT type for an array
            public readonly bool         IsArray;
            public readonly int          Raw0;    // scalar raw0 (undefined for arrays)
            public readonly int          Raw1;    // scalar raw1 (Fixed/Point high word; 0 otherwise / for arrays)
            public readonly int          ArrayCount; // live element count (0 for scalars)
            public readonly uint         Version;
            public WatchVar(string name, VarScope scope, DslValueType type, bool isArray,
                            int raw0, int raw1, int arrayCount, uint version)
            {
                Name = name; Scope = scope; Type = type; IsArray = isArray;
                Raw0 = raw0; Raw1 = raw1; ArrayCount = arrayCount; Version = version;
            }
        }

        /// <summary>
        /// Story 7.15 — a PURE read-side enumeration of every declared watchable variable (the addressable set:
        /// declared Global + Per-player scalars + declared Global arrays), each with its current value + version off
        /// the published tear-free snapshot. <c>TriggerLocal</c>/loop scratch is never in the read rail, so it is
        /// absent here by construction (not a coverage gap). Per-player scalars are read at
        /// <paramref name="faction"/> (a 0-based DSL slot, clamped 0..7 — e.g. via <see cref="PlayerSlotForFaction"/>).
        /// Adds NO folded state; the readback is already excluded from <c>SimChecksum</c>.
        /// </summary>
        public List<WatchVar> Enumerate(int faction = 0)
        {
            Snapshot s = _published;
            var list = new List<WatchVar>(_gCount + _pCount + _aCount);
            for (int i = 0; i < _gCount; i++)
                list.Add(new WatchVar(_gNames[i], VarScope.Global, _gTypes[i], false,
                                      s.GRaw0[i], s.GRaw1[i], 0, s.GVer[i]));
            int slot = faction < 0 ? 0 : (faction >= PlayerSlots ? PlayerSlots - 1 : faction);
            for (int i = 0; i < _pCount; i++)
            {
                int idx = i * PlayerSlots + slot;
                list.Add(new WatchVar(_pNames[i], VarScope.PerPlayer, _pTypes[i], false,
                                      s.PRaw0[idx], s.PRaw1[idx], 0, s.PVer[i]));
            }
            for (int i = 0; i < _aCount; i++)
                list.Add(new WatchVar(_aNames[i], VarScope.Global, _aElem[i], true,
                                      0, 0, s.ALen[i], s.AVer[i]));
            return list;
        }

        /// <summary>Read a declared array's element type, live count, and version. False for an unknown/non-array name.</summary>
        public bool TryGetArray(string name, out DslValueType elem, out int count, out uint version)
        {
            Snapshot s = _published;
            if (_aIndex.TryGetValue(name, out int ai))
            {
                elem = _aElem[ai]; count = s.ALen[ai]; version = s.AVer[ai];
                return true;
            }
            elem = DslValueType.Int; count = 0; version = 0;
            return false;
        }

        /// <summary>Read array element <paramref name="index"/>'s raw from the published snapshot (0 for out-of-range
        /// / unknown name — the total-semantics default).</summary>
        public int ArrayGet(string name, int index)
        {
            Snapshot s = _published;
            if (_aIndex.TryGetValue(name, out int ai) && index >= 0 && index < s.ALen[ai])
                return s.ARaw[ai][index];
            return 0;
        }

        /// <summary>
        /// The immutable-per-read published state. A single instance is filled and swapped in each <see cref="Publish"/>;
        /// a reader that grabbed it keeps reading a consistent snapshot after a later swap.
        /// </summary>
        public sealed class Snapshot
        {
            public static readonly Snapshot Empty = new(0, 0, 0, Array.Empty<int>());

            public uint Tick;
            public readonly int[]   GRaw0, GRaw1;
            public readonly uint[]  GVer;
            public readonly int[]   PRaw0, PRaw1;
            public readonly uint[]  PVer;
            public readonly int[]   ALen;
            public readonly uint[]  AVer;
            public readonly int[][] ARaw;

            private Snapshot(int g, int p, int a, int[] aCap)
            {
                GRaw0 = new int[g]; GRaw1 = new int[g]; GVer = new uint[g];
                PRaw0 = new int[p * PlayerSlots]; PRaw1 = new int[p * PlayerSlots]; PVer = new uint[p];
                ALen = new int[a]; AVer = new uint[a]; ARaw = new int[a][];
                for (int i = 0; i < a; i++) ARaw[i] = new int[aCap[i]];
            }

            public static Snapshot Allocate(int g, int p, int a, int[] aCap) => new(g, p, a, aCap);
        }
    }
}
