---
title: 'Four-player verified end-to-end'
type: 'feature'
created: '2026-07-24'
status: 'done'
baseline_revision: '9952521cc5e63cd28e24d7fe840c4ed68475c9c4'
final_revision: 'a9f74cf'
review_loop_iteration: 0
followup_review_recommended: false
context:
  - '{project-root}/godot/CLAUDE.md'
warnings:
  - oversized
---

<intent-contract>

## Intent

**Problem:** Epic 9 built every 4-player piece — widened `SimChecksum` + server checksum collector (9.1/9.3), the 8-faction model (9.2), server-authoritative merged tick (9.3), teams/alliances seeding (9.14), and N-faction team-aware victory (7.12/7.13) — but "supports 4 players" is still an *aggregate assumption*: no single flow proves a real 4-start-position map runs both **2v2** and **4-FFA** through load → play → one elimination → victory with the server quorum green and zero desync. Concretely: no 4-start-position map asset exists (all 11 shipped scenarios are 2-slot), the game-over score screen is hardcoded to P1/P2 only (`MainScene.ShowGameOver` :1538-1547), there is no guard against new hardcoded player-count literals, and no 4-player late-game load perf number is recorded for Story 10.3.

**Approach:** Ship the single verify-story (1.9b/10.1 precedent): author one committed 4-start-position scenario asset; add a headless N=4 end-to-end test that drives BOTH 2v2 and 4-FFA through the *real* production paths (`ScenarioApplier.Apply` → `MergedTickBuilder(4)`/`MergedTickApplier` → `AllianceSeeder.Seed` → `WinConditionSystem`) to victory, feeding each tick's `SimChecksum` into a live `ServerHost(4)` and proving byte-identical two-run determinism with the quorum green throughout; generalize the game-over screen to render all active slots via a Godot-free, unit-tested summary builder; add a source-scan guard that no new player-count literal exists beyond the sanctioned `MpSeatCeiling`/`FACTION_ARRAY_SIZE`; and record a non-gated 4-player ms/tick number for 10.3.

## Boundaries & Constraints

**Always:**
- No new mutable sim state is introduced → **NO `SimChecksum` `AlgoVersion` bump and NO golden re-baseline**; every pre-existing golden stays byte-identical. The new scenario asset must not be pulled into any directory-enumerating golden (verify; if one enumerates the folder, exclude the fixture rather than re-baseline — per the checksum-fold timing rule).
- The N=4 e2e must exercise the **real** production paths: `ScenarioApplier.Apply` for load, `MergedTickBuilder(4)`/`MergedTickApplier` (`src/Multiplayer/Server/`) for the merged tick — not a single hand-stepped host — `AllianceSeeder.Seed` for teams, `WinConditionSystem` for victory, and `SimChecksum.Compute` over all active factions. A thinner proxy does not prove the promise.
- "Zero desync / server quorum green throughout" is asserted via a live `ServerHost(4, …)` fed each tick's `SimChecksum` for all four slots: `WindowsCompared` climbs past a threshold with `Passing == true && Halted == false && DesyncCount == 0`, **plus** byte-identical `SimChecksum` across two independent runs of each config.
- Constants stay **two-ceiling** correct: the sim runs to `FactionRegistry.PLAYER_COUNT` (8); MP transport seats `PlayerCountPolicy.MpSeatCeiling` (4). The 8-player bump is `MpSeatCeiling 4→8` re-verified by existing tests — **documented here, not coded here**.
- Sim/seeding/e2e-harness/guard/perf code stays Godot-free and banned-API-clean (no `float`/`Random`/`DateTime`/`Dictionary`-enumeration in the sim assembly). The game-over generalization lives in the presentation layer (`float`/wall-clock allowed there) with its slot-selection logic extracted into the Godot-free `GameOverSummary` builder.

**Block If:**
- The 4-start map cannot be authored as valid `ScenarioData` that `ScenarioValidator` accepts without a *hard* failure implying a missing engine capability (would need a design decision).
- Making both 2v2 and 4-FFA resolve to victory would require changing `WinConditionSystem` *resolution logic* — it must already be N-faction team-aware (7.12/7.13). If it is not, that is an upstream gap, not this story's to redesign.
- Any pre-existing golden moves as a result of this change (would mean this verify-story mutated sim truth — stop; do not re-baseline).

**Never:**
- No new sim systems, no new per-entity SoA arrays, no wire-format change. This is verification + one presentation fix + one data asset, not new gameplay.
- No AI-in-MP slot UI or AI-slot loopback verification: the canonical Story 9.15 block (epics.md:2989-3005) has **no** AI acceptance criterion; the "9.15 UI provides AI slots" cross-reference (epics.md:3387) is a *different* story's dependency, and AI-in-MP determinism is blocked on the `AiOpponentSystem` float→Fixed port (D2 debt). Out of scope — flag as a noted inconsistency, do not implement.
- No real-ENet rewrite of `LoopbackDesyncSelfTest` to run a full sim to victory — the headless deterministic harness is the CI-gated proof; the existing loopback smoke test (already `PlayerCount = 4`) stands as the complementary real-transport gate.
- No gating on the perf number (record only; feeds 10.3, not gated here).
- No rewrite of the lobby slot grid — `LobbyUi.RebuildSlotGrid` is already `PLAYER_COUNT`-driven.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| 2v2 e2e | quad map, teams {1,1,2,2} | winning team's survivors latch `VERDICT_WON`, losers `VERDICT_LOST`; merged-tick two-run `SimChecksum` byte-identical; `ServerHost` `Passing && !Halted && DesyncCount==0` | No error |
| 4-FFA e2e | quad map, teams {0,0,0,0} | last faction standing WON, other three LOST; two-run byte-identical; quorum green throughout | No error |
| Score-screen data | winnerFaction + N active slots + `MatchStats`/`WinStateStore` | `GameOverSummary` returns exactly `activeCount` rows, each with correct faction/verdict/kills/color; no P1/P2 truncation | No error |
| No-hardcode guard | scan sim/lobby/scenario `.cs` for player-count `4`/`9` literals | only allowlisted sites present (`MpSeatCeiling=4`, `FACTION_ARRAY_SIZE=9`, LobbyUi P2P `<=2`-seat, floor defaults `=2`); FactionRegistry bump-invariant holds | Test RED on any new stray literal |
| Perf record | 4-slot late-game world, K ticks | median ms/tick measured, emitted, and recorded to a committed note; **no** ceiling assertion | No error (non-gated) |
| Minority desync (negative) | one slot reports a wrong checksum in a window | `ServerHost` emits `DesyncAlert` to that slot; `DesyncCount>0`, `Passing==false` (proves quorum is not trivially green) | Detected, not silently passed |

</intent-contract>

## Code Map

- `godot/resources/data/scenarios/quad_map_01.json` -- **NEW** 4-slot, 4-start-position scenario (slots 0..3 → Player1..4, four distinct `base_x`/`base_z`, one pre-built `CommandCenter` + starter units per slot, `team` default 0=FFA). Shape mirrors `map_05_crossroads.json`.
- `godot/src/Core/Definitions/ScenarioData.cs` -- `ScenarioPlayerSlot {Slot, FactionJson, StartOre, StartCrystal, Team, BaseX, BaseZ}` (~:153); `PlayerSlots[]` (~:732). Reference for the asset; **no change**.
- `godot/src/Core/Sim/ScenarioApplier.cs` -- Godot-free sole writer of sim truth (~:29); consumes `Validated<ScenarioData>`; the load path the e2e must use.
- `godot/src/Core/AllianceSeeder.cs` -- `Seed(host.Alliances, scenario)` seeds the team mask; the 2v2 config uses it.
- `godot/src/Core/WinConditionSystem.cs` -- N-faction team-aware victory (`Configure` + `Tick`); resolves both configs. **No change expected** (Block If it would).
- `godot/src/Multiplayer/Server/MergedTickBuilder.cs` / `MergedTickApplier.cs` -- real merged-tick fan-in at N=4 (`new MergedTickBuilder(4, slotFactions)` + `MergedTickApplier.Apply(...)`, per `Golden/MergedTickN3Scenario.cs`). The e2e driver.
- `godot/src/Multiplayer/Server/ServerHost.cs` -- `ServerHost(expectedPeerCount, ILogSink, sendReliableTo, broadcastReliable)` (~:54); `OnChecksum(slot, tick, hash)` (~:68); observable `WindowsCompared`, `Passing`, `Halted`, `DesyncCount`. The quorum gate.
- `godot/src/Multiplayer/Server/ServerChecksumCollector.cs` -- `MaxSlots = 4` majority-vote collector backing `ServerHost`.
- `godot/src/Core/SimChecksum.cs` -- `Compute(world, buildings, resources, factions, … winState, alliances)` (~:252) over all active factions; the determinism oracle.
- `godot/src/Core/FactionRegistry.cs` -- `PLAYER_COUNT=8` (:19), `FACTION_ARRAY_SIZE=9` (:25), `ToFaction` (:28). Constant source of truth.
- `godot/src/Multiplayer/PlayerCountPolicy.cs` -- `MpFloor=2` (:21), `MpSeatCeiling=4` (:25) — the ONE sanctioned player-count `4` and the 8-bump point.
- `godot/src/Core/MainScene.cs` -- `ShowGameOver(int winnerPlayer)` (:1515); P1/P2-hardcoded body (:1538-1547). **Generalize** to render all active slots via the new builder; keep the heading generalized (winnerFaction, not `==1`/`==2`). Presentation only.
- `godot/src/Core/MatchStats.cs` -- per-faction (sized 9) kills/losses/built/ore; already 4-slot capable; feeds the builder.
- `godot/src/Core/WinStateStore.cs` -- per-faction `Verdict[]`, `MatchTicks`, `WinnerFaction()`; feeds the builder + result assertions.
- `godot/src/Core/GameOverSummary.cs` -- **NEW** Godot-free pure builder: `Build(winnerFaction, activeCount, MatchStats, WinStateStore)` → per-slot rows (faction, WON/LOST, kills, losses, color-key). Unit-tested; `ShowGameOver` renders it.
- Tests (all under `godot/ProjectChimera.Sim.Tests/`):
  - `Multiplayer/FourPlayerEndToEndTests.cs` -- **NEW**, the 2v2 + 4-FFA merged-tick e2e + `ServerHost` quorum + two-run byte-identity + negative minority-desync sub-test.
  - `UI/GameOverSummaryTests.cs` -- **NEW**, 4-slot-correct summary rows.
  - `Meta/NoHardcodedPlayerCountTests.cs` -- **NEW**, source-scan guard with allowlist + bump-invariant assertions.
  - `Perf/FourPlayerLoadPerfTests.cs` -- **NEW**, non-gated ms/tick recorder.
  - Reuse (patterns, no change): `WinConditions/TwoVsTwoSeededDeterminismTests.cs`, `WinConditions/NFactionVictoryTests.cs`, `Golden/MergedTickN3Scenario.cs`, `Golden/FactionRegistryTests.cs`, `Multiplayer/PlayerCountPolicyTests.cs`, `Golden/MultiFactionExpansionTests.cs`, `Validation/CanonicalModelHashPerfTests.cs` (perf-harness pattern).
- `godot/src/Multiplayer/LoopbackDesyncSelfTest.cs` -- existing real-transport 4-peer smoke test (`PlayerCount = 4`); reference as complementary gate, **no change**.
- `_bmad-output/implementation-artifacts/perf-4player-9-15.md` -- **NEW** note recording the observed median ms/tick for Story 10.3.

## Tasks & Acceptance

**Execution:**
- `godot/resources/data/scenarios/quad_map_01.json` -- Author a 4-slot, 4-start-position scenario: slots 0..3 → Player1..4 (alpha/beta or the four available factions), four distinct base points, one pre-built `CommandCenter` + a small starter unit set per slot, `resource_nodes` near each base, `team` omitted (0=FFA) so the map is FFA by default and the 2v2 test assigns teams at load. Must pass `ScenarioValidator` with no hard failure.
- `godot/src/Core/GameOverSummary.cs` -- **New** Godot-free pure builder producing one row per active faction (`Player1..activeCount`): faction id, WON/LOST from `WinStateStore.Verdict`, kills/losses from `MatchStats`, and the canonical faction/team color-key. No Godot types; no `float` (colors as a stable enum/index or `(byte)` rgb tuple, not `Color`).
- `godot/src/Core/MainScene.cs` -- Replace the P1/P2-hardcoded body of `ShowGameOver` (:1538-1547) with a loop over `GameOverSummary` rows so slots 3–4 render; generalize the heading to `winnerFaction`. Presentation-layer change only — do not touch sim state or the win-resolution path at :950-962.
- `godot/ProjectChimera.Sim.Tests/Multiplayer/FourPlayerEndToEndTests.cs` -- **New.** For BOTH configs — **2v2** (teams `{1,1,2,2}`) and **4-FFA** (teams `0`): load `quad_map_01` via `ScenarioApplier.Apply` (2v2 assigns slot teams before apply), `AllianceSeeder.Seed`, then drive the match through `MergedTickBuilder(4)`/`MergedTickApplier` to one elimination and full victory, feeding each tick's `SimChecksum` into a live `ServerHost(4, NullLogSink, …)`. Assert: per-faction WON/LOST verdicts correct + `WinCon.IsFullyResolved()`; `ServerHost` `Passing && !Halted && DesyncCount==0 && WindowsCompared >= threshold`; and `SimChecksum` byte-identical across two independent runs of the config. Add a **negative sub-test** injecting a minority-wrong checksum for one slot in a window → `DesyncCount>0`, a `DesyncAlert` to that slot, `Passing==false`.
- `godot/ProjectChimera.Sim.Tests/UI/GameOverSummaryTests.cs` -- **New.** Assert the builder yields exactly `activeCount` rows with correct verdict/kills/color for a resolved 4-slot 2v2 and a 4-FFA outcome (no P1/P2 truncation; slots 3–4 present and correct).
- `godot/ProjectChimera.Sim.Tests/Meta/NoHardcodedPlayerCountTests.cs` -- **New.** Scan sim/lobby/scenario source for bare player-count `4`/`9` literals; assert only allowlisted sites remain (`PlayerCountPolicy.MpSeatCeiling=4`, `FactionRegistry.FACTION_ARRAY_SIZE=9`, `LobbyUi` P2P `<=2`-seat literals, floor defaults `=2`); RED on any new stray literal. Also assert the FactionRegistry bump-invariant chain (`PLAYER_COUNT+1 == FACTION_ARRAY_SIZE == (int)Player8+1`) and that `PlayerCountPolicy` documents the 8-bump.
- `godot/ProjectChimera.Sim.Tests/Perf/FourPlayerLoadPerfTests.cs` -- **New.** Build a 4-slot late-game world (heavy, near `EntityWorld.MAX_ENTITIES` per the `CanonicalModelHashPerfTests` max-caps fixture), measure median ms/tick over K `host.StepOnce()` ticks (median-of-5, JIT warm-up), emit via `ITestOutputHelper`. **No timing assertion** (non-gated).
- `_bmad-output/implementation-artifacts/perf-4player-9-15.md` -- **New** note recording the observed median ms/tick (and the measurement conditions) as input to Story 10.3.

**Acceptance Criteria:**
- Given the committed 4-start-position map, when the headless N=4 e2e runs both 2v2 and 4-FFA through merged-tick play to one elimination and victory, then every active faction latches the correct WON/LOST verdict, `ServerHost` quorum is green throughout (`Passing`, `!Halted`, `DesyncCount==0`, `WindowsCompared` past threshold), and two independent runs produce byte-identical `SimChecksum` (zero desync).
- Given a resolved 4-slot match, when the game-over summary is built, then it contains exactly one correct row per active slot (up to 4) — verdict, kills, color — with no P1/P2 truncation, and `ShowGameOver` renders all active slots.
- Given the source tree, when the no-hardcoded-player-count guard runs, then only the sanctioned constants remain, the FactionRegistry bump-invariant holds, and the 8-player bump is documented as `MpSeatCeiling 4→8` + re-verification (via `PlayerCountPolicyTests`/`MatchmakerConfigTests`/`MultiFactionExpansionTests.EightFactions…`).
- Given a 4-slot late-game world, when tick throughput is measured, then a median ms/tick number is recorded to a committed note for Story 10.3, with no ceiling gate.
- Given all pre-existing golden-checksum tests, when the full suite runs, then every golden is byte-identical (no `AlgoVersion` bump, no re-baseline) and the new scenario asset is not pulled into any golden.

## Spec Change Log

## Review Triage Log

### 2026-07-24 — Follow-up review pass
- intent_gap: 0
- bad_spec: 0
- patch: 0
- defer: 1
- reject: 11
- addressed_findings:
  - none
- deferred (1): the high-severity `ShowGameOver` local-win headline logic (VICTORY iff the LOCAL faction's own latched verdict is WON, + the "Team Victory" sub-line) has no automated regression net — only `GameOverSummary.Build` rows are unit-tested, so a revert to `winnerPlayer == 1` keying would pass all tests. Shipped code is correct (all four reviewers concur); the spec's Design Notes route score-screen verification to the data layer + optional in-engine `/godot-verify`, so this is a coverage gap consistent with intent, not a defect. Logged to the deferred-work ledger.
- rejected (11): guard "misses inline literals / un-tokened names / values 3-5-6-7" (×4 reviewers) — the intent's I/O matrix scopes the guard to allowlisted NAMED constants; the diff's own tests are full of inline `4`s, confirming inline-literal scanning is unworkable, and the prior pass already rejected further broadening as diminishing returns; the guard's comment-strip not being string-literal-aware (real but speculative — no current trigger, suite green, proper fix needs tokenization); the vacuous-pass self-check "brittle to a non-{2,4,8,9} bump" (the documented bump is 4→8 and 8 is in the set, so it survives); e2e victory "scripted via RazeFaction not merged-command-caused" (already addressed prior pass via the load-bearing `MergedTickApplier_AppliesAnOrder` test; disclosed verify-story pattern); quorum "unanimous by construction / detection via synthetic hashes" (already addressed — docstrings + `MinorityDesync` negative test; disclosed); same-process determinism / float-AI cross-platform (prior pass rejected — D2 debt, out of scope per intent); offline `?? Faction.Player1` "wrong for non-P1 offline" (the documented sibling-site convention; offline single-player is P1, pre-existing); team-victory sub-line "tinted a single faction's color" (cosmetic; prior pass rejected the analogous `winnerColor` item); perf note "static number decoupled / 64 buildings unverified" (non-gated per intent; the static record is the prior pass's fix for the overwrite class); "no 5-8 slot test" (the 4→8 bump is documented-not-coded per intent — 8-player out of scope); `ServerTransport.MAX_SLOTS=8` vs `MpSeatCeiling=4` "disagreement" (pre-existing constant not set by this story; reviewer uncertain a clamp doesn't protect it, and the guard allowlist + `TwoCeilingPolicy_ConstantsAgree` already document/pin the relationship); game-over card "overflows at 8 rows" (a `PanelContainer` grows to fit and does not clip — 560×380 is a minimum; no truncation at the shipped N=4).

### 2026-07-24 — Review pass
- intent_gap: 0
- bad_spec: 0
- patch: 6: (high 1, medium 4, low 1)
- defer: 1
- reject: 6
- addressed_findings:
  - `[high]` `[patch]` The generalized `MainScene.ShowGameOver` heading computed `localWin` as `LocalFaction == winnerPlayer`, but `winnerPlayer` = `WinStateStore.WinnerFaction()` = the *lowest* WON slot (team representative) — so a winning ally on a higher slot (e.g. P2 on team {P1,P2}) saw **DEFEAT** while its own stat row showed WON, contradicting AC1's "every screen shows 4-slot-correct data". Fixed: `localWin` now keys off the local faction's OWN latched verdict (`WinState.Verdict[(int)local]==VERDICT_WON`), the local faction resolved via the null-guarded `_ctx.Lockstep?.EffectiveLocalFaction ?? Faction.Player1` accessor sibling sites use (raw `LocalFaction` was stale offline-after-online and NRE-prone since `Lockstep` is `null!`); the sub-heading now phrases a multi-WON result as a team victory instead of "Player N Wins!".
  - `[medium]` `[patch]` `GameOverSummary.Build` mapped a scattered non-NONE `activeCount` onto contiguous slots `0..activeCount-1`, so a non-contiguous active set ({P1,P3}) would emit inactive P2 and drop real P3. Fixed: Build now emits one row per playable faction whose `Verdict != NONE` (correct-by-construction; floor removed) and the dead `winnerFaction` parameter was dropped.
  - `[medium]` `[patch]` `FourPlayerLoadPerfTests` overwrote the committed tracked note `perf-4player-9-15.md` on every run (machine-dependent number + `DateTime.UtcNow`) — the documented "committed shipped-content mutation → red baseline" class. Fixed: the test emits the median via `ITestOutputHelper` only; the note is now a static one-time record.
  - `[medium]` `[patch]` `NoHardcodedPlayerCountTests` matched only literal values `[49]`, so `MpSeatCeiling 4→8` would stop matching and the vacuous-pass guard `found.Contains("MpSeatCeiling")` would then turn the guard RED on the exact sanctioned bump it protects; the allowlist was also name-keyed (stray same-named constant excused). Fixed: match ceiling/size const names against `{2,4,8,9}` (survives the 8-bump and catches a new `=8`), allowlist keyed on fully-qualified `File.cs::NAME`, broadened name vocabulary, comment-strip + whitespace-normalize the scan.
  - `[medium]` `[patch]` The e2e's "REAL merged-tick path" was unobserved — victory came from `RazeFaction`→`BuildingStore.Destroy` and the fanned-in Move orders were inert, so a no-op `MergedTickApplier` would still pass every assertion. Fixed: added `MergedTickApplier_AppliesAnOrder_TheUnitMovesTowardTarget` fanning a real Move through `MergedTickBuilder(4)`/`MergedTickApplier` and asserting the unit actually moved (RED on a no-op applier).
  - `[low]` `[patch]` The e2e docstrings overclaimed what the positive run proves (quorum is trivially green — one in-process host echoed to 4 slots). Fixed: docstrings now state precisely — positive run proves liveness + unanimous-accept, detection is proven by the `MinorityDesync` negative test, determinism by same-process run-vs-run byte-identity (not a cross-platform golden).
  - Deferred (1): pre-existing — `LoopbackDesyncSelfTest` sends a hardcoded `GOOD` checksum rather than each peer's computed sim checksum, so no automated artifact runs N independent sims that compute+compare their own checksums (literal 4-client zero-desync proven only by the manual two-machine LAN runbook).
  - Rejected (6): the recorded 141 ms/tick "exceeds 30 Hz budget" (intent routes perf to 10.3, explicitly not gated — flagged in the note instead); "two-run determinism can't catch cross-machine float-AI risk" (honestly disclosed in the test + a known D2 debt); the AI float-scoring cross-platform caveat (D2, out of scope per intent); guard "synonym could still escape" beyond the reasonable broaden (diminishing returns); the `winnerColor` inert defeat-branch (harmless, unreachable-by-construction); and the "single flow is three layer-tests" framing (the project's Godot-free/Tier-1 architecture selects the layered proof — reviewer-endorsed as idiomatic).

## Design Notes

**Verify-story pattern (1.9b/10.1 precedent):** the deliverable is *proof* plus the minimum fixes that make the proof honest — the 4-start asset and the P1/P2 score-screen generalization — not new gameplay.

**Two ceilings (load-bearing):** the sim runs to `PLAYER_COUNT=8`; MP transport seats `MpSeatCeiling=4`. "No new hardcoded 4s" means no *new* player-count literal — `MpSeatCeiling=4` and `FACTION_ARRAY_SIZE=9` are the sanctioned ones; LobbyUi's `<=2`-seat P2P strings are a real ENet transport limit (maxPeers=1 → 2 seats), allowlisted, and survive the 8-bump unchanged.

**Modeling "4 peers agree":** a single canonical sim driven through the real `MergedTickBuilder(4)`/`MergedTickApplier` is exactly what four real peers converge to; byte-identical two-run `SimChecksum` + a green `ServerHost(4)` fed those per-tick checksums is the established N-peer determinism proof (`MergedTickN3Scenario`, `TwoVsTwoSeededDeterminismTests`). Real-ENet 4-peer transport is already smoke-proven headlessly by `LoopbackDesyncSelfTest`. The negative minority-desync sub-test proves the quorum is not trivially green.

**Headless vs presentation split:** lobby and score screens are Godot; the honest CI-gated proof is the headless merged-tick determinism + server quorum. The score screen's 4-slot correctness is proven at the *data* layer via the Godot-free `GameOverSummary` builder; the visual render is confirmed in-engine (optional `/godot-verify`).

**No checksum fold → no re-baseline:** no new mutable sim array is added, so goldens stay byte-identical; a new scenario asset only adds a file. FFA (teams=0) is byte-identical to today per 9.14; the 2v2 config's checksum is new and internally two-run-verified, not golden-pinned.

**Noted inconsistency (not resolved here):** epics.md:3387 attributes "MP lobby AI slots" to "9.15 UI", but the canonical 9.15 block has no such AC and AI-in-MP determinism is blocked on the `AiOpponentSystem` float→Fixed port (D2 debt). Left out of scope; flag for planning if a downstream story depends on it.

## Verification

**Commands:**
- `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` -- expected: all green incl. the new `FourPlayerEndToEndTests` (2v2 + 4-FFA + negative desync), `GameOverSummaryTests`, `NoHardcodedPlayerCountTests`, and `FourPlayerLoadPerfTests`; **every pre-existing golden unchanged** (proves no re-baseline; ~3409+ prior tests stay green).
- `dotnet build godot/godot.csproj` -- expected: clean; the new Godot-free `GameOverSummary` + the `ShowGameOver` change raise no banned-API findings; presentation-layer `float`/wall-clock stays confined to `MainScene`.

**Manual checks:**
- In-engine (optional, via `/godot-verify`): load `quad_map_01`, run a 2v2 to victory, confirm the game-over screen shows all four slots' results (not just P1/P2) and the lobby renders 4 slots with team glyphs.

## Auto Run Result

Status: done (follow-up review pass — the prior run had `followup_review_recommended: true`)

**Summary:** Re-reviewed the committed Story 9.15 change (4-start `quad_map_01` scenario, the Godot-free `GameOverSummary` builder, the generalized `MainScene.ShowGameOver`, the N=4 headless e2e, the no-hardcoded-player-count guard, and the non-gated perf recorder) against `baseline_revision` 9952521 with four independent review lenses (adversarial, edge-case, verification-gap, intent-alignment). **No code changes were made** — no patch or bad_spec finding survived triage. The shipped code is correct; the single actionable item is a test-coverage gap routed to the deferred-work ledger.

**Files changed this pass:**
- `spec-9-15-four-player-verified-end-to-end.md` — status/followup frontmatter + new follow-up Review Triage Log entry.
- `deferred-work.md` — one new defer entry (the `ShowGameOver` local-win headline regression-net gap).

**Review findings breakdown:** 0 patches applied · 1 deferred (`ShowGameOver` local-win headline + team-victory sub-line have no automated regression net; shipped code correct, verification intentionally routed to data-layer + in-engine per Design Notes) · 11 rejected (guard-narrowness cluster ×4 reviewers — intent's I/O matrix scopes the guard to allowlisted NAMED constants; scripted-victory / unanimous-quorum / float-AI-determinism — already addressed or explicitly rejected in the prior pass and disclosed in-test; offline `?? Player1` — documented sibling-site convention; `PanelContainer` "overflow" — grows to fit, does not clip; `ServerTransport.MAX_SLOTS=8` vs `MpSeatCeiling=4` — pre-existing, already pinned by `TwoCeilingPolicy_ConstantsAgree`; cosmetic sub-line tint; non-gated perf note; no 5–8-slot test — the 4→8 bump is documented-not-coded per intent).

**Verification:** No re-run performed — zero code changed this pass (no patch, no bad_spec loopback), so the prior run's green suite/build stands unchanged.

**Follow-up review recommended:** `false` (0 patched findings this pass → score 0).

**Residual risks:** The deferred `ShowGameOver` headline coverage gap means a future regression of the local-win keying (back to `winnerPlayer == 1`) would not be caught by an automated test — flagged for later focused attention in the ledger, consistent with the spec's presentation-verification-via-in-engine choice.

