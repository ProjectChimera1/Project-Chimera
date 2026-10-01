#nullable enable
using System;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using ProjectChimera.Core;
using ProjectChimera.Core.Sim;
using ProjectChimera.Multiplayer; // UnitOrder

namespace ProjectChimera.SimNative
{
    /// <summary>
    /// Unreal trial A5 (plan A section 3.4): the C ABI of <c>ChimeraSim.dll</c>, one <c>[UnmanagedCallersOnly]</c> export per
    /// row of <c>include/chimera_sim.h</c> (the ABI of record; <c>tools/sim-trial/check_exports.py</c> asserts DLL exports ==
    /// header == allow-list). Every export returns int32, takes the process-wide <see cref="SessionTable.Gate"/>, and wraps
    /// its whole body in <c>try/catch (Exception)</c>: no managed exception ever crosses the ABI (an exception becomes
    /// <c>-6</c> and its text is kept for <c>chimera_last_error</c>; the error writers take the lock too). An exception in
    /// <c>chimera_submit_order</c> or <c>chimera_step</c> faults the session: later mutators return <c>-6</c>.
    ///
    /// <para><b>State:</b> only <c>chimera_submit_order</c> and <c>chimera_step</c> write folded sim state (the command stream
    /// and the tick); <c>chimera_set_checksum_interval</c>, <c>chimera_session_create</c> and <c>chimera_session_destroy</c>
    /// are host configuration. Everything else is a pure read of the live world.</para>
    /// </summary>
    public static unsafe class ChimeraExports
    {
        private static string Num(int v) => v.ToString(CultureInfo.InvariantCulture);

        private static int Fail(int session, int code, string message)
        {
            SessionTable.SetError(session, message);
            return code;
        }

        private static int Fault(int session, Exception e)
        {
            string msg;
            try { msg = e.GetType().Name + ": " + e.Message; } catch (Exception) { msg = "exception"; }
            SessionTable.SetError(session, msg);
            return Abi.EException;
        }

        /// <summary>A mutator's fault: record the message, mark the session faulted (it may be part-stepped) and return -6.</summary>
        private static int FaultSession(NativeSession? s, int session, Exception e)
        {
            int rc = Fault(session, e);
            try
            {
                lock (SessionTable.Gate)
                {
                    if (s != null && !s.Faulted) { s.Faulted = true; s.FaultMessage = s.LastError; }
                }
            }
            catch (Exception) { /* nothing left to do */ }
            return rc;
        }

        /// <summary>The refusal every mutator returns on a faulted session: -6 with the original fault message.</summary>
        private static int Refuse(NativeSession s, string what)
        {
            SessionTable.SetError(s.Id, what + ": session faulted earlier: " + s.FaultMessage);
            return Abi.EException;
        }

        private static bool TrySession(int id, out NativeSession s)
        {
            if (SessionTable.TryGet(id, out s)) return true;
            SessionTable.SetError(0, "unknown or destroyed session " + Num(id));
            return false;
        }

        /// <summary>UTF-8, NUL-terminated string out (the header's rule): *len = bytes without the NUL; needs cap &gt;= len + 1.</summary>
        private static int WriteString(string s, byte* buf, int cap, int* len)
        {
            if (len == null) return Abi.EArg;
            int n = Encoding.UTF8.GetByteCount(s);
            *len = n;
            if (cap < 0 || cap < n + 1 || buf == null) return Abi.EBuffer;
            Encoding.UTF8.GetBytes(s.AsSpan(), new Span<byte>(buf, cap));
            buf[n] = 0;
            return Abi.Ok;
        }

        // ── identity ─────────────────────────────────────────────────────────────────────────────────────────────

        [UnmanagedCallersOnly(EntryPoint = "chimera_abi_version", CallConvs = new[] { typeof(CallConvCdecl) })]
        public static int AbiVersion()
        {
            try
            {
                lock (SessionTable.Gate) { return Abi.Version; }
            }
            catch (Exception e) { return Fault(0, e); }
        }

        [UnmanagedCallersOnly(EntryPoint = "chimera_abi_check", CallConvs = new[] { typeof(CallConvCdecl) })]
        public static int AbiCheck(int unitSize, int buildingSize)
        {
            try
            {
                lock (SessionTable.Gate)
                {
                    if (unitSize != Abi.UnitSize || buildingSize != Abi.BuildingSize
                        || Unsafe.SizeOf<ChimeraUnit>() != Abi.UnitSize || Unsafe.SizeOf<ChimeraBuilding>() != Abi.BuildingSize)
                        return Fail(0, Abi.EAbi, "ABI mismatch: host unit/building sizes " + Num(unitSize) + "/" + Num(buildingSize)
                            + ", library " + Num(Unsafe.SizeOf<ChimeraUnit>()) + "/" + Num(Unsafe.SizeOf<ChimeraBuilding>())
                            + " (expected " + Num(Abi.UnitSize) + "/" + Num(Abi.BuildingSize) + ")");
                    return Abi.Ok;
                }
            }
            catch (Exception e) { return Fault(0, e); }
        }

        [UnmanagedCallersOnly(EntryPoint = "chimera_build_info", CallConvs = new[] { typeof(CallConvCdecl) })]
        public static int BuildInfo(byte* buf, int cap, int* len)
        {
            try
            {
                lock (SessionTable.Gate)
                {
                    string info = "abi=1.0;algo=" + Num(SimChecksum.AlgoVersion)
                        + ";commit=" + BuildStamp.Commit + ";dirty=" + BuildStamp.Dirty
                        + ";runtime=" + RuntimeInformation.FrameworkDescription
                        + ";aot=" + (RuntimeFeature.IsDynamicCodeSupported ? "0" : "1");
                    return WriteString(info, buf, cap, len);
                }
            }
            catch (Exception e) { return Fault(0, e); }
        }

        // ── sessions ─────────────────────────────────────────────────────────────────────────────────────────────

        [UnmanagedCallersOnly(EntryPoint = "chimera_session_create", CallConvs = new[] { typeof(CallConvCdecl) })]
        public static int SessionCreate(byte* contentRoot, byte* scenario, ulong seed, uint flags, int* outId)
        {
            try
            {
                lock (SessionTable.Gate)
                {
                    if (outId == null) return Fail(0, Abi.EArg, "session_create: out_id is null");
                    *outId = 0;
                    if (contentRoot == null || scenario == null) return Fail(0, Abi.EArg, "session_create: content_root and scenario are required");
                    if ((flags & ~Abi.FlagAi) != 0) return Fail(0, Abi.EArg, "session_create: unknown flag bits 0x" + (flags & ~Abi.FlagAi).ToString("X", CultureInfo.InvariantCulture));
                    if (SessionTable.LiveCount >= Abi.MaxSessions) return Fail(0, Abi.EArg, "session_create: too many live sessions");
                    string? root = Marshal.PtrToStringUTF8((IntPtr)contentRoot);
                    string? scn = Marshal.PtrToStringUTF8((IntPtr)scenario);
                    if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(scn)) return Fail(0, Abi.EArg, "session_create: content_root and scenario must not be empty");

                    var log = new CollectingLog();
                    SimSession sim;
                    try
                    {
                        sim = SimSession.Build(root, scn, seed, (flags & Abi.FlagAi) != 0, log);
                    }
                    catch (SimSessionBuildException e)
                    {
                        int code = e.Failure == SimSessionFailure.ScenarioRejected ? Abi.EScenario : Abi.EContent;
                        return Fail(0, code, e.Message + log.Joined());
                    }
                    PreTickHashes pre = sim.PreTick();
                    NativeSession s = SessionTable.Add(sim, pre);
                    *outId = s.Id;
                    return Abi.Ok;
                }
            }
            catch (Exception e) { return Fault(0, e); }
        }

        [UnmanagedCallersOnly(EntryPoint = "chimera_session_destroy", CallConvs = new[] { typeof(CallConvCdecl) })]
        public static int SessionDestroy(int session)
        {
            try
            {
                lock (SessionTable.Gate)
                {
                    if (!SessionTable.Remove(session)) return Fail(0, Abi.ESession, "unknown or destroyed session " + Num(session));
                    return Abi.Ok;
                }
            }
            catch (Exception e) { return Fault(0, e); }
        }

        // ── checksums ────────────────────────────────────────────────────────────────────────────────────────────

        [UnmanagedCallersOnly(EntryPoint = "chimera_set_checksum_interval", CallConvs = new[] { typeof(CallConvCdecl) })]
        public static int SetChecksumInterval(int session, int interval)
        {
            try
            {
                lock (SessionTable.Gate)
                {
                    if (!TrySession(session, out NativeSession s)) return Abi.ESession;
                    if (interval < 0) return Fail(session, Abi.EArg, "checksum interval must be >= 0");
                    s.Sim.Host.ChecksumInterval = interval; // host loop config; the interval is not folded into any hash
                    return Abi.Ok;
                }
            }
            catch (Exception e) { return Fault(session, e); }
        }

        [UnmanagedCallersOnly(EntryPoint = "chimera_last_checksum", CallConvs = new[] { typeof(CallConvCdecl) })]
        public static int LastChecksum(int session, uint* tick, uint* hash)
        {
            try
            {
                lock (SessionTable.Gate)
                {
                    if (!TrySession(session, out NativeSession s)) return Abi.ESession;
                    if (tick == null || hash == null) return Fail(session, Abi.EArg, "last_checksum: null out pointer");
                    *tick = s.LastChecksumTick;
                    *hash = s.LastChecksumHash;
                    return Abi.Ok;
                }
            }
            catch (Exception e) { return Fault(session, e); }
        }

        [UnmanagedCallersOnly(EntryPoint = "chimera_pre_tick_hashes", CallConvs = new[] { typeof(CallConvCdecl) })]
        public static int PreTickHashesExport(int session, ulong* output)
        {
            try
            {
                lock (SessionTable.Gate)
                {
                    if (!TrySession(session, out NativeSession s)) return Abi.ESession;
                    if (output == null) return Fail(session, Abi.EArg, "pre_tick_hashes: null out pointer");
                    s.Pre.CopyTo(new Span<ulong>(output, Abi.PreTickCount));
                    return Abi.Ok;
                }
            }
            catch (Exception e) { return Fault(session, e); }
        }

        // ── the command stream and the tick ──────────────────────────────────────────────────────────────────────

        [UnmanagedCallersOnly(EntryPoint = "chimera_submit_order", CallConvs = new[] { typeof(CallConvCdecl) })]
        public static int SubmitOrder(int session, int faction, int unitRef, int cmd, int xRaw, int zRaw, int slot)
        {
            NativeSession? live = null;
            try
            {
                lock (SessionTable.Gate)
                {
                    if (!TrySession(session, out NativeSession s)) return Abi.ESession;
                    if (s.Faulted) return Refuse(s, "submit_order");
                    // Faction ordinals index the sim's per-faction stores (FACTION_ARRAY_SIZE slots, 0 = Neutral): reject
                    // the rest here, before OrderApplier could throw partway through an apply.
                    if ((uint)faction >= (uint)FactionRegistry.FACTION_ARRAY_SIZE)
                        return Fail(session, Abi.EArg, "submit_order: faction must be 0-" + Num(FactionRegistry.FACTION_ARRAY_SIZE - 1));
                    if ((uint)cmd > 255 || (uint)slot > 255)
                        return Fail(session, Abi.EArg, "submit_order: cmd and slot must be 0-255");
                    var row = new OrderRow(0, (byte)faction, unitRef, (byte)cmd, xRaw, zRaw, (byte)slot);
                    UnitOrder order = row.ToUnitOrder();
                    live = s;
                    return s.Sim.ApplyOrder(row.IssuingFaction, in order) ? 0 : 1;
                }
            }
            catch (Exception e) { return FaultSession(live, session, e); }
        }

        [UnmanagedCallersOnly(EntryPoint = "chimera_step", CallConvs = new[] { typeof(CallConvCdecl) })]
        public static int Step(int session)
        {
            NativeSession? live = null;
            try
            {
                lock (SessionTable.Gate)
                {
                    if (!TrySession(session, out NativeSession s)) return Abi.ESession;
                    if (s.Faulted) return Refuse(s, "step");
                    live = s;
                    s.Sim.Step(); // the loop's checksum sink (NativeSession) records (tick, hash) when it really folds
                    return Abi.Ok;
                }
            }
            catch (Exception e) { return FaultSession(live, session, e); }
        }

        // ── reading the world ────────────────────────────────────────────────────────────────────────────────────

        [UnmanagedCallersOnly(EntryPoint = "chimera_read_units", CallConvs = new[] { typeof(CallConvCdecl) })]
        public static int ReadUnits(int session, ChimeraUnit* output, int cap, int* count)
        {
            try
            {
                lock (SessionTable.Gate)
                {
                    if (!TrySession(session, out NativeSession s)) return Abi.ESession;
                    if (count == null) return Fail(session, Abi.EArg, "read_units: count is null");
                    EntityWorld w = s.Sim.Host.World;
                    int n = w.HighWaterMark;
                    *count = n;
                    if (output == null && cap == 0) return Abi.EBuffer; // the size query: *count set, error slot untouched
                    if (n > cap || (n > 0 && output == null))
                        return Fail(session, Abi.EBuffer, "read_units: need " + Num(n) + " rows, capacity " + Num(cap));
                    for (int id = 0; id < n; id++)
                    {
                        ChimeraUnit* r = output + id;
                        FixedVec3 p = w.Position[id], q = w.PrevPosition[id], v = w.Velocity[id];
                        r->Id = id;
                        r->Ref = w.PackRef(id);
                        r->PosX = p.X.Raw; r->PosY = p.Y.Raw; r->PosZ = p.Z.Raw;
                        r->PrevX = q.X.Raw; r->PrevY = q.Y.Raw; r->PrevZ = q.Z.Raw;
                        r->VelX = v.X.Raw; r->VelY = v.Y.Raw; r->VelZ = v.Z.Raw;
                        r->HpRaw = w.Health[id].Raw;
                        r->MaxHpRaw = w.EffectiveMaxHealth[id].Raw;
                        r->Faction = (byte)w.FactionOf[id];
                        r->MeshType = w.MeshType[id];
                        r->Flags = (byte)w.Flags[id];
                        r->Command = (byte)w.CommandState[id];
                    }
                    return Abi.Ok;
                }
            }
            catch (Exception e) { return Fault(session, e); }
        }

        [UnmanagedCallersOnly(EntryPoint = "chimera_read_buildings", CallConvs = new[] { typeof(CallConvCdecl) })]
        public static int ReadBuildings(int session, ChimeraBuilding* output, int cap, int* count)
        {
            try
            {
                lock (SessionTable.Gate)
                {
                    if (!TrySession(session, out NativeSession s)) return Abi.ESession;
                    if (count == null) return Fail(session, Abi.EArg, "read_buildings: count is null");
                    BuildingStore b = s.Sim.Host.Buildings;
                    int n = b.Count;
                    *count = n;
                    if (output == null && cap == 0) return Abi.EBuffer; // the size query: *count set, error slot untouched
                    if (n > cap || (n > 0 && output == null))
                        return Fail(session, Abi.EBuffer, "read_buildings: need " + Num(n) + " rows, capacity " + Num(cap));
                    for (int i = 0; i < n; i++)
                    {
                        ChimeraBuilding* r = output + i;
                        FixedVec3 p = b.Position[i];
                        r->Slot = i;
                        r->PosX = p.X.Raw; r->PosY = p.Y.Raw; r->PosZ = p.Z.Raw;
                        r->HpRaw = b.Health[i].Raw;
                        r->MaxHpRaw = b.MaxHealth[i].Raw;
                        r->Faction = (byte)b.FactionOf[i];
                        r->Type = (byte)b.Type[i];
                        r->Alive = b.Alive[i] ? (byte)1 : (byte)0;
                        r->Pad = 0;
                    }
                    return Abi.Ok;
                }
            }
            catch (Exception e) { return Fault(session, e); }
        }

        [UnmanagedCallersOnly(EntryPoint = "chimera_units_digest", CallConvs = new[] { typeof(CallConvCdecl) })]
        public static int UnitsDigest(int session, ulong* output)
        {
            try
            {
                lock (SessionTable.Gate)
                {
                    if (!TrySession(session, out NativeSession s)) return Abi.ESession;
                    if (output == null) return Fail(session, Abi.EArg, "units_digest: null out pointer");
                    *output = WorldDigest.UnitsDigest(s.Sim.Host.World);
                    return Abi.Ok;
                }
            }
            catch (Exception e) { return Fault(session, e); }
        }

        [UnmanagedCallersOnly(EntryPoint = "chimera_wide_digest", CallConvs = new[] { typeof(CallConvCdecl) })]
        public static int WideDigest(int session, ulong* output)
        {
            try
            {
                lock (SessionTable.Gate)
                {
                    if (!TrySession(session, out NativeSession s)) return Abi.ESession;
                    if (output == null) return Fail(session, Abi.EArg, "wide_digest: null out pointer");
                    SimulationHost host = s.Sim.Host;
                    *output = ProjectChimera.Core.Sim.WorldDigest.WideDigest(host.World, host.Projectiles);
                    return Abi.Ok;
                }
            }
            catch (Exception e) { return Fault(session, e); }
        }

        [UnmanagedCallersOnly(EntryPoint = "chimera_unit_def_id", CallConvs = new[] { typeof(CallConvCdecl) })]
        public static int UnitDefId(int session, int unitId, byte* buf, int cap, int* len)
        {
            try
            {
                lock (SessionTable.Gate)
                {
                    if (!TrySession(session, out NativeSession s)) return Abi.ESession;
                    EntityWorld w = s.Sim.Host.World;
                    if (unitId < 0 || unitId >= w.HighWaterMark) return Fail(session, Abi.EArg, "unit_def_id: unit id out of range");
                    return WriteString(w.SourceDefinition[unitId]?.Id ?? "", buf, cap, len);
                }
            }
            catch (Exception e) { return Fault(session, e); }
        }

        [UnmanagedCallersOnly(EntryPoint = "chimera_building_def_id", CallConvs = new[] { typeof(CallConvCdecl) })]
        public static int BuildingDefId(int session, int slot, byte* buf, int cap, int* len)
        {
            try
            {
                lock (SessionTable.Gate)
                {
                    if (!TrySession(session, out NativeSession s)) return Abi.ESession;
                    BuildingStore b = s.Sim.Host.Buildings;
                    if (slot < 0 || slot >= b.Count) return Fail(session, Abi.EArg, "building_def_id: slot out of range");
                    return WriteString(b.DefinitionId[slot] ?? "", buf, cap, len);
                }
            }
            catch (Exception e) { return Fault(session, e); }
        }

        [UnmanagedCallersOnly(EntryPoint = "chimera_verdict", CallConvs = new[] { typeof(CallConvCdecl) })]
        public static int Verdict(int session, int* output)
        {
            try
            {
                lock (SessionTable.Gate)
                {
                    if (!TrySession(session, out NativeSession s)) return Abi.ESession;
                    if (output == null) return Fail(session, Abi.EArg, "verdict: null out pointer");
                    WinStateStore w = s.Sim.Host.WinState;
                    *output = w.Verdict[(int)Faction.Player1] | (w.Verdict[(int)Faction.Player2] << 4);
                    return Abi.Ok;
                }
            }
            catch (Exception e) { return Fault(session, e); }
        }

        [UnmanagedCallersOnly(EntryPoint = "chimera_stats", CallConvs = new[] { typeof(CallConvCdecl) })]
        public static int Stats(int session, long* output)
        {
            try
            {
                lock (SessionTable.Gate)
                {
                    if (output == null) return Fail(session, Abi.EArg, "stats: null out pointer");
                    long tick = 0, alive = 0, hwm = 0;
                    if (session != 0)
                    {
                        if (!TrySession(session, out NativeSession s)) return Abi.ESession;
                        tick = s.Sim.Host.CurrentTick;
                        alive = s.Sim.Host.World.AliveCount;
                        hwm = s.Sim.Host.World.HighWaterMark;
                    }
                    output[0] = GC.GetTotalMemory(false);
                    output[1] = GC.CollectionCount(0);
                    output[2] = GC.CollectionCount(1);
                    output[3] = GC.CollectionCount(2);
                    output[4] = GC.GetTotalAllocatedBytes(false);
                    output[5] = tick;
                    output[6] = alive;
                    output[7] = hwm;
                    return Abi.Ok;
                }
            }
            catch (Exception e) { return Fault(session, e); }
        }

        // ── files, self tests, errors ────────────────────────────────────────────────────────────────────────────

        [UnmanagedCallersOnly(EntryPoint = "chimera_file_sha256", CallConvs = new[] { typeof(CallConvCdecl) })]
        public static int FileSha256(byte* path, byte* buf, int cap, int* len)
        {
            try
            {
                lock (SessionTable.Gate)
                {
                    if (path == null) return Fail(0, Abi.EArg, "file_sha256: path is null");
                    string? p = Marshal.PtrToStringUTF8((IntPtr)path);
                    if (string.IsNullOrEmpty(p)) return Fail(0, Abi.EArg, "file_sha256: path is empty");
                    byte[] hash;
                    try
                    {
                        using FileStream fs = File.OpenRead(p);
                        hash = SHA256.HashData(fs);
                    }
                    catch (IOException e) { return Fail(0, Abi.EContent, "file_sha256: " + e.Message); }
                    catch (UnauthorizedAccessException e) { return Fail(0, Abi.EContent, "file_sha256: " + e.Message); }
                    return WriteString(Convert.ToHexString(hash).ToLowerInvariant(), buf, cap, len);
                }
            }
            catch (Exception e) { return Fault(0, e); }
        }

        private static byte[]? s_keep; // defeats dead-store elimination of the selftest allocation

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int Deref(int[]? a) => a![0];

        [UnmanagedCallersOnly(EntryPoint = "chimera_selftest", CallConvs = new[] { typeof(CallConvCdecl) })]
        public static int SelfTest(int kind)
        {
            try
            {
                lock (SessionTable.Gate)
                {
                    switch (kind)
                    {
                        case 1:
                            try { throw new InvalidOperationException("selftest 1"); }
                            catch (InvalidOperationException) { return 0; }
                        case 2:
                            try { return Deref(null) == 0 ? 21 : 22; }
                            catch (NullReferenceException) { return 0; }
                        case 3:
                        {
                            int before = GC.CollectionCount(2);
                            s_keep = new byte[64 * 1024 * 1024];
                            for (int i = 0; i < s_keep.Length; i += 4096) s_keep[i] = 1;
                            s_keep = null;
                            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
                            GC.WaitForPendingFinalizers();
                            return GC.CollectionCount(2) > before ? 0 : 31;
                        }
                        default:
                            return Fail(0, Abi.EArg, "selftest: unknown kind " + Num(kind));
                    }
                }
            }
            catch (Exception e) { return Fault(0, e); }
        }

        [UnmanagedCallersOnly(EntryPoint = "chimera_last_error", CallConvs = new[] { typeof(CallConvCdecl) })]
        public static int LastError(int sessionOr0, byte* buf, int cap, int* len)
        {
            try
            {
                lock (SessionTable.Gate)
                {
                    string msg;
                    if (sessionOr0 == 0) msg = SessionTable.ProcessError;
                    else if (SessionTable.TryGet(sessionOr0, out NativeSession s)) msg = s.LastError;
                    else
                    {
                        return Fail(0, Abi.ESession, "unknown or destroyed session " + Num(sessionOr0));
                    }
                    return WriteString(msg, buf, cap, len);
                }
            }
            catch (Exception e) { return Fault(0, e); }
        }
    }
}
