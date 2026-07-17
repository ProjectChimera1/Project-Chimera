---
status: blocked
---

# BMad Dev Auto Result

## RESOLVED 2026-07-17 — the blocking condition below NO LONGER HOLDS

Story 7.5 has since LANDED on master as merge commit `1597e4d` (finalized `eb75385`; sprint-status
`7-5: done`; determinism-gate CI green on Windows + Linux). The sweep below reported "zero code on
disk" because it grepped GUESSED API names — 7.5's real surface is `DslEventQueue` /
`EventDispatchPlan` / `EventBounds` (`godot/src/Dsl/`), the `RaiseEventNode` / `custom_event`
subscription / `expr_event_param` node kinds, and the registry on `ScenarioData.CustomEvents`
(SimChecksum v18, CanonicalModelHash v10). This artifact's "Resolution" step is therefore complete:
re-dispatch 7.9. The plumbing-target inventory in the Investigation summary below remains valid and
is worth reusing. Everything after this section is the ORIGINAL blocked report, kept verbatim.

Status: blocked
Blocking condition: **Unmet hard dependency — Story 7.5 (custom events) is not implemented.** Story 7.9's write rail is defined (epics.md / epic-7-context.md lines 50, 67) as "a Button raises a **registered custom event** with `LockstepManager.EnqueueDslEvent(eventId, args)`; `ApplyDslEvents` enforces the **per-event allowed-raiser set** sim-side." Every load-bearing noun there — the custom-event *registry* (names + typed params + allowed-raiser set), the `RaiseEvent` node, the acyclic same-tick dispatch/work-list drain, and `ApplyDslEvents` — is delivered by **Story 7.5 (custom events — define/raise/subscribe with acyclic same-tick dispatch)**, which is `backlog` in `sprint-status.yaml:147` and has **zero code on disk** (whole-repo sweep for `EnqueueDslEvent|DslEventCommand|ApplyDslEvents|RaiseEvent|EventRegistry|EventDefinition|allowed.?raiser|MaxDslEventsPerTick|RaiseEventNextTick|MaxCascade` returns empty). The trigger IR today has only the built-in *trigger-firing* closed event set (`NodeBase.cs:334` `EventTypes = { match_start, unit_dies, building_completed, timer_expires, resource_threshold, unit_count_threshold }`) — these are trigger *sources*, not raisable, subscribable, raiser-authorized custom events.

**Why this cannot be planned unattended:** With no custom-event registry, a Button has no `eventId` to target, `ApplyDslEvents` has no allowed-raiser set to enforce, and a correctly-transported event would subscribe to nothing and mutate no sim state — so the write rail's entire purpose is unrealizable. Satisfying it would mean folding all of Story 7.5 into 7.9 (registry + `RaiseEvent` + typed params + DAG-acyclicity proof + same-tick work-list drain + `RaiseEventNextTick` with its own `SimChecksum` fold and golden re-baseline + `MaxCascadeOps`/`MaxEventFanOut`/`MaxEventCascadeDepth` caps), i.e. a second large, determinism-critical story with its own re-baseline, entangled with 7.9's own replay-v2/command-fold re-baseline in one unattended pass. That is an unsanctioned scope explosion and violates the READY-FOR-DEVELOPMENT "Sufficient" bar (no unresolved dependency gaps). This is a missing-prerequisite blocker, not an intent ambiguity — the 7.9 intent itself is unambiguous.

**Resolution:** Dispatch **Story 7.5** first (implement the custom-event system), then re-dispatch `7-9-custom-runtime-ui-write-rail-dsleventcommand-on-lockstep-bus-replay-v2-dsl-event-record-local-only-action-whitelist`. Epic-7 execution ran 7.4 → 7.6 → 7.7 → 7.8 and skipped 7.5; the linear IR/DSL spine (epic-7-context.md:66) has 7.5 between 7.4 and 7.6, and 7.6 (`ForEachBatched` "rides the next-tick event queue") also assumes 7.5's event queue exists — so 7.5 is an out-of-order gap that should be closed before 7.9 (and its absence is worth verifying against 7.6 as well).

## Investigation summary (facts established, for the re-dispatch)

Routing resolved this as the epic-story freeform path (specs live directly in `implementation-artifacts/` as `spec-{slug}.md`; no `stories.yaml`/`SPEC.md` folder). Working tree clean, on `master` (consistent with all epic-7 work). Cached `epic-7-context.md` valid; continuity source = `spec-7-8-*` (`status: done`).

The 7.9 **plumbing** targets that DO exist (relevant once unblocked):
- `godot/src/Multiplayer/LockstepManager.cs` — lockstep bus (would gain `EnqueueDslEvent`).
- `godot/src/Multiplayer/NetworkCommand.cs` — command model / `TickCommandPacket` (would gain a DSL-event record; `MaxDslEventsPerTick` cap).
- `godot/src/Multiplayer/ReplayRecorder.cs`, `ReplayPlayer.cs` (`ApplyOrders`), `DedicatedServer.cs` / `Server/ServerHost.cs` — the four command-application sites the replay-v2 DSL-event record must thread; v1 replays hard-rejected.
- `godot/src/Core/ScenarioDirector.cs` — end-of-tick drain point; pinned tick-phase order (apply DSL events → sim systems tick → director drains bus).
- `godot/src/Core/Definitions/CustomUiTree.cs` (7.8) — widget tree; needs a `Button` widget kind (7.8 shipped 8 non-interactive kinds).
- Local-only whitelist (`ToggleWidgetVisible`/`OpenSubPanel`/`CloseSelf`/`SetLocalUiVar`) — presentation-side, must be proven disjoint from sim/DSL namespaces and outside `SimChecksum`.
- Re-baseline landing: 7.9 is where the golden harness is re-recorded for the command/replay fold (per epic-7-context.md:67).

No spec file was written for 7.9 (planning halted before authoring). No code was changed.
