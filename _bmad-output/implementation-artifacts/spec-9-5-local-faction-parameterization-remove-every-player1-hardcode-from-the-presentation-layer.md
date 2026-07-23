---
title: 'Local-faction parameterization — remove every Player1 hardcode from the presentation layer'
type: 'refactor'
created: '2026-07-23'
status: 'done'
baseline_revision: '64a7a7f359b4e12d1d462c00222740ab05e262d3'
final_revision: 'af50ec7'
review_loop_iteration: 0
followup_review_recommended: false
context:
  - '{project-root}/_bmad-output/project-context.md'
  - '{project-root}/_bmad-output/implementation-artifacts/epic-9-context.md'
  - '{project-root}/godot/src/UI/SelectionSystem.cs'
  - '{project-root}/godot/src/UI/CommandCardSystem.cs'
  - '{project-root}/godot/src/UI/MinimapBridge.cs'
  - '{project-root}/godot/src/Core/FogOfWarSystem.cs'
  - '{project-root}/godot/src/Core/MainScene.cs'
  - '{project-root}/godot/src/Core/Bootstrap/Phases/CameraPhase.cs'
  - '{project-root}/godot/src/Core/Bootstrap/Phases/MinimapPhase.cs'
  - '{project-root}/godot/src/Core/Bootstrap/Phases/MatchLifecycleController.cs'
  - '{project-root}/godot/src/Multiplayer/LockstepManager.cs'
  - '{project-root}/godot/ProjectChimera.Sim.Tests/Core/HeightAdvantageVisionTests.cs'
warnings: [oversized]
---

<intent-contract>

## Intent

**Problem:** The presentation layer assumes the local player is always `Faction.Player1`. `LockstepManager.LocalFaction` (default `Player1`, correctly set to the server-assigned faction via `GoOnline` at match start) is the single source of truth, and the overlays/chat/hero-owner paths already consume it — but the interaction and perspective cluster does NOT: `SelectionSystem`/`CommandCardSystem` gate "is this mine / issue as me" on the literal `Faction.Player1`, `MinimapBridge` colours own-vs-enemy off `Player1`, the worker-build placement in `MainScene` tags the local build `Player1`, and the per-client `FogOfWarSystem` is constructed and pinned to `Player1` at `SimulationHost.cs:203`. Consequently a client assigned Player2..Player8 (dedicated-server path, `_assignedFaction` → `LocalFaction`) cannot box-select or command its own units, sees the enemy's fog/colours instead of its own, and its own units are fogged — the game is unplayable from any non-Player1 slot. This blocks the epic's up-to-4 verified multiplayer (9.5 sequences before 9.6/9.7).

**Approach:** Route every presentation site that means "the local player" through `LockstepManager.LocalFaction` instead of the `Player1` literal. Inject a `Func<Faction>` local-faction getter (default `() => Faction.Player1`, wired at the construction phases to `() => _ctx.Lockstep?.LocalFaction ?? Faction.Player1`) into `SelectionSystem`, `CommandCardSystem`, and `MinimapBridge` — the same late-bound getter pattern the overlays already use, so single-player (LocalFaction never leaves its Player1 default) is behaviour-identical. `MainScene` reads `_ctx.Lockstep?.LocalFaction` directly at the worker-build site. Add `FogOfWarSystem.SetViewer(Faction)` and call it in `MatchLifecycleController.OnMatchStart`'s player branch so each client's fog reveals its own faction. This is presentation-perspective only: no sim array is folded, so no golden and no `AlgoVersion` may move.

## Boundaries & Constraints

**Always:** The getter defaults to `() => Faction.Player1` and reads `_ctx.Lockstep?.LocalFaction ?? Faction.Player1`, so an un-wired system and every single-player/offline path stay byte-identical to today. Ownership/selection filters (`FactionOf == Player1` "is this mine", `enemy = not me & not Neutral`, own-building exclusion) run on BOTH the online and offline paths and MUST become the local faction — these are the correctness-critical sites. Order-issue faction in `OrderApplier.Apply(..., Faction.Player1)` calls only executes on the offline branch (`_lockstep == null ? apply-now`; online defers to the server-restamped merged tick), where the local faction is Player1 — swapping to the getter is behaviour-preserving there and forward-correct. Fog `_faction` retarget is safe because the fog `Grid` is read ONLY by presentation (`MinimapBridge`, `FogOfWarBridge`) — never by any sim system — and the class doc already states it is not folded into `SimChecksum`; a per-client differing fog is correct RTS behaviour. Keep `SimulationHost.cs:203`'s `new FogOfWarSystem(Faction.Player1)` default (SP viewer). All touched sim-layer code (`FogOfWarSystem`) stays Godot-free (`SetViewer` just assigns the enum field — no float/Dictionary/DateTime/Random).

**Block If:** Any pre-existing committed golden moves (`golden-scenario`, `golden-multifaction`, `golden-applier-scenario`, `golden-merged-n2`, `hero-start-state.golden.txt`, the `SimChecksumCoverageGuardTest` pin) or `SimChecksum.AlgoVersion`(21) / `StartStateHash.AlgoVersion`(2) / `PROTOCOL_VERSION`(2) changes: this story folds no sim array, so a moved golden means an unintended sim-path edit — STOP, do not re-baseline.

**Never:** Do NOT touch the dual-faction INFORMATIONAL readouts — the `MainScene` HUD unit-count line, resource label, and end-of-match stats panel (`MainScene.cs:1183-1206,1390-1395`) and the cosmetic `== Player1 ? "P1" : "P2"` faction-name labels (`SelectionSystem.cs:1187`, `CommandCardSystem.cs:299`). These are literal per-faction displays (they show all factions explicitly and remain readable/functional from any slot), NOT local-player proxies; generalising them to a personalised or N-faction readout is separate presentation work, out of scope here. Do NOT touch editor/authoring `Player1` (`EntityPlacer`), per-faction VISUAL maps (`BuildingBridge`, `FactionVisualsPhase`), or any `FactionRegistry.ToFaction`/slot-def population (sim/visual, not "local"). Do NOT enable or verify >2 live players, raise `MAX_PLAYERS`, or change build-command online replication (pre-existing; orthogonal). Do NOT modify `StartStateHash`, `SimChecksum` coverage, or any wire format.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Fog viewer = Player2 | world with a P1 unit and a P2 unit at distinct cells, `new FogOfWarSystem(Player2)`, one Tick | P2's unit cell is VISIBLE; the P1-only cell is NOT visible | n/a (pure) |
| Fog viewer = Player1 (default) | same world, default ctor / `SimulationHost` | P1's cell VISIBLE, P2's cell not — byte-identical to pre-change | n/a |
| `SetViewer` retarget | fog default Player1, `SetViewer(Player2)`, Tick | reveals Player2 (as if constructed with Player2) | idempotent field set |
| Local player = Player2 selects | box/click over own P2 units (online, LocalFaction=Player2) | only P2 units enter the selection; P1/enemy units excluded | out-of-faction ignored |
| Offline single-player | no `GoOnline` call, LocalFaction stays Player1 | every getter returns Player1 → identical selection/fog/minimap/order behaviour to today | n/a |
| Un-wired getter | a system whose `SetLocalFaction` was never called | default `() => Player1` → today's behaviour | never null |

</intent-contract>

## Code Map

- `godot/src/Core/FogOfWarSystem.cs` -- sim-layer, Godot-free, Tier-1 (`src/Core/**`). Make `_faction` mutable; add `public void SetViewer(Faction faction) => _faction = faction;`. Not folded into checksum (see class doc `:78-79`). Ctor default stays `Player1`.
- `godot/src/UI/SelectionSystem.cs` -- add `private Func<Faction> _localFaction = () => Faction.Player1;` + `public void SetLocalFaction(Func<Faction> getter) => _localFaction = getter;`. Swap the LOCAL literals: `:562`, `:991` (select-own), `:922` (cast caster-mine guard), `:1015` (enemy = not-me), `:1060` (exclude own buildings), `:957` (SetRally issue faction). LEAVE `:1187` (cosmetic label). Note `:933` already applies with the caster's own faction — no change.
- `godot/src/UI/CommandCardSystem.cs` -- same getter+setter. Swap: `:239/:251/:270` (focused-unit-mine guards), `:746/:773/:803/:835/:851` (building-mine guards), `:752/:778/:808/:840/:856` (order-issue faction), `:790` (`FindNearestOwnedHero` owner). LEAVE `:299` (cosmetic label).
- `godot/src/UI/MinimapBridge.cs` -- same getter+setter. Swap `:241` and `:251` `== Faction.Player1 ? P1_COLOR : P2_COLOR` → `== _localFaction() ? P1_COLOR : P2_COLOR` (own=P1_COLOR, everyone else=P2_COLOR — own-vs-enemy semantics; keep the two colour constants).
- `godot/src/Core/MainScene.cs` -- `:564` worker-build `Faction.Player1` → `_ctx.Lockstep?.LocalFaction ?? Faction.Player1`. Do NOT touch the HUD/resource/stats blocks.
- `godot/src/Core/Bootstrap/Phases/CameraPhase.cs` -- after constructing selection/commandCard, call `selection.SetLocalFaction(() => _ctx.Lockstep?.LocalFaction ?? Faction.Player1);` and the same on `commandCard`. (`_ctx.Lockstep` is built later at phase 17 — the closure defers the read to gameplay time.)
- `godot/src/Core/Bootstrap/Phases/MinimapPhase.cs` -- `minimap.SetLocalFaction(() => _ctx.Lockstep?.LocalFaction ?? Faction.Player1);` after `Initialize`.
- `godot/src/Core/Bootstrap/Phases/MatchLifecycleController.cs` -- in `OnMatchStart`'s player branch (`:113-121`, alongside `GoOnline(localFaction)`), add `_ctx.Fog.SetViewer(localFaction);` so the client's fog reveals its assigned faction. (Spectator branch keeps `FogBridge.RevealAll = true`.)
- `godot/ProjectChimera.Sim.Tests/Core/FogPerspectiveTests.cs` -- NEW, Tier-1. Mirror `HeightAdvantageVisionTests` world construction (`w.Create(pos, faction, hp, spd)`, `VisionRange[id]`, `fog.Tick(w, Fixed.Zero)`, `fog.IsVisible(x,z)`).

## Tasks & Acceptance

**Execution:**
- `godot/src/Core/FogOfWarSystem.cs` -- add `SetViewer(Faction)`, make `_faction` mutable -- the per-client fog viewer retarget seam.
- `godot/src/UI/SelectionSystem.cs` -- add local-faction getter/setter; parameterize the 6 LOCAL sites -- a non-Player1 client selects/commands/casts on its OWN units.
- `godot/src/UI/CommandCardSystem.cs` -- add getter/setter; parameterize the 11 LOCAL sites -- the command card acts on the local player's units/buildings/hero.
- `godot/src/UI/MinimapBridge.cs` -- add getter/setter; parameterize the 2 colour sites -- own-vs-enemy minimap colours from the local player's view.
- `godot/src/Core/MainScene.cs` -- parameterize the worker-build placement faction -- a local build is tagged the local faction.
- `godot/src/Core/Bootstrap/Phases/CameraPhase.cs` + `MinimapPhase.cs` -- wire the getter to `_ctx.Lockstep?.LocalFaction` -- inject the live local faction into the three UI systems.
- `godot/src/Core/Bootstrap/Phases/MatchLifecycleController.cs` -- `Fog.SetViewer(localFaction)` at match start -- retarget the client's fog to its assigned faction.
- `godot/ProjectChimera.Sim.Tests/Core/FogPerspectiveTests.cs` (NEW) -- Tier-1 tests over every fog I/O-matrix row (Player2 viewer, Player1 default byte-identical, `SetViewer` retarget) -- the automated perspective proof.

**Acceptance Criteria:**
- Given a `FogOfWarSystem(Faction.Player2)` and a world holding one Player1 and one Player2 unit at distinct cells, when ticked, then the Player2 unit's cell is `VISIBLE` and the Player1-only cell is not; and the symmetric Player1 case reveals the Player1 cell only.
- Given a fog built with the default ctor, when `SetViewer(Faction.Player2)` is called before the tick, then its revealed grid equals a fog constructed directly with `Faction.Player2`.
- Given an offline/single-player match (LocalFaction never leaves its Player1 default), when the player selects, commands, views the minimap, and reads fog, then behaviour is identical to before this change (every getter returns Player1).
- Given the full suite, when it runs, then every pre-existing committed golden is byte-identical and `SimChecksum.AlgoVersion`(21) / `StartStateHash.AlgoVersion`(2) / `PROTOCOL_VERSION`(2) are unchanged (moved golden = Block-If).

## Design Notes

**Why a `Func<Faction>` getter, not a value.** The three UI systems are constructed at bootstrap phases 7/10, but `LockstepManager` (owner of `LocalFaction`) is built at phase 17, and the assigned faction only resolves at match start (`GoOnline`). A captured closure over `_ctx` defers the read to gameplay time, exactly matching the established `CustomHudOverlayPhase`/`ObjectiveLogOverlayPhase` pattern. The `?? Faction.Player1` guard covers any pre-match invocation.

**Why online determinism is untouched.** On the online path, unit/building orders are enqueued and the server re-stamps faction from `SLOT_FACTION[sourceSlot]` (Story 9.3); the client's `OrderApplier.Apply(..., Player1)` runs ONLY offline where the local faction IS Player1. So the order-issue swaps are behaviour-preserving; the load-bearing fixes are the ownership/selection FILTERS (run on both paths) and the fog viewer. None feed `SimChecksum`, so zero goldens move — the presentation-only guarantee the epic relies on.

## Verification

**Commands:**
- `dotnet build godot/godot.csproj` -- expected: compiles clean; determinism analyzer green (no new float/Dictionary/Random in `FogOfWarSystem.SetViewer`).
- `dotnet test godot/ProjectChimera.Sim.Tests` -- expected: all pass incl. the new `FogPerspectiveTests`; EVERY pre-existing golden byte-identical (moved golden = Block-If, not a re-baseline).
- `dotnet test godot/ProjectChimera.Sim.Tests --filter "FullyQualifiedName~Golden|SimChecksumCoverageGuard|VersionStampConsistency"` -- expected: goldens unchanged; AlgoVersion pins (21 / 2) and PROTOCOL_VERSION (2) unchanged.

**Manual checks (Godot-side integration, not Tier-1):**
- `godot --headless -- --loopback-test` (`LoopbackDesyncSelfTest`) -- expected: still `RESULT: PASS` — the getter/fog wiring does not regress the server + 2-client handshake/desync path.
- Two-client (or a forced `LocalFaction = Player2`) run: the Player2-assigned client can box-select and command its OWN units, its own units light the fog, and the minimap paints its units as own (P1_COLOR) and the enemy as P2_COLOR — the presentation surfaces that cannot be Tier-1 tested (Godot-coupled), the same accepted boundary as 9.3/9.4's client-node wiring.

## Spec Change Log

_None — no bad_spec loopback occurred; the review pass resolved via patches only._

## Review Triage Log

### 2026-07-23 — Follow-up review pass #3 (review_loop_iteration 0)
- intent_gap: 0
- bad_spec: 0
- patch: 1: (high 0, medium 0, low 1)
- defer: 2: (high 0, medium 0, low 2)
- reject: 14 (plus 1 re-surfaced finding already in `deferred-work.md`, not re-added per the NEW-entries-only constraint)
- addressed_findings:
  - `[low]` `[patch]` **`CommandCardSystem.UpdatePanels` did not hoist the local-faction read** (adversarial). The prior pass hoisted `Faction me = _localFaction();` in `MinimapBridge.DrawDots` and the four `SelectionSystem` scan loops but missed `UpdatePanels`, which called the getter three separate times (`workerSelected`/`abilitySelected`/`inventorySelected`). Hoisted to a single frame-constant `Faction me` at the top of the method, matching the sibling systems — one consistent per-frame read instead of three delegate dispatches. Verified: `dotnet build` clean (determinism analyzer green), `dotnet test` 3088 passed / 1 skipped / 0 failed, every golden byte-identical, AlgoVersion pins (21/2) and PROTOCOL_VERSION (2) unchanged.
- deferred (see `deferred-work.md`, 2 NEW entries): (1) the pre-existing raw-`LockstepManager.LocalFaction` consumers (the personalised DSL-readback overlays + hero-owner minting) are NOT clamped through `EffectiveLocalFaction`, so `GoOffline` (which resets `IsOnline`/`IsSpectator` but never `LocalFaction`) leaves them resolving to the stale online faction across the same-process online→Edit→offline-F5 seam — a two-tier source of truth the 9.5 clamp closed for its own sites but not for these; (2) `MinimapBridge.DrawDots` draws every unit/building dot with no fog-visibility gate, so each client sees all enemy positions on its minimap regardless of fog — a pre-existing fairness gap, distinct from the already-deferred spectator-`RevealAll` entry (that one is fog-not-revealed for spectators; this one is fog-not-concealed for players).
- rejected (14, highlights): the enum-indexed-array crash risk from the widened Player2..Player8 faction domain (VERIFIED not a defect — every faction-indexed array is sized to `FactionRegistry.FACTION_ARRAY_SIZE` = 9 = Neutral + Player1..Player8, so no out-of-bounds); the online-Neutral clamp on `LocalFactionPolicy.Effective` (unreachable — `MatchLifecycleController` routes Neutral to `GoSpectate`, so the online non-spectator branch never sees Neutral); the null-getter guard on `SetLocalFaction` (defensive nit; only trusted bootstrap phases wire it); the fog viewer sourced from raw `localFaction` vs UI's `EffectiveLocalFaction` (provably equal at the player-branch call site — online non-spectator with that faction); the worker-build 5th inline resolution path (spec-sanctioned — the intent's Approach explicitly says `MainScene` reads `_ctx.Lockstep` directly at the worker-build site); the replay-playback and 3+-faction/FFA-minimap perspective findings (out of scope per intent — N-faction colour is 9.7/9.14/9.15, replay-perspective is separate presentation work, and both are pre-existing); the Godot-coupled verification-gap findings — the `GoOffline`→`EffectiveLocalFaction` flag-reset seam, the fog reset seam, and the getter wiring are untestable Tier-1 (`LockstepManager`/`MainScene`/`SelectionSystem` are Godot-coupled — `GoOnline`/`GoOffline` call `GD.Print`; the existing parity tests confirm this and route through `ReplayPlayer`), the accepted Tier-1/Tier-2 boundary already disclosed in the spec's Verification section, same as 9.3/9.4; the intent-alignment auditor's descriptive Reading-A/Reading-B analysis (the concrete two-tier-leak half is deferred above; the rest is descriptive and already disclosed).

### 2026-07-23 — Follow-up review pass (review_loop_iteration 0)
- intent_gap: 0
- bad_spec: 0
- patch: 3: (high 1, medium 0, low 2)
- defer: 0 (the worker-build online-desync surfaced again by the edge-case lens is ALREADY in `deferred-work.md` from the prior pass — not re-added, per the NEW-entries-only constraint and to avoid ledger duplication)
- reject: 16
- addressed_findings:
  - `[high]` `[patch]` **Stale online-state defeats the offline clamp after online→Edit→offline-F5 in one process** (converged by the adversarial + edge-case lenses). The prior pass's `LocalFactionPolicy.Effective(isOnline, isSpectator, localFaction)` clamps offline→Player1 by gating on `isOnline`, but `LockstepManager.GoOffline()` (the ONLY writer of `IsOnline = false`) has zero callers and `ResetMatchOnReturnToEdit` never reset the online flags. Because the `LockstepManager` is built ONCE at bootstrap and survives every F5 Edit↔Play re-apply (confirmed by `MatchLifecycleController.cs:35`), after any online match as Player2..Player8 (or spectate as Neutral) a subsequent OFFLINE F5 playtest saw `IsOnline` still true → `EffectiveLocalFaction` resolved to the stale online faction → selection selected nothing, the minimap mis-coloured, the command card was inert, and worker-build mis-tagged — the EXACT leak the prior pass's policy was written to close, reintroduced via the `IsOnline` flag instead of `LocalFaction`. (Regression is this-story-caused: pre-9.5 these sites were hardcoded `Player1`, so offline-after-online was correct.) Only the fog escaped, via its separate `SetViewer(Player1)` reset. Fixed by calling `_ctx.Lockstep.GoOffline()` at the `ResetMatchOnReturnToEdit` seam (beside the existing fog reset) — `GoOffline` is a pure flag reset (`IsOnline`/`IsSpectator`/`IsStalling` → false; no transport teardown), and a following online match re-establishes everything via `GoOnline`. The offline clamp now engages for all four consumers.
  - `[low]` `[patch]` **`LocalFactionPolicy` doc claimed "no reset required"** (adversarial) — the load-bearing invariant the class documented as self-enforcing was false (it silently assumed `IsOnline` is reset offline, which it was not). Rewrote the `<para>` to state the actual dependency: the clamp needs the online FLAGS accurate, so the return-to-Edit seam calls `GoOffline()`; without it the flags persist across the same-process Edit↔Play boundary and the clamp never engages.
  - `[low]` `[patch]` **Three injected-getter field comments named the raw `LocalFaction`** (adversarial) — `SelectionSystem`/`CommandCardSystem`/`MinimapBridge` field docs said "CameraPhase/MinimapPhase wires it to `_ctx.Lockstep?.LocalFaction`" while the phases actually wire `EffectiveLocalFaction`. The distinction is the whole point (raw leaks, Effective clamps), so a maintainer could have "fixed" the leak by switching to raw and undone the clamp. Corrected all three to name `EffectiveLocalFaction` and note the offline/spectator→Player1 clamp.
- rejected (16, highlights): the 2-colour minimap / spectator-command / worker-build-5th-access-pattern / SetViewer-Neutral-validation findings (out of scope per intent — N-faction colour is 9.7/9.14/9.15; spectator sends are already dropped at `EnqueueOrder`'s `IsSpectator` guard; the build faction arg is the only in-scope build change and its online-desync is separately deferred); the online-Neutral-clamp and abnormal-exit-fog-stale findings (unreachable by the `Neutral→GoSpectate` convention / speculative non-`ResetMatchOnReturnToEdit` exit path, not demonstrated); the Godot-coupled verification-gap findings — non-P1 selection/command/minimap sites, `EffectiveLocalFaction` property wiring, and the phase wiring all untested (accepted Tier-1/Tier-2 boundary, already disclosed in the spec's Verification section, same as 9.3/9.4); the double-fallback "fails silent" and fog round-trip / test magic-number nits (by-design safety mechanism / low-value noise); the intent-alignment auditor's observations (descriptive, already disclosed).

### 2026-07-23 — Review pass (review_loop_iteration 0)
- intent_gap: 0
- bad_spec: 0
- patch: 4: (high 1, medium 1, low 2)
- defer: 2: (high 1, medium 0, low 1)
- reject: 3
- addressed_findings:
  - `[high]` `[patch]` **Stale local-faction after online/spectate → offline** (converged independently by all four lenses — adversarial, edge-case, verification-gap, intent-alignment). The added getters `_ctx.Lockstep?.LocalFaction ?? Faction.Player1` reintroduced the exact hazard `LockstepManager.EnqueueDslEvent` documents and routes around (`LockstepManager.cs:312-315`): `_ctx.Lockstep` is non-null offline and `LocalFaction` is never reset by `GoOffline`, so after any online (Player2) or spectate (Neutral) match a subsequent offline F5 playtest in the same process resolved "the local player" to the stale Player2/Neutral → selection selects nothing, minimap mis-colours, command card inert, worker-build mis-tagged. Fixed with a Godot-free pure rule `LocalFactionPolicy.Effective(isOnline, isSpectator, localFaction) => (isOnline && !isSpectator) ? localFaction : Player1` (new `src/Core/LocalFactionPolicy.cs`, Tier-1 tested with 7 rows incl. stale-Player2/Player8/Neutral → Player1), surfaced as `LockstepManager.EffectiveLocalFaction`, with all four getter/read sites (CameraPhase ×2, MinimapPhase, MainScene worker-build) repointed to it. Also closes the spectator-minimap colour regression (spectator → Player1, restoring pre-9.5 P1-blue/P2-red) and the spectator-selection concern in one shot.
  - `[medium]` `[patch]` **Fog viewer stale after online → offline** (adversarial + edge-case). `SetViewer` was called only in the online player branch of `OnMatchStart`, and `FogOfWarSystem.Reset()` wipes the Grid but not `_faction`, so an offline F5 after an online-as-Player2 match revealed Player2's vision. Fixed by resetting `_ctx.Fog.SetViewer(Faction.Player1)` in `MainScene.ResetMatchOnReturnToEdit()` beside the existing `FogBridge.RevealAll = false` reset — NOT inside `Fog.Reset()` (which runs on the Edit→Play transition after `OnMatchStart`'s SetViewer and would clobber the online viewer). Online player-branch `SetViewer(localFaction)` still wins for a live match.
  - `[low]` `[patch]` **Stale "Player1" doc comments** in the touched methods (adversarial). Swept the "(Player1)"/"P1"/"each alive P1 unit" parentheticals in `CommandCardSystem` (5 Issue* XML docs + 3 focus guards), `SelectionSystem` (cast/follow/enemy comments), and the `FogOfWarSystem` class header to "the local faction" / "the viewer faction" so a maintainer cannot grep them as justification to re-hardcode.
  - `[low]` `[patch]` **Per-entity delegate dispatch in hot loops** (adversarial). Hoisted `Faction me = _localFaction();` out of the per-entity/per-building loops in `MinimapBridge.DrawDots` and `SelectionSystem` (`FinalizeBoxSelect`/`FindNearestUnit`/`FindNearestEnemyUnit`/`FindNearestEnemyBuilding`) — the value is frame-constant.
- deferred (see `deferred-work.md`, 2 NEW entries): (1) worker-build placement is a direct sim mutation not routed through the lockstep `EnqueueOrder` seam → a reachable-online desync (pre-existing; 9.5 only changed the faction arg; the intent explicitly excludes build-command online replication); (2) spectator minimap fog ignores `FogBridge.RevealAll` (pre-existing MinimapBridge-reads-Grid-directly; surfaced by the spectator analysis).
- rejected (3): the 2-colour minimap / `"P1"/"P2"` card-label N-faction ceiling (out of scope per the intent's local-faction-parameterization reading — N-faction colour/label expansion is the epic's dedicated N-player / "faction colours are sacred" work in 9.7/9.14/9.15, and is pre-existing); the `?? Faction.Player1` "masks a null Lockstep as a fault" nit (with `EffectiveLocalFaction` the offline→Player1 result is the intended default, and a pre-match null Lockstep resolving to Player1 is correct, not a masked fault); the intent-alignment auditor's "the loopback AC is proven manually, not automatically" observation (descriptive and already disclosed in the spec's Verification section — the fog perspective mechanic IS Tier-1 tested and the Godot-coupled UI surfaces sit on the same accepted Tier-1/Tier-2 boundary as 9.3/9.4).



## Auto Run Result

Status: done (follow-up review pass #3)

**Summary:** A fresh follow-up review of the already-shipped Story 9.5 change (presentation-layer local-faction parameterization). Four review lenses (adversarial, edge-case, verification-gap, intent-alignment) ran in parallel over the full `64a7a7f..HEAD` diff. The two prior passes had already closed the headline defects (the stale-online-state leak, the fog reset, the doc-comment drift) and already deferred the worker-build online-desync and the spectator-minimap `RevealAll` gaps, so this pass converged fast: one small completeness patch, two new defers, everything else re-surfaced-and-already-handled or rejected.

**Files changed this pass:**
- `godot/src/UI/CommandCardSystem.cs` — hoisted the local-faction read to a single frame-constant `Faction me` in `UpdatePanels` (the one hot-loop site the prior pass's hoist sweep missed); three `_localFaction()` calls → one.

**Review findings breakdown:**
- Patches applied: 1 (`[low]` CommandCardSystem hoist).
- Deferred: 2 NEW entries appended to `deferred-work.md` — (1) pre-existing raw-`LocalFaction` consumers (personalised overlays + hero-owner minting) leak the stale online faction across the offline-after-online seam (two-tier source of truth); (2) `MinimapBridge.DrawDots` draws enemy dots through fog (no `IsVisible` gate) — a fairness gap distinct from the already-deferred spectator-`RevealAll` entry.
- Rejected: 14 (enum-indexed-array crash risk — verified safe, arrays sized to `FACTION_ARRAY_SIZE`=9; online-Neutral clamp — unreachable via `Neutral→GoSpectate`; null-getter guard — defensive nit; fog-viewer-source drift — provably equal at call site; worker-build inline path — spec-sanctioned; replay/N-faction perspective — out of scope + pre-existing; the Godot-coupled verification-gap seams — accepted Tier-1 boundary, `LockstepManager` calls `GD.Print`; intent-alignment descriptive analysis). One re-surfaced finding (spectator minimap `RevealAll`) was NOT re-added — already in the ledger.

**Follow-up review recommendation:** `false`. Patched findings this pass: 1 low, 0 medium, 0 high → score = 3×0 + 1×1 = 1 (< 5, no high).

**Verification performed:**
- `dotnet build godot/godot.csproj` — 0 errors (13 pre-existing warnings), determinism analyzer green.
- `dotnet test godot/ProjectChimera.Sim.Tests` — 3088 passed, 1 skipped (pre-existing), 0 failed; every committed golden byte-identical; `SimChecksum.AlgoVersion`(21) / `StartStateHash.AlgoVersion`(2) / `PROTOCOL_VERSION`(2) pins unchanged (Block-If clause held — no sim-path edit).

**Residual risks:** The two new defers are pre-existing and out of 9.5's intent scope (raw-`LocalFaction` consumer clamp; minimap fog concealment); both are captured for focused later attention. The interaction/perspective presentation surfaces remain Godot-coupled and unverifiable in Tier-1 — the accepted boundary the spec's Verification section already discloses; the in-engine two-client / forced-`Player2` manual checks are the sanctioned closure and are unchanged by this pass. Residual artifact in the working tree: `sprint-status.yaml` (modified before this run, orchestrator-owned — left in place, not part of the reviewed diff).
