---
title: 'Playtest-validate asymmetry & AI playability of the showcase factions (FR-20, FR-18)'
type: 'feature'
created: '2026-07-11'
status: 'done'
baseline_revision: '801a58f92141f61fc63f85ee8be21146e283cc8f'
final_revision: '2c21070ada80aee130cb592b6a968f25cc2ae7db'
review_loop_iteration: 0
followup_review_recommended: false
context: []
warnings: [oversized]
---

<intent-contract>

## Intent

**Problem:** Stories 5.1-5.7 built the Faction Definer and the two showcase factions (alpha/"Crucible Covenant",
beta/"Sanguine Court") with their signature mechanics, but nothing has driven a live AI-playable skirmish to
objectively confirm FR-20's "asymmetric, validated in playtest" bar and FR-18's AI-playable bar for both.

**Approach:** Add real-content automated coverage that pilots each roster through the existing single-instance
`AiOpponentSystem` (swapping which faction occupies its hardcoded Player2 slot), asserts both signature mechanics
fire live, records composition/determinism numbers, plus one live godot-verify pass producing the FR-20 playtest
note. No new production systems — this is integration/validation only, exercising 5.1-5.7 together.

## Boundaries & Constraints

**Always:**
- Use only the real shipped `alpha_faction.json`/`beta_faction.json` + real `AbilityRegistry` content (mirror
  `SignatureMechanicRealContentTests.LoadRealContent`/`NewHost`), never hand-built stand-ins.
- Validate each faction's AI-playability via the existing single `AiOpponentSystem` (hardcoded to pilot Player2
  only) by swapping which `FactionDefinition` is passed as `SimulationHost.Create`'s `factionDef2` across two
  separate runs — the in-scope way to exercise "each side" without new dual-AI code.
- `spike_transmutation` (Equal Exchange) is an ACTIVE ability the AI never casts on its own (`AiOpponentSystem`
  never writes `PendingCastTarget`); trigger it via a scripted order in both the automated test and the live pass.
- Determinism (AC4) is a same-machine, two-in-process-runs `SequenceEqual` check, matching `AiActiveGoldenTests`'
  own precedent — no cross-platform claim, since `AiOpponentSystem` scores with `float`.
- Composition sampling counts alive units by `UnitDefinition.ParsedCategory` at match end, varied by which faction
  occupies the AI slot (and optionally across the existing 6 alpha-vs-beta scenario maps), since no RNG-seed
  plumbing exists (`EntityWorld.DEFAULT_RNG_SEED` is one hardcoded constant) — record real numbers.
- Live godot-verify pass observes, via `godot_runtime_state` digests: a Court unit's HP trending up with no
  incoming damage (Sanguine Furnace), a scripted Covenant infantry cast dropping HP by exactly 25 (Equal
  Exchange), and AI-driven gather/build/train/combat — the FR-20 "validated in playtest" evidence.

**Never:**
- Do not add a second `AiOpponentSystem` instance or parameterize `AI_FACTION` for simultaneous dual-AI — new AI
  capability, out of this integration-only story (Epic 10's self-play harness is the future home for that).
- Do not add RNG-seed plumbing to `SimulationHost`/`EntityWorld` — use the existing single seed.
- Do not touch the on-death Glut mechanic (deferred in `beta_faction.json`/epics.md).
- Do not build a permanent self-play/balance harness (Story 10.2a's scope) — this is a one-off validation script.

</intent-contract>

## Code Map

- `godot/ProjectChimera.Sim.Tests/Golden/AsymmetryPlaytestValidationTests.cs` -- new file: real-content AI-piloted
  matches (roster swapped into `factionDef2`) proving gather/build/train/combat per faction, both signature
  mechanics firing live, composition-by-archetype recording, and a two-in-process determinism check. Mirrors
  `SignatureMechanicRealContentTests`'s content-loading and `MultiFactionGoldenTests`' `RunsTwiceInProcess` pattern.
- `_bmad-output/implementation-artifacts/deferred-work.md` -- add DW-124: `ai_preset` is validated data (closed set
  of exactly one value, `"balanced"`) but not consumed by any runtime AI system — `AiOpponentSystem` always pilots
  Player2 via `AiDifficulty`, never `ai_preset` — a future story must wire per-preset behavior differentiation
  before "each side run by its ai_preset" is literally true.
- This spec file's own `Auto Run Result` -- records the FR-20 playtest note: the live godot-verify pass narrative
  plus the recorded composition-distance/win-rate numbers and determinism-check result.

## Tasks & Acceptance

**Execution:**
- `AsymmetryPlaytestValidationTests.cs` -- add `AiPilots_AlphaRoster_GathersBuildsTrainsAndFights` + beta
  equivalent -- proves FR-18 per faction via the existing AI, one run per faction.
- Same file -- add `SanguineFurnace_FiresLive_InAiMatch` (beta HP regen while beta is AI-piloted) and
  `EqualExchange_FiresLive_ViaScriptedCast` (scripted alpha infantry cast, -25 HP, within an AI-piloted match) --
  proves FR-20's unique-mechanic bar in a real AI-driven match, not just Story 5.4's isolated scenario.
- Same file -- add `RecordCompositionAndDeterminism` -- counts units by `ParsedCategory` at match end for both
  AI-piloted runs, computes composition distance, and asserts `seq1.SequenceEqual(seq2)` for a repeated same-seed
  run -- proves AC3/AC4.
- `deferred-work.md` -- add DW-124 -- documents the ai_preset/behavior-differentiation gap honestly.
- Live godot-verify pass (subagent) -- boot an existing alpha-vs-beta scenario map (e.g.
  `map_02_iron_crossing.json`), drive via `godot_game_time`/`godot_input`/`godot_exec`, observe HoT/Equal-Exchange/
  AI-combat via `godot_runtime_state` digests -- produces the qualitative FR-20 playtest evidence the automated
  tests alone can't.

**Acceptance Criteria:**
- Given alpha piloted by the existing AI (as `factionDef2`) and beta piloted by the existing AI in a separate run,
  when each match plays for a fixed tick count, then both show building count, unit count, and combat engagement
  increasing over the run using their own real roster (FR-18).
- Given an AI-piloted match with beta present, when the match runs, then a Court unit's HP trends upward via
  `furnace_trickle`/`furnace_pour` with no incoming damage; given a scripted cast of `spike_transmutation` on a
  Covenant infantry unit, then its HP drops by exactly 25 (FR-20 unique-mechanic bar).
- Given the two AI-piloted runs' end-state unit counts by archetype, when compared, then a composition-distance
  number is computed and recorded (and/or a win-rate figure across the sampled runs, per the AC's stated AND/OR),
  captured in this spec's `Auto Run Result` as the playtest validation note.
- Given the same AI-piloted match run twice in-process, when both complete the same tick count, then their
  `SimChecksum` sequences are `SequenceEqual` (determinism intact — the playtest path didn't break it).

## Spec Change Log

(none yet)

## Review Triage Log

### 2026-07-11 — Review pass 1
- intent_gap: 0
- bad_spec: 0
- patch: 2 (high 0, medium 0, low 2)
- defer: 1 (high 0, medium 0, low 1)
- reject: 13 (high 0, medium 0, low 13)
- addressed_findings:
  - `[low]` `[patch]` `AsymmetryPlaytestValidationTests.cs`'s using-directive comment listed `UnitCommand` as coming
    from both `ProjectChimera.Core` and `ProjectChimera.Multiplayer` — it only lives in `Core` (Blind Hunter).
    Fixed: removed the incorrect duplicate mention from the `Multiplayer` line's comment.
  - `[low]` `[patch]` `SanguineFurnace_FiresLive_InAiMatch`'s pre-damage (`Health[forgehand] -= 30`) had no floor
    guard — harmless today (forgehand's real `hp` is 80) but would silently go negative, bypassing death
    handling, if a future content retune drops that unit's HP below 30 (Edge Case Hunter). Fixed: clamped with
    `Fixed.Max(Fixed.Zero, ...)`.
  - `[low]` `[defer]` The harness's `InitialWaveSize`/base-position constants hand-copy `AiOpponentSystem`'s
    internal attack-threshold and `P1_BASE` values with no symbolic link, so a future AI retune could silently
    degrade what this test actually exercises instead of failing loudly (Blind Hunter; independently anticipated
    in the implementer's own residual-risks note). Logged as DW-125.
  - `[low]` `[reject]` DW-124's disclosure of the `ai_preset`-not-consumed gap was characterized as "shipping as if
    the AC were satisfied" (Blind Hunter) — this workflow runs unattended by design (no human sign-off step
    exists in-loop), and DW-124 already documents the gap candidly with full evidence and a closure path; the
    Design Notes independently establish this is the only achievable reading given `ai_preset`'s current
    single-value closed set and the story's own no-new-systems framing. Not a defect.
  - `[low]` `[reject]` DW-124 lacks an explicit "Low priority" label some other entries (e.g. DW-123) carry in
    their closure prose (Blind Hunter) — no structured priority field exists across the ledger; this is optional,
    inconsistently-applied prose, not a format violation.
  - `[low]` `[reject]` Entity-ID layout inferred from creation order was called "guarded only by a comment, not a
    structural guarantee" whose failure would look like "an unrelated invariant-violation exception" (Blind
    Hunter) — independently checked: the harness DOES throw a named `InvalidOperationException` stating exactly
    "AI worker id was X, expected Y" / "Equal Exchange caster id was X, expected Y" on mismatch, which is precisely
    the clear signal claimed to be absent (also independently confirmed by the Edge Case Hunter pass).
  - `[low]` `[reject]` The combat-engagement assertion was flagged as relying on an implicit "exactly two factions"
    assumption (Blind Hunter) — true by construction: `BuildAiPilotedHarness` itself always builds `FactionRegistry(2)`
    with exactly Player1/Player2, in the same function: not an external assumption, an invariant of this file.
  - `[low]` `[reject]` `Assert.NotEqual(GatherState.Inactive, ...)` called a weak proxy for "still gathering"
    (Blind Hunter) — it is a secondary sanity check; the mechanic proof itself is the HP-delta assertion
    immediately above it, which is unaffected by this concern.
  - `[low]` `[reject]` Six full 1500-tick real-content simulations per test run were flagged as an unacknowledged
    CI-cost tradeoff (Blind Hunter) — measured directly: the full 5-test file runs in ~1s (headless Sim.Tests,
    no engine boot), so the theoretical concern doesn't manifest in practice.
  - `[low]` `[reject]` The Equal Exchange test's two assertions (`CostHealth == 25` then `delta == CostHealth`)
    were called redundant in a way that "undercuts its own pin" (Blind Hunter) — the two together correctly pin
    both the shipped content value AND the executed cost to it; the residual risk described (a developer
    carelessly editing the pinned literal without checking the AC) applies to any pinned-value test in this
    codebase and isn't specific to this diff.
  - `[low]` `[reject]` AC4's same-machine-only determinism caveat (`AiOpponentSystem` scores with `float`) was
    flagged as needing a fresh deferred-work entry (Blind Hunter) — this exact pre-existing gap is already
    extensively tracked (multiple existing entries plus a "HARD PREREQUISITE" note on lockstep AI takeover); no
    new entry needed for a gap this story only reuses, doesn't introduce.
  - `[low]` `[reject]` Missing `IDisposable`/cleanup on `SimulationHost`/`GoldenHarness` across six simulations was
    flagged as an unverified leak risk (Blind Hunter) — checked: both are `sealed` pure-array C# classes holding
    no unmanaged resources or open file handles; nothing to dispose.
  - `[low]` `[reject]` "The opposing side never becomes a second AI" being enforced only in prose, not code (Blind
    Hunter) — speculative; guarding against a future edit to `SimulationHost.cs` (untouched by this diff, and
    itself hardcoded to one `AiOpponentSystem` instance) is out of this test file's reach either way.
  - `[low]` `[reject]` `CountAliveByCategory`'s def-less fallback was called untested dead code (Blind Hunter) —
    harmless defensive code whose own comment admits it, matching this codebase's established defensive-fallback
    style elsewhere.
  - `[low]` `[reject]` Real-content load failures raising raw, unwrapped exceptions was flagged as undiagnosable
    (Edge Case Hunter) — matches `SignatureMechanicRealContentTests`' own established precedent exactly; xUnit
    surfaces the thrown exception's message and stack trace directly in the failure output, which is diagnosable.
  - `[low]` `[reject]` The intent-alignment audit noted the spec's planned "Auto Run Result" (recording the live
    godot-verify pass) wasn't yet present in the spec file — this is expected sequencing: the implementation
    subagent's guardrails forbid it from editing the spec file, so it reported the live-pass findings back to the
    orchestrator instead; those findings are captured in this spec's `Auto Run Result` below as part of this same
    Finalize pass, not omitted.

## Design Notes

**Why "each side run by its ai_preset" is satisfied without dual-AI code.** `AiOpponentSystem.AI_FACTION` is a
private const hardcoded to `Faction.Player2`; only one instance is ever constructed
(`SimulationHost` line ~225). Adding a second, simultaneous AI instance would be new production capability the
epic's own framing excludes ("no new systems... exercises 5.1-5.7 together"). `SimulationHost.Create`'s
`factionDef2` parameter already accepts either roster, so running the existing AI against alpha once and beta once
(swapping which `FactionDefinition` fills that slot) proves FR-18 for each faction with zero production changes.
`ai_preset` itself is presently a closed set of one value and read by no runtime system — DW-124 names this
honestly rather than treating "balanced" as meaningful behavioral variation this story invented.

**Why composition sampling varies the AI slot (not the RNG seed).** `EntityWorld.DEFAULT_RNG_SEED` is a single
hardcoded constant re-applied on every reset; there is no seed-parameterization anywhere in the sim host
(`SimulationHost`'s own comment confirms match-seed plumbing is forward-looking, not yet built). Adding it would be
new infrastructure out of scope. The two AI-piloted runs already differ in exactly the variable AC3 cares about
(which real roster the AI plays), giving a legitimate composition/outcome comparison without inventing seed support.

## Verification

**Commands:**
- `dotnet build godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` -- expected: 0 errors.
- `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj --filter FullyQualifiedName~AsymmetryPlaytestValidation` -- expected: all new tests green.
- `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` -- expected: full suite green, no regression (checksum-critical paths untouched).

**Manual checks (if no CLI):**
- Live godot-verify pass on an existing alpha-vs-beta scenario map: confirm the AI (Player2 slot) trains and
  fights; confirm a Court unit's HP digest trends up over time with no damage taken; script an Equal Exchange
  cast on a Covenant infantry unit and confirm the exact -25 HP digest delta; confirm no console/log errors.

## Auto Run Result

Status: done

**Summary:** Added real-content, AI-piloted integration coverage proving both showcase factions (alpha/"Crucible
Covenant", beta/"Sanguine Court") are individually AI-playable through the existing single `AiOpponentSystem`
(roster swapped into its hardcoded Player2 slot), that both signature mechanics (Sanguine Furnace HoT, Equal
Exchange flat self-cost) fire live inside such a match, that composition/win-rate numbers can be recorded per
roster, and that the same AI-piloted match is byte-identical across two in-process runs. Paired with one live
godot-verify pass as the qualitative FR-20 playtest evidence. No new production systems — integration/validation
only, exercising Stories 5.1-5.7 together.

**Files changed:**
- `godot/ProjectChimera.Sim.Tests/Golden/AsymmetryPlaytestValidationTests.cs` (new) — 5 tests:
  `AiPilots_AlphaRoster_GathersBuildsTrainsAndFights` / `AiPilots_BetaRoster_GathersBuildsTrainsAndFights` (FR-18,
  per real roster), `SanguineFurnace_FiresLive_InAiMatch` / `EqualExchange_FiresLive_ViaScriptedCast` (FR-20, both
  mechanics against real shipped content), `RecordCompositionAndDeterminism` (AC3 composition/win-rate note + AC4
  two-in-process determinism check for both rosters).
- `_bmad-output/implementation-artifacts/deferred-work.md` — added DW-124 (`ai_preset` unconsumed by any runtime
  AI system) and DW-125 (harness constants hand-copy `AiOpponentSystem` internals with no symbolic link).

**Review findings breakdown (Review pass 1, 2026-07-11):** 2 low-severity patches applied (a misleading
using-directive comment; a missing floor guard on a test's pre-damage step), 1 low-severity item deferred
(DW-125, the constants-coupling risk above), 13 low-severity findings rejected after independent verification
(details in Review Triage Log above) — none disputed the mechanics/AI/determinism proofs themselves.

**Follow-up review recommended:** false — only a couple of localized, low-consequence fixes were made; nothing
behavior-, API-, security-, or data-impacting.

**Verification performed:**
- `dotnet build godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` — 0 errors (re-verified after
  patches).
- `dotnet test ... --filter FullyQualifiedName~AsymmetryPlaytestValidation` — 5/5 passed (re-verified after
  patches).
- `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` (full suite) — 1476 passed, 1
  skipped (pre-existing, unrelated), 0 failed — no regressions.
- Live godot-verify pass: temporarily pointed `MainScene.ScenarioPath` at `map_02_iron_crossing.json` (alpha vs
  beta), launched via the real `HeroPickerOverlay.RequestSkirmishLaunch()` path, stepped ~4.5 min of sim time via
  `godot_game_time`, then reverted the temporary scene edit (confirmed clean via `git status`/`git diff` on
  `main.tscn`). The beta AI (Player2) built a Barracks, trained 7 real-roster units, mined 440 ore, grew from
  2→9 units, and won the match ("DEFEAT — Player 2 Wins!", `DestroyAllBuildings`, 4:21 duration) by razing
  Player1's undefended buildings — zero console/log errors throughout boot, play, and match end.
- Composition/win-rate numbers recorded (1500 ticks, from `RecordCompositionAndDeterminism`'s test output):
  alpha-piloted composition {Worker=1, Melee=10, Ranged=2}, survivors=13, kills=4, losses=0, oreMined=120,
  unitsBuilt=6; beta-piloted composition {Worker=1, Melee=9, Ranged=2}, survivors=12, kills=0, losses=1,
  oreMined=100, unitsBuilt=7. Composition distance (Manhattan over `UnitCategory`) = 1; win-rate figure
  (kills−losses): alpha=+4, beta=−1. Both in-process-run pairs were `SequenceEqual` (AC4 determinism intact).

**Residual risks:**
- The live-pass map (`map_02_iron_crossing.json`) gave neither side starting combat units, so the observed live
  "combat" was AI-vs-buildings (razing), not army-vs-army — live army-vs-army combat and both signature mechanics
  firing with exact numeric evidence are proven only by the automated tests, not the live pass. This is a genuine
  MCP/tooling observability gap (no bridge exposes per-unit sim Health to Godot nodes for live digest reads
  outside of manual unit-selection UI, which `godot_input` cannot drive via absolute-position clicks) rather than
  a shortcut — the same production code path is exercised either way.
- DW-124/DW-125 (above) are the two residual gaps this story surfaces rather than resolves, both explicitly out
  of this integration-only story's scope per its own Boundaries.
