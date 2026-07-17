#nullable enable
using System;
using System.Collections.Generic;
using ProjectChimera.Core; // Fixed

namespace ProjectChimera.Dsl
{
    /// <summary>
    /// Story 7.3 — the top-level, typed, scoped sim store for DSL variables + timers (a sibling of the other
    /// <c>SimulationHost</c> stores). Replaces <c>ScenarioDirector</c>'s ad-hoc <c>_variableNames/_variableValues</c>
    /// and <c>_timerNames/_timerRemaining</c> lists. Pure sim-layer C#: Godot-free, float-free — fractional numerics
    /// are <see cref="Fixed"/> 16.16 stored as their raw int; it never references <c>SimulationLoop</c> (integer
    /// timer ticks are computed at the Core boundary and fed in).
    ///
    /// Storage is DENSE SoA in declaration/creation-index order (never a <c>Dictionary</c>, never name-sorted, never
    /// hash-set-enumerated — AR-16 determinism). Name lookup is a deterministic linear scan (small N).
    ///
    /// Scopes:
    ///   • <see cref="VarScope.Global"/> — a growable list seeded with declared Globals; an UNDECLARED
    ///     <c>set_variable</c> name appends a Global/Int slot (today's <c>GetVariable</c>/<c>SetVariable</c>
    ///     semantics). Folded into <c>SimChecksum</c>.
    ///   • <see cref="VarScope.PerPlayer"/> — one slot per player (0..<see cref="PlayerSlots"/>), fixed after init.
    ///     Folded for EVERY player slot (0..<see cref="PlayerSlots"/>, ascending) per declaration, in declaration
    ///     index — see <see cref="FoldInto"/> (writes can land on inactive slots, so all slots must fold).
    ///   • <see cref="VarScope.TriggerLocal"/> — per-firing scratch, reset on <see cref="Enter"/> and freed on
    ///     <see cref="Exit"/>. NEVER folded.
    ///
    /// Timers are integer ticks only (0 = inactive); decremented in ascending creation index; a timer reaching 0
    /// fires on that tick (byte-identical to the legacy <c>ScenarioDirector</c> path).
    /// </summary>
    public sealed class DslVarTable
    {
        /// <summary>The fixed per-player slot count (players 0..7). Matches the engine's PLAYER_COUNT.</summary>
        public const int PlayerSlots = 8;

        // ── Global variables (declared Globals seed these; undeclared set_variable appends). ──
        private readonly List<string>       _gNames = new();
        private readonly List<DslValueType> _gTypes = new();
        private readonly List<int>          _gRaw0  = new();
        private readonly List<int>          _gRaw1  = new();

        // ── Per-player variables (fixed after init). Flat storage [declIndex * PlayerSlots + player]. ──
        private string[]       _pNames = Array.Empty<string>();
        private DslValueType[] _pTypes = Array.Empty<DslValueType>();
        private int[]          _pRaw0  = Array.Empty<int>();
        private int[]          _pRaw1  = Array.Empty<int>();
        private int _pCount; // number of per-player declarations

        // ── Trigger-local scratch (declared TriggerLocal templates; reset on Enter, freed on Exit). ──
        private string[]       _tNames    = Array.Empty<string>();
        private DslValueType[] _tTypes    = Array.Empty<DslValueType>();
        private int[]          _tInitRaw0 = Array.Empty<int>();
        private int[]          _tInitRaw1 = Array.Empty<int>();
        private int[]          _tRaw0     = Array.Empty<int>();
        private int[]          _tRaw1     = Array.Empty<int>();
        private int  _tCount;
        private bool _inTrigger;

        // ── Story 7.6 — Global arrays (declared Array-typed variables; storage preallocated at declared
        //    capacity; Global scope only this story). Declaration-index order; never a Dictionary. ──
        private string[]       _aNames = Array.Empty<string>();
        private DslValueType[] _aElem  = Array.Empty<DslValueType>(); // element type (Int/Fixed/Bool)
        private int[]          _aCap   = Array.Empty<int>();          // declared capacity
        private int[]          _aCount = Array.Empty<int>();          // live element count
        private int[][]        _aRaws  = Array.Empty<int[]>();        // preallocated element raws (length = capacity)
        private int _aDeclCount;

        // ── Timers (declared timers seed these; create_timer appends new names). ──
        private readonly List<string> _timerNames     = new();
        private readonly List<int>    _timerRemaining = new(); // remaining ticks; 0 = inactive/expired slot

        /// <summary>
        /// (Re)initialize the table from a scenario's declarations. Clears all prior state first, so a re-apply
        /// (Edit↔Play) is non-additive. Declared Globals seed the Global list in declaration order; PerPlayer
        /// declarations size the dense per-player arrays and initialize every player slot to the declared initial;
        /// TriggerLocal declarations become per-firing templates; declared timers start active at their tick count.
        /// </summary>
        public void InitFromDeclarations(IReadOnlyList<DslVarDecl> variables, IReadOnlyList<DslTimerDecl> timers)
        {
            Clear();

            // First pass: count per-player + trigger-local + array declarations so the dense arrays can be sized
            // once. Story 7.6: an Array-typed declaration routes to the dedicated array store (its raws are
            // preallocated at declared capacity here — never grown in the tick), NOT to the scalar stores.
            int ppCount = 0, tlCount = 0, arrCount = 0;
            for (int i = 0; i < variables.Count; i++)
            {
                if (variables[i].Type == DslValueType.Array)          arrCount++;
                else if (variables[i].Scope == VarScope.PerPlayer)    ppCount++;
                else if (variables[i].Scope == VarScope.TriggerLocal) tlCount++;
            }

            _aDeclCount = arrCount;
            _aNames = new string[arrCount];
            _aElem  = new DslValueType[arrCount];
            _aCap   = new int[arrCount];
            _aCount = new int[arrCount];
            _aRaws  = new int[arrCount][];

            _pCount = ppCount;
            _pNames = new string[ppCount];
            _pTypes = new DslValueType[ppCount];
            _pRaw0  = new int[ppCount * PlayerSlots];
            _pRaw1  = new int[ppCount * PlayerSlots];

            _tCount    = tlCount;
            _tNames    = new string[tlCount];
            _tTypes    = new DslValueType[tlCount];
            _tInitRaw0 = new int[tlCount];
            _tInitRaw1 = new int[tlCount];
            _tRaw0     = new int[tlCount];
            _tRaw1     = new int[tlCount];

            int pi = 0, ti = 0, ai = 0;
            for (int i = 0; i < variables.Count; i++)
            {
                DslVarDecl d = variables[i];
                if (d.Type == DslValueType.Array)
                {
                    // Story 7.6: arrays seed EMPTY (count 0) at their declared capacity. A degenerate direct-
                    // constructed capacity < 1 clamps to 1 (the gates reject it for authored content) so the
                    // preallocated buffer is never zero-length.
                    _aNames[ai] = d.Name;
                    _aElem[ai]  = d.ElementType;
                    _aCap[ai]   = d.Capacity < 1 ? 1 : d.Capacity;
                    _aCount[ai] = 0;
                    _aRaws[ai]  = new int[_aCap[ai]];
                    ai++;
                    continue;
                }
                // Review (7.4 pass 2): the declaration-init path applies the SAME Bool 0/1 normalization every
                // write path funnels through — the validator gate rejects non-binary Bool initials for authored
                // scenarios, but a direct-constructed DslVarDecl(Bool, raw 7) would otherwise seed a slot where
                // `flag == true` is false while `flag`/`!flag` treat it truthy.
                int raw0 = d.Raw0, raw1 = d.Raw1;
                NormalizeBoolWrite(d.Type, ref raw0, ref raw1);
                switch (d.Scope)
                {
                    case VarScope.Global:
                        _gNames.Add(d.Name);
                        _gTypes.Add(d.Type);
                        _gRaw0.Add(raw0);
                        _gRaw1.Add(raw1);
                        break;
                    case VarScope.PerPlayer:
                        _pNames[pi] = d.Name;
                        _pTypes[pi] = d.Type;
                        for (int p = 0; p < PlayerSlots; p++)
                        {
                            _pRaw0[pi * PlayerSlots + p] = raw0;
                            _pRaw1[pi * PlayerSlots + p] = raw1;
                        }
                        pi++;
                        break;
                    case VarScope.TriggerLocal:
                        _tNames[ti]    = d.Name;
                        _tTypes[ti]    = d.Type;
                        _tInitRaw0[ti] = raw0;
                        _tInitRaw1[ti] = raw1;
                        ti++;
                        break;
                }
            }

            for (int i = 0; i < timers.Count; i++)
            {
                _timerNames.Add(timers[i].Name);
                _timerRemaining.Add(timers[i].Ticks);
            }
        }

        /// <summary>Reset the table to empty (Edit↔Play reset, or before an InitFromDeclarations re-apply).</summary>
        public void Clear()
        {
            _gNames.Clear(); _gTypes.Clear(); _gRaw0.Clear(); _gRaw1.Clear();
            _pNames = Array.Empty<string>(); _pTypes = Array.Empty<DslValueType>();
            _pRaw0 = Array.Empty<int>(); _pRaw1 = Array.Empty<int>(); _pCount = 0;
            _tNames = Array.Empty<string>(); _tTypes = Array.Empty<DslValueType>();
            _tInitRaw0 = Array.Empty<int>(); _tInitRaw1 = Array.Empty<int>();
            _tRaw0 = Array.Empty<int>(); _tRaw1 = Array.Empty<int>(); _tCount = 0; _inTrigger = false;
            _aNames = Array.Empty<string>(); _aElem = Array.Empty<DslValueType>();
            _aCap = Array.Empty<int>(); _aCount = Array.Empty<int>(); _aRaws = Array.Empty<int[]>(); _aDeclCount = 0;
            _timerNames.Clear(); _timerRemaining.Clear();
        }

        // ── Trigger-local lifecycle ───────────────────────────────────────────────

        /// <summary>Open a trigger-local scope: reset every trigger-local slot to its declared initial. Called when
        /// a trigger begins executing its actions.</summary>
        public void Enter()
        {
            for (int i = 0; i < _tCount; i++)
            {
                _tRaw0[i] = _tInitRaw0[i];
                _tRaw1[i] = _tInitRaw1[i];
            }
            _inTrigger = true;
        }

        /// <summary>Close a trigger-local scope: free every trigger-local slot (values inaccessible until the next
        /// <see cref="Enter"/>). Never folded regardless.</summary>
        public void Exit() => _inTrigger = false;

        // ── Int get/set (the only typed access the 7.3 ECA leaf uses) ──────────────

        /// <summary>
        /// Read a named integer variable. Resolution: a declared PerPlayer var uses the <paramref name="faction"/>
        /// slot; a trigger-local var uses the active scratch (0 outside a trigger scope); a declared Global var, or
        /// an UNDECLARED name, uses the Global list (undeclared → default 0 WITHOUT creating a slot, matching the
        /// legacy <c>GetVariable</c> semantics). Non-Int types return their raw int (7.3 reads Int only in practice).
        /// </summary>
        public int GetInt(string name, int faction)
        {
            switch (Resolve(name, out int i))
            {
                case Store.PerPlayer:    return _pRaw0[i * PlayerSlots + ClampSlot(faction)];
                case Store.TriggerLocal: return _inTrigger ? _tRaw0[i] : 0;
                case Store.Global:       return _gRaw0[i];
                default:                 return 0; // undeclared → Global/Int default 0 (no slot created on read)
            }
        }

        /// <summary>
        /// Write a named integer variable. Resolution mirrors <see cref="GetInt"/>: a declared PerPlayer var writes
        /// the <paramref name="faction"/> slot; a trigger-local var writes the active scratch (a no-op outside a
        /// trigger scope); a declared Global var writes its slot; an UNDECLARED name appends a Global/Int slot
        /// (creation-index order preserved), reproducing the legacy <c>SetVariable</c> append semantics. A write to
        /// a declared Bool-typed slot normalizes to 0/1 (the same central rule as <see cref="SetRaw"/>).
        /// </summary>
        public void SetInt(string name, int faction, int value)
        {
            int raw1 = 0; // SetInt never writes raw1 (already 0 on every Bool slot); consumed only by NormalizeBoolWrite
            switch (Resolve(name, out int i))
            {
                case Store.PerPlayer:
                    NormalizeBoolWrite(_pTypes[i], ref value, ref raw1);
                    _pRaw0[i * PlayerSlots + ClampSlot(faction)] = value;
                    return;
                case Store.TriggerLocal:
                    if (_inTrigger)
                    {
                        NormalizeBoolWrite(_tTypes[i], ref value, ref raw1);
                        _tRaw0[i] = value;
                    }
                    return; // outside a scope the write is the documented no-op (never a Global fall-through)
                case Store.Global:
                    NormalizeBoolWrite(_gTypes[i], ref value, ref raw1);
                    _gRaw0[i] = value;
                    return;
                default:
                    // Undeclared → append a Global/Int slot (matches the legacy list-append; preserves creation index).
                    _gNames.Add(name);
                    _gTypes.Add(DslValueType.Int);
                    _gRaw0.Add(value);
                    _gRaw1.Add(0);
                    return;
            }
        }

        private static int ClampSlot(int faction) =>
            faction < 0 ? 0 : (faction >= PlayerSlots ? PlayerSlots - 1 : faction);

        // ── The single name-resolution walk (shared by every get/set/decl lookup) ──

        /// <summary>Which backing store a name resolved to (<see cref="Undeclared"/> = no slot exists).</summary>
        private enum Store : byte { PerPlayer, TriggerLocal, Global, Undeclared }

        /// <summary>
        /// THE resolution walk, shared by <see cref="GetInt"/>/<see cref="SetInt"/>/<see cref="GetRaw"/>/
        /// <see cref="SetRaw"/>/<see cref="TryGetDecl"/> so the order can never drift between them: PerPlayer
        /// first, then TriggerLocal (a trigger-local NAME always resolves to the trigger-local slot — review
        /// follow-up: it must never fall through to a same-named Global), then Global (declared + undeclared-
        /// appended). Deterministic linear scans in declaration/creation index (small N). Returns the store and
        /// the index within it (-1 for <see cref="Store.Undeclared"/>).
        /// </summary>
        private Store Resolve(string name, out int index)
        {
            for (int i = 0; i < _pCount; i++)
                if (string.Equals(_pNames[i], name, StringComparison.Ordinal)) { index = i; return Store.PerPlayer; }
            for (int i = 0; i < _tCount; i++)
                if (string.Equals(_tNames[i], name, StringComparison.Ordinal)) { index = i; return Store.TriggerLocal; }
            for (int i = 0; i < _gNames.Count; i++)
                if (string.Equals(_gNames[i], name, StringComparison.Ordinal)) { index = i; return Store.Global; }
            index = -1;
            return Store.Undeclared;
        }

        /// <summary>Central Bool write normalization: a Bool-typed slot stores raw0 as 0/1 with raw1 forced 0.
        /// Every declared-slot write path (<see cref="SetInt"/>, <see cref="SetRaw"/>, and
        /// <see cref="InitFromDeclarations"/> alike) funnels through this, so the 0/1 invariant cannot drift.
        ///
        /// LEGACY-SEMANTICS NOTE (review, 7.4 pass 2): applying this inside <see cref="SetInt"/> changed a
        /// pre-7.4-reachable write path on a checksummed store (a declared-Bool slot used to keep e.g. 7 verbatim
        /// and fold 7). That is unreachable for GATE-VALIDATED content by three independent rules — the flat AND
        /// literal-graph set_variable channels are Int-target-only (P5), and Bool initials are gated to {0,1} —
        /// so no golden/legacy scenario can observe it; only direct LoadScenario/table callers do, for whom 0/1
        /// is the pinned post-7.4 contract. If any of those three rules is ever relaxed, this note is the tripwire:
        /// re-audit the checksum impact before shipping.</summary>
        private static void NormalizeBoolWrite(DslValueType type, ref int raw0, ref int raw1)
        {
            if (type != DslValueType.Bool) return;
            raw0 = raw0 != 0 ? 1 : 0;
            raw1 = 0;
        }

        // ── Story 7.4 — typed declaration lookup + raw get/set (the expression substrate) ─────

        /// <summary>
        /// Look up a name's declared type/scope, in the SAME resolution order as <see cref="GetInt"/>/<see
        /// cref="SetInt"/> (PerPlayer → TriggerLocal → Global). Undeclared-then-appended Globals (the legacy
        /// <c>SetVariable</c> append) resolve as Global/Int. Returns false (Int/Global outs) for an unknown name.
        /// </summary>
        public bool TryGetDecl(string name, out DslValueType type, out VarScope scope)
        {
            switch (Resolve(name, out int i))
            {
                case Store.PerPlayer:    type = _pTypes[i]; scope = VarScope.PerPlayer;    return true;
                case Store.TriggerLocal: type = _tTypes[i]; scope = VarScope.TriggerLocal; return true;
                case Store.Global:       type = _gTypes[i]; scope = VarScope.Global;       return true;
                default:
                    // Story 7.6: a declared Array resolves through the dedicated array store (Global scope only).
                    if (ResolveArray(name) >= 0) { type = DslValueType.Array; scope = VarScope.Global; return true; }
                    type = DslValueType.Int; scope = VarScope.Global; return false;
            }
        }

        // ── Story 7.6 — Global arrays: total-semantics ops (never throw in the tick; OOB/at-capacity are
        //    deterministic no-ops, an OOB read is 0 — the div-by-zero precedent). Zero per-tick allocation:
        //    element storage is preallocated at declared capacity in InitFromDeclarations. ──

        /// <summary>Deterministic linear scan of the array declarations (declaration index; small N). -1 = unknown.</summary>
        private int ResolveArray(string name)
        {
            for (int i = 0; i < _aDeclCount; i++)
                if (string.Equals(_aNames[i], name, StringComparison.Ordinal)) return i;
            return -1;
        }

        /// <summary>Look up a declared array's element type + capacity. False (Int/0 outs) for an unknown name.</summary>
        public bool TryGetArrayDecl(string name, out DslValueType elementType, out int capacity)
        {
            int i = ResolveArray(name);
            if (i < 0) { elementType = DslValueType.Int; capacity = 0; return false; }
            elementType = _aElem[i]; capacity = _aCap[i];
            return true;
        }

        /// <summary>Append <paramref name="raw"/> to the named array. At declared capacity (or on an unknown
        /// name) this is a deterministic NO-OP. A Bool-element array normalizes the write to 0/1.</summary>
        public void ArrayPush(string name, int raw)
        {
            int i = ResolveArray(name);
            if (i < 0 || _aCount[i] >= _aCap[i]) return;
            int r1 = 0;
            NormalizeBoolWrite(_aElem[i], ref raw, ref r1);
            _aRaws[i][_aCount[i]++] = raw;
        }

        /// <summary>Write element <paramref name="index"/>. An out-of-bounds index (vs the LIVE count) or an
        /// unknown name is a deterministic NO-OP. Bool elements normalize to 0/1.</summary>
        public void ArraySet(string name, int index, int raw)
        {
            int i = ResolveArray(name);
            if (i < 0 || index < 0 || index >= _aCount[i]) return;
            int r1 = 0;
            NormalizeBoolWrite(_aElem[i], ref raw, ref r1);
            _aRaws[i][index] = raw;
        }

        /// <summary>Reset the named array to empty (count 0). Unknown name = no-op. Elements beyond the live
        /// count are never read or folded, so no scrub is needed.</summary>
        public void ArrayClear(string name)
        {
            int i = ResolveArray(name);
            if (i >= 0) _aCount[i] = 0;
        }

        /// <summary>Read element <paramref name="index"/>'s raw. Out-of-bounds (vs the live count) or an unknown
        /// name reads 0 — the total-semantics default (the div-by-zero precedent).</summary>
        public int ArrayGet(string name, int index)
        {
            int i = ResolveArray(name);
            if (i < 0 || index < 0 || index >= _aCount[i]) return 0;
            return _aRaws[i][index];
        }

        /// <summary>The named array's live element count (0 for an unknown name).</summary>
        public int ArrayLen(string name)
        {
            int i = ResolveArray(name);
            return i < 0 ? 0 : _aCount[i];
        }

        /// <summary>Copy the named array's live elements into <paramref name="buffer"/> (the caller's preallocated
        /// loop-entry snapshot) and return the copied count (≤ buffer.Length). Unknown name copies 0.</summary>
        public int ArraySnapshot(string name, int[] buffer)
        {
            int i = ResolveArray(name);
            if (i < 0) return 0;
            int n = _aCount[i] < buffer.Length ? _aCount[i] : buffer.Length;
            System.Array.Copy(_aRaws[i], buffer, n);
            return n;
        }

        /// <summary>
        /// Read a named variable's raw ints (raw0 = Int value / Fixed.Raw / Bool 0-1 / Point X; raw1 = Point Z,
        /// 0 for scalars). SAME resolution order and contracts as <see cref="GetInt"/>: PerPlayer uses the
        /// <paramref name="faction"/> slot; TriggerLocal reads the active scratch (0 outside a trigger scope,
        /// never falling through to a same-named Global); undeclared reads (0, 0) without creating a slot.
        /// </summary>
        public void GetRaw(string name, int faction, out int raw0, out int raw1)
        {
            switch (Resolve(name, out int i))
            {
                case Store.PerPlayer:
                {
                    int idx = i * PlayerSlots + ClampSlot(faction);
                    raw0 = _pRaw0[idx]; raw1 = _pRaw1[idx];
                    return;
                }
                case Store.TriggerLocal:
                    raw0 = _inTrigger ? _tRaw0[i] : 0;
                    raw1 = _inTrigger ? _tRaw1[i] : 0;
                    return;
                case Store.Global:
                    raw0 = _gRaw0[i]; raw1 = _gRaw1[i];
                    return;
                default:
                    raw0 = 0; raw1 = 0; // undeclared → default (no slot created on read)
                    return;
            }
        }

        /// <summary>
        /// Write a named variable's raw ints, mirroring <see cref="SetInt"/>'s exact resolution order and
        /// contracts: PerPlayer writes the <paramref name="faction"/> slot; TriggerLocal writes the active scratch
        /// (a no-op outside a trigger scope); Global writes its slot; an UNDECLARED name appends a Global/Int slot
        /// (creation-index preserved — REACHABLE via a compiled expression whose set_variable TARGET is undeclared:
        /// the compiler rejects undeclared READS only, and an undeclared write target defaults to Int, the legacy
        /// append parity). A write to a Bool-typed slot normalizes raw0 to 0/1 (raw1 forced 0).
        /// </summary>
        public void SetRaw(string name, int faction, int raw0, int raw1)
        {
            switch (Resolve(name, out int i))
            {
                case Store.PerPlayer:
                {
                    NormalizeBoolWrite(_pTypes[i], ref raw0, ref raw1);
                    int idx = i * PlayerSlots + ClampSlot(faction);
                    _pRaw0[idx] = raw0; _pRaw1[idx] = raw1;
                    return;
                }
                case Store.TriggerLocal:
                    if (_inTrigger)
                    {
                        NormalizeBoolWrite(_tTypes[i], ref raw0, ref raw1);
                        _tRaw0[i] = raw0; _tRaw1[i] = raw1;
                    }
                    return; // outside a scope the write is the documented no-op (never a Global fall-through)
                case Store.Global:
                    NormalizeBoolWrite(_gTypes[i], ref raw0, ref raw1);
                    _gRaw0[i] = raw0; _gRaw1[i] = raw1;
                    return;
                default:
                    // Undeclared → append a Global/Int slot (mirrors SetInt's legacy append; creation index preserved).
                    _gNames.Add(name);
                    _gTypes.Add(DslValueType.Int);
                    _gRaw0.Add(raw0);
                    _gRaw1.Add(raw1);
                    return;
            }
        }

        // ── Timers ─────────────────────────────────────────────────────────────────

        /// <summary>Create or reset a named timer to <paramref name="ticks"/> remaining, preserving its creation
        /// index (a reset reuses the slot; a new name appends). No Dictionary — linear scan (small N).</summary>
        public void TimerSet(string name, int ticks)
        {
            for (int i = 0; i < _timerNames.Count; i++)
                if (string.Equals(_timerNames[i], name, StringComparison.Ordinal))
                {
                    _timerRemaining[i] = ticks;
                    return;
                }
            _timerNames.Add(name);
            _timerRemaining.Add(ticks);
        }

        /// <summary>
        /// Decrement every ACTIVE timer by one tick in ascending creation index, appending the name of each timer
        /// that reaches 0 THIS tick to <paramref name="expiredOut"/> (in that same order). An expired timer is
        /// marked inactive (remaining = 0) in place; a later <see cref="TimerSet"/> of the same name reactivates its
        /// slot, preserving its creation index. Byte-identical to the legacy <c>ScenarioDirector</c> timer loop.
        /// </summary>
        public void TimerTickAndCollectExpired(List<string> expiredOut)
        {
            for (int i = 0; i < _timerRemaining.Count; i++)
            {
                if (_timerRemaining[i] <= 0) continue; // inactive/expired slot
                int remaining = _timerRemaining[i] - 1;
                if (remaining <= 0)
                {
                    _timerRemaining[i] = 0;
                    expiredOut.Add(_timerNames[i]);
                }
                else
                {
                    _timerRemaining[i] = remaining;
                }
            }
        }

        // ── Checksum fold ────────────────────────────────────────────────────────

        /// <summary>
        /// Fold live Global then Per-player variable values, then timer remaining-ticks, into the running FNV-1a
        /// <paramref name="hash"/> — each in ascending declaration/creation index. EVERY per-player slot (0..
        /// <see cref="PlayerSlots"/>) is folded, in ascending slot order, because <see cref="SetInt"/>/
        /// <see cref="ClampSlot"/> can write ANY slot 0..7 (not only the active factions) — so no written slot may
        /// escape the checksum (the story's core anti-desync promise). Trigger-local scratch is NEVER folded. Uses the
        /// caller's <paramref name="mix"/> primitive so the fold shares <c>SimChecksum.Mix</c>.
        /// </summary>
        public void FoldInto(ref uint hash, Func<uint, int, uint> mix)
        {
            // Globals (declared + undeclared), creation-index order — a leading count so a divergent global set is
            // caught even before its values.
            hash = mix(hash, _gNames.Count);
            for (int i = 0; i < _gNames.Count; i++)
            {
                hash = mix(hash, _gRaw0[i]);
                if (_gTypes[i] == DslValueType.Point) hash = mix(hash, _gRaw1[i]);
            }

            // Per-player: outer per-player declaration index, inner EVERY player slot ascending (0..PlayerSlots).
            // Folding all 8 slots (not just active factions) guarantees a write to an inactive slot still folds —
            // deterministic and total across peers.
            for (int i = 0; i < _pCount; i++)
                for (int slot = 0; slot < PlayerSlots; slot++)
                {
                    hash = mix(hash, _pRaw0[i * PlayerSlots + slot]);
                    if (_pTypes[i] == DslValueType.Point) hash = mix(hash, _pRaw1[i * PlayerSlots + slot]);
                }

            // Story 7.6 (v17) — arrays, BETWEEN the per-player section and timers: a leading declaration count,
            // then per array (declaration index ascending) its live count and every live element raw in ascending
            // element index. Slots beyond the live count are never folded (they are unreachable by reads too).
            hash = mix(hash, _aDeclCount);
            for (int i = 0; i < _aDeclCount; i++)
            {
                hash = mix(hash, _aCount[i]);
                for (int k = 0; k < _aCount[i]; k++)
                    hash = mix(hash, _aRaws[i][k]);
            }

            // Timers (creation-index order) — a leading count, then each remaining-tick.
            hash = mix(hash, _timerNames.Count);
            for (int i = 0; i < _timerRemaining.Count; i++)
                hash = mix(hash, _timerRemaining[i]);
        }

        /// <summary>
        /// Fold BYTE-IDENTICALLY to an empty table's <see cref="FoldInto"/> (a leading 0-count for globals, then
        /// a 0-count for arrays (v17), then a 0-count for timers, no per-player/value mixes) so a NULL table and
        /// a non-null EMPTY table are interchangeable in <c>SimChecksum</c>. Production always passes a real
        /// table; this is the null-caller path.
        /// </summary>
        public static void FoldEmpty(ref uint hash, Func<uint, int, uint> mix)
        {
            hash = mix(hash, 0); // 0 globals (matches FoldInto's leading global-count on an empty table)
            hash = mix(hash, 0); // 0 arrays  (matches FoldInto's leading array-declaration count, v17)
            hash = mix(hash, 0); // 0 timers  (matches FoldInto's leading timer-count on an empty table)
        }
    }
}
