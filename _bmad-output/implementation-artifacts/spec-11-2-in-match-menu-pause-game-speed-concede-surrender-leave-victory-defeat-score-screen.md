---
title: 'Story 11.2 — In-match menu / pause / game-speed + concede/leave + victory-defeat score screen'
type: 'feature'
created: '2026-07-28'
status: 'done'
baseline_revision: 'f47e2bdad293f982f089703c39312d3062283d4b'
final_revision: '7a84845500787f87ed4ad1cb88c79ffefc108101'
review_loop_iteration: 0
followup_review_recommended: true
context:
  - '{project-root}/_bmad-output/implementation-artifacts/epic-11-context.md'
  - '{project-root}/godot/CLAUDE.md'
warnings: [multiple-goals, oversized]
---

<intent-contract>

## Intent

**Problem:** After 11.1 a skirmish boots into `[PLAY]`, but there is no session shell around it: no in-match menu, no true single-player pause, no game-speed control, no way to concede or leave, and the end-of-match payoff is a raw-node placeholder telling the player to "Press F5 to return to Edit". Everything between "the match is running" and "back at the menu" is missing.

**Approach:** Add the offline session shell as presentation-layer controls over the untouched 30 Hz sim: an Esc/F10 in-match menu (Resume / Settings / Save / Load / Concede / Quit to Menu) built from the 3.1x kit; true SP pause and 0.5×–3× game-speed by scaling/skipping the wall-clock delta fed to `_host.Update` on the offline free-run branch (per-tick `FixedDt` untouched → byte-identical tick stream); a **Concede** command riding the existing `OrderApplier` single-switch that latches the already-folded `WinStateStore.Verdict` so `WinConditionSystem` awards the winner deterministically; two new observational score counters (crystal gathered, buildings razed) in the unfolded `MatchStats`; and a real kit-styled victory/defeat score screen replacing the raw-node `ShowGameOver` body, driven by the existing `GameOverSummary` projection.

## Boundaries & Constraints

**Always:**
- Pause and game-speed are **presentation-loop cadence controls only**, applied on the offline free-run branch (`MainScene._Process`, the `_host.Update((float)delta)` call). Scale by multiplying `delta`; pause by not calling `_host.Update` at all (do not feed 0, do not accumulate). The sim's per-tick `FixedDt` and system order are untouched — **zero `SimChecksum` change, zero golden re-baseline**. The HUD keeps painting while paused.
- Concede rides the **existing order stream** through `OrderApplier.Apply`, keyed off `expectedFaction` (a peer can only concede its own faction — the anti-cheat truth), handled BEFORE the entity-ownership guard (it names a faction, not an entity, like `DslEvent`). It latches `WinStateStore.Verdict[(int)expectedFaction] = VERDICT_LOST` only when currently `VERDICT_NONE` (monotone). The `WinStateStore?` handle is threaded through BOTH apply call sites (`LockstepManager` live AND `ReplayPlayer` replay) — the one-switch parity rule; a command handled in one path but not the other is a guaranteed desync.
- The new score counters (`_crystalMined`, `_buildingsRazed`) live in `MatchStats`, which is **deliberately NOT folded into `SimChecksum`** (the observational scoreboard, `SimChecksum.cs:63/415`) — so adding them moves no golden.
- All new UI composes from the 3.1x kit (`ChimeraComponents` factories + `ChimeraDialog` confirms), mirroring `SettingsPanel`/`HeroPickerOverlay` (call `EnsureKitInitialized()` before any factory). Not the raw-node `SkirmishSetupOverlay` idiom.
- MP asymmetry is explicit: on the online branch, Save/Load and game-speed are **disabled** and the menu does **not** pause the sim (peers can't be paused); Settings / Concede / Quit-to-Menu remain available. Detect online via the same branch condition `_Process` already uses (the online-lockstep path vs the offline free-run path).
- Godot-free sim files (`MatchStats`, `GameOverSummary`, `OrderApplier`, `EntityWorld`) stay Godot-free (`using Godot;`-free) — they are globbed into the Tier-1 compile and must stay Tier-1-testable.

**Block If:**
- Delivering pause or game-speed would require folding a speed/pause value into `SimChecksum`/`MatchAgreementHash` or otherwise making cadence sim-visible. HALT `blocked`, condition `pause/speed requires a determinism fold`.
- Concede cannot be resolved through the existing folded `WinStateStore` + `WinConditionSystem` last-team-standing without a NEW folded store or a golden re-baseline. HALT `blocked`, condition `concede requires a determinism fold`.

**Never:**
- Implementing the mid-match save/load serializer — that is Story 11.3. Save/Load appear in the menu **disabled** with a "coming in 11.3" affordance (present so 11.3 wires them; disabled so nothing unbuilt is launchable, honoring the 11.7 honesty principle).
- Touching the **online lockstep cadence** for pause/speed (it is peer-gated) or recording a speed-change as a tick-stamped replay event (unnecessary: replays play back by tick, so an identical tick stream reproduces the run at any playback speed).
- Army-value column or army-value-over-time graph, multi-AI/per-slot AI behavior, MP save/load, subgroup tabs / buff icons / alerts / production-queue-depth (11.4/11.5/11.6). Defer the army-value graph to `deferred-work.md`.
- Changing the tick rate, `FixedDt`, sim system order, or re-baselining any golden/checksum file.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Open menu (offline) | `[PLAY]`, offline, Esc or F10, menu closed | In-match menu opens; sim **pauses** (tick counter frozen); Save/Load disabled, speed control enabled | No error |
| Resume | Menu open, Esc/F10/Resume | Menu closes; sim resumes ticking from the same tick | No error |
| Game speed 2× | Offline, speed set 2× | Sim advances ~2× ticks per wall-second; HUD shows `2×`; per-tick `SimChecksum` stream identical to 1× | Clamp to {0.5,1,2,3} |
| Concede (1v1, offline) | Human=Player1 alive, presses Concede + confirms | Concede order issued for Player1; `Verdict[Player1]=LOST`; next `WinConditionSystem` tick awards the AI (last team standing); score screen shows **DEFEAT** | Already-resolved → no-op |
| Concede cancelled | Confirm dialog dismissed | No order issued; match continues | Safe cancel |
| Quit to Menu | Menu → Quit, confirm | Match resets to `[EDIT]` (existing return-to-Edit reset) and the main-menu overlay is re-shown | Cancel → stays in match |
| Victory reached | Last enemy building destroyed | Score screen (kit-styled) shows **VICTORY**, one row per active faction with verdict + built/killed/lost/razed/ore/crystal + match duration | — |
| Score counters | Match with crystal mined + enemy building razed | Rows reflect crystal gathered and buildings razed per faction; `SimChecksum` unchanged (MatchStats unfolded) | 0 when none |
| Online menu | Online branch, menu opened | Sim does **not** pause; Speed + Save/Load disabled; Settings/Concede/Quit available | — |
| Wrong-faction concede | A concede order whose `expectedFaction` ≠ latch target | `OrderApplier` latches only `expectedFaction`; no cross-faction concede | Anti-cheat no-op |

</intent-contract>

## Code Map

- `godot/src/Core/MainScene.cs` -- `_Process` (:1045) offline free-run `_host.Update((float)delta)` (:1108) = pause/speed insertion point; online branch (:1098) = MP detection; `_UnhandledInput` Esc block (:803, currently `_ctx.SettingsPanel.ToggleVisible()`) = menu bind; `UpdateHud` (:1486, text at :1509) = speed indicator; `ShowGameOver` (:1684, raw-node body :1728-1841) = replace with kit score screen; `LaunchSkirmish` (:747) + `ResetMatchOnReturnToEdit` (:2277, `ModeChanged`-wired) = quit/return reuse; guards `_headless`/`_bootAborted`/`_bootPending` (:257/:168/:177).
- `godot/src/Core/SimulationLoop.cs` -- `Update(realDelta)` (:154) accumulator; `TICKS_PER_SECOND=30` (:24), `FixedDt` (:27) — the per-tick unit, **do not scale**.
- `godot/src/Core/WinStateStore.cs` -- `Verdict[]` (:48, folded v19), `VERDICT_NONE/_WON/_LOST` (:22-26), `WinnerFaction()` (:76), `SoleLoserFaction()` (:91), `MatchTicks` (:33) = duration source.
- `godot/src/Core/WinConditionSystem.cs` -- `ApplyLastTeamStanding` (:447) awards the sole live team once `AnyLost()`; every loop guards `Verdict != NONE` (:247,:421,:437) → an externally-latched LOST is honored. `IsFullyResolved()` (:527) drives the overlay.
- `godot/src/Multiplayer/NetworkCommand.cs` -- `UnitOrder` (:135), `OrderApplier.Apply` (:178) single switch; faction-named commands dispatched before the entity guard (`DslEvent` :270, guard :277-279). Add `Concede` case + `WinStateStore?` param here.
- `godot/src/Core/EntityWorld.cs` -- `enum UnitCommand : byte` (:12-46, frozen 0-21, `DslEvent=21`); add `Concede=22` (≤0x3F; bits 6-7 = queued flag).
- `godot/src/Multiplayer/LockstepManager.cs` -- `ApplyOrders` (live apply) + `EnqueueOrder` (:304, offline applies immediately :306) = concede issue + apply-site-1. `godot/src/Multiplayer/ReplayPlayer.cs` -- `ApplyOrders` = apply-site-2 (must pass `winState` too).
- `godot/src/Core/MatchStats.cs` -- add `_crystalMined`/`_buildingsRazed` + Record/accessor/`Reset` (:80). Unfolded.
- `godot/src/Core/GameOverSummary.cs` -- `GameOverRow` (:22) + `Build(MatchStats, WinStateStore)` (:82) — extend with Crystal + BuildingsRazed columns.
- `godot/src/Economy/GatheringSystem.cs` -- crystal-credit site (ore recorded at :270; crystal at :259-260 records nothing) = `RecordCrystalMined` call. Building-destruction site (`DamageResolver`/`BuildingSystem`, mirror unit `RecordKill` `DamageResolver.cs:117`) = `RecordBuildingRazed(killerFaction)`.
- `godot/src/UI/SettingsPanel.cs` -- kit-overlay template (`EnsureKitInitialized` :183, `_Input` Esc-close :549) + the Settings surface the menu's "Settings" opens. `godot/src/UI/Components/ChimeraDialog.cs` -- `Create`/`AddConfirm(danger)`/`AddCancel`/`Open` + `Confirmed`/`Dismissed` (concede/quit confirms).
- `godot/src/Core/Bootstrap/Phases/HudPhase.cs` / `GameOverOverlayPhase.cs` / `SceneContext.cs` -- overlay construction + `_ctx` handles pattern (mirror to construct the in-match menu + score overlay).

## Tasks & Acceptance

**Execution — sim (Godot-free, Tier-1):**
- `godot/src/Core/EntityWorld.cs` -- add `Concede = 22` to `UnitCommand` (frozen-order append; comment it names a faction, not an entity). -- the wire command.
- `godot/src/Multiplayer/NetworkCommand.cs` -- add optional `WinStateStore? winState = null` to `OrderApplier.Apply`; handle `Concede` before the entity guard: if `winState != null && expectedFaction != Neutral && Verdict[(int)expectedFaction]==VERDICT_NONE` then set it `VERDICT_LOST`; `return`. Null handle ⇒ deterministic no-op (golden/headless). -- concede resolution through the one switch.
- `godot/src/Multiplayer/LockstepManager.cs` + `godot/src/Multiplayer/ReplayPlayer.cs` -- pass the host's `WinStateStore` into every `OrderApplier.Apply` call (both paths) so live and replay resolve identically. -- parity.
- `godot/src/Core/MatchStats.cs` -- add `_crystalMined` (Fixed→int, mirror `RecordOreMined`) and `_buildingsRazed` (mirror `RecordKill` killer side) with accessors + `Reset` coverage. -- observational counters, unfolded.
- `godot/src/Economy/GatheringSystem.cs` (+ the building-destruction site) -- call `RecordCrystalMined` at crystal credit and `RecordBuildingRazed(killerFaction)` at building destruction. -- feed the counters.
- `godot/src/Core/GameOverSummary.cs` -- extend `GameOverRow` + `Build` with `Crystal` and `BuildingsRazed` (from `MatchStats`). -- score-screen data.
- `godot/ProjectChimera.Sim.Tests/**` -- NEW `Skirmish`- or `WinCondition`-adjacent xUnit tests: concede latches LOST (monotone; re-concede no-op; `winState==null` no-op; wrong `expectedFaction` isolation); a conceded 1v1 resolves the opponent WON via a `WinConditionSystem` tick; `MatchStats` crystal + razed counters + `Reset`; `GameOverSummary` new columns; and a determinism assertion that a concede+resolve run leaves no golden/`SimChecksum` file changed. -- Tier-1 proof of the Godot-free core.

**Execution — presentation (Godot-coupled, in-engine gated):**
- `godot/src/UI/InMatchMenuOverlay.cs` -- NEW kit `CanvasLayer` (mirror `SettingsPanel`): Resume / Settings / Save(disabled) / Load(disabled) / Concede(`ChimeraDialog` danger confirm) / Quit-to-Menu(danger confirm), plus a game-speed selector {0.5,1,2,3} and Pause toggle. Disable Speed + Save/Load and suppress auto-pause when online. `_Input` Esc closes (resume). Exposes events the scene wires. -- the menu.
- `godot/src/UI/ScoreScreenOverlay.cs` -- NEW kit overlay consuming `GameOverSummary.Build(...)`: VICTORY/DEFEAT banner keyed off the local faction's verdict, per-active-faction rows (Result / Built / Killed / Lost / Razed / Ore / Crystal), match duration from `MatchTicks/30`, actions **Play Again** (re-open `SkirmishSetupOverlay`, prefilled if a setup is retained) / **Quit to Menu** / **Save Replay** (existing conditional). -- replaces the raw-node `ShowGameOver` body.
- `godot/src/Core/MainScene.cs` -- EDIT: in the offline free-run branch, skip `_host.Update` when paused and pass `delta * _gameSpeed` otherwise; add `_gameSpeed`/`_paused` presentation fields; in `_UnhandledInput`, in `GameMode.Play` route Esc/F10 to toggle `InMatchMenuOverlay` (opening it pauses in SP), leaving Edit-mode Esc→Settings unchanged; wire menu events (Resume=close+resume, Settings=open `SettingsPanel`, Concede=issue a `Concede` `UnitOrder` for the local faction via the existing order-issue path, Quit=confirmed→`ResetMatchOnReturnToEdit`+show `_ctx.MainMenu`); repoint `ShowGameOver` to `ScoreScreenOverlay`; append the speed/pause indicator to the HUD clock line. -- the wiring.
- `godot/src/Core/Bootstrap/Phases/HudPhase.cs` (or a sibling phase) + `godot/src/Core/Bootstrap/SceneContext.cs` -- construct `InMatchMenuOverlay` + `ScoreScreenOverlay`, store on `_ctx`. -- overlay lifecycle, mirroring `SettingsPanel`/`GameOverOverlay`.

**Acceptance Criteria:**
- Given `[PLAY]` offline, when the player presses Esc or F10, then the kit in-match menu opens and the sim pauses (the HUD tick counter stops advancing); pressing Resume/Esc closes it and ticking resumes.
- Given offline play, when the player sets game speed to 2× (or 0.5×), then the sim's wall-clock tick rate scales accordingly, the HUD shows the active speed, and the per-tick `SimChecksum` stream is byte-identical to a 1× run (no golden re-baseline).
- Given a 1v1 offline match, when the human concedes and confirms, then their faction latches `VERDICT_LOST`, the opponent is awarded the win by `WinConditionSystem` on its next tick, and the score screen shows DEFEAT — with no new folded store and no `SimChecksum` change.
- Given the in-match menu, when the player chooses Quit to Menu and confirms, then the match returns to Edit (existing reset) and the main-menu overlay is shown; Save/Load are visibly disabled.
- Given a match that ends in victory or defeat, when it resolves, then a kit-styled score screen renders one row per active faction (verdict + built/killed/lost/razed/ore/crystal) and the match duration, with Play Again / Quit to Menu actions.
- Given the online branch, when the in-match menu is opened, then the sim does not pause and Speed + Save/Load are disabled while Settings/Concede/Quit remain available.
- Given the full Tier-1 suite, when it runs, then all new sim tests pass and no `SimChecksum` golden is re-baselined.

## Design Notes

**Why pause/speed need no fold:** scaling the wall-clock `delta` fed to `SimulationLoop.Update` only changes how many *identical* fixed ticks are consumed per real second; each tick still runs with the same `FixedDt` and the same system order, so the produced tick/`SimChecksum` stream is invariant to speed. Pause = skip `_host.Update` entirely (no accumulation), so no catch-up spiral on resume (delta is clamped to 0.25 s anyway). A replay plays back by *tick*, so it reproduces the run at any playback speed — recording the speed as a tick-stamped event would be redundant and is explicitly out of scope.

**Why concede needs no new store:** `WinStateStore.Verdict` is already folded (v19) and `WinConditionSystem` already treats any externally non-`NONE` verdict as final (every resolution loop guards `Verdict != NONE`). So a concede that latches `Verdict[f]=LOST` through the order stream is picked up by the very next `ApplyLastTeamStanding`: with the conceding faction excluded, the sole remaining live team wins (`AnyLost()` is now true). Concede latches only the conceding faction (not its whole team) — a teammate fights on (WC3 semantics). Goldens never issue `Concede=22`, so the dormant command changes no existing golden (checksum-fold-timing rule: no new mutable array).

**Save/Load honesty:** present-but-disabled with a "coming in 11.3" tooltip keeps the menu layout stable for 11.3 to wire in while not launching an unbuilt system (11.7 principle). This is the epic's stated `11.3 → 11.2` dependency, not scope creep.

## Verification

**Commands:**
- `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` -- expected: all pass incl. the new concede / MatchStats / GameOverSummary tests; no golden re-baseline.
- `dotnet build godot/godot.csproj` -- expected: overlays + MainScene edits compile with no banned-API/AOT analyzer regressions.

**Manual checks (in-engine, gated — Epic-11 per-story gate via `/godot-verify` / godot-mcp bridge):**
- Launch a skirmish → in `[PLAY]` press Esc: menu opens and the HUD tick counter freezes; set 2× and confirm the tick counter advances faster; Concede and confirm → DEFEAT score screen with per-player rows; Quit to Menu returns to the menu. Verify against numbers (tick-counter delta while paused vs running; the conceding faction's verdict; `SimChecksum` unchanged in the diff), per the in-engine gate discipline.

## Review Triage Log

### 2026-07-28 — Review pass
- intent_gap: 0
- bad_spec: 0
- patch: 11: (high 0, medium 3, low 8)
- defer: 1
- reject: 5
- addressed_findings:
  - `[medium]` `[patch]` **KotH concede dead-end**: `WinConditionSystem.ResolveTick`'s `KingOfTheHill` branch `return`ed before `ApplyLastTeamStanding` and `SymmetricLoss` is false for KotH, so a conceded (`Verdict=LOST`-latched) faction never resolved the survivor → the match hung forever (`IsFullyResolved` never true, no score screen). Fixed: the KotH branch now falls through to `ApplyLastTeamStanding` (safe — it no-ops unless `AnyLost()` + one live team, which under KotH only happens on a concede/forfeit); added a 1v1-KotH concede→P2-WON test.
  - `[medium]` `[patch]` **Confirm-dialog state leak**: `InMatchMenuOverlay._activeDialog` was only nulled by the dialog's own callbacks, so an external `Close()` (match resolving mid-confirm) left it non-null and permanently blocked all future Concede/Quit confirms across matches. Fixed: `Close()` now frees + nulls the active dialog first.
  - `[medium]` `[patch]` **Online/replay concede parity untested**: the `winState` threading into `MergedTickApplier` (online merge) and `ReplayPlayer` (replay) had no test; a dropped arg would silently no-op a networked/replayed surrender. Fixed: added `MergedTickApplier` Concede-latch tests + a new `ReplayConcedeTests`.
  - `[low]` `[patch]` Buildings-razed credited any building death — added a `razer != Neutral && razer != owner` enemy check (no self/neutral raze credit) + tests.
  - `[low]` `[patch]` `MainScene.IssueConcede` null-deref inconsistency (`?.` then unguarded) — added an explicit `Lockstep == null` guard.
  - `[low]` `[patch]` Concede confirm copy over-promised "the opponent is awarded the win" (false in team/KotH) — reworded to "Your faction forfeits the match."
  - `[low]` `[patch]` Speed selector kept a stale highlight after a match reset — `Open(online, currentSpeed)` now re-syncs the toggle to the scene's actual `_gameSpeed`.
  - `[low]` `[patch]` Esc on the score screen fell through to toggling a hidden `SettingsPanel` — Esc/F10 now swallowed while `_gameOver`.
  - `[low]` `[patch]` Wiring-seam test gaps for the razer credit (`DamageResolver.ApplyToBuilding`) and crystal credit (`GatheringSystem`) — added `DamageResolverTests` (+4) and `GatheringSystemTests` (+2) driving the counters through their real call sites.
- **defer (1):** the online concede robustness/UX (buffer-full silent drop + no pending-surrender feedback) — MP-only, un-exercisable until Epic 9; appended to `deferred-work.md`.
- **reject (5):** headless overlay construction in `GameOverOverlayPhase` (kit-in-phase is a pre-existing pattern — `SettingsPanel` already does it; no new headless risk); int-overflow duration in a `GD.Print` debug line (pathological 800+-day match, cosmetic, score overlay already guards); un-pause-while-menu-open interaction (by design — explicit un-pause); score-screen Crystal/Razed columns "beyond the slug" (a documented in-spec scope choice); `MatchLifecycleController` same-instance double `WinState` assign (by design — offline apply-now vs online re-assign of the same instance).

## Verification — In-Engine Gate (independent review-layer drive)

### In-Engine Gate - 2026-07-28
- surface: main menu → skirmish setup → Launch → `[PLAY]`; in-match menu (Esc), true-pause, 2× game-speed, Concede→confirm→score screen, Quit to Menu. 1v1 Crucible Covenant (Human) vs Crucible Covenant (AI Normal), Alpha Skirmish, DestroyAllBuildings.
- launched: `dotnet build godot/godot.csproj` (0/0) then `godot_editor_edit run`; menu + buttons driven by emitted Button `pressed` / `ChimeraDialog` confirm signals; HUD Tick/Hash read via tree-walk gated on the MainScene instance id changing on launch (38470157803 → 2238869150255) so the post-reload match is measured, not the boot scene.
- digest: pause held `Tick 641 / Hash 0xDFC2B085` unchanged across 2001 ms of wall-clock (FPS 542, render still running), then Resume advanced 641→752. Speed: 1× = Δtick 294 / Δms 9794 = 30.02 tps; 2× (HUD `2×` tag) = Δtick 606 / Δms 10100 = 60.0 tps → ratio 2.00×. Concede→confirm resolved next `WinConditionSystem` tick to score screen `DEFEAT` / "Player 2 Wins!" / Duration 2:09; rows `● P1 (local): LOST` (Built 0, Ore 539, Crystal 0) and `■ P2 (AI): WON` (Built 5, Ore 559). Quit to Menu → HUD `[EDIT] Tick 0 Hash —`, main-menu PLAY visible, speed/pause reset. Save/Load render present-but-disabled "COMING IN 11.3".
- asserted: (pause) the tick stream halts — 0 ticks + hash invariant over 2 s, vs the I/O-matrix "tick counter frozen"; (speed) cadence = exactly 2.00× the 1× rate, both arms compared directly at 30 tps baseline; (concede) local faction latches LOST and the opponent is awarded WON, matching the I/O-matrix concede row (P2 Built 5 reconciles with the live HUD's P2 unit count; P1 Ore 539 reconciles with the resource HUD); (quit) returns to Edit + menu. No golden/checksum file appears anywhere in the diff. No editor/runtime errors across the session.
- caveat: the online-branch asymmetry (menu doesn't pause; Speed + Save/Load disabled) and a non-zero Crystal/Razed value were NOT exercised in-engine (single-client offline drive; this match gathered no crystal and razed no buildings, so the counters correctly read 0 = "0 when none"). Non-zero accumulation is proven by Tier-1 `MatchStatsCountersTests` / `DamageResolverTests` / `GatheringSystemTests`; online parity by `MergedTickApplierTests` + `ReplayConcedeTests`.
- result: PASS

## Auto Run Result — dev-auto (2026-07-28)

**Summary:** Built the offline session shell for Story 11.2 as presentation-layer controls over the untouched 30 Hz sim: an Esc/F10 in-match menu (Resume/Settings/Save·Load-disabled/Concede/Quit) from the 3.1x kit; true SP pause + 0.5×–3× game-speed by skipping/scaling the wall-clock delta on the offline free-run branch (byte-identical tick stream, zero SimChecksum change); a `UnitCommand.Concede = 22` riding the `OrderApplier` single-switch that latches the already-folded `WinStateStore.Verdict` so `WinConditionSystem` awards the winner; two unfolded `MatchStats` counters (crystal gathered, buildings razed); and a kit-styled victory/defeat score screen replacing the raw-node `ShowGameOver` body. No new folded store, no golden re-baseline.

**Files changed (one line each):**
- `godot/src/Core/EntityWorld.cs` — `UnitCommand.Concede = 22` (frozen-order append; names a faction).
- `godot/src/Multiplayer/NetworkCommand.cs` — `OrderApplier.Apply` gains `WinStateStore? winState`; `Concede` case latches `Verdict[expectedFaction]=LOST` monotone, before the entity guard; null = no-op.
- `godot/src/Multiplayer/LockstepManager.cs` / `ReplayPlayer.cs` / `Server/MergedTickApplier.cs` — thread `WinState` into the live, replay, and online-merge apply paths (one-switch parity); `EnqueueConcede` (offline apply-now / online buffer).
- `godot/src/Core/WinConditionSystem.cs` — KotH branch falls through to `ApplyLastTeamStanding` so a forfeit resolves under every preset (review patch #1).
- `godot/src/Core/MatchStats.cs` — `_crystalMined` / `_buildingsRazed` counters + accessors + `Reset` (unfolded/observational).
- `godot/src/Economy/GatheringSystem.cs` — `RecordCrystalMined` at the crystal-credit site.
- `godot/src/Combat/DamageResolver.cs` — `ApplyToBuilding` credits `RecordBuildingRazed(razer)` only for an enemy (`razer != Neutral && razer != owner`); `CombatSystem.cs` / `ProjectileSystem.cs` pass the razer + `MatchStats`.
- `godot/src/Core/GameOverSummary.cs` — `GameOverRow` + `Build` gain `CrystalMined` / `BuildingsRazed` columns.
- `godot/src/UI/InMatchMenuOverlay.cs` — NEW kit menu (Resume/Settings/Save·Load-disabled/Concede-confirm/Quit-confirm + speed selector {0.5,1,2,3} + pause; online-aware; Esc-close; dialog-leak-safe `Close`).
- `godot/src/UI/ScoreScreenOverlay.cs` — NEW kit victory/defeat screen (per-faction rows Built/Killed/Lost/Razed/Ore/Crystal, `MatchTicks/30` duration, Play Again / Quit to Menu / Save Replay).
- `godot/src/Core/Bootstrap/Phases/GameOverOverlayPhase.cs` / `SceneContext.cs` / `MatchLifecycleController.cs` — construct + `_ctx`-store the two overlays; wire `Lockstep.WinState` / `ReplayPlayer.WinState`.
- `godot/src/Core/MainScene.cs` — `_gameSpeed`/`_paused` fields; offline branch skips/scales `_host.Update`; Play-mode Esc/F10 → in-match menu (pauses in SP), `_gameOver` swallows Esc; `WireSessionShell` (resume/concede/quit/play-again); `ShowGameOver` → `ScoreScreenOverlay`; HUD speed/pause indicator; reset on return-to-Edit.
- Tests: NEW `ConcedeCommandTests` (latch/monotone/null/Neutral/1v1-resolve/KotH-resolve/checksum-invariance), `MatchStatsCountersTests`, `ReplayConcedeTests`; extended `GameOverSummaryTests`, `DamageResolverTests` (+4 razer), `GatheringSystemTests` (+2 crystal), `MergedTickApplierTests` (+2 concede).

**Review findings breakdown:** 11 patched (0 high / 3 medium / 8 low — all applied and re-verified), 1 deferred (online concede robustness/UX → `deferred-work.md`), 5 rejected (see triage log).

**Follow-up review recommendation:** `true`. Patched this pass: 0 high, 3 medium, 8 low → score = 3×3 + 1×8 = 17 (≥ 5) → true.

**Verification performed (independently re-run by the orchestrator):** `dotnet build godot/godot.csproj` → 0 errors / 0 warnings. `dotnet test …/ProjectChimera.Sim.Tests` → **3593 passed / 0 failed / 1 pre-existing skip** (+11 new). `git status` shows **no** golden/`SimChecksum` file changed. In-engine gate: **PASS** (see block above).

**Residual risks:** the online concede path (buffer-full drop + pending-surrender feedback) is deferred and un-exercisable until Epic 9; the online-menu asymmetry and non-zero crystal/razed values were verified by Tier-1 tests + structurally, not in a live online/heavy match; `LockstepManager.EnqueueConcede` has no direct unit test (Godot/ENet-coupled, excluded from the Tier-1 assembly) — its offline core is `OrderApplier.Apply(..., winState)`, covered by `ConcedeCommandTests` and the in-engine gate.
