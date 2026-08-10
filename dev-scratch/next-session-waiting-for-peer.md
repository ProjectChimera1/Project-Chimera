# Next session: the "Waiting for peer…" stalls (DW-924, open)

Project Chimera's online match is now correct AND mostly playable — fog, buildings, menus, ping
readout all fixed 2026-08-10. What remains is the original render-cost mystery in its final,
sharpened form: **both machines hit bursts of 135–145 ms frames whose time goes NOWHERE the
process can see, and every burst window stalls the lockstep long enough to flash the banner.**

## The measurement that matters (do not re-derive)

Every slow frame now attributes itself in the client logs (DW-924 `FramePhaseProbe`):

    [Catchup] 3 sim ticks in one 145 ms frame at tick 2343 — sim 0 ms (0.1 ms/tick), presentation 144 ms
      [phase: render cpu 0.7 + gpu 6.0 ms | gc 0/0/0 +0.0 ms pause | faults +0 | focus 84.3s ago]

Read that tail: of a 145 ms frame, the GPU worked **6 ms**, render CPU **0.7 ms**, zero GC, zero
page faults, no focus change. ~138 ms are spent in NO measurable phase. Identical signature on
the RTX 3060 PC (144 Hz, D3D12) and the GTX 1650 laptop (60 Hz, D3D12). A 150 ms artificial
process-suspend reproduces the signature EXACTLY (`godot/tools/loopback-suspend-pulse.ps1`), so
the class is "main thread blocked wholesale by something outside the game": present/swapchain
stall, DWM/MPO transition, driver, or AV.

## Exonerated — do not re-investigate (all with numbers, 2026-08-10 overnight + field)

- The renderer/content: 16M primitives at 265 FPS offline on the same PC; laptop never below 60.
- Sim tick (0.1 ms), GC, paging (probe fields say 0 on every field burst).
- Synthetic loads on the exact rig: GPU, 12-core CPU, Wi-Fi saturation, 8 GB touched memory
  pressure, mouse storms, camera panning, focus churn — ~14,000 loopback ticks, zero frames ≥67 ms.
  Baseline rig: `godot/tools/loopback-perf-rig.ps1` (a run here is KNOWN-CLEAN; if it ever shows
  Catchup lines, whatever changed is the cause).

## The live clues

1. **Burst windows repeat by MATCH TIME across machines and nights**: ~tick 2340–2970 burst on
   the laptop (11:21 match) and the PC (01:30 and 02:27 matches, ~tick 2344–2461). Tick 4 in
   every run (match-start transition). Something match-clocked participates.
2. **Delay renegotiations cluster inside burst windows** — almost certainly DOWNSTREAM (long
   frames delay packet sends → server RTT jitter → renegotiation), but the feedback loop
   (jitter → delay change → gap seed → stall → banner) is what the player FEELS. Worth asking:
   should the server's delay controller DAMP oscillation (2↔3 flapping all match in the 11:21 log)?
3. **The environment line** (`[FrameProbe]`) exposed: clients run **ExclusiveFullscreen although
   settings.json says "windowed"** — find WHY (project.godot mode=3 vs SettingsManager mapping;
   this mismatch is a bug on its own), and the PC pairs a 144 Hz panel with a 60 Hz one (mixed-
   refresh DWM interactions are a known stutter class on Windows 11 + D3D12).
4. Banner sightings now correlate with a live per-machine `ping N ms` HUD readout (DW-926) —
   watch whether ping jumps WITH the banner (network-led) or stays flat while frames burst
   (present-led).

## The A/B experiments queued (cheapest first)

1. **Vulkan vs D3D12**: `godot/tools/loopback-perf-rig.ps1 -Tag vulkan -ExtraArgs "--rendering-driver vulkan"`
   first (sanity), then a REAL two-machine match with both clients launched with that flag.
   D3D12 is the less-mature Godot backend and the whole "externally blocked present" class
   suspects it. If bursts vanish → flip `rendering_device/driver.windows` and done.
2. **True windowed / borderless**: fix the ExclusiveFullscreen-despite-windowed-settings bug,
   re-run — exclusive fullscreen mode transitions are the other prime suspect.
3. If both fail: Windows Performance Recorder / LatencyMon on the PC during a match — the probe
   has proven the block is outside the process, so the next instrument must be OS-level.

**Success metric**: the `[FrameHistogram]` line each client prints on window close. The 11:21
laptop match read `67-100=0 100-150=16 >=150=0` of 36,290 frames. Target: 67ms+ buckets → 0,
and Alec reporting the banner gone.

## Constraints (unchanged)

- Presentation-only changes cannot desync; free hand in `src/UI/**`. Do NOT move work back out
  of the sim tick (`FlowFieldSteeringTickPacingTests` guards DW-916 at spine index 3).
- `src/UI/**`, `src/Core/Bootstrap/**`, `MainScene.cs`, `scenes/**` → in-engine gate (godot/CLAUDE.md).
- Tier-1 baseline **6392 / 0 / 1**; `CanonicalModelHashPerf` alone failing = CPU-contention flake.
- The godot-mcp bridge is single-client (127.0.0.1:6550); close idle sessions first.

## Also open (pick up if time allows)

- **DW-929**: selecting an ENEMY building shows its train card with a live buy button — gate the
  production surface on ownership AND verify the order path rejects non-owned training (if it
  doesn't, that's an ownership bypass into folded stores — test first, it outranks the UI fix).
- **Formal in-engine gate pass** on DW-917/920/921/923 (+ verify lines of DW-925/927/928): all
  field-confirmed by Alec in live matches; the scripted `/godot-verify` pass required by
  godot/CLAUDE.md was never run. One combined in-engine session covers the whole list.
- Replay QoL noted in DW-927: perspective cycling shares one sticky fog grid (cosmetic), and
  replay playback advances at FRAME rate (~8× on a 260 FPS machine) rather than wall-clock.
