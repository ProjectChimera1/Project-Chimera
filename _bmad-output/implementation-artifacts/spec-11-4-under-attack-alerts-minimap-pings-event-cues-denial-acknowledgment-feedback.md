---
title: 'Story 11.4 — Under-attack alerts / minimap pings / event cues + denial/acknowledgment feedback'
type: 'feature'
created: '2026-07-29'
status: 'done'
baseline_revision: '1b646b731fdbbfeacc4e1ce9215231815d6dccb8'
review_loop_iteration: 0
followup_review_recommended: false
context:
  - '{project-root}/_bmad-output/implementation-artifacts/epic-11-context.md'
  - '{project-root}/godot/CLAUDE.md'
warnings: [multiple-goals, oversized]
---

<intent-contract>

## Intent

**Problem:** The approved HUD promised a match-feedback floor (GDD §6) that the stories dropped: the game never tells you when your base is burning off-screen, has no minimap event pings or camera-view box, silently refuses unaffordable orders (Train and ability-cast emit *nothing* on rejection; other guards emit a reason-less `OrderDenied`), and never acknowledges an issued order. FR-74 requires under-attack alerts, minimap pings + camera box, production/research completion cues, guard-sourced denial feedback, and issue-time order acknowledgment — plus the DW-313 toast-stack cap/evict/coalesce policy.

**Approach:** A pure presentation layer over the untouched 30 Hz sim. Extend the **non-folded** `CombatEventQueue` (which the file itself documents is *not* a `SimChecksum` input) with a relevant-`Faction` field, a `DenialReason` code, and a `TrainingComplete` type; have the existing rejection guards stamp the reason they already compute (single truth source — the UI never re-derives the *reactive* denial reason). A new read-only drainer Node (`MatchAlertBridge`, modeled on `AudioManager`) reads the queue each frame *before* `CombatFeedbackBridge`'s single `Clear()`, filters to `EffectiveLocalFaction`, and raises under-attack toasts (throttled per region/time window) + minimap flashes + completion/denial cues through the SFX-bus audio pool. Minimap gains a camera-view box and an Alt-click ping (replicated in MP via the existing tick-stamped presentation rail — never a new fold). Order acknowledgment (per-unit sound + a ground marker) fires at **issue time** in `SelectionSystem`, masking lockstep delay while sim effects still land at exec-tick. DW-313 is fixed in `ChimeraToastHost.Show`.

## Boundaries & Constraints

**Always:**
- **Zero sim writes; SimChecksum byte-identical feature on vs off (the 2.7 posture).** Every feature here is presentation-only. `CombatEventQueue` is provably *not* a `SimChecksum` input (its own header comments state appending enum values / carrying presentation refs cannot move a golden) — so adding a `Faction`/`DenialReason` field to `CombatEvent`, appending `TrainingComplete`, and adding guard `Push` calls are golden-safe by construction. No hash `AlgoVersion` may move; no golden/checksum file may be re-baselined.
- **The rejecting guard emits the denial reason; the reactive denial-feedback path never re-derives it.** Each guard branch that rejects a Train / Build / Research / Ability-cast / Shop action stamps the specific `DenialReason` it just computed plus the acting `Faction` onto the `OrderDenied` event (adding the currently-missing emissions in `TrainUnitCommand` and `AbilityCastSystem.TryCast`). Guards run in the shared apply path, so this keeps replay/live parity. (The **proactive** command-card affordability tooltips in `CommandCardSystem` are a separate, allowed affordance and stay as-is — the single-truth rule governs the reactive on-click denial reason only.)
- **The new drainer never owns `Clear()`.** `MatchAlertBridge` reads `_ctx.CombatEvents` read-only and must run *before* `CombatFeedbackBridge._Process` (the sole `Clear()` owner), exactly as `AudioManager` does. It touches no sim store.
- **Under-attack fires only for the local player's units/buildings damaged *outside* the current viewport, throttled per region/time window.** Filter events by `EffectiveLocalFaction`; gate on `RtsCameraController.IsInView(pos)` being false; suppress repeat alerts for the same coarse region within a named time window (`AlertRegionCellSize`, `AlertRegionWindowSec`) so a sustained raid is one alert stream, not spam.
- **Order acknowledgment plays at ISSUE time, sim effects at exec-tick.** Ack sound + order-confirmed ground marker fire locally in `SelectionSystem.Issue*Command` right after `EnqueueOrder`, on the input frame — never from `OrderApplier`/`EntityWorld`.
- **Every cue routes through a named audio bus.** New sounds play via `AudioManager`'s `"SFX"`-bus `AudioStreamPlayer` pool so `SettingsManager.SetBusVolume` governs them for free. Per-unit acknowledgment sound is a nullable `string? AckSoundId` on `CombatFeedbackProfile` (dual-loader safe: declared primitive, default null, presentation-only, excluded from `SimChecksum`); asset production is out of scope — hook + a default clip only.
- **MP pings replicate on the existing tick-stamped presentation rail.** A ping shows immediately for the initiator; in MP it replicates to allies as a tick-stamped presentation event on the existing rail (the `SendPlayerChat`/`EnqueueDslEvent` or reliable chat-channel precedent) so every peer/replay surfaces it at the same tick — **without folding new state into `SimChecksum` or bumping any replay/algo version**. Research completion is already on the bus (`ResearchComplete`); training completion is the newly-added `TrainingComplete`.
- **All new UI composes from the 3.1x kit** (`ChimeraComponents`/`ChimeraToastHost`/`ChimeraDialog`, `EnsureKitInitialized` first); presentation Nodes are constructed in a bootstrap phase and published on `SceneContext`, mirroring `MinimapPhase`/`AudioPhase`.

**Block If:**
- Deterministic MP ping replication cannot be achieved by reusing the existing tick-stamped presentation rail — i.e. it would require folding new state into `SimChecksum`/`MatchAgreementHash`, bumping a replay/`AlgoVersion`, or re-baselining a golden. HALT `blocked`, condition `ping replication requires a determinism fold`.
- A rejecting guard cannot surface its denial reason on the shared apply path without the reactive UI re-deriving it (the reject site cannot reach the event queue and cannot be threaded without changing a folded sim signature). HALT `blocked`, condition `denial reason cannot be guard-sourced`.
- Adding the relevant-`Faction`/`DenialReason` field, appending `TrainingComplete`, or the guard emissions would move any golden or require a `SimChecksum` fold / `AlgoVersion` bump. HALT `blocked`, condition `feedback plumbing requires a determinism fold`.

**Never:**
- Any sim write, tick-order change, `FixedDt`/tick-rate change, `SimChecksum` coverage change, or hash `AlgoVersion` bump from this layer; re-baselining any golden or checksum file.
- Re-deriving the denial reason in the reactive denial-feedback path (the guard is the one truth source); calling `CombatEventQueue.Clear()` from the new drainer.
- Shipping real voice/sound assets (voice-set production is out of scope — provide the data hook + one default clip); MP save of pings; an under-attack alert for damage the player can already see on-screen or for a non-local faction.
- Adding or reordering a setup phase pinned by `PhaseOrderTest`, or a phase that runs before sim setup completes.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Under-attack (off-screen) | Local unit/building takes a hit at a position outside the camera view | One under-attack alert: Danger toast + minimap flash at the location + SFX cue | Queue full (256) drops some hits → throttle still fires on the next surviving hit |
| Sustained raid (same region) | Many hits on the local base in one region over several seconds | ONE alert stream — repeats within `AlertRegionWindowSec` for the same region cell are suppressed | — |
| Damage on-screen | Local unit hit inside the current viewport | No under-attack alert (player already sees it); normal hit feedback only | — |
| Enemy takes damage | A non-local faction's units are hit off-screen | No local under-attack alert | — |
| Minimap ping | Alt-click on the minimap | Ping shown immediately for the initiator; in MP replicated to allies tick-stamped | Offline → local only, no replication |
| Camera-view box | Camera pans/zooms | Minimap always draws the current view rectangle | — |
| Completion cue | Local player's training or research completes | Completion SFX cue (consumes `TrainingComplete`/`ResearchComplete`) | Non-local completion → no local cue |
| Denial (unaffordable/invalid) | Local Train/Build/Research/Cast/Shop rejected | Reason-specific text ("Not enough Ore/Crystal", "Supply capped", "Invalid location", "On cooldown", "Not enough energy", …) + denial SFX, sourced from the guard | Enemy AI denial → no local feedback |
| Order acknowledgment | Player issues a move/attack/patrol/etc. order to a selection | Ack sound (per-unit `AckSoundId` or default) + order-confirmed ground marker at the target, at issue time | No selection → no ack |
| Toast burst (DW-313) | More than `MaxVisibleToasts` toasts, or repeated same-identity toasts | Stack capped (oldest evicted via the existing dismiss path); same title+variant coalesces (count/refresh) — never marches off-screen | — |
| Determinism | Same match run with the feedback layer on vs a reference | `SimChecksum` stream byte-identical; `AlgoVersion` pins unchanged; no golden re-baselined | Divergence = test failure |

</intent-contract>

## Code Map

- `godot/src/Combat/CombatEventQueue.cs` -- the presentation bus. `enum CombatEventType` (:8, `OrderDenied`/`ResearchComplete` present; append `TrainingComplete`); `struct CombatEvent { Type; Position; Feedback }` (:47 — add `Faction Faction` + `DenialReason Reason`); `Push` overloads (:83/:91 — add faction+reason variants); documented **not folded into SimChecksum** (:68) → the golden-safe seam for every addition here.
- `godot/src/Combat/CombatSystem.cs` (:638/:691) / `Combat/ProjectileSystem.cs` (:127/:150) / `Combat/DamageResolver.cs` (:116/:125/:152) -- hit/kill/building-destroyed push sites; each already reads victim faction (`world.FactionOf[target]` / `_buildings.FactionOf[b]`) — stamp it on the event. Pure reads, no behavior change.
- `godot/src/Economy/BuildingSystem.cs` -- Train guards (:386 prereq / :391 supply / :400 afford) **push nothing today** → add `Deny(reason, faction)`; construction affordability (:504-514) + shop guards (:562-586) already `Deny` → add reason+faction; `Deny` helper (:603) → take reason+faction; `SpawnTrainedUnit` (:176) → push `TrainingComplete` (faction+pos).
- `godot/src/Economy/ResearchSystem.cs` (:187 afford / :429 `Deny`) -- add reason+faction; `ResearchComplete` already pushed at :320.
- `godot/src/Effects/AbilityCastSystem.cs` `TryCast` (:186 cooldown / :193 energy / :194-195 afford / :199 self-lethal / :211 invalid target) -- **silent today** → add `OrderDenied` push with the branch's `DenialReason` + caster faction.
- `godot/src/Core/Definitions/CombatFeedbackProfile.cs` (:29, fields :33-53) -- add nullable `string? AckSoundId` (`[JsonPropertyName("ack_sound")]`, default null). Dual-loader constraint: `CombatFeedbackProfile` rides the lenient faction loader AND the strict `Disallow` ability loader — declared primitive only, no enum. Excluded from `SimChecksum`.
- `godot/src/UI/CombatFeedbackBridge.cs` (:84 drain, :132 the sole `Clear()`) -- the drain-order anchor: `MatchAlertBridge` must run BEFORE this. `RenderingPhase.cs:43-45` wires it.
- `godot/src/UI/AudioManager.cs` (:75 `"SFX"` bus, :104 drain w/o clear, :148 `PlayTrainingComplete()` UNCALLED, :161 `PlayOneShot`) -- add under-attack/denial/ack/ping cues + wire `PlayTrainingComplete`; the settings-bus routing seam (`SettingsManager.cs:118-131`).
- `godot/src/UI/MinimapBridge.cs` -- `_GuiInput` (:186 LMB `PanTo`; add Alt-click ping branch); `DrawDots` (:241)/`_Draw` (:235) + `_Process` (:178 `QueueRedraw`) for the camera-view box + decaying ping/flash draw pass; `WorldToMinimap`/`MinimapToWorld` (:270/:278), `HALF_MAP=128` (:30); `SetLocalFaction` (:61). Wired in `MinimapPhase.cs`.
- `godot/src/UI/RtsCameraController.cs` (:16, `GetCamera` :220, `_zoomDist`/`_pitchDeg` private :37-38) -- add read-only `bool IsInView(Vector3)` / `Rect2 GetViewBounds()` (the outside-viewport gate + minimap box source).
- `godot/src/UI/SelectionSystem.cs` -- issue-time seam: `Issue*Command` (`IssueMoveCommand` :613, `IssueAttack*` :790/:817, `IssuePatrol` :848, `IssueFollow` :868, `IssueHold` :725, `SetRallyPoint` :955, …) all after the `EnqueueCommand`/`EnqueueOrder` choke (:289-318) → fire ack sound + spawn order-confirmed marker at the resolved world target.
- `godot/src/UI/BuildingBridge.cs` (rally-pole marker pattern: `_rallyMarkers` :48, `BuildRallyMaterial` :158, `UpdateRallyMarkers` :327) -- the pooled world-space ground-marker precedent to reuse for the order-confirmed marker.
- `godot/src/Multiplayer/LockstepManager.cs` -- `EffectiveLocalFaction` (:169) local-player filter; `SendPlayerChat`/`EnqueueDslEvent` tick-stamped rail (:528-538) + reliable `SendChat`/`OnChatReceived` (:522/:591) — the two candidate ping-replication rails (pick the one that adds no fold).
- `godot/src/UI/Components/ChimeraToastHost.cs` -- DW-313: `Show` (:45, add cap/coalesce between :47 and :53), `NextY` (:79-85, unbounded sum), `Dismiss(_toasts[0])` (:87) = the evict path, `_toasts`/`_reflowTweens` (:30/:33) = state (add a life-tween/expiry track for coalesce restart).
- `godot/src/Core/MainScene.cs` (`_Process` presentation tail :1407-1429, overlay `.Update()` drains :1417-1421; `EffectiveLocalFaction` :997/:1395; `WireSessionShell` :800) / `godot/src/Core/Bootstrap/SceneContext.cs` -- hold + drain the new bridge; wire `SelectionSystem` ack/marker deps.
- `godot/src/Core/Bootstrap/Phases/MinimapPhase.cs` / `AudioPhase.cs` / `RenderingPhase.cs` -- the presentation-phase construction precedents for a new `MatchAlertPhase`; `PhaseOrderTest` pins the ORDER — a new presentation phase must sit after sim setup and not perturb pinned indices.
- `godot/ProjectChimera.Sim.Tests/**` + `SimSources.props` -- Tier-1 test home; `SimSources.props` (or a direct test `<Compile Include>`) pulls a Godot-free `UnderAttackThrottle` into Tier-1 (the 11.3 persistence-core precedent). `Golden/GoldenChecksumReplay.cs`/`Sim/SimResetTests.cs` for the golden-unchanged + `AlgoVersion`-pin assertions.

## Tasks & Acceptance

**Execution — event plumbing (Godot-free sim-adjacent; additive, non-folded, golden-safe):**
- `godot/src/Combat/CombatEventQueue.cs` -- extend `CombatEvent` with `Faction Faction` (victim for hit/kill/razed; actor for denial/completion) and `DenialReason Reason` (default `None`); add `enum DenialReason { None, NeedOre, NeedCrystal, SupplyCapped, PrereqMissing, OnCooldown, NoEnergy, InvalidLocation, InvalidTarget, OutOfRange, InventoryFull, QueueFull }`; append `CombatEventType.TrainingComplete`; add `Push` overloads carrying faction+reason (keep existing overloads defaulting `Faction`/`Reason`); mirror the existing "not folded → golden-safe" comment. -- the bus contract.
- `godot/src/Combat/CombatSystem.cs`, `ProjectileSystem.cs`, `DamageResolver.cs` -- stamp the victim faction on the existing hit/kill/`BuildingDestroyed` pushes (pure reads of `FactionOf`). -- lets presentation know *whose* units were hit.
- `godot/src/Economy/BuildingSystem.cs` + `ResearchSystem.cs` + `Effects/AbilityCastSystem.cs` -- at each rejecting guard branch, emit `OrderDenied` with the specific `DenialReason` + acting faction (adding the currently-missing Train and ability-cast emissions; updating the `Deny` helper + existing shop/construction/research emissions to carry reason+faction). -- guard-sourced denial reasons.
- `godot/src/Economy/BuildingSystem.cs` `SpawnTrainedUnit` -- push `TrainingComplete` (training building faction + position). -- production completion cue source.
- `godot/src/Core/Definitions/CombatFeedbackProfile.cs` -- add `[JsonPropertyName("ack_sound")] string? AckSoundId` (default null). -- per-unit acknowledgment sound hook (dual-loader safe).

**Execution — presentation (Godot-coupled, in-engine gated):**
- `godot/src/UI/UnderAttackThrottle.cs` -- NEW, Godot-free: quantize a `FixedVec3`/world pos to a coarse region cell (`AlertRegionCellSize`) and suppress repeat alerts for that cell within `AlertRegionWindowSec`; `bool ShouldAlert(pos, double nowSec)`. Pulled into Tier-1 via `SimSources.props` (or a direct test `<Compile Include>`). -- testable throttle policy.
- `godot/src/UI/MatchAlertBridge.cs` -- NEW presentation Node: read-only drainer of `_ctx.CombatEvents` running BEFORE `CombatFeedbackBridge` (never `Clear()`); filter by `EffectiveLocalFaction`; raise under-attack (toast Danger + minimap flash + SFX, gated on `IsInView`==false + `UnderAttackThrottle`), denial (reason→text + SFX), and completion cues (`TrainingComplete`/`ResearchComplete`). -- the feedback coordinator.
- `godot/src/UI/RtsCameraController.cs` -- add read-only `IsInView(Vector3)` / `GetViewBounds()` from the ground pivot + zoom-derived extent; no camera-behavior change. -- viewport gate + minimap box source.
- `godot/src/UI/MinimapBridge.cs` -- draw the camera-view box (from `GetViewBounds()`, mapped via `WorldToMinimap`); add an Alt-click ping branch in `_GuiInput`; add a decaying ping/alert-flash draw pass (timer list, updated in `_Process`). -- minimap pings + camera box + alert flash.
- `godot/src/Multiplayer/LockstepManager.cs` -- replicate a ping to allies in MP as a tick-stamped presentation event on the existing rail (add no `SimChecksum` fold / version bump); local ping shows immediately. -- MP ping parity.
- `godot/src/UI/AudioManager.cs` -- add `"SFX"`-bus one-shot cues for under-attack / denial / ack / ping and WIRE `PlayTrainingComplete`; ack resolves the selected unit's `AckSoundId` or a default; all through the existing settings-governed pool. -- audio cues.
- `godot/src/UI/SelectionSystem.cs` (+ `godot/src/UI/OrderMarkerBridge.cs` NEW or reuse `BuildingBridge` rally-pole) -- at each `Issue*Command`, after `EnqueueOrder`, fire the ack sound + spawn a short-lived faction-tinted order-confirmed ground marker at the resolved world target. -- issue-time acknowledgment.
- `godot/src/UI/Components/ChimeraToastHost.cs` -- DW-313: in `Show` (between :47 and :53) coalesce same title+variant (update text / restart the toast's lifetime instead of adding — track each toast's life tween/expiry via a new field beside `_reflowTweens`) and cap at `MaxVisibleToasts` (evict oldest via `Dismiss(_toasts[0])`). -- bounded toast stack.
- `godot/src/Core/Bootstrap/Phases/MatchAlertPhase.cs` (NEW) + `SceneContext.cs` + `godot/src/Core/MainScene.cs` -- construct `MatchAlertBridge` (+ `OrderMarkerBridge`, + the shared `ChimeraToastHost` if not already present), wire deps from `_ctx` (`CombatEvents`, `Minimap`, `Cam`, `AudioMgr`, `EffectiveLocalFaction`, `SelectionSystem`), publish on `_ctx`, and drain it in the `_Process` presentation tail before `CombatFeedbackBridge`'s clear. New presentation phase sits after sim setup; do not perturb `PhaseOrderTest`-pinned indices. -- lifecycle + wiring.

**Execution — tests:**
- `godot/ProjectChimera.Sim.Tests/**` -- NEW xUnit: (a) `UnderAttackThrottle` — same region within window suppressed; different region or after window → alert; (b) golden-unchanged — a scenario exercising denial/completion/hit pushes yields a `SimChecksum` stream byte-identical to the golden, and the `AlgoVersion` pins (`SimChecksum`/`CanonicalModelHash`/`StartStateHash`) are unchanged, no golden/checksum file touched (reuse `SimResetTests`/`GoldenChecksumReplay`); (c) denial-reason→text mapping is total (every `DenialReason` maps). -- the determinism + policy proof.

**Acceptance Criteria:**
- Given a local unit/building takes damage outside the current viewport, when the hit fires, then exactly one under-attack alert (Danger toast + minimap flash at the location + an SFX cue on the SFX bus) is raised, and repeated hits in the same region within `AlertRegionWindowSec` are suppressed to one alert stream; damage on-screen or to a non-local faction raises no alert.
- Given the minimap, when the player Alt-clicks it, then a ping appears immediately for the initiator and (in MP) replicates to allies as a tick-stamped presentation event with no new `SimChecksum` fold; and the minimap always draws the current camera-view box. Given a local production/research order completes, then a completion cue plays.
- Given a local Train/Build/Research/Ability-cast/Shop order is rejected, when it is attempted, then reason-specific denial text + a denial SFX fire, sourced from the guard that rejected it (not re-derived); an enemy faction's rejected order produces no local feedback.
- Given the player issues a move/attack/patrol/etc. order, when the order is issued, then an acknowledgment sound + an order-confirmed ground marker fire at issue time at the target, while the sim effect still occurs at exec-tick.
- Given a burst of toasts (or repeated same-identity toasts), when they are shown, then the stack is capped at `MaxVisibleToasts` (oldest evicted) and same-identity toasts coalesce — the stack never marches off-screen.
- Given the full Tier-1 suite, when it runs, then the throttle + reason-map + golden-unchanged tests pass, every hash `AlgoVersion` pin is unchanged, and no `SimChecksum` golden is re-baselined.

## Spec Change Log

_(no bad_spec loopback — empty.)_

## Review Triage Log

### 2026-07-29 — Review pass
- intent_gap: 0
- bad_spec: 0
- patch: 9: (high 0, medium 5, low 4)
- defer: 4
- reject: 3
- addressed_findings:
  - `[medium]` `[patch]` **Map pings ignore alliances** — the server `BroadcastReliable`d a ping to every peer and `HandleMpPing` rendered any non-self ping ally-green; in 1v1 the ping leaked to the enemy shown as friendly. Fixed: filter relay + display through `AllianceStore.AreAllied(sender, local)` (same-team only) on the server relay, the P2P receive, and the display path.
  - `[medium]` `[patch]` **Toast coalesce showed the wrong denial reason** — `ChimeraToastHost.TryCoalesce` keyed on (title, variant) only, so distinct denial reasons (all title "Can't do that"/Warn) collapsed into one toast displaying the FIRST reason. Fixed: include the message/`DenialReason` in the coalesce identity (or refresh the displayed message on coalesce).
  - `[medium]` `[patch]` **"Invalid location" denial defined but never emitted** — `DenialReason.InvalidLocation` + text exist but no guard pushed it, so the epic's named denial could never appear. Fixed: the invalid-build-spot rejection now produces `InvalidLocation` feedback (guard-sourced, or the UI placement-denial path if placement is pre-validated there).
  - `[medium]` `[patch]` **Cross-match state leak** — `MatchAlertBridge.ResetForMatch()` had zero callers and `MinimapBridge` ping/alert markers were never cleared; throttle-suppression + markers persisted into a rematch, silently suppressing the first alert of a region. Fixed: reset the throttle + clear minimap markers on match (re)start / `ResetToAuthoredStart` / `ClearForReset`.
  - `[medium]` `[patch]` **Denial contract undertested** — only Train/supply-cap asserted its reason+faction; Research/Shop/Ability-cast (newly non-silent)/ring-full stamps and victim-faction correctness at the combat push sites were unverified (an attacker/victim swap would pass every existing test yet alert the wrong player), and the `MapPing` wire round-trip had no test. Fixed: added Tier-1 tests for a representative reject reason+faction per guard system, victim-faction on drained hit/kill events, `MakeMapPing`↔`TryReadMapPing` round-trip + re-stamp, and ore-only→NeedOre / crystal-only→NeedCrystal.
  - `[low]` `[patch]` **`AffordReason` fabricated "Not enough Ore"** for any shortage beyond the ore/crystal probe (custom/sparse cost keys). Fixed: resolve the first actually-unaffordable resource from the cost map (generic fallback), hoisting the duplicated logic to one shared home.
  - `[low]` `[patch]` **Item-pickup denials silent** — `ItemSystem.cs:171/191` still used the faction-less `Push`, so a local hero's inventory-full/modifier-capped reject was filtered out. Fixed: `PushDenied` with the hero faction + reason.
  - `[low]` `[patch]` **Denial SFX spam** — the cue fired on every `OrderDenied` with no throttle while the toast coalesced. Fixed: suppress the denial cue when the toast coalesced (short per-reason cooldown).
  - `[low]` `[patch]` **`QueueWorkerBuild` dropped `Spend()`'s return** after the `CanAfford`-then-`Spend` refactor (latent free-build if a future `Spend` fails post-`CanAfford`). Fixed: restore the return-value guard.
- **defer (4):** minimap panel renders off-screen at 1920×1080 (PRE-EXISTING `MinimapPhase` layout, untouched by 11.4 — the new ping/box/flash inherit it and are not visible; surface to Alec); `CombatEventQueue` 256-ring can silently drop denial/completion cues under heavy combat burst (pre-existing lossy-queue design, no non-hit priority); `SendMapPing` P2P path trusts the wire faction byte (spoofable; server path re-stamps — MP anti-spoof hardening); spectator/observer (`EffectiveLocalFaction`→Player1) receives Player1's feedback as if owning it. Appended to `deferred-work.md`.
- **reject (3):** `IsInView` generous zoom-box erring toward on-screen (deliberate false-alert-avoidance; the in-engine gate confirmed the gate fires correctly); issue-time optimistic ack for a possibly-rejected order (that IS the GDD §6 issue-time-feedback design); `OnAttackBuilding` stale marker position on an exact select→recycle race (cosmetic, negligible).

### 2026-07-29 — Post-merge ultra-review (covers 11-3 AND 11-4)

Run after both stories were committed (`origin/master`=`ca9da36` .. `HEAD`=`e6a3273`). 9 findings; 3 patched, 6 deferred.

- patch: 3: (high 1, medium 2, low 0)
- defer: 6
- reject: 0
- addressed_findings:
  - `[high]` `[patch]` **Save/load re-fires building-completion triggers — 11-3's byte-identical-resume claim was false.** `ScenarioDirector._prevFlags`/`_prevBuildingDone` are mid-match-mutable but not serialized; `LoadScenario` seeds them from the AUTHORED board and the save overlay lands afterward, so the first resumed tick emitted a spurious `building_completed` for every player-built building, re-firing non-`run_once` triggers into FOLDED state. Fixed by `ScenarioDirector.ReseedChangeDetection(world)`, called at the end of `RestoreDirector` — no save-format change, because `UpdateSnapshots` is the last step of the director tick, so re-deriving the snapshots from the restored world reproduces the saved values exactly. Regression test `SaveLoad_ResumeByteIdentical_WithMidMatchBuiltBuilding` **verified load-bearing**: with the fix reverted it fails with checksum drift at tick 93, the first resumed tick.
  - `[medium]` `[patch]` **The under-attack viewport gate was geometrically wrong and degenerate at max zoom.** `ViewHalfExtent() => _zoomDist * 0.85f` modelled the view as a symmetric square on the ground pivot; the real rig (pitch 50°, 75° FOV) sees ~49 units behind the pivot and ~225 in front, so the gate alerted for battles already on screen, and at `ZoomMax=150` produced a 255×255 box over a 256×256 map where the alert could never fire at all. Fixed: `GetViewBounds` projects the four viewport corners onto the pivot's ground plane and returns the true footprint's AABB (tracks pitch/FOV/aspect/yaw); corner rays that clear the horizon clamp to `MAX_GROUND_REACH`. Note this **supersedes 11.4's own `reject`** of the "generous zoom-box" finding — the bias was not one-directional as that reject assumed.
  - `[medium]` `[patch]` **DW-313's toast coalescing silently ate existing content.** `TryCoalesce` keyed on `(title, variant)` only and OVERWROTE the message, so any caller reusing a title across distinct messages lost content — `ObjectiveLogOverlay` shows one "New Objective" toast per activated objective, so two activating on the same frame rendered as one toast naming only the second; `HeroPickerOverlay`'s four "Saved" messages collapsed the same way. Fixed: the message joins the coalesce identity. Anti-spam is preserved for genuine repeats, and distinct denial reasons now each get a toast (better than replace-newest — two rejection reasons are two things worth reading).
- **defer (6):** `PendingLoadedSave` static survives an early-return launch-gate veto (stale save overlays the next skirmish); `Validate()` bounds free-list lengths but not elements (corrupt id escapes the fail-closed gate); `RestoreResources`/`RestoreResearch`/`RestoreWinState` bound every array by `Ore.Length` alone; `CaptureBuildings` aliases the live `ShopStock` `string[]` by reference; issue-time ack fires alongside the synchronous `QueueFull` denial for one click; Alt+LMB minimap ping also pans the camera on any pixel of drag. All appended to `deferred-work.md`.
- **verification:** Tier-1 suite 3640 passed / 0 failed / 1 skipped (+1 new test); `dotnet build godot.csproj` 0 errors; `AlgoVersion` pins 21/14/2/3/1/1 unchanged; no golden or checksum file re-baselined. The camera and toast fixes are Godot `Node`/`Control` code, outside the Tier-1 link set — **not covered by an automated test and not yet re-observed in-engine.**

### In-Engine Gate — 2026-07-29 (post-review-fix re-verification): **PASS**

Godot 4.6.3, `res://scenes/main.tscn`, 1920x1080, live skirmish (Alpha Skirmish, P1 Human vs P2 AI). Driven through
the real UI (main menu PLAY -> setup Launch -> briefing CONTINUE) via `pressed`-signal emission; camera zoom driven by
real wheel events through `Viewport.push_input`. Editor error log clean (0 errors) across the whole session.

| # | Criterion | Result | Evidence (numbers, not vibes) |
|---|---|---|---|
| 1 | Minimap panel renders on-screen | **PASS** | Rect `P(1712, 872) S(200, 200)` in a 1920x1080 viewport; `Rect2(0,0,1920,1080).encloses(minimap_rect) == true`. Offsets now `-208,-208,-8,-8` (were `0,0,-8,-8`, giving a NEGATIVE-size rect pinned to the raw corner). Screenshot confirms the panel with live unit dots bottom-right. |
| 2 | 11.4 minimap features visible | **PASS** | All three render at their computed pixels: ping (green) expected `x1890 y894` -> observed top-right; alert flash (red) expected `x1734 y972` -> observed left-middle; camera-view box expected `x1726..1774 y1012..1034` -> observed as a white rect there. Box tracks the pivot (recomputed after a pan and matched). |
| 3 | Corrected viewport gate | **PASS** | Default zoom (d=80), pivot origin: bounds `x -306.4..306.4`, `z -225.0..48.7` — asymmetric, ~225 forward / ~49 back, matching the rig geometry. `IsInView` at `z=-100/-200` = true (the old symmetric +/-68 box called these OFF-screen -> the false-alert bug); `z=-260`, `z=+80`, `x=400` = false. |
| 3b | Alert still possible at max zoom | **PASS** | Wheel-driven to the `ZoomMax=150` clamp (bounds stable at `752x334` across 40 further notches). Map corners SW `(-128,128)` and SE `(128,128)` plus `(0,100)`/`(0,200)` all report NOT-in-view, so 4 of 6 probes can still raise an alert. The old `+/-127.5` box was 255x255 over a 256x256 map — the alert could never fire zoomed out. |
| 4 | Toast coalescing | **PASS** | From 0 toasts: two DIFFERENT denial messages both returned `true` (new) -> child count **2**, both texts retained. The same message x2 more returned `false` (coalesced) -> count still **2**, rendered `"Not enough Ore.  (×3)"`. Previously the second distinct reason overwrote the first into a single toast. |

### Follow-up fixes — 2026-07-29 (both re-verified in-engine): **PASS**

Both items the gate raised were fixed rather than deferred.

**1. Alt+LMB pinged AND panned the camera.** Root cause was wider than the review described: the Alt branch consumed
only the PRESS, so the matching RELEASE leaked into the pan branch (which never tested `mb.Pressed`) and any drag
leaked into the motion branch. A `_pingGesture` latch now swallows the remainder of the gesture, self-healing on a
plain press so a release delivered outside the Control cannot strand it. Verified: Alt+click and Alt+drag both leave
the pivot at `(0,0,0)`; a plain click still pans to `(-79.36, 0, 74.24)` — click-to-pan is not regressed.

**2. The view footprint was reported as an AABB, not the real shape.** A tilted perspective camera sees a TRAPEZOID —
far edge much wider than the near one — and its bounding box overstates it badly. That is what produced the
"613 x 274 against a 256 x 256 map" reading: the AABB width is the FAR edge's width, while the near half of the
screen shows far less ground. So this was mostly a measurement artifact, not a camera-tuning problem, and the fix is
to stop using the AABB where the shape matters:

- `TryGetViewQuad` returns the four ground-projected corners; `GetViewBounds` still returns their AABB for cheap-bound
  callers.
- `IsInView` now runs a crossing-number point-in-polygon over the quad (crossing-number, not a half-plane test, because
  horizon clamping can make the ring non-convex). Verified in-engine at default zoom: the AABB's NEAR corners
  `(±300, +40)` now report **outside** the view — they read "on screen" under the old bounds test and silently
  swallowed the player's alert — while the genuinely wide far edge `(300, -220)` stays inside, and `(200, -100)` is
  correctly outside because the trapezoid narrows toward the camera.
- The minimap draws the quad as a polyline. Screenshot confirms a visible trapezoid (wide top / narrow bottom) where a
  rectangle used to be.

**Still open for Alec (design, not a defect):** even measured honestly the camera is wide — at `ZoomMax` the footprint
reaches well past the `256 x 256` map, so the top zoom stops are not useful play positions. The usable range is roughly
`ZoomMin`..~33 world units of distance. Worth trimming `ZoomMax` (currently 150) to match the map, but that is a
feel change and is left to you.

**Not covered:** the under-attack toast/SFX path was verified at the gate level (`IsInView`) and the toast level, but
not end-to-end from a real off-screen combat event; MP ping replication remains code-reviewed only (single-client
editor cannot host a peer, same posture as 11.3).

## Design Notes

**Why the non-folded bus is safe to extend.** `CombatEventQueue` is explicitly *not* a `SimChecksum` input — its own comments repeatedly assert that appending enum values and carrying presentation refs cannot move a golden, and `OrderDenied` was itself appended noting "Story 11.9 consumes this" (this merged story). Adding a `Faction`/`DenialReason` field and stamping it at push sites that already read that state is the same golden-safe operation; the determinism proof is "existing goldens still pass + `AlgoVersion` pins untouched", asserted in-memory, not a re-baseline.

**Guard-emits-reason vs proactive tooltips.** The single-truth rule (guard emits the reason, UI renders it) governs the *reactive* on-click denial. The *proactive* command-card affordability tooltips (`CommandCardSystem`, "[need ore]" etc.) are a distinct affordance that legitimately previews state before a click — leave them. Train and ability-cast currently reject *silently*; adding their `OrderDenied` emissions is the missing half of the feedback loop.

**Drain order is load-bearing.** `CombatFeedbackBridge._Process` owns the only `Clear()`. `MatchAlertBridge` and `AudioManager` are read-only siblings that must run before it, or events vanish before they are seen. The new drainer touches no sim store — that is what keeps checksum parity.

**Issue-time ack masks lockstep delay.** GDD §6 promises immediate feedback; the ack sound + ground marker play on the click frame in `SelectionSystem` (local, presentation-only), while the deterministic order still executes ticks later in `OrderApplier`. Never emit the ack from the apply path.

**MP ping determinism boundary.** A ping is presentation; replicating it must not fold new sim state. Prefer the rail that adds zero `SimChecksum`/`AlgoVersion` cost — reuse the existing tick-stamped presentation-event rail (`SendPlayerChat`/`EnqueueDslEvent`, Story 7.9/7.13 precedent) or the reliable chat channel with a tick stamp. If neither can replicate deterministically without a fold/version bump, that is the `ping replication requires a determinism fold` Block-If. The offline/local-initiator arm is the in-engine-verifiable path; the MP-replication arm is code-reviewed (single-client editor cannot host a peer — same posture as 11.3's online asymmetry).

**DW-313.** In `ChimeraToastHost.Show`, before `_toasts.Add`, coalesce a same-(title,variant) toast (update its text / restart its lifetime — requires tracking each toast's life tween or expiry, a new field, since today it is a local var) and, once `_toasts.Count > MaxVisibleToasts`, evict the oldest through the existing `Dismiss(_toasts[0])` slide-out+`Reflow` path. `NextY` then never sums an unbounded stack.

## Verification

**Commands:**
- `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj -c Release` -- expected: throttle + denial-reason-map + golden-unchanged tests pass within a green suite; `AlgoVersion` pins unchanged; no golden re-baselined.
- `dotnet build godot/godot.csproj` -- expected: event plumbing + bridges + MainScene wiring compile with no banned-API/AOT analyzer regressions.

**Manual checks (in-engine, gated — Epic-11 per-story gate via `/godot-verify` / godot-mcp bridge):**
- Launch a SP skirmish → `[PLAY]`. Drive an off-screen attack on a local building (pan away, or use a scenario/`godot_exec` to damage a local unit outside view): confirm ONE under-attack Danger toast + a minimap flash at the location + an SFX cue, and that sustained hits in that region do not re-spam. Alt-click the minimap → a ping appears and the camera-view box tracks the camera. Attempt an unaffordable Train and an unaffordable ability cast → reason-specific denial text + sound (previously silent). Issue a move order → ack sound + ground marker at issue time. Trigger a burst of toasts → stack stays capped/coalesced. A/B the local-faction filter (my damage alerts; the enemy's identical damage does not). Verify against numbers and `git status` (no golden/checksum file changed; `AlgoVersion` pins intact), per the in-engine gate discipline.
