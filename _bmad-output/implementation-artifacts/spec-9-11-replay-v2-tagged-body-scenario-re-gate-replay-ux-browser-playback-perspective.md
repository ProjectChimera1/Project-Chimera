---
title: 'Replay v2 (tagged body + scenario re-gate) + replay UX (browser/playback/perspective)'
type: 'feature'
created: '2026-07-24'
status: 'done'
baseline_revision: '8cc5ca93dd716e9211c0f9cf36a4013ac6428247'
final_revision: '72eee14a0591d4f80ea093c6ee12fba0946280d8'
review_loop_iteration: 0
followup_review_recommended: true
context:
  - '{project-root}/_bmad-output/project-context.md'
  - '{project-root}/_bmad-output/implementation-artifacts/epic-9-context.md'
  - '{project-root}/godot/src/Multiplayer/ReplayRecorder.cs'
  - '{project-root}/godot/src/Multiplayer/ReplayPlayer.cs'
  - '{project-root}/godot/src/Multiplayer/NetworkCommand.cs'
  - '{project-root}/godot/src/Core/Bootstrap/Phases/MatchLifecycleController.cs'
  - '{project-root}/godot/src/Core/MainScene.cs'
  - '{project-root}/godot/src/Core/Definitions/CanonicalModelHash.cs'
  - '{project-root}/godot/src/Core/Definitions/RulesetHash.cs'
  - '{project-root}/godot/src/Core/FactionRegistry.cs'
  - '{project-root}/godot/src/Core/Bootstrap/Phases/ContentBrowserPhase.cs'
  - '{project-root}/godot/ProjectChimera.Sim.Tests/Golden/SimRngChecksumReplayTests.cs'
warnings: ['oversized', 'multiple-goals']
---

<intent-contract>

## Intent

**Problem:** Chimera's `.chmr` replay (format v3) stores only a scenario *path* + seed. It embeds no content fingerprint, so playback cannot detect that the replay was recorded on a different version of the scenario (silent desync), and there is no in-game way to browse, play, control, or view replays — the only entry point is an Inspector `ReplayPath` string. FR-77 and the M5 "replays are viewable/shareable" promise are unmet.

**Approach:** Bump the replay format to **v4** ("replay v2"): a self-describing **tagged body** built from the frozen `MergedTickPacket` envelope (Story 9.3) plus a result trailer, and a header that embeds the canonical scenario hash, ruleset hash, model algo-version, faction count, and roster. Hard-reject any pre-v4 file and **re-gate the embedded scenario hash against the loaded scenario before the first replayed tick** (fail-closed, mirroring `HandshakeGate.CheckStart`). Then build the replay UX: a main-menu **replay browser** (metadata + rename/delete), **playback controls** (pause/resume, 1x/2x/4x/8x, seek-forward, tick/clock), a **perspective toggle** (per-player fog or reveal-all), and a **"Save Replay"** affordance on the score screen.

## Boundaries & Constraints

**Always:**
- Reuse the **frozen `MergedTickPacket` tagged envelope** (`PacketType.TickCommandsMerged = 0x14`, layout `type+tick+subBundleCount+[faction+orderCount+orders]`) for per-tick records — call `MergedTickPacket.Write`/`TryRead` verbatim; sub-bundles written **ascending by faction id** so wire order stays the canonical apply order.
- Record→replay must reproduce **byte-identical SimChecksums** (D6 determinism): the round-trip test in `SimRngChecksumReplayTests` stays green.
- Playback re-gate is **fail-closed**: if the embedded scenario hash is `0`, the loaded scenario hash is `0`, or they differ, refuse to play and surface the reason — never play a mismatched replay.
- Reject a replay whose embedded `modelAlgoVersion` is **newer** than this build's `CanonicalModelHash.AlgoVersion` (forward-incompatible).
- The replay format and UX are **presentation/IO only** — no change to `SimChecksum`, sim arrays, or golden baselines. No golden re-baseline in this story.
- Fog perspective changes go through existing `Fog.SetViewer` / `FogBridge.RevealAll`; they are view-only and must not touch sim state or the checksum.
- A replay is **never silently discarded**: the auto-recorded file is always retained on disk; "Save Replay" only renames/annotates it.

**Block If:**
- Honoring the format would require **changing the frozen `MergedTickPacket` wire layout or its `0x14` type byte** — it is frozen by Story 9.3 and consumed live by the merged-tick path; a change there is out-of-band. HALT `blocked`.
- Making record→replay reproduce identical checksums would require folding a new array into `SimChecksum` or moving a golden — that is a determinism decision. HALT `blocked`.

**Never:**
- Never touch the live `TickCommandsMerged` network path, `MergedTickBuilder`, or `MergedTickApplier` server logic — this story only *reuses the codec* for file IO.
- Never implement rewind by storing per-tick world snapshots — backward navigation is re-sim from tick 0 only (deterministic), matching "no rewind in 1.0".
- Never keep back-compat playback for v1/v2/v3 files (they lack the embedded hash the re-gate requires) — hard-reject with an explanatory "recorded on an older replay format, please re-record" error.
- Do not add per-tick RNG state to the file; the seed in the header is the sole stream origin (D6).

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Record then replay v4 | A match recorded to `.chmr`, then loaded | Header round-trips (magic/version=4/seed/scenarioHash/rulesetHash/modelAlgoVersion/factionCount/roster); replay reproduces the identical per-tick SimChecksum sequence | n/a |
| Scenario re-gate pass | Replay's embedded scenarioHash == loaded scenario's `CanonicalModelHash.Compute` | Playback proceeds from tick 0 | n/a |
| Scenario re-gate fail | Embedded hash != loaded hash (or either is 0) | Playback refused; reason surfaced to caller/UI | Fail-closed block reason string, no ticks stepped |
| Legacy file | A v1/v2/v3 `.chmr` header | Load rejected | `InvalidDataException` "older replay format" |
| Forward-incompatible | Embedded `modelAlgoVersion` > build `AlgoVersion` | Load rejected | `InvalidDataException` "newer replay format" |
| Truncated header | File ends before roster is read | Load rejected | `InvalidDataException`, no partial playback |
| Browser metadata | `user://replays/*.chmr` present | Each row shows map name, players/factions, date, duration (finalTick→mm:ss @30tps), result (winner/no-victor/incomplete) | Unreadable/legacy file listed as "unplayable (old format)", not crash |
| Empty replays dir | No `.chmr` files | Browser shows an empty-state message | n/a |
| Result trailer | Match ends with a winner / no-victor / return-to-edit before end | Trailer records `winnerFaction`, `finalTick`, `completed`; incomplete recordings carry `completed=false` | Missing trailer ⇒ browser shows "incomplete" |

</intent-contract>

## Code Map

- `godot/src/Multiplayer/ReplayRecorder.cs` -- writer; bump `VERSION 3→4`, extend header, buffer per-tick sub-bundles and flush one `MergedTickPacket` frame per tick, write a result trailer + EOF.
- `godot/src/Multiplayer/ReplayPlayer.cs` -- reader; parse v4 header, expose `ScenarioHash`/`RulesetHash`/`Roster`/`FactionCount`/`FinalTick`/`WinnerFaction`/`Completed`; decode tagged frames via `MergedTickPacket.TryRead`; hard-reject pre-v4 + forward-incompatible.
- `godot/src/Multiplayer/NetworkCommand.cs` -- **read-only reference**: `MergedTickPacket.Write`/`TryRead`, `UnitOrder` (11B), `PacketType.TickCommandsMerged=0x14`.
- `godot/src/Multiplayer/ReplayHeader.cs` -- **new**: lightweight static `Read(string path)` returning header metadata only (no full parse) for the browser list.
- `godot/src/Core/Definitions/CanonicalModelHash.cs` / `RulesetHash.cs` -- **read-only**: `Compute`, `AlgoVersion` — the header hash sources + re-gate recompute.
- `godot/src/Core/FactionRegistry.cs` -- **read-only**: `ToFaction(slot)`, `PLAYER_COUNT` — roster derivation.
- `godot/src/Core/Bootstrap/Phases/MatchLifecycleController.cs` -- `StartRecording` computes+passes header fields; `StopRecording(winnerFaction, completed)` finalizes trailer; `TryLoadReplay` performs the fail-closed re-gate before entering Play.
- `godot/src/Core/MainScene.cs` -- playback loop (`_Process`) gains pause/speed/seek stepping + tick/clock; `ShowGameOver` calls `StopRecording(winner, completed:true)` and adds the "Save Replay" button; wires perspective toggle to `Fog.SetViewer`/`RevealAll`.
- `godot/src/UI/ReplayBrowserPanel.cs` -- **new**: Control listing `.chmr` metadata with Play/Rename/Delete (mirror `ContentBrowserPanel`).
- `godot/src/UI/ReplayPlaybackControls.cs` -- **new**: overlay for pause/resume, speed steps, seek-forward field, perspective toggle, tick/clock label.
- `godot/src/Core/Bootstrap/Phases/ReplayBrowserPhase.cs` -- **new**: creates+wires the browser panel (hotkey-opened, `ContentBrowserPhase` pattern); Play sets `ScenarioPath` to the replay's scenario then calls `TryLoadReplay`.
- `godot/ProjectChimera.Sim.Tests/Golden/SimRngChecksumReplayTests.cs` & `godot/ProjectChimera.Sim.Tests/Multiplayer/ReplayDslEventTests.cs` -- update ctor calls + version assertions (`IsFour`), add v4 round-trip + re-gate + legacy-reject tests.

## Tasks & Acceptance

**Execution:**
- `godot/src/Multiplayer/ReplayRecorder.cs` -- Bump `VERSION=4`; extend the ctor to accept `(scenarioHash, rulesetHash, modelAlgoVersion, Faction[] roster)`; write them after `seed`. Accumulate each tick's per-faction sub-bundles (from `RecordTick`) and, on tick advance / `Close`, emit one length-framed `MergedTickPacket` (sub-bundles sorted ascending by faction). On `Close(winnerFaction, completed)` write a result-trailer frame (`winnerFaction(1)+finalTick(4)+completed(1)`) then the frame-length EOF (`0`). Track `finalTick`.
- `godot/src/Multiplayer/ReplayPlayer.cs` -- Parse the v4 header; hard-reject version `< 4` ("older format") and `modelAlgoVersion > CanonicalModelHash.AlgoVersion` ("newer format"); expose the new metadata props. Read length-framed body: dispatch on payload[0] (`0x14`→`MergedTickPacket.TryRead`→store per-tick orders; trailer→winner/finalTick/completed); stop at frame-len `0`. Keep applying via the shared `OrderApplier.Apply` so replay stays identical to live.
- `godot/src/Multiplayer/ReplayHeader.cs` -- New static `ReplayHeader.Read(path)` → `{ ScenarioPath, ScenarioHash, Roster, FactionCount, FinalTick, WinnerFaction, Completed, IsPlayable }`; reads header (+ scans for trailer) only. Legacy/corrupt ⇒ `IsPlayable=false`.
- `godot/src/Core/Bootstrap/Phases/MatchLifecycleController.cs` -- `StartRecording`: compute `CanonicalModelHash.Compute(_ctx.Scenario)`, `RulesetHash.Compute()`, roster via `FactionRegistry.ToFaction`, pass to the recorder. `StopRecording(int winnerFaction, bool completed)`: forward to `Close`. `TryLoadReplay`: after the scenario is loaded, recompute the loaded scenario hash and apply the fail-closed re-gate (embedded==0 ‖ loaded==0 ‖ mismatch ⇒ do not enter Play; return the block reason); replace the current soft path-mismatch warning.
- `godot/src/Core/MainScene.cs` -- Playback loop: honor a `Paused` flag (skip stepping), a `Speed` (step `Speed` sim ticks/frame for 1/2/4/8), and a seek-forward request (fast-loop `Flush`+`StepOnce` to the target tick without per-frame render); update a tick/clock label each frame. `ShowGameOver`: call `StopRecording(winner, completed:true)` (was arg-less) and add a "Save Replay" button opening the rename dialog. Wire the perspective toggle to cycle `Fog.SetViewer(faction)` across the roster + `FogBridge.RevealAll()`.
- `godot/src/UI/ReplayBrowserPanel.cs` -- New Control; on open, enumerate `ProjectSettings.GlobalizePath("user://replays/")` `*.chmr`, read each via `ReplayHeader.Read`, render rows (name, roster glyphs/colors, date=file mtime, duration=finalTick/30→mm:ss, result). Play (emits selected path), Rename (rename file on disk, refresh), Delete (delete file, refresh). Empty-state label when none.
- `godot/src/UI/ReplayPlaybackControls.cs` -- New overlay: Pause/Resume, 1x/2x/4x/8x segmented control, seek-to-tick input, perspective cycle button, tick/clock label — bound to MainScene playback state.
- `godot/src/Core/Bootstrap/Phases/ReplayBrowserPhase.cs` -- New phase (register in the same list as `ContentBrowserPhase`, `MainScene.cs:453` neighbourhood); create+add the panel, hotkey to toggle in menu/Edit mode; on Play set `_ctx.Scene.ScenarioPath` to the replay's embedded scenario then `TryLoadReplay`.
- `godot/ProjectChimera.Sim.Tests/Golden/SimRngChecksumReplayTests.cs` + `.../Multiplayer/ReplayDslEventTests.cs` -- Update ctor/version usages; add: `V4RoundTrip_ReproducesChecksums`, `ScenarioReGate_MismatchIsRejected`, `LegacyVersion_IsHardRejected`, `ForwardAlgoVersion_IsRejected`, `ResultTrailer_RoundTrips`, `HeaderRead_ReturnsMetadata`. Cover every I/O-matrix row.

**Acceptance Criteria:**
- Given a match recorded to a v4 `.chmr`, when it is replayed, then the per-tick SimChecksum sequence is byte-identical to the recording (round-trip test green) and the header fields round-trip exactly.
- Given a v4 replay whose embedded scenario hash differs from the currently loaded scenario, when `TryLoadReplay` runs, then playback is refused with a fail-closed reason and zero ticks are stepped.
- Given a v1/v2/v3 file or a file whose `modelAlgoVersion` exceeds this build's, when it is loaded, then it is hard-rejected with a descriptive error and never partially played.
- Given `.chmr` files in `user://replays/`, when the replay browser is opened, then each is listed with map, players/factions, date, duration, and result, and Rename/Delete mutate the file on disk and refresh the list.
- Given a replay is playing, when the user pauses / sets 2x·4x·8x / seeks forward to a tick / toggles perspective, then playback pauses/steps-N/fast-advances/changes fog viewer accordingly, and the tick/clock display tracks the current tick — with no effect on determinism.
- Given a match reaches the score screen, when the game-over overlay appears, then a "Save Replay" affordance is present and the recorded file is retained on disk regardless of whether it is pressed.

## Spec Change Log

## Review Triage Log

### 2026-07-24 — Review pass (follow-up 2)
- intent_gap: 0
- bad_spec: 0
- patch: 4: (high 0, medium 1, low 3)
- defer: 3: (high 0, medium 2, low 1)
- reject: 8
- addressed_findings:
  - `[medium]` `[patch]` The CORE new v4 buffer-and-flush-on-tick-advance model had no test recording orders on TWO DIFFERENT ticks — every round-trip test flushes a single tick (or two factions on ONE tick), so `FlushTick`-on-advance emitting the earlier buffered tick's frame was never proven. Added Tier-1 `V4MultiTickOrders_BothTicksRoundTrip`: records tick 3 then tick 7, asserts tick 3's buffered frame applies unit 0 (and NOT unit 1) at `Flush(3)`, then tick 7 applies unit 1 at `Flush(7)` — proving the earlier tick survived the advance uncorrupted.
  - `[low]` `[patch]` `ReplayHeader.Read` cast roster bytes `(Faction)` without the value-range check `ReplayPlayer`'s ctor already applies (P8 follow-up), so a corrupt-roster file (byte outside `1..PLAYER_COUNT`) listed as PLAYABLE in the browser while the player hard-rejects it on click — the two playability gates disagreed. Added the same `1..PLAYER_COUNT` reject to the header reader; strengthened `OutOfRangeRosterFaction_IsRejected` to assert `ReplayHeader.Read(path).IsPlayable == false` (mirrors `OverlargeFactionCount`).
  - `[low]` `[patch]` The header reader's FULL-SCAN trailer decode (independent little-endian `finalTick` reconstruction, reached only when the fixed-tail fast path's signature fails) was never exercised WITH a trailer present. Added `HeaderRead_FullScanTrailer_MatchesFastPath`: appends a stray byte after EOF to defeat the fast path, asserts the scan reconstructs the same winner/finalTick/completed.
  - `[low]` `[patch]` The result trailer's incomplete-winner (`0`) and negative-winner clamp (`Close(-1,…)` → `0`) were unasserted. Added `ResultTrailer_IncompleteAndNegativeWinner_ClampToZero`.

Deferred (3): ruleset hash embedded in the v4 header but never re-gated on playback (scenario-only re-gate — new latent silent-desync gap beyond the intent's scenario-scoped re-gate); natural replay-finish leaves the last-selected single-player fog perspective applied to the frozen final frame (only F5/return-to-Edit resets it); recorder silently drops per-tick sub-bundles past `MERGED_MAX_SUBBUNDLES` (unreachable at `PLAYER_COUNT==8` today, latent if slots grow). All logged to `deferred-work.md`.

Rejected (8): header tail fast-path false-positive (astronomically improbable byte collision, metadata-only, playback re-parses — **already rejected last pass**); `(int)targetTick` narrowing / seek-past-end teardown (~828-day-match narrowing **already rejected last pass**; and seek-past-end lands on `IsFinished` with or without a clamp — no realistic behavior change); `Fog.SetViewer` unguarded deref (Fog is a non-null-in-Play invariant deref'd unguarded across the codebase — **already rejected last pass**); empty-scenario "dead Play button" (**already rejected last pass**); duplicated rename/sanitize logic in `MainScene`+`ReplayBrowserPanel` (both copies written deliberately last pass; DRY-only, no defect); Godot-side re-gate ENFORCEMENT + browser/controls/save automated-coverage gaps (**already rejected last pass** — covered by the spec's designated manual `godot-verify` checks); perspective label `Player1` vs browser `P1` (cosmetic); mid-roster / over-declared-`frameLen` truncation coverage (contained — callers swallow the exception; existing corruption coverage is thorough).

### 2026-07-24 — Review pass (follow-up)
- intent_gap: 0
- bad_spec: 0
- patch: 4: (high 0, medium 1, low 3)
- defer: 0
- reject: 11
- addressed_findings:
  - `[medium]` `[patch]` The CORE new v4 glue — `ReplayRecorder.FlushTick`'s per-tick sub-bundle selection-sort and `ReplayPlayer`'s multi-sub-bundle fan-out — had ZERO coverage (every round-trip test is single-faction-per-tick, so the sort never swaps and the fan-out loop never runs >1). Added Tier-1 `V4MultiFactionTick_RoundTripsSortedAscending`: records two factions on the SAME tick in DESCENDING call order, replays, asserts both applied AND ascending-by-faction (proves the sort + canonical wire order).
  - `[low]` `[patch]` A result-trailer frame (`0x1A`) shorter than its fixed 7-byte payload was silently ignored in `ReplayPlayer` — inconsistent with the story's own P3 fail-closed contract (corrupt-merged + unknown-type both throw). Now throws `InvalidDataException`; test `TruncatedTrailer_IsHardRejected`.
  - `[low]` `[patch]` Roster bytes were cast `(Faction)` unvalidated while `factionCount` was ceiling-bounded (P8) — a corrupt byte became an out-of-range `Faction` flowing to `Fog.SetViewer`. `ReplayPlayer` now rejects any roster value outside `1..PLAYER_COUNT`; test `OutOfRangeRosterFaction_IsRejected`.
  - `[low]` `[patch]` Stale `// … VERSION 3` comments sat next to `Assert.Equal(4, ReplayRecorder.VERSION)` in `CommandApplyParityTests` (3 sites) — corrected to VERSION 4.

Rejected (11): FinalTick = last-order tick (an acceptable duration proxy for a browser row; true-end-tick threading is scope creep); RNG reseed on a refused re-gate (inert — the seed is always `DEFAULT_RNG_SEED` and the autoplay path reloads to a fresh tick-0 world; revisit only at Epic-9 real seed handshake); header tail fast-path false-positive (astronomically improbable byte collision, triple-guarded); `(int)targetTick` narrowing (~828-day match); `ClampSpeed` non-snap to {1,2,4,8} (by design, UI-unreachable); seek ignores pause; empty-scenario "dead Play button"; rename reserved-name guards; `Fog.SetViewer` "NRE" (Fog is a non-null-in-Play invariant, deref'd unguarded across the codebase); Godot-side seek/perspective/re-gate-adoption automated-coverage gaps (covered by the spec's designated manual `godot-verify` checks).

### 2026-07-24 — Review pass
- intent_gap: 0
- bad_spec: 0
- patch: 10: (high 1, medium 4, low 5)
- defer: 1
- reject: 3
- addressed_findings:
  - `[high]` `[patch]` Browser Play ran `TryLoadReplay` against the stale in-session world (`ReplayBrowserPhase.HandlePlay` set `ScenarioPath` then loaded with no reload) → falsely blocked / non-tick-0 for the general case. Rewired to the `_Ready` pending-replay autoplay path (static `PendingReplayPath`/`PendingReplayScenarioPath` survive `ReloadCurrentScene`): reload → clean tick-0 world → re-gate compares the correctly-loaded scenario → playback from tick 0.
  - `[medium]` `[patch]` Rename could silently overwrite a different replay (`File.Move(overwrite:true)`) — added `File.Exists` dest guard + `overwrite:false` + visible reason in both `ReplayBrowserPanel.DoRename` and `MainScene.RenameReplayFile`.
  - `[medium]` `[patch]` A well-framed-but-corrupt / unknown-type body frame was silently dropped in `ReplayPlayer` — now throws `InvalidDataException` (fail-closed); test `CorruptOrUnknownFrame_IsHardRejected`.
  - `[medium]` `[patch]` Unbounded seek froze the main thread — capped to a bounded ticks/frame batch with a "Seeking…" readout.
  - `[medium]` `[patch]` Browser unreachable from the main menu (intent surface) — added a "Replays" nav button/event to `MainMenuOverlay` (Edit-mode `N` hotkey retained).
  - `[low]` `[patch]` Load failure/refusal was a silent dead button — now surfaces a dialog + status label.
  - `[low]` `[patch]` Duration/Result/speed-clamp inlined with three hardcoded 30-tps literals — extracted to Godot-free `ReplayFormat` (single `SimulationLoop.TICKS_PER_SECOND`) with Tier-1 `ReplayFormatTests`.
  - `[low]` `[patch]` `factionCount` unbounded on read — rejected above `FactionRegistry.PLAYER_COUNT` in `ReplayPlayer` + `ReplayHeader`; test `OverlargeFactionCount_IsRejected`.
  - `[low]` `[patch]` Trailer-less (crash-mid-record) fallback untested — added `HeaderRead_NoTrailer_FallsBackToMaxMergedTick`.
  - `[low]` `[patch]` `ReplayHeader.Read` scanned the whole body per browser refresh — now reads the trailer from the fixed EOF tail, full-scan fallback only when the tail isn't a trailer.

Deferred (1): recording only fires on the online `OnMatchStart` player path, so offline skirmish/AI matches produce no `.chmr` (pre-existing; logged to `deferred-work.md`). Rejected (3): v2/v3 hard-reject is the intended, spec-documented behavior (not v1-only); no backward-nav is correct per "no rewind in 1.0"; speed-button-resumes-pause is by design.

## Design Notes

**Why v4 rejects v2/v3, not only v1.** The re-gate ("re-gate scenarioHash on playback") is an *invariant* — every played replay is verified against its scenario. v2/v3 headers carry no scenario hash, so honoring them would mean skipping the re-gate for some files, breaking the invariant. Replays live in per-user `user://` (throwaway, not shipped content) in a pre-1.0 EA, so rejecting old files with a clear "re-record" message is the coherent, safe choice — the product outcome (viewable, verified v2 replays) is identical either way; only back-compat for disposable files differs.

**Tagged body via the frozen envelope.** Body = a stream of `frameLen(2 LE) + frame` records terminated by `frameLen==0`. `frame[0]` self-discriminates: `0x14` (`TickCommandsMerged`) is a full `MergedTickPacket` decoded by the frozen `TryRead`; `0x1A` is the replay result trailer. This reuses the Story-9.3 codec verbatim (no re-implementation, no wire change) and is why the envelope was co-designed to be shared across merged packet / DSL record / replay v2. The recorder buffers a tick's per-faction sub-bundles and flushes one merged frame per tick, sorted ascending by faction id — the same canonical apply order the live merged path uses, preserving determinism.

**Re-gate mirrors `HandshakeGate.CheckStart`.** Recompute `CanonicalModelHash.Compute(loadedModel)`; block if `embedded==0 || loaded==0 || embedded!=loaded`. Same fail-closed shape already trusted for the MP start handshake; replaces `TryLoadReplay`'s current soft `GD.PrintErr` path-mismatch warning.

**Header field sources (no new hashing).** `scenarioHash=CanonicalModelHash.Compute(model)`; `rulesetHash=RulesetHash.Compute()`; `modelAlgoVersion=CanonicalModelHash.AlgoVersion`; `roster[i]=FactionRegistry.ToFaction(i)` for `i in 0..N-1`; `N=model.PlayerSlots.Length`. All already computed for the MP handshake (`MatchAgreementHash`) — reuse, do not invent.

**Seek/speed/pause are pure re-sim.** Speed = number of `StepOnce` calls per frame; seek-forward = a tight `Flush`+`StepOnce` loop (no render) to the target tick; backward = restart from tick 0 (no snapshots). Deterministic re-sim is the only navigation mechanism — matches "no rewind in 1.0".

## Verification

**Commands:**
- `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` -- expected: all green, including the new v4 round-trip / re-gate / legacy-reject / trailer / header-read tests and the pre-existing `SimRngChecksumReplay*` round-trip (proves determinism preserved).
- `dotnet build godot/godot.csproj` -- expected: presentation layer (browser panel, controls overlay, phase, MainScene/lifecycle edits) compiles clean.

**Manual checks (in-engine, via the `godot-verify` skill):**
- Record a skirmish to completion, confirm a `.chmr` appears in `user://replays/` and the score screen shows "Save Replay".
- Open the replay browser: rows show map/factions/date/duration/result; Rename + Delete mutate the file; Play launches playback.
- During playback: Pause halts, 2x/4x/8x speed up, seek-forward jumps to a tick, the tick/clock label tracks, and the perspective toggle cycles each player's fog + reveal-all.
- Load a replay against a *mutated* scenario (or a legacy file) and confirm it is refused with a clear message rather than playing a desync.


## Auto Run Result

Status: done (follow-up review pass 2)

**Summary:** Second follow-up review of the shipped Replay v2 (v4 format + scenario re-gate + replay UX) work. Four review lenses (adversarial, edge-case, verification-gap, intent-alignment) run in parallel against the full diff since baseline `8cc5ca9`. Findings triaged: 0 intent_gap, 0 bad_spec, 4 patch, 3 defer, 8 reject. Prior-pass rejections (int-narrowing/seek, Fog-deref, empty-scenario, Godot-layer coverage, fast-path FP) were re-surfaced and re-rejected without re-litigation. No spec loopback; `<intent-contract>` untouched.

**Files changed this pass:**
- `godot/src/Multiplayer/ReplayHeader.cs` — `Read` now rejects roster bytes outside `1..PLAYER_COUNT` (aligns the browser playability gate with the player's existing ctor check).
- `godot/ProjectChimera.Sim.Tests/Golden/SimRngChecksumReplayTests.cs` — +3 Tier-1 tests (`V4MultiTickOrders_BothTicksRoundTrip`, `HeaderRead_FullScanTrailer_MatchesFastPath`, `ResultTrailer_IncompleteAndNegativeWinner_ClampToZero`) and strengthened `OutOfRangeRosterFaction_IsRejected` to assert header `IsPlayable == false`.

**Review findings breakdown:**
- Patches applied (4): multi-tick buffer-flush round-trip coverage (medium); header roster-value validation + test (low); full-scan trailer decode coverage (low); incomplete/negative-winner trailer assertions (low).
- Deferred (3, new ledger entries): ruleset hash embedded but never re-gated (medium); natural-finish leaves stale fog perspective (medium); recorder silently drops sub-bundles past `MERGED_MAX_SUBBUNDLES` (low, latent).
- Rejected (8): see Review Triage Log.

**Follow-up review recommendation:** true. Patched this pass: 0 high, 1 medium, 3 low → score `3×1 + 1×3 = 6` ≥ 5.

**Verification:**
- `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` → Passed: 3320, Failed: 0, Skipped: 1 (incl. the 3 new tests).
- `dotnet build godot/godot.csproj` → 0 errors (13 pre-existing warnings).

**Residual risks:** The three deferred items remain open (ruleset re-gate gap is the most material — a ruleset-only drift can still silently desync a replayed match; scenario drift is fully gated). All UX/enforcement flows (browser render, playback controls, re-gate block, save/rename) remain verified only by the spec's designated manual `godot-verify` checks, not automated tests — a standing consequence of the Godot-free Tier-1 constraint. Untracked `.uid` residual artifacts (Godot metadata, several from prior stories 9-9/9-10) are left in place, not part of this change.
