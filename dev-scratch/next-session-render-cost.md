# Next session prompt — Project Chimera render cost / "Waiting for peer"

Paste everything below the line into a fresh session.

---

Project Chimera (RTS, Godot 4.6.3 mono, C#) has an online multiplayer match that is
CORRECT but unplayable. Determinism holds, matches run to completion, no desyncs and no
hangs. The problem is frame time. Find it and fix it.

**The one measurement that matters — already taken, do not re-derive it:**

```
[Catchup] 3 sim ticks in one 137 ms frame at tick 2500 — sim 0 ms (0.1 ms/tick), presentation 137 ms
```

The simulation costs **0.1 ms per tick**. All 80–143 ms of the frame is PRESENTATION.
Nothing in the sim tick is worth touching. Evidence: `lan-logs/*client*.out.log`,
`grep Catchup`. The split is emitted by the online branch of `MainScene._Process`
(search `DW-919`).

**Shape of the problem:** slow frames arrive in sustained bursts, not isolated hitches —
e.g. ticks 2386→2461 continuously at 91–143 ms, then completely clean for 200 ticks.
3–4 ticks per 117 ms frame is ~30 ticks/sec, i.e. the sim keeps pace while only ~8
frames render. "Waiting for peer" is a SYMPTOM, not a separate bug: the client cannot
poll ENet while the frame is blocked. Fix the frame time and the banner goes quiet.
(It already has a 400 ms debounce, so if you still see it, the stalls are real.)

**Already ruled out — do not re-investigate:**
- The sim tick (0.1 ms; measured, not assumed).
- Synchronous navmesh baking — was 136–180 ms per building change, removed in DW-918.
  `NavObstacleManager.BakeEnabled` is false; nothing queries that navmesh.
- SDFGI / SSAO / SSIL / glow — none are enabled. `LightingPhase` builds a plain
  `Environment` with colour ambient only.
- Fog occlusion (DW-920) and flow-field steering (DW-916) — both cheap, both measured.

**Prime suspects, in order:**
1. **Unit/building GLB geometry with no LOD.** `godot/assets/models/factions/*/*.glb` are
   ~468 KB each, roughly 18–30k verts, drawn through `MultiMeshBridge` (one
   `MultiMeshInstance3D` per unit type). No LOD, no occlusion culling.
2. **Shadows.** Directional shadow atlas is 8192 at `high` (`SettingsManager`), plus 4×
   MSAA. Every high-poly instance is drawn again per shadow pass.
3. **Per-frame CPU work in a presentation bridge.** `godot/src/UI/*Bridge.cs` all run
   `_Process`. `BuildingBridge.Rebuild` resizes `MultiMesh.InstanceCount` whenever its
   dirty test trips — check it is not tripping every frame.

**Cheapest first move:** set the quality preset to `low` (shadows off, MSAA off) and
re-read the `[Catchup]` split. If presentation ms collapses, it is GPU/shadow/geometry
and the fix is LOD + shadow tuning. If it barely moves, it is CPU-side in a bridge and
you want the profiler.

**Use the profiler properly.** `godot-mcp` exposes `godot_profiler`; it needs the Godot
editor OPEN with the addon enabled, and the bridge is SINGLE-CLIENT (one client on
127.0.0.1:6550 — close idle Claude sessions first). Do not guess where the time goes
when you can measure it. That lesson cost this project three sessions: the navmesh bake
was "the leading suspect" for two weeks and was dismissed on the wrong property
(frequency, when duration was what mattered).

**Constraints:**
- Presentation-only changes cannot desync — the fog grid and all render state are
  unfolded, outside `SimChecksum`. You have a free hand in `src/UI/**`.
- Do NOT move work back out of the sim tick. `FlowFieldSteeringSystem` sits at spine
  index 3 deliberately (DW-916): steering paced by the rendered frame is what desynced
  the LAN gate at tick 2640. `FlowFieldSteeringTickPacingTests` will fail if you undo it.
- Anything touching `src/UI/**`, `src/Core/Bootstrap/**`, `MainScene.cs` or `scenes/**`
  needs the in-engine gate (see `godot/CLAUDE.md`).
- Tier-1 baseline is **6392 passed / 0 failed / 1 skipped**. `CanonicalModelHashPerf` is
  a known CPU-contention flake — if it is the lone failure, re-run it in isolation.

**Also outstanding (lower priority):** the in-engine gate has never been run on the
2026-08-10 fixes — builders trainable at the command center, fog occlusion, building
vision radius, command-card label wrapping. All are confirmed working in live play by
the developer, but none have been formally observed through the bridge.

Read `Snapshot.md` and the tail of
`_bmad-output/implementation-artifacts/deferred-work.md` (DW-916 through DW-923 plus the
FIELD VERIFICATION block) before starting.
