#nullable enable
using Godot;

namespace ProjectChimera.UI
{
    /// <summary>
    /// DW-924 — per-frame PHASE ATTRIBUTION for the online render-cost investigation.
    ///
    /// <para><b>Why this exists.</b> The 2026-08-10 two-machine runs logged sustained bursts of 80–150 ms frames
    /// (`[Catchup]` lines) whose cost sat entirely outside the sim step loop — and a full day of controlled
    /// reproduction on the SAME machine, build, map and settings could not produce a single one: not under GPU
    /// load (16M primitives rendered at 265 FPS), not under full-core CPU contention, not under Wi-Fi saturation,
    /// not under 8 GB of touched memory pressure, not under focus churn, mouse storms or camera panning, and not
    /// in a 12,000-tick loopback lockstep match. Meanwhile the field bursts are TICK-LOCKED across separate
    /// matches (tick 4 in every run; ~150–166 and ~843–847 in two runs 40 minutes apart), so the trigger is
    /// deterministic and only present in a real two-machine session. "Presentation 137 ms" names a bucket, not a
    /// cause — this probe splits that bucket in the moment, on the rig where the problem actually happens.</para>
    ///
    /// <para><b>What it measures, per frame.</b> Viewport render time CPU and GPU
    /// (<see cref="RenderingServer.ViewportSetMeasureRenderTime"/> — the same counters the editor profiler uses),
    /// .NET GC collection counts and total-pause delta (<see cref="System.GC.GetTotalPauseDuration"/>), the
    /// process hard/soft page-fault delta (PSAPI), and the time since the last application focus transition.
    /// Every value is a PREVIOUS-frame sample, which aligns with the `[Catchup]` line's own <c>frameMs</c>
    /// semantics (`delta` measures the frame BEFORE the one that prints; the GPU counter may lag one further
    /// frame — close enough to attribute a 100 ms burst). Each outcome class of the next slow run now reads
    /// differently in the log:
    /// <list type="bullet">
    ///   <item>GPU ms high → real GPU load (shadow/geometry — the LOD path).</item>
    ///   <item>render CPU ms high → renderer main-thread cost (culling, buffer uploads).</item>
    ///   <item>both low, GC pause or faults high → collector or paging stalls (also covers the rare 88–229 ms
    ///     spikes INSIDE StepOnce those runs logged).</item>
    ///   <item>everything low with frameMs huge → the present/compositor path blocked (vsync, DWM/MPO, driver)
    ///     — re-run the clients with <c>--rendering-driver vulkan</c> to A/B the D3D12 backend.</item>
    ///   <item>bursts bracketed by `[Focus]` lines → window/occlusion transitions (the operator alt-tabs between
    ///     two machines in every real run; no solo rig does).</item>
    /// </list></para>
    ///
    /// <para><b>Presentation-only.</b> Reads no sim state, writes no sim state, never folded into
    /// <c>SimChecksum</c> — wall-clock observation exactly like the DW-919 sim/presentation split it extends.
    /// Cost when frames are healthy: two RenderingServer reads, three GC counter reads, one PSAPI call — well
    /// under the noise floor of a 33 ms tick.</para>
    /// </summary>
    public sealed class FramePhaseProbe
    {
        // ── Previous-frame samples (aligned with [Catchup]'s previous-frame frameMs) ──────────────────────────
        private double _renderCpuMs;
        private double _renderGpuMs;
        private double _gcPauseDeltaMs;
        private int    _gc0Delta, _gc1Delta, _gc2Delta;
        private long   _pageFaultDelta;

        // ── Counter baselines ─────────────────────────────────────────────────────────────────────────────────
        private System.TimeSpan _lastGcPause;
        private int  _lastGc0, _lastGc1, _lastGc2;
        private long _lastPageFaults;

        // ── Focus transitions (stamped by MainScene._Notification) ────────────────────────────────────────────
        private ulong _lastFocusChangeMs; // Time.GetTicksMsec at the last transition; 0 = none seen yet

        // ── Frame-time histogram, dumped at match end ─────────────────────────────────────────────────────────
        // Buckets: <17 / 17–33 / 33–67 / 67–100 / 100–150 / ≥150 ms. The last three are the buckets a player
        // FEELS; their counts quantify "unpleasant to play" run-over-run, so any future fix has a number to move.
        private static readonly double[] BUCKET_EDGES = { 17.0, 33.4, 66.7, 100.0, 150.0 };
        private readonly long[] _hist = new long[BUCKET_EDGES.Length + 1];
        private double _worstFrameMs;
        private long   _sampledFrames;

        private Rid  _viewport;
        private bool _attached;

        /// <summary>Frames sampled this match — the "did an online match actually run" gate for the summary dump.</summary>
        public long SampledFrames => _sampledFrames;

        /// <summary>
        /// Enable viewport render-time measurement and print the one-time present-environment line (window mode,
        /// vsync, refresh rate, adapter). Idempotent; called lazily from the online branch so offline play pays
        /// nothing. The environment line exists because tonight's investigation burned an hour discovering the
        /// occluded-window/vsync state differed between rigs — now every run states it up front.
        /// </summary>
        public void EnsureAttached(Viewport viewport)
        {
            if (_attached) return;
            _attached = true;

            _viewport = viewport.GetViewportRid();
            RenderingServer.ViewportSetMeasureRenderTime(_viewport, true);

            // Seed baselines so the first sample's deltas are ~0 rather than process-lifetime totals.
            _lastGcPause    = System.GC.GetTotalPauseDuration();
            _lastGc0        = System.GC.CollectionCount(0);
            _lastGc1        = System.GC.CollectionCount(1);
            _lastGc2        = System.GC.CollectionCount(2);
            _lastPageFaults = ReadPageFaultCount();

            GD.Print($"[FrameProbe] Present environment: window={DisplayServer.WindowGetMode()} " +
                     $"vsync={DisplayServer.WindowGetVsyncMode()} size={DisplayServer.WindowGetSize()} " +
                     $"screen={DisplayServer.ScreenGetRefreshRate():F0} Hz adapter=\"{RenderingServer.GetVideoAdapterName()}\" " +
                     $"(DW-924 phase attribution active).");
        }

        /// <summary>
        /// Capture this frame's previous-frame phase numbers and bucket <paramref name="prevFrameMs"/> into the
        /// match histogram. Call once per rendered frame from the online branch, before the step loop.
        /// </summary>
        public void SampleFrame(double prevFrameMs)
        {
            if (!_attached) return;

            _renderCpuMs = RenderingServer.ViewportGetMeasuredRenderTimeCpu(_viewport);
            _renderGpuMs = RenderingServer.ViewportGetMeasuredRenderTimeGpu(_viewport);

            System.TimeSpan pause = System.GC.GetTotalPauseDuration();
            _gcPauseDeltaMs = (pause - _lastGcPause).TotalMilliseconds;
            _lastGcPause    = pause;

            int g0 = System.GC.CollectionCount(0), g1 = System.GC.CollectionCount(1), g2 = System.GC.CollectionCount(2);
            _gc0Delta = g0 - _lastGc0; _gc1Delta = g1 - _lastGc1; _gc2Delta = g2 - _lastGc2;
            _lastGc0 = g0; _lastGc1 = g1; _lastGc2 = g2;

            long faults = ReadPageFaultCount();
            _pageFaultDelta = faults - _lastPageFaults;
            _lastPageFaults = faults;

            _sampledFrames++;
            if (prevFrameMs > _worstFrameMs) _worstFrameMs = prevFrameMs;
            int b = 0;
            while (b < BUCKET_EDGES.Length && prevFrameMs >= BUCKET_EDGES[b]) b++;
            _hist[b]++;
        }

        /// <summary>Stamp an application focus transition (wired from MainScene._Notification). The tick makes a
        /// burst that starts right after an alt-tab name itself in one run.</summary>
        public void NoteFocusChange(bool gained, uint tick)
        {
            _lastFocusChangeMs = Time.GetTicksMsec();
            if (_attached)
                GD.Print($"[Focus] Application focus {(gained ? "GAINED" : "LOST")} at sim tick {tick}.");
        }

        /// <summary>The attribution tail appended to each `[Catchup]` line. Self-contained bracket so the existing
        /// line format (which tooling greps) is extended, never altered.</summary>
        public string CatchupSuffix()
        {
            if (!_attached) return "";
            string focus = _lastFocusChangeMs == 0
                ? "none"
                : $"{(Time.GetTicksMsec() - _lastFocusChangeMs) / 1000.0:F1}s ago";
            return $" [phase: render cpu {_renderCpuMs:F1} + gpu {_renderGpuMs:F1} ms | " +
                   $"gc {_gc0Delta}/{_gc1Delta}/{_gc2Delta} +{_gcPauseDeltaMs:F1} ms pause | " +
                   $"faults +{_pageFaultDelta} | focus {focus}]";
        }

        /// <summary>Match-end histogram line. Dump only when <see cref="SampledFrames"/> is non-zero.</summary>
        public string Summary() =>
            $"{_sampledFrames} frames: <17ms={_hist[0]}  17-33={_hist[1]}  33-67={_hist[2]}  " +
            $"67-100={_hist[3]}  100-150={_hist[4]}  >=150={_hist[5]}  worst={_worstFrameMs:F0} ms " +
            "(67ms+ buckets are the frames a player feels — the number a render fix has to move).";

        /// <summary>Reset per-match accumulation (histogram, worst) on the return-to-Edit seam. Counter baselines
        /// and the viewport attachment survive — they are process-lifetime, not match-lifetime.</summary>
        public void ResetMatch()
        {
            System.Array.Clear(_hist, 0, _hist.Length);
            _worstFrameMs  = 0;
            _sampledFrames = 0;
        }

        // ── PSAPI page-fault counter ──────────────────────────────────────────────────────────────────────────
        // PageFaultCount covers hard AND soft faults; a paging storm shows as tens of thousands per 100 ms frame,
        // ordinary allocation as a few dozen — the magnitudes do not overlap, so one counter suffices.

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct PROCESS_MEMORY_COUNTERS
        {
            public uint cb;
            public uint PageFaultCount;
            public System.UIntPtr PeakWorkingSetSize;
            public System.UIntPtr WorkingSetSize;
            public System.UIntPtr QuotaPeakPagedPoolUsage;
            public System.UIntPtr QuotaPagedPoolUsage;
            public System.UIntPtr QuotaPeakNonPagedPoolUsage;
            public System.UIntPtr QuotaNonPagedPoolUsage;
            public System.UIntPtr PagefileUsage;
            public System.UIntPtr PeakPagefileUsage;
        }

        [System.Runtime.InteropServices.DllImport("psapi.dll", SetLastError = true)]
        private static extern bool GetProcessMemoryInfo(System.IntPtr process,
                                                        out PROCESS_MEMORY_COUNTERS counters, uint size);

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern System.IntPtr GetCurrentProcess();

        /// <summary>Process-lifetime page-fault count; 0 on failure (never throws — a diagnostic must not be able
        /// to take the match down).</summary>
        private static long ReadPageFaultCount()
        {
            try
            {
                if (GetProcessMemoryInfo(GetCurrentProcess(), out PROCESS_MEMORY_COUNTERS c,
                                         (uint)System.Runtime.InteropServices.Marshal.SizeOf<PROCESS_MEMORY_COUNTERS>()))
                    return c.PageFaultCount;
            }
            catch (System.Exception) { /* diagnostic only — fall through */ }
            return 0;
        }
    }
}
