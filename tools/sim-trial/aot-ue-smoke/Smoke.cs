#nullable enable
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AotUeSmoke;

/// <summary>Exports for the day-1 NativeAOT-inside-Unreal smoke (X1). Plain C ABI, no callbacks into the host.</summary>
public static unsafe class Exports
{
    private static byte[]? s_keep; // defeats dead-store elimination of the big allocation

    [UnmanagedCallersOnly(EntryPoint = "smoke_add", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int Add(int a, int b) => a + b;

    /// <summary>Writes "runtime=&lt;framework&gt; aot=&lt;0|1&gt;" (UTF-8, not terminated) into buf; *len gets the byte count. Returns 0, or -1 if cap is too small.</summary>
    [UnmanagedCallersOnly(EntryPoint = "smoke_runtime", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int Runtime(byte* buf, int cap, int* len)
    {
        string s = "runtime=" + RuntimeInformation.FrameworkDescription + " aot=" + (RuntimeFeature.IsDynamicCodeSupported ? "0" : "1");
        byte[] b = Encoding.UTF8.GetBytes(s);
        *len = b.Length;
        if (b.Length > cap) return -1;
        for (int i = 0; i < b.Length; i++) buf[i] = b[i];
        return 0;
    }

    /// <summary>1 throw/catch, 2 null dereference caught as NullReferenceException, 3 256 MB alloc + blocking full GC, 4 Task.Run + Wait, 5 Thread start/join. 0 = pass.</summary>
    [UnmanagedCallersOnly(EntryPoint = "smoke_selftest", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int SelfTest(int kind)
    {
        try
        {
            switch (kind)
            {
                case 1:
                    try { throw new InvalidOperationException("x1"); }
                    catch (InvalidOperationException) { return 0; }
                case 2:
                    try { return Deref(null) == 0 ? 21 : 22; }
                    catch (NullReferenceException) { return 0; }
                case 3:
                {
                    s_keep = new byte[256 * 1024 * 1024];
                    for (int i = 0; i < s_keep.Length; i += 4096) s_keep[i] = 1;
                    long before = GC.GetTotalMemory(false);
                    s_keep = null;
                    GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
                    GC.WaitForPendingFinalizers();
                    long after = GC.GetTotalMemory(true);
                    return before > after + 128L * 1024 * 1024 ? 0 : 31;
                }
                case 4:
                {
                    int v = 0;
                    Task t = Task.Run(() => { v = 42; });
                    return t.Wait(10000) && v == 42 ? 0 : 41;
                }
                case 5:
                {
                    int v = 0;
                    var th = new Thread(() => { v = 7; });
                    th.Start();
                    return th.Join(10000) && v == 7 ? 0 : 51;
                }
                default: return 99;
            }
        }
        catch (Exception) { return 100 + kind; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Deref(int[]? a) => a![0];
}
