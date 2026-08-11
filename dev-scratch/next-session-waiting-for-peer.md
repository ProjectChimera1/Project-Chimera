# "Waiting for peer…" — state after the 2026-08-11 burn-down session (DW-924 still the quarry)

Five deferrals moved this session (master `bc711913` + the docs commit after it): **DW-929, DW-931,
DW-932 closed with in-engine verification; DW-930 fixed and PC-verified; DW-924 narrowed hard.**
The formal scripted gate passes owed on DW-917/920/921/923 are DONE (see their ledger verify lines).
Tier-1: **6404 / 0 / 1** (baseline 6392 + 12 new pins). The debug seam gained a building/fog/selection
half (`DebugBuildingJson`, `DebugFogAt`, `DebugSelectBuilding/Unit`, `DebugPlaceBuilding`) — building-
facing gates no longer need a human.

## What changed for the next real match (both machines: git pull + rebuild + relaunch)

1. **Clients finally run the window mode settings.json asks for** (DW-930). They had run
   EXCLUSIVE fullscreen since Story 11.7 — a Godot 4.6.3/Windows bug: a boot-created-fullscreen
   window turns a later Windowed request into permanent ExclusiveFullscreen. Boot is now windowed
   (`project.godot` mode 0), the persisted mode is reapplied pre-first-frame, and a
   `[Settings] window mode requested X but engine reports Y` tripwire prints on any future mismatch.
2. **The delay controller no longer flaps** (DW-931). Streak + asymmetric dwell + deadband: expect
   ~1–3 renegotiations per match instead of ~40, so almost no widening gap-seed hiccups. Rig-observed:
   ≤5 dictates per 8100-tick run, 143/143 determinism windows clean.
3. **Enemy/spectator building cards are info-only** (DW-929), and **replays play at wall-clock with
   per-perspective fog** (DW-932) — both verified in-engine.

## DW-924: what the four-run rig matrix eliminated (2026-08-11, full table in the ledger entry)

**The burst class now reproduces on the loopback rig** (it could not on 2026-08-10): 70–148 ms
frames, externally-blocked `[phase:]` signature, on ONE machine, no LAN. That converts DW-924 from
a two-machine mystery into a local, 4-minute-per-experiment problem.

- **Eliminated: window mode.** Windowed and ExclusiveFullscreen both burst (14/65 vs 34/34 ≥67 ms
  frames per 8100 ticks). DW-930 stands as a settings-honesty fix, not the burst cure.
- **Eliminated: the vsync/present-queue wait.** vsync OFF still bursts (13/11) — the main thread is
  NOT waiting on the compositor swap; the block is driver/kernel/DWM-scheduler side.
- **Amplifier found: a second GPU-presenting process.** Editor open ≈ 3–5× more bursts (65 vs 13).
  On match nights, close everything that renders (editor, hw-accel browsers, overlays).
- **Open question: why was the 2026-08-10 overnight rig CLEAN (0 in 14k ticks)?** Same script, same
  map. Suspects: machine uptime/DWM state (known Windows 11 MPO degradation class), driver/pending-
  update state, thermals.

## Next experiments, ALL local, cheapest first (rig: `godot/tools/loopback-perf-rig.ps1 -Tag <t>`)

1. **Reboot → immediately `-Tag postboot`.** Clean ⇒ uptime/DWM state is the trigger; the fix for
   match nights is "fresh boot, nothing else rendering" while the OS-level cause is chased.
2. **MPO kill switch**: `HKLM\SOFTWARE\Microsoft\Windows\Dwm` → DWORD `OverlayTestMode=5`
   (24H2+: also `OverlayMinFPS=0`), reboot, re-run (NVIDIA KB a_id/5157). Revert by deleting the key.
3. **Windows graphics toggles**: Settings → System → Display → Graphics → "Optimizations for
   windowed games" OFF, Auto-HDR off; exe Properties → "Disable fullscreen optimizations". Re-run.
4. **PresentMon / WPR wait-analysis** on one burst if 1–3 fail — the probe proves the wait is
   outside the process, so the next instrument must be OS-level.
5. `--rendering-driver vulkan` stays a per-run discriminator via `-ExtraArgs` (research consensus:
   NOT the fix — Godot moved Windows to D3D12 deliberately; Windows Vulkan drivers are the less-
   maintained path).

**Success metric unchanged**: `[FrameHistogram]` 67 ms+ buckets → 0 on both machines in a real
interactive match, and Alec reporting the banner gone. Watch the HUD ping (DW-926): ping jumping
WITH the banner = network-led; ping flat while frames burst = present-led.

## Still owed / carried

- DW-930: borderless settings arm, external WS_CAPTION check, GTX 1650 laptop confirmation.
- DW-925 (Esc online) + DW-928 (fog'd bars/rally) verify lines: online-only, ride the next LAN match
  (field-confirmed 2026-08-10; offline Esc-menu arm re-observed this session).
- DW-931 field confirmation: real Wi-Fi flap collapse (~40 → ~1–3 dictates).
- Candidate follow-up from DW-929's review: worker/ability/inventory cards still use `FactionOf == me`
  alone — a spectator/replay viewer focusing a P1 UNIT sees its live affordances (seatless-viewer
  class, UI-only). Also DW-930's cosmetic residue: 1920×1080 decorated window overhangs a 1080p work
  area (clamp to `ScreenGetUsableRect`).

## Constraints (unchanged)

- Presentation-only changes cannot desync; free hand in `src/UI/**`. Never move work out of the sim
  tick (`FlowFieldSteeringTickPacingTests` guards DW-916).
- `src/UI/**`, `src/Core/Bootstrap/**`, `MainScene.cs`, `scenes/**`, `project.godot` → in-engine gate.
- Tier-1 baseline **6404 / 0 / 1**; `CanonicalModelHashPerf` alone failing = CPU-contention flake.
- The godot-mcp bridge is single-client (127.0.0.1:6550); close idle sessions first.
- All pre-2026-08-11 `[FrameHistogram]` baselines were recorded under ExclusiveFullscreen — never
  compare across that boundary without re-recording.
